using System.Buffers.Binary;
using System.IO.Compression;

namespace Meitou.Data.Save;

/// <summary>
/// The portrait atlas of a save (<c>portraits_texture.png</c>, docs/formats/save.md): 2048 x 2048, 8-bit RGBA, 16 x 16 cells of 128 px. A save made
/// from nothing has no portraits yet, so it gets an atlas of transparent cells (a few KB once deflated); a save made from a loaded one keeps the
/// atlas it came with.
/// </summary>
public static class SavePortraits
{
    /// <summary>
    /// The atlas width and height in pixels.
    /// </summary>
    public const int Size = 2048;

    /// <summary>
    /// The blank atlas PNG, made on first use.
    /// </summary>
    static readonly Lazy<byte[]> BlankImage = new(MakeBlank);

    /// <summary>
    /// The CRC-32 table of every byte value.
    /// </summary>
    static readonly uint[] Table = MakeTable();

    /// <summary>
    /// A transparent atlas.
    /// </summary>
    public static byte[] Blank() => (byte[])BlankImage.Value.Clone();

    /// <summary>
    /// Encodes the transparent atlas as a PNG: signature, IHDR (8-bit RGBA), one IDAT of the zlib-deflated rows, IEND.
    /// </summary>
    static byte[] MakeBlank()
    {
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Size);
        header[8] = 8;    // bit depth
        header[9] = 6;    // colour type: RGBA
        Chunk(png, "IHDR", header);

        // Every row is a filter byte (0) and the pixels, all zero.
        var data = new MemoryStream();
        using (var z = new ZLibStream(data, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[1 + Size * 4];
            for (int y = 0; y < Size; y++) z.Write(row);
        }
        Chunk(png, "IDAT", data.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// Writes a PNG chunk: big-endian length, type, data and the CRC-32 of type and data.
    /// </summary>
    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        s.Write(length);
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type, body);
        data.CopyTo(body, 4);
        s.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc32(body));
        s.Write(length);
    }

    /// <summary>
    /// Builds the CRC-32 table (reflected polynomial <c>0xEDB88320</c>).
    /// </summary>
    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>
    /// The CRC-32 of <paramref name="bytes"/>, as PNG chunks use it.
    /// </summary>
    static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in bytes) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}