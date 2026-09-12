using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;

namespace NpuBridge.Tests;

/// <summary>
/// Chunk 8, task 2: <see cref="NpuBridge.Api.GenerationScheduler"/> wired into both response shapes of
/// <c>/v1/chat/completions</c> and into <c>/debug/generate</c>. <see cref="ContextCacheEndpointTests"/>
/// covers the ordinary queueing case (two generations for one conversation, one runs while the other
/// waits); these tests are about the scheduler becoming visible at the edges: a full queue answering
/// 429 before either shape writes a byte, a queued stream's keep-alives, the log line's
/// <c>queue_wait_ms</c>, <c>/healthz</c>'s live count, and the debug endpoint no longer jumping the
/// line.
/// </summary>
public class GenerationSchedulerEndpointTests
{
    private const string Path = "/v1/chat/completions";

    [Fact]
    public async Task Queue_full_returns_429_with_retry_after_and_a_conformant_body_on_both_shapes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"], FirstTokenGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, QueueCapacity = 1 });

        // Occupies the one worker, blocked at the gate -- not counted in QueueDepth once dequeued.
        var running = host.Client.PostAsJsonAsync(Path, ChatBody.User("a"));
        await TestWait.UntilAsync(() => fake.Calls.Count == 1);

        // Fills the queue's one slot.
        var queued = host.Client.PostAsJsonAsync(Path, ChatBody.User("b"));
        await TestWait.UntilAsync(() => host.Scheduler.QueueDepth == 1);

        // A third, non-streamed request finds the queue full and never reaches the backend at all.
        var rejectedJson = await host.Client.PostAsJsonAsync(Path, ChatBody.User("c"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedJson.StatusCode);
        AssertRetryAfter(rejectedJson);
        var jsonError = (await ReadJson(rejectedJson)).GetProperty("error");
        Assert.Equal("rate_limit_error", jsonError.GetProperty("type").GetString());
        Assert.Equal("queue_full", jsonError.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, jsonError.GetProperty("param").ValueKind);
        Assert.False(string.IsNullOrEmpty(jsonError.GetProperty("message").GetString()));

        // A fourth, streamed request is rejected identically -- decision 1 (task-2-brief.md): the
        // rejection is synchronous with the enqueue attempt, so the stream never opens at all and this
        // is the ordinary JSON error body and status line, not an SSE event.
        var rejectedStream = await host.Client.PostAsJsonAsync(Path, new { model = "fake", stream = true, messages = new[] { new { role = "user", content = "d" } } });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedStream.StatusCode);
        Assert.Equal("application/json", rejectedStream.Content.Headers.ContentType?.MediaType);
        AssertRetryAfter(rejectedStream);
        var streamError = (await ReadJson(rejectedStream)).GetProperty("error");
        Assert.Equal("rate_limit_error", streamError.GetProperty("type").GetString());
        Assert.Equal("queue_full", streamError.GetProperty("code").GetString());

        // Still exactly one worker slot busy and one queued: the two rejections never touched the queue.
        Assert.Equal(1, host.Scheduler.QueueDepth);

        gate.SetResult();
        var settled = await Task.WhenAll(running, queued);
        Assert.All(settled, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        // The two rejected requests each did their own context lookup (a miss, since "c" and "d" are
        // new conversations) before ever reaching the scheduler -- the queue wait sits after that, not
        // before it (task-2-brief.md) -- so each created a context nobody ever generated on, and each
        // is disposed rather than cached.
        Assert.Equal(4, fake.ContextsCreated);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task A_streamed_request_queued_behind_another_sends_keep_alives_while_it_waits()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"], FirstTokenGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake,
            keepAliveInterval: TimeSpan.FromMilliseconds(20),
            firstKeepAliveDelay: TimeSpan.FromMilliseconds(20));

        var running = host.Client.PostAsJsonAsync(Path, ChatBody.User("a"));
        await TestWait.UntilAsync(() => fake.Calls.Count == 1);

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(new { model = "fake", stream = true, messages = new[] { new { role = "user", content = "b" } } }),
        };

        // Headers only come from a keep-alive comment here: nothing else could have written them,
        // since the queued job has not reached the backend and so has produced no delta (integration
        // decisions 1 and 2, task-2-brief.md -- a queue wait is invisible to the reader loop, which
        // just keeps emitting keep-alives).
        var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, guard.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await TestWait.UntilAsync(() => host.Scheduler.QueueDepth == 1);
        Assert.Single(fake.Calls); // still just the running one; the queued job never touched the model

        gate.SetResult();
        var body = await response.Content.ReadAsStringAsync(guard.Token);
        await running;

        Assert.Contains(": keep-alive", body, StringComparison.Ordinal);
        Assert.Contains("\"finish_reason\":\"stop\"", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        host.AssertNoLeak();
    }

    /// <summary>
    /// The margin (a short real wait between the queued request reaching the queue and the gate that
    /// releases the one ahead of it) is not itself the assertion (D54): the claim is the ordering
    /// <c>queued's wait &gt; immediate's wait</c>, which the scheduler's own clock guarantees regardless
    /// of how long that margin actually is. The margin exists only so the two values are distinguishable
    /// at the log line's own precision (one decimal place) instead of both flooring to the same
    /// <c>0.0</c> on a fast run.
    /// </summary>
    [Fact]
    public async Task Queue_wait_ms_appears_in_the_log_line_and_is_larger_for_the_request_that_queued()
    {
        var capture = new CapturingLoggerProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"], FirstTokenGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        var running = host.Client.PostAsJsonAsync(Path, ChatBody.User("a"));
        await TestWait.UntilAsync(() => fake.Calls.Count == 1);

        var queued = host.Client.PostAsJsonAsync(Path, ChatBody.User("b"));
        await TestWait.UntilAsync(() => host.Scheduler.QueueDepth == 1);

        await Task.Delay(30);
        gate.SetResult();

        // Task.WhenAll preserves argument order regardless of which request actually finished first
        // (running's own generation resuming and queued's dequeue-then-run race each other once the
        // gate opens, and either can log first) -- so responses[0] is always "a", responses[1] always
        // "b". The requestId each carries is what pins a log line to the request that produced it.
        var responses = await Task.WhenAll(running, queued);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var runningId = (await ReadJson(responses[0])).GetProperty("id").GetString()!;
        var queuedId = (await ReadJson(responses[1])).GetProperty("id").GetString()!;

        double QueueWaitFor(string requestId)
        {
            var line = Assert.Single(capture.Records, r => r.Message.Contains($"req={requestId} ", StringComparison.Ordinal));
            var match = Regex.Match(line.Message, "queue_wait_ms=(?<v>[0-9.]+)");
            Assert.True(match.Success, $"no queue_wait_ms field in: {line.Message}");
            return double.Parse(match.Groups["v"].Value, CultureInfo.InvariantCulture);
        }

        var runningWait = QueueWaitFor(runningId);
        var queuedWait = QueueWaitFor(queuedId);
        Assert.True(queuedWait > runningWait, $"expected the queued request's wait ({queuedWait}) to exceed the one that ran immediately ({runningWait})");
        Assert.True(queuedWait > 0, "the queued request's own log line should report a real, nonzero wait");
    }

    [Fact]
    public async Task Healthz_reports_a_nonzero_queue_depth_while_a_job_waits_and_zero_once_it_drains()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"], FirstTokenGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var running = host.Client.PostAsJsonAsync(Path, ChatBody.User("a"));
        await TestWait.UntilAsync(() => fake.Calls.Count == 1);

        var queued = host.Client.PostAsJsonAsync(Path, ChatBody.User("b"));
        await TestWait.UntilAsync(() => host.Scheduler.QueueDepth == 1);

        var waiting = await ReadJson(await host.Client.GetAsync("/healthz"));
        Assert.Equal(1, waiting.GetProperty("queue_depth").GetInt32());

        gate.SetResult();
        await Task.WhenAll(running, queued);

        await TestWait.UntilAsync(() => host.Scheduler.QueueDepth == 0);
        var drained = await ReadJson(await host.Client.GetAsync("/healthz"));
        Assert.Equal(0, drained.GetProperty("queue_depth").GetInt32());
    }

    [Fact]
    public async Task Debug_generate_queues_behind_a_chat_request_and_does_not_create_its_context_early()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"], FirstTokenGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chat = host.Client.PostAsJsonAsync(Path, ChatBody.User("a"));
        await TestWait.UntilAsync(() => fake.Calls.Count == 1);
        Assert.Equal(1, fake.ContextsCreated);

        var debug = host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "hello" });
        await TestWait.UntilAsync(() => host.Scheduler.QueueDepth == 1);

        // Integration decision 5 (task-2-brief.md): /debug/generate's context is not created until the
        // job's turn, so while it is still queued behind the chat request neither the model nor a
        // context has been touched for it.
        Assert.Single(fake.Calls);
        Assert.Equal(1, fake.ContextsCreated);

        gate.SetResult();
        var responses = await Task.WhenAll(chat, debug);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(2, fake.ContextsCreated);
        host.AssertNoLeak();
    }

    private static void AssertRetryAfter(HttpResponseMessage response)
    {
        Assert.NotNull(response.Headers.RetryAfter);
        var seconds = response.Headers.RetryAfter!.Delta;
        Assert.True(seconds is { } d && d >= TimeSpan.FromSeconds(1), "Retry-After should be at least the scheduler's own 1-second floor");
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}
