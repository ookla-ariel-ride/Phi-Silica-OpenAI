using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// The parts of phase two that are the same whichever shape the reply takes. Both
/// <see cref="ChatCompletionsEndpoint"/> and <see cref="ChatCompletionsStreamEndpoint"/> drive one
/// generation, time its first delta, may cancel it early, and then report what came back; only the
/// writing differs. Those shared steps used to be written out twice, and D56 and D57 each record a
/// drift between exactly those two copies — the same generation labelled <c>stop</c> on one shape and
/// <c>length</c> on the other, a backend <c>Error</c> answered 502 on one and 200 on the other. Chunk
/// 7's buffer-the-whole-reply path would have been a third copy, so they live here instead.
/// </summary>
internal static class GenerationPipeline
{
    /// <summary>
    /// Cancels the generation without letting the cancel itself fail the request.
    /// <see cref="CancellationTokenSource.CancelAsync"/> faults when a registration on the token
    /// throws, and CsWinRT registers one that calls <c>IAsyncInfo.Cancel()</c> on the live WinRT
    /// operation — a COM call that can fail rather than no-op. Every place either shape cancels goes on
    /// to await the generation and dispose the context regardless, so a throw here is a Debug line and
    /// nothing more. Cancelling a source twice is a no-op, so calling this at the cut and again on the
    /// way out is fine.
    /// </summary>
    /// <param name="where">Where the cancel was made, for the log line: "at the cut", "on the way out".</param>
    public static async Task CancelGuardedAsync(
        CancellationTokenSource generationCts,
        ILogger logger,
        string requestId,
        string where)
    {
        try
        {
            await generationCts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "req={RequestId} cancelling the generation {Where} threw; draining and disposing anyway.",
                requestId, where);
        }
    }

    /// <summary>
    /// The whole model output, verbatim, under <c>--verbose</c>. The one place the raw text is logged:
    /// what a client is shown has been through the cut and the status mapping, so this is how a
    /// question about the model rather than about the bridge gets answered.
    /// </summary>
    public static void LogRawOutput(ILogger logger, BridgeOptions options, string requestId, GenerationResult result)
    {
        if (!options.Verbose)
        {
            return;
        }

        logger.LogInformation("req={RequestId} raw model output ({Status}):\n---- output ----\n{Text}\n---- end ----",
            requestId, result.Status, result.Text);
    }
}

/// <summary>
/// The only thing the backend's callback thread can reach. It holds a stopwatch and exactly one
/// destination, either a <see cref="ChannelWriter{T}"/> or a <see cref="CutWatcher"/>: no
/// <see cref="Microsoft.AspNetCore.Http.HttpResponse"/>, no <c>HttpContext</c>, and no delegate that
/// could close over one. So "never write to the response from the callback" stays a property of what
/// is in scope rather than a rule someone has to remember. An <c>Action&lt;string&gt;</c> parameter here
/// would accept a closure over the response and give that property away, which is why both
/// destinations are named types and why the constructor is private.
///
/// The mutable fields are touched through interlocked operations because <see cref="OnDelta"/> and the
/// request's own task run at once; the destination and the stopwatch are readonly and need none.
/// </summary>
internal sealed class DeltaSink
{
    private readonly Stopwatch _stopwatch;
    private readonly ChannelWriter<string>? _writer;
    private readonly CutWatcher? _watcher;
    private long _firstTokenTicks;
    private int _count;

    private DeltaSink(Stopwatch stopwatch, ChannelWriter<string>? writer, CutWatcher? watcher)
    {
        _stopwatch = stopwatch;
        _writer = writer;
        _watcher = watcher;
    }

    /// <summary>
    /// The streaming path's sink: deltas cross into the channel whose single reader owns the response.
    /// That reader runs the cut, so there is no watcher here.
    /// </summary>
    /// <param name="stopwatch">Started when the request's generation phase began; read at the first delta.</param>
    public static DeltaSink ToChannel(Stopwatch stopwatch, ChannelWriter<string> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new DeltaSink(stopwatch, writer, watcher: null);
    }

    /// <summary>
    /// The non-streaming path's sink. There is no channel — the handler awaits the whole text — but the
    /// deltas are still watched as they go by, to decide when to stop the model early.
    /// </summary>
    /// <param name="watcher">Null when the request set no limits, which is the ordinary case.</param>
    public static DeltaSink ToWatcher(Stopwatch stopwatch, CutWatcher? watcher) =>
        new(stopwatch, writer: null, watcher);

    /// <summary>Callbacks seen. Not a token count: runtimes batch several tokens per callback (D44).</summary>
    private int Count => Volatile.Read(ref _count);

    /// <summary>Stopwatch ticks at the first callback; meaningless when <see cref="Count"/> is 0.</summary>
    private long FirstTokenTicks => Interlocked.Read(ref _firstTokenTicks);

    /// <summary>
    /// Time to first token, the one thing either handler asks this type for. A generation that produced
    /// no delta at all has no first token to time, and reporting 0 would read as an instant one, so the
    /// whole elapsed time is reported instead.
    /// </summary>
    public double TtftMs(double totalMs) => Count == 0 ? totalMs : FirstTokenTicks * 1000.0 / Stopwatch.Frequency;

    public void OnDelta(string delta)
    {
        if (Interlocked.Increment(ref _count) == 1)
        {
            Interlocked.Exchange(ref _firstTokenTicks, _stopwatch.ElapsedTicks);
        }

        // Exactly one of these is ever set, so their order here says nothing and must not be read as a
        // rule. A future caller that wants both -- a streamed request that also watches its own deltas
        // -- has to add a third factory and decide the order there, because the watcher seeing a delta
        // after the channel reader has already acted on it is a different cut from the one this path
        // makes. Unbounded channel: TryWrite only fails once the writer is completed, which happens
        // after GenerateAsync has returned and so after the last callback.
        _writer?.TryWrite(delta);
        _watcher?.Accept(delta);
    }
}

/// <summary>
/// The non-streaming path's early stop. It runs an <see cref="OutputCutter"/> over the deltas purely to
/// decide when the generation has produced enough; the authoritative cut is applied afterwards to the
/// text the backend finally reports, with a second cutter, so the answer does not depend on which
/// deltas this one happened to see.
///
/// It never cancels anything itself. <see cref="Accept"/> runs on the backend's callback thread, and
/// cancelling from there is wrong twice over: a straight <c>Cancel()</c> can complete the generation's
/// await inline and re-enter the adapter while it is still inside this callback (Phi Silica then spins
/// draining a callback that cannot finish until it returns), and <c>CancelAfter(0)</c> moves the cancel
/// onto a timer thread, where a throwing registration — CsWinRT's <c>IAsyncInfo.Cancel()</c> on the live
/// operation is one — is rethrown with nothing above it to catch it, and the process terminates. So this
/// completes <see cref="Signal"/> and the request's own task, awaiting it, cancels on its own thread
/// inside a try. <c>RunContinuationsAsynchronously</c> keeps that continuation off the callback thread too.
///
/// The streaming path needs none of this: it drives its cutter from the single channel reader, which is
/// already the thread that owns the response.
/// </summary>
internal sealed class CutWatcher
{
    private readonly OutputCutter _cutter;
    private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CutWatcher(OutputLimits limits) => _cutter = new OutputCutter(limits);

    /// <summary>Completes once the model should be stopped, and never faults. Never completes if no limit fires.</summary>
    public Task Signal => _signal.Task;

    /// <summary>
    /// One delta, from the backend's thread. Locked because the cutter is not thread-safe and a runtime
    /// is free to raise the next callback before this one returns.
    /// </summary>
    public void Accept(string delta)
    {
        // StopRequested, not IsCut: with a token budget the cutter may know the budget is passed before
        // it can place the cut exactly (D80); either way the model stops and the authoritative cut runs
        // over the text the backend returns.
        bool stop;
        lock (_cutter)
        {
            if (_cutter.StopRequested)
            {
                return;
            }

            _cutter.Accept(delta);
            stop = _cutter.StopRequested;
        }

        if (stop)
        {
            _signal.TrySetResult();
        }
    }
}
