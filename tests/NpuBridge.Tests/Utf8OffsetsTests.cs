using NpuBridge.Backends;

namespace NpuBridge.Tests;

/// <summary>
/// <see cref="Utf8Offsets.CharIndexAtByteOffset"/>: Phi Silica's <c>GetUsablePromptLength</c> answers in
/// UTF-8 bytes (measured, D80), and the pipeline slices .NET strings by UTF-16 index. The conversion
/// rounds down to a character boundary and never lands between the halves of a surrogate pair.
/// </summary>
public class Utf8OffsetsTests
{
    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("abc", 0, 0)]
    [InlineData("abc", 2, 2)]
    [InlineData("abc", 3, 3)]
    public void Ascii_is_the_identity(string text, long bytes, int expected) =>
        Assert.Equal(expected, Utf8Offsets.CharIndexAtByteOffset(text, bytes));

    [Theory]
    [InlineData("abc", 4, 3)]
    [InlineData("abc", long.MaxValue, 3)]
    [InlineData("", 7, 0)]
    public void An_offset_past_the_end_is_the_length(string text, long bytes, int expected) =>
        Assert.Equal(expected, Utf8Offsets.CharIndexAtByteOffset(text, bytes));

    [Fact]
    public void A_negative_offset_is_zero() =>
        Assert.Equal(0, Utf8Offsets.CharIndexAtByteOffset("abc", -1));

    [Theory]
    [InlineData("→ab", 3, 1)] // the arrow is three bytes; the offset lands after it
    [InlineData("→ab", 4, 2)]
    [InlineData("→ab", 5, 3)]
    [InlineData("aéb", 3, 2)] // e-acute is two bytes
    public void A_multi_byte_character_counts_its_bytes_once(string text, long bytes, int expected) =>
        Assert.Equal(expected, Utf8Offsets.CharIndexAtByteOffset(text, bytes));

    [Theory]
    [InlineData("→ab", 1, 0)] // inside the arrow: round down to before it
    [InlineData("→ab", 2, 0)]
    [InlineData("a→b", 2, 1)] // inside the arrow after 'a'
    public void An_offset_inside_a_character_rounds_down_to_its_start(string text, long bytes, int expected) =>
        Assert.Equal(expected, Utf8Offsets.CharIndexAtByteOffset(text, bytes));

    [Theory]
    [InlineData("\U0001F600a", 4, 2)] // the emoji is four bytes and two UTF-16 chars
    [InlineData("\U0001F600a", 5, 3)]
    [InlineData("\U0001F600a", 2, 0)] // inside the emoji: before the pair, never between its halves
    [InlineData("\U0001F600a", 3, 0)]
    [InlineData("x\U0001F600", 3, 1)]
    public void A_surrogate_pair_is_never_split(string text, long bytes, int expected) =>
        Assert.Equal(expected, Utf8Offsets.CharIndexAtByteOffset(text, bytes));

    /// <summary>
    /// The three non-ASCII probes from the D80 measurement: the runtime's byte answer converts to the
    /// character prefix whose Phi-3 token count is the same 3581 as every ASCII text's.
    /// </summary>
    [Theory]
    [InlineData("The arrow → maps § to ≈ nothing — see “quotes” ", 800, 13848, 11223)]
    [InlineData("机器学习是人工智能的一个分支。", 3000, 9474, 3158)]
    [InlineData("Hello \U0001F600 world \U0001F680 again \U0001F389 ", 2000, 6562, 5370)]
    public void The_measured_preflight_answers_convert_to_the_measured_prefixes(string unit, int repeats, long bytes, int expectedChars)
    {
        var text = string.Concat(Enumerable.Repeat(unit, repeats));
        Assert.Equal(expectedChars, Utf8Offsets.CharIndexAtByteOffset(text, bytes));
    }
}
