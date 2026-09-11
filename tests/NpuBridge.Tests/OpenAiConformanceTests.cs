using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Api;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;

namespace NpuBridge.Tests;

/// <summary>
/// The wire shapes against OpenAI's published schema (D77): fields the schema marks required but
/// nullable are written as explicit nulls, request constraints the schema states are enforced, and
/// the error envelope always carries all four keys. Each test names the schema rule it pins.
/// </summary>
public class OpenAiConformanceTests
{
    private const string Path = "/v1/chat/completions";

    private static object User(string text) => new { role = "user", content = text };

    [Fact]
    public async Task A_chat_completion_carries_logprobs_and_refusal_as_explicit_nulls()
    {
        // CreateChatCompletionResponse.choices[] requires logprobs; ChatCompletionResponseMessage
        // requires refusal. Both nullable, both present.
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["hi"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await (await host.Client.PostAsJsonAsync(Path, ChatBody.User("x"))).Content.ReadAsStringAsync();
        var choice = JsonDocument.Parse(body).RootElement.GetProperty("choices")[0];

        Assert.Equal(JsonValueKind.Null, choice.GetProperty("logprobs").ValueKind);
        Assert.Equal(JsonValueKind.Null, choice.GetProperty("message").GetProperty("refusal").ValueKind);
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("hi", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("chat.completion", JsonDocument.Parse(body).RootElement.GetProperty("object").GetString());
    }

    [Fact]
    public async Task Every_streamed_choice_carries_finish_reason_and_logprobs_keys()
    {
        // CreateChatCompletionStreamResponse.choices[] requires delta, finish_reason and index;
        // logprobs is written as null on every chunk as OpenAI's streams do.
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["a", "b"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await (await host.Client.PostAsJsonAsync(Path, new { model = "fake", stream = true, messages = new[] { User("x") } })).Content.ReadAsStringAsync();
        var chunks = Sse.Chunks(body).Where(c => c.GetProperty("choices").GetArrayLength() > 0).ToList();

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
        {
            var choice = c.GetProperty("choices")[0];
            Assert.True(choice.TryGetProperty("finish_reason", out _));
            Assert.Equal(JsonValueKind.Null, choice.GetProperty("logprobs").ValueKind);
            Assert.Equal(0, choice.GetProperty("index").GetInt32());
            Assert.Equal("chat.completion.chunk", c.GetProperty("object").GetString());
        });
        Assert.Equal("stop", chunks[^1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());

        // Without stream_options.include_usage no chunk carries a usage key at all.
        Assert.All(Sse.Chunks(body), c => Assert.False(c.TryGetProperty("usage", out _)));
    }

    [Theory]
    [InlineData("stream_options", false)]
    [InlineData("stream_options", null)]
    public async Task Stream_options_without_stream_is_a_400(string param, bool? stream)
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream,
            stream_options = new { include_usage = true },
            messages = new[] { User("x") },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(param, (await Error(response)).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData("temperature", 2.5)]
    [InlineData("temperature", -0.1)]
    [InlineData("top_p", 1.5)]
    [InlineData("top_p", -1)]
    public async Task Sampling_values_outside_the_schema_ranges_are_a_400(string param, double value)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["never"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = param == "temperature"
            ? (object)new { model = "fake", temperature = value, messages = new[] { User("x") } }
            : new { model = "fake", top_p = value, messages = new[] { User("x") } };
        var response = await host.Client.PostAsJsonAsync(Path, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(param, (await Error(response)).GetProperty("param").GetString());
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(2.0, 0.0)]
    public async Task Sampling_values_on_the_schema_boundaries_are_accepted(double temperature, double topP)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", temperature, top_p = topP, messages = new[] { User("x") } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task N_below_one_is_a_400()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", n = 0, messages = new[] { User("x") } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("n", (await Error(response)).GetProperty("param").GetString());
    }

    [Fact]
    public async Task Every_error_envelope_carries_all_four_keys()
    {
        // Error requires type, message, param and code; the latter two nullable. A backend fault has
        // a code and no param; a validation failure has a param and no code; both keys still appear.
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["a"], FailAfterTokens = 0, FailureException = new InvalidOperationException("boom") });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var fault = await Error(await host.Client.PostAsJsonAsync(Path, ChatBody.User("x")));
        Assert.Equal("server_error", fault.GetProperty("type").GetString());
        Assert.Equal("backend_error", fault.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, fault.GetProperty("param").ValueKind);

        var invalid = await Error(await host.Client.PostAsJsonAsync(Path, new { model = "fake", messages = Array.Empty<object>() }));
        Assert.Equal("invalid_request_error", invalid.GetProperty("type").GetString());
        Assert.Equal("messages", invalid.GetProperty("param").GetString());
        Assert.Equal(JsonValueKind.Null, invalid.GetProperty("code").ValueKind);

        // The same envelope inside a stream, after a token has gone out.
        fake.Options.FailAfterTokens = 1;
        fake.Options.Responder = _ => ["a", "b"];
        var stream = await (await host.Client.PostAsJsonAsync(Path, new { model = "fake", stream = true, messages = new[] { User("x") } })).Content.ReadAsStringAsync();
        var frame = Sse.Chunks(stream).Single(c => c.TryGetProperty("error", out _)).GetProperty("error");
        Assert.True(frame.TryGetProperty("param", out _));
        Assert.True(frame.TryGetProperty("code", out _));
        Assert.True(frame.TryGetProperty("message", out _));
        Assert.True(frame.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task The_model_list_and_model_object_have_the_required_keys()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var list = JsonDocument.Parse(await (await host.Client.GetAsync("/v1/models")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("list", list.GetProperty("object").GetString());
        var model = Assert.Single(list.GetProperty("data").EnumerateArray().ToArray());
        foreach (var key in new[] { "id", "object", "created", "owned_by" })
        {
            Assert.True(model.TryGetProperty(key, out _), key);
        }

        Assert.Equal("model", model.GetProperty("object").GetString());
        Assert.Equal("fake", model.GetProperty("id").GetString());
    }

    [Fact]
    public async Task An_unknown_model_is_a_404_even_while_the_backend_is_still_loading()
    {
        // The model check runs before readiness: a loading server answers 404 for the wrong id, and
        // param is null, as OpenAI sends it. Both envelopes (this and GET /v1/models/{id}) agree.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { InitGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake, waitForReady: false);

        var post = await host.Client.PostAsJsonAsync(Path, new { model = "gpt-4o", messages = new[] { User("x") } });
        var get = await host.Client.GetAsync("/v1/models/gpt-4o");

        // A valid id while loading is still the 503; checked before the gate opens.
        var loading = await host.Client.PostAsJsonAsync(Path, ChatBody.User("x"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, loading.StatusCode);
        gate.SetResult();

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        var postError = await Error(post);
        var getError = await Error(get);
        Assert.Equal("model_not_found", postError.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, postError.GetProperty("param").ValueKind);
        Assert.Equal(JsonValueKind.Null, getError.GetProperty("param").ValueKind);
        Assert.Equal(postError.GetProperty("message").GetString(), getError.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(float.NaN, null)]
    [InlineData(float.PositiveInfinity, null)]
    [InlineData(null, float.NaN)]
    [InlineData(null, float.NegativeInfinity)]
    public void Non_finite_sampling_values_fail_validation(float? temperature, float? topP)
    {
        // JSON cannot carry these, but the DTO can be built directly; the validator rejects them.
        var request = new ChatCompletionRequest("fake",
            [new ChatMessage("user", ChatMessageContent.FromText("x"), null, null)],
            null, null, temperature, topP, null, null, null, null, null, null, null, null, null, null, null, null);

        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal(temperature is null ? "top_p" : "temperature", result.Failure!.Param);
    }

    [Fact]
    public async Task With_include_usage_a_reply_with_no_deltas_still_carries_usage_null_before_the_usage_chunk()
    {
        // No delta at all: the role chunk and the finish chunk are written after the generation ended.
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => [] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await (await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream = true,
            stream_options = new { include_usage = true },
            messages = new[] { User("x") },
        })).Content.ReadAsStringAsync();
        var chunks = Sse.Chunks(body);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("usage").ValueKind));
        Assert.Equal(JsonValueKind.Object, chunks[^1].GetProperty("usage").ValueKind);
        Assert.Equal(0, chunks[^1].GetProperty("choices").GetArrayLength());
    }

    [Fact]
    public async Task With_include_usage_a_stream_that_fails_carries_usage_null_on_its_chunks_and_a_plain_error_frame()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["a", "b"], FailAfterTokens = 1, FailureStatus = GenerationStatus.Error });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await (await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream = true,
            stream_options = new { include_usage = true },
            messages = new[] { User("x") },
        })).Content.ReadAsStringAsync();
        var chunks = Sse.Chunks(body);

        var frames = chunks.Where(c => c.TryGetProperty("choices", out _)).ToList();
        Assert.NotEmpty(frames);
        Assert.All(frames, c => Assert.Equal(JsonValueKind.Null, c.GetProperty("usage").ValueKind));
        var error = Assert.Single(chunks, c => c.TryGetProperty("error", out _));
        Assert.False(error.TryGetProperty("usage", out _));
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> Error(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").Clone();
    }
}
