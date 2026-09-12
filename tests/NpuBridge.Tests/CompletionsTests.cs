using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Backends.Fake;

namespace NpuBridge.Tests;

/// <summary>
/// <c>POST /v1/completions</c> (chunk 8 task 3, PLAN §2.2): the legacy <c>text_completion</c> shape.
/// <c>prompt</c> is wrapped into one user message and run through the identical pipeline
/// <c>/v1/chat/completions</c> uses from the model-id check onward — <see cref="ChatRequestPreparationTests"/>
/// and <see cref="ChatCompletionsTests"/> already pin that shared pipeline exhaustively, so this file
/// is about what is actually different: the wire shape (<c>prompt</c> in, <c>text_completion</c> out)
/// and the one controller ruling unique to this endpoint (a multi-element <c>prompt</c> array is a
/// 400).
/// </summary>
public class CompletionsTests
{
    private const string Path = "/v1/completions";

    [Fact]
    public async Task Minimal_request_returns_a_text_completion()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["Hello", " world"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", prompt = "say hi" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await ReadJson(response);
        Assert.Equal("text_completion", root.GetProperty("object").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("id").GetString()));
        Assert.Equal("fake", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("created").GetInt64() > 0);

        var choice = Assert.Single(root.GetProperty("choices").EnumerateArray().ToArray());
        Assert.Equal(0, choice.GetProperty("index").GetInt32());
        Assert.Equal("Hello world", choice.GetProperty("text").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());

        // D77: required-but-nullable fields are explicit nulls, not omitted.
        Assert.Equal(JsonValueKind.Null, choice.GetProperty("logprobs").ValueKind);

        var usage = root.GetProperty("usage");
        Assert.True(usage.GetProperty("prompt_tokens").GetInt32() > 0);
        Assert.True(usage.GetProperty("completion_tokens").GetInt32() > 0);
        Assert.Equal(
            usage.GetProperty("prompt_tokens").GetInt32() + usage.GetProperty("completion_tokens").GetInt32(),
            usage.GetProperty("total_tokens").GetInt32());

        host.AssertNoLeak();
    }

    [Fact]
    public async Task A_single_element_array_prompt_is_accepted_exactly_like_a_bare_string()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", prompt = new[] { "say hi" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var choice = (await ReadJson(response)).GetProperty("choices")[0];
        Assert.Equal("ok", choice.GetProperty("text").GetString());
        host.AssertNoLeak();
    }

    /// <summary>
    /// Controller ruling: real OpenAI batches several prompts into several choices, which this
    /// single-worker bridge cannot serve. Refusing is honest; silently taking the first element would
    /// let a client believe every prompt it sent was answered.
    /// </summary>
    [Fact]
    public async Task A_prompt_array_with_more_than_one_element_is_a_400()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["x"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", prompt = new[] { "one", "two" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("prompt", error.GetProperty("param").GetString());
        Assert.Empty(fake.Calls);
        Assert.Equal(0, fake.ContextsCreated);
    }

    [Fact]
    public async Task A_missing_prompt_is_a_400_naming_the_parameter()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["x"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("prompt", error.GetProperty("param").GetString());
        Assert.Empty(fake.Calls);
    }

    /// <summary>
    /// Every OpenAI error body carries all four keys, <c>param</c>/<c>code</c> included when null,
    /// exactly as the chat shape's does (D77) — the two shapes share <see cref="NpuBridge.Api.OpenAiError"/>,
    /// so this is really a check that <c>/v1/completions</c>'s own validation (the prompt-array
    /// ruling) reaches it too rather than hand-building a different envelope.
    /// </summary>
    [Fact]
    public async Task An_error_body_always_carries_all_four_keys()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["x"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", prompt = new[] { "one", "two" } });
        var error = (await ReadJson(response)).GetProperty("error");

        Assert.True(error.TryGetProperty("message", out _));
        Assert.True(error.TryGetProperty("type", out _));
        Assert.True(error.TryGetProperty("param", out _));
        Assert.True(error.TryGetProperty("code", out _));
    }

    [Fact]
    public async Task An_unknown_model_is_a_404_model_not_found()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["x"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "gpt-4o", prompt = "hi" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("model_not_found", error.GetProperty("code").GetString());
        Assert.Empty(fake.Calls);
    }

    /// <summary><c>n</c> greater than 1 is rejected on this endpoint exactly as it is on the chat shape, via the shared validator.</summary>
    [Fact]
    public async Task N_greater_than_one_is_a_400()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["x"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", prompt = "hi", n = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("n", (await ReadJson(response)).GetProperty("error").GetProperty("param").GetString());
    }

    [Fact]
    public async Task Max_tokens_cuts_the_reply_exactly_as_it_does_on_the_chat_shape()
    {
        // "0123" is 1 token under chars/4 four times over -- a budget of 2 tokens caps at 8 characters.
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["0123", "4567", "89ab"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", prompt = "hi", max_tokens = 2 });

        var root = await ReadJson(response);
        var choice = root.GetProperty("choices")[0];
        Assert.Equal("01234567", choice.GetProperty("text").GetString());
        Assert.Equal("length", choice.GetProperty("finish_reason").GetString());
        Assert.Equal(2, root.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
    }

    [Fact]
    public async Task A_stop_string_truncates_the_reply_and_is_excluded_from_the_output()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["hello", " world", " END", " more"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", prompt = "hi", stop = "END" });

        var choice = (await ReadJson(response)).GetProperty("choices")[0];
        Assert.Equal("hello world ", choice.GetProperty("text").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
    }

    /// <summary>A wrong method on a known <c>/v1</c> path is a 405 naming the allowed method, not a bare 404.</summary>
    [Fact]
    public async Task A_get_to_v1_completions_is_405_not_404()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("POST", string.Join(",", response.Content.Headers.Allow));
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("method_not_allowed", error.GetProperty("code").GetString());
    }

    /// <summary>
    /// A successful generation goes back into the same <see cref="NpuBridge.Api.ContextCache"/> the chat
    /// shape uses -- one more proof that <c>/v1/completions</c> is not a parallel path, and
    /// <see cref="BridgeTestHost.AssertNoLeak"/> is the same invariant every other endpoint is held to.
    /// A repeat of the identical prompt cannot itself land a cache <em>hit</em> through this wire shape:
    /// <c>prompt</c> has no way to echo the model's own reply back as history, so every request is a
    /// fresh one-turn transcript and <see cref="NpuBridge.Api.ConversationKey.PrefixKeys"/> is empty for
    /// one turn by design (<see cref="ConversationKeyTests"/> pins that directly) -- there is nothing
    /// shorter for a second identical request to extend.
    /// </summary>
    [Fact]
    public async Task A_completed_context_is_stored_in_the_cache_rather_than_leaked()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await host.Client.PostAsJsonAsync(Path, new { model = "fake", prompt = "say hi" });

        Assert.Equal(1, host.Cache.Count);
        host.AssertNoLeak();
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
