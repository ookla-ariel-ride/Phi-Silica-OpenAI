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

        var limits = prepared.Limits;

        // Cancelled either by the client going away or by the client-side cut deciding it has enough
        // text. Only the latter needs a source of the handler's own; the former arrives through the link.
        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);

        IModelContext? context = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            long firstTokenTicks = 0;
            var callbacks = 0;

            // Watches the text as it arrives purely to decide when to stop the generation early; the
            // authoritative cut is applied below to the text the backend finally reports, with the same
            // OutputCutter, so the answer does not depend on which deltas the watcher happened to see.
            // Null when the request set no limits, which is the ordinary case and costs nothing.
            var watcher = limits.IsEmpty ? null : new OutputCutter(limits);

            // Set when this handler cancels the generation because the watcher tripped a limit, and
            // read when the status comes back: a Cancelled this handler asked for is the cut, any
            // other Cancelled is a failure. Recorded as a fact rather than inferred from the cutter
            // afterwards, because the whole-text cut below can commit a cap the watcher never
            // cancelled for, and the stream reads a different cutter — so inferring it made the two
            // shapes answer the same backend status differently.
            var cancelledByCut = 0;

            // How the callback tells this task that the cut fired. The callback never cancels anything
            // itself: it runs on the backend's thread, and cancelling from there is wrong twice over. A
            // straight Cancel() can complete the generation's await inline and re-enter the adapter while
            // it is still inside this callback (Phi Silica then spins draining a callback that cannot
            // finish until we return); and CancelAfter(0) moves the cancel onto a timer thread, where a
            // throwing registration -- CsWinRT's IAsyncInfo.Cancel() on the live operation is one -- is
            // rethrown with nothing above it to catch it, and the process terminates. So the callback
            // sets this and the request task, awaiting below, cancels on its own thread inside a try.
            // RunContinuationsAsynchronously keeps that continuation off the callback thread too.
            var cutSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // 7. A fresh context per request, disposed in the finally: D11 says a context whose generation
            // did not end Complete has indeterminate state, and there is no cache to return it to yet.
            context = backend.CreateContext(prepared.NativeSystem);

            var generation = backend.GenerateAsync(
                context,
                prepared.Rendered.Prompt,
                prepared.Sampling,
                delta =>
                {
                    if (Interlocked.Increment(ref callbacks) == 1)
                    {
                        Interlocked.Exchange(ref firstTokenTicks, stopwatch.ElapsedTicks);
                    }

                    if (watcher is null)
                    {
                        return;
                    }

                    bool cut;
                    lock (watcher)
                    {
                        if (watcher.IsCut)
                        {
                            return;
                        }

                        watcher.Accept(delta);
                        cut = watcher.IsCut;
                    }

                    if (cut)
                    {
                        Volatile.Write(ref cancelledByCut, 1);
                        cutSignal.TrySetResult();
                    }
                },
                generationCts.Token);

            // Whichever comes first. When it is the cut, cancel here -- on this thread, guarded -- and
            // then wait for the generation to end as it would have anyway. The overshoot is a delta or
            // two and costs nothing: the cut itself is applied to the final text below. Cancelling
            // faults when a registration on the token throws, and that is a Debug line here rather than
            // a failure, because the generation still ends, the text is still cut, and the finally still
            // disposes the context -- exactly as the streaming path treats its own cancel.
            if (!ReferenceEquals(await Task.WhenAny(generation, cutSignal.Task).ConfigureAwait(false), generation))
            {
                try
                {
                    await generationCts.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "req={RequestId} cancelling the generation at the cut threw; waiting for it to end anyway.",
                        requestId);
                }
            }

            var result = await generation.ConfigureAwait(false);

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

            // 8a. The client-side cut, before the status mapping. A generation this handler cancelled
            // because the cap or a stop string was reached comes back Cancelled, which is a 502 for any
            // other reason; the cut is what tells the two apart, and it is decided by the text rather
            // than by the status so that a cut which landed on the last delta reads the same either way.
            var cut = limits.Cut(result.Text);

            // Filtering outranks the cut: it is the one status that means "do not hand this text on",
            // and a cut is not a licence to. Spelled out by name rather than as "not Complete", because
            // Cancelled now reaches here legitimately whenever a limit fired.
            var filtered = result.Status is GenerationStatus.ContentFiltered or GenerationStatus.BlockedByPolicy;

            // Only a Cancelled may be attributed to the cut, and only one this handler asked for. Gating
            // the whole mapping on "a cut fired" suppressed every failure status, which was a regression
            // against main: an Error that used to be a 502 became HTTP 200 with truncated text and
            // finish_reason "length". And deciding "did the cut fire" from the whole-text cut was wrong
            // too: it can report a cap the watcher never cancelled for, so a backend that reported
            // Cancelled on its own was a success here and a failure on the stream.
            var selfCancelled = Volatile.Read(ref cancelledByCut) == 1 && result.Status is GenerationStatus.Cancelled;

            if (!filtered && !selfCancelled && GenerationFailure.FromStatus(result) is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: failure.StatusCode);
                return failure.ToResult();
            }

            var content = filtered ? string.Empty : cut.Text;
            var finishReason = filtered ? "content_filter" : cut.FinishReason ?? "stop";

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
