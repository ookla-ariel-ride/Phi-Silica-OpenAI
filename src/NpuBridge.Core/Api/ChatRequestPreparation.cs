using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;
using NpuBridge.Prompting;
using NpuBridge.Tools;

namespace NpuBridge.Api;

/// <summary>
/// Everything the generation phase needs, once preparation has decided the request is servable. Built
/// by <see cref="ChatRequestPreparer.PrepareAsync"/> (<c>/v1/chat/completions</c>) and, since chunk 8
/// task 3, by <see cref="ChatRequestPreparer.PrepareForCompletionAsync"/> (<c>/v1/completions</c>) —
/// both funnel through the same private core, so a streamed and a non-streamed request, and a chat and
/// a completions request, all get the identical shape of value, which is the point of the split.
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
/// <param name="Tools">
/// The tools this request offered, or null when it offered none, <c>tool_choice</c> was <c>none</c>,
/// or <c>--tool-emulation off</c>. Non-null is the single question phase two asks: it means the
/// instruction block is in the system text and the reply must be buffered whole and parsed before
/// anything is emitted, because nothing can tell a tool call from prose until the model has stopped.
/// </param>
internal sealed record PreparedChatRequest(
    string RequestId,
    string BackendName,
    ChatCompletionRequest Request,
    ILanguageModelBackend Backend,
    RenderedPrompt Rendered,
    string? NativeSystem,
    SamplingOptions? Sampling,
    OutputLimits Limits,
    int PromptChars,
    ToolCatalog? Tools = null,
    string? ToolInstructions = null);

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
/// Phase one of <c>POST /v1/chat/completions</c> (and, since chunk 8 task 3, of <c>POST
/// /v1/completions</c>): body, validation, readiness, ignored-parameter warnings, system-prompt
/// placement, rendering and sampling. Everything that is identical whether the reply is a single JSON
/// object or a stream of SSE chunks, so that the two paths cannot drift on the pieces that are
/// cheapest to get subtly different. It never creates a context and never writes a body: it returns
/// either a ready-made failure result or the value phase two generates from.
///
/// <see cref="PrepareAsync"/> and <see cref="PrepareForCompletionAsync"/> are the two thin callers:
/// each reads its own wire shape (a <see cref="ChatCompletionRequest"/> with <c>messages[]</c>, or a
/// <see cref="CompletionRequest"/> whose <c>prompt</c> is wrapped into one user message) and then
/// shares everything from the model-id check onward through <see cref="PrepareCoreAsync"/>, including
/// <see cref="PreparedChatRequest.Limits"/>: both callers synthesise a real
/// <see cref="ChatCompletionRequest"/> (completions leaves <c>MaxCompletionTokens</c> null on it, since
/// its wire shape has no such field) and the core builds <see cref="OutputLimits"/> off that one
/// request the same way for both, via <see cref="OutputLimits.From"/> — which is the only overload that
/// also normalises <c>stop</c> (drops an empty entry, D53's "never contains an empty entry" invariant).
/// An earlier version of this split gave completions its own <see cref="OutputLimits.Create"/> call,
/// which skipped that normalisation and was the only behavioural difference the split introduced (fix
/// round 1, finding 1); there is no longer any per-caller code here at all.
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

        return await PrepareCoreAsync(http, lifecycle, options, ignoredLog, logger, requestId, backendName, request)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Phase one of <c>POST /v1/completions</c> (chunk 8 task 3, PLAN §2.2): <c>prompt</c> must be a
    /// string or a single-element array — a controller ruling makes more than one element a 400 rather
    /// than silently taking the first, since a single-worker bridge cannot serve the multiple choices
    /// real OpenAI would batch it into. The one valid prompt is wrapped into a single user message and
    /// handed to the same <see cref="PrepareCoreAsync"/> chat's <see cref="PrepareAsync"/> uses, so
    /// readiness, placement, rendering and every later phase treat it exactly like a one-message chat
    /// request. <c>tools</c> do not exist on this wire shape, so the synthesised
    /// <see cref="ChatCompletionRequest"/> never carries any.
    /// </summary>
    public static async Task<ChatRequestPreparation> PrepareForCompletionAsync(
        HttpContext http,
        BackendLifecycle lifecycle,
        BridgeOptions options,
        IgnoredParameterLog ignoredLog,
        ILogger logger)
    {
        var requestId = ChatCompletionId.NewId();
        var backendName = options.Backend.ToConfigName();

        CompletionRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<CompletionRequest>(JsonDefaults.Options, http.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return ChatRequestPreparation.Failed(OpenAiError.BadRequest($"Request body is not valid JSON: {ex.Message}"));
        }
        catch (InvalidOperationException)
        {
            return ChatRequestPreparation.Failed(
                OpenAiError.BadRequest("Request body must be JSON (Content-Type: application/json)."));
        }

        if (request is null)
        {
            return ChatRequestPreparation.Failed(
                OpenAiError.BadRequest("Request body is required and must be a JSON object."));
        }

        if (request.Prompt is null || request.Prompt.Count == 0)
        {
            return ChatRequestPreparation.Failed(
                OpenAiError.BadRequest("prompt is required and must be a string or a single-element array of strings.",
                    code: "missing_prompt", param: "prompt"));
        }

        // Controller ruling: real OpenAI batches several prompts into several choices, which this
        // single-worker bridge cannot serve. Refusing is honest; silently taking the first element is
        // not, and would let a client believe every prompt it sent was answered.
        if (request.Prompt.Count > 1)
        {
            return ChatRequestPreparation.Failed(
                OpenAiError.BadRequest("prompt must be a string, or an array with exactly one element; multiple prompts are not supported.",
                    param: "prompt"));
        }

        var chatRequest = new ChatCompletionRequest(
            Model: request.Model,
            Messages: [new ChatMessage("user", ChatMessageContent.FromText(request.Prompt[0]), null, null)],
            Stream: request.Stream,
            N: request.N,
            Temperature: request.Temperature,
            TopP: request.TopP,
            TopK: null,
            MaxTokens: request.MaxTokens,
            MaxCompletionTokens: null,
            Stop: request.Stop,
            Tools: null,
            ToolChoice: null,
            Logprobs: null,
            ResponseFormat: null,
            // These four map straight onto fields the shared validator already checks (fix round 1,
            // finding 5), so carrying them through is what gets them onto the once-per-process warning
            // for free -- no change to ChatCompletionRequestValidator at all.
            Seed: request.Seed,
            PresencePenalty: request.PresencePenalty,
            FrequencyPenalty: request.FrequencyPenalty,
            User: request.User,
            StreamOptions: request.StreamOptions);

        // The remaining legacy-only fields have no equivalent on ChatCompletionRequest to ride along
        // on (this endpoint's integer `logprobs` is not chat's boolean one), so they get their own
        // once-per-process warning via the same IgnoredParameterLog the shared validator's findings
        // use (fix round 1, finding 5) rather than vanishing silently at deserialisation.
        var legacyOnlyIgnored = new List<string>(4);
        if (request.Echo is not null) legacyOnlyIgnored.Add("echo");
        if (request.BestOf is not null) legacyOnlyIgnored.Add("best_of");
        if (request.Suffix is not null) legacyOnlyIgnored.Add("suffix");
        if (request.Logprobs is not null) legacyOnlyIgnored.Add("logprobs");
        if (request.LogitBias is not null) legacyOnlyIgnored.Add("logit_bias");

        return await PrepareCoreAsync(http, lifecycle, options, ignoredLog, logger, requestId, backendName, chatRequest,
            legacyOnlyIgnored).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything from validation onward, shared by both wire shapes once each has produced an
    /// already-parsed <see cref="ChatCompletionRequest"/> (real, or synthesised from a
    /// <c>/v1/completions</c> prompt). There is no remaining per-caller step for building the request
    /// itself: <see cref="OutputLimits"/> is built here, off <paramref name="request"/>, the same way
    /// for both (fix round 1, finding 1). <paramref name="extraIgnoredParameters"/> is the one
    /// per-caller addition to the once-per-process ignored-parameter warning (fix round 1, finding 5):
    /// completions' legacy-only fields (<c>echo</c>, <c>best_of</c>, <c>suffix</c>, <c>logprobs</c>,
    /// <c>logit_bias</c>) have no field on <see cref="ChatCompletionRequest"/> to ride along on, so
    /// they cannot reach <see cref="ChatCompletionRequestValidator.Validate"/>'s own list; chat's
    /// caller passes none.
    /// </summary>
    private static async Task<ChatRequestPreparation> PrepareCoreAsync(
        HttpContext http,
        BackendLifecycle lifecycle,
        BridgeOptions options,
        IgnoredParameterLog ignoredLog,
        ILogger logger,
        string requestId,
        string backendName,
        ChatCompletionRequest request,
        IReadOnlyList<string>? extraIgnoredParameters = null)
    {
        // 2. Validation. The failure carries its own param/code; bind them by name — the failure record
        // orders them (Message, Param, Code) while OpenAiError.Result takes code before param.
        var validation = ChatCompletionRequestValidator.Validate(request, options.ToolEmulation);
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
        // the backend cannot apply them. extraIgnoredParameters (fix round 1, finding 5) rides the same
        // once-per-process IgnoredParameterLog as the validator's own findings; it is empty for chat.
        foreach (var parameter in validation.IgnoredParameters.Concat(extraIgnoredParameters ?? []))
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
            // 5a. Tools. The catalog is null unless the request offered usable tools, emulation is on
            // and tool_choice is not "none" -- and null is what phase two reads to mean "no buffering,
            // no parse", so every one of those three switches turns the whole feature off by the same
            // path. The instruction block goes into the system text, which is what puts it into the
            // conversation key: two requests offering different tools must not share a context, having
            // been told about different tools (D71's trap, named in CLAUDE.md).
            var toolChoice = ToolChoice.From(request.ToolChoice);
            var catalog = options.ToolEmulation ? ToolCatalog.From(request.Tools) : null;
            var toolInstructions = catalog is null
                ? null
                : ToolSchemaRenderer.Render(catalog, options.ToolSchema, toolChoice);

            // An empty block is "none": the renderer was asked to inject nothing, so there is nothing
            // for the model to follow and nothing for phase two to parse.
            if (string.IsNullOrEmpty(toolInstructions))
            {
                catalog = null;
            }

            // 6. Rendering, inside the guard. It cannot throw today -- validation guarantees non-null
            // messages and known roles -- but D49 records exactly that reasoning failing once already,
            // and chunk 7 adds tool-call rendering to this call. A throw here has to come out as an
            // OpenAI-shaped error, not a 500.
            var rendered = PromptTemplate.Render(request.Messages!, useNativeSystem, toolInstructions);

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
                PromptChars: promptChars,
                Tools: catalog,
                ToolInstructions: toolInstructions));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The same envelope a backend that throws during generation gets, from the same place, so a
            // client cannot tell the two apart by their shape -- it hand-built its own copy of this body
            // before, which is how two spellings of one failure come about.
            var backendFailure = GenerationFailure.FromException(ex);
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-", httpStatus: backendFailure.StatusCode);
            return ChatRequestPreparation.Failed(backendFailure.ToResult());
        }
    }
}

/// <summary>
/// The per-request log line and the token estimate, shared by both phases so a streamed request
/// reports the same numbers in the same format as a non-streamed one.
/// </summary>
internal static class ChatRequestMetrics
{
    /// <summary>
    /// One line per request. <c>http=0</c> means no response was sent because the client had already
    /// disconnected. <c>prompt_chars</c> is what was sent to the backend on this request (the tail, on a
    /// cache hit); <c>cache</c> is <c>hit</c>, <c>miss</c>, or <c>-</c> when the request failed before
    /// the lookup; <c>tail_turns</c> counts the turns rendered; <c>truncated_turns</c> the turns
    /// <c>--truncate-history</c> dropped. <c>queue_wait_ms</c> (chunk 8) is how long the attempt that
    /// produced this line waited behind <see cref="GenerationScheduler"/>'s one worker; it stays the
    /// default 0 on every call site that never reached the scheduler (preparation failures, a preflight
    /// refusal) rather than reporting a wait that was never measured.
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
        int truncatedTurns = 0,
        double queueWaitMs = 0)
    {
        var tokensPerSecond = totalMs is > 0 ? tokens * 1000.0 / totalMs.Value : 0;
        logger.LogInformation(
            "req={RequestId} backend={Backend} prompt_chars={PromptChars} cache={Cache} tail_turns={TailTurns} truncated_turns={TruncatedTurns} ttft_ms={TtftMs:F1} tokens={Tokens} tok_s={TokensPerSecond:F1} status={Status} finish={Finish} http={Http} queue_wait_ms={QueueWaitMs:F1}",
            requestId, backend, promptChars, cache, tailTurns, truncatedTurns, Math.Round(ttftMs, 1), tokens, Math.Round(tokensPerSecond, 1), status, finish, httpStatus, Math.Round(queueWaitMs, 1));
    }
}
