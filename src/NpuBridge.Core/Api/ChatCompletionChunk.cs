using System.Text.Json.Serialization;

namespace NpuBridge.Api;

/// <summary>
/// Wire shape of one server-sent event on a streamed <c>POST /v1/chat/completions</c>. Every chunk of
/// one response repeats the same <see cref="Id"/>, <see cref="Created"/> and <see cref="Model"/> the
/// non-streamed reply would have carried; only <see cref="Choices"/> differs from frame to frame.
/// <see cref="Usage"/> is set on exactly one trailing chunk, and only when the request asked for it
/// through <c>stream_options.include_usage</c>; that chunk carries an empty <see cref="Choices"/>.
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
}

/// <summary>
/// One choice inside a chunk. <see cref="FinishReason"/> is null on every chunk but the last real one,
/// and null properties are omitted by <see cref="JsonDefaults.Options"/>, so it simply does not appear
/// until the stream ends.
/// </summary>
public sealed record ChatCompletionChunkChoice(
    int Index,
    ChatCompletionDelta Delta,
    string? FinishReason);

/// <summary>
/// The incremental half of a <see cref="ChatCompletionResponseMessage"/>. The first chunk sets
/// <see cref="Role"/> with an empty <see cref="Content"/>; content chunks set only
/// <see cref="Content"/>; the finish chunk sets neither and serialises as <c>{}</c>.
/// </summary>
public sealed record ChatCompletionDelta(
    string? Role,
    string? Content);
