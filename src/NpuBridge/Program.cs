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
using NpuBridge.PhiSilica;

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
            case CommandVerb.Task:
                return TaskCommands.Run(parsed);
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

        var identity = ProcessIdentity.Detect();

        if (options.HideConsole && !isService)
        {
            ConsoleWindow.Hide();
        }

        // Phi Silica needs package identity, which only package activation grants. If we were started by
        // path (terminal, scheduled task) and the sparse package is registered for this folder, hand over
        // to an activated instance and supervise it (D33, D37).
        if (RelaunchArguments.ShouldRelaunch(options, identity.HasPackageIdentity, isService))
        {
            return await RelaunchAsync(parsed).ConfigureAwait(false);
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

        builder.Services.AddSingleton<IProcessIdentity>(identity);
        builder.Services.AddNpuBridgeCore(options, sp => BackendFactory.Create(options, identity, sp));

        var app = builder.Build();
        app.MapNpuBridge();

        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NpuBridge");
        if (options.SupervisorPid is { } supervisorPid)
        {
            Supervisor.WatchParent(supervisorPid, app.Lifetime, message => log.LogInformation("{Message}", message));
        }

        log.LogInformation("npu-bridge {Version} starting: backend={Backend} listen={Listen} identity={Identity} service={IsService} pid={Pid}",
            BridgeEndpoints.Version,
            options.Backend.ToConfigName(),
            options.Listen,
            identity.HasPackageIdentity ? identity.PackageFamilyName : "none",
            isService,
            Environment.ProcessId);

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

    private static async Task<int> RelaunchAsync(CommandLineParse parsed)
    {
        var exeDir = AppContext.BaseDirectory;
        var family = PackageActivation.FindRegisteredFamilyName(exeDir, out var detail);
        if (family is null)
        {
            Console.Error.WriteLine("error: --backend phi-silica needs package identity and this process has none.");
            Console.Error.WriteLine($"       {detail}");
            Console.Error.WriteLine("       (Use --self-relaunch off to start anyway and see the failure in /healthz.)");
            return 3;
        }

        // Activation does not inherit this process's environment, so every effective NPU_BRIDGE_* setting
        // is re-expressed on the child's command line. The child has identity and SelfRelaunch=off, so it
        // can never come back here.
        var plan = RelaunchArguments.Build(parsed.ConfigArgs, Environment.GetEnvironmentVariables(), Environment.ProcessId);
        foreach (var dropped in plan.DroppedSecrets)
        {
            Console.Error.WriteLine($"warning: {dropped} is set in this shell but is NOT forwarded to the activated instance " +
                                    "(it would land on its command line). Put it in appsettings.local.json next to the exe instead.");
        }

        uint pid;
        try
        {
            pid = PackageActivation.Activate(family, plan.Arguments);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 3;
        }

        Console.WriteLine($"npu-bridge: activated instance with package identity ({family}) as pid {pid}: {plan.Arguments}");
        return await Supervisor.WaitForChildAsync(pid).ConfigureAwait(false);
    }
}

internal static class BackendFactory
{
    public static ILanguageModelBackend Create(BridgeOptions options, IProcessIdentity identity, IServiceProvider services)
    {
        return options.Backend switch
        {
            BackendKind.Fake => new Backends.Fake.FakeBackend(),
            BackendKind.PhiSilica => new PhiSilicaBackend(options, identity,
                services.GetRequiredService<ILogger<PhiSilicaBackend>>()),
            BackendKind.Aion => new UnavailableBackend("aion-instruct", "Aion Instruct Preview",
                "The Aion Instruct adapter is not built yet (planned for chunk 6). Use --backend fake for now."),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Backend, "Unknown backend."),
        };
    }
}
