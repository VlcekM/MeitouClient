using System.Buffers.Binary;

namespace Meitou.Data.Audio;

/// <summary>
/// A Wwise PCM WEM (docs/formats/wwise-bnk.md "PCM"): a RIFF/WAVE whose <c>fmt </c> codec is <c>0x0001</c> or <c>0xFFFE</c> with 16-bit samples,
/// its <c>data</c> chunk the interleaved little-endian samples. Kenshi's own banks hold none; mods' banks made with Wwise's PCM setting do.
/// </summary>
public sealed class WwisePcm
{
    /// <summary>
    /// Channel count (<c>fmt </c> offset 2).
    /// </summary>
    public int Channels { get; private init; }
    /// <summary>
    /// Samples per second per channel (<c>fmt </c> offset 4).
    /// </summary>
    public int SampleRate { get; private init; }
    /// <summary>
    /// The samples, interleaved, scaled to -1..1.
    /// </summary>
    public float[] Samples { get; private init; } = [];

    /// <summary>
    /// The sample frames the sound plays.
    /// </summary>
    public int Frames => Samples.Length / Channels;

    /// <summary>
    /// The WEM's samples, or null when it is not 16-bit PCM (its codec, sample size, block alignment or data rate say otherwise).
    /// </summary>
    public static WwisePcm? TryRead(ReadOnlyMemory<byte> wem)
    {
        var s = wem.Span;
        if (s.Length < 12 || !s.StartsWith("RIFF"u8) || !s[8..12].SequenceEqual("WAVE"u8)) return null;
        if (FindChunks(s) is not ({ } fmt, { } data)) return null;

        int codec = BinaryPrimitives.ReadUInt16LittleEndian(s[fmt.Start..]);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(s[(fmt.Start + 2)..]);
        int rate = BinaryPrimitives.ReadInt32LittleEndian(s[(fmt.Start + 4)..]);
        int bytesPerSecond = BinaryPrimitives.ReadInt32LittleEndian(s[(fmt.Start + 8)..]);
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(s[(fmt.Start + 12)..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(s[(fmt.Start + 14)..]);
        bool pcm = codec is 0x0001 or 0xFFFE && bits == 16 && channels is >= 1 and <= 8 && rate > 0;
        if (!pcm || blockAlign != 2 * channels || bytesPerSecond != rate * blockAlign) return null;

        var samples = s.Slice(data.Start, data.Length - data.Length % blockAlign);
        return new WwisePcm { Channels = channels, SampleRate = rate, Samples = ToFloats(samples) };
    }

    /// <summary>
    /// The <c>fmt </c> (at least 16 bytes) and <c>data</c> chunks' bodies; null when either is missing or a chunk runs past the file.
    /// </summary>
    static ((int Start, int Length) Fmt, (int Start, int Length) Data)? FindChunks(ReadOnlySpan<byte> s)
    {
        (int, int)? fmt = null, data = null;
        for (int p = 12; p + 8 <= s.Length;)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(s[(p + 4)..]);
            if (len < 0 || p + 8 + (long)len > s.Length) return null;

            var tag = s.Slice(p, 4);
            if (tag.SequenceEqual("fmt "u8) && len >= 16) fmt = (p + 8, len);
            else if (tag.SequenceEqual("data"u8)) data = (p + 8, len);
            p += 8 + len + (len & 1);
        }

        return fmt is { } f && data is { } d ? (f, d) : null;
    }

    /// <summary>
    /// 16-bit little-endian samples as floats in -1..1.
    /// </summary>
    static float[] ToFloats(ReadOnlySpan<byte> bytes)
    {
        var samples = new float[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(2 * i)..]) / 32768f;
        return samples;
    }
}
