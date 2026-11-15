using Meitou.Data.World;

namespace Meitou.Simulation.Construction;

/// <summary>
/// The instance ids the level data's zone building lists hold, which a new site's id must not take (<see cref="BuildingRegistry.NewId"/>). Asked
/// live, so buildings the level editor adds or clears count at once.
/// </summary>
public sealed class LevelBuildingIds(WorldLevelData levels)
{
    /// <summary>
    /// Whether the zone's building list holds <paramref name="id"/> with a target; a cleared entry (no target) is free again, as in the original
    /// (docs/formats/zones.md "Containers and IDs").
    /// </summary>
    public bool Holds(ZoneCoordinate zone, string id) =>
        levels.Zones.TryGetValue(zone, out var db)
        && db.Find(WorldLevelData.BuildingListId) is { } list
        && list.Instances.TryGetValue(id, out var instance)
        && !instance.IsCleared;
}
