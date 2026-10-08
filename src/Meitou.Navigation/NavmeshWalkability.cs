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

    /// <summary>The doors' run-time state; shared with every query made through this object.</summary>
    public NavDoors Doors { get; } = new();

    public NavWorld World => world;

    /// <summary>Replaces the loaded meshes; only <see cref="NavMeshService"/> calls it, under its publish lock.</summary>
    internal void SetWorld(NavWorld value) => world = value;

    /// <summary>
    /// How close (in height) the mesh has to be to the terrain (engine choice) for the terrain's own height to be used there. The mesh is a simplified surface (cell
    /// 2, heights about 0.5 off, more on steep slopes); where it lies on the ground the heightmap is exact, and where it is above it (a floor, a wall top,
    /// a ramp) the mesh height stands.
    /// </summary>
    public const float OnTerrainTolerance = 3;

    /// <summary>
    /// The height a character stands at: the terrain's where the mesh lies on the ground, else the mesh's (floors, wall tops), else the terrain's.
    /// Movement should sample this every tick rather than interpolate between path points.
    /// </summary>
    public float GroundHeight(float x, float z)
    {
        if (!world.TryGroundHeight(x, z, out float h)) return ground(x, z);
        float t = ground(x, z);
        return MathF.Abs(t - h) <= OnTerrainTolerance && h > WorldWater.Height ? t : h;
    }

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
        var path = new NavQuery(w, Doors).FindPath(from, to, agent);
        if (!path.Found) return path;
        // Path points are on the simplified surface: put the ones standing on the terrain on the real ground.
        return new PathResult([.. path.Points.Select(p => new Vector3(p.X, GroundHeight(p.X, p.Z) is var y && MathF.Abs(y - p.Y) <= OnTerrainTolerance ? y : p.Y, p.Z))]);
    }
}
