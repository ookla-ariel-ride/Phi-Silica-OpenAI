using Microsoft.Extensions.Logging.Abstractions;
using NpuBridge.Backends;

namespace NpuBridge.Tests;

/// <summary>
/// The delta accumulator both real adapters share (D67). It is the whole of the text contract (D65):
/// <c>Text</c> is the deltas delivered, in delivery order; a callback that arrives after the barrier
/// is dropped and counted when the generation had completed; a runtime text that disagrees with the
/// deltas is counted. These tests drive <c>OnProgress</c> directly, the way a WinRT Progress handler
/// would, from thread-pool threads, and never assert on wall-clock time.
/// </summary>
public class DeltaAccumulatorTests
{
    private sealed class Probe
    {
        public readonly List<string> Delivered = [];
        public int Late;
        public int Mismatches;
        public Action<string> Sink;

        public Probe()
        {
            Sink = delta =>
            {
                lock (Delivered)
                {
                    Delivered.Add(delta);
                }
            };
        }

        public DeltaAccumulator Build(CancellationToken token = default) => new(
            NullLogger.Instance,
            "Test",
            delta => Sink(delta),
            onLateDelta: () => Interlocked.Increment(ref Late),
            onTextMismatch: () => Interlocked.Increment(ref Mismatches),
            token);
    }

    [Fact]
    public async Task Text_is_the_deltas_in_the_order_they_were_delivered_under_concurrent_callbacks()
    {
        var probe = new Probe();
        var accumulator = probe.Build();

        var tasks = Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                accumulator.OnProgress($"[{thread}:{i}]");
            }
        }));
        await Task.WhenAll(tasks);
        accumulator.Drain();

        // Append and delivery happen under one lock, so the concatenation of what the sink saw, in the
        // order it saw it, is exactly the accumulated text. Neither order is fixed; their equality is.
        Assert.Equal(400, probe.Delivered.Count);
        Assert.Equal(string.Concat(probe.Delivered), accumulator.Text);
        Assert.Equal(0, probe.Late);
    }

    [Fact]
    public void A_throwing_sink_is_captured_and_later_deltas_still_accumulate()
    {
        var probe = new Probe();
        var boom = new InvalidOperationException("sink failed");
        var calls = 0;
        probe.Sink = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw boom;
            }
        };
        var accumulator = probe.Build();

        accumulator.OnProgress("first");
        accumulator.OnProgress("second");
        accumulator.Drain();

        Assert.Same(boom, accumulator.DeltaFailure);
        Assert.Equal("firstsecond", accumulator.Text);
    }

    [Fact]
    public void A_callback_after_a_completed_generation_is_dropped_and_counted_late()
    {
        var probe = new Probe();
        var accumulator = probe.Build();

        accumulator.OnProgress("in time");
        accumulator.Drain();
        accumulator.OnProgress("too late");

        Assert.Equal("in time", accumulator.Text);
        Assert.Equal(["in time"], probe.Delivered);
        Assert.Equal(1, probe.Late);
    }

    [Fact]
    public void A_callback_after_a_cancelled_generation_is_dropped_but_not_counted()
    {
        using var cts = new CancellationTokenSource();
        var probe = new Probe();
        var accumulator = probe.Build(cts.Token);

        accumulator.OnProgress("in time");
        cts.Cancel();
        accumulator.Drain();
        accumulator.OnProgress("on the way out");

        Assert.Equal("in time", accumulator.Text);
        Assert.Equal(["in time"], probe.Delivered);
        Assert.Equal(0, probe.Late);
    }

    [Fact]
    public void Cancelling_the_token_after_a_completed_generation_does_not_exempt_a_late_callback()
    {
        // The streaming endpoint cancels the generation token in its finally on every path, completed
        // ones included (D51). Whether a straggler is a contract breach is decided by how the generation
        // ended, at the barrier, not by what the token says when the straggler arrives; otherwise the
        // streaming shape could never count a late delta at all.
        using var cts = new CancellationTokenSource();
        var probe = new Probe();
        var accumulator = probe.Build(cts.Token);

        accumulator.OnProgress("in time");
        accumulator.Drain();
        cts.Cancel();
        accumulator.OnProgress("too late");

        Assert.Equal("in time", accumulator.Text);
        Assert.Equal(1, probe.Late);
    }

    [Fact]
    public async Task Drain_waits_for_a_callback_that_is_mid_flight()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var probe = new Probe();
        var inner = probe.Sink;
        probe.Sink = delta =>
        {
            entered.Set();
            release.Wait();
            inner(delta);
        };
        var accumulator = probe.Build();

        var callback = Task.Run(() => accumulator.OnProgress("slow"));
        entered.Wait();
        var drain = Task.Run(accumulator.Drain);

        // The barrier is open while the callback is inside the sink. Releasing the sink is what lets
        // the drain finish, and the text read after the drain includes the delta the drain waited for.
        Assert.False(drain.IsCompleted);
        release.Set();
        await Task.WhenAll(callback, drain);

        Assert.Equal("slow", accumulator.Text);
        Assert.Equal(["slow"], probe.Delivered);
        Assert.Equal(0, probe.Late);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("ab", 0)]
    [InlineData("abc", 1)]
    public void Reconcile_returns_the_deltas_and_counts_only_a_disagreeing_runtime_text(string? runtimeText, int expectedMismatches)
    {
        var probe = new Probe();
        var accumulator = probe.Build();

        accumulator.OnProgress("a");
        accumulator.OnProgress("b");
        accumulator.Drain();

        Assert.Equal("ab", accumulator.Reconcile(runtimeText));
        Assert.Equal(expectedMismatches, probe.Mismatches);
    }
}
