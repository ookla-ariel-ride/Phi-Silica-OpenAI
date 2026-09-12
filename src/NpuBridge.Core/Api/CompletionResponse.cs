using System.Text.Json.Serialization;

namespace NpuBridge.Api;

/// <summary>
/// Wire shape of a non-streaming <c>POST /v1/completions</c> response: OpenAI's legacy
/// <c>text_completion</c> object. Follows the same D77 conventions as
/// <see cref="ChatCompletionResponse"/> — <see cref="CompletionChoice.Logprobs"/> is written as an
/// explicit null rather than omitted, since the bridge never computes log probabilities and the schema
/// requires the field to be present regardless. <see cref="Usage"/> reuses
/// <see cref="CompletionUsage"/>: it is the same two numbers on both endpoints.
/// </summary>
public sealed record CompletionResponse(
    string Id,
    long Created,
    string Model,
    IReadOnlyList<CompletionChoice> Choices,
    CompletionUsage Usage)
{
    [JsonPropertyName("object")]
    public string ObjectType { get; init; } = "text_completion";
}

/// <summary><see cref="FinishReason"/> is one of <c>stop | length | content_filter</c> — this endpoint has no <c>tools</c>, so never <c>tool_calls</c>.</summary>
public sealed record CompletionChoice(
    string Text,
    int Index,
    string FinishReason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? Logprobs { get; init; }
}
