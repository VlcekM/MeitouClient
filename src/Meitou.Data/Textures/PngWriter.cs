using System.Buffers.Binary;
using System.IO.Compression;

namespace Meitou.Data.Textures;

/// <summary>
/// Minimal PNG encoder (8-bit RGBA) for screenshots: each row takes the filter that leaves the smallest sum of residuals (the PNG spec's heuristic).
/// </summary>
public static class PngWriter
{
    /// <summary>
    /// Writes <paramref name="rgba"/> (<paramref name="width"/> x <paramref name="height"/>, rows top to bottom) as a PNG file at
    /// <paramref name="path"/>.
    /// </summary>
    public static void Write(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        using var file = File.Create(path);
        Write(file, width, height, rgba);
    }

    /// <summary>
    /// Writes <paramref name="rgba"/> as a PNG to <paramref name="file"/>: signature, IHDR (8-bit RGBA), the filtered rows deflated in IDAT, IEND.
    /// </summary>
    public static void Write(Stream file, int width, int height, ReadOnlySpan<byte> rgba)
    {
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // colour type RGBA
        Chunk(file, "IHDR", header);

        using var raw = new MemoryStream();
        int stride = width * 4;
        var rows = new byte[5][];
        for (int f = 0; f < 5; f++) rows[f] = new byte[stride];
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < height; y++)
            {
                var row = rgba.Slice(y * stride, stride);
                var above = y > 0 ? rgba.Slice((y - 1) * stride, stride) : default;
                int best = 0;
                long bestSum = long.MaxValue;
                for (int f = 0; f < 5; f++)
                {
                    long sum = Filter(f, row, above, rows[f]);
                    if (sum < bestSum) (best, bestSum) = (f, sum);
                }
                z.WriteByte((byte)best);
                z.Write(rows[best]);
            }
        Chunk(file, "IDAT", raw.ToArray());
        Chunk(file, "IEND", []);
    }

    /// <summary>
    /// The row through PNG filter <paramref name="f"/> (none, sub, up, average, Paeth; 4 bytes a pixel) into <paramref name="output"/>; returns the
    /// sum of the residuals as signed bytes.
    /// </summary>
    static long Filter(int f, ReadOnlySpan<byte> row, ReadOnlySpan<byte> above, byte[] output)
    {
        long sum = 0;
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= 4 ? row[i - 4] : 0, b = above.IsEmpty ? 0 : above[i], c = i >= 4 && !above.IsEmpty ? above[i - 4] : 0;
            int predicted = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) };
            byte value = (byte)(row[i] - predicted);
            output[i] = value;
            int residual = (sbyte)value;
            sum += residual < 0 ? -residual : residual;
        }
        return sum;
    }

    /// <summary>
    /// The PNG Paeth predictor of a pixel byte from its left (<paramref name="a"/>), upper (<paramref name="b"/>) and upper-left
    /// (<paramref name="c"/>) neighbours.
    /// </summary>
    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>
    /// Writes one PNG chunk: big-endian length, type, data and the CRC of type and data.
    /// </summary>
    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buf, ~Crc(Crc(0xFFFFFFFF, typeBytes), data));
        s.Write(buf);
    }

    /// <summary>
    /// The CRC-32 table of the PNG specification (polynomial 0xEDB88320).
    /// </summary>
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>
    /// Folds <paramref name="data"/> into the running CRC-32 <paramref name="crc"/>.
    /// </summary>
    static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}