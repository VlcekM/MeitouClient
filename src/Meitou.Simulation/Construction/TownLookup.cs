using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Construction;

/// <summary>
/// Which town a building or a point belongs to (<c>FUN_1400f6c20</c>, <c>FUN_1409f9100</c>, <c>FUN_1409f9180</c>, <b>Observed</b>): the town of the
/// building's root object when its handle resolves, else the NEAREST non-nest town by horizontal distance, whatever its owner and however far.
/// </summary>
public static class TownLookup
{
    /// <summary>
    /// The key <see cref="KeyOf"/> gives a building of a town of the level data that has no player town number (a bought NPC-town house).
    /// </summary>
    public const int LevelOwned = int.MinValue;

    /// <summary>
    /// The nearest town to <paramref name="at"/> by horizontal distance: a player town's number, a town of the level data <c>-(index + 1)</c> (nests
    /// excluded), 0 when the world has none. A tie goes to the player town, then to the lower index.
    /// </summary>
    public static int NearestKey(World world, Vector3 at)
    {
        int found = 0;
        float best = float.MaxValue;
        var here = new Vector2(at.X, at.Z);
        foreach (var t in world.Buildings.Towns)
        {
            float d = Vector2.DistanceSquared(new Vector2(t.Position.X, t.Position.Z), here);
            if (d < best) (found, best) = (t.Number, d);
        }

        if (world.SystemOf<PopulationSystem>()?.Data.Sites is not { } sites) return found;

        foreach (var t in sites)
        {
            if (t.Town.Type is TownType.Nest or TownType.NestMarker) continue;

            float d = Vector2.DistanceSquared(new Vector2(t.Position.X, t.Position.Z), here);
            if (d < best) (found, best) = (-(t.Index + 1), d);
        }

        return found;
    }

    /// <summary>
    /// The town a building belongs to: its <see cref="BuildSite.Town"/> while that outpost exists; a building of the level data without one is
    /// <see cref="LevelOwned"/> (its level town's, by its saved handle in the original); any other building (a handle that is null or dangling, as
    /// after an overlapping outpost was removed on load) takes the nearest town (<see cref="NearestKey"/>).
    /// </summary>
    public static int KeyOf(World world, BuildSite site)
    {
        if (site.Town != 0 && world.Buildings.FindTown(site.Town) is not null) return site.Town;

        return site.LevelPlacement is not null ? LevelOwned : NearestKey(world, site.Position);
    }

    /// <summary>
    /// The player outpost a building belongs to (<see cref="KeyOf"/>); null when it belongs to a town of the level data or to none.
    /// </summary>
    public static PlayerTown? PlayerTownOf(World world, BuildSite site) =>
        KeyOf(world, site) is > 0 and var key ? world.Buildings.FindTown(key) : null;
}
