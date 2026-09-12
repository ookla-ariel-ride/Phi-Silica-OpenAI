using System.Text.Json.Serialization;

namespace NpuBridge.Api;

/// <summary>
/// Wire shape of one server-sent event on a streamed <c>POST /v1/chat/completions</c>. Every chunk of
/// one response repeats the same <see cref="Id"/>, <see cref="Created"/> and <see cref="Model"/> the
/// non-streamed reply would have carried; only <see cref="Choices"/> differs from frame to frame.
/// <see cref="Usage"/> is set on exactly one trailing chunk, and only when the request asked for it
/// through <c>stream_options.include_usage</c>; that chunk carries an empty <see cref="Choices"/>.
/// When usage was asked for, every other chunk carries <c>"usage": null</c>, as OpenAI's do; when it
/// was not, the field is absent. That is what <see cref="WithNullUsage"/> produces: the null travels
/// in the extension data because the serializer omits null properties everywhere else.
/// </summary>
public sealed record ChatCompletionChunk(
    string Id,
    long Created,
    string Model,
    IReadOnlyList<ChatCompletionChunkChoice> Choices,
    CompletionUsage? Usage = null)
{
    [JsonPropertyName("object")]
    public string ObjectType { get; init; } = "chat.completion.chunk";

    /// <summary>Extra fields written verbatim, including nulls. Only <c>"usage": null</c> uses it.</summary>
    [JsonExtensionData]
    public IDictionary<string, object?>? Extra { get; init; }

    /// <summary>
    /// A copy that writes <c>"usage": null</c>, for the chunks before the usage chunk. A fresh
    /// dictionary each time: the extension data is exposed as a mutable interface, and one shared
    /// instance would let any caller that touched it change every later chunk.
    /// </summary>
    public ChatCompletionChunk WithNullUsage() => this with { Extra = new Dictionary<string, object?> { ["usage"] = null } };
}

/// <summary>
/// One choice inside a chunk. <see cref="FinishReason"/> is null on every chunk but the last real one
/// and is written as an explicit null there, as the schema requires; <see cref="Logprobs"/> is always
/// null, written the same way.
/// </summary>
public sealed record ChatCompletionChunkChoice(
    int Index,
    ChatCompletionDelta Delta,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FinishReason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Logprobs { get; init; }
}

/// <summary>
/// The incremental half of a <see cref="ChatCompletionResponseMessage"/>. The first chunk sets
/// <see cref="Role"/> with an empty <see cref="Content"/>; content chunks set only
/// <see cref="Content"/>; the finish chunk sets neither and serialises as <c>{}</c>.
///
/// A tool-call reply sets <see cref="ToolCalls"/> on one chunk carrying the whole array, arguments
/// included, which is the shape OpenAI produces when it sends the arguments in one piece rather than
/// streaming them across chunks. The bridge always sends them in one piece: it cannot know a reply is
/// a tool call until the model has finished, so there is nothing to stream (PLAN §2.6 item 2).
/// </summary>
public sealed record ChatCompletionDelta(
    string? Role,
    string? Content,
    IReadOnlyList<ChatCompletionToolCall>? ToolCalls = null);

/// <summary>
/// One tool call on the wire. <see cref="Index"/> is required on the streaming shape, where a client
/// assembles calls across chunks by it; it is harmless on the non-streaming one and is written there
/// too rather than kept as a second nearly-identical type.
/// </summary>
public sealed record ChatCompletionToolCall(
    int Index,
    string Id,
    ChatCompletionFunctionCall Function)
{
    [JsonPropertyName("type")]
    public string ToolType { get; init; } = "function";
}

/// <summary><see cref="Arguments"/> is JSON <em>text</em>, per OpenAI's schema — never a nested object.</summary>
public sealed record ChatCompletionFunctionCall(string Name, string Arguments);
