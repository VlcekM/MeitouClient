using System.Buffers.Binary;

namespace Meitou.Data.World;

/// <summary>
/// Minimal baseline TIFF reader for what the world maps use: one uncompressed greyscale image of 8 or 16
/// bits per sample, in strips, either byte order. Rows are read on demand, so a 537 MB heightmap never has
/// to be in memory at once. Layout: TIFF 6.0 (public spec); see docs/formats/terrain.md.
/// </summary>
public sealed class TiffImage : IDisposable
{
    readonly Stream stream;
    readonly bool ownsStream;
    readonly long[] stripOffsets;
    readonly long[] stripByteCounts;

    TiffImage(Stream stream, bool ownsStream, bool littleEndian, int width, int height, int bitsPerSample,
        int rowsPerStrip, long[] stripOffsets, long[] stripByteCounts, IReadOnlyDictionary<int, TiffTag> tags)
    {
        this.stream = stream;
        this.ownsStream = ownsStream;
        LittleEndian = littleEndian;
        Width = width;
        Height = height;
        BitsPerSample = bitsPerSample;
        RowsPerStrip = rowsPerStrip;
        this.stripOffsets = stripOffsets;
        this.stripByteCounts = stripByteCounts;
        Tags = tags;
    }

    public bool LittleEndian { get; }
    public int Width { get; }
    public int Height { get; }
    public int BitsPerSample { get; }
    public int RowsPerStrip { get; }
    public int StripCount => stripOffsets.Length;
    public int BytesPerRow => Width * (BitsPerSample / 8);

    /// <summary>Every tag of the first image directory, by tag number (including ones this reader ignores).</summary>
    public IReadOnlyDictionary<int, TiffTag> Tags { get; }

    /// <summary>Byte offset of the first strip's pixel data.</summary>
    public long DataOffset => stripOffsets[0];

    public static TiffImage Open(string path) => Open(File.OpenRead(path), ownsStream: true);

    public static TiffImage Open(Stream stream, bool ownsStream = false)
    {
        try { return Parse(stream, ownsStream); }
        catch (Exception e)
        {
            if (ownsStream) stream.Dispose();
            if (e is EndOfStreamException) throw new InvalidDataException("TIFF file is truncated.", e);
            throw;
        }
    }

    static TiffImage Parse(Stream stream, bool ownsStream)
    {
        Span<byte> head = stackalloc byte[8];
        stream.Position = 0;
        stream.ReadExactly(head);
        bool le = head[0] == 'I' && head[1] == 'I';
        if (!le && !(head[0] == 'M' && head[1] == 'M'))
            throw new InvalidDataException("Not a TIFF file (no II/MM byte-order mark).");
        if (U16(head[2..], le) != 42)
            throw new InvalidDataException("Not a classic TIFF file (magic is not 42).");
        long ifd = U32(head[4..], le);

        stream.Position = ifd;
        Span<byte> two = stackalloc byte[2];
        stream.ReadExactly(two);
        int entryCount = U16(two, le);
        var entries = new byte[entryCount * 12];
        stream.ReadExactly(entries);

        var tags = new Dictionary<int, TiffTag>();
        for (int i = 0; i < entryCount; i++)
        {
            var e = entries.AsSpan(i * 12, 12);
            var tag = new TiffTag(U16(e, le), U16(e[2..], le), U32(e[4..], le), e[8..12].ToArray());
            tags[tag.Tag] = tag;
        }

        long[] Values(int tag)
        {
            if (!tags.TryGetValue(tag, out var t))
                throw new InvalidDataException($"TIFF tag {tag} is missing.");
            return ReadValues(stream, t, le);
        }
        long Single(int tag, long fallback) => tags.ContainsKey(tag) ? Values(tag)[0] : fallback;

        int width = checked((int)Single(256, -1)), height = checked((int)Single(257, -1));
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("TIFF image has no size.");
        int bits = (int)Single(258, 1);
        if (Single(259, 1) != 1)
            throw new NotSupportedException("Compressed TIFF images are not supported.");
        if (Single(277, 1) != 1)
            throw new NotSupportedException("Only single-channel TIFF images are supported.");
        if (Single(262, 1) is not (0 or 1))
            throw new NotSupportedException("Only greyscale TIFF images are supported.");
        if (bits is not (8 or 16))
            throw new NotSupportedException($"{bits}-bit TIFF samples are not supported.");
        int rowsPerStrip = (int)Math.Min(Single(278, height), height);
        var offsets = Values(273);
        var counts = Values(279);
        int expectedStrips = (height + rowsPerStrip - 1) / rowsPerStrip;
        if (offsets.Length != expectedStrips || counts.Length != expectedStrips)
            throw new InvalidDataException($"TIFF has {offsets.Length} strips, expected {expectedStrips}.");
        long rowBytes = (long)width * (bits / 8);
        for (int s = 0; s < offsets.Length; s++)
        {
            long rows = Math.Min(rowsPerStrip, height - (long)s * rowsPerStrip);
            if (counts[s] < rows * rowBytes || offsets[s] + rows * rowBytes > stream.Length)
                throw new InvalidDataException($"TIFF strip {s} is truncated.");
        }
        return new TiffImage(stream, ownsStream, le, width, height, bits, rowsPerStrip, offsets, counts, tags);
    }

    static long[] ReadValues(Stream stream, TiffTag tag, bool le)
    {
        int size = tag.Type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 => 4, _ => throw new NotSupportedException($"TIFF tag {tag.Tag} has unsupported type {tag.Type}.") };
        long byteCount = size * tag.Count;
        byte[] data;
        if (byteCount <= 4)
            data = tag.RawValue;
        else
        {
            long saved = stream.Position;
            stream.Position = U32(tag.RawValue, le);
            data = new byte[checked((int)byteCount)];
            stream.ReadExactly(data);
            stream.Position = saved;
        }
        var values = new long[tag.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = size switch { 1 => data[i], 2 => U16(data.AsSpan(i * 2), le), _ => U32(data.AsSpan(i * 4), le) };
        return values;
    }

    /// <summary>Reads one row's raw bytes (in file byte order) into <paramref name="destination"/>.</summary>
    public void ReadRowBytes(int row, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Height);
        stream.Position = RowOffset(row);
        stream.ReadExactly(destination[..BytesPerRow]);
    }

    /// <summary>Reads one row of samples (8-bit samples widen to 0..255).</summary>
    public void ReadRow(int row, Span<ushort> destination)
    {
        var bytes = new byte[BytesPerRow];
        ReadRowBytes(row, bytes);
        DecodeRow(bytes, destination);
    }

    /// <summary>Reads the sample at (<paramref name="column"/>, <paramref name="row"/>).</summary>
    public ushort ReadSample(int column, int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, Width);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Height);
        int size = BitsPerSample / 8;
        stream.Position = RowOffset(row) + (long)column * size;
        Span<byte> b = stackalloc byte[2];
        stream.ReadExactly(b[..size]);
        return size == 1 ? b[0] : U16(b, LittleEndian);
    }

    /// <summary>Converts raw row bytes (from <see cref="ReadRowBytes"/>) to samples.</summary>
    public void DecodeRow(ReadOnlySpan<byte> bytes, Span<ushort> destination)
    {
        if (BitsPerSample == 8)
            for (int x = 0; x < Width; x++) destination[x] = bytes[x];
        else
            for (int x = 0; x < Width; x++) destination[x] = U16(bytes[(x * 2)..], LittleEndian);
    }

    long RowOffset(int row) => stripOffsets[row / RowsPerStrip] + (long)(row % RowsPerStrip) * BytesPerRow;

    static ushort U16(ReadOnlySpan<byte> b, bool le) => le ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);
    static uint U32(ReadOnlySpan<byte> b, bool le) => le ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);

    public void Dispose()
    {
        if (ownsStream) stream.Dispose();
    }
}

/// <summary>A raw TIFF directory entry. <see cref="RawValue"/> holds the value, or its offset when it doesn't fit in 4 bytes.</summary>
public sealed record TiffTag(int Tag, int Type, uint Count, byte[] RawValue);
