using System.Numerics;
using Meitou.Content;
using Meitou.Data.Textures;

namespace Meitou.Data.World;

/// <summary>
/// The terrain the foliage placer sees in one zone (docs/formats/foliage.md, "Ground"): a 129 × 129 height grid 36 units
/// apart from the zone's minimum corner (every second heightmap sample), with the game's bilinear height, its slope
/// measure (the larger height step to the previous sample along X and the next one along Z, in units per 36 units) and a
/// normal from central differences.
/// </summary>
public sealed class FoliageGround
{
    public const int Size = 129;
    public const float Spacing = 36;

    readonly float[] heights;

    public FoliageGround(float x0, float z0, float[] heights)
    {
        if (heights.Length != Size * Size) throw new ArgumentException($"{heights.Length} heights for a {Size}² grid.", nameof(heights));
        (X0, Z0, this.heights) = (x0, z0, heights);
    }

    public float X0 { get; }
    public float Z0 { get; }

    /// <summary>The 129 x 129 grid, row = +Z (for the layout cache).</summary>
    internal ReadOnlySpan<float> Heights => heights;

    /// <summary>The zone's grid from the heightmap (reads 129 rows of the file).</summary>
    public static FoliageGround Read(TerrainHeightmap map, ZoneCoordinate zone)
    {
        var (x0, z0) = WorldLayout.ZoneOrigin(zone);
        var window = map.ReadWindow(zone.X * WorldLayout.SamplesPerZone, zone.Y * WorldLayout.SamplesPerZone, Size, Size, 2);
        var h = new float[Size * Size];
        for (int i = 0; i < h.Length; i++) h[i] = WorldLayout.RawToHeight(window.Raw[i]);
        return new FoliageGround((float)x0, (float)z0, h);
    }

    public float At(int i, int j) => heights[Math.Clamp(j, 0, Size - 1) * Size + Math.Clamp(i, 0, Size - 1)];

    /// <summary>Bilinear height, the point clamped to the grid.</summary>
    public float Height(float x, float z)
    {
        float u = Math.Clamp(x, X0, X0 + (Size - 1) * Spacing) - X0, v = Math.Clamp(z, Z0, Z0 + (Size - 1) * Spacing) - Z0;
        u /= Spacing;
        v /= Spacing;
        float fu = MathF.Floor(u), fv = MathF.Floor(v);
        int i = (int)fu, j = (int)fv;
        int i1 = Math.Min(i + 1, Size - 1), j1 = Math.Min(j + 1, Size - 1);
        float tu = u - fu, tv = v - fv;
        float top = (float)(tu * heights[j * Size + i1] + heights[j * Size + i] * (1.0 - tu));
        float bottom = (float)(tu * heights[j1 * Size + i1] + heights[j1 * Size + i] * (1.0 - tu));
        return (float)(top * (1.0 - tv) + bottom * tv);
    }

    /// <summary>The game's slope: max(|h − h(i−1, j)|, |h − h(i, j+1)|) at the sample the point falls in (truncated, clamped).</summary>
    public float Slope(float x, float z)
    {
        float u = Math.Clamp((x - X0) / Spacing, 0, Size - 1), v = Math.Clamp((z - Z0) / Spacing, 0, Size - 1);
        int i = (int)u, j = (int)v;
        float h = heights[j * Size + i];
        float left = i > 0 ? heights[j * Size + i - 1] : h;
        float down = j < Size - 1 ? heights[(j + 1) * Size + i] : h;
        return Math.Max(MathF.Abs(h - left), MathF.Abs(h - down));
    }

    /// <summary>Surface normal at a point from the heights around it (central differences one grid step apart).</summary>
    public Vector3 Normal(float x, float z)
    {
        float dx = (Height(x + Spacing, z) - Height(x - Spacing, z)) / (2 * Spacing);
        float dz = (Height(x, z + Spacing) - Height(x, z - Spacing)) / (2 * Spacing);
        return Vector3.Normalize(new Vector3(-dx, 1, -dz));
    }
}

/// <summary>
/// The overlay map (<c>new_overlay.X.Y.png</c>, 36 units per pixel) around one zone as the foliage placer reads and writes it
/// (docs/formats/foliage.md, "Overlay"): R grass coverage (generated from the GRASS noise), G grass spots, B dirt, A road.
/// Pixel lookups are clamped to the overlay tile the zone lies in, as the game's are.
/// </summary>
public sealed class FoliageOverlay
{
    public const float PixelSize = 36;
    public const int TilePixels = 1024;
    public const int Margin = 16;
    public const int ZonePixels = 128;
    public const int Window = ZonePixels + 1 + 2 * Margin;

    readonly byte[] rgba;

    /// <param name="tileRgba">The zone's overlay tile, RGBA rows of <see cref="TilePixels"/>, row = +Z.</param>
    public FoliageOverlay(ZoneCoordinate zone, byte[] tileRgba)
    {
        if (tileRgba.Length != TilePixels * TilePixels * 4) throw new ArgumentException("Not a 1024² RGBA overlay tile.", nameof(tileRgba));
        Zone = zone;
        int zonesPerTile = WorldLayout.ZoneCount / TerrainMaps.OverlayTiles;
        TileX = zone.X / zonesPerTile;
        TileZ = zone.Y / zonesPerTile;
        TileX0 = TileX * TilePixels * PixelSize - WorldLayout.HalfWorldSize;
        TileZ0 = TileZ * TilePixels * PixelSize - WorldLayout.HalfWorldSize;
        ZonePixelX = zone.X % zonesPerTile * ZonePixels;
        ZonePixelZ = zone.Y % zonesPerTile * ZonePixels;
        rgba = new byte[Window * Window * 4];
        for (int j = 0; j < Window; j++)
            for (int i = 0; i < Window; i++)
            {
                int ti = Math.Clamp(ZonePixelX - Margin + i, 0, TilePixels - 1), tj = Math.Clamp(ZonePixelZ - Margin + j, 0, TilePixels - 1);
                Buffer.BlockCopy(tileRgba, (tj * TilePixels + ti) * 4, rgba, (j * Window + i) * 4, 4);
            }
    }

    public ZoneCoordinate Zone { get; }
    public int TileX { get; }
    public int TileZ { get; }
    public float TileX0 { get; }
    public float TileZ0 { get; }
    /// <summary>Tile pixel of the zone's minimum corner.</summary>
    public int ZonePixelX { get; }
    public int ZonePixelZ { get; }

    /// <summary>Tile pixel column of world X (truncated, clamped to the tile), as the game computes it.</summary>
    public int PixelX(float x) => x < TileX0 ? 0 : x > TileX0 + TilePixels * PixelSize ? TilePixels - 1 : Math.Min((int)((x - TileX0) / PixelSize), TilePixels - 1);
    public int PixelZ(float z) => z < TileZ0 ? 0 : z > TileZ0 + TilePixels * PixelSize ? TilePixels - 1 : Math.Min((int)((z - TileZ0) / PixelSize), TilePixels - 1);

    /// <summary>World X/Z of a tile pixel's centre.</summary>
    public (float X, float Z) PixelCentre(int i, int j) => ((i + 0.5f) * PixelSize + TileX0, (j + 0.5f) * PixelSize + TileZ0);

    int Offset(int i, int j)
    {
        int wi = Math.Clamp(i - ZonePixelX + Margin, 0, Window - 1), wj = Math.Clamp(j - ZonePixelZ + Margin, 0, Window - 1);
        return (wj * Window + wi) * 4;
    }

    static bool InTile(int i, int j) => i is >= 0 and < TilePixels && j is >= 0 and < TilePixels;

    /// <summary>Channel value (0 R, 1 G, 2 B, 3 A) at tile pixel (i, j); 0 outside the tile.</summary>
    public byte Get(int channel, int i, int j) => InTile(i, j) ? rgba[Offset(i, j) + channel] : (byte)0;

    public void Set(int channel, int i, int j, byte value)
    {
        if (InTile(i, j)) rgba[Offset(i, j) + channel] = value;
    }

    /// <summary>Road weight at tile pixel (i, j).</summary>
    public byte Road(int i, int j) => Get(3, i, j);

    /// <summary>Grass coverage of the terrain at a world point: R + G (the game's test for "limit to grass areas").</summary>
    public int GrassAt(float x, float z) { int i = PixelX(x), j = PixelZ(z); return Get(0, i, j) + Get(1, i, j); }

    /// <summary>Clears the zone's 128² of R and G, as the game does before it generates grass coverage.</summary>
    public void ClearGrass()
    {
        for (int j = ZonePixelZ; j < ZonePixelZ + ZonePixels; j++)
            for (int i = ZonePixelX; i < ZonePixelX + ZonePixels; i++)
            {
                Set(0, i, j, 0);
                Set(1, i, j, 0);
            }
    }

    /// <summary>The zone's overlay tile as RGBA bytes.</summary>
    public static byte[] ReadTile(GameInstall install, ZoneCoordinate zone)
    {
        int zonesPerTile = WorldLayout.ZoneCount / TerrainMaps.OverlayTiles;
        var image = TextureLoader.LoadImage(File.ReadAllBytes(TerrainMaps.OverlayTile(install, "new_overlay", zone.X / zonesPerTile, zone.Y / zonesPerTile)));
        if (image.Width != TilePixels || image.Height != TilePixels) throw new InvalidDataException($"Overlay tile is {image.Width}x{image.Height}, not {TilePixels}².");
        return image.Pixels;
    }
}

/// <summary>Biome index colour at a world point from <c>biomemap.png</c> (1024², 288 units per pixel, row = +Z).</summary>
public sealed class FoliageBiomeMap(int size, byte[] rgba)
{
    public static FoliageBiomeMap Open(GameInstall install)
    {
        var image = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BiomeMap)));
        return new FoliageBiomeMap(image.Width, image.Pixels);
    }

    public uint At(float x, float z)
    {
        float cell = (float)WorldLayout.WorldSize / size;
        int i = Math.Clamp((int)((x + WorldLayout.HalfWorldSize) / cell), 0, size - 1), j = Math.Clamp((int)((z + WorldLayout.HalfWorldSize) / cell), 0, size - 1);
        int o = (j * size + i) * 4;
        return (uint)(rgba[o] << 16 | rgba[o + 1] << 8 | rgba[o + 2]);
    }
}
