using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Backends.Fake;

namespace NpuBridge.Tests;

/// <summary>
/// The server-sent-event shape of <c>POST /v1/completions</c>: the <c>text_completion</c> counterpart
/// of <see cref="ChatCompletionsStreamingTests"/>. Every scheduler-wiring, keep-alive, cancel-drain-
/// dispose and D77-null rule that file pins is the exact same code running here (chunk 8 task 3), so
/// this file is about what is actually different -- there is no role chunk (a text completion has no
/// role) and every chunk's <c>object</c> is <c>text_completion</c>, the same string the non-streamed
/// reply carries, unlike the chat shape's separate <c>chat.completion.chunk</c>.
/// </summary>
public class CompletionsStreamingTests
{
    private const string Path = "/v1/completions";

    [Fact]
    public async Task The_response_carries_the_three_streaming_headers()
    {
        await using var host = await BridgeTestHost.StartAsync(new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] }));

        var response = await PostStreamAsync(host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
    }

    [Fact]
    public async Task Every_chunk_is_a_text_completion_object_and_concatenated_deltas_reproduce_the_text()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["Hello", " world"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        Assert.All(chunks, c => Assert.Equal("text_completion", c.GetProperty("object").GetString()));

        var texts = chunks
            .Select(c => c.GetProperty("choices")[0].GetProperty("text").GetString()!)
            .ToList();
        Assert.Equal("Hello world", string.Concat(texts));
    }

    [Fact]
    public async Task Id_created_and_model_are_identical_across_every_chunk()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["a", "b", "c"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        Assert.Single(chunks.Select(c => c.GetProperty("id").GetString()).Distinct());
        Assert.Single(chunks.Select(c => c.GetProperty("created").GetInt64()).Distinct());
        Assert.Single(chunks.Select(c => c.GetProperty("model").GetString()).Distinct());
    }

    [Fact]
    public async Task The_last_chunk_carries_the_finish_reason_and_logprobs_is_an_explicit_null()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["hi"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        var finishChunks = chunks.Where(c => c.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind != JsonValueKind.Null).ToList();
        var last = Assert.Single(finishChunks);
        Assert.Equal("stop", last.GetProperty("choices")[0].GetProperty("finish_reason").GetString());

        // D77: every chunk's logprobs is a written null, on every choice, not only the last.
        Assert.All(chunks, c => Assert.Equal(JsonValueKind.Null, c.GetProperty("choices")[0].GetProperty("logprobs").ValueKind));
    }

    [Fact]
    public async Task Include_usage_adds_one_trailing_chunk_with_empty_choices_and_null_usage_before_it()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["abcd"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, includeUsage: true));

        var usageChunk = Assert.Single(chunks, c => c.GetProperty("choices").GetArrayLength() == 0);
        Assert.True(usageChunk.GetProperty("usage").GetProperty("completion_tokens").GetInt32() > 0);

        foreach (var chunk in chunks.Where(c => c.GetProperty("choices").GetArrayLength() > 0))
        {
            Assert.Equal(JsonValueKind.Null, chunk.GetProperty("usage").ValueKind);
        }
    }

    [Fact]
    public async Task Max_tokens_cuts_the_stream_exactly_as_it_does_on_the_chat_shape()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["0123", "4567", "89ab"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, maxTokens: 2));

        var text = string.Concat(chunks.Select(c => c.GetProperty("choices")[0].GetProperty("text").GetString()));
        Assert.Equal("01234567", text);

        var finish = chunks.Select(c => c.GetProperty("choices")[0].GetProperty("finish_reason"))
            .Single(f => f.ValueKind != JsonValueKind.Null);
        Assert.Equal("length", finish.GetString());
    }

    [Fact]
    public async Task A_stop_string_truncates_the_stream_and_is_excluded_from_the_output()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["hello", " world", " END", " more"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, stop: "END"));

        var text = string.Concat(chunks.Select(c => c.GetProperty("choices")[0].GetProperty("text").GetString()));
        Assert.Equal("hello world ", text);

        var finish = chunks.Select(c => c.GetProperty("choices")[0].GetProperty("finish_reason"))
            .Single(f => f.ValueKind != JsonValueKind.Null);
        Assert.Equal("stop", finish.GetString());
    }

    [Fact]
    public async Task The_stream_leaves_no_context_leak()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["hi"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await PostStreamAsync(host);

        host.AssertNoLeak();
        Assert.Equal(1, host.Cache.Count);
    }

    private static object Body(string? model, bool? stream, bool? includeUsage, int? maxTokens, string? stop) => new
    {
        model,
        stream,
        stream_options = includeUsage is null ? null : new { include_usage = includeUsage },
        prompt = "say hi",
        max_tokens = maxTokens,
        stop,
    };

    private static Task<HttpResponseMessage> PostStreamAsync(
        BridgeTestHost host, string model = "fake", bool? includeUsage = null, int? maxTokens = null, string? stop = null) =>
        host.Client.PostAsJsonAsync(Path, Body(model, stream: true, includeUsage, maxTokens, stop));

    /// <summary>Parses the raw SSE body into the JSON payload of every <c>data:</c> frame but <c>[DONE]</c>.</summary>
    private static async Task<List<JsonElement>> ReadChunksAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        var chunks = Sse.Chunks(body);
        Assert.NotEmpty(chunks);
        return chunks;
    }
}
