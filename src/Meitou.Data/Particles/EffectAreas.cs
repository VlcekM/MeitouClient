using Meitou.Data.World;

namespace Meitou.Data.Particles;

/// <summary>
/// Where an effect may be placed: a random point of an area (the weather region's cells, or a disc round the camera).
/// </summary>
public interface IEffectArea
{
    /// <summary>
    /// A random point of the area, from <paramref name="random"/>; false when the area has none.
    /// </summary>
    bool TryPick(Random random, out float x, out float z);
}

/// <summary>
/// A disc (sqrt-uniform): the viewer's test override <c>--particle-area</c>, and the fallback when no region is known.
/// </summary>
public sealed class DiscArea(float centreX, float centreZ, float radius) : IEffectArea
{
    /// <inheritdoc/>
    public bool TryPick(Random random, out float x, out float z)
    {
        float a = random.NextSingle() * MathF.Tau, r = MathF.Sqrt(random.NextSingle()) * radius;
        x = centreX + MathF.Cos(a) * r;
        z = centreZ + MathF.Sin(a) * r;
        return true;
    }
}

/// <summary>
/// A weather region's cells (docs/formats/weather.md "Where"): a random point is taken in a uniformly chosen cell, inside the middle 80 % of it
/// (Verified (decompiled), FUN_1408fc3c0: a margin of 0.2 · 0.5 of the width on each side; the margin's sign in z is not resolved in the decompile,
/// both axes use it here).
/// </summary>
public sealed class RegionArea : IEffectArea
{
    /// <summary>
    /// The region's cells as rectangles in x and z.
    /// </summary>
    readonly (float X0, float Z0, float X1, float Z1)[] _rects;

    /// <summary>
    /// An area of the cells <paramref name="rects"/>.
    /// </summary>
    public RegionArea(IEnumerable<(float X0, float Z0, float X1, float Z1)> rects) => this._rects = [.. rects];
    /// <summary>
    /// How many cells the area has.
    /// </summary>
    public int Count => _rects.Length;

    /// <summary>
    /// The cells that have the colour of the cell at (<paramref name="x"/>, <paramref name="z"/>) (the game's region cells), or null off the map.
    /// </summary>
    public static RegionArea? At(WeatherAreas areas, float x, float z)
    {
        var (cx, cz) = WeatherAreas.CellOf(x, z);
        if (!WeatherAreas.InMap(cx, cz)) return null;
        int colour = areas.ColourOf(cx, cz);
        var list = new List<(float, float, float, float)>();
        for (int j = 0; j < WeatherAreas.Cells; j++)
            for (int i = 0; i < WeatherAreas.Cells; i++)
                if (areas.ColourOf(i, j) == colour) list.Add(WeatherAreas.CellRect(i, j));
        return new RegionArea(list);
    }

    /// <inheritdoc/>
    public bool TryPick(Random random, out float x, out float z)
    {
        x = z = 0;
        if (_rects.Length == 0) return false;
        var r = _rects[random.Next(_rects.Length)];
        float mx = (r.X1 - r.X0) * 0.1f, mz = (r.Z1 - r.Z0) * 0.1f;
        x = r.X0 + mx + random.NextSingle() * (r.X1 - r.X0 - 2 * mx);
        z = r.Z0 + mz + random.NextSingle() * (r.Z1 - r.Z0 - 2 * mz);
        return true;
    }
}
