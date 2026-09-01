using System.Collections;
using Microsoft.Extensions.Configuration;

namespace NpuBridge.Configuration;

/// <summary>
/// The one place that defines where settings come from and in what order. Used by the server host
/// and by the <c>service</c> verbs so both resolve the same <see cref="BridgeOptions"/>.
/// Precedence (last wins): <c>appsettings.json</c> &lt; <c>appsettings.local.json</c> &lt;
/// <c>NPU_BRIDGE_*</c> environment &lt; command line.
/// </summary>
public static class BridgeConfiguration
{
    public const string SettingsFile = "appsettings.json";
    public const string LocalSettingsFile = "appsettings.local.json";

    /// <param name="builder">Builder to add sources to (existing sources are kept and rank lowest).</param>
    /// <param name="basePath">Folder holding the JSON files, normally the exe's directory.</param>
    /// <param name="configArgs">Normalised <c>Key=value</c> settings from <see cref="CommandLine.Parse"/>.</param>
    /// <param name="environment">Override for tests; defaults to the process environment.</param>
    public static IConfigurationBuilder AddNpuBridgeSources(
        this IConfigurationBuilder builder,
        string basePath,
        IReadOnlyList<string> configArgs,
        IDictionary? environment = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(configArgs);

        builder.AddJsonFile(Path.Combine(basePath, SettingsFile), optional: true, reloadOnChange: false);
        builder.AddJsonFile(Path.Combine(basePath, LocalSettingsFile), optional: true, reloadOnChange: false);
        builder.AddNpuBridgeEnvironmentVariables(environment: environment);
        builder.AddCommandLine([.. configArgs]);
        return builder;
    }

    /// <summary>Builds a standalone configuration with exactly the npu-bridge sources.</summary>
    public static IConfigurationRoot Build(string basePath, IReadOnlyList<string> configArgs, IDictionary? environment = null) =>
        new ConfigurationBuilder().AddNpuBridgeSources(basePath, configArgs, environment).Build();
}
