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

        // Cancelled either by the client going away or by the client-side cut deciding it has enough
        // text. Only the latter needs a source of the handler's own; the former arrives through the link.
        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);

        ContextLease? lease = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            long firstTokenTicks = 0;
            var callbacks = 0;

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

                // Watches the text as it arrives purely to decide when to stop the generation early; the
                // authoritative cut is applied below to the text the backend finally reports, with the
                // same OutputCutter, so the answer does not depend on which deltas the watcher happened
                // to see. Null when the request set no limits, which is the ordinary case and costs
                // nothing. Fresh per attempt, like the signal beside it.
                var watcher = limits.IsEmpty ? null : new OutputCutter(limits);

                // How the callback tells this task that the cut fired. The callback never cancels
                // anything itself: it runs on the backend's thread, and cancelling from there is wrong
                // twice over. A straight Cancel() can complete the generation's await inline and re-enter
                // the adapter while it is still inside this callback (Phi Silica then spins draining a
                // callback that cannot finish until we return); and CancelAfter(0) moves the cancel onto
                // a timer thread, where a throwing registration -- CsWinRT's IAsyncInfo.Cancel() on the
                // live operation is one -- is rethrown with nothing above it to catch it, and the process
                // terminates. So the callback sets this and the request task, awaiting below, cancels on
                // its own thread inside a try. RunContinuationsAsynchronously keeps that continuation
                // off the callback thread too.
                var cutSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var generation = backend.GenerateAsync(
                    lease.Context,
                    lease.Prompt,
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
                            cutSignal.TrySetResult();
                        }
                    },
                    generationCts.Token);

                // Whichever comes first. When the cut has fired, cancel here -- on this thread, guarded
                // -- and then wait for the generation to end as it would have anyway. The overshoot is a
                // delta or two and costs nothing: the cut itself is applied to the final text below.
                // Cancelling faults when a registration on the token throws, and that is a Debug line
                // here rather than a failure, because the generation still ends, the text is still cut,
                // and the finally still disposes the context -- exactly as the streaming path treats its
                // own cancel.
                //
                // "Has the cut fired" rather than "did the cut win the race": a generation that ends in
                // the same instant the watcher trips is still cancelled, so the flag beside the cancel
                // means the same thing here as on the stream, which cancels whenever a delta trips the
                // cutter no matter what the generation has done since. Cancelling a finished generation
                // is a no-op.
                await Task.WhenAny(generation, cutSignal.Task).ConfigureAwait(false);
                if (cutSignal.Task.IsCompleted)
                {
                    cancelledByCut = true;
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
            var ttftMs = callbacks == 0 ? totalMs : firstTokenTicks * 1000.0 / Stopwatch.Frequency;
            var cacheLabel = lease.CacheHit ? "hit" : "miss";
            var promptChars = lease.PromptChars;

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
                    status: result.Status.ToString(), finish: "-", httpStatus: 0,
                    cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns);
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
            var selfCancelled = cancelledByCut && result.Status is GenerationStatus.Cancelled;

            if (!filtered && !selfCancelled && GenerationFailure.FromStatus(result) is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: failure.StatusCode,
                    cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns);
                return failure.ToResult();
            }

            var content = filtered ? string.Empty : cut.Text;
            var finishReason = filtered ? "content_filter" : cut.FinishReason ?? "stop";

            // 8b. Back into the cache -- only a context whose generation ended Complete, and only when
            // the client got the whole reply. After a cut the context holds text the client never saw,
            // so the transcript it would be stored under is not the one the client will send back; it
            // is disposed by the finally like any other context that cannot be trusted (D11).
            if (result.Status == GenerationStatus.Complete && cut.FinishReason is null)
            {
                lease.Keep(result.Text);
            }

            // 9. Usage. Both numbers are chars/4 estimates, not a tokenizer's output; the progress
            // callback count is not usable (Phi Silica batches several tokens per callback). The prompt
            // side is the whole transcript the model holds, not the tail sent on a cache hit: a client
            // budgeting its context wants the former, and the number must not change with a cache hit.
            var promptTokens = ChatRequestMetrics.EstimateTokens(lease.TranscriptChars);
            var completionTokens = ChatRequestMetrics.EstimateTokens(content.Length);

            var body = new ChatCompletionResponse(
                Id: requestId,
                Created: time.GetUtcNow().ToUnixTimeSeconds(),
                Model: prepared.Request.Model ?? backend.ModelId,
                Choices: [new ChatCompletionChoice(0, new ChatCompletionResponseMessage("assistant", content), finishReason)],
                Usage: new CompletionUsage(promptTokens, completionTokens, promptTokens + completionTokens));

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs,
                cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns);

            session.ApplyTruncationHeader(http.Response);
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
