using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;
using NpuBridge.Prompting;
using NpuBridge.Tokenizers;

namespace NpuBridge.Api;

/// <summary>
/// A context checked out of the cache or freshly created, together with the prompt to send on it.
/// The generation phase of both response shapes runs on one of these instead of on a bare context,
/// and settles it exactly once on the way out: <see cref="Keep"/> when the generation ended
/// <see cref="GenerationStatus.Complete"/> with its text intact, so the context goes back into the
/// cache under the key of the transcript it now holds; <see cref="ReturnUntouched"/> when no
/// generation ran on it at all (a preflight refused the prompt), so a cached context goes back under
/// the key it came out under; and <see cref="Dispose"/>, which disposes the context unless one of the
/// other two already settled it. Dispose is what the callers' <c>finally</c> runs, after the drain, so
/// a context is never disposed while its generation may still be writing to it (D51), and every
/// context that is not in the cache is disposed on every path (D43).
/// </summary>
internal sealed class ContextLease : IDisposable
{
    private readonly ContextCache _cache;
    private readonly string? _cacheKey;
    private readonly string? _systemText;
    private readonly IReadOnlyList<ChatMessage> _turns;
    private bool _settled;

    internal ContextLease(
        ContextCache cache,
        IModelContext context,
        string prompt,
        bool cacheHit,
        string? cacheKey,
        int tailTurns,
        int promptChars,
        int transcriptChars,
        int transcriptTokens,
        string? systemText,
        IReadOnlyList<ChatMessage> turns)
    {
        _cache = cache;
        _cacheKey = cacheKey;
        _systemText = systemText;
        _turns = turns;
        Context = context;
        Prompt = prompt;
        CacheHit = cacheHit;
        TailTurns = tailTurns;
        PromptChars = promptChars;
        TranscriptChars = transcriptChars;
        TranscriptTokens = transcriptTokens;
    }

    public IModelContext Context { get; }

    /// <summary>What to send: the whole transcript on a miss, only the turns after the cached prefix on a hit.</summary>
    public string Prompt { get; }

    public bool CacheHit { get; }

    /// <summary>Turns rendered into <see cref="Prompt"/>: all of them on a miss, the tail on a hit.</summary>
    public int TailTurns { get; }

    /// <summary>Characters the model sees on this request: the prompt, plus native system text on a miss. The log line's <c>prompt_chars</c>.</summary>
    public int PromptChars { get; }

    /// <summary>Characters of the whole transcript as the model holds it after this request's prompt: the overflow message and the pressure check.</summary>
    public int TranscriptChars { get; }

    /// <summary>The same transcript in the backend's tokens (native system text included): <c>usage.prompt_tokens</c>, the same on a hit and a miss (D74, D80).</summary>
    public int TranscriptTokens { get; }

    /// <summary>
    /// The generation ended Complete and <paramref name="reply"/> is the text the client received,
    /// uncut, so the context's state is exactly the transcript plus this reply: store it under that
    /// transcript's key. Call after the generation task has ended and never touch the context again.
    /// </summary>
    /// <param name="toolCalls">
    /// The calls the reply was reshaped into, when it was one (chunk 7). The stored key has to
    /// describe the turn as the *client* will send it back, and for a tool call that is an assistant
    /// message with null content and this array — not the fenced text the model wrote. Keying it by
    /// that text instead made every tool-using conversation miss on its next turn, which is every turn
    /// of an agent loop and exactly what the cache is for (D83).
    /// </param>
    public void Keep(string reply, IReadOnlyList<ChatToolCall>? toolCalls = null)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (_settled)
        {
            return;
        }

        _settled = true;

        var turn = toolCalls is { Count: > 0 }
            ? new ChatMessage("assistant", null, null, null, toolCalls)
            : new ChatMessage("assistant", ChatMessageContent.FromText(reply), null, null);

        _cache.Store(ConversationKey.Compute(_systemText, _turns, turn), Context);
    }

    /// <summary>
    /// No generation ran on the context. A cached one goes back under the key it was checked out with,
    /// its state unchanged; a fresh one is disposed, there being nothing worth keeping.
    /// </summary>
    public void ReturnUntouched()
    {
        if (_settled)
        {
            return;
        }

        _settled = true;
        if (_cacheKey is not null)
        {
            _cache.Store(_cacheKey, Context);
        }
        else
        {
            Context.Dispose();
        }
    }

    public void Dispose()
    {
        if (_settled)
        {
            return;
        }

        _settled = true;
        Context.Dispose();
    }
}

/// <summary>Either a lease to generate on, or the failure to report instead. Exactly one is set.</summary>
internal sealed record ContextAcquisition(ContextLease? Lease, GenerationFailure? Failure)
{
    public static ContextAcquisition Acquired(ContextLease lease) => new(lease, null);

    public static ContextAcquisition Refused(GenerationFailure failure) => new(null, failure);
}

/// <summary>
/// The conversation half of one <c>/v1/chat/completions</c> request: which turns are being sent,
/// which cached prefix (if any) they extend, and how many turns overflow handling has dropped. Built
/// from a <see cref="PreparedChatRequest"/>; shared by both response shapes so a streamed and a
/// non-streamed request for the same transcript hit the same entry and truncate the same way.
///
/// <see cref="Acquire"/> is the cache lookup plus the overflow check. On a backend with
/// <see cref="BackendCapabilities.PromptLengthPreflight"/> the check is
/// <see cref="ILanguageModelBackend.GetUsablePromptLength"/> before generating — never a failed
/// generation's status, which on Phi Silica is a generic <c>Error</c> after 26 s rather than
/// <c>PromptLargerThanContext</c> (D55) — and, with <c>--truncate-history</c>, the truncation loop runs
/// here: drop the oldest exchange, look the shorter transcript up again, check again. On a backend
/// without the preflight there is nothing to ask, so <see cref="Acquire"/> returns the lease and the
/// caller learns from the generation's status instead: a <c>PromptLargerThanContext</c> is followed by
/// <see cref="TryDropOldestExchange"/> and, when that dropped something, a fresh
/// <see cref="Acquire"/>. Aion has no preflight and its overflow behaviour is unmeasured (D70), so
/// that path is the one to re-check when it runs.
/// </summary>
internal sealed class ConversationSession
{
    /// <summary>Response header carrying how many turns were dropped, when any were.</summary>
    public const string TruncatedTurnsHeader = "x-npu-bridge-truncated-turns";

    private readonly PreparedChatRequest _prepared;
    private readonly ContextCache _cache;
    private readonly BridgeOptions _options;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ChatMessage> _systemMessages;
    private readonly bool _useNativeSystem;
    private readonly bool _preflight;
    private IReadOnlyList<ChatMessage> _turns;
    private bool _pressureLogged;

    public ConversationSession(PreparedChatRequest prepared, ContextCache cache, BridgeOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _prepared = prepared;
        _cache = cache;
        _options = options;
        _logger = logger;
        _systemMessages = PromptTemplate.SystemMessages(prepared.Request.Messages!);
        _turns = PromptTemplate.Turns(prepared.Request.Messages!);
        _useNativeSystem = prepared.NativeSystem is not null;
        _preflight = prepared.Backend.Capabilities.HasFlag(BackendCapabilities.PromptLengthPreflight);
    }

    private int _droppedTurns;

    /// <summary>
    /// The <see cref="DroppedTurns"/> value <see cref="ApplyTruncationHeader"/> has already written onto
    /// the response, or -1 for none. Only ever touched by whichever thread owns the response, so it
    /// needs no synchronisation: the JSON shape's caller is suspended while its closure runs, and the
    /// streaming shape calls this from the request thread alone (chunk 8 fix round 2). It exists so the
    /// header can be applied at more than one moment without the second call mistaking "already sent,
    /// still accurate" for "too late to send", which would log a warning about a header the client
    /// actually received.
    /// </summary>
    private int _headerAppliedFor = -1;

    /// <summary>
    /// False whenever <see cref="DroppedTurns"/> may still grow: while <see cref="Acquire"/> is running,
    /// and from the moment <see cref="TryDropOldestExchange"/> commits to a drop on the status-driven
    /// retry path, which the endpoint closures drive from outside <see cref="Acquire"/>. Set true by
    /// <see cref="Acquire"/>'s finally, so a preflight that threw settles it too — nothing will drop
    /// another turn after that either. Volatile for the same reason <see cref="DroppedTurns"/> is:
    /// since chunk 8 the truncation loop runs on the scheduler's worker while the request thread is
    /// writing keep-alives, and the release on this write is what publishes the count the acquiring
    /// read below then trusts.
    /// </summary>
    private bool _truncationSettled;

    /// <summary>
    /// Turns dropped so far by overflow handling. The value of the response header when non-zero.
    /// Written by whichever thread runs the truncation loop — since chunk 8 that is the scheduler's
    /// worker — and read by the request thread for its log line and its header, so the two accessors
    /// are volatile: the reader must not be handed a value the compiler hoisted out of a loop, and on
    /// the streaming shape the two threads genuinely run at once (the scheduled task is started, not
    /// awaited, so the reader loop is emitting keep-alives while the closure truncates).
    /// </summary>
    public int DroppedTurns
    {
        get => Volatile.Read(ref _droppedTurns);
        private set => Volatile.Write(ref _droppedTurns, value);
    }

    /// <summary>
    /// Looks the transcript up in the cache, creates a fresh context on a miss, and — on a backend
    /// with a preflight — refuses or truncates before anything is generated. The returned lease is
    /// the caller's to settle.
    /// </summary>
    public ContextAcquisition Acquire()
    {
        Volatile.Write(ref _truncationSettled, false);
        try
        {
            return AcquireCore();
        }
        finally
        {
            Volatile.Write(ref _truncationSettled, true);
        }
    }

    private ContextAcquisition AcquireCore()
    {
        while (true)
        {
            var lease = Lookup();
            if (!_preflight)
            {
                return ContextAcquisition.Acquired(lease);
            }

            // Guarded: the lease is owned here until it is handed back, and the caller's finally
            // cannot reach a lease it never received. A preflight that throws is a runtime fault
            // against this very context, cached or fresh, so it is disposed rather than returned.
            int? usable;
            try
            {
                usable = _prepared.Backend.GetUsablePromptLength(lease.Context, lease.Prompt);
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            if (usable is null || usable.Value >= lease.Prompt.Length)
            {
                return ContextAcquisition.Acquired(lease);
            }

            // Overflow, known before a token was generated. The context is untouched: a cached one
            // goes back, a fresh one is dropped.
            lease.ReturnUntouched();

            if (_options.TruncateHistory && TryDropOldestExchange())
            {
                continue;
            }

            return ContextAcquisition.Refused(Overflow(lease, usable.Value));
        }
    }

    /// <summary>
    /// The one place turns are ever dropped, and only <c>--truncate-history</c> reaches it. Removes
    /// the oldest exchange: every turn from the start of the transcript up to, not including, the
    /// next user turn. An exchange is a user turn and everything the model did in answer to it —
    /// the assistant reply, and with tool use the assistant's calls, the tool results and the
    /// assistant's final answer — so what remains still begins with a user turn and no tool result
    /// is ever left without the call it answered (the boundary at the first assistant turn did
    /// exactly that; both chunk 5 reviews found it). The final turn is never dropped, and a
    /// transcript with no second user turn — a single question, or a question whose tool results
    /// are still being answered — is the active exchange and cannot be truncated: this returns
    /// false. Each drop is logged at Warning.
    /// </summary>
    public bool TryDropOldestExchange()
    {
        if (!_options.TruncateHistory)
        {
            return false;
        }

        var nextUser = -1;
        for (var i = 1; i < _turns.Count; i++)
        {
            if (string.Equals(_turns[i].Role, "user", StringComparison.Ordinal))
            {
                nextUser = i;
                break;
            }
        }

        if (nextUser < 0)
        {
            return false;
        }

        var dropped = nextUser;
        _turns = _turns.Skip(dropped).ToList();

        // Unsettled before the count moves, not only inside Acquire. The endpoint closures call this
        // directly on the status-driven retry path (a backend with no preflight reports the overflow by
        // finishing the generation), and there the flag is true and DroppedTurns is about to grow with
        // no Acquire running to have reset it. The window that opens here spans the failed attempt's
        // lease disposal -- a real WinRT context disposal on hardware -- and a keep-alive landing inside
        // it would stamp this partial count and lock it in, which is the defect the IfSettled hook
        // exists to prevent, arriving by the sibling path. A no-op inside AcquireCore, where the flag is
        // already false; the retry's own Acquire re-settles it in its finally.
        Volatile.Write(ref _truncationSettled, false);
        DroppedTurns += dropped;
        _logger.LogWarning(
            "req={RequestId} prompt overflow: dropped the oldest {Dropped} turn(s) (--truncate-history); {Remaining} turn(s) remain, {TotalDropped} dropped so far.",
            _prepared.RequestId, dropped, _turns.Count, DroppedTurns);
        return true;
    }

    /// <summary>
    /// The truncation header. Set only when turns were dropped and only while the status line is still
    /// the server's: on a stream that has already sent a keep-alive the headers are spent, and the
    /// caller says so in the log instead.
    ///
    /// Safe to call more than once for one request, which the streaming shape does (chunk 8 fix round
    /// 2): once just before the first SSE frame commits the response, and once when the generation's
    /// outcome is known, since a truncation can land on either side of that first frame. A second call
    /// that would write exactly what the first already wrote does nothing at all — in particular it does
    /// not warn that the header "cannot be sent" about a header the client already has. **Must be called
    /// only from the thread that owns the response**: it reads <c>HasStarted</c> and then mutates the
    /// header collection, and Kestrel's is neither thread-safe nor mutable once the response has begun.
    /// </summary>
    public void ApplyTruncationHeader(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var dropped = DroppedTurns;
        if (dropped == 0 || dropped == _headerAppliedFor)
        {
            return;
        }

        if (response.HasStarted)
        {
            _logger.LogWarning("req={RequestId} {Dropped} turn(s) were dropped after the response headers were committed; the {Header} header cannot be sent.",
                _prepared.RequestId, dropped, TruncatedTurnsHeader);
            return;
        }

        _headerAppliedFor = dropped;
        response.Headers[TruncatedTurnsHeader] = dropped.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What the streaming shapes' first-frame hook calls, and the only difference from
    /// <see cref="ApplyTruncationHeader"/> is that it declines to write a count that is still growing.
    /// A keep-alive can land while turns are still being dropped, and the count it would stamp is
    /// whatever had gone by then; <see cref="_headerAppliedFor"/> makes that partial number permanent,
    /// and the real one — larger — reaches only the log. A client told "2 turns dropped" when 6 went is
    /// worse off than one told nothing, so it is told nothing: the post-outcome call then either writes
    /// the complete count, if the response is somehow still unstarted, or logs the "cannot be sent"
    /// warning that a late truncation on a stream has always produced.
    ///
    /// Both places that can grow the count clear the flag first — <see cref="Acquire"/> for the
    /// preflight loop, <see cref="TryDropOldestExchange"/> for the status-driven retry the endpoint
    /// closures drive — so what this declines to write is every partial count, not only the preflight
    /// loop's.
    /// </summary>
    public void ApplyTruncationHeaderIfSettled(HttpResponse response)
    {
        if (Volatile.Read(ref _truncationSettled))
        {
            ApplyTruncationHeader(response);
        }
    }

    private ContextLease Lookup()
    {
        var systemText = _prepared.Rendered.SystemText;
        var backend = _prepared.Backend;

        // The whole transcript as the model will hold it after this request, whichever path sends it.
        // Rendered even on a hit: it is what usage.prompt_tokens estimates from, and the pressure check
        // below is about the transcript, not about the tail.
        var full = PromptTemplate.Render(Transcript(), _useNativeSystem, _prepared.ToolInstructions);
        var nativeSystem = _useNativeSystem ? full.SystemText : null;
        var transcriptChars = full.Prompt.Length + (nativeSystem?.Length ?? 0);
        LogPressure(transcriptChars);

        // usage.prompt_tokens, in the backend's own count (D80). The native system text is counted on
        // its own: the runtime holds it in the context, outside the prompt string.
        var counter = backend.TokenCounter;
        // The preparer already counted this immutable native system text for the wire-level guard.
        var transcriptTokens = counter.Count(full.Prompt) + (nativeSystem is null ? 0 : _prepared.NativeSystemTokens);

        var checkout = _cache.CheckoutLongest(ConversationKey.PrefixKeys(systemText, _turns));
        if (checkout is not null)
        {
            var tail = _turns.Skip(checkout.Prefix.TurnCount).ToList();
            var prompt = PromptTemplate.RenderTail(tail);
            if (_options.Verbose)
            {
                // The preparer already printed the whole rendered transcript; on a hit this is what
                // actually goes to the model instead.
                _logger.LogInformation(
                    "req={RequestId} context cache hit on context {ContextId}: {PrefixTurns} turn(s) cached, {TailTurns} rendered:\n---- tail ----\n{Tail}\n---- end ----",
                    _prepared.RequestId, checkout.Context.Id, checkout.Prefix.TurnCount, tail.Count, prompt);
            }

            return new ContextLease(_cache, checkout.Context, prompt, cacheHit: true, checkout.Prefix.Key,
                tailTurns: tail.Count, promptChars: prompt.Length, transcriptChars, transcriptTokens, systemText, _turns);
        }

        var context = backend.CreateContext(nativeSystem);
        return new ContextLease(_cache, context, full.Prompt, cacheHit: false, cacheKey: null,
            tailTurns: _turns.Count, promptChars: transcriptChars, transcriptChars, transcriptTokens, systemText, _turns);
    }

    private List<ChatMessage> Transcript()
    {
        var transcript = new List<ChatMessage>(_systemMessages.Count + _turns.Count);
        transcript.AddRange(_systemMessages);
        transcript.AddRange(_turns);
        return transcript;
    }

    /// <summary>
    /// Context pressure, per request, against <c>--context-window-hint</c> (tokens, converted to
    /// characters at <see cref="CharEstimateTokenCounter.CharsPerToken"/>). Deliberately the estimate
    /// rather than the backend's own counter (D80): a hint the operator typed is not worth tokenizing
    /// the whole transcript a second time for, and this is not a measurement — the preflight is the
    /// measurement, where one exists. Hence a Warning at nine tenths of the window and silence below
    /// it, logged once per request however many truncation rounds it takes.
    /// </summary>
    private void LogPressure(int transcriptChars)
    {
        if (_pressureLogged)
        {
            return;
        }

        var windowChars = (long)_options.ContextWindowHint * CharEstimateTokenCounter.CharsPerToken;
        if (transcriptChars * 10L >= windowChars * 9)
        {
            _pressureLogged = true;
            _logger.LogWarning(
                "req={RequestId} context pressure: transcript is {TranscriptChars} chars, {Percent}% of the --context-window-hint window ({HintTokens} tokens, about {WindowChars} chars).",
                _prepared.RequestId, transcriptChars, transcriptChars * 100L / Math.Max(1, windowChars), _options.ContextWindowHint, windowChars);
        }
    }

    private GenerationFailure Overflow(ContextLease lease, int usable)
    {
        var where = lease.CacheHit
            ? $"the {lease.Prompt.Length}-character tail of {lease.TailTurns} new turn(s) on a cached context"
            : $"the {lease.Prompt.Length}-character prompt";
        var hint = _options.TruncateHistory
            ? "Nothing older than the message being answered is left to drop."
            : "Send a shorter conversation, or start the bridge with --truncate-history to drop the oldest turns instead.";
        var detail = $"Backend '{_prepared.Backend.ModelId}' can take {usable} characters of {where}; the transcript is {lease.TranscriptChars} characters over {_turns.Count} turn(s). {hint}";

        return GenerationFailure.ContextLengthExceeded(detail);
    }
}
