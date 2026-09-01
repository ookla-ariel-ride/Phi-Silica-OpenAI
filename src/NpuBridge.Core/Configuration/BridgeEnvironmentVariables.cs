using System.Collections;
using Microsoft.Extensions.Configuration;

namespace NpuBridge.Configuration;

/// <summary>
/// Environment-variable configuration source that maps <c>NPU_BRIDGE_LAF_TOKEN</c> (and
/// <c>NPU_BRIDGE_LAFTOKEN</c>) onto the <see cref="BridgeOptions"/> property <c>LafToken</c>. The stock
/// provider keeps the underscores, so multi-word settings would silently never bind.
/// </summary>
public sealed class BridgeEnvironmentVariablesSource : IConfigurationSource
{
    public const string DefaultPrefix = "NPU_BRIDGE_";

    private readonly string _prefix;
    private readonly IDictionary? _environment;

    /// <param name="prefix">Variable prefix, stripped before mapping.</param>
    /// <param name="environment">Override for tests; defaults to the process environment.</param>
    public BridgeEnvironmentVariablesSource(string prefix = DefaultPrefix, IDictionary? environment = null)
    {
        _prefix = prefix;
        _environment = environment;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new Provider(_prefix, _environment ?? Environment.GetEnvironmentVariables());

    /// <summary>Canonical key for a variable name: strips the prefix and underscores and matches a <see cref="BridgeOptions"/> property.</summary>
    public static string Canonicalize(string nameWithoutPrefix)
    {
        var compact = nameWithoutPrefix.Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var property in typeof(BridgeOptions).GetProperties())
        {
            if (string.Equals(property.Name, compact, StringComparison.OrdinalIgnoreCase))
            {
                return property.Name;
            }
        }

        return nameWithoutPrefix;
    }

    private sealed class Provider : ConfigurationProvider
    {
        private readonly string _prefix;
        private readonly IDictionary _environment;

        public Provider(string prefix, IDictionary environment)
        {
            _prefix = prefix;
            _environment = environment;
        }

        public override void Load()
        {
            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in _environment)
            {
                var name = entry.Key as string;
                if (name is null || !name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                data[Canonicalize(name[_prefix.Length..])] = entry.Value as string;
            }

            Data = data;
        }
    }
}

public static class BridgeEnvironmentVariablesExtensions
{
    public static IConfigurationBuilder AddNpuBridgeEnvironmentVariables(
        this IConfigurationBuilder builder,
        string prefix = BridgeEnvironmentVariablesSource.DefaultPrefix,
        IDictionary? environment = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(new BridgeEnvironmentVariablesSource(prefix, environment));
        return builder;
    }
}
