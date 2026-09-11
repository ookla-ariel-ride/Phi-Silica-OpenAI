using System.Text.Json.Serialization;

namespace NpuBridge.Api;

/// <summary>
/// Wire shape of a non-streaming <c>POST /v1/chat/completions</c> response. The fields OpenAI's
/// schema marks required but nullable (<c>choices[].logprobs</c>, <c>message.refusal</c>) are written
/// as explicit nulls rather than omitted, against the serializer's usual null omission: a client
/// generated from the schema may read them without a presence check.
/// </summary>
public sealed record ChatCompletionResponse(
    string Id,
    long Created,
    string Model,
    IReadOnlyList<ChatCompletionChoice> Choices,
    CompletionUsage Usage)
{
    [JsonPropertyName("object")]
    public string ObjectType { get; init; } = "chat.completion";
}

/// <summary>
/// <see cref="FinishReason"/> is one of <c>stop | length | tool_calls | content_filter</c>.
/// <see cref="Logprobs"/> is always null: the bridge never computes log probabilities, and the
/// schema requires the field to be present.
/// </summary>
public sealed record ChatCompletionChoice(
    int Index,
    ChatCompletionResponseMessage Message,
    string FinishReason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Logprobs { get; init; }
}

/// <summary>
/// The assistant message. The schema requires <c>role</c>, <c>content</c> and <c>refusal</c>, the
/// last two nullable, so a null <see cref="Content"/> is written as a null rather than omitted.
/// <see cref="Refusal"/> is always null: neither runtime reports a refusal separately from a content
/// filter, which is a finish reason here.
/// </summary>
public sealed record ChatCompletionResponseMessage(
    string Role,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Content)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Refusal { get; init; }
}

public sealed record CompletionUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);
