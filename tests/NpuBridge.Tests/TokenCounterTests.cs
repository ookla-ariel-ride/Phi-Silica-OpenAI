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
    public void Char_estimate_index_is_the_raw_budget_even_inside_a_surrogate_pair()
    {
        // Four chars per token lands between the halves of the emoji at index 4. The counter reports
        // the raw boundary; the cutter steps back before slicing (D58) and compares positions raw, so
        // stop-string precedence is what it was under cap * 4 characters.
        Assert.Equal(4, CharEstimateTokenCounter.Instance.IndexAtTokenCount("abc\U0001F600z", 1));
    }

    [Theory]
    [InlineData("abcdefghij", 0, 0)]
    [InlineData("abcdefghij", 1, 1)]
    [InlineData("abcdefghij", 4, 1)]
    [InlineData("abcdefghij", 5, 2)]
    [InlineData("abcdefghij", 10, 3)]
    [InlineData("abcdefghij", 50, 3)]
    public void Char_estimate_tokens_covering_a_prefix_are_its_own_estimate(string text, int chars, int expected) =>
        Assert.Equal(expected, CharEstimateTokenCounter.Instance.TokensCovering(text, chars));

    [Fact]
    public void Char_estimate_index_reports_the_total_too()
    {
        Assert.Equal(8, CharEstimateTokenCounter.Instance.IndexAtTokenCount("abcdefghij", 2, out var total));
        Assert.Equal(3, total);
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
    public void Phi3_index_reports_the_total_too()
    {
        Assert.Equal(5, Phi3TokenCounter.Instance.IndexAtTokenCount("Hello world, how are you today?", 1, out var total));
        Assert.Equal(8, total);
    }

    /// <summary>
    /// The tokens the model produced to reach a point in its own text, not the count of the prefix on
    /// its own: "international" is one token, while "internation" alone is "intern" + "ation".
    /// </summary>
    [Theory]
    [InlineData("international", 13, 1)]
    [InlineData("international", 11, 1)]
    [InlineData("international", 1, 1)]
    [InlineData("international", 0, 0)]
    [InlineData("Hello world, how are you today?", 12, 3)] // Hello | world | ,
    [InlineData("Hello world, how are you today?", 11, 2)] // ends exactly after "world"
    [InlineData("Hello world, how are you today?", 6, 2)]  // one char into " world"
    [InlineData("Hello world, how are you today?", 31, 8)]
    [InlineData("Hello world, how are you today?", 99, 8)]
    public void Phi3_tokens_covering_a_prefix_count_the_generated_tokens_it_ends_inside(string text, int chars, int expected) =>
        Assert.Equal(expected, Phi3TokenCounter.Instance.TokensCovering(text, chars));

    /// <summary>
    /// A CJK character outside the vocabulary is three byte-fallback tokens sharing its offsets. A budget
    /// that ends inside them stops before the character, and the prefix cut there is covered by no more
    /// tokens than the budget.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(10)]
    public void Phi3_index_inside_a_byte_fallback_character_stops_before_it(int tokens)
    {
        var text = string.Concat(Enumerable.Repeat("机器学习是人工智能的一个分支。", 3));
        var index = Phi3TokenCounter.Instance.IndexAtTokenCount(text, tokens);
        Assert.InRange(Phi3TokenCounter.Instance.TokensCovering(text, index), 0, tokens);
    }

    [Fact]
    public void Phi3_counts_the_stop_truncated_prefix_differently_on_its_own()
    {
        // The reason TokensCovering exists.
        Assert.Equal(1, Phi3TokenCounter.Instance.Count("international"));
        Assert.Equal(2, Phi3TokenCounter.Instance.Count("internation"));
    }

    /// <summary>
    /// The D80 review's counterexample to any fixed lookback: twenty hyphens tokenize as "----" first,
    /// twenty-one as "-" first. Only a whitespace boundary settles a SentencePiece piece.
    /// </summary>
    [Fact]
    public void Phi3_can_retokenize_a_run_from_its_start_when_one_character_is_appended()
    {
        Assert.Equal(4, Phi3TokenCounter.Instance.IndexAtTokenCount(new string('-', 20), 1));
        Assert.Equal(1, Phi3TokenCounter.Instance.IndexAtTokenCount(new string('-', 21), 1));
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
