using Meitou.Content;

namespace Meitou.Data.World;

/// <summary>
/// The world heightmap, <c>data/newland/land/fullmap.tif</c>: 16385 × 16385 unsigned 16-bit samples, column
/// along world +X, row along world +Z, <see cref="WorldLayout.RawToHeight"/> to world units
/// (docs/formats/terrain.md). Reads from the file on demand.
/// </summary>
public sealed class TerrainHeightmap : IDisposable
{
    /// <summary>Path of the heightmap relative to the install's <c>data/</c> folder.</summary>
    public const string RelativePath = "newland/land/fullmap.tif";

    TerrainHeightmap(TiffImage image) => Image = image;

    public TiffImage Image { get; }
    public int Size => Image.Width;

    public static TerrainHeightmap Open(GameInstall install) => Open(Path.Combine(install.DataDirectory, RelativePath));

    public static TerrainHeightmap Open(string path) => FromImage(TiffImage.Open(path));

    public static TerrainHeightmap Open(Stream stream) => FromImage(TiffImage.Open(stream));

    static TerrainHeightmap FromImage(TiffImage image)
    {
        if (image.BitsPerSample != 16 || image.Width != image.Height)
        {
            image.Dispose();
            throw new InvalidDataException($"Heightmap must be a square 16-bit image, got {image.Width}x{image.Height} at {image.BitsPerSample} bits.");
        }
        return new TerrainHeightmap(image);
    }

    public ushort Sample(int column, int row) => Image.ReadSample(column, row);

    public void ReadRow(int row, Span<ushort> destination) => Image.ReadRow(row, destination);

    /// <summary>
    /// Bilinearly interpolated world height at (<paramref name="x"/>, <paramref name="z"/>), assuming this
    /// heightmap covers the whole world (<see cref="WorldLayout"/>). Points outside are clamped to the edge.
    /// </summary>
    public float HeightAt(double x, double z)
    {
        double scale = (Size - 1) / (double)WorldLayout.WorldSize;
        double u = Math.Clamp((x + WorldLayout.HalfWorldSize) * scale, 0, Size - 1);
        double v = Math.Clamp((z + WorldLayout.HalfWorldSize) * scale, 0, Size - 1);
        int c = Math.Min((int)u, Size - 2), r = Math.Min((int)v, Size - 2);
        double fu = u - c, fv = v - r;
        double h = Sample(c, r) * (1 - fu) * (1 - fv) + Sample(c + 1, r) * fu * (1 - fv) +
                   Sample(c, r + 1) * (1 - fu) * fv + Sample(c + 1, r + 1) * fu * fv;
        return (float)(h * (WorldLayout.MaxHeight / ushort.MaxValue));
    }

    /// <summary>
    /// Every <paramref name="step"/>-th sample on both axes (point sampling, starting at 0), streaming the file
    /// once. Returns a row-major grid of <c>(Size - 1) / step + 1</c> samples per side.
    /// </summary>
    public ushort[] Downsample(int step, out int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        size = (Size - 1) / step + 1;
        var result = new ushort[size * size];
        var bytes = new byte[Image.BytesPerRow];
        var row = new ushort[Size];
        for (int r = 0, y = 0; y < size; r += step, y++)
        {
            Image.ReadRowBytes(r, bytes);
            Image.DecodeRow(bytes, row);
            for (int x = 0; x < size; x++)
                result[y * size + x] = row[x * step];
        }
        return result;
    }

    /// <summary>Calls <paramref name="visit"/> with every row in order (the span is reused between calls).</summary>
    public void ForEachRow(Action<int, ReadOnlySpan<ushort>> visit)
    {
        var bytes = new byte[Image.BytesPerRow];
        var row = new ushort[Size];
        for (int r = 0; r < Size; r++)
        {
            Image.ReadRowBytes(r, bytes);
            Image.DecodeRow(bytes, row);
            visit(r, row);
        }
    }

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// A square tile of raw little-endian unsigned 16-bit samples without a header, as in the legacy
/// <c>data/land/grasssplits/fullmap.X.Y.raw</c> (257 × 257). Neighbouring tiles share their edge samples.
/// </summary>
public sealed class RawHeightTile
{
    RawHeightTile(int size, ushort[] samples)
    {
        Size = size;
        Samples = samples;
    }

    public int Size { get; }

    /// <summary>Row-major samples, <see cref="Size"/> × <see cref="Size"/>.</summary>
    public ushort[] Samples { get; }

    public ushort this[int column, int row] => Samples[row * Size + column];

    public static RawHeightTile ReadFile(string path) => Read(File.ReadAllBytes(path));

    public static RawHeightTile Read(ReadOnlySpan<byte> data)
    {
        int size = (int)Math.Round(Math.Sqrt(data.Length / 2.0));
        if (data.Length % 2 != 0 || size * size * 2 != data.Length)
            throw new InvalidDataException($"{data.Length} bytes is not a square grid of 16-bit samples.");
        var samples = new ushort[size * size];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
        return new RawHeightTile(size, samples);
    }

    /// <summary>Parses the tile indices from <c>fullmap.X.Y.raw</c>.</summary>
    public static bool TryParseFileName(string path, out int x, out int y)
    {
        x = y = 0;
        var parts = Path.GetFileName(path).Split('.');
        return parts is [_, var a, var b, var ext] && ext.Equals("raw", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(a, out x) && int.TryParse(b, out y);
    }
}
