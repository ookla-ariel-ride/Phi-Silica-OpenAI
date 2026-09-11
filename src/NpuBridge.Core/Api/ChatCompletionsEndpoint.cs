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
/// otherwise — shaped into one OpenAI response. Tool emulation (chunk 7) and the request scheduler
/// (chunk 8) are still deliberately absent: nothing is queued.
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
                .StreamAsync(http, prepared, options, streaming, cache, time, streamLogger)
                .ConfigureAwait(false) ?? Results.Empty;
        }

        var requestId = prepared.RequestId;
        var backendName = prepared.BackendName;
        var backend = prepared.Backend;

        var limits = prepared.Limits;
        var session = new ConversationSession(prepared, cache, options, logger);

        ContextLease? lease = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();

            // Counts the deltas and times the first one, exactly as it does on the streaming path; the
            // channel writer that path passes it is the only difference, and it is optional. Assigned
            // once per attempt inside the loop, so a retry times itself rather than the attempt before it.
            DeltaSink sink;

            // Set beside the CancelAsync below, when this handler cancels the generation because the
            // watcher tripped a limit, and read when the status comes back: a Cancelled this handler
            // asked for is the cut, any other Cancelled is a failure (D62). Recorded as a fact rather
            // than inferred from the cutter afterwards, because the whole-text cut below can commit a
            // cap the watcher never cancelled for, and the stream reads a different cutter — so
            // inferring it made the two shapes answer the same backend status differently.
            var cancelledByCut = false;

            GenerationResult result;
            while (true)
            {
                // 7. The context: checked out of the cache when the transcript extends a cached prefix,
                // created fresh otherwise, and refused here -- before a token is generated -- when a
                // backend with a preflight says the prompt does not fit (D55). Settled in the finally.
                var acquisition = session.Acquire();
                if (acquisition.Failure is { } refused)
                {
                    ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                        status: GenerationStatus.PromptLargerThanContext.ToString(), finish: "-", httpStatus: refused.StatusCode,
                        truncatedTurns: session.DroppedTurns);
                    return refused.ToResult();
                }

                lease = acquisition.Lease!;

                // Once a generation is attempted on a truncated transcript the header says so, whatever
                // that generation goes on to report -- the same moment the streaming path sets it. A
                // refusal above carries none: no reply was produced for the dropped turns to describe.
                // Re-applied on a retry, so a later drop updates the count; the JSON result executes
                // after this method returns, so the headers are still open.
                session.ApplyTruncationHeader(http.Response);

                // Everything an attempt cancels with, or learns from its own deltas, belongs to that
                // attempt. A retry after a cut used to inherit the cancelled token and the cut flag, so
                // the retried generation returned Cancelled at once and the stale flag reported that as
                // a successful cut: HTTP 200, empty content, finish_reason "stop". Cancelled either by
                // the client going away or by the client-side cut deciding it has enough text; only the
                // latter needs a source of the handler's own, the former arrives through the link.
                using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                cancelledByCut = false;

                // Watches the text as it arrives purely to decide when to stop the generation early; the
                // authoritative cut is applied below to the text the backend finally reports, with a
                // second OutputCutter, so the answer does not depend on which deltas the watcher happened
                // to see. Null when the request set no limits, which is the ordinary case and costs
                // nothing. Fresh per attempt, like the sink beside it.
                var watcher = limits.IsEmpty ? null : new CutWatcher(limits);
                sink = new DeltaSink(stopwatch, observer: watcher is null ? null : watcher.Accept);

                var generation = backend.GenerateAsync(
                    lease.Context,
                    lease.Prompt,
                    prepared.Sampling,
                    sink.OnDelta,
                    generationCts.Token);

                // Whichever comes first. When the cut has fired, cancel here -- on this thread, guarded
                // -- and then wait for the generation to end as it would have anyway. The overshoot is a
                // delta or two and costs nothing: the cut itself is applied to the final text below.
                //
                // "Has the cut fired" rather than "did the cut win the race": a generation that ends in
                // the same instant the watcher trips is still cancelled, so the flag beside the cancel
                // means the same thing here as on the stream, which cancels whenever a delta trips the
                // cutter no matter what the generation has done since. Cancelling a finished generation
                // is a no-op.
                //
                // Skipped when the request set no limits: there is no watcher, so nothing can ever
                // complete the other half of the race, and awaiting the generation alone says the same.
                if (watcher is not null)
                {
                    await Task.WhenAny(generation, watcher.Signal).ConfigureAwait(false);
                    if (watcher.Signal.IsCompleted)
                    {
                        cancelledByCut = true;
                        await GenerationPipeline.CancelGuardedAsync(generationCts, logger, requestId, "at the cut").ConfigureAwait(false);
                    }
                }

                result = await generation.ConfigureAwait(false);

                // 7a. A backend without a preflight can only say "too long" by failing the generation.
                // With --truncate-history that is not the end: drop the oldest exchange and go round
                // again on a fresh context (this one ended in a non-Complete status and is disposed, D11).
                // A backend with a preflight never reaches this -- Acquire refused or truncated already --
                // unless it reports the status the preflight did not predict, in which case the same
                // loop handles it.
                if (result.Status == GenerationStatus.PromptLargerThanContext && session.TryDropOldestExchange())
                {
                    lease.Dispose();
                    lease = null;
                    continue;
                }

                break;
            }

            stopwatch.Stop();
            var totalMs = stopwatch.Elapsed.TotalMilliseconds;
            var ttftMs = sink.TtftMs(totalMs);
            var cacheLabel = lease.CacheHit ? "hit" : "miss";
            var promptChars = lease.PromptChars;

            GenerationPipeline.LogRawOutput(logger, options, requestId, result);

            // 8. Status → response or error. The mapping itself lives in GenerationFailure, shared with
            // the streaming path so the two shapes cannot describe the same condition differently.
            if (result.Status == GenerationStatus.Cancelled && http.RequestAborted.IsCancellationRequested)
            {
                // The client is gone; there is nobody to send a body to and this is not an error.
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: 0,
                    cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns);
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
            var outcome = GenerationOutcome.Classify(result, cancelledByCut);

            if (outcome.Failure is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: failure.StatusCode,
                    cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns);
                return failure.ToResult();
            }

            var content = outcome.Filtered ? string.Empty : cut.Text;
            var finishReason = outcome.FinishReason(cut.FinishReason);

            // 8b. Back into the cache -- the rule is GenerationOutcome's, and the finally disposes every
            // context it refuses (D11, D43).
            if (outcome.KeepsContext(cut.FinishReason))
            {
                lease.Keep(result.Text);
            }

            // 9. Usage, in the backend's own count (D80): Phi-3 tokens on Phi Silica, chars/4 where the
            // tokenizer is unpublished; never the progress-callback count (Phi Silica batches several
            // tokens per callback, D44). The prompt side is the whole transcript the model holds, not the
            // tail sent on a cache hit: a client budgeting its context wants the former, and the number
            // must not change with a cache hit.
            var promptTokens = lease.TranscriptTokens;
            // The tokens the model produced to reach the cut, in the tokenization of its own text: a
            // stop-truncated prefix can count more on its own than the model spent on it (D80).
            var completionTokens = backend.TokenCounter.TokensCovering(result.Text, content.Length);

            var body = new ChatCompletionResponse(
                Id: requestId,
                Created: time.GetUtcNow().ToUnixTimeSeconds(),
                Model: backend.ModelId,
                Choices: [new ChatCompletionChoice(0, new ChatCompletionResponseMessage("assistant", content), finishReason)],
                Usage: CompletionUsage.For(promptTokens, completionTokens));

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs,
                cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns);

            return Results.Json(body, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = GenerationFailure.FromException(ex);
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-", httpStatus: failure.StatusCode,
                cache: lease is null ? "-" : lease.CacheHit ? "hit" : "miss", tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns);
            return failure.ToResult();
        }
        finally
        {
            // Disposes the context unless Keep or ReturnUntouched already settled it: every path that
            // took a context out of the cache or created one ends here (D43).
            lease?.Dispose();
        }
    }
}
