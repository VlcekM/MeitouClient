using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Production;

/// <summary>
/// <c>Building::getPositionMarker(from)</c> (<c>FUN_14054b850</c>, <b>Verified</b>; docs/game/buildings-production.md "Where the walk to a building
/// goes") for player machines: the use node nearest the character (a lone node is the marker), else the position marker of a part flagged
/// <c>is for position marker</c>. An operator walks to it and has arrived within the machine's <c>use range</c> of it; a building with neither
/// keeps <see cref="SiteReach"/>'s footprint rule.
/// </summary>
sealed class UseSpots(GameDatabase db)
{
    #region Markers

    /// <summary>
    /// Each BUILDING record's markers in its own frame (scaled, unrotated): its use nodes, else its position marker, else none. Filled lazily from
    /// any phase.
    /// </summary>
    readonly ConcurrentDictionary<string, Vector3[]> _local = new(StringComparer.Ordinal);

    /// <summary>
    /// The sites whose last walk goal found no marker a worker can stand at, so arrival keeps the footprint rule. Written by the serial goal step.
    /// </summary>
    readonly HashSet<string> _unstandable = new(StringComparer.Ordinal);

    /// <summary>
    /// The site's marker nearest <paramref name="from"/> (squared 3D distance) in the world; null when its building has none.
    /// </summary>
    public Vector3? MarkerOf(BuildSite site, Vector3 from)
    {
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        foreach (var local in LocalOf(site.Record))
        {
            var at = site.Position + Vector3.Transform(local, site.Rotation);
            float d = Vector3.DistanceSquared(at, from);
            if (d >= bestDistance) continue;
            best = at;
            bestDistance = d;
        }

        return best;
    }

    /// <summary>
    /// Whether an operator's arrival at <paramref name="site"/> is measured to its marker: it has one and its last walk goal found it standable.
    /// </summary>
    public bool ArrivesAtMarker(BuildSite site) => LocalOf(site.Record).Length > 0 && !_unstandable.Contains(site.Id);

    /// <summary>
    /// The walk goal at the site's markers: of those a worker can stand at on the site's floor (<paramref name="floorY"/>, within
    /// <paramref name="heightLimit"/>), the one nearest <paramref name="from"/>, on that floor; null when the building has none or none is standable
    /// (engine choice standing in for the original's bad-node map, which skips a node a walk to which proved impossible). Serial phases only.
    /// </summary>
    public Vector3? WalkGoal(IWalkability walk, BuildSite site, float floorY, float heightLimit, Vector3 from)
    {
        var goal = NearestStandable(walk, site, floorY, heightLimit, from);
        if (goal is null) _unstandable.Add(site.Id);
        else _unstandable.Remove(site.Id);
        return goal;
    }

    /// <summary>
    /// <see cref="WalkGoal"/>'s search over the markers.
    /// </summary>
    Vector3? NearestStandable(IWalkability walk, BuildSite site, float floorY, float heightLimit, Vector3 from)
    {
        Vector3? best = null;
        float bestDistance = float.MaxValue;
        foreach (var local in LocalOf(site.Record))
        {
            var at = site.Position + Vector3.Transform(local, site.Rotation);
            float d = Vector3.DistanceSquared(at, from);
            if (d >= bestDistance || !SiteReach.OnFloor(walk, at.X, at.Z, floorY, heightLimit, out float y)) continue;
            best = new Vector3(at.X, y, at.Z);
            bestDistance = d;
        }

        return best;
    }

    /// <summary>
    /// The record's markers in its own frame.
    /// </summary>
    Vector3[] LocalOf(string record) => _local.GetOrAdd(record, Load);

    /// <summary>
    /// Reads a record's use nodes (<see cref="FixtureRegistry.UseNodes"/>), else its position marker part (<see cref="WorldObjectLayout.PositionMarker"/>).
    /// </summary>
    Vector3[] Load(string record)
    {
        if (db.Find(record) is not { } building) return [];
        if (FixtureRegistry.UseNodes(db, building) is { } nodes) return [.. nodes.Select(n => n.At)];
        return WorldObjectLayout.PositionMarker(db, building, Vector3.Zero, Quaternion.Identity) is { } marker ? [marker] : [];
    }

    #endregion
}
