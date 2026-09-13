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
/// the non-streaming phase two itself: a single generation on a context from
/// <see cref="ConversationSession"/> — cached when the transcript extends one the cache holds, fresh
/// otherwise — shaped into one OpenAI response. The context lookup, the preflight and the generation
/// are one scheduled unit behind <see cref="GenerationScheduler"/> (chunk 8), on both shapes
/// identically: all three are calls on the one shared model handle, so none of them may run while
/// another request's generation is live.
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
        ContextCache cache,
        GenerationScheduler scheduler,
        GenerationHealth generationHealth,
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
                .StreamAsync(http, prepared, options, streaming, cache, scheduler, generationHealth, time, streamLogger)
                .ConfigureAwait(false) ?? Results.Empty;
        }

        var requestId = prepared.RequestId;
        var backendName = prepared.BackendName;
        var backend = prepared.Backend;

        var limits = prepared.Limits;
        var session = new ConversationSession(prepared, cache, options, logger);

        // The current attempt's lease. Assigned from *inside* the scheduled closure, the instant
        // Acquire hands one over, rather than from the value the closure returns: a lease the outer
        // scope learns about only from a returned value is a lease it does not have on any path where
        // that value never arrives, and the finally below is the one place a context is released (D43).
        // Reassigned on a --truncate-history retry, whose previous lease the closure has already
        // disposed -- Dispose is idempotent, so a stale reference here is a no-op rather than a double
        // release. Reading a disposed lease's own CacheHit/TailTurns/PromptChars afterwards is likewise
        // safe: they are plain fields set once in the constructor, and the catch clauses' log line needs
        // them to say cache=hit|miss rather than cache=-.
        ContextLease? lease = null;

        // How long the attempt that actually produced `result` waited behind the scheduler's one
        // worker. Stays 0 for every log line written before a generation was ever scheduled; reassigned
        // once the single ScheduleAsync call below settles. Declared outside the try so both catch
        // clauses, which run for a throw at any point including before scheduling, can still log the
        // best value they have.
        var queueWaitMs = 0.0;
        var generationAttempted = false;

        try
        {
            // Chunk 8 fix round 1 (controller ruling): the whole attempt -- Acquire's own preflight and
            // truncation loop, the outer retry-after-a-failed-generation loop that used to live here,
            // and the generation itself -- runs inside one scheduled closure now, not just the
            // generation. CreateContext and GetUsablePromptLength are calls on the one shared model
            // handle exactly like GenerateAsync is; leaving them outside the queue let N concurrent
            // requests make N concurrent handle calls against a live generation, which is the gap
            // docs/FUTURE.md:388 describes and the one PLAN §2.7 says chunk 8 closes ("a job cancelled
            // while queued is dropped without touching the model"). A retry (--truncate-history)
            // continues on the same scheduled slot rather than re-entering the queue -- see the report
            // for why. ChatAttemptResult carries back what the rest of this method needs: the lease
            // (Refused leaves it null, since Acquire settled it via ReturnUntouched before returning),
            // the result, whether this handler cancelled it for the cut, and its own timing.
            var scheduled = await scheduler.ScheduleAsync(async ct =>
            {
                var stopwatch = Stopwatch.StartNew();
                while (true)
                {
                    // 7. The context: checked out of the cache when the transcript extends a cached
                    // prefix, created fresh otherwise, and refused here -- before a token is generated --
                    // when a backend with a preflight says the prompt does not fit (D55).
                    var acquisition = session.Acquire();
                    if (acquisition.Failure is { } refused)
                    {
                        return ChatAttemptResult.Refused(refused);
                    }

                    var attemptLease = acquisition.Lease!;

                    // Published to the caller's scope before anything can throw, so the finally out
                    // there always has this context to settle however this method leaves -- including
                    // the paths where the value this closure returns never reaches its caller.
                    lease = attemptLease;

                    // Guarded all the same: a throw here would unwind through ScheduleAsync, and the
                    // caller's finally disposes exactly once whether this dispose ran first or not
                    // (Dispose is idempotent). Disposing here keeps the release as close to the failure
                    // as it was before chunk 8, when this loop ran in the caller's own try/finally.
                    try
                    {
                        // Once a generation is attempted on a truncated transcript the header says so,
                        // whatever that generation goes on to report -- the same moment the streaming
                        // path sets it. A refusal above carries none: no reply was produced for the
                        // dropped turns to describe. Re-applied on a retry, so a later drop updates the
                        // count. Mutating the response from the worker thread is safe: the caller is
                        // suspended on this same ScheduleAsync call and touches neither the response nor
                        // session again until it resumes (fix round 1, Finding 6: this used to run before
                        // scheduling, so a request that then hit a full queue or a shutdown could carry
                        // the header despite no generation ever having been attempted).
                        session.ApplyTruncationHeader(http.Response);

                        // Everything an attempt cancels with, or learns from its own deltas, belongs to
                        // that attempt. A retry after a cut used to inherit the cancelled token and the
                        // cut flag, so the retried generation returned Cancelled at once and the stale
                        // flag reported that as a successful cut: HTTP 200, empty content, finish_reason
                        // "stop". Cancelled either by the client going away or by the client-side cut
                        // deciding it has enough text; only the latter needs a source of the handler's
                        // own, the former arrives through ct, which is the scheduler's own token for this
                        // job (linked from http.RequestAborted by ScheduleAsync's caller, below).
                        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var cancelledByCut = false;

                        // Watches the text as it arrives purely to decide when to stop the generation
                        // early; the authoritative cut is applied below to the text the backend finally
                        // reports, with a second OutputCutter, so the answer does not depend on which
                        // deltas the watcher happened to see. Null when the request set no limits, which
                        // is the ordinary case and costs nothing. Fresh per attempt, like the sink beside
                        // it.
                        var watcher = limits.IsEmpty ? null : new CutWatcher(limits);
                        var sink = DeltaSink.ToWatcher(stopwatch, watcher);

                        generationAttempted = true;
                        var generation = backend.GenerateAsync(
                            attemptLease.Context,
                            attemptLease.Prompt,
                            prepared.Sampling,
                            sink.OnDelta,
                            generationCts.Token);

                        // Whichever comes first. When the cut has fired, cancel here -- on this thread,
                        // guarded -- and then wait for the generation to end as it would have anyway. The
                        // overshoot is a delta or two and costs nothing: the cut itself is applied to the
                        // final text below.
                        //
                        // "Has the cut fired" rather than "did the cut win the race": a generation that
                        // ends in the same instant the watcher trips is still cancelled, so the flag
                        // beside the cancel means the same thing here as on the stream, which cancels
                        // whenever a delta trips the cutter no matter what the generation has done since.
                        // Cancelling a finished generation is a no-op.
                        //
                        // Skipped when the request set no limits: there is no watcher, so nothing can
                        // ever complete the other half of the race, and awaiting the generation alone
                        // says the same.
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

                        // 7a. A backend without a preflight can only say "too long" by failing the
                        // generation. With --truncate-history that is not the end: drop the oldest
                        // exchange and go round again on a fresh context (this one ended in a
                        // non-Complete status and is disposed, D11). A backend with a preflight never
                        // reaches this -- Acquire refused or truncated already -- unless it reports the
                        // status the preflight did not predict, in which case the same loop handles it.
                        // This retry continues on the same scheduled slot rather than re-entering the
                        // queue (see the fix report).
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
                // The client was already gone before its turn came, or the scheduler was already
                // shutting down at the same moment -- either way there is nobody to send a body to,
                // exactly like the same check further down for an attempt that did run.
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                    status: scheduled.Kind.ToString(), finish: "-", httpStatus: 0,
                    truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
                return Results.Empty;
            }

            if (admission != SchedulerOutcome.Completed)
            {
                // QueueFull (429), QueueShuttingDown (503, task-2-brief.md integration decision 4), or
                // BackendThrewCancellation (502, fix round 1 Finding 1) -- none of which ever reached the
                // closure above in the first two cases, so no context exists to dispose beyond what the
                // finally already handles (null).
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

            // 8. Status → response or error. The mapping itself lives in GenerationFailure, shared with
            // the streaming path so the two shapes cannot describe the same condition differently.
            if (result.Status == GenerationStatus.Cancelled && http.RequestAborted.IsCancellationRequested)
            {
                // The client is gone; there is nobody to send a body to and this is not an error.
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: 0,
                    cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns,
                    queueWaitMs: queueWaitMs);
                return Results.Empty;
            }

            // 8a. The client-side cut, before the status mapping. A generation this handler cancelled
            // because the cap or a stop string was reached comes back Cancelled, which is a 502 for any
            // other reason; the cut is what tells the two apart, and it is decided by the text rather
            // than by the status so that a cut which landed on the last delta reads the same either way.
            // The whole text in one go, so the cut's verdict is legible immediately -- unlike the stream,
            // which must wait for its Flush (D57).
            var cut = limits.Cut(result.Text);

            // Error, filtered, or content: one classification, shared with the streaming path, so the two
            // shapes cannot describe the same generation differently. See GenerationOutcome for why the
            // cut is consulted as the flag recorded at the cancel rather than as the cutter's state.
            var outcome = GenerationOutcome.Classify(result, cancelledByCut, generationHealth, totalMs);

            if (outcome.Failure is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: failure.StatusCode,
                    cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns,
                    queueWaitMs: queueWaitMs);
                return failure.ToResult();
            }

            string? content = outcome.Filtered ? string.Empty : cut.Text;
            var finishReason = outcome.FinishReason(cut.FinishReason);

            // 8a-i. Tool calls (chunk 7). Only when the request offered tools, and only over text the
            // client would otherwise have been given: a filtered reply is not parsed, because parsing
            // it would be the one place withheld text came back as arguments. A reply that parses to
            // nothing is ordinary content, which is the common case and costs one scan.
            var toolCalls = ToolCallReply.From(prepared.Tools, outcome, content);

            // What the model produced for this client, whether it went out as content or was reshaped
            // into tool_calls. Read before the content is cleared: the JSON the model wrote cost the
            // tokens it cost, and a tool call reporting completion_tokens 0 would tell a client
            // budgeting its context that the call was free.
            var deliveredChars = content?.Length ?? 0;

            if (toolCalls is not null)
            {
                content = null;

                // The cut keeps its label, for the reason the streaming path gives: a budget that
                // fired produced this call out of a reply the model had not finished, and saying
                // "tool_calls" would tell a client that resumes on "length" there is nothing to
                // resume. It still gets the calls.
                finishReason = cut.FinishReason ?? "tool_calls";
            }

            // 8b. Back into the cache -- the rule is GenerationOutcome's, and the finally disposes every
            // context it refuses (D11, D43). A tool call is stored under the transcript the client will
            // send back, which is the array this reply emitted and not the fenced text the model wrote.
            if (outcome.KeepsContext(cut.FinishReason))
            {
                lease.Keep(result.Text, ToolCallReply.Carried(toolCalls));
            }

            // 9. Usage, in the backend's own count (D80): Phi-3 tokens on Phi Silica, chars/4 where the
            // tokenizer is unpublished; never the progress-callback count (Phi Silica batches several
            // tokens per callback, D44). The prompt side is the whole transcript the model holds, not the
            // tail sent on a cache hit: a client budgeting its context wants the former, and the number
            // must not change with a cache hit.
            var promptTokens = lease.TranscriptTokens;
            // The tokens the model produced to reach the cut, in the tokenization of its own text: a
            // stop-truncated prefix can count more on its own than the model spent on it (D80).
            var completionTokens = backend.TokenCounter.TokensCovering(result.Text, deliveredChars);

            var body = new ChatCompletionResponse(
                Id: requestId,
                Created: time.GetUtcNow().ToUnixTimeSeconds(),
                Model: backend.ModelId,
                Choices:
                [
                    new ChatCompletionChoice(
                        0,
                        new ChatCompletionResponseMessage("assistant", content) { ToolCalls = toolCalls },
                        finishReason),
                ],
                Usage: CompletionUsage.For(promptTokens, completionTokens));

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs,
                cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);

            return Results.Json(body, JsonDefaults.Options);
        }
        catch (Exception ex) when (http.RequestAborted.IsCancellationRequested)
        {
            // The client is gone, so there is nobody to hand a body to and this is not an error --
            // the same answer the returned-Cancelled check above gives, for the thrown form. http=0
            // says so, as it does on the streaming path. The finally still disposes.
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex is OperationCanceledException ? nameof(GenerationStatus.Cancelled) : ex.GetType().Name,
                finish: "-", httpStatus: 0,
                cache: CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);
            return Results.Empty;
        }
        // Unfiltered, so that the two clauses together really are exhaustive -- the same pair, in the
        // same order, as the streaming path. Excluding OperationCanceledException here left the case
        // "cancelled, but not by the client" uncaught: an adapter that breaks the
        // ILanguageModelBackend rule about swallowing the runtime's cancellation lets one out of the
        // cut's own linked token, RequestAborted is not set, the filter above does not match, and the
        // request died as an unhandled exception -- HTTP 500 with no OpenAI envelope, where the stream
        // answered the identical event with a 502 and the ordinary error body.
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
            // Disposes the context unless Keep or ReturnUntouched already settled it: every path that
            // took a context out of the cache or created one ends here (D43).
            lease?.Dispose();
        }
    }

    private static string CacheLabel(ContextLease? lease) => lease is null ? "-" : lease.CacheHit ? "hit" : "miss";
}
