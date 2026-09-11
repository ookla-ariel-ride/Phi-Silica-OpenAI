using Microsoft.ML.Tokenizers;

namespace NpuBridge.Tokenizers;

/// <summary>
/// Phi-3.5-mini's tokenizer over the vendored <c>tokenizer.model</c> (<c>Tokenizers/Phi3/README.md</c>).
/// Measured to be the Phi Silica runtime's own vocabulary: the prompt-length preflight lands on 3581
/// tokens of this tokenizer at every ASCII boundary tested and within 1 % on punctuation-dense text
/// (D80). Counts carry no BOS: <c>usage</c> reports what the caller sent, not the runtime's framing.
/// Loaded once per process, on first use; the tokenizer is safe to share between requests.
/// </summary>
public sealed class Phi3TokenCounter : ITokenCounter
{
    private const string ResourceName = "NpuBridge.Tokenizers.Phi3.tokenizer.model";

    private static readonly Lazy<Phi3TokenCounter> Shared = new(() => new Phi3TokenCounter());

    private readonly Tokenizer _tokenizer;

    private Phi3TokenCounter()
    {
        using var model = typeof(Phi3TokenCounter).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing from NpuBridge.Core.");
        _tokenizer = LlamaTokenizer.Create(model, addBeginOfSentence: false, addEndOfSentence: false);
    }

    public static Phi3TokenCounter Instance => Shared.Value;

    public string Name => "phi-3";

    /// <summary>SentencePiece BPE: a merge can reach into text that arrives later within the same word.</summary>
    public bool PrefixStable => false;

    public int Count(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? 0 : _tokenizer.CountTokens(text);
    }

    public int IndexAtTokenCount(string text, int tokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (tokens <= 0 || text.Length == 0)
        {
            return 0;
        }

        // The index refers to the normalized text, which the Llama normalizer lengthens by its dummy
        // prefix (one leading word-boundary marker) and otherwise maps one char to one char, so the
        // length difference is the shift back to the caller's string.
        var index = _tokenizer.GetIndexByTokenCount(text, tokens, out var normalized, out _);
        var shift = (normalized?.Length ?? text.Length) - text.Length;
        index = Math.Clamp(index - shift, 0, text.Length);
        return SurrogatePairs.StepBackIfSplitting(text, index);
    }
}
