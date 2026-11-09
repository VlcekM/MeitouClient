using System.Numerics;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Construction;

/// <summary>
/// How near a worker must stand to a building to build, operate or mine it (docs/game/buildings-production.md "Work distance"): within the building's reach of
/// it, twice that once the worker has stopped (the original's <c>isMoving</c> doubling). Engine choice: the gap is to the building's footprint hull (the
/// original measures to its position marker and also counts a worker touching its collision), else to its position.
/// </summary>
public static class SiteReach
{
    /// <summary>
    /// The construction reach of any building (<c>Building::getReachRange</c>, <b>Observed</b>).
    /// </summary>
    public const float Building = 40;

    /// <summary>
    /// The construction reach of a wall (<c>WallBuilding::getReachRange</c>, <b>Observed</b>).
    /// </summary>
    public const float Wall = 60;

    /// <summary>
    /// The X/Z gap from <paramref name="from"/> to the site: its footprint hull by <paramref name="edge"/> (0 inside), else its position.
    /// </summary>
    public static float Gap(Func<BuildSite, Vector2, float?>? edge, BuildSite site, Vector3 from) =>
        edge?.Invoke(site, new Vector2(from.X, from.Z)) ?? Vector2.Distance(new Vector2(from.X, from.Z), new Vector2(site.Position.X, site.Position.Z));

    /// <summary>
    /// Within <paramref name="reach"/>, or twice it when the character has no path (it has stopped).
    /// </summary>
    public static bool Within(in CharacterHot n, float gap, float reach) =>
        gap < reach || ((n.Flags & (ushort)MoveFlags.AnyPath) == 0 && gap < 2 * reach);

    /// <summary>
    /// The height difference below which a worker has arrived at a building when it is not on the building's floor (<c>AI::isWithinChoppingRange</c>,
    /// <b>Verified</b> <c>FUN_14059c6d0</c>): 130 for a door or a building with a door, else 45 for a building standing outside any building, else 15 (the
    /// original's ceiling-mounted class, <c>vfunc 0x428</c>, also gets 45; it is not read).
    /// </summary>
    public static float HeightLimit(bool door, bool inBuilding) => door ? 130 : inBuilding ? 15 : 45;

    /// <summary>
    /// Whether the worker is at the site's height: Meitou has no floor id, so a height difference below <see cref="HeightLimit"/> stands for "the same floor, or
    /// close enough" (the original passes the same floor whatever the difference).
    /// </summary>
    public static bool AtHeight(in CharacterHot n, BuildSite site, bool door) => MathF.Abs(n.Position.Y - site.Position.Y) < HeightLimit(door, site.Parent is not null);
}
