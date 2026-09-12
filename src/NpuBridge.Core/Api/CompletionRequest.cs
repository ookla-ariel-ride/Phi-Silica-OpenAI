using System.Text.Json.Serialization;

namespace NpuBridge.Api;

/// <summary>
/// Wire shape of a <c>POST /v1/completions</c> request body (PLAN §2.2, chunk 8 task 3). <c>prompt</c>
/// reuses <see cref="StringOrArrayConverter"/> — the same reader <see cref="ChatCompletionRequest.Stop"/>
/// uses — so it accepts a bare string or a JSON array; <see cref="ChatRequestPreparer.PrepareForCompletionAsync"/>
/// is what refuses more than one element (a controller ruling: real OpenAI would batch several prompts
/// into several choices, which this single-worker bridge cannot serve). <c>tools</c> and
/// <c>tool_choice</c> do not exist on this endpoint at all, unlike on the chat shape where they are
/// merely ignored without emulation.
/// </summary>
public sealed record CompletionRequest(
    string? Model,
    [property: JsonConverter(typeof(StringOrArrayConverter))] IReadOnlyList<string>? Prompt,
    bool? Stream,
    int? N,
    float? Temperature,
    float? TopP,
    int? MaxTokens,
    [property: JsonConverter(typeof(StringOrArrayConverter))] IReadOnlyList<string>? Stop,
    ChatStreamOptions? StreamOptions = null);
