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
/// <c>text</c> content parts, and <c>n</c> at most 1. Does not log; the caller
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

        for (var i = 0; i < request.Messages.Count; i++)
        {
            // System.Text.Json happily deserializes a JSON null into a list of a non-nullable reference
            // type, so the annotation is not a guarantee: check, or a hostile body crashes the request.
            var message = request.Messages[i];
            if (message is null)
            {
                return ChatCompletionValidationResult.Invalid(
                    $"messages[{i}] is null; every message must be a JSON object.", param: "messages");
            }

            if (message.Role is null || Array.IndexOf(KnownRoles, message.Role) < 0)
            {
                return ChatCompletionValidationResult.Invalid(
                    $"messages[{i}] has an unknown or missing role: '{message.Role}'.", param: "messages");
            }

            if (message.Content is { IsParts: true, Parts: { } parts })
            {
                for (var p = 0; p < parts.Count; p++)
                {
                    var part = parts[p];
                    if (!string.Equals(part.Type, "text", StringComparison.Ordinal))
                    {
                        return ChatCompletionValidationResult.Invalid(
                            $"messages[{i}].content[{p}] is a content part of type '{part.Type}'; only 'text' is supported.", param: "messages");
                    }

                    // A part that calls itself text must carry text. An empty string is text; a missing
                    // or null field is a malformed part, not an empty message.
                    if (part.Text is null)
                    {
                        return ChatCompletionValidationResult.Invalid(
                            $"messages[{i}].content[{p}] is a 'text' content part with no 'text' field.", param: "messages");
                    }
                }
            }
        }

        if (request.N is > 1)
        {
            return ChatCompletionValidationResult.Invalid("n greater than 1 is not supported.", param: "n");
        }

        // A cap of zero or less asks for no completion at all. OpenAI rejects it rather than returning an
        // empty reply with finish_reason "length", and so do we: a client that computed a negative budget
        // has a bug, and answering it with an empty string hides that.
        if (request.MaxTokens is { } maxTokens and <= 0)
        {
            return ChatCompletionValidationResult.Invalid(
                $"max_tokens must be a positive integer; got {maxTokens}.", param: "max_tokens");
        }

        if (request.MaxCompletionTokens is { } maxCompletionTokens and <= 0)
        {
            return ChatCompletionValidationResult.Invalid(
                $"max_completion_tokens must be a positive integer; got {maxCompletionTokens}.",
                param: "max_completion_tokens");
        }

        // `stream` is deliberately absent from every list here: chunk 4 implements it, so it is neither
        // an error (D46 recorded the rejection as temporary) nor an ignored parameter. `stream_options`
        // rides with it and is likewise honoured, not ignored. So are `max_tokens`,
        // `max_completion_tokens` and `stop` since chunk 4 task 4 (D53); OutputLimits applies them.
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
