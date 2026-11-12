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
    /// <summary>
    /// Cells per side of the map.
    /// </summary>
    public const int Cells = 64;
    /// <summary>
    /// The edge of a cell in world units: one zone.
    /// </summary>
    public const float CellSize = 4608f;
    /// <summary>
    /// Pixels of <c>areasmap.tga</c> per cell side.
    /// </summary>
    public const int PixelsPerCell = 4;
    /// <summary>
    /// The cell index of world coordinate 0 on each axis: <c>cell = floor(coord / 4608) + 32</c>.
    /// </summary>
    public const int Origin = 32;
    /// <summary>
    /// The map's path under the data folder.
    /// </summary>
    public const string RelativePath = "newland/land/areasmap.tga";
    /// <summary>
    /// The offset of the camera's region lookup (<see cref="CameraCellOf"/>): 32 × 4608 + 9.
    /// </summary>
    const float CameraOrigin = 147465f;
    /// <summary>
    /// The world's half extent the map spans on each axis (<c>FUN_1408fb580</c>: the rectangle (-147456, -147456) to (147456, 147456)).
    /// </summary>
    const float HalfExtent = 147456f;

    /// <summary>
    /// Each cell's colour (0xRRGGBB), row-major (z, then x).
    /// </summary>
    readonly int[] _colours;
    /// <summary>
    /// Whether each pixel of the map is not black, row-major (z, then x); null: every point counts as painted.
    /// </summary>
    readonly bool[]? _painted;
    /// <summary>
    /// The pixels per side of <see cref="_painted"/>.
    /// </summary>
    readonly int _side;

    /// <param name="colours">Row-major (z, then x), 64 × 64 entries, 0xRRGGBB.</param>
    /// <param name="painted">Each pixel's not-black flag, row-major, <paramref name="side"/> squared entries; null: every point is painted.</param>
    /// <param name="side">The pixels per side of <paramref name="painted"/>.</param>
    public WeatherAreas(int[] colours, bool[]? painted = null, int side = 0)
    {
        if (colours.Length != Cells * Cells) throw new ArgumentException($"Expected {Cells * Cells} cells.", nameof(colours));
        if (painted is not null && painted.Length != side * side) throw new ArgumentException("Expected side x side pixels.", nameof(painted));
        this._colours = colours;
        _painted = painted;
        _side = side;
    }

    /// <summary>
    /// The map of a game install: <c>areasmap.tga</c> of resource group <c>Overlaymaps</c> (<b>Verified</b> <c>FUN_140408fa0</c>,
    /// <c>FUN_140408970</c>: Ogre's <c>resourceExists</c> then <c>Image::load</c> there, so an enabled mod's <c>newland/land/areasmap.tga</c>
    /// replaces it), else <c>data/newland/land/areasmap.tga</c>.
    /// </summary>
    public static WeatherAreas Load(GameInstall install) =>
        FromTga(File.ReadAllBytes(Ogre.GameResources.Find(install, "areasmap.tga", "Overlaymaps")
            ?? Path.Combine(install.DataDirectory, RelativePath)));

    /// <summary>
    /// The map of a TGA file's bytes.
    /// </summary>
    public static WeatherAreas FromTga(ReadOnlySpan<byte> tga) => FromImage(TgaReader.Read(tga));

    /// <summary>
    /// Takes each cell's colour from the pixel at the cell's centre (<b>Observed</b>: 3051 of the 4096 4 × 4 blocks are one colour; the rest sit on
    /// region borders).
    /// </summary>
    public static WeatherAreas FromImage(RgbaImage image)
    {
        if (image.Width % Cells != 0 || image.Height % Cells != 0)
            throw new FormatException($"The areas map is {image.Width} x {image.Height}, not a multiple of {Cells}.");
        int pw = image.Width / Cells, ph = image.Height / Cells;
        var colours = new int[Cells * Cells];
        for (int z = 0; z < Cells; z++)
            for (int x = 0; x < Cells; x++)
            {
                var (r, g, b, _) = image[x * pw + pw / 2, z * ph + ph / 2];
                colours[z * Cells + x] = r << 16 | g << 8 | b;
            }

        return image.Width == image.Height ? new WeatherAreas(colours, Painted(image), image.Width) : new WeatherAreas(colours);
    }

    /// <summary>
    /// Each pixel's not-black flag, row-major.
    /// </summary>
    static bool[] Painted(RgbaImage image)
    {
        var painted = new bool[image.Width * image.Height];
        for (int z = 0; z < image.Height; z++)
            for (int x = 0; x < image.Width; x++)
            {
                var (r, g, b, _) = image[x, z];
                painted[z * image.Width + x] = (r | g | b) != 0;
            }

        return painted;
    }

    /// <summary>
    /// Whether the map's pixel at world (<paramref name="x"/>, <paramref name="z"/>) is not black: the spawn-spot test of <c>FUN_1408f4d70</c>
    /// (<b>Verified</b>: the pixel <c>(int)((coordinate + 147456) / (294912 / pixels))</c> per axis, a coordinate below the map taking pixel 0 and
    /// one beyond it the last, any of its three bytes non-zero).
    /// </summary>
    public bool IsPainted(float x, float z) => _painted is null || _painted[PixelOf(z) * _side + PixelOf(x)];

    /// <summary>
    /// The pixel row or column of a world coordinate (<see cref="IsPainted"/>).
    /// </summary>
    int PixelOf(float coordinate)
    {
        if (coordinate < -HalfExtent) return 0;
        if (coordinate > HalfExtent) return _side - 1;
        return Math.Min((int)((coordinate + HalfExtent) / (2 * HalfExtent / _side)), _side - 1);
    }

    /// <summary>
    /// The cell holding a world position (may lie outside 0..63 for positions off the map).
    /// </summary>
    public static (int X, int Z) CellOf(float x, float z) => ((int)MathF.Floor(x / CellSize) + Origin, (int)MathF.Floor(z / CellSize) + Origin);

    /// <summary>
    /// The cell of the camera's region lookup (<c>FUN_1408f56b0</c>): <c>(int)((coordinate + 147465) / 4608)</c> truncated toward zero and clamped to
    /// 0..63 per axis, so a position off the map takes the edge cell. The origin 147465 = 32 x 4608 + 9 puts the original's cell boundaries 9 units
    /// from <see cref="CellOf"/>'s.
    /// </summary>
    public static (int X, int Z) CameraCellOf(float x, float z) =>
        (Math.Clamp((int)((x + CameraOrigin) / CellSize), 0, Cells - 1), Math.Clamp((int)((z + CameraOrigin) / CellSize), 0, Cells - 1));

    /// <summary>
    /// Whether cell (<paramref name="cx"/>, <paramref name="cz"/>) lies on the map.
    /// </summary>
    public static bool InMap(int cx, int cz) => (uint)cx < Cells && (uint)cz < Cells;

    /// <summary>
    /// The colour (0xRRGGBB) of a cell, or -1 outside the map.
    /// </summary>
    public int ColourOf(int cx, int cz) => InMap(cx, cz) ? _colours[cz * Cells + cx] : -1;

    /// <summary>
    /// The cell's rectangle in world units: minimum X, minimum Z, maximum X, maximum Z.
    /// </summary>
    public static (float MinX, float MinZ, float MaxX, float MaxZ) CellRect(int cx, int cz) =>
        ((cx - Origin) * CellSize, (cz - Origin) * CellSize, (cx - Origin + 1) * CellSize, (cz - Origin + 1) * CellSize);

    /// <summary>
    /// The squared distance in the ground plane from a position to a cell's rectangle (0 inside it).
    /// </summary>
    public static float DistanceSquaredToCell(int cx, int cz, float x, float z)
    {
        var (minX, minZ, maxX, maxZ) = CellRect(cx, cz);
        float dx = MathF.Max(MathF.Max(minX - x, x - maxX), 0), dz = MathF.Max(MathF.Max(minZ - z, z - maxZ), 0);
        return dx * dx + dz * dz;
    }

    /// <summary>
    /// The region of a cell; cells outside the map and colours without a BIOME_GROUP are in <see cref="WeatherData.None"/>.
    /// </summary>
    public RegionDef RegionOf(WeatherData data, int cx, int cz) => InMap(cx, cz) ? data.RegionOfColour(_colours[cz * Cells + cx]) : data.None;

    /// <summary>
    /// The region at world (<paramref name="x"/>, <paramref name="z"/>) (<see cref="CellOf"/>).
    /// </summary>
    public RegionDef RegionAt(WeatherData data, float x, float z)
    {
        var (cx, cz) = CellOf(x, z);
        return RegionOf(data, cx, cz);
    }
}