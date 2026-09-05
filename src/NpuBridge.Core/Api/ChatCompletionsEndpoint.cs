using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;
using NpuBridge.Prompting;

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
/// Non-streaming <c>POST /v1/chat/completions</c>. Validates, renders the messages into one prompt,
/// runs a single generation on a fresh context, and shapes the OpenAI response. Streaming (chunk 4),
/// the context cache (chunk 5), tool emulation (chunk 7) and the request scheduler (chunk 8) are all
/// deliberately absent: every request creates and disposes its own context.
/// </summary>
internal sealed class ChatCompletionsEndpoint
{
    /// <summary>Parameters that are only ignored when the backend cannot do sampling at all.</summary>
    private static readonly string[] SamplingParameters = ["temperature", "top_p", "top_k"];

    // Never instantiated: the type exists so the handler has an ILogger<T> category of its own, which a
    // static class cannot have (a static type is not a legal generic type argument).
    private ChatCompletionsEndpoint()
    {
    }

    public static async Task<IResult> PostAsync(
        HttpContext http,
        BackendLifecycle lifecycle,
        BridgeOptions options,
        IgnoredParameterLog ignoredLog,
        TimeProvider time,
        ILogger<ChatCompletionsEndpoint> logger)
    {
        var requestId = ChatCompletionId.NewId();
        var backendName = options.Backend.ToConfigName();

        // 1. Body. A malformed or non-JSON body is the client's fault, never a 500: read it here rather
        // than through model binding, whose failure shape is not the OpenAI error body.
        ChatCompletionRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<ChatCompletionRequest>(JsonDefaults.Options, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return OpenAiError.BadRequest($"Request body is not valid JSON: {ex.Message}");
        }
        catch (InvalidOperationException)
        {
            // Thrown for a missing or non-JSON Content-Type.
            return OpenAiError.BadRequest("Request body must be JSON (Content-Type: application/json).");
        }

        if (request is null)
        {
            return OpenAiError.BadRequest("Request body is required and must be a JSON object.");
        }

        // 2. Validation. The failure carries its own param/code; bind them by name — the failure record
        // orders them (Message, Param, Code) while OpenAiError.Result takes code before param.
        var validation = ChatCompletionRequestValidator.Validate(request);
        if (validation.Failure is { } failure)
        {
            LogRequest(logger, requestId, backendName, promptChars: 0, ttftMs: 0, tokens: 0,
                status: "invalid_request", finish: "-", httpStatus: StatusCodes.Status400BadRequest);
            return OpenAiError.Result(
                StatusCodes.Status400BadRequest,
                message: failure.Message,
                type: OpenAiError.InvalidRequest,
                code: failure.Code,
                param: failure.Param);
        }

        // 3. Readiness, before any work.
        var snapshot = lifecycle.Snapshot;
        if (snapshot.Kind != BackendStateKind.Ready)
        {
            if (snapshot.Kind != BackendStateKind.Failed)
            {
                http.Response.Headers.RetryAfter = "10";
            }

            LogRequest(logger, requestId, backendName, promptChars: 0, ttftMs: 0, tokens: 0,
                status: snapshot.Kind.ToString(), finish: "-", httpStatus: StatusCodes.Status503ServiceUnavailable);
            return OpenAiError.Result(
                StatusCodes.Status503ServiceUnavailable,
                $"Backend is {snapshot.Kind}. {snapshot.Error}".Trim(),
                OpenAiError.Server,
                code: snapshot.Kind == BackendStateKind.Failed ? "model_unavailable" : "model_loading");
        }

        var backend = lifecycle.Backend;
        var capabilities = backend.Capabilities;
        var samplingSupported = capabilities.HasFlag(BackendCapabilities.SamplingOptions);

        // 4. One warning per ignored parameter per process. Sampling parameters are only ignored when
        // the backend cannot apply them.
        foreach (var parameter in validation.IgnoredParameters)
        {
            if (samplingSupported && Array.IndexOf(SamplingParameters, parameter) >= 0)
            {
                continue;
            }

            if (ignoredLog.ShouldWarn(parameter))
            {
                logger.LogWarning("Request parameter {Parameter} is accepted but not implemented yet; it is ignored. This is logged once per process.",
                    parameter);
            }
        }

        // 5. System-prompt placement. Chosen here, enforced only after rendering: forcing `native` on
        // a backend with no native system context is a conflict solely for a request that actually
        // carries system text. A request with none needs no native context and is served normally --
        // rejecting it up front would reject every request on such a backend (Aion, chunk 6).
        var nativeSupported = capabilities.HasFlag(BackendCapabilities.SystemPromptContext);
        var useNativeSystem = options.SystemPromptPlacement != SystemPromptPlacement.Prompt && nativeSupported;

        var sampling = samplingSupported
            ? new SamplingOptions(request.Temperature, request.TopP, request.TopK)
            : null;

        var promptChars = 0;

        IModelContext? context = null;
        try
        {
            // 6. Rendering, inside the guard. It cannot throw today -- validation guarantees non-null
            // messages and known roles -- but D49 records exactly that reasoning failing once already,
            // and chunk 7 adds tool-call rendering to this call. A throw here has to come out as an
            // OpenAI-shaped error, not a 500.
            var rendered = PromptTemplate.Render(request.Messages!, useNativeSystem);

            if (options.SystemPromptPlacement == SystemPromptPlacement.Native
                && !nativeSupported
                && !string.IsNullOrEmpty(rendered.SystemText))
            {
                LogRequest(logger, requestId, backendName, promptChars: 0, ttftMs: 0, tokens: 0,
                    status: "invalid_request", finish: "-", httpStatus: StatusCodes.Status400BadRequest);
                return OpenAiError.BadRequest(
                    $"--system-prompt-placement native was requested but backend '{backend.ModelId}' has no native system-prompt context. Use auto or prompt.",
                    code: "system_prompt_placement_unsupported");
            }

            var nativeSystem = useNativeSystem ? rendered.SystemText : null;

            // Chars the model actually sees: the prompt string, plus the system text when it travels
            // separately through the native context.
            promptChars = rendered.Prompt.Length + (nativeSystem?.Length ?? 0);

            if (options.Verbose)
            {
                logger.LogInformation(
                    "req={RequestId} prompt (system placement: {Placement}):\n---- system ----\n{System}\n---- prompt ----\n{Prompt}\n---- end ----",
                    requestId,
                    string.IsNullOrEmpty(rendered.SystemText) ? "no system message"
                        : useNativeSystem ? "native context" : "folded into prompt",
                    rendered.SystemText ?? "(no system message)",
                    rendered.Prompt);
            }

            var stopwatch = Stopwatch.StartNew();
            long firstTokenTicks = 0;
            var callbacks = 0;

            // 7. A fresh context per request, disposed in the finally: D11 says a context whose generation
            // did not end Complete has indeterminate state, and there is no cache to return it to yet.
            context = backend.CreateContext(nativeSystem);

            var result = await backend.GenerateAsync(
                context,
                rendered.Prompt,
                sampling is null || sampling.IsEmpty ? null : sampling,
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

            // 8. Status → response or error.
            switch (result.Status)
            {
                case GenerationStatus.Complete:
                case GenerationStatus.ContentFiltered:
                case GenerationStatus.BlockedByPolicy:
                    break;

                case GenerationStatus.PromptLargerThanContext:
                    LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                        status: result.Status.ToString(), finish: "-", httpStatus: StatusCodes.Status400BadRequest);
                    return OpenAiError.BadRequest(
                        $"The prompt is longer than the model's context window. {result.Detail}".Trim(),
                        code: "context_length_exceeded");

                case GenerationStatus.Cancelled:
                    if (http.RequestAborted.IsCancellationRequested)
                    {
                        // The client is gone; there is nobody to send a body to and this is not an error.
                        LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                            status: result.Status.ToString(), finish: "-", httpStatus: 0);
                        return Results.Empty;
                    }

                    LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                        status: result.Status.ToString(), finish: "-", httpStatus: StatusCodes.Status502BadGateway);
                    return OpenAiError.Result(StatusCodes.Status502BadGateway,
                        $"Generation was cancelled by the backend. {result.Detail}".Trim(), OpenAiError.Server);

                case GenerationStatus.Error:
                default:
                    LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                        status: result.Status.ToString(), finish: "-", httpStatus: StatusCodes.Status502BadGateway);
                    return OpenAiError.Result(StatusCodes.Status502BadGateway,
                        $"The model failed to generate a response. {result.Detail}".Trim(), OpenAiError.Server);
            }

            var filtered = result.Status != GenerationStatus.Complete;
            var content = filtered ? string.Empty : result.Text;
            var finishReason = filtered ? "content_filter" : "stop";

            // 9. Usage. Both numbers are chars/4 estimates, not a tokenizer's output; the progress
            // callback count is not usable (Phi Silica batches several tokens per callback).
            var promptTokens = EstimateTokens(promptChars);
            var completionTokens = EstimateTokens(content.Length);

            var body = new ChatCompletionResponse(
                Id: requestId,
                Created: time.GetUtcNow().ToUnixTimeSeconds(),
                Model: request.Model ?? backend.ModelId,
                Choices: [new ChatCompletionChoice(0, new ChatCompletionResponseMessage("assistant", content), finishReason)],
                Usage: new CompletionUsage(promptTokens, completionTokens, promptTokens + completionTokens));

            LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs);

            return Results.Json(body, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRequest(logger, requestId, backendName, promptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-", httpStatus: StatusCodes.Status502BadGateway);
            return OpenAiError.Result(StatusCodes.Status502BadGateway,
                $"Backend threw: {ex.GetType().Name}: {ex.Message}", OpenAiError.Server, code: "backend_error");
        }
        finally
        {
            context?.Dispose();
        }
    }

    /// <summary>Documented estimate: four characters per token, rounded up. Never a real tokenizer.</summary>
    private static int EstimateTokens(int chars) => (chars + 3) / 4;

    /// <summary>
    /// One line per request. <c>http=0</c> means no response was sent because the client had already
    /// disconnected. Queue wait and cache hits are deliberately absent: neither exists yet, and a
    /// hard-coded <c>queue_wait_ms=0</c> would read as a measurement.
    /// </summary>
    private static void LogRequest(
        ILogger logger,
        string requestId,
        string backend,
        int promptChars,
        double ttftMs,
        int tokens,
        string status,
        string finish,
        int httpStatus,
        double? totalMs = null)
    {
        var tokensPerSecond = totalMs is > 0 ? tokens * 1000.0 / totalMs.Value : 0;
        logger.LogInformation(
            "req={RequestId} backend={Backend} prompt_chars={PromptChars} ttft_ms={TtftMs:F1} tokens={Tokens} tok_s={TokensPerSecond:F1} status={Status} finish={Finish} http={Http}",
            requestId, backend, promptChars, Math.Round(ttftMs, 1), tokens, Math.Round(tokensPerSecond, 1), status, finish, httpStatus);
    }
}
