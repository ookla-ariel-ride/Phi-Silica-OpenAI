using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// Streaming phase two of <c>POST /v1/chat/completions</c>: the server-sent-event sibling of
/// <see cref="ChatCompletionsEndpoint"/>'s second half. Phase one is shared — this type is only ever
/// reached with a <see cref="PreparedChatRequest"/>, so it never validates the request.
///
/// It does still decide a status code, and that is the one thing about it worth reading twice. The
/// headers are not written when the handler starts; they are written by the first frame that has
/// something to say. Until then the status line is still the server's to set, so a generation that
/// fails before it produces a single token — an over-length prompt, a backend that throws on the way in
/// — comes back as the ordinary HTTP status with the ordinary JSON body, exactly as it would without
/// <c>stream: true</c>. Only once a byte has gone out does an error have to travel inside the stream as
/// a <c>data: {"error":...}</c> event. <see cref="GenerationFailure"/> owns both forms so the two
/// cannot describe the same condition differently.
///
/// The other thing worth reading twice is the shape of the exit: cancel, drain, settle. The context is
/// a live handle the generation task may still be writing to, so its lease is settled -- stored back
/// in the cache or disposed -- only after that task has finished, on every path out of the method
/// including a client that vanished mid-frame.
/// </summary>
internal sealed class ChatCompletionsStreamEndpoint
{
    /// <summary>Terminator of an SSE stream, per the OpenAI protocol. Not JSON, deliberately.</summary>
    private const string DoneFrame = "data: [DONE]\n\n";

    /// <summary>
    /// An SSE comment: a client's event parser drops the line, but a proxy counting idle seconds sees
    /// traffic. This is what stops a slow first token — a cold model load can be tens of seconds on this
    /// hardware — from being read as a dead connection.
    /// </summary>
    private const string KeepAliveFrame = ": keep-alive\n\n";

    // Never instantiated: the type exists so the streaming phase has an ILogger<T> category of its
    // own. ChatRequestPreparer takes a plain ILogger and therefore adopts whatever category its caller
    // hands it, so the category has to be chosen on purpose somewhere; this is where.
    private ChatCompletionsStreamEndpoint()
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
        TimeProvider time,
        ILogger logger)
    {
        var aborted = http.RequestAborted;

        var requestId = prepared.RequestId;
        var backendName = prepared.BackendName;
        var session = new ConversationSession(prepared, cache, options, logger);

        // Identity of the reply, fixed once and repeated on every chunk: a client that stitches the
        // chunks back together must see the same id/created/model a non-streamed reply would carry.
        // The served id, never the requested one: preparation already refused any other (D77).
        var model = prepared.Backend.ModelId;
        var created = time.GetUtcNow().ToUnixTimeSeconds();
        var includeUsage = prepared.Request.StreamOptions?.IncludeUsage == true;

        var sse = new SseStream(http.Response);
        var stopwatch = Stopwatch.StartNew();

        // The client-side cut. It runs here, on the single channel reader, and never on the backend's
        // callback thread: it decides what goes on the wire, so it belongs on the side of the hand-off
        // that owns the response. Holding text back is the whole difference from the JSON path — a
        // stop string can straddle two deltas, and a delta already written cannot be recalled.
        var cutter = new OutputCutter(prepared.Limits);

        // Whether a delta ever arrived, which is not the same question as "has anything been written":
        // a keep-alive comment starts the stream without opening the assistant message, and a reply with
        // no deltas at all still needs its role chunk before the finish chunk. Declared out here because
        // the loop below may run more than once and the answer that matters is the last attempt's.
        var streamed = false;

        // Set when this handler cancels the generation because a limit fired while streaming, and read
        // when the status comes back. A fact recorded at the cancel, not inferred from the cutter
        // afterwards: the JSON path used to infer it from a different cutter state, and the two shapes
        // answered a backend's unprompted Cancelled differently (D62).
        var cancelledByCut = false;

        ContextLease? lease = null;
        Task<GenerationResult>? generation = null;

        // Linked, not the request's own token: this cancels the generation for reasons of the handler's
        // own (a write that failed, an exception on the way out) without pretending the client aborted.
        // Cancelling it is the first half of the cancel-drain-dispose exit in the finally.
        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(aborted);

        try
        {
            GenerationResult result;
            DeltaSink sink;
            while (true)
            {
                // The context: checked out of the cache when the transcript extends a cached prefix,
                // created fresh otherwise, and refused here -- before a byte has gone out -- when a
                // backend with a preflight says the prompt does not fit (D55). Settled in the finally.
                // Streaming makes this easier to get wrong because the response outlives the generation
                // call, so the try starts before it.
                var acquisition = session.Acquire();
                if (acquisition.Failure is { } refused)
                {
                    ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                        status: GenerationStatus.PromptLargerThanContext.ToString(), finish: "-",
                        httpStatus: sse.Started ? StatusCodes.Status200OK : refused.StatusCode,
                        truncatedTurns: session.DroppedTurns);
                    return await FailAsync(sse, refused, logger, requestId, aborted).ConfigureAwait(false);
                }

                lease = acquisition.Lease!;

                // Before the first frame, while the headers are still ours. On a retry after a
                // keep-alive the response has started and the session logs that the header is lost.
                session.ApplyTruncationHeader(http.Response);

                // The hand-off. The backend raises its progress callback on a thread-pool thread (the
                // fake does this on purpose, mirroring WinRT's Progress), so the callback may not touch
                // the HTTP response: it only writes into this channel. AllowSynchronousContinuations is
                // false so that a TryWrite cannot run the reader's continuation inline on the callback
                // thread, which would smuggle response writes back onto it. One reader — this task —
                // drains it. Fresh per attempt: a completed channel cannot be reopened.
                var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                });

                sink = new DeltaSink(stopwatch, channel.Writer);

                // Started, not awaited: the reader loop below runs concurrently with it. The channel is
                // completed in that method's finally, which is what ends the loop on every outcome,
                // including a throw.
                generation = GenerateAsync(prepared, lease, sink, channel.Writer, generationCts.Token);

                // Nothing has been written yet, on purpose. Waiting here — rather than opening with the
                // role chunk — is what keeps the status line available for a failure that arrives before
                // the first token. The first keep-alive comment is what ends that window, about a second in.
                streamed = await WaitForFirstDeltaAsync(sse, channel.Reader, streaming, aborted)
                    .ConfigureAwait(false);

                if (streamed)
                {
                    // The role chunk. OpenAI clients rely on it to open the assistant message.
                    await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                        new ChatCompletionDelta("assistant", string.Empty), finishReason: null), aborted).ConfigureAwait(false);

                    // Deliberately not cancelled by `aborted`: the loop must end when the channel completes,
                    // so that `generation` is always reached and always drained below.
                    await foreach (var delta in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        // What the cutter releases, not the delta: with stop strings configured this lags
                        // the backend by up to Holdback characters, and on the delta that trips a limit it
                        // is the truncated prefix.
                        var release = cutter.Accept(delta);
                        if (release.Length > 0)
                        {
                            await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                                new ChatCompletionDelta(null, release), finishReason: null), aborted).ConfigureAwait(false);
                        }

                        if (cutter.StopRequested && !cancelledByCut)
                        {
                            // Stop the model. On a settled cut nothing more will be emitted; on a token
                            // budget the cutter may know the budget is passed before it can place the cut
                            // (D80), and then the deltas already in flight keep coming through it so the
                            // flush below decides over everything the model produced.
                            cancelledByCut = true;
                            await GenerationPipeline.CancelGuardedAsync(generationCts, logger, requestId, "at the cut").ConfigureAwait(false);
                        }

                        if (cutter.IsCut)
                        {
                            // Stop consuming. Whatever is still queued is discarded; the finally's
                            // cancel-drain-settle then runs unchanged, so the context is still disposed
                            // exactly once and only after the generation task has ended.
                            break;
                        }
                    }

                    result = await generation.ConfigureAwait(false);
                    break;
                }

                result = await generation.ConfigureAwait(false);

                // Not one delta, and the backend says the prompt was too long. On a backend without a
                // preflight this is the only way it can say so; with --truncate-history the answer is
                // to drop the oldest exchange and go round again on a fresh context. This one ended in a
                // non-Complete status and is disposed (D11). The stream is unaffected: nothing but
                // keep-alive comments can have gone out, and those open no message.
                if (result.Status == GenerationStatus.PromptLargerThanContext && session.TryDropOldestExchange())
                {
                    lease.Dispose();
                    lease = null;
                    generation = null;
                    continue;
                }

                break;
            }

            stopwatch.Stop();
            var cacheLabel = lease.CacheHit ? "hit" : "miss";
            var tailTurns = lease.TailTurns;
            var promptChars = lease.PromptChars;
            var truncatedTurns = session.DroppedTurns;

            var totalMs = stopwatch.Elapsed.TotalMilliseconds;
            var ttftMs = sink.TtftMs(totalMs);

            GenerationPipeline.LogRawOutput(logger, options, requestId, result);

            if (aborted.IsCancellationRequested)
            {
                // The client is gone: no finish chunk, no error event, nothing. http=0 says so, as it
                // does on the JSON path. The finally still drains and disposes.
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: 0,
                    cache: cacheLabel, tailTurns: tailTurns, truncatedTurns: truncatedTurns);
                return null;
            }

            // Error, filtered, or content: one classification, shared with the non-streaming path, in
            // the same order and for the same reasons. See GenerationOutcome for why a Cancelled the
            // handler asked for is not a failure while every other status still is, and why "the cut
            // caused it" is the flag set beside the CancelAsync above rather than the cutter's state.
            var outcome = GenerationOutcome.Classify(result, cancelledByCut);
            if (outcome.Failure is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-",
                    httpStatus: sse.Started ? StatusCodes.Status200OK : failure.StatusCode,
                    cache: cacheLabel, tailTurns: tailTurns, truncatedTurns: truncatedTurns);
                return await FailAsync(sse, failure, logger, requestId, aborted).ConfigureAwait(false);
            }

            if (!streamed)
            {
                // Not one delta arrived — an empty reply, or a filtered one — so the role chunk has not
                // gone out yet. It still has to: a client builds the assistant message from it, and it
                // has to precede the tail chunk below.
                await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                    new ChatCompletionDelta("assistant", string.Empty), finishReason: null), aborted).ConfigureAwait(false);
            }

            // The held tail. The generation ended without a stop string forming, so the characters that
            // were withheld in case they were its first half are ordinary output after all and must go
            // out — losing them would silently truncate every reply whose last characters happened to
            // look like the start of a stop string. Empty when a limit fired: the text after a cut is
            // never sent, and empty as well when no stop string was configured, since nothing was held.
            //
            // Not flushed at all when the runtime withheld the answer. The deltas already on the wire
            // cannot be recalled, but these have not been written yet and the bridge now knows they were
            // withheld: writing them here would be the one place a filtered reply gained text.
            var tail = outcome.Filtered ? string.Empty : cutter.Flush();

            // Read after the flush, never before. Flush() can be the call that commits the cap: it is
            // deliberately deferred until the text runs Holdback past the budget, so a reply that ends
            // inside that window is only cut here. Reading FinishReason first labelled such a request
            // "stop" on this shape while the JSON path -- which reads it after its own flush -- called
            // the very same generation "length", and a client that resumes on "length" stopped instead
            // (D57). Hence the post-flush verdict is an argument to the shared classifier rather than
            // something it reads for itself.
            var finishReason = outcome.FinishReason(cutter.FinishReason);

            // Back into the cache, by the shared rule. The generation task has already ended (awaited
            // above), so the context is idle; the finally's drain finds nothing to wait for and its
            // Dispose finds the lease already settled (D51).
            if (outcome.KeepsContext(cutter.FinishReason))
            {
                lease.Keep(result.Text);
            }

            if (tail.Length > 0)
            {
                await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                    new ChatCompletionDelta(null, tail), finishReason: null), aborted).ConfigureAwait(false);
            }

            // The last real chunk. Its delta is empty; it exists to carry finish_reason.
            await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                new ChatCompletionDelta(null, null), finishReason), aborted).ConfigureAwait(false);

            // Usage, in the backend's own count on both sides, as on the non-streaming path (D80; never
            // the progress-callback count, D44). Counted off the cutter rather than the backend's
            // returned text, always: after a cut that text runs past what was sent, after a filtered
            // reply it is empty while deltas did go out, and the cutter is the only thing that knows
            // exactly what reached the client. The prompt side is the whole transcript the model holds,
            // not the tail sent on a cache hit, as on the JSON path.
            var promptTokens = lease.TranscriptTokens;
            // The tokens the model produced to reach what was sent, in the tokenization of everything the
            // cutter saw (D80): a prefix counted on its own can tokenize differently.
            var completionTokens = prepared.Backend.TokenCounter.TokensCovering(cutter.AllText, cutter.ContentLength);

            if (includeUsage)
            {
                await sse.WriteChunkAsync(new ChatCompletionChunk(
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
                cache: cacheLabel, tailTurns: tailTurns, truncatedTurns: truncatedTurns);
            return null;
        }
        catch (Exception ex) when (aborted.IsCancellationRequested)
        {
            // A write that failed because the client went away, or the cancellation that follows it.
            // There is nobody to report anything to, and it is not an error: swallow it here rather than
            // let it escape as an unhandled request exception. The finally still drains and disposes.
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex is OperationCanceledException ? nameof(GenerationStatus.Cancelled) : ex.GetType().Name,
                finish: "-", httpStatus: 0,
                cache: CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns);
            return null;
        }
        // Unfiltered, so that the two clauses together really are exhaustive. Excluding
        // OperationCanceledException here left the case "cancelled, but not by the client" uncaught: an
        // adapter that breaks the ILanguageModelBackend rule about swallowing the runtime's cancellation
        // lets one out of the cut's own linked token, RequestAborted is not set, neither filter matches,
        // and the request dies as an unhandled exception mid-stream instead of emitting its finish chunk.
        // A cancellation that reaches here is a generation that failed, and is reported as one.
        catch (Exception ex)
        {
            var failure = GenerationFailure.FromException(ex);
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-",
                httpStatus: sse.Started ? StatusCodes.Status200OK : failure.StatusCode,
                cache: CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns);
            return await FailAsync(sse, failure, logger, requestId, aborted).ConfigureAwait(false);
        }
        finally
        {
            // Cancel, drain, settle — in that order, and the order is the whole point. The context is a
            // live WinRT handle that the generation task may still be generating against; disposing it
            // while that task runs is a use-after-dispose, not merely an unobserved task. Cancelling
            // first is what keeps the wait short; awaiting is what makes the disposal safe. The lease's
            // Dispose is a no-op when Keep already stored the context, which only happens after the
            // generation was awaited above, so the cache never receives a context still in use.
            // Guarded because this was the one statement in the method outside a try, and it stands
            // between a failure and the disposal below: letting a throw escape would skip both the drain
            // and Dispose(), leaking exactly the handle D43 guarantees is released -- worse than the D51
            // defect, which disposed too early rather than never.
            await GenerationPipeline.CancelGuardedAsync(generationCts, logger, requestId, "on the way out").ConfigureAwait(false);

            if (generation is not null)
            {
                try
                {
                    await generation.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Already reported above, or unreportable because the client is gone. Observed here
                    // only so that the drain completes and the task does not fault unobserved.
                    logger.LogDebug(ex, "req={RequestId} generation ended with an exception; drained before disposing the context.",
                        requestId);
                }
            }

            lease?.Dispose();
        }
    }

    private static string CacheLabel(ContextLease? lease) => lease is null ? "-" : lease.CacheHit ? "hit" : "miss";

    /// <summary>
    /// Waits until either the first delta is queued (true) or the generation ended without producing one
    /// (false), emitting <c>: keep-alive</c> comments meanwhile. The first comment is the first byte of
    /// the response and commits the headers, so its delay answers a different question from the ones
    /// after it: those keep a proxy from calling the connection idle, this one keeps a client from
    /// waiting on headers — and it is also the window in which a failure can still be a real HTTP status.
    /// Hence two intervals: about a second, then every fifteen.
    ///
    /// The wait itself is never cancelled. The channel is completed on every outcome of the generation,
    /// a cancelled one included, so this returns and the caller always reaches the drain. Only the
    /// keep-alive timer and the write it guards observe the client's token.
    /// </summary>
    private static async Task<bool> WaitForFirstDeltaAsync(
        SseStream sse,
        ChannelReader<string> reader,
        StreamingOptions streaming,
        CancellationToken cancellationToken)
    {
        var wait = reader.WaitToReadAsync(CancellationToken.None).AsTask();
        if (streaming.KeepAliveInterval <= TimeSpan.Zero)
        {
            return await wait.ConfigureAwait(false);
        }

        var next = streaming.FirstKeepAliveDelay > TimeSpan.Zero
            ? streaming.FirstKeepAliveDelay
            : streaming.KeepAliveInterval;

        while (true)
        {
            try
            {
                // WaitAsync returns a task of its own and leaves `wait` -- the channel's, awaited again
                // on the next lap -- to complete when it completes. Its timer is released either way;
                // what it does leave behind is one continuation on `wait` per lap, and the laps are
                // bounded by the generation's own length over the fifteen-second interval.
                return await wait.WaitAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // No delta yet. Say something on the wire and wait again.
            }

            // Belt and braces: a cancel that lands in the same instant as the timeout is reported as the
            // timeout, and writing to a connection whose client has gone is not worth the exception.
            cancellationToken.ThrowIfCancellationRequested();

            await sse.WriteAsync(KeepAliveFrame, cancellationToken).ConfigureAwait(false);

            // The headers are out now, so every later comment is only about proxy idle timeouts.
            next = streaming.KeepAliveInterval;
        }
    }

    /// <summary>
    /// Reports a failure the only way still available. Before the first byte that is the ordinary status
    /// and body, returned to the caller; after it, the status line is spent, so the same body goes out as
    /// an SSE event followed by the done marker — a stream that ends badly still ends.
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
            // The stream is open but the client is not there to read the bad news. Writing would only
            // throw, and an unhandled exception is a worse way to end than silence.
            return null;
        }

        try
        {
            await sse.WriteAsync(failure.ToEventFrame(), cancellationToken).ConfigureAwait(false);
            await sse.WriteAsync(DoneFrame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // This is the last thing the handler does for the client, and it is already reporting a
            // failure. A connection that dies between the check above and the write here would otherwise
            // throw a second exception on the way out of a catch block, which reaches the host as an
            // unhandled request exception and says nothing useful. The drain and the disposal in the
            // caller's finally are unaffected either way.
            logger.LogDebug(ex, "req={RequestId} could not write the stream's error event; the client is gone.", requestId);
        }

        return null;
    }

    /// <summary>
    /// One content-bearing chunk. With <paramref name="nullUsage"/> (the request asked for usage) it
    /// carries <c>"usage": null</c>, as every chunk before the usage chunk must.
    /// </summary>
    private static ChatCompletionChunk Chunk(
        string id,
        long created,
        string model,
        bool nullUsage,
        ChatCompletionDelta delta,
        string? finishReason)
    {
        var chunk = new ChatCompletionChunk(id, created, model, [new ChatCompletionChunkChoice(0, delta, finishReason)]);
        return nullUsage ? chunk.WithNullUsage() : chunk;
    }

    /// <summary>
    /// Runs the generation and, whatever happens, closes the channel so the reader loop ends. A throw
    /// is deliberately not passed to <c>Complete</c>: the reader finishes normally, drains what was
    /// already queued, and the exception surfaces where the task is awaited.
    /// </summary>
    private static async Task<GenerationResult> GenerateAsync(
        PreparedChatRequest prepared,
        ContextLease lease,
        DeltaSink sink,
        ChannelWriter<string> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            return await prepared.Backend.GenerateAsync(
                lease.Context,
                lease.Prompt,
                prepared.Sampling,
                sink.OnDelta,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// The response plus one fact: whether anything has gone out on it yet. The SSE headers are set by
    /// the first write rather than up front, so <see cref="Started"/> is exactly the question "is the
    /// status code still mine to choose?" — the boundary the whole error story turns on.
    /// </summary>
    private sealed class SseStream
    {
        private readonly HttpResponse _response;

        public SseStream(HttpResponse response) => _response = response;

        /// <summary>True once a frame has been written, i.e. once 200 and <c>text/event-stream</c> are the answer.</summary>
        public bool Started { get; private set; }

        public Task WriteChunkAsync(ChatCompletionChunk chunk, CancellationToken cancellationToken) =>
            WriteAsync($"data: {JsonSerializer.Serialize(chunk, JsonDefaults.Options)}\n\n", cancellationToken);

        /// <summary>
        /// One SSE frame, flushed immediately: without the flush the chunks sit in Kestrel's buffer and
        /// the client sees the whole reply at once, which is the one thing streaming exists to avoid.
        /// </summary>
        public async Task WriteAsync(string frame, CancellationToken cancellationToken)
        {
            if (!Started)
            {
                // X-Accel-Buffering defeats nginx's response buffering, which would otherwise hold the
                // whole stream and deliver it as one lump.
                _response.StatusCode = StatusCodes.Status200OK;
                _response.ContentType = "text/event-stream";
                _response.Headers.CacheControl = "no-cache";
                _response.Headers["X-Accel-Buffering"] = "no";
                Started = true;
            }

            await _response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
