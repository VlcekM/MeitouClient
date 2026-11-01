using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Threading;

namespace Meitou.Navigation;

/// <summary>
/// Long trips across zones whose navmesh is not loaded (a move order on the map): A* over a coarse grid of the whole world built from the terrain
/// alone. A step between two cells is barred where the terrain between them is steeper than <see cref="MaxSlopeDegrees"/>; cells under the water
/// cost the agent's water cost for their depth. A step that crosses a building's footprint (the level data's walls and buildings, and the player's: <see cref="SetPlayerObstacles"/>) costs
/// <see cref="BuildingCost"/> times as much, so routes go round towns and walls where open ground allows and still through them where not (a coarse cell
/// is wider than a gate). Foliage is not in it: the navmesh path near the walker refines the route as its zones load (<see cref="NavmeshWalkability"/>).
/// Engine choice, not the original's (which routes over its pre-built navmesh tiles, docs/game/pathfinding.md).
/// Thread-safety: the grid is built once on first use; queries are serialised (one search buffer).
/// </summary>
public sealed class CoarseRoute
{
    /// <summary>Cell edge in units: 24 cells a zone, 1536 a world side (engine choice: about 2.4 M cells, a cross-map search in well under a second).</summary>
    public const int Cell = 192;
    public const int Side = WorldLayout.WorldSize / Cell;
    /// <summary>The terrain's walkable slope (the navmesh builder's terrain limit), tested between samples <see cref="SlopeStep"/> apart along every step.</summary>
    public const float MaxSlopeDegrees = 40;
    /// <summary>Spacing (units) of the slope samples: two heightmap samples.</summary>
    public const float SlopeStep = 2 * WorldLayout.SampleSpacing;
    /// <summary>The heuristic's weight (engine choice: slightly greedy, so open-ground trips expand few cells; routes are at most this much longer than the best).</summary>
    public const float HeuristicWeight = 1.2f;
    /// <summary>Cells expanded at most per search.</summary>
    public const int MaxExpanded = 3_000_000;
    /// <summary>The cost factor of a step across a building's footprint (engine choice: a 5.5k-unit wall is walked round, not crossed; finite, so a walled town is still reached).</summary>
    public const float BuildingCost = 40;

    // Direction k: (dx, dz). Bits 0..7 of a cell: the step in direction k is barred. Bit 8: under the water; bits 9..10: then its wet state 1..3 (Speed.WetStateOfDepth).
    static readonly (int Dx, int Dz)[] Directions = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];
    const ushort Water = 1 << 8;
    const int WetShift = 9;

    readonly Func<float, float, float> terrain;
    Func<Func<float, float, float>>? slopeTerrain;
    Func<IReadOnlyList<Vector2[]>>? obstacles;
    readonly Lazy<ushort[]> cells;
    // Bit k of a cell: the step in direction k crosses a building's footprint (filled with the grid). The player's buildings have their own layer,
    // replaced whole when they change (empty: none).
    byte[] crossings = [];
    volatile byte[] playerCrossings = [];

    /// <summary>Sets the player's buildings (footprints as for the constructor's obstacles), replacing the last set; the next search sees them.</summary>
    public void SetPlayerObstacles(IReadOnlyList<Vector2[]> hulls) => playerCrossings = hulls.Count == 0 ? [] : Crossings(hulls);
    readonly object searchLock = new();
    float[]? g;
    int[]? parent, stamp;
    int generation;

    /// <param name="terrainHeight">Heights of the route's points; must be safe from any thread.</param>
    /// <param name="slopeTerrain">
    /// Makes the height function the grid is built from (once, then dropped): it should be exact to about <see cref="SlopeStep"/>, or cliffs smooth into
    /// ramps the walker cannot take. Null: <paramref name="terrainHeight"/>.
    /// </param>
    /// <param name="obstacles">Makes the building footprints (world X/Z polygons, convex) the grid is built with (once, then dropped). Null: none.</param>
    public CoarseRoute(Func<float, float, float> terrainHeight, Func<Func<float, float, float>>? slopeTerrain = null, Func<IReadOnlyList<Vector2[]>>? obstacles = null)
    {
        terrain = terrainHeight;
        this.slopeTerrain = slopeTerrain;
        this.obstacles = obstacles;
        cells = new Lazy<ushort[]>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Builds the grid now (it is otherwise built by the first <see cref="Find"/>, which then holds every path thread asking meanwhile: ~3 s).</summary>
    public void Warm() => _ = cells.Value;

    static float Origin => -WorldLayout.HalfWorldSize;
    static int CellOf(float v) => Math.Clamp((int)MathF.Floor((v - Origin) / Cell), 0, Side - 1);
    static Vector2 Centre(int x, int z) => new(Origin + (x + 0.5f) * Cell, Origin + (z + 0.5f) * Cell);

    ushort[] Build()
    {
        var height = slopeTerrain?.Invoke() ?? terrain;
        slopeTerrain = null;
        var result = new ushort[Side * Side];
        float rise = MathF.Tan(MaxSlopeDegrees * MathF.PI / 180) * SlopeStep;
        Parallel.For(0, Side, z =>
        {
            for (int x = 0; x < Side; x++)
            {
                var c = Centre(x, z);
                float depth = WorldWater.Height - height(c.X, c.Y);
                ushort flags = depth > 0 ? (ushort)(Water | (Speed.WetStateOfDepth(depth) << WetShift)) : (ushort)0;
                // Directions 0..3 are tested here; 4..7 are the same steps seen from the other end.
                for (int k = 0; k < 4; k++)
                {
                    var (dx, dz) = Directions[k];
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= Side || nz >= Side) { flags |= (ushort)(1 << k); continue; }
                    if (TooSteep(height, c, Centre(nx, nz), rise)) flags |= (ushort)(1 << k);
                }
                result[z * Side + x] = flags;
            }
        });
        // The steps 4..7 from the cell at the other end.
        for (int z = 0; z < Side; z++)
            for (int x = 0; x < Side; x++)
                for (int k = 0; k < 4; k++)
                {
                    var (dx, dz) = Directions[k];
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= Side || nz >= Side) continue;
                    if ((result[z * Side + x] & (1 << k)) != 0) result[nz * Side + nx] |= (ushort)(1 << (k + 4));
                }
        for (int z = 0; z < Side; z++)
            for (int x = 0; x < Side; x++) result[z * Side + x] |= Border(x, z);
        crossings = Crossings(obstacles?.Invoke() ?? []);
        obstacles = null;
        return result;
    }

    /// <summary>For every step, whether the segment between the two cell centres meets one of the footprints.</summary>
    static byte[] Crossings(IReadOnlyList<Vector2[]> hulls)
    {
        var result = new byte[Side * Side];
        foreach (var hull in hulls)
        {
            if (hull.Length < 3) continue;
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var p in hull) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            // Every step with an end within a cell of the hull's box (a step reaches one cell out, the (-1, 1) one from the column to the right).
            int x0 = Math.Max(0, CellOf(min.X) - 1), x1 = Math.Min(Side - 1, CellOf(max.X) + 1), z0 = Math.Max(0, CellOf(min.Y) - 1), z1 = CellOf(max.Y);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    for (int k = 0; k < 4; k++)
                    {
                        var (dx, dz) = Directions[k];
                        int nx = x + dx, nz = z + dz;
                        if (nx < 0 || nz < 0 || nx >= Side || nz >= Side || !Meets(Centre(x, z), Centre(nx, nz), hull)) continue;
                        result[z * Side + x] |= (byte)(1 << k);
                        result[nz * Side + nx] |= (byte)(1 << (k + 4));
                    }
        }
        return result;
    }

    // The level data's crossings with the player's layer on top (a copy only while the player has buildings), cached per pair of arrays.
    sealed record Layers(byte[] Static, byte[] Player, byte[] Both);
    volatile Layers? combined;
    byte[] Combined(byte[] level, byte[] player)
    {
        if (player.Length == 0) return level;
        if (combined is { } c && ReferenceEquals(c.Static, level) && ReferenceEquals(c.Player, player)) return c.Both;
        var both = level.Length == 0 ? new byte[Side * Side] : (byte[])level.Clone();
        for (int i = 0; i < both.Length; i++) both[i] |= player[i];
        combined = new Layers(level, player, both);
        return both;
    }

    /// <summary>Whether the segment a-b meets the convex polygon (either winding).</summary>
    static bool Meets(Vector2 a, Vector2 b, Vector2[] hull)
    {
        // Clip the segment's parameter range against each edge's half-plane.
        float area = 0;
        // Relative to a vertex: in world units the products are ~1e8 and the sum cancels in floats.
        for (int i = 1; i + 1 < hull.Length; i++) area += Cross(hull[i] - hull[0], hull[i + 1] - hull[0]);
        float sign = area >= 0 ? 1 : -1;
        float t0 = 0, t1 = 1;
        var d = b - a;
        for (int i = 0; i < hull.Length; i++)
        {
            var p = hull[i];
            var e = hull[(i + 1) % hull.Length] - p;
            // Inside: sign * cross(e, x - p) >= 0.
            float num = sign * Cross(e, a - p), den = sign * Cross(e, d);
            if (MathF.Abs(den) < 1e-9f) { if (num < 0) return false; continue; }
            float t = -num / den;
            if (den > 0) t0 = MathF.Max(t0, t); else t1 = MathF.Min(t1, t);
            if (t0 > t1) return false;
        }
        return true;
    }

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    static bool TooSteep(Func<float, float, float> terrain, Vector2 a, Vector2 b, float rise)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / SlopeStep));
        float previous = MathF.Max(terrain(a.X, a.Y), WorldWater.Height);
        for (int i = 1; i <= steps; i++)
        {
            var p = Vector2.Lerp(a, b, i / (float)steps);
            float h = MathF.Max(terrain(p.X, p.Y), WorldWater.Height);   // the water surface is flat: swimming has no slope
            if (MathF.Abs(h - previous) > rise * (Vector2.Distance(a, b) / steps) / SlopeStep) return true;
            previous = h;
        }
        return false;
    }

    /// <summary>
    /// A route from <paramref name="from"/> to <paramref name="to"/>: the points where it turns (X/Z, Y the terrain or the water surface), both ends included;
    /// empty when the goal cannot be reached over the terrain (or the search ran out).
    /// </summary>
    public List<Vector3> Find(Vector3 from, Vector3 to, WaterCost water)
    {
        // A goal the slope test cuts off (a narrow ramp the coarse grid smooths into a cliff) is still routed to, over every slope: the navmesh near the
        // walker finds the real way up.
        var route = Find(from, to, water, slopes: true);
        return route.Count > 0 ? route : Find(from, to, water, slopes: false);
    }

    List<Vector3> Find(Vector3 from, Vector3 to, WaterCost water, bool slopes)
    {
        var map = cells.Value;
        var crossing = Combined(crossings, playerCrossings);
        int sx = CellOf(from.X), sz = CellOf(from.Z), gx = CellOf(to.X), gz = CellOf(to.Z);
        int start = sz * Side + sx, goal = gz * Side + gx;
        // By wet state 1..3, never below dry ground.
        Span<float> waterCost = [1, MathF.Max(1, water[1]), MathF.Max(1, water[2]), MathF.Max(1, water[3])];
        List<int> cellsOnRoute;
        lock (searchLock)
        {
            g ??= new float[Side * Side];
            parent ??= new int[Side * Side];
            stamp ??= new int[Side * Side];
            if (++generation == int.MaxValue) { Array.Clear(stamp); generation = 1; }
            var open = new PriorityQueue<int, float>();
            g[start] = 0; parent[start] = -1; stamp[start] = generation;
            open.Enqueue(start, 0);
            int expanded = 0;
            bool found = false;
            while (open.TryDequeue(out int cell, out float f))
            {
                if (cell == goal) { found = true; break; }
                int cx = cell % Side, cz = cell / Side;
                float gc = g[cell];
                if (f > gc + HeuristicWeight * Heuristic(cx, cz, gx, gz) + 1e-3f) continue;   // a stale entry
                if (++expanded > MaxExpanded) break;
                SafePoint.Every(expanded);
                ushort flags = map[cell];
                ushort barred = slopes ? flags : Border(cx, cz);
                for (int k = 0; k < 8; k++)
                {
                    if ((barred & (1 << k)) != 0) continue;
                    var (dx, dz) = Directions[k];
                    int next = (cz + dz) * Side + cx + dx;
                    float step = (k & 1) == 0 ? 1 : MathF.Sqrt(2);
                    // Half the step in each cell; water cells cost the water factor.
                    float cost = step * 0.5f * (waterCost[(flags >> WetShift) & 3] + waterCost[(map[next] >> WetShift) & 3]);
                    if ((crossing[cell] & (1 << k)) != 0) cost *= BuildingCost;
                    float gn = gc + cost;
                    if (stamp[next] == generation && gn >= g[next]) continue;
                    stamp[next] = generation; g[next] = gn; parent[next] = cell;
                    open.Enqueue(next, gn + HeuristicWeight * Heuristic(cx + dx, cz + dz, gx, gz));
                }
            }
            if (!found) return [];
            cellsOnRoute = [];
            for (int c = goal; c >= 0; c = parent[c]) cellsOnRoute.Add(c);
        }
        cellsOnRoute.Reverse();

        // Keep only the cells where the route turns, then put the real ends on it.
        var route = new List<Vector3> { from };
        for (int i = 1; i + 1 < cellsOnRoute.Count; i++)
        {
            int a = cellsOnRoute[i - 1], b = cellsOnRoute[i], c = cellsOnRoute[i + 1];
            if (b - a == c - b) continue;
            var centre = Centre(b % Side, b / Side);
            route.Add(new Vector3(centre.X, MathF.Max(terrain(centre.X, centre.Y), WorldWater.Height), centre.Y));
        }
        route.Add(to);
        return Smooth(route, slopes ? map : null, crossing);
    }

    /// <summary>The steps out of the world from a cell on its edge.</summary>
    static ushort Border(int x, int z) =>
        (ushort)((z == 0 ? 0b1110_0000 : 0) | (z == Side - 1 ? 0b0000_1110 : 0) | (x == 0 ? 0b0011_1000 : 0) | (x == Side - 1 ? 0b1000_0011 : 0));

    static float Heuristic(int x, int z, int gx, int gz)
    {
        int dx = Math.Abs(x - gx), dz = Math.Abs(z - gz);
        return Math.Max(dx, dz) + (MathF.Sqrt(2) - 1) * Math.Min(dx, dz);
    }

    /// <summary>Drops turning points the walker can skip: a straight line between the neighbours that crosses only open, dry cells and no building.</summary>
    static List<Vector3> Smooth(List<Vector3> route, ushort[]? map, byte[] crossing)
    {
        if (route.Count <= 2 || map is null) return route;
        var result = new List<Vector3> { route[0] };
        int anchor = 0;
        for (int i = 2; i < route.Count; i++)
        {
            if (Clear(route[anchor], route[i], map, crossing)) continue;
            result.Add(route[i - 1]);
            anchor = i - 1;
        }
        result.Add(route[^1]);
        return result;
    }

    /// <summary>Whether the straight line steps only between neighbouring dry cells without a barred step or a building crossing (walked cell by cell).</summary>
    static bool Clear(Vector3 a, Vector3 b, ushort[] map, byte[] crossing)
    {
        int x = CellOf(a.X), z = CellOf(a.Z), tx = CellOf(b.X), tz = CellOf(b.Z);
        float length = Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
        int samples = Math.Max(1, (int)MathF.Ceiling(length / (Cell * 0.5f)));
        for (int i = 1; i <= samples; i++)
        {
            var p = Vector3.Lerp(a, b, i / (float)samples);
            int nx = CellOf(p.X), nz = CellOf(p.Z);
            if (nx == x && nz == z) continue;
            int k = Array.IndexOf(Directions, (Math.Sign(nx - x), Math.Sign(nz - z)));
            if (Math.Abs(nx - x) > 1 || Math.Abs(nz - z) > 1 || k < 0) return false;
            if ((map[z * Side + x] & (1 << k)) != 0 || (crossing[z * Side + x] & (1 << k)) != 0 || (map[nz * Side + nx] & Water) != 0) return false;
            (x, z) = (nx, nz);
        }
        return x == tx && z == tz;
    }
}
