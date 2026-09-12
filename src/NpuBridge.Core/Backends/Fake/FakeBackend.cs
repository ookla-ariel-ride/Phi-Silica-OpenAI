using System.Text;
using System.Text.RegularExpressions;

namespace NpuBridge.Backends.Fake;

/// <summary>
/// Deterministic in-process backend. Streams scripted tokens, injects faults on request, and records
/// every call so tests can assert exactly what a real backend would have seen.
/// </summary>
public sealed partial class FakeBackend : ILanguageModelBackend
{
    private readonly FakeBackendOptions _options;
    private readonly object _gate = new();
    private readonly List<FakeGenerationRequest> _calls = new();
    private int _contextsCreated;
    private int _contextsDisposed;
    private int _nextContextId;
    private int _preflightCalls;
    private bool _initialized;
    private bool _disposed;

    public FakeBackend() : this(new FakeBackendOptions())
    {
    }

    public FakeBackend(FakeBackendOptions options)
    {
        _options = options;
        Diagnostics = new Dictionary<string, object?> { ["fake"] = true };
    }

    public FakeBackendOptions Options => _options;

    public string ModelId => _options.ModelId;

    public string DisplayName => "Fake backend";

    public BackendCapabilities Capabilities => _options.Capabilities;

    public Tokenizers.ITokenCounter TokenCounter => _options.TokenCounter;

    public int? ContextWindowTokens => _options.ContextWindowTokens;

    public IReadOnlyDictionary<string, object?> Diagnostics { get; }

    public bool IsInitialized => _initialized;

    public int ContextsCreated => Volatile.Read(ref _contextsCreated);

    public int ContextsDisposed => Volatile.Read(ref _contextsDisposed);

    /// <summary>Contexts created and not yet disposed. Leak tests assert this returns to zero.</summary>
    public int ActiveContexts => ContextsCreated - ContextsDisposed;

    /// <summary>Every generation request, in order.</summary>
    public IReadOnlyList<FakeGenerationRequest> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_options.InitDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.InitDelay, cancellationToken).ConfigureAwait(false);
        }

        if (_options.InitGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_options.InitFailure is { } failure)
        {
            throw failure;
        }

        _initialized = true;
    }

    public IModelContext CreateContext(string? systemPrompt)
    {
        ThrowIfDisposed();
        ThrowIfNotInitialized();
        var id = $"fake-ctx-{Interlocked.Increment(ref _nextContextId)}";
        Interlocked.Increment(ref _contextsCreated);
        var effectiveSystem = Capabilities.HasFlag(BackendCapabilities.SystemPromptContext) ? systemPrompt : null;
        return new FakeContext(this, id, effectiveSystem);
    }

    public int? GetUsablePromptLength(IModelContext context, string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ThrowIfDisposed();
        ThrowIfNotInitialized();
        if (!Capabilities.HasFlag(BackendCapabilities.PromptLengthPreflight))
        {
            return null;
        }

        var fake = Own(context);
        _options.OnPreflight?.Invoke(Interlocked.Increment(ref _preflightCalls));
        if (_options.PreflightFailure is { } preflightFailure)
        {
            throw preflightFailure;
        }

        if (_options.MaxPromptChars is not { } max)
        {
            return prompt.Length;
        }

        var room = Math.Max(0, max - fake.TotalChars);
        return Math.Min(prompt.Length, room);
    }

    public async Task<GenerationResult> GenerateAsync(
        IModelContext context,
        string prompt,
        SamplingOptions? sampling,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(onDelta);
        ThrowIfDisposed();
        ThrowIfNotInitialized();
        var fake = Own(context);

        var request = new FakeGenerationRequest(fake.Id, fake.SystemPrompt, fake.History, prompt, sampling);
        lock (_gate)
        {
            _calls.Add(request);
        }

        // Before any verdict, including whether the prompt fits: a real runtime answers that instantly,
        // and a test that needs the answer to arrive late — after the caller has already committed to a
        // response shape by sending a keep-alive comment — has no other way to arrange it. FirstTokenGate
        // cannot: it is held after the prompt-length verdict, so it never delays the verdict itself.
        if (!await WaitAtGateAsync(_options.StartGate, cancellationToken).ConfigureAwait(false))
        {
            return new GenerationResult(string.Empty, GenerationStatus.Cancelled, "fake: cancelled at the start gate");
        }

        if (_options.MaxPromptChars is { } max && fake.TotalChars + prompt.Length > max)
        {
            return new GenerationResult(string.Empty, GenerationStatus.PromptLargerThanContext, "fake: MaxPromptChars exceeded");
        }

        // Held after the prompt verdict and before the first token, so a test can order events against
        // the stream instead of against a clock: hold this until the response headers have been read and
        // "the headers came before the first token" is an assertion rather than a stopwatch bound.
        if (!await WaitAtGateAsync(_options.FirstTokenGate, cancellationToken).ConfigureAwait(false))
        {
            return new GenerationResult(string.Empty, GenerationStatus.Cancelled, "fake: cancelled at the first-token gate");
        }

        // While a CancellationGate is set and incomplete the token is not looked at: the generation keeps
        // producing and never returns Cancelled. That is what a runtime whose in-flight operation cannot
        // be stopped on demand looks like, and it is the case the caller's cancel-drain-dispose ordering
        // exists for -- disposing the context while this is still running is a use-after-dispose.
        bool ObservesCancellation() => _options.CancellationGate?.Task.IsCompleted ?? true;

        if (_options.ThrowFromCancellationRegistration)
        {
            // Not disposed on purpose -- see the option's own documentation. It has to still be attached
            // when the caller cancels in its finally.
            cancellationToken.Register(static () => throw new InvalidOperationException("fake: cancellation registration failed"));
        }

        var tokens = (_options.Responder ?? DefaultResponder)(request);
        var text = new StringBuilder();
        var emitted = 0;

        foreach (var token in tokens)
        {
            if (cancellationToken.IsCancellationRequested && ObservesCancellation())
            {
                return CancelledOrThrow(text.ToString(), "fake: cancelled before token", cancellationToken);
            }

            if (_options.FailAfterTokens == emitted)
            {
                if (_options.FailureException is { } ex)
                {
                    throw ex;
                }

                return new GenerationResult(text.ToString(), _options.FailureStatus, "fake: injected failure");
            }

            var delay = emitted == 0 ? _options.FirstTokenDelay + _options.TokenDelay : _options.TokenDelay;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, ObservesCancellation() ? cancellationToken : CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return CancelledOrThrow(text.ToString(), "fake: cancelled during delay", cancellationToken);
                }
            }

            text.Append(token);
            emitted++;

            // The real runtimes raise Progress on a thread-pool thread, never on the awaiting flow.
            // Mirror that so callers that touch non-thread-safe state in onDelta fail here, not on the NPU.
            if (_options.DeliverDeltasOnThreadPool)
            {
                var captured = token;
                await Task.Run(() => onDelta(captured), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                onDelta(token);
            }
        }

        // A fault configured at exactly the end of the stream fires after the last delta, like a runtime
        // that streams everything and then reports a non-Complete status.
        if (_options.FailAfterTokens == emitted)
        {
            if (_options.FailureException is { } ex)
            {
                throw ex;
            }

            return new GenerationResult(text.ToString(), _options.FailureStatus, "fake: injected failure after last token");
        }

        var full = text.ToString();
        fake.Record(prompt, full);
        return GenerationResult.Complete(full);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Default reply: acknowledges the last non-empty line of the prompt, split into word tokens.</summary>
    public static IEnumerable<string> DefaultResponder(FakeGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lastLine = request.Prompt
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0) ?? string.Empty;
        return Tokenize($"This is the fake npu-bridge backend. You said: {lastLine}");
    }

    /// <summary>Splits text into word-ish tokens that keep their trailing whitespace, so concatenation round-trips.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TokenPattern().Matches(text).Select(m => m.Value).ToArray();
    }

    internal void OnContextDisposed() => Interlocked.Increment(ref _contextsDisposed);

    /// <summary>
    /// Waits at one of the generation's gates. False means the caller's token was cancelled while the
    /// wait was still in progress; the caller names the gate it was holding at in the status detail, so
    /// the waiting is shared and the verdict is not. <see cref="InitializeAsync"/> deliberately does not
    /// use this: its gate has no status to report and lets the cancellation throw.
    ///
    /// A gate that is already open wins over a token that is already cancelled — <c>WaitAsync</c> takes
    /// the completed task's fast path without consulting the token — so an open gate does not report
    /// <see cref="GenerationStatus.Cancelled"/> the way the delay this replaced would have. That is the
    /// behaviour every other gate here already had, and it is the right one for a gate: an open gate is
    /// an event that has happened, and a test that wants a cancellation observed at this point holds the
    /// gate shut.
    /// </summary>
    private static async Task<bool> WaitAtGateAsync(TaskCompletionSource? gate, CancellationToken cancellationToken)
    {
        if (gate is null)
        {
            return true;
        }

        try
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// How this backend reports a cancellation it observed: as the <see cref="GenerationStatus.Cancelled"/>
    /// status <see cref="ILanguageModelBackend"/> requires, or — with
    /// <see cref="FakeBackendOptions.ThrowOnCancellation"/> — by letting an
    /// <see cref="OperationCanceledException"/> escape, which is the adapter contract violation D82's
    /// unfiltered catches exist for and the only way a test can reach the paths that handle one.
    /// </summary>
    private GenerationResult CancelledOrThrow(string text, string detail, CancellationToken cancellationToken)
    {
        if (_options.ThrowOnCancellation)
        {
            throw new OperationCanceledException(detail, cancellationToken);
        }

        return new GenerationResult(text, GenerationStatus.Cancelled, detail);
    }

    private FakeContext Own(IModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context is not FakeContext fake || !ReferenceEquals(fake.Owner, this))
        {
            throw new ArgumentException("Context was not created by this backend.", nameof(context));
        }

        fake.ThrowIfDisposed();
        return fake;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfNotInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("FakeBackend.InitializeAsync has not completed; the real runtimes have no model handle before CreateAsync returns.");
        }
    }

    [GeneratedRegex(@"\s*\S+\s*|\s+")]
    private static partial Regex TokenPattern();
}

public sealed class FakeBackendOptions
{
    public string ModelId { get; set; } = "fake";

    public BackendCapabilities Capabilities { get; set; } =
        BackendCapabilities.SamplingOptions
        | BackendCapabilities.SystemPromptContext
        | BackendCapabilities.PromptLengthPreflight
        | BackendCapabilities.Cancellation;

    /// <summary>
    /// The counter behind <c>usage</c> and the <c>max_tokens</c> budget. chars/4 by default, so a test
    /// that asserts usage numbers reads them as D44 defined them; a test of the counted path sets one
    /// no estimate could mimic.
    /// </summary>
    public Tokenizers.ITokenCounter TokenCounter { get; set; } = Tokenizers.CharEstimateTokenCounter.Instance;

    /// <summary>Measured usable context window in <see cref="TokenCounter"/> tokens. Null leaves it unknown.</summary>
    public int? ContextWindowTokens { get; set; }

    /// <summary>Produces the token stream for a request. Defaults to <see cref="FakeBackend.DefaultResponder"/>.</summary>
    public Func<FakeGenerationRequest, IEnumerable<string>>? Responder { get; set; }

    /// <summary>Delay before each token; lets tests exercise cancellation and TTFT paths.</summary>
    public TimeSpan TokenDelay { get; set; }

    /// <summary>Extra delay before the first token only (simulates prompt processing / slow TTFT).</summary>
    public TimeSpan FirstTokenDelay { get; set; }

    /// <summary>
    /// While set and incomplete, the generation waits before deciding anything at all, the
    /// prompt-length verdict included. <see cref="FirstTokenGate"/> is held after that verdict and so
    /// cannot arrange a test whose subject is a verdict that lands late, once a keep-alive comment has
    /// already committed the response headers. Released by the test that holds it rather than by a
    /// clock, so "the keep-alive went out before the verdict" is a property of the arrangement instead
    /// of a millisecond bound that a loaded machine will break.
    /// </summary>
    public TaskCompletionSource? StartGate { get; set; }

    /// <summary>
    /// While set and incomplete, the generation waits after the prompt-length verdict and before its
    /// first token. Unlike <see cref="FirstTokenDelay"/> it is released by the test rather than by a
    /// clock, so an ordering ("the response headers were committed while no token existed yet") can be
    /// asserted as an ordering instead of as a millisecond bound that a loaded machine will break.
    /// </summary>
    public TaskCompletionSource? FirstTokenGate { get; set; }

    /// <summary>
    /// While set and incomplete, the generation ignores the cancellation token entirely: it keeps
    /// producing deltas and never reports <see cref="GenerationStatus.Cancelled"/>. Models a runtime
    /// whose in-flight operation cannot be stopped on demand, so that a test can hold a generation open
    /// after its caller has given up and check that the context is not disposed underneath it.
    /// </summary>
    public TaskCompletionSource? CancellationGate { get; set; }

    /// <summary>
    /// Report an observed cancellation by throwing <see cref="OperationCanceledException"/> instead of
    /// returning <see cref="GenerationStatus.Cancelled"/> — an adapter breaking the
    /// <see cref="ILanguageModelBackend"/> rule that the runtime's own cancellation is swallowed and
    /// reported as a status. That violation is the one D82's unfiltered catch clauses exist for, and
    /// since chunk 8 it also decides whether the scheduler reports a job that ran and threw or one it
    /// never ran at all (<see cref="NpuBridge.Api.ScheduleResult{TResult}.Ran"/>), so it needs to be
    /// reachable from a test rather than only reasoned about.
    /// </summary>
    public bool ThrowOnCancellation { get; set; }

    /// <summary>
    /// Invoke <c>onDelta</c> on a thread-pool thread like the WinRT Progress callback does (default),
    /// or inline for tests that want deterministic single-threaded behaviour.
    /// </summary>
    public bool DeliverDeltasOnThreadPool { get; set; } = true;

    /// <summary>Delay in <see cref="FakeBackend.InitializeAsync"/> before completing.</summary>
    public TimeSpan InitDelay { get; set; }

    /// <summary>When set, initialization waits for this to complete. Lets tests observe the loading state.</summary>
    public TaskCompletionSource? InitGate { get; set; }

    /// <summary>When set, initialization throws this.</summary>
    public Exception? InitFailure { get; set; }

    /// <summary>Fail after this many tokens have been emitted (0 = fail before the first token).</summary>
    public int? FailAfterTokens { get; set; }

    /// <summary>Status returned when <see cref="FailAfterTokens"/> is hit and <see cref="FailureException"/> is null.</summary>
    public GenerationStatus FailureStatus { get; set; } = GenerationStatus.Error;

    /// <summary>Exception thrown when <see cref="FailAfterTokens"/> is hit (simulates a runtime fault).</summary>
    public Exception? FailureException { get; set; }

    /// <summary>Simulated context window in characters (context history + prompt). Null = unlimited.</summary>
    public int? MaxPromptChars { get; set; }

    /// <summary>
    /// Leaves a callback on the caller's cancellation token that throws when the token is cancelled.
    /// <see cref="CancellationTokenSource.CancelAsync"/> collects such a throw and faults the task it
    /// returns, which is how a real CsWinRT registration behaves when the <c>IAsyncInfo.Cancel()</c> it
    /// makes on the live WinRT operation fails rather than no-ops. The registration is deliberately not
    /// disposed, so it outlives the generation and it is the caller's own cancel — the one in its
    /// finally, standing immediately before the context is disposed — that trips it.
    /// </summary>
    public bool ThrowFromCancellationRegistration { get; set; }

    /// <summary>
    /// When set, <see cref="FakeBackend.GetUsablePromptLength"/> throws this instead of answering,
    /// after the context has been validated. The Phi Silica preflight is a raw WinRT call whose
    /// guard only translates access-denied; any other COM failure propagates, and the context it was
    /// asked about was already checked out of the cache or freshly created when it did.
    /// </summary>
    public Exception? PreflightFailure { get; set; }

    /// <summary>
    /// Called on every <see cref="FakeBackend.GetUsablePromptLength"/> with that call's 1-based number,
    /// after the context has been validated and before an answer is computed. It exists because the
    /// <c>--truncate-history</c> loop has no other observable moment: it runs start to finish inside
    /// <c>ConversationSession.Acquire</c>, on the scheduler's worker since chunk 8, while the request
    /// thread may already be writing keep-alives to the same response — and a test whose subject is what
    /// that thread is allowed to commit *during* the loop has to be able to stand inside it. Synchronous
    /// on purpose, since the preflight it models is a blocking call on the shared model handle; a test
    /// that parks here parks the worker, which is exactly the arrangement.
    /// </summary>
    public Action<int>? OnPreflight { get; set; }
}

/// <summary>What the fake backend saw for one generation.</summary>
public sealed record FakeGenerationRequest(
    string ContextId,
    string? SystemPrompt,
    IReadOnlyList<FakeTurn> History,
    string Prompt,
    SamplingOptions? Sampling);

public sealed record FakeTurn(string Prompt, string Response);

internal sealed class FakeContext : IModelContext
{
    private readonly List<FakeTurn> _history = new();
    private bool _disposed;

    internal FakeContext(FakeBackend owner, string id, string? systemPrompt)
    {
        Owner = owner;
        Id = id;
        SystemPrompt = systemPrompt;
    }

    public string Id { get; }

    public string? SystemPrompt { get; }

    internal FakeBackend Owner { get; }

    internal IReadOnlyList<FakeTurn> History
    {
        get
        {
            lock (_history)
            {
                return _history.ToArray();
            }
        }
    }

    /// <summary>Characters absorbed so far (system prompt plus every prompt and response).</summary>
    internal int TotalChars
    {
        get
        {
            lock (_history)
            {
                return (SystemPrompt?.Length ?? 0) + _history.Sum(t => t.Prompt.Length + t.Response.Length);
            }
        }
    }

    internal void Record(string prompt, string response)
    {
        // Disposed mid-generation: the real runtime would have thrown; drop the turn rather than resurrect it.
        ThrowIfDisposed();
        lock (_history)
        {
            _history.Add(new FakeTurn(prompt, response));
        }
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Owner.OnContextDisposed();
    }
}
