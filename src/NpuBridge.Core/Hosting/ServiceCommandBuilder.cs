using System.Text;
using NpuBridge.Configuration;

namespace NpuBridge.Hosting;

/// <param name="FileName">Executable to run (always <c>sc.exe</c> today).</param>
/// <param name="Arguments">Raw argument string. Built by hand because <c>sc.exe</c>'s quoting rules
/// (<c>binPath= "..."</c> with a space after the equals sign) defeat automatic ArgumentList escaping.</param>
/// <param name="Description">What this step does, for console output.</param>
/// <param name="IgnoreFailure">Non-zero exit is expected in some states (e.g. stopping a stopped service).</param>
public sealed record ServiceCommand(string FileName, string Arguments, string Description, bool IgnoreFailure = false);

/// <summary>Turns <c>service install|uninstall|start|stop</c> into the <c>sc.exe</c> invocations that implement it.</summary>
public static class ServiceCommandBuilder
{
    public const string ServiceDescription = "OpenAI-compatible HTTP endpoint for the on-device NPU language model (npu-bridge).";

    public static readonly IReadOnlySet<string> Verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "install", "uninstall", "start", "stop",
    };

    /// <summary>Settings that must never be baked into the service's registry-visible command line.</summary>
    public static readonly IReadOnlySet<string> SecretSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        nameof(BridgeOptions.LafToken),
        nameof(BridgeOptions.LafAttestation),
    };

    /// <param name="verb">One of <see cref="Verbs"/>.</param>
    /// <param name="serviceName">Service name (single token, see <see cref="BridgeOptionsBinder.IsValidServiceName"/>).</param>
    /// <param name="exePath">Absolute path of the npu-bridge executable.</param>
    /// <param name="configArgs">Normalised <c>Key=value</c> settings to bake into the service command line.</param>
    public static IReadOnlyList<ServiceCommand> Build(
        string verb,
        string serviceName,
        string exePath,
        IReadOnlyList<string> configArgs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(configArgs);

        if (!BridgeOptionsBinder.IsValidServiceName(serviceName))
        {
            throw new ArgumentException("Service name must be a single token without whitespace, slashes or quotes.", nameof(serviceName));
        }

        return verb.ToLowerInvariant() switch
        {
            "install" =>
            [
                new ServiceCommand("sc.exe",
                    $"create {serviceName} binPath= {CrtQuote(BuildBinPath(exePath, configArgs), force: true)} start= auto DisplayName= \"npu-bridge ({serviceName})\"",
                    $"Create service '{serviceName}'"),
                new ServiceCommand("sc.exe",
                    $"description {serviceName} \"{ServiceDescription}\"",
                    "Set service description"),
                new ServiceCommand("sc.exe",
                    $"failure {serviceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000",
                    "Configure automatic restart on failure",
                    IgnoreFailure: true),
            ],
            "uninstall" =>
            [
                new ServiceCommand("sc.exe", $"stop {serviceName}", $"Stop service '{serviceName}' if running", IgnoreFailure: true),
                new ServiceCommand("sc.exe", $"delete {serviceName}", $"Delete service '{serviceName}'"),
            ],
            "start" => [new ServiceCommand("sc.exe", $"start {serviceName}", $"Start service '{serviceName}'")],
            "stop" => [new ServiceCommand("sc.exe", $"stop {serviceName}", $"Stop service '{serviceName}'")],
            _ => throw new ArgumentException($"Unknown service verb '{verb}'. Expected install, uninstall, start or stop.", nameof(verb)),
        };
    }

    /// <summary>
    /// The command line the SCM will run: quoted exe path, the <c>run</c> verb, then each setting as a
    /// <c>--option value</c> pair, quoted per the C runtime's rules so it parses back identically.
    /// Secret settings are rejected because <c>ImagePath</c> is world-readable in the registry.
    /// </summary>
    public static string BuildBinPath(string exePath, IReadOnlyList<string> configArgs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(configArgs);

        foreach (var setting in configArgs)
        {
            var eq = setting.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new ArgumentException($"Setting '{setting}' is not in Key=value form.", nameof(configArgs));
            }

            if (SecretSettings.Contains(setting[..eq]))
            {
                throw new ArgumentException(
                    $"'{setting[..eq]}' must not be baked into the service command line (it would be readable by every local user in the registry). " +
                    "Put it in appsettings.local.json next to the exe instead.", nameof(configArgs));
            }
        }

        var rendered = CommandLine.Render(configArgs);
        return rendered.Length == 0
            ? $"{CrtQuote(exePath, force: true)} run"
            : $"{CrtQuote(exePath, force: true)} run {rendered}";
    }

    /// <summary>
    /// Quotes one argument so that <c>CommandLineToArgvW</c> / the C runtime parse it back verbatim:
    /// wrap in quotes, escape embedded quotes with a backslash, and double any run of backslashes that
    /// precedes a quote or the closing quote. Applied twice (value, then whole <c>binPath=</c>) the
    /// result survives sc.exe → registry → service argv.
    /// </summary>
    public static string CrtQuote(string value, bool force)
    {
        ArgumentNullException.ThrowIfNull(value);
        var needsQuotes = force || value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || c == '"') || value.EndsWith('\\');
        if (!needsQuotes)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
