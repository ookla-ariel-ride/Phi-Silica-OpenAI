using System.Text.Json.Serialization;

namespace NpuBridge.Api;

/// <summary>Wire shape of a non-streaming <c>POST /v1/chat/completions</c> response.</summary>
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

/// <summary><see cref="FinishReason"/> is one of <c>stop | length | tool_calls | content_filter</c>.</summary>
public sealed record ChatCompletionChoice(
    int Index,
    ChatCompletionResponseMessage Message,
    string FinishReason);

public sealed record ChatCompletionResponseMessage(
    string Role,
    string? Content);

public sealed record CompletionUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);
