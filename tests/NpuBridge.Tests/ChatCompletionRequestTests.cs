using System.Text.Json;
using NpuBridge.Api;

namespace NpuBridge.Tests;

public class ChatCompletionRequestTests
{
    [Fact]
    public void Minimal_request_with_string_content_round_trips()
    {
        var json = """{"model":"fake","messages":[{"role":"user","content":"hi there"}]}""";

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;

        Assert.Equal("fake", request.Model);
        var message = Assert.Single(request.Messages!);
        Assert.Equal("user", message.Role);
        Assert.NotNull(message.Content);
        Assert.False(message.Content!.IsParts);
        Assert.Equal("hi there", message.Content.Text);
        Assert.Null(message.Content.Parts);

        var result = ChatCompletionRequestValidator.Validate(request);
        Assert.True(result.IsValid);
        Assert.Empty(result.IgnoredParameters);
    }

    [Fact]
    public void Array_of_parts_content_deserializes_in_order()
    {
        var json = """
            {"model":"fake","messages":[{"role":"user","content":[
                {"type":"text","text":"first"},
                {"type":"text","text":"second"}
            ]}]}
            """;

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;

        var message = Assert.Single(request.Messages!);
        Assert.True(message.Content!.IsParts);
        Assert.Equal(["first", "second"], message.Content.Parts!.Select(p => p.Text));
        Assert.All(message.Content.Parts!, p => Assert.Equal("text", p.Type));

        var result = ChatCompletionRequestValidator.Validate(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Content_may_be_null_on_an_assistant_message()
    {
        var json = """{"model":"fake","messages":[{"role":"assistant","content":null}]}""";

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;
        var message = Assert.Single(request.Messages!);
        Assert.Null(message.Content);

        var result = ChatCompletionRequestValidator.Validate(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Empty_string_content_on_a_user_message_is_valid()
    {
        var json = """{"model":"fake","messages":[{"role":"user","content":""}]}""";

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;
        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Tool_message_carries_name_and_tool_call_id()
    {
        var json = """
            {"model":"fake","messages":[
                {"role":"user","content":"what's the weather"},
                {"role":"tool","tool_call_id":"call_01","name":"get_weather","content":"{\"temp\":21}"}
            ]}
            """;

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;
        var toolMessage = request.Messages![1];

        Assert.Equal("tool", toolMessage.Role);
        Assert.Equal("call_01", toolMessage.ToolCallId);
        Assert.Equal("get_weather", toolMessage.Name);
        Assert.Equal("{\"temp\":21}", toolMessage.Content!.Text);
    }

    [Fact]
    public void Image_content_part_fails_validation_with_messages_param()
    {
        var json = """{"model":"fake","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"http://x"}}]}]}""";

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;
        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("messages", result.Failure!.Param);
        Assert.Contains("image_url", result.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// System.Text.Json puts a JSON null into the list despite the non-nullable element annotation, so
    /// the validator must check rather than trust the type. Before the check this threw, and because
    /// validation runs before the endpoint's try block, the request became a 500.
    /// </summary>
    [Theory]
    [InlineData("""{"model":"fake","messages":[null]}""")]
    [InlineData("""{"model":"fake","messages":[{"role":"user","content":"hi"},null]}""")]
    [InlineData("""{"model":"fake","messages":[null,{"role":"user","content":"hi"}]}""")]
    public void Null_message_element_fails_validation_instead_of_throwing(string json)
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;

        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("messages", result.Failure!.Param);
        Assert.Null(result.Failure.Code);
        Assert.Contains("null", result.Failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"model":"fake","messages":[{"role":"user","content":[{"type":"text"}]}]}""")]
    [InlineData("""{"model":"fake","messages":[{"role":"user","content":[{"type":"text","text":null}]}]}""")]
    [InlineData("""{"model":"fake","messages":[{"role":"user","content":[{"type":"text","text":"ok"},{"type":"text"}]}]}""")]
    public void Text_content_part_without_text_fails_validation(string json)
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;

        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("messages", result.Failure!.Param);
    }

    [Fact]
    public void An_empty_text_content_part_stays_valid()
    {
        var json = """{"model":"fake","messages":[{"role":"user","content":[{"type":"text","text":""}]}]}""";

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;

        Assert.True(ChatCompletionRequestValidator.Validate(request).IsValid);
    }

    /// <summary>Ruled deliberate: a message with no content at all renders as empty text, not an error.</summary>
    [Theory]
    [InlineData("""{"model":"fake","messages":[{"role":"user"}]}""")]
    [InlineData("""{"model":"fake","messages":[{"role":"user","content":null}]}""")]
    public void A_message_without_content_stays_valid(string json)
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;

        Assert.True(ChatCompletionRequestValidator.Validate(request).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("banana")]
    public void Unknown_or_missing_role_fails_validation(string? role)
    {
        var message = new ChatMessage(role, ChatMessageContent.FromText("hi"), null, null);
        var request = new ChatCompletionRequest("fake", [message], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("messages", result.Failure!.Param);
    }

    [Fact]
    public void Missing_messages_fails_validation()
    {
        var request = new ChatCompletionRequest("fake", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("messages", result.Failure!.Param);
    }

    [Fact]
    public void Empty_messages_array_fails_validation()
    {
        var request = new ChatCompletionRequest("fake", [], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("messages", result.Failure!.Param);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void N_greater_than_one_fails_validation(int? n, bool expectedValid)
    {
        var message = new ChatMessage("user", ChatMessageContent.FromText("hi"), null, null);
        var request = new ChatCompletionRequest("fake", [message], null, n, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
        {
            Assert.Equal("n", result.Failure!.Param);
        }
    }

    /// <summary>
    /// Chunk 4 implements streaming, so <c>stream</c> is valid whatever it says, and it is not an
    /// ignored parameter either: it changes the response shape rather than being accepted and dropped.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void Stream_is_valid_and_is_not_an_ignored_parameter(bool? stream)
    {
        var message = new ChatMessage("user", ChatMessageContent.FromText("hi"), null, null);
        var request = new ChatCompletionRequest("fake", [message], stream, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.DoesNotContain("stream", result.IgnoredParameters, StringComparer.Ordinal);
    }

    [Fact]
    public void Stream_options_deserializes_and_is_not_an_ignored_parameter()
    {
        var json = """{"model":"fake","messages":[{"role":"user","content":"hi"}],"stream":true,"stream_options":{"include_usage":true}}""";

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;

        Assert.True(request.Stream);
        Assert.True(request.StreamOptions!.IncludeUsage);
        Assert.DoesNotContain("stream_options", ChatCompletionRequestValidator.Validate(request).IgnoredParameters, StringComparer.Ordinal);
    }

    [Fact]
    public void Every_ignored_parameter_deserializes_and_is_reported()
    {
        var json = """
            {
                "model": "fake",
                "messages": [{"role":"user","content":"hi"}],
                "temperature": 0.5,
                "top_p": 0.9,
                "top_k": 40,
                "max_tokens": 100,
                "max_completion_tokens": 100,
                "stop": ["\n", "END"],
                "tools": [{"type":"function","function":{"name":"f"}}],
                "tool_choice": "auto",
                "logprobs": true,
                "response_format": {"type":"json_object"},
                "seed": 42,
                "presence_penalty": 0.1,
                "frequency_penalty": 0.1,
                "user": "u-123"
            }
            """;

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;
        var result = ChatCompletionRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(
            new[]
            {
                "temperature", "top_p", "top_k",
                "tools", "tool_choice", "logprobs", "response_format", "seed",
                "presence_penalty", "frequency_penalty", "user",
            },
            result.IgnoredParameters);

        // The body above still carries all three, and they are deliberately absent from the list: the
        // client-side cut implements them (D53), so a request that sets one is not warned about it.
        Assert.DoesNotContain("max_tokens", result.IgnoredParameters, StringComparer.Ordinal);
        Assert.DoesNotContain("max_completion_tokens", result.IgnoredParameters, StringComparer.Ordinal);
        Assert.DoesNotContain("stop", result.IgnoredParameters, StringComparer.Ordinal);
    }

    [Fact]
    public void Stop_as_bare_string_and_as_array_normalize_to_same_list()
    {
        var stringForm = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """{"model":"fake","messages":[{"role":"user","content":"hi"}],"stop":"END"}""", JsonDefaults.Options)!;
        var arrayForm = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """{"model":"fake","messages":[{"role":"user","content":"hi"}],"stop":["END"]}""", JsonDefaults.Options)!;

        Assert.Equal(["END"], stringForm.Stop);
        Assert.Equal(["END"], arrayForm.Stop);
        Assert.Equal(stringForm.Stop, arrayForm.Stop);
    }

    [Fact]
    public void Stop_array_with_multiple_entries_normalizes_to_a_list()
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """{"model":"fake","messages":[{"role":"user","content":"hi"}],"stop":["a","b"]}""", JsonDefaults.Options)!;

        Assert.Equal(["a", "b"], request.Stop);
    }

    [Fact]
    public void Response_serializes_snake_case_omits_nulls_and_has_chatcmpl_id()
    {
        var response = new ChatCompletionResponse(
            ChatCompletionId.NewId(),
            1234567890,
            "fake",
            [new ChatCompletionChoice(0, new ChatCompletionResponseMessage("assistant", "hello"), "stop")],
            new CompletionUsage(3, 2, 5));

        var json = JsonSerializer.Serialize(response, JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.StartsWith("chatcmpl-", root.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal(1234567890, root.GetProperty("created").GetInt64());
        Assert.Equal("fake", root.GetProperty("model").GetString());

        var choice = root.GetProperty("choices")[0];
        Assert.Equal(0, choice.GetProperty("index").GetInt32());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("hello", choice.GetProperty("message").GetProperty("content").GetString());

        var usage = root.GetProperty("usage");
        Assert.Equal(3, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(2, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("total_tokens").GetInt32());

        // The schema requires content (nullable) on the response message, so a null-content assistant
        // message writes "content": null rather than omitting the key (D77).
        var nullContentJson = JsonSerializer.Serialize(
            new ChatCompletionResponseMessage("assistant", null), JsonDefaults.Options);
        using var nullContentDoc = JsonDocument.Parse(nullContentJson);
        Assert.Equal(JsonValueKind.Null, nullContentDoc.RootElement.GetProperty("content").ValueKind);
    }

    [Fact]
    public void Ids_are_unique_and_shaped_like_chatcmpl_plus_26_char_ulid()
    {
        var first = ChatCompletionId.NewId();
        var second = ChatCompletionId.NewId();

        Assert.NotEqual(first, second);
        Assert.StartsWith("chatcmpl-", first, StringComparison.Ordinal);
        Assert.Equal("chatcmpl-".Length + 26, first.Length);
    }

    [Fact]
    public void Assistant_message_carries_its_tool_calls()
    {
        var json = """
            {"model":"fake","messages":[{"role":"assistant","content":null,"tool_calls":[
              {"id":"call_01","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Paris\"}"}}
            ]}]}
            """;

        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!;

        var message = Assert.Single(request.Messages!);
        Assert.Null(message.Content);
        var call = Assert.Single(message.ToolCalls!);
        Assert.Equal("call_01", call.Id);
        Assert.Equal("function", call.Type);
        Assert.Equal("get_weather", call.Function!.Name);
        Assert.Equal("{\"city\":\"Paris\"}", call.Function.Arguments);
        Assert.True(ChatCompletionRequestValidator.Validate(request).IsValid);
    }
}
