namespace Meitou.Data.Audio;

/// <summary>
/// Reads Vorbis-style bit fields: least significant bit first within each byte, fields least significant bit first.
/// </summary>
ref struct VorbisBitReader(ReadOnlySpan<byte> data)
{
    /// <summary>
    /// The packet's bytes.
    /// </summary>
    readonly ReadOnlySpan<byte> _data = data;

    /// <summary>
    /// The next bit to read, counted from the first byte's lowest bit.
    /// </summary>
    public int Position { get; private set; }
    /// <summary>
    /// The packet's length in bits.
    /// </summary>
    public readonly int Length => _data.Length * 8;
    /// <summary>
    /// Bits not read yet.
    /// </summary>
    public readonly int Remaining => Length - Position;

    /// <summary>
    /// Reads up to 32 bits.
    /// </summary>
    public uint Read(int bits)
    {
        if (bits > Remaining)
            throw new InvalidDataException($"Read of {bits} bits at bit {Position} runs past the packet ({Length} bits)");
        ulong value = 0;
        int got = 0;
        while (got < bits)
        {
            int at = Position >> 3, shift = Position & 7;
            int take = Math.Min(8 - shift, bits - got);
            value |= (ulong)((_data[at] >> shift) & ((1 << take) - 1)) << got;
            got += take;
            Position += take;
        }
        return (uint)value;
    }

    /// <summary>
    /// Reads one bit as a flag.
    /// </summary>
    public bool Flag() => Read(1) != 0;
}

/// <summary>
/// Writes Vorbis-style bit fields (the order of <see cref="VorbisBitReader"/>).
/// </summary>
sealed class VorbisBitWriter
{
    /// <summary>
    /// The bytes written so far; grows as needed, never shrinks.
    /// </summary>
    byte[] _bytes = new byte[256];
    /// <summary>
    /// The next bit to write, counted from the first byte's lowest bit.
    /// </summary>
    int _bit;

    /// <summary>
    /// Writes the low <paramref name="bits"/> (up to 32) of <paramref name="value"/>.
    /// </summary>
    public void Write(uint value, int bits)
    {
        if (bits == 0) return;
        int needed = (_bit + bits + 7) >> 3;
        if (needed > _bytes.Length) Array.Resize(ref _bytes, Math.Max(needed, _bytes.Length * 2));
        ulong v = bits == 32 ? value : value & ((1u << bits) - 1);
        int at = _bit >> 3, shift = _bit & 7;
        v <<= shift;
        for (int left = bits + shift; left > 0; left -= 8, at++, v >>= 8) _bytes[at] |= (byte)v;
        _bit += bits;
    }

    /// <summary>
    /// Writes one bit: 1 for true.
    /// </summary>
    public void Write(bool flag) => Write(flag ? 1u : 0u, 1);

    /// <summary>
    /// Copies <paramref name="bits"/> bits from the reader.
    /// </summary>
    public void Copy(ref VorbisBitReader reader, int bits)
    {
        while (bits > 0)
        {
            int n = Math.Min(bits, 32);
            Write(reader.Read(n), n);
            bits -= n;
        }
    }

    /// <summary>
    /// Writes each byte of <paramref name="data"/> as 8 bits.
    /// </summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) Write(b, 8);
    }

    /// <summary>
    /// The bytes written so far, the last one padded with zero bits.
    /// </summary>
    public byte[] ToArray() => _bytes[..((_bit + 7) >> 3)];

    /// <summary>
    /// The number of bits needed for <paramref name="v"/> (Vorbis <c>ilog</c>).
    /// </summary>
    public static int Log(uint v)
    {
        int r = 0;
        while (v != 0)
        {
            r++;
            v >>= 1;
        }
        return r;
    }
}