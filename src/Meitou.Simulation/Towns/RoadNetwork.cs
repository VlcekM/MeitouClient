using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation.Towns;

/// <summary>
/// The road network off-screen squads travel by (docs/game/factions-squads-towns.md "Off-screen world", roads): each ROAD with 2 points or more and a
/// positive <c>length</c> is an edge between its <c>start</c> and <c>end</c> junctions, costing its <c>length</c>. <see cref="Route"/> applies the
/// original's rule for a trip; the result is the points to walk through. Read-only once built, so any thread may ask.
/// </summary>
public sealed class RoadNetwork
{
    /// <summary>
    /// Trips this long or shorter go straight (<b>Verified</b>).
    /// </summary>
    public const float MinTrip = 10000;
    /// <summary>
    /// How far from a trip's ends a road may be joined (<b>Verified</b>, X/Z).
    /// </summary>
    public const float JoinRange = 20000;
    /// <summary>
    /// The edge of a grid cell of the road segments (X/Z).
    /// </summary>
    const float Cell = 2000;

    /// <summary>
    /// A road: its ROAD record's id, its points (X/Z), each point's share of the way along it (0..1), its <c>length</c> and its two junctions.
    /// </summary>
    sealed record Road(string Id, Vector2[] Points, float[] Along, float Length, int From, int To);

    /// <summary>
    /// The roads.
    /// </summary>
    readonly List<Road> _roads = [];
    /// <summary>
    /// The junctions' places.
    /// </summary>
    readonly List<Vector2> _junctions = [];
    /// <summary>
    /// The roads of each junction.
    /// </summary>
    readonly List<List<int>> _edges = [];
    /// <summary>
    /// The road segments crossing each grid cell.
    /// </summary>
    readonly Dictionary<(int, int), List<(int Road, int Segment)>> _grid = [];
    /// <summary>
    /// The grid cells any road crosses lie within these (none when the low one passes the high one).
    /// </summary>
    int _lowX = int.MaxValue, _lowZ = int.MaxValue, _highX = int.MinValue, _highZ = int.MinValue;

    /// <summary>
    /// The number of roads.
    /// </summary>
    public int Count => _roads.Count;

    /// <summary>
    /// Builds the network from the level's roads (a road with fewer than 2 points or no length is left out).
    /// </summary>
    public RoadNetwork(IEnumerable<RoadPath> paths)
    {
        var junctionOf = new Dictionary<int, int>();

        int Junction(int number, Vector2 at)
        {
            if (junctionOf.TryGetValue(number, out int j)) return j;

            junctionOf[number] = j = _junctions.Count;
            _junctions.Add(at);
            _edges.Add([]);
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
            int index = _roads.Count;
            _roads.Add(new Road(path.StringId, points, along, path.Length, from, to));
            _edges[from].Add(index);
            if (to != from) _edges[to].Add(index);

            for (int s = 0; s + 1 < points.Length; s++)
            {
                var (a, b) = (points[s], points[s + 1]);

                for (int cx = CellOf(MathF.Min(a.X, b.X)); cx <= CellOf(MathF.Max(a.X, b.X)); cx++)
                {
                    for (int cz = CellOf(MathF.Min(a.Y, b.Y)); cz <= CellOf(MathF.Max(a.Y, b.Y)); cz++)
                    {
                        if (!_grid.TryGetValue((cx, cz), out var list)) _grid[(cx, cz)] = list = [];

                        list.Add((index, s));
                        (_lowX, _highX) = (Math.Min(_lowX, cx), Math.Max(_highX, cx));
                        (_lowZ, _highZ) = (Math.Min(_lowZ, cz), Math.Max(_highZ, cz));
                    }
                }
            }
        }
    }

    /// <summary>
    /// The grid cell of a coordinate.
    /// </summary>
    static int CellOf(float v) => (int)MathF.Floor(v / Cell);

    /// <summary>
    /// A road point a trip may join at: its road, its share of the way along it, the point and its distance.
    /// </summary>
    readonly record struct Spot(int Road, float T, Vector2 Point, float Distance);

    /// <summary>
    /// The road point a trip joins at (<b>Verified</b>: the nearest projected point within <see cref="JoinRange"/>, scored by its distance less a
    /// pull of <paramref name="pull"/> towards <paramref name="direction"/>; engine reading: the pull times the point's forward distance).
    /// </summary>
    Spot? Nearest(Vector2 at, Vector2 direction, float pull)
    {
        Spot? best = null;
        float bestScore = float.MaxValue;
        var seen = new HashSet<(int, int)>();
        int r = (int)MathF.Ceiling(JoinRange / Cell);
        int x0 = CellOf(at.X), z0 = CellOf(at.Y);

        for (int cx = x0 - r; cx <= x0 + r; cx++)
        {
            for (int cz = z0 - r; cz <= z0 + r; cz++)
            {
                if (!_grid.TryGetValue((cx, cz), out var list)) continue;

                foreach (var (index, s) in list)
                {
                    if (!seen.Add((index, s))) continue;

                    var road = _roads[index];
                    var (q, u, d) = Closest(road, s, at);
                    if (d > JoinRange) continue;

                    float score = d - pull * Vector2.Dot(q - at, direction);
                    if (score >= bestScore) continue;

                    bestScore = score;
                    best = new Spot(index, road.Along[s] + (road.Along[s + 1] - road.Along[s]) * u, q, d);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The way from <paramref name="from"/> to <paramref name="to"/> for a faction of <paramref name="roadPreference"/> (<b>Verified</b>, the
    /// original's mover): over <see cref="MinTrip"/> and with w = max(0, 1 - preference) below 1, it joins the roads near each end and takes them
    /// when the two legs, and the legs plus w × the road path's cost, are each no longer than the straight line. Returns the points to walk through
    /// (the join, the roads' points, the leave, then <paramref name="to"/>), or null to go straight.
    /// </summary>
    public List<Vector2>? Route(Vector2 from, Vector2 to, float roadPreference)
    {
        float straight = Vector2.Distance(from, to);
        float w = MathF.Max(0, 1 - roadPreference);
        if (straight <= MinTrip || w >= 1 || _roads.Count == 0) return null;

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
    /// The A* between two road points (<b>Verified</b>: edges cost their road's <c>length</c>, the ends link to both junctions of their road at the
    /// share of its length, the two roads' own edges are not walked, the heuristic is the straight distance to the goal). Returns the cost and the
    /// points between them.
    /// </summary>
    (float Cost, List<Vector2> Points)? Search(Spot entry, Spot exit)
    {
        var er = _roads[entry.Road];
        var xr = _roads[exit.Road];

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
            open.Enqueue(junction, c + Vector2.Distance(_junctions[junction], exit.Point));
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
            if (j == xr.To && g + (1 - exit.T) * xr.Length < best)
                (best, bestJunction) = (g + (1 - exit.T) * xr.Length, j);

            foreach (int index in _edges[j])
            {
                if (index == entry.Road || index == exit.Road) continue;

                var road = _roads[index];
                int other = road.From == j ? road.To : road.From;
                float c = g + road.Length;
                if (cost.TryGetValue(other, out float old) && old <= c) continue;

                cost[other] = c;
                back[other] = index;
                open.Enqueue(other, c + Vector2.Distance(_junctions[other], exit.Point));
            }
        }

        if (best == float.MaxValue) return null;
        return (best, RoutePoints(entry, exit, back, bestJunction));
    }

    /// <summary>
    /// The points of the route <see cref="Search"/> found: along the entry road to its junction, the chain of roads <paramref name="back"/> holds
    /// to <paramref name="bestJunction"/>, then along the exit road (along the one road when <paramref name="bestJunction"/> is -1).
    /// </summary>
    List<Vector2> RoutePoints(Spot entry, Spot exit, Dictionary<int, int> back, int bestJunction)
    {
        var er = _roads[entry.Road];
        var xr = _roads[exit.Road];
        var points = new List<Vector2>();

        if (bestJunction < 0)
        {
            AddAlong(points, er, entry.T, exit.T);
            return points;
        }

        // Back from the junction the exit road was reached at, to a seed junction.
        var chain = new List<(int Road, int To)>();
        for (int j = bestJunction; back.TryGetValue(j, out int via) && via >= 0; j = _roads[via].From == j ? _roads[via].To : _roads[via].From)
            chain.Add((via, j));

        chain.Reverse();
        int first = chain.Count > 0
            ? (_roads[chain[0].Road].From == chain[0].To ? _roads[chain[0].Road].To : _roads[chain[0].Road].From) : bestJunction;
        AddAlong(points, er, entry.T, first == er.From ? 0 : 1);

        foreach (var (index, end) in chain)
        {
            var road = _roads[index];
            AddAlong(points, road, road.To == end ? 0 : 1, road.To == end ? 1 : 0);
        }

        AddAlong(points, xr, bestJunction == xr.From ? 0 : 1, exit.T);
        return points;
    }

    /// <summary>
    /// The road's points strictly between the shares <paramref name="t0"/> and <paramref name="t1"/> of it, in that direction, then its point at
    /// <paramref name="t1"/>.
    /// </summary>
    static void AddAlong(List<Vector2> points, Road road, float t0, float t1)
    {
        if (t1 >= t0)
        {
            for (int i = 0; i < road.Points.Length; i++)
                if (road.Along[i] > t0 && road.Along[i] < t1) points.Add(road.Points[i]);
        }
        else
        {
            for (int i = road.Points.Length - 1; i >= 0; i--)
                if (road.Along[i] < t0 && road.Along[i] > t1) points.Add(road.Points[i]);
        }

        points.Add(PointAt(road, t1));
    }

    /// <summary>
    /// The point <paramref name="t"/> of the way along <paramref name="road"/>.
    /// </summary>
    static Vector2 PointAt(Road road, float t)
    {
        for (int i = 1; i < road.Points.Length; i++)
        {
            if (road.Along[i] >= t)
            {
                float span = road.Along[i] - road.Along[i - 1];
                return Vector2.Lerp(road.Points[i - 1], road.Points[i], span > 0 ? (t - road.Along[i - 1]) / span : 0);
            }
        }

        return road.Points[^1];
    }

    /// <summary>
    /// The number of junctions: the road ends, numbered from 0 in the order the roads first name them (roads naming one ROAD <c>start</c> or
    /// <c>end</c> number meet at one junction; the junction's own number is not that record number).
    /// </summary>
    public int JunctionCount => _junctions.Count;

    /// <summary>
    /// Where junction <paramref name="junction"/> lies (X/Z): the end of the first road that named it.
    /// </summary>
    public Vector2 JunctionAt(int junction) => _junctions[junction];

    /// <summary>
    /// The roads (indices below <see cref="Count"/>) that end at junction <paramref name="junction"/>, in road order; a road from a junction to
    /// itself is listed once.
    /// </summary>
    public ReadOnlySpan<int> RoadsAt(int junction) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_edges[junction]);

    /// <summary>
    /// Road <paramref name="road"/>: its ROAD record's id, its two junctions and its <c>length</c>, the cost a trip pays for it.
    /// </summary>
    public RoadInfo RoadAt(int road)
    {
        var r = _roads[road];
        return new RoadInfo(r.Id, r.From, r.To, r.Length);
    }

    /// <summary>
    /// Road <paramref name="road"/>'s points (X/Z), from its first junction to its second.
    /// </summary>
    public ReadOnlySpan<Vector2> PointsOf(int road) => _roads[road].Points;

    /// <summary>
    /// The point of any road nearest to <paramref name="at"/> within <paramref name="within"/> (X/Z), with its road, its share of the way along it
    /// from its first junction (0..1) and its distance; null when no road comes that close, or for a point or distance that is not finite or a
    /// distance below 0. It looks at the grid cells the distance covers that hold roads, so a large one costs more, up to the whole network.
    /// </summary>
    public RoadPoint? NearestRoad(Vector2 at, float within)
    {
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(within) || within < 0) return null;

        RoadPoint? best = null;
        var (x0, x1) = Cells(at.X, within, _lowX, _highX);
        var (z0, z1) = Cells(at.Y, within, _lowZ, _highZ);
        for (int cx = x0; cx <= x1; cx++)
        {
            for (int cz = z0; cz <= z1; cz++)
            {
                if (!_grid.TryGetValue((cx, cz), out var list)) continue;

                foreach (var (index, s) in list)
                {
                    var (point, u, d) = Closest(_roads[index], s, at);
                    if (d > within || best is { } b && (d > b.Distance || d == b.Distance && index >= b.Road)) continue;

                    var road = _roads[index];
                    best = new RoadPoint(index, road.Along[s] + (road.Along[s + 1] - road.Along[s]) * u, point, d);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Fills <paramref name="into"/> (cleared first) with the roads that pass within <paramref name="radius"/> of <paramref name="at"/> (X/Z), each
    /// once, in road order: the roads a place of that radius sits on. Nothing for a point or radius that is not finite or a radius below 0. It looks
    /// at the grid cells the radius covers that hold roads, as <see cref="NearestRoad"/>.
    /// </summary>
    public void RoadsNear(Vector2 at, float radius, List<int> into)
    {
        into.Clear();
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(radius) || radius < 0) return;

        var (x0, x1) = Cells(at.X, radius, _lowX, _highX);
        var (z0, z1) = Cells(at.Y, radius, _lowZ, _highZ);
        for (int cx = x0; cx <= x1; cx++)
        {
            for (int cz = z0; cz <= z1; cz++)
            {
                if (!_grid.TryGetValue((cx, cz), out var list)) continue;

                foreach (var (index, s) in list)
                {
                    if (!into.Contains(index) && Closest(_roads[index], s, at).Distance <= radius) into.Add(index);
                }
            }
        }

        into.Sort();
    }

    /// <summary>
    /// The grid cells from <paramref name="at"/> - <paramref name="reach"/> to <paramref name="at"/> + <paramref name="reach"/> on one axis, kept
    /// within the cells roads cross (<paramref name="low"/> to <paramref name="high"/>); an empty range (low above high) when they miss.
    /// </summary>
    static (int Low, int High) Cells(float at, float reach, int low, int high)
    {
        float from = MathF.Floor((at - reach) / Cell), to = MathF.Floor((at + reach) / Cell);
        if (low > high || to < low || from > high) return (1, 0);

        return ((int)MathF.Max(from, low), (int)MathF.Min(to, high));
    }

    /// <summary>
    /// The point of segment <paramref name="segment"/> of <paramref name="road"/> nearest to <paramref name="at"/>, its share of the segment and its
    /// distance.
    /// </summary>
    static (Vector2 Point, float Share, float Distance) Closest(Road road, int segment, Vector2 at)
    {
        var (a, b) = (road.Points[segment], road.Points[segment + 1]);
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float u = len2 > 0 ? Math.Clamp(Vector2.Dot(at - a, ab) / len2, 0, 1) : 0;
        var q = a + ab * u;
        return (q, u, Vector2.Distance(at, q));
    }
}

/// <summary>
/// A road of the <see cref="RoadNetwork"/> (docs/modding.md "Roads"): its ROAD record's id, the junctions it joins
/// (<see cref="RoadNetwork.JunctionAt"/>) and its <c>length</c>, the cost a trip pays for it.
/// </summary>
public readonly record struct RoadInfo(string Id, int From, int To, float Length);

/// <summary>
/// A point on a road of the <see cref="RoadNetwork"/> (<see cref="RoadNetwork.NearestRoad"/>): the road, its share of the way along it from its first
/// junction (0..1), the point (X/Z) and its distance from the point asked about.
/// </summary>
public readonly record struct RoadPoint(int Road, float Along, Vector2 Point, float Distance);
