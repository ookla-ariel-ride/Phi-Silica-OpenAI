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
/// The context cache as <c>/v1/chat/completions</c> uses it (chunk 5): a continuing conversation
/// sends only its newest turns, on the context that already holds the rest. Every test counts what
/// the fake backend saw — which context, what prompt, what history — because "it hit" is a claim
/// about the backend's inputs, not about a counter.
/// </summary>
public class ContextCacheEndpointTests
{
    private const string Path = "/v1/chat/completions";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task The_second_turn_of_a_conversation_reuses_the_first_turns_context_and_sends_only_the_tail(bool firstStreams, bool secondStreams)
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["Paris", " is", " the", " capital."] });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        var reply = await AskAsync(host, firstStreams, Msg("system", "Be brief."), Msg("user", "Capital of France?"));
        Assert.Equal("Paris is the capital.", reply);
        Assert.Equal(1, host.Cache.Count);

        var second = await AskAsync(host, secondStreams,
            Msg("system", "Be brief."), Msg("user", "Capital of France?"), Msg("assistant", reply), Msg("user", "And Italy?"));
        Assert.Equal("Paris is the capital.", second);

        Assert.Equal(2, fake.Calls.Count);
        var first = fake.Calls[0];
        var hit = fake.Calls[1];

        // Same context, and the backend already held the first exchange when the second prompt arrived.
        Assert.Equal(first.ContextId, hit.ContextId);
        var turn = Assert.Single(hit.History);
        Assert.Equal(first.Prompt, turn.Prompt);
        Assert.Equal("Paris is the capital.", turn.Response);

        // Only the tail was rendered, in the marker format, with no system text and no history.
        var user = new ChatMessage("user", ChatMessageContent.FromText("And Italy?"), null, null);
        Assert.Equal(PromptTemplate.RenderTail([user]), hit.Prompt);
        Assert.DoesNotContain("Capital of France?", hit.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Be brief.", hit.Prompt, StringComparison.Ordinal);

        // One context ever created; it is back in the cache under the longer transcript's key.
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, host.Cache.Count);
        host.AssertNoLeak();

        var lines = capture.Records.Where(r => r.Message.Contains("cache=", StringComparison.Ordinal)).Select(r => r.Message).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains("cache=miss tail_turns=1 truncated_turns=0", lines[0], StringComparison.Ordinal);
        Assert.Contains("cache=hit tail_turns=1 truncated_turns=0", lines[1], StringComparison.Ordinal);
        Assert.Contains($"prompt_chars={hit.Prompt.Length} ", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_third_turn_extends_the_context_again_so_the_conversation_never_replays()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = r => ["reply-", r.History.Count.ToString()] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var messages = new List<object> { Msg("user", "one") };
        for (var i = 0; i < 3; i++)
        {
            var reply = await AskAsync(host, stream: i % 2 == 1, messages.ToArray());
            Assert.Equal($"reply-{i}", reply);
            messages.Add(Msg("assistant", reply));
            messages.Add(Msg("user", $"turn {i + 2}"));
        }

        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(3, fake.Calls.Count);
        Assert.All(fake.Calls, c => Assert.Equal(fake.Calls[0].ContextId, c.ContextId));
        Assert.Equal([0, 1, 2], fake.Calls.Select(c => c.History.Count));
        Assert.Equal(1, host.Cache.Count);
    }

    [Fact]
    public async Task A_different_assistant_text_in_the_history_is_a_different_conversation()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await AskAsync(host, stream: false, Msg("user", "hi"));
        await AskAsync(host, stream: false, Msg("user", "hi"), Msg("assistant", "not what the model said"), Msg("user", "more"));

        Assert.Equal(2, fake.ContextsCreated);
        Assert.NotEqual(fake.Calls[0].ContextId, fake.Calls[1].ContextId);
        Assert.Empty(fake.Calls[1].History);

        // The miss replays the whole transcript.
        Assert.Contains("not what the model said", fake.Calls[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(2, host.Cache.Count);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task A_different_system_prompt_is_a_different_conversation_even_under_native_placement()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await AskAsync(host, stream: false, Msg("system", "You are Ada."), Msg("user", "hi"));
        await AskAsync(host, stream: false, Msg("system", "You are Bob."), Msg("user", "hi"), Msg("assistant", "ok"), Msg("user", "more"));

        Assert.Equal(2, fake.ContextsCreated);
        Assert.Equal("You are Ada.", fake.Calls[0].SystemPrompt);
        Assert.Equal("You are Bob.", fake.Calls[1].SystemPrompt);
    }

    [Fact]
    public async Task Trailing_whitespace_on_the_echoed_reply_still_hits()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await AskAsync(host, stream: false, Msg("user", "hi"));
        await AskAsync(host, stream: false, Msg("user", "hi"), Msg("assistant", "ok \n"), Msg("user", "more"));

        Assert.Equal(1, fake.ContextsCreated);
        Assert.Single(fake.Calls[1].History);
    }

    [Fact]
    public async Task Cache_size_zero_replays_every_request_on_a_fresh_context_and_keeps_nothing()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, ContextCacheSize = 0 });

        await AskAsync(host, stream: false, Msg("user", "hi"));
        await AskAsync(host, stream: true, Msg("user", "hi"), Msg("assistant", "ok"), Msg("user", "more"));

        Assert.Equal(2, fake.ContextsCreated);
        Assert.Equal(2, fake.ContextsDisposed);
        Assert.Equal(0, fake.ActiveContexts);
        Assert.Equal(0, host.Cache.Count);
        Assert.Empty(fake.Calls[1].History);

        var health = await ReadJson(await host.Client.GetAsync("/healthz"));
        Assert.Equal(0, health.GetProperty("contexts_cached").GetInt32());
    }

    [Fact]
    public async Task The_bound_evicts_the_least_recently_used_context_and_healthz_counts_what_is_held()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, ContextCacheSize = 2 });

        await AskAsync(host, stream: false, Msg("user", "a"));
        await AskAsync(host, stream: false, Msg("user", "b"));
        Assert.Equal(2, (await ReadJson(await host.Client.GetAsync("/healthz"))).GetProperty("contexts_cached").GetInt32());

        // Touch "a" so "b" is the oldest, then add a third.
        await AskAsync(host, stream: false, Msg("user", "a"), Msg("assistant", "ok"), Msg("user", "again"));
        await AskAsync(host, stream: false, Msg("user", "c"));

        Assert.Equal(3, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Equal(2, host.Cache.Count);
        host.AssertNoLeak();

        // "a" is still there, with both exchanges; "b" is gone, so continuing it replays on a new context.
        await AskAsync(host, stream: false, Msg("user", "a"), Msg("assistant", "ok"), Msg("user", "again"), Msg("assistant", "ok"), Msg("user", "third"));
        Assert.Equal(3, fake.ContextsCreated);
        Assert.Equal(2, fake.Calls[^1].History.Count);
        await AskAsync(host, stream: false, Msg("user", "b"), Msg("assistant", "ok"), Msg("user", "again"));
        Assert.Equal(4, fake.ContextsCreated);
        Assert.Empty(fake.Calls[^1].History);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cut_reply_does_not_return_its_context_to_the_cache(bool stream)
    {
        // The context has absorbed text the client never saw; the transcript it would be stored
        // under is not the one the client will send back.
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["one ", "two ", "three ", "four"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream,
            max_tokens = 1,
            messages = new[] { Msg("user", "count") },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("length", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Equal(0, host.Cache.Count);
    }

    [Fact]
    public async Task A_stop_string_that_only_the_whole_text_cut_catches_still_keeps_the_context_out()
    {
        // Single delta, so the watcher never cancels; the whole-text cut on the JSON path trims the
        // reply after the fact. The status is Complete but the text is not what the client got.
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["alpha END omega"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stop = "END",
            messages = new[] { Msg("user", "go") },
        });

        var root = await ReadJson(response);
        Assert.Equal("alpha ", root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(0, host.Cache.Count);
        Assert.Equal(1, fake.ContextsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failure_on_a_cached_context_disposes_it_rather_than_returning_it(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await AskAsync(host, stream: false, Msg("user", "hi"));
        Assert.Equal(1, host.Cache.Count);

        // Second request hits, then the backend faults after the first token.
        fake.Options.FailAfterTokens = 1;
        fake.Options.Responder = _ => ["o", "k"];
        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream,
            messages = new[] { Msg("user", "hi"), Msg("assistant", "ok"), Msg("user", "more") },
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("server_error", body, StringComparison.Ordinal);

        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(fake.Calls[0].ContextId, fake.Calls[1].ContextId);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Equal(0, host.Cache.Count);
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Fact]
    public async Task Two_concurrent_requests_for_one_conversation_never_share_a_context()
    {
        // Both start before either finishes: the first checks the context out, the second misses and
        // creates its own. Both generations then complete with the same reply, so both are stored
        // under the same key and the cache keeps one, disposing the other (D11 for the older).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["same"], FirstTokenGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var conversation = new[] { Msg("user", "hi"), Msg("assistant", "same"), Msg("user", "more") };

        // Seed the cache with the first exchange.
        gate.SetResult();
        await AskAsync(host, stream: false, Msg("user", "hi"));
        Assert.Equal(1, host.Cache.Count);

        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Options.FirstTokenGate = secondGate;

        var a = host.Client.PostAsJsonAsync(Path, new { model = "fake", messages = conversation });
        var b = host.Client.PostAsJsonAsync(Path, new { model = "fake", stream = true, messages = conversation });
        await WaitUntilAsync(() => fake.Calls.Count == 3);

        // One of them is on the cached context, the other on a fresh one.
        Assert.Equal(2, fake.ContextsCreated);
        Assert.NotEqual(fake.Calls[1].ContextId, fake.Calls[2].ContextId);
        Assert.Contains(fake.Calls[0].ContextId, new[] { fake.Calls[1].ContextId, fake.Calls[2].ContextId });
        Assert.Equal(0, host.Cache.Count);

        secondGate.SetResult();
        var responses = await Task.WhenAll(a, b);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

        await WaitUntilAsync(() => fake.ContextsDisposed == 1);
        Assert.Equal(1, host.Cache.Count);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Prompt_tokens_report_the_whole_transcript_whether_or_not_the_request_hit()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);
        object[] conversation = [Msg("system", "sys"), Msg("user", "hi"), Msg("assistant", "ok"), Msg("user", "more")];

        var miss = await ReadJson(await host.Client.PostAsJsonAsync(Path, new { model = "fake", messages = conversation }));
        await AskAsync(host, stream: false, Msg("system", "sys"), Msg("user", "hi"));
        var hit = await ReadJson(await host.Client.PostAsJsonAsync(Path, new { model = "fake", messages = conversation }));

        Assert.Equal("miss then hit", $"{(fake.Calls[0].History.Count == 0 ? "miss" : "?")} then {(fake.Calls[2].History.Count == 1 ? "hit" : "?")}");
        var expected = ChatRequestMetrics.EstimateTokens("sys".Length + PromptTemplate.Render(
        [
            new ChatMessage("system", ChatMessageContent.FromText("sys"), null, null),
            new ChatMessage("user", ChatMessageContent.FromText("hi"), null, null),
            new ChatMessage("assistant", ChatMessageContent.FromText("ok"), null, null),
            new ChatMessage("user", ChatMessageContent.FromText("more"), null, null),
        ], nativeSystemPromptSupported: true).Prompt.Length);
        Assert.Equal(expected, miss.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(expected, hit.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
    }

    [Fact]
    public async Task Healthz_reports_the_contexts_held_and_shutdown_releases_them()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        var host = await BridgeTestHost.StartAsync(fake);

        await AskAsync(host, stream: false, Msg("user", "a"));
        await AskAsync(host, stream: true, Msg("user", "b"));

        Assert.Equal(2, (await ReadJson(await host.Client.GetAsync("/healthz"))).GetProperty("contexts_cached").GetInt32());

        await host.DisposeAsync();
        Assert.Equal(2, fake.ContextsDisposed);
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Fact]
    public async Task A_transcript_ending_in_an_assistant_turn_can_hit_on_the_whole_transcript()
    {
        // Prefill-style request: the last message is the assistant's. The cached prefix is the whole
        // transcript, the tail is empty, and what goes to the model is the reply heading alone.
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await AskAsync(host, stream: false, Msg("user", "hi"));
        await AskAsync(host, stream: false, Msg("user", "hi"), Msg("assistant", "ok"));

        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(PromptTemplate.RenderTail([]), fake.Calls[1].Prompt);
    }

    private static object Msg(string role, string content) => new { role, content };

    /// <summary>Posts on either shape and returns the reply text the client would assemble.</summary>
    private static async Task<string> AskAsync(BridgeTestHost host, bool stream, params object[] messages)
    {
        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", stream, messages });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        if (!stream)
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        }

        var text = new System.Text.StringBuilder();
        foreach (var line in body.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)))
        {
            var payload = line["data: ".Length..];
            if (payload == "[DONE]")
            {
                continue;
            }

            using var doc = JsonDocument.Parse(payload);
            foreach (var choice in doc.RootElement.GetProperty("choices").EnumerateArray())
            {
                if (choice.GetProperty("delta").TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    text.Append(content.GetString());
                }
            }
        }

        return text.ToString();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}
