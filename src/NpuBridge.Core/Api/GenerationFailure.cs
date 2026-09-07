using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NpuBridge.Backends;

namespace NpuBridge.Api;

/// <summary>
/// What a generation that did not succeed is reported as. One mapping, shared by both response shapes,
/// so that the body a client gets from a non-streamed <c>/v1/chat/completions</c> and the body it gets
/// inside a stream's <c>data: {"error":...}</c> event are the same object for the same condition —
/// including the status code, which the streaming path can still use when it learns of the failure
/// before writing its first byte. Keeping the two paths in one place is the point: an over-length
/// prompt answered with 400 on one shape and a cheerful <c>finish_reason: "stop"</c> on the other is
/// exactly the drift this type exists to prevent, and is exactly what happened before it existed.
/// </summary>
/// <param name="StatusCode">The HTTP status, used when nothing has been written yet.</param>
/// <param name="Body">The OpenAI error envelope.</param>
internal sealed record GenerationFailure(int StatusCode, OpenAiErrorBody Body)
{
    /// <summary>The ordinary HTTP answer: status line plus JSON envelope.</summary>
    public IResult ToResult() => OpenAiError.Result(StatusCode, Body);

    /// <summary>The mid-stream answer: one SSE frame carrying the identical envelope.</summary>
    public string ToEventFrame() => $"data: {JsonSerializer.Serialize(Body, JsonDefaults.Options)}\n\n";

    /// <summary>
    /// The failure a terminal status maps to, or null when the status is something the client is told
    /// about as a success. Content filtering is a success with <c>finish_reason: "content_filter"</c>,
    /// not an error: the model answered, the answer was withheld.
    /// </summary>
    public static GenerationFailure? FromStatus(GenerationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            GenerationStatus.Complete or GenerationStatus.ContentFiltered or GenerationStatus.BlockedByPolicy => null,

            GenerationStatus.PromptLargerThanContext => new GenerationFailure(
                StatusCodes.Status400BadRequest,
                OpenAiError.Body(
                    $"The prompt is longer than the model's context window. {result.Detail}".Trim(),
                    OpenAiError.InvalidRequest,
                    code: "context_length_exceeded")),

            GenerationStatus.Cancelled => new GenerationFailure(
                StatusCodes.Status502BadGateway,
                OpenAiError.Body(
                    $"Generation was cancelled by the backend. {result.Detail}".Trim(),
                    OpenAiError.Server)),

            // GenerationStatus.Error and anything a future adapter adds without updating this switch.
            _ => new GenerationFailure(
                StatusCodes.Status502BadGateway,
                OpenAiError.Body(
                    $"The model failed to generate a response. {result.Detail}".Trim(),
                    OpenAiError.Server)),
        };
    }

    /// <summary>A backend that threw rather than returning a status.</summary>
    public static GenerationFailure FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new GenerationFailure(
            StatusCodes.Status502BadGateway,
            OpenAiError.Body(
                $"Backend threw: {exception.GetType().Name}: {exception.Message}",
                OpenAiError.Server,
                code: "backend_error"));
    }
}
