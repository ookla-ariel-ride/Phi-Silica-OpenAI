using System.Collections;
using NpuBridge.Configuration;

namespace NpuBridge.Hosting;

/// <summary>
/// Builds the command line handed to the package-activated child. Activation does not inherit the
/// parent's environment, so every effective <c>NPU_BRIDGE_*</c> setting is re-expressed as a CLI option.
/// Secrets set only in the parent's process environment are dropped (they would otherwise appear on the
/// child's command line); secrets already given on the parent's command line are forwarded unchanged.
/// </summary>
public static class RelaunchArguments
{
    /// <summary>Keys that describe the relaunch itself and must not be forwarded verbatim.</summary>
    private static readonly HashSet<string> RelaunchKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(BridgeOptions.SelfRelaunch),
        nameof(BridgeOptions.SupervisorPid),
    };

    /// <param name="cliConfigArgs">The parent's normalised <c>Key=value</c> settings from the command line.</param>
    /// <param name="environment">The parent's process environment (tests pass a dictionary).</param>
    /// <param name="supervisorPid">The parent's process id, so the child can exit when the parent does.</param>
    /// <param name="prefix">Environment prefix, normally <c>NPU_BRIDGE_</c>.</param>
    public static RelaunchPlan Build(
        IReadOnlyList<string> cliConfigArgs,
        IDictionary environment,
        int supervisorPid,
        string prefix = BridgeEnvironmentVariablesSource.DefaultPrefix)
    {
        ArgumentNullException.ThrowIfNull(cliConfigArgs);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentOutOfRangeException.ThrowIfLessThan(supervisorPid, 1);

        var forwarded = new List<string>();
        var droppedSecrets = new List<string>();

        // Environment first so command-line values, appended later, still win in the child's configuration.
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is not string name || entry.Value is not string value
                || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var key = BridgeEnvironmentVariablesSource.Canonicalize(name[prefix.Length..]);
            if (RelaunchKeys.Contains(key) || !CommandLine.Options.Values.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ServiceCommandBuilder.SecretSettings.Contains(key))
            {
                droppedSecrets.Add(name);
                continue;
            }

            forwarded.Add($"{key}={value}");
        }

        foreach (var setting in cliConfigArgs)
        {
            var eq = setting.IndexOf('=', StringComparison.Ordinal);
            var key = eq > 0 ? setting[..eq] : setting;
            if (!RelaunchKeys.Contains(key))
            {
                forwarded.Add(setting);
            }
        }

        forwarded.Add($"{nameof(BridgeOptions.SelfRelaunch)}=off");
        forwarded.Add($"{nameof(BridgeOptions.SupervisorPid)}={supervisorPid}");

        return new RelaunchPlan(forwarded, droppedSecrets, "run " + CommandLine.Render(forwarded));
    }

    /// <summary>The by-path process should hand over to an activated instance when all of these hold.</summary>
    public static bool ShouldRelaunch(BridgeOptions options, bool hasPackageIdentity, bool isWindowsService)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Backend == BackendKind.PhiSilica && !hasPackageIdentity && options.SelfRelaunch && !isWindowsService;
    }
}

/// <param name="ConfigArgs">Normalised settings for the child.</param>
/// <param name="DroppedSecrets">Environment variable names that were not forwarded.</param>
/// <param name="Arguments">The rendered argument string for <c>ActivateApplication</c>.</param>
public sealed record RelaunchPlan(IReadOnlyList<string> ConfigArgs, IReadOnlyList<string> DroppedSecrets, string Arguments);
