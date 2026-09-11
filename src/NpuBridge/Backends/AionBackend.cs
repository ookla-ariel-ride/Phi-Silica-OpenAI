using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using Aion = AionInstructPreview.Text;

namespace NpuBridge.AionInstruct;

/// <summary>
/// Aion Instruct Preview through <c>AionInstructPreview.Text</c>, the framework package from the sample
/// repo's release. Thin by design, and thinner than Phi Silica's: the API is a strict subset (PLAN §1 F2)
/// with no options, no system-prompt context, no preflight, no ready-state and no LAF, so the adapter is
/// a dynamic dependency on the framework, <c>CreateAsync</c>, and the four calls the interface needs.
/// The pipeline learns what is missing from <see cref="Capabilities"/>, never from the backend type.
/// </summary>
internal sealed class AionBackend : ILanguageModelBackend
{
    /// <summary>The framework MSIX from the sample repo's release; installed with <c>Add-AppxPackage</c>, user scope.</summary>
    public const string FrameworkFamilyName = "Microsoft.AionInstructPreview.Framework.1.0_8wekyb3d8bbwe";

    /// <summary>The WinML stack the model runs on. The SDK's own manifest injection pins this floor for packaged consumers.</summary>
    public const string WindowsAppRuntime18FamilyName = "Microsoft.WindowsAppRuntime.1.8_8wekyb3d8bbwe";

    private static readonly ulong WindowsAppRuntime18MinVersion = PackageDependency.Version(8000, 836, 2153, 0);

    private readonly ILogger<AionBackend> _logger;
    private readonly ConcurrentDictionary<string, object?> _diagnostics = new();
    private Aion.LanguageModel? _model;
    private bool _disposed;

    // The two ways the runtime can break the text contract (ILanguageModelBackend: Text is the deltas
    // delivered, concatenated). Each is a Warning and a counter in /healthz, so the smoke test's
    // text-contract step can assert both stayed at zero (D65).
    private int _textMismatches;
    private int _lateDeltas;

    public AionBackend(ILogger<AionBackend> logger)
    {
        _logger = logger;
        _diagnostics["sdk"] = "AionInstructPreview.Text.Framework 1.0.0";
        _diagnostics["text_mismatches"] = 0;
        _diagnostics["late_deltas"] = 0;
    }

    public string ModelId => "aion-instruct";

    public string DisplayName => "Aion Instruct Preview";

    /// <summary>
    /// No <see cref="BackendCapabilities.SamplingOptions"/> (no <c>LanguageModelOptions</c> type), no
    /// <see cref="BackendCapabilities.SystemPromptContext"/> (<c>CreateContext()</c> only), no
    /// <see cref="BackendCapabilities.PromptLengthPreflight"/> (no <c>GetUsablePromptLength</c>).
    /// <see cref="BackendCapabilities.Cancellation"/> is not advertised until the smoke test's cut
    /// measurement shows an early cut ending in a fraction of a late one on this runtime; that
    /// measurement is blocked on the machine, not the code (D68, docs/FUTURE.md chunk 6).
    /// </summary>
    public BackendCapabilities Capabilities => BackendCapabilities.None;

    /// <summary>The preview model's tokenizer is unpublished and no generation has run here to measure it against: chars/4 (D44, D80).</summary>
    public NpuBridge.Tokenizers.ITokenCounter TokenCounter => NpuBridge.Tokenizers.CharEstimateTokenCounter.Instance;

    public IReadOnlyDictionary<string, object?> Diagnostics => _diagnostics;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            throw new BackendUnavailableException(
                $"Aion Instruct Preview ships for ARM64 only and this process is {RuntimeInformation.ProcessArchitecture}.");
        }

        // No package identity here, so the framework is reached the way the sample's unpackaged console
        // does it: a process-lifetime dynamic dependency puts the framework's folder on the DLL search
        // path and CsWinRT activates AionInstructPreview.Text.dll registration-free. The WinML stack the
        // model runs on lives in Windows App Runtime 1.8, which the SDK's packaged path also depends on.
        try
        {
            _diagnostics["framework_package"] = PackageDependency.Add(FrameworkFamilyName);
        }
        catch (InvalidOperationException ex)
        {
            throw new BackendUnavailableException(
                $"The Aion Instruct Preview framework package is not installed for this user ({ex.Message}). Download " +
                "AionInstructPreview.LanguageModel.Framework_<ver>_ARM64.msix from " +
                "https://github.com/microsoft/Aion-Instruct-Preview-Sample/releases and run Add-AppxPackage on it " +
                $"(no elevation needed), then check: Get-AppxPackage {FrameworkFamilyName.Split('_')[0]}", ex);
        }

        try
        {
            _diagnostics["windows_app_runtime_1_8"] = PackageDependency.Add(WindowsAppRuntime18FamilyName, WindowsAppRuntime18MinVersion);
        }
        catch (InvalidOperationException ex)
        {
            throw new BackendUnavailableException(
                $"Windows App Runtime 1.8 (8000.836.2153.0 or newer) is not installed for this user ({ex.Message}). " +
                "Install it with: winget install --id Microsoft.WindowsAppRuntime.1.8", ex);
        }

        // The first load on a device compiles the model for the NPU (the sample says three to five
        // minutes); BackendLifecycle reports that as loading with first_run_compile_likely.
        _logger.LogInformation("Loading Aion Instruct Preview (the first load on this device compiles the model for the NPU and can take minutes).");
        var stopwatch = Stopwatch.StartNew();
        _model = await Aion.LanguageModel.CreateAsync().AsTask(cancellationToken).ConfigureAwait(false);
        _diagnostics["create_ms"] = stopwatch.ElapsedMilliseconds;
    }

    /// <summary>Aion has no system-prompt context; the caller folds the system text into the prompt (the argument is ignored).</summary>
    public IModelContext CreateContext(string? systemPrompt)
    {
        var model = Model();
        return new AionContext(model.CreateContext());
    }

    /// <summary>No preflight on this runtime: overflow is only learned from a failed generation (D67).</summary>
    public int? GetUsablePromptLength(IModelContext context, string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        Own(context);
        return null;
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
        var model = Model();
        var ctx = Own(context).Context;

        // Sampling is dropped by preparation before it gets here (the capability is absent); if a caller
        // passes it anyway there is nothing to hand the runtime, which has no options type at all.
        var op = model.GenerateResponseAsync(ctx, prompt);

        var deltas = new DeltaAccumulator(
            _logger,
            "Aion Instruct",
            onDelta,
            onLateDelta: () => Count("late_deltas", ref _lateDeltas),
            onTextMismatch: () => Count("text_mismatches", ref _textMismatches),
            cancellationToken);
        op.Progress = (_, delta) => deltas.OnProgress(delta);

        // The drain is the callback barrier the pipeline relies on before it disposes the context
        // (D51: cancel, drain, dispose), so it runs on every exit, a thrown exception included.
        Aion.LanguageModelResponseResult? result = null;
        try
        {
            result = await op.AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Handled below, after the drain, so the returned text includes a straggling delta.
        }
        finally
        {
            deltas.Drain();
        }

        if (result is null)
        {
            return new GenerationResult(deltas.Text, GenerationStatus.Cancelled, "cancelled");
        }

        if (deltas.DeltaFailure is { } failure)
        {
            return new GenerationResult(deltas.Text, GenerationStatus.Error, $"delta callback threw: {failure.GetType().Name}: {failure.Message}");
        }

        // A runtime that honours Cancel() by finishing early still reports Complete; the caller asked to stop.
        if (cancellationToken.IsCancellationRequested)
        {
            return new GenerationResult(deltas.Text, GenerationStatus.Cancelled, "cancelled (runtime finished early)");
        }

        return new GenerationResult(deltas.Reconcile(result.Text), MapStatus(result.Status), result.Status.ToString());
    }

    /// <summary>
    /// Increments a counter and publishes it to <see cref="Diagnostics"/> as one step, so two concurrent
    /// generations cannot publish 1 and 2 in the opposite order.
    /// </summary>
    private void Count(string key, ref int counter)
    {
        lock (_diagnostics)
        {
            _diagnostics[key] = ++counter;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _model?.Dispose();
        _model = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// By name, never by value: Aion's enum has four members with <c>Error = 2</c>, Phi Silica's has ten
    /// with <c>Error = 6</c>. <c>InProgress</c> is not a terminal status; a result carrying it is a fault.
    /// Aion has no content-moderation statuses, so <see cref="GenerationStatus.ContentFiltered"/> is unreachable here.
    /// </summary>
    private static GenerationStatus MapStatus(Aion.LanguageModelResponseStatus status) => status switch
    {
        Aion.LanguageModelResponseStatus.Complete => GenerationStatus.Complete,
        Aion.LanguageModelResponseStatus.PromptLargerThanContext => GenerationStatus.PromptLargerThanContext,
        _ => GenerationStatus.Error,
    };

    private Aion.LanguageModel Model()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _model ?? throw new InvalidOperationException("Aion Instruct Preview model is not loaded; InitializeAsync has not completed.");
    }

    private static AionContext Own(IModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context is not AionContext own)
        {
            throw new ArgumentException("Context was not created by the Aion Instruct backend.", nameof(context));
        }

        own.ThrowIfDisposed();
        return own;
    }

    private sealed class AionContext : IModelContext
    {
        private static int _next;
        private bool _disposed;

        public AionContext(Aion.LanguageModelContext context)
        {
            Context = context;
            Id = $"aion-ctx-{Interlocked.Increment(ref _next)}";
        }

        public string Id { get; }

        public Aion.LanguageModelContext Context { get; }

        public void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Context.Dispose();
        }
    }
}
