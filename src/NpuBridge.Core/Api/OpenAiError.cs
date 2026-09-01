using Microsoft.AspNetCore.Http;

namespace NpuBridge.Api;

/// <summary>OpenAI's error envelope: <c>{"error":{"message","type","param","code"}}</c>.</summary>
public sealed record OpenAiErrorBody(OpenAiErrorDetail Error);

public sealed record OpenAiErrorDetail(string Message, string Type, string? Param, string? Code);

public static class OpenAiError
{
    public const string InvalidRequest = "invalid_request_error";
    public const string NotFound = "not_found_error";
    public const string RateLimit = "rate_limit_error";
    public const string Server = "server_error";

    public static IResult Result(int statusCode, string message, string type, string? code = null, string? param = null) =>
        Results.Json(new OpenAiErrorBody(new OpenAiErrorDetail(message, type, param, code)), JsonDefaults.Options, statusCode: statusCode);

    public static IResult BadRequest(string message, string? code = null, string? param = null) =>
        Result(StatusCodes.Status400BadRequest, message, InvalidRequest, code, param);

    public static IResult NotFoundResult(string message, string? code = "not_found", string? param = null) =>
        Result(StatusCodes.Status404NotFound, message, NotFound, code, param);
}
