using NpuBridge.Api;
using NpuBridge.Backends;

namespace NpuBridge.Tests;

/// <summary>
/// The classifier both response shapes now run instead of each deciding for itself. Its rules were
/// written out twice before, and D56 and D57 each record a drift between exactly those two copies, so
/// these tests state the rules once rather than through two endpoints: every status, with and without
/// the handler's own cancel, and the two labels the caller derives from the cut's post-flush verdict.
///
/// The endpoint tests still exercise the same conditions end to end — this is not a replacement for
/// them. It is the guard that says which answer is correct when they disagree.
/// </summary>
public class GenerationOutcomeTests
{
    private static GenerationResult Result(GenerationStatus status, string text = "hello") =>
        new(text, status, "detail");

    /// <summary>
    /// The statuses a client is told about as a success. Complete is the ordinary reply; the two
    /// filtered ones are a reply the runtime withheld, which is a finish reason and not an error.
    /// </summary>
    [Theory]
    [InlineData(GenerationStatus.Complete, false)]
    [InlineData(GenerationStatus.ContentFiltered, true)]
    [InlineData(GenerationStatus.BlockedByPolicy, true)]
    public void A_status_the_client_is_told_about_as_a_success_carries_no_failure(GenerationStatus status, bool filtered)
    {
        var outcome = GenerationOutcome.Classify(Result(status), cancelledByCut: false);

        Assert.Null(outcome.Failure);
        Assert.Equal(filtered, outcome.Filtered);
    }

    /// <summary>
    /// Filtering outranks the cut. A generation the handler cancelled that still came back filtered is
    /// filtered: a cut is not a licence to hand on text the runtime withheld.
    /// </summary>
    [Theory]
    [InlineData(GenerationStatus.ContentFiltered)]
    [InlineData(GenerationStatus.BlockedByPolicy)]
    public void Filtering_outranks_the_cut(GenerationStatus status)
    {
        var outcome = GenerationOutcome.Classify(Result(status), cancelledByCut: true);

        Assert.Null(outcome.Failure);
        Assert.True(outcome.Filtered);
        Assert.Equal("content_filter", outcome.FinishReason(cutFinishReason: "length"));
    }

    /// <summary>
    /// A Cancelled the handler asked for is the client-side cut, not a failure — and a Cancelled it did
    /// not ask for is a 502 on both shapes. This is the distinction D62 records: inferring it from a
    /// cutter's state made the two shapes answer the same backend status differently.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_cancelled_status_is_a_failure_only_when_the_handler_did_not_ask_for_it(bool cancelledByCut, bool isFailure)
    {
        var outcome = GenerationOutcome.Classify(Result(GenerationStatus.Cancelled), cancelledByCut);

        Assert.Equal(isFailure, outcome.Failure is not null);
        Assert.False(outcome.Filtered);
    }

    /// <summary>
    /// "A cut fired" is not a licence to discard every other status. An Error after the cut is still an
    /// Error: D55 established that a real prompt overflow on this hardware surfaces as exactly that
    /// generic Error, and suppressing it hands the client HTTP 200 with a truncated reply instead.
    /// </summary>
    [Theory]
    [InlineData(GenerationStatus.Error)]
    [InlineData(GenerationStatus.PromptLargerThanContext)]
    public void A_failure_status_survives_the_cut(GenerationStatus status)
    {
        var cut = GenerationOutcome.Classify(Result(status), cancelledByCut: true);
        var uncut = GenerationOutcome.Classify(Result(status), cancelledByCut: false);

        Assert.NotNull(cut.Failure);
        Assert.NotNull(uncut.Failure);
        Assert.Equal(uncut.Failure.StatusCode, cut.Failure.StatusCode);
        Assert.Equal(uncut.Failure.Body, cut.Failure.Body);
    }

    /// <summary>The failure body is the one <see cref="GenerationFailure.FromStatus"/> already produced; the classifier only decides whether it applies.</summary>
    [Fact]
    public void The_failure_it_reports_is_the_one_the_status_maps_to()
    {
        var result = Result(GenerationStatus.PromptLargerThanContext);

        var outcome = GenerationOutcome.Classify(result, cancelledByCut: false);

        Assert.Equal(GenerationFailure.FromStatus(result), outcome.Failure);
    }

    /// <summary>
    /// The finish reason is the caller's cut verdict unless the reply was filtered, and "stop" when no
    /// limit fired. The verdict arrives as an argument because the streaming path may only read it after
    /// <c>Flush()</c> (D57) while the non-streaming path has it immediately.
    /// </summary>
    [Theory]
    [InlineData(null, "stop")]
    [InlineData("length", "length")]
    [InlineData("stop", "stop")]
    public void The_finish_reason_is_the_cuts_verdict_or_stop(string? cutFinishReason, string expected)
    {
        var outcome = GenerationOutcome.Classify(Result(GenerationStatus.Complete), cancelledByCut: false);

        Assert.Equal(expected, outcome.FinishReason(cutFinishReason));
    }

    /// <summary>
    /// A context goes back into the cache only after a Complete generation the client saw whole. A cut
    /// leaves the context holding text the client never received, so the transcript it would be keyed
    /// under is not the one the client will send back (D11, D72).
    /// </summary>
    [Theory]
    [InlineData(GenerationStatus.Complete, null, true)]
    [InlineData(GenerationStatus.Complete, "length", false)]
    [InlineData(GenerationStatus.Complete, "stop", false)]
    [InlineData(GenerationStatus.Cancelled, null, false)]
    [InlineData(GenerationStatus.ContentFiltered, null, false)]
    [InlineData(GenerationStatus.Error, null, false)]
    public void A_context_is_kept_only_after_an_uncut_complete_generation(
        GenerationStatus status, string? cutFinishReason, bool keeps)
    {
        // cancelledByCut: true for the two that could only arise from a cut, false otherwise -- the
        // caching rule is deliberately independent of it, and this pins that.
        var outcome = GenerationOutcome.Classify(Result(status), cancelledByCut: cutFinishReason is not null);

        Assert.Equal(keeps, outcome.KeepsContext(cutFinishReason));
    }

    [Fact]
    public void Classify_rejects_a_null_result() =>
        Assert.Throws<ArgumentNullException>(() => GenerationOutcome.Classify(null!, cancelledByCut: false));
}
