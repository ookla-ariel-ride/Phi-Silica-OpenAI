using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NpuBridge.Api;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;

namespace NpuBridge.Tests;

/// <summary>
/// The client-side cut: <c>max_tokens</c>, <c>max_completion_tokens</c> and <c>stop</c>. Neither
/// Windows runtime offers either feature, so the bridge watches the text and cuts it (D53).
///
/// Almost every case here is a <see cref="TheoryAttribute"/> over both response shapes, run through
/// one helper that reduces a JSON reply and an SSE stream to the same three facts — content, finish
/// reason, reported completion tokens. That is deliberate: the shapes decide the cut in different
/// places (whole text on one side, delta by delta on the other), and a test that only ever exercised
/// one of them would let them drift.
///
/// The case that matters most is <see cref="Streaming_never_leaks_a_stop_string_split_across_deltas"/>.
/// Streamed text cannot be recalled and a stop string can straddle two deltas, so the streaming path
/// holds a tail back; its mirror image,
/// <see cref="Streaming_flushes_the_held_tail_when_no_stop_string_forms"/>, is the case where holding
/// back too eagerly would silently swallow the end of an ordinary reply.
/// </summary>
public class OutputCutTests
{
    private const string Path = "/v1/chat/completions";

    /// <summary>22 characters in three deltas, so a cut can land on or inside a delta boundary.</summary>
    private static readonly string[] Reply = ["Hello, ", "world!", " Goodbye."];

    private const string ReplyText = "Hello, world! Goodbye.";

    // ---------------------------------------------------------------- the cap

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cap_smaller_than_the_reply_truncates_it_and_finishes_with_length(bool stream)
    {
        await using var host = await StartAsync(Reply);

        // The budget is cap * 4 characters, the same four characters per token that usage counts with,
        // so the cut lands inside the second delta rather than on its boundary.
        var completion = await CompleteAsync(host, stream, Body(stream, maxTokens: 2));

        Assert.Equal("Hello, w", completion.Content);
        Assert.Equal("length", completion.FinishReason);
        Assert.Equal(2, completion.CompletionTokens);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cap_larger_than_the_reply_changes_nothing_and_finishes_with_stop(bool stream)
    {
        await using var host = await StartAsync(Reply);

        var completion = await CompleteAsync(host, stream, Body(stream, maxTokens: 100));

        Assert.Equal(ReplyText, completion.Content);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(6, completion.CompletionTokens);
    }

    /// <summary>
    /// The consistency rule the cap exists to keep: <c>usage.completion_tokens</c> is
    /// <c>ceil(chars/4)</c> (D44), so a cap measured any other way — by counting progress callbacks,
    /// say — would let a reply report more completion tokens than the client allowed.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 5)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 5)]
    public async Task Completion_tokens_never_exceeds_the_cap(bool stream, int cap)
    {
        await using var host = await StartAsync(Reply);

        var completion = await CompleteAsync(host, stream, Body(stream, maxTokens: cap));

        Assert.True(
            completion.CompletionTokens <= cap,
            $"reported {completion.CompletionTokens} completion tokens for a cap of {cap}");
        Assert.Equal(cap * 4, completion.Content.Length);
        Assert.Equal(ReplyText[..(cap * 4)], completion.Content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_both_cap_fields_present_the_smaller_one_wins(bool stream)
    {
        await using var host = await StartAsync(Reply);

        var maxTokensSmaller = await CompleteAsync(host, stream, Body(stream, maxTokens: 1, maxCompletionTokens: 4));
        var maxCompletionSmaller = await CompleteAsync(host, stream, Body(stream, maxTokens: 4, maxCompletionTokens: 1));

        Assert.Equal("Hell", maxTokensSmaller.Content);
        Assert.Equal("Hell", maxCompletionSmaller.Content);
        Assert.Equal("length", maxTokensSmaller.FinishReason);
        Assert.Equal("length", maxCompletionSmaller.FinishReason);
    }

    [Theory]
    [InlineData(true, "max_tokens", 0)]
    [InlineData(true, "max_tokens", -1)]
    [InlineData(true, "max_completion_tokens", 0)]
    [InlineData(true, "max_completion_tokens", -7)]
    [InlineData(false, "max_tokens", 0)]
    [InlineData(false, "max_tokens", -1)]
    [InlineData(false, "max_completion_tokens", 0)]
    [InlineData(false, "max_completion_tokens", -7)]
    public async Task A_zero_or_negative_cap_is_a_400(bool stream, string field, int value)
    {
        await using var host = await StartAsync(Reply);

        var body = $$"""
            {"model":"fake","stream":{{(stream ? "true" : "false")}},"{{field}}":{{value}},
             "messages":[{"role":"user","content":"say hi"}]}
            """;
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await host.Client.PostAsync(Path, content);

        // Validation runs before a byte is written, so a streamed request fails the ordinary way too.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(field, error.GetProperty("param").GetString());
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
    }

    // --------------------------------------------------------------- stop strings

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stop_string_mid_reply_truncates_there_and_is_excluded_from_the_output(bool stream)
    {
        await using var host = await StartAsync(Reply);

        var completion = await CompleteAsync(host, stream, Body(stream, stop: "world"));

        Assert.Equal("Hello, ", completion.Content);
        Assert.DoesNotContain("world", completion.Content, StringComparison.Ordinal);
        Assert.Equal("stop", completion.FinishReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stop_string_that_never_appears_changes_nothing(bool stream)
    {
        await using var host = await StartAsync(Reply);

        var completion = await CompleteAsync(host, stream, Body(stream, stop: "ZZZ"));

        Assert.Equal(ReplyText, completion.Content);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(6, completion.CompletionTokens);
    }

    /// <summary>The earliest occurrence wins, not the first entry in the array.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_several_stop_strings_the_earliest_occurrence_wins(bool stream)
    {
        await using var host = await StartAsync(["alpha beta gamma delta"]);

        // "gamma" is listed first and occurs last; "beta" occurs first and must be the one that fires.
        var completion = await CompleteAsync(host, stream, Body(stream, stop: new[] { "gamma", "beta" }));

        Assert.Equal("alpha ", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
    }

    /// <summary>A stop string reached before the cap wins, and vice versa.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Whichever_limit_is_reached_first_decides_the_finish_reason(bool stream)
    {
        await using var host = await StartAsync(Reply);

        var stopFirst = await CompleteAsync(host, stream, Body(stream, maxTokens: 4, stop: "world"));
        var capFirst = await CompleteAsync(host, stream, Body(stream, maxTokens: 1, stop: "world"));

        Assert.Equal("Hello, ", stopFirst.Content);
        Assert.Equal("stop", stopFirst.FinishReason);
        Assert.Equal("Hell", capFirst.Content);
        Assert.Equal("length", capFirst.FinishReason);
    }

    /// <summary>
    /// The budget boundary falling inside a stop-string occurrence. <c>"abcd"</c> then <c>"EFGH"</c>
    /// with <c>stop: "dEFG"</c> and a four-character budget: the stop string starts at character three,
    /// one before the budget, and only completes four characters after it. A cap committed the moment
    /// the budget was reached would answer <c>"abcd"</c> / <c>length</c> on the streaming path and
    /// <c>"abc"</c> / <c>stop</c> on the JSON path — the two shapes disagreeing, and half a stop string
    /// on the wire. The cap waits for the same lookahead the holdback waits for.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stop_string_straddling_the_budget_boundary_wins_on_both_shapes(bool stream)
    {
        await using var host = await StartAsync(["abcd", "EFGH"]);

        var completion = await CompleteAsync(host, stream, Body(stream, maxTokens: 1, stop: "dEFG"));

        Assert.Equal("abc", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
    }

    /// <summary>
    /// A reply that lands exactly on the budget was not truncated, so it finishes <c>stop</c>. The cap
    /// fires when text is dropped, not when the budget is touched — and the streaming path cannot know
    /// which of those happened until it has looked past the budget, which is the same wait.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reply_landing_exactly_on_the_budget_finishes_with_stop(bool stream)
    {
        await using var host = await StartAsync(["abcd", "efgh"]);

        var completion = await CompleteAsync(host, stream, Body(stream, maxTokens: 2));

        Assert.Equal("abcdefgh", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(2, completion.CompletionTokens);
    }

    /// <summary>An empty stop string would match at position 0 of everything; it is dropped, not honoured.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_empty_stop_string_is_ignored_rather_than_matching_immediately(bool stream)
    {
        await using var host = await StartAsync(Reply);

        var completion = await CompleteAsync(host, stream, Body(stream, stop: new[] { "" }));

        Assert.Equal(ReplyText, completion.Content);
        Assert.Equal("stop", completion.FinishReason);
    }

    // ------------------------------------------------- the streaming-only hazards

    /// <summary>
    /// The test this task exists for. The fake is arranged so <c>END</c> arrives as <c>"EN"</c> then
    /// <c>"D"</c>: neither delta contains the stop string, and a path that emitted each delta as it
    /// arrived would have written <c>EN</c> to the client before discovering it was half of one. The
    /// assertion is on the raw wire text, because "the client never sees it" is a statement about the
    /// bytes, not about the reassembled content.
    /// </summary>
    [Fact]
    public async Task Streaming_never_leaks_a_stop_string_split_across_deltas()
    {
        await using var host = await StartAsync(["Hello ", "EN", "D and more"]);

        var response = await host.Client.PostAsJsonAsync(Path, Body(stream: true, stop: "END"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The chunk id is repeated on every frame and is Crockford base32, an alphabet that contains
        // both E and N -- about 1.5% of generated ids contain "EN" somewhere, which made this assertion
        // fail roughly one run in 65 for a reason that has nothing to do with the stop string. Strip the
        // ids: the claim is about the text the bridge wrote, not the identifier it happened to draw.
        var wire = System.Text.RegularExpressions.Regex.Replace(body, "chatcmpl-[0-9A-Za-z]+", "chatcmpl-");
        Assert.DoesNotContain("EN", wire, StringComparison.Ordinal);

        var completion = Reduce(body);
        Assert.Equal("Hello ", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
    }

    /// <summary>
    /// The other half of the holdback, and the more dangerous one to get wrong because it fails
    /// silently: a reply whose last characters could have begun a stop string but did not must arrive
    /// whole. Here the reply ends in <c>EN</c> with <c>END</c> as the stop string, so those two
    /// characters are held back until the generation ends and are only then flushed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Streaming_flushes_the_held_tail_when_no_stop_string_forms(bool stream)
    {
        await using var host = await StartAsync(["Hello ", "EN"]);

        var completion = await CompleteAsync(host, stream, Body(stream, stop: "END"));

        Assert.Equal("Hello EN", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(2, completion.CompletionTokens);
    }

    /// <summary>
    /// A reply shorter than the held tail itself: everything is withheld until the flush, so the role
    /// chunk goes out with no content chunk behind it and the whole reply arrives in one piece at the
    /// end. The degenerate case of the holdback, and the one an off-by-one loses entirely.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reply_shorter_than_the_holdback_still_arrives_whole(bool stream)
    {
        await using var host = await StartAsync(["ab"]);

        var completion = await CompleteAsync(host, stream, Body(stream, stop: "abcdefgh"));

        Assert.Equal("ab", completion.Content);
        Assert.Equal("stop", completion.FinishReason);
    }

    // ------------------------------------------------- cancellation and disposal

    /// <summary>
    /// A cut stops the model rather than letting it run to the end and throwing the rest away, and the
    /// context is still disposed exactly once — the cut cancels the generation's own token, so the
    /// cancel-drain-dispose ordering of D51 has to survive a cancellation that is nobody's disconnect.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cut_cancels_the_generation_and_still_disposes_the_context_once(bool stream)
    {
        var produced = new StrongBox<int>(0);
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => Counted(produced, 400),
            TokenDelay = TimeSpan.FromMilliseconds(5),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var completion = await CompleteAsync(host, stream, Body(stream, maxTokens: 2));

        Assert.Equal("0123", completion.Content[..4]);
        Assert.Equal(8, completion.Content.Length);
        Assert.Equal("length", completion.FinishReason);

        // Left to itself the responder yields 400 tokens over about two seconds. Some get past the cut
        // while the cancellation propagates, and how many is thread-pool scheduling rather than
        // behaviour, so the bound is loose on purpose: four hundred means it never propagated at all,
        // which is the only thing this assertion is entitled to claim.
        var count = Volatile.Read(ref produced.Value);
        Assert.True(count < 200, $"the generation produced {count} of 400 tokens; it was not cancelled");

        await WaitUntilAsync(() => fake.ActiveContexts == 0);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
    }

    /// <summary>
    /// Both shapes, same generation, same limits: identical content, finish reason and usage. The two
    /// paths cut in different places — one over the whole text, one delta by delta — and this is the
    /// assertion that they agree about the result.
    /// </summary>
    [Fact]
    public async Task Both_response_shapes_cut_a_generation_identically()
    {
        await using var host = await StartAsync(Reply);

        foreach (var body in new (int? Cap, object? Stop)[]
                 {
                     (2, null), (3, null), (100, null), (null, "world"), (null, "ZZZ"), (2, "world"), (100, "o, w"),
                     // The budget boundary at character 12 lands inside "d! G", which starts at 11.
                     (3, "d! G"),
                 })
        {
            var streamed = await CompleteAsync(host, stream: true, Body(true, maxTokens: body.Cap, stop: body.Stop));
            var json = await CompleteAsync(host, stream: false, Body(false, maxTokens: body.Cap, stop: body.Stop));

            Assert.Equal(json.Content, streamed.Content);
            Assert.Equal(json.FinishReason, streamed.FinishReason);
            Assert.Equal(json.CompletionTokens, streamed.CompletionTokens);
        }
    }

    /// <summary>
    /// The cap can be committed by the flush rather than by a delta: it is deliberately deferred until
    /// the text runs <c>Holdback</c> past the budget, so a reply that ends inside that window is only cut
    /// at the very end. Reading the finish reason before the flush labelled such a request <c>stop</c> on
    /// the stream while the JSON path — which reads it after its own flush — called the same generation
    /// <c>length</c>, and a client that resumes on <c>length</c> silently stopped instead.
    /// </summary>
    [Fact]
    public async Task A_cap_committed_by_the_flush_is_length_on_both_shapes()
    {
        await using var host = await StartAsync(["123456789"]);

        var streamed = await CompleteAsync(host, stream: true, Body(true, maxTokens: 2, stop: "```"));
        var json = await CompleteAsync(host, stream: false, Body(false, maxTokens: 2, stop: "```"));

        Assert.Equal("12345678", json.Content);
        Assert.Equal("length", json.FinishReason);
        Assert.Equal(json.Content, streamed.Content);
        Assert.Equal(json.FinishReason, streamed.FinishReason);
    }

    /// <summary>
    /// A cut is a licence to reinterpret <see cref="GenerationStatus.Cancelled"/> — the status this
    /// handler's own cancellation produces — and nothing else. The whole-text cut on the JSON path can
    /// report a cap the watcher never cancelled for, because with a long stop string it is still waiting
    /// for lookahead when the generation ends; gating the entire failure mapping on "a cut fired" then
    /// turned a backend <c>Error</c> into HTTP 200 with truncated text and <c>finish_reason: "length"</c>.
    /// That is a regression against main, which always answered 502, and it is the case that matters on
    /// this hardware: D55 established that a real prompt overflow surfaces as exactly this generic Error.
    /// </summary>
    [Fact]
    public async Task A_backend_error_is_not_suppressed_by_a_cut_that_did_not_cancel_it()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["abcd", "efgh"],
            FailAfterTokens = 2,
            FailureStatus = GenerationStatus.Error,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        // Holdback 9 against a budget of 4: the eight generated characters never run far enough past the
        // budget for the watcher to commit, so no cancellation happens and the cut is the flush's alone.
        var response = await host.Client.PostAsJsonAsync(Path, Body(false, maxTokens: 1, stop: "ZZZZZZZZZZ"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("server_error", error.GetProperty("type").GetString());
    }

    /// <summary>
    /// The other half of the same discrimination, and a path no endpoint test covered on either shape:
    /// a <see cref="GenerationStatus.Cancelled"/> the backend reports on its own, with the client still
    /// there and no limit set. It is a 502, not a truncated success — the cut is what distinguishes it
    /// from the cancellation this handler asks for, and that distinction is load-bearing now.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_backend_cancelled_with_a_live_client_is_a_failure(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => Reply,
            FailAfterTokens = 1,
            FailureStatus = GenerationStatus.Cancelled,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Body(stream));
        var body = await response.Content.ReadAsStringAsync();

        if (stream)
        {
            // Headers are already committed, so the failure arrives as an error event and then [DONE].
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("\"error\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"finish_reason\":\"stop\"", body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }

        await WaitUntilAsync(() => fake.ActiveContexts == 0);
        Assert.Equal(fake.ContextsCreated, fake.ContextsDisposed);
    }

    /// <summary>
    /// "The cut caused this cancellation" has to be a fact the handler recorded when it cancelled, not
    /// something inferred from the cutter afterwards. The two shapes inferred it from different inputs:
    /// the JSON path from the whole-text cut, which includes a cap committed by the flush, the stream
    /// from <c>IsCut</c> before its flush. So a backend that reported <c>Cancelled</c> on its own, with
    /// a reply ending inside the lookahead window, was HTTP 200 <c>length</c> on one shape and a 502
    /// on the other. Neither handler cancelled here — eight characters is four past the budget, less
    /// than the holdback of nine — so this is a failure on both.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_backend_cancelled_the_handler_did_not_ask_for_is_a_failure_even_when_the_flush_commits_a_cap(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["abcd", "efgh"],
            FailAfterTokens = 2,
            FailureStatus = GenerationStatus.Cancelled,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, Body(stream, maxTokens: 1, stop: "ZZZZZZZZZZ"));
        var body = await response.Content.ReadAsStringAsync();

        if (stream)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("\"error\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"finish_reason\":\"length\"", body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Contains("\"error\"", body, StringComparison.Ordinal);
        }

        await WaitUntilAsync(() => fake.ActiveContexts == 0);
    }

    // ------------------------------------------------------------- the cutter itself

    [Fact]
    public void The_holdback_is_the_longest_stop_string_minus_one()
    {
        Assert.Equal(0, Limits(null).Holdback);
        Assert.Equal(0, Limits(null, "x").Holdback);
        Assert.Equal(4, Limits(null, "END", "abcde").Holdback);
    }

    [Fact]
    public void The_cutter_withholds_a_partial_stop_string_and_releases_it_on_the_flush()
    {
        var cutter = new OutputCutter(Limits(null, "END"));

        Assert.Equal("Hell", cutter.Accept("Hello "));
        Assert.Equal("o ", cutter.Accept("EN"));
        Assert.False(cutter.IsCut);

        // The two characters that could have started "END" are still held; only the flush frees them.
        Assert.Equal("EN", cutter.Flush());
        Assert.Equal(8, cutter.ContentLength);
    }

    [Fact]
    public void The_cutter_cuts_at_a_stop_string_that_completes_in_a_later_delta()
    {
        var cutter = new OutputCutter(Limits(null, "END"));

        Assert.Equal("Hell", cutter.Accept("Hello "));
        Assert.Equal("o ", cutter.Accept("EN"));
        Assert.Equal(string.Empty, cutter.Accept("D and more"));

        Assert.True(cutter.IsCut);
        Assert.Equal("stop", cutter.FinishReason);
        Assert.Equal(6, cutter.ContentLength);

        // Nothing after a cut, ever.
        Assert.Equal(string.Empty, cutter.Accept("more still"));
        Assert.Equal(string.Empty, cutter.Flush());
        Assert.Equal(6, cutter.ContentLength);
    }

    /// <summary>
    /// The incremental cut and the whole-text cut are the same function. Asserted over every possible
    /// split of the same text, because the streaming path's answer must not depend on how the backend
    /// happened to batch its callbacks — a real one batches several tokens per delta and never twice
    /// the same way.
    /// </summary>
    [Theory]
    [InlineData(null, "END")]
    [InlineData(null, "lo, ")]
    [InlineData(null, "ZZZ")]
    [InlineData(3, null)]
    [InlineData(3, "world")]
    [InlineData(1000, "world")]
    // The budget boundary at character 12 lands inside "d! T", which starts at 11: the cap must wait
    // for the stop string to prove itself rather than committing the moment the budget is reached.
    [InlineData(3, "d! T")]
    [InlineData(7, "d! T")]
    public void Every_split_of_the_same_text_cuts_to_the_same_place(int? cap, string? stop)
    {
        const string Text = "Hello, world! The END is nigh.";
        var limits = Limits(cap, stop);
        var whole = limits.Cut(Text);

        for (var split = 0; split <= Text.Length; split++)
        {
            var cutter = new OutputCutter(limits);
            var emitted = cutter.Accept(Text[..split]) + cutter.Accept(Text[split..]) + cutter.Flush();

            Assert.Equal(whole.Text, emitted);
            Assert.Equal(whole.FinishReason, cutter.FinishReason);
            Assert.Equal(whole.Text.Length, cutter.ContentLength);
        }
    }

    /// <summary>
    /// A shorter stop string sitting inside a longer one. <c>b</c> matches at 1 while <c>abcd</c> is
    /// still forming at 0, and committing the shorter match the moment it appeared made the reply depend
    /// on where the runtime happened to split its callbacks: <c>"ab"</c> + <c>"cd"</c> cut at 1 and
    /// emitted <c>a</c>, while the identical text in one delta cut at 0 and emitted nothing. A match is
    /// only settled once no longer stop string starting earlier can still form.
    /// </summary>
    [Fact]
    public void Every_split_agrees_when_a_shorter_stop_string_sits_inside_a_longer_one()
    {
        const string Text = "abcd";
        var limits = Limits(null, "abcd", "b");
        var whole = limits.Cut(Text);

        Assert.Equal(string.Empty, whole.Text);
        Assert.Equal("stop", whole.FinishReason);

        for (var split = 0; split <= Text.Length; split++)
        {
            var cutter = new OutputCutter(limits);
            var emitted = cutter.Accept(Text[..split]) + cutter.Accept(Text[split..]) + cutter.Flush();

            Assert.Equal(whole.Text, emitted);
            Assert.Equal(whole.FinishReason, cutter.FinishReason);
        }
    }

    /// <summary>
    /// Neither the holdback nor the character budget respects character boundaries, so both can slice
    /// between the halves of a surrogate pair. That does not merely delay the character: each slice is
    /// serialized as its own JSON string and <c>System.Text.Json</c> writes a lone surrogate as U+FFFD,
    /// so an emoji split across two SSE frames reaches the client as two replacement characters no
    /// client can reassemble. Asserted piece by piece, because the concatenation hides the damage.
    /// </summary>
    [Fact]
    public void No_piece_of_a_release_ends_inside_a_surrogate_pair()
    {
        // Holdback 3 puts the release boundary on the low half of the emoji.
        var cutter = new OutputCutter(Limits(null, "ZZZZ"));

        var first = cutter.Accept("ab\U0001F600cd");
        var tail = cutter.Flush();

        Assert.Equal("ab\U0001F600cd", first + tail);
        foreach (var piece in new[] { first, tail })
        {
            Assert.False(piece.Length > 0 && char.IsHighSurrogate(piece[^1]), $"'{piece}' ends on a high surrogate");
            Assert.False(piece.Length > 0 && char.IsLowSurrogate(piece[0]), $"'{piece}' starts on a low surrogate");
        }
    }

    /// <summary>
    /// The ordinary streaming case — no stop strings, so nothing is held back — has the same hole at the
    /// end of a delta rather than inside it: a runtime that splits a pair across two callbacks delivers
    /// a delta ending on the high half, and with a holdback of zero "everything pending" is the release.
    /// The high half must wait for the next delta regardless, or the pair is destroyed exactly as above.
    /// The existing guard only ever stepped back from an index strictly inside the text, and setting any
    /// unrelated stop string hid the problem because the holdback then happened to catch it.
    /// </summary>
    [Fact]
    public void A_delta_ending_on_a_high_surrogate_holds_it_back_even_with_no_stop_strings()
    {
        var cutter = new OutputCutter(OutputLimits.None);

        var first = cutter.Accept("hi\uD83D");
        var second = cutter.Accept("\uDE00 there");
        var tail = cutter.Flush();

        Assert.Equal("hi", first);
        Assert.Equal("\U0001F600 there", second);
        Assert.Equal(string.Empty, tail);
        Assert.Equal(10, cutter.ContentLength);
    }

    /// <summary>
    /// A high surrogate that is genuinely the last character the model produced is not the bridge's to
    /// suppress: the flush releases it, so a held half is delayed by one delta and never lost.
    /// </summary>
    [Fact]
    public void A_held_high_surrogate_is_released_by_the_flush_when_nothing_follows()
    {
        var cutter = new OutputCutter(OutputLimits.None);

        var first = cutter.Accept("hi\uD83D");
        var tail = cutter.Flush();

        Assert.Equal("hi", first);
        Assert.Equal("\uD83D", tail);
    }

    /// <summary>
    /// The same for the cap, where stepping back is also what keeps ceil(chars/4) under the budget: the
    /// fourth character of the budget is the low half of the emoji, so the cut takes three.
    /// </summary>
    [Fact]
    public void A_cap_landing_inside_a_surrogate_pair_cuts_before_it()
    {
        var cut = Limits(1).Cut("abc\U0001F600d");

        Assert.Equal("abc", cut.Text);
        Assert.Equal("length", cut.FinishReason);
    }

    [Fact]
    public void An_absurd_cap_saturates_rather_than_overflowing_into_a_negative_budget()
    {
        var limits = Limits(int.MaxValue);

        Assert.Equal(int.MaxValue, limits.MaxChars);
        Assert.Equal(new CutResult("anything at all", null), limits.Cut("anything at all"));
    }

    // ------------------------------------------------------------------- helpers

    private static OutputLimits Limits(int? cap, params string?[] stop)
    {
        var stops = stop.Where(s => s is not null).Select(s => s!).ToArray();
        var json = JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content = "hi" } },
            max_tokens = cap,
            stop = stops.Length == 0 ? null : stops,
        });

        return OutputLimits.From(JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonDefaults.Options)!);
    }

    private static IEnumerable<string> Counted(StrongBox<int> counter, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Interlocked.Increment(ref counter.Value);
            yield return "0123";
        }
    }

    private static Task<BridgeTestHost> StartAsync(IReadOnlyList<string> tokens) =>
        BridgeTestHost.StartAsync(new FakeBackend(new FakeBackendOptions { Responder = _ => tokens }));

    private static object Body(bool stream, int? maxTokens = null, int? maxCompletionTokens = null, object? stop = null) => new
    {
        model = "fake",
        stream,
        // Usage only rides along on a stream when it is asked for, and every assertion here wants it.
        stream_options = stream ? new { include_usage = true } : null,
        max_tokens = maxTokens,
        max_completion_tokens = maxCompletionTokens,
        stop,
        messages = new[] { new { role = "user", content = "say hi" } },
    };

    /// <summary>Content, finish reason and reported completion tokens, whichever shape carried them.</summary>
    private sealed record Completion(string Content, string? FinishReason, int CompletionTokens);

    private static async Task<Completion> CompleteAsync(BridgeTestHost host, bool stream, object body)
    {
        var response = await host.Client.PostAsJsonAsync(Path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        if (stream)
        {
            return Reduce(text);
        }

        var root = JsonDocument.Parse(text).RootElement;
        var choice = root.GetProperty("choices")[0];
        return new Completion(
            choice.GetProperty("message").GetProperty("content").GetString()!,
            choice.GetProperty("finish_reason").GetString(),
            root.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
    }

    /// <summary>Reassembles an SSE body into the same three facts the JSON reply states outright.</summary>
    private static Completion Reduce(string body)
    {
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        var content = new System.Text.StringBuilder();
        string? finishReason = null;
        var completionTokens = 0;

        foreach (var line in body.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)))
        {
            var payload = line["data: ".Length..];
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            var chunk = JsonDocument.Parse(payload).RootElement;
            if (chunk.TryGetProperty("usage", out var usage))
            {
                completionTokens = usage.GetProperty("completion_tokens").GetInt32();
            }

            if (chunk.GetProperty("choices").GetArrayLength() == 0)
            {
                continue;
            }

            var choice = chunk.GetProperty("choices")[0];
            if (choice.GetProperty("delta").TryGetProperty("content", out var delta) && delta.ValueKind == JsonValueKind.String)
            {
                content.Append(delta.GetString());
            }

            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                finishReason = finish.GetString();
            }
        }

        return new Completion(content.ToString(), finishReason, completionTokens);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? description = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {description}");
            await Task.Delay(10);
        }
    }
}
