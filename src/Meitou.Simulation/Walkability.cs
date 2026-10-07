using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>A path from a query: world positions from the start to the goal, both included; empty when none was found.</summary>
public sealed record PathResult(IReadOnlyList<Vector3> Points)
{
    public static readonly PathResult NotFound = new([]);
    public bool Found => Points.Count > 0;
}

/// <summary>
/// Where characters can stand and walk (docs/simulation.md, "Walkability and movement"). The open-ground stand-in comes first;
/// the navmesh replaces it. <see cref="GroundHeight"/> and <see cref="IsWalkable"/> are cheap and safe from any thread;
/// <see cref="FindPath"/> may be slow and is only called from the path service's threads, never inside a tick's phases.
/// </summary>
public interface IWalkability
{
    /// <summary>Height a character stands at, at world X/Z.</summary>
    float GroundHeight(float x, float z);
    /// <summary>Whether a character may stand at world X/Z.</summary>
    bool IsWalkable(float x, float z);
    PathResult FindPath(Vector3 from, Vector3 to);
}

/// <summary>
/// The stand-in until the navmesh exists: the terrain everywhere above the water, no buildings or other obstacles, paths straight.
/// A path is refused when the straight line dips under the water (sampled every <see cref="SampleStep"/> units).
/// </summary>
public sealed class OpenGroundWalkability(Func<float, float, float> ground) : IWalkability
{
    /// <summary>Spacing of the water test along a straight path (engine choice).</summary>
    public const float SampleStep = 50f;

    public float GroundHeight(float x, float z) => ground(x, z);

    public bool IsWalkable(float x, float z) => ground(x, z) > WorldWater.Height;

    public PathResult FindPath(Vector3 from, Vector3 to)
    {
        if (!IsWalkable(from.X, from.Z) || !IsWalkable(to.X, to.Z)) return PathResult.NotFound;
        float length = Vector2.Distance(new(from.X, from.Z), new(to.X, to.Z));
        int steps = (int)MathF.Ceiling(length / SampleStep);
        for (int i = 1; i < steps; i++)
        {
            var p = Vector3.Lerp(from, to, i / (float)steps);
            if (!IsWalkable(p.X, p.Z)) return PathResult.NotFound;
        }
        var start = new Vector3(from.X, GroundHeight(from.X, from.Z), from.Z);
        var goal = new Vector3(to.X, GroundHeight(to.X, to.Z), to.Z);
        return new([start, goal]);
    }
}

/// <summary>A walkability that answers paths for a given footprint radius (RACE <c>pathfind footprint radius</c>: the clearance across portals is twice it).</summary>
public interface IAgentWalkability : IWalkability
{
    /// <summary>A path for an agent of <paramref name="footprintRadius"/> units; 0 or less means the default human.</summary>
    PathResult FindPath(Vector3 from, Vector3 to, float footprintRadius);
}
