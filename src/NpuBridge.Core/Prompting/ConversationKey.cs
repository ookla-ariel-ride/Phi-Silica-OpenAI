using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NpuBridge.Api;

namespace NpuBridge.Prompting;

/// <summary>
/// One cacheable prefix of a transcript: <see cref="TurnCount"/> turns starting <see cref="Offset"/>
/// turns in, keyed. The offset is zero for the transcript's own prefixes and positive only for the
/// suffix candidates a session tries under <c>--truncate-history</c>, where a cached context holds a
/// transcript whose oldest exchanges were dropped (issue #12).
/// </summary>
/// <param name="TurnCount">How many turns the prefix covers, counted from <see cref="Offset"/>. The last of them is an assistant turn.</param>
/// <param name="Key">The conversation key of <c>(system, turn_offset … turn_{offset+TurnCount-1})</c>.</param>
/// <param name="Offset">Leading turns of the transcript that the prefix does not cover.</param>
public sealed record ConversationPrefix(int TurnCount, string Key, int Offset = 0);

/// <summary>
/// The context cache's identity function (PLAN section 2.5): SHA-256 over a canonical encoding of
/// <c>(system, turn_0 … turn_k)</c>. Deliberately <b>not</b> the rendered prompt. Three things make
/// <see cref="PromptTemplate.Render"/>'s output unusable as an identity, all recorded before the cache
/// existed: turn markers are not escaped, so a user message containing a line reading
/// <c>[Assistant]</c> renders like a real turn boundary; native system-prompt placement leaves the
/// system text out of the prompt entirely, so two conversations differing only in their system prompt
/// render byte-identically; and a lone bare user message is passed through raw, with no markers at
/// all, so a user message that happens to look like a transcript collides with that transcript.
///
/// The encoding here is boundary-preserving by construction: every field is written as a presence
/// byte, its byte length and then its bytes, so no content can imitate a field boundary, a turn
/// boundary or the system text, whatever it contains. What goes into a field is the same text the
/// model sees for that turn — trailing whitespace trimmed, content parts joined — so a client that
/// re-serialises our own reply with different trailing whitespace still hits, and an assistant turn's
/// <c>tool_calls</c> are encoded field by field (id, type, function name, trimmed arguments text)
/// rather than as the raw JSON, so a client that re-serialises the tool-call objects with different
/// key order or spacing still hits. The arguments text itself is compared as sent, trimmed: it is an
/// opaque string on the wire and stays one here.
///
/// The system text is whatever <see cref="PromptTemplate.Render"/> reported: null when there was no
/// system message, the empty string when there was an empty one, and the joined text otherwise. The
/// three are distinct inputs, because they are three distinct context states.
/// </summary>
public static class ConversationKey
{
    /// <summary>Bumped whenever the encoding changes, so a stale key can never match a new one.</summary>
    private static readonly byte[] Magic = Encoding.UTF8.GetBytes("npu-bridge/conversation-key/1\n");

    private const byte TagSystem = 0x01;
    private const byte TagTurn = 0x02;
    private const byte TagToolCall = 0x03;

    /// <summary>
    /// The key of the whole transcript, optionally extended by <paramref name="assistantReply"/>. The
    /// extended form is what a context is stored under after a successful generation: it has absorbed
    /// the prefix and the reply, so it is the context for the transcript the client will send back next.
    ///
    /// The reply is a whole <see cref="ChatMessage"/> rather than its text because that is what the
    /// client sends back. A tool call returns as an assistant message with null content and a
    /// <c>tool_calls</c> array, and <see cref="AppendTurn"/> hashes that array's ids, names and
    /// arguments — so a reply stored as text could never match one, and every tool-using conversation
    /// missed its next turn (D83).
    /// </summary>
    public static string Compute(string? systemText, IReadOnlyList<ChatMessage> turns, ChatMessage? assistantReply = null)
    {
        ArgumentNullException.ThrowIfNull(turns);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSystem(hash, systemText);
        foreach (var turn in turns)
        {
            AppendTurn(hash, turn);
        }

        if (assistantReply is not null)
        {
            AppendTurn(hash, assistantReply);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Every prefix of <paramref name="turns"/> that ends in an assistant turn, keyed, shortest first.
    /// One pass over the transcript: the hash is snapshotted at each assistant boundary rather than
    /// recomputed per prefix. A caller looking for the longest cached prefix walks the list backwards.
    /// </summary>
    public static IReadOnlyList<ConversationPrefix> PrefixKeys(string? systemText, IReadOnlyList<ChatMessage> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var prefixes = new List<ConversationPrefix>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSystem(hash, systemText);

        for (var i = 0; i < turns.Count; i++)
        {
            AppendTurn(hash, turns[i]);
            if (string.Equals(turns[i].Role, "assistant", StringComparison.Ordinal))
            {
                prefixes.Add(new ConversationPrefix(i + 1, Convert.ToHexStringLower(hash.GetCurrentHash())));
            }
        }

        return prefixes;
    }

    private static void AppendSystem(IncrementalHash hash, string? systemText)
    {
        hash.AppendData(Magic);
        hash.AppendData([TagSystem]);
        AppendField(hash, systemText);
    }

    private static void AppendTurn(IncrementalHash hash, ChatMessage turn)
    {
        hash.AppendData([TagTurn]);
        AppendField(hash, turn.Role);
        AppendField(hash, PromptTemplate.TurnText(turn));
        AppendField(hash, turn.Name);
        AppendField(hash, turn.ToolCallId);

        var toolCalls = turn.ToolCalls;
        AppendInt(hash, toolCalls?.Count ?? 0);
        if (toolCalls is null)
        {
            return;
        }

        foreach (var call in toolCalls)
        {
            hash.AppendData([TagToolCall]);
            AppendField(hash, call?.Id);
            AppendField(hash, call?.Type);
            AppendField(hash, call?.Function?.Name);
            AppendField(hash, call?.Function?.Arguments?.Trim());
        }
    }

    /// <summary>Presence byte, then for a present value its UTF-8 byte length and the bytes.</summary>
    private static void AppendField(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            hash.AppendData([0]);
            return;
        }

        hash.AppendData([1]);
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        hash.AppendData(buffer);
    }
}
