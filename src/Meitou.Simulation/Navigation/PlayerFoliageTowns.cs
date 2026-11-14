using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The towns the player's buildings made (the original's <c>player_town_template</c> outposts, at their current centre) as the foliage placer sees
/// them: the original's town list is the live one, so a mesh with <c>avoid towns</c> is rejected within the nearest such town's <c>no-foliage
/// range</c> too (docs/formats/foliage.md "Towns"). Immutable; <see cref="NavRuntimeBuildings"/> holds the current one.
/// </summary>
public sealed class PlayerFoliageTowns
{
    /// <summary>
    /// How far (units) past a town's reach a zone's mesh can still depend on it: the gather margin (72) plus how far a foliage object may stand
    /// past it (200), rounded up.
    /// </summary>
    public const float MeshPad = 300;

    /// <summary>
    /// No player town.
    /// </summary>
    public static PlayerFoliageTowns None { get; } = new([], 0);

    /// <summary>
    /// The towns: X/Z, <c>no-foliage range</c> and whether the player's faction owns the town.
    /// </summary>
    public IReadOnlyList<FoliageTown> Towns { get; }

    /// <summary>
    /// The largest <c>no-foliage range</c> of the level's towns. A town that becomes the nearest one for a point inside a level town's range changes
    /// that point's verdict, so a town's effect reaches this far even when its own range is shorter.
    /// </summary>
    public float LevelRange { get; }

    /// <summary>
    /// The towns <paramref name="towns"/> over a level whose towns reach at most <paramref name="levelRange"/>.
    /// </summary>
    public PlayerFoliageTowns(IReadOnlyList<FoliageTown> towns, float levelRange)
    {
        Towns = towns;
        LevelRange = levelRange;
    }

    /// <summary>
    /// How far a point can be from <paramref name="town"/> and still have its placement decided differently by it.
    /// </summary>
    public float Reach(FoliageTown town) => MathF.Max(town.Range, LevelRange);

    /// <summary>
    /// Whether <paramref name="town"/> can change foliage inside the box <paramref name="min"/>..<paramref name="max"/> grown by
    /// <paramref name="pad"/> (0 for the box's own placed foliage, <see cref="MeshPad"/> for what a navmesh built from the box sees).
    /// </summary>
    public bool Affects(FoliageTown town, Vector2 min, Vector2 max, float pad)
    {
        float dx = MathF.Max(MathF.Max(min.X - town.At.X, town.At.X - max.X), 0);
        float dz = MathF.Max(MathF.Max(min.Y - town.At.Y, town.At.Y - max.Y), 0);
        float reach = Reach(town) + pad;
        return dx * dx + dz * dz <= reach * reach;
    }

    /// <summary>
    /// The towns in either list but not the other (a town founded, gone, moved, resized or taken over).
    /// </summary>
    public static List<FoliageTown> Difference(IReadOnlyList<FoliageTown> a, IReadOnlyList<FoliageTown> b)
    {
        var list = new List<FoliageTown>();
        list.AddRange(a.Where(t => !b.Contains(t)));
        list.AddRange(b.Where(t => !a.Contains(t)));
        return list;
    }

    /// <summary>
    /// The towns that can change the zone's navmesh, in a fixed order.
    /// </summary>
    public IEnumerable<FoliageTown> NearZone(ZoneCoordinate zone)
    {
        var (min, max) = Box(zone);
        return Towns.Where(t => Affects(t, min, max, MeshPad)).OrderBy(t => t.At.X).ThenBy(t => t.At.Y);
    }

    /// <summary>
    /// The zones whose navmesh <paramref name="town"/> can change.
    /// </summary>
    public IEnumerable<ZoneCoordinate> ZonesOf(FoliageTown town)
    {
        float r = Reach(town) + MeshPad;
        var low = WorldLayout.ZoneOf(town.At.X - r, town.At.Y - r);
        var high = WorldLayout.ZoneOf(town.At.X + r, town.At.Y + r);
        for (int y = low.Y; y <= high.Y; y++)
        {
            for (int x = low.X; x <= high.X; x++)
            {
                var zone = new ZoneCoordinate(x, y);
                var (min, max) = Box(zone);
                if (zone.IsInsideGrid && Affects(town, min, max, MeshPad)) yield return zone;
            }
        }
    }

    /// <summary>
    /// A zone's X/Z box.
    /// </summary>
    public static (Vector2 Min, Vector2 Max) Box(ZoneCoordinate zone)
    {
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var min = new Vector2((float)ox, (float)oz);
        return (min, min + new Vector2(WorldLayout.ZoneSize));
    }
}
