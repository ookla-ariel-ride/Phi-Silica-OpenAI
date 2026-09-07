using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// Remembers which accepted-but-ignored request parameters have already been warned about, so the log
/// carries one line per parameter for the life of the process rather than one per request. A DI
/// singleton rather than a static field: the process has exactly one host, so it is still once per
/// process, and a test host gets its own instance instead of inheriting another test's state.
/// </summary>
public sealed class IgnoredParameterLog
{
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    /// <summary>True the first time a given parameter name is seen, false forever after. Thread-safe.</summary>
    public bool ShouldWarn(string parameter) => _warned.TryAdd(parameter, 0);
}

public static class ChatCompletionsEndpoints
{
    public static IEndpointRouteBuilder MapNpuBridgeChat(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/v1/chat/completions", ChatCompletionsEndpoint.PostAsync);
        return app;
    }
}

/// <summary>
/// <c>POST /v1/chat/completions</c>. Phase one — body, validation, readiness, placement, rendering —
/// belongs to <see cref="ChatRequestPreparer"/> and is shared by both response shapes; this type then
/// either hands a <c>stream: true</c> request to <see cref="ChatCompletionsStreamEndpoint"/> or runs
/// the non-streaming phase two itself: a single generation on a fresh context, shaped into one OpenAI
/// response. The context cache (chunk 5), tool emulation (chunk 7) and the request scheduler (chunk 8)
/// are all deliberately absent: every request creates and disposes its own context.
/// </summary>
internal sealed class ChatCompletionsEndpoint
{
    // Never instantiated: the type exists so the handler has an ILogger<T> category of its own, which a
    // static class cannot have (a static type is not a legal generic type argument).
    private ChatCompletionsEndpoint()
    {
    }

    public static async Task<IResult> PostAsync(
        HttpContext http,
        BackendLifecycle lifecycle,
        BridgeOptions options,
        StreamingOptions streaming,
        IgnoredParameterLog ignoredLog,
        TimeProvider time,
        ILogger<ChatCompletionsEndpoint> logger,
        ILogger<ChatCompletionsStreamEndpoint> streamLogger)
    {
        var preparation = await ChatRequestPreparer
            .PrepareAsync(http, lifecycle, options, ignoredLog, logger)
            .ConfigureAwait(false);

        // Preparation is shared, and it fails before a single byte is written — so a streamed request
        // that fails it still gets the ordinary JSON error with its ordinary status code. Only once the
        // stream's headers are committed does error handling have to move into the stream.
        if (preparation.IsFailed)
        {
            return preparation.Failure;
        }

        var prepared = preparation.Prepared;

        // The branch. Everything above ran identically for both shapes; everything below is the
        // single-JSON-object generation phase, whose SSE sibling lives in ChatCompletionsStreamEndpoint.
        // The streaming phase usually writes the response itself and leaves nothing to return; it
        // returns a result only when it failed before writing a byte, and then the status line is still
        // ours to set, so the client gets the ordinary error instead of a 200 stream that says "stop".
        if (prepared.Request.Stream == true)
        {
            return await ChatCompletionsStreamEndpoint
                .StreamAsync(http, prepared, options, streaming, time, streamLogger)
                .ConfigureAwait(false) ?? Results.Empty;
        }

        var requestId = prepared.RequestId;
        var backendName = prepared.BackendName;
        var backend = prepared.Backend;
        var promptChars = prepared.PromptChars;

        IModelContext? context = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            long firstTokenTicks = 0;
            var callbacks = 0;

            // 7. A fresh context per request, disposed in the finally: D11 says a context whose generation
            // did not end Complete has indeterminate state, and there is no cache to return it to yet.
            context = backend.CreateContext(prepared.NativeSystem);

            var result = await backend.GenerateAsync(
                context,
                prepared.Rendered.Prompt,
                prepared.Sampling,
                _ =>
                {
                    if (Interlocked.Increment(ref callbacks) == 1)
                    {
                        Interlocked.Exchange(ref firstTokenTicks, stopwatch.ElapsedTicks);
                    }
                },
                http.RequestAborted).ConfigureAwait(false);

            stopwatch.Stop();
            var totalMs = stopwatch.Elapsed.TotalMilliseconds;
            var ttftMs = callbacks == 0 ? totalMs : firstTokenTicks * 1000.0 / Stopwatch.Frequency;

            if (options.Verbose)
            {
                logger.LogInformation("req={RequestId} raw model output ({Status}):\n---- output ----\n{Text}\n---- end ----",
                    requestId, result.Status, result.Text);
            }

            // 8. Status → response or error. The mapping itself lives in GenerationFailure, shared with
            // the streaming path so the two shapes cannot describe the same condition differently.
            if (result.Status == GenerationStatus.Cancelled && http.RequestAborted.IsCancellationRequested)
            {
                // The client is gone; there is nobody to send a body to and this is not an error.
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: 0);
                return Results.Empty;
            }

            if (GenerationFailure.FromStatus(result) is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: failure.StatusCode);
                return failure.ToResult();
            }

            var filtered = result.Status != GenerationStatus.Complete;
            var content = filtered ? string.Empty : result.Text;
            var finishReason = filtered ? "content_filter" : "stop";

            // 9. Usage. Both numbers are chars/4 estimates, not a tokenizer's output; the progress
            // callback count is not usable (Phi Silica batches several tokens per callback).
            var promptTokens = ChatRequestMetrics.EstimateTokens(promptChars);
            var completionTokens = ChatRequestMetrics.EstimateTokens(content.Length);

            var body = new ChatCompletionResponse(
                Id: requestId,
                Created: time.GetUtcNow().ToUnixTimeSeconds(),
                Model: prepared.Request.Model ?? backend.ModelId,
                Choices: [new ChatCompletionChoice(0, new ChatCompletionResponseMessage("assistant", content), finishReason)],
                Usage: new CompletionUsage(promptTokens, completionTokens, promptTokens + completionTokens));

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs);

            return Results.Json(body, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = GenerationFailure.FromException(ex);
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-", httpStatus: failure.StatusCode);
            return failure.ToResult();
        }
        finally
        {
            context?.Dispose();
        }
    }
}
