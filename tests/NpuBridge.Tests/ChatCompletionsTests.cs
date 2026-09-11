using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;

namespace NpuBridge.Tests;

public class ChatCompletionsTests
{
    private const string Path = "/v1/chat/completions";

    [Fact]
    public async Task Minimal_request_returns_an_openai_chat_completion()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["Hello", " ", "world"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            messages = new[] { new { role = "user", content = "say hi" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await ReadJson(response);
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.StartsWith("chatcmpl-", root.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal("fake", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("created").GetInt64() > 0);

        var choice = Assert.Single(root.GetProperty("choices").EnumerateArray().ToArray());
        Assert.Equal(0, choice.GetProperty("index").GetInt32());
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Hello world", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());

        host.AssertNoLeak();
    }

    [Fact]
    public async Task Model_falls_back_to_the_backend_id_when_the_request_omits_it()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["x"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new { messages = new[] { new { role = "user", content = "hi" } } });

        var root = await ReadJson(response);
        Assert.Equal("fake", root.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Usage_counts_characters_over_four_rounded_up()
    {
        // Prompt "0123456789" is 10 chars -> 3 tokens. Output "abcde" is 5 chars -> 2 tokens.
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["abcde"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            messages = new[] { new { role = "user", content = "0123456789" } },
        });

        var usage = (await ReadJson(response)).GetProperty("usage");
        Assert.Equal(3, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(2, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task Usage_counts_native_system_text_that_never_enters_the_prompt()
    {
        // Prompt is the 8-char user text; the 4-char system text rides the native context but is billed.
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => [""] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            messages = new object[]
            {
                new { role = "system", content = "abcd" },
                new { role = "user", content = "12345678" },
            },
        });

        var root = await ReadJson(response);
        var promptChars = Assert.Single(host.Fake.Calls).Prompt.Length;
        Assert.Equal((promptChars + 4 + 3) / 4, root.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
    }

    [Fact]
    public async Task Bare_single_user_message_reaches_the_backend_raw()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            messages = new[] { new { role = "user", content = "What is 2 + 2?" } },
        });

        var call = Assert.Single(fake.Calls);
        Assert.Equal("What is 2 + 2?", call.Prompt);
        Assert.Null(call.SystemPrompt);
    }

    [Fact]
    public async Task A_conversation_reaches_the_backend_with_the_template_markers()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            messages = new object[]
            {
                new { role = "user", content = "first" },
                new { role = "assistant", content = "answer" },
                new { role = "user", content = "second" },
            },
        });

        var call = Assert.Single(fake.Calls);
        Assert.Equal(
            "### Conversation so far\n[User]\nfirst\n[Assistant]\nanswer\n\n### Reply as the assistant to the latest message.\n[User]\nsecond",
            call.Prompt);
    }

    [Fact]
    public async Task System_text_goes_to_the_native_context_when_the_backend_has_one()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await PostConversationWithSystem(host);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("be terse", call.SystemPrompt);
        Assert.DoesNotContain("be terse", call.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task System_text_is_folded_into_the_prompt_when_the_backend_has_no_native_context()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = BackendCapabilities.None,
            Responder = _ => ["ok"],
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await PostConversationWithSystem(host);

        var call = Assert.Single(fake.Calls);
        Assert.Null(call.SystemPrompt);
        Assert.StartsWith("be terse\n\n### Conversation so far", call.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Placement_prompt_folds_the_system_text_even_when_a_native_context_exists()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, Options(SystemPromptPlacement.Prompt));

        await PostConversationWithSystem(host);

        var call = Assert.Single(fake.Calls);
        Assert.Null(call.SystemPrompt);
        Assert.StartsWith("be terse\n\n", call.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Placement_native_uses_the_context_when_the_backend_supports_it()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, Options(SystemPromptPlacement.Native));

        await PostConversationWithSystem(host);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("be terse", call.SystemPrompt);
        Assert.DoesNotContain("be terse", call.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Placement_native_is_400_when_the_backend_has_no_native_context_and_the_request_has_system_text()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = BackendCapabilities.None,
            Responder = _ => ["ok"],
        });
        await using var host = await BridgeTestHost.StartAsync(fake, Options(SystemPromptPlacement.Native));

        var response = await PostConversationWithSystem(host);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("system_prompt_placement_unsupported", error.GetProperty("code").GetString());
        Assert.Empty(fake.Calls);
        host.AssertNoLeak();
    }

    /// <summary>
    /// The placement conflict is a property of the request, not of the option alone: a request with no
    /// system message needs no native context, so forcing <c>native</c> on a backend that has none must
    /// still serve it. Rejecting on the option alone would reject every request on such a backend, which
    /// is what chunk 6's Aion backend will be.
    /// </summary>
    [Fact]
    public async Task Placement_native_still_serves_a_request_with_no_system_message_on_a_backend_with_no_native_context()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = BackendCapabilities.None,
            Responder = _ => ["ok"],
        });
        await using var host = await BridgeTestHost.StartAsync(fake, Options(SystemPromptPlacement.Native));

        var response = await host.Client.PostAsJsonAsync(Path, Simple());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await ReadJson(response);
        var choice = Assert.Single(root.GetProperty("choices").EnumerateArray().ToArray());
        Assert.Equal("ok", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());

        var call = Assert.Single(fake.Calls);
        Assert.Null(call.SystemPrompt);
        Assert.Equal("say hi", call.Prompt);
        Assert.Equal(1, fake.ContextsCreated);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Loading_backend_is_503_with_retry_after_and_model_loading()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeBackend(new FakeBackendOptions { InitGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake, waitForReady: false);

        var response = await host.Client.PostAsJsonAsync(Path, Simple());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("10", response.Headers.RetryAfter?.ToString());
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("model_loading", error.GetProperty("code").GetString());
        Assert.Equal("server_error", error.GetProperty("type").GetString());
        host.AssertNoLeak();

        gate.SetResult();
        await host.Lifecycle.Initialization;
    }

    [Fact]
    public async Task Failed_backend_is_503_model_unavailable()
    {
        var fake = new FakeBackend(new FakeBackendOptions { InitFailure = new InvalidOperationException("nope") });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Simple());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("model_unavailable", error.GetProperty("code").GetString());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Prompt_larger_than_context_is_400_context_length_exceeded()
    {
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 3, Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Simple("a much longer prompt than three characters"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Generation_error_is_502()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["a"],
            FailAfterTokens = 0,
            FailureStatus = GenerationStatus.Error,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Simple());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("server_error", (await ReadJson(response)).GetProperty("error").GetProperty("type").GetString());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Backend_exception_is_502_and_disposes_the_context()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["a"],
            FailAfterTokens = 0,
            FailureException = new InvalidOperationException("kaboom"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Simple());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("backend_error", (await ReadJson(response)).GetProperty("error").GetProperty("code").GetString());
        host.AssertNoLeak();
    }

    [Theory]
    [InlineData(GenerationStatus.ContentFiltered)]
    [InlineData(GenerationStatus.BlockedByPolicy)]
    public async Task Filtered_content_is_200_with_empty_content_and_content_filter(GenerationStatus status)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["some", " text"],
            FailAfterTokens = 1,
            FailureStatus = status,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Simple());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var choice = (await ReadJson(response)).GetProperty("choices")[0];
        Assert.Equal(string.Empty, choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("content_filter", choice.GetProperty("finish_reason").GetString());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Malformed_json_is_400_not_500()
    {
        await using var host = await BridgeTestHost.StartAsync();

        using var content = new StringContent("{\"messages\": [", Encoding.UTF8, "application/json");
        var response = await host.Client.PostAsync(Path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task A_non_json_body_is_400_not_500()
    {
        await using var host = await BridgeTestHost.StartAsync();

        using var content = new StringContent("not json at all", Encoding.UTF8, "text/plain");
        var response = await host.Client.PostAsync(Path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request_error", (await ReadJson(response)).GetProperty("error").GetProperty("type").GetString());
    }

    /// <summary>scripts/smoke.ps1 feature-detects this endpoint by posting {} and requiring a 400.</summary>
    [Fact]
    public async Task An_empty_json_object_is_400_so_smoke_can_feature_detect()
    {
        await using var host = await BridgeTestHost.StartAsync();

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await host.Client.PostAsync(Path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("messages", error.GetProperty("param").GetString());
    }

    /// <summary>
    /// Regression: <c>{"messages":[null]}</c> used to escape validation as an unhandled exception and
    /// come back as a 500. Validation runs before the handler's try block, so a null element has to be
    /// caught by the validator itself.
    /// </summary>
    [Theory]
    [InlineData("""{"messages":[null]}""")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"},null]}""")]
    [InlineData("""{"messages":[null,{"role":"user","content":"hi"}]}""")]
    public async Task A_null_message_element_is_400_not_500(string json)
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await PostRaw(host, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("messages", error.GetProperty("param").GetString());
        Assert.Empty(host.Fake.Calls);
        host.AssertNoLeak();
    }

    /// <summary>The content-part converter rejects a null part while reading; this pins that it stays a 400.</summary>
    [Fact]
    public async Task A_null_content_part_is_400()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await PostRaw(host, """{"messages":[{"role":"user","content":[null]}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request_error", (await ReadJson(response)).GetProperty("error").GetProperty("type").GetString());
        Assert.Empty(host.Fake.Calls);
    }

    [Theory]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text"}]}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text","text":null}]}]}""")]
    public async Task A_text_content_part_with_no_text_is_400_and_never_generates(string json)
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await PostRaw(host, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("messages", (await ReadJson(response)).GetProperty("error").GetProperty("param").GetString());
        Assert.Empty(host.Fake.Calls);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task An_empty_text_content_part_still_generates()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostRaw(host, """{"model":"fake","messages":[{"role":"user","content":[{"type":"text","text":""}]}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, Assert.Single(fake.Calls).Prompt);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task More_than_one_choice_is_400_with_param_n()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            n = 2,
            messages = new[] { new { role = "user", content = "hi" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("n", (await ReadJson(response)).GetProperty("error").GetProperty("param").GetString());
    }

    /// <summary>
    /// ChatCompletionValidationFailure orders its fields (Message, Param, Code) while OpenAiError.Result
    /// takes code before param: a positional pass-through would put "n" in code and null in param. This
    /// test fails the moment that happens.
    /// </summary>
    [Fact]
    public async Task Validation_failure_keeps_param_and_code_in_their_own_fields()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            n = 2,
            messages = new[] { new { role = "user", content = "hi" } },
        });

        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("n", error.GetProperty("param").GetString());
        Assert.False(error.TryGetProperty("code", out _), "the n validation failure sets no code; a swap would put 'n' there");
    }

    [Fact]
    public async Task An_image_content_part_is_400_with_param_messages()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "what is this?" },
                        new { type = "image_url", image_url = new { url = "https://example.invalid/x.png" } },
                    },
                },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("messages", error.GetProperty("param").GetString());
        Assert.Empty(host.Fake.Calls);
    }

    [Fact]
    public async Task Get_on_the_chat_completions_path_is_405_with_allow()
    {
        await using var host = await BridgeTestHost.StartAsync();

        var response = await host.Client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("POST", string.Join(",", response.Content.Headers.Allow));
        Assert.Equal("method_not_allowed", (await ReadJson(response)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Sampling_options_reach_a_backend_that_supports_them()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            temperature = 0.3,
            top_p = 0.8,
            top_k = 10,
            messages = new[] { new { role = "user", content = "hi" } },
        });

        var call = Assert.Single(fake.Calls);
        Assert.Equal(0.3f, call.Sampling?.Temperature);
        Assert.Equal(0.8f, call.Sampling?.TopP);
        Assert.Equal(10, call.Sampling?.TopK);
    }

    [Fact]
    public async Task Unspecified_sampling_values_are_not_invented()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            temperature = 0.5,
            messages = new[] { new { role = "user", content = "hi" } },
        });

        var call = Assert.Single(fake.Calls);
        Assert.Equal(0.5f, call.Sampling?.Temperature);
        Assert.Null(call.Sampling?.TopP);
        Assert.Null(call.Sampling?.TopK);
    }

    [Fact]
    public async Task Sampling_is_dropped_and_warned_when_the_backend_lacks_it()
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = BackendCapabilities.None,
            Responder = _ => ["ok"],
        });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            temperature = 0.3,
            messages = new[] { new { role = "user", content = "hi" } },
        });

        var call = Assert.Single(fake.Calls);
        Assert.Null(call.Sampling);
        Assert.Contains(capture.Records, r => r.Level == LogLevel.Warning && r.Message.Contains("temperature", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ignored_parameters_are_warned_once_per_process_not_once_per_request()
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        var body = new
        {
            model = "fake",
            seed = 42,
            user = "u-123",
            // Implemented by the client-side cut since chunk 4 (D53), so they must not be warned about
            // even though they sit right next to the two that still are.
            max_tokens = 64,
            stop = "END",
            messages = new[] { new { role = "user", content = "hi" } },
        };
        await host.Client.PostAsJsonAsync(Path, body);
        await host.Client.PostAsJsonAsync(Path, body);

        var warnings = capture.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings, w => w.Message.Contains("seed", StringComparison.Ordinal));
        Assert.Single(warnings, w => w.Message.Contains("user", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Message.Contains("max_tokens", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Message.Contains("stop", StringComparison.Ordinal));
        // temperature was never sent, so it is never warned about.
        Assert.DoesNotContain(warnings, w => w.Message.Contains("temperature", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_completed_request_logs_one_metrics_line()
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["Hello", " world"] });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        await host.Client.PostAsJsonAsync(Path, Simple("say hi"));

        var line = Assert.Single(capture.Records, r => r.Message.StartsWith("req=chatcmpl-", StringComparison.Ordinal));
        Assert.Contains("backend=fake", line.Message, StringComparison.Ordinal);
        Assert.Contains("prompt_chars=6", line.Message, StringComparison.Ordinal);
        Assert.Contains("tokens=3", line.Message, StringComparison.Ordinal);
        Assert.Contains("status=Complete", line.Message, StringComparison.Ordinal);
        Assert.Contains("finish=stop", line.Message, StringComparison.Ordinal);
        Assert.Contains("http=200", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verbose_echoes_the_prompt_the_system_placement_and_the_raw_output()
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["raw model text"] });
        var options = new BridgeOptions { Backend = BackendKind.Fake, Verbose = true };
        await using var host = await BridgeTestHost.StartAsync(fake, options, loggerProvider: capture);

        await PostConversationWithSystem(host);

        var messages = capture.Records.Select(r => r.Message).ToList();
        Assert.Contains(messages, m => m.Contains("native context", StringComparison.Ordinal) && m.Contains("be terse", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("raw model output", StringComparison.Ordinal) && m.Contains("raw model text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verbose_is_silent_about_prompts_when_it_is_off()
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["raw model text"] });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        await PostConversationWithSystem(host);

        Assert.DoesNotContain(capture.Records, r => r.Message.Contains("raw model text", StringComparison.Ordinal));
    }

    /// <summary>
    /// The leak guard: whatever the outcome, every context the backend created was disposed. Asserted
    /// explicitly on created-vs-disposed counts, not on a side effect. <c>expectedContexts</c> keeps the
    /// guard honest: created-equals-disposed is trivially true when nothing is created, so each case
    /// also pins how many contexts should have existed — one for anything that reaches the backend,
    /// zero for a request rejected before it. Chunks 5 and 8 lean on this guard harder than chunk 3.
    /// </summary>
    [Fact]
    public async Task Every_outcome_disposes_every_context_it_created()
    {
        // A generation that ended Complete parks its context in the cache (chunk 5); every other
        // outcome disposes it (D11). Either way nothing is left dangling, and shutdown empties the cache.
        await AssertBalanced(new FakeBackendOptions { Responder = _ => ["ok"] }, Simple(), expectedContexts: 1, expectedCached: 1);
        await AssertBalanced(new FakeBackendOptions { MaxPromptChars = 2, Responder = _ => ["ok"] }, Simple("a long prompt"), expectedContexts: 1, expectedCached: 0);
        await AssertBalanced(new FakeBackendOptions { Responder = _ => ["a"], FailAfterTokens = 0, FailureStatus = GenerationStatus.Error }, Simple(), expectedContexts: 1, expectedCached: 0);
        await AssertBalanced(new FakeBackendOptions { Responder = _ => ["a"], FailAfterTokens = 0, FailureStatus = GenerationStatus.ContentFiltered }, Simple(), expectedContexts: 1, expectedCached: 0);
        await AssertBalanced(new FakeBackendOptions { Responder = _ => ["a"], FailAfterTokens = 0, FailureException = new InvalidOperationException("boom") }, Simple(), expectedContexts: 1, expectedCached: 0);

        // A streamed request reaches the backend like any other, so it creates one context and must
        // account for it too -- the response outliving the generation call is exactly what makes
        // streaming the easy place to leak one.
        await AssertBalanced(new FakeBackendOptions { Responder = _ => ["ok"] }, new { model = "fake", stream = true, messages = new[] { new { role = "user", content = "hi" } } }, expectedContexts: 1, expectedCached: 1);

        // Rejected before the backend is touched: zero created is the right expectation here.
        await AssertBalanced(new FakeBackendOptions { Responder = _ => ["ok"] }, new { model = "fake", n = 2, messages = new[] { new { role = "user", content = "hi" } } }, expectedContexts: 0, expectedCached: 0);
        await AssertBalanced(new FakeBackendOptions { Responder = _ => ["ok"] }, new { model = "fake" }, expectedContexts: 0, expectedCached: 0);

        static async Task AssertBalanced(FakeBackendOptions options, object body, int expectedContexts, int expectedCached)
        {
            var fake = new FakeBackend(options);
            var host = await BridgeTestHost.StartAsync(fake);
            await host.Client.PostAsJsonAsync(Path, body);
            Assert.Equal(expectedContexts, fake.ContextsCreated);
            Assert.Equal(expectedCached, host.Cache.Count);
            host.AssertNoLeak();

            await host.DisposeAsync();
            Assert.Equal(fake.ContextsCreated, fake.ContextsDisposed);
            Assert.Equal(0, fake.ActiveContexts);
        }
    }

    [Fact]
    public async Task Client_disconnect_disposes_the_context_and_does_not_500()
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => Enumerable.Repeat("tok ", 200),
            TokenDelay = TimeSpan.FromMilliseconds(20),
        });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        using var cts = new CancellationTokenSource();
        var post = host.Client.PostAsJsonAsync(Path, Simple(), cts.Token);

        // Disconnect on an observed signal rather than after a fixed delay: on a loaded machine a
        // stopwatch fires before the request has reached the handler, and then the test asserts nothing
        // about a disconnect — it asserts that a request nobody started leaked no context. Waiting for
        // the backend to have been called is the same thing the streaming disconnect tests do by
        // reading a byte off the response first.
        await WaitUntilAsync(() => fake.Calls.Count > 0);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => post);

        await WaitUntilAsync(() => fake.ActiveContexts == 0);
        Assert.Equal(fake.ContextsCreated, fake.ContextsDisposed);
        Assert.Equal(0, fake.ActiveContexts);
        Assert.Single(fake.Calls);
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

    private static BridgeOptions Options(SystemPromptPlacement placement) =>
        new() { Backend = BackendKind.Fake, SystemPromptPlacement = placement };

    private static object Simple(string content = "say hi") =>
        new { model = "fake", messages = new[] { new { role = "user", content } } };

    private static Task<HttpResponseMessage> PostConversationWithSystem(BridgeTestHost host) =>
        host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            messages = new object[]
            {
                new { role = "system", content = "be terse" },
                new { role = "user", content = "hi" },
            },
        });

    /// <summary>Posts a literal JSON body, for shapes an anonymous object cannot express (nulls, junk).</summary>
    private static async Task<HttpResponseMessage> PostRaw(BridgeTestHost host, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await host.Client.PostAsync(Path, content);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}
