using System.Numerics;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// A town's point outside its gates (<c>Town::getPositionOutsideTownGates(dist)</c>, <c>FUN_140927120</c> over <c>FUN_140929560</c>,
/// docs/game/factions-squads-towns.md): the first of the town's gate set (its <c>is gateway</c> buildings, <see cref="TownBuilding.IsGateway"/>; Town
/// <c>+0x390</c>, which <c>FUN_140929560</c> walks), in list order, that is a gate outside the walls (its own gate code 0) or a wall with an
/// interior, moved <see cref="Offset"/> outward (<see cref="GatePoint"/>, <see cref="Away"/>); the town's position when there is none. It takes
/// only the town, so every walker is given the same point (<b>Verified</b>; no walkability test anywhere, and the original's cache of the point is
/// <b>Unknown</b>, not modelled). The patrols, the throwing out of an intruder (274), the travel rows 53 / 130, the war point and the homeward walks use it; for a town
/// without gates the original's waypoint (Town <c>+0x38</c>) is the door point of the building under its position, for which the town's position
/// stands (engine reading).
/// </summary>
public static class TownExit
{
    /// <summary>
    /// The distance both callers pass (<c>getPositionOutsideTownGates(200)</c>).
    /// </summary>
    public const float Offset = 200;

    /// <summary>
    /// A placed town's point. Engine choice: an unknown gate code (no labelling listed the gate, or no gate regions) counts as outside the walls.
    /// </summary>
    public static Vector2 Of(TownSite town, IGateRegions? regions, Func<TownBuilding, bool>? destroyed = null)
    {
        var centre = new Vector2(town.Position.X, town.Position.Z);
        foreach (var b in town.Buildings)
        {
            if (!b.IsGateway || !Qualifies(b, regions, destroyed)) continue;

            var at = new Vector2(b.Position.X, b.Position.Z);
            return b.IsGate ? GatePoint(centre, at, b.Key, regions) : Away(centre, at);
        }

        return centre;
    }

    /// <summary>
    /// Whether a building of the gate set gives the point (<c>FUN_140929560</c>, <b>Verified</b>): a gate (a GatewayBuilding, Building slot 123) only
    /// outside the walls (<see cref="OwnCode"/>) and with a door (a ruin has none), never as a wall; any other building when it is a wall with an
    /// interior.
    /// </summary>
    static bool Qualifies(TownBuilding b, IGateRegions? regions, Func<TownBuilding, bool>? destroyed)
    {
        if (b.IsGate) return !(destroyed?.Invoke(b) ?? b.Destroyed) && !(OwnCode(regions, b.Key) > 0);

        return b.IsWall && b.HasInterior;
    }

    /// <summary>
    /// A gate's own <c>amInsideTownWalls</c> (RootObject <c>+0xA0</c>): the second code the last labelling gave it, its outer side's
    /// (<c>FUN_1402f03e0</c>, <b>Verified</b>), not the code under its origin, which can lie in the walled region (the Hub's two gates stood up
    /// by its residents: codes [1, 0], origin code 1, <b>Observed</b>). Null when no labelling listed it (no door, or its zone not loaded).
    /// </summary>
    static int? OwnCode(IGateRegions? regions, string building)
    {
        if (regions is null) return null;

        foreach (var gate in regions.Gates)
            if (gate.Building == building && gate.Codes.Length > 0) return gate.Codes[^1];
        return null;
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

        return PlayerPoint(world, data, regions, town, cache);
    }

    /// <summary>
    /// <c>Town::hasGates</c> (slot 94: the gate set Town <c>+0x390</c> is not empty; false for a nest's TownBase), <b>Verified</b>: the set is filled
    /// only by Building slot 138 (<c>FUN_140551b20</c>, run from the Building constructor), which adds a building whose record has
    /// <c>is gateway</c>, built or not: gates and the gateway walls alike (docs/game/dialogue.md, conditions 49 and 58). An in-place upgrade
    /// into a wall gate without <c>is gateway</c> is not tracked; the base data has none.
    /// </summary>
    public static bool HasGates(World world, PopulationData data, CampaignTown town)
    {
        if (town.Site >= 0) return PlacedGates(data, town);

        foreach (var site in world.Buildings.All)
            if (site.Town == town.PlayerTown && IsGateway(data, site.Record)) return true;
        return false;
    }

    /// <summary>
    /// <see cref="HasGates"/> for a placed town: an <c>is gateway</c> building; a nest (a TownBase) has none.
    /// </summary>
    static bool PlacedGates(PopulationData data, CampaignTown town)
    {
        var placed = data.Sites[town.Site];
        if (placed.Town.Type == Meitou.Data.Gameplay.TownType.Nest) return false;

        foreach (var b in placed.Buildings)
            if (IsGateway(data, b.BuildingId)) return true;
        return false;
    }

    /// <summary>
    /// Whether the BUILDING record <paramref name="id"/> has <c>is gateway</c>.
    /// </summary>
    static bool IsGateway(PopulationData data, string id) => data.Db.Find(id)?.GetBool("is gateway") == true;

    /// <summary>
    /// The centre of a town (its site's, else its player town's).
    /// </summary>
    public static Vector2 Centre(World world, PopulationData data, CampaignTown town)
    {
        if (town.Site >= 0) return Flat(data.Sites[town.Site].Position);

        return world.Buildings.FindTown(town.PlayerTown) is { } t ? Flat(t.Position) : default;
    }

    /// <summary>
    /// A player town's point over its complete buildings of its gate set (<c>is gateway</c>) in list order: the first gate outside the walls
    /// (<see cref="GatePoint"/>) or other linking building with an interior (<see cref="Away"/>); the town's centre with neither.
    /// </summary>
    static Vector2 PlayerPoint(World world, PopulationData data, IGateRegions? regions, CampaignTown town, Dictionary<string, GameRecord?> cache)
    {
        var centre = Centre(world, data, town);
        foreach (var site in world.Buildings.All)
        {
            if (site.Town != town.PlayerTown || !site.Complete || data.Db.Find(site.Record) is not { } r || !r.GetBool("is gateway")) continue;

            var at = Flat(site.Position);
            bool gate = TownBuildings.IsGate(data.Db, r, cache);
            if (gate && !(OwnCode(regions, site.Id) > 0)) return GatePoint(centre, at, site.Id, regions);
            if (!gate && r.GetBool("links together") && r.GetReferences("interior").Count > 0) return Away(centre, at);
        }

        return centre;
    }

    /// <summary>
    /// A gate's point (<c>FUN_140929560</c>, <b>Verified</b> by disassembly): its first door's outer marker (<c>doorClosedPositionOut</c>
    /// <c>+0x3bc</c>) moved <see cref="Offset"/> along the horizontal way from its inner marker (<c>+0x3c8</c>) to it, so outward through the gate
    /// whatever the town's position. Engine choice: a gate the labelling did not list (its zone's navmesh not built) uses <see cref="Away"/> from the
    /// gate's origin.
    /// </summary>
    static Vector2 GatePoint(Vector2 centre, Vector2 origin, string building, IGateRegions? regions)
    {
        if (regions?.SidesOf(building) is not { } sides) return Away(centre, origin);

        // The side points are the markers pushed apart by half their distance, so the outer marker lies a quarter of the way back from the second.
        var through = sides.Second - sides.First;
        var outer = sides.Second - through / 4;
        var way = new Vector2(through.X, through.Z);
        if (way.LengthSquared() == 0) return Away(centre, origin);

        return Flat(outer) + Vector2.Normalize(way) * Offset;
    }

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>
    /// The point <see cref="Offset"/> beyond <paramref name="at"/> on the way out from <paramref name="centre"/>; the centre when the two are
    /// the same. A wall's point (<c>FUN_140929560</c>, <b>Verified</b>: its position moved along its position minus the town's).
    /// </summary>
    static Vector2 Away(Vector2 centre, Vector2 at)
    {
        var d = at - centre;
        return d.LengthSquared() > 0 ? at + Vector2.Normalize(d) * Offset : centre;
    }
}