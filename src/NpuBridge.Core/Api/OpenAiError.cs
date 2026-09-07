using Microsoft.AspNetCore.Http;

namespace NpuBridge.Api;

/// <summary>OpenAI's error envelope: <c>{"error":{"message","type","param","code"}}</c>.</summary>
public sealed record OpenAiErrorBody(OpenAiErrorDetail Error);

public sealed record OpenAiErrorDetail(string Message, string Type, string? Param, string? Code);

/// <summary>
/// Error types are the ones OpenAI actually emits: <c>invalid_request_error</c> (including 404s for
/// unknown models and endpoints), <c>rate_limit_error</c>, <c>server_error</c>. Status codes carry the
/// HTTP semantics; <c>code</c> carries the specific reason.
/// </summary>
public static class OpenAiError
{
    public const string InvalidRequest = "invalid_request_error";
    public const string RateLimit = "rate_limit_error";
    public const string Server = "server_error";

    /// <summary>
    /// The envelope on its own, without an HTTP status wrapped around it. The streaming path needs this:
    /// once the headers are committed an error has to travel inside the stream as a <c>data:</c> frame,
    /// and it has to be the same object the JSON path would have returned, not a lookalike.
    /// </summary>
    public static OpenAiErrorBody Body(string message, string type, string? code = null, string? param = null) =>
        new(new OpenAiErrorDetail(message, type, param, code));

    public static IResult Result(int statusCode, OpenAiErrorBody body) =>
        Results.Json(body, JsonDefaults.Options, statusCode: statusCode);

    public static IResult Result(int statusCode, string message, string type, string? code = null, string? param = null) =>
        Result(statusCode, Body(message, type, code, param));

    public static IResult BadRequest(string message, string? code = null, string? param = null) =>
        Result(StatusCodes.Status400BadRequest, message, InvalidRequest, code, param);

    /// <summary>404 with OpenAI's <c>invalid_request_error</c> type, as returned for unknown models and routes.</summary>
    public static IResult NotFoundResult(string message, string? code = "not_found", string? param = null) =>
        Result(StatusCodes.Status404NotFound, message, InvalidRequest, code, param);
}
