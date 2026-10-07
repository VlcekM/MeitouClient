using System.Numerics;
using Meitou.Navigation;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The simulation's <see cref="IWalkability"/> over the navmesh (<see cref="NavSystem"/>): the only place the game knows the navigation
/// types, so the navmesh track can change its side without touching the simulation. Paths take the character's footprint radius
/// (RACE <c>pathfind footprint radius</c>); where a zone is not loaded yet the navmesh answers with its open-ground stand-in
/// (<see cref="NavmeshWalkability"/>). Safe from any thread.
/// </summary>
sealed class NavAdapter(NavmeshWalkability nav) : IAgentWalkability
{
    public float GroundHeight(float x, float z) => nav.GroundHeight(x, z);
    public bool IsWalkable(float x, float z) => nav.IsWalkable(x, z);
    public PathResult FindPath(Vector3 from, Vector3 to) => nav.FindPath(from, to);
    public PathResult FindPath(Vector3 from, Vector3 to, float footprintRadius) =>
        nav.FindPath(from, to, footprintRadius > 0 ? new NavAgent { Radius = footprintRadius } : NavAgent.Human);
}
