using System.Numerics;
using Meitou.Data;
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
    /// A town's point by its handle: a placed town's (destroyed buildings left out), a player town's over its complete sites (a gate outside the
    /// walls, or a building that links together with an interior). <paramref name="cache"/> is the caller's functionality cache (not shared
    /// between threads).
    /// </summary>
    public static Vector2 Of(World world, PopulationData data, IGateRegions? regions, CampaignTown town, Dictionary<string, GameRecord?> cache)
    {
        if (town.Site >= 0) return Of(data.Sites[town.Site], regions, world.Fixtures.IsDestroyed);

        return Of(Centre(world, data, town), PlayerExits(world, data, regions, town, cache));
    }

    /// <summary>
    /// <c>Town::hasGates</c> (slot 94, Town <c>+0x3b0</c> set; 0 for TownBase), <b>engine reading</b>: a gate
    /// (<see cref="TownBuildings.IsGate"/>) among a placed town's buildings, or a complete one among a player town's sites.
    /// </summary>
    public static bool HasGates(World world, PopulationData data, CampaignTown town, Dictionary<string, GameRecord?> cache)
    {
        if (town.Site >= 0)
        {
            foreach (var b in data.Sites[town.Site].Buildings)
                if (data.Db.Find(b.BuildingId) is { } r && TownBuildings.IsGate(data.Db, r, cache)) return true;
            return false;
        }

        foreach (var site in world.Buildings.All)
        {
            if (site.Town == town.PlayerTown && site.Complete && data.Db.Find(site.Record) is { } r && TownBuildings.IsGate(data.Db, r, cache))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The centre of a town (its site's, else its player town's).
    /// </summary>
    public static Vector2 Centre(World world, PopulationData data, CampaignTown town)
    {
        if (town.Site >= 0) return Flat(data.Sites[town.Site].Position);

        return world.Buildings.FindTown(town.PlayerTown) is { } t ? Flat(t.Position) : default;
    }

    /// <summary>
    /// The player town's complete buildings as exit candidates: each one's point and whether it is a way through (an unlocked gate, or a linking
    /// building with an interior).
    /// </summary>
    static IEnumerable<(Vector2, bool)> PlayerExits(World world, PopulationData data, IGateRegions? regions, CampaignTown town,
        Dictionary<string, GameRecord?> cache)
    {
        foreach (var site in world.Buildings.All)
        {
            if (site.Town != town.PlayerTown || !site.Complete || data.Db.Find(site.Record) is not { } r) continue;

            bool gate = TownBuildings.IsGate(data.Db, r, cache) && !(regions?.CodeAt(site.Position) > 0);
            yield return (Flat(site.Position), gate || r.GetBool("links together") && r.GetReferences("interior").Count > 0);
        }
    }

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

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