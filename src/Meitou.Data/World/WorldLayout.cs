namespace Meitou.Data.World;

/// <summary>
/// Size and coordinate system of the Newland world (docs/formats/terrain.md, docs/formats/zones.md).
/// World X and Z are the ground plane, Y is up. The world is a square centred on the origin, cut into a
/// 64 × 64 grid of zones; the heightmap has one sample per 18 units, 256 samples per zone.
/// </summary>
public static class WorldLayout
{
    /// <summary>Zones per side of the grid; zone indices run 0..63 on both axes.</summary>
    public const int ZoneCount = 64;

    /// <summary>Edge length of one zone in world units.</summary>
    public const int ZoneSize = 4608;

    /// <summary>Edge length of the whole world: <see cref="ZoneCount"/> × <see cref="ZoneSize"/>.</summary>
    public const int WorldSize = ZoneCount * ZoneSize;

    /// <summary>The world spans [-HalfWorldSize, +HalfWorldSize] on X and Z.</summary>
    public const int HalfWorldSize = WorldSize / 2;

    /// <summary>Heightmap samples per zone edge.</summary>
    public const int SamplesPerZone = 256;

    /// <summary>Distance between neighbouring heightmap samples in world units.</summary>
    public const int SampleSpacing = ZoneSize / SamplesPerZone;

    /// <summary>Samples per heightmap edge (2^n + 1: the last row and column sit on the far world edge).</summary>
    public const int HeightmapSize = ZoneCount * SamplesPerZone + 1;

    /// <summary>World height of the largest raw heightmap value (65535); raw 0 is height 0.</summary>
    public const float MaxHeight = 9800f;

    /// <summary>World height of a raw 16-bit heightmap sample.</summary>
    public static float RawToHeight(ushort raw) => raw * (MaxHeight / ushort.MaxValue);

    /// <summary>The zone containing world point (<paramref name="x"/>, <paramref name="z"/>); may lie outside the grid.</summary>
    public static ZoneCoordinate ZoneOf(double x, double z) =>
        new((int)Math.Floor(x / ZoneSize) + ZoneCount / 2, (int)Math.Floor(z / ZoneSize) + ZoneCount / 2);

    /// <summary>World X/Z of the zone's minimum corner.</summary>
    public static (double X, double Z) ZoneOrigin(ZoneCoordinate zone) =>
        ((zone.X - ZoneCount / 2) * (double)ZoneSize, (zone.Y - ZoneCount / 2) * (double)ZoneSize);

    /// <summary>Fractional heightmap column and row of a world point (column follows +X, row follows +Z).</summary>
    public static (double Column, double Row) ToSample(double x, double z) =>
        ((x + HalfWorldSize) / SampleSpacing, (z + HalfWorldSize) / SampleSpacing);

    /// <summary>World X/Z of a heightmap sample.</summary>
    public static (double X, double Z) FromSample(double column, double row) =>
        (column * SampleSpacing - HalfWorldSize, row * SampleSpacing - HalfWorldSize);
}

/// <summary>A cell of the zone grid, as in <c>zone.X.Y.zone</c>: X follows world +X, Y follows world +Z.</summary>
public readonly record struct ZoneCoordinate(int X, int Y)
{
    public bool IsInsideGrid => X is >= 0 and < WorldLayout.ZoneCount && Y is >= 0 and < WorldLayout.ZoneCount;

    /// <summary>File name of the zone's level data, <c>zone.X.Y.zone</c>.</summary>
    public string FileName => $"zone.{X}.{Y}.zone";

    /// <summary>Parses <c>zone.X.Y.zone</c> (case-insensitive, any directory).</summary>
    public static bool TryParseFileName(string path, out ZoneCoordinate zone)
    {
        zone = default;
        var parts = Path.GetFileName(path).Split('.');
        if (parts is not [var prefix, var x, var y, var ext] ||
            !prefix.Equals("zone", StringComparison.OrdinalIgnoreCase) ||
            !ext.Equals("zone", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(x, System.Globalization.NumberStyles.None, null, out int zx) ||
            !int.TryParse(y, System.Globalization.NumberStyles.None, null, out int zy))
            return false;
        zone = new ZoneCoordinate(zx, zy);
        return true;
    }

    public override string ToString() => $"{X}.{Y}";
}
