using NpuBridge.Tokenizers;

namespace NpuBridge.Tests;

/// <summary>
/// The two <see cref="ITokenCounter"/> implementations. <see cref="CharEstimateTokenCounter"/> is the
/// chars/4 estimate every backend used before D80 and Aion still does; <see cref="Phi3TokenCounter"/>
/// is Phi-3.5-mini's tokenizer over the vendored <c>tokenizer.model</c>, which the D80 measurement
/// showed to be the Phi Silica runtime's own (3581 tokens at every ASCII preflight boundary).
/// </summary>
public class TokenCounterTests
{
    public const string FoxSentence = "The quick brown fox jumps over the lazy dog. ";

    /// <summary>D55's over-length prompt: 5000 fox sentences and a request, 225,042 characters.</summary>
    public static string FoxFiller() => string.Concat(Enumerable.Repeat(FoxSentence, 5000)) + "\nSummarize the text above in one sentence.";

    // ---- chars/4 ----

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("12345678", 2)]
    public void Char_estimate_counts_ceil_of_chars_over_four(string text, int expected) =>
        Assert.Equal(expected, CharEstimateTokenCounter.Instance.Count(text));

    [Theory]
    [InlineData("abcdefghij", 2, 8)]
    [InlineData("abcdefghij", 3, 10)]
    [InlineData("abcdefghij", 0, 0)]
    [InlineData("", 5, 0)]
    public void Char_estimate_index_is_four_chars_per_token_capped_at_the_length(string text, int tokens, int expected) =>
        Assert.Equal(expected, CharEstimateTokenCounter.Instance.IndexAtTokenCount(text, tokens));

    [Fact]
    public void Char_estimate_index_never_splits_a_surrogate_pair()
    {
        // Four chars per token lands between the halves of the emoji at index 4; step back to 3.
        Assert.Equal(3, CharEstimateTokenCounter.Instance.IndexAtTokenCount("abc\U0001F600z", 1));
    }

    [Fact]
    public void Char_estimate_saturates_on_an_absurd_cap()
    {
        Assert.Equal(3, CharEstimateTokenCounter.Instance.IndexAtTokenCount("abc", int.MaxValue));
    }

    [Fact]
    public void Char_estimate_is_prefix_stable_and_named()
    {
        Assert.True(CharEstimateTokenCounter.Instance.PrefixStable);
        Assert.Equal("chars/4", CharEstimateTokenCounter.Instance.Name);
    }

    // ---- Phi-3 ----

    [Theory]
    [InlineData("", 0)]
    [InlineData("Hello world", 2)]
    [InlineData(FoxSentence, 13)]
    [InlineData("1234567890 ", 12)] // every digit is its own token
    [InlineData("{\"a\":\"b\",\"c\":\"d\"},", 9)]
    [InlineData(" leading space", 3)]
    [InlineData("a\nb", 3)]
    [InlineData("机器学习是人工智能的一个分支。", 18)]
    [InlineData("Hello \U0001F600 world", 7)]
    [InlineData("### Conversation so far\n", 7)]
    public void Phi3_counts_match_the_reference_tokenizer(string text, int expected) =>
        Assert.Equal(expected, Phi3TokenCounter.Instance.Count(text));

    [Fact]
    public void Phi3_does_not_count_a_beginning_of_sentence_token()
    {
        // With BOS "Hello world" would be 3. usage counts what the caller sends, not the runtime's framing.
        Assert.Equal(2, Phi3TokenCounter.Instance.Count("Hello world"));
    }

    /// <summary>
    /// The D55/D80 boundary: the runtime said 13,429 characters of the fox filler fit, and that prefix
    /// is 3581 Phi-3 tokens while one more character is 3582. This is the measurement the adoption
    /// rests on, pinned so a tokenizer or model-file change cannot drift from the runtime unnoticed.
    /// </summary>
    [Fact]
    public void Phi3_counts_the_measured_fox_boundary()
    {
        var filler = FoxFiller();
        Assert.Equal(3581, Phi3TokenCounter.Instance.Count(filler[..13429]));
        Assert.Equal(3582, Phi3TokenCounter.Instance.Count(filler[..13430]));
    }

    [Fact]
    public void Phi3_index_at_the_window_is_the_measured_boundary()
    {
        Assert.Equal(13429, Phi3TokenCounter.Instance.IndexAtTokenCount(FoxFiller(), 3581));
    }

    [Theory]
    [InlineData("Hello world", 1, 5)]   // "Hello" | " world"
    [InlineData("Hello world", 2, 11)]
    [InlineData("Hello world", 50, 11)] // more tokens than the text has: the whole text
    [InlineData("Hello world", 0, 0)]
    [InlineData("", 3, 0)]
    public void Phi3_index_is_where_that_many_tokens_end(string text, int tokens, int expected) =>
        Assert.Equal(expected, Phi3TokenCounter.Instance.IndexAtTokenCount(text, tokens));

    [Fact]
    public void Phi3_index_never_splits_a_surrogate_pair()
    {
        // The emoji is several byte-fallback tokens; a cut inside them must fall before the pair.
        var text = "Hi \U0001F600 there";
        for (var tokens = 1; tokens < Phi3TokenCounter.Instance.Count(text); tokens++)
        {
            var index = Phi3TokenCounter.Instance.IndexAtTokenCount(text, tokens);
            Assert.False(index > 0 && index < text.Length && char.IsLowSurrogate(text[index]), $"split at {index} for {tokens} tokens");
        }
    }

    [Fact]
    public void Phi3_is_not_prefix_stable_and_named()
    {
        Assert.False(Phi3TokenCounter.Instance.PrefixStable);
        Assert.Equal("phi-3", Phi3TokenCounter.Instance.Name);
    }

    [Fact]
    public void Phi3_counts_are_the_same_from_many_threads()
    {
        var counter = Phi3TokenCounter.Instance;
        var texts = new[] { FoxSentence, "Hello world", "1234567890 ", "{\"a\":\"b\",\"c\":\"d\"}," };
        var expected = texts.Select(counter.Count).ToArray();
        Parallel.For(0, 200, i =>
        {
            var text = texts[i % texts.Length];
            Assert.Equal(expected[i % texts.Length], counter.Count(text));
        });
    }
}
