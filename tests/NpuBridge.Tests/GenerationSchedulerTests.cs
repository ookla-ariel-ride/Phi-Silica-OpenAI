using Microsoft.Extensions.Logging.Abstractions;
using NpuBridge.Api;
using NpuBridge.Configuration;

namespace NpuBridge.Tests;

/// <summary>
/// The scheduler on its own, with no backend and no HTTP: <see cref="GenerationScheduler.ScheduleAsync{TResult}"/>
/// takes an arbitrary async operation, so every test drives it with hand-built gates the way
/// <c>FakeBackend</c>'s <c>StartGate</c>/<c>FirstTokenGate</c>/<c>CancellationGate</c> drive a
/// generation — released by the test, never by a clock (D54). Ordering is asserted structurally: the
/// worker is a single reader over a single channel, so "job B cannot have started" while job A's gate
/// is still open is a fact about the implementation, not a timing guess.
/// </summary>
public class GenerationSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    private static GenerationScheduler NewScheduler(int queueCapacity = 4, TimeProvider? time = null) =>
        new(new BridgeOptions { QueueCapacity = queueCapacity }, time ?? TimeProvider.System, NullLogger<GenerationScheduler>.Instance);

    [Fact]
    public async Task Jobs_run_one_at_a_time()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var order = new List<int>();
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();

            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                lock (order) { order.Add(1); }
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);

            await startedA.Task;

            var taskB = scheduler.ScheduleAsync<string>(ct =>
            {
                lock (order) { order.Add(2); }
                return Task.FromResult("b");
            }, CancellationToken.None);

            // Structural, not timing-based: the worker is a single reader awaiting A's body to
            // completion, and A's body is parked on gateA, so B cannot have been dequeued yet.
            lock (order)
            {
                Assert.DoesNotContain(2, order);
            }

            gateA.SetResult();
            var resultA = await taskA;
            var resultB = await taskB;

            Assert.Equal(new[] { 1, 2 }, order);
            Assert.Equal(ScheduleResultKind.Completed, resultA.Kind);
            Assert.Equal("a", resultA.Result);
            Assert.Equal(ScheduleResultKind.Completed, resultB.Kind);
            Assert.Equal("b", resultB.Result);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Ordering_is_FIFO_across_more_than_two_jobs()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var order = new List<int>();
            var gate = new TaskCompletionSource();
            var startedFirst = new TaskCompletionSource();

            // The first job holds the worker so the other three are still sitting in the channel,
            // in the order they were enqueued, when the worker finally reaches them.
            var first = scheduler.ScheduleAsync<int>(async ct =>
            {
                startedFirst.TrySetResult();
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                lock (order) { order.Add(0); }
                return 0;
            }, CancellationToken.None);
            await startedFirst.Task;

            var second = Enqueue(2);
            var third = Enqueue(3);
            var fourth = Enqueue(4);

            Task<ScheduleResult<int>> Enqueue(int n) => scheduler.ScheduleAsync<int>(ct =>
            {
                lock (order) { order.Add(n); }
                return Task.FromResult(n);
            }, CancellationToken.None);

            gate.SetResult();
            await first;
            await second;
            await third;
            await fourth;

            Assert.Equal(new[] { 0, 2, 3, 4 }, order);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enqueue_past_capacity_is_rejected_with_a_retry_after_of_at_least_one_second()
    {
        var scheduler = NewScheduler(queueCapacity: 1);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task; // A is running; the channel itself is empty.

            var gateB = new TaskCompletionSource();
            var taskB = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateB.Task.WaitAsync(ct).ConfigureAwait(false);
                return "b";
            }, CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1); // B fills the one slot.

            // With no generation ever completed the rolling average is 0, so the floor applies.
            var rejected = await scheduler.ScheduleAsync<string>(_ => Task.FromResult("c"), CancellationToken.None);

            Assert.Equal(ScheduleResultKind.Rejected, rejected.Kind);
            Assert.True(rejected.RetryAfterSeconds >= 1);
            Assert.Equal(TimeSpan.Zero, rejected.QueueWait);

            gateA.SetResult();
            gateB.SetResult();
            await taskA;
            await taskB;
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retry_after_is_queue_depth_times_the_rolling_average_floored_at_one()
    {
        var time = new ManualTimeProvider(T0);
        var scheduler = NewScheduler(queueCapacity: 2, time: time);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            // Two completed generations, 2s and 4s: rolling average settles at 3s, measured entirely
            // through the manual clock -- no real delay anywhere in this test.
            await RunOneJobAsync(scheduler, time, TimeSpan.FromSeconds(2));
            await RunOneJobAsync(scheduler, time, TimeSpan.FromSeconds(4));

            // Fill the queue to its capacity of 2: X is running, Y and Z sit in the channel.
            var gateX = new TaskCompletionSource();
            var startedX = new TaskCompletionSource();
            var taskX = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedX.TrySetResult();
                await gateX.Task.WaitAsync(ct).ConfigureAwait(false);
                return "x";
            }, CancellationToken.None);
            await startedX.Task;

            var gateY = new TaskCompletionSource();
            var taskY = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateY.Task.WaitAsync(ct).ConfigureAwait(false);
                return "y";
            }, CancellationToken.None);
            var gateZ = new TaskCompletionSource();
            var taskZ = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateZ.Task.WaitAsync(ct).ConfigureAwait(false);
                return "z";
            }, CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 2);

            var rejected = await scheduler.ScheduleAsync<string>(_ => Task.FromResult("w"), CancellationToken.None);

            Assert.Equal(ScheduleResultKind.Rejected, rejected.Kind);
            Assert.Equal(6, rejected.RetryAfterSeconds); // ceil(2 * 3.0)

            gateX.SetResult();
            gateY.SetResult();
            gateZ.SetResult();
            await taskX;
            await taskY;
            await taskZ;
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    /// <summary>Runs one job whose measured duration is exactly <paramref name="duration"/>, advancing only the manual clock -- never a real delay.</summary>
    private static async Task RunOneJobAsync(GenerationScheduler scheduler, ManualTimeProvider time, TimeSpan duration)
    {
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var task = scheduler.ScheduleAsync<string>(async ct =>
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return "ok";
        }, CancellationToken.None);

        await started.Task; // the worker has captured its start time by this point
        time.Advance(duration);
        gate.SetResult();
        await task;
    }

    [Fact]
    public async Task A_job_cancelled_while_queued_never_runs_its_body()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task; // A is running and holds the worker.

            using var ctsB = new CancellationTokenSource();
            var bodyRan = false;
            var taskB = scheduler.ScheduleAsync<string>(ct =>
            {
                bodyRan = true;
                return Task.FromResult("b");
            }, ctsB.Token);

            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1); // B confirmed still queued, not dequeued.
            ctsB.Cancel();

            gateA.SetResult();
            var resultA = await taskA;
            var resultB = await taskB;

            Assert.Equal(ScheduleResultKind.Completed, resultA.Kind);
            Assert.Equal(ScheduleResultKind.Cancelled, resultB.Kind);
            Assert.False(bodyRan);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_job_cancelled_while_running_is_awaited_to_completion_before_the_next_job_starts()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var order = new List<string>();
            var startedB = new TaskCompletionSource();
            var letBFinish = new TaskCompletionSource();
            using var ctsB = new CancellationTokenSource();

            // Models a runtime whose in-flight operation cannot be stopped on demand (the fake's
            // CancellationGate): the body ignores its own token entirely and only ends when the test
            // lets it, so "the next job waited" is a fact about the scheduler, not about how fast this
            // body happens to run.
            var taskB = scheduler.ScheduleAsync<string>(async ct =>
            {
                lock (order) { order.Add("b-start"); }
                startedB.TrySetResult();
                await letBFinish.Task.ConfigureAwait(false);
                lock (order) { order.Add("b-end"); }
                return "b";
            }, ctsB.Token);
            await startedB.Task;

            var taskC = scheduler.ScheduleAsync<string>(ct =>
            {
                lock (order) { order.Add("c"); }
                return Task.FromResult("c");
            }, CancellationToken.None);

            ctsB.Cancel(); // B's own token fires while B is still running.

            // Structural: C cannot have run, because the worker is still awaiting B's RunAsync task,
            // which is parked on letBFinish and does not observe ctsB at all.
            lock (order)
            {
                Assert.DoesNotContain("c", order);
            }

            letBFinish.SetResult();
            var resultB = await taskB;
            var resultC = await taskC;

            Assert.Equal(new[] { "b-start", "b-end", "c" }, order);
            Assert.Equal(ScheduleResultKind.Completed, resultB.Kind); // B's own body decided to finish normally despite the cancel.
            Assert.Equal(ScheduleResultKind.Completed, resultC.Kind);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Shutdown_completes_still_queued_jobs_as_cancelled_without_running_them()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);

        var gateA = new TaskCompletionSource();
        var startedA = new TaskCompletionSource();
        var taskA = scheduler.ScheduleAsync<string>(async ct =>
        {
            startedA.TrySetResult();
            await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
            return "a";
        }, CancellationToken.None);
        await startedA.Task; // A is running and holds the worker.

        var bodyRanB = false;
        var taskB = scheduler.ScheduleAsync<string>(ct =>
        {
            bodyRanB = true;
            return Task.FromResult("b");
        }, CancellationToken.None);
        await TestWait.UntilAsync(() => scheduler.QueueDepth == 1); // B confirmed still queued.

        // Shutdown is requested while B is provably still sitting in the channel and A is provably
        // still running (parked on gateA): the worker cannot have reached B yet.
        var stopTask = scheduler.StopAsync(CancellationToken.None);
        gateA.SetResult(); // let A finish so shutdown's drain can proceed; this is D51, not suspended for shutdown.
        var resultA = await taskA;
        await stopTask; // does not hang: B is drained as cancelled, then the worker exits.

        var resultB = await taskB;
        Assert.Equal(ScheduleResultKind.Completed, resultA.Kind);
        Assert.Equal(ScheduleResultKind.Cancelled, resultB.Kind);
        Assert.False(bodyRanB);
    }

    [Fact]
    public async Task Queue_depth_reflects_what_is_waiting_not_the_job_running()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(0, scheduler.QueueDepth);

            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task;
            Assert.Equal(0, scheduler.QueueDepth); // A is running; nothing is waiting.

            var gateB = new TaskCompletionSource();
            var taskB = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateB.Task.WaitAsync(ct).ConfigureAwait(false);
                return "b";
            }, CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1);

            var gateC = new TaskCompletionSource();
            var taskC = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateC.Task.WaitAsync(ct).ConfigureAwait(false);
                return "c";
            }, CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 2);

            gateA.SetResult();
            await taskA;
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1); // B is now running; C still waits.

            gateB.SetResult();
            await taskB;
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 0); // C is now running.

            gateC.SetResult();
            await taskC;
            Assert.Equal(0, scheduler.QueueDepth);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Completed_result_reports_how_long_the_job_waited_in_the_queue()
    {
        var time = new ManualTimeProvider(T0);
        var scheduler = NewScheduler(time: time);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task;

            var taskB = scheduler.ScheduleAsync<string>(_ => Task.FromResult("b"), CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1);

            time.Advance(TimeSpan.FromSeconds(7));
            gateA.SetResult();
            await taskA;

            var resultB = await taskB;
            Assert.Equal(ScheduleResultKind.Completed, resultB.Kind);
            Assert.Equal(TimeSpan.FromSeconds(7), resultB.QueueWait);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }
}
