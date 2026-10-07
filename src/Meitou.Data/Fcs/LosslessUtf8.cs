using System.Text;

namespace Meitou.Data.Fcs;

/// <summary>
/// UTF-8 that never fails: a byte that is not part of a valid sequence becomes the lone low surrogate U+DC00 + byte (as Python's
/// <c>surrogateescape</c> does) and turns back into that byte when encoded, so any byte string survives decoding and encoding unchanged. Save files need it:
/// the ids of the instances in a TERRAIN_DECALS record are raw 4-byte integers in a string slot (docs/formats/save.md).
/// </summary>
public static class LosslessUtf8
{
    public static string GetString(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        while (!bytes.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf8(bytes, out var rune, out int used) == System.Buffers.OperationStatus.Done)
            {
                sb.Append(rune.ToString());
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

    public static byte[] GetBytes(string s)
    {
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
                int n = new System.Text.Rune(c, s[i + 1]).EncodeToUtf8(buffer);
                for (int k = 0; k < n; k++) bytes.Add(buffer[k]);
                i++;
                continue;
            }
            if (char.IsSurrogate(c)) throw new ArgumentException("A lone surrogate other than an escaped byte cannot be written.", nameof(s));
            int len = new System.Text.Rune(c).EncodeToUtf8(buffer);
            for (int k = 0; k < len; k++) bytes.Add(buffer[k]);
        }
        return [.. bytes];
    }
}
