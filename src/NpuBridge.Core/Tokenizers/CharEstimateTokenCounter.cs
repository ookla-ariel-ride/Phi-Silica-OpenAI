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

    public int IndexAtTokenCount(string text, int tokens) => IndexAtTokenCount(text, tokens, out _);

    public int IndexAtTokenCount(string text, int tokens, out int totalTokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        totalTokens = Count(text);
        if (tokens <= 0)
        {
            return 0;
        }

        // tokens * 4 overflows int past ~536 million; an absurd budget is the whole text.
        return (int)Math.Min((long)tokens * CharsPerToken, text.Length);
    }

    /// <summary>On a four-character grid the tokens covering a prefix are its own estimate: <c>ceil(chars/4)</c>, as D44 always reported.</summary>
    public int TokensCovering(string text, int prefixChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chars = Math.Clamp(prefixChars, 0, text.Length);
        return (chars + CharsPerToken - 1) / CharsPerToken;
    }
}
