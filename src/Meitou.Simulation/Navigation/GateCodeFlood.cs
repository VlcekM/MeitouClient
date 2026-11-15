using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The gate-code pass's walks over the navmesh under its gate filter, which never enters a door face (docs/game/pathfinding.md "Gate codes"): the
/// connection test between two points (<c>FUN_1402ec580</c>) and the flood that writes a code into a region (<c>FUN_1402ed460</c>).
/// </summary>
internal sealed class GateCodeFlood
{
    #region Connection

    /// <summary>
    /// Each kept polygon's connected part under the gate filter, by zone slot (-1 for door and pruned polygons).
    /// </summary>
    readonly int[][] _part;

    /// <summary>
    /// The navmesh walked.
    /// </summary>
    readonly NavWorld _world;

    /// <summary>
    /// Splits <paramref name="world"/> into the parts the gate filter leaves connected.
    /// </summary>
    public GateCodeFlood(NavWorld world)
    {
        _world = world;
        _part = new int[world.ZoneCount][];
        for (int s = 0; s < world.ZoneCount; s++)
        {
            _part[s] = new int[world.Mesh(s).PolygonCount];
            Array.Fill(_part[s], -1);
        }

        MarkParts();
    }

    /// <summary>
    /// <c>FUN_1402ec580</c>: whether a path under the gate filter joins the faces at <paramref name="a"/> and <paramref name="b"/>; false when either
    /// point finds no face (<c>FUN_1402ea530</c>). The original asks Havok's search (100 000 iterations); the parts answer the same.
    /// </summary>
    public bool Connected(Vector3 a, Vector3 b)
    {
        if (!GateFaces.TryFaceAbove(_world, a, out var from) || !GateFaces.TryFaceAbove(_world, b, out var to)) return false;
        if (from == to) return true;

        // A path may leave a door face but never enters one.
        if (PartOf(to) < 0) return false;
        if (PartOf(from) >= 0) return PartOf(from) == PartOf(to);

        foreach (var (n, _, _) in Around(from))
            if (PartOf(n) == PartOf(to)) return true;

        return false;
    }

    /// <summary>
    /// <c>NavMesh::getGateCode</c> at <paramref name="at"/> over <paramref name="codes"/>.
    /// </summary>
    public int CodeAt(Vector3 at, Dictionary<long, int> codes) => GateFaces.CodeAt(_world, codes, at);

    /// <summary>
    /// The part of a polygon (-1 for a door's).
    /// </summary>
    int PartOf(NavRef r) => _part[r.Zone][r.Polygon];

    /// <summary>
    /// Numbers the connected parts of the kept, non-door polygons.
    /// </summary>
    void MarkParts()
    {
        int next = 0;
        var queue = new Queue<NavRef>();
        for (int s = 0; s < _world.ZoneCount; s++)
        {
            var m = _world.Mesh(s);
            for (int p = 0; p < m.PolygonCount; p++)
            {
                if (!Passable(new NavRef(s, p)) || _part[s][p] >= 0) continue;

                _part[s][p] = next;
                queue.Enqueue(new NavRef(s, p));
                Spread(queue, next++);
            }
        }
    }

    /// <summary>
    /// Gives every passable polygon reached from <paramref name="queue"/> the part <paramref name="part"/>.
    /// </summary>
    void Spread(Queue<NavRef> queue, int part)
    {
        while (queue.TryDequeue(out var r))
        {
            foreach (var (n, _, _) in Around(r))
            {
                if (!Passable(n) || _part[n.Zone][n.Polygon] >= 0) continue;

                _part[n.Zone][n.Polygon] = part;
                queue.Enqueue(n);
            }
        }
    }

    #endregion

    #region Flood

    /// <summary>
    /// The flood crosses only edges whose nearest point lies within this of its start, seen from above (<b>Verified</b>, <c>FUN_1402ed460</c>: search
    /// radius 900 Havok units).
    /// </summary>
    public const float FloodRadius = 9000;

    /// <summary>
    /// A flood that takes more steps than this fails (<b>Verified</b>, <c>FUN_140ce4a50</c>: 100 000 iterations).
    /// </summary>
    public const int FloodIterations = 100_000;

    /// <summary>
    /// A boundary edge at least this long on the zone's outer side, joined to nothing, is the loaded navmesh's open edge (<b>Verified</b>,
    /// <c>FUN_1402ea7a0</c>: squared length 4 Havok units).
    /// </summary>
    public const float OpenEdgeLength = 20;

    /// <summary>
    /// What a flood did.
    /// </summary>
    public enum Outcome
    {
        /// <summary>
        /// It wrote its code.
        /// </summary>
        Written,
        /// <summary>
        /// Its start point finds no face ("Floodfill has no starting polygon"): nothing written, not a failure.
        /// </summary>
        NoStart,
        /// <summary>
        /// Its start face has a code already: nothing done.
        /// </summary>
        Labelled,
        /// <summary>
        /// It reached the loaded navmesh's open edge or ran out of steps ("LIMIT REACHED"): nothing written, the code failed.
        /// </summary>
        Failed,
    }

    /// <summary>
    /// <c>FUN_1402ed460</c> for one code: floods from the face at <paramref name="seed"/> under the gate filter, within <see cref="FloodRadius"/>,
    /// and writes <paramref name="code"/> into every face reached, unless the start face has a code already or the flood fails.
    /// </summary>
    public Outcome Flood(int code, Vector3 seed, Dictionary<long, int> codes)
    {
        if (!GateFaces.TryFaceAbove(_world, seed, out var start)) return Outcome.NoStart;
        if (codes.ContainsKey(start.Key)) return Outcome.Labelled;
        if (Reach(start, new Vector2(seed.X, seed.Z)) is not { } reached) return Outcome.Failed;

        foreach (var r in reached)
            if (!GateFaces.IsDoor(_world.Mesh(r.Zone), r.Polygon)) codes[r.Key] = code;

        return Outcome.Written;
    }

    /// <summary>
    /// The faces a flood from <paramref name="start"/> reaches, null when one of them is at the open edge (the fill callback refuses it and the
    /// search stops) or it takes more than <see cref="FloodIterations"/> steps.
    /// </summary>
    List<NavRef>? Reach(NavRef start, Vector2 seed)
    {
        var reached = new List<NavRef> { start };
        var seen = new HashSet<long> { start.Key };
        var queue = new Queue<NavRef>();
        queue.Enqueue(start);
        if (AtOpenEdge(start)) return null;

        int steps = 0;
        while (queue.TryDequeue(out var r))
        {
            if (++steps > FloodIterations) return null;

            foreach (var (n, a, b) in Around(r))
            {
                if (!Passable(n) || DistanceToEdge(seed, a, b) > FloodRadius || !seen.Add(n.Key)) continue;
                if (AtOpenEdge(n)) return null;

                reached.Add(n);
                queue.Enqueue(n);
            }
        }

        return reached;
    }

    /// <summary>
    /// <c>FUN_1402ea7a0</c>: an exterior face with a boundary edge at least <see cref="OpenEdgeLength"/> long lying on its zone's outer side and
    /// joined to nothing. Meitou reading: only toward a zone with no mesh loaded (the original's tiles join across every loaded border; Meitou's stitching
    /// leaves short gaps there, Stack's yard over the 19.29 / 20.29 border).
    /// </summary>
    bool AtOpenEdge(NavRef r)
    {
        var m = _world.Mesh(r.Zone);
        if (r.Polygon >= m.InteriorStart) return false;

        var zone = new ZoneCoordinate(m.ZoneX, m.ZoneZ);
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var lo = new Vector2((float)ox, (float)oz);
        var hi = lo + new Vector2(WorldLayout.ZoneSize);
        var poly = m.Polygons[r.Polygon];
        for (int k = 0; k < poly.Length; k++)
        {
            if (m.Neighbours[r.Polygon][k] >= 0 || m.HasLink(r.Polygon, k)) continue;

            var a = m.Vertices[poly[k]];
            var b = m.Vertices[poly[(k + 1) % poly.Length]];
            if (Vector3.Distance(a, b) >= OpenEdgeLength && SideOf(a, b, lo, hi) is var (dx, dz) && !Loaded(zone, dx, dz)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the zone next to <paramref name="zone"/> in direction (<paramref name="dx"/>, <paramref name="dz"/>) has a mesh loaded.
    /// </summary>
    bool Loaded(ZoneCoordinate zone, int dx, int dz) => _world.Contains(new ZoneCoordinate(zone.X + dx, zone.Y + dz));

    /// <summary>
    /// The side of the zone's box both ends of an edge lie on, as a direction; null when they lie on none.
    /// </summary>
    static (int Dx, int Dz)? SideOf(Vector3 a, Vector3 b, Vector2 lo, Vector2 hi)
    {
        if (NavGeometry.OnLine(a.X, b.X, lo.X)) return (-1, 0);
        if (NavGeometry.OnLine(a.X, b.X, hi.X)) return (1, 0);
        if (NavGeometry.OnLine(a.Z, b.Z, lo.Y)) return (0, -1);
        if (NavGeometry.OnLine(a.Z, b.Z, hi.Y)) return (0, 1);

        return null;
    }

    /// <summary>
    /// The distance seen from above from <paramref name="p"/> to the edge <paramref name="a"/>-<paramref name="b"/>.
    /// </summary>
    static float DistanceToEdge(Vector2 p, Vector3 a, Vector3 b)
    {
        var a2 = new Vector2(a.X, a.Z);
        var ab = new Vector2(b.X, b.Z) - a2;
        float len2 = ab.LengthSquared();
        float t = len2 > 0 ? Math.Clamp(Vector2.Dot(p - a2, ab) / len2, 0, 1) : 0;
        return Vector2.Distance(p, a2 + ab * t);
    }

    #endregion

    #region Mesh walking

    /// <summary>
    /// Whether the gate filter lets a walk enter the polygon: kept and not a door's (<c>GateFilter</c>: face data 4 and 5 are refused).
    /// </summary>
    bool Passable(NavRef r)
    {
        var m = _world.Mesh(r.Zone);
        return m.Kept[r.Polygon] && !GateFaces.IsDoor(m, r.Polygon);
    }

    /// <summary>
    /// The polygons next to <paramref name="r"/> with the edge between: behind its edges, across its own links and across the zone borders.
    /// </summary>
    IEnumerable<(NavRef To, Vector3 A, Vector3 B)> Around(NavRef r)
    {
        var m = _world.Mesh(r.Zone);
        var poly = m.Polygons[r.Polygon];
        for (int k = 0; k < poly.Length; k++)
        {
            int q = m.Neighbours[r.Polygon][k];
            if (q >= 0) yield return (new NavRef(r.Zone, q), m.Vertices[poly[k]], m.Vertices[poly[(k + 1) % poly.Length]]);
        }

        var own = m.LinksOf(r.Polygon).ToArray();
        for (int i = 0; i + 1 < own.Length; i += 2)
            yield return (new NavRef(r.Zone, own[i + 1]), m.Vertices[poly[own[i]]], m.Vertices[poly[(own[i] + 1) % poly.Length]]);

        foreach (var link in _world.LinksOf(r).ToArray())
            yield return (link.To, m.Vertices[poly[link.Edge]], m.Vertices[poly[(link.Edge + 1) % poly.Length]]);
    }

    #endregion
}
