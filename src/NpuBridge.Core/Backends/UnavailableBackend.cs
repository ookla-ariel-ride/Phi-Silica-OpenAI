namespace NpuBridge.Backends;

/// <summary>
/// Stand-in for a backend that cannot be constructed on this machine or is not built yet. Initialization
/// fails with the supplied reason so <c>/healthz</c> reports it instead of the process crashing.
/// </summary>
public sealed class UnavailableBackend : ILanguageModelBackend
{
    private readonly string _reason;

    public UnavailableBackend(string modelId, string displayName, string reason)
    {
        ModelId = modelId;
        DisplayName = displayName;
        _reason = reason;
    }

    public string ModelId { get; }

    public string DisplayName { get; }

    public BackendCapabilities Capabilities => BackendCapabilities.None;

    public IReadOnlyDictionary<string, object?> Diagnostics { get; } = new Dictionary<string, object?>();

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Task.FromException(new BackendUnavailableException(_reason));

    public IModelContext CreateContext(string? systemPrompt) => throw new BackendUnavailableException(_reason);

    public int? GetUsablePromptLength(IModelContext context, string prompt) => null;

    public Task<GenerationResult> GenerateAsync(
        IModelContext context,
        string prompt,
        SamplingOptions? sampling,
        Action<string> onDelta,
        CancellationToken cancellationToken) => throw new BackendUnavailableException(_reason);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class BackendUnavailableException : Exception
{
    public BackendUnavailableException(string message) : base(message)
    {
    }

    public BackendUnavailableException()
    {
    }

    public BackendUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
