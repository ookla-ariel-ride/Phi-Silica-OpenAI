namespace NpuBridge.Configuration;

public enum CommandVerb
{
    Run,
    Service,
    Help,
    Version,
}

/// <param name="Verb">What to do.</param>
/// <param name="VerbArgs">Positional arguments after the verb (e.g. <c>install</c> for <c>service install</c>).</param>
/// <param name="ConfigArgs">Settings normalised to <c>Key=value</c>, ready for <c>AddCommandLine</c>.</param>
/// <param name="Error">Non-null when parsing failed; the caller prints it with the usage text and exits non-zero.</param>
public sealed record CommandLineParse(
    CommandVerb Verb,
    IReadOnlyList<string> VerbArgs,
    IReadOnlyList<string> ConfigArgs,
    string? Error)
{
    public bool IsError => Error is not null;
}

/// <summary>
/// Tiny hand-rolled parser: verbs first, then <c>--kebab-case</c> options mapped to <see cref="BridgeOptions"/>
/// property names. Kept out of <c>Microsoft.Extensions.Configuration.CommandLine</c>'s hands so bare boolean
/// switches (<c>--verbose</c>) and <c>--flag=value</c> both work and unknown flags are rejected loudly.
/// </summary>
public static class CommandLine
{
    /// <summary>Option name → configuration key.</summary>
    public static readonly IReadOnlyDictionary<string, string> Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["--backend"] = nameof(BridgeOptions.Backend),
        ["--listen"] = nameof(BridgeOptions.Listen),
        ["--queue-capacity"] = nameof(BridgeOptions.QueueCapacity),
        ["--context-cache-size"] = nameof(BridgeOptions.ContextCacheSize),
        ["--truncate-history"] = nameof(BridgeOptions.TruncateHistory),
        ["--tool-emulation"] = nameof(BridgeOptions.ToolEmulation),
        ["--tool-schema"] = nameof(BridgeOptions.ToolSchema),
        ["--context-window-hint"] = nameof(BridgeOptions.ContextWindowHint),
        ["--laf-token"] = nameof(BridgeOptions.LafToken),
        ["--laf-attestation"] = nameof(BridgeOptions.LafAttestation),
        ["--verbose"] = nameof(BridgeOptions.Verbose),
        ["--service-name"] = nameof(BridgeOptions.ServiceName),
    };

    /// <summary>Options that may appear without a value, meaning <c>true</c>.</summary>
    public static readonly IReadOnlySet<string> BooleanSwitches = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--truncate-history",
        "--verbose",
    };

    public const string Usage = """
        npu-bridge — OpenAI-compatible endpoint for the on-device NPU language model

        Usage:
          npu-bridge [run] [options]              Start the HTTP server (default verb)
          npu-bridge service install [options]    Register as a Windows service (elevated prompt)
          npu-bridge service uninstall            Remove the Windows service
          npu-bridge service start|stop           Control the installed service
          npu-bridge --help | --version

        Options (also settable in appsettings.json and as NPU_BRIDGE_<NAME> environment variables):
          --backend phi-silica|aion|fake    Model backend                      [phi-silica]
          --listen <url[;url]>              Kestrel listen URL(s)              [http://127.0.0.1:5273]
          --queue-capacity <n>              Queued requests before 429         [4]
          --context-cache-size <n>          Cached conversation contexts       [4]
          --truncate-history                Drop oldest turns on overflow instead of returning 400
          --tool-emulation on|off           Emulated function calling          [on]
          --tool-schema compact|full        How tool schemas reach the prompt  [compact]
          --context-window-hint <tokens>    Used only for pressure warnings    [4096]
          --laf-token <token>               Phi Silica LAF token (prefer env NPU_BRIDGE_LAF_TOKEN)
          --laf-attestation <text>          Phi Silica LAF attestation (prefer env NPU_BRIDGE_LAF_ATTESTATION)
          --service-name <name>             Windows service name               [NpuBridge]
          --verbose                         Log flattened prompts and raw model output
        """;

    public static CommandLineParse Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var verb = CommandVerb.Run;
        var verbArgs = new List<string>();
        var config = new List<string>();
        var i = 0;

        if (args.Count > 0 && !args[0].StartsWith('-'))
        {
            switch (args[0].ToLowerInvariant())
            {
                case "run":
                    verb = CommandVerb.Run;
                    break;
                case "service":
                    verb = CommandVerb.Service;
                    break;
                case "help":
                case "/?":
                    return new CommandLineParse(CommandVerb.Help, verbArgs, config, null);
                case "version":
                    return new CommandLineParse(CommandVerb.Version, verbArgs, config, null);
                default:
                    return Error($"Unknown command '{args[0]}'.");
            }

            i = 1;
        }

        for (; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg is "-h" or "--help" or "-?" or "/?")
            {
                return new CommandLineParse(CommandVerb.Help, verbArgs, config, null);
            }

            if (arg is "--version")
            {
                return new CommandLineParse(CommandVerb.Version, verbArgs, config, null);
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (verb == CommandVerb.Service)
                {
                    verbArgs.Add(arg);
                    continue;
                }

                return Error($"Unexpected argument '{arg}'.");
            }

            var name = arg;
            string? value = null;
            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                name = arg[..eq];
                value = arg[(eq + 1)..];
            }

            if (!Options.TryGetValue(name, out var key))
            {
                return Error($"Unknown option '{name}'.");
            }

            if (value is null)
            {
                var next = i + 1 < args.Count ? args[i + 1] : null;
                if (BooleanSwitches.Contains(name))
                {
                    // Only swallow the next token when it is unmistakably a boolean, so
                    // `service --verbose install` keeps `install` as the verb argument.
                    if (next is not null && BridgeOptionsBinder.TryParseBool(next, out _))
                    {
                        value = args[++i];
                    }
                    else
                    {
                        value = "true";
                    }
                }
                else if (next is not null && !next.StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
                else
                {
                    return Error($"Option '{name}' requires a value.");
                }
            }

            if (value.Length == 0)
            {
                return Error($"Option '{name}' requires a value.");
            }

            config.Add($"{key}={value}");
        }

        if (verb == CommandVerb.Service && verbArgs.Count == 0)
        {
            return Error("The 'service' command needs one of: install, uninstall, start, stop.");
        }

        return new CommandLineParse(verb, verbArgs, config, null);

        CommandLineParse Error(string message) => new(verb, verbArgs, config, message);
    }
}
