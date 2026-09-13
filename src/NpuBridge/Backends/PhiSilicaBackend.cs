using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;
using Microsoft.Windows.ApplicationModel.DynamicDependency;
using NpuBridge.Backends;
using NpuBridge.Configuration;
using NpuBridge.Hosting;
using Windows.ApplicationModel;

namespace NpuBridge.PhiSilica;

/// <summary>
/// Phi Silica through the Windows App SDK (<c>Microsoft.Windows.AI.Text</c>). Thin by design: identity
/// check, runtime bootstrap, optional LAF unlock, ready-state, then the four calls the interface needs.
/// Everything that can be reasoned about without an NPU lives in <c>NpuBridge.Core</c>.
/// </summary>
internal sealed class PhiSilicaBackend : ILanguageModelBackend
{
    public const string FeatureId = "com.microsoft.windows.ai.languagemodel";
    // Measured by the D80 smoke boundary check; see issue #29 and D97 for the native-system guard.
    private const int UsableContextWindowTokens = 3581;
    private const uint AccessDenied = 0x80070005;

    private readonly BridgeOptions _options;
    private readonly IProcessIdentity _identity;
    private readonly ILogger<PhiSilicaBackend> _logger;
    private readonly ConcurrentDictionary<string, object?> _diagnostics = new();
    private LanguageModel? _model;
    private bool _bootstrapped;
    private bool _disposed;
    private string _lafHint = string.Empty;

    // The two ways the runtime can break the text contract (ILanguageModelBackend: Text is the deltas
    // delivered, concatenated). Neither is silent: each is a Warning and a counter in /healthz, so the
    // smoke test can assert both stayed at zero and a session that saw one can find it afterwards.
    private int _textMismatches;
    private int _lateDeltas;

    public PhiSilicaBackend(BridgeOptions options, IProcessIdentity identity, ILogger<PhiSilicaBackend> logger)
    {
        _options = options;
        _identity = identity;
        _logger = logger;
        _diagnostics["sdk"] =
            $"Microsoft.WindowsAppSDK {Microsoft.WindowsAppSDK.Release.Major}.{Microsoft.WindowsAppSDK.Release.Minor}.{Microsoft.WindowsAppSDK.Release.Patch}{Microsoft.WindowsAppSDK.Release.FormattedVersionTag}";
        _diagnostics["text_mismatches"] = 0;
        _diagnostics["late_deltas"] = 0;
    }

    public string ModelId => "phi-silica";

    public string DisplayName => "Phi Silica";

    public BackendCapabilities Capabilities =>
        BackendCapabilities.SamplingOptions
        | BackendCapabilities.SystemPromptContext
        | BackendCapabilities.PromptLengthPreflight
        | BackendCapabilities.Cancellation;

    /// <summary>
    /// Phi-3.5-mini's tokenizer: measured to be this runtime's own vocabulary (the preflight lands on
    /// 3581 of its tokens at every ASCII boundary tested, D80). When Aion Instruct arrives as a model
    /// swap behind this API, re-run the D80 measurement before trusting it for that model.
    /// </summary>
    public NpuBridge.Tokenizers.ITokenCounter TokenCounter => NpuBridge.Tokenizers.Phi3TokenCounter.Instance;

    public int? ContextWindowTokens => UsableContextWindowTokens;

    public IReadOnlyDictionary<string, object?> Diagnostics => _diagnostics;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_identity.HasPackageIdentity)
        {
            throw new BackendUnavailableException(
                "Phi Silica requires package identity, and this process has none. Register the sparse package " +
                "(scripts\\identity.ps1 -Install) and start npu-bridge through package activation; with " +
                "--self-relaunch on (default) that happens automatically.");
        }

        _diagnostics["package_family_name"] = _identity.PackageFamilyName;

        // With identity and a manifest PackageDependency on the runtime this is a no-op by design; without
        // identity it would wire the framework package into the process graph. Either way it must succeed.
        if (Bootstrap.TryInitialize(
                Microsoft.WindowsAppSDK.Release.MajorMinor,
                Microsoft.WindowsAppSDK.Release.VersionTag,
                new Microsoft.Windows.ApplicationModel.DynamicDependency.PackageVersion(Microsoft.WindowsAppSDK.Runtime.Version.UInt64),
                Bootstrap.InitializeOptions.OnPackageIdentity_NOOP,
                out var hresult))
        {
            _bootstrapped = true;
            _diagnostics["bootstrap"] = "ok";
        }
        else
        {
            _diagnostics["bootstrap"] = $"0x{hresult:X8}";
            throw new BackendUnavailableException(
                $"Windows App Runtime bootstrap failed (HRESULT 0x{hresult:X8}). Re-run scripts\\identity.ps1 -Install, which installs the runtime the manifest depends on.");
        }

        // LAF status is recorded and logged but not fatal: whether the model API actually needs the token
        // depends on the SDK channel and Windows build, so let the API call be the judge.
        _lafHint = UnlockLimitedAccessFeature();

        var readyState = Guarded(() => LanguageModel.GetReadyState(), "GetReadyState");
        _diagnostics["ready_state"] = readyState.ToString();
        switch (readyState)
        {
            case AIFeatureReadyState.NotSupportedOnCurrentSystem:
            case AIFeatureReadyState.NotCompatibleWithSystemHardware:
                throw new BackendUnavailableException($"Phi Silica is not supported on this device ({readyState}): no supported NPU/GPU.");
            case AIFeatureReadyState.OSUpdateNeeded:
                throw new BackendUnavailableException("Phi Silica needs a newer Windows build (OSUpdateNeeded). Run Windows Update.");
            case AIFeatureReadyState.DisabledByUser:
                throw new BackendUnavailableException("Phi Silica is disabled in Windows Settings > System > AI components. Enable it and restart npu-bridge.");
            case AIFeatureReadyState.CapabilityMissing:
                throw new BackendUnavailableException(
                    "The package this process runs under does not declare the systemAIModels capability (CapabilityMissing). " +
                    "Re-register with scripts\\identity.ps1 -Install; the manifest in packaging/AppxManifest.xml declares it.");
            case AIFeatureReadyState.NotReady:
                if (!_options.InstallModel)
                {
                    // Microsoft's guidance: never start the multi-GB model download without the user's consent.
                    throw new BackendUnavailableException(
                        "The Phi Silica model is not installed on this device (NotReady). Install it from Settings > System > AI components, " +
                        "or start npu-bridge once with --install-model to let Windows Update download it (several GB).");
                }

                _logger.LogWarning("Phi Silica model is not installed; --install-model is set, asking Windows to download it (this can take minutes).");
                var ensure = await Guarded(() => LanguageModel.EnsureReadyAsync().AsTask(cancellationToken), "EnsureReadyAsync").ConfigureAwait(false);
                _diagnostics["ensure_ready"] = ensure.Status.ToString();
                if (ensure.Status != AIFeatureReadyResultState.Success)
                {
                    throw new BackendUnavailableException(
                        $"Phi Silica model installation did not succeed ({ensure.Status}): {ensure.ErrorDisplayText} {ensure.ExtendedError?.Message}".Trim());
                }

                break;
        }

        var stopwatch = Stopwatch.StartNew();
        _model = await Guarded(() => LanguageModel.CreateAsync().AsTask(cancellationToken), "CreateAsync").ConfigureAwait(false);
        _diagnostics["create_ms"] = stopwatch.ElapsedMilliseconds;

        // The Phi-3 counter parses its 500 KB model on first use (D80). Pay that here, while the backend
        // is still loading, rather than on the first request after /healthz says ready.
        stopwatch.Restart();
        _ = TokenCounter.Count("warm");
        _diagnostics["tokenizer_load_ms"] = stopwatch.ElapsedMilliseconds;
    }

    public IModelContext CreateContext(string? systemPrompt)
    {
        // Defence in depth for direct hardware callers: the Core wire guard cannot protect an init probe
        // or adapter harness that bypasses it, and this call can fail-fast WorkloadsSessionHost.exe above
        // the measured ceiling. This cannot be unit-tested without the Phi Silica runtime and NPU.
        if (systemPrompt?.Length > BackendLimits.NativeSystemTextCharacterCeiling)
        {
            throw new ArgumentException(
                $"Native system text exceeds the {BackendLimits.NativeSystemTextCharacterCeiling}-character safety ceiling.",
                nameof(systemPrompt));
        }

        var model = Model();
        var context = Guarded(() => systemPrompt is null ? model.CreateContext() : model.CreateContext(systemPrompt), "CreateContext");
        return new PhiSilicaContext(context);
    }

    public int? GetUsablePromptLength(IModelContext context, string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var model = Model();
        var ctx = Own(context).Context;
        // The runtime answers in UTF-8 bytes, not UTF-16 chars: identical for ASCII, and up to three
        // times too large for CJK if read as a char index (measured against the Phi-3 tokenizer, D80).
        // The conversion rounds down to a character boundary and never splits a surrogate pair, so
        // callers can slice the prompt at the answer.
        var bytes = Guarded(() => model.GetUsablePromptLength(ctx, prompt), "GetUsablePromptLength");
        return Utf8Offsets.CharIndexAtByteOffset(prompt, bytes > long.MaxValue ? long.MaxValue : (long)bytes);
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

        // SDK 2.4 has no context overload without options; defaults come from a fresh LanguageModelOptions.
        var op = Guarded(() => model.GenerateResponseAsync(ctx, prompt, ToOptions(sampling)), "GenerateResponseAsync");

        // The deltas are accumulated, delivered under the append lock and drained after the operation
        // ends by the accumulator both adapters share (the contract: Text is the deltas delivered,
        // concatenated; the two response shapes cut the same characters only because of it, D65).
        var deltas = new DeltaAccumulator(
            _logger,
            "Phi Silica",
            onDelta,
            onLateDelta: () => Count("late_deltas", ref _lateDeltas),
            onTextMismatch: () => Count("text_mismatches", ref _textMismatches),
            cancellationToken);
        op.Progress = (_, delta) => deltas.OnProgress(delta);

        // The drain is the callback barrier the pipeline relies on before it disposes the context
        // (D51: cancel, drain, dispose), so it runs on every exit, a thrown exception included.
        LanguageModelResponseResult? result = null;
        try
        {
            result = await op.AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Handled below, after the drain, so the returned text includes a straggling delta.
        }
        catch (Exception ex) when ((uint)ex.HResult == AccessDenied)
        {
            throw new BackendUnavailableException($"Phi Silica refused access (E_ACCESSDENIED) during generation. {_lafHint}", ex);
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

        var status = MapStatus(result.Status);
        if (status == GenerationStatus.ContentFiltered)
        {
            // Never hand back text the runtime withheld, whatever the deltas said.
            return new GenerationResult(string.Empty, status, DescribeStatus(result));
        }

        // The delivered deltas are the answer, on every status (D65); the runtime's own Text is a
        // cross-check the accumulator counts a disagreement on. Not observed on hardware; measured by
        // the smoke test's text-contract step.
        return new GenerationResult(deltas.Reconcile(result.Text), status, DescribeStatus(result));
    }

    /// <summary>
    /// Increments a counter and publishes it to <see cref="Diagnostics"/> as one step. Two concurrent
    /// generations (nothing queues them until chunk 8) could otherwise increment to 1 and 2 and publish
    /// in the opposite order, leaving <c>/healthz</c> a step behind until the next event.
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
        if (_bootstrapped)
        {
            Bootstrap.Shutdown();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Attempts the LAF unlock; returns a hint for error messages when it did not succeed.</summary>
    private string UnlockLimitedAccessFeature()
    {
        var token = _options.LafToken ?? string.Empty;
        var attestation = _options.LafAttestation ?? string.Empty;
        string status;
        try
        {
            var result = LimitedAccessFeatures.TryUnlockFeature(FeatureId, token, attestation);
            status = result.Status.ToString();
            _diagnostics["laf_status"] = status;
            if (result.Status is LimitedAccessFeatureStatus.Available or LimitedAccessFeatureStatus.AvailableWithoutToken)
            {
                _logger.LogInformation("Limited Access Feature {Feature}: {Status}.", FeatureId, status);
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            status = $"exception: {ex.Message}";
            _diagnostics["laf_status"] = status;
        }

        var hint = string.IsNullOrEmpty(token)
            ? $"Limited Access Feature status is {status} and no LAF token is configured. Request one for Package Family Name " +
              $"{_identity.PackageFamilyName} and put it in appsettings.local.json (LafToken, LafAttestation)."
            : $"Limited Access Feature status is {status}; the configured token was not accepted. Check it was issued for {_identity.PackageFamilyName}.";
        _logger.LogWarning("{Hint} Continuing to see whether the model API accepts the call anyway.", hint);
        return hint;
    }

    /// <summary>Runs a runtime call, translating E_ACCESSDENIED into a message that names the PFN to request a token for.</summary>
    private T Guarded<T>(Func<T> call, string operation)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when ((uint)ex.HResult == AccessDenied)
        {
            throw new BackendUnavailableException($"Phi Silica refused access (E_ACCESSDENIED) at {operation}. {_lafHint}", ex);
        }
    }

    private async Task<T> Guarded<T>(Func<Task<T>> call, string operation)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when ((uint)ex.HResult == AccessDenied)
        {
            throw new BackendUnavailableException($"Phi Silica refused access (E_ACCESSDENIED) at {operation}. {_lafHint}", ex);
        }
    }

    private static LanguageModelOptions ToOptions(SamplingOptions? sampling)
    {
        var options = new LanguageModelOptions();
        if (sampling is null)
        {
            return options;
        }

        if (sampling.Temperature is { } t)
        {
            options.Temperature = Math.Clamp(t, 0f, 2f);
        }

        if (sampling.TopP is { } p)
        {
            options.TopP = Math.Clamp(p, 0f, 1f);
        }

        if (sampling.TopK is { } k)
        {
            options.TopK = (uint)Math.Clamp(k, 0, 32064);
        }

        return options;
    }

    private static GenerationStatus MapStatus(LanguageModelResponseStatus status) => status switch
    {
        LanguageModelResponseStatus.Complete => GenerationStatus.Complete,
        LanguageModelResponseStatus.PromptLargerThanContext => GenerationStatus.PromptLargerThanContext,
        LanguageModelResponseStatus.PromptBlockedByContentModeration => GenerationStatus.ContentFiltered,
        LanguageModelResponseStatus.ResponseBlockedByContentModeration => GenerationStatus.ContentFiltered,
        LanguageModelResponseStatus.BlockedByPolicy => GenerationStatus.BlockedByPolicy,
        _ => GenerationStatus.Error,
    };

    private static string DescribeStatus(LanguageModelResponseResult result)
    {
        var detail = result.Status.ToString();
        try
        {
            if (result.ExtendedError is { } error)
            {
                detail += $": {error.Message}";
            }
        }
        catch (Exception)
        {
            // ExtendedError is documented but not guaranteed to be populated; never let diagnostics throw.
        }

        return detail;
    }

    private LanguageModel Model()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _model ?? throw new InvalidOperationException("Phi Silica model is not loaded; InitializeAsync has not completed.");
    }

    private static PhiSilicaContext Own(IModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context is not PhiSilicaContext own)
        {
            throw new ArgumentException("Context was not created by the Phi Silica backend.", nameof(context));
        }

        own.ThrowIfDisposed();
        return own;
    }

    private sealed class PhiSilicaContext : IModelContext
    {
        private static int _next;
        private bool _disposed;

        public PhiSilicaContext(LanguageModelContext context)
        {
            Context = context;
            Id = $"phi-ctx-{Interlocked.Increment(ref _next)}";
        }

        public string Id { get; }

        public LanguageModelContext Context { get; }

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
