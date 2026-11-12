using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// The part with the heights under and over a point.
/// </summary>
public sealed partial class FloorSurfaces
{
    /// <summary>
    /// The height of the highest floor or stairs triangle straight below (<paramref name="x"/>, <paramref name="z"/>) at or under
    /// <paramref name="top"/>, or null when there is none: what a ray cast down from <paramref name="top"/> hits on this building (docs/audio.md
    /// "Surface", the footstep surface).
    /// </summary>
    public float? HighestBelow(float x, float z, float top)
    {
        float best = float.NegativeInfinity;
        var q = new Vector2(x, z);

        foreach (var s in _surfaces)
        {
            if (q.X < s.Min.X || q.Y < s.Min.Y || q.X > s.Max.X || q.Y > s.Max.Y || s.YMin > top) continue;

            foreach (int i in s.Grid.At(q))
            {
                if (HeightAt(s.Corners[i], s.Corners[i + 1], s.Corners[i + 2], q) is { } y && y <= top && y > best)
                    best = y;
            }
        }

        return float.IsNegativeInfinity(best) ? null : best;
    }

    /// <summary>
    /// The height of the highest ordinary part's triangle (no stairs) at (<paramref name="x"/>, <paramref name="z"/>) on floors up to
    /// <paramref name="floor"/>, or null: the building's ground for the strategy camera (docs/formats/camera.md "Height").
    /// </summary>
    public float? HighestUpTo(float x, float z, int floor)
    {
        float best = float.NegativeInfinity;
        var q = new Vector2(x, z);

        foreach (var s in _surfaces)
        {
            if (s.Floor > floor || s.Stairs || q.X < s.Min.X || q.Y < s.Min.Y || q.X > s.Max.X || q.Y > s.Max.Y || s.YMax <= best)
                continue;
            foreach (int i in s.Grid.At(q))
                if (HeightAt(s.Corners[i], s.Corners[i + 1], s.Corners[i + 2], q) is { } y && y > best) best = y;
        }

        return float.IsNegativeInfinity(best) ? null : best;
    }

    /// <summary>
    /// The height of the lowest floor, stairs or unwalkable roof triangle straight above (<paramref name="x"/>, <paramref name="z"/>) at or over
    /// <paramref name="bottom"/>, or null when there is none: what covers a point inside the building (<see cref="InteriorVolume.Covered"/>).
    /// </summary>
    public float? LowestAbove(float x, float z, float bottom, int maxFloor = int.MaxValue) => Lowest(x, z, bottom, [_surfaces, _roofs], maxFloor);

    /// <summary>
    /// <see cref="LowestAbove"/> with the solids' undersides: the ceiling straight above a point, the underside of the slab or roof over it rather
    /// than its top (a ceiling ghost hangs from it, docs/game/buildings-production.md "Placement").
    /// </summary>
    public float? CeilingAbove(float x, float z, float bottom) => Lowest(x, z, bottom, [_surfaces, _roofs, _undersides]);

    /// <summary>
    /// The lowest triangle height over (<paramref name="x"/>, <paramref name="z"/>) at or above <paramref name="bottom"/> among
    /// <paramref name="lists"/>, on floors up to <paramref name="maxFloor"/>; null when there is none.
    /// </summary>
    float? Lowest(float x, float z, float bottom, ReadOnlySpan<List<Surface>> lists, int maxFloor = int.MaxValue)
    {
        float best = float.PositiveInfinity;
        var q = new Vector2(x, z);

        foreach (var list in lists)
        {
            foreach (var s in list)
            {
                if (s.Floor > maxFloor) continue;
                if (q.X < s.Min.X || q.Y < s.Min.Y || q.X > s.Max.X || q.Y > s.Max.Y || s.YMax < bottom) continue;

                foreach (int i in s.Grid.At(q))
                {
                    if (HeightAt(s.Corners[i], s.Corners[i + 1], s.Corners[i + 2], q) is { } y && y >= bottom && y < best)
                        best = y;
                }
            }
        }

        return float.IsPositiveInfinity(best) ? null : best;
    }
}