namespace NpuBridge.Tokenizers;

/// <summary>
/// Four characters per token, rounded up (D44): the estimate every backend used before D80 and the
/// fallback where the runtime's tokenizer is unpublished. Prefix-stable by construction, so the cut may
/// run right up to the budget of <c>tokens * 4</c> characters, exactly as D53 specified it.
/// </summary>
public sealed class CharEstimateTokenCounter : ITokenCounter
{
    public const int CharsPerToken = 4;

    public static CharEstimateTokenCounter Instance { get; } = new();

    private CharEstimateTokenCounter()
    {
    }

    public string Name => "chars/4";

    public bool PrefixStable => true;

    public int Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text.Length + CharsPerToken - 1) / CharsPerToken;
    }

    public int IndexAtTokenCount(string text, int tokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (tokens <= 0)
        {
            return 0;
        }

        // tokens * 4 overflows int past ~536 million; an absurd budget is the whole text.
        var index = (int)Math.Min((long)tokens * CharsPerToken, text.Length);
        return SurrogatePairs.StepBackIfSplitting(text, index);
    }
}

/// <summary>Shared by every counter: a cut index never falls between the halves of a surrogate pair (D58).</summary>
internal static class SurrogatePairs
{
    public static int StepBackIfSplitting(string text, int index) =>
        index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1])
            ? index - 1
            : index;
}
