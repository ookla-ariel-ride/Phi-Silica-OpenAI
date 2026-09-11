using NpuBridge.Api;
using NpuBridge.Prompting;

namespace NpuBridge.Tests;

public class PromptTemplateTests
{
    private static ChatMessage User(string? text) => new("user", ChatMessageContent.FromText(text), null, null);

    private static ChatMessage Assistant(string? text) => new("assistant", ChatMessageContent.FromText(text), null, null);

    private static ChatMessage System(string? text) => new("system", ChatMessageContent.FromText(text), null, null);

    private static ChatMessage Developer(string? text) => new("developer", ChatMessageContent.FromText(text), null, null);

    private static ChatMessage Tool(string? text, string? name, string? toolCallId) =>
        new("tool", ChatMessageContent.FromText(text), name, toolCallId);

    [Fact]
    public void Bare_single_user_message_is_passed_through_raw()
    {
        var messages = new[] { User("  what's the capital of france?  ") };

        var result = PromptTemplate.Render(messages, nativeSystemPromptSupported: true);

        Assert.Equal("  what's the capital of france?  ", result.Prompt);
        Assert.Null(result.SystemText);
        Assert.False(result.SystemInPrompt);
    }

    [Fact]
    public void Single_user_message_plus_system_message_uses_markers()
    {
        var messages = new ChatMessage[] { System("Be terse."), User("hi") };

        var result = PromptTemplate.Render(messages, nativeSystemPromptSupported: true);

        Assert.Equal(
            "### Conversation so far\n\n" +
            "### Reply as the assistant to the latest message.\n" +
            "[User]\n" +
            "hi",
            result.Prompt);
        Assert.Equal("Be terse.", result.SystemText);
        Assert.False(result.SystemInPrompt);
    }

    [Fact]
    public void Three_turn_conversation_renders_in_order_with_markers_and_headings()
    {
        var messages = new ChatMessage[]
        {
            User("what's the weather in paris?"),
            Assistant("Let me check."),
            User("thanks, and london too?"),
        };

        var result = PromptTemplate.Render(messages, nativeSystemPromptSupported: true);

        Assert.Equal(
            "### Conversation so far\n" +
            "[User]\n" +
            "what's the weather in paris?\n" +
            "[Assistant]\n" +
            "Let me check.\n\n" +
            "### Reply as the assistant to the latest message.\n" +
            "[User]\n" +
            "thanks, and london too?",
            result.Prompt);
    }

    [Fact]
    public void System_and_developer_messages_join_in_order_and_never_appear_in_transcript()
    {
        var messages = new ChatMessage[]
        {
            System("System first."),
            Developer("Developer second."),
            User("hello"),
        };

        var result = PromptTemplate.Render(messages, nativeSystemPromptSupported: true);

        Assert.Equal("System first.\n\nDeveloper second.", result.SystemText);
        Assert.DoesNotContain("System first.", result.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Developer second.", result.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_system_prompt_supported_keeps_system_text_out_of_the_prompt()
    {
        var messages = new ChatMessage[] { System("Be terse."), User("hi") };

        var result = PromptTemplate.Render(messages, nativeSystemPromptSupported: true);

        Assert.Equal("Be terse.", result.SystemText);
        Assert.False(result.SystemInPrompt);
        Assert.DoesNotContain("Be terse.", result.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_system_prompt_unsupported_folds_system_text_into_top_of_prompt()
    {
        var messages = new ChatMessage[] { System("Be terse."), User("hi") };

        var result = PromptTemplate.Render(messages, nativeSystemPromptSupported: false);

        Assert.Equal("Be terse.", result.SystemText);
        Assert.True(result.SystemInPrompt);
        Assert.Equal(
            "Be terse.\n\n" +
            "### Conversation so far\n\n" +
            "### Reply as the assistant to the latest message.\n" +
            "[User]\n" +
            "hi",
            result.Prompt);
    }

    [Fact]
    public void Tool_message_renders_with_documented_marker()
    {
        var messages = new ChatMessage[]
        {
            User("what's the weather?"),
            Assistant(null),
            Tool("{\"temp\": 21}", "get_weather", "call_01"),
            User("thanks"),
        };

        var result = PromptTemplate.Render(messages, nativeSystemPromptSupported: true);

        Assert.Equal(
            "### Conversation so far\n" +
            "[User]\n" +
            "what's the weather?\n" +
            "[Assistant]\n" +
            "\n" +
            "[Tool result: get_weather (call_01)]\n" +
            "{\"temp\": 21}\n\n" +
            "### Reply as the assistant to the latest message.\n" +
            "[User]\n" +
            "thanks",
            result.Prompt);
    }

    [Fact]
    public void Tool_message_missing_name_and_id_degrades_gracefully()
    {
        var messages = new ChatMessage[]
        {
            User("go"),
            Tool("result text", null, null),
            User("and then?"),
        };

        var result = PromptTemplate.Render(messages, nativeSystemPromptSupported: true);

        Assert.Contains("[Tool result]\nresult text", result.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Array_of_parts_content_is_joined_in_order()
    {
        var parts = new[] { new ChatContentPart("text", "first"), new ChatContentPart("text", "second") };
        var messages = new ChatMessage[] { new("user", ChatMessageContent.FromParts(parts), null, null) };

        var result = PromptTemplate.Render(messages, nativeSystemPromptSupported: true);

        Assert.Equal("first\nsecond", result.Prompt);
    }

    [Fact]
    public void Rendering_the_same_messages_twice_is_deterministic()
    {
        var messages = new ChatMessage[]
        {
            System("Be terse."),
            User("what's the weather in paris?"),
            Assistant("Let me check."),
            Tool("{\"temp\": 21}", "get_weather", "call_01"),
            User("and london?"),
        };

        var first = PromptTemplate.Render(messages, nativeSystemPromptSupported: false);
        var second = PromptTemplate.Render(messages, nativeSystemPromptSupported: false);

        Assert.Equal(first.Prompt, second.Prompt);
        Assert.Equal(first.SystemText, second.SystemText);
        Assert.Equal(first.SystemInPrompt, second.SystemInPrompt);
    }

    [Fact]
    public void Conversation_ending_in_an_assistant_message_renders_without_throwing()
    {
        var messages = new ChatMessage[]
        {
            User("hello"),
            Assistant("hi there"),
        };

        var result = PromptTemplate.Render(messages, nativeSystemPromptSupported: true);

        Assert.Equal(
            "### Conversation so far\n" +
            "[User]\n" +
            "hello\n" +
            "[Assistant]\n" +
            "hi there\n\n" +
            "### Reply as the assistant to the latest message.",
            result.Prompt);
        Assert.False(result.Prompt.EndsWith('\n'));
    }

    [Fact]
    public void A_tail_of_one_user_message_uses_the_markers_and_never_the_raw_pass_through()
    {
        // The raw form exists for the single-message curl case; a tail continues a marked-up
        // conversation the context already holds, so it keeps the markers.
        Assert.Equal(
            "### Conversation so far\n\n" +
            "### Reply as the assistant to the latest message.\n" +
            "[User]\n" +
            "and london too?",
            PromptTemplate.RenderTail([User("and london too?  ")]));
    }

    [Fact]
    public void A_tail_with_tool_results_renders_them_before_the_reply_heading()
    {
        var tail = new ChatMessage[] { Tool("{\"temp\": 21}", "get_weather", "call_01"), User("so?") };

        Assert.Equal(
            "### Conversation so far\n" +
            "[Tool result: get_weather (call_01)]\n" +
            "{\"temp\": 21}\n\n" +
            "### Reply as the assistant to the latest message.\n" +
            "[User]\n" +
            "so?",
            PromptTemplate.RenderTail(tail));
    }

    [Fact]
    public void A_tail_ignores_system_messages_and_an_empty_tail_is_the_reply_heading_alone()
    {
        Assert.Equal(PromptTemplate.RenderTail([User("x")]), PromptTemplate.RenderTail([System("late system text"), User("x")]));
        Assert.Equal(
            "### Conversation so far\n\n" +
            "### Reply as the assistant to the latest message.",
            PromptTemplate.RenderTail([]));
    }

    [Fact]
    public void Turn_text_is_the_rendered_body_of_the_turn()
    {
        Assert.Equal("hi", PromptTemplate.TurnText(User("hi  \n")));
        Assert.Equal(string.Empty, PromptTemplate.TurnText(Assistant(null)));
        ChatMessage[] mixed = [System("s"), User("a"), Developer("d"), Assistant("b")];
        Assert.Equal(["user", "assistant"], PromptTemplate.Turns(mixed).Select(m => m.Role));
        Assert.Equal(["system", "developer"], PromptTemplate.SystemMessages(mixed).Select(m => m.Role));
    }
}
