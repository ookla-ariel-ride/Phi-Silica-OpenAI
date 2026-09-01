using NpuBridge.Configuration;

namespace NpuBridge.Hosting;

/// <summary>
/// Turns <c>task install|uninstall|status</c> into <c>schtasks.exe</c> invocations. The task runs at the
/// user's logon, in their interactive session, and starts the exe by path; the exe then relaunches itself
/// through package activation so Phi Silica gets identity (DECISIONS D24) and supervises that instance
/// (D37), so ending the task ends the server. A Windows service cannot do that, which is why this exists
/// alongside <see cref="ServiceCommandBuilder"/>.
/// </summary>
public static class ScheduledTaskCommandBuilder
{
    /// <summary>schtasks rejects (or truncates) a /TR value longer than this.</summary>
    public const int MaxTaskRunLength = 261;

    public static readonly IReadOnlySet<string> Verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "install", "uninstall", "status",
    };

    /// <summary>Settings that only steer the <c>task</c> verbs or the relaunch and must not be baked into the task.</summary>
    private static readonly HashSet<string> TaskOnlyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(BridgeOptions.TaskName),
        nameof(BridgeOptions.ServiceName),
        nameof(BridgeOptions.SupervisorPid),
    };

    /// <param name="verb">One of <see cref="Verbs"/>.</param>
    /// <param name="taskName">Task name (single token).</param>
    /// <param name="exePath">Absolute path of the npu-bridge executable.</param>
    /// <param name="configArgs">Normalised <c>Key=value</c> settings to bake into the task's command line.</param>
    /// <param name="userName">Account the task runs as (<c>DOMAIN\user</c>); its interactive session hosts the process.</param>
    /// <param name="hideConsole">Add <c>--hide-console</c> unless the caller already set it either way.</param>
    public static IReadOnlyList<ServiceCommand> Build(
        string verb,
        string taskName,
        string exePath,
        IReadOnlyList<string> configArgs,
        string userName,
        bool hideConsole = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(configArgs);

        if (!BridgeOptionsBinder.IsValidServiceName(taskName))
        {
            throw new ArgumentException("Task name must be a single token without whitespace, slashes or quotes.", nameof(taskName));
        }

        if (userName.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("User name must not contain quotes.", nameof(userName));
        }

        return verb.ToLowerInvariant() switch
        {
            "install" =>
            [
                new ServiceCommand("schtasks.exe",
                    $"/Create /TN \"{taskName}\" /SC ONLOGON /RU \"{userName}\" /RL LIMITED /IT /F /TR {ServiceCommandBuilder.CrtQuote(BuildTaskRun(exePath, configArgs, hideConsole), force: true)}",
                    $"Create logon task '{taskName}' for {userName}"),
            ],
            "uninstall" =>
            [
                new ServiceCommand("schtasks.exe", $"/End /TN \"{taskName}\"", $"Stop task '{taskName}' if running", IgnoreFailure: true),
                new ServiceCommand("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", $"Delete task '{taskName}'"),
            ],
            "status" => [new ServiceCommand("schtasks.exe", $"/Query /TN \"{taskName}\" /V /FO LIST", $"Query task '{taskName}'", IgnoreFailure: true)],
            _ => throw new ArgumentException($"Unknown task verb '{verb}'. Expected install, uninstall or status.", nameof(verb)),
        };
    }

    /// <summary>The command line the task runs: exe by path, <c>run</c>, the forwarded settings, <c>--hide-console</c>.</summary>
    public static string BuildTaskRun(string exePath, IReadOnlyList<string> configArgs, bool hideConsole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(configArgs);

        var settings = new List<string>();
        var hasHideConsole = false;
        foreach (var setting in configArgs)
        {
            var eq = setting.IndexOf('=', StringComparison.Ordinal);
            var key = eq > 0 ? setting[..eq] : setting;
            var value = eq > 0 ? setting[(eq + 1)..] : string.Empty;

            if (TaskOnlyKeys.Contains(key))
            {
                continue;
            }

            if (key.Equals(nameof(BridgeOptions.SelfRelaunch), StringComparison.OrdinalIgnoreCase)
                && BridgeOptionsBinder.TryParseBool(value, out var relaunch) && !relaunch)
            {
                throw new ArgumentException(
                    "A logon task with --self-relaunch off would start Phi Silica without package identity. Leave self-relaunch on for the task.",
                    nameof(configArgs));
            }

            hasHideConsole |= key.Equals(nameof(BridgeOptions.HideConsole), StringComparison.OrdinalIgnoreCase);
            settings.Add(setting);
        }

        if (hideConsole && !hasHideConsole)
        {
            settings.Add($"{nameof(BridgeOptions.HideConsole)}=true");
        }

        var run = ServiceCommandBuilder.BuildBinPath(exePath, settings);
        if (run.Length > MaxTaskRunLength)
        {
            throw new ArgumentException(
                $"The task command line is {run.Length} characters; schtasks allows {MaxTaskRunLength}. " +
                "Move settings into appsettings.json next to the exe, or install from a shorter path.", nameof(configArgs));
        }

        return run;
    }
}
