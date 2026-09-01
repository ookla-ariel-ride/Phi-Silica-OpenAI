using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using NpuBridge.Api;
using NpuBridge.Backends;
using NpuBridge.Configuration;
using NpuBridge.Hosting;

namespace NpuBridge;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var parsed = CommandLine.Parse(args);
        if (parsed.IsError)
        {
            Console.Error.WriteLine($"error: {parsed.Error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLine.Usage);
            return 2;
        }

        switch (parsed.Verb)
        {
            case CommandVerb.Help:
                Console.WriteLine(CommandLine.Usage);
                return 0;
            case CommandVerb.Version:
                Console.WriteLine($"npu-bridge {BridgeEndpoints.Version}");
                return 0;
            case CommandVerb.Service:
                return ServiceCommands.Run(parsed);
            case CommandVerb.Run:
            default:
                return await RunServerAsync(parsed).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunServerAsync(CommandLineParse parsed)
    {
        var isService = WindowsServiceHelpers.IsWindowsService();

        // Content root = the exe's folder so appsettings.json is found when the SCM starts us from System32.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        // CreateBuilder registers appsettings*.json and an *unprefixed* environment provider (which would
        // let a stray VERBOSE=1 in the shell override the config file). Drop those and add the single
        // composition shared with the service verbs (BridgeConfiguration): json < local json < NPU_BRIDGE_* < CLI.
        foreach (var source in builder.Configuration.Sources
                     .Where(s => s is EnvironmentVariablesConfigurationSource or JsonConfigurationSource)
                     .ToList())
        {
            builder.Configuration.Sources.Remove(source);
        }

        builder.Configuration.AddNpuBridgeSources(AppContext.BaseDirectory, parsed.ConfigArgs);

        BridgeOptions options;
        try
        {
            options = BridgeOptionsBinder.Bind(builder.Configuration);
        }
        catch (BridgeConfigurationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        builder.Logging.ClearProviders();
        if (isService)
        {
            // The SCM has no console; the Application event log is the only place output survives.
            // The provider's default floor is Warning, so lift it for our own categories.
            builder.Logging.AddEventLog(o => o.SourceName = "npu-bridge");
            builder.Logging.AddFilter<EventLogLoggerProvider>("NpuBridge", options.Verbose ? LogLevel.Debug : LogLevel.Information);
            builder.Logging.AddFilter<EventLogLoggerProvider>("Microsoft.Hosting.Lifetime", LogLevel.Information);
        }
        else
        {
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        }

        if (options.Verbose)
        {
            builder.Logging.AddFilter("NpuBridge", LogLevel.Debug);
        }

        builder.Host.UseWindowsService(o => o.ServiceName = options.ServiceName);
        builder.WebHost.UseUrls(options.Listen);

        var identity = ProcessIdentity.Detect();
        builder.Services.AddSingleton<IProcessIdentity>(identity);
        builder.Services.AddNpuBridgeCore(options, sp => BackendFactory.Create(options, identity, sp));

        var app = builder.Build();
        app.MapNpuBridge();

        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NpuBridge");
        log.LogInformation("npu-bridge {Version} starting: backend={Backend} listen={Listen} identity={Identity} service={IsService}",
            BridgeEndpoints.Version,
            options.Backend.ToConfigName(),
            options.Listen,
            identity.HasPackageIdentity ? identity.PackageFamilyName : "none",
            isService);

        try
        {
            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Typical cause: the port is already in use.
            log.LogCritical(ex, "npu-bridge could not start.");
            return 1;
        }
    }
}

internal static class BackendFactory
{
    public static ILanguageModelBackend Create(BridgeOptions options, IProcessIdentity identity, IServiceProvider services)
    {
        return options.Backend switch
        {
            BackendKind.Fake => new Backends.Fake.FakeBackend(),
            BackendKind.PhiSilica => new UnavailableBackend("phi-silica", "Phi Silica",
                "The Phi Silica adapter is not built yet (planned for chunk 2). Use --backend fake for now."),
            BackendKind.Aion => new UnavailableBackend("aion-instruct", "Aion Instruct Preview",
                "The Aion Instruct adapter is not built yet (planned for chunk 6). Use --backend fake for now."),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Backend, "Unknown backend."),
        };
    }
}
