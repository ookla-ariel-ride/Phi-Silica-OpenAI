using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NpuBridge.Api;
using NpuBridge.Backends.Fake;
using NpuBridge.Tokenizers;

namespace NpuBridge.Tests;

/// <summary>
/// The <c>max_tokens</c> budget measured in the backend's own tokens (D80) instead of <c>cap * 4</c>
/// characters (D53). The whole-text cut takes the first <c>cap</c> tokens; the stream must land on the
/// same text although it releases as it goes, which a BPE tokenizer complicates: a merge can reach into
/// text that has not arrived yet, so near the budget the stream holds the trailing partial word back.
/// The counters here are deliberately simple stand-ins whose behaviour the test can state; the last
/// tests run the real Phi-3 counter through the pipeline.
/// </summary>
public class TokenBudgetCutTests
{
    private const string Path = "/v1/chat/completions";

    /// <summary>
    /// A token per whitespace-delimited word, with the whitespace attached to the word that follows it,
    /// as SentencePiece attaches its word-boundary marker. Declared not prefix-stable so the stream
    /// applies its holdback, although a word counter alone would never need it.
    /// </summary>
    private sealed class WordCounter : WordwiseCounter
    {
        public override string Name => "words";

        protected override IEnumerable<int> PieceLengths(string piece, int wordStart) => [piece.Length];
    }

    /// <summary>
    /// Like <see cref="WordCounter"/> but a word of odd length (three or more) is two tokens, its first
    /// character and the rest. Appending one character to an even word therefore makes it count more,
    /// which is the direction that lets an eager stream overshoot the budget.
    /// </summary>
    private sealed class EvenOddCounter : WordwiseCounter
    {
        public override string Name => "even-odd";

        protected override IEnumerable<int> PieceLengths(string piece, int wordStart)
        {
            var wordLength = piece.Length - wordStart;
            if (wordLength >= 3 && wordLength % 2 == 1)
            {
                yield return wordStart + 1;
                yield return wordLength - 1;
            }
            else
            {
                yield return piece.Length;
            }
        }
    }

    private abstract class WordwiseCounter : ITokenCounter
    {
        public abstract string Name { get; }

        public bool PrefixStable => false;

        /// <param name="piece">Leading whitespace plus one word (or trailing whitespace alone).</param>
        /// <param name="wordStart">Where the word begins inside <paramref name="piece"/>.</param>
        protected abstract IEnumerable<int> PieceLengths(string piece, int wordStart);

        public int Count(string text) => Tokens(text).Count();

        public int IndexAtTokenCount(string text, int tokens)
        {
            if (tokens <= 0)
            {
                return 0;
            }

            var index = 0;
            var taken = 0;
            foreach (var (start, length) in Tokens(text))
            {
                if (taken == tokens)
                {
                    return index;
                }

                index = start + length;
                taken++;
            }

            return text.Length;
        }

        private IEnumerable<(int Start, int Length)> Tokens(string text)
        {
            var i = 0;
            while (i < text.Length)
            {
                var start = i;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                var wordStart = i - start;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                var at = start;
                foreach (var length in PieceLengths(text[start..i], wordStart))
                {
                    yield return (at, length);
                    at += length;
                }
            }
        }
    }

    private static OutputLimits Limits(int? maxTokens, ITokenCounter counter, params string[] stop) =>
        OutputLimits.Create(maxTokens, stop, counter);

    private static string Drive(OutputCutter cutter, params string[] deltas)
    {
        var emitted = new System.Text.StringBuilder();
        foreach (var delta in deltas)
        {
            emitted.Append(cutter.Accept(delta));
        }

        emitted.Append(cutter.Flush());
        return emitted.ToString();
    }

    [Fact]
    public void The_test_counters_behave_as_their_summaries_say()
    {
        var words = new WordCounter();
        Assert.Equal(4, words.Count("Hello world foo bar"));
        Assert.Equal(11, words.IndexAtTokenCount("Hello world foo bar", 2));
        Assert.Equal(19, words.IndexAtTokenCount("Hello world foo bar", 9));

        var evenOdd = new EvenOddCounter();
        Assert.Equal(1, evenOdd.Count("ab"));
        Assert.Equal(2, evenOdd.Count("abc"));
        Assert.Equal(1, evenOdd.IndexAtTokenCount("abc", 1));
        Assert.Equal(3, evenOdd.Count(" abcd xyz")); // " abcd" is one, " xyz" is odd and so two
    }

    [Fact]
    public void The_whole_text_cut_takes_the_first_n_tokens()
    {
        var cut = Limits(2, new WordCounter()).Cut("Hello world foo bar");

        Assert.Equal("Hello world", cut.Text);
        Assert.Equal("length", cut.FinishReason);
    }

    [Theory]
    [InlineData(4)] // exactly on the budget: nothing dropped, so no cut (as D53 has it for characters)
    [InlineData(9)]
    public void A_reply_within_the_budget_is_not_cut(int cap)
    {
        var cut = Limits(cap, new WordCounter()).Cut("Hello world foo bar");

        Assert.Equal("Hello world foo bar", cut.Text);
        Assert.Null(cut.FinishReason);
    }

    [Fact]
    public void The_stream_lands_on_the_whole_text_cut_whatever_the_delta_boundaries()
    {
        var limits = Limits(2, new WordCounter());
        var whole = limits.Cut("Hello world and more");

        var cutter = new OutputCutter(limits);
        var streamed = Drive(cutter, "Hel", "lo wor", "ld and", " more");

        Assert.Equal(whole.Text, streamed);
        Assert.Equal(whole.Text, cutter.EmittedText);
        Assert.Equal("length", cutter.FinishReason);
    }

    [Fact]
    public void Far_from_the_budget_the_stream_releases_every_delta_whole()
    {
        var cutter = new OutputCutter(Limits(OutputCutter.NearBudgetReserveTokens + 20, new WordCounter()));

        Assert.Equal("Hello world ", cutter.Accept("Hello world "));
        Assert.Equal("foo", cutter.Accept("foo"));
        Assert.Equal(string.Empty, cutter.Flush());
        Assert.Null(cutter.FinishReason);
    }

    [Fact]
    public void Near_the_budget_the_stream_holds_the_trailing_partial_word()
    {
        // A budget of two is within the reserve from the first delta on.
        var cutter = new OutputCutter(Limits(2, new WordCounter()));

        Assert.Equal("Hello", cutter.Accept("Hello wor"));
        Assert.Equal(" world", cutter.Accept("ld foo"));
        Assert.Equal(string.Empty, cutter.Flush());
        Assert.Equal("Hello world", cutter.EmittedText);
        Assert.Equal("length", cutter.FinishReason);
    }

    [Fact]
    public void The_reserve_is_where_the_holdback_starts()
    {
        var counter = new WordCounter();
        var cap = OutputCutter.NearBudgetReserveTokens + 2;
        var cutter = new OutputCutter(Limits(cap, counter));

        // One word below the reserve line: still far, the partial word is released.
        var farText = string.Concat(Enumerable.Repeat("w ", cap - OutputCutter.NearBudgetReserveTokens - 1)) + "par";
        Assert.Equal(farText, cutter.Accept(farText));

        // The next word crosses the line: from here the trailing partial word is held.
        Assert.Equal("tial", cutter.Accept("tial next"));
        Assert.Equal(string.Empty, cutter.Accept("Word"));
    }

    [Fact]
    public void A_word_that_counts_more_once_it_grows_cannot_make_the_stream_overshoot()
    {
        var limits = Limits(1, new EvenOddCounter());
        var whole = limits.Cut("abc");
        Assert.Equal("a", whole.Text);

        // An eager stream would release "ab" (one token) and then find "abc" is two.
        var cutter = new OutputCutter(limits);
        var streamed = Drive(cutter, "ab", "c");

        Assert.Equal(whole.Text, streamed);
        Assert.True(limits.Counter.Count(cutter.EmittedText) <= 1);
        Assert.Equal("length", cutter.FinishReason);
    }

    [Fact]
    public void A_prefix_stable_counter_never_holds_a_partial_word()
    {
        // chars/4: three tokens are twelve characters, and "Hello wor" fits, so it goes out whole.
        var cutter = new OutputCutter(Limits(3, CharEstimateTokenCounter.Instance));

        Assert.Equal("Hello wor", cutter.Accept("Hello wor"));
    }

    [Theory]
    [InlineData(3, "Hello world foo", "length")] // the budget lands before the stop string
    [InlineData(5, "Hello world foo ", "stop")]  // the stop string lands before the budget
    public void A_stop_string_and_the_budget_still_decide_by_position(int cap, string expected, string finish)
    {
        var cut = Limits(cap, new WordCounter(), "fox").Cut("Hello world foo fox bar");

        Assert.Equal(expected, cut.Text);
        Assert.Equal(finish, cut.FinishReason);
    }

    // ---- the real counter through the pipeline ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Max_tokens_is_a_phi3_token_budget_when_the_backend_counts_that_way(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["The quick", " brown fox", " jumps"],
            TokenCounter = Phi3TokenCounter.Instance,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var (content, finish, completionTokens) = await CompleteAsync(host, stream, maxTokens: 3);

        // "The quick brown" is three Phi-3 tokens; cap * 4 characters would have given twelve.
        Assert.Equal("The quick brown", content);
        Assert.Equal("length", finish);
        Assert.Equal(3, completionTokens);
        Assert.Equal(1, fake.ContextsDisposed);
        host.AssertNoLeak();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reply_exactly_on_the_phi3_budget_is_whole_and_finishes_stop(bool stream)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["The quick", " brown fox", " jumps"],
            TokenCounter = Phi3TokenCounter.Instance,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        // Seven tokens: The, quick, brown, fox, j, umps... measured against the reference tokenizer.
        var (content, finish, completionTokens) = await CompleteAsync(host, stream, maxTokens: 7);

        Assert.Equal("The quick brown fox jumps", content);
        Assert.Equal("stop", finish);
        Assert.Equal(7, completionTokens);
        Assert.Equal(1, host.Cache.Count);
        host.AssertNoLeak();
    }

    private static async Task<(string Content, string? Finish, int CompletionTokens)> CompleteAsync(BridgeTestHost host, bool stream, int maxTokens)
    {
        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream,
            stream_options = stream ? new { include_usage = true } : null,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = "Say the sentence." } },
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        if (!stream)
        {
            var root = JsonDocument.Parse(text).RootElement;
            var choice = root.GetProperty("choices")[0];
            return (
                choice.GetProperty("message").GetProperty("content").GetString()!,
                choice.GetProperty("finish_reason").GetString(),
                root.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        }

        var content = new System.Text.StringBuilder();
        string? finish = null;
        var tokens = 0;
        foreach (var line in text.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal) && !l.EndsWith("[DONE]", StringComparison.Ordinal)))
        {
            var chunk = JsonDocument.Parse(line["data: ".Length..]).RootElement;
            if (chunk.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                tokens = usage.GetProperty("completion_tokens").GetInt32();
            }

            if (chunk.GetProperty("choices").GetArrayLength() == 0)
            {
                continue;
            }

            var choice = chunk.GetProperty("choices")[0];
            if (choice.GetProperty("delta").TryGetProperty("content", out var delta) && delta.ValueKind == JsonValueKind.String)
            {
                content.Append(delta.GetString());
            }

            if (choice.GetProperty("finish_reason").ValueKind == JsonValueKind.String)
            {
                finish = choice.GetProperty("finish_reason").GetString();
            }
        }

        return (content.ToString(), finish, tokens);
    }
}
