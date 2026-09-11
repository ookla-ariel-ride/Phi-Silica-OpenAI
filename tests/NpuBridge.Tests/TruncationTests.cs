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
/// Overflow handling (chunk 5). On a backend with a preflight the verdict comes from
/// <c>GetUsablePromptLength</c> before anything is generated (D55); on one without, from the
/// generation's status. Without <c>--truncate-history</c> both are a 400 <c>context_length_exceeded</c>;
/// with it, the oldest exchange is dropped and the request retried, and the response says how many
/// turns went. The fake's <c>MaxPromptChars</c> is the window; it counts what a context has absorbed
/// plus the incoming prompt, like the real thing.
/// </summary>
public class TruncationTests
{
    private const string Path = "/v1/chat/completions";

    private static readonly BackendCapabilities NoPreflight =
        BackendCapabilities.SamplingOptions | BackendCapabilities.SystemPromptContext | BackendCapabilities.Cancellation;

    private static object Msg(string role, string content) => new { role, content };

    /// <summary>
    /// Three exchanges of filler plus the question: 7 turns, 399 rendered characters. Dropping one
    /// exchange leaves 298, two leave 197, three leave 96 -- so a 250-character window fits after two.
    /// </summary>
    private static object[] LongConversation(int turnChars = 40) =>
    [
        Msg("user", new string('a', turnChars)),
        Msg("assistant", new string('b', turnChars)),
        Msg("user", new string('c', turnChars)),
        Msg("assistant", new string('d', turnChars)),
        Msg("user", new string('e', turnChars)),
        Msg("assistant", new string('f', turnChars)),
        Msg("user", "final question"),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_a_preflight_an_over_length_transcript_is_refused_without_generating(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 150, Responder = _ => ["never"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", stream, messages = LongConversation() });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Contains("'fake'", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("--truncate-history", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        // The point of D55: no generation ran, and the context that was created for the check is gone.
        Assert.Empty(fake.Calls);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.False(response.Headers.Contains("x-npu-bridge-truncated-turns"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_truncate_history_the_oldest_exchanges_are_dropped_until_the_transcript_fits(bool stream)
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 250, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture,
            options: new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true });

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", stream, messages = LongConversation() });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal("4", Assert.Single(response.Headers.GetValues("x-npu-bridge-truncated-turns")));

        // Exactly one generation, on the transcript that fit: the two oldest exchanges are gone, the
        // third and the question remain.
        var call = Assert.Single(fake.Calls);
        Assert.DoesNotContain("aaaa", call.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("dddd", call.Prompt, StringComparison.Ordinal);
        Assert.Contains("eeee", call.Prompt, StringComparison.Ordinal);
        Assert.Contains("ffff", call.Prompt, StringComparison.Ordinal);
        Assert.Contains("final question", call.Prompt, StringComparison.Ordinal);
        Assert.True(call.Prompt.Length <= 250);

        // Each refused attempt created and released a context; the one that generated is cached.
        Assert.Equal(3, fake.ContextsCreated);
        Assert.Equal(2, fake.ContextsDisposed);
        Assert.Equal(1, host.Cache.Count);

        var warnings = capture.Records.Where(r => r.Level == LogLevel.Warning && r.Message.Contains("dropped the oldest", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Contains("2 turn(s)", warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("4 dropped so far", warnings[1].Message, StringComparison.Ordinal);

        var line = Assert.Single(capture.Records, r => r.Message.Contains("cache=", StringComparison.Ordinal));
        Assert.Contains("cache=miss tail_turns=3 truncated_turns=4", line.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nothing_older_than_the_question_is_ever_dropped(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 10, Responder = _ => ["never"] });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true });

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream,
            messages = new[] { Msg("user", "a"), Msg("assistant", "b"), Msg("user", new string('q', 100)) },
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Contains("Nothing older than the message being answered is left to drop", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("x-npu-bridge-truncated-turns"));
        Assert.Empty(fake.Calls);
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Fact]
    public async Task A_tail_that_does_not_fit_a_cached_context_returns_that_context_untouched_and_truncates_afresh()
    {
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 120, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true });

        // First exchange fits and is cached; the context has absorbed prompt + reply.
        var first = await host.Client.PostAsJsonAsync(Path, new { model = "fake", messages = new[] { Msg("user", new string('a', 60)) } });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, host.Cache.Count);

        // The continuation's tail alone is bigger than the room left in the cached context.
        var second = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            messages = new[] { Msg("user", new string('a', 60)), Msg("assistant", "ok"), Msg("user", new string('z', 70)) },
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("2", Assert.Single(second.Headers.GetValues("x-npu-bridge-truncated-turns")));

        // The generation ran on a fresh context with only the question; the cached context was put
        // back untouched and is still there beside the new one.
        Assert.Equal(2, fake.Calls.Count);
        Assert.NotEqual(fake.Calls[0].ContextId, fake.Calls[1].ContextId);
        Assert.Empty(fake.Calls[1].History);
        Assert.DoesNotContain("aaaa", fake.Calls[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(2, host.Cache.Count);
        Assert.Equal(2, fake.ContextsCreated);
        Assert.Equal(0, fake.ContextsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_preflight_the_generation_status_drives_the_same_truncation(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Capabilities = NoPreflight, MaxPromptChars = 250, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true });

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", stream, messages = LongConversation() });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal("4", Assert.Single(response.Headers.GetValues("x-npu-bridge-truncated-turns")));

        // Three generations: two refused by status, one that answered. Every refused context is gone.
        Assert.Equal(3, fake.Calls.Count);
        Assert.Contains("final question", fake.Calls[^1].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("dddd", fake.Calls[^1].Prompt, StringComparison.Ordinal);
        Assert.Equal(3, fake.ContextsCreated);
        Assert.Equal(2, fake.ContextsDisposed);
        Assert.Equal(1, host.Cache.Count);
    }

    [Fact]
    public async Task Without_a_preflight_and_without_truncate_history_the_status_is_still_the_400()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Capabilities = NoPreflight, MaxPromptChars = 150 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", messages = LongConversation() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(fake.Calls);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Equal(0, host.Cache.Count);
    }

    [Fact]
    public async Task A_status_driven_truncation_after_a_keep_alive_still_answers_but_cannot_send_the_header()
    {
        // The verdict lands after the first keep-alive committed the headers. The retry still happens
        // and the stream still carries the answer; the header is lost, and the log says so.
        //
        // The first generation waits at the start gate, which this test opens only once it holds the
        // headers — and the only thing that can have committed them is a keep-alive comment, since no
        // generation has returned anything yet. So "the verdict came too late for the header" is
        // arranged rather than raced against the keep-alive interval (D54). The gate is opened once and
        // the retries run straight through it.
        var capture = new CapturingLoggerProvider();
        var start = new TaskCompletionSource();
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = NoPreflight,
            MaxPromptChars = 250,
            StartGate = start,
            Responder = _ => ["ok"],
        });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture,
            keepAliveInterval: TimeSpan.FromSeconds(30),
            firstKeepAliveDelay: TimeSpan.FromMilliseconds(20),
            options: new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true });

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(new { model = "fake", stream = true, messages = LongConversation() }),
        };
        var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, guard.Token);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        start.SetResult();
        var body = await response.Content.ReadAsStringAsync(guard.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(": keep-alive", body, StringComparison.Ordinal);
        Assert.Contains("\"content\":\"ok\"", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("x-npu-bridge-truncated-turns"));
        Assert.Contains(capture.Records, r => r.Level == LogLevel.Warning && r.Message.Contains("cannot be sent", StringComparison.Ordinal));
        Assert.Equal(3, fake.Calls.Count);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Context_pressure_is_warned_once_per_request_near_the_window_hint()
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture,
            options: new BridgeOptions { Backend = BackendKind.Fake, ContextWindowHint = 256 });

        // 256 tokens is 1024 chars; nine tenths is 922.
        await host.Client.PostAsJsonAsync(Path, ChatBody.User(new string('x', 900)));
        Assert.DoesNotContain(capture.Records, r => r.Message.Contains("context pressure", StringComparison.Ordinal));

        await host.Client.PostAsJsonAsync(Path, ChatBody.User(new string('x', 950)));
        var warning = Assert.Single(capture.Records, r => r.Message.Contains("context pressure", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("950 chars, 92%", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exchange_with_tool_use_is_dropped_whole_so_no_tool_result_is_orphaned()
    {
        // Full render is about 414 characters; dropping the first exchange (question, call, result,
        // answer: four turns) leaves about 197. A 300-character window therefore fits after one drop.
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 300, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true });

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            messages = new object[]
            {
                Msg("user", new string('q', 40)),
                Msg("assistant", new string('c', 40)),
                new { role = "tool", name = "t", tool_call_id = "c1", content = new string('r', 40) },
                Msg("assistant", new string('a', 40)),
                Msg("user", new string('e', 40)),
                Msg("assistant", new string('f', 40)),
                Msg("user", "final question"),
            },
        });

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal("4", Assert.Single(response.Headers.GetValues("x-npu-bridge-truncated-turns")));
        var call = Assert.Single(fake.Calls);
        Assert.DoesNotContain("qqqq", call.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Tool result", call.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("aaaa", call.Prompt, StringComparison.Ordinal);
        Assert.StartsWith("### Conversation so far\n[User]\neeee", call.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_question_still_being_answered_through_tool_results_is_never_truncated()
    {
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 10, Responder = _ => ["never"] });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true });

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            messages = new object[]
            {
                Msg("user", "question"),
                Msg("assistant", "calling"),
                new { role = "tool", name = "t", tool_call_id = "c1", content = new string('r', 100) },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Nothing older than the message being answered", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("x-npu-bridge-truncated-turns"));
        Assert.Empty(fake.Calls);
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_throwing_preflight_is_a_502_and_the_context_it_was_asked_about_is_disposed(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions { PreflightFailure = new InvalidOperationException("com fault"), Responder = _ => ["never"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", stream, messages = new[] { Msg("user", "hi") } });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("backend_error", body, StringComparison.Ordinal);
        Assert.Contains("com fault", body, StringComparison.Ordinal);
        Assert.Empty(fake.Calls);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Equal(0, host.Cache.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_throwing_preflight_on_a_cached_context_disposes_that_context_rather_than_losing_it(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var first = await host.Client.PostAsJsonAsync(Path, new { model = "fake", messages = new[] { Msg("user", "hi") } });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, host.Cache.Count);

        fake.Options.PreflightFailure = new InvalidOperationException("com fault");
        var second = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream,
            messages = new[] { Msg("user", "hi"), Msg("assistant", "ok"), Msg("user", "more") },
        });

        Assert.Equal(HttpStatusCode.BadGateway, second.StatusCode);
        Assert.Single(fake.Calls);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Equal(0, fake.ActiveContexts);
        Assert.Equal(0, host.Cache.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_generation_that_fails_after_truncation_still_carries_the_header(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            MaxPromptChars = 250,
            Responder = _ => ["a"],
            FailAfterTokens = 0,
            FailureStatus = GenerationStatus.Error,
        });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true });

        var response = await host.Client.PostAsJsonAsync(Path, new { model = "fake", stream, messages = LongConversation() });

        // No delta went out, so on both shapes this is the ordinary 502 with the ordinary headers.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("4", Assert.Single(response.Headers.GetValues("x-npu-bridge-truncated-turns")));
        Assert.Single(fake.Calls);
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Fact]
    public async Task A_retry_after_a_cut_starts_with_its_own_cancellation_state()
    {
        // The attempt emits five characters, which trips a one-token cap, and then reports the prompt
        // as too long. The retry must run on a fresh token and be judged on its own status: with the
        // previous attempt's cancelled token it returned Cancelled at once, and the stale cut flag
        // turned that into HTTP 200 with empty content and finish_reason "stop".
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = NoPreflight,
            Responder = _ => ["12345"],
            FailAfterTokens = 1,
            FailureStatus = GenerationStatus.PromptLargerThanContext,
        });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true });

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            max_tokens = 1,
            messages = new[] { Msg("user", "a"), Msg("assistant", "b"), Msg("user", "c") },
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("context_length_exceeded", body, StringComparison.Ordinal);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(2, fake.ContextsCreated);
        Assert.Equal(2, fake.ContextsDisposed);
    }

    [Fact]
    public async Task The_turn_after_a_truncation_finds_the_truncated_context_without_a_replay()
    {
        // Request 1 overflows and is answered after two exchanges are dropped; its context is stored
        // under the truncated transcript's key. Request 2 sends the full transcript plus the reply and
        // a new question. Its own prefixes miss, the preflight refuses the whole thing again, and the
        // truncation loop drops the same two exchanges, at which point the shortened transcript's
        // prefix keys are exactly the stored key: a hit, on the same context, with only the new turn
        // rendered. The cost is the refused preflight rounds, never a second generation of the history.
        var capture = new CapturingLoggerProvider();
        // 200-character turns: the whole render is 1,359 characters, 517 after two exchanges are
        // dropped; the fake counts what a context has absorbed plus the incoming prompt, so a
        // 640-character window leaves room for the 90-character tail of the follow-up on the cached
        // context. (With 40-character turns and a 250 window the tail did not fit and a third
        // exchange went, which is the fake being tiny, not the lookup failing.)
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 640, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture,
            options: new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true });

        var first = await host.Client.PostAsJsonAsync(Path, new { model = "fake", messages = LongConversation(200) });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("4", Assert.Single(first.Headers.GetValues("x-npu-bridge-truncated-turns")));
        var contextsAfterFirst = fake.ContextsCreated;

        var followUp = LongConversation(200).Concat([Msg("assistant", "ok"), Msg("user", "one more")]).ToArray();
        var second = await host.Client.PostAsJsonAsync(Path, new { model = "fake", messages = followUp });
        Assert.True(second.StatusCode == HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        Assert.Equal("4", Assert.Single(second.Headers.GetValues("x-npu-bridge-truncated-turns")));

        // Same context, one prior exchange in its history, and only the new question rendered.
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(fake.Calls[0].ContextId, fake.Calls[1].ContextId);
        Assert.Single(fake.Calls[1].History);
        Assert.Equal(PromptTemplate.RenderTail([new ChatMessage("user", ChatMessageContent.FromText("one more"), null, null)]), fake.Calls[1].Prompt);

        // What it cost: fresh contexts created for the refused preflight rounds and released again, no
        // generation on any of them; the hit counter moved by one.
        Assert.Equal(1, host.Cache.Count);
        Assert.Equal(1, host.Cache.Hits);
        Assert.Equal(fake.ContextsCreated - 1, fake.ContextsDisposed);
        Assert.True(fake.ContextsCreated > contextsAfterFirst, "the follow-up paid at least one refused preflight round on a fresh context");
        var line = capture.Records.Last(r => r.Message.Contains("cache=", StringComparison.Ordinal)).Message;
        Assert.Contains("cache=hit tail_turns=1 truncated_turns=4", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cached branch of the refusal. Without <c>--truncate-history</c> a tail that does not fit
    /// the cached context is the 400, its message says the tail was the problem, and the context
    /// goes back into the cache exactly as it came out: the next request that does fit still hits it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tail_that_does_not_fit_a_cached_context_is_refused_and_the_context_goes_back_untouched(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 400, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        // A lone user message renders raw (D71), so the context has absorbed 100 + 2
        // characters; a tail renders in the marker format, so it costs more than its text.
        var first = await host.Client.PostAsJsonAsync(Path, new { model = "fake", messages = new[] { Msg("user", new string('a', 100)) } });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, host.Cache.Count);

        var refused = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream,
            messages = new[] { Msg("user", new string('a', 100)), Msg("assistant", "ok"), Msg("user", new string('z', 300)) },
        });
        var body = await refused.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Contains("tail of 1 new turn(s) on a cached context", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(refused.Headers.Contains("x-npu-bridge-truncated-turns"));

        // No generation ran, nothing was created or disposed, and the context is back where it was.
        Assert.Single(fake.Calls);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(0, fake.ContextsDisposed);
        Assert.Equal(1, host.Cache.Count);

        // A tail that fits hits the very same context, with the first exchange still in it.
        var fits = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream,
            messages = new[] { Msg("user", new string('a', 100)), Msg("assistant", "ok"), Msg("user", "short") },
        });
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(fake.Calls[0].ContextId, fake.Calls[1].ContextId);
        Assert.Single(fake.Calls[1].History);
        Assert.Equal(1, fake.ContextsCreated);
        host.AssertNoLeak();
    }
}
