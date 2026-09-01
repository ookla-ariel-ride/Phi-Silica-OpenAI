using System.Collections;
using NpuBridge.Configuration;
using NpuBridge.Hosting;

namespace NpuBridge.Tests;

public class RelaunchArgumentsTests
{
    [Fact]
    public void Forwards_environment_then_cli_and_pins_relaunch_keys()
    {
        var env = new Hashtable
        {
            ["NPU_BRIDGE_QUEUE_CAPACITY"] = "7",
            ["NPU_BRIDGE_HIDE_CONSOLE"] = "true",
            ["NPU_BRIDGE_SELF_RELAUNCH"] = "on",       // relaunch key: never forwarded verbatim
            ["NPU_BRIDGE_LAF_TOKEN"] = "secret",       // env secret: dropped and reported
            ["NPU_BRIDGE_UNKNOWN_THING"] = "x",        // not an option: ignored
            ["PATH"] = "irrelevant",
        };

        var plan = RelaunchArguments.Build(["Backend=phi-silica", "SelfRelaunch=on", "Listen=http://127.0.0.1:5298"], env, 1234);

        Assert.Equal(
            ["QueueCapacity=7", "HideConsole=true", "Backend=phi-silica", "Listen=http://127.0.0.1:5298", "SelfRelaunch=off", "SupervisorPid=1234"],
            plan.ConfigArgs.OrderBy(a => a.StartsWith("Backend", StringComparison.Ordinal) ? 2 : a.StartsWith("Listen", StringComparison.Ordinal) ? 3 : a.StartsWith("SelfRelaunch", StringComparison.Ordinal) ? 4 : a.StartsWith("SupervisorPid", StringComparison.Ordinal) ? 5 : a.StartsWith("QueueCapacity", StringComparison.Ordinal) ? 0 : 1).ToArray());
        Assert.Equal(["NPU_BRIDGE_LAF_TOKEN"], plan.DroppedSecrets);
        Assert.StartsWith("run ", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains("--self-relaunch off", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains("--supervisor-pid 1234", plan.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", plan.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_secret_is_forwarded_because_it_was_already_on_a_command_line()
    {
        var plan = RelaunchArguments.Build(["LafToken=abc=="], new Hashtable(), 1);
        Assert.Contains("--laf-token abc==", plan.Arguments, StringComparison.Ordinal);
        Assert.Empty(plan.DroppedSecrets);
    }

    [Fact]
    public void Rendered_arguments_parse_back_identically()
    {
        var env = new Hashtable { ["NPU_BRIDGE_LAF_ATTESTATION"] = "x has registered", ["NPU_BRIDGE_CONTEXT_CACHE_SIZE"] = "9" };
        var plan = RelaunchArguments.Build(["Listen=http://127.0.0.1:5298; http://[::1]:5298"], env, 42);

        var argv = SplitArgv(plan.Arguments);
        var parsed = CommandLine.Parse(argv);
        Assert.False(parsed.IsError, parsed.Error);
        Assert.Equal(CommandVerb.Run, parsed.Verb);
        Assert.Contains("ContextCacheSize=9", parsed.ConfigArgs);
        Assert.Contains("Listen=http://127.0.0.1:5298; http://[::1]:5298", parsed.ConfigArgs);
        Assert.Contains("SupervisorPid=42", parsed.ConfigArgs);
        Assert.DoesNotContain(parsed.ConfigArgs, a => a.StartsWith("LafAttestation", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BackendKind.PhiSilica, false, false, true)]
    [InlineData(BackendKind.PhiSilica, true, false, false)]   // already has identity
    [InlineData(BackendKind.PhiSilica, false, true, false)]   // service: never relaunch
    [InlineData(BackendKind.Fake, false, false, false)]
    [InlineData(BackendKind.Aion, false, false, false)]
    public void Relaunch_decision(BackendKind backend, bool identity, bool service, bool expected)
    {
        var options = new BridgeOptions { Backend = backend };
        Assert.Equal(expected, RelaunchArguments.ShouldRelaunch(options, identity, service));
        Assert.False(RelaunchArguments.ShouldRelaunch(new BridgeOptions { Backend = backend, SelfRelaunch = false }, identity, service));
    }

    /// <summary>Same CRT rules as the argv parser in ServiceCommandBuilderTests.</summary>
    private static string[] SplitArgv(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var have = false;
        for (var i = 0; i < commandLine.Length;)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { n++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) { current.Append('"'); i++; }
                }
                else
                {
                    current.Append('\\', n);
                }

                have = true;
                continue;
            }

            if (c == '"') { inQuotes = !inQuotes; have = true; i++; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (have) { args.Add(current.ToString()); current.Clear(); have = false; }
                i++;
                continue;
            }

            current.Append(c);
            have = true;
            i++;
        }

        if (have)
        {
            args.Add(current.ToString());
        }

        return [.. args];
    }
}
