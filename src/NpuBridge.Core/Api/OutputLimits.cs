namespace NpuBridge.Api;

/// <summary>
/// The client-side cut: <c>max_tokens</c>/<c>max_completion_tokens</c> and <c>stop</c>, reduced to the
/// two things the pipeline can actually act on — a character budget and a set of stop strings.
///
/// Neither Windows runtime offers a token cap or stop sequences (Phi Silica's
/// <c>LanguageModelOptions</c> carries sampling knobs and nothing else; Aion has no options object at
/// all), so the bridge watches the text as it arrives and cuts it itself. That makes both features
/// best effort in one specific sense: the model is not steered by them, it is interrupted by them, so
/// the tokens up to the cut are generated either way.
///
/// The cap is a **character** budget rather than a token count because that is the only measure this
/// process has. <c>usage.completion_tokens</c> is <c>ceil(chars/4)</c> (D44) — the progress callback
/// batches several tokens per call, so counting callbacks is not a token count — and a cap measured
/// any other way would let a reply report more completion tokens than the client asked for. Four
/// characters per token, so the budget is exactly <c>cap * 4</c> characters: cut there and
/// <c>ceil(chars/4)</c> lands on the cap, never above it.
/// </summary>
internal sealed class OutputLimits
{
    /// <summary>No cap and no stop strings: <see cref="Cut"/> and <see cref="OutputCutter"/> pass text through.</summary>
    public static readonly OutputLimits None = new(null, []);

    /// <summary>Characters per token in the <c>usage</c> estimate. The cap is converted with the same number.</summary>
    private const int CharsPerToken = 4;

    private OutputLimits(int? maxChars, IReadOnlyList<string> stop)
    {
        MaxChars = maxChars;
        Stop = stop;

        var longest = 0;
        for (var i = 0; i < stop.Count; i++)
        {
            longest = Math.Max(longest, stop[i].Length);
        }

        // A stop string of length N can straddle a delta boundary, so the last N-1 characters emitted
        // so far are never safe to hand on: they may turn out to be its first half. Zero when there are
        // no stop strings, which is what makes the no-limits case a pass-through.
        Holdback = Math.Max(0, longest - 1);
    }

    /// <summary>Character budget for the completion, or null when the request set no cap.</summary>
    public int? MaxChars { get; }

    /// <summary>Stop strings, already normalised from a bare string or an array. Never contains an empty entry.</summary>
    public IReadOnlyList<string> Stop { get; }

    /// <summary>Characters a streaming caller must hold back: the longest stop string's length minus one.</summary>
    public int Holdback { get; }

    /// <summary>True when nothing was requested, so no text can ever be cut.</summary>
    public bool IsEmpty => MaxChars is null && Stop.Count == 0;

    /// <summary>
    /// Reads the two cap fields and <c>stop</c> off a validated request. When both caps are present the
    /// smaller wins, which is what OpenAI's own deprecation of <c>max_tokens</c> in favour of
    /// <c>max_completion_tokens</c> leaves ambiguous and a client sending both cannot be surprised by.
    /// Both are known positive here: a zero or negative cap is rejected in validation.
    ///
    /// An empty stop string is dropped rather than honoured: it matches at position 0 of everything, so
    /// honouring it would answer every such request with an empty completion. It is not an error
    /// either — a client that sends one gets the reply it would have got without it.
    /// </summary>
    public static OutputLimits From(ChatCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        int? cap = (request.MaxTokens, request.MaxCompletionTokens) switch
        {
            ({ } a, { } b) => Math.Min(a, b),
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };

        // cap * 4 overflows int for a cap past ~536 million. Saturate rather than wrap: an absurd cap
        // is indistinguishable from no cap at all, and a negative budget would cut everything.
        var maxChars = cap is { } tokens ? (int)Math.Min((long)tokens * CharsPerToken, int.MaxValue) : (int?)null;

        var stop = request.Stop is null
            ? []
            : request.Stop.Where(s => !string.IsNullOrEmpty(s)).ToArray();

        return maxChars is null && stop.Length == 0 ? None : new OutputLimits(maxChars, stop);
    }

    /// <summary>
    /// The whole-text form of the cut, for the non-streaming path. Runs the same
    /// <see cref="OutputCutter"/> the streaming path runs, on one delta containing everything, so the
    /// two shapes cannot decide differently about the same generated text.
    /// </summary>
    public CutResult Cut(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsEmpty || text.Length == 0)
        {
            return new CutResult(text, null);
        }

        var cutter = new OutputCutter(this);
        var head = cutter.Accept(text);
        var tail = cutter.Flush();
        return new CutResult(head + tail, cutter.FinishReason);
    }
}

/// <summary>Text after the cut, and the finish reason it forced — null when nothing was cut.</summary>
/// <param name="Text">The completion the client is told about.</param>
/// <param name="FinishReason"><c>length</c>, <c>stop</c>, or null when the limits did not fire.</param>
internal readonly record struct CutResult(string Text, string? FinishReason);

/// <summary>
/// Applies <see cref="OutputLimits"/> to a stream of deltas, deciding at each one how much of the text
/// so far is safe to hand to the client.
///
/// The streaming path has a problem the non-streaming path does not: text already written cannot be
/// recalled, and a stop string can straddle two deltas — <c>"EN"</c> then <c>"D"</c> is a hit on
/// <c>"END"</c> that neither delta contains. So the cutter never emits the last
/// <see cref="OutputLimits.Holdback"/> characters it has seen; those are only released by further text
/// proving no stop string starts inside them, or by <see cref="Flush"/> when the generation ends
/// without a hit. Both failure modes of getting this wrong are worse than not having the feature: too
/// little holdback leaks half a stop string, too much silently drops the end of an ordinary reply.
///
/// <b>The cap waits on the same lookahead.</b> A stop string can straddle the budget boundary too —
/// with <c>stop: "dEFG"</c> and a four-character budget, <c>"abcd"</c> then <c>"EFGH"</c> is a stop hit
/// at character three, so the reply is <c>"abc"</c> and finishes <c>stop</c>, not <c>"abcd"</c>
/// finishing <c>length</c>. Committing the cap the moment the budget is reached would decide that
/// before the evidence arrived, and would disagree with the whole-text answer. So the cap is only
/// committed once the text runs at least <see cref="OutputLimits.Holdback"/> characters past the
/// budget, or the generation ends — the same rule, in the same place, as the holdback itself. It also
/// means a reply that lands exactly on the budget finishes <c>stop</c>: the cap fires when text is
/// dropped, not when the budget is touched.
///
/// Not thread-safe. The streaming path drives it from the single channel reader, never from the
/// backend's callback thread; the non-streaming path locks its watcher instance.
/// </summary>
internal sealed class OutputCutter
{
    private readonly OutputLimits _limits;

    /// <summary>Text generated but not yet emitted: the held tail, plus whatever the last delta added.</summary>
    private string _pending = string.Empty;

    private int _emitted;

    public OutputCutter(OutputLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
    }

    /// <summary><c>length</c> or <c>stop</c> once a limit fired; null while the reply is still running.</summary>
    public string? FinishReason { get; private set; }

    /// <summary>True once a limit fired: nothing further will ever be emitted, and the generation should be cancelled.</summary>
    public bool IsCut => FinishReason is not null;

    /// <summary>Characters emitted so far — the length of the completion the client will have received.</summary>
    public int ContentLength => _emitted;

    /// <summary>
    /// Takes one backend delta and returns the text that is now safe to send, which may be empty (it is
    /// held back), the delta itself (no stop strings are configured), or a truncated prefix (a limit
    /// fired inside it). After a call that sets <see cref="IsCut"/> every later call returns empty.
    /// </summary>
    public string Accept(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        return Consume(delta, final: false);
    }

    /// <summary>
    /// The held tail, released because the generation ended without a stop string forming, or the last
    /// piece before the cap when the reply stopped inside the lookahead window. Empty once a limit
    /// fired — the text after a cut is deliberately never sent. Call once, after the last delta.
    /// </summary>
    public string Flush() => Consume(string.Empty, final: true);

    /// <summary>
    /// One decision point for both callers. <paramref name="final"/> says there is no more text coming,
    /// which is the only thing that changes: every wait-for-more-evidence rule below is satisfied
    /// immediately, because the evidence can no longer arrive.
    /// </summary>
    private string Consume(string incoming, bool final)
    {
        if (IsCut)
        {
            return string.Empty;
        }

        if (incoming.Length > 0)
        {
            _pending = _pending.Length == 0 ? incoming : _pending + incoming;
        }
        else if (!final && _pending.Length == 0)
        {
            return string.Empty;
        }

        // Where the earliest stop string starts inside the pending window, relative to it. Everything
        // before the window was searched with the full lookahead already, which is precisely what the
        // holdback buys: a character is only emitted once the longest stop string could have completed
        // after it.
        var stopAt = -1;
        var stops = _limits.Stop;
        for (var i = 0; i < stops.Count; i++)
        {
            var at = _pending.IndexOf(stops[i], StringComparison.Ordinal);
            if (at >= 0 && (stopAt < 0 || at < stopAt))
            {
                stopAt = at;
            }
        }

        // The cap's cut point in the same relative coordinates; null when the request set no cap.
        // Never negative: the release below never hands out more than capAt characters, so _emitted
        // cannot pass the budget without a cut.
        var capAt = _limits.MaxChars is { } max ? max - _emitted : (int?)null;

        int cutAt;
        if (stopAt >= 0 && (capAt is null || stopAt < capAt))
        {
            // The stop string itself is excluded from the output, per the OpenAI contract.
            cutAt = stopAt;
            FinishReason = "stop";
        }
        else if (capAt is { } cap && _pending.Length > cap && (final || _pending.Length - cap >= _limits.Holdback))
        {
            // Text was actually dropped (> cap, not >= cap) and no stop string can still turn out to
            // start before the budget: a stop string beginning at cap-1 would end by cap-1+Holdback+1,
            // so Holdback characters past the budget is exactly enough evidence to rule one out. Cutting
            // at the budget itself makes ceil(chars/4) land on the cap rather than one above it.
            cutAt = cap;
            FinishReason = "length";
        }
        else if (final)
        {
            // Nothing was cut and nothing more is coming, so the whole tail is ordinary output.
            cutAt = _pending.Length;
        }
        else
        {
            // Release everything that can no longer be the first half of a stop string, and never more
            // than the budget, which the branch above has not yet had the evidence to commit to.
            var safe = _pending.Length - _limits.Holdback;
            if (capAt is { } room && safe > room)
            {
                safe = room;
            }

            if (safe <= 0)
            {
                return string.Empty;
            }

            var release = _pending[..safe];
            _pending = _pending[safe..];
            _emitted += safe;
            return release;
        }

        var cut = _pending[..cutAt];
        _pending = string.Empty;
        _emitted += cutAt;
        return cut;
    }
}
