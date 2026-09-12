using NpuBridge.Prompting;
using NpuBridge.Tools;

namespace NpuBridge.Api;

/// <summary>
/// Turns a finished reply into wire-shaped <c>tool_calls</c>, or decides it is ordinary content.
/// Both response shapes call this and neither decides for itself, for the reason D81 gives: the two
/// copies of the post-generation pipeline drifted twice before they were made one, and this is the
/// decision a third copy would have been most likely to get subtly different.
/// </summary>
internal static class ToolCallReply
{
    /// <summary>
    /// The calls to send, or null when the reply is content. Null whenever the request offered no
    /// tools, so the ordinary request pays one null check and nothing else.
    /// </summary>
    /// <param name="tools">The request's catalog; null means emulation is not active for it.</param>
    /// <param name="outcome">
    /// The classified generation. A filtered reply is never parsed: the runtime withheld that text,
    /// and parsing it would be the one place it came back — as arguments, which the client would then
    /// execute.
    /// </param>
    /// <param name="content">The text the client would otherwise receive, after the cut.</param>
    public static IReadOnlyList<ChatCompletionToolCall>? From(
        ToolCatalog? tools,
        GenerationOutcome outcome,
        string? content)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (tools is null || outcome.Filtered || string.IsNullOrEmpty(content))
        {
            return null;
        }

        var parsed = ToolCallParser.Parse(content);
        if (parsed is null)
        {
            return null;
        }

        var calls = new ChatCompletionToolCall[parsed.Count];
        for (var i = 0; i < parsed.Count; i++)
        {
            // A fresh id per call, which is what a client sends back as tool_call_id on the result and
            // what the prompt's tool marker then shows the model. Deliberately not derived from the
            // arguments: two identical calls in one reply are two calls, and a client matching results
            // to calls by id would collapse them.
            calls[i] = new ChatCompletionToolCall(
                i,
                "call_" + Ulid.NewUlid(),
                new ChatCompletionFunctionCall(parsed[i].Name, parsed[i].Arguments));
        }

        return calls;
    }

    /// <summary>
    /// The emitted calls in the request-side shape a client sends back, for the context cache's key,
    /// or null when the reply was not a call.
    ///
    /// The raw model text will not do as the stored key. It is what the context holds — the fence, the
    /// prose around the JSON, whatever the model actually wrote — but that is not what comes back; the
    /// client returns an assistant message with null content and the <c>tool_calls</c> array this
    /// reply emitted, and <c>ConversationKey</c> hashes that array's ids, names and arguments. Keyed
    /// by the text instead, a tool-using conversation missed on its next turn, which is every turn of
    /// an agent loop and the case the cache exists for (D83).
    ///
    /// The ids round-trip because they are ours: the client echoes what it was sent.
    /// </summary>
    public static IReadOnlyList<ChatToolCall>? Carried(IReadOnlyList<ChatCompletionToolCall>? calls) =>
        calls is null
            ? null
            : calls
                .Select(c => new ChatToolCall(c.Id, c.ToolType, new ChatFunctionCall(c.Function.Name, c.Function.Arguments)))
                .ToArray();
}
