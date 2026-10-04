using System.Buffers.Binary;
using Meitou.Content;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class TerrainTests
{
    /// <summary>Builds a baseline greyscale TIFF: 16-bit samples in strips of <paramref name="rowsPerStrip"/> rows.</summary>
    static byte[] Tiff(int width, int height, Func<int, int, ushort> sample, bool littleEndian = true, int rowsPerStrip = 0, bool longSizes = false, int compression = 1)
    {
        if (rowsPerStrip <= 0) rowsPerStrip = height;
        int strips = (height + rowsPerStrip - 1) / rowsPerStrip;
        var ms = new MemoryStream();
        void U16(int v) { Span<byte> b = stackalloc byte[2]; if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); ms.Write(b); }
        void U32(long v) { Span<byte> b = stackalloc byte[4]; if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); else BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); ms.Write(b); }

        ms.Write(littleEndian ? "II"u8 : "MM"u8);
        U16(42);
        U32(0); // IFD offset, patched below

        // Pixel data first, strip by strip, with a padding byte between strips to catch offset mistakes.
        var offsets = new List<long>();
        var counts = new List<long>();
        for (int s = 0; s < strips; s++)
        {
            ms.WriteByte(0xEE);
            offsets.Add(ms.Position);
            int rows = Math.Min(rowsPerStrip, height - s * rowsPerStrip);
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < width; c++)
                    U16(sample(c, s * rowsPerStrip + r));
            counts.Add(rows * width * 2L);
        }
        long arrays = ms.Position;
        foreach (var o in offsets) U32(o);
        foreach (var c in counts) U32(c);
        if (ms.Position % 2 != 0) ms.WriteByte(0);
        long ifd = ms.Position;

        var entries = new List<(int Tag, int Type, long Count, long Value)>
        {
            (256, longSizes ? 4 : 3, 1, width),
            (257, longSizes ? 4 : 3, 1, height),
            (258, 3, 1, 16),
            (259, 3, 1, compression),
            (262, 3, 1, 1),
            (273, 4, strips, strips == 1 ? offsets[0] : arrays),
            (277, 3, 1, 1),
            (278, longSizes ? 4 : 3, 1, rowsPerStrip),
            (279, 4, strips, strips == 1 ? counts[0] : arrays + 4 * strips),
        };
        U16(entries.Count);
        foreach (var (tag, type, count, value) in entries)
        {
            U16(tag); U16(type); U32(count);
            if (type == 3 && count == 1) { U16((int)value); U16(0); } // SHORT values sit in the first 2 bytes
            else U32(value);
        }
        U32(0);
        var bytes = ms.ToArray();
        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)ifd);
        else BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)ifd);
        return bytes;
    }

    static ushort Pattern(int c, int r) => (ushort)(r * 1000 + c * 7);

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(false, 0, false)]
    [InlineData(true, 2, true)]
    [InlineData(false, 3, true)]
    public void Tiff_reader_reads_samples_in_either_byte_order_and_strip_layout(bool littleEndian, int rowsPerStrip, bool longSizes)
    {
        using var image = TiffImage.Open(new MemoryStream(Tiff(5, 7, Pattern, littleEndian, rowsPerStrip, longSizes)));
        Assert.Equal((5, 7, 16), (image.Width, image.Height, image.BitsPerSample));
        Assert.Equal(littleEndian, image.LittleEndian);
        Assert.Equal(rowsPerStrip == 0 ? 1 : (7 + rowsPerStrip - 1) / rowsPerStrip, image.StripCount);

        var row = new ushort[5];
        for (int r = 0; r < 7; r++)
        {
            image.ReadRow(r, row);
            Assert.Equal(Enumerable.Range(0, 5).Select(c => Pattern(c, r)), row);
        }
        Assert.Equal(Pattern(4, 6), image.ReadSample(4, 6));
        Assert.Equal(Pattern(0, 3), image.ReadSample(0, 3));
    }

    [Fact]
    public void Tiff_reader_rejects_what_it_cannot_decode()
    {
        Assert.Throws<NotSupportedException>(() => TiffImage.Open(new MemoryStream(Tiff(2, 2, Pattern, compression: 5))));
        Assert.Throws<InvalidDataException>(() => TiffImage.Open(new MemoryStream("PK\x03\x04xxxxxxxx"u8.ToArray())));
        var truncated = Tiff(4, 4, Pattern);
        Assert.Throws<InvalidDataException>(() => TiffImage.Open(new MemoryStream(truncated[..40])));
    }

    [Fact]
    public void Heightmap_interpolates_world_heights_bilinearly()
    {
        // 3 x 3 samples spanning the whole world: samples sit at -half, 0, +half on both axes.
        ushort[,] s = { { 0, 65535, 0 }, { 65535, 65535, 0 }, { 0, 0, 0 } }; // [row, column]
        using var map = TerrainHeightmap.Open(new MemoryStream(Tiff(3, 3, (c, r) => s[r, c])));
        const int h = WorldLayout.HalfWorldSize;
        Assert.Equal(0f, map.HeightAt(-h, -h));
        Assert.Equal(WorldLayout.MaxHeight, map.HeightAt(0, -h), 2);
        Assert.Equal(WorldLayout.MaxHeight, map.HeightAt(0, 0), 2);
        Assert.Equal(WorldLayout.MaxHeight / 2, map.HeightAt(h / 2, 0), 2);              // between (1,1) and (2,1)
        Assert.Equal(WorldLayout.MaxHeight * 3 / 4, map.HeightAt(-h / 2, -h / 2), 2);    // centre of the top-left cell
        Assert.Equal(0f, map.HeightAt(h, h));
        Assert.Equal(0f, map.HeightAt(h * 2.0, h * 2.0));                                 // clamped
        Assert.Equal([0, 65535, 0, 65535, 65535, 0, 0, 0, 0], map.Downsample(1, out int n1));
        Assert.Equal(3, n1);
        Assert.Equal([0, 0, 0, 0], map.Downsample(2, out int n2));
        Assert.Equal(2, n2);
    }

    [Fact]
    public void World_layout_maps_points_to_zones_and_samples()
    {
        Assert.Equal(294912, WorldLayout.WorldSize);
        Assert.Equal(18, WorldLayout.SampleSpacing);
        Assert.Equal(16385, WorldLayout.HeightmapSize);
        Assert.Equal(9800f, WorldLayout.RawToHeight(ushort.MaxValue), 2);
        Assert.Equal(new ZoneCoordinate(32, 32), WorldLayout.ZoneOf(0, 0));
        Assert.Equal(new ZoneCoordinate(31, 32), WorldLayout.ZoneOf(-0.01, 4607.9));
        Assert.Equal(new ZoneCoordinate(0, 63), WorldLayout.ZoneOf(-147456, 147455));
        Assert.Equal(new ZoneCoordinate(64, -1), WorldLayout.ZoneOf(147456, -147457));
        Assert.False(new ZoneCoordinate(64, 0).IsInsideGrid);
        Assert.Equal((-4608.0, 59904.0), WorldLayout.ZoneOrigin(new ZoneCoordinate(31, 45)));
        Assert.Equal((0.0, 16384.0), WorldLayout.ToSample(-147456, 147456));
        Assert.Equal((-147456.0, 0.0), WorldLayout.FromSample(0, 8192));
    }

    [Fact]
    public void Raw_tile_is_a_square_of_little_endian_samples()
    {
        var bytes = new byte[3 * 3 * 2];
        for (int i = 0; i < 9; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)(i * 300));
        var tile = RawHeightTile.Read(bytes);
        Assert.Equal(3, tile.Size);
        Assert.Equal(5 * 300, tile[2, 1]);
        Assert.Throws<InvalidDataException>(() => RawHeightTile.Read(new byte[10]));
        Assert.True(RawHeightTile.TryParseFileName("x/fullmap.10.31.raw", out int x, out int y));
        Assert.Equal((10, 31), (x, y));
    }

    // --- Base game -------------------------------------------------------------------------------

    [Fact]
    public void Base_game_heightmap_covers_the_zone_grid()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        using var map = TerrainHeightmap.Open(install!);
        Assert.Equal(WorldLayout.HeightmapSize, map.Size);
        Assert.Equal(WorldLayout.HeightmapSize, map.Image.Height);
        Assert.Equal(16, map.Image.BitsPerSample);
        Assert.True(map.Image.LittleEndian);
        // One uncompressed strip that ends exactly where the file's trailing metadata starts.
        Assert.Equal(1, map.Image.StripCount);
        Assert.True(map.Image.DataOffset + (long)map.Size * map.Size * 2 <= new FileInfo(Path.Combine(install!.DataDirectory, TerrainHeightmap.RelativePath)).Length);
        // The world border is sea level (raw 0); the corners and edge midpoints are all 0.
        foreach (var (c, r) in new[] { (0, 0), (16384, 0), (0, 16384), (16384, 16384), (8192, 0), (0, 8192) })
            Assert.Equal(0, map.Sample(c, r));
    }

    [Fact]
    public void Base_game_legacy_height_tiles_share_their_edges()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var tiles = Directory.EnumerateFiles(Path.Combine(install!.DataDirectory, "land", "grasssplits"), "*.raw")
            .ToDictionary(f => RawHeightTile.TryParseFileName(f, out int x, out int y) ? (x, y) : throw new InvalidDataException(f), RawHeightTile.ReadFile);
        Assert.Equal(256, tiles.Count);
        Assert.All(tiles.Values, t => Assert.Equal(257, t.Size));
        Assert.Equal((10, 25, 16, 31), (tiles.Keys.Min(k => k.x), tiles.Keys.Max(k => k.x), tiles.Keys.Min(k => k.y), tiles.Keys.Max(k => k.y)));
        foreach (var ((x, y), t) in tiles)
        {
            if (tiles.TryGetValue((x + 1, y), out var right))
                for (int k = 0; k < 257; k++) Assert.Equal(t[256, k], right[0, k]);
            if (tiles.TryGetValue((x, y + 1), out var below))
                for (int k = 0; k < 257; k++) Assert.Equal(t[k, 256], below[k, 0]);
        }
    }
}
