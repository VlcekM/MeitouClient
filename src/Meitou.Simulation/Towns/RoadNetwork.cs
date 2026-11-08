using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>
/// The road network off-screen squads travel by (docs/game/factions-squads-towns.md "Off-screen world", roads): each ROAD with 2 points or more and a
/// positive <c>length</c> is an edge between its <c>start</c> and <c>end</c> junctions, costing its <c>length</c>. <see cref="Route"/> applies the original's
/// rule for a trip; the result is the points to walk through. Read-only once built, so any thread may ask.
/// </summary>
public sealed class RoadNetwork
{
    /// <summary>Trips this long or shorter go straight (<b>Verified</b>).</summary>
    public const float MinTrip = 10000;
    /// <summary>How far from a trip's ends a road may be joined (<b>Verified</b>, X/Z).</summary>
    public const float JoinRange = 20000;
    const float Cell = 2000;

    sealed record Road(Vector2[] Points, float[] Along, float Length, int From, int To);

    readonly List<Road> roads = [];
    readonly List<Vector2> junctions = [];
    readonly List<List<int>> edges = [];
    readonly Dictionary<(int, int), List<(int Road, int Segment)>> grid = [];

    public int Count => roads.Count;

    public RoadNetwork(IEnumerable<RoadPath> paths)
    {
        var junctionOf = new Dictionary<int, int>();
        int Junction(int number, Vector2 at)
        {
            if (junctionOf.TryGetValue(number, out int j)) return j;
            junctionOf[number] = j = junctions.Count;
            junctions.Add(at);
            edges.Add([]);
            return j;
        }
        foreach (var path in paths)
        {
            if (path.Points.Count < 2 || path.Length <= 0) continue;
            var points = path.Points.Select(p => new Vector2(p.X, p.Z)).ToArray();
            var along = new float[points.Length];
            for (int i = 1; i < points.Length; i++) along[i] = along[i - 1] + Vector2.Distance(points[i - 1], points[i]);
            float total = along[^1];
            for (int i = 0; i < along.Length; i++) along[i] = total > 0 ? along[i] / total : 0;
            int from = Junction(path.Start, points[0]), to = Junction(path.End, points[^1]);
            int index = roads.Count;
            roads.Add(new Road(points, along, path.Length, from, to));
            edges[from].Add(index);
            if (to != from) edges[to].Add(index);
            for (int s = 0; s + 1 < points.Length; s++)
            {
                var (a, b) = (points[s], points[s + 1]);
                for (int cx = CellOf(MathF.Min(a.X, b.X)); cx <= CellOf(MathF.Max(a.X, b.X)); cx++)
                    for (int cz = CellOf(MathF.Min(a.Y, b.Y)); cz <= CellOf(MathF.Max(a.Y, b.Y)); cz++)
                    {
                        if (!grid.TryGetValue((cx, cz), out var list)) grid[(cx, cz)] = list = [];
                        list.Add((index, s));
                    }
            }
        }
    }

    static int CellOf(float v) => (int)MathF.Floor(v / Cell);

    readonly record struct Spot(int Road, float T, Vector2 Point, float Distance);

    /// <summary>
    /// The road point a trip joins at (<b>Verified</b>: the nearest projected point within <see cref="JoinRange"/>, scored by its distance less a pull of
    /// <paramref name="pull"/> towards <paramref name="direction"/>; engine reading: the pull times the point's forward distance).
    /// </summary>
    Spot? Nearest(Vector2 at, Vector2 direction, float pull)
    {
        Spot? best = null;
        float bestScore = float.MaxValue;
        var seen = new HashSet<(int, int)>();
        int r = (int)MathF.Ceiling(JoinRange / Cell);
        int x0 = CellOf(at.X), z0 = CellOf(at.Y);
        for (int cx = x0 - r; cx <= x0 + r; cx++)
            for (int cz = z0 - r; cz <= z0 + r; cz++)
            {
                if (!grid.TryGetValue((cx, cz), out var list)) continue;
                foreach (var (index, s) in list)
                {
                    if (!seen.Add((index, s))) continue;
                    var road = roads[index];
                    var (a, b) = (road.Points[s], road.Points[s + 1]);
                    var ab = b - a;
                    float len2 = ab.LengthSquared();
                    float u = len2 > 0 ? Math.Clamp(Vector2.Dot(at - a, ab) / len2, 0, 1) : 0;
                    var q = a + ab * u;
                    float d = Vector2.Distance(at, q);
                    if (d > JoinRange) continue;
                    float score = d - pull * Vector2.Dot(q - at, direction);
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = new Spot(index, road.Along[s] + (road.Along[s + 1] - road.Along[s]) * u, q, d);
                }
            }
        return best;
    }

    /// <summary>
    /// The way from <paramref name="from"/> to <paramref name="to"/> for a faction of <paramref name="roadPreference"/> (<b>Verified</b>, the original's mover): over
    /// <see cref="MinTrip"/> and with w = max(0, 1 - preference) below 1, it joins the roads near each end and takes them when the two legs, and the legs plus w × the
    /// road path's cost, are each no longer than the straight line. Returns the points to walk through (the join, the roads' points, the leave, then
    /// <paramref name="to"/>), or null to go straight.
    /// </summary>
    public List<Vector2>? Route(Vector2 from, Vector2 to, float roadPreference)
    {
        float straight = Vector2.Distance(from, to);
        float w = MathF.Max(0, 1 - roadPreference);
        if (straight <= MinTrip || w >= 1 || roads.Count == 0) return null;
        var dir = (to - from) / straight;
        float pull = Math.Clamp(w, 0.2f, 0.9f);
        if (Nearest(from, dir, pull) is not { } entry || Nearest(to, -dir, pull) is not { } exit) return null;
        float legs = entry.Distance + exit.Distance;
        if (legs > straight) return null;
        if (Search(entry, exit) is not { } path || legs + w * path.Cost > straight) return null;
        var points = new List<Vector2> { entry.Point };
        points.AddRange(path.Points);
        points.Add(exit.Point);
        points.Add(to);
        return points;
    }

    /// <summary>
    /// The A* between two road points (<b>Verified</b>: edges cost their road's <c>length</c>, the ends link to both junctions of their road at the share of its
    /// length, the two roads' own edges are not walked, the heuristic is the straight distance to the goal). Returns the cost and the points between them.
    /// </summary>
    (float Cost, List<Vector2> Points)? Search(Spot entry, Spot exit)
    {
        var er = roads[entry.Road];
        var xr = roads[exit.Road];
        // Start and goal on one road: along it.
        float direct = entry.Road == exit.Road ? MathF.Abs(entry.T - exit.T) * er.Length : float.MaxValue;
        var cost = new Dictionary<int, float>();
        var back = new Dictionary<int, int>();   // junction -> the road it was reached by
        var open = new PriorityQueue<int, float>();
        void Seed(int junction, float c)
        {
            if (cost.TryGetValue(junction, out float old) && old <= c) return;
            cost[junction] = c;
            back[junction] = -1;
            open.Enqueue(junction, c + Vector2.Distance(junctions[junction], exit.Point));
        }
        Seed(er.From, entry.T * er.Length);
        Seed(er.To, (1 - entry.T) * er.Length);
        float best = direct;
        int bestJunction = -1;
        var closed = new HashSet<int>();
        while (open.TryDequeue(out int j, out float f))
        {
            if (f >= best) break;
            if (!closed.Add(j)) continue;
            float g = cost[j];
            // Reaching the exit road's junctions finishes along it.
            if (j == xr.From && g + exit.T * xr.Length < best) (best, bestJunction) = (g + exit.T * xr.Length, j);
            if (j == xr.To && g + (1 - exit.T) * xr.Length < best) (best, bestJunction) = (g + (1 - exit.T) * xr.Length, j);
            foreach (int index in edges[j])
            {
                if (index == entry.Road || index == exit.Road) continue;
                var road = roads[index];
                int other = road.From == j ? road.To : road.From;
                float c = g + road.Length;
                if (cost.TryGetValue(other, out float old) && old <= c) continue;
                cost[other] = c;
                back[other] = index;
                open.Enqueue(other, c + Vector2.Distance(junctions[other], exit.Point));
            }
        }
        if (best == float.MaxValue) return null;
        var points = new List<Vector2>();
        if (bestJunction < 0)
        {
            AddAlong(points, er, entry.T, exit.T);
            return (best, points);
        }
        // Back from the junction the exit road was reached at, to a seed junction.
        var chain = new List<(int Road, int To)>();
        for (int j = bestJunction; back.TryGetValue(j, out int via) && via >= 0; j = roads[via].From == j ? roads[via].To : roads[via].From) chain.Add((via, j));
        chain.Reverse();
        int first = chain.Count > 0 ? (roads[chain[0].Road].From == chain[0].To ? roads[chain[0].Road].To : roads[chain[0].Road].From) : bestJunction;
        AddAlong(points, er, entry.T, first == er.From ? 0 : 1);
        foreach (var (index, end) in chain)
        {
            var road = roads[index];
            AddAlong(points, road, road.To == end ? 0 : 1, road.To == end ? 1 : 0);
        }
        AddAlong(points, xr, bestJunction == xr.From ? 0 : 1, exit.T);
        return (best, points);
    }

    /// <summary>The road's points strictly between the shares <paramref name="t0"/> and <paramref name="t1"/> of it, in that direction, then its point at <paramref name="t1"/>.</summary>
    static void AddAlong(List<Vector2> points, Road road, float t0, float t1)
    {
        if (t1 >= t0)
        {
            for (int i = 0; i < road.Points.Length; i++) if (road.Along[i] > t0 && road.Along[i] < t1) points.Add(road.Points[i]);
        }
        else
        {
            for (int i = road.Points.Length - 1; i >= 0; i--) if (road.Along[i] < t0 && road.Along[i] > t1) points.Add(road.Points[i]);
        }
        points.Add(PointAt(road, t1));
    }

    static Vector2 PointAt(Road road, float t)
    {
        for (int i = 1; i < road.Points.Length; i++)
            if (road.Along[i] >= t)
            {
                float span = road.Along[i] - road.Along[i - 1];
                return Vector2.Lerp(road.Points[i - 1], road.Points[i], span > 0 ? (t - road.Along[i - 1]) / span : 0);
            }
        return road.Points[^1];
    }
}
