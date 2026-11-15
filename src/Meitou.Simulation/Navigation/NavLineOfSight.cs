using System.Numerics;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The navmesh line of sight the fight's chase test asks (<c>CombatClassAI::vfunc_9</c> → <c>FUN_1403a56e0</c>, Havok AI's line-of-sight query,
/// docs/game/combat.md "Combat stance"): a line of no width walked face to face over the loaded meshes, from the face under one character to the face
/// under the other. It is clear when it reaches the goal face; it is blocked when it leaves a face across an edge with no face behind it, or into a
/// closed door's face (the world's edge filter, face data 5), or ends in another face (the goal's floor above or below). It follows the surface, with no
/// height test of its own.
/// </summary>
static class NavLineOfSight
{
    /// <summary>
    /// How far (XZ) from a character its face is looked for (engine choice: a walker may stand <see cref="NavmeshWalkability.EdgeTolerance"/> off
    /// the mesh; the original's agent always has a face).
    /// </summary>
    const float FaceSnap = 2 * NavmeshWalkability.EdgeTolerance;

    /// <summary>
    /// The most faces one walk crosses (the query's <c>m_maxNumberOfIterations</c> default, 100000, the input constructor <c>FUN_140d045f0</c>).
    /// </summary>
    const int MaxFaces = 100000;

    /// <summary>
    /// How far (XZ) the crossing point may lie off the face behind an edge and still be in it: as loose as the path search's portals across
    /// near-miss seams and interior joins (<see cref="NavGeometry.NearMissGap"/>, <see cref="NavInteriors.JoinDistance"/>).
    /// </summary>
    const float SeamGap = NavInteriors.JoinDistance;

    /// <summary>
    /// The tolerance on the line's parameter (0 at the start, 1 at the goal) for crossings at a vertex or at the line's ends.
    /// </summary>
    const float Epsilon = 1e-4f;

    /// <summary>
    /// Whether the line from <paramref name="from"/> to <paramref name="to"/> is clear. A character with no face within <see cref="FaceSnap"/> blocks
    /// it (the query with an invalid face key returns "not reached", <c>FUN_140d08210</c>).
    /// </summary>
    public static bool Clear(NavWorld world, NavDoors doors, Vector3 from, Vector3 to)
    {
        if (!world.TryFindPolygon(from, FaceSnap, out var start, out var a)) return false;
        if (!world.TryFindPolygon(to, FaceSnap, out var goal, out var b)) return false;

        return Walk(world, doors, start, a, goal, b);
    }

    /// <summary>
    /// Walks the line from <paramref name="a"/> on face <paramref name="start"/> to <paramref name="b"/> on face <paramref name="goal"/>; true when
    /// it reaches the goal face.
    /// </summary>
    static bool Walk(NavWorld world, NavDoors doors, NavRef start, Vector3 a, NavRef goal, Vector3 b)
    {
        var line = new Vector2(b.X - a.X, b.Z - a.Z);
        var origin = new Vector2(a.X, a.Z);
        NavRef face = start, came = new(-1, -1);
        float t = 0;
        for (int n = 0; n < MaxFaces; n++)
        {
            if (face == goal) return true;
            if (!Cross(world, doors, face, came, origin, line, ref t, out var next)) return false;

            (came, face) = (face, next);
        }

        return false;
    }

    /// <summary>
    /// Leaves <paramref name="face"/> where the line goes out of it at or after <paramref name="t"/>: into the face behind that edge (at a vertex,
    /// behind either edge), not back into <paramref name="came"/> at the same point. False when the line ends inside the face or nothing is behind.
    /// </summary>
    static bool Cross(NavWorld world, NavDoors doors, NavRef face, NavRef came, Vector2 origin, Vector2 line, ref float t, out NavRef next)
    {
        next = default;
        var m = world.Mesh(face.Zone);
        var poly = m.Polygons[face.Polygon];
        float exit = ExitParameter(m, poly, origin, line, t);
        if (exit > 1 + Epsilon) return false;

        var at = origin + line * exit;
        for (int k = 0; k < poly.Length; k++)
        {
            if (MathF.Abs(EdgeExit(m, poly, k, origin, line, t) - exit) > Epsilon) continue;
            if (!Behind(world, doors, face, k, at, out next)) continue;
            if (next == came && exit - t < Epsilon) continue;

            t = exit;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The first parameter at or after <paramref name="t"/> where the line leaves the face (<see cref="float.MaxValue"/> when it never does).
    /// </summary>
    static float ExitParameter(ZoneNavMesh m, int[] poly, Vector2 origin, Vector2 line, float t)
    {
        float exit = float.MaxValue;
        for (int k = 0; k < poly.Length; k++) exit = MathF.Min(exit, EdgeExit(m, poly, k, origin, line, t));

        return exit;
    }

    /// <summary>
    /// The parameter where the line goes out across edge <paramref name="k"/> (the inside is left of each edge seen from above,
    /// <see cref="ZoneNavMesh.Contains"/>), when at or after <paramref name="t"/>; else <see cref="float.MaxValue"/>.
    /// </summary>
    static float EdgeExit(ZoneNavMesh m, int[] poly, int k, Vector2 origin, Vector2 line, float t)
    {
        var v0 = m.Vertices[poly[k]];
        var v1 = m.Vertices[poly[(k + 1) % poly.Length]];
        var edge = new Vector2(v1.X - v0.X, v1.Z - v0.Z);
        float outward = edge.X * line.Y - edge.Y * line.X;
        if (outward >= 0) return float.MaxValue;

        float inside = edge.X * (origin.Y - v0.Z) - edge.Y * (origin.X - v0.X);
        float s = -inside / outward;

        return s >= t - Epsilon ? MathF.Max(s, t) : float.MaxValue;
    }

    /// <summary>
    /// The face behind edge <paramref name="k"/> of <paramref name="face"/> at <paramref name="at"/>: its neighbour, a tile link or a world link
    /// across that edge, kept, not a closed door, and the nearest to the point within <see cref="SeamGap"/>.
    /// </summary>
    static bool Behind(NavWorld world, NavDoors doors, NavRef face, int k, Vector2 at, out NavRef next)
    {
        var m = world.Mesh(face.Zone);
        next = default;
        float best = float.MaxValue;

        int q = m.Neighbours[face.Polygon][k];
        if (q >= 0) Consider(world, doors, new NavRef(face.Zone, q), at, ref next, ref best);

        var links = m.LinksOf(face.Polygon);
        for (int i = 0; i < links.Length; i += 2)
            if (links[i] == k) Consider(world, doors, new NavRef(face.Zone, links[i + 1]), at, ref next, ref best);

        foreach (var link in world.LinksOf(face))
            if (link.Edge == k) Consider(world, doors, link.To, at, ref next, ref best);

        return best <= SeamGap;
    }

    /// <summary>
    /// Takes <paramref name="candidate"/> as the face behind when it is open and nearer to <paramref name="at"/> than the best so far.
    /// </summary>
    static void Consider(NavWorld world, NavDoors doors, NavRef candidate, Vector2 at, ref NavRef next, ref float best)
    {
        var m = world.Mesh(candidate.Zone);
        if (!m.Kept[candidate.Polygon] || ClosedDoor(m, candidate.Polygon, doors)) return;

        float d = m.DistanceXz(candidate.Polygon, at.X, at.Y);
        if (d >= best) return;

        best = d;
        next = candidate;
    }

    /// <summary>
    /// Whether face <paramref name="p"/> belongs to a door closed in <paramref name="doors"/> (face data 5, which the world's <c>EdgeFilter</c>
    /// <c>FUN_1403b58e0</c> refuses).
    /// </summary>
    static bool ClosedDoor(ZoneNavMesh m, int p, NavDoors doors) =>
        m.DoorOf is { } of && of[p] is var d and >= 0 && doors.IsClosed(m.DoorIds![d]);
}
