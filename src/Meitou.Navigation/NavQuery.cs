using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Navigation;

/// <summary>Who is asking for a path: the footprint radius (clearance is 2 × radius across every portal) and what the ground costs them.</summary>
public sealed record NavAgent
{
    /// <summary>RACE <c>pathfind footprint radius</c> × the size factor (humans 4, Garru 7, Leviathan 40).</summary>
    public float Radius { get; init; } = 4;
    /// <summary>
    /// Cost multiplier of water polygons: a + 1 for RACE <c>water avoidance</c> a ≥ 0, 1 / (1 − a) below, halved for the player's faction
    /// (docs/game/pathfinding.md, "Filters and costs"). 7 is a Greenlander (a = 6).
    /// </summary>
    public float WaterFactor { get; init; } = 7;
    /// <summary>Doors are blocked (closed, face data 5) when this is true; open doors cost <see cref="OpenDoorCost"/> extra.</summary>
    public bool DoorsClosed { get; init; }
    public static NavAgent Human { get; } = new();
}

/// <summary>
/// Path queries over a <see cref="NavWorld"/>: A* over the polygons with a portal-width clearance of 2 × radius, the water cost factor and the door
/// rules, then string pulling with the portals pulled in by the radius (docs/game/pathfinding.md, "Path queries" and "What our builder needs").
/// </summary>
public sealed class NavQuery(NavWorld world)
{
    /// <summary>Extra cost of an open door (the original's WaterCostModifier adds 5.0 to each cost component, Havok units).</summary>
    public const float OpenDoorCost = 50;
    /// <summary>The original's iteration limit.</summary>
    public const int MaxIterations = 100000;
    /// <summary>How far from the start / the goal a polygon may be and still count (the original: 500 and about 30 Havok units × 0.1 ... engine choice).</summary>
    public const float StartSnap = 60, GoalSnap = 30;

    public NavWorld World { get; } = world;

    /// <summary>Result of the polygon search before string pulling.</summary>
    public sealed record Corridor(List<NavRef> Polygons, Vector3 Start, Vector3 Goal, int Iterations);

    sealed class Node
    {
        public NavRef Ref;
        public Vector3 Position;
        public float G, F;
        public long Parent = -1;
        public bool Closed;
    }

    public PathResult FindPath(Vector3 from, Vector3 to, NavAgent? agent = null)
    {
        agent ??= NavAgent.Human;
        if (!World.TryFindPolygon(from, StartSnap, out var start, out var startPoint) || !World.TryFindPolygon(to, GoalSnap, out var goal, out var goalPoint))
            return PathResult.NotFound;
        var corridor = FindCorridor(start, startPoint, goal, goalPoint, agent);
        if (corridor is null) return PathResult.NotFound;
        return new PathResult(Pull(corridor, agent));
    }

    /// <summary>A* from the start polygon to the goal polygon; null when there is none (or the iteration limit ran out).</summary>
    public Corridor? FindCorridor(NavRef start, Vector3 startPoint, NavRef goal, Vector3 goalPoint, NavAgent agent)
    {
        var nodes = new Dictionary<long, Node>();
        var open = new PriorityQueue<Node, float>();
        var first = new Node { Ref = start, Position = startPoint, G = 0, F = Vector3.Distance(startPoint, goalPoint) };
        nodes[start.Key] = first;
        open.Enqueue(first, first.F);
        int iterations = 0;
        Node? last = null;
        float minFactor = Math.Min(1f, agent.WaterFactor);

        while (open.Count > 0 && iterations++ < MaxIterations)
        {
            var node = open.Dequeue();
            if (node.Closed) continue;
            node.Closed = true;
            if (node.Ref == goal) { last = node; break; }

            var mesh = World.Mesh(node.Ref.Zone);
            int p = node.Ref.Polygon;
            float factor = Factor(mesh.Areas[p], agent);
            var poly = mesh.Polygons[p];
            for (int e = 0; e < poly.Length; e++)
            {
                int q = mesh.Neighbours[p][e];
                if (q >= 0) Relax(node, mesh, e, new NavRef(node.Ref.Zone, q), factor);
            }
            var tl = mesh.LinksOf(p);
            for (int i = 0; i < tl.Length; i += 2) Relax(node, mesh, tl[i], new NavRef(node.Ref.Zone, tl[i + 1]), factor);
            foreach (var link in World.LinksOf(node.Ref))
                Relax(node, mesh, link.Edge, link.To, factor);
        }
        if (last is null) return null;

        var path = new List<NavRef>();
        for (var n = last; ; n = nodes[n.Parent])
        {
            path.Add(n.Ref);
            if (n.Parent < 0) break;
        }
        path.Reverse();
        return new Corridor(path, startPoint, goalPoint, iterations);

        void Relax(Node from, ZoneNavMesh mesh, int edge, NavRef to, float factor)
        {
            var target = World.Mesh(to.Zone);
            if (!target.Kept[to.Polygon]) return;
            byte area = target.Areas[to.Polygon];
            if (area == NavArea.Door && agent.DoorsClosed) return;
            if (!Portal(from.Ref, edge, to, out var left, out var right)) return;
            float width = Vector2.Distance(new(left.X, left.Z), new(right.X, right.Z));
            if (width < 2 * agent.Radius) return;
            var mid = BestCrossing(from.Position, left, right, goalPoint);
            float g = from.G + Vector3.Distance(from.Position, mid) * factor;
            if (area == NavArea.Door) g += OpenDoorCost;
            if (!nodes.TryGetValue(to.Key, out var node))
            {
                node = new Node { Ref = to, Position = mid, G = float.MaxValue };
                nodes[to.Key] = node;
            }
            if (node.Closed || g >= node.G) return;
            node.G = g;
            node.Position = mid;
            node.Parent = from.Ref.Key;
            node.F = g + Vector3.Distance(mid, goalPoint) * minFactor;
            open.Enqueue(node, node.F);
        }
    }

    /// <summary>The point of the portal that makes the way from <paramref name="prev"/> through it to the goal shortest (the portal's ends clamp it), so the search sees near-straight legs even through thin polygons.</summary>
    static Vector3 BestCrossing(Vector3 prev, Vector3 left, Vector3 right, Vector3 goal)
    {
        float lo = 0, hi = 1;
        for (int i = 0; i < 14; i++)
        {
            float a = lo + (hi - lo) / 3, b = hi - (hi - lo) / 3;
            if (Detour(prev, Vector3.Lerp(left, right, a), goal) < Detour(prev, Vector3.Lerp(left, right, b), goal)) hi = b; else lo = a;
        }
        return Vector3.Lerp(left, right, (lo + hi) / 2);
    }

    static float Detour(Vector3 a, Vector3 p, Vector3 b) => Vector3.Distance(a, p) + Vector3.Distance(p, b);

    static float Factor(byte area, NavAgent agent) => area == NavArea.Water ? agent.WaterFactor : 1;

    /// <summary>
    /// The shared segment between a polygon's edge and the polygon behind it: the overlap of the two collinear edges. Left and right are as seen when
    /// walking from <paramref name="from"/> into <paramref name="to"/> (polygons are counter-clockwise on the X/Z plane, so the edge's end is on the left).
    /// </summary>
    bool Portal(NavRef from, int edge, NavRef to, out Vector3 left, out Vector3 right) => Portal(from, edge, to, out left, out right, out _, out _);

    bool Portal(NavRef from, int edge, NavRef to, out Vector3 left, out Vector3 right, out bool leftWall, out bool rightWall)
    {
        var ma = World.Mesh(from.Zone);
        var pa = ma.Polygons[from.Polygon];
        var a0 = ma.Vertices[pa[edge]];
        var a1 = ma.Vertices[pa[(edge + 1) % pa.Length]];
        left = a1;
        right = a0;
        leftWall = ma.IsWallVertex(pa[(edge + 1) % pa.Length]);
        rightWall = ma.IsWallVertex(pa[edge]);
        // Overlap with the facing edge of the other polygon (found as the edge that leads back, nearest in position).
        var mb = World.Mesh(to.Zone);
        var pb = mb.Polygons[to.Polygon];
        int bestEdge = -1;
        float bestDistance = float.MaxValue;
        var mid = (a0 + a1) * 0.5f;
        for (int k = 0; k < pb.Length; k++)
        {
            var b0 = mb.Vertices[pb[k]];
            var b1 = mb.Vertices[pb[(k + 1) % pb.Length]];
            // Collinear and facing: both ends within a small distance of a0 a1's line, opposite direction, overlapping.
            var d = a1 - a0;
            float len2 = d.X * d.X + d.Z * d.Z;
            if (len2 < 1e-9f) continue;
            float t0 = ((b0.X - a0.X) * d.X + (b0.Z - a0.Z) * d.Z) / len2, t1 = ((b1.X - a0.X) * d.X + (b1.Z - a0.Z) * d.Z) / len2;
            float dist0 = DistanceToLine(a0, d, b0), dist1 = DistanceToLine(a0, d, b1);
            if (dist0 > 0.2f || dist1 > 0.2f) continue;
            if (t1 > t0) continue; // facing edges run the opposite way
            float lo = Math.Max(0, t1), hi = Math.Min(1, t0);
            if (hi - lo < 1e-4f) continue;
            float score = Vector3.Distance(mid, (b0 + b1) * 0.5f);
            if (score < bestDistance) { bestDistance = score; bestEdge = k; }
        }
        if (bestEdge < 0) return true; // no facing edge found (a degenerate stitch): the whole edge
        {
            var b0 = mb.Vertices[pb[bestEdge]];
            var b1 = mb.Vertices[pb[(bestEdge + 1) % pb.Length]];
            var d = a1 - a0;
            float len2 = d.X * d.X + d.Z * d.Z;
            float t0 = ((b0.X - a0.X) * d.X + (b0.Z - a0.Z) * d.Z) / len2, t1 = ((b1.X - a0.X) * d.X + (b1.Z - a0.Z) * d.Z) / len2;
            float lo = Math.Max(0, t1), hi = Math.Min(1, t0);
            right = Vector3.Lerp(a0, a1, lo);
            left = Vector3.Lerp(a0, a1, hi);
            if (hi < 1 - 1e-4f) leftWall = mb.IsWallVertex(pb[bestEdge]);
            if (lo > 1e-4f) rightWall = mb.IsWallVertex(pb[(bestEdge + 1) % pb.Length]);
        }
        return true;
    }

    static float DistanceToLine(Vector3 a, Vector3 d, Vector3 p)
    {
        float len = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        return MathF.Abs((p.X - a.X) * d.Z - (p.Z - a.Z) * d.X) / len;
    }

    // ---- string pulling ----

    /// <summary>The straight path through a corridor: the funnel over the portals, each pulled in by the agent's radius.</summary>
    public List<Vector3> Pull(Corridor c, NavAgent agent)
    {
        var polys = c.Polygons;
        var portalsL = new List<Vector3>(polys.Count + 1);
        var portalsR = new List<Vector3>(polys.Count + 1);
        portalsL.Add(c.Start); portalsR.Add(c.Start);
        for (int i = 0; i + 1 < polys.Count; i++)
        {
            var from = polys[i];
            var to = polys[i + 1];
            bool lw = true, rw = true;
            if (!FindPortal(from, to, out var l, out var r, out lw, out rw)) { l = r = World.Mesh(to.Zone).Centre(to.Polygon); }
            // Pull in by the radius the ends that touch a wall (an obstacle or a drop), never past the middle; the other ends are open ground.
            var d = r - l;
            float len = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            if (len > 1e-4f)
            {
                var u = d / len;
                float ins = Math.Min(agent.Radius, len / 2);
                float insetL = lw ? ins : 0, insetR = rw ? ins : 0;
                if (lw && rw && insetL + insetR > len) insetL = insetR = len / 2;
                l += u * insetL;
                r -= u * insetR;
            }
            portalsL.Add(l); portalsR.Add(r);
        }
        portalsL.Add(c.Goal); portalsR.Add(c.Goal);

        var path = new List<Vector3> { c.Start };
        var apex = c.Start;
        var left = c.Start;
        var right = c.Start;
        int apexIndex = 0, leftIndex = 0, rightIndex = 0;
        for (int i = 1; i < portalsL.Count; i++)
        {
            var nl = portalsL[i];
            var nr = portalsR[i];
            // Tighten the right side.
            if (Cross(apex, right, nr) >= 0)
            {
                if (Same(apex, right) || Cross(apex, left, nr) < 0) { right = nr; rightIndex = i; }
                else
                {
                    path.Add(left);
                    apex = left; apexIndex = leftIndex;
                    left = right = apex; leftIndex = rightIndex = apexIndex;
                    i = apexIndex;
                    continue;
                }
            }
            // Tighten the left side.
            if (Cross(apex, left, nl) <= 0)
            {
                if (Same(apex, left) || Cross(apex, right, nl) > 0) { left = nl; leftIndex = i; }
                else
                {
                    path.Add(right);
                    apex = right; apexIndex = rightIndex;
                    left = right = apex; leftIndex = rightIndex = apexIndex;
                    i = apexIndex;
                    continue;
                }
            }
        }
        if (!Same(path[^1], c.Goal)) path.Add(c.Goal);
        else path[^1] = c.Goal;

        // Heights: the surface of the corridor polygon the point lies in.
        for (int i = 0; i < path.Count; i++) path[i] = OnSurface(path[i], polys);
        return path;
    }

    Vector3 OnSurface(Vector3 p, List<NavRef> polys)
    {
        float best = float.MaxValue;
        var result = p;
        foreach (var r in polys)
        {
            var m = World.Mesh(r.Zone);
            var onPoly = NavWorld.ClosestPoint(m, r.Polygon, p);
            float d = (onPoly.X - p.X) * (onPoly.X - p.X) + (onPoly.Z - p.Z) * (onPoly.Z - p.Z);
            if (d < best - 1e-6f) { best = d; result = new Vector3(p.X, onPoly.Y, p.Z); if (d < 1e-6f) break; }
        }
        return result;
    }

    static bool Same(Vector3 a, Vector3 b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z) < 1e-6f;

    /// <summary>Positive when c is to the left of the line a → b on the X/Z plane (X right, Z up).</summary>
    static float Cross(Vector3 a, Vector3 b, Vector3 c) => (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);

    bool FindPortal(NavRef from, NavRef to, out Vector3 left, out Vector3 right, out bool leftWall, out bool rightWall)
    {
        var m = World.Mesh(from.Zone);
        if (from.Zone == to.Zone)
        {
            var nb = m.Neighbours[from.Polygon];
            for (int e = 0; e < nb.Length; e++)
                if (nb[e] == to.Polygon) return Portal(from, e, to, out left, out right, out leftWall, out rightWall);
            var tl = m.LinksOf(from.Polygon);
            for (int i = 0; i < tl.Length; i += 2)
                if (tl[i + 1] == to.Polygon) return Portal(from, tl[i], to, out left, out right, out leftWall, out rightWall);
        }
        foreach (var link in World.LinksOf(from))
            if (link.To == to) return Portal(from, link.Edge, to, out left, out right, out leftWall, out rightWall);
        left = right = default;
        leftWall = rightWall = true;
        return false;
    }
}
