using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Backends.Fake;
using NpuBridge.Tokenizers;

namespace NpuBridge.Tests;

/// <summary>
/// <c>POST /debug/tokenize</c>: the backend's token count of a literal text (D80). Loopback only,
/// like <c>/debug/generate</c>; it needs no model, so it answers while the backend is still loading,
/// which is what lets the smoke script measure the preflight boundary against the tokenizer.
/// </summary>
public class DebugTokenizeTests
{
    private const string Path = "/debug/tokenize";

    [Fact]
    public async Task Counts_with_the_backends_counter_and_names_it()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync(Path, new { text = "abcdefghij" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("chars/4", root.GetProperty("counter").GetString());
        Assert.Equal(10, root.GetProperty("chars").GetInt32());
        Assert.Equal(3, root.GetProperty("tokens").GetInt32());
    }

    [Fact]
    public async Task Counts_phi3_tokens_when_the_backend_counts_that_way()
    {
        var fake = new FakeBackend(new FakeBackendOptions { TokenCounter = Phi3TokenCounter.Instance });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { text = "Hello world" });

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("phi-3", root.GetProperty("counter").GetString());
        Assert.Equal(11, root.GetProperty("chars").GetInt32());
        Assert.Equal(2, root.GetProperty("tokens").GetInt32());
    }

    [Fact]
    public async Task An_empty_text_is_zero_tokens()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync(Path, new { text = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, root.GetProperty("tokens").GetInt32());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":null}")]
    [InlineData("null")]
    public async Task A_body_without_text_is_a_400_with_the_envelope(string json)
    {
        await using var host = await BridgeTestHost.StartAsync();

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await host.Client.PostAsync(Path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("missing_text", error.GetProperty("code").GetString());
        Assert.Equal("text", error.GetProperty("param").GetString());
    }

    [Fact]
    public async Task Answers_while_the_backend_is_still_loading()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { InitGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake, waitForReady: false);

        var response = await host.Client.PostAsJsonAsync(Path, new { text = "abcd" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("tokens").GetInt32());
        gate.SetResult();
    }

    [Fact]
    public async Task Non_loopback_callers_get_403()
    {
        await using var host = await BridgeTestHost.StartAsync(remoteAddress: IPAddress.Parse("10.0.0.5"));

        var response = await host.Client.PostAsJsonAsync(Path, new { text = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("loopback_only", error.GetProperty("code").GetString());
    }
}
