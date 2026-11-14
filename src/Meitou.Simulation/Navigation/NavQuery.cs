using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Towns;
using Meitou.Threading;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Who is asking for a path: the footprint radius (a portal must leave the radius clear at each end on a wall) and what the ground costs them.
/// </summary>
public sealed record NavAgent
{
    /// <summary>
    /// RACE <c>pathfind footprint radius</c> × the size factor (humans 4, Garru 7, Leviathan 40).
    /// </summary>
    public float Radius { get; init; } = 4;
    /// <summary>
    /// Cost multipliers of the water polygons by depth (<see cref="NavArea.WetState"/>): at least a + 1 for RACE <c>water avoidance</c> a ≥ 0, 1 /
    /// (1 − a) below, halved for the player's faction, and more where the agent is that much slower than on land (docs/game/pathfinding.md, "Water
    /// costs"). 7 is a Greenlander (a = 6).
    /// </summary>
    public WaterCost Water { get; init; } = WaterCost.Uniform(7);
    /// <summary>
    /// FACTION <c>road preference</c>: a long route takes the roads by the original's rule (<see cref="RoadNetwork.Route"/>); 0 never.
    /// </summary>
    public float RoadPreference { get; init; }
    /// <summary>
    /// Doors are blocked (closed, face data 5) when this is true; open doors cost <see cref="NavQuery.OpenDoorCost"/> extra.
    /// </summary>
    public bool DoorsClosed { get; init; }
    /// <summary>
    /// The default agent: a human's radius 4, water cost 7, no road preference, doors open.
    /// </summary>
    public static NavAgent Human { get; } = new();
}

/// <summary>
/// Path queries over a <see cref="NavWorld"/>: A* over the polygons with a portal clearance of the radius per wall end, the water cost factor and the
/// door rules, then string pulling with the portals pulled in by the radius (docs/game/pathfinding.md, "Path queries" and "What our builder needs").
/// </summary>
public sealed class NavQuery(NavWorld world, NavDoors? doors = null)
{
    /// <summary>
    /// Extra cost of an open door (the original's WaterCostModifier adds 5.0 to each cost component, Havok units).
    /// </summary>
    public const float OpenDoorCost = 50;
    /// <summary>
    /// The original's iteration limit.
    /// </summary>
    public const int MaxIterations = 100000;

    /// <summary>
    /// The counters behind <see cref="Stats"/>, updated with <see cref="Interlocked"/>.
    /// </summary>
    static long s_searches, s_expanded, s_exhausted;

    /// <summary>
    /// Corridor searches so far, the polygons they expanded and how many ran to <see cref="MaxIterations"/> (all threads; the exit log prints them).
    /// </summary>
    public static (long Searches, long Expanded, long Exhausted) Stats => (Interlocked.Read(ref s_searches), Interlocked.Read(ref s_expanded),
            Interlocked.Read(ref s_exhausted));
    /// <summary>
    /// How far (units) from the start / the goal a polygon may be and still count. Engine choice: the original looks for the start face within 500
    /// Havok units (5000 Kenshi units) and the goal face within about 30 (Observed, docs/game/pathfinding.md, "Path queries").
    /// </summary>
    public const float StartSnap = 60, GoalSnap = 30;
    /// <summary>
    /// How near (units) a walker shut out of its goal must stand to its start polygon's point nearest the goal to have no way toward it (engine
    /// choice).
    /// </summary>
    const float PartialStandStill = 1;

    /// <summary>
    /// The navmesh the queries search.
    /// </summary>
    public NavWorld World { get; } = world;
    /// <summary>
    /// The run-time door table the queries consult; null: every door is open.
    /// </summary>
    public NavDoors? Doors { get; } = doors;

    /// <summary>
    /// Whether the door a polygon belongs to has been closed at run time.
    /// </summary>
    bool IsDoorClosed(ZoneNavMesh mesh, int polygon) =>
        Doors is not null && mesh.DoorOf is { } of && of[polygon] is var d and >= 0 && Doors.IsClosed(mesh.DoorIds![d]);

    /// <summary>
    /// Result of the polygon search before string pulling.
    /// </summary>
    public sealed record Corridor(List<NavRef> Polygons, Vector3 Start, Vector3 Goal, int Iterations);

    /// <summary>
    /// One polygon reached by a search: its cost so far and estimate, and the key of the node it was reached from (-1 for the start).
    /// </summary>
    sealed class Node
    {
        /// <summary>
        /// The polygon this node stands for.
        /// </summary>
        public NavRef Ref;
        /// <summary>
        /// The point the search entered the polygon at.
        /// </summary>
        public Vector3 Position;
        /// <summary>
        /// The cost from the start (G) and that plus the estimate to the goal (F, the open list's priority).
        /// </summary>
        public float G, F;
        /// <summary>
        /// What two entries into the polygon are compared by: the cost so far plus the straight way on to the goal at the polygon's own cost
        /// factor (<see cref="Relax"/>).
        /// </summary>
        public float Through;
        /// <summary>
        /// The <see cref="NavRef.Key"/> of the node it was reached from; -1 for the start.
        /// </summary>
        public long Parent = -1;
        /// <summary>
        /// Whether the node has been expanded.
        /// </summary>
        public bool Closed;
    }

    /// <summary>
    /// A thread's A* state, reused by every search on it.
    /// </summary>
    sealed class SearchScratch
    {
        /// <summary>
        /// The nodes of the current search by <see cref="NavRef.Key"/>.
        /// </summary>
        public readonly Dictionary<long, Node> Nodes = [];
        /// <summary>
        /// The open list, by <see cref="Node.F"/>.
        /// </summary>
        public readonly PriorityQueue<Node, float> Open = new();
        /// <summary>
        /// Whether the last search explored all it could reach without finding its goal (not cut short by <see cref="MaxIterations"/>).
        /// </summary>
        public bool Exhaustive;
        /// <summary>
        /// Whether the current search was turned back by a door closed at run time (<see cref="Doors"/>).
        /// </summary>
        public bool MetClosedDoor;
        /// <summary>
        /// Every node made on this thread, handed out again from the start by each search (<see cref="Take"/>).
        /// </summary>
        readonly List<Node> _made = [];
        /// <summary>
        /// How many of <see cref="_made"/> the current search has taken.
        /// </summary>
        int _used;

        /// <summary>
        /// Empties the node table and the open list for a new search, keeping their capacity and the nodes made.
        /// </summary>
        public void Reset()
        {
            // Kept whatever a search grew it to (MaxIterations bounds it: tens of MB a path thread). Dropping it after a long search made every
            // search that ran to the limit (an unreachable goal) rebuild it on the large object heap, ~280 MB/s of garbage in a town (issue #124,
            // docs/bench.md).
            Nodes.Clear();
            Open.Clear();
            _used = 0;
            MetClosedDoor = false;
        }

        /// <summary>
        /// A node for polygon <paramref name="at"/> entered at <paramref name="position"/>, reused from an earlier search where one is free.
        /// </summary>
        public Node Take(NavRef at, Vector3 position, float g, float f)
        {
            if (_used == _made.Count) _made.Add(new Node());
            var node = _made[_used++];
            (node.Ref, node.Position, node.G, node.F, node.Parent, node.Closed) = (at, position, g, f, -1, false);
            node.Through = float.MaxValue;
            return node;
        }
    }

    /// <summary>
    /// This thread's search state, made by its first search.
    /// </summary>
    [ThreadStatic] static SearchScratch? s_searchScratch;

    /// <summary>
    /// The state one <see cref="FindCorridor"/> search shares with its steps.
    /// </summary>
    struct CorridorSearch(SearchScratch scratch, NavAgent agent, Node first, Vector3 goalPoint)
    {
        /// <summary>
        /// The thread's table, queue and nodes.
        /// </summary>
        public readonly SearchScratch Scratch = scratch;

        /// <summary>
        /// The walker searched for.
        /// </summary>
        public readonly NavAgent Agent = agent;

        /// <summary>
        /// The start polygon's node.
        /// </summary>
        public readonly Node First = first;

        /// <summary>
        /// The point searched toward.
        /// </summary>
        public readonly Vector3 GoalPoint = goalPoint;

        /// <summary>
        /// The lowest area cost factor, which keeps the heuristic admissible.
        /// </summary>
        public readonly float MinFactor = Math.Min(1f, agent.Water.Min);

        /// <summary>
        /// A start polygon none of whose exits leaves the radius clear (a sliver by a stair or a wall the walker was pushed into) is left through the
        /// narrow ones: the walker stands in it already (docs/game/pathfinding.md, "Own builder").
        /// </summary>
        public bool SqueezeOut;
    }

    /// <summary>
    /// A path from <paramref name="from"/> to <paramref name="to"/> for <paramref name="agent"/> (null: <see cref="NavAgent.Human"/>).
    /// </summary>
    public PathResult FindPath(Vector3 from, Vector3 to, NavAgent? agent = null) => FindPath(from, to, agent ?? NavAgent.Human, null, out _);

    /// <summary>
    /// As <see cref="FindPath(Vector3, Vector3, NavAgent?)"/>, from the nearest start polygon <paramref name="skipStart"/> does not rule out;
    /// <paramref name="start"/> is the one taken (Zone −1 when none was found).
    /// </summary>
    public PathResult FindPath(Vector3 from, Vector3 to, NavAgent agent, Func<NavRef, bool>? skipStart, out NavRef start)
    {
        if (!World.TryFindPolygon(from, StartSnap, out start, out var startPoint, skipStart))
        {
            start = new NavRef(-1, -1);
            return PathResult.NotFound;
        }
        if (s_searchScratch is { } last) last.Exhaustive = false;
        if (!World.TryFindPolygon(to, GoalSnap, out var goal, out var goalPoint)) return PathResult.NotFound;
        var corridor = FindCorridor(start, startPoint, goal, goalPoint, agent);
        if (corridor is null) return PathResult.NotFound;
        return new PathResult(Pull(corridor, agent));
    }

    /// <summary>
    /// When the last search on this thread failed after reaching all it could, adds the polygons it reached to <paramref name="into"/>
    /// (<see cref="NavRef.Key"/>): a search from any of them fails too.
    /// </summary>
    public static void AddReachedByFailed(HashSet<long> into)
    {
        if (s_searchScratch is { Exhaustive: true } s)
            foreach (var (key, node) in s.Nodes)
                if (node.Closed) into.Add(key);
    }

    /// <summary>
    /// Whether the last search on this thread was turned back by a door closed at run time somewhere.
    /// </summary>
    public static bool LastSearchMetClosedDoor => s_searchScratch?.MetClosedDoor == true;

    /// <summary>
    /// The point nearest <paramref name="near"/> of the polygons of doors closed at run time on the way from <paramref name="from"/> to
    /// <paramref name="to"/> with every door open; null when that way does not exist or crosses no closed door.
    /// </summary>
    public Vector3? ClosedDoorOnTheWay(Vector3 from, Vector3 to, Vector3 near, NavAgent agent)
    {
        if (!World.TryFindPolygon(from, StartSnap, out var start, out var startPoint)) return null;
        if (!World.TryFindPolygon(to, GoalSnap, out var goal, out var goalPoint)) return null;
        if (new NavQuery(World).FindCorridor(start, startPoint, goal, goalPoint, agent) is not { } open) return null;

        Vector3? best = null;
        foreach (var p in open.Polygons)
        {
            var mesh = World.Mesh(p.Zone);
            if (mesh.Areas[p.Polygon] != NavArea.Door || !IsDoorClosed(mesh, p.Polygon)) continue;

            var point = NavWorld.ClosestPoint(mesh, p.Polygon, near);
            if (best is not { } b || Vector3.Distance(point, near) < Vector3.Distance(b, near)) best = point;
        }

        return best;
    }

    /// <summary>
    /// A path to <paramref name="to"/>, or, when the goal cannot be reached (or is off the mesh), to the reachable point nearest it: the first part
    /// of a long route, whose end is then left to the next query (docs/game/pathfinding.md, "Long routes").
    /// </summary>
    public PathResult FindPathToward(Vector3 from, Vector3 to, NavAgent? agent = null)
    {
        agent ??= NavAgent.Human;
        if (!World.TryFindPolygon(from, StartSnap, out var start, out var startPoint)) return PathResult.NotFound;
        if (!World.TryFindPolygon(to, GoalSnap, out var goal, out var goalPoint))
            (goal, goalPoint) = (new NavRef(-1, -1), to);
        var corridor = FindCorridor(start, startPoint, goal, goalPoint, agent, partial: true);
        return corridor is null ? PathResult.NotFound : new PathResult(Pull(corridor, agent));
    }

    /// <summary>
    /// A* from the start polygon to the goal polygon; null when there is none (or the iteration limit ran out). <paramref name="partial"/>: then the
    /// corridor to the searched polygon nearest the goal point instead.
    /// </summary>
    public Corridor? FindCorridor(NavRef start, Vector3 startPoint, NavRef goal, Vector3 goalPoint, NavAgent agent, bool partial = false)
    {
        // The search's table, queue and nodes are the thread's, kept between searches: a fresh set per search was a tenth of the game's garbage
        // (docs/bench.md "Play benchmark").
        var scratch = s_searchScratch ??= new SearchScratch();
        scratch.Reset();
        var nodes = scratch.Nodes;
        var open = scratch.Open;
        var first = scratch.Take(start, startPoint, 0, Vector3.Distance(startPoint, goalPoint));
        nodes[start.Key] = first;
        open.Enqueue(first, first.F);
        var search = new CorridorSearch(scratch, agent, first, goalPoint);
        var (last, nearest, iterations) = Search(ref search, goal, partial);

        Interlocked.Increment(ref s_searches);
        Interlocked.Add(ref s_expanded, iterations);
        if (iterations > MaxIterations) Interlocked.Increment(ref s_exhausted);
        scratch.Exhaustive = last is null && iterations <= MaxIterations;

        // The start polygon itself can be the nearest: a large one runs up to the wall between the walker and the goal, so it walks across it unless
        // it stands there already.
        if (last is null && partial && NavWorld.ClosestPoint(World.Mesh(nearest.Ref.Zone), nearest.Ref.Polygon, goalPoint) is var near
            && (nearest != first || Vector3.Distance(near, startPoint) > PartialStandStill))
        {
            last = nearest;
            goalPoint = near;
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
    }

    /// <summary>
    /// The A* loop of <see cref="FindCorridor"/>: the goal's node when reached, the searched node nearest the goal point (<paramref name="partial"/>
    /// only) and the iterations used.
    /// </summary>
    (Node? Last, Node Nearest, int Iterations) Search(ref CorridorSearch search, NavRef goal, bool partial)
    {
        var open = search.Scratch.Open;
        var first = search.First;
        var goalPoint = search.GoalPoint;
        int iterations = 0;
        Node? last = null, nearest = first;
        float nearestDistance = float.MaxValue;
        while (open.Count > 0 && iterations++ < MaxIterations)
        {
            SafePoint.Every(iterations);
            var node = open.Dequeue();
            if (node.Closed) continue;
            node.Closed = true;
            if (node.Ref == goal)
            {
                last = node;
                break;
            }

            var mesh = World.Mesh(node.Ref.Zone);
            int p = node.Ref.Polygon;
            // Nearest in 3D, not on the X/Z plane: for a goal on a wall top the ground beside the wall is nearer on the plane than the walkway it
            // lies on.
            if (partial && Vector3.Distance(NavWorld.ClosestPoint(mesh, p, goalPoint), goalPoint) is var d && d < nearestDistance)
                (nearest, nearestDistance) = (node, d);
            Expand(ref search, node, mesh, p);
            if (node == first && open.Count == 0 && last is null && !search.SqueezeOut)
            {
                search.SqueezeOut = true;
                first.Closed = false;
                open.Enqueue(first, first.F);
            }
        }
        return (last, nearest, iterations);
    }

    /// <summary>
    /// Relaxes every way out of polygon <paramref name="p"/>: its neighbours, its tile links and the world's links.
    /// </summary>
    void Expand(ref CorridorSearch search, Node node, ZoneNavMesh mesh, int p)
    {
        float factor = Factor(mesh.Areas[p], search.Agent);
        var poly = mesh.Polygons[p];
        for (int e = 0; e < poly.Length; e++)
        {
            int q = mesh.Neighbours[p][e];
            if (q >= 0) Relax(ref search, node, mesh, e, new NavRef(node.Ref.Zone, q), factor);
        }
        var tl = mesh.LinksOf(p);
        for (int i = 0; i < tl.Length; i += 2)
            Relax(ref search, node, mesh, tl[i], new NavRef(node.Ref.Zone, tl[i + 1]), factor);
        foreach (var link in World.LinksOf(node.Ref))
            Relax(ref search, node, mesh, link.Edge, link.To, factor);
    }

    /// <summary>
    /// One A* edge: the way from <paramref name="from"/> through its edge <paramref name="edge"/> into <paramref name="to"/>, when open and wide
    /// enough, improves <paramref name="to"/>'s node.
    /// </summary>
    void Relax(ref CorridorSearch search, Node from, ZoneNavMesh mesh, int edge, NavRef to, float factor)
    {
        var agent = search.Agent;
        var target = World.Mesh(to.Zone);
        if (!target.Kept[to.Polygon]) return;
        byte area = target.Areas[to.Polygon];
        if (area == NavArea.Door && agent.DoorsClosed) return;
        if (area == NavArea.Door && IsDoorClosed(target, to.Polygon))
        {
            search.Scratch.MetClosedDoor = true;
            return;
        }
        if (!Portal(from.Ref, edge, to, out var left, out var right, out bool leftWall, out bool rightWall)) return;

        // Only an end on a wall takes the radius off the portal (as Pull insets it); a tile-border, seam or area-change end has walkable ground
        // beyond.
        float width = Vector2.Distance(new(left.X, left.Z), new(right.X, right.Z));
        if (width < ((leftWall ? 1 : 0) + (rightWall ? 1 : 0)) * agent.Radius && !(search.SqueezeOut && from == search.First))
            return;
        var mid = BestCrossing(from.Position, left, right, search.GoalPoint);
        float g = from.G + Vector3.Distance(from.Position, mid) * factor;
        if (area == NavArea.Door) g += OpenDoorCost;
        var nodes = search.Scratch.Nodes;
        if (!nodes.TryGetValue(to.Key, out var node))
        {
            node = search.Scratch.Take(to, mid, float.MaxValue, 0);
            nodes[to.Key] = node;
        }

        // A polygon keeps one entry point. Of two, the cheaper so far is not the better one when it lies farther from the goal across a large
        // polygon of costly ground: the way on is counted at the polygon's own factor (docs/game/pathfinding.md, "Search cost").
        float toGoal = Vector3.Distance(mid, search.GoalPoint);
        float through = g + toGoal * Factor(area, agent);
        if (node.Closed || through >= node.Through) return;

        node.Through = through;
        node.G = g;
        node.Position = mid;
        node.Parent = from.Ref.Key;
        node.F = g + toGoal * search.MinFactor;
        search.Scratch.Open.Enqueue(node, node.F);
    }

    /// <summary>
    /// The point of the portal that makes the way from <paramref name="prev"/> through it to the goal shortest (the portal's ends clamp it), so the
    /// search sees near-straight legs even through thin polygons.
    /// </summary>
    static Vector3 BestCrossing(Vector3 prev, Vector3 left, Vector3 right, Vector3 goal)
    {
        // Golden-section search (the detour is convex along the portal): each step keeps one of its two probes, so 12 steps cost 14 detours and
        // narrow the interval to 0.3 % as the 14 ternary steps (28 detours) before did; the A* spends most of its time here (docs/game/pathfinding.md
        // "Search cost").
        const float invPhi = 0.618034f;
        float lo = 0, hi = 1;
        float a = hi - invPhi * (hi - lo), b = lo + invPhi * (hi - lo);
        float fa = Detour(prev, Vector3.Lerp(left, right, a), goal), fb = Detour(prev, Vector3.Lerp(left, right, b), goal);
        for (int i = 0; i < 12; i++)
        {
            if (fa < fb)
            {
                (hi, b, fb) = (b, a, fa);
                a = hi - invPhi * (hi - lo);
                fa = Detour(prev, Vector3.Lerp(left, right, a), goal);
            }
            else
            {
                (lo, a, fa) = (a, b, fb);
                b = lo + invPhi * (hi - lo);
                fb = Detour(prev, Vector3.Lerp(left, right, b), goal);
            }
        }
        return Vector3.Lerp(left, right, (lo + hi) / 2);
    }

    /// <summary>
    /// The length of the way from <paramref name="a"/> through <paramref name="p"/> to <paramref name="b"/>.
    /// </summary>
    static float Detour(Vector3 a, Vector3 p, Vector3 b) => Vector3.Distance(a, p) + Vector3.Distance(p, b);

    /// <summary>
    /// The cost factor of a polygon of <paramref name="area"/> for <paramref name="agent"/>: its water cost for the polygon's wet state, 1 on
    /// dry ground.
    /// </summary>
    static float Factor(byte area, NavAgent agent) => NavArea.WetState(area) is var state and > 0 ? agent.Water[state] : 1;

    /// <summary>
    /// The shared segment between a polygon's edge and the polygon behind it: the overlap of the two collinear edges. Left and right are as seen when
    /// walking from <paramref name="from"/> into <paramref name="to"/> (polygons are counter-clockwise on the X/Z plane, so the edge's end is on the
    /// left).
    /// </summary>
    bool Portal(NavRef from, int edge, NavRef to, out Vector3 left, out Vector3 right) => Portal(from, edge, to, out left, out right, out _, out _);

    /// <summary>
    /// As the other overload, also telling whether each end of the portal is a wall vertex (where the radius must be left clear).
    /// </summary>
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
            if (score < bestDistance)
            {
                bestDistance = score;
                bestEdge = k;
            }
        }
        if (bestEdge < 0)
        {
            // A near-miss seam (docs/game/pathfinding.md, "Inner seams") or a door join: the facing edge strays off the line, so take the overlap
            // with a looser gap, as loose as the join's (a tighter one cuts a slanted door edge down to a sliver no one fits through).
            for (int k = 0; k < pb.Length; k++)
            {
                var b0 = mb.Vertices[pb[k]];
                var b1 = mb.Vertices[pb[(k + 1) % pb.Length]];
                if (!NavGeometry.FacingOverlap(a0, a1, b0, b1, Math.Max(NavGeometry.NearMissGap + 0.5f, NavInteriors.JoinDistance), out float lo,
                        out float hi, out _, out _))
                    continue;
                float score = Vector3.Distance(mid, (b0 + b1) * 0.5f);
                if (score >= bestDistance) continue;
                bestDistance = score;
                right = Vector3.Lerp(a0, a1, lo);
                left = Vector3.Lerp(a0, a1, hi);
                leftWall = hi < 1 - 1e-4f ? mb.IsWallVertex(pb[k]) : ma.IsWallVertex(pa[(edge + 1) % pa.Length]);
                rightWall = lo > 1e-4f ? mb.IsWallVertex(pb[(k + 1) % pb.Length]) : ma.IsWallVertex(pa[edge]);
            }
            return true; // with no facing edge at all (a degenerate stitch): the whole edge
        }
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

    /// <summary>
    /// The distance on the X/Z plane from <paramref name="p"/> to the line through <paramref name="a"/> along <paramref name="d"/>.
    /// </summary>
    static float DistanceToLine(Vector3 a, Vector3 d, Vector3 p)
    {
        float len = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        return MathF.Abs((p.X - a.X) * d.Z - (p.Z - a.Z) * d.X) / len;
    }

    // ---- string pulling ----

    /// <summary>
    /// The straight path through a corridor: the funnel over the portals, each pulled in by the agent's radius.
    /// </summary>
    public List<Vector3> Pull(Corridor c, NavAgent agent)
    {
        var polys = c.Polygons;
        var (portalsL, portalsR) = Portals(c, agent);
        var path = Funnel(c, portalsL, portalsR);

        // Heights: the surface of the corridor polygon the point lies in.
        for (int i = 0; i < path.Count; i++) path[i] = OnSurface(path[i], polys);
        return path;
    }

    /// <summary>
    /// The corridor's portals from its start to its goal, as left and right ends, each pulled in by the agent's radius where it touches a wall.
    /// </summary>
    (List<Vector3> Left, List<Vector3> Right) Portals(Corridor c, NavAgent agent)
    {
        var polys = c.Polygons;
        var portalsL = new List<Vector3>(polys.Count + 1);
        var portalsR = new List<Vector3>(polys.Count + 1);
        portalsL.Add(c.Start);
        portalsR.Add(c.Start);
        for (int i = 0; i + 1 < polys.Count; i++)
        {
            var from = polys[i];
            var to = polys[i + 1];
            bool lw = true, rw = true;
            if (!FindPortal(from, to, out var l, out var r, out lw, out rw))
            {
                l = r = World.Mesh(to.Zone).Centre(to.Polygon);
            }
            var d = r - l;
            float len = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            bool OnPortal(Vector3 p) =>
                DistanceToLine(l, d, p) < 1e-3f && (p.X - l.X) * d.X + (p.Z - l.Z) * d.Z is var t && t >= 0 && t <= len * len;
            // A start on the portal's own line (a point on a shared edge) already stands past it: the funnel from a point on its line is degenerate
            // and swung out to the portal's end (Observed, Swamp Village stair foot: 153 for 83).
            if (len > 1e-4f && portalsL.Count == 1 && OnPortal(c.Start)) continue;
            // Pull in by the radius the ends that touch a wall (an obstacle or a drop), never past the middle; the other ends are open ground.
            if (len > 1e-4f)
            {
                var u = d / len;
                float ins = Math.Min(agent.Radius, len / 2);
                float insetL = lw ? ins : 0, insetR = rw ? ins : 0;
                if (lw && rw && insetL + insetR > len) insetL = insetR = len / 2;
                l += u * insetL;
                r -= u * insetR;
            }
            portalsL.Add(l);
            portalsR.Add(r);
        }
        portalsL.Add(c.Goal);
        portalsR.Add(c.Goal);
        return (portalsL, portalsR);
    }

    /// <summary>
    /// The funnel over the portals: the straight path from the corridor's start to its goal, at the portals' heights.
    /// </summary>
    static List<Vector3> Funnel(Corridor c, List<Vector3> portalsL, List<Vector3> portalsR)
    {
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
                if (Same(apex, right) || Cross(apex, left, nr) < 0)
                {
                    right = nr;
                    rightIndex = i;
                }
                else
                {
                    path.Add(left);
                    apex = left;
                    apexIndex = leftIndex;
                    left = right = apex;
                    leftIndex = rightIndex = apexIndex;
                    i = apexIndex;
                    continue;
                }
            }
            // Tighten the left side.
            if (Cross(apex, left, nl) <= 0)
            {
                if (Same(apex, left) || Cross(apex, right, nl) > 0)
                {
                    left = nl;
                    leftIndex = i;
                }
                else
                {
                    path.Add(right);
                    apex = right;
                    apexIndex = rightIndex;
                    left = right = apex;
                    leftIndex = rightIndex = apexIndex;
                    i = apexIndex;
                    continue;
                }
            }
        }
        if (!Same(path[^1], c.Goal)) path.Add(c.Goal);
        else path[^1] = c.Goal;
        return path;
    }

    /// <summary>
    /// The point at the height of the corridor polygon it lies in; of stacked ones (a corridor through a ground floor and up onto the roof above it)
    /// the one nearest its own height, so a goal on the roof is not put on the floor under it (Observed: the Bughouse roof beds at Squin).
    /// </summary>
    Vector3 OnSurface(Vector3 p, List<NavRef> polys)
    {
        float best = float.MaxValue, bestRise = float.MaxValue;
        var result = p;
        foreach (var r in polys)
        {
            var m = World.Mesh(r.Zone);
            var onPoly = NavWorld.ClosestPoint(m, r.Polygon, p);
            float d = (onPoly.X - p.X) * (onPoly.X - p.X) + (onPoly.Z - p.Z) * (onPoly.Z - p.Z), rise = MathF.Abs(onPoly.Y - p.Y);
            if (d < 1e-6f && best < 1e-6f ? rise < bestRise - StackedGap : d < best - 1e-6f)
            {
                (best, bestRise) = (d, rise);
                result = new Vector3(p.X, onPoly.Y, p.Z);
            }
        }
        return result;
    }

    /// <summary>
    /// How much nearer in height another polygon under a path point must be to count as the layer it stands on (engine choice: below a storey, as
    /// <see cref="NavmeshWalkability.StandRise"/>; overlapping stair polygons differ by ~1.3 at the Hub).
    /// </summary>
    const float StackedGap = 12;

    /// <summary>
    /// Whether two points coincide on the X/Z plane (within 1e-3).
    /// </summary>
    static bool Same(Vector3 a, Vector3 b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z) < 1e-6f;

    /// <summary>
    /// Positive when c is to the left of the line a → b on the X/Z plane (X right, Z up).
    /// </summary>
    static float Cross(Vector3 a, Vector3 b, Vector3 c) => (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);

    /// <summary>
    /// The portal between two adjacent polygons: across a shared edge or a link in the same zone, else across a border link; false (both ends walls)
    /// when they do not touch.
    /// </summary>
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
                if (tl[i + 1] == to.Polygon)
                    return Portal(from, tl[i], to, out left, out right, out leftWall, out rightWall);
        }
        foreach (var link in World.LinksOf(from))
            if (link.To == to) return Portal(from, link.Edge, to, out left, out right, out leftWall, out rightWall);
        left = right = default;
        leftWall = rightWall = true;
        return false;
    }
}