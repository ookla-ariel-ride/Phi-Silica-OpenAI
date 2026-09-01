using System.Diagnostics;
using System.Security.Principal;
using NpuBridge.Hosting;

namespace NpuBridge;

/// <summary>Runs sc.exe / schtasks.exe steps and echoes their output; shared by the service and task verbs.</summary>
internal static class ExternalCommands
{
    public static int RunAll(IReadOnlyList<ServiceCommand> commands)
    {
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

        return 0;
    }

    public static int Execute(ServiceCommand command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // schtasks may prompt for a password in some configurations; with stdin closed it fails fast instead of hanging.
            RedirectStandardInput = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {command.FileName}.");
        process.StandardInput.Close();
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

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string CurrentUserName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }

    /// <summary>The built exe path, refusing the `dotnet` host so nothing registers `dotnet.exe` as a service.</summary>
    public static string? ResolveExePath(out string? error)
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            error = "Cannot determine the path of the running executable.";
            return null;
        }

        if (Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "This command must be run from the built NpuBridge.exe, not via 'dotnet NpuBridge.dll' or 'dotnet run'.";
            return null;
        }

        error = null;
        return exePath;
    }
}
