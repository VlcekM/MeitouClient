using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Simulation.Perception;

/// <summary>
/// The simulation's rooms and lamps of the town buildings (<see cref="SightMap"/>): the building's indoor volume from its <c>interior mask</c>
/// collision and its lights with those of its layouts, both from the level data the renderer builds its own from. Called on the simulation thread
/// when a town registers; the collision files are cached.
/// </summary>
sealed class BuildingSight(GameInstall install, GameDatabase db, WorldLevelData levels, BuildingLayouts layouts) : ISightSource
{
    /// <summary>
    /// The <c>interior mask</c> collision files read so far.
    /// </summary>
    readonly InteriorCollisions _collisions = new(install);
    /// <summary>
    /// The placed buildings' group-9 surfaces a ground ray meets (<see cref="Ground"/>; null until first asked for).
    /// </summary>
    GroundShapes? _ground;

    /// <summary>
    /// The placed buildings' group-9 surfaces a ground ray meets, read once each from <see cref="_collisions"/>.
    /// </summary>
    GroundShapes Ground => _ground ??= new GroundShapes(db, _collisions);

    /// <inheritdoc/>
    public InteriorVolume? Room(TownBuilding building) =>
        building.Resolved is { } r ? InteriorVolume.For(db, r.Record, building.Key, r.Position, r.Placement.Rotation, _collisions) : null;

    /// <inheritdoc/>
    public IReadOnlyList<WorldLight> Lights(TownBuilding building) =>
        building.Resolved is { } r ? WorldLights.ForBuilding(db, levels, r, layouts) : [];

    /// <inheritdoc/>
    public bool EnforcesCeiling(TownBuilding building) => db.Find(building.BuildingId)?.GetBool("enforce ceiling") == true;

    /// <inheritdoc/>
    public GroundHit? TopAt(TownBuilding building, bool destroyed, Vector2 at) => building.Resolved is { } r
        ? Ground.Hit(r.Record, building.Key, r.Position, r.Placement.Rotation, destroyed, at)
        : null;

    /// <inheritdoc/>
    public GroundHit? TopAt(string record, string id, Vector3 position, Quaternion rotation, bool destroyed, Vector2 at) =>
        db.Find(record) is { } building ? Ground.Hit(building, id, position, rotation, destroyed, at) : null;

    /// <inheritdoc/>
    public float BoundsRadius(TownBuilding building, bool destroyed) => building.Resolved is { } r
        ? BuildingShape.BoundsRadius(db, r.Record, building.Key, r.Position, r.Placement.Rotation, destroyed, _collisions)
        : 0;

    /// <summary>
    /// A completed player building's lamps, placed as the renderer lights them (<c>GameHost.UpdateLamps</c>: its own lights, no interior layouts).
    /// </summary>
    public IReadOnlyList<WorldLight> SiteLights(Construction.BuildSite site)
    {
        if (db.Find(site.Record) is not { } record) return [];

        var placed = new List<PlacedLight>();
        WorldObjectLayout.Building(db, record, site.Id, site.Position, site.Rotation, new BuildingState { Identity = site.Id }, placed);
        bool indoors = WorldLights.IsIndoors(record, site.Parent is not null);
        return [.. placed.Select(p => WorldLights.FromPlaced(p, interior: false, indoors: indoors))];
    }
}
