using System.Net;
using System.Net.Http.Json;
using System.Text;
using NpuBridge.Api;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;
using NpuBridge.Prompting;

namespace NpuBridge.Tests;

/// <summary>
/// Pins the seam between preparation (body, validation, readiness, placement, rendering) and
/// generation. Preparation never touches the backend, so every failure it produces must leave the
/// created-context count at zero — not merely balanced, which is trivially true when a context was
/// created and disposed. When the streaming path grows its own generation phase these are the tests
/// that catch it if the shared front half starts doing work it should not, or stops doing work it
/// should.
/// </summary>
public class ChatRequestPreparationTests
{
    private const string Path = "/v1/chat/completions";

    [Fact]
    public async Task A_malformed_body_creates_no_context()
    {
        var fake = new FakeBackend();
        await using var host = await BridgeTestHost.StartAsync(fake);

        using var content = new StringContent("{not json", Encoding.UTF8, "application/json");
        var response = await host.Client.PostAsync(Path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.ContextsCreated);
    }

    [Fact]
    public async Task A_validation_failure_creates_no_context()
    {
        var fake = new FakeBackend();
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            n = 2,
            messages = new[] { new { role = "user", content = "hi" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.ContextsCreated);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_backend_that_is_still_loading_creates_no_context()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeBackend(new FakeBackendOptions { InitGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake, waitForReady: false);

        var response = await host.Client.PostAsJsonAsync(Path, Simple());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, fake.ContextsCreated);

        gate.SetResult();
        await host.Lifecycle.Initialization;
    }

    [Fact]
    public async Task A_backend_that_failed_to_load_creates_no_context()
    {
        var fake = new FakeBackend(new FakeBackendOptions { InitFailure = new InvalidOperationException("nope") });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Simple());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, fake.ContextsCreated);
    }

    [Fact]
    public async Task A_forced_placement_conflict_creates_no_context()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Capabilities = BackendCapabilities.None });
        await using var host = await BridgeTestHost.StartAsync(
            fake,
            new BridgeOptions { Backend = BackendKind.Fake, SystemPromptPlacement = SystemPromptPlacement.Native });

        var response = await host.Client.PostAsJsonAsync(Path, WithSystem);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.ContextsCreated);
        Assert.Empty(fake.Calls);
    }

    /// <summary>
    /// The rendering half of the seam: what reaches the backend is exactly what
    /// <see cref="PromptTemplate.Render"/> produced for this request, character for character, split
    /// between the native context and the prompt the way the placement decided. The HTTP-level tests in
    /// <see cref="ChatCompletionsTests"/> assert the placement with StartsWith/DoesNotContain; this one
    /// compares against the template's own output so the two cannot drift apart silently.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_prepared_request_hands_the_backend_exactly_what_the_template_rendered(bool nativeSystem)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = nativeSystem ? BackendCapabilities.SystemPromptContext : BackendCapabilities.None,
            Responder = _ => ["ok"],
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, WithSystem);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var expected = PromptTemplate.Render(
            [
                new ChatMessage("system", ChatMessageContent.FromText("be terse"), null, null),
                new ChatMessage("user", ChatMessageContent.FromText("hi"), null, null),
            ],
            nativeSystem);

        var call = Assert.Single(fake.Calls);
        Assert.Equal(expected.Prompt, call.Prompt);
        Assert.Equal(nativeSystem ? expected.SystemText : null, call.SystemPrompt);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(fake.ContextsCreated, fake.ContextsDisposed);
    }

    private static object Simple() =>
        new { model = "fake", messages = new[] { new { role = "user", content = "say hi" } } };

    private static object WithSystem => new
    {
        model = "fake",
        messages = new object[]
        {
            new { role = "system", content = "be terse" },
            new { role = "user", content = "hi" },
        },
    };
}
