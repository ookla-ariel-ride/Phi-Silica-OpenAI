using NpuBridge.Configuration;
using NpuBridge.Hosting;

namespace NpuBridge;

/// <summary>Implements <c>npu-bridge service install|uninstall|start|stop</c> by driving <c>sc.exe</c>.</summary>
internal static class ServiceCommands
{
    public static int Run(CommandLineParse parsed)
    {
        var verb = parsed.VerbArgs[0];
        if (!ServiceCommandBuilder.Verbs.Contains(verb))
        {
            Console.Error.WriteLine($"error: unknown service verb '{verb}'. Expected install, uninstall, start or stop.");
            return 2;
        }

        if (parsed.VerbArgs.Count > 1)
        {
            Console.Error.WriteLine($"error: unexpected argument '{parsed.VerbArgs[1]}' after 'service {verb}'.");
            return 2;
        }

        // Resolve settings exactly as the server does (appsettings.json < local < NPU_BRIDGE_* < CLI) so
        // the service name matches, and so a typo fails here instead of as an opaque SCM start error.
        BridgeOptions options;
        try
        {
            options = BridgeOptionsBinder.Bind(BridgeConfiguration.Build(AppContext.BaseDirectory, parsed.ConfigArgs));
        }
        catch (BridgeConfigurationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        var exePath = ExternalCommands.ResolveExePath(out var pathError);
        if (exePath is null)
        {
            Console.Error.WriteLine($"error: {pathError}");
            return 2;
        }

        IReadOnlyList<ServiceCommand> commands;
        try
        {
            commands = ServiceCommandBuilder.Build(verb, options.ServiceName, exePath, parsed.ConfigArgs);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        if (string.Equals(verb, "install", StringComparison.OrdinalIgnoreCase) && options.Backend == BackendKind.PhiSilica)
        {
            Console.Error.WriteLine("error: a Windows service starts without package identity, which Phi Silica requires (DECISIONS D24).");
            Console.Error.WriteLine("       Use 'task install' for --backend phi-silica; 'service install' works for --backend aion or fake.");
            return 2;
        }

        if (!ExternalCommands.IsElevated())
        {
            Console.Error.WriteLine("error: 'service' commands change the Service Control Manager and need an elevated (Administrator) prompt.");
            Console.Error.WriteLine($"       Re-run from an elevated terminal: {Path.GetFileName(exePath)} service {verb}");
            return 5;
        }

        var exit = ExternalCommands.RunAll(commands);
        if (exit != 0)
        {
            return exit;
        }

        if (string.Equals(verb, "install", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine($"Service '{options.ServiceName}' installed (start type: automatic). Start it with:");
            Console.WriteLine($"  {Path.GetFileName(exePath)} service start");
            Console.WriteLine("Logs: Windows Event Log > Application, source 'npu-bridge'. Settings: the options shown above plus");
            Console.WriteLine($"  {Path.Combine(AppContext.BaseDirectory, "appsettings.json")} and appsettings.local.json.");
        }

        return 0;
    }
}
