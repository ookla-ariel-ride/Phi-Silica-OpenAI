using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;

namespace NpuBridge.Tests;

/// <summary>
/// The server-sent-event shape of <c>POST /v1/chat/completions</c>, asserted on the raw wire text
/// rather than on a deserialized convenience view: the framing (<c>data: </c> prefix, blank-line
/// separator, the literal <c>[DONE]</c> terminator) is as much of the contract as the JSON inside it,
/// and a typed reader would hide a broken frame.
///
/// The fake backend raises its progress callback on a thread-pool thread by default, which is the
/// point: if the endpoint wrote to the HTTP response from that callback instead of handing deltas
/// through a channel, a reply long enough to span many deltas would come back interleaved or short,
/// and <see cref="Concatenated_deltas_reproduce_the_generated_text_exactly"/> would fail.
/// </summary>
public class ChatCompletionsStreamingTests
{
    private const string Path = "/v1/chat/completions";

    /// <summary>Long enough to span many deltas and to make an ordering or interleaving bug visible.</summary>
    private static IReadOnlyList<string> LongReply { get; } = FakeBackend.Tokenize(string.Join(
        ' ',
        Enumerable.Range(0, 200).Select(i => $"token{i.ToString(CultureInfo.InvariantCulture)}")));

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
    public async Task The_first_chunk_opens_the_assistant_message_and_every_chunk_is_a_completion_chunk()
    {
        await using var host = await BridgeTestHost.StartAsync(new FakeBackend(new FakeBackendOptions { Responder = _ => ["Hello", " world"] }));

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        var first = chunks[0];
        var delta = first.GetProperty("choices")[0].GetProperty("delta");
        Assert.Equal("assistant", delta.GetProperty("role").GetString());
        Assert.Equal(string.Empty, delta.GetProperty("content").GetString());

        Assert.All(chunks, c => Assert.Equal("chat.completion.chunk", c.GetProperty("object").GetString()));
    }

    [Fact]
    public async Task Concatenated_deltas_reproduce_the_generated_text_exactly()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => LongReply });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        // Every content delta, in wire order, minus the empty one the role chunk carries.
        var deltas = chunks
            .Where(c => c.GetProperty("choices").GetArrayLength() > 0)
            .Select(c => c.GetProperty("choices")[0].GetProperty("delta"))
            .Where(d => d.TryGetProperty("content", out _))
            .Select(d => d.GetProperty("content").GetString()!)
            .ToList();

        Assert.Equal(string.Concat(LongReply), string.Concat(deltas));

        // Nothing lost and nothing duplicated: one content chunk per delta, plus the role chunk's empty
        // one. Asserted on the count as well as the concatenation, because a dropped delta and a
        // duplicated neighbour would cancel out in the text alone.
        Assert.Equal(LongReply.Count + 1, deltas.Count);
        Assert.Equal(LongReply, deltas.Skip(1));
    }

    [Fact]
    public async Task Id_created_and_model_are_identical_across_every_chunk()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => LongReply });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, model: "my-model", includeUsage: true));

        var id = chunks[0].GetProperty("id").GetString();
        var created = chunks[0].GetProperty("created").GetInt64();

        Assert.StartsWith("chatcmpl-", id, StringComparison.Ordinal);
        Assert.True(created > 0);
        Assert.All(chunks, c =>
        {
            Assert.Equal(id, c.GetProperty("id").GetString());
            Assert.Equal(created, c.GetProperty("created").GetInt64());
            Assert.Equal("my-model", c.GetProperty("model").GetString());
        });
    }

    [Fact]
    public async Task Only_the_last_choices_chunk_carries_a_finish_reason()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["a", "b", "c"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        var withChoices = chunks.Where(c => c.GetProperty("choices").GetArrayLength() > 0).ToList();
        var last = withChoices[^1];

        Assert.Equal("stop", last.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.All(withChoices.Take(withChoices.Count - 1), c =>
            Assert.False(c.GetProperty("choices")[0].TryGetProperty("finish_reason", out _)));
    }

    [Fact]
    public async Task The_body_ends_with_the_done_sentinel()
    {
        await using var host = await BridgeTestHost.StartAsync(new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] }));

        var body = await (await PostStreamAsync(host)).Content.ReadAsStringAsync();

        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        Assert.Single(Lines(body), l => string.Equals(l, "data: [DONE]", StringComparison.Ordinal));
    }

    /// <summary>
    /// Same prompt, same generation, both response shapes: the usage chunk must report exactly the
    /// numbers the JSON reply reports, or a client that adds up either one gets a different answer for
    /// the same work.
    /// </summary>
    [Fact]
    public async Task Include_usage_appends_one_usage_chunk_matching_the_json_paths_estimate()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["abcde"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, includeUsage: true));

        var usageChunks = chunks.Where(c => c.TryGetProperty("usage", out _)).ToList();
        var usageChunk = Assert.Single(usageChunks);

        // Exactly one, it is the last chunk before [DONE], and it carries no choices.
        Assert.True(chunks[^1].TryGetProperty("usage", out _));
        Assert.Equal(0, usageChunk.GetProperty("choices").GetArrayLength());

        var json = await host.Client.PostAsJsonAsync(Path, Body(model: "fake", stream: false));
        var jsonUsage = JsonDocument.Parse(await json.Content.ReadAsStringAsync()).RootElement.GetProperty("usage");

        var usage = usageChunk.GetProperty("usage");
        Assert.Equal(jsonUsage.GetProperty("prompt_tokens").GetInt32(), usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(jsonUsage.GetProperty("completion_tokens").GetInt32(), usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(jsonUsage.GetProperty("total_tokens").GetInt32(), usage.GetProperty("total_tokens").GetInt32());

        // Pinned against the documented chars/4 estimate too, so a change on both paths at once still
        // has to be deliberate. Prompt "say hi" is 6 chars -> 2; output "abcde" is 5 chars -> 2.
        Assert.Equal(2, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(2, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(4, usage.GetProperty("total_tokens").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Without_include_usage_no_chunk_carries_usage(bool? includeUsage)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["abcde"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, includeUsage: includeUsage));

        Assert.DoesNotContain(chunks, c => c.TryGetProperty("usage", out _));
        Assert.All(chunks, c => Assert.Equal(1, c.GetProperty("choices").GetArrayLength()));
    }

    [Fact]
    public async Task A_streamed_request_creates_exactly_one_context_and_disposes_it()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => LongReply });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await PostStreamAsync(host);

        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Fact]
    public async Task The_stream_emits_the_same_per_request_log_line_the_json_path_does()
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["abcde"] });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        await PostStreamAsync(host);

        var line = Assert.Single(capture.Records, r => r.Message.StartsWith("req=chatcmpl-", StringComparison.Ordinal));
        Assert.Contains("backend=fake", line.Message, StringComparison.Ordinal);
        Assert.Contains("prompt_chars=6", line.Message, StringComparison.Ordinal);
        Assert.Contains("tokens=2", line.Message, StringComparison.Ordinal);
        Assert.Contains("status=Complete", line.Message, StringComparison.Ordinal);
        Assert.Contains("finish=stop", line.Message, StringComparison.Ordinal);
        Assert.Contains("http=200", line.Message, StringComparison.Ordinal);

        // The category is chosen on purpose by the streaming phase rather than inherited from whatever
        // logger the shared preparation phase happened to be handed.
        Assert.Equal("NpuBridge.Api.ChatCompletionsStreamEndpoint", line.Category);
    }

    /// <summary>
    /// Preparation fails before a single byte is written, so the status line is still the server's to
    /// set: a streamed request that never reaches generation gets the ordinary JSON error with the
    /// ordinary status, not a 200 stream carrying an error frame.
    /// </summary>
    [Fact]
    public async Task A_validation_failure_with_stream_true_is_a_plain_json_400()
    {
        var fake = new FakeBackend();
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream = true,
            n = 2,
            messages = new[] { new { role = "user", content = "say hi" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);

        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("n", error.GetProperty("param").GetString());
        Assert.Equal(0, fake.ContextsCreated);
    }

    [Fact]
    public async Task A_not_ready_backend_with_stream_true_is_a_plain_json_503()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeBackend(new FakeBackendOptions { InitGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake, waitForReady: false);

        var response = await host.Client.PostAsJsonAsync(Path, Body(model: "fake", stream: true));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);
        Assert.Equal("server_error", JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(0, fake.ContextsCreated);

        gate.SetResult();
        await host.Lifecycle.Initialization;
    }

    /// <summary>
    /// The system-prompt placement decision is preparation's, so it must reach the backend unchanged on
    /// the streaming path: the same rendering assertion <see cref="ChatRequestPreparationTests"/> makes
    /// for the JSON path.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_streaming_path_honours_the_system_prompt_placement(bool nativeSystem)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = nativeSystem ? BackendCapabilities.SystemPromptContext : BackendCapabilities.None,
            Responder = _ => ["ok"],
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream = true,
            messages = new object[]
            {
                new { role = "system", content = "be terse" },
                new { role = "user", content = "hi" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(fake.Calls);
        if (nativeSystem)
        {
            Assert.Contains("be terse", call.SystemPrompt!, StringComparison.Ordinal);
            Assert.DoesNotContain("be terse", call.Prompt, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(call.SystemPrompt);
            Assert.StartsWith("be terse", call.Prompt, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every frame is <c>data: &lt;payload&gt;</c> followed by a blank line, and nothing else appears
    /// between them. A stray write -- for example one made from the callback thread while the request's
    /// own task was mid-frame -- shows up here as a line that is neither.
    /// </summary>
    [Fact]
    public async Task Every_line_in_the_body_is_a_data_frame_or_a_blank_separator()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => LongReply });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await (await PostStreamAsync(host, includeUsage: true)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("\r", body, StringComparison.Ordinal);
        var raw = body.Split('\n');

        // "data: x", "", "data: y", "", ..., "" -- pairs, so an odd number of pieces after the split.
        Assert.Equal(1, raw.Length % 2);
        for (var i = 0; i < raw.Length - 1; i += 2)
        {
            Assert.StartsWith("data: ", raw[i], StringComparison.Ordinal);
            Assert.Equal(string.Empty, raw[i + 1]);
        }

        Assert.Equal(string.Empty, raw[^1]);
    }

    private static object Body(string? model, bool? stream, bool? includeUsage = null) => new
    {
        model,
        stream,
        stream_options = includeUsage is null ? null : new { include_usage = includeUsage },
        messages = new[] { new { role = "user", content = "say hi" } },
    };

    private static Task<HttpResponseMessage> PostStreamAsync(BridgeTestHost host, string model = "fake", bool? includeUsage = null) =>
        host.Client.PostAsJsonAsync(Path, Body(model, stream: true, includeUsage));

    /// <summary>Parses the raw SSE body into the JSON payload of every <c>data:</c> frame but <c>[DONE]</c>.</summary>
    private static async Task<List<JsonElement>> ReadChunksAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        var chunks = new List<JsonElement>();
        foreach (var line in Lines(body))
        {
            Assert.StartsWith("data: ", line, StringComparison.Ordinal);
            var payload = line["data: ".Length..];
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            chunks.Add(JsonDocument.Parse(payload).RootElement);
        }

        Assert.NotEmpty(chunks);
        return chunks;
    }

    private static IEnumerable<string> Lines(string body) =>
        body.Split('\n').Where(l => l.Length > 0);
}
