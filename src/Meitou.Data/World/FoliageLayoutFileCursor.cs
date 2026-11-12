using System.Buffers.Binary;
using System.Text;

namespace Meitou.Data.World;

/// <summary>
/// The read position in a foliage layout cache file's bytes (<see cref="FoliageLayoutCache"/>), little-endian, bounded by the end of its data (the
/// bytes before the checksum): a read past it throws <see cref="InvalidDataException"/>. A ref struct passed by <c>ref</c>, so decoding allocates
/// nothing for it.
/// </summary>
ref struct FoliageLayoutFileCursor
{
    /// <summary>
    /// The whole file.
    /// </summary>
    readonly byte[] _bytes;
    /// <summary>
    /// The end of the data: no read goes past it.
    /// </summary>
    readonly int _end;

    /// <summary>
    /// A cursor at the start of <paramref name="bytes"/>, its data ending at <paramref name="end"/>.
    /// </summary>
    public FoliageLayoutFileCursor(byte[] bytes, int end)
    {
        _bytes = bytes;
        _end = end;
    }

    /// <summary>
    /// The offset of the next read.
    /// </summary>
    public int At { get; private set; }

    /// <summary>
    /// The next <paramref name="n"/> bytes, which the cursor moves past.
    /// </summary>
    public ReadOnlySpan<byte> Take(int n)
    {
        Need(n);
        var span = _bytes.AsSpan(At, n);
        At += n;
        return span;
    }

    /// <summary>
    /// The next unsigned 32-bit value.
    /// </summary>
    public uint ReadU32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(At, 4));
        At += 4;
        return v;
    }

    /// <summary>
    /// The next signed 32-bit value.
    /// </summary>
    public int ReadI32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(At, 4));
        At += 4;
        return v;
    }

    /// <summary>
    /// The next unsigned 16-bit value.
    /// </summary>
    public int ReadU16()
    {
        Need(2);
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(At, 2));
        At += 2;
        return v;
    }

    /// <summary>
    /// The next byte.
    /// </summary>
    public byte ReadU8()
    {
        Need(1);
        return _bytes[At++];
    }

    /// <summary>
    /// The next float, stored as its bits.
    /// </summary>
    public float ReadF32() => BitConverter.Int32BitsToSingle(ReadI32());

    /// <summary>
    /// The next count: a signed 32-bit value from 0 to 2^26.
    /// </summary>
    public int ReadCount()
    {
        int n = ReadI32();
        if (n < 0 || n > (1 << 26)) throw new InvalidDataException("count");
        return n;
    }

    /// <summary>
    /// The next string list: a 16-bit count, then each string's 16-bit byte length and UTF-8 bytes.
    /// </summary>
    public string[] ReadStrings()
    {
        int n = ReadU16();
        var s = new string[n];
        for (int i = 0; i < n; i++)
        {
            int length = ReadU16();
            Need(length);
            s[i] = Encoding.UTF8.GetString(_bytes, At, length);
            At += length;
        }
        return s;
    }

    /// <summary>
    /// Throws when fewer than <paramref name="n"/> bytes of data are left (or <paramref name="n"/> is negative).
    /// </summary>
    readonly void Need(int n)
    {
        if (n < 0 || At + n > _end) throw new InvalidDataException("truncated");
    }
}
