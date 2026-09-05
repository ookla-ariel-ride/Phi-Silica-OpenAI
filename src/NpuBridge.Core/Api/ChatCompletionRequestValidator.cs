namespace NpuBridge.Api;

/// <summary>
/// A validation failure ready to become an OpenAI error body via <see cref="OpenAiError.Result"/>:
/// <see cref="Param"/> and <see cref="Code"/> map straight to that method's <c>param</c>/<c>code</c>
/// arguments, so the endpoint layer (chunk 3 task 3) makes no decisions of its own about the shape of
/// the error, only the HTTP status code.
/// </summary>
public sealed record ChatCompletionValidationFailure(string Message, string? Param, string? Code);

/// <summary>
/// Result of <see cref="ChatCompletionRequestValidator.Validate"/>. Either <see cref="Failure"/> is
/// set (request is invalid) or it is null and <see cref="IgnoredParameters"/> lists the accepted-but-
/// not-yet-implemented parameters present on the request, for the caller to log once per process.
/// </summary>
public sealed class ChatCompletionValidationResult
{
    private ChatCompletionValidationResult(ChatCompletionValidationFailure? failure, IReadOnlyList<string> ignoredParameters)
    {
        Failure = failure;
        IgnoredParameters = ignoredParameters;
    }

    public bool IsValid => Failure is null;

    public ChatCompletionValidationFailure? Failure { get; }

    public IReadOnlyList<string> IgnoredParameters { get; }

    public static ChatCompletionValidationResult Valid(IReadOnlyList<string> ignoredParameters) =>
        new(null, ignoredParameters);

    public static ChatCompletionValidationResult Invalid(string message, string? param, string? code = null) =>
        new(new ChatCompletionValidationFailure(message, param, code), []);
}

/// <summary>
/// Validates a <see cref="ChatCompletionRequest"/> against the rules for this chunk (see
/// docs/PLAN.md / the chunk 3 constraints): non-empty <c>messages</c> with known roles and only
/// <c>text</c> content parts, <c>n</c> at most 1, and no streaming yet. Does not log; the caller
/// decides what to do with <see cref="ChatCompletionValidationResult.IgnoredParameters"/>.
/// </summary>
public static class ChatCompletionRequestValidator
{
    private static readonly string[] KnownRoles = ["system", "developer", "user", "assistant", "tool"];

    public static ChatCompletionValidationResult Validate(ChatCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Messages is null || request.Messages.Count == 0)
        {
            return ChatCompletionValidationResult.Invalid("messages is required and must be a non-empty array.", param: "messages");
        }

        foreach (var message in request.Messages)
        {
            if (message.Role is null || Array.IndexOf(KnownRoles, message.Role) < 0)
            {
                return ChatCompletionValidationResult.Invalid(
                    $"messages contains an unknown or missing role: '{message.Role}'.", param: "messages");
            }

            if (message.Content is { IsParts: true, Parts: { } parts })
            {
                foreach (var part in parts)
                {
                    if (!string.Equals(part.Type, "text", StringComparison.Ordinal))
                    {
                        return ChatCompletionValidationResult.Invalid(
                            $"messages contains a content part of type '{part.Type}'; only 'text' is supported.", param: "messages");
                    }
                }
            }
        }

        if (request.N is > 1)
        {
            return ChatCompletionValidationResult.Invalid("n greater than 1 is not supported.", param: "n");
        }

        if (request.Stream == true)
        {
            return ChatCompletionValidationResult.Invalid(
                "Streaming (stream: true) is not implemented yet.", param: null);
        }

        return ChatCompletionValidationResult.Valid(CollectIgnoredParameters(request));
    }

    private static List<string> CollectIgnoredParameters(ChatCompletionRequest request)
    {
        var ignored = new List<string>();

        void AddIfPresent(bool present, string name)
        {
            if (present)
            {
                ignored.Add(name);
            }
        }

        AddIfPresent(request.Temperature is not null, "temperature");
        AddIfPresent(request.TopP is not null, "top_p");
        AddIfPresent(request.TopK is not null, "top_k");
        AddIfPresent(request.MaxTokens is not null, "max_tokens");
        AddIfPresent(request.MaxCompletionTokens is not null, "max_completion_tokens");
        AddIfPresent(request.Stop is not null, "stop");
        AddIfPresent(request.Tools is not null, "tools");
        AddIfPresent(request.ToolChoice is not null, "tool_choice");
        AddIfPresent(request.Logprobs is not null, "logprobs");
        AddIfPresent(request.ResponseFormat is not null, "response_format");
        AddIfPresent(request.Seed is not null, "seed");
        AddIfPresent(request.PresencePenalty is not null, "presence_penalty");
        AddIfPresent(request.FrequencyPenalty is not null, "frequency_penalty");
        AddIfPresent(request.User is not null, "user");

        return ignored;
    }
}
