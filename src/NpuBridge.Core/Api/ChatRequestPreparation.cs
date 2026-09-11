using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;
using NpuBridge.Prompting;

namespace NpuBridge.Api;

/// <summary>
/// Everything the generation phase needs, once preparation has decided the request is servable. Built
/// only by <see cref="ChatRequestPreparer.PrepareAsync"/>; a streamed and a non-streamed request get
/// the identical value, which is the point of the split.
/// </summary>
/// <param name="RequestId">The <c>chatcmpl-</c> id, already allocated: it is on the failure log lines too.</param>
/// <param name="BackendName">Configured backend name, for the per-request log line only.</param>
/// <param name="Request">The deserialized, validated body.</param>
/// <param name="Backend">The ready backend from the lifecycle snapshot.</param>
/// <param name="Rendered">The prompt template's output, kept whole so callers can log the placement.</param>
/// <param name="NativeSystem">System text to hand to <c>CreateContext</c>, or null when it was folded into the prompt.</param>
/// <param name="Sampling">Already normalised: null when the backend cannot sample or the request set nothing.</param>
/// <param name="Limits">The client-side cut: <c>max_tokens</c>/<c>max_completion_tokens</c> and <c>stop</c>.</param>
/// <param name="PromptChars">Characters the model actually sees: the prompt, plus native system text.</param>
internal sealed record PreparedChatRequest(
    string RequestId,
    string BackendName,
    ChatCompletionRequest Request,
    ILanguageModelBackend Backend,
    RenderedPrompt Rendered,
    string? NativeSystem,
    SamplingOptions? Sampling,
    OutputLimits Limits,
    int PromptChars);

/// <summary>
/// Outcome of preparation: either a <see cref="Prepared"/> request or a <see cref="Failure"/> that is
/// already the exact <see cref="IResult"/> to return. Mirrors
/// <see cref="ChatCompletionValidationResult"/>'s shape — one of the two is set, never both — so the
/// caller makes no decisions about error bodies, only about what to do with a good request.
/// </summary>
internal sealed record ChatRequestPreparation(PreparedChatRequest? Prepared, IResult? Failure)
{
    /// <summary>True when <see cref="Failure"/> must be returned as-is; false guarantees <see cref="Prepared"/>.</summary>
    [MemberNotNullWhen(true, nameof(Failure))]
    [MemberNotNullWhen(false, nameof(Prepared))]
    public bool IsFailed => Failure is not null;

    public static ChatRequestPreparation Ready(PreparedChatRequest prepared) => new(prepared, null);

    public static ChatRequestPreparation Failed(IResult failure) => new(null, failure);
}

/// <summary>
/// Phase one of <c>POST /v1/chat/completions</c>: body, validation, readiness, ignored-parameter
/// warnings, system-prompt placement, rendering and sampling. Everything that is identical whether the
/// reply is a single JSON object or a stream of SSE chunks, so that the two paths cannot drift on the
/// pieces that are cheapest to get subtly different. It never creates a context and never writes a
/// body: it returns either a ready-made failure result or the value phase two generates from.
///
/// The ordering here is load-bearing. Validation before readiness before placement, and — D50 —
/// rendering before the forced-placement conflict check, because that conflict is a property of the
/// request (does it carry system text?) rather than of the option alone.
/// </summary>
internal static class ChatRequestPreparer
{
    /// <summary>Parameters that are only ignored when the backend cannot do sampling at all.</summary>
    private static readonly string[] SamplingParameters = ["temperature", "top_p", "top_k"];

    public static async Task<ChatRequestPreparation> PrepareAsync(
        HttpContext http,
        BackendLifecycle lifecycle,
        BridgeOptions options,
        IgnoredParameterLog ignoredLog,
        ILogger logger)
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
            return ChatRequestPreparation.Failed(OpenAiError.BadRequest($"Request body is not valid JSON: {ex.Message}"));
        }
        catch (InvalidOperationException)
        {
            // Thrown for a missing or non-JSON Content-Type.
            return ChatRequestPreparation.Failed(
                OpenAiError.BadRequest("Request body must be JSON (Content-Type: application/json)."));
        }

        if (request is null)
        {
            return ChatRequestPreparation.Failed(
                OpenAiError.BadRequest("Request body is required and must be a JSON object."));
        }

        // 2. Validation. The failure carries its own param/code; bind them by name — the failure record
        // orders them (Message, Param, Code) while OpenAiError.Result takes code before param.
        var validation = ChatCompletionRequestValidator.Validate(request);
        if (validation.Failure is { } failure)
        {
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars: 0, ttftMs: 0, tokens: 0,
                status: "invalid_request", finish: "-", httpStatus: StatusCodes.Status400BadRequest);
            return ChatRequestPreparation.Failed(OpenAiError.Result(
                StatusCodes.Status400BadRequest,
                message: failure.Message,
                type: OpenAiError.InvalidRequest,
                code: failure.Code,
                param: failure.Param));
        }

        // 2a. The model must be the one this process serves. OpenAI answers any other id with a 404
        // and this code; the bridge used to echo whatever id the client sent, which let a client
        // asking for a cloud model believe it had been served by one (D77). Checked before readiness,
        // like validation: it is a property of the request, and the served id is known while loading.
        var servedModel = lifecycle.Backend.ModelId;
        if (!string.Equals(request.Model, servedModel, StringComparison.OrdinalIgnoreCase))
        {
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars: 0, ttftMs: 0, tokens: 0,
                status: "model_not_found", finish: "-", httpStatus: StatusCodes.Status404NotFound);
            return ChatRequestPreparation.Failed(OpenAiError.NotFoundResult(
                $"The model '{request.Model}' does not exist. This server exposes '{servedModel}'.",
                code: "model_not_found"));
        }

        // 3. Readiness, before any work.
        var snapshot = lifecycle.Snapshot;
        if (snapshot.Kind != BackendStateKind.Ready)
        {
            if (snapshot.Kind != BackendStateKind.Failed)
            {
                http.Response.Headers.RetryAfter = "10";
            }

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars: 0, ttftMs: 0, tokens: 0,
                status: snapshot.Kind.ToString(), finish: "-", httpStatus: StatusCodes.Status503ServiceUnavailable);
            return ChatRequestPreparation.Failed(OpenAiError.Result(
                StatusCodes.Status503ServiceUnavailable,
                $"Backend is {snapshot.Kind}. {snapshot.Error}".Trim(),
                OpenAiError.Server,
                code: snapshot.Kind == BackendStateKind.Failed ? "model_unavailable" : "model_loading"));
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
                logger.LogWarning("Request parameter {Parameter} is accepted and ignored: the active backend cannot apply it, or the bridge does not implement it yet. This is logged once per process.",
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
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars: 0, ttftMs: 0, tokens: 0,
                    status: "invalid_request", finish: "-", httpStatus: StatusCodes.Status400BadRequest);
                return ChatRequestPreparation.Failed(OpenAiError.BadRequest(
                    $"--system-prompt-placement native was requested but backend '{backend.ModelId}' has no native system-prompt context. Use auto or prompt.",
                    code: "system_prompt_placement_unsupported"));
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

            return ChatRequestPreparation.Ready(new PreparedChatRequest(
                RequestId: requestId,
                BackendName: backendName,
                Request: request,
                Backend: backend,
                Rendered: rendered,
                NativeSystem: nativeSystem,
                Sampling: sampling is null || sampling.IsEmpty ? null : sampling,
                Limits: OutputLimits.From(request, backend.TokenCounter),
                PromptChars: promptChars));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-", httpStatus: StatusCodes.Status502BadGateway);
            return ChatRequestPreparation.Failed(OpenAiError.Result(StatusCodes.Status502BadGateway,
                $"Backend threw: {ex.GetType().Name}: {ex.Message}", OpenAiError.Server, code: "backend_error"));
        }
    }
}

/// <summary>
/// The per-request log line and the token estimate, shared by both phases so a streamed request
/// reports the same numbers in the same format as a non-streamed one.
/// </summary>
internal static class ChatRequestMetrics
{
    /// <summary>Characters per token in every estimate this process makes: usage, the cut (D53), the pressure check.</summary>
    public const int CharsPerToken = 4;

    /// <summary>Documented estimate: four characters per token, rounded up. Never a real tokenizer.</summary>
    public static int EstimateTokens(int chars) => (chars + CharsPerToken - 1) / CharsPerToken;

    /// <summary>
    /// One line per request. <c>http=0</c> means no response was sent because the client had already
    /// disconnected. <c>prompt_chars</c> is what was sent to the backend on this request (the tail, on a
    /// cache hit); <c>cache</c> is <c>hit</c>, <c>miss</c>, or <c>-</c> when the request failed before
    /// the lookup; <c>tail_turns</c> counts the turns rendered; <c>truncated_turns</c> the turns
    /// <c>--truncate-history</c> dropped. Queue wait is deliberately absent: the scheduler does not exist
    /// yet, and a hard-coded <c>queue_wait_ms=0</c> would read as a measurement.
    /// </summary>
    public static void LogRequest(
        ILogger logger,
        string requestId,
        string backend,
        int promptChars,
        double ttftMs,
        int tokens,
        string status,
        string finish,
        int httpStatus,
        double? totalMs = null,
        string cache = "-",
        int tailTurns = 0,
        int truncatedTurns = 0)
    {
        var tokensPerSecond = totalMs is > 0 ? tokens * 1000.0 / totalMs.Value : 0;
        logger.LogInformation(
            "req={RequestId} backend={Backend} prompt_chars={PromptChars} cache={Cache} tail_turns={TailTurns} truncated_turns={TruncatedTurns} ttft_ms={TtftMs:F1} tokens={Tokens} tok_s={TokensPerSecond:F1} status={Status} finish={Finish} http={Http}",
            requestId, backend, promptChars, cache, tailTurns, truncatedTurns, Math.Round(ttftMs, 1), tokens, Math.Round(tokensPerSecond, 1), status, finish, httpStatus);
    }
}
