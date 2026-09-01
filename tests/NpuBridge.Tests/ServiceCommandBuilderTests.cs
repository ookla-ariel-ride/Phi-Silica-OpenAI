using NpuBridge.Hosting;

namespace NpuBridge.Tests;

public class ServiceCommandBuilderTests
{
    private const string Exe = @"C:\Program Files\npu-bridge\NpuBridge.exe";

    [Fact]
    public void Install_creates_service_with_quoted_binpath_and_auto_start()
    {
        var commands = ServiceCommandBuilder.Build("install", "NpuBridge", Exe, ["Backend=aion", "Listen=http://127.0.0.1:5273"]);

        Assert.Equal(3, commands.Count);
        var create = commands[0];
        Assert.Equal("sc.exe", create.FileName);
        Assert.Equal(
            "create NpuBridge binPath= \"\\\"C:\\Program Files\\npu-bridge\\NpuBridge.exe\\\" run --backend aion --listen http://127.0.0.1:5273\" start= auto DisplayName= \"npu-bridge (NpuBridge)\"",
            create.Arguments);
        Assert.False(create.IgnoreFailure);

        Assert.StartsWith("description NpuBridge ", commands[1].Arguments, StringComparison.Ordinal);
        Assert.StartsWith("failure NpuBridge ", commands[2].Arguments, StringComparison.Ordinal);
        Assert.True(commands[2].IgnoreFailure);
    }

    [Fact]
    public void Binpath_is_the_service_command_line_with_crt_quoting()
    {
        var binPath = ServiceCommandBuilder.BuildBinPath(Exe, ["ServiceName=Svc", "ContextWindowHint=8192"]);
        Assert.Equal("\"C:\\Program Files\\npu-bridge\\NpuBridge.exe\" run --service-name Svc --context-window-hint 8192", binPath);
    }

    [Fact]
    public void Binpath_quotes_values_with_spaces()
    {
        var binPath = ServiceCommandBuilder.BuildBinPath(Exe, ["Listen=http://127.0.0.1:5273; http://[::1]:5273"]);
        Assert.Equal("\"C:\\Program Files\\npu-bridge\\NpuBridge.exe\" run --listen \"http://127.0.0.1:5273; http://[::1]:5273\"", binPath);
    }

    [Theory]
    [InlineData("plain", false, "plain")]
    [InlineData("has space", false, "\"has space\"")]
    [InlineData("say \"hi\"", false, "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\x\", false, "\"C:\\x\\\\\"")]
    [InlineData(@"a\\""b", false, "\"a\\\\\\\\\\\"b\"")]
    [InlineData("", false, "\"\"")]
    [InlineData("plain", true, "\"plain\"")]
    public void Crt_quoting_matches_argv_rules(string value, bool force, string expected)
    {
        Assert.Equal(expected, ServiceCommandBuilder.CrtQuote(value, force));
    }

    [Fact]
    public void Crt_quoting_round_trips_through_argv_parsing()
    {
        string[] samples = ["plain", "has space", "say \"hi\"", @"C:\x\", @"a\\""b", @"trailing\\", "\"", " "];
        foreach (var sample in samples)
        {
            var quoted = ServiceCommandBuilder.CrtQuote(sample, force: false);
            Assert.Equal(sample, Assert.Single(ParseArgv(quoted)));

            // Double-quoted, as it would be inside binPath= "...".
            var twice = ServiceCommandBuilder.CrtQuote(quoted, force: true);
            Assert.Equal(quoted, Assert.Single(ParseArgv(twice)));
        }
    }

    [Fact]
    public void Secrets_are_refused_in_binpath()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServiceCommandBuilder.BuildBinPath(Exe, ["LafToken=abc"]));
        Assert.Contains("appsettings.local.json", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ServiceCommandBuilder.BuildBinPath(Exe, ["LafAttestation=x has registered"]));
    }

    [Fact]
    public void Uninstall_stops_then_deletes()
    {
        var commands = ServiceCommandBuilder.Build("uninstall", "NpuBridge", Exe, []);
        Assert.Equal(2, commands.Count);
        Assert.Equal("stop NpuBridge", commands[0].Arguments);
        Assert.True(commands[0].IgnoreFailure);
        Assert.Equal("delete NpuBridge", commands[1].Arguments);
        Assert.False(commands[1].IgnoreFailure);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("STOP")]
    public void Start_and_stop_are_single_commands(string verb)
    {
        var command = Assert.Single(ServiceCommandBuilder.Build(verb, "Svc", Exe, []));
        Assert.Equal($"{verb.ToLowerInvariant()} Svc", command.Arguments);
    }

    [Fact]
    public void Unknown_verb_throws()
    {
        Assert.Throws<ArgumentException>(() => ServiceCommandBuilder.Build("restart", "Svc", Exe, []));
    }

    [Theory]
    [InlineData("Npu Bridge")]
    [InlineData("Npu/Bridge")]
    [InlineData("Npu\"Bridge")]
    [InlineData("")]
    public void Bad_service_names_are_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => ServiceCommandBuilder.Build("start", name, Exe, []));
    }

    [Fact]
    public void Setting_not_in_key_value_form_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ServiceCommandBuilder.BuildBinPath(Exe, ["Verbose"]));
    }

    /// <summary>Minimal CommandLineToArgvW-compatible parser (post-2008 CRT rules) for round-trip checks.</summary>
    private static List<string> ParseArgv(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var i = 0;
        var haveArg = false;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', slashes);
                }

                haveArg = true;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                haveArg = true;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (haveArg)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    haveArg = false;
                }

                i++;
                continue;
            }

            current.Append(c);
            haveArg = true;
            i++;
        }

        if (haveArg)
        {
            args.Add(current.ToString());
        }

        return args;
    }
}
