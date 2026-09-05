using System.Numerics;
using System.Security.Cryptography;

namespace NpuBridge.Api;

/// <summary>
/// Minimal ULID generator (<see href="https://github.com/ulid/spec"/>): a 48-bit millisecond
/// timestamp followed by 80 bits of cryptographic randomness, encoded as 26 Crockford-base32
/// characters. No ULID package exists anywhere in this repo and none is warranted for one call site,
/// so this is a small self-contained implementation rather than a new dependency.
/// </summary>
internal static class Ulid
{
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewUlid()
    {
        Span<byte> data = stackalloc byte[16];

        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++)
        {
            data[5 - i] = (byte)(ms & 0xFF);
            ms >>= 8;
        }

        RandomNumberGenerator.Fill(data[6..]);

        // Treat the 16 bytes as an unsigned big-endian 128-bit integer and emit its base-32 digits
        // most-significant first. 26 symbols * 5 bits = 130 bits, 2 more than the 128 available; the
        // extra capacity naturally comes out as leading zero digits once the value is exhausted.
        var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        Span<char> chars = stackalloc char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = CrockfordAlphabet[(int)(value & 0x1F)];
            value >>= 5;
        }

        return new string(chars);
    }
}

/// <summary>Generates ids for chat completion responses.</summary>
public static class ChatCompletionId
{
    public static string NewId() => $"chatcmpl-{Ulid.NewUlid()}";
}
