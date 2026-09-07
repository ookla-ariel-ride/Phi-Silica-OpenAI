using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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

    // ---- failure paths, cancellation, keep-alive ------------------------------------------------

    /// <summary>
    /// The status line is spent once a chunk has gone out, so a backend failure after the first token
    /// has to travel inside the stream. What it must not do is end as <c>stop</c>: before this existed,
    /// every non-completed status was reported to the client as a normal end of message.
    /// </summary>
    [Fact]
    public async Task A_backend_error_after_the_first_token_becomes_an_error_event_then_the_done_marker()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["one ", "two ", "three"],
            FailAfterTokens = 2,
            FailureStatus = GenerationStatus.Error,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // The stream ends, rather than hanging: the error event is the last thing before [DONE].
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        var error = ErrorEvent(body);
        Assert.Equal("server_error", error.GetProperty("type").GetString());
        Assert.Contains("failed to generate", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        // The two deltas that did get generated were streamed first, and nothing claimed a normal finish.
        Assert.Contains("one ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("finish_reason", body, StringComparison.Ordinal);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
    }

    /// <summary>
    /// The event's payload is not merely error-shaped: it is byte-for-byte the body the JSON path
    /// returns for the same failure, because both come from the same mapping.
    /// </summary>
    [Fact]
    public async Task The_error_event_body_is_exactly_the_body_the_json_path_returns()
    {
        var options = () => new FakeBackendOptions
        {
            Responder = _ => ["one ", "two"],
            FailAfterTokens = 1,
            FailureStatus = GenerationStatus.Error,
        };

        await using var streamHost = await BridgeTestHost.StartAsync(new FakeBackend(options()));
        var streamed = ErrorPayload(await (await PostStreamAsync(streamHost)).Content.ReadAsStringAsync());

        await using var jsonHost = await BridgeTestHost.StartAsync(new FakeBackend(options()));
        var json = await jsonHost.Client.PostAsJsonAsync(Path, Body(model: "fake", stream: false));

        Assert.Equal(HttpStatusCode.BadGateway, json.StatusCode);
        Assert.Equal(await json.Content.ReadAsStringAsync(), streamed);
    }

    /// <summary>
    /// A backend that throws rather than returning a status, after the headers are committed. Same
    /// treatment, and the <c>backend_error</c> code the JSON path uses survives into the stream.
    /// </summary>
    [Fact]
    public async Task A_backend_that_throws_mid_stream_becomes_an_error_event_with_the_backend_error_code()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["one ", "two"],
            FailAfterTokens = 1,
            FailureException = new InvalidOperationException("runtime went away"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await (await PostStreamAsync(host)).Content.ReadAsStringAsync();

        var error = ErrorEvent(body);
        Assert.Equal("backend_error", error.GetProperty("code").GetString());
        Assert.Contains("InvalidOperationException", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        Assert.Equal(1, fake.ContextsDisposed);
    }

    /// <summary>
    /// The other side of the boundary. Nothing has been written when the failure is discovered, so the
    /// status line is still the server's to set and the client gets the ordinary 502 — not a 200 stream
    /// carrying an error frame.
    /// </summary>
    [Fact]
    public async Task A_backend_failure_before_the_first_token_is_a_plain_json_502()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["never"],
            FailAfterTokens = 0,
            FailureStatus = GenerationStatus.Error,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);
        Assert.Equal("server_error", JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("type").GetString());

        // The context still existed and was still released.
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
    }

    /// <summary>
    /// The defect this replaces: an over-length prompt used to reach the client as HTTP 200, an empty
    /// reply and <c>finish_reason: "stop"</c> — the model reported as having answered when it refused.
    /// The verdict arrives before a single byte is written, so it is the real 400 the JSON path returns.
    /// </summary>
    [Fact]
    public async Task An_over_length_prompt_is_the_same_400_the_json_path_returns_and_never_a_stop()
    {
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 1 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stop", body, StringComparison.Ordinal);

        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());

        var json = await host.Client.PostAsJsonAsync(Path, Body(model: "fake", stream: false));
        Assert.Equal(await json.Content.ReadAsStringAsync(), body);
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
    }

    /// <summary>
    /// The same condition once the headers are gone: a keep-alive comment has already committed 200, so
    /// the refusal has to travel as an error event. Either way the answer is an error — <c>stop</c> is
    /// not a reachable outcome for a prompt that did not fit.
    /// </summary>
    [Fact]
    public async Task An_over_length_prompt_discovered_after_a_keep_alive_is_an_error_event_not_a_stop()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            MaxPromptChars = 1,
            StartDelay = TimeSpan.FromMilliseconds(300),
        });
        await using var host = await BridgeTestHost.StartAsync(fake, keepAliveInterval: TimeSpan.FromMilliseconds(20));

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(": keep-alive", body, StringComparison.Ordinal);
        Assert.DoesNotContain("finish_reason", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        var error = ErrorEvent(body);
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
    }

    /// <summary>
    /// Content filtering is not a failure and never was: the generation ran, the answer was withheld,
    /// and the client is told so with a finish reason rather than an error event. Same as the JSON path.
    /// </summary>
    [Theory]
    [InlineData(GenerationStatus.ContentFiltered)]
    [InlineData(GenerationStatus.BlockedByPolicy)]
    public async Task Content_filtering_still_ends_as_a_successful_content_filter_finish(GenerationStatus status)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["one ", "two ", "three"],
            FailAfterTokens = 2,
            FailureStatus = status,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);

        var withChoices = Payloads(body)
            .Where(p => !string.Equals(p, "[DONE]", StringComparison.Ordinal))
            .Select(p => JsonDocument.Parse(p).RootElement)
            .Where(c => c.GetProperty("choices").GetArrayLength() > 0)
            .ToList();
        Assert.Equal("content_filter", withChoices[^1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    /// <summary>
    /// While the first token is still being waited for, the response has to show signs of life or a
    /// proxy will close it. The interval is injected in milliseconds here; it is fifteen seconds in
    /// production, and a test that waited that long would not survive its first slow-suite complaint.
    /// </summary>
    [Fact]
    public async Task Keep_alive_comments_fill_the_wait_for_the_first_token_and_stop_once_it_arrives()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["one ", "two"],
            FirstTokenDelay = TimeSpan.FromMilliseconds(300),
        });
        await using var host = await BridgeTestHost.StartAsync(fake, keepAliveInterval: TimeSpan.FromMilliseconds(20));

        var body = await (await PostStreamAsync(host)).Content.ReadAsStringAsync();
        var lines = body.Split('\n');

        Assert.True(lines.Count(l => string.Equals(l, ": keep-alive", StringComparison.Ordinal)) >= 2,
            $"expected repeated keep-alive comments while the first token was delayed, got:\n{body}");

        // Every one of them precedes the first real chunk: they fill the wait and then stop.
        var lastKeepAlive = Array.FindLastIndex(lines, l => string.Equals(l, ": keep-alive", StringComparison.Ordinal));
        var firstFrame = Array.FindIndex(lines, l => l.StartsWith("data: ", StringComparison.Ordinal));
        Assert.True(lastKeepAlive < firstFrame, $"a keep-alive followed the first chunk:\n{body}");

        // And the stream is otherwise exactly the ordinary one.
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        Assert.Equal("one two", string.Concat(Payloads(body)
            .Where(p => !string.Equals(p, "[DONE]", StringComparison.Ordinal))
            .Select(p => JsonDocument.Parse(p).RootElement.GetProperty("choices")[0].GetProperty("delta"))
            .Where(d => d.TryGetProperty("content", out _))
            .Select(d => d.GetProperty("content").GetString())));
    }

    /// <summary>
    /// An empty reply that arrived slowly: keep-alive comments have already started the stream, but they
    /// are comments, not the assistant message. The role chunk still has to open it before the finish
    /// chunk closes it, or a client has a finish_reason for a message it was never told began.
    /// </summary>
    [Fact]
    public async Task An_empty_reply_after_a_keep_alive_still_opens_with_the_role_chunk()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => [],
            StartDelay = TimeSpan.FromMilliseconds(300),
        });
        await using var host = await BridgeTestHost.StartAsync(fake, keepAliveInterval: TimeSpan.FromMilliseconds(20));

        var body = await (await PostStreamAsync(host)).Content.ReadAsStringAsync();
        var chunks = Payloads(body)
            .Where(p => !string.Equals(p, "[DONE]", StringComparison.Ordinal))
            .Select(p => JsonDocument.Parse(p).RootElement)
            .ToList();

        Assert.Contains(": keep-alive", body, StringComparison.Ordinal);
        Assert.Equal(2, chunks.Count);
        Assert.Equal("assistant", chunks[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());
        Assert.Equal("stop", chunks[1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    /// <summary>A first token that arrives before the interval elapses costs the client nothing extra.</summary>
    [Fact]
    public async Task A_prompt_first_token_produces_no_keep_alive_comment()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, keepAliveInterval: TimeSpan.FromSeconds(30));

        var body = await (await PostStreamAsync(host)).Content.ReadAsStringAsync();

        Assert.DoesNotContain(": keep-alive", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The critical one. A client that disappears mid-stream makes the next response write throw, and
    /// that exception used to unwind straight into the <c>finally</c> that disposes the model context —
    /// while the generation was still running against it. On the real backends that is a use-after-free
    /// on a live WinRT handle, not merely an unobserved task.
    ///
    /// The fake here is given a cancellation gate, so its generation keeps running after the client is
    /// gone exactly as a runtime whose operation cannot be stopped on demand would. The handler must
    /// therefore sit in the drain, holding the context alive, until that generation ends.
    /// </summary>
    [Fact]
    public async Task A_client_that_disconnects_mid_stream_is_drained_before_the_context_is_disposed()
    {
        var gate = new TaskCompletionSource();
        FakeBackend fake = null!;
        var disposedDuringGeneration = false;

        IEnumerable<string> Tokens()
        {
            for (var i = 0; i < 200; i++)
            {
                // Read from inside the generation: if the context was released while this was still
                // producing, the handler disposed something the backend was still using.
                disposedDuringGeneration |= fake.ContextsDisposed > 0;
                yield return $"token{i.ToString(CultureInfo.InvariantCulture)} ";
            }
        }

        fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => Tokens(),
            TokenDelay = TimeSpan.FromMilliseconds(10),
            CancellationGate = gate,
        });

        var capture = new CapturingLoggerProvider();
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        try
        {
            using var cts = new CancellationTokenSource();
            using var request = new HttpRequestMessage(HttpMethod.Post, Path)
            {
                Content = JsonContent.Create(Body(model: "fake", stream: true)),
            };

            var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token))
            {
                var buffer = new byte[128];
                Assert.True(await stream.ReadAsync(buffer, cts.Token) > 0);
            }

            // The client goes away mid-generation.
            await cts.CancelAsync();

            // http=0 is the handler saying there is nobody left to write to: it is done with the client
            // and is now in the drain. The generation has not finished, so the context must still exist.
            await WaitUntilAsync(() => capture.Records.Any(r => r.Message.Contains("http=0", StringComparison.Ordinal)));
            Assert.Equal(1, fake.ContextsCreated);
            Assert.Equal(0, fake.ContextsDisposed);
        }
        finally
        {
            gate.SetResult();
        }

        // Only now, once the generation can end, is the context released.
        await WaitUntilAsync(() => fake.ActiveContexts == 0);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.False(disposedDuringGeneration, "the context was disposed while the backend was still generating");

        // Nothing escaped as an unhandled request exception.
        Assert.DoesNotContain(capture.Records, r => r.Level >= LogLevel.Error);
    }

    /// <summary>
    /// The disconnect case for a generation that does stop when told to: the context balances, and the
    /// per-request line reports http=0 rather than a status nobody received.
    /// </summary>
    [Fact]
    public async Task A_disconnected_client_is_logged_as_http_0_and_leaks_no_context()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => LongReply,
            TokenDelay = TimeSpan.FromMilliseconds(10),
        });
        var capture = new CapturingLoggerProvider();
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(Body(model: "fake", stream: true)),
        };

        var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token))
        {
            var buffer = new byte[128];
            Assert.True(await stream.ReadAsync(buffer, cts.Token) > 0);
        }

        await cts.CancelAsync();

        await WaitUntilAsync(() => fake.ActiveContexts == 0);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);

        var line = Assert.Single(capture.Records, r => r.Message.StartsWith("req=chatcmpl-", StringComparison.Ordinal));
        Assert.Contains("http=0", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(capture.Records, r => r.Level >= LogLevel.Error);
    }

    /// <summary>Polls until the condition holds, or fails the test rather than hanging the suite.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? description = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {description}");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The one error event in the body, checked to be the last frame before the done marker: an error
    /// event that left the stream hanging, or that was followed by more chunks, fails here.
    /// </summary>
    private static string ErrorPayload(string body)
    {
        var payloads = Payloads(body);
        Assert.Equal("[DONE]", payloads[^1]);
        var frame = Assert.Single(payloads, p => p.Contains("\"error\"", StringComparison.Ordinal));
        Assert.Equal(payloads[^2], frame);
        return frame;
    }

    private static JsonElement ErrorEvent(string body) =>
        JsonDocument.Parse(ErrorPayload(body)).RootElement.GetProperty("error");

    /// <summary>The payload of every <c>data:</c> frame, in wire order, keep-alive comments excluded.</summary>
    private static List<string> Payloads(string body) =>
        body.Split('\n')
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => l["data: ".Length..])
            .ToList();

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
