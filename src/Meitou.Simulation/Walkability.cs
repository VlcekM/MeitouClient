using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>
/// A path from a query: world positions from the start to the goal, both included; empty when none was found.
/// </summary>
public sealed record PathResult(IReadOnlyList<Vector3> Points)
{
    public static readonly PathResult NotFound = new([]);
    public bool Found => Points.Count > 0;
    /// <summary>
    /// How many of the points, from the start, follow the real walkable surface; the rest are a provisional long-range route over the terrain through
    /// zones whose navmesh is not loaded, to be asked for again on the way (all of them for an ordinary path).
    /// </summary>
    public int Exact { get; init; } = int.MaxValue;
    public bool IsProvisional => Exact < Points.Count;
}

/// <summary>
/// Where characters can stand and walk (docs/simulation.md, "Movement and paths"). The open-ground stand-in comes first;
/// the navmesh replaces it. <see cref="GroundHeight"/> and <see cref="IsWalkable"/> are cheap and safe from any thread;
/// <see cref="FindPath"/> may be slow and is only called from the path service's threads, never inside a tick's phases.
/// </summary>
public interface IWalkability
{
    /// <summary>
    /// Height a character stands at, at world X/Z.
    /// </summary>
    float GroundHeight(float x, float z);
    /// <summary>
    /// Whether a character may stand at world X/Z.
    /// </summary>
    bool IsWalkable(float x, float z);
    PathResult FindPath(Vector3 from, Vector3 to);
    /// <summary>
    /// Height a character now at height <paramref name="nearY"/> stands at: where floors are stacked (a walkway over a street, an upper storey), the
    /// one under it, not the highest.
    /// </summary>
    float GroundHeight(float x, float z, float nearY) => GroundHeight(x, z);
    /// <summary>
    /// Whether a character now at height <paramref name="nearY"/> may stand at world X/Z (a surface only far above it, such as a wall top, does not
    /// count).
    /// </summary>
    bool IsWalkable(float x, float z, float nearY) => IsWalkable(x, z);

    /// <summary>
    /// <see cref="IsWalkable(float, float, float)"/> and, in <paramref name="y"/>, <see cref="GroundHeight(float, float, float)"/> (also when not
    /// walkable), answered together: a step asks both of the same spot.
    /// </summary>
    bool TryStand(float x, float z, float nearY, out float y)
    {
        y = GroundHeight(x, z, nearY);
        return IsWalkable(x, z, nearY);
    }

    /// <summary>
    /// The part of a free move (root motion, combat footwork) a body of <paramref name="radius"/> at <paramref name="from"/> can make along
    /// <paramref name="step"/> (X, Z): the original's nav agent sweeps its circle against the walls of the walkable surface and slides along the one
    /// it meets (docs/game/pathfinding.md "Walls"). Default: the whole step (the caller still tests where it ends).
    /// </summary>
    Vector2 Slide(Vector3 from, Vector2 step, float radius) => step;
    /// <summary>
    /// The terrain's own height at world X/Z, also under the water (where <see cref="GroundHeight(float, float)"/> is the flat water surface): the
    /// depth of the water is the water level minus this.
    /// </summary>
    float TerrainHeight(float x, float z) => GroundHeight(x, z);

    /// <summary>
    /// What a character at height <paramref name="y"/> on the water stands on, as the original's water state reads it (docs/game/pathfinding.md
    /// "Water states"): a walkable floor or deck just under it (a building whose ground floor lies under the water level, #345), else the terrain
    /// under the water. Default: the terrain.
    /// </summary>
    float WaterBed(float x, float z, float y) => TerrainHeight(x, z);

    /// <summary>
    /// The floor point at or next to a spot on a floor at height <paramref name="y"/>: the spot itself where the floor under it is within
    /// <paramref name="slack"/>, else the first such point on rings of 2..<see cref="FloorSearch"/> units round it (8 directions each); null when
    /// there is none. Furniture cuts holes in a building's floor mesh and the ground below shows through them (Observed: the Hub's Storm House, its
    /// counter, tables and stools), so a node or chair there would otherwise be stood on the ground under the floor.
    /// </summary>
    Vector3? FloorNear(float x, float z, float y, float slack) => ScanFloor(this, x, z, y, slack);

    /// <summary>
    /// The search of <see cref="FloorNear"/>, for an implementation that keeps its answers; uncached out to <paramref name="reach"/> for a wider one.
    /// </summary>
    static Vector3? ScanFloor(IWalkability walk, float x, float z, float y, float slack, float reach = FloorSearch)
    {
        for (float r = 0; r <= reach; r += 2)
        {
            for (int k = 0; k < (r == 0 ? 1 : 8); k++)
            {
                float px = x + r * MathF.Cos(k * MathF.PI / 4), pz = z + r * MathF.Sin(k * MathF.PI / 4);
                if (!walk.TryStand(px, pz, y, out float g)) continue;
                if (MathF.Abs(g - y) <= slack) return new Vector3(px, g, pz);
            }
        }

        return null;
    }

    /// <summary>
    /// How far (units) <see cref="FloorNear"/> looks round a spot (engine choice: more than a table's half width).
    /// </summary>
    public const float FloorSearch = 12;
}

/// <summary>
/// The stand-in until the navmesh exists: the terrain everywhere above the water, no buildings or other obstacles, paths straight.
/// A path is refused when the straight line dips under the water (sampled every <see cref="SampleStep"/> units).
/// </summary>
public sealed class OpenGroundWalkability(Func<float, float, float> ground) : IWalkability
{
    /// <summary>
    /// Spacing of the water test along a straight path (engine choice).
    /// </summary>
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

/// <summary>
/// A walkability that answers paths for a given footprint radius (RACE <c>pathfind footprint radius</c>: the clearance across portals is twice it).
/// </summary>
public interface IAgentWalkability : IWalkability
{
    /// <summary>
    /// A path for an agent of <paramref name="footprintRadius"/> units; 0 or less means the default human, and the water cost factors
    /// <paramref name="water"/> (unset: the default). A long trip takes the roads by the original's rule for FACTION <c>road preference</c>
    /// <paramref name="roadPreference"/> (0: never).
    /// </summary>
    PathResult FindPath(Vector3 from, Vector3 to, float footprintRadius, WaterCost water, float roadPreference = 0);
}

/// <summary>
/// What a unit of path through water costs a character, by depth: the wet water states 1 shallow, 2 waist-deep, 3 deep
/// (<see cref="Bodies.Speed.WaterState"/>). Each is the larger of RACE <c>water avoidance</c> (the original's flat water cost) and how much longer
/// the character takes there than on land (docs/game/pathfinding.md "Water costs"). Default: unset.
/// </summary>
public readonly record struct WaterCost(float Shallow, float Waist, float Deep)
{
    public bool IsSet => Shallow > 0;
    public float Min => MathF.Min(Shallow, MathF.Min(Waist, Deep));
    public float this[int state] => state switch { 3 => Deep, 2 => Waist, _ => Shallow };
    public static WaterCost Uniform(float factor) => new(factor, factor, factor);

    /// <summary>
    /// The costs for avoidance factor <paramref name="avoidance"/> (0 or less: unset), land top speed <paramref name="run"/> and swim speed
    /// <paramref name="swim"/>, with the speeds of <see cref="Bodies.Speed.InWater"/> (a race that does not swim has no shallow state: it walks those
    /// at its land speed).
    /// </summary>
    public static WaterCost Of(float avoidance, float run, Meitou.Data.Gameplay.Bodies.RaceData? race, float swim)
    {
        if (avoidance <= 0) return default;
        if (race is null || run <= 0) return Uniform(avoidance);

        return new(Factor(1), Factor(2), Factor(3));

        float Factor(int state)
        {
            float speed = state == 1 && !race.Swims ? run : Bodies.Speed.InWater(state, run, race, swim);
            return speed > 0 ? MathF.Max(avoidance, run / speed) : avoidance;
        }
    }
}

/// <summary>
/// Finding a walkable spot near a point (a stand-in's characters coming back into the world, docs/simulation.md "Off-screen tier").
/// </summary>
public static class WalkableSpots
{
    static readonly float[] Radii = [4, 8, 16, 32, 64, 128];
    static readonly Vector2[] Directions = [.. Enumerable.Range(0, 8).Select(k => new Vector2(MathF.Cos(k * MathF.PI / 4), MathF.Sin(k * MathF.PI / 4)))];

    /// <summary>
    /// <paramref name="at"/> when it is walkable, else the first walkable point on rings of 4 to <paramref name="maxRadius"/> units round it (eight
    /// directions each, nearest ring first, a fixed order); false (and <paramref name="spot"/> = <paramref name="at"/>) when there is none.
    /// </summary>
    public static bool Nearest(IWalkability walk, Vector2 at, out Vector2 spot, float maxRadius = 128)
    {
        spot = at;
        if (walk.IsWalkable(at.X, at.Y)) return true;

        foreach (float r in Radii)
        {
            if (r > maxRadius) break;

            foreach (var dir in Directions)
            {
                var p = at + dir * r;
                if (!walk.IsWalkable(p.X, p.Y)) continue;

                spot = p;
                return true;
            }
        }

        return false;
    }
}