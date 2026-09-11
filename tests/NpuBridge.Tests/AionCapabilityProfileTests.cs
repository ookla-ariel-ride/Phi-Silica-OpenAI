using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NpuBridge.Api;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;
using NpuBridge.Prompting;

namespace NpuBridge.Tests;

/// <summary>
/// The pipeline seen through the capability set the Aion Instruct adapter advertises (chunk 6): no
/// sampling options, no native system-prompt context, no prompt-length preflight. The adapter itself is
/// WinRT and lives in the exe, so these tests run the fake with that exact profile and pin what the
/// endpoints must do with it -- fold the system text, drop and warn about sampling, and learn about
/// overflow only from the generation. Each test counts contexts created against disposed.
/// </summary>
public class AionCapabilityProfileTests
{
    private const string Path = "/v1/chat/completions";

    /// <summary>
    /// Mirror of <c>AionBackend.Capabilities</c>. Cancellation is the only flag the runtime earned on
    /// hardware (D68); the three absent ones are the subject here.
    /// </summary>
    private const BackendCapabilities AionProfile = BackendCapabilities.Cancellation;

    private static readonly object[] ConversationWithSystem =
    [
        new { role = "system", content = "You are Ada. Answer with exactly: I am Ada." },
        new { role = "user", content = "What is your name?" },
        new { role = "assistant", content = "I am Ada." },
        new { role = "user", content = "Again?" },
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_system_text_is_folded_into_the_prompt_body_on_both_shapes(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Capabilities = AionProfile, Responder = _ => ["I am Ada."] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "aion-instruct", stream, messages = ConversationWithSystem });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(fake.Calls);
        Assert.Null(call.SystemPrompt);

        // Exactly what the template renders under folded placement: the system text leads the prompt,
        // the transcript follows. Not a substring check, so a drift in either direction shows up.
        var expected = PromptTemplate.Render(Messages(), nativeSystemPromptSupported: false);
        Assert.Equal(expected.Prompt, call.Prompt);
        AssertNoLeak(fake);
    }

    [Fact]
    public async Task Forced_native_placement_rejects_only_requests_that_carry_system_text()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Capabilities = AionProfile, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, new BridgeOptions
        {
            Backend = BackendKind.Fake,
            SystemPromptPlacement = SystemPromptPlacement.Native,
        });

        var rejected = await host.Client.PostAsJsonAsync(Path, new { model = "aion-instruct", messages = ConversationWithSystem });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("system_prompt_placement_unsupported", (await ReadJson(rejected)).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fake.Calls);

        var served = await host.Client.PostAsJsonAsync(Path, new { model = "aion-instruct", messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        var call = Assert.Single(fake.Calls);
        Assert.Null(call.SystemPrompt);
        Assert.Equal("hi", call.Prompt);
        AssertNoLeak(fake);
    }

    [Fact]
    public async Task Sampling_parameters_never_reach_the_backend_and_each_is_warned_once_per_process()
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions { Capabilities = AionProfile, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        for (var i = 0; i < 2; i++)
        {
            var response = await host.Client.PostAsJsonAsync(Path, new
            {
                model = "aion-instruct",
                temperature = 0.2,
                top_p = 0.9,
                top_k = 5,
                stream = i == 1,
                messages = new[] { new { role = "user", content = "hi" } },
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(2, fake.Calls.Count);
        Assert.All(fake.Calls, call => Assert.Null(call.Sampling));

        foreach (var parameter in new[] { "temperature", "top_p", "top_k" })
        {
            var warnings = capture.Records.Where(r =>
                r.Level == LogLevel.Warning && r.Message.Contains($"parameter {parameter} ", StringComparison.Ordinal));
            Assert.Single(warnings);
        }

        AssertNoLeak(fake);
    }

    [Fact]
    public async Task Without_a_preflight_the_debug_endpoint_reports_no_usable_length_even_when_the_fake_has_a_window()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Capabilities = AionProfile, MaxPromptChars = 8, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "fits" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await ReadJson(response);
        Assert.False(root.TryGetProperty("usable_prompt_chars", out _));
        Assert.Equal("Complete", root.GetProperty("status").GetString());
        AssertNoLeak(fake);
    }

    /// <summary>
    /// The third overflow behaviour (D67): with no preflight, the only place an over-length prompt can be
    /// learned about is the generation's own verdict. When the runtime does say
    /// <c>PromptLargerThanContext</c>, both shapes still turn it into <c>400 context_length_exceeded</c>
    /// and dispose the context.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_overflow_reported_by_the_generation_is_still_a_400_on_both_shapes(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Capabilities = AionProfile, MaxPromptChars = 8, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "aion-instruct",
            stream,
            messages = new[] { new { role = "user", content = "this prompt is longer than eight characters" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("context_length_exceeded", (await ReadJson(response)).GetProperty("error").GetProperty("code").GetString());
        Assert.Single(fake.Calls);
        Assert.Equal(1, fake.ContextsCreated);
        AssertNoLeak(fake);
    }

    private static List<ChatMessage> Messages() =>
        ConversationWithSystem
            .Select(m => JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(m), JsonDefaults.Options)!)
            .ToList();

    private static void AssertNoLeak(FakeBackend fake)
    {
        Assert.Equal(fake.ContextsCreated, fake.ContextsDisposed);
        Assert.Equal(0, fake.ActiveContexts);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}
