using NpuBridge.Configuration;

namespace NpuBridge.Tests;

public class CommandLineTests
{
    [Fact]
    public void No_args_means_run_with_no_settings()
    {
        var parsed = CommandLine.Parse([]);
        Assert.False(parsed.IsError);
        Assert.Equal(CommandVerb.Run, parsed.Verb);
        Assert.Empty(parsed.ConfigArgs);
    }

    [Theory]
    [InlineData("--backend", "fake")]
    [InlineData("--backend=fake", null)]
    [InlineData("--BACKEND", "fake")]
    public void Backend_option_maps_to_config_key(string first, string? second)
    {
        var args = second is null ? new[] { first } : new[] { first, second };
        var parsed = CommandLine.Parse(args);
        Assert.False(parsed.IsError, parsed.Error);
        Assert.Equal(["Backend=fake"], parsed.ConfigArgs);
    }

    [Fact]
    public void Bare_boolean_switches_become_true()
    {
        var parsed = CommandLine.Parse(["--verbose", "--truncate-history", "--backend", "fake"]);
        Assert.False(parsed.IsError, parsed.Error);
        Assert.Equal(["Verbose=true", "TruncateHistory=true", "Backend=fake"], parsed.ConfigArgs);
    }

    [Theory]
    [InlineData("--verbose", "off", "Verbose=off")]
    [InlineData("--verbose=off", null, "Verbose=off")]
    [InlineData("--truncate-history", "1", "TruncateHistory=1")]
    public void Boolean_switch_accepts_explicit_value(string first, string? second, string expected)
    {
        var args = second is null ? new[] { first } : new[] { first, second };
        var parsed = CommandLine.Parse(args);
        Assert.Equal([expected], parsed.ConfigArgs);
    }

    [Fact]
    public void Boolean_switch_does_not_swallow_a_non_boolean_token()
    {
        var parsed = CommandLine.Parse(["service", "--verbose", "install"]);
        Assert.False(parsed.IsError, parsed.Error);
        Assert.Equal(["install"], parsed.VerbArgs);
        Assert.Equal(["Verbose=true"], parsed.ConfigArgs);

        var help = CommandLine.Parse(["--verbose", "-h"]);
        Assert.Equal(CommandVerb.Help, help.Verb);
    }

    [Theory]
    [InlineData("--backend=")]
    [InlineData("--listen=")]
    public void Empty_value_is_an_error_not_a_reset(string arg)
    {
        var parsed = CommandLine.Parse([arg]);
        Assert.True(parsed.IsError);
        Assert.Contains("requires a value", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Value_option_followed_by_another_option_is_an_error()
    {
        var parsed = CommandLine.Parse(["--backend", "--verbose"]);
        Assert.True(parsed.IsError);
        Assert.Contains("--backend", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Negative_numbers_and_duplicates_pass_through_in_order()
    {
        var parsed = CommandLine.Parse(["--context-window-hint", "-1", "--backend", "fake", "--backend", "aion"]);
        Assert.False(parsed.IsError, parsed.Error);
        Assert.Equal(["ContextWindowHint=-1", "Backend=fake", "Backend=aion"], parsed.ConfigArgs);
    }

    [Fact]
    public void Stray_positional_after_run_is_an_error()
    {
        var parsed = CommandLine.Parse(["run", "foo"]);
        Assert.True(parsed.IsError);
        Assert.Contains("foo", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Double_dash_alone_is_an_unknown_option()
    {
        Assert.True(CommandLine.Parse(["--"]).IsError);
    }

    [Fact]
    public void Value_option_without_value_is_an_error()
    {
        var parsed = CommandLine.Parse(["--backend"]);
        Assert.True(parsed.IsError);
        Assert.Contains("--backend", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_option_is_an_error()
    {
        var parsed = CommandLine.Parse(["--temperature", "1"]);
        Assert.True(parsed.IsError);
        Assert.Contains("--temperature", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_verb_is_explicit_synonym()
    {
        var parsed = CommandLine.Parse(["run", "--listen", "http://127.0.0.1:9000"]);
        Assert.Equal(CommandVerb.Run, parsed.Verb);
        Assert.Equal(["Listen=http://127.0.0.1:9000"], parsed.ConfigArgs);
    }

    [Fact]
    public void Service_verb_collects_subverb_and_settings()
    {
        var parsed = CommandLine.Parse(["service", "install", "--backend", "aion", "--service-name", "NpuBridgeDev"]);
        Assert.False(parsed.IsError, parsed.Error);
        Assert.Equal(CommandVerb.Service, parsed.Verb);
        Assert.Equal(["install"], parsed.VerbArgs);
        Assert.Equal(["Backend=aion", "ServiceName=NpuBridgeDev"], parsed.ConfigArgs);
    }

    [Fact]
    public void Service_verb_without_subverb_is_an_error()
    {
        var parsed = CommandLine.Parse(["service"]);
        Assert.True(parsed.IsError);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    [InlineData("help")]
    public void Help_forms(string arg)
    {
        Assert.Equal(CommandVerb.Help, CommandLine.Parse([arg]).Verb);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("version")]
    public void Version_forms(string arg)
    {
        Assert.Equal(CommandVerb.Version, CommandLine.Parse([arg]).Verb);
    }

    [Fact]
    public void Unknown_verb_is_an_error()
    {
        var parsed = CommandLine.Parse(["serve"]);
        Assert.True(parsed.IsError);
        Assert.Contains("serve", parsed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_option_in_usage_text_is_known()
    {
        foreach (var option in CommandLine.Options.Keys)
        {
            Assert.Contains(option, CommandLine.Usage, StringComparison.Ordinal);
        }
    }
}
