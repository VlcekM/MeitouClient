using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// A town's point outside its gates (<c>Town::getPositionOutsideTownGates(dist)</c>, <c>FUN_140927120</c> over <c>FUN_140929560</c>,
/// docs/game/factions-squads-towns.md): the first of the town's buildings, in list order, that is a gate outside the walls (gate code 0) or a wall
/// with an interior, moved <see cref="Offset"/> outward from the town's position; the town's position when there is none. It takes only the town, so
/// every walker is given the same point (<b>Verified</b>; no walkability test anywhere, and the original's cache of the point is <b>Unknown</b>, not
/// modelled). The patrols and the throwing out of an intruder (274) use it.
/// </summary>
public static class TownExit
{
    /// <summary>
    /// The distance both callers pass (<c>getPositionOutsideTownGates(200)</c>).
    /// </summary>
    public const float Offset = 200;

    /// <summary>
    /// A placed town's point. Engine choice: an unknown gate code (off the navmesh, or no gate regions) counts as outside the walls; the building's
    /// origin stands for the door point and "outward" is away from the town's position (<b>Observed</b>).
    /// </summary>
    public static Vector2 Of(TownSite town, IGateRegions? regions, Func<TownBuilding, bool>? destroyed = null)
    {
        var centre = new Vector2(town.Position.X, town.Position.Z);
        foreach (var b in town.Buildings)
        {
            bool gate = b.IsGate && !(destroyed?.Invoke(b) ?? b.Destroyed) && !(regions?.CodeAt(b.Position) > 0);
            if (gate || b.IsWall && b.HasInterior) return Away(centre, new Vector2(b.Position.X, b.Position.Z));
        }

        return centre;
    }

    /// <summary>
    /// A player town's point, over its sites <paramref name="sites"/> (position, gate or wall with an interior) in list order.
    /// </summary>
    public static Vector2 Of(Vector2 centre, IEnumerable<(Vector2 At, bool Qualifies)> sites)
    {
        foreach (var (at, qualifies) in sites) if (qualifies) return Away(centre, at);
        return centre;
    }

    /// <summary>
    /// The point <see cref="Offset"/> beyond <paramref name="at"/> on the way out from <paramref name="centre"/>; the centre when the two are
    /// the same.
    /// </summary>
    static Vector2 Away(Vector2 centre, Vector2 at)
    {
        var d = at - centre;
        return d.LengthSquared() > 0 ? at + Vector2.Normalize(d) * Offset : centre;
    }
}