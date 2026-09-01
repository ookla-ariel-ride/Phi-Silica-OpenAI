using System.Collections;
using NpuBridge.Configuration;

namespace NpuBridge.Tests;

/// <summary>Exercises the real source composition the exe and the service verbs share.</summary>
public class BridgeConfigurationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("npu-bridge-cfg-").FullName;

    [Fact]
    public void Full_precedence_chain_with_real_files()
    {
        File.WriteAllText(Path.Combine(_dir, BridgeConfiguration.SettingsFile),
            """{ "Backend": "phi-silica", "QueueCapacity": 2, "ServiceName": "FromJson", "ContextCacheSize": 9 }""");
        File.WriteAllText(Path.Combine(_dir, BridgeConfiguration.LocalSettingsFile),
            """{ "ServiceName": "FromLocal", "ContextCacheSize": 1, "LafToken": "tok" }""");
        var env = new Hashtable
        {
            ["NPU_BRIDGE_SERVICE_NAME"] = "FromEnv",
            ["NPU_BRIDGE_QUEUE_CAPACITY"] = "3",
            ["VERBOSE"] = "maybe",   // unprefixed noise must be ignored
        };

        var options = BridgeOptionsBinder.Bind(BridgeConfiguration.Build(_dir, ["Backend=fake"], env));

        Assert.Equal(BackendKind.Fake, options.Backend);   // CLI
        Assert.Equal("FromEnv", options.ServiceName);      // env beats local beats json
        Assert.Equal(3, options.QueueCapacity);            // env beats json
        Assert.Equal(1, options.ContextCacheSize);         // local beats json
        Assert.Equal("tok", options.LafToken);             // local only
        Assert.False(options.Verbose);
    }

    [Fact]
    public void Missing_files_are_fine()
    {
        var options = BridgeOptionsBinder.Bind(BridgeConfiguration.Build(_dir, [], new Hashtable()));
        Assert.Equal(BackendKind.PhiSilica, options.Backend);
    }

    [Fact]
    public void Service_verbs_see_the_same_service_name_as_the_server()
    {
        File.WriteAllText(Path.Combine(_dir, BridgeConfiguration.SettingsFile), """{ "ServiceName": "BridgeDev" }""");
        var options = BridgeOptionsBinder.Bind(BridgeConfiguration.Build(_dir, [], new Hashtable()));
        Assert.Equal("BridgeDev", options.ServiceName);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
