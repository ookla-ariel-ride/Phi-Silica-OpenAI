using System.Globalization;
using NpuBridge.Backends;

namespace NpuBridge.Api;

/// <summary>Refuses native system text that would make creating a model context unsafe or useless.</summary>
public static class SystemTextGuard
{
    // D94 / issue #29: CreateContext crashes the Windows model host above the measured boundary.
    public const int NativeSystemTextCharacterCeiling = 32_000;

    /// <summary>
    /// Returns the normal prompt-overflow failure before a native system text reaches
    /// <see cref="ILanguageModelBackend.CreateContext"/>, or <c>null</c> when it is safe to create.
    /// </summary>
    internal static GenerationFailure? RefusalFor(
        ILanguageModelBackend backend,
        string? nativeSystemText,
        bool includesToolDefinitions,
        int? nativeSystemTokens = null) =>
        Evaluate(backend, nativeSystemText, includesToolDefinitions, nativeSystemTokens, countForUsage: false).Failure;

    /// <summary>
    /// Checks native system text before preparation computes its usage count. The character ceiling is
    /// decisive before tokenization; a non-refused native system text is counted exactly once for later
    /// conversation usage calculations.
    /// </summary>
    internal static (GenerationFailure? Failure, int NativeSystemTokens) RefusalForPreparation(
        ILanguageModelBackend backend,
        string? nativeSystemText,
        bool includesToolDefinitions) =>
        Evaluate(backend, nativeSystemText, includesToolDefinitions, nativeSystemTokens: null, countForUsage: true);

    private static (GenerationFailure? Failure, int NativeSystemTokens) Evaluate(
        ILanguageModelBackend backend,
        string? nativeSystemText,
        bool includesToolDefinitions,
        int? nativeSystemTokens,
        bool countForUsage)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (nativeSystemText is null)
        {
            return (null, 0);
        }

        if (nativeSystemText.Length > NativeSystemTextCharacterCeiling)
        {
            return (Overflow(
                string.Create(CultureInfo.InvariantCulture,
                    $"Native system text alone exceeds the {NativeSystemTextCharacterCeiling:N0}-character safety ceiling: {nativeSystemText.Length:N0} characters."),
                includesToolDefinitions), 0);
        }

        var windowTokens = backend.ContextWindowTokens;
        var systemTokens = nativeSystemTokens;
        if (windowTokens is not null || countForUsage)
        {
            systemTokens ??= backend.TokenCounter.Count(nativeSystemText);
        }

        if (windowTokens is { } usableWindowTokens && systemTokens is { } countedTokens && countedTokens >= usableWindowTokens)
        {
            return (Overflow(
                string.Create(CultureInfo.InvariantCulture,
                    $"Native system text alone exceeds the context window: {countedTokens:N0} tokens fills the {usableWindowTokens:N0}-token usable window."),
                includesToolDefinitions), countedTokens);
        }

        return (null, systemTokens ?? 0);
    }

    private static GenerationFailure Overflow(string detail, bool includesToolDefinitions)
    {
        if (includesToolDefinitions)
        {
            detail += " Rendered tool definitions are included in that count.";
        }

        return GenerationFailure.ContextLengthExceeded(detail);
    }
}
