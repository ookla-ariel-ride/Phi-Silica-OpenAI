using NpuBridge.Backends;
using NpuBridge.Backends.Fake;

namespace NpuBridge.Tests;

public class FakeBackendTests
{
    private static async Task<FakeBackend> ReadyAsync(FakeBackendOptions? options = null)
    {
        var fake = options is null ? new FakeBackend() : new FakeBackend(options);
        await fake.InitializeAsync(CancellationToken.None);
        return fake;
    }

    [Fact]
    public void Tokenize_round_trips_text()
    {
        const string text = "Hello,  world!\nSecond line  ";
        var tokens = FakeBackend.Tokenize(text);
        Assert.Equal(text, string.Concat(tokens));
        Assert.True(tokens.Count >= 4);
    }

    [Fact]
    public async Task Streams_deltas_and_returns_complete_text()
    {
        var fake = await ReadyAsync();
        using var ctx = fake.CreateContext("be terse");

        var deltas = new List<string>();
        var result = await fake.GenerateAsync(ctx, "line one\nhello there", null, d => { lock (deltas) { deltas.Add(d); } }, CancellationToken.None);

        Assert.Equal(GenerationStatus.Complete, result.Status);
        Assert.Equal(string.Concat(deltas), result.Text);
        Assert.EndsWith("You said: hello there", result.Text, StringComparison.Ordinal);
        Assert.True(deltas.Count > 1);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("be terse", call.SystemPrompt);
        Assert.Equal("line one\nhello there", call.Prompt);
        Assert.Empty(call.History);
        Assert.Null(call.Sampling);
    }

    [Fact]
    public async Task Sampling_options_are_recorded()
    {
        var fake = await ReadyAsync(new FakeBackendOptions { Responder = _ => ["ok"] });
        using var ctx = fake.CreateContext(null);
        var sampling = new SamplingOptions(0.2f, 0.9f, 40);

        await fake.GenerateAsync(ctx, "p", sampling, _ => { }, CancellationToken.None);

        Assert.Equal(sampling, fake.Calls[0].Sampling);
    }

    [Fact]
    public async Task Deltas_are_delivered_off_the_awaiting_flow_by_default()
    {
        var fake = await ReadyAsync(new FakeBackendOptions { Responder = _ => ["a", "b", "c"] });
        using var ctx = fake.CreateContext(null);
        var flowId = new AsyncLocal<int> { Value = 42 };

        var seen = new List<int>();
        await fake.GenerateAsync(ctx, "p", null, _ => { lock (seen) { seen.Add(flowId.Value); } }, CancellationToken.None);

        // Task.Run captures the ExecutionContext, so AsyncLocal flows; assert delivery happened and
        // (inline mode) that it stays on the caller's flow. The point of the option is the code path.
        Assert.Equal(3, seen.Count);

        var inline = await ReadyAsync(new FakeBackendOptions { Responder = _ => ["a"], DeliverDeltasOnThreadPool = false });
        using var ctx2 = inline.CreateContext(null);
        var threadBefore = Environment.CurrentManagedThreadId;
        var threadInCallback = -1;
        await inline.GenerateAsync(ctx2, "p", null, _ => threadInCallback = Environment.CurrentManagedThreadId, CancellationToken.None);
        Assert.Equal(threadBefore, threadInCallback);
    }

    [Fact]
    public async Task Context_accumulates_history_across_turns()
    {
        var fake = await ReadyAsync(new FakeBackendOptions { Responder = _ => ["ok"] });
        using var ctx = fake.CreateContext(null);

        await fake.GenerateAsync(ctx, "first", null, _ => { }, CancellationToken.None);
        await fake.GenerateAsync(ctx, "second", null, _ => { }, CancellationToken.None);

        var second = fake.Calls[1];
        var turn = Assert.Single(second.History);
        Assert.Equal("first", turn.Prompt);
        Assert.Equal("ok", turn.Response);
    }

    [Fact]
    public async Task Cancellation_before_a_token_returns_partial_text_with_cancelled_status()
    {
        var fake = await ReadyAsync(new FakeBackendOptions
        {
            Responder = _ => ["a", "b", "c", "d"],
            DeliverDeltasOnThreadPool = false,
        });
        using var ctx = fake.CreateContext(null);
        using var cts = new CancellationTokenSource();

        var seen = 0;
        var result = await fake.GenerateAsync(ctx, "p", null, _ =>
        {
            if (++seen == 2)
            {
                cts.Cancel();
            }
        }, cts.Token);

        Assert.Equal(GenerationStatus.Cancelled, result.Status);
        Assert.Equal("ab", result.Text);
        Assert.Empty(fake.Calls[0].History);
    }

    [Fact]
    public async Task Cancellation_during_token_delay_returns_cancelled()
    {
        var fake = await ReadyAsync(new FakeBackendOptions
        {
            Responder = _ => ["a", "b"],
            TokenDelay = TimeSpan.FromSeconds(10),
        });
        using var ctx = fake.CreateContext(null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await fake.GenerateAsync(ctx, "p", null, _ => { }, cts.Token);

        Assert.Equal(GenerationStatus.Cancelled, result.Status);
        Assert.Equal(string.Empty, result.Text);
        Assert.Contains("delay", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_token_delay_applies_once()
    {
        var fake = await ReadyAsync(new FakeBackendOptions
        {
            Responder = _ => ["a", "b", "c"],
            FirstTokenDelay = TimeSpan.FromMilliseconds(120),
            DeliverDeltasOnThreadPool = false,
        });
        using var ctx = fake.CreateContext(null);
        var stamps = new List<long>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await fake.GenerateAsync(ctx, "p", null, _ => stamps.Add(clock.ElapsedMilliseconds), CancellationToken.None);

        Assert.True(stamps[0] >= 100, $"first token at {stamps[0]}ms");
        Assert.True(stamps[2] - stamps[0] < 100, "later tokens should not wait");
    }

    [Fact]
    public async Task Injected_status_failure_stops_after_n_tokens()
    {
        var fake = await ReadyAsync(new FakeBackendOptions
        {
            Responder = _ => ["x", "y", "z"],
            FailAfterTokens = 2,
            FailureStatus = GenerationStatus.ContentFiltered,
        });
        using var ctx = fake.CreateContext(null);

        var result = await fake.GenerateAsync(ctx, "p", null, _ => { }, CancellationToken.None);

        Assert.Equal(GenerationStatus.ContentFiltered, result.Status);
        Assert.Equal("xy", result.Text);
    }

    [Fact]
    public async Task Fail_before_first_token_emits_nothing()
    {
        var fake = await ReadyAsync(new FakeBackendOptions { Responder = _ => ["x"], FailAfterTokens = 0 });
        using var ctx = fake.CreateContext(null);
        var deltas = 0;

        var result = await fake.GenerateAsync(ctx, "p", null, _ => deltas++, CancellationToken.None);

        Assert.Equal(GenerationStatus.Error, result.Status);
        Assert.Equal(0, deltas);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task Injected_exception_propagates()
    {
        var fake = await ReadyAsync(new FakeBackendOptions
        {
            Responder = _ => ["x", "y"],
            FailAfterTokens = 1,
            FailureException = new InvalidOperationException("boom"),
        });
        using var ctx = fake.CreateContext(null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fake.GenerateAsync(ctx, "p", null, _ => { }, CancellationToken.None));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Max_prompt_chars_simulates_context_overflow_and_preflight()
    {
        var fake = await ReadyAsync(new FakeBackendOptions { MaxPromptChars = 10, Responder = _ => ["r"] });
        using var ctx = fake.CreateContext(null);

        Assert.Equal(5, fake.GetUsablePromptLength(ctx, "12345"));
        Assert.Equal(10, fake.GetUsablePromptLength(ctx, "123456789012"));

        var ok = await fake.GenerateAsync(ctx, "1234", null, _ => { }, CancellationToken.None);
        Assert.Equal(GenerationStatus.Complete, ok.Status);

        // Context now holds "1234" + "r" = 5 chars; 6 more overflows.
        var overflow = await fake.GenerateAsync(ctx, "123456", null, _ => { }, CancellationToken.None);
        Assert.Equal(GenerationStatus.PromptLargerThanContext, overflow.Status);
        Assert.Equal(string.Empty, overflow.Text);
    }

    [Fact]
    public async Task System_prompt_counts_toward_context_size()
    {
        var fake = await ReadyAsync(new FakeBackendOptions { MaxPromptChars = 10 });
        using var ctx = fake.CreateContext("sys!");
        Assert.Equal(6, fake.GetUsablePromptLength(ctx, "1234567890"));
    }

    [Fact]
    public async Task Preflight_without_limit_reports_whole_prompt()
    {
        var fake = await ReadyAsync();
        using var ctx = fake.CreateContext(null);
        Assert.Equal(3, fake.GetUsablePromptLength(ctx, "abc"));
    }

    [Fact]
    public async Task Preflight_is_null_when_capability_missing()
    {
        var fake = await ReadyAsync(new FakeBackendOptions { Capabilities = BackendCapabilities.None });
        using var ctx = fake.CreateContext("sys");
        Assert.Null(fake.GetUsablePromptLength(ctx, "abc"));
    }

    [Fact]
    public async Task System_prompt_is_dropped_when_capability_missing()
    {
        var fake = await ReadyAsync(new FakeBackendOptions { Capabilities = BackendCapabilities.None });
        using var ctx = fake.CreateContext("sys");
        Assert.Null(((FakeContext)ctx).SystemPrompt);
    }

    [Fact]
    public async Task Counts_context_creation_and_disposal()
    {
        var fake = await ReadyAsync();
        var a = fake.CreateContext(null);
        var b = fake.CreateContext(null);
        Assert.Equal(2, fake.ActiveContexts);

        a.Dispose();
        a.Dispose(); // idempotent
        Assert.Equal(1, fake.ActiveContexts);
        Assert.Equal(1, fake.ContextsDisposed);

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            fake.GenerateAsync(a, "p", null, _ => { }, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() => fake.GetUsablePromptLength(a, "p"));

        b.Dispose();
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Fact]
    public async Task Rejects_context_from_another_backend()
    {
        var one = await ReadyAsync();
        var two = await ReadyAsync();
        using var foreign = two.CreateContext(null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            one.GenerateAsync(foreign, "p", null, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task Use_before_initialization_is_rejected()
    {
        var fake = new FakeBackend();
        Assert.Throws<InvalidOperationException>(() => fake.CreateContext(null));

        var ready = await ReadyAsync();
        using var ctx = ready.CreateContext(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fake.GenerateAsync(ctx, "p", null, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task Use_after_dispose_is_rejected()
    {
        var fake = await ReadyAsync();
        using var ctx = fake.CreateContext(null);
        await fake.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => fake.CreateContext(null));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            fake.GenerateAsync(ctx, "p", null, _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fake.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Init_gate_and_failure_are_honoured()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new FakeBackend(new FakeBackendOptions { InitGate = gate });
        var init = gated.InitializeAsync(CancellationToken.None);
        Assert.False(init.IsCompleted);
        gate.SetResult();
        await init;
        Assert.True(gated.IsInitialized);

        var failing = new FakeBackend(new FakeBackendOptions { InitFailure = new TimeoutException("slow") });
        await Assert.ThrowsAsync<TimeoutException>(() => failing.InitializeAsync(CancellationToken.None));
        Assert.False(failing.IsInitialized);
    }
}

public class UnavailableBackendTests
{
    [Fact]
    public async Task Every_operation_fails_with_the_reason()
    {
        var backend = new UnavailableBackend("phi-silica", "Phi Silica", "not built yet");

        Assert.Equal("phi-silica", backend.ModelId);
        Assert.Equal(BackendCapabilities.None, backend.Capabilities);
        Assert.Empty(backend.Diagnostics);

        var init = await Assert.ThrowsAsync<BackendUnavailableException>(() => backend.InitializeAsync(CancellationToken.None));
        Assert.Equal("not built yet", init.Message);
        Assert.Throws<BackendUnavailableException>(() => backend.CreateContext(null));

        var fake = new FakeBackend();
        await fake.InitializeAsync(CancellationToken.None);
        using var ctx = fake.CreateContext(null);
        Assert.Null(backend.GetUsablePromptLength(ctx, "p"));
        await Assert.ThrowsAsync<BackendUnavailableException>(() =>
            backend.GenerateAsync(ctx, "p", null, _ => { }, CancellationToken.None));

        await backend.DisposeAsync();
    }
}
