namespace NpuBridge.Backends;

/// <summary>
/// Converts a UTF-8 byte offset into a UTF-16 char index of the same string. Phi Silica's
/// <c>GetUsablePromptLength</c> answers in bytes (measured against the Phi-3 tokenizer on CJK, emoji and
/// typographic punctuation, D80), and everything downstream slices .NET strings by char index.
/// </summary>
public static class Utf8Offsets
{
    /// <summary>
    /// The largest char index whose UTF-8 encoding is at most <paramref name="utf8ByteOffset"/> bytes:
    /// an offset inside a multi-byte character rounds down to that character's start, so the result is
    /// never between the halves of a surrogate pair. Negative offsets are 0; offsets past the end are the
    /// length. A lone surrogate counts the three bytes .NET's encoder writes for it.
    /// </summary>
    public static int CharIndexAtByteOffset(string text, long utf8ByteOffset)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (utf8ByteOffset <= 0)
        {
            return 0;
        }

        long bytes = 0;
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            var pair = char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
            var size = pair ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
            if (bytes + size > utf8ByteOffset)
            {
                break;
            }

            bytes += size;
            index += pair ? 2 : 1;
        }

        return index;
    }
}
