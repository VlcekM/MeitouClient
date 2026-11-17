using System.Text;
using System.Text.Unicode;

namespace Meitou.Data.Fcs;

/// <summary>
/// UTF-8 that never fails: a byte that is not part of a valid sequence becomes the lone low surrogate U+DC00 + byte (as Python's
/// <c>surrogateescape</c> does) and turns back into that byte when encoded, so any byte string survives decoding and encoding unchanged. Every FCS
/// string goes through it: the game copies string bytes as they are, so mods carry other encodings (docs/formats/fcs-mod.md "Strings") and saves
/// raw 4-byte integers in the instance ids of a TERRAIN_DECALS record (docs/formats/save.md). Decoding is one-to-one, so ordinal comparison of
/// decoded strings is the game's byte comparison.
/// </summary>
public static class LosslessUtf8
{
    /// <summary>
    /// Decodes <paramref name="bytes"/>: valid UTF-8 sequences as their characters, every other byte as U+DC00 + byte.
    /// </summary>
    public static string GetString(ReadOnlySpan<byte> bytes)
    {
        if (Utf8.IsValid(bytes)) return Encoding.UTF8.GetString(bytes);

        var sb = new StringBuilder(bytes.Length);
        Span<char> units = stackalloc char[2];
        while (!bytes.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(bytes, out var rune, out int used) == System.Buffers.OperationStatus.Done)
            {
                sb.Append(units[..rune.EncodeToUtf16(units)]);
                bytes = bytes[used..];
            }
            else
            {
                sb.Append((char)(0xDC00 + bytes[0]));
                bytes = bytes[1..];
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Encodes <paramref name="s"/>: a lone U+DC80..U+DCFF back to its byte, a surrogate pair as its UTF-8 sequence, everything else as UTF-8; any
    /// other lone surrogate throws.
    /// </summary>
    public static byte[] GetBytes(string s)
    {
        if (s.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') < 0) return Encoding.UTF8.GetBytes(s);

        var bytes = new List<byte>(s.Length);
        Span<byte> buffer = stackalloc byte[4];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is >= (char)0xDC80 and <= (char)0xDCFF && (i == 0 || !char.IsHighSurrogate(s[i - 1])))
            {
                bytes.Add((byte)(c - 0xDC00));
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                int n = new Rune(c, s[i + 1]).EncodeToUtf8(buffer);
                for (int k = 0; k < n; k++) bytes.Add(buffer[k]);
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
                throw new ArgumentException("A lone surrogate other than an escaped byte cannot be written.", nameof(s));

            int len = new Rune(c).EncodeToUtf8(buffer);
            for (int k = 0; k < len; k++) bytes.Add(buffer[k]);
        }

        return [.. bytes];
    }
}
