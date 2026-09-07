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
/// reached with a <see cref="PreparedChatRequest"/>, so it never validates, never decides a status
/// code and never emits a JSON error body: by the time it runs, 200 and the SSE content type are
/// already the answer.
///
/// Three things are deliberately missing and belong to the next task: the mid-stream
/// <c>data: {"error":...}</c> event, cancel-and-drain on client disconnect, and the <c>: keep-alive</c>
/// comment while waiting for the first token. What is here is the path that works.
/// </summary>
internal sealed class ChatCompletionsStreamEndpoint
{
    /// <summary>Terminator of an SSE stream, per the OpenAI protocol. Not JSON, deliberately.</summary>
    private const string DoneFrame = "data: [DONE]\n\n";

    // Never instantiated: the type exists so the streaming phase has an ILogger<T> category of its
    // own. ChatRequestPreparer takes a plain ILogger and therefore adopts whatever category its caller
    // hands it, so the category has to be chosen on purpose somewhere; this is where.
    private ChatCompletionsStreamEndpoint()
    {
    }

    public static async Task StreamAsync(
        HttpContext http,
        PreparedChatRequest prepared,
        BridgeOptions options,
        TimeProvider time,
        ILogger logger)
    {
        var response = http.Response;
        var aborted = http.RequestAborted;

        var requestId = prepared.RequestId;
        var backendName = prepared.BackendName;
        var promptChars = prepared.PromptChars;

        // Identity of the reply, fixed once and repeated on every chunk: a client that stitches the
        // chunks back together must see the same id/created/model a non-streamed reply would carry.
        var model = prepared.Request.Model ?? prepared.Backend.ModelId;
        var created = time.GetUtcNow().ToUnixTimeSeconds();
        var includeUsage = prepared.Request.StreamOptions?.IncludeUsage == true;

        // X-Accel-Buffering defeats nginx's response buffering, which would otherwise hold the whole
        // stream and deliver it as one lump.
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        IModelContext? context = null;
        var stopwatch = Stopwatch.StartNew();

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
            var generation = GenerateAsync(prepared, context, sink, channel.Writer, aborted);

            // The role chunk. OpenAI clients rely on it to open the assistant message.
            await WriteChunkAsync(response, Chunk(requestId, created, model,
                new ChatCompletionDelta("assistant", string.Empty), finishReason: null), aborted).ConfigureAwait(false);

            // Deliberately not cancelled by `aborted`: the loop must end when the channel completes, so
            // that `generation` is always awaited below and never left unobserved. Draining a
            // disconnected client properly is the next task's job.
            await foreach (var delta in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await WriteChunkAsync(response, Chunk(requestId, created, model,
                    new ChatCompletionDelta(null, delta), finishReason: null), aborted).ConfigureAwait(false);
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

            // The status codes the non-streaming path turns into HTTP errors cannot be HTTP errors here:
            // 200 and the headers went out with the first chunk. The next task replaces this with an SSE
            // error event; until then the stream is still closed cleanly rather than left hanging, and
            // the log line below carries the true status either way.
            var finishReason = result.Status switch
            {
                GenerationStatus.ContentFiltered or GenerationStatus.BlockedByPolicy => "content_filter",
                _ => "stop",
            };

            // The last real chunk. Its delta is empty; it exists to carry finish_reason.
            await WriteChunkAsync(response, Chunk(requestId, created, model,
                new ChatCompletionDelta(null, null), finishReason), aborted).ConfigureAwait(false);

            // Usage, same chars/4 estimate on both sides as the non-streaming path (D-chunk 3): the
            // progress-callback count is not a token count.
            var promptTokens = ChatRequestMetrics.EstimateTokens(promptChars);
            var completionTokens = ChatRequestMetrics.EstimateTokens(result.Text.Length);

            if (includeUsage)
            {
                await WriteChunkAsync(response, new ChatCompletionChunk(
                    Id: requestId,
                    Created: created,
                    Model: model,
                    Choices: [],
                    Usage: new CompletionUsage(promptTokens, completionTokens, promptTokens + completionTokens)),
                    aborted).ConfigureAwait(false);
            }

            await WriteRawAsync(response, DoneFrame, aborted).ConfigureAwait(false);

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs);
        }
        finally
        {
            context?.Dispose();
        }
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

    private static Task WriteChunkAsync(HttpResponse response, ChatCompletionChunk chunk, CancellationToken cancellationToken) =>
        WriteRawAsync(response, $"data: {JsonSerializer.Serialize(chunk, JsonDefaults.Options)}\n\n", cancellationToken);

    /// <summary>
    /// One SSE frame, flushed immediately: without the flush the chunks sit in Kestrel's buffer and the
    /// client sees the whole reply at once, which is the one thing streaming exists to avoid.
    /// </summary>
    private static async Task WriteRawAsync(HttpResponse response, string frame, CancellationToken cancellationToken)
    {
        await response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
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
