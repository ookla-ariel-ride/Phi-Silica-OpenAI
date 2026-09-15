using System.Globalization;
using Microsoft.AspNetCore.Http;
using NpuBridge.Backends;

namespace NpuBridge.Api;

/// <summary>
/// What a <see cref="GenerationScheduler.ScheduleAsync{TResult}"/> result means for a caller, once it
/// is known whether the caller's own client is already gone. Shared by both <c>/v1/chat/completions</c>
/// shapes and <c>/debug/generate</c> (chunk 8 fix round 1, Finding 4) so a full queue and a scheduler
/// shutdown are not spelled three different ways as <c>/v1/completions</c> (Task 3) becomes a fourth
/// caller — exactly the drift D81 exists to prevent one level up, and the two OpenAI shapes had already
/// grown apart on this by day one (Finding 2).
/// </summary>
internal enum SchedulerOutcome
{
    /// <summary>The operation ran to completion; the caller's <see cref="ScheduleResult{TResult}.Result"/> is real.</summary>
    Completed,

    /// <summary>
    /// The caller's own client is already gone — whether the job was dropped while still queued or ran
    /// and ended by throwing <see cref="OperationCanceledException"/> for that same reason — so there is
    /// nobody to report a failure to. The caller's own "client is gone" handling applies, exactly as it
    /// would for a generation that ran and then found the same thing.
    /// </summary>
    ClientGone,

    /// <summary>The queue was full. Report <see cref="GenerationFailure.QueueFull"/> with the scheduled result's own <c>RetryAfterSeconds</c>.</summary>
    QueueFull,

    /// <summary>
    /// The scheduler will never run this job — a post-shutdown enqueue, or one dropped while queued
    /// because shutdown began before the worker reached it — and nothing touched the model for it.
    /// Report <see cref="GenerationFailure.QueueShuttingDown"/>.
    /// </summary>
    QueueShuttingDown,

    /// <summary>
    /// The operation ran and ended by throwing <see cref="OperationCanceledException"/> for its own
    /// token instead of reporting a domain result — most plausibly a backend adapter letting the
    /// runtime's own cancellation escape rather than answering with a <c>Cancelled</c> status, the exact
    /// contract violation D82 exists to answer. Not the scheduler's business: report it exactly as an
    /// escaped exception always has, via <see cref="GenerationFailure.FromException"/>, so a client
    /// cannot tell "the queue is fine, the backend broke its contract" from "the bridge threw" by the
    /// shape of the two bodies.
    /// </summary>
    BackendThrewCancellation,
}

internal static class SchedulerAdmission
{
    /// <summary>
    /// Classifies a scheduled result that has already been awaited. <paramref name="clientAlreadyGone"/>
    /// is checked first and wins over every other reason, matching the convention every other failure
    /// path in these endpoints already follows (an aborted client is reported as silence, never as a
    /// body nobody will read). Every endpoint, including <c>/debug/generate</c>, passes its current
    /// request-abort state explicitly rather than relying on a hidden default.
    /// </summary>
    public static SchedulerOutcome Classify<TResult>(ScheduleResult<TResult> scheduled, bool clientAlreadyGone)
    {
        ArgumentNullException.ThrowIfNull(scheduled);

        if (scheduled.Kind == ScheduleResultKind.Completed)
        {
            return SchedulerOutcome.Completed;
        }

        if (clientAlreadyGone)
        {
            return SchedulerOutcome.ClientGone;
        }

        if (scheduled.Kind == ScheduleResultKind.Rejected)
        {
            return SchedulerOutcome.QueueFull;
        }

        // Kind == Cancelled from here. Ran is what tells apart a job the worker never got to run at all
        // (dropped while still queued, or a post-shutdown enqueue -- QueueShuttingDown, nothing touched
        // the model) from one that ran and ended by throwing for its own token (BackendThrewCancellation,
        // a real failure the queue had nothing to do with). Finding 1.
        return scheduled.Ran ? SchedulerOutcome.BackendThrewCancellation : SchedulerOutcome.QueueShuttingDown;
    }

    /// <summary>
    /// The failure to report for every <see cref="SchedulerOutcome"/> except <see cref="SchedulerOutcome.Completed"/>
    /// and <see cref="SchedulerOutcome.ClientGone"/> (neither of which has one to report: the first has a
    /// real result instead, the second has nobody to send it to). Throws for those two on purpose --
    /// callers branch on <see cref="Classify{TResult}"/>'s result before ever reaching this, so reaching
    /// it for either would itself be the bug.
    /// </summary>
    public static GenerationFailure FailureFor(
        SchedulerOutcome outcome,
        int retryAfterSeconds,
        GenerationHealth? health = null,
        double durationMs = 0) =>
        outcome switch
        {
            SchedulerOutcome.QueueFull => GenerationFailure.QueueFull(retryAfterSeconds),
            SchedulerOutcome.QueueShuttingDown => GenerationFailure.QueueShuttingDown(),
            SchedulerOutcome.BackendThrewCancellation => GenerationFailure.FromException(
                new OperationCanceledException(
                    "The backend's generation ended by throwing OperationCanceledException instead of reporting a Cancelled status (adapter contract violation, D82)."),
                health,
                durationMs),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Completed and ClientGone have no failure to report; the caller must branch before reaching this."),
        };

    /// <summary>
    /// Writes <c>Retry-After</c> for a queue-full rejection, and nothing at all for anything else.
    /// Silent on a response that has already started, which is both the only safe thing to do — the
    /// headers are read-only by then and assigning one throws <see cref="InvalidOperationException"/> —
    /// and the correct thing: a stream that has already sent a keep-alive tells the client the queue was
    /// full through the error event's own body instead. Shared rather than written at each of the three
    /// call sites, because the streaming shape was the only one that remembered the guard (Finding 2)
    /// and the three spelled the number three different ways.
    /// </summary>
    public static void ApplyRetryAfter(HttpResponse response, SchedulerOutcome outcome, int retryAfterSeconds)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (outcome != SchedulerOutcome.QueueFull || response.HasStarted)
        {
            return;
        }

        response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
    }
}
