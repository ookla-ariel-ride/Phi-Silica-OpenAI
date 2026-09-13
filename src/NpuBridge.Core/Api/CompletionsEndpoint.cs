using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

public static class CompletionsEndpoints
{
    public static IEndpointRouteBuilder MapNpuBridgeCompletions(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/v1/completions", CompletionsEndpoint.PostAsync);
        return app;
    }
}

/// <summary>
/// <c>POST /v1/completions</c> (chunk 8 task 3, PLAN §2.2): the legacy text-completion shape.
/// <c>prompt</c> — a string, or a single-element array — is wrapped into one user message by
/// <see cref="ChatRequestPreparer.PrepareForCompletionAsync"/> and run through the identical pipeline
/// <c>/v1/chat/completions</c> uses from the model-id check onward: readiness, placement, rendering,
/// <see cref="ConversationSession"/>/<see cref="ContextCache"/>, the scheduler, the client-side cut,
/// <see cref="GenerationPipeline"/>, <see cref="GenerationOutcome"/> and
/// <see cref="ChatRequestMetrics.LogRequest"/> are all the exact same code the chat shape runs — a
/// second worker thread against the one shared model handle is exactly the bug chunk 8 exists to
/// close, wherever the request came from. <c>tools</c> do not exist on this wire shape at all, so
/// unlike <see cref="ChatCompletionsEndpoint"/> there is no tool-call branch to consider here.
///
/// The scheduler wiring mirrors <see cref="ChatCompletionsEndpoint"/> exactly, including the two
/// controller rulings task 2 already paid for: <see cref="ConversationSession.Acquire"/> runs inside
/// the scheduled closure (never outside it, so it cannot race a running generation for the one shared
/// handle), and the lease is published to this method's own <c>lease</c> variable the instant
/// <c>Acquire</c> hands it over, never carried back only on the closure's return value.
/// </summary>
internal sealed class CompletionsEndpoint
{
    // Never instantiated: the type exists so the handler has an ILogger<T> category of its own, which a
    // static class cannot have (a static type is not a legal generic type argument).
    private CompletionsEndpoint()
    {
    }

    public static async Task<IResult> PostAsync(
        HttpContext http,
        BackendLifecycle lifecycle,
        BridgeOptions options,
        StreamingOptions streaming,
        IgnoredParameterLog ignoredLog,
        ContextCache cache,
        GenerationScheduler scheduler,
        GenerationHealth generationHealth,
        TimeProvider time,
        ILogger<CompletionsEndpoint> logger,
        ILogger<CompletionsStreamEndpoint> streamLogger)
    {
        var preparation = await ChatRequestPreparer
            .PrepareForCompletionAsync(http, lifecycle, options, ignoredLog, logger)
            .ConfigureAwait(false);

        if (preparation.IsFailed)
        {
            return preparation.Failure;
        }

        var prepared = preparation.Prepared;

        if (prepared.Request.Stream == true)
        {
            return await CompletionsStreamEndpoint
                .StreamAsync(http, prepared, options, streaming, cache, scheduler, generationHealth, time, streamLogger)
                .ConfigureAwait(false) ?? Results.Empty;
        }

        var requestId = prepared.RequestId;
        var backendName = prepared.BackendName;
        var backend = prepared.Backend;

        var limits = prepared.Limits;
        var session = new ConversationSession(prepared, cache, options, logger);

        // Published from *inside* the scheduled closure, the instant Acquire hands one over -- see
        // ChatCompletionsEndpoint's fuller account of why (D43 + D51, chunk 8 fix round 1).
        ContextLease? lease = null;
        var queueWaitMs = 0.0;
        var generationAttempted = false;

        try
        {
            var scheduled = await scheduler.ScheduleAsync(async ct =>
            {
                var stopwatch = Stopwatch.StartNew();
                while (true)
                {
                    var acquisition = session.Acquire();
                    if (acquisition.Failure is { } refused)
                    {
                        return ChatAttemptResult.Refused(refused);
                    }

                    var attemptLease = acquisition.Lease!;
                    lease = attemptLease;

                    try
                    {
                        session.ApplyTruncationHeader(http.Response);

                        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var cancelledByCut = false;

                        var watcher = limits.IsEmpty ? null : new CutWatcher(limits);
                        var sink = DeltaSink.ToWatcher(stopwatch, watcher);

                        generationAttempted = true;
                        var generation = backend.GenerateAsync(
                            attemptLease.Context,
                            attemptLease.Prompt,
                            prepared.Sampling,
                            sink.OnDelta,
                            generationCts.Token);

                        if (watcher is not null)
                        {
                            await Task.WhenAny(generation, watcher.Signal).ConfigureAwait(false);
                            if (watcher.Signal.IsCompleted)
                            {
                                cancelledByCut = true;
                                await GenerationPipeline.CancelGuardedAsync(generationCts, logger, requestId, "at the cut").ConfigureAwait(false);
                            }
                        }

                        var result = await generation.ConfigureAwait(false);

                        if (result.Status == GenerationStatus.PromptLargerThanContext && session.TryDropOldestExchange())
                        {
                            attemptLease.Dispose();
                            continue;
                        }

                        var totalMs = stopwatch.Elapsed.TotalMilliseconds;
                        var ttftMs = sink.TtftMs(totalMs);
                        return ChatAttemptResult.Generated(result, cancelledByCut, ttftMs, totalMs);
                    }
                    catch
                    {
                        attemptLease.Dispose();
                        throw;
                    }
                }
            }, http.RequestAborted).ConfigureAwait(false);

            queueWaitMs = scheduled.QueueWait.TotalMilliseconds;

            var admission = SchedulerAdmission.Classify(scheduled, http.RequestAborted.IsCancellationRequested);
            if (admission == SchedulerOutcome.ClientGone)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                    status: scheduled.Kind.ToString(), finish: "-", httpStatus: 0,
                    truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
                return Results.Empty;
            }

            if (admission != SchedulerOutcome.Completed)
            {
                var schedulerFailure = SchedulerAdmission.FailureFor(admission, scheduled.RetryAfterSeconds, generationHealth);
                SchedulerAdmission.ApplyRetryAfter(http.Response, admission, scheduled.RetryAfterSeconds);

                ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                    status: admission.ToString(), finish: "-", httpStatus: schedulerFailure.StatusCode,
                    truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
                return schedulerFailure.ToResult();
            }

            var attempt = scheduled.Result!;

            if (attempt.Refusal is { } attemptRefused)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                    status: GenerationStatus.PromptLargerThanContext.ToString(), finish: "-", httpStatus: attemptRefused.StatusCode,
                    truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
                return attemptRefused.ToResult();
            }

            var result = attempt.Result!;
            var cancelledByCut = attempt.CancelledByCut;
            var ttftMs = attempt.TtftMs;
            var totalMs = attempt.TotalMs;
            var cacheLabel = lease!.CacheHit ? "hit" : "miss";
            var promptChars = lease.PromptChars;

            GenerationPipeline.LogRawOutput(logger, options, requestId, result);

            if (result.Status == GenerationStatus.Cancelled && http.RequestAborted.IsCancellationRequested)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: 0,
                    cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns,
                    queueWaitMs: queueWaitMs);
                return Results.Empty;
            }

            // The client-side cut and the shared status classification, exactly as the chat shape runs
            // them: same OutputCutter, same GenerationOutcome, so the same generated text yields the
            // same reply and finish reason regardless of which endpoint asked for it.
            var cut = limits.Cut(result.Text);
            var outcome = GenerationOutcome.Classify(result, cancelledByCut, generationHealth, totalMs);

            if (outcome.Failure is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: failure.StatusCode,
                    cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns,
                    queueWaitMs: queueWaitMs);
                return failure.ToResult();
            }

            var content = outcome.Filtered ? string.Empty : cut.Text;
            var finishReason = outcome.FinishReason(cut.FinishReason);

            // Back into the cache -- the rule is GenerationOutcome's, and the finally disposes every
            // context it refuses (D11, D43). No tool calls exist on this endpoint, so unlike the chat
            // shape there is nothing but the plain reply text to store.
            if (outcome.KeepsContext(cut.FinishReason))
            {
                lease.Keep(result.Text);
            }

            var promptTokens = lease.TranscriptTokens;
            var completionTokens = backend.TokenCounter.TokensCovering(result.Text, content.Length);

            var body = new CompletionResponse(
                Id: requestId,
                Created: time.GetUtcNow().ToUnixTimeSeconds(),
                Model: backend.ModelId,
                Choices: [new CompletionChoice(content, 0, finishReason)],
                Usage: CompletionUsage.For(promptTokens, completionTokens));

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs,
                cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);

            return Results.Json(body, JsonDefaults.Options);
        }
        catch (Exception ex) when (http.RequestAborted.IsCancellationRequested)
        {
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex is OperationCanceledException ? nameof(GenerationStatus.Cancelled) : ex.GetType().Name,
                finish: "-", httpStatus: 0,
                cache: CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);
            return Results.Empty;
        }
        catch (Exception ex)
        {
            var failure = GenerationFailure.FromException(ex, generationAttempted ? generationHealth : null);
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-", httpStatus: failure.StatusCode,
                cache: CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);
            return failure.ToResult();
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private static string CacheLabel(ContextLease? lease) => lease is null ? "-" : lease.CacheHit ? "hit" : "miss";
}
