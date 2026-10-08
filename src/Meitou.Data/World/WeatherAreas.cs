using Meitou.Content;
using Meitou.Data.Textures;

namespace Meitou.Data.World;

/// <summary>
/// The weather regions on the map: a 64 × 64 grid of zone cells (4608 units each), the colour of each cell read from <c>areasmap.tga</c>
/// (docs/formats/weather.md "Where"). Cell (0, 0) is the zone with the lowest X and Z of the world ([-147456, 147456) on both axes); the map's rows
/// run along +Z, no flip (<b>Verified</b> against the town positions in <c>WeatherTests.Areas_map_places_known_towns_in_their_regions</c>).
/// </summary>
public sealed class WeatherAreas
{
    public const int Cells = 64;
    public const float CellSize = 4608f;
    public const int PixelsPerCell = 4;
    /// <summary>The cell index of world coordinate 0 on each axis: <c>cell = floor(coord / 4608) + 32</c>.</summary>
    public const int Origin = 32;
    public const string RelativePath = "newland/land/areasmap.tga";

    readonly int[] colours;

    /// <param name="colours">Row-major (z, then x), 64 × 64 entries, 0xRRGGBB.</param>
    public WeatherAreas(int[] colours)
    {
        if (colours.Length != Cells * Cells) throw new ArgumentException($"Expected {Cells * Cells} cells.", nameof(colours));
        this.colours = colours;
    }

    /// <summary>The map of a game install (<c>data/newland/land/areasmap.tga</c>).</summary>
    public static WeatherAreas Load(GameInstall install) => FromTga(File.ReadAllBytes(Path.Combine(install.DataDirectory, RelativePath)));

    public static WeatherAreas FromTga(ReadOnlySpan<byte> tga) => FromImage(TgaReader.Read(tga));

    /// <summary>Takes each cell's colour from the pixel at the cell's centre (<b>Observed</b>: 3051 of the 4096 4 × 4 blocks are one colour; the rest sit on region borders).</summary>
    public static WeatherAreas FromImage(RgbaImage image)
    {
        if (image.Width % Cells != 0 || image.Height % Cells != 0) throw new FormatException($"The areas map is {image.Width} x {image.Height}, not a multiple of {Cells}.");
        int pw = image.Width / Cells, ph = image.Height / Cells;
        var colours = new int[Cells * Cells];
        for (int z = 0; z < Cells; z++)
            for (int x = 0; x < Cells; x++)
            {
                var (r, g, b, _) = image[x * pw + pw / 2, z * ph + ph / 2];
                colours[z * Cells + x] = r << 16 | g << 8 | b;
            }
        return new WeatherAreas(colours);
    }

    /// <summary>The cell holding a world position (may lie outside 0..63 for positions off the map).</summary>
    public static (int X, int Z) CellOf(float x, float z) => ((int)MathF.Floor(x / CellSize) + Origin, (int)MathF.Floor(z / CellSize) + Origin);

    public static bool InMap(int cx, int cz) => (uint)cx < Cells && (uint)cz < Cells;

    /// <summary>The colour (0xRRGGBB) of a cell, or -1 outside the map.</summary>
    public int ColourOf(int cx, int cz) => InMap(cx, cz) ? colours[cz * Cells + cx] : -1;

    /// <summary>The cell's rectangle in world units: minimum X, minimum Z, maximum X, maximum Z.</summary>
    public static (float MinX, float MinZ, float MaxX, float MaxZ) CellRect(int cx, int cz) =>
        ((cx - Origin) * CellSize, (cz - Origin) * CellSize, (cx - Origin + 1) * CellSize, (cz - Origin + 1) * CellSize);

    /// <summary>The squared distance in the ground plane from a position to a cell's rectangle (0 inside it).</summary>
    public static float DistanceSquaredToCell(int cx, int cz, float x, float z)
    {
        var (minX, minZ, maxX, maxZ) = CellRect(cx, cz);
        float dx = MathF.Max(MathF.Max(minX - x, x - maxX), 0), dz = MathF.Max(MathF.Max(minZ - z, z - maxZ), 0);
        return dx * dx + dz * dz;
    }

    /// <summary>The region of a cell; cells outside the map and colours without a BIOME_GROUP are in <see cref="WeatherData.None"/>.</summary>
    public RegionDef RegionOf(WeatherData data, int cx, int cz) => InMap(cx, cz) ? data.RegionOfColour(colours[cz * Cells + cx]) : data.None;

    public RegionDef RegionAt(WeatherData data, float x, float z)
    {
        var (cx, cz) = CellOf(x, z);
        return RegionOf(data, cx, cz);
    }
}
