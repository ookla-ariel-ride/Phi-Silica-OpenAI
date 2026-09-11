using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NpuBridge.Backends;

public enum BackendStateKind
{
    NotStarted,
    Loading,
    Ready,
    Failed,
}

/// <summary>Immutable view of the backend's lifecycle at one instant.</summary>
public sealed record BackendSnapshot(
    BackendStateKind Kind,
    DateTimeOffset? LoadStartedAt,
    DateTimeOffset? LoadFinishedAt,
    string? Error)
{
    /// <summary>Time spent loading so far (or in total once finished).</summary>
    public TimeSpan? LoadingElapsed(DateTimeOffset now) => LoadStartedAt is null
        ? null
        : (LoadFinishedAt ?? now) - LoadStartedAt.Value;
}

/// <summary>
/// Owns the backend. Starts <see cref="ILanguageModelBackend.InitializeAsync"/> in the background at host
/// start and tracks its outcome, so requests arriving during a multi-minute first-run compile get a 503
/// with a reason instead of blocking or crashing. Disposes the backend only after initialization has
/// finished (or a bounded grace period has elapsed) so a runtime that ignores cancellation is never
/// torn down underneath its own <c>CreateAsync</c>.
/// </summary>
public sealed class BackendLifecycle : IHostedService, IAsyncDisposable
{
    /// <summary>Loading longer than this is almost certainly the one-time NPU model compile.</summary>
    public static readonly TimeSpan FirstRunCompileThreshold = TimeSpan.FromSeconds(60);

    /// <summary>How long disposal waits for a still-running initialization before giving up on a clean teardown.</summary>
    public static readonly TimeSpan DisposeGracePeriod = TimeSpan.FromSeconds(15);

    private readonly ILanguageModelBackend _backend;
    private readonly TimeProvider _time;
    private readonly ILogger<BackendLifecycle> _logger;
    private readonly ContextCache? _cache;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private BackendSnapshot _snapshot = new(BackendStateKind.NotStarted, null, null, null);
    private Task _initialization = Task.CompletedTask;
    private bool _disposed;

    /// <param name="cache">The context cache, when there is one: its contexts are disposed at stop and again at disposal, always before the backend.</param>
    public BackendLifecycle(ILanguageModelBackend backend, TimeProvider time, ILogger<BackendLifecycle> logger, ContextCache? cache = null)
    {
        _backend = backend;
        _time = time;
        _logger = logger;
        _cache = cache;
    }

    public ILanguageModelBackend Backend => _backend;

    public BackendSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public bool IsReady => Snapshot.Kind == BackendStateKind.Ready;

    /// <summary>Completes when initialization has finished, successfully or not. Never faults.</summary>
    public Task Initialization => _initialization;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_snapshot.Kind != BackendStateKind.NotStarted)
            {
                return Task.CompletedTask;
            }

            _snapshot = new BackendSnapshot(BackendStateKind.Loading, _time.GetUtcNow(), null, null);
            _initialization = Task.Run(RunInitializationAsync, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        // Cached contexts first: they are live handles into the model. A request still finishing after
        // this stores nothing -- the cache disposes anything handed to it once it has shut down.
        _cache?.Dispose();

        // Give an in-flight initialization a moment to observe cancellation; never block shutdown on it.
        // A stubborn runtime is handled by DisposeAsync's grace period instead.
        var finished = await Task.WhenAny(_initialization, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
            .ConfigureAwait(false);
        if (finished != _initialization)
        {
            _logger.LogWarning("Backend {Backend} was still initializing at shutdown; disposal will wait up to {Grace}s for it.",
                _backend.DisplayName, DisposeGracePeriod.TotalSeconds);
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

        var finished = await Task.WhenAny(_initialization, Task.Delay(DisposeGracePeriod)).ConfigureAwait(false);
        if (finished != _initialization)
        {
            _logger.LogError("Backend {Backend} did not finish initializing within {Grace}s; disposing it anyway.",
                _backend.DisplayName, DisposeGracePeriod.TotalSeconds);
        }

        _cache?.Dispose();
        await _backend.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task RunInitializationAsync()
    {
        _logger.LogInformation("Loading {Backend} (model id {ModelId}). First run on this machine may take several minutes.",
            _backend.DisplayName, _backend.ModelId);
        try
        {
            await _backend.InitializeAsync(_shutdown.Token).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            BackendSnapshot ready;
            lock (_gate)
            {
                ready = _snapshot = _snapshot with { Kind = BackendStateKind.Ready, LoadFinishedAt = now };
            }

            _logger.LogInformation("{Backend} ready after {Seconds:F1}s.", _backend.DisplayName,
                ready.LoadingElapsed(now)?.TotalSeconds ?? 0);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            SetFailed("Initialization cancelled by shutdown.");
        }
        catch (Exception ex)
        {
            SetFailed(ex.Message);
            _logger.LogError(ex, "{Backend} failed to initialize.", _backend.DisplayName);
        }
    }

    private void SetFailed(string error)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { Kind = BackendStateKind.Failed, LoadFinishedAt = _time.GetUtcNow(), Error = error };
        }
    }
}
