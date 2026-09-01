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

        if (_options.MaxPromptChars is { } max && fake.TotalChars + prompt.Length > max)
        {
            return new GenerationResult(string.Empty, GenerationStatus.PromptLargerThanContext, "fake: MaxPromptChars exceeded");
        }

        var tokens = (_options.Responder ?? DefaultResponder)(request);
        var text = new StringBuilder();
        var emitted = 0;

        foreach (var token in tokens)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new GenerationResult(text.ToString(), GenerationStatus.Cancelled, "fake: cancelled before token");
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
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new GenerationResult(text.ToString(), GenerationStatus.Cancelled, "fake: cancelled during delay");
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

    /// <summary>Produces the token stream for a request. Defaults to <see cref="FakeBackend.DefaultResponder"/>.</summary>
    public Func<FakeGenerationRequest, IEnumerable<string>>? Responder { get; set; }

    /// <summary>Delay before each token; lets tests exercise cancellation and TTFT paths.</summary>
    public TimeSpan TokenDelay { get; set; }

    /// <summary>Extra delay before the first token only (simulates prompt processing / slow TTFT).</summary>
    public TimeSpan FirstTokenDelay { get; set; }

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
