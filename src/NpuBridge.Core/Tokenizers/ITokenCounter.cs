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
    /// The char index at which the first <paramref name="tokens"/> tokens of <paramref name="text"/> end:
    /// <c>text[..index]</c> counts at most <paramref name="tokens"/>. The whole length when the text has
    /// no more tokens than that, 0 for a non-positive budget, and never between the halves of a
    /// surrogate pair.
    /// </summary>
    int IndexAtTokenCount(string text, int tokens);
}
