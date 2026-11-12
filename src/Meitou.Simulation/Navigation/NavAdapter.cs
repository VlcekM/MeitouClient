using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The simulation's <see cref="IWalkability"/> over the navmesh (<see cref="NavSystem"/>): the only place the simulation knows the navigation types,
/// so the navmesh track can change its side without touching the simulation. Paths take the character's footprint radius (RACE <c>pathfind footprint
/// radius</c>); where a zone is not loaded yet the navmesh answers with its open-ground stand-in (<see cref="NavmeshWalkability"/>). Safe from any
/// thread. It is also the world's door gate: locked buildings close their doors in the navmesh's door table.
/// </summary>
sealed class NavAdapter(NavmeshWalkability nav) : IAgentWalkability, Locks.IDoorGate
{
    /// <summary>
    /// A building whose doors are all locked is closed for every path (docs/simulation.md "Cages, beds and locks").
    /// </summary>
    public void SetDoorsBlocked(string buildingInstance, bool blocked) => nav.Doors.Set(buildingInstance, blocked);

    /// <inheritdoc/>
    public void ResetDoors() => nav.Doors.Clear();

    /// <inheritdoc/>
    public Locks.DoorSpot? DoorSpotOf(string buildingRecordId, int doorIndex) => nav.DoorGeometry?.Spot(buildingRecordId, doorIndex);

    /// <inheritdoc/>
    public float GroundHeight(float x, float z) => nav.GroundHeight(x, z);

    /// <inheritdoc/>
    public bool IsWalkable(float x, float z) => nav.IsWalkable(x, z);

    /// <inheritdoc/>
    public float TerrainHeight(float x, float z) => nav.TerrainHeight(x, z);

    /// <inheritdoc/>
    public float WaterBed(float x, float z, float y) => nav.WaterBed(x, z, y);

    /// <inheritdoc/>
    public float GroundHeight(float x, float z, float nearY) => nav.GroundHeight(x, z, nearY);

    /// <inheritdoc/>
    public bool IsWalkable(float x, float z, float nearY) => nav.IsWalkable(x, z, nearY);

    /// <inheritdoc/>
    public bool IsOnSurface(float x, float z, float nearY) => nav.IsOnSurface(x, z, nearY);

    /// <inheritdoc/>
    public Vector3 SurfacePoint(Vector3 at, float inset) => nav.SurfacePoint(at, inset);

    /// <inheritdoc/>
    public bool TryStand(float x, float z, float nearY, out float y) => nav.TryStand(x, z, nearY, out y);

    /// <inheritdoc/>
    public Vector2 Slide(Vector3 from, Vector2 step, float radius) => nav.Slide(from, step, radius);

    /// <inheritdoc/>
    public Vector3? FloorNear(float x, float z, float y, float slack) => nav.FloorNear(x, z, y, slack);

    /// <summary>
    /// A path for the default human agent (the navmesh's own default).
    /// </summary>
    public PathResult FindPath(Vector3 from, Vector3 to) => nav.FindPath(from, to);

    /// <inheritdoc/>
    public PathResult FindPath(Vector3 from, Vector3 to, float footprintRadius, WaterCost water, float roadPreference = 0)
    {
        var agent = new NavAgent
        {
            Radius = footprintRadius > 0 ? footprintRadius : NavAgent.Human.Radius,
            Water = water.IsSet ? water : NavAgent.Human.Water,
            RoadPreference = roadPreference,
        };
        return nav.FindPath(from, to, agent);
    }
}
