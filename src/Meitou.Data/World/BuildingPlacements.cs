using System.Numerics;

namespace Meitou.Data.World;

/// <summary>A building placement with what the world needs to place it: its record, its absolute position and whether its state says it is destroyed.</summary>
public readonly record struct ResolvedBuilding(BuildingPlacement Placement, GameRecord Record, Vector3 Position, bool Destroyed);

/// <summary>
/// Resolves a zone's <see cref="BuildingPlacement"/> the way the game places it (docs/formats/zones.md, "From placements to meshes"): a placement at
/// the origin is unplaced, the record is looked up by id, Y is the state's absolute <c>world Y pos</c> when there is one and else the terrain height
/// plus the instance's Y, and the state record of the zone file says whether the building is destroyed.
/// </summary>
public static class BuildingPlacements
{
    /// <summary>The resolved placement; null for an unplaced one (position 0) or one whose building record does not exist.</summary>
    public static ResolvedBuilding? Resolve(BuildingPlacement b, GameDatabase db, WorldLevelData levels, Func<double, double, float> terrainHeight)
    {
        if (b.Position == Vector3.Zero) return null;
        if (db.Find(b.BuildingId) is not { } record) return null;
        float y = b.WorldY ?? terrainHeight(b.Position.X, b.Position.Z) + b.Position.Y;
        var state = b.StateId is not null && levels.Zones.TryGetValue(b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;
        return new ResolvedBuilding(b, record, new Vector3(b.Position.X, y, b.Position.Z), state?.GetBool("destroyed") ?? false);
    }
}
