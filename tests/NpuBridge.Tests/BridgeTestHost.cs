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

    private BridgeTestHost(WebApplication app, HttpClient client, ILanguageModelBackend backend)
    {
        _app = app;
        Client = client;
        Backend = backend;
    }

    public HttpClient Client { get; }

    public ILanguageModelBackend Backend { get; }

    public FakeBackend Fake => (FakeBackend)Backend;

    public BackendLifecycle Lifecycle => _app.Services.GetRequiredService<BackendLifecycle>();

    /// <param name="remoteAddress">Remote IP presented to endpoints; TestServer has none, so loopback is simulated by default.</param>
    public static async Task<BridgeTestHost> StartAsync(
        ILanguageModelBackend? backend = null,
        BridgeOptions? options = null,
        IProcessIdentity? identity = null,
        TimeProvider? time = null,
        bool waitForReady = true,
        System.Net.IPAddress? remoteAddress = null,
        ILoggerProvider? loggerProvider = null)
    {
        remoteAddress ??= System.Net.IPAddress.Loopback;
        backend ??= new FakeBackend();
        options ??= new BridgeOptions { Backend = BackendKind.Fake };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (loggerProvider is not null)
        {
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

        builder.Services.AddNpuBridgeCore(options, _ => backend);

        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = remoteAddress;
            return next(context);
        });
        app.MapNpuBridge();
        await app.StartAsync();

        var host = new BridgeTestHost(app, app.GetTestClient(), backend);
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
