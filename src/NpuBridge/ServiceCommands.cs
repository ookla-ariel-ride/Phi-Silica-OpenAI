using System.Diagnostics;
using System.Security.Principal;
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

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the path of the running executable.");
        if (Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("error: 'service install' must be run from the built NpuBridge.exe, not via 'dotnet NpuBridge.dll' or 'dotnet run'.");
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

        if (!IsElevated())
        {
            Console.Error.WriteLine("error: 'service' commands change the Service Control Manager and need an elevated (Administrator) prompt.");
            Console.Error.WriteLine($"       Re-run from an elevated terminal: {Path.GetFileName(exePath)} service {verb}");
            return 5;
        }

        foreach (var command in commands)
        {
            Console.WriteLine($"> {command.Description}");
            Console.WriteLine($"  {command.FileName} {command.Arguments}");
            var exit = Execute(command);
            if (exit != 0 && !command.IgnoreFailure)
            {
                Console.Error.WriteLine($"error: '{command.FileName} {command.Arguments}' exited with code {exit}.");
                return exit;
            }
        }

        if (string.Equals(verb, "install", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine($"Service '{options.ServiceName}' installed (start type: automatic). Start it with:");
            Console.WriteLine($"  {Path.GetFileName(exePath)} service start");
            Console.WriteLine("Logs: Windows Event Log > Application, source 'npu-bridge'. Settings: the options shown above plus");
            Console.WriteLine($"  {Path.Combine(AppContext.BaseDirectory, "appsettings.json")} and appsettings.local.json (for LafToken/LafAttestation).");
            if (options.Backend == BackendKind.PhiSilica)
            {
                Console.WriteLine();
                Console.WriteLine("Note: a service process starts without package identity, which Phi Silica requires.");
                Console.WriteLine("      The service verbs suit --backend aion or fake; the Phi Silica auto-start path is the logon task (chunk 2).");
            }
        }

        return 0;
    }

    private static int Execute(ServiceCommand command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {command.FileName}.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();

        foreach (var line in (stdout + stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Console.WriteLine($"  {line}");
        }

        return process.ExitCode;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
