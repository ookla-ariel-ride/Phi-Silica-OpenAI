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
    /// Registers everything the endpoints need. Register a custom <see cref="IProcessIdentity"/>,
    /// <see cref="TimeProvider"/> or <see cref="StreamingOptions"/> <em>before</em> calling this to
    /// override the defaults.
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

        // TryAdd, like TimeProvider: a caller (a test) that wants a keep-alive it can drive in
        // milliseconds registers its own before calling this.
        services.TryAddSingleton<StreamingOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProcessIdentity>(NoProcessIdentity.Instance);
        services.TryAddSingleton<IgnoredParameterLog>();

        // The context cache is owned by the lifecycle below, which disposes the cached contexts before
        // the model they belong to: a LanguageModelContext must not outlive its LanguageModel.
        services.AddSingleton(sp => new ContextCache(
            options.ContextCacheSize,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ContextCache>>()));

        // BackendLifecycle owns the backend and disposes it only after initialization has finished.
        // The backend is deliberately not registered as its own disposable singleton, which would let
        // the container tear it down while a stubborn InitializeAsync is still running.
        services.AddSingleton(sp => new BackendLifecycle(
            backendFactory(sp),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackendLifecycle>>(),
            sp.GetRequiredService<ContextCache>()));
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
