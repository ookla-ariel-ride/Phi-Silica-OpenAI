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
/// The other thing worth reading twice is the shape of the exit: cancel, drain, dispose. The context is
/// a live handle the generation task may still be writing to, so it is disposed only after that task
/// has finished, on every path out of the method including a client that vanished mid-frame.
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
        TimeProvider time,
        ILogger logger)
    {
        var aborted = http.RequestAborted;

        var requestId = prepared.RequestId;
        var backendName = prepared.BackendName;
        var promptChars = prepared.PromptChars;

        // Identity of the reply, fixed once and repeated on every chunk: a client that stitches the
        // chunks back together must see the same id/created/model a non-streamed reply would carry.
        var model = prepared.Request.Model ?? prepared.Backend.ModelId;
        var created = time.GetUtcNow().ToUnixTimeSeconds();
        var includeUsage = prepared.Request.StreamOptions?.IncludeUsage == true;

        var sse = new SseStream(http.Response);
        var stopwatch = Stopwatch.StartNew();

        // The client-side cut. It runs here, on the single channel reader, and never on the backend's
        // callback thread: it decides what goes on the wire, so it belongs on the side of the hand-off
        // that owns the response. Holding text back is the whole difference from the JSON path — a
        // stop string can straddle two deltas, and a delta already written cannot be recalled.
        var cutter = new OutputCutter(prepared.Limits);

        // Not the same question as "has anything been written": a keep-alive comment starts the stream
        // without opening the assistant message, and a reply with no deltas at all still needs its role
        // chunk before the finish chunk.
        var roleSent = false;

        IModelContext? context = null;
        Task<GenerationResult>? generation = null;

        // Linked, not the request's own token: this cancels the generation for reasons of the handler's
        // own (a write that failed, an exception on the way out) without pretending the client aborted.
        // Cancelling it is the first half of the cancel-drain-dispose exit in the finally.
        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(aborted);

        try
        {
            // The hand-off. The backend raises its progress callback on a thread-pool thread (the fake
            // does this on purpose, mirroring WinRT's Progress), so the callback may not touch the HTTP
            // response: it only writes into this channel. AllowSynchronousContinuations is false so
            // that a TryWrite cannot run the reader's continuation inline on the callback thread, which
            // would smuggle response writes back onto it. One reader — this task — drains it.
            var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

            var sink = new DeltaSink(channel.Writer, stopwatch);

            // A fresh context per request, disposed in the finally: D11/D43. Streaming makes this easier
            // to get wrong because the response outlives the generation call, so the try starts here.
            context = prepared.Backend.CreateContext(prepared.NativeSystem);

            // Started, not awaited: the reader loop below runs concurrently with it. The channel is
            // completed in that method's finally, which is what ends the loop on every outcome,
            // including a throw.
            generation = GenerateAsync(prepared, context, sink, channel.Writer, generationCts.Token);

            // Nothing has been written yet, on purpose. Waiting here — rather than opening with the role
            // chunk — is what keeps the status line available for a failure that arrives before the
            // first token. The first keep-alive comment is what ends that window, about a second in.
            var streamed = await WaitForFirstDeltaAsync(sse, channel.Reader, streaming, aborted)
                .ConfigureAwait(false);

            if (streamed)
            {
                // The role chunk. OpenAI clients rely on it to open the assistant message.
                roleSent = true;
                await sse.WriteChunkAsync(Chunk(requestId, created, model,
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
                        await sse.WriteChunkAsync(Chunk(requestId, created, model,
                            new ChatCompletionDelta(null, release), finishReason: null), aborted).ConfigureAwait(false);
                    }

                    if (cutter.IsCut)
                    {
                        // Stop consuming and stop the model. Whatever is still queued is discarded; the
                        // finally's cancel-drain-dispose then runs unchanged, so the context is still
                        // disposed exactly once and only after the generation task has ended.
                        await generationCts.CancelAsync().ConfigureAwait(false);
                        break;
                    }
                }
            }

            var result = await generation.ConfigureAwait(false);
            stopwatch.Stop();

            var totalMs = stopwatch.Elapsed.TotalMilliseconds;
            var ttftMs = sink.Count == 0 ? totalMs : sink.FirstTokenTicks * 1000.0 / Stopwatch.Frequency;

            if (options.Verbose)
            {
                logger.LogInformation("req={RequestId} raw model output ({Status}):\n---- output ----\n{Text}\n---- end ----",
                    requestId, result.Status, result.Text);
            }

            if (aborted.IsCancellationRequested)
            {
                // The client is gone: no finish chunk, no error event, nothing. http=0 says so, as it
                // does on the JSON path. The finally still drains and disposes.
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: 0);
                return null;
            }

            // A generation this handler cancelled because a limit fired reports Cancelled, which is a
            // 502 for every other reason. The cut is what tells those apart, so it is consulted first —
            // as it is on the JSON path, in the same order, for the same reason.
            //
            // Only Cancelled, though. "A cut fired" is not a licence to discard every other status: a
            // backend that reports Error after the cut has still failed, and on this hardware that is
            // not hypothetical -- D55 established that a real prompt overflow surfaces as exactly that
            // generic Error. Suppressing it hands the client HTTP 200, a truncated reply and
            // finish_reason "stop", with nothing to say the generation faulted.
            //
            // IsCut is read before the flush deliberately: a cut this handler committed while streaming
            // is the only kind that could have caused the cancellation, and one established later by
            // Flush() cannot have, because nothing cancelled for it.
            var selfCancelled = cutter.IsCut && result.Status is GenerationStatus.Cancelled;
            if (!selfCancelled && GenerationFailure.FromStatus(result) is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-",
                    httpStatus: sse.Started ? StatusCodes.Status200OK : failure.StatusCode);
                return await FailAsync(sse, failure, logger, requestId, aborted).ConfigureAwait(false);
            }

            if (!roleSent)
            {
                // Not one delta arrived — an empty reply, or a filtered one — so the role chunk has not
                // gone out yet. It still has to: a client builds the assistant message from it, and it
                // has to precede the tail chunk below.
                roleSent = true;
                await sse.WriteChunkAsync(Chunk(requestId, created, model,
                    new ChatCompletionDelta("assistant", string.Empty), finishReason: null), aborted).ConfigureAwait(false);
            }

            // Content filtering is not a failure: the generation ran, and the client is told so with a
            // finish reason rather than an error, exactly as on the JSON path — and, as there, it
            // outranks the cut, so the two shapes label the same outcome the same way.
            var filtered = result.Status is GenerationStatus.ContentFiltered or GenerationStatus.BlockedByPolicy;

            // The held tail. The generation ended without a stop string forming, so the characters that
            // were withheld in case they were its first half are ordinary output after all and must go
            // out — losing them would silently truncate every reply whose last characters happened to
            // look like the start of a stop string. Empty when a limit fired: the text after a cut is
            // never sent, and empty as well when no stop string was configured, since nothing was held.
            //
            // Not flushed at all when the runtime withheld the answer. The deltas already on the wire
            // cannot be recalled, but these have not been written yet and the bridge now knows they were
            // withheld: writing them here would be the one place a filtered reply gained text.
            var tail = filtered ? string.Empty : cutter.Flush();

            // Read after the flush, never before. Flush() can be the call that commits the cap: it is
            // deliberately deferred until the text runs Holdback past the budget, so a reply that ends
            // inside that window is only cut here. Reading FinishReason first labelled such a request
            // "stop" on this shape while the JSON path -- which reads it after its own flush -- called
            // the very same generation "length", and a client that resumes on "length" stopped instead.
            var finishReason = filtered ? "content_filter" : cutter.FinishReason ?? "stop";

            if (tail.Length > 0)
            {
                await sse.WriteChunkAsync(Chunk(requestId, created, model,
                    new ChatCompletionDelta(null, tail), finishReason: null), aborted).ConfigureAwait(false);
            }

            // The last real chunk. Its delta is empty; it exists to carry finish_reason.
            await sse.WriteChunkAsync(Chunk(requestId, created, model,
                new ChatCompletionDelta(null, null), finishReason), aborted).ConfigureAwait(false);

            // Usage, same chars/4 estimate on both sides as the non-streaming path (D44): the
            // progress-callback count is not a token count. Counted off the cutter rather than the
            // backend's returned text, always: after a cut that text runs past what was sent, after a
            // filtered reply it is empty while deltas did go out, and the cutter is the only thing that
            // knows exactly how many characters reached the client.
            var promptTokens = ChatRequestMetrics.EstimateTokens(promptChars);
            var completionTokens = ChatRequestMetrics.EstimateTokens(cutter.ContentLength);

            if (includeUsage)
            {
                await sse.WriteChunkAsync(new ChatCompletionChunk(
                    Id: requestId,
                    Created: created,
                    Model: model,
                    Choices: [],
                    Usage: new CompletionUsage(promptTokens, completionTokens, promptTokens + completionTokens)),
                    aborted).ConfigureAwait(false);
            }

            await sse.WriteAsync(DoneFrame, aborted).ConfigureAwait(false);

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs);
            return null;
        }
        catch (Exception ex) when (aborted.IsCancellationRequested)
        {
            // A write that failed because the client went away, or the cancellation that follows it.
            // There is nobody to report anything to, and it is not an error: swallow it here rather than
            // let it escape as an unhandled request exception. The finally still drains and disposes.
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs: 0, tokens: 0,
                status: ex is OperationCanceledException ? nameof(GenerationStatus.Cancelled) : ex.GetType().Name,
                finish: "-", httpStatus: 0);
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
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-",
                httpStatus: sse.Started ? StatusCodes.Status200OK : failure.StatusCode);
            return await FailAsync(sse, failure, logger, requestId, aborted).ConfigureAwait(false);
        }
        finally
        {
            // Cancel, drain, dispose — in that order, and the order is the whole point. The context is a
            // live WinRT handle that the generation task may still be generating against; disposing it
            // while that task runs is a use-after-dispose, not merely an unobserved task. Cancelling
            // first is what keeps the wait short; awaiting is what makes the disposal safe.
            // Guarded because this was the one statement in the method outside a try, and it stands
            // between a failure and the disposal below. CancelAsync faults when a registration on the
            // token throws, and CsWinRT registers one that calls IAsyncInfo.Cancel() on the live WinRT
            // operation -- a COM call that can fail rather than no-op. Letting it escape would skip both
            // the drain and Dispose(), leaking exactly the handle D43 guarantees is released: worse than
            // the D51 defect, which disposed too early rather than never.
            try
            {
                await generationCts.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "req={RequestId} cancelling the generation threw; draining and disposing anyway.",
                    requestId);
            }

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

            context?.Dispose();
        }
    }

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
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var finished = await Task.WhenAny(wait, Task.Delay(next, timer.Token)).ConfigureAwait(false);
            if (ReferenceEquals(finished, wait))
            {
                await timer.CancelAsync().ConfigureAwait(false);
                return await wait.ConfigureAwait(false);
            }

            // The delay lost the race to nothing but its own token: the client is gone.
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

    private static ChatCompletionChunk Chunk(
        string id,
        long created,
        string model,
        ChatCompletionDelta delta,
        string? finishReason) =>
        new(id, created, model, [new ChatCompletionChunkChoice(0, delta, finishReason)]);

    /// <summary>
    /// Runs the generation and, whatever happens, closes the channel so the reader loop ends. A throw
    /// is deliberately not passed to <c>Complete</c>: the reader finishes normally, drains what was
    /// already queued, and the exception surfaces where the task is awaited.
    /// </summary>
    private static async Task<GenerationResult> GenerateAsync(
        PreparedChatRequest prepared,
        IModelContext context,
        DeltaSink sink,
        ChannelWriter<string> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            return await prepared.Backend.GenerateAsync(
                context,
                prepared.Rendered.Prompt,
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

    /// <summary>
    /// The only thing the backend's callback thread can reach. It holds a channel writer and a
    /// stopwatch and nothing else — no <see cref="HttpResponse"/>, no <see cref="HttpContext"/>, not
    /// even a closure over one — so "never write to the response from the callback" is a property of
    /// what is in scope rather than a rule someone has to remember. Every field is touched through
    /// interlocked operations because <see cref="OnDelta"/> and the request's own task run at once.
    /// </summary>
    private sealed class DeltaSink
    {
        private readonly ChannelWriter<string> _writer;
        private readonly Stopwatch _stopwatch;
        private long _firstTokenTicks;
        private int _count;

        public DeltaSink(ChannelWriter<string> writer, Stopwatch stopwatch)
        {
            _writer = writer;
            _stopwatch = stopwatch;
        }

        /// <summary>Callbacks seen. Not a token count: runtimes batch several tokens per callback.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Stopwatch ticks at the first callback; meaningless when <see cref="Count"/> is 0.</summary>
        public long FirstTokenTicks => Interlocked.Read(ref _firstTokenTicks);

        public void OnDelta(string delta)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                Interlocked.Exchange(ref _firstTokenTicks, _stopwatch.ElapsedTicks);
            }

            // Unbounded channel: TryWrite only fails once the writer is completed, which happens after
            // GenerateAsync has returned and so after the last callback.
            _writer.TryWrite(delta);
        }
    }
}
