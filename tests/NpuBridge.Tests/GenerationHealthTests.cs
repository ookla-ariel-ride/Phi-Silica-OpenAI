using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Tokenizers;

namespace NpuBridge.Tests;

public class GenerationHealthTests
{
    [Fact]
    public async Task One_backend_fault_does_not_degrade_health_and_preserves_the_exceptions_first_line()
    {
        var options = new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("RPC unavailable\r\nsecond line"),
        };
        var fake = new FakeBackend(options);
        await using var host = await BridgeTestHost.StartAsync(fake);

        var fault = await PostChatAsync(host, ChatBody.User());
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, fault.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ready", health.Body.GetProperty("status").GetString());
        Assert.Equal(1, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        var last = health.Body.GetProperty("last_generation");
        Assert.Equal("backend_fault", last.GetProperty("outcome").GetString());
        Assert.Equal("RPC unavailable", last.GetProperty("error").GetString());
        Assert.True(last.GetProperty("duration_ms").GetInt32() >= 0);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Two_faults_degrade_health_a_preflight_refusal_does_not_change_the_count_and_a_cut_success_recovers()
    {
        var options = new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("RPC unavailable"),
            Responder = _ => ["abcdefgh", " still generating"],
            DeltaGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var fake = new FakeBackend(options);
        await using var host = await BridgeTestHost.StartAsync(fake);

        Assert.Equal(HttpStatusCode.BadGateway, (await PostChatAsync(host, ChatBody.User())).StatusCode);

        options.MaxPromptChars = 1;
        var refusal = await PostChatAsync(host, ChatBody.User("too long"));
        var afterRefusal = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
        Assert.Equal(1, afterRefusal.Body.GetProperty("consecutive_backend_faults").GetInt32());

        options.MaxPromptChars = null;
        Assert.Equal(HttpStatusCode.BadGateway, (await PostChatAsync(host, ChatBody.User())).StatusCode);
        var degraded = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, degraded.StatusCode);
        Assert.Equal("degraded", degraded.Body.GetProperty("status").GetString());
        Assert.Equal("RPC unavailable", degraded.Body.GetProperty("error").GetString());
        Assert.Equal(2, degraded.Body.GetProperty("consecutive_backend_faults").GetInt32());

        options.FailAfterTokens = null;
        options.FailureException = null;
        var deltasBeforeCut = fake.DeltasEmitted;
        var recoveredTask = host.Client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fake",
            messages = new[] { new { role = "user", content = "say hi" } },
            max_tokens = 1,
        });
        await TestWait.UntilAsync(() => fake.DeltasEmitted == deltasBeforeCut + 1);
        await TestWait.UntilAsync(() => fake.CancellationsObserved == 1);
        options.DeltaGate!.SetResult();
        var recovered = await recoveredTask;
        var afterSuccess = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(deltasBeforeCut + 1, fake.DeltasEmitted);
        using var recoveredDocument = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync());
        Assert.Equal("length", recoveredDocument.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(HttpStatusCode.OK, afterSuccess.StatusCode);
        Assert.Equal("ready", afterSuccess.Body.GetProperty("status").GetString());
        Assert.Equal(0, afterSuccess.Body.GetProperty("consecutive_backend_faults").GetInt32());
        var last = afterSuccess.Body.GetProperty("last_generation");
        Assert.Equal("ok", last.GetProperty("outcome").GetString());
        Assert.True(last.GetProperty("duration_ms").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("error").ValueKind);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Streamed_and_non_streamed_faults_share_the_counter()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("RPC unavailable"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var streamed = await host.Client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fake",
            messages = new[] { new { role = "user", content = "say hi" } },
            stream = true,
        });
        var json = await PostChatAsync(host, ChatBody.User());
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, streamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, json.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal(2, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Legacy_completions_record_backend_faults()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("RPC unavailable"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var streamed = await host.Client.PostAsJsonAsync("/v1/completions", new { model = "fake", prompt = "say hi", stream = true });
        var json = await host.Client.PostAsJsonAsync("/v1/completions", new { model = "fake", prompt = "say hi" });
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, streamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, json.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal(2, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Debug_generation_records_backend_faults()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("RPC unavailable"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "say hi" });
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Debug_generation_with_a_preflight_known_overflow_does_not_change_health()
    {
        var options = new FakeBackendOptions
        {
            FailAfterTokens = 0,
            FailureStatus = GenerationStatus.Error,
        };
        var fake = new FakeBackend(options);
        await using var host = await BridgeTestHost.StartAsync(fake);

        async Task<HttpResponseMessage> PostPreflightKnownOverflowAsync()
        {
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callsBefore = fake.Calls.Count;
            options.MaxPromptChars = 1;
            options.StartGate = startGate;

            var responseTask = host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "too long" });
            await TestWait.UntilAsync(() => fake.Calls.Count == callsBefore + 1);
            options.MaxPromptChars = 100;
            startGate.SetResult();
            var response = await responseTask;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, document.RootElement.GetProperty("usable_prompt_chars").GetInt32());
            return response;
        }

        Assert.Equal(HttpStatusCode.OK, (await PostPreflightKnownOverflowAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostPreflightKnownOverflowAsync()).StatusCode);

        var unchanged = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(0, unchanged.Body.GetProperty("consecutive_backend_faults").GetInt32());
        Assert.Equal(JsonValueKind.Null, unchanged.Body.GetProperty("last_generation").ValueKind);

        options.StartGate = null;
        var fittingResponse = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "fits" });
        var afterFittingFailure = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.OK, fittingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterFittingFailure.StatusCode);
        Assert.Equal(1, afterFittingFailure.Body.GetProperty("consecutive_backend_faults").GetInt32());
        Assert.Equal("backend_fault", afterFittingFailure.Body.GetProperty("last_generation").GetProperty("outcome").GetString());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Post_generation_bridge_exception_does_not_replace_a_successful_health_outcome()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            TokenCounter = new ThrowingTokensCoveringCounter(),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostChatAsync(host, ChatBody.User());
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(0, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        Assert.Equal("ok", health.Body.GetProperty("last_generation").GetProperty("outcome").GetString());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Post_generation_tokenizer_failures_do_not_count_as_backend_faults_on_any_response_shape()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            TokenCounter = new ThrowingTokensCoveringCounter(),
            Responder = _ => ["done"],
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        using var chatJson = await PostChatAsync(host, ChatBody.User("chat json"));
        using var chatStream = await host.Client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fake",
            messages = new[] { new { role = "user", content = "chat stream" } },
            stream = true,
        });
        using var completionsJson = await host.Client.PostAsJsonAsync("/v1/completions", new { model = "fake", prompt = "completions json" });
        using var completionsStream = await host.Client.PostAsJsonAsync("/v1/completions", new { model = "fake", prompt = "completions stream", stream = true });
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, chatJson.StatusCode);
        Assert.Equal(HttpStatusCode.OK, chatStream.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, completionsJson.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completionsStream.StatusCode);
        Assert.Equal(0, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        Assert.Equal("ok", health.Body.GetProperty("last_generation").GetProperty("outcome").GetString());
        host.AssertNoLeak();
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/completions")]
    public async Task Cutter_tokenizer_failures_do_not_count_as_backend_faults_on_JSON_shapes(string path)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            TokenCounter = new ThrowingIndexAtTokenCountCounter(),
            Responder = _ => ["done"],
        });
        await using var host = await BridgeTestHost.StartAsync(fake);
        object body = path == "/v1/chat/completions"
            ? new { model = "fake", messages = new[] { new { role = "user", content = "chat json" } }, max_tokens = 1 }
            : new { model = "fake", prompt = "completions json", max_tokens = 1 };

        using var response = await host.Client.PostAsJsonAsync(path, body);
        var health = await ReadHealthAsync(host);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("server_error", error.GetProperty("type").GetString());
        Assert.Equal("backend_error", error.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("param").ValueKind);
        Assert.Equal(0, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        host.AssertNoLeak();
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/completions")]
    public async Task Cutter_tokenizer_failure_cancels_a_gated_generation_before_its_next_delta(string path)
    {
        var deltaGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions
        {
            TokenCounter = new ThrowingIndexAtTokenCountCounter(),
            Responder = _ => Enumerable.Repeat("done ", 200),
            DeltaGate = deltaGate,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);
        object body = path == "/v1/chat/completions"
            ? new { model = "fake", messages = new[] { new { role = "user", content = "chat json" } }, max_tokens = 1 }
            : new { model = "fake", prompt = "completions json", max_tokens = 1 };

        var responseTask = host.Client.PostAsJsonAsync(path, body);
        await TestWait.UntilAsync(() => fake.DeltasEmitted == 1);
        await TestWait.UntilAsync(() => fake.CancellationsObserved == 1);
        deltaGate.SetResult();

        using var response = await responseTask;
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, fake.DeltasEmitted);
        Assert.Equal(0, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Create_context_failures_count_as_backend_faults_on_every_generation_shape()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            CreateContextFailure = new InvalidOperationException("CreateContext RPC unavailable"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        using var chatJson = await PostChatAsync(host, ChatBody.User());
        using var chatStream = await host.Client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fake",
            messages = new[] { new { role = "user", content = "stream" } },
            stream = true,
        });
        using var completionsJson = await host.Client.PostAsJsonAsync("/v1/completions", new { model = "fake", prompt = "completion" });
        using var completionsStream = await host.Client.PostAsJsonAsync("/v1/completions", new { model = "fake", prompt = "stream completion", stream = true });
        using var debug = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "debug" });
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadGateway, chatJson.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, chatStream.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, completionsJson.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, completionsStream.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, debug.StatusCode);
        Assert.Equal(5, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        Assert.Equal("backend_fault", health.Body.GetProperty("last_generation").GetProperty("outcome").GetString());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Native_system_text_guard_does_not_record_a_backend_fault()
    {
        await using var host = await BridgeTestHost.StartAsync();

        using var response = await host.Client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "fake",
            messages = new[]
            {
                new { role = "system", content = new string('x', 32_001) },
                new { role = "user", content = "hello" },
            },
        });
        var health = await ReadHealthAsync(host);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        Assert.Equal(JsonValueKind.Null, health.Body.GetProperty("last_generation").ValueKind);
        host.AssertNoLeak();
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/completions")]
    public async Task Stream_write_failure_does_not_count_as_a_backend_fault(string path)
    {
        var fake = new FakeBackend();
        await using var host = await BridgeTestHost.StartAsync(
            fake,
            responseBodyFactory: context => context.Request.Path == path ? new ThrowAfterFirstWriteStream() : null);
        object body = path == "/v1/chat/completions"
            ? new { model = "fake", messages = new[] { new { role = "user", content = "hello" } }, stream = true }
            : new { model = "fake", prompt = "hello", stream = true };

        try
        {
            using var response = await host.Client.PostAsJsonAsync(path, body);
        }
        catch (IOException)
        {
            // TestServer surfaces the second failed write while serializing the ordinary error result.
        }

        await TestWait.UntilAsync(() => host.Requests.Completed > 0);
        var health = await ReadHealthAsync(host);

        Assert.NotEmpty(fake.Calls);
        Assert.Equal(0, health.Body.GetProperty("consecutive_backend_faults").GetInt32());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Backend_calls_before_generation_record_faults_and_a_success_clears_them()
    {
        var options = new FakeBackendOptions
        {
            CreateContextFailure = new InvalidOperationException("CreateContext RPC unavailable"),
        };
        var fake = new FakeBackend(options);
        await using var host = await BridgeTestHost.StartAsync(fake);

        Assert.Equal(HttpStatusCode.BadGateway, (await PostChatAsync(host, ChatBody.User())).StatusCode);
        host.AssertNoLeak();
        Assert.Equal(HttpStatusCode.BadGateway, (await PostChatAsync(host, ChatBody.User())).StatusCode);
        host.AssertNoLeak();

        var afterCreateFailures = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, afterCreateFailures.StatusCode);
        Assert.Equal("degraded", afterCreateFailures.Body.GetProperty("status").GetString());
        Assert.Equal(2, afterCreateFailures.Body.GetProperty("consecutive_backend_faults").GetInt32());

        options.CreateContextFailure = null;
        options.PreflightFailure = new InvalidOperationException("Preflight RPC unavailable");
        Assert.Equal(HttpStatusCode.BadGateway, (await PostChatAsync(host, ChatBody.User())).StatusCode);
        host.AssertNoLeak();

        var afterPreflightFailure = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, afterPreflightFailure.StatusCode);
        Assert.Equal(3, afterPreflightFailure.Body.GetProperty("consecutive_backend_faults").GetInt32());

        options.PreflightFailure = null;
        Assert.Equal(HttpStatusCode.OK, (await PostChatAsync(host, ChatBody.User())).StatusCode);
        host.AssertNoLeak();

        var recovered = await ReadHealthAsync(host);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(0, recovered.Body.GetProperty("consecutive_backend_faults").GetInt32());
    }

    private static Task<HttpResponseMessage> PostChatAsync(BridgeTestHost host, object body) =>
        host.Client.PostAsJsonAsync("/v1/chat/completions", body);

    private static async Task<(HttpStatusCode StatusCode, JsonElement Body)> ReadHealthAsync(BridgeTestHost host)
    {
        var response = await host.Client.GetAsync("/healthz");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private sealed class ThrowingIndexAtTokenCountCounter : ITokenCounter
    {
        public string Name => CharEstimateTokenCounter.Instance.Name;

        public bool PrefixStable => CharEstimateTokenCounter.Instance.PrefixStable;

        public int Count(string text) => CharEstimateTokenCounter.Instance.Count(text);

        public int IndexAtTokenCount(string text, int tokens, out int totalTokens) =>
            throw new InvalidOperationException("cut watcher tokenizer failure");

        public int TokensCovering(string text, int prefixChars) =>
            CharEstimateTokenCounter.Instance.TokensCovering(text, prefixChars);
    }

    private sealed class ThrowingTokensCoveringCounter : ITokenCounter
    {
        public string Name => CharEstimateTokenCounter.Instance.Name;

        public bool PrefixStable => CharEstimateTokenCounter.Instance.PrefixStable;

        public int Count(string text) => CharEstimateTokenCounter.Instance.Count(text);

        public int IndexAtTokenCount(string text, int tokens, out int totalTokens) =>
            CharEstimateTokenCounter.Instance.IndexAtTokenCount(text, tokens, out totalTokens);

        public int TokensCovering(string text, int prefixChars) => throw new InvalidOperationException("post-generation usage failure");
    }

    private sealed class ThrowAfterFirstWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private int _writes;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowAfterFirstWrite();
            _inner.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowAfterFirstWrite();
            await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowAfterFirstWrite();
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        private void ThrowAfterFirstWrite()
        {
            if (Interlocked.Increment(ref _writes) > 1)
            {
                throw new IOException("test response body write failure");
            }
        }
    }
}
