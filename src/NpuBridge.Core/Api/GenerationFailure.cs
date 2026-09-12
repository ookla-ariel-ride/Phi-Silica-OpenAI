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

    /// <summary>
    /// The generation queue (chunk 8, <see cref="GenerationScheduler"/>) was already full when this
    /// request tried to enqueue. <paramref name="retryAfterSeconds"/> is the scheduler's own estimate
    /// (queue depth × rolling average generation time, floored at 1); the caller still has to copy it
    /// onto the <c>Retry-After</c> header itself, since that is an HTTP header rather than part of the
    /// OpenAI error envelope this type owns.
    /// </summary>
    public static GenerationFailure QueueFull(int retryAfterSeconds) =>
        new(StatusCodes.Status429TooManyRequests,
            OpenAiError.Body(
                $"The generation queue is full. Retry after {retryAfterSeconds} second(s).",
                OpenAiError.RateLimit,
                code: "queue_full"));

    /// <summary>
    /// The scheduler will never run this job: either the enqueue itself landed after
    /// <see cref="GenerationScheduler.StopAsync"/> began, or the job was still queued when shutdown
    /// started and was dropped instead of run. Either way nothing is coming back to honour a
    /// <c>Retry-After</c>, so this is 503 rather than <see cref="QueueFull"/>'s 429 (task-2-brief.md,
    /// integration decision 4).
    /// </summary>
    public static GenerationFailure QueueShuttingDown() =>
        new(StatusCodes.Status503ServiceUnavailable,
            OpenAiError.Body(
                "The generation queue is shutting down and will not run this request.",
                OpenAiError.Server,
                code: "queue_shutting_down"));
}

/// <summary>
/// What a finished generation means for the client, decided once for both response shapes: an error, a
/// filtered reply, or content. <see cref="GenerationFailure.FromStatus"/> maps a status to a body;
/// this decides whether that mapping applies at all, which is the part the two shapes used to answer
/// differently (D56, D62).
///
/// Three rules, in this order:
/// <list type="number">
///   <item>Filtering outranks everything. It is the one status that means "do not hand this text on",
///   and a cut is not a licence to; it is reported as a success with
///   <c>finish_reason: "content_filter"</c> rather than as an error, because the model answered and the
///   answer was withheld.</item>
///   <item>A <see cref="GenerationStatus.Cancelled"/> the handler itself asked for is the client-side
///   cut, not a failure. Only <c>Cancelled</c>, and only when the handler cancelled: "a cut fired" is
///   not a licence to discard every other status. Gating the whole mapping on the cut suppressed every
///   failure — an <c>Error</c> that should be a 502 became HTTP 200 with truncated text and
///   <c>finish_reason: "length"</c>, and on this hardware that is not hypothetical, since D55
///   established that a real prompt overflow surfaces as exactly that generic <c>Error</c>.</item>
///   <item>Anything else <see cref="GenerationFailure.FromStatus"/> calls a failure is one.</item>
/// </list>
///
/// The cut's own verdict is deliberately not an input to the classification, only to the labels below:
/// <paramref name="CancelledByCut"/> is the fact recorded beside the <c>CancelAsync</c>, not the
/// cutter's state, because a cut established later by <c>Flush()</c> cannot have caused a cancellation
/// — nothing cancelled for it — and inferring it from a cutter made the two shapes answer the same
/// backend status differently (D62).
/// </summary>
/// <param name="Result">The generation as the backend reported it.</param>
/// <param name="Failure">Non-null when the client is told about an error instead of a completion.</param>
/// <param name="Filtered">True when the runtime withheld the answer.</param>
internal sealed record GenerationOutcome(GenerationResult Result, GenerationFailure? Failure, bool Filtered)
{
    /// <param name="cancelledByCut">
    /// True when this handler cancelled the generation because the cut fired. Recorded at the cancel
    /// rather than inferred afterwards — see the type's own remarks.
    /// </param>
    public static GenerationOutcome Classify(GenerationResult result, bool cancelledByCut)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status is GenerationStatus.ContentFiltered or GenerationStatus.BlockedByPolicy)
        {
            return new GenerationOutcome(result, Failure: null, Filtered: true);
        }

        if (cancelledByCut && result.Status is GenerationStatus.Cancelled)
        {
            return new GenerationOutcome(result, Failure: null, Filtered: false);
        }

        return new GenerationOutcome(result, GenerationFailure.FromStatus(result), Filtered: false);
    }

    /// <summary>
    /// The reply's <c>finish_reason</c>. Takes the cut's verdict as an argument rather than reading a
    /// cutter, because when that verdict is legible differs by shape: the streaming path must read it
    /// only after <c>Flush()</c>, which can be the call that commits the cap (D57), while the
    /// non-streaming path cuts the whole text in one go and has it immediately.
    /// </summary>
    /// <param name="cutFinishReason"><c>length</c>, <c>stop</c>, or null when no limit fired.</param>
    public string FinishReason(string? cutFinishReason) =>
        Filtered ? "content_filter" : cutFinishReason ?? "stop";

    /// <summary>
    /// Whether the context may go back into the cache: only after a generation that ended
    /// <see cref="GenerationStatus.Complete"/>, and only when the client got the whole reply. After a
    /// cut the context holds text the client never saw, so the transcript it would be stored under is
    /// not the one the client will send back; it is disposed like any other context that cannot be
    /// trusted (D11, D72).
    /// </summary>
    public bool KeepsContext(string? cutFinishReason) =>
        Result.Status == GenerationStatus.Complete && cutFinishReason is null;
}
