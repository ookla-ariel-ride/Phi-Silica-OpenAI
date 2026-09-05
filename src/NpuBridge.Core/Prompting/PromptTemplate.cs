using System.Text;
using NpuBridge.Api;

namespace NpuBridge.Prompting;

/// <summary>
/// Result of flattening an OpenAI <c>messages</c> array into the single prompt string the backend
/// APIs require. <see cref="SystemText"/> is null only when no <c>system</c>/<c>developer</c> message
/// was present at all; when one was present but empty it is the empty string, still non-null, so a
/// caller can tell "no system message" apart from "an empty one". <see cref="SystemInPrompt"/> is true
/// only when <see cref="SystemText"/> was actually folded into the top of <see cref="Prompt"/>, i.e.
/// when the caller asked for that placement and there was non-empty system text to fold.
/// </summary>
public sealed record RenderedPrompt(
    string? SystemText,
    string Prompt,
    bool SystemInPrompt);

/// <summary>
/// Flattens an OpenAI chat <c>messages</c> array into the single flat prompt string
/// <see cref="NpuBridge.Backends.ILanguageModelBackend.GenerateAsync"/> requires, per docs/PLAN.md
/// section 2.3 / the chunk 3 prompt-rendering constraints (reproduced in the format below). Pure
/// logic: no I/O, no backend dependency. Deterministic by construction — the same messages always
/// render to the same string — because chunk 5's context cache hashes this exact output as its key.
///
/// Format for anything other than a bare single user message:
/// <code>
/// ### Conversation so far
/// [User]
/// ...
/// [Assistant]
/// ...
/// [Tool result: get_weather (call_01)]
/// {"temp": 21}
///
/// ### Reply as the assistant to the latest message.
/// [User]
/// &lt;newest user text&gt;
/// </code>
/// </summary>
public static class PromptTemplate
{
    private const string ConversationHeading = "### Conversation so far";
    private const string ReplyHeading = "### Reply as the assistant to the latest message.";

    /// <param name="messages">The request's chat messages, in wire order.</param>
    /// <param name="nativeSystemPromptSupported">
    /// True when the target backend can take system text through its own context (see
    /// <c>BackendCapabilities.SystemPromptContext</c>): system text is returned in
    /// <see cref="RenderedPrompt.SystemText"/> for the caller to hand to the backend separately, and
    /// left out of <see cref="RenderedPrompt.Prompt"/>. False folds it into the top of the prompt
    /// instead. Both placements are required by the controller measuring which one this model
    /// actually obeys; this method makes the choice a parameter rather than a policy.
    /// </param>
    public static RenderedPrompt Render(IReadOnlyList<ChatMessage> messages, bool nativeSystemPromptSupported)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Rule: a bare single user message with no system/developer content and no history renders
        // as its raw text, byte for byte, with no markers of any kind added.
        if (messages.Count == 1 && string.Equals(messages[0].Role, "user", StringComparison.Ordinal))
        {
            return new RenderedPrompt(null, GetText(messages[0].Content), false);
        }

        var systemText = BuildSystemText(messages);
        var turns = messages.Where(m => !IsSystemLike(m.Role)).ToList();
        var body = BuildBody(turns);

        // Nothing to fold: either the caller wants the native system-context path, or there was no
        // (non-empty) system text to begin with. SystemText is still returned either way so a caller
        // can log or forward it.
        if (nativeSystemPromptSupported || string.IsNullOrEmpty(systemText))
        {
            return new RenderedPrompt(systemText, body, false);
        }

        var folded = systemText + "\n\n" + body;
        return new RenderedPrompt(systemText, folded, true);
    }

    private static bool IsSystemLike(string? role) =>
        string.Equals(role, "system", StringComparison.Ordinal) ||
        string.Equals(role, "developer", StringComparison.Ordinal);

    /// <summary>
    /// System and developer messages both feed the system text, in order, joined with a blank line.
    /// Returns null only when there was no system/developer message at all.
    /// </summary>
    private static string? BuildSystemText(IReadOnlyList<ChatMessage> messages)
    {
        List<string>? entries = null;

        foreach (var message in messages)
        {
            if (!IsSystemLike(message.Role))
            {
                continue;
            }

            entries ??= [];
            entries.Add(GetText(message.Content).TrimEnd());
        }

        return entries is null ? null : string.Join("\n\n", entries);
    }

    /// <summary>
    /// Builds the <c>### Conversation so far</c> / <c>### Reply as...</c> body from every non-system
    /// message, in order. The last turn in <paramref name="turns"/> — not the last user-role message
    /// wherever it falls — decides the shape: when it is a user turn, it becomes the trailing
    /// <c>[User]</c> block after the reply heading and every earlier turn goes into the conversation
    /// block; otherwise (e.g. a trailing assistant message, or no turns at all) every turn, including
    /// the last, goes into the conversation block and the reply heading ends the string with nothing
    /// after it.
    /// </summary>
    private static string BuildBody(List<ChatMessage> turns)
    {
        var isLastUser = turns.Count > 0 && string.Equals(turns[^1].Role, "user", StringComparison.Ordinal);
        var beforeTurns = isLastUser ? turns.Take(turns.Count - 1) : turns;
        var conversationLines = beforeTurns.Select(RenderTurn).ToList();

        var sb = new StringBuilder();
        sb.Append(ConversationHeading);
        if (conversationLines.Count > 0)
        {
            sb.Append('\n').Append(string.Join("\n", conversationLines));
        }

        sb.Append("\n\n").Append(ReplyHeading);

        if (isLastUser)
        {
            sb.Append('\n').Append("[User]\n").Append(RenderTurnBody(turns[^1]));
        }

        return sb.ToString();
    }

    private static string RenderTurn(ChatMessage message)
    {
        var marker = message.Role switch
        {
            "user" => "[User]",
            "assistant" => "[Assistant]",
            "tool" => ToolMarker(message),
            _ => $"[{message.Role}]",
        };

        return marker + "\n" + RenderTurnBody(message);
    }

    private static string RenderTurnBody(ChatMessage message) => GetText(message.Content).TrimEnd();

    /// <summary>
    /// <c>[Tool result: name (id)]</c> when both are present. When one is missing, drop just that
    /// piece rather than render a placeholder or a stray space: <c>[Tool result: name]</c>,
    /// <c>[Tool result: (id)]</c>, or bare <c>[Tool result]</c> when neither is present.
    /// </summary>
    private static string ToolMarker(ChatMessage message)
    {
        var hasName = !string.IsNullOrEmpty(message.Name);
        var hasId = !string.IsNullOrEmpty(message.ToolCallId);

        return (hasName, hasId) switch
        {
            (true, true) => $"[Tool result: {message.Name} ({message.ToolCallId})]",
            (true, false) => $"[Tool result: {message.Name}]",
            (false, true) => $"[Tool result: ({message.ToolCallId})]",
            (false, false) => "[Tool result]",
        };
    }

    /// <summary>
    /// A message's text content, whichever wire shape it arrived in. Array-of-parts content is joined
    /// with a newline in order; null or empty content renders as an empty string, never the literal
    /// "null".
    /// </summary>
    private static string GetText(ChatMessageContent? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        return content.IsParts
            ? string.Join("\n", content.Parts!.Select(p => p.Text ?? string.Empty))
            : content.Text ?? string.Empty;
    }
}
