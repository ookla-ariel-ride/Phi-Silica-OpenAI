using System.Text.Json;
using System.Text.Json.Serialization;

namespace NpuBridge.Api;

/// <summary>
/// Wire shape of a <c>POST /v1/completions</c> request body (PLAN §2.2, chunk 8 task 3). <c>prompt</c>
/// reuses <see cref="StringOrArrayConverter"/>'s reader — the same one <see cref="ChatCompletionRequest.Stop"/>
/// uses — through <see cref="PromptOrArrayConverter"/>, which only overrides the field name in its error
/// messages (fix round 1, finding 4: a malformed <c>prompt</c> used to be reported as a malformed
/// <c>stop</c>, since both fields shared the same converter and its messages were hard-coded to
/// "stop..."). <see cref="ChatRequestPreparer.PrepareForCompletionAsync"/> is what refuses more than one
/// element (a controller ruling: real OpenAI would batch several prompts into several choices, which
/// this single-worker bridge cannot serve). <c>tools</c> and <c>tool_choice</c> do not exist on this
/// endpoint at all, unlike on the chat shape where they are merely ignored without emulation.
///
/// <see cref="Seed"/>, <see cref="PresencePenalty"/>, <see cref="FrequencyPenalty"/> and
/// <see cref="User"/> carry straight onto the synthesised <see cref="ChatCompletionRequest"/>, so the
/// shared validator's existing ignored-parameter check reports them for free. <see cref="Echo"/>,
/// <see cref="BestOf"/>, <see cref="Suffix"/>, <see cref="Logprobs"/> and <see cref="LogitBias"/> have
/// no equivalent field on <see cref="ChatCompletionRequest"/> at all (this endpoint's <c>logprobs</c>
/// is legacy completions' integer "how many", not chat's boolean "whether") — accepted here purely so
/// <see cref="ChatRequestPreparer.PrepareForCompletionAsync"/> can add them to the same once-per-process
/// warning chat's ignored sampling parameters use, rather than being silently discarded at
/// deserialisation before any validator ever saw them existed (fix round 1, finding 5). None of the
/// five is implemented: <c>echo</c> in particular still returns a different answer than a real OpenAI
/// server would (the prompt is not prepended to <c>text</c>), now with a log line saying so.
/// </summary>
public sealed record CompletionRequest(
    string? Model,
    [property: JsonConverter(typeof(PromptOrArrayConverter))] IReadOnlyList<string>? Prompt,
    bool? Stream,
    int? N,
    float? Temperature,
    float? TopP,
    int? MaxTokens,
    [property: JsonConverter(typeof(StringOrArrayConverter))] IReadOnlyList<string>? Stop,
    long? Seed,
    float? PresencePenalty,
    float? FrequencyPenalty,
    string? User,
    bool? Echo,
    int? BestOf,
    string? Suffix,
    int? Logprobs,
    JsonElement? LogitBias,
    ChatStreamOptions? StreamOptions = null);

/// <summary><see cref="StringOrArrayConverter"/> with its error messages naming <c>prompt</c> instead of <c>stop</c> (fix round 1, finding 4).</summary>
public sealed class PromptOrArrayConverter : StringOrArrayConverter
{
    protected override string FieldName => "prompt";
}
