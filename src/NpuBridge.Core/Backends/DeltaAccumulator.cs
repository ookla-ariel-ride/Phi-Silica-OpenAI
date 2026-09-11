using System.Text;
using Microsoft.Extensions.Logging;

namespace NpuBridge.Backends;

/// <summary>
/// The delta side of a WinRT <c>IAsyncOperationWithProgress</c> generation, shared by the Phi Silica
/// and Aion adapters so that the text contract (<see cref="ILanguageModelBackend"/>: <c>Text</c> is the
/// deltas delivered, concatenated) is implemented once. Progress delivers the newest token(s) only, on a
/// WinRT thread; this accumulates them, because the accumulation <em>is</em> the text the adapter returns,
/// and delivers each delta to the caller under the same lock, so the order the caller sees is the order
/// the text accumulates in. Completion of the operation does not guarantee the last callback has finished
/// (or even started), so callbacks are counted and drained by <see cref="Drain"/> before the adapter
/// returns; anything arriving after the barrier is dropped rather than delivered to a caller that has
/// moved on, and counted when it is a contract breach (D65).
/// </summary>
public sealed class DeltaAccumulator
{
    /// <summary>Upper bound on waiting for a straggling Progress callback after the operation completed.</summary>
    private static readonly TimeSpan CallbackDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly StringBuilder _accumulated = new();
    private readonly ILogger _logger;
    private readonly string _runtime;
    private readonly Action<string> _onDelta;
    private readonly Action _onLateDelta;
    private readonly Action _onTextMismatch;
    private readonly CancellationToken _cancellationToken;
    private Exception? _deltaFailure;
    private int _inFlight;
    private int _closed;
    private bool _endedCancelled;

    /// <param name="logger">The adapter's logger.</param>
    /// <param name="runtime">The runtime's display name, for log lines ("Phi Silica", "Aion Instruct").</param>
    /// <param name="onDelta">The caller's delta sink. Exceptions it throws are captured, never lost on the WinRT thread.</param>
    /// <param name="onLateDelta">Counts a callback that arrived after a <em>completed</em> generation ended.</param>
    /// <param name="onTextMismatch">Counts a runtime text that disagreed with the delivered deltas.</param>
    /// <param name="cancellationToken">The generation's token, read once when <see cref="Drain"/> closes the barrier: a callback after a generation that ended cancelled is expected and not counted.</param>
    public DeltaAccumulator(
        ILogger logger,
        string runtime,
        Action<string> onDelta,
        Action onLateDelta,
        Action onTextMismatch,
        CancellationToken cancellationToken)
    {
        _logger = logger;
        _runtime = runtime;
        _onDelta = onDelta;
        _onLateDelta = onLateDelta;
        _onTextMismatch = onTextMismatch;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The exception the caller's sink threw, if any. Read after <see cref="Drain"/>.</summary>
    public Exception? DeltaFailure => Volatile.Read(ref _deltaFailure);

    /// <summary>The deltas delivered so far, concatenated. Safe to read at any time; complete after <see cref="Drain"/>.</summary>
    public string Text
    {
        get
        {
            lock (_accumulated)
            {
                return _accumulated.ToString();
            }
        }
    }

    /// <summary>The Progress handler body. Assign <c>op.Progress = (_, delta) => accumulator.OnProgress(delta)</c>.</summary>
    public void OnProgress(string delta)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            if (Volatile.Read(ref _closed) == 1)
            {
                // After a cancellation the barrier closes the moment AsTask throws, so a callback the
                // runtime raises on its way out is expected and its text was going to be discarded
                // anyway: not a contract breach, not counted. After a completion it is text the runtime
                // produced that neither shape will ever see, and it is both. Which of the two applies
                // was fixed when the barrier closed: the streaming endpoint cancels the token in its
                // finally on every path, so the token's state now says nothing about how the generation
                // ended.
                if (_endedCancelled)
                {
                    _logger.LogDebug("A {Runtime} progress callback ({Length} chars) arrived after the cancelled generation ended; dropped.", _runtime, delta.Length);
                }
                else
                {
                    _onLateDelta();
                    _logger.LogWarning("A {Runtime} progress callback ({Length} chars) arrived after the generation completed and was dropped.", _runtime, delta.Length);
                }

                return;
            }

            // Append and deliver under the same lock: if two callbacks ever overlapped, appending inside
            // the lock but delivering outside it could hand the stream "BA" while reporting "AB". Neither
            // the JSON watcher nor the stream's channel sink blocks inside onDelta, so holding the lock
            // across the call costs nothing.
            lock (_accumulated)
            {
                _accumulated.Append(delta);
                try
                {
                    _onDelta(delta);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _deltaFailure, ex, null);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>
    /// Closes the gate so a late callback exits early, then waits for any that are mid-flight. Call
    /// after the operation has ended on every path (complete, cancelled, thrown) and before reading
    /// <see cref="Text"/> for the result.
    /// </summary>
    public void Drain()
    {
        // Written before the gate closes; a callback that sees the gate closed therefore sees this too.
        _endedCancelled = _cancellationToken.IsCancellationRequested;

        // A full fence, not a release-only write: the callback side increments _inFlight and then reads
        // _closed, this side writes _closed and then reads _inFlight, and the two must not both miss.
        Interlocked.Exchange(ref _closed, 1);
        if (!SpinWait.SpinUntil(() => Volatile.Read(ref _inFlight) == 0, CallbackDrainTimeout))
        {
            _logger.LogWarning("A {Runtime} progress callback did not finish within {Timeout}; continuing.", _runtime, CallbackDrainTimeout);
        }
    }

    /// <summary>
    /// The delivered deltas are the answer, on every status (D65). The runtime's own text is a
    /// cross-check, not a source: if it ever differs, the JSON path would have cut different characters
    /// from the ones the stream cut, and the same request would answer differently by shape. Returns
    /// the deltas; counts and logs a disagreement.
    /// </summary>
    public string Reconcile(string? runtimeText)
    {
        var text = Text;
        if (!string.IsNullOrEmpty(runtimeText) && !string.Equals(runtimeText, text, StringComparison.Ordinal))
        {
            _onTextMismatch();
            _logger.LogWarning(
                "{Runtime}'s result text ({ResultLength} chars) differs from the delivered deltas ({DeltaLength} chars); returning the deltas.",
                _runtime, runtimeText.Length, text.Length);
        }

        return text;
    }
}
