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

    public static async Task<BridgeTestHost> StartAsync(
        ILanguageModelBackend? backend = null,
        BridgeOptions? options = null,
        IProcessIdentity? identity = null,
        TimeProvider? time = null,
        bool waitForReady = true)
    {
        backend ??= new FakeBackend();
        options ??= new BridgeOptions { Backend = BackendKind.Fake };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

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

/// <summary>Manually advanced clock for tests that care about elapsed time.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
