using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// Serializes generation against the one NPU model handle (PLAN §2.7, chunk 8). One worker drains a
/// bounded queue of jobs strictly one at a time, in FIFO order, so two concurrent requests never each
/// grab their own context the way chunk 5 left them able to. A full queue rejects the caller outright
/// rather than blocking it: the caller is an HTTP request with a client waiting on the other end, and a
/// request that blocked inside <see cref="ScheduleAsync{TResult}"/> would hold its connection, its
/// cancellation token and, on the streaming shape, its not-yet-committed response hostage until a slot
/// opened. D52 needs the queue-full decision made before the first SSE frame, so rejection is
/// synchronous with the enqueue attempt — never awaited, never a wait with a timeout.
///
/// This class knows nothing about backends, contexts or HTTP: <see cref="ScheduleAsync{TResult}"/>
/// takes an arbitrary async operation, so both response shapes drive their own generation through it
/// without either duplicating the queueing logic.
/// </summary>
/// <remarks>
/// Invariants that must not regress:
/// <list type="bullet">
///   <item>The worker never starts a job's body until the previous job's body has completed, including
///   one still running after its own token was cancelled — D51 one level up. Awaiting a cancelled
///   operation to the end is the drain; only then does the loop move to the next job.</item>
///   <item>A job cancelled while still queued completes its caller as
///   <see cref="ScheduleResultKind.Cancelled"/> the instant its token fires, without ever invoking its
///   body: the model is never touched for work nobody is waiting for, and the caller does not wait for
///   the worker to drain to its position first. The queue slot itself is freed only when the worker
///   reaches it (deferred; see the fix report).</item>
///   <item>An enqueue attempt after the scheduler has started shutting down is
///   <see cref="ScheduleResultKind.Cancelled"/>, not <see cref="ScheduleResultKind.Rejected"/>: a
///   stopped scheduler is never coming back to honour a <c>Retry-After</c>.</item>
///   <item>A queue-full rejection carries a <c>Retry-After</c>, already computed as whole seconds:
///   queue depth times the rolling average generation duration, floored at 1. With no generation yet
///   completed the average is 0, so a cold-start rejection floors to 1 second — PLAN §2.7 does not
///   define this case; that floor is the decision (task-1-brief.md).</item>
///   <item>Shutdown stops accepting new work and drains whatever is left in the queue as cancelled
///   rather than running it, but still awaits a job already running to its natural end (same D51
///   invariant, not suspended for shutdown).</item>
/// </list>
/// </remarks>
public sealed class GenerationScheduler : IHostedService, IAsyncDisposable
{
    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for the worker to drain before giving up and returning
    /// anyway. Mirrors <c>BackendLifecycle.DisposeGracePeriod</c>: disposal must have the same escape
    /// hatch <see cref="StopAsync"/> has, or the two disagree about whether shutdown can hang forever on
    /// a generation that ignores cancellation (fix-round-1 finding 1).
    /// </summary>
    public static readonly TimeSpan DisposeGracePeriod = TimeSpan.FromSeconds(15);

    private readonly Channel<IQueuedJob> _queue;
    private readonly TimeProvider _time;
    private readonly ILogger<GenerationScheduler> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _statsGate = new();

    private double _averageGenerationSeconds;
    private long _completedGenerations;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;

    public GenerationScheduler(BridgeOptions options, TimeProvider time, ILogger<GenerationScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _time = time;
        _logger = logger;

        // BridgeOptionsBinder validates QueueCapacity to 1..1000; the floor here is only for a
        // scheduler built directly (as every test does), never routed through the binder.
        var capacity = Math.Max(1, options.QueueCapacity);
        _queue = Channel.CreateBounded<IQueuedJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Jobs waiting for the worker right now — dequeued and running does not count. Read by <c>/healthz</c> (Task 2).</summary>
    public int QueueDepth => _queue.Reader.Count;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The worker's own lifetime is governed by _shutdown (via StopAsync/DisposeAsync), not by the
        // token the host happens to pass to this call, so that is deliberate rather than forwarded.
        _worker = Task.Run(RunWorkerAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops accepting new work and waits for the worker to drain. A job already running is awaited to
    /// its natural end regardless of who asked to cancel it (D51); everything still only queued is
    /// completed as <see cref="ScheduleResultKind.Cancelled"/> instead of run, so shutdown never hangs
    /// on work nobody will collect.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();

        var finished = await Task.WhenAny(_worker, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
            .ConfigureAwait(false);
        if (finished != _worker)
        {
            _logger.LogWarning("Generation scheduler did not drain before the host's own shutdown deadline; a job may still be running.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();

        // Same escape hatch as StopAsync, on its own bounded clock rather than the host's: the two
        // must not disagree about whether disposal can be made to wait forever behind a generation
        // that never observes cancellation (fix-round-1 finding 1).
        var finished = await Task.WhenAny(_worker, Task.Delay(DisposeGracePeriod)).ConfigureAwait(false);
        if (finished != _worker)
        {
            _logger.LogWarning("Generation scheduler did not drain within {Grace}s of disposal; a job may still be running.",
                DisposeGracePeriod.TotalSeconds);
        }

        _shutdown.Dispose();
    }

    /// <summary>
    /// Enqueues one generation. Returns immediately — never blocks — with
    /// <see cref="ScheduleResultKind.Rejected"/> when the queue is already full, or with
    /// <see cref="ScheduleResultKind.Cancelled"/> when the scheduler is already shutting down (a
    /// stopped scheduler answers "this will never run" rather than "try again in N seconds", since
    /// nothing will be here to honour a <c>Retry-After</c>). Otherwise the returned task completes once
    /// the worker has run <paramref name="operation"/>, or has dropped it, uncalled, because
    /// <paramref name="cancellationToken"/> fired — either while the job was still queued (completed
    /// immediately, without waiting for the worker to drain to its position) or, if the operation itself
    /// throws <see cref="OperationCanceledException"/> for that same token, while it was running.
    /// <see cref="ScheduleResult{TResult}.QueueWait"/> on every non-rejected outcome is how long the job
    /// actually waited, for the caller's <c>queue_wait_ms</c> log field; a rejected or already-cancelled
    /// job never queued at all, so its <see cref="ScheduleResult{TResult}.RetryAfterSeconds"/> (rejected
    /// only) is what matters instead.
    /// </summary>
    /// <remarks>
    /// The returned task can also fault: <paramref name="operation"/> throwing anything other than an
    /// <see cref="OperationCanceledException"/> for its own token is not translated into a
    /// <see cref="ScheduleResultKind"/> — the caller sees that exception rethrown from its own
    /// <c>await</c>, exactly as if it had called <paramref name="operation"/> directly. Only
    /// cancellation and queue state are the scheduler's to interpret.
    /// </remarks>
    public Task<ScheduleResult<TResult>> ScheduleAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var job = new QueuedJob<TResult>(operation, _time.GetUtcNow(), _time, cancellationToken);
        if (_queue.Writer.TryWrite(job))
        {
            // Controller ruling, fix-round-1 finding 3: complete the caller the moment its own token
            // fires rather than leaving it to wait for the worker to drain to this job's position in
            // the queue. The worker's own Drop/RunAsync completions already use TrySetResult, so
            // whichever of the two gets there first wins and the other is a no-op. The queue slot
            // itself is not freed early -- that half is deferred (see the fix report).
            job.ArmCancellationCompletion();
            return job.Completion.Task;
        }

        if (_shutdown.IsCancellationRequested)
        {
            // Controller ruling, review finding 8: the scheduler is going away, not merely busy, so
            // the true answer is Cancelled -- Task 2 turns this into a 503, not a 429 with a
            // Retry-After aimed at a process that will not be here to honour it.
            return Task.FromResult(ScheduleResult.Cancelled<TResult>(TimeSpan.Zero));
        }

        var retryAfter = ComputeRetryAfterSeconds();
        _logger.LogWarning("Generation queue is full (depth {Depth}); rejecting with Retry-After {RetryAfterSeconds}s.",
            QueueDepth, retryAfter);
        return Task.FromResult(ScheduleResult.Rejected<TResult>(retryAfter));
    }

    /// <summary>queue depth × rolling average generation seconds, floored at 1, in whole seconds (task-1-brief.md).</summary>
    private int ComputeRetryAfterSeconds()
    {
        var depth = QueueDepth;
        double average;
        lock (_statsGate)
        {
            average = _averageGenerationSeconds;
        }

        var seconds = (int)Math.Ceiling(depth * average);
        return Math.Max(1, seconds);
    }

    private async Task RunWorkerAsync()
    {
        while (await _queue.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out var job))
            {
                // From here on this job is the worker's to decide about; ArmCancellationCompletion's
                // registration steps back once it sees this (fix-round-1 finding 3's carve-out), so a
                // cancel that arrives after this point is the running job's own to answer for -- D51 --
                // rather than something this loop preempts out from under it.
                job.MarkDequeued();

                var queueWait = _time.GetUtcNow() - job.EnqueuedAt;

                // Shutdown or the request's own cancellation: either way this job was never dequeued
                // while there was still someone to run it for, so its body never runs.
                if (_shutdown.IsCancellationRequested || job.IsCancellationRequested)
                {
                    job.Drop(queueWait);
                    continue;
                }

                var start = _time.GetUtcNow();
                await job.RunAsync(queueWait).ConfigureAwait(false);
                RecordGenerationDuration((_time.GetUtcNow() - start).TotalSeconds);
            }
        }
    }

    /// <summary>Cumulative mean over every job whose body actually ran (completed or cancelled mid-run) — never one dropped while only queued, which touched the model for zero seconds and would only drag the average down.</summary>
    private void RecordGenerationDuration(double seconds)
    {
        lock (_statsGate)
        {
            _completedGenerations++;
            _averageGenerationSeconds += (seconds - _averageGenerationSeconds) / _completedGenerations;
        }
    }

    /// <summary>The type-erased half of <see cref="QueuedJob{TResult}"/> the worker loop can hold in one channel regardless of what each caller's operation returns.</summary>
    private interface IQueuedJob
    {
        DateTimeOffset EnqueuedAt { get; }

        bool IsCancellationRequested { get; }

        /// <summary>
        /// Marks that the worker now owns this job's fate. Called exactly once, the moment it is
        /// dequeued, whether it is about to be run or dropped -- from this point on, a cancellation of
        /// its own token is the worker's (via <see cref="Drop"/> or <see cref="RunAsync"/>) to answer,
        /// not the standing registration's.
        /// </summary>
        void MarkDequeued();

        /// <summary>Completes the job as cancelled without ever invoking its operation.</summary>
        void Drop(TimeSpan queueWait);

        /// <summary>Invokes the operation and completes the job with whatever it returned, threw, or was cancelled with.</summary>
        Task RunAsync(TimeSpan queueWait);
    }

    private sealed class QueuedJob<TResult> : IQueuedJob
    {
        private readonly Func<CancellationToken, Task<TResult>> _operation;
        private readonly TimeProvider _time;
        private readonly CancellationToken _cancellationToken;
        private CancellationTokenRegistration _cancellationRegistration;
        private int _dequeued;

        public QueuedJob(Func<CancellationToken, Task<TResult>> operation, DateTimeOffset enqueuedAt, TimeProvider time, CancellationToken cancellationToken)
        {
            _operation = operation;
            EnqueuedAt = enqueuedAt;
            _time = time;
            _cancellationToken = cancellationToken;
        }

        public TaskCompletionSource<ScheduleResult<TResult>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset EnqueuedAt { get; }

        public bool IsCancellationRequested => _cancellationToken.IsCancellationRequested;

        public void MarkDequeued() => Volatile.Write(ref _dequeued, 1);

        /// <summary>
        /// Completes <see cref="Completion"/> as <see cref="ScheduleResultKind.Cancelled"/> the instant
        /// <see cref="_cancellationToken"/> fires, instead of leaving the caller to wait for the worker
        /// to drain to this job's position in the queue (fix-round-1 finding 3) -- but only while the
        /// job is still genuinely queued. Once <see cref="MarkDequeued"/> has run, a later cancel is the
        /// worker's own to answer: D51 requires an already-running operation be awaited to whatever end
        /// it reaches (which may be a normal <see cref="ScheduleResultKind.Completed"/>, if the
        /// operation itself chooses to ignore its token), not preempted by this callback the instant the
        /// token fires. Races harmlessly with <see cref="Drop"/> and <see cref="RunAsync"/> in every
        /// case: all three use <c>TrySetResult</c>, so only the first to arrive matters.
        /// <see cref="CancellationToken.UnsafeRegister"/> is used (not <c>Register</c>) because this
        /// callback captures no ambient context worth flowing, matching the reviewer's suggestion.
        /// </summary>
        public void ArmCancellationCompletion()
        {
            _cancellationRegistration = _cancellationToken.UnsafeRegister(static state =>
            {
                var job = (QueuedJob<TResult>)state!;
                if (Volatile.Read(ref job._dequeued) != 0)
                {
                    // The worker already owns this job (running, or about to decide to drop it via its
                    // own IsCancellationRequested check); Drop/RunAsync's own completion and dispose is
                    // what settles it, not this callback.
                    return;
                }

                var queueWait = job._time.GetUtcNow() - job.EnqueuedAt;
                job.Completion.TrySetResult(ScheduleResult.Cancelled<TResult>(queueWait));

                // Safe to dispose the registration from inside its own callback: it is already
                // running, so this only stops it from being disposed again later and releases the
                // token's reference to it.
                job._cancellationRegistration.Dispose();
            }, this);
        }

        public void Drop(TimeSpan queueWait)
        {
            Completion.TrySetResult(ScheduleResult.Cancelled<TResult>(queueWait));
            _cancellationRegistration.Dispose();
        }

        public async Task RunAsync(TimeSpan queueWait)
        {
            try
            {
                var result = await _operation(_cancellationToken).ConfigureAwait(false);
                Completion.TrySetResult(ScheduleResult.Completed(result, queueWait));
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetResult(ScheduleResult.Cancelled<TResult>(queueWait));
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
            finally
            {
                _cancellationRegistration.Dispose();
            }
        }
    }
}

public enum ScheduleResultKind
{
    /// <summary>The operation ran to completion and returned a result.</summary>
    Completed,

    /// <summary>
    /// The job's cancellation token fired before the worker reached it (its body never ran), or the
    /// operation itself ended by throwing <see cref="OperationCanceledException"/> for that same token.
    /// </summary>
    Cancelled,

    /// <summary>The queue was already full; the operation never ran and never will for this call. <see cref="ScheduleResult{TResult}.RetryAfterSeconds"/> is set.</summary>
    Rejected,
}

/// <summary>
/// What <see cref="GenerationScheduler.ScheduleAsync{TResult}"/> hands back. <see cref="Result"/> is
/// meaningful only when <see cref="Kind"/> is <see cref="ScheduleResultKind.Completed"/>;
/// <see cref="RetryAfterSeconds"/> only when it is <see cref="ScheduleResultKind.Rejected"/>.
/// <see cref="QueueWait"/> is meaningful on every kind except <see cref="ScheduleResultKind.Rejected"/>,
/// which never queued at all.
/// </summary>
public sealed class ScheduleResult<TResult>
{
    internal ScheduleResult(ScheduleResultKind kind, TResult? result, TimeSpan queueWait, int retryAfterSeconds)
    {
        Kind = kind;
        Result = result;
        QueueWait = queueWait;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public ScheduleResultKind Kind { get; }

    /// <summary>The operation's return value. Default when <see cref="Kind"/> is not <see cref="ScheduleResultKind.Completed"/>.</summary>
    public TResult? Result { get; }

    /// <summary>
    /// How long the job sat in the queue before the worker reached it — the caller's <c>queue_wait_ms</c>
    /// log field. <see cref="TimeSpan.Zero"/> on <see cref="ScheduleResultKind.Rejected"/>.
    /// </summary>
    public TimeSpan QueueWait { get; }

    /// <summary>Whole seconds for the <c>Retry-After</c> header. Zero when <see cref="Kind"/> is not <see cref="ScheduleResultKind.Rejected"/>.</summary>
    public int RetryAfterSeconds { get; }
}

/// <summary>
/// Non-generic factories for <see cref="ScheduleResult{TResult}"/> (CA1000: a generic type may not
/// declare its own static members), so a caller writes <c>ScheduleResult.Completed(text, wait)</c> with
/// <typeparamref name="TResult"/> inferred rather than named.
/// </summary>
public static class ScheduleResult
{
    public static ScheduleResult<TResult> Completed<TResult>(TResult result, TimeSpan queueWait) =>
        new(ScheduleResultKind.Completed, result, queueWait, 0);

    public static ScheduleResult<TResult> Cancelled<TResult>(TimeSpan queueWait) =>
        new(ScheduleResultKind.Cancelled, default, queueWait, 0);

    public static ScheduleResult<TResult> Rejected<TResult>(int retryAfterSeconds) =>
        new(ScheduleResultKind.Rejected, default, TimeSpan.Zero, retryAfterSeconds);
}
