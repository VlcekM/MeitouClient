using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Navigation;

/// <summary>
/// <see cref="IWalkability"/> over the loaded zone meshes. Where a zone's mesh is not there yet (it is built on a worker thread,
/// <see cref="NavMeshService"/>) the open-ground stand-in answers, so the simulation never waits. The world is swapped as a whole
/// (immutable snapshots), so every method is safe from any thread.
/// </summary>
public sealed class NavmeshWalkability : IWalkability
{
    readonly Func<float, float, float> ground;
    readonly IWalkability fallback;
    volatile NavWorld world = NavWorld.Empty;

    public NavmeshWalkability(Func<float, float, float> terrainHeight, IWalkability? fallback = null)
    {
        ground = terrainHeight;
        this.fallback = fallback ?? new OpenGroundWalkability(terrainHeight);
    }

    public NavWorld World => world;

    /// <summary>Replaces the loaded meshes.</summary>
    public void SetWorld(NavWorld value) => world = value;

    public float GroundHeight(float x, float z) => world.TryGroundHeight(x, z, out float h) ? h : ground(x, z);

    public bool IsWalkable(float x, float z)
    {
        var w = world;
        if (!w.Contains(WorldLayout.ZoneOf(x, z))) return fallback.IsWalkable(x, z);
        return w.TryGroundHeight(x, z, out _);
    }

    public PathResult FindPath(Vector3 from, Vector3 to) => FindPath(from, to, NavAgent.Human);

    /// <summary>A path for a given agent (footprint radius, water cost, closed doors). Without both ends on loaded zones the stand-in answers.</summary>
    public PathResult FindPath(Vector3 from, Vector3 to, NavAgent agent)
    {
        var w = world;
        if (!w.Contains(WorldLayout.ZoneOf(from.X, from.Z)) || !w.Contains(WorldLayout.ZoneOf(to.X, to.Z))) return fallback.FindPath(from, to);
        return new NavQuery(w).FindPath(from, to, agent);
    }
}
