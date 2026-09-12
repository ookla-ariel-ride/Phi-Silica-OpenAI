using System.Text.Json;
using NpuBridge.Tools;

namespace NpuBridge.Tests;

/// <summary>
/// The parser's regression guard (issue #3). Its input is written by a ~3.3B model that was asked for
/// one JSON object and answers with something adjacent to one, so every shape here is hard-coded from
/// what such a model actually emits rather than generated from the happy path: the fence with a
/// language tag, the sentence in front of the object, the missing wrapper, the trailing comma, the
/// Python-flavoured quotes, the argument value that contains a brace.
///
/// Two properties are asserted throughout. Anything unreadable is <c>null</c>, which means the reply
/// goes to the client as content and nothing is lost; and <see cref="ParsedToolCall.Arguments"/> is
/// always compact JSON object text, because the endpoint puts it straight into a string field that
/// the client parses without looking.
/// </summary>
public class ToolCallParserTests
{
    private const string OneCall = """{"tool_calls":[{"name":"search","arguments":{"q":"rain"}}]}""";

    private static ParsedToolCall SingleCall(string text)
    {
        var calls = ToolCallParser.Parse(text);
        Assert.NotNull(calls);
        return Assert.Single(calls);
    }

    /// <summary>Reads one argument back out, so a test can assert on the value rather than its spelling.</summary>
    private static string ArgumentValue(ParsedToolCall call, string name)
    {
        using var document = JsonDocument.Parse(call.Arguments);
        return document.RootElement.GetProperty(name).GetString()!;
    }

    [Fact]
    public void A_clean_fenced_block_is_the_shape_the_instruction_asks_for()
    {
        var call = SingleCall("""
            ```json
            {"tool_calls":[{"name":"get_weather","arguments":{"location":"Paris","unit":"c"}}]}
            ```
            """);

        Assert.Equal("get_weather", call.Name);
        Assert.Equal("""{"location":"Paris","unit":"c"}""", call.Arguments);
    }

    /// <summary>
    /// The fence's info string is the model's guess at a language name, not a promise about the
    /// content, so it is ignored in every spelling — including the bare fence and a tag that names no
    /// language at all.
    /// </summary>
    [Theory]
    [InlineData("```json")]
    [InlineData("```JSON")]
    [InlineData("```Json")]
    [InlineData("```")]
    [InlineData("```tool_code")]
    public void The_fence_tag_is_ignored_whatever_the_model_called_it(string opening)
    {
        var call = SingleCall($"Sure, let me look that up.\n{opening}\n{OneCall}\n```\nI will report back.");

        Assert.Equal("search", call.Name);
        Assert.Equal("""{"q":"rain"}""", call.Arguments);
    }

    /// <summary>The first fence holds something else; the block that parses is the one that counts.</summary>
    [Fact]
    public void A_fence_that_is_not_json_does_not_stop_the_one_that_is()
    {
        var call = SingleCall($"Here is the shape:\n```text\nname, then arguments\n```\nand the call:\n```json\n{OneCall}\n```");

        Assert.Equal("search", call.Name);
    }

    /// <summary>A reply cut off by the token budget loses its closing fence but not its object.</summary>
    [Fact]
    public void An_unclosed_fence_still_yields_its_block()
    {
        Assert.Equal("search", SingleCall($"```json\n{OneCall}").Name);
    }

    [Fact]
    public void A_bare_object_with_tool_calls_is_found_between_prose_on_both_sides()
    {
        var call = SingleCall($"Let me check that for you. {OneCall} Standing by for the result.");

        Assert.Equal("search", call.Name);
        Assert.Equal("""{"q":"rain"}""", call.Arguments);
    }

    [Fact]
    public void A_single_unwrapped_call_needs_no_tool_calls_wrapper()
    {
        var call = SingleCall("""I'll use: {"name":"get_time","arguments":{"tz":"UTC"}}""");

        Assert.Equal("get_time", call.Name);
        Assert.Equal("""{"tz":"UTC"}""", call.Arguments);
    }

    [Fact]
    public void A_bare_array_of_calls_is_read_without_its_wrapper()
    {
        var calls = ToolCallParser.Parse("""[{"name":"a","arguments":{"x":1}},{"name":"b","arguments":{}}]""");

        Assert.NotNull(calls);
        Assert.Equal(new[] { "a", "b" }, calls.Select(c => c.Name).ToArray());
        Assert.Equal("""{"x":1}""", calls[0].Arguments);
        Assert.Equal("{}", calls[1].Arguments);
    }

    /// <summary>
    /// Two calls, and the trap that fixes the strategy order: the wrapper object contains
    /// <c>"name"</c> and <c>"arguments"</c> itself, so a parser that ran the single-call strategy
    /// first would match the same text one level deeper and return only the first call.
    /// </summary>
    [Fact]
    public void Two_calls_in_one_reply_both_survive_the_wrapper()
    {
        var calls = ToolCallParser.Parse("""
            {"tool_calls":[{"name":"read","arguments":{"path":"a.txt"}},{"name":"write","arguments":{"path":"b.txt"}}]}
            """);

        Assert.NotNull(calls);
        Assert.Equal(2, calls.Count);
        Assert.Equal("read", calls[0].Name);
        Assert.Equal("""{"path":"a.txt"}""", calls[0].Arguments);
        Assert.Equal("write", calls[1].Name);
        Assert.Equal("""{"path":"b.txt"}""", calls[1].Arguments);
    }

    /// <summary>OpenAI's own wire format types arguments as a string; a model imitating it does too.</summary>
    [Fact]
    public void Arguments_given_as_a_json_encoded_string_are_unwrapped()
    {
        var call = SingleCall("""{"tool_calls":[{"name":"search","arguments":"{\"q\":\"rain\",\"n\":3}"}]}""");

        Assert.Equal("""{"q":"rain","n":3}""", call.Arguments);
    }

    /// <summary>A call with nothing to pass still has to carry an object, since the client parses it unconditionally.</summary>
    [Fact]
    public void A_call_with_no_arguments_carries_an_empty_object()
    {
        Assert.Equal("{}", SingleCall("""{"tool_calls":[{"name":"list_files"}]}""").Arguments);
    }

    /// <summary>
    /// Arguments that are not an object at all become <c>{}</c> rather than being passed through: the
    /// field is typed as an object everywhere it is going, and a client doing
    /// <c>JSON.parse(arguments).path</c> should get undefined rather than a throw.
    /// </summary>
    [Theory]
    [InlineData("""{"name":"a","arguments":null}""")]
    [InlineData("""{"name":"a","arguments":""}""")]
    [InlineData("""{"name":"a","arguments":"not json at all"}""")]
    [InlineData("""{"name":"a","arguments":[1,2]}""")]
    [InlineData("""{"name":"a","arguments":7}""")]
    [InlineData("""{"name":"a","arguments":"[1,2]"}""")]
    public void Arguments_that_are_not_an_object_become_an_empty_object(string reply)
    {
        Assert.Equal("{}", SingleCall(reply).Arguments);
    }

    /// <summary>
    /// The brace scanner has to know it is inside a string. Each of these ends the candidate at the
    /// wrong character if it does not: a brace in a value, a quote escaped with a backslash, a value
    /// ending in an escaped backslash, and a value that is itself a fragment of JSON.
    /// </summary>
    [Theory]
    [InlineData("""{"name":"echo","arguments":{"text":"a } b { c"}}""", "a } b { c")]
    [InlineData("""{"name":"echo","arguments":{"text":"he said \"}\" loudly"}}""", "he said \"}\" loudly")]
    [InlineData("""{"name":"echo","arguments":{"text":"C:\\"}}""", @"C:\")]
    [InlineData("""{"name":"echo","arguments":{"text":"{\"nested\":\"}\"}"}}""", """{"nested":"}"}""")]
    [InlineData("""{"name":"echo","arguments":{"text":"[{ unbalanced"}}""", "[{ unbalanced")]
    public void Structure_inside_a_string_value_is_text_not_structure(string reply, string expected)
    {
        var call = SingleCall(reply);

        Assert.Equal("echo", call.Name);
        Assert.Equal(expected, ArgumentValue(call, "text"));
    }

    /// <summary>The most common single defect in a small model's JSON, at three depths at once.</summary>
    [Fact]
    public void Trailing_commas_are_tolerated_and_never_reappear_in_the_output()
    {
        var call = SingleCall("""{"tool_calls":[{"name":"a","arguments":{"x":1,},},]}""");

        Assert.Equal("a", call.Name);
        Assert.Equal("""{"x":1}""", call.Arguments);
    }

    /// <summary>Whatever the model wrote around its values, the arguments string is compact.</summary>
    [Fact]
    public void Pretty_printed_json_is_re_serialised_compact()
    {
        var call = SingleCall("""
            {
              "tool_calls": [
                {
                  "name": "write",
                  "arguments": {
                    "path": "a.txt",
                    "lines": [ 1, 2 ]
                  }
                }
              ]
            }
            """);

        Assert.Equal("""{"path":"a.txt","lines":[1,2]}""", call.Arguments);
    }

    [Fact]
    public void Nested_objects_and_arrays_in_arguments_are_preserved_exactly()
    {
        const string Arguments = """{"path":"a.txt","meta":{"tags":["x","y"],"depth":{"list":[1,2,{"k":true,"v":null}]}}}""";
        var call = SingleCall("""{"tool_calls":[{"name":"write","arguments":""" + Arguments + "}]}");

        Assert.Equal(Arguments, call.Arguments);
    }

    /// <summary>
    /// Unicode survives the re-serialisation with its meaning intact. What the client is handed is a
    /// JSON string, so the assertion that matters is the decoded value, not the bytes.
    ///
    /// The bytes are worth pinning anyway, because they are not what you would guess: the relaxed
    /// encoder writes characters of the Basic Multilingual Plane through literally — <c>é</c> and
    /// <c>東京</c> below — but escapes a supplementary-plane character as its surrogate pair, so the
    /// rocket comes back as a <c>🚀</c> escape. That is valid JSON that decodes to the same
    /// rocket, which is why it is accepted rather than fought, and it is pinned here so that a later
    /// change of encoder is a visible decision instead of a surprise.
    /// </summary>
    [Fact]
    public void A_unicode_argument_value_survives_the_round_trip()
    {
        var call = SingleCall("""{"name":"say","arguments":{"text":"héllo 🚀 東京"}}""");

        Assert.Equal("héllo 🚀 東京", ArgumentValue(call, "text"));
        Assert.Equal("{\"text\":\"héllo \\uD83D\\uDE80 東京\"}", call.Arguments);
    }

    /// <summary>The parser is handed no catalog, so a tool nobody offered reaches the client as written.</summary>
    [Fact]
    public void An_unknown_tool_name_is_surfaced_not_filtered()
    {
        Assert.Equal("definitely_not_offered", SingleCall("""{"tool_calls":[{"name":"definitely_not_offered","arguments":{}}]}""").Name);
    }

    /// <summary>
    /// A model that has seen OpenAI tool calls in training — or is being fed our own reply back as
    /// history — writes the call nested under <c>function</c>, with the arguments as a string.
    /// </summary>
    [Fact]
    public void The_openai_function_wrapper_shape_is_accepted()
    {
        var call = SingleCall("""
            {"tool_calls":[{"id":"call_01","type":"function","function":{"name":"get_weather","arguments":"{\"location\":\"Oslo\"}"}}]}
            """);

        Assert.Equal("get_weather", call.Name);
        Assert.Equal("""{"location":"Oslo"}""", call.Arguments);
    }

    /// <summary><c>parameters</c> is the word the injected JSON Schema uses, so the model reads it back.</summary>
    [Fact]
    public void Parameters_is_accepted_as_a_synonym_for_arguments()
    {
        Assert.Equal("""{"x":1}""", SingleCall("""{"tool_calls":[{"name":"a","parameters":{"x":1}}]}""").Arguments);
    }

    [Fact]
    public void Property_names_are_matched_whatever_the_model_capitalised()
    {
        var call = SingleCall("""{"Tool_Calls":[{"Name":"a","Arguments":{"x":1}}]}""");

        Assert.Equal("a", call.Name);
        Assert.Equal("""{"x":1}""", call.Arguments);
    }

    [Theory]
    [InlineData("The weather in Paris is mild this time of year.")]
    [InlineData("I don't need a tool for that.")]
    [InlineData("Use the { key to open the menu, then } to close it.")]
    [InlineData("Here is some code: function f() { return 1; }")]
    public void Ordinary_prose_is_content(string reply)
    {
        Assert.Null(ToolCallParser.Parse(reply));
    }

    /// <summary>Talking about the protocol is not using it; there is no JSON here to find.</summary>
    [Theory]
    [InlineData("I could answer with a tool_calls object, but I already know the answer.")]
    [InlineData("The tool_calls field is an array whose entries have a name and arguments.")]
    public void Prose_that_mentions_the_protocol_is_still_content(string reply)
    {
        Assert.Null(ToolCallParser.Parse(reply));
    }

    /// <summary>
    /// The gate on the unwrapped single call: <c>"name"</c> alone is a word that appears in ordinary
    /// data, so the strategy requires <c>arguments</c> (or <c>parameters</c>) alongside it and a
    /// sentence about a person named Ada does not become a call to a tool named Ada.
    /// </summary>
    [Theory]
    [InlineData("""My colleague is {"name": "Ada"} in the staff records.""")]
    [InlineData("""The record looks like {"name": "Ada", "role": "engineer"}.""")]
    public void A_bare_name_object_in_prose_is_not_a_call(string reply)
    {
        Assert.Null(ToolCallParser.Parse(reply));
    }

    /// <summary>A reply the token budget cut in half is content, not an exception.</summary>
    [Theory]
    [InlineData("""{"tool_calls":[{"name":"a","arguments":{"x":1}""")]
    [InlineData("""{"tool_calls":[{"name":"a",""")]
    [InlineData("```json\n{\"tool_calls\":[{\"name\":")]
    [InlineData("""[{"name":"a","arguments":{}""")]
    [InlineData("""{"name":"a","arguments":{"text":"unterminated}""")]
    public void Truncated_json_is_content(string reply)
    {
        Assert.Null(ToolCallParser.Parse(reply));
    }

    /// <summary>An empty array is a model that decided against calling anything; that is content.</summary>
    [Theory]
    [InlineData("""{"tool_calls":[]}""")]
    [InlineData("```json\n{\"tool_calls\": []}\n```")]
    [InlineData("[]")]
    public void An_empty_tool_calls_array_is_content(string reply)
    {
        Assert.Null(ToolCallParser.Parse(reply));
    }

    /// <summary>A nameless entry cannot be dispatched, so it is dropped; a named sibling is not.</summary>
    [Fact]
    public void A_call_with_no_usable_name_is_dropped_and_its_siblings_are_not()
    {
        var calls = ToolCallParser.Parse("""
            {"tool_calls":[{"name":"","arguments":{}},{"arguments":{"x":1}},{"name":"   ","arguments":{}},{"name":"b","arguments":{"y":2}}]}
            """);

        Assert.NotNull(calls);
        var call = Assert.Single(calls);
        Assert.Equal("b", call.Name);
        Assert.Equal("""{"y":2}""", call.Arguments);
    }

    /// <summary>Every entry dropped leaves nothing to call, which is the same as never having found one.</summary>
    [Theory]
    [InlineData("""{"tool_calls":[{"name":"","arguments":{"x":1}}]}""")]
    [InlineData("""{"tool_calls":[{"arguments":{"x":1}}]}""")]
    [InlineData("""{"name":null,"arguments":{"x":1}}""")]
    [InlineData("""{"name":42,"arguments":{"x":1}}""")]
    public void A_reply_whose_every_call_is_dropped_is_content(string reply)
    {
        Assert.Null(ToolCallParser.Parse(reply));
    }

    /// <summary>Python-flavoured quoting, which only the last-resort pass accepts.</summary>
    [Theory]
    [InlineData("""{'tool_calls': [{'name': 'get_weather', 'arguments': {'location': 'Paris'}}]}""")]
    [InlineData("""{'name': 'get_weather', 'arguments': {'location': 'Paris'}}""")]
    [InlineData("Here you go:\n```json\n{'tool_calls': [{'name': 'get_weather', 'arguments': {'location': 'Paris'}}]}\n```")]
    public void Single_quoted_json_is_read_by_the_last_resort_pass(string reply)
    {
        var call = SingleCall(reply);

        Assert.Equal("get_weather", call.Name);
        Assert.Equal("""{"location":"Paris"}""", call.Arguments);
    }

    /// <summary>
    /// The relaxed pass runs only after everything else has failed, so an apostrophe in the prose
    /// around a well-formed call cannot rewrite the strings inside it. Without that ordering the
    /// apostrophe in "I'll" opens a string that runs to the one in "it's" and swallows the object.
    /// </summary>
    [Fact]
    public void An_apostrophe_in_the_prose_does_not_disturb_a_well_formed_call()
    {
        var call = SingleCall("""I'll check that, it's quick: {"name":"get_time","arguments":{"tz":"UTC"}} — one moment.""");

        Assert.Equal("get_time", call.Name);
        Assert.Equal("""{"tz":"UTC"}""", call.Arguments);
    }

    /// <summary>A double-quoted value keeps its apostrophes even when the relaxed pass has to run.</summary>
    [Fact]
    public void The_relaxed_pass_leaves_double_quoted_values_alone()
    {
        var call = SingleCall("""{'name': 'say', 'arguments': {'text': "it's fine"}}""");

        Assert.Equal("say", call.Name);
        Assert.Equal("it's fine", ArgumentValue(call, "text"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Nothing_to_read_is_content(string? reply)
    {
        Assert.Null(ToolCallParser.Parse(reply!));
    }

    /// <summary>Garbage is content. The point of each case is that it returns rather than throws.</summary>
    [Theory]
    [InlineData("{{{{{{{{")]
    [InlineData("[[[[[[[[")]
    [InlineData("}}}}]]]]")]
    [InlineData("\"\"\"")]
    [InlineData("{\"a\":\"\\")]
    [InlineData("```json\n```")]
    [InlineData("```")]
    [InlineData("{\"tool_calls\":")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"name\":{\"name\":\"x\"},\"arguments\":{}}")]
    public void Garbage_never_throws(string reply)
    {
        Assert.Null(ToolCallParser.Parse(reply));
    }

    /// <summary>
    /// A nesting depth past the reader's cap fails the whole candidate rather than being read
    /// partially, and the reply is content. The cap is what stops a pathological reply from costing
    /// stack while the client waits.
    /// </summary>
    [Fact]
    public void A_deeply_nested_argument_is_content_rather_than_a_stack_overflow()
    {
        var depth = 200;
        var reply = """{"name":"a","arguments":""" + string.Concat(Enumerable.Repeat("""{"k":""", depth))
            + "1" + new string('}', depth) + "}";

        Assert.Null(ToolCallParser.Parse(reply));
    }

    /// <summary>
    /// The model writes one object and then explains itself in a second one. The first balanced
    /// candidate that reads as calls wins, which keeps the reply deterministic.
    /// </summary>
    [Fact]
    public void The_first_readable_candidate_wins()
    {
        var call = SingleCall("""
            {"tool_calls":[{"name":"first","arguments":{}}]}
            Actually, also: {"tool_calls":[{"name":"second","arguments":{}}]}
            """);

        Assert.Equal("first", call.Name);
    }
}
