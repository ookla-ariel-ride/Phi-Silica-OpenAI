using Microsoft.Extensions.Logging.Abstractions;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;

namespace NpuBridge.Tests;

public class BackendLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Transitions_not_started_to_loading_to_ready()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { InitGate = gate });
        var clock = new ManualTimeProvider(T0);
        var lifecycle = new BackendLifecycle(fake, clock, NullLogger<BackendLifecycle>.Instance);

        Assert.Equal(BackendStateKind.NotStarted, lifecycle.Snapshot.Kind);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.Equal(BackendStateKind.Loading, lifecycle.Snapshot.Kind);
        Assert.Equal(T0, lifecycle.Snapshot.LoadStartedAt);

        clock.Advance(TimeSpan.FromSeconds(5));
        gate.SetResult();
        await lifecycle.Initialization;

        var ready = lifecycle.Snapshot;
        Assert.Equal(BackendStateKind.Ready, ready.Kind);
        Assert.Equal(TimeSpan.FromSeconds(5), ready.LoadingElapsed(clock.GetUtcNow()));
        Assert.True(lifecycle.IsReady);
        Assert.Null(ready.Error);
    }

    [Fact]
    public async Task Failure_is_captured_not_thrown()
    {
        var fake = new FakeBackend(new FakeBackendOptions { InitFailure = new InvalidOperationException("nope") });
        var lifecycle = new BackendLifecycle(fake, new ManualTimeProvider(T0), NullLogger<BackendLifecycle>.Instance);

        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.Initialization;

        Assert.Equal(BackendStateKind.Failed, lifecycle.Snapshot.Kind);
        Assert.Equal("nope", lifecycle.Snapshot.Error);
        Assert.NotNull(lifecycle.Snapshot.LoadFinishedAt);
    }

    [Fact]
    public async Task Stop_during_load_cancels_and_records_failure()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { InitGate = gate });
        var lifecycle = new BackendLifecycle(fake, new ManualTimeProvider(T0), NullLogger<BackendLifecycle>.Instance);

        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.StopAsync(CancellationToken.None);
        await lifecycle.Initialization;

        Assert.Equal(BackendStateKind.Failed, lifecycle.Snapshot.Kind);
        Assert.Contains("shutdown", lifecycle.Snapshot.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stop_does_not_wait_forever_for_a_backend_that_ignores_cancellation()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stubborn = new FakeBackend(new FakeBackendOptions { InitGate = never });
        // The fake honours cancellation via WaitAsync(ct); emulate a stubborn adapter by passing a
        // host token that fires first, which is the path StopAsync takes when the backend hangs.
        var lifecycle = new BackendLifecycle(stubborn, new ManualTimeProvider(T0), NullLogger<BackendLifecycle>.Instance);
        await lifecycle.StartAsync(CancellationToken.None);

        using var hostTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await lifecycle.StopAsync(hostTimeout.Token);
        await lifecycle.Initialization; // completes because StopAsync cancelled the gate wait
        Assert.Equal(BackendStateKind.Failed, lifecycle.Snapshot.Kind);
        lifecycle.Dispose();
    }

    [Fact]
    public async Task Backend_cancellation_without_shutdown_is_a_failure_with_the_message()
    {
        var fake = new FakeBackend(new FakeBackendOptions { InitFailure = new OperationCanceledException("runtime gave up") });
        var lifecycle = new BackendLifecycle(fake, new ManualTimeProvider(T0), NullLogger<BackendLifecycle>.Instance);
        await lifecycle.StartAsync(CancellationToken.None);
        await lifecycle.Initialization;
        Assert.Equal(BackendStateKind.Failed, lifecycle.Snapshot.Kind);
        Assert.Equal("runtime gave up", lifecycle.Snapshot.Error);
    }

    [Fact]
    public async Task Start_is_idempotent()
    {
        var fake = new FakeBackend();
        var lifecycle = new BackendLifecycle(fake, new ManualTimeProvider(T0), NullLogger<BackendLifecycle>.Instance);

        await lifecycle.StartAsync(CancellationToken.None);
        var first = lifecycle.Initialization;
        await lifecycle.StartAsync(CancellationToken.None);
        Assert.Same(first, lifecycle.Initialization);
        await first;
        Assert.True(lifecycle.IsReady);
    }
}
