using System.Text.Json;
using NpuBridge.Api;
using NpuBridge.Prompting;

namespace NpuBridge.Tests;

/// <summary>
/// How an assistant turn that called a tool is rendered back into the transcript (chunk 7, PLAN §2.6
/// item 5). Before this the turn rendered as its content alone, which for a tool call is empty — so
/// the model was shown a blank assistant turn where its own call had been, followed by a tool result
/// for a call it could not see it had made.
///
/// These tests are as much about the conversation cache as about the prompt. The rendered body is
/// what <see cref="PromptTemplate.TurnText"/> returns and therefore what <c>ConversationKey</c>
/// hashes (D71), so the rendering has to be byte-deterministic and has to match what this bridge's
/// own replies serialise to: a client that sends our <c>tool_calls</c> array straight back must key
/// to the same conversation, or every tool-using exchange misses the cache on its second turn.
/// </summary>
public class ToolCallRenderingTests
{
    private static ChatMessage User(string text) => new("user", ChatMessageContent.FromText(text), null, null);

    private static ChatMessage AssistantCalling(string? content, params (string Name, string? Arguments)[] calls) =>
        new("assistant", content is null ? null : ChatMessageContent.FromText(content), null, null,
            calls.Select(c => new ChatToolCall($"call_{c.Name}", "function", new ChatFunctionCall(c.Name, c.Arguments))).ToArray());

    private static ChatMessage ToolResult(string text, string name, string id) =>
        new("tool", ChatMessageContent.FromText(text), name, id);

    [Fact]
    public void An_assistant_turn_that_called_a_tool_renders_the_call_in_the_models_own_envelope()
    {
        var message = AssistantCalling(null, ("get_weather", """{"location":"Paris"}"""));

        var body = PromptTemplate.TurnText(message);

        Assert.Equal("""{"tool_calls":[{"name":"get_weather","arguments":{"location":"Paris"}}]}""", body);
    }

    /// <summary>
    /// The envelope is the one the injected instruction asks for, so the model sees the protocol it
    /// was taught. Pinned exactly, key by key: the parser on the other side reads this shape, and a
    /// rename here would break the round trip silently.
    /// </summary>
    [Fact]
    public void The_rendered_envelope_is_the_shape_the_parser_reads()
    {
        var body = PromptTemplate.TurnText(AssistantCalling(null, ("f", """{"a":1}""")));

        using var document = JsonDocument.Parse(body);
        var call = Assert.Single(document.RootElement.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("f", call.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, call.GetProperty("arguments").ValueKind);
        Assert.Equal(1, call.GetProperty("arguments").GetProperty("a").GetInt32());
    }

    /// <summary>
    /// The call id is deliberately absent. The model never produced one — the bridge assigns it — and
    /// a per-request ulid in the hashed body would mean no tool-using conversation could ever hit the
    /// cache. The id the model does need, to match a result to its call, arrives on the tool result's
    /// own marker.
    /// </summary>
    [Fact]
    public void The_call_id_is_not_rendered_because_it_would_poison_the_cache_key()
    {
        var body = PromptTemplate.TurnText(AssistantCalling(null, ("get_weather", "{}")));

        Assert.DoesNotContain("call_", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_and_calls_together_render_the_content_first()
    {
        var message = AssistantCalling("Let me look that up.", ("get_weather", """{"location":"Paris"}"""));

        var body = PromptTemplate.TurnText(message);

        Assert.Equal(
            "Let me look that up.\n" + """{"tool_calls":[{"name":"get_weather","arguments":{"location":"Paris"}}]}""",
            body);
    }

    [Fact]
    public void Several_calls_in_one_turn_keep_the_order_the_client_sent()
    {
        var body = PromptTemplate.TurnText(AssistantCalling(null, ("first", "{}"), ("second", "{}")));

        Assert.Equal("""{"tool_calls":[{"name":"first","arguments":{}},{"name":"second","arguments":{}}]}""", body);
    }

    /// <summary>Absent, empty and whitespace arguments all become <c>{}</c>: the envelope always carries the key.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_arguments_render_as_an_empty_object(string? arguments)
    {
        var body = PromptTemplate.TurnText(AssistantCalling(null, ("f", arguments)));

        Assert.Equal("""{"tool_calls":[{"name":"f","arguments":{}}]}""", body);
    }

    /// <summary>
    /// Arguments arrive as JSON text and are re-emitted, which normalises the client's whitespace out
    /// of the hashed body. Two clients that send the same call formatted differently must key the
    /// same, or the second one misses a context the model is holding.
    /// </summary>
    [Fact]
    public void Whitespace_in_the_clients_arguments_does_not_change_the_rendering()
    {
        var compact = PromptTemplate.TurnText(AssistantCalling(null, ("f", """{"a":1,"b":[2,3]}""")));
        var spaced = PromptTemplate.TurnText(AssistantCalling(null, ("f", "{ \"a\" : 1 ,\n  \"b\" : [ 2 , 3 ] }")));

        Assert.Equal(compact, spaced);
    }

    [Fact]
    public void Nested_objects_and_arrays_in_arguments_survive_intact()
    {
        var arguments = """{"filter":{"tags":["a","b"],"depth":2},"dry_run":false}""";

        var body = PromptTemplate.TurnText(AssistantCalling(null, ("search", arguments)));

        using var document = JsonDocument.Parse(body);
        var rendered = document.RootElement.GetProperty("tool_calls")[0].GetProperty("arguments");
        Assert.Equal(2, rendered.GetProperty("filter").GetProperty("depth").GetInt32());
        Assert.Equal(["a", "b"], rendered.GetProperty("filter").GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.False(rendered.GetProperty("dry_run").GetBoolean());
    }

    /// <summary>
    /// A brace or a quote inside an argument string is data, not structure. Rendering through the JSON
    /// writer rather than by concatenation is what keeps that true, and the body stays parseable.
    /// </summary>
    [Fact]
    public void Braces_and_quotes_inside_an_argument_value_stay_data()
    {
        var arguments = """{"code":"if (x) { return \"}\"; }"}""";

        var body = PromptTemplate.TurnText(AssistantCalling(null, ("run", arguments)));

        using var document = JsonDocument.Parse(body);
        Assert.Equal("if (x) { return \"}\"; }",
            document.RootElement.GetProperty("tool_calls")[0].GetProperty("arguments").GetProperty("code").GetString());
    }

    /// <summary>
    /// Arguments that are not valid JSON are shown back as the string they are. The model wrote
    /// something the bridge could not read; dropping the turn's only content would teach it nothing,
    /// and the rendering has to stay parseable either way.
    /// </summary>
    [Fact]
    public void Unparseable_arguments_are_rendered_as_a_string_rather_than_dropped()
    {
        var body = PromptTemplate.TurnText(AssistantCalling(null, ("f", "location=Paris")));

        using var document = JsonDocument.Parse(body);
        var rendered = document.RootElement.GetProperty("tool_calls")[0].GetProperty("arguments");
        Assert.Equal(JsonValueKind.String, rendered.ValueKind);
        Assert.Equal("location=Paris", rendered.GetString());
    }

    [Fact]
    public void An_assistant_turn_without_calls_is_unchanged()
    {
        var plain = new ChatMessage("assistant", ChatMessageContent.FromText("just prose"), null, null);

        Assert.Equal("just prose", PromptTemplate.TurnText(plain));
    }

    /// <summary>An empty array is not a tool call, and must not add an envelope to an ordinary turn.</summary>
    [Fact]
    public void An_empty_tool_calls_array_renders_as_the_content_alone()
    {
        var message = new ChatMessage("assistant", ChatMessageContent.FromText("prose"), null, null, []);

        Assert.Equal("prose", PromptTemplate.TurnText(message));
    }

    /// <summary>
    /// The whole exchange as the model sees it on the next turn: its own call, then the result marked
    /// with the name and id that tie back to it. This is the shape PLAN §2.6 item 5 specifies, and the
    /// reason for it — a model shown a result for a call it cannot see rarely continues sensibly.
    /// </summary>
    [Fact]
    public void A_call_and_its_result_render_as_one_legible_exchange()
    {
        ChatMessage[] messages =
        [
            User("weather in paris?"),
            AssistantCalling(null, ("get_weather", """{"location":"Paris"}""")),
            ToolResult("18C, clear", "get_weather", "call_abc"),
            User("and tomorrow?"),
        ];

        var prompt = PromptTemplate.Render(messages, nativeSystemPromptSupported: true).Prompt;

        Assert.Contains("[Assistant]\n" + """{"tool_calls":[{"name":"get_weather","arguments":{"location":"Paris"}}]}""",
            prompt, StringComparison.Ordinal);
        Assert.Contains("[Tool result: get_weather (call_abc)]\n18C, clear", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property this rendering exists to hold: the same call, rendered twice, is the same bytes.
    /// <see cref="PromptTemplate.TurnText"/> feeds the conversation key, so anything non-deterministic
    /// here — dictionary order, indentation, a generated id — turns every tool-using conversation's
    /// second turn into a cache miss on a context the model is still holding.
    /// </summary>
    [Fact]
    public void The_rendering_is_byte_deterministic()
    {
        var first = PromptTemplate.TurnText(AssistantCalling("thinking", ("a", """{"x":[1,{"y":"z"}]}"""), ("b", "{}")));
        var second = PromptTemplate.TurnText(AssistantCalling("thinking", ("a", """{"x":[1,{"y":"z"}]}"""), ("b", "{}")));

        Assert.Equal(first, second);
    }
}
