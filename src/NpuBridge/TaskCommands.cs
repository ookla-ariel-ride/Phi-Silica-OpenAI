using NpuBridge.Configuration;
using NpuBridge.Hosting;

namespace NpuBridge;

/// <summary>
/// Implements <c>npu-bridge task install|uninstall|status</c>: a logon-triggered scheduled task in the
/// user's interactive session. The task starts the exe by path; the exe relaunches itself through package
/// activation (how Phi Silica gets identity, D24) and supervises that instance (D37), so ending the task
/// ends the server.
/// </summary>
internal static class TaskCommands
{
    public static int Run(CommandLineParse parsed)
    {
        var verb = parsed.VerbArgs[0];
        if (!ScheduledTaskCommandBuilder.Verbs.Contains(verb))
        {
            Console.Error.WriteLine($"error: unknown task verb '{verb}'. Expected install, uninstall or status.");
            return 2;
        }

        if (parsed.VerbArgs.Count > 1)
        {
            Console.Error.WriteLine($"error: unexpected argument '{parsed.VerbArgs[1]}' after 'task {verb}'.");
            return 2;
        }

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

        var isInstall = string.Equals(verb, "install", StringComparison.OrdinalIgnoreCase);
        var isStatus = string.Equals(verb, "status", StringComparison.OrdinalIgnoreCase);
        var userName = ExternalCommands.CurrentUserName();

        IReadOnlyList<ServiceCommand> commands;
        try
        {
            commands = ScheduledTaskCommandBuilder.Build(verb, options.TaskName, exePath, parsed.ConfigArgs, userName);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        if (isInstall && options.Backend == BackendKind.PhiSilica)
        {
            var family = PackageActivation.FindRegisteredFamilyName(Path.GetDirectoryName(exePath)!, out var detail);
            if (family is null)
            {
                Console.Error.WriteLine($"error: the logon task would start Phi Silica without identity: {detail}");
                return 2;
            }

            Console.WriteLine($"Package identity: {detail}");
        }

        if (!isStatus && !ExternalCommands.IsElevated())
        {
            Console.Error.WriteLine("error: creating or deleting a logon task needs an elevated (Administrator) prompt of the SAME user account.");
            Console.Error.WriteLine($"       Re-run from an elevated terminal: {Path.GetFileName(exePath)} task {verb}");
            return 5;
        }

        if (isStatus)
        {
            var exitCode = ExternalCommands.Execute(commands[0]);
            if (exitCode != 0)
            {
                Console.WriteLine($"Logon task '{options.TaskName}' is not installed. Install it with: {Path.GetFileName(exePath)} task install");
            }

            return 0;
        }

        var exit = ExternalCommands.RunAll(commands);
        if (exit != 0)
        {
            return exit;
        }

        if (isInstall)
        {
            var firstUrl = options.Listen.Split(';')[0].TrimEnd('/');
            Console.WriteLine();
            Console.WriteLine($"Logon task '{options.TaskName}' installed for {userName}. It starts at the next logon; start it now with:");
            Console.WriteLine($"  schtasks /Run /TN \"{options.TaskName}\"");
            Console.WriteLine($"Stop it with: schtasks /End /TN \"{options.TaskName}\" (the activated instance exits with its supervisor).");
            Console.WriteLine($"The console window is hidden (--hide-console); check {firstUrl}/healthz.");
        }

        return 0;
    }
}
