using System.Numerics;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Construction;

/// <summary>
/// How near a worker must stand to a building to build, operate or mine it (docs/game/buildings-production.md "Work distance"): within the building's
/// reach of it, twice that once the worker has stopped (the original's <c>isMoving</c> doubling). The gap is to the building's position or its
/// footprint hull, whichever is nearer (the original measures to its position marker and also counts a worker touching its collision; the hull
/// stands in for touching).
/// </summary>
public static class SiteReach
{
    #region Arrival

    /// <summary>
    /// The construction reach of any building (<c>Building::getReachRange</c>, <b>Observed</b>).
    /// </summary>
    public const float Building = 40;

    /// <summary>
    /// The construction reach of a wall (<c>WallBuilding::getReachRange</c>, <b>Observed</b>).
    /// </summary>
    public const float Wall = 60;

    /// <summary>
    /// The X/Z gap from <paramref name="from"/> to the site: the nearer of its position and its footprint hull by <paramref name="edge"/> (0 inside),
    /// as the original arrives by its position marker or by touching its collision. A ramp's hull lies 159..188 off its origin on the wall, so a
    /// builder standing on the origin was 164 off the hull and never arrived (#1432).
    /// </summary>
    public static float Gap(Func<BuildSite, Vector2, float?>? edge, BuildSite site, Vector3 from)
    {
        var point = new Vector2(from.X, from.Z);
        float marker = Vector2.Distance(point, new Vector2(site.Position.X, site.Position.Z));
        return edge?.Invoke(site, point) is { } hull ? MathF.Min(hull, marker) : marker;
    }

    /// <summary>
    /// Within <paramref name="reach"/>, or twice it when the character has no path (it has stopped).
    /// </summary>
    public static bool Within(in CharacterHot n, float gap, float reach) =>
        gap < reach || ((n.Flags & (ushort)MoveFlags.AnyPath) == 0 && gap < 2 * reach);

    /// <summary>
    /// The height difference below which a worker has arrived at a building when it is not on the building's floor (<c>AI::isWithinChoppingRange</c>,
    /// <b>Verified</b> <c>FUN_14059c6d0</c>): 130 for a door or a building with a door, else 45 for a building standing outside any building, else 15
    /// (the original's ceiling-mounted class, <c>vfunc 0x428</c>, also gets 45; it is not read).
    /// </summary>
    public static float HeightLimit(bool door, bool inBuilding) => door ? 130 : inBuilding ? 15 : 45;

    /// <summary>
    /// Whether the worker is at the site's height: a height difference below <see cref="HeightLimit"/> stands for "the same floor, or close enough"
    /// where Meitou knows no floor (the original passes the same floor whatever the difference; builders pass outdoors first,
    /// <c>ConstructionSystem.OnSiteFloor</c>).
    /// </summary>
    public static bool AtHeight(in CharacterHot n, BuildSite site, bool door) => AtHeight(n, site, site.Position.Y, door);

    /// <summary>
    /// <see cref="AtHeight(in CharacterHot, BuildSite, bool)"/> measured from <paramref name="floorY"/>, the height of the floor the site belongs to
    /// (a ceiling light hangs up to about 50 over its floor, #592).
    /// </summary>
    public static bool AtHeight(in CharacterHot n, BuildSite site, float floorY, bool door) =>
        MathF.Abs(n.Position.Y - floorY) < HeightLimit(door, InBuilding(site));

    /// <summary>
    /// Whether the site stands inside a building (the original's containing-building handle, slot 0x1d8).
    /// </summary>
    public static bool InBuilding(BuildSite site) => site.Parent is not null && !site.Outside;

    #endregion

    #region Work spot

    /// <summary>
    /// The spacing (units) of the rings and the number of directions per ring <see cref="WorkSpot"/> searches (engine choice: finer than a
    /// worker's body, coarse enough to search a builder's ring in a few hundred stand tests).
    /// </summary>
    const float SpotStep = 4;

    /// <summary>
    /// The directions on each ring <see cref="WorkSpot"/> searches.
    /// </summary>
    const int SpotDirections = 16;

    /// <summary>
    /// How far past the reach from the site's position <see cref="WorkSpot"/> searches (engine choice: the reach is measured to the footprint's
    /// edge, so a spot within the reach of a wide storage chest or bench lies further than the reach from its middle).
    /// </summary>
    const float SpotMargin = 40;

    /// <summary>
    /// The point a worker walks to for a site it works from <paramref name="reach"/> (the original walks to the site's position marker,
    /// docs/game/buildings-production.md "Where the walk to a building goes"): the site's own position on its floor (<paramref name="floorY"/>) when a
    /// worker can stand there within <paramref name="heightLimit"/> of the floor; else, of the points on rings round it out to the reach plus
    /// <see cref="SpotMargin"/> that lie on that floor and within the reach (<see cref="Gap"/>), the one nearest <paramref name="from"/>; else the
    /// site's position. Engine choice: furniture cuts holes in a floor's mesh (<see cref="IWalkability.FloorNear"/>), so a walk to the position
    /// itself can end on any surface within the path's goal floor slack (<see cref="Navigation.NavQuery.GoalFloorSlack"/>, 40), such as a stair
    /// beside it, where the arrival's height limit (<see cref="HeightLimit"/>, 15 inside a building) is never met.
    /// </summary>
    public static Vector3 WorkSpot(IWalkability walk, Func<BuildSite, Vector2, float?>? edge, BuildSite site, float floorY, float heightLimit,
        float reach, Vector3 from)
    {
        var at = site.Position;
        if (OnFloor(walk, at.X, at.Z, floorY, heightLimit, out float y)) return new Vector3(at.X, y, at.Z);

        return NearestOnFloor(walk, edge, site, floorY, heightLimit, reach, from) ?? at;
    }

    /// <summary>
    /// <see cref="WorkSpot"/>'s ring search: the point on its rings nearest <paramref name="from"/> that lies on the floor and within the reach; null
    /// when none does.
    /// </summary>
    static Vector3? NearestOnFloor(IWalkability walk, Func<BuildSite, Vector2, float?>? edge, BuildSite site, float floorY, float heightLimit,
        float reach, Vector3 from)
    {
        var at = site.Position;
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        for (float r = SpotStep; r <= reach + SpotMargin; r += SpotStep)
        {
            for (int k = 0; k < SpotDirections; k++)
            {
                float angle = k * 2 * MathF.PI / SpotDirections;
                float x = at.X + r * MathF.Cos(angle), z = at.Z + r * MathF.Sin(angle);
                if (!OnFloor(walk, x, z, floorY, heightLimit, out float y)) continue;
                var spot = new Vector3(x, y, z);
                if (Gap(edge, site, spot) >= reach) continue;

                float d = Vector3.DistanceSquared(spot, from);
                if (d >= bestDistance) continue;
                best = spot;
                bestDistance = d;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a worker can stand at X/Z on the surface itself (not grazing the edge of a hole beside it) within <paramref name="heightLimit"/> of
    /// the floor at <paramref name="floorY"/>; <paramref name="y"/> is that surface's height.
    /// </summary>
    internal static bool OnFloor(IWalkability walk, float x, float z, float floorY, float heightLimit, out float y)
    {
        y = walk.GroundHeight(x, z, floorY);
        return walk.IsOnSurface(x, z, floorY) && MathF.Abs(y - floorY) < heightLimit;
    }

    #endregion
}
