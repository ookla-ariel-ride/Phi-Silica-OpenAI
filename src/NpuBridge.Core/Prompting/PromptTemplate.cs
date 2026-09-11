using System.Buffers;
using System.Text;
using System.Text.Json;
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
/// logic: no I/O, no backend dependency. Deterministic by construction: the same messages always
/// render to the same string. It is not the context cache's identity, though: that is
/// <see cref="ConversationKey"/>, which encodes the same per-turn text with boundaries the rendered
/// string does not have.
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

    /// <summary>
    /// The prompt for a context-cache hit: only the turns after the cached prefix, in the marker format
    /// and nothing else. No system text — the cached context already holds it, whichever placement put
    /// it there — and never the raw pass-through, which exists for the common single-message
    /// <c>curl</c> case and would here hand the model a bare string in the middle of a marked-up
    /// conversation. System-role messages in <paramref name="tail"/> are ignored rather than
    /// rendered, defensively: the session never passes any (its turns exclude them, and a system
    /// message anywhere changes the key), so a caller that does is handing over the wrong list.
    /// </summary>
    public static string RenderTail(IReadOnlyList<ChatMessage> tail)
    {
        ArgumentNullException.ThrowIfNull(tail);
        return BuildBody(tail.Where(m => !IsSystemLike(m.Role)).ToList());
    }

    /// <summary>The non-system messages of a request, in order: the turns every rendering and the cache key are over.</summary>
    public static IReadOnlyList<ChatMessage> Turns(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Where(m => !IsSystemLike(m.Role)).ToList();
    }

    /// <summary>The system-role messages of a request, in order, so a caller can rebuild a transcript from truncated turns.</summary>
    public static IReadOnlyList<ChatMessage> SystemMessages(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Where(m => IsSystemLike(m.Role)).ToList();
    }

    /// <summary>
    /// The text of one turn exactly as it is rendered into the prompt: content parts joined, trailing
    /// whitespace trimmed, null content as the empty string. <see cref="ConversationKey"/> hashes this
    /// rather than the raw content so its identity is the text the model saw.
    /// </summary>
    public static string TurnText(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return RenderTurnBody(message);
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

    /// <summary>
    /// One turn's text. An assistant message that made tool calls renders its content, if any, and
    /// then the calls as the same JSON envelope the injected instruction asks the model to produce —
    /// so the model sees its own protocol in the transcript rather than an empty turn where its call
    /// used to be, which is what it saw before chunk 7 and which taught it nothing.
    ///
    /// This is also the body <see cref="TurnText"/> returns, and therefore what
    /// <see cref="ConversationKey"/> hashes. That is deliberate and it is the trap CLAUDE.md names:
    /// anything a turn gains has to enter the key through here, or a cached context gets handed to a
    /// conversation the model never saw. It is the reason the rendering below is byte-deterministic
    /// and the reason it matches what this bridge's own replies serialise to — a client that sends our
    /// <c>tool_calls</c> array back still hits the cache.
    /// </summary>
    private static string RenderTurnBody(ChatMessage message)
    {
        var text = GetText(message.Content).TrimEnd();
        if (message.ToolCalls is not { Count: > 0 } calls)
        {
            return text;
        }

        var rendered = RenderToolCalls(calls);
        return text.Length == 0 ? rendered : text + "\n" + rendered;
    }

    /// <summary>
    /// The tool calls of one assistant turn, in the wire envelope <c>ToolSchemaRenderer</c> instructs
    /// the model to use and <c>ToolCallParser</c> reads back. Compact and in the order the client sent
    /// them: this text is hashed, so indentation or reordering would silently miss the cache.
    ///
    /// The call <c>id</c> is deliberately not rendered. The instruction's envelope has no id — the
    /// model never produced one, the bridge assigns it — and the tool *result* turn carries it in its
    /// marker, which is where the model needs it to match a result to a call. Including it here would
    /// also put a per-request ulid into the cache key, so no conversation could ever hit.
    /// </summary>
    private static string RenderToolCalls(IReadOnlyList<ChatToolCall> calls)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteStartArray("tool_calls");
        foreach (var call in calls)
        {
            writer.WriteStartObject();
            writer.WriteString("name", call.Function?.Name ?? string.Empty);
            writer.WritePropertyName("arguments");
            WriteArguments(writer, call.Function?.Arguments);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// <c>arguments</c> as the model is asked to write it: an object. The client sends it as JSON
    /// text, so it is parsed and re-emitted, which also normalises whitespace out of the hashed text.
    /// Absent or empty becomes <c>{}</c> rather than null, because the instruction's envelope always
    /// has the key. Text that is not valid JSON is written as the JSON string it is: the model wrote
    /// something the bridge could not read, and showing it back verbatim is more use to it than
    /// dropping the turn's only content, while keeping the rendering parseable.
    /// </summary>
    private static void WriteArguments(Utf8JsonWriter writer, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(arguments);
        }
    }

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
