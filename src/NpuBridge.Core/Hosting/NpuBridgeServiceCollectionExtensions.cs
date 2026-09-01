using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NpuBridge.Api;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Hosting;

public static class NpuBridgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything the endpoints need. Register a custom <see cref="IProcessIdentity"/> or
    /// <see cref="TimeProvider"/> <em>before</em> calling this to override the defaults.
    /// </summary>
    public static IServiceCollection AddNpuBridgeCore(
        this IServiceCollection services,
        BridgeOptions options,
        Func<IServiceProvider, ILanguageModelBackend> backendFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backendFactory);

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProcessIdentity>(NoProcessIdentity.Instance);

        // The container owns the backend's lifetime (IAsyncDisposable is honoured on shutdown).
        services.AddSingleton(backendFactory);
        services.AddSingleton<BackendLifecycle>();
        services.AddHostedService(sp => sp.GetRequiredService<BackendLifecycle>());

        services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonDefaults.Options.PropertyNamingPolicy;
            o.SerializerOptions.DictionaryKeyPolicy = null;
            o.SerializerOptions.DefaultIgnoreCondition = JsonDefaults.Options.DefaultIgnoreCondition;
            o.SerializerOptions.NumberHandling = JsonDefaults.Options.NumberHandling;
        });

        return services;
    }
}
