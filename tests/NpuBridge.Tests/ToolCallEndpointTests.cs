using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;

namespace NpuBridge.Tests;

/// <summary>
/// Tool-call emulation through the whole pipeline, on both response shapes (chunk 7, PLAN §2.6).
/// The parser has its own adversarial suite; this is about the wiring around it — that the
/// instruction reaches the model, that a reply which parses becomes <c>tool_calls</c> with
/// <c>content: null</c> and <c>finish_reason: "tool_calls"</c>, that one that does not is ordinary
/// content, and that the streamed shape holds everything back until it knows which.
/// </summary>
public class ToolCallEndpointTests
{
    private const string Path = "/v1/chat/completions";

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
                    required = new[] { "location" },
                },
            },
        },
    ];

    private static readonly object[] Time =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "get_time",
                description = "Get the current time",
            },
        },
    ];

    private static object Body(bool stream, object? tools = null, object? toolChoice = null, string content = "weather in paris?") => new
    {
        model = "fake",
        stream,
        tools,
        tool_choice = toolChoice,
        messages = new[] { new { role = "user", content } },
    };

    private static async Task<JsonElement> PostAsync(BridgeTestHost host, object body)
    {
        var response = await host.Client.PostAsJsonAsync(Path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>The reply the model is instructed to produce, in the fenced form the injection asks for.</summary>
    private static readonly string FencedCall =
        "```json\n" + """{"tool_calls":[{"name":"get_weather","arguments":{"location":"Paris"}}]}""" + "\n```";

    // ---- the instruction reaches the model -------------------------------------------------------

    [Fact]
    public async Task The_tool_instruction_is_injected_into_the_system_text_the_backend_sees()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await PostAsync(host, Body(stream: false, tools: Weather));

        var call = Assert.Single(fake.Calls);
        Assert.Contains("You can call tools. Available tools:", call.SystemPrompt ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("get_weather(location: string)", call.SystemPrompt ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Three switches turn the feature off, and each must do it completely: no instruction reaches the
    /// model, so nothing invites a call, and the reply is never parsed. They share one path — the
    /// catalog is null — so this pins all three against that one behaviour.
    /// </summary>
    [Theory]
    [InlineData(false, null, false)]          // no tools offered
    [InlineData(true, "none", true)]          // tool_choice: none
    [InlineData(true, null, false)]           // --tool-emulation off
    public async Task Nothing_is_injected_when_emulation_is_off_or_unasked(bool offerTools, string? choice, bool emulation)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => [FencedCall] });
        await using var host = await BridgeTestHost.StartAsync(fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, ToolEmulation = emulation });

        var body = await PostAsync(host, Body(stream: false, tools: offerTools ? Weather : null, toolChoice: choice));

        Assert.DoesNotContain("You can call tools", Assert.Single(fake.Calls).SystemPrompt ?? string.Empty, StringComparison.Ordinal);

        // And the reply that would have parsed is handed back as content instead.
        var message = body.GetProperty("choices")[0].GetProperty("message");
        Assert.Equal(JsonValueKind.String, message.GetProperty("content").ValueKind);
        Assert.False(message.TryGetProperty("tool_calls", out _));
        Assert.Equal("stop", body.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    // ---- the non-streaming shape -----------------------------------------------------------------

    [Fact]
    public async Task A_parsed_call_becomes_tool_calls_with_null_content_and_a_tool_calls_finish()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => [FencedCall] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await PostAsync(host, Body(stream: false, tools: Weather));

        var choice = body.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());

        var message = choice.GetProperty("message");
        Assert.Equal(JsonValueKind.Null, message.GetProperty("content").ValueKind);

        var call = Assert.Single(message.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.StartsWith("call_", call.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());

        // Arguments are JSON *text*, per OpenAI's schema — never a nested object.
        var arguments = call.GetProperty("function").GetProperty("arguments");
        Assert.Equal(JsonValueKind.String, arguments.ValueKind);
        Assert.Equal("Paris", JsonDocument.Parse(arguments.GetString()!).RootElement.GetProperty("location").GetString());
    }

    [Fact]
    public async Task A_short_form_zero_argument_call_is_emitted_on_both_shapes()
    {
        const string reply = """{"name":"get_time"}""";
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => FakeBackend.Tokenize(reply) });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var json = await PostAsync(host, Body(stream: false, tools: Time));
        var jsonChoice = json.GetProperty("choices")[0];
        var jsonMessage = jsonChoice.GetProperty("message");
        Assert.Equal("tool_calls", jsonChoice.GetProperty("finish_reason").GetString());
        Assert.Equal(JsonValueKind.Null, jsonMessage.GetProperty("content").ValueKind);
        var jsonCall = Assert.Single(jsonMessage.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("get_time", jsonCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{}", jsonCall.GetProperty("function").GetProperty("arguments").GetString());

        var streamText = await (await host.Client.PostAsJsonAsync(Path, Body(stream: true, tools: Time))).Content.ReadAsStringAsync();
        var streamCall = Sse.Chunks(streamText)
            .First(c => c.GetProperty("choices").GetArrayLength() > 0
                && c.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("tool_calls", out _))
            .GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0];
        Assert.Equal("get_time", streamCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{}", streamCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool_calls", Sse.Chunks(streamText)[^1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Theory]
    [InlineData("```json\n{\"name\":\"get_time\",\"description\":\"Get the current time\"}\n```")]
    [InlineData("The available tool is {\"name\":\"get_time\",\"description\":\"Get the current time\"}.")]
    public async Task An_echoed_zero_argument_tool_definition_stays_content(string reply)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => FakeBackend.Tokenize(reply) });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await PostAsync(host, Body(stream: false, tools: Time));

        var choice = body.GetProperty("choices")[0];
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal(reply, choice.GetProperty("message").GetProperty("content").GetString());
        Assert.False(choice.GetProperty("message").TryGetProperty("tool_calls", out _));
    }

    [Fact]
    public async Task A_reply_that_is_not_a_call_stays_ordinary_content()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["It is ", "sunny in Paris."] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await PostAsync(host, Body(stream: false, tools: Weather));

        var choice = body.GetProperty("choices")[0];
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("It is sunny in Paris.", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.False(choice.GetProperty("message").TryGetProperty("tool_calls", out _));
    }

    /// <summary>
    /// A tool call costs the tokens the model spent writing it. Reporting zero would tell a client
    /// budgeting its context that the call was free, which is exactly wrong: the JSON is usually longer
    /// than the prose answer it replaced.
    /// </summary>
    [Fact]
    public async Task A_tool_call_still_reports_the_completion_tokens_it_cost()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => [FencedCall] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await PostAsync(host, Body(stream: false, tools: Weather));

        Assert.True(body.GetProperty("usage").GetProperty("completion_tokens").GetInt32() > 0);
    }

    /// <summary>A tool nobody offered is surfaced, not filtered: the client decides what to do with it.</summary>
    [Fact]
    public async Task An_unknown_tool_name_is_surfaced_to_the_client()
    {
        var reply = """{"tool_calls":[{"name":"rm_rf","arguments":{}}]}""";
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => [reply] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await PostAsync(host, Body(stream: false, tools: Weather));

        var call = Assert.Single(body.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("rm_rf", call.GetProperty("function").GetProperty("name").GetString());
    }

    // ---- the streaming shape ---------------------------------------------------------------------

    [Fact]
    public async Task A_streamed_call_arrives_as_one_tool_calls_chunk_then_the_finish_chunk()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => FakeBackend.Tokenize(FencedCall) });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Body(stream: true, tools: Weather));
        var text = await response.Content.ReadAsStringAsync();
        var chunks = Sse.Chunks(text);

        Assert.Equal("assistant", chunks[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());

        var callChunk = Assert.Single(chunks, c =>
            c.GetProperty("choices").GetArrayLength() > 0
            && c.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("tool_calls", out _));
        var call = Assert.Single(callChunk.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls").EnumerateArray());
        Assert.Equal(0, call.GetProperty("index").GetInt32());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());

        Assert.Equal("tool_calls", chunks[^1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.EndsWith("data: [DONE]\n\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of buffering: not one character of the reply goes out before the parse has decided
    /// what it is. A client that saw the JSON arrive as content deltas and then also received
    /// <c>tool_calls</c> would render the protocol to the user and call the tool.
    /// </summary>
    [Fact]
    public async Task Nothing_of_a_buffered_reply_is_emitted_as_content_before_the_parse_decides()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => FakeBackend.Tokenize(FencedCall) });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Body(stream: true, tools: Weather));
        var text = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("tool_calls\\\":", text, StringComparison.Ordinal);
        Assert.DoesNotContain("```", text, StringComparison.Ordinal);
        Assert.All(Sse.Chunks(text), chunk =>
        {
            if (chunk.GetProperty("choices").GetArrayLength() == 0)
            {
                return;
            }

            var delta = chunk.GetProperty("choices")[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                Assert.Equal(string.Empty, content.GetString());
            }
        });
    }

    /// <summary>
    /// A buffered reply that is not a call still reaches the client whole, as one content chunk. A
    /// client concatenating deltas sees exactly what the non-streamed shape returns.
    /// </summary>
    [Fact]
    public async Task A_buffered_reply_that_is_not_a_call_is_delivered_as_content()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["It is ", "sunny in Paris."] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Body(stream: true, tools: Weather));
        var text = await response.Content.ReadAsStringAsync();

        var content = string.Concat(Sse.Chunks(text)
            .Where(c => c.GetProperty("choices").GetArrayLength() > 0)
            .Select(c => c.GetProperty("choices")[0].GetProperty("delta"))
            .Where(d => d.TryGetProperty("content", out var v) && v.ValueKind == JsonValueKind.String)
            .Select(d => d.GetProperty("content").GetString()));

        Assert.Equal("It is sunny in Paris.", content);
        Assert.Equal("stop", Sse.Chunks(text)[^1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    /// <summary>
    /// Both shapes must answer the same generation the same way — the drift D56 and D57 record, in the
    /// one feature that decides its answer after the fact. The reply is fixed, so anything that differs
    /// is the pipeline, not the model.
    /// </summary>
    [Fact]
    public async Task Both_shapes_report_the_same_call_for_the_same_reply()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => FakeBackend.Tokenize(FencedCall) });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var json = await PostAsync(host, Body(stream: false, tools: Weather));
        var streamText = await (await host.Client.PostAsJsonAsync(Path, Body(stream: true, tools: Weather))).Content.ReadAsStringAsync();

        var jsonCall = json.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0];
        var streamCall = Sse.Chunks(streamText)
            .First(c => c.GetProperty("choices").GetArrayLength() > 0
                && c.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("tool_calls", out _))
            .GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0];

        Assert.Equal(jsonCall.GetProperty("function").GetProperty("name").GetString(),
            streamCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(jsonCall.GetProperty("function").GetProperty("arguments").GetString(),
            streamCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool_calls", json.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal("tool_calls", Sse.Chunks(streamText)[^1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    /// <summary>
    /// Offering different tools must not share a cached context with a request that offered others:
    /// the instruction is part of the system text, so the conversation key covers it (D71). Two
    /// requests with the same messages and different tools therefore both miss.
    /// </summary>
    [Fact]
    public async Task Requests_offering_different_tools_do_not_share_a_context()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        object[] other =
        [
            new { type = "function", function = new { name = "list_files", description = "List files" } },
        ];

        await PostAsync(host, Body(stream: false, tools: Weather));
        await PostAsync(host, Body(stream: false, tools: other));

        Assert.Equal(2, fake.ContextsCreated);
        Assert.NotEqual(fake.Calls[0].SystemPrompt, fake.Calls[1].SystemPrompt);
        host.AssertNoLeak();
    }

    /// <summary>
    /// The next turn of an agent loop hits the cache. This is the case the context cache exists for
    /// and the one a tool-using client is always in: call, result, call again.
    ///
    /// It did not work at first. The reply was stored under the raw text the model wrote — fence,
    /// prose and all — while the client sends back the <c>tool_calls</c> array the bridge emitted, so
    /// the two could never key the same and every tool-using conversation missed on every turn. Found
    /// by an adversarial review (D83); the fix stores the transcript form, built through the same
    /// <c>PromptTemplate.TurnText</c> that renders it back.
    /// </summary>
    [Fact]
    public async Task The_turn_after_a_tool_call_hits_the_cached_context()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => [FencedCall] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var first = await PostAsync(host, Body(stream: false, tools: Weather));
        var call = first.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0];

        // Exactly what a client sends next: our own assistant message back, then the tool's result.
        var followUp = new
        {
            model = "fake",
            tools = Weather,
            messages = new object[]
            {
                new { role = "user", content = "weather in paris?" },
                new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = new[]
                    {
                        new
                        {
                            id = call.GetProperty("id").GetString(),
                            type = "function",
                            function = new
                            {
                                name = call.GetProperty("function").GetProperty("name").GetString(),
                                arguments = call.GetProperty("function").GetProperty("arguments").GetString(),
                            },
                        },
                    },
                },
                new { role = "tool", name = "get_weather", tool_call_id = call.GetProperty("id").GetString(), content = "18C, clear" },
                new { role = "user", content = "and tomorrow?" },
            },
        };

        var before = host.Cache.Hits;
        await PostAsync(host, followUp);

        Assert.Equal(before + 1, host.Cache.Hits);

        // And the model was sent only the new turns, not the whole conversation again.
        Assert.Equal(2, fake.Calls.Count);
        Assert.DoesNotContain("weather in paris?", fake.Calls[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("and tomorrow?", fake.Calls[1].Prompt, StringComparison.Ordinal);
        host.AssertNoLeak();
    }

    /// <summary>
    /// A buffered reply still says something on the wire while it generates. Nothing of the reply can
    /// go out until the parse decides, so the keep-alive comment is the only thing standing between a
    /// long tool-call generation and a client or proxy calling the connection dead.
    ///
    /// The first version starved: it waited a fresh interval after every delta, so a model producing
    /// deltas faster than the interval completed every wait before its timer and the response said
    /// nothing at all for the whole generation — the exact silence buffering needs keep-alives for.
    /// The deadline now runs from the last frame written, not the last delta received. Found by an
    /// adversarial review (D83).
    /// </summary>
    [Fact]
    public async Task A_buffered_reply_keeps_the_connection_alive_while_deltas_keep_arriving()
    {
        // Deltas arrive steadily and forever-ish; the keep-alive interval is shorter than the reply is
        // long, so a correct implementation must emit at least one comment during the drain.
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => Enumerable.Repeat("word ", 40),
            TokenDelay = TimeSpan.FromMilliseconds(5),
        });
        await using var host = await BridgeTestHost.StartAsync(fake,
            keepAliveInterval: TimeSpan.FromMilliseconds(20),
            firstKeepAliveDelay: TimeSpan.FromMilliseconds(20));

        var text = await (await host.Client.PostAsJsonAsync(Path, Body(stream: true, tools: Weather))).Content.ReadAsStringAsync();

        Assert.Contains(": keep-alive", text, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>index</c> belongs to the streaming shape only. A client assembles the array across chunks by
    /// it, and OpenAI's non-streaming tool call has just <c>id</c>, <c>type</c> and <c>function</c> —
    /// so writing it on both was one type tidier and wrong by D77, which is a decision to follow the
    /// schema's shapes exactly. A client generated from that schema rejects an unknown field.
    /// </summary>
    [Fact]
    public async Task Index_is_written_on_the_streamed_call_and_omitted_from_the_json_one()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => [FencedCall] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var json = await PostAsync(host, Body(stream: false, tools: Weather));
        Assert.False(json.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0]
            .TryGetProperty("index", out _));

        var text = await (await host.Client.PostAsJsonAsync(Path, Body(stream: true, tools: Weather))).Content.ReadAsStringAsync();
        var streamed = Sse.Chunks(text)
            .First(c => c.GetProperty("choices").GetArrayLength() > 0
                && c.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("tool_calls", out _))
            .GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0];
        Assert.Equal(0, streamed.GetProperty("index").GetInt32());
    }

    /// <summary>
    /// A cut keeps its label even when what survived parses as a call. The budget fired, so the model
    /// had not finished, and reporting <c>tool_calls</c> would tell a client that resumes on
    /// <c>length</c> there is nothing left to resume. It gets both facts: the calls, and the truth
    /// that the text was truncated.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cut_that_still_parses_reports_length_rather_than_tool_calls(bool stream)
    {
        // The budget lands inside the trailing prose, after a complete call has already been written.
        var reply = """{"tool_calls":[{"name":"get_weather","arguments":{"location":"Paris"}}]}""";
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => FakeBackend.Tokenize(reply + " and then some more words to overrun the budget") });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = new
        {
            model = "fake",
            stream,
            tools = Weather,
            max_tokens = reply.Length / 4,
            messages = new[] { new { role = "user", content = "weather in paris?" } },
        };

        var response = await host.Client.PostAsJsonAsync(Path, body);
        var text = await response.Content.ReadAsStringAsync();

        var finish = stream
            ? Sse.Chunks(text)[^1].GetProperty("choices")[0].GetProperty("finish_reason").GetString()
            : JsonDocument.Parse(text).RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString();

        Assert.Equal("length", finish);
    }

    /// <summary>
    /// A filtered reply is never parsed, on either shape — it is the one path where withheld text
    /// could come back as arguments the client would execute — and it reports no completion tokens,
    /// because with tools present nothing was delivered before the filter was known.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_filtered_reply_with_tools_present_yields_no_call_and_no_delivered_tokens(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => FakeBackend.Tokenize(FencedCall),
            FailAfterTokens = 3,
            FailureStatus = NpuBridge.Backends.GenerationStatus.ContentFiltered,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        if (stream)
        {
            var text = await (await host.Client.PostAsJsonAsync(Path, new
            {
                model = "fake",
                stream = true,
                tools = Weather,
                stream_options = new { include_usage = true },
                messages = new[] { new { role = "user", content = "weather in paris?" } },
            })).Content.ReadAsStringAsync();

            Assert.DoesNotContain("\"tool_calls\"", text, StringComparison.Ordinal);
            Assert.Equal("content_filter", Sse.Chunks(text).Last(c => c.GetProperty("choices").GetArrayLength() > 0)
                .GetProperty("choices")[0].GetProperty("finish_reason").GetString());

            var usage = Sse.Chunks(text).Single(c => c.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object);
            Assert.Equal(0, usage.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        }
        else
        {
            var body = await PostAsync(host, Body(stream: false, tools: Weather));
            var choice = body.GetProperty("choices")[0];

            Assert.Equal("content_filter", choice.GetProperty("finish_reason").GetString());
            Assert.False(choice.GetProperty("message").TryGetProperty("tool_calls", out _));
            Assert.Equal(0, body.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        }

        host.AssertNoLeak();
    }

    /// <summary>
    /// <c>tool_choice: "none"</c> injects nothing, so a request carrying tools keys identically to the
    /// same conversation without them — which is what "genuinely inert" has to mean once the block
    /// lives in the system text.
    /// </summary>
    [Fact]
    public async Task Tool_choice_none_keys_the_same_as_a_request_with_no_tools()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await PostAsync(host, Body(stream: false, tools: Weather, toolChoice: "none"));
        await PostAsync(host, Body(stream: false));

        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(fake.Calls[0].SystemPrompt, fake.Calls[1].SystemPrompt);
        Assert.Equal(fake.Calls[0].Prompt, fake.Calls[1].Prompt);
    }

    [Fact]
    public async Task A_tool_call_leaks_no_context_on_either_shape()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => FakeBackend.Tokenize(FencedCall) });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await PostAsync(host, Body(stream: false, tools: Weather));
        await (await host.Client.PostAsJsonAsync(Path, Body(stream: true, tools: Weather))).Content.ReadAsStringAsync();

        await TestWait.UntilAsync(() => host.Requests.Completed == 2);
        host.AssertNoLeak();
    }
}
