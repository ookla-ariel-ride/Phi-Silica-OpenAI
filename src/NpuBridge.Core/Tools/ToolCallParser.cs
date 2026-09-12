using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NpuBridge.Tools;

/// <summary>
/// One tool call the model asked for. <see cref="Arguments"/> is JSON object text, never null and
/// never empty: a call the model gave no arguments for carries <c>"{}"</c>, because OpenAI's wire
/// format types the field as a string holding a JSON object and clients parse it unconditionally.
/// </summary>
/// <param name="Name">The tool the model named, verbatim. The parser knows nothing about which tools
/// were offered, so an unknown name reaches the client as it was written (PLAN §2.6 item 3).</param>
/// <param name="Arguments">The arguments re-serialised as compact JSON object text.</param>
internal sealed record ParsedToolCall(string Name, string Arguments);

/// <summary>
/// Turns a model reply into tool calls, or decides it is ordinary prose.
///
/// The model has no native tool calling: it was asked, in the system section, to answer with a JSON
/// object and it answers with whatever a ~3.3B model answers with — the object inside a fence, the
/// object with a sentence in front of it, the object without its wrapper, a bare array, single
/// quotes, a trailing comma. None of that is an error on the wire, so none of it may be one here:
/// <see cref="Parse"/> never throws, and anything it cannot read is content, which is the outcome a
/// client can still do something with.
///
/// The strategies run in the order PLAN §2.6 item 3 fixes, each more willing to find a call in text
/// that was not meant to be one:
/// <list type="number">
///   <item>a fenced block, which is what the instruction asks for;</item>
///   <item>the first balanced <c>{…}</c> containing <c>"tool_calls"</c>;</item>
///   <item>the first balanced <c>{…}</c> carrying both <c>"name"</c> and <c>"arguments"</c>;</item>
///   <item>the first balanced <c>[{…}]</c> array;</item>
///   <item>all four again over a relaxed rewrite that also accepts single-quoted strings.</item>
/// </list>
/// The order is what keeps a wrapper from being read as its first element: a
/// <c>{"tool_calls":[…]}</c> object contains <c>"name"</c> and <c>"arguments"</c> too, so strategy 3
/// would match the same text one level too deep if it ran first.
///
/// Every scan for a balanced brace is string-aware. A <c>{</c>, <c>}</c> or <c>"</c> inside a JSON
/// string literal is text, not structure, and the escapes matter as much as the quotes: in
/// <c>{"path":"C:\\"}</c> the backslash escapes a backslash and the string ends where it looks like
/// it ends, while in <c>{"q":"\"}"</c> it escapes the quote and the brace is inside the string.
/// Counting either one wrong ends the candidate at the wrong character and loses a call whose
/// arguments merely mentioned a brace.
/// </summary>
internal static class ToolCallParser
{
    /// <summary>
    /// Reading JSON the way the model writes it: trailing commas after the last element are the most
    /// common single defect in a small model's JSON, and a comment is free to allow. The depth cap is
    /// well past any real tool argument and bounds the work a nested candidate can cost.
    /// </summary>
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 64,
    };

    /// <summary>
    /// Non-ASCII is written through rather than escaped, so an emoji or a CJK argument stays legible
    /// in logs and in the arguments string itself. It is still escaped once on the wire, where this
    /// text becomes a JSON string value, so the client decodes the same characters either way.
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The calls the reply asked for, or null when it is ordinary content. Never throws: null, empty,
    /// truncated, adversarial and plainly-not-JSON input all return null, because a malformed reply
    /// is a reply, not a fault. The list is never empty — an object whose <c>tool_calls</c> array is
    /// empty, or whose every entry lacks a name, is content.
    /// </summary>
    internal static IReadOnlyList<ParsedToolCall>? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var strict = ParseStrict(text);
        if (strict is not null)
        {
            return strict;
        }

        // Last resort only. Rewriting single quotes is the one pass that can read prose as a call —
        // an apostrophe opens a string that runs to the next apostrophe — so it runs after every
        // strategy that requires the model to have produced real JSON has already failed.
        var relaxed = RelaxSingleQuotes(text);
        return relaxed is null ? null : ParseStrict(relaxed);
    }

    /// <summary>The four strategies over one piece of text, in PLAN order.</summary>
    private static List<ParsedToolCall>? ParseStrict(string text)
    {
        foreach (var fenced in FencedBlocks(text))
        {
            var calls = Interpret(fenced);
            if (calls is not null)
            {
                return calls;
            }
        }

        return FirstCandidate(text, '{', preferEnclosingArray: false, "\"tool_calls\"")
            ?? FirstCandidate(text, '{', preferEnclosingArray: true, "\"name\"", "\"arguments\"", "\"parameters\"")
            ?? FirstCandidate(text, '[', preferEnclosingArray: false, "\"name\"");
    }

    /// <summary>
    /// The contents of each <c>```</c> fence, in order. The opening fence's info string is ignored
    /// rather than matched against <c>json</c>: the tag is the model's guess at a language name and
    /// it writes <c>JSON</c>, <c>tool_calls</c>, nothing at all, and occasionally <c>python</c>. What
    /// is inside the fence decides, not what the model called it.
    ///
    /// A fence that is never closed yields everything after it, because a reply cut off by the token
    /// budget still holds a complete object more often than not.
    /// </summary>
    private static IEnumerable<string> FencedBlocks(string text)
    {
        const string Fence = "```";
        var at = 0;
        while (true)
        {
            var open = text.IndexOf(Fence, at, StringComparison.Ordinal);
            if (open < 0)
            {
                yield break;
            }

            // The rest of the opening line is the info string; the block starts on the next line.
            var newline = text.IndexOf('\n', open);
            var start = newline < 0 ? text.Length : newline + 1;

            var close = text.IndexOf(Fence, start, StringComparison.Ordinal);
            var end = close < 0 ? text.Length : close;
            yield return text[start..end];

            at = close < 0 ? text.Length : close + Fence.Length;
            if (at >= text.Length)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// The first balanced <paramref name="opener"/>-delimited span that carries
    /// <paramref name="marker"/>, carries one of <paramref name="anyOf"/> when any are named, and
    /// reads as calls.
    ///
    /// The markers are not decoration. Requiring <c>"name"</c> and <c>"arguments"</c> together for
    /// the unwrapped single call is what PLAN §2.6 item 3 asks for, and it is what stops
    /// <c>{"name": "Ada"}</c> in a sentence about a person from becoming a call to a tool named Ada.
    /// Inside a wrapper or an array the model has already declared what the object is, so
    /// <see cref="ReadCall"/> accepts a call with no arguments there and supplies <c>{}</c>.
    ///
    /// The comparison ignores case for the same reason the property lookup does: the markers stand in
    /// for property names, and the model capitalises them as it pleases.
    /// </summary>
    /// <param name="preferEnclosingArray">
    /// Skip a candidate that is an element of an array, leaving it to the bare-array strategy that
    /// runs after. Only the unwrapped single-call strategy sets it, and without it a bare
    /// <c>[{"name":"a",…},{"name":"b",…}]</c> loses every call but the first: that strategy scans for
    /// <c>{</c>, reaches the array's first element before the array strategy ever runs, and returns it
    /// alone. Skipping cannot mis-fire on a call whose own arguments contain an array of objects,
    /// because the object being tested there is the call itself and nothing encloses it.
    /// </param>
    private static List<ParsedToolCall>? FirstCandidate(
        string text, char opener, bool preferEnclosingArray, string marker, params string[] anyOf)
    {
        for (var start = text.IndexOf(opener); start >= 0; start = text.IndexOf(opener, start + 1))
        {
            var end = MatchingBrace(text, start);
            if (end < 0)
            {
                continue;
            }

            if (preferEnclosingArray && IsArrayElement(text, start))
            {
                continue;
            }

            var span = text[start..(end + 1)];
            if (!span.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (anyOf.Length > 0 && !anyOf.Any(m => span.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var calls = Interpret(span);
            if (calls is not null)
            {
                return calls;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the opener at <paramref name="start"/> is an element of an array rather than a value
    /// standing on its own. The nearest non-whitespace character before it decides: <c>[</c> makes it
    /// the first element, <c>,</c> a later one. A comma cannot mean anything else in front of an
    /// object — an object that is a property's value follows a colon.
    /// </summary>
    private static bool IsArrayElement(string text, int start)
    {
        for (var i = start - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            return text[i] is '[' or ',';
        }

        return false;
    }

    /// <summary>
    /// The index of the brace or bracket that closes the one at <paramref name="start"/>, or -1 when
    /// the text ends first (a truncated reply, or an opener that was only ever prose).
    ///
    /// String-aware, which is the whole point: a backslash consumes the character after it, so
    /// <c>\"</c> does not end a string and <c>\\</c> does not stop the next quote from ending it, and
    /// structure inside a string is ignored. Without that, <c>{"note":"} done"}</c> closes at the
    /// brace in the string and the candidate is invalid JSON.
    /// </summary>
    private static int MatchingBrace(string text, int start)
    {
        var opener = text[start];
        var closer = opener == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == opener)
            {
                depth++;
            }
            else if (c == closer && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads one JSON fragment as calls: a <c>{"tool_calls":[…]}</c> wrapper, a bare array of calls,
    /// or a single unwrapped call. Null when it does not parse or holds no call with a name.
    /// </summary>
    private static List<ParsedToolCall>? Interpret(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var calls = new List<ParsedToolCall>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                ReadCalls(root, calls);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGet(root, "tool_calls", out var wrapped))
                {
                    // A model that emits one call sometimes drops the array and leaves the object.
                    if (wrapped.ValueKind == JsonValueKind.Array)
                    {
                        ReadCalls(wrapped, calls);
                    }
                    else if (wrapped.ValueKind == JsonValueKind.Object && ReadCall(wrapped) is { } single)
                    {
                        calls.Add(single);
                    }
                }
                else if (ReadCall(root) is { } bare)
                {
                    calls.Add(bare);
                }
            }

            return calls.Count == 0 ? null : calls;
        }
    }

    private static void ReadCalls(JsonElement array, List<ParsedToolCall> into)
    {
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object && ReadCall(element) is { } call)
            {
                into.Add(call);
            }
        }
    }

    /// <summary>
    /// One call object. Null when it carries no usable name, which drops that entry rather than the
    /// whole reply: a model that produced two calls and named only one has still asked for one.
    ///
    /// Three shapes are accepted for the same thing. <c>{"name":…,"arguments":…}</c> is what the
    /// injected instruction asks for; <c>{"function":{"name":…}}</c> is OpenAI's own wire shape,
    /// which a model that has seen tool calls in its training data (or our own reply, fed back as
    /// history) reproduces; <c>parameters</c> is the JSON Schema word for the same field and the
    /// model reads the word in the schema it was handed.
    /// </summary>
    private static ParsedToolCall? ReadCall(JsonElement call)
    {
        var body = TryGet(call, "function", out var function) && function.ValueKind == JsonValueKind.Object
            ? function
            : call;

        if (!TryGet(body, "name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var tool = name.GetString()?.Trim();
        if (string.IsNullOrEmpty(tool))
        {
            return null;
        }

        if (!TryGet(body, "arguments", out var arguments))
        {
            TryGet(body, "parameters", out arguments);
        }

        return new ParsedToolCall(tool, Arguments(arguments));
    }

    /// <summary>
    /// The arguments as compact JSON object text. Anything that is not an object becomes <c>{}</c>
    /// rather than being passed through, because the field is typed as an object everywhere it is
    /// going and a client that does <c>JSON.parse(arguments).x</c> should get undefined, not a throw.
    ///
    /// The encoded-string form is the case worth having: <c>"arguments": "{\"a\":1}"</c> is what a
    /// model imitating OpenAI's wire format produces, where the field really is a string, and it
    /// carries the same arguments as the object form.
    /// </summary>
    private static string Arguments(JsonElement arguments)
    {
        switch (arguments.ValueKind)
        {
            case JsonValueKind.Object:
                return Compact(arguments);

            case JsonValueKind.String:
                var inner = arguments.GetString();
                if (string.IsNullOrWhiteSpace(inner))
                {
                    return "{}";
                }

                try
                {
                    using var nested = JsonDocument.Parse(inner, DocumentOptions);
                    return nested.RootElement.ValueKind == JsonValueKind.Object
                        ? Compact(nested.RootElement)
                        : "{}";
                }
                catch (JsonException)
                {
                    return "{}";
                }

            default:
                return "{}";
        }
    }

    /// <summary>
    /// Re-writes an element as compact JSON. The raw text is deliberately not reused: it still
    /// carries whatever the model wrote around the values, including the trailing comma and the
    /// comment the reader was told to tolerate, and a trailing comma inside the arguments string
    /// would fail in the client's own parser after passing ours.
    /// </summary>
    private static string Compact(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Property lookup that ignores case, since a model writes <c>Name</c> and <c>Tool_Calls</c> as
    /// readily as the lowercase the instruction showed it.
    /// </summary>
    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // ---------------------------------------------------------------------------------------------
    // The last-resort relaxed pass. Everything below this line runs only after all four strategies
    // have failed on the text as written, because it is the one rewrite that can manufacture JSON out
    // of prose: "don't" opens a string at the apostrophe that swallows text until the next one. It is
    // worth having because a small model asked for JSON does emit Python-flavoured dict syntax, and
    // it is safe enough only because the rewrite still has to parse and still has to yield a named
    // call before anything is returned.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The text with single-quoted strings rewritten as double-quoted ones, or null when there is no
    /// apostrophe in it and the pass would change nothing.
    ///
    /// Quotes already inside a double-quoted string are left alone, so a legitimate string containing
    /// an apostrophe does not start a rewrite; inside a rewritten string an unescaped <c>"</c> is
    /// escaped and <c>\'</c> becomes a plain apostrophe, both of which would otherwise turn a
    /// successful rewrite into invalid JSON.
    /// </summary>
    private static string? RelaxSingleQuotes(string text)
    {
        if (!text.Contains('\''))
        {
            return null;
        }

        var relaxed = new StringBuilder(text.Length);
        var inDouble = false;
        var inSingle = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inDouble)
            {
                relaxed.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    relaxed.Append(text[++i]);
                }
                else if (c == '"')
                {
                    inDouble = false;
                }

                continue;
            }

            if (inSingle)
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    var escaped = text[++i];
                    if (escaped == '\'')
                    {
                        relaxed.Append('\'');
                    }
                    else
                    {
                        relaxed.Append(c).Append(escaped);
                    }
                }
                else if (c == '\'')
                {
                    relaxed.Append('"');
                    inSingle = false;
                }
                else if (c == '"')
                {
                    relaxed.Append("\\\"");
                }
                else
                {
                    relaxed.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inDouble = true;
                    relaxed.Append(c);
                    break;
                case '\'':
                    inSingle = true;
                    relaxed.Append('"');
                    break;
                default:
                    relaxed.Append(c);
                    break;
            }
        }

        return relaxed.ToString();
    }
}
