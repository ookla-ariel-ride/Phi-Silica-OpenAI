using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Api;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;
using NpuBridge.Tokenizers;

namespace NpuBridge.Tests;

public class SystemTextGuardTests
{
    private const string ChatPath = "/v1/chat/completions";

    private static readonly object[] Weather =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "get_weather",
                description = "Get current weather",
                parameters = new
                {
                    type = "object",
                    properties = new { location = new { type = "string" } },
                },
            },
        },
    ];

    private sealed class FixedTokenCountCounter : ITokenCounter
    {
        public string Name => "fixed";

        public bool PrefixStable => true;

        public int Count(string text) => 32_001;

        public int IndexAtTokenCount(string text, int tokens, out int totalTokens)
        {
            totalTokens = Count(text);
            return text.Length;
        }

        public int TokensCovering(string text, int prefixChars) => Count(text);
    }

    private sealed class CountingTokenCounter : ITokenCounter
    {
        public string Name => CharEstimateTokenCounter.Instance.Name;

        public bool PrefixStable => CharEstimateTokenCounter.Instance.PrefixStable;

        public int CountCalls { get; private set; }

        public int Count(string text)
        {
            CountCalls++;
            return CharEstimateTokenCounter.Instance.Count(text);
        }

        public int IndexAtTokenCount(string text, int tokens, out int totalTokens) =>
            CharEstimateTokenCounter.Instance.IndexAtTokenCount(text, tokens, out totalTokens);

        public int TokensCovering(string text, int prefixChars) =>
            CharEstimateTokenCounter.Instance.TokensCovering(text, prefixChars);
    }

    [Fact]
    public async Task Token_window_refuses_native_system_text_before_context_creation_and_allows_smaller_text()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var refused = await PostChatAsync(host, new string('s', 500));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = await ErrorAsync(refused);
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Contains("Native system text alone exceeds the context window: 125 tokens fills the 100-token usable window.",
            error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();

        var accepted = await PostChatAsync(host, new string('s', 200));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Character_ceiling_applies_when_the_context_window_is_unknown()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = null });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var refused = await PostChatAsync(host, new string('s', 32_001));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = await ErrorAsync(refused);
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Contains("Native system text alone exceeds the 32,000-character safety ceiling: 32,001 characters.",
            error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();

        var accepted = await PostChatAsync(host, new string('s', 32_000));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Character_ceiling_refusal_does_not_tokenize_native_system_text()
    {
        var counter = new CountingTokenCounter();
        var fake = new FakeBackend(new FakeBackendOptions
        {
            ContextWindowTokens = 100,
            TokenCounter = counter,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var refused = await PostChatAsync(host, new string('s', 32_001));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, counter.CountCalls);
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();
    }

    [Fact]
    public void Refusal_messages_use_invariant_grouping_under_a_non_invariant_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var backend = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 32_000 });

            var characterFailure = SystemTextGuard.RefusalFor(backend, new string('s', 32_001), includesToolDefinitions: false);
            var tokenBackend = new FakeBackend(new FakeBackendOptions
            {
                ContextWindowTokens = 32_000,
                TokenCounter = new FixedTokenCountCounter(),
            });
            var tokenFailure = SystemTextGuard.RefusalFor(tokenBackend, "s", includesToolDefinitions: false);

            Assert.Contains("32,000-character safety ceiling: 32,001 characters.", characterFailure!.Body.Error.Message,
                StringComparison.Ordinal);
            Assert.Contains("32,001 tokens fills the 32,000-token usable window.", tokenFailure!.Body.Error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Tool_definitions_are_named_only_when_they_are_in_the_system_text()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var withoutTools = await PostChatAsync(host, new string('s', 500));
        var withoutToolsError = await ErrorAsync(withoutTools);
        Assert.DoesNotContain("Rendered tool definitions", withoutToolsError.GetProperty("message").GetString(), StringComparison.Ordinal);

        var withTools = await PostChatAsync(host, new string('s', 500), tools: Weather);
        var withToolsError = await ErrorAsync(withTools);
        Assert.Contains("Rendered tool definitions are included in that count.",
            withToolsError.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Truncate_history_does_not_retry_a_native_system_text_refusal()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        var options = new BridgeOptions { Backend = BackendKind.Fake, TruncateHistory = true };
        var capture = new CapturingLoggerProvider();
        await using var host = await BridgeTestHost.StartAsync(fake, options, loggerProvider: capture);

        var response = await host.Client.PostAsJsonAsync(ChatPath, new
        {
            model = "fake",
            messages = new[]
            {
                new { role = "system", content = new string('s', 500) },
                new { role = "user", content = "An earlier question." },
                new { role = "assistant", content = "An earlier answer." },
                new { role = "user", content = "Reply with OK." },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("x-npu-bridge-truncated-turns"));
        Assert.DoesNotContain(capture.Records, r => r.Message.Contains("dropped the oldest", StringComparison.Ordinal));
        Assert.Equal(0, fake.ContextsCreated);
        Assert.Empty(fake.Calls);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Streamed_native_system_text_refusal_is_a_plain_bad_request_before_any_frame()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostChatAsync(host, new string('s', 500), stream: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("data:", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Native_system_text_refusal_bypasses_a_busy_queue_and_preserves_the_rolling_mean()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var generationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions
        {
            ContextWindowTokens = 100,
            StartGate = generationGate,
        });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, QueueCapacity = 1 }, time: clock);

        // The running generation owns the worker but is not queued itself.
        var running = PostChatAsync(host, "safe");
        await TestWait.UntilAsync(() => fake.Calls.Count == 1);

        // Preparation runs before ScheduleAsync, so this streamed request returns a normal JSON 400
        // without spending the worker's slot or committing an SSE keep-alive.
        var refusedTask = PostChatAsync(host, new string('s', 500), stream: true);
        await TestWait.UntilAsync(() => refusedTask.IsCompleted);
        var refused = await refusedTask;
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("application/json", refused.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(": keep-alive", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, host.Scheduler.QueueDepth);
        Assert.Single(fake.Calls);

        // Make the only admitted generation take two scheduler seconds. Its duration is the mean that
        // a later full-queue response must use; a guarded refusal must not add a zero-duration job.
        clock.Advance(TimeSpan.FromSeconds(2));
        generationGate.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await running).StatusCode);

        var schedulerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedulerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = host.Scheduler.ScheduleAsync(async _ =>
        {
            schedulerEntered.SetResult();
            await schedulerGate.Task;
            return 0;
        }, CancellationToken.None);
        await schedulerEntered.Task;

        var queued = host.Scheduler.ScheduleAsync(_ => Task.FromResult(0), CancellationToken.None);
        await TestWait.UntilAsync(() => host.Scheduler.QueueDepth == 1);
        var rejected = await host.Scheduler.ScheduleAsync(_ => Task.FromResult(0), CancellationToken.None);

        Assert.Equal(ScheduleResultKind.Rejected, rejected.Kind);
        Assert.Equal(2, rejected.RetryAfterSeconds);

        schedulerGate.SetResult();
        await Task.WhenAll(held, queued);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Debug_generate_uses_the_same_guard_before_context_creation()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 100 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "reply", system = new string('s', 500) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ErrorAsync(response);
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Equal(0, fake.ContextsCreated);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Folded_system_text_is_left_to_the_prompt_preflight()
    {
        var preflightCalls = 0;
        var fake = new FakeBackend(new FakeBackendOptions
        {
            ContextWindowTokens = 100,
            MaxPromptChars = 100,
            OnPreflight = _ => preflightCalls++,
        });
        var options = new BridgeOptions { Backend = BackendKind.Fake, SystemPromptPlacement = SystemPromptPlacement.Prompt };
        await using var host = await BridgeTestHost.StartAsync(fake, options);

        var response = await PostChatAsync(host, new string('s', 500));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ErrorAsync(response);
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Contains(
            "Backend 'fake' can take 100 characters of the 598-character prompt; the transcript is 598 characters over 1 turn(s).",
            error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Native system text alone", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(preflightCalls >= 1);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Folded_system_text_uses_no_native_system_context_when_the_prompt_fits()
    {
        var fake = new FakeBackend();
        var options = new BridgeOptions { Backend = BackendKind.Fake, SystemPromptPlacement = SystemPromptPlacement.Prompt };
        await using var host = await BridgeTestHost.StartAsync(fake, options);

        var response = await PostChatAsync(host, new string('s', 500));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(fake.Calls).SystemPrompt);
        host.AssertNoLeak();
    }

    [Fact]
    public void Unavailable_backend_has_no_known_context_window()
    {
        var backend = new UnavailableBackend("unavailable", "Unavailable", "not installed");
        Assert.Null(backend.ContextWindowTokens);
    }

    private static Task<HttpResponseMessage> PostChatAsync(BridgeTestHost host, string system, bool stream = false, object[]? tools = null) =>
        host.Client.PostAsJsonAsync(ChatPath, new
        {
            model = "fake",
            stream,
            tools,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = "Reply with OK." },
            },
        });

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").Clone();
    }
}
