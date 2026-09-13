using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Backends.Fake;

namespace NpuBridge.Tests;

public class GenerationHealthTests
{
    [Fact]
    public async Task One_backend_fault_does_not_degrade_health_and_preserves_the_exceptions_first_line()
    {
        var options = new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("RPC unavailable\r\nsecond line"),
        };
        var fake = new FakeBackend(options);
        await using var host = await BridgeTestHost.StartAsync(fake);

        var fault = await PostChatAsync(host, ChatBody.User());
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, fault.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ready", health.Body.GetProperty("status").GetString());
        Assert.Equal(1, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        var last = health.Body.GetProperty("last_generation");
        Assert.Equal("backend_fault", last.GetProperty("outcome").GetString());
        Assert.Equal("RPC unavailable", last.GetProperty("error").GetString());
        Assert.True(last.GetProperty("duration_ms").GetInt32() >= 0);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Two_faults_degrade_health_a_preflight_refusal_does_not_change_the_count_and_a_cut_success_recovers()
    {
        var options = new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("RPC unavailable"),
            Responder = _ => ["abcdefgh"],
        };
        var fake = new FakeBackend(options);
        await using var host = await BridgeTestHost.StartAsync(fake);

        Assert.Equal(HttpStatusCode.BadGateway, (await PostChatAsync(host, ChatBody.User())).StatusCode);

        options.MaxPromptChars = 1;
        var refusal = await PostChatAsync(host, ChatBody.User("too long"));
        var afterRefusal = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
        Assert.Equal(1, afterRefusal.Body.GetProperty("consecutive_backend_faults").GetInt32());

        options.MaxPromptChars = null;
        Assert.Equal(HttpStatusCode.BadGateway, (await PostChatAsync(host, ChatBody.User())).StatusCode);
        var degraded = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, degraded.StatusCode);
        Assert.Equal("degraded", degraded.Body.GetProperty("status").GetString());
        Assert.Equal("RPC unavailable", degraded.Body.GetProperty("error").GetString());
        Assert.Equal(2, degraded.Body.GetProperty("consecutive_backend_faults").GetInt32());

        options.FailAfterTokens = null;
        options.FailureException = null;
        var recovered = await host.Client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fake",
            messages = new[] { new { role = "user", content = "say hi" } },
            max_tokens = 1,
        });
        var afterSuccess = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterSuccess.StatusCode);
        Assert.Equal("ready", afterSuccess.Body.GetProperty("status").GetString());
        Assert.Equal(0, afterSuccess.Body.GetProperty("consecutive_backend_faults").GetInt32());
        var last = afterSuccess.Body.GetProperty("last_generation");
        Assert.Equal("ok", last.GetProperty("outcome").GetString());
        Assert.True(last.GetProperty("duration_ms").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("error").ValueKind);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Streamed_and_non_streamed_faults_share_the_counter()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("RPC unavailable"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var streamed = await host.Client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fake",
            messages = new[] { new { role = "user", content = "say hi" } },
            stream = true,
        });
        var json = await PostChatAsync(host, ChatBody.User());
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, streamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, json.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal(2, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Legacy_completions_record_backend_faults()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("RPC unavailable"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var streamed = await host.Client.PostAsJsonAsync("/v1/completions", new { model = "fake", prompt = "say hi", stream = true });
        var json = await host.Client.PostAsJsonAsync("/v1/completions", new { model = "fake", prompt = "say hi" });
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, streamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, json.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal(2, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Debug_generation_records_backend_faults()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("RPC unavailable"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "say hi" });
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Backend_calls_before_generation_record_faults_and_a_success_clears_them()
    {
        var options = new FakeBackendOptions
        {
            CreateContextFailure = new InvalidOperationException("CreateContext RPC unavailable"),
        };
        var fake = new FakeBackend(options);
        await using var host = await BridgeTestHost.StartAsync(fake);

        Assert.Equal(HttpStatusCode.BadGateway, (await PostChatAsync(host, ChatBody.User())).StatusCode);
        host.AssertNoLeak();
        Assert.Equal(HttpStatusCode.BadGateway, (await PostChatAsync(host, ChatBody.User())).StatusCode);
        host.AssertNoLeak();

        var afterCreateFailures = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, afterCreateFailures.StatusCode);
        Assert.Equal("degraded", afterCreateFailures.Body.GetProperty("status").GetString());
        Assert.Equal(2, afterCreateFailures.Body.GetProperty("consecutive_backend_faults").GetInt32());

        options.CreateContextFailure = null;
        options.PreflightFailure = new InvalidOperationException("Preflight RPC unavailable");
        Assert.Equal(HttpStatusCode.BadGateway, (await PostChatAsync(host, ChatBody.User())).StatusCode);
        host.AssertNoLeak();

        var afterPreflightFailure = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, afterPreflightFailure.StatusCode);
        Assert.Equal(3, afterPreflightFailure.Body.GetProperty("consecutive_backend_faults").GetInt32());

        options.PreflightFailure = null;
        Assert.Equal(HttpStatusCode.OK, (await PostChatAsync(host, ChatBody.User())).StatusCode);
        host.AssertNoLeak();

        var recovered = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(0, recovered.Body.GetProperty("consecutive_backend_faults").GetInt32());
    }

    private static Task<HttpResponseMessage> PostChatAsync(BridgeTestHost host, object body) =>
        host.Client.PostAsJsonAsync("/v1/chat/completions", body);

    private static async Task<(HttpStatusCode StatusCode, JsonElement Body)> ReadHealthAsync(BridgeTestHost host)
    {
        var response = await host.Client.GetAsync("/healthz");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }
}
