using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;

namespace NpuBridge.Tests;

public class SystemTextGuardTests
{
    private const string ChatPath = "/v1/chat/completions";

    private static readonly object[] Weather =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "get_weather",
                description = "Get current weather",
                parameters = new
                {
                    type = "object",
                    properties = new { location = new { type = "string" } },
                },
            },
        },
    ];

    [Fact]
    public async Task Token_window_refuses_native_system_text_before_context_creation_and_allows_smaller_text()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var refused = await PostChatAsync(host, new string('s', 500));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = await ErrorAsync(refused);
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Contains("Native system text alone exceeds the context window: 125 tokens fills the 100-token usable window.",
            error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();

        var accepted = await PostChatAsync(host, new string('s', 200));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Character_ceiling_applies_when_the_context_window_is_unknown()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = null });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var refused = await PostChatAsync(host, new string('s', 32_001));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = await ErrorAsync(refused);
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Contains("Native system text alone exceeds the 32,000-character safety ceiling: 32,001 characters.",
            error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();

        var accepted = await PostChatAsync(host, new string('s', 32_000));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Tool_definitions_are_named_only_when_they_are_in_the_system_text()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var withoutTools = await PostChatAsync(host, new string('s', 500));
        var withoutToolsError = await ErrorAsync(withoutTools);
        Assert.DoesNotContain("Rendered tool definitions", withoutToolsError.GetProperty("message").GetString(), StringComparison.Ordinal);

        var withTools = await PostChatAsync(host, new string('s', 500), tools: Weather);
        var withToolsError = await ErrorAsync(withTools);
        Assert.Contains("Rendered tool definitions are included in that count.",
            withToolsError.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Truncate_history_does_not_retry_a_native_system_text_refusal()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        var options = new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true };
        var capture = new CapturingLoggerProvider();
        await using var host = await BridgeTestHost.StartAsync(fake, options, loggerProvider: capture);

        var response = await host.Client.PostAsJsonAsync(ChatPath, new
        {
            model = "fake",
            messages = new[]
            {
                new { role = "system", content = new string('s', 500) },
                new { role = "user", content = "An earlier question." },
                new { role = "assistant", content = "An earlier answer." },
                new { role = "user", content = "Reply with OK." },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("x-npu-bridge-truncated-turns"));
        Assert.DoesNotContain(capture.Records, r => r.Message.Contains("dropped the oldest", StringComparison.Ordinal));
        Assert.Equal(0, fake.ContextsCreated);
        Assert.Empty(fake.Calls);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Streamed_native_system_text_refusal_is_a_plain_bad_request_before_any_frame()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostChatAsync(host, new string('s', 500), stream: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("data:", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Debug_generate_uses_the_same_guard_before_context_creation()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "reply", system = new string('s', 500) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ErrorAsync(response);
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Folded_system_text_is_left_to_the_prompt_preflight()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        var options = new BridgeOptions { Backend = BackendKind.Fake, SystemPromptPlacement = SystemPromptPlacement.Prompt };
        await using var host = await BridgeTestHost.StartAsync(fake, options);

        var response = await PostChatAsync(host, new string('s', 500));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(fake.Calls).SystemPrompt);
        host.AssertNoLeak();
    }

    [Fact]
    public void Unavailable_backend_has_no_known_context_window()
    {
        var backend = new UnavailableBackend("unavailable", "Unavailable", "not installed");
        Assert.Null(backend.ContextWindowTokens);
    }

    private static Task<HttpResponseMessage> PostChatAsync(BridgeTestHost host, string system, bool stream = false, object[]? tools = null) =>
        host.Client.PostAsJsonAsync(ChatPath, new
        {
            model = "fake",
            stream,
            tools,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = "Reply with OK." },
            },
        });

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").Clone();
    }
}
