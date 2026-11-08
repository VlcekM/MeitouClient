using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The simulation's <see cref="IWalkability"/> over the navmesh (<see cref="NavSystem"/>): the only place the simulation knows the navigation
/// types, so the navmesh track can change its side without touching the simulation. Paths take the character's footprint radius
/// (RACE <c>pathfind footprint radius</c>); where a zone is not loaded yet the navmesh answers with its open-ground stand-in
/// (<see cref="NavmeshWalkability"/>). Safe from any thread. It is also the world's door gate: locked buildings close their doors in the navmesh's door table.
/// </summary>
sealed class NavAdapter(NavmeshWalkability nav) : IAgentWalkability, Meitou.Simulation.Locks.IDoorGate
{
    /// <summary>A building whose doors are all locked is closed for every path (docs/simulation.md "Cages, beds and locks").</summary>
    public void SetDoorsBlocked(string buildingInstance, bool blocked) => nav.Doors.Set(buildingInstance, blocked);
    public void ResetDoors() => nav.Doors.Clear();
    public Meitou.Simulation.Locks.DoorSpot? DoorSpotOf(string buildingRecordId, int doorIndex) => nav.DoorGeometry?.Spot(buildingRecordId, doorIndex);
    public float GroundHeight(float x, float z) => nav.GroundHeight(x, z);
    public bool IsWalkable(float x, float z) => nav.IsWalkable(x, z);
    public float TerrainHeight(float x, float z) => nav.TerrainHeight(x, z);
    public float WaterBed(float x, float z, float y) => nav.WaterBed(x, z, y);
    public float GroundHeight(float x, float z, float nearY) => nav.GroundHeight(x, z, nearY);
    public bool IsWalkable(float x, float z, float nearY) => nav.IsWalkable(x, z, nearY);
    public bool TryStand(float x, float z, float nearY, out float y) => nav.TryStand(x, z, nearY, out y);
    public Vector2 Slide(Vector3 from, Vector2 step, float radius) => nav.Slide(from, step, radius);
    public Vector3? FloorNear(float x, float z, float y, float slack) => nav.FloorNear(x, z, y, slack);
    public PathResult FindPath(Vector3 from, Vector3 to) => nav.FindPath(from, to);
    public PathResult FindPath(Vector3 from, Vector3 to, float footprintRadius, WaterCost water, float roadPreference = 0) =>
        nav.FindPath(from, to, new NavAgent
        {
            Radius = footprintRadius > 0 ? footprintRadius : NavAgent.Human.Radius,
            Water = water.IsSet ? water : NavAgent.Human.Water,
            RoadPreference = roadPreference,
        });
}
