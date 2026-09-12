using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// Streaming phase two of <c>POST /v1/completions</c>: the server-sent-event sibling of
/// <see cref="CompletionsEndpoint"/>'s second half, and the <c>text_completion</c> counterpart of
/// <see cref="ChatCompletionsStreamEndpoint"/>. Phase one is shared — this type is only ever reached
/// with a <see cref="PreparedChatRequest"/> built from a wrapped <c>prompt</c>, so it never validates
/// the request.
///
/// Every rule <see cref="ChatCompletionsStreamEndpoint"/> documents applies here unchanged: headers
/// commit on the first frame that has something to say, so a failure before it is the ordinary HTTP
/// status and body (D52); the exit is cancel, drain, settle, in that order, because the context is a
/// live handle the generation task may still be writing to (D51); the scheduled closure never touches
/// <c>http.Response</c>, so the request thread alone applies the truncated-turns header (chunk 8 fix
/// round 2); and the lease is published to this method's own <c>lease</c> variable the instant
/// <see cref="ConversationSession.Acquire"/> hands it over, never carried back only on the closure's
/// return value (D43 + D51).
///
/// It is simpler than the chat shape in exactly one way: <c>tools</c> do not exist on this wire shape,
/// so there is no buffer-the-whole-reply branch to consider, no role chunk (a text completion has no
/// role to open), and no tool-call parse. Every delta is written out as soon as the cutter releases it.
/// </summary>
internal sealed class CompletionsStreamEndpoint
{
    private const string DoneFrame = "data: [DONE]\n\n";
    private const string KeepAliveFrame = ": keep-alive\n\n";

    // Never instantiated: the type exists so the streaming phase has an ILogger<T> category of its own.
    private CompletionsStreamEndpoint()
    {
    }

    /// <summary>
    /// Writes the stream and returns null, or — when the request failed before a single byte was written
    /// — returns the ordinary JSON error result for the caller to return unchanged.
    /// </summary>
    public static async Task<IResult?> StreamAsync(
        HttpContext http,
        PreparedChatRequest prepared,
        BridgeOptions options,
        StreamingOptions streaming,
        ContextCache cache,
        GenerationScheduler scheduler,
        TimeProvider time,
        ILogger logger)
    {
        var aborted = http.RequestAborted;

        var requestId = prepared.RequestId;
        var backendName = prepared.BackendName;
        var session = new ConversationSession(prepared, cache, options, logger);

        var model = prepared.Backend.ModelId;
        var created = time.GetUtcNow().ToUnixTimeSeconds();
        var includeUsage = prepared.Request.StreamOptions?.IncludeUsage == true;

        var sse = new SseStream(http.Response, () => session.ApplyTruncationHeader(http.Response));
        var stopwatch = Stopwatch.StartNew();

        // The client-side cut. Runs on the single channel reader, never on the backend's callback
        // thread, exactly as on the chat shape.
        var cutter = new OutputCutter(prepared.Limits);

        var streamed = false;

        // Set when this handler cancels the generation because a limit fired while streaming, read
        // when the status comes back. See ChatCompletionsStreamEndpoint for why this is a fact recorded
        // at the cancel rather than inferred from the cutter afterwards (D62).
        var cancelledByCut = false;

        var queueWaitMs = 0.0;

        // Published from *inside* the scheduled closure -- see the type-level remarks and
        // ChatCompletionsStreamEndpoint's fuller account of why (D43 + D51).
        ContextLease? lease = null;

        // One channel for the whole request, not one per attempt (chunk 8 fix round 1): Acquire() runs
        // inside the scheduled closure alongside the generation, and a --truncate-history retry
        // continues on this same channel rather than re-entering the queue.
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        var sink = DeltaSink.ToChannel(stopwatch, channel.Writer);

        // The current attempt's own cancellation source; not `using` inside the closure, because the
        // caller's finally is what cancels through it on the way out (see ChatCompletionsStreamEndpoint).
        CancellationTokenSource? currentGenerationCts = null;

        // The scheduled attempt, not the generation itself -- unwrapped only where it is actually
        // needed, by ReportSchedulerOutcomeAsync below.
        Task<ScheduleResult<ChatAttemptResult>>? generation = null;

        try
        {
            generation = scheduler.ScheduleAsync(async ct =>
            {
                while (true)
                {
                    var acquisition = session.Acquire();
                    if (acquisition.Failure is { } refused)
                    {
                        channel.Writer.TryComplete();
                        return ChatAttemptResult.Refused(refused);
                    }

                    var attemptLease = acquisition.Lease!;
                    lease = attemptLease;

                    try
                    {
                        var generationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        Interlocked.Exchange(ref currentGenerationCts, generationCts)?.Dispose();

                        var result = await prepared.Backend.GenerateAsync(
                            attemptLease.Context,
                            attemptLease.Prompt,
                            prepared.Sampling,
                            sink.OnDelta,
                            generationCts.Token).ConfigureAwait(false);

                        if (result.Status == GenerationStatus.PromptLargerThanContext && session.TryDropOldestExchange())
                        {
                            attemptLease.Dispose();
                            continue;
                        }

                        channel.Writer.TryComplete();
                        var totalMs = stopwatch.Elapsed.TotalMilliseconds;
                        var ttftMs = sink.TtftMs(totalMs);
                        return ChatAttemptResult.Generated(result, cancelledByCut, ttftMs, totalMs);
                    }
                    catch
                    {
                        channel.Writer.TryComplete();
                        attemptLease.Dispose();
                        throw;
                    }
                }
            }, aborted);

            // Backstop for a job dropped without ever running (shutdown mid-wait); a harmless no-op on
            // every other path, which completes the channel itself.
            _ = generation.ContinueWith(
                static (_, state) => ((ChannelWriter<string>)state!).TryComplete(),
                channel.Writer,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Rejection and a post-shutdown enqueue are both decided synchronously, before the
            // scheduler ever touches the channel (D52).
            if (generation.IsCompleted)
            {
                var immediateResult = await ReportSchedulerOutcomeAsync(
                    generation, sse, http, logger, requestId, backendName, prepared, session, aborted)
                    .ConfigureAwait(false);
                if (immediateResult.Handled)
                {
                    return immediateResult.Response;
                }
            }

            // Nothing has been written yet, on purpose: waiting here is what keeps the status line
            // available for a failure that arrives before the first token.
            streamed = await WaitForFirstDeltaAsync(sse, channel.Reader, streaming, aborted)
                .ConfigureAwait(false);

            GenerationResult result;
            if (streamed)
            {
                // Deliberately not cancelled by `aborted`: the loop must end when the channel completes,
                // so that `generation` is always reached and always drained below.
                await foreach (var delta in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    var release = cutter.Accept(delta);
                    if (release.Length > 0)
                    {
                        await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage, release, finishReason: null), aborted).ConfigureAwait(false);
                    }

                    if (cutter.StopRequested && !cancelledByCut)
                    {
                        cancelledByCut = true;
                        await GenerationPipeline.CancelGuardedAsync(currentGenerationCts!, logger, requestId, "at the cut").ConfigureAwait(false);
                    }

                    if (cutter.IsCut)
                    {
                        break;
                    }
                }

                var schedulerOutcome = await ReportSchedulerOutcomeAsync(
                    generation, sse, http, logger, requestId, backendName, prepared, session, aborted)
                    .ConfigureAwait(false);
                if (schedulerOutcome.Handled)
                {
                    return schedulerOutcome.Response;
                }

                result = schedulerOutcome.Attempt!.Result!;
                queueWaitMs = schedulerOutcome.QueueWaitMs;
            }
            else
            {
                var schedulerOutcome = await ReportSchedulerOutcomeAsync(
                    generation, sse, http, logger, requestId, backendName, prepared, session, aborted)
                    .ConfigureAwait(false);
                if (schedulerOutcome.Handled)
                {
                    return schedulerOutcome.Response;
                }

                result = schedulerOutcome.Attempt!.Result!;
                queueWaitMs = schedulerOutcome.QueueWaitMs;
            }

            // A generation was attempted on the truncated transcript, so the header says so, exactly as
            // on the chat shape's stream.
            session.ApplyTruncationHeader(http.Response);

            stopwatch.Stop();
            var cacheLabel = lease!.CacheHit ? "hit" : "miss";
            var tailTurns = lease.TailTurns;
            var promptChars = lease.PromptChars;
            var truncatedTurns = session.DroppedTurns;

            var totalMs = stopwatch.Elapsed.TotalMilliseconds;
            var ttftMs = sink.TtftMs(totalMs);

            GenerationPipeline.LogRawOutput(logger, options, requestId, result);

            if (aborted.IsCancellationRequested)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: 0,
                    cache: cacheLabel, tailTurns: tailTurns, truncatedTurns: truncatedTurns,
                    queueWaitMs: queueWaitMs);
                return null;
            }

            var outcome = GenerationOutcome.Classify(result, cancelledByCut);
            if (outcome.Failure is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-",
                    httpStatus: sse.Started ? StatusCodes.Status200OK : failure.StatusCode,
                    cache: cacheLabel, tailTurns: tailTurns, truncatedTurns: truncatedTurns,
                    queueWaitMs: queueWaitMs);
                return await FailAsync(sse, failure, logger, requestId, aborted).ConfigureAwait(false);
            }

            // The held tail: the generation ended without a stop string forming, so characters withheld
            // in case they were its first half are ordinary output after all. Empty when a limit fired,
            // and empty as well when the runtime withheld the answer (never flushed then, exactly as on
            // the chat shape).
            var tail = outcome.Filtered ? string.Empty : cutter.Flush();

            // Read after the flush, never before -- Flush() can be the call that commits the cap (D57).
            var finishReason = outcome.FinishReason(cutter.FinishReason);

            // Back into the cache, by the shared rule. No tool calls exist on this endpoint.
            if (outcome.KeepsContext(cutter.FinishReason))
            {
                lease.Keep(result.Text);
            }

            if (tail.Length > 0)
            {
                await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage, tail, finishReason: null), aborted).ConfigureAwait(false);
            }

            // The last real chunk. Its text is empty; it exists to carry finish_reason.
            await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage, string.Empty, finishReason), aborted).ConfigureAwait(false);

            var promptTokens = lease.TranscriptTokens;
            var deliveredChars = cutter.ContentLength;
            var completionTokens = prepared.Backend.TokenCounter.TokensCovering(cutter.AllText, deliveredChars);

            if (includeUsage)
            {
                await sse.WriteChunkAsync(new CompletionChunk(
                    Id: requestId,
                    Created: created,
                    Model: model,
                    Choices: [],
                    Usage: CompletionUsage.For(promptTokens, completionTokens)),
                    aborted).ConfigureAwait(false);
            }

            await sse.WriteAsync(DoneFrame, aborted).ConfigureAwait(false);

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs,
                cache: cacheLabel, tailTurns: tailTurns, truncatedTurns: truncatedTurns,
                queueWaitMs: queueWaitMs);
            return null;
        }
        catch (Exception ex) when (aborted.IsCancellationRequested)
        {
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex is OperationCanceledException ? nameof(GenerationStatus.Cancelled) : ex.GetType().Name,
                finish: "-", httpStatus: 0,
                cache: CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);
            return null;
        }
        catch (Exception ex)
        {
            var failure = GenerationFailure.FromException(ex);
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-",
                httpStatus: sse.Started ? StatusCodes.Status200OK : failure.StatusCode,
                cache: CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);
            return await FailAsync(sse, failure, logger, requestId, aborted).ConfigureAwait(false);
        }
        finally
        {
            // Cancel, drain, settle -- in that order (D51). See ChatCompletionsStreamEndpoint for the
            // full account of why each step is where it is.
            var generationCts = Volatile.Read(ref currentGenerationCts);
            if (generationCts is not null)
            {
                await GenerationPipeline.CancelGuardedAsync(generationCts, logger, requestId, "on the way out").ConfigureAwait(false);
            }

            if (generation is not null)
            {
                try
                {
                    await generation.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "req={RequestId} generation ended with an exception; drained before disposing the context.",
                        requestId);
                }
            }

            lease?.Dispose();

            Volatile.Read(ref currentGenerationCts)?.Dispose();
        }
    }

    private static string CacheLabel(ContextLease? lease) => lease is null ? "-" : lease.CacheHit ? "hit" : "miss";

    /// <summary>
    /// Waits until either the first delta is queued (true) or the generation ended without producing one
    /// (false), emitting <c>: keep-alive</c> comments meanwhile. See
    /// <see cref="ChatCompletionsStreamEndpoint"/>'s copy of this helper for the full account of the two
    /// intervals and why the channel always completes on its own.
    /// </summary>
    private static Task<bool> WaitForFirstDeltaAsync(
        SseStream sse,
        ChannelReader<string> reader,
        StreamingOptions streaming,
        CancellationToken cancellationToken) =>
        WaitForDeltaAsync(
            sse,
            reader,
            streaming,
            streaming.FirstKeepAliveDelay > TimeSpan.Zero ? streaming.FirstKeepAliveDelay : streaming.KeepAliveInterval,
            cancellationToken);

    private static async Task<bool> WaitForDeltaAsync(
        SseStream sse,
        ChannelReader<string> reader,
        StreamingOptions streaming,
        TimeSpan firstDelay,
        CancellationToken cancellationToken)
    {
        var wait = reader.WaitToReadAsync(CancellationToken.None).AsTask();
        if (streaming.KeepAliveInterval <= TimeSpan.Zero)
        {
            return await wait.ConfigureAwait(false);
        }

        var next = firstDelay > TimeSpan.Zero ? firstDelay : streaming.KeepAliveInterval;

        while (true)
        {
            try
            {
                return await wait.WaitAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (wait.IsCompleted)
                {
                    return await wait.ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            await sse.WriteAsync(KeepAliveFrame, cancellationToken).ConfigureAwait(false);

            next = streaming.KeepAliveInterval;
        }
    }

    /// <summary>
    /// Reports a failure the only way still available. Before the first byte that is the ordinary status
    /// and body, returned to the caller; after it, the status line is spent, so the same body goes out as
    /// an SSE event followed by the done marker.
    /// </summary>
    private static async Task<IResult?> FailAsync(
        SseStream sse,
        GenerationFailure failure,
        ILogger logger,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!sse.Started)
        {
            return failure.ToResult();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            await sse.WriteAsync(failure.ToEventFrame(), cancellationToken).ConfigureAwait(false);
            await sse.WriteAsync(DoneFrame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "req={RequestId} could not write the stream's error event; the client is gone.", requestId);
        }

        return null;
    }

    /// <summary>
    /// One content-bearing chunk. With <paramref name="nullUsage"/> (the request asked for usage) it
    /// carries <c>"usage": null</c>, as every chunk before the usage chunk must.
    /// </summary>
    private static CompletionChunk Chunk(
        string id,
        long created,
        string model,
        bool nullUsage,
        string text,
        string? finishReason)
    {
        var chunk = new CompletionChunk(id, created, model, [new CompletionChunkChoice(text, 0, finishReason)]);
        return nullUsage ? chunk.WithNullUsage() : chunk;
    }

    /// <summary>
    /// What awaiting the scheduled attempt meant, and whether the caller already has its answer. See
    /// <see cref="ChatCompletionsStreamEndpoint"/>'s copy of this type for the full account.
    /// </summary>
    private readonly record struct SchedulerOutcomeReport(bool Handled, IResult? Response, ChatAttemptResult? Attempt, double QueueWaitMs);

    private static async Task<SchedulerOutcomeReport> ReportSchedulerOutcomeAsync(
        Task<ScheduleResult<ChatAttemptResult>> generation,
        SseStream sse,
        HttpContext http,
        ILogger logger,
        string requestId,
        string backendName,
        PreparedChatRequest prepared,
        ConversationSession session,
        CancellationToken aborted)
    {
        var scheduled = await generation.ConfigureAwait(false);
        var queueWaitMs = scheduled.QueueWait.TotalMilliseconds;
        var admission = SchedulerAdmission.Classify(scheduled, aborted.IsCancellationRequested);

        if (admission == SchedulerOutcome.ClientGone)
        {
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: scheduled.Kind.ToString(), finish: "-", httpStatus: 0,
                truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
            return new SchedulerOutcomeReport(true, null, null, queueWaitMs);
        }

        if (admission != SchedulerOutcome.Completed)
        {
            var schedulerFailure = SchedulerAdmission.FailureFor(admission, scheduled.RetryAfterSeconds);
            SchedulerAdmission.ApplyRetryAfter(http.Response, admission, scheduled.RetryAfterSeconds);

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: admission.ToString(), finish: "-",
                httpStatus: sse.Started ? StatusCodes.Status200OK : schedulerFailure.StatusCode,
                truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
            var response = await FailAsync(sse, schedulerFailure, logger, requestId, aborted).ConfigureAwait(false);
            return new SchedulerOutcomeReport(true, response, null, queueWaitMs);
        }

        var attempt = scheduled.Result!;
        if (attempt.Refusal is { } refused)
        {
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: GenerationStatus.PromptLargerThanContext.ToString(), finish: "-",
                httpStatus: sse.Started ? StatusCodes.Status200OK : refused.StatusCode,
                truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
            var response = await FailAsync(sse, refused, logger, requestId, aborted).ConfigureAwait(false);
            return new SchedulerOutcomeReport(true, response, null, queueWaitMs);
        }

        return new SchedulerOutcomeReport(false, null, attempt, queueWaitMs);
    }

    /// <summary>
    /// The response, plus the SSE framing and the one-time header assignment. A private copy of
    /// <see cref="ChatCompletionsStreamEndpoint"/>'s nested class of the same name and behaviour: both
    /// are generic over nothing but a string frame, so there is nothing chat-shaped to reuse from, but
    /// duplicating this one small class was judged lower-risk than extracting it out of a file task 2
    /// already shipped and reviewed. See the task 3 report for the note this leaves in
    /// <c>docs/FUTURE.md</c>.
    /// </summary>
    private sealed class SseStream
    {
        private readonly HttpResponse _response;
        private readonly Action? _beforeHeaders;
        private bool _headersPrepared;

        public SseStream(HttpResponse response, Action? beforeHeaders = null)
        {
            _response = response;
            _beforeHeaders = beforeHeaders;
        }

        public bool Started => _response.HasStarted;

        public Task WriteChunkAsync(CompletionChunk chunk, CancellationToken cancellationToken) =>
            WriteAsync($"data: {JsonSerializer.Serialize(chunk, JsonDefaults.Options)}\n\n", cancellationToken);

        public async Task WriteAsync(string frame, CancellationToken cancellationToken)
        {
            if (!_headersPrepared)
            {
                _beforeHeaders?.Invoke();

                _response.StatusCode = StatusCodes.Status200OK;
                _response.ContentType = "text/event-stream";
                _response.Headers.CacheControl = "no-cache";
                _response.Headers["X-Accel-Buffering"] = "no";
                _headersPrepared = true;
            }

            await _response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
