using System.Text.Json.Serialization;

namespace NpuBridge.Api;

/// <summary>
/// Wire shape of one server-sent event on a streamed <c>POST /v1/completions</c>. OpenAI's legacy
/// streaming completions use the same <c>object: "text_completion"</c> on every chunk as the
/// non-streamed reply, unlike the chat shape's separate <c>chat.completion.chunk</c> — there is no
/// role to open the message with, so a chunk carries only <see cref="CompletionChunkChoice.Text"/> and,
/// on the last one, <see cref="CompletionChunkChoice.FinishReason"/>. <see cref="Usage"/> and
/// <see cref="WithNullUsage"/> follow <see cref="ChatCompletionChunk"/>'s convention exactly: set on one
/// trailing chunk with empty <see cref="Choices"/> when <c>stream_options.include_usage</c> was asked
/// for, and an explicit <c>"usage": null</c> on every chunk before it.
/// </summary>
public sealed record CompletionChunk(
    string Id,
    long Created,
    string Model,
    IReadOnlyList<CompletionChunkChoice> Choices,
    CompletionUsage? Usage = null)
{
    [JsonPropertyName("object")]
    public string ObjectType { get; init; } = "text_completion";

    /// <summary>Extra fields written verbatim, including nulls. Only <c>"usage": null</c> uses it.</summary>
    [JsonExtensionData]
    public IDictionary<string, object?>? Extra { get; init; }

    /// <summary>A copy that writes <c>"usage": null</c>, for the chunks before the usage chunk.</summary>
    public CompletionChunk WithNullUsage() => this with { Extra = new Dictionary<string, object?> { ["usage"] = null } };
}

/// <summary>
/// One choice inside a chunk. <see cref="FinishReason"/> is null on every chunk but the last real one
/// and is written as an explicit null there, as the schema requires; <see cref="Logprobs"/> is always
/// null, written the same way.
/// </summary>
public sealed record CompletionChunkChoice(
    string Text,
    int Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FinishReason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Logprobs { get; init; }
}
