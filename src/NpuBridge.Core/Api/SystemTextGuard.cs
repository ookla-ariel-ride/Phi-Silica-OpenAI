using System.Globalization;
using NpuBridge.Backends;

namespace NpuBridge.Api;

/// <summary>Refuses native system text that would make creating a model context unsafe or useless.</summary>
internal static class SystemTextGuard
{
    // D94 / issue #29: CreateContext crashes the Windows model host above the measured boundary.
    internal const int NativeSystemTextCharacterCeiling = 32_000;

    /// <summary>
    /// Returns the normal prompt-overflow failure before a native system text reaches
    /// <see cref="ILanguageModelBackend.CreateContext"/>, or <c>null</c> when it is safe to create.
    /// </summary>
    public static GenerationFailure? RefusalFor(
        ILanguageModelBackend backend,
        string? nativeSystemText,
        bool includesToolDefinitions)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (nativeSystemText is null)
        {
            return null;
        }

        if (nativeSystemText.Length > NativeSystemTextCharacterCeiling)
        {
            return Overflow(
                string.Create(CultureInfo.InvariantCulture,
                    $"Native system text alone exceeds the {NativeSystemTextCharacterCeiling:N0}-character safety ceiling: {nativeSystemText.Length:N0} characters."),
                includesToolDefinitions);
        }

        if (backend.ContextWindowTokens is { } windowTokens)
        {
            var systemTokens = backend.TokenCounter.Count(nativeSystemText);
            if (systemTokens >= windowTokens)
            {
                return Overflow(
                    string.Create(CultureInfo.InvariantCulture,
                        $"Native system text alone exceeds the context window: {systemTokens:N0} tokens fills the {windowTokens:N0}-token usable window."),
                    includesToolDefinitions);
            }
        }

        return null;
    }

    private static GenerationFailure Overflow(string detail, bool includesToolDefinitions)
    {
        if (includesToolDefinitions)
        {
            detail += " Rendered tool definitions are included in that count.";
        }

        return GenerationFailure.FromStatus(
            new GenerationResult(string.Empty, GenerationStatus.PromptLargerThanContext, detail))!;
    }
}
