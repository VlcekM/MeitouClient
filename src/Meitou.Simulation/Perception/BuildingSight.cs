using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Simulation.Perception;

/// <summary>
/// The simulation's rooms and lamps of the town buildings (<see cref="SightMap"/>): the building's indoor volume from its <c>interior mask</c> collision and its lights with those of
/// its layouts, both from the level data the renderer builds its own from. Called on the simulation thread when a town registers; the collision files are cached.
/// </summary>
sealed class BuildingSight(GameInstall install, GameDatabase db, WorldLevelData levels, BuildingLayouts layouts) : ISightSource
{
    readonly InteriorCollisions collisions = new(install);

    public InteriorVolume? Room(TownBuilding building) =>
        building.Resolved is { } r ? InteriorVolume.For(db, r.Record, building.Key, r.Position, r.Placement.Rotation, collisions) : null;

    public IReadOnlyList<WorldLight> Lights(TownBuilding building) =>
        building.Resolved is { } r ? WorldLights.ForBuilding(db, levels, r, layouts) : [];

    public bool EnforcesCeiling(TownBuilding building) => db.Find(building.BuildingId)?.GetBool("enforce ceiling") == true;

    /// <summary>A completed player building's lamps, placed as the renderer lights them (<c>GameHost.UpdateLamps</c>: its own lights, no interior layouts).</summary>
    public IReadOnlyList<WorldLight> SiteLights(Meitou.Simulation.Construction.BuildSite site)
    {
        if (db.Find(site.Record) is not { } record) return [];
        var placed = new List<PlacedLight>();
        WorldObjectLayout.Building(db, record, site.Id, site.Position, site.Rotation, new BuildingState { Identity = site.Id }, placed);
        bool indoors = WorldLights.IsIndoors(record, site.Parent is not null);
        return [.. placed.Select(p => WorldLights.FromPlaced(p, interior: false, indoors: indoors))];
    }
}
