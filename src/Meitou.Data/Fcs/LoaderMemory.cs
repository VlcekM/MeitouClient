using System.Buffers.Binary;

namespace Meitou.Data.Fcs;

/// <summary>
/// One of the game loader's fixed-size variables (a count, a length, a value): its bytes and whether a read ever filled them. A read at the end of the
/// stream changes only the bytes it got, so a variable keeps its last value, and one no read filled has no value the file decides
/// (docs/formats/fcs-mod.md, "Damaged files").
/// </summary>
internal sealed class LoaderValue(int size)
{
    /// <summary>
    /// The variable's bytes, little-endian.
    /// </summary>
    readonly byte[] _bytes = new byte[size];

    /// <summary>
    /// Whether every byte was filled by a read or a set.
    /// </summary>
    bool _known;

    /// <summary>
    /// The variable as an int; throws <see cref="UnreadValueException"/> when no read filled it.
    /// </summary>
    public int Int32 => BinaryPrimitives.ReadInt32LittleEndian(Bytes);

    /// <summary>
    /// The variable as an unsigned int; throws <see cref="UnreadValueException"/> when no read filled it.
    /// </summary>
    public uint UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(Bytes);

    /// <summary>
    /// The variable's first byte; throws <see cref="UnreadValueException"/> when no read filled it.
    /// </summary>
    public byte Byte => Bytes[0];

    /// <summary>
    /// The float at byte <paramref name="offset"/>; throws <see cref="UnreadValueException"/> when no read filled the variable.
    /// </summary>
    public float Single(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Bytes[offset..]);

    /// <summary>
    /// The bytes, once filled.
    /// </summary>
    ReadOnlySpan<byte> Bytes => _known ? _bytes : throw new UnreadValueException();

    /// <summary>
    /// Reads the variable from <paramref name="stream"/>: the bytes it gets replace the first ones; a short read leaves the rest as they were.
    /// </summary>
    public void Read(LoaderStream stream)
    {
        if (stream.Read(_bytes) == _bytes.Length) _known = true;
    }

    /// <summary>
    /// Sets the variable to <paramref name="bytes"/> (a constructor's value).
    /// </summary>
    public void Set(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_bytes);
        _known = true;
    }

    /// <summary>
    /// Sets the variable to the int <paramref name="value"/>.
    /// </summary>
    public void Set(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_bytes, value);
        _known = true;
    }

    /// <summary>
    /// Forgets the value (a local of a new call).
    /// </summary>
    public void Forget() => _known = false;
}

/// <summary>
/// One of the game loader's character buffers: its bytes and which of them were ever written. A string is taken from it up to the first NUL
/// (docs/formats/fcs-mod.md, "Strings"); a byte never written has no value the file decides.
/// </summary>
internal sealed class LoaderBuffer(int size)
{
    /// <summary>
    /// The buffer's bytes.
    /// </summary>
    readonly byte[] _bytes = new byte[size];

    /// <summary>
    /// Which bytes were written.
    /// </summary>
    readonly bool[] _known = new bool[size];

    /// <summary>
    /// A buffer whose every byte is zero and known (a global, zero in the image).
    /// </summary>
    public static LoaderBuffer Zeroed(int size)
    {
        var buffer = new LoaderBuffer(size);
        Array.Fill(buffer._known, true);
        return buffer;
    }

    /// <summary>
    /// Forgets every byte (a stack buffer of a new call).
    /// </summary>
    public void Forget() => Array.Clear(_known);

    /// <summary>
    /// Reads up to <paramref name="count"/> bytes from <paramref name="stream"/> into the buffer at <paramref name="offset"/>; the bytes a short read
    /// does not get stay as they were.
    /// </summary>
    public void Fill(int offset, LoaderStream stream, int count)
    {
        int n = stream.Read(_bytes.AsSpan(offset, count));
        _known.AsSpan(offset, n).Fill(true);
    }

    /// <summary>
    /// Reads a length into <paramref name="length"/> and the string after it from <paramref name="stream"/> into the buffer at
    /// <paramref name="offset"/>: up to <paramref name="size"/> bytes whole with a terminator after them (one past the buffer at exactly size), a
    /// longer one (or a negative length) as its first size - 1 bytes, the rest skipped by a relative seek.
    /// </summary>
    public void ReadString(LoaderStream stream, int offset, int size, LoaderValue length)
    {
        length.Read(stream);
        uint n = length.UInt32;
        if (n <= size)
        {
            Fill(offset, stream, (int)n);
            Put(offset + (int)n, 0);
            return;
        }

        Fill(offset, stream, size);
        stream.Seek((long)(int)n - size);
        Put(offset + size - 1, 0);
    }

    /// <summary>
    /// Writes <paramref name="value"/> at <paramref name="index"/>.
    /// </summary>
    public void Put(int index, byte value)
    {
        _bytes[index] = value;
        _known[index] = true;
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> at <paramref name="offset"/>.
    /// </summary>
    public void Put(int offset, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_bytes.AsSpan(offset));
        _known.AsSpan(offset, bytes.Length).Fill(true);
    }

    /// <summary>
    /// The bytes from <paramref name="offset"/> up to the first NUL, decoded with <see cref="LosslessUtf8"/>; throws
    /// <see cref="UnreadValueException"/> when a byte before it was never written or no NUL comes before the buffer's end.
    /// </summary>
    public string CString(int offset)
    {
        for (int i = offset; i < _bytes.Length; i++)
        {
            if (!_known[i]) throw new UnreadValueException();
            if (_bytes[i] == 0) return LosslessUtf8.GetString(_bytes.AsSpan(offset, i - offset));
        }

        throw new UnreadValueException();
    }

    /// <summary>
    /// Whether the byte at <paramref name="index"/> is a written NUL; throws <see cref="UnreadValueException"/> when it was never written.
    /// </summary>
    public bool IsNul(int index) => _known[index] ? _bytes[index] == 0 : throw new UnreadValueException();
}

/// <summary>
/// The game loader would use a variable or buffer byte that no read of the file filled: from here on what it does depends on memory the file does not
/// decide, so the reading stops (docs/formats/fcs-mod.md, "Damaged files").
/// </summary>
internal sealed class UnreadValueException : Exception;