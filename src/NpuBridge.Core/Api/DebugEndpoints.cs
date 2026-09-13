using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NpuBridge.Backends;

namespace NpuBridge.Api;

/// <summary>Request body for <c>POST /debug/generate</c>.</summary>
public sealed record DebugGenerateRequest(
    string? Prompt,
    string? System,
    float? Temperature,
    float? TopP,
    int? TopK);

/// <summary>Request body for <c>POST /debug/tokenize</c>.</summary>
public sealed record DebugTokenizeRequest(string? Text);

/// <summary>The backend's token count of a literal text (D80). Not an OpenAI shape; for diagnostics and the smoke test.</summary>
/// <param name="Counter">Which counter answered: <c>phi-3</c> or <c>chars/4</c>.</param>
/// <param name="Chars">UTF-16 length of the text, the unit the preflight's answer is reported in.</param>
/// <param name="Tokens">Tokens as the backend counts them, without framing tokens such as BOS.</param>
public sealed record DebugTokenizeResponse(string Counter, int Chars, int Tokens);

/// <summary>Raw backend result with timing. Not an OpenAI shape; for diagnostics and the smoke test.</summary>
/// <param name="ProgressCallbacks">Number of delta callbacks. On Phi Silica each may carry several tokens.</param>
/// <param name="Chars">Characters generated; <c>chars / 4</c> is the usual token estimate.</param>
/// <param name="UsablePromptChars">Backend preflight: how many leading prompt chars fit the context window (null = unsupported).</param>
public sealed record DebugGenerateResponse(
    string Text,
    string Status,
    string? Detail,
    int ProgressCallbacks,
    int Chars,
    int PromptChars,
    int? UsablePromptChars,
    double TtftMs,
    double TotalMs,
    double? CallbacksPerSecond,
    string ContextId);

/// <summary>
/// <c>POST /debug/generate</c>: one prompt straight into the backend, bypassing message flattening and
/// the context cache. Exists so the adapter can be exercised on real hardware before the OpenAI
/// endpoints land, and afterwards to debug what the model does with a literal prompt. Loopback callers
/// only: it is not part of the OpenAI surface. Chunk 8 (D40's deferral, superseded) put it behind the
/// same <see cref="GenerationScheduler"/> as everything else: its context is not created until the
/// job's turn, so it queues behind an ordinary chat request rather than jumping ahead of it.
/// </summary>
public static class DebugEndpoints
{
    public static IEndpointRouteBuilder MapNpuBridgeDebug(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/debug/generate", GenerateAsync);
        app.MapPost("/debug/tokenize", Tokenize);
        return app;
    }

    /// <summary>
    /// The backend's token count of a literal text, so the smoke script can put the preflight's boundary
    /// beside the tokenizer's count per build (D80). Needs no model: it answers while the backend is
    /// still loading. Loopback only, like everything under <c>/debug</c>.
    /// </summary>
    private static IResult Tokenize(DebugTokenizeRequest? request, BackendLifecycle lifecycle, HttpContext http)
    {
        var remote = http.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return OpenAiError.Result(StatusCodes.Status403Forbidden, "The debug endpoint accepts loopback connections only.", OpenAiError.InvalidRequest, code: "loopback_only");
        }

        if (request?.Text is null)
        {
            return OpenAiError.BadRequest("Body must be JSON with a 'text' string.", code: "missing_text", param: "text");
        }

        var counter = lifecycle.Backend.TokenCounter;
        return Results.Json(new DebugTokenizeResponse(counter.Name, request.Text.Length, counter.Count(request.Text)), JsonDefaults.Options);
    }

    private static async Task<IResult> GenerateAsync(
        DebugGenerateRequest? request,
        BackendLifecycle lifecycle,
        GenerationScheduler scheduler,
        GenerationHealth generationHealth,
        HttpContext http)
    {
        // Fail closed: an unknown remote address is not a loopback address.
        var remote = http.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return OpenAiError.Result(StatusCodes.Status403Forbidden, "The debug endpoint accepts loopback connections only.", OpenAiError.InvalidRequest, code: "loopback_only");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            return OpenAiError.BadRequest("Body must be JSON with a non-empty 'prompt'.", code: "missing_prompt", param: "prompt");
        }

        var snapshot = lifecycle.Snapshot;
        if (snapshot.Kind != BackendStateKind.Ready)
        {
            return OpenAiError.Result(StatusCodes.Status503ServiceUnavailable,
                $"Backend is {snapshot.Kind}. {snapshot.Error}".Trim(), OpenAiError.Server,
                code: snapshot.Kind == BackendStateKind.Failed ? "model_unavailable" : "model_loading");
        }

        var backend = lifecycle.Backend;
        var sampling = new SamplingOptions(request.Temperature, request.TopP, request.TopK);

        // Chunk 8, task-2-brief.md integration decision 5: this endpoint used to create its context
        // outside any queueing (D40's deferral, and the docs/FUTURE.md chunk 2 entry it names -- both
        // superseded now). The context must not exist until the job's turn, so creation moves inside
        // the scheduled closure along with the generation it was always paired with; the whole
        // try/catch/finally that used to run inline now runs only once the scheduler's one worker
        // reaches this job, and its result is the IResult this method already returned.
        var scheduled = await scheduler.ScheduleAsync(async ct =>
        {
            var stopwatch = Stopwatch.StartNew();
            long firstTokenTicks = 0;
            var callbacks = 0;

            IModelContext? context = null;
            var generationAttempted = false;
            try
            {
                var systemTextFailure = SystemTextGuard.RefusalFor(backend, request.System, includesToolDefinitions: false);
                if (systemTextFailure is not null)
                {
                    return systemTextFailure.ToResult();
                }

                generationAttempted = true;
                context = backend.CreateContext(request.System);
                var usable = backend.GetUsablePromptLength(context, request.Prompt);

                var result = await backend.GenerateAsync(
                    context,
                    request.Prompt,
                    sampling.IsEmpty ? null : sampling,
                    _ =>
                    {
                        if (Interlocked.Increment(ref callbacks) == 1)
                        {
                            Interlocked.Exchange(ref firstTokenTicks, stopwatch.ElapsedTicks);
                        }
                    },
                    ct);

                stopwatch.Stop();
                var totalMs = stopwatch.Elapsed.TotalMilliseconds;
                if (!http.RequestAborted.IsCancellationRequested)
                {
                    _ = GenerationOutcome.Classify(result, cancelledByCut: false, health: generationHealth, durationMs: totalMs);
                }
                var ttftMs = callbacks == 0 ? totalMs : firstTokenTicks * 1000.0 / Stopwatch.Frequency;
                var decodeMs = totalMs - ttftMs;
                double? cps = callbacks > 1 && decodeMs > 0 ? (callbacks - 1) * 1000.0 / decodeMs : null;

                var body = new DebugGenerateResponse(
                    result.Text,
                    result.Status.ToString(),
                    result.Detail,
                    callbacks,
                    result.Text.Length,
                    request.Prompt.Length,
                    usable,
                    Math.Round(ttftMs, 1),
                    Math.Round(totalMs, 1),
                    cps is null ? null : Math.Round(cps.Value, 2),
                    context.Id);

                return (IResult)Results.Json(body, JsonDefaults.Options);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return GenerationFailure.FromException(
                    ex,
                    generationAttempted ? generationHealth : null,
                    stopwatch.Elapsed.TotalMilliseconds).ToResult();
            }
            finally
            {
                context?.Dispose();
            }
        }, http.RequestAborted).ConfigureAwait(false);

        // The same classification both /v1/chat/completions shapes use (fix round 1, Finding 4): the
        // three callers spelled a full queue and a scheduler shutdown out separately and had already
        // drifted apart on day one, which is the drift D81 exists to prevent one level up.
        // clientAlreadyGone is false explicitly rather than by default: this diagnostic endpoint has no
        // streamed shape and no client worth sparing a status line for, so unlike the two OpenAI
        // endpoints it does not answer an aborted caller with silence.
        var admission = SchedulerAdmission.Classify(scheduled, clientAlreadyGone: false);
        if (admission == SchedulerOutcome.Completed)
        {
            return scheduled.Result!;
        }

        var failure = SchedulerAdmission.FailureFor(admission, scheduled.RetryAfterSeconds, generationHealth);
        SchedulerAdmission.ApplyRetryAfter(http.Response, admission, scheduled.RetryAfterSeconds);
        return failure.ToResult();
    }
}
