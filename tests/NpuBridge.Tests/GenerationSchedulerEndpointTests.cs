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

        // Only the running request has touched the model at all: the queued one has not looked anything
        // up yet, because Acquire() is inside the scheduled closure (fix round 1, controller ruling).
        Assert.Equal(1, fake.ContextsCreated);

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

        // And neither rejection touched the model. This is the ruling's own point (fix round 1): with
        // Acquire() outside the queue, shedding load was the path that hammered the shared handle
        // hardest -- a burst of N requests created N contexts and ran N preflights before throwing most
        // of them away. A rejected request now creates nothing at all.
        Assert.Equal(1, fake.ContextsCreated);

        gate.SetResult();
        var settled = await Task.WhenAll(running, queued);
        Assert.All(settled, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        // One per request that was actually admitted, and not one more: "a" ran, then "b" was dequeued
        // and did its own lookup (a miss, being a different conversation), and "c"/"d" never got that
        // far.
        Assert.Equal(2, fake.ContextsCreated);
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
    /// No clock of its own (global constraint 2, fix round 1 Finding 7): the scheduler times its queue
    /// waits off the injected <see cref="TimeProvider"/>, so the test advances that provider by a known
    /// amount while the second request sits in the queue and then asserts the exact number the log line
    /// must carry. The first draft of this test used a real 30 ms <c>Task.Delay</c> as the margin that
    /// made the two values distinguishable at the log's one-decimal precision, which is a wall-clock
    /// assumption dressed as a comment: the request that ran immediately reports idle-worker dequeue
    /// latency, and a pool stall on a loaded agent can make that exceed the margin and invert the claim.
    /// </summary>
    [Fact]
    public async Task Queue_wait_ms_appears_in_the_log_line_and_is_larger_for_the_request_that_queued()
    {
        var capture = new CapturingLoggerProvider();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"], FirstTokenGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake, time: clock, loggerProvider: capture);

        var running = host.Client.PostAsJsonAsync(Path, ChatBody.User("a"));
        await TestWait.UntilAsync(() => fake.Calls.Count == 1);

        var queued = host.Client.PostAsJsonAsync(Path, ChatBody.User("b"));
        await TestWait.UntilAsync(() => host.Scheduler.QueueDepth == 1);

        // "b" is in the queue and "a" is still holding the worker, so every tick of this advance lands
        // on "b"'s wait and none of it on "a"'s, which was dequeued at the instant it was enqueued.
        clock.Advance(TimeSpan.FromSeconds(2));
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

        // Exact, not merely ordered: the clock only moved while "b" was queued.
        Assert.Equal(0, QueueWaitFor(runningId));
        Assert.Equal(2000, QueueWaitFor(queuedId));
    }

    /// <summary>
    /// Fix round 1, Finding 1. A backend that breaks the <c>ILanguageModelBackend</c> contract by letting
    /// the runtime's own <see cref="OperationCanceledException"/> escape — the violation D82's unfiltered
    /// catch clauses exist for — must still be reported as the backend failure it is, even though the
    /// request now runs inside the scheduler. The scheduler is a new place for such an exception to be
    /// absorbed on its way to the client: swallow it into a bare <c>Cancelled</c> and the endpoint sees
    /// something indistinguishable from "the queue never ran this job" and answers 503
    /// <c>queue_shutting_down</c> — a false statement, and the wrong status class, where before chunk 8
    /// it was a 502 <c>backend_error</c>. The cut is what cancels here: the client is still connected
    /// throughout, so nothing about this is the client's own abort.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_backend_that_throws_the_cuts_cancellation_is_a_502_not_a_queue_error(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["0123", "4567", "89ab", "cdef"],
            TokenDelay = TimeSpan.FromMilliseconds(5),
            ThrowOnCancellation = true,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream,
            max_tokens = 2,
            messages = new[] { new { role = "user", content = "hi" } },
        });

        // The streamed shape has deltas on the wire by the time the throw happens, so its status line is
        // spent and the same envelope arrives as an SSE error event (D52) -- the JSON shape answers with
        // the status itself. Either way the body is the backend's failure, never the queue's.
        var error = stream
            ? JsonDocument.Parse(ErrorEventPayload(await response.Content.ReadAsStringAsync())).RootElement.GetProperty("error")
            : (await ReadJson(response)).GetProperty("error");

        Assert.Equal(stream ? HttpStatusCode.OK : HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("backend_error", error.GetProperty("code").GetString());
        Assert.Contains("OperationCanceledException", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        // And the context the failed attempt held is released, not left to the cache (D43).
        await TestWait.UntilAsync(() => fake.ActiveContexts == 0);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        host.AssertNoLeak();
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

    /// <summary>The one <c>data:</c> payload carrying an error, which must also be the last frame before the done marker.</summary>
    private static string ErrorEventPayload(string body)
    {
        var payloads = Sse.Payloads(body);
        Assert.Equal("[DONE]", payloads[^1]);
        return Assert.Single(payloads, p => p.Contains("\"error\"", StringComparison.Ordinal));
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
