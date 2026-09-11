namespace NpuBridge.Tokenizers;

/// <summary>
/// How a backend counts tokens: the number behind <c>usage</c> and the <c>max_tokens</c> budget (D80).
/// The preflight still decides what fits; a counter only counts. <see cref="Phi3TokenCounter"/> is the
/// Phi Silica runtime's own vocabulary; <see cref="CharEstimateTokenCounter"/> is the chars/4 estimate
/// for a backend whose tokenizer is unpublished (Aion) and the fake's default.
/// </summary>
public interface ITokenCounter
{
    /// <summary>Short name for logs and <c>/debug/tokenize</c>: <c>phi-3</c> or <c>chars/4</c>.</summary>
    string Name { get; }

    /// <summary>
    /// True when appending text can never change how an earlier prefix is counted, so a streamed cut
    /// may release text right up to the budget. False for a BPE tokenizer, where a merge can run into
    /// text that arrives later; the cutter then holds the trailing partial word back near the budget.
    /// </summary>
    bool PrefixStable { get; }

    /// <summary>Tokens in <paramref name="text"/> as the model would see it, without framing tokens such as BOS.</summary>
    int Count(string text);

    /// <summary>
    /// The char index at which the first <paramref name="tokens"/> tokens of <paramref name="text"/> end,
    /// in <paramref name="text"/>'s own tokenization, and the text's total token count. The whole length
    /// when the text has no more tokens than that, 0 for a non-positive budget. The index is the raw
    /// token boundary: it may fall between the halves of a surrogate pair, which the caller that
    /// slices must step back from (D58); comparing positions uses the raw value.
    /// </summary>
    int IndexAtTokenCount(string text, int tokens, out int totalTokens);

    /// <summary>The index alone; see the three-argument form.</summary>
    int IndexAtTokenCount(string text, int tokens) => IndexAtTokenCount(text, tokens, out _);

    /// <summary>
    /// How many of <paramref name="text"/>'s tokens it takes to cover its first
    /// <paramref name="prefixChars"/> characters: the tokens the model produced to reach that point,
    /// counting a token the prefix ends inside of. This is <c>usage.completion_tokens</c> for a reply
    /// cut at <paramref name="prefixChars"/>: a prefix counted on its own can tokenize differently
    /// (<c>international</c> is one token, its stop-truncated <c>internation</c> two), and the budget
    /// is about what was generated, not about how the remainder would tokenize alone.
    /// </summary>
    int TokensCovering(string text, int prefixChars);
}
