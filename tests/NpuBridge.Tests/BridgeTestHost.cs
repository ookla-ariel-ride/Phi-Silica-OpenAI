using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NpuBridge.Api;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;
using NpuBridge.Hosting;

namespace NpuBridge.Tests;

/// <summary>Boots the real endpoint pipeline on an in-memory TestServer with a fake backend.</summary>
internal sealed class BridgeTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private BridgeTestHost(WebApplication app, HttpClient client, ILanguageModelBackend backend, RequestLedger requests)
    {
        _app = app;
        Client = client;
        Backend = backend;
        Requests = requests;
    }

    public HttpClient Client { get; }

    public ILanguageModelBackend Backend { get; }

    public FakeBackend Fake => (FakeBackend)Backend;

    /// <summary>
    /// Every request that ran the whole pipeline, and every exception that escaped it. A handler that
    /// dies mid-stream is invisible to a client that has already gone and to the log's Error level
    /// (TestServer has no Kestrel to log it), so this is how a test says "the request ended quietly".
    /// </summary>
    public RequestLedger Requests { get; }

    public BackendLifecycle Lifecycle => _app.Services.GetRequiredService<BackendLifecycle>();

    public ContextCache Cache => _app.Services.GetRequiredService<ContextCache>();

    /// <summary>
    /// The leak invariant since chunk 5: every context the fake created is either in the cache or
    /// disposed, and nothing is both. Before the cache, "no leak" meant "all disposed"; a successful
    /// generation now parks its context in the cache instead, so the count of live contexts must
    /// equal the count the cache holds. Shutdown disposes the rest (see the lifecycle tests).
    /// </summary>
    public void AssertNoLeak()
    {
        Assert.Equal(Fake.ContextsCreated, Fake.ContextsDisposed + Cache.Count);
        Assert.Equal(Cache.Count, Fake.ActiveContexts);
    }

    /// <param name="remoteAddress">Remote IP presented to endpoints; TestServer has none, so loopback is simulated by default.</param>
    public static async Task<BridgeTestHost> StartAsync(
        ILanguageModelBackend? backend = null,
        BridgeOptions? options = null,
        IProcessIdentity? identity = null,
        TimeProvider? time = null,
        bool waitForReady = true,
        System.Net.IPAddress? remoteAddress = null,
        ILoggerProvider? loggerProvider = null,
        TimeSpan? keepAliveInterval = null,
        TimeSpan? firstKeepAliveDelay = null)
    {
        remoteAddress ??= System.Net.IPAddress.Loopback;
        backend ??= new FakeBackend();
        options ??= new BridgeOptions { Backend = BackendKind.Fake };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (loggerProvider is not null)
        {
            // Everything, Debug included: the host's default floor is Information, and some of the
            // lines tests need to see -- the guard around a cancel that threw, for one -- are Debug.
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(loggerProvider);
        }

        if (identity is not null)
        {
            builder.Services.AddSingleton(identity);
        }

        if (time is not null)
        {
            builder.Services.AddSingleton(time);
        }

        if (keepAliveInterval is { } interval)
        {
            // Registered before AddNpuBridgeCore, whose TryAddSingleton then leaves it alone: the SSE
            // keep-alive is a second then every fifteen in production, and milliseconds here. The first
            // delay defaults to the interval so a test that cares about neither says one number.
            builder.Services.AddSingleton(new StreamingOptions
            {
                KeepAliveInterval = interval,
                FirstKeepAliveDelay = firstKeepAliveDelay ?? interval,
            });
        }

        builder.Services.AddNpuBridgeCore(options, _ => backend);

        var app = builder.Build();
        var requests = new RequestLedger();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                requests.Record(ex);
                throw;
            }
            finally
            {
                requests.Record(null);
            }
        });
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = remoteAddress;
            return next(context);
        });
        app.MapNpuBridge();
        await app.StartAsync();

        var host = new BridgeTestHost(app, app.GetTestClient(), backend, requests);
        if (waitForReady)
        {
            await host.Lifecycle.Initialization;
        }

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>What the pipeline middleware in <see cref="BridgeTestHost"/> saw: see <see cref="BridgeTestHost.Requests"/>.</summary>
internal sealed class RequestLedger
{
    private readonly List<Exception> _escaped = new();
    private int _completed;

    /// <summary>Requests whose pipeline has returned, however they ended.</summary>
    public int Completed => Volatile.Read(ref _completed);

    /// <summary>Exceptions that left the pipeline, in order. Empty is the claim most tests make.</summary>
    public IReadOnlyList<Exception> Escaped
    {
        get
        {
            lock (_escaped)
            {
                return _escaped.ToArray();
            }
        }
    }

    internal void Record(Exception? escaped)
    {
        if (escaped is null)
        {
            Interlocked.Increment(ref _completed);
            return;
        }

        lock (_escaped)
        {
            _escaped.Add(escaped);
        }
    }
}

/// <summary>Collects every log record the host emits, so tests can assert on the per-request line.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<LogRecord> _records = new();

    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_records)
            {
                return _records.ToArray();
            }
        }
    }

    /// <summary>Called synchronously on the logging thread for every record, so a test can act inside the window a log line marks.</summary>
    public Action<LogRecord>? OnRecord { get; set; }

    public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogRecord record)
    {
        lock (_records)
        {
            _records.Add(record);
        }

        OnRecord?.Invoke(record);
    }

    internal sealed record LogRecord(string Category, LogLevel Level, string Message);

    private sealed class Capturing : ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        private readonly string _category;

        public Capturing(CapturingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _owner.Add(new LogRecord(_category, logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>Manually advanced clock for tests that care about elapsed time.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
