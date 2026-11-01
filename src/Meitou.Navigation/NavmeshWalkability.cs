using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Navigation;

/// <summary>
/// <see cref="IWalkability"/> over the loaded zone meshes. Where a zone's mesh is not there yet (it is built on a worker thread,
/// <see cref="NavMeshService"/>) the open-ground stand-in answers, so the simulation never waits. The world is swapped as a whole
/// (immutable snapshots), so every method is safe from any thread.
/// </summary>
public sealed partial class NavmeshWalkability : IWalkability
{
    readonly Func<float, float, float> ground;
    readonly IWalkability fallback;
    volatile NavWorld world = NavWorld.Empty;

    /// <param name="routeTerrain">Makes the finer height function the long-range grid is built from (<see cref="CoarseRoute"/>); null: <paramref name="terrainHeight"/>.</param>
    /// <param name="routeObstacles">Makes the building footprints the long-range grid avoids; null: none.</param>
    public NavmeshWalkability(Func<float, float, float> terrainHeight, IWalkability? fallback = null, Func<Func<float, float, float>>? routeTerrain = null,
        Func<IReadOnlyList<Vector2[]>>? routeObstacles = null)
    {
        ground = terrainHeight;
        this.fallback = fallback ?? new OpenGroundWalkability(terrainHeight);
        coarse = new Lazy<CoarseRoute>(() => new CoarseRoute(terrainHeight, routeTerrain, routeObstacles));
    }

    /// <summary>The doors' run-time state; shared with every query made through this object.</summary>
    public NavDoors Doors { get; } = new();

    /// <summary>Where the buildings' doors are (<see cref="NavDoorGeometry"/>); null until a <see cref="NavSystem"/> gives it the data.</summary>
    public NavDoorGeometry? DoorGeometry { get; internal set; }

    public NavWorld World => world;

    /// <summary>Replaces the loaded meshes; only <see cref="NavMeshService"/> calls it, under its publish lock.</summary>
    internal void SetWorld(NavWorld value) => world = value;

    /// <summary>Keeps the pruned region a point stands on (<see cref="NavMeshService.KeepAt"/>); true when the world changed. Set by <see cref="NavSystem"/>.</summary>
    internal Func<Vector3, bool>? KeepStanding { get; set; }

    /// <summary>
    /// How far (in height) a walkable collision surface may be from the mesh for its height to be used instead, and the terrain's where no collision
    /// surface is near (engine choices). The mesh is a simplified surface: about 0.5 off on the flat, but a polygon is planar only between its corners,
    /// so over stairs, ramps and steep slopes it can be 15 off (Observed: the Hub's gate stairs and the ramp north of it). The terrain and the walkable
    /// collision (<see cref="NavSurfaces"/>) are exact.
    /// </summary>
    public const float SnapRange = 16, TerrainSnapRange = 30;

    /// <summary>How far (units) above the water's height a polygon over flooded ground still counts as the water surface (engine choice: Observed 100..104).</summary>
    public const float WaterSurfaceBand = 6;

    /// <summary>
    /// How far above a character's height a surface may be and still be the one it stands on (engine choice): above the 5-unit climb, below a storey,
    /// so a walkway or upper floor overhead is not taken for the ground.
    /// </summary>
    public const float StandRise = 12;

    /// <summary>
    /// How far below a character's height a surface may be and still be stepped onto (engine choice, as <see cref="StandRise"/>): the ground showing through a hole
    /// furniture cuts in a building's floor mesh is not (Observed: 22..28 under the Hub's Storm House floor, where walkers fell through).
    /// </summary>
    public const float StandDrop = 12;

    /// <summary>How far (units) off the mesh a walker may stand on the polygon beside it (engine choice: a path hugs corners and edges exactly, and a step can graze past them).</summary>
    public const float EdgeTolerance = 2;

    [ThreadStatic] static List<float>? scratch;

    /// <summary>The terrain height: the loaded zone's own heightmap samples, else the terrain function (whose grid may be coarse far from the start).</summary>
    float Ground(NavWorld w, float x, float z) => w.TryTerrain(x, z, out float h) ? h : ground(x, z);

    /// <summary>
    /// The height a character stands at: the highest walkable surface there, each mesh polygon put on the real surface it stands for (<see cref="Snap"/>);
    /// the terrain's off the mesh. Movement uses the overload with the character's height, which picks the floor under it where floors are stacked.
    /// </summary>
    public float GroundHeight(float x, float z) => TryStand(world, x, z, float.PositiveInfinity, out float y) ? y : Ground(world, x, z);

    /// <summary>As <see cref="GroundHeight(float, float)"/>, for a character at height <paramref name="nearY"/>: the highest surface at most <see cref="StandRise"/> above it.</summary>
    public float GroundHeight(float x, float z, float nearY) => TryStand(world, x, z, nearY, out float y) ? y : Ground(world, x, z);

    public float TerrainHeight(float x, float z) => Ground(world, x, z);

    public bool IsWalkable(float x, float z)
    {
        var w = world;
        if (!w.Contains(WorldLayout.ZoneOf(x, z))) return fallback.IsWalkable(x, z);
        return w.TryGroundHeight(x, z, out _);
    }

    /// <summary>Whether there is a surface at (x, z) no more than <see cref="StandRise"/> above <paramref name="nearY"/> (a wall top beside the walker is not) nor
    /// <see cref="StandDrop"/> below it (the ground under a floor is not).</summary>
    public bool IsWalkable(float x, float z, float nearY)
    {
        var w = world;
        if (!w.Contains(WorldLayout.ZoneOf(x, z))) return fallback.IsWalkable(x, z);
        return TryStand(w, x, z, nearY, out _, out bool reachable) && reachable;
    }

    public bool TryStand(float x, float z, float nearY, out float y)
    {
        var w = world;
        if (!TryStand(w, x, z, nearY, out y, out bool reachable)) { y = Ground(w, x, z); reachable = false; }
        return w.Contains(WorldLayout.ZoneOf(x, z)) ? reachable : fallback.IsWalkable(x, z);
    }

    bool TryStand(NavWorld w, float x, float z, float nearY, out float y) => TryStand(w, x, z, nearY, out y, out _);

    /// <summary>
    /// <see cref="IWalkability.FloorNear"/>, kept for the loaded world it was found on: the AI asks it of the same spots at every think, and a spot with no floor near
    /// tries every point of the search.
    /// </summary>
    public Vector3? FloorNear(float x, float z, float y, float slack)
    {
        var cache = floors;
        if (!ReferenceEquals(cache.World, world)) floors = cache = new FloorCache(world);
        if (cache.Found.TryGetValue((x, z, y, slack), out var known)) return known;
        // Counted apart: ConcurrentDictionary.Count takes every lock, and misses come all at once after a swap, from every partition.
        if (Interlocked.Increment(ref cache.Added) > FloorCacheLimit) { cache.Found.Clear(); Interlocked.Exchange(ref cache.Added, 0); }
        return cache.Found[(x, z, y, slack)] = IWalkability.ScanFloor(this, x, z, y, slack);
    }

    /// <summary>How many <see cref="FloorNear"/> answers are kept at most (engine choice: well above a town's spots).</summary>
    const int FloorCacheLimit = 16384;

    sealed class FloorCache(NavWorld world)
    {
        public readonly NavWorld World = world;
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(float X, float Z, float Y, float Slack), Vector3?> Found = new();
        /// <summary>Answers added since <see cref="Found"/> was last cleared (a spot asked twice at once counts twice: the limit is loose).</summary>
        public int Added;
    }

    volatile FloorCache floors = new(NavWorld.Empty);

    /// <summary>
    /// The real surface of the highest mesh layer at (x, z) within <see cref="StandRise"/> above <paramref name="nearY"/> and <see cref="StandDrop"/> below it
    /// (by the mesh's height or the real one: on steep stairs the mesh is up to 20 off, and judged by the real height alone the walker's own layer could fall
    /// out of reach and drop it to a floor far below, Observed at Fallen Tower); with none there, the one of a polygon within <see cref="EdgeTolerance"/>
    /// (a walker grazing the edge of a hole in a floor); else the lowest. <paramref name="reachable"/>: a layer was within reach. False off the mesh.
    /// </summary>
    bool TryStand(NavWorld w, float x, float z, float nearY, out float y, out bool reachable)
    {
        var heights = scratch ??= [];
        heights.Clear();
        w.HeightsAt(x, z, heights, float.IsFinite(nearY) ? nearY : 0, float.IsFinite(nearY) ? EdgeTolerance : 0);
        y = float.MinValue;
        reachable = false;
        if (heights.Count == 0) return false;
        bool near = float.IsFinite(nearY);
        float lowest = float.MaxValue, layer = float.MinValue;
        foreach (float h in heights)
        {
            float s = Snap(w, x, z, h, out bool beside);
            // Where the mesh is too far off for its surface to be found (a ramp the polygon cuts under), the surface at the walker's own height, when that
            // is nearer the mesh than what was found (Observed: the terrain 30 below, inside the ramp, at the Great Fortress) or within the terrain snap's reach
            // of it: a polygon spanning a flat top and its slope sags up to ~25 under the top and lies nearer a face inside the rock (Observed: the rock by the
            // Hub's gate, where the original's polygon is flat on the top).
            if (near && MathF.Abs(s - nearY) > StandRise && w.TrySurfaceNear(x, z, nearY, StandRise, out float own)
                && MathF.Abs(own - h) < MathF.Max(MathF.Abs(s - h), TerrainSnapRange)) s = own;
            // A walker on the ground along the foot of a raised top whose edge the snap took (nothing is over the spot): the ground, when the mesh is within the
            // terrain snap's reach of it (Observed: a top at 560 over ground at 537 west of Black Desert City, a polygon at 550 by its foot, stopping walkers there).
            else if (near && beside && MathF.Abs(s - nearY) > StandRise && Ground(w, x, z) is var t && MathF.Abs(t - nearY) <= StandRise
                && MathF.Abs(t - h) <= TerrainSnapRange) s = t;
            lowest = MathF.Min(lowest, s);
            float reach = MathF.Min(h, s);
            if (reach > nearY + StandRise || reach <= layer || near && MathF.Max(h, s) < nearY - StandDrop) continue;
            layer = reach;
            y = s;
            reachable = true;
        }
        if (!reachable && near && w.TryEdgeHeight(x, z, nearY, EdgeTolerance, out float edge))
        {
            float s = Snap(w, x, z, edge, out _);
            if (MathF.Min(edge, s) <= nearY + StandRise && MathF.Max(edge, s) >= nearY - StandDrop) (y, reachable) = (s, true);
        }
        if (!reachable) y = lowest;
        return true;
    }

    /// <summary>
    /// The real surface a mesh height <paramref name="h"/> stands for: the walkable collision surface nearest it within <see cref="SnapRange"/> unless the
    /// terrain is nearer; else the terrain within <see cref="TerrainSnapRange"/>; else the mesh's own height. <paramref name="beside"/>: the surface taken is
    /// only beside the spot (its edge within <see cref="NavSurfaces.EdgeReach"/>).
    /// </summary>
    float Snap(NavWorld w, float x, float z, float h, out bool beside)
    {
        beside = false;
        float t = Ground(w, x, z);
        // Over ground under the water a polygon near the water's height is the water surface (the builder's water area): kept, not put on the seabed or a
        // sunken floor, so all of the water is at one height.
        bool waterSurface = t < WorldWater.Height && h < WorldWater.Height + WaterSurfaceBand;
        if (waterSurface) return h;
        float terrain = MathF.Abs(t - h);
        if (w.TrySurfaceNear(x, z, h, MathF.Min(SnapRange, terrain), out float s, out beside)) return s;
        return terrain <= TerrainSnapRange ? t : h;
    }

    public PathResult FindPath(Vector3 from, Vector3 to) => FindPath(from, to, NavAgent.Human);

    /// <summary>The long-range router over the terrain, for goals in zones whose navmesh is not loaded (its grid is built on first use).</summary>
    public CoarseRoute Coarse => coarse.Value;
    readonly Lazy<CoarseRoute> coarse;

    /// <summary>Spacing (units) of the points along a long route tried as the end of its navmesh part (engine choice).</summary>
    public const float HandoverStep = 150;
    /// <summary>How far (units) the end of the navmesh part keeps from a zone that is not loaded (engine choice).</summary>
    public const float HandoverMargin = 120;

    /// <summary>
    /// A path for a given agent (footprint radius, water cost, closed doors). With both ends on loaded zones, the navmesh path; otherwise a long route
    /// (<see cref="CoarseRoute"/>) whose first part, as far as the loaded zones reach, is a navmesh path, and the rest provisional
    /// (<see cref="PathResult.Exact"/>). Where the terrain gives no route either, the open-ground stand-in answers.
    /// </summary>
    public PathResult FindPath(Vector3 from, Vector3 to, NavAgent agent)
    {
        var w = world;
        bool fromLoaded = w.Contains(WorldLayout.ZoneOf(from.X, from.Z));
        if (!fromLoaded || !w.Contains(WorldLayout.ZoneOf(to.X, to.Z))) return LongRoute(w, from, to, agent, fromLoaded);
        // A goal just found unreachable (shut in, or a way in too narrow for the radius): a walker that can reach the nearest point found then is on the
        // same ground, which does not reach the goal either, so it walks there instead of running the exact and the nearest-point searches, each of
        // which explores all the ground it can reach.
        var key = UnreachableKey(w, to, agent);
        if (key is { } known && unreachable.TryGetValue(known, out var hit) && ReferenceEquals(hit.World, w)
            && System.Diagnostics.Stopwatch.GetElapsedTime(hit.At).TotalSeconds < UnreachableSeconds
            && new NavQuery(w, Doors).FindPath(from, hit.Nearest, agent, null, out _) is { Found: true } near)
            return Provisional(w, FromWalker(from, near), to);
        var exact = Exact(w, from, to, agent);
        if (exact.Found) return exact;
        // A walker on a region its zone's pruning dropped (its way to a seed leads through a zone not loaded): that region is kept, and the path asked again.
        if (!IsWalkable(from.X, from.Z, from.Y) && KeepStanding?.Invoke(from) == true)
        {
            w = world;
            exact = Exact(w, from, to, agent);
            if (exact.Found) return exact;
        }
        // Both ends loaded but in separate blocks of loaded zones (two groups of walkers far apart): a long route like any other through the zones
        // between, not a straight line from the nearest reachable point. Within one block the navmesh's answer stands (the terrain search is not run).
        if (!Joined(w, WorldLayout.ZoneOf(from.X, from.Z), WorldLayout.ZoneOf(to.X, to.Z))) return LongRoute(w, from, to, agent, fromLoaded);
        // Unreachable over the loaded zones (shut in, or the way round leads through zones not loaded): as near as can be got, then on to the goal
        // provisionally, which asks again a few times on the way (MovementSystem) before the walker gives up.
        var part = Toward(w, from, to, agent);
        if (!part.Found) return exact;
        if (Vector2.Distance(new(part.Points[^1].X, part.Points[^1].Z), new(to.X, to.Z)) <= NavQuery.GoalSnap) return part;
        // Also after a search cut short by its limit: the exact search would be cut short the same way, so the walker gets this same provisional route.
        if (key is { } found)
        {
            if (unreachable.Count >= UnreachableLimit) unreachable.Clear();
            unreachable[found] = (w, part.Points[^1], System.Diagnostics.Stopwatch.GetTimestamp());
        }
        return Provisional(w, part, to);
    }

    /// <summary>A navmesh part to the nearest reachable point, then on to the goal provisionally (asked again on the way, MovementSystem).</summary>
    PathResult Provisional(NavWorld w, PathResult part, Vector3 to)
    {
        List<Vector3> points = [.. part.Points, to];
        Ahead(w, points, part.Points.Count);
        return new PathResult(points) { Exact = part.Points.Count };
    }

    /// <summary>How long (s) a goal found unreachable is remembered (engine choice: a door may open, and a door has no version).</summary>
    public const double UnreachableSeconds = 10;
    const int UnreachableLimit = 4096;

    // Goals found unreachable over the loaded zones, by goal polygon and the agent's radius and door rule: the navmesh they were found on (any other
    // mesh, after a zone went live or unloaded, asks again), the nearest reachable point and when. Each was 300-600 ms of searching, asked again
    // and again by a town's residents walking to a goal they cannot reach (Observed, zone 14.23: a cabinet in a building, docs/game/pathfinding.md).
    readonly System.Collections.Concurrent.ConcurrentDictionary<(NavRef Goal, float Radius, bool DoorsClosed), (NavWorld World, Vector3 Nearest, long At)> unreachable = new();

    static (NavRef, float, bool)? UnreachableKey(NavWorld w, Vector3 to, NavAgent agent) =>
        w.TryFindPolygon(to, NavQuery.GoalSnap, out var goal, out _) ? (goal, agent.Radius, agent.DoorsClosed) : null;

    /// <summary>How far (units) along the provisional part of a route the zones are asked for (<see cref="RouteZones"/>), so the walker finds rocks,
    /// walls and cliffs the terrain grid does not know before it gets there (engine choice: about a zone).</summary>
    public const float Lookahead = 4000;

    readonly System.Collections.Concurrent.ConcurrentDictionary<ZoneCoordinate, long> routeZones = new();

    /// <summary>The zones without a mesh that a provisional route passed through within <see cref="Lookahead"/> of its navmesh part, in the last
    /// <paramref name="maxAge"/>: the host loads them, and every walker asks for its path again when they are live.</summary>
    public IEnumerable<ZoneCoordinate> RouteZones(TimeSpan maxAge)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var (zone, at) in routeZones)
            if (System.Diagnostics.Stopwatch.GetElapsedTime(at, now) <= maxAge) yield return zone;
            else routeZones.TryRemove(zone, out _);
    }

    void Ahead(NavWorld w, List<Vector3> points, int exact)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float left = Lookahead;
        for (int i = Math.Max(exact, 1); i < points.Count && left > 0; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            float length = Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
            for (float d = 0; d <= MathF.Min(length, left); d += HandoverStep)
            {
                var p = Vector3.Lerp(a, b, d / MathF.Max(length, 1e-3f));
                var zone = WorldLayout.ZoneOf(p.X, p.Z);
                if (!w.Contains(zone)) routeZones[zone] = now;
            }
            left -= length;
        }
    }

    /// <summary>The navmesh path to <paramref name="to"/>, or to the reachable point nearest it (<see cref="NavQuery.FindPathToward"/>), on the real ground.</summary>
    PathResult Toward(NavWorld w, Vector3 from, Vector3 to, NavAgent agent)
    {
        var path = new NavQuery(w, Doors).FindPathToward(from, to, agent);
        return path.Found ? FromWalker(from, path) : path;
    }

    [ThreadStatic] static HashSet<long>? failedRegions;

    PathResult Exact(NavWorld w, Vector3 from, Vector3 to, NavAgent agent)
    {
        var query = new NavQuery(w, Doors);
        var path = OnGround(query.FindPath(from, to, agent, null, out var start));
        // A walker off the mesh (pushed beside a stair) can be nearest to a polygon it cannot step onto: the corner of the stair over it, whose simplified
        // height snaps to the walker's own ground. Its path then begins with a wall, so the next nearest starts are tried, the first whose way the walker can
        // step along taken (docs/game/pathfinding.md "Own builder").
        if (start.Zone < 0 || IsOnMesh(w, from) || path.Found && Steppable(from, path.Points)) return path;
        var tried = new HashSet<NavRef> { start };
        // The polygons a failed search reached are one region with its start: a search from any of them fails too, so they are not tried.
        var failed = failedRegions ??= [];
        failed.Clear();
        NavQuery.AddReachedByFailed(failed);
        for (int k = 0; k < OffMeshStarts; k++)
        {
            var other = OnGround(query.FindPath(from, to, agent, at => tried.Contains(at) || failed.Contains(at.Key), out start));
            if (start.Zone < 0) break;
            tried.Add(start);
            NavQuery.AddReachedByFailed(failed);
            if (other.Found && Steppable(from, other.Points)) return other;
        }
        return path;

        PathResult OnGround(PathResult found) => found.Found ? FromWalker(from, found) : found;
    }

    /// <summary>
    /// A navmesh path for movement: its points put on the real ground (they are on the simplified surface), and the walker's own position first when the path starts
    /// elsewhere. Movement takes the first point for where the walker stands; for one off the mesh the path starts at the nearest point of the mesh, which it walks to
    /// first (Observed: zone 22.10, a walker on the terrain beside the Old Storm House's door stair dropped the step onto the stair's foot and walked up its side).
    /// </summary>
    PathResult FromWalker(Vector3 from, PathResult found)
    {
        var points = new List<Vector3>(found.Points.Count + 1);
        if (Vector2.Distance(new(from.X, from.Z), new(found.Points[0].X, found.Points[0].Z)) > OffMeshStart) points.Add(from);
        foreach (var p in found.Points) points.Add(new Vector3(p.X, GroundHeight(p.X, p.Z, p.Y) is var y && MathF.Abs(y - p.Y) <= TerrainSnapRange ? y : p.Y, p.Z));
        return new PathResult(points);
    }

    /// <summary>How far (units) the start of a navmesh path may be from the walker before the walker's own position is put first (<see cref="FromWalker"/>).</summary>
    public const float OffMeshStart = 0.5f;

    /// <summary>How many other start polygons a walker off the mesh tries when the nearest one's path begins with a step it cannot take (engine choice).</summary>
    public const int OffMeshStarts = 4;
    /// <summary>How far (units) along its path a walker off the mesh must be able to step for the path to count (engine choice: past a stair's corner).</summary>
    public const float SteppableReach = 10;

    static bool IsOnMesh(NavWorld w, Vector3 p)
    {
        var heights = scratch ??= [];
        heights.Clear();
        w.HeightsAt(p.X, p.Z, heights);
        return heights.Count > 0;
    }

    /// <summary>Whether a walker at <paramref name="from"/> can stand at every unit of the first <see cref="SteppableReach"/> units of the path, its height carried on as movement does.</summary>
    bool Steppable(Vector3 from, IReadOnlyList<Vector3> points)
    {
        var at = from;
        float y = from.Y, walked = 0;
        foreach (var next in points)
        {
            var leg = new Vector2(next.X - at.X, next.Z - at.Z);
            float length = leg.Length();
            if (length < 1e-3f) continue;
            for (float d = MathF.Min(1, length); d <= length && walked + d <= SteppableReach; d += 1)
            {
                var p = new Vector2(at.X, at.Z) + leg * (d / length);
                if (!TryStand(p.X, p.Y, y, out y)) return false;
            }
            walked += length;
            if (walked >= SteppableReach) return true;
            at = next with { Y = y };
        }
        return true;
    }

    /// <summary>The road network long routes may take (<see cref="Route"/>); null: none, the terrain grid alone.</summary>
    public RoadNetwork? Roads { get; set; }

    /// <summary>How far (units) along a road route its navmesh part reaches at most (engine choice: the navmesh path to a far point would cut the road's bends,
    /// so the walker asks again every so often and keeps to the road).</summary>
    public const float RoadHandover = 1500;

    /// <summary>
    /// The long route over the terrain: by the roads when the original's rule takes them for the agent's road preference (<see cref="RoadNetwork.Route"/>), the legs to
    /// and from the road over the terrain grid (engine choice: the original walks them on its navmesh); else the grid's route. Empty when there is none; road routes
    /// are flagged.
    /// </summary>
    (List<Vector3> Route, bool Road) Route(Vector3 from, Vector3 to, NavAgent agent)
    {
        if (agent.RoadPreference <= 0 || Roads?.Route(new(from.X, from.Z), new(to.X, to.Z), agent.RoadPreference) is not { } via) return (Coarse.Find(from, to, agent.Water), false);
        // via: the join, the road's points, the leave, then the goal.
        var route = new List<Vector3> { from };
        void Leg(Vector3 a, Vector3 b)
        {
            var leg = Coarse.Find(a, b, agent.Water);
            if (leg.Count > 1) route.AddRange(leg.Skip(1));
            else route.Add(b);
        }
        Vector3 At(Vector2 p) => new(p.X, MathF.Max(ground(p.X, p.Y), WorldWater.Height), p.Y);
        Leg(from, At(via[0]));
        for (int i = 1; i < via.Count - 1; i++) route.Add(At(via[i]));
        Leg(route[^1], to);
        return (route, true);
    }

    /// <summary>
    /// The long route (<see cref="Route"/>), with its start replaced by a navmesh path to the farthest point of it that lies well inside the loaded zones (the handover;
    /// on a road route at most <see cref="RoadHandover"/> along it).
    /// </summary>
    PathResult LongRoute(NavWorld w, Vector3 from, Vector3 to, NavAgent agent, bool fromLoaded)
    {
        var (route, road) = Route(from, to, agent);
        if (route.Count == 0) return fallback.FindPath(from, to);
        if (!fromLoaded) { Ahead(w, route, 1); return new PathResult(route) { Exact = 1 }; }

        // The handover: the farthest point along the route, in steps, that lies well inside the loaded zones, and the index of the route point after it.
        (int Next, Vector3 Point)? handover = null;
        float reach = road ? RoadHandover : float.MaxValue, along = 0;
        for (int i = 1; i < route.Count; i++)
        {
            var a = route[i - 1];
            var b = route[i];
            float length = Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
            bool left = false;
            for (float d = HandoverStep; d < length + HandoverStep; d += HandoverStep)
            {
                var p = Vector3.Lerp(a, b, MathF.Min(d, length) / MathF.Max(length, 1e-3f));
                if (!WellInside(w, p) || along + MathF.Min(d, length) > reach) { left = true; break; }
                handover = (i, p);
            }
            if (left) break;
            along += length;
        }
        if (handover is not var (next, point)) { Ahead(w, route, 1); return new PathResult(route) { Exact = 1 }; }

        // The navmesh path there, or to the reachable point nearest it (it may stand in a building, or behind a wall or a cliff the terrain grid
        // does not know); from such a point the rest of the route is looked for again.
        var target = new Vector3(point.X, GroundHeight(point.X, point.Z, point.Y), point.Z);
        var part = Toward(w, from, target, agent);
        if (!part.Found) { Ahead(w, route, 1); return new PathResult(route) { Exact = 1 }; }
        var points = part.Points.ToList();
        var end = points[^1];
        bool reached = Vector2.Distance(new(end.X, end.Z), new(target.X, target.Z)) <= NavQuery.GoalSnap;
        var rest = reached ? route.Skip(next).ToList() : Route(end, to, agent).Route.Skip(1).ToList();
        if (rest.Count == 0) rest = [.. route.Skip(next)];
        int exact = points.Count;
        points.AddRange(rest);
        Ahead(w, points, exact);
        return new PathResult(points) { Exact = exact };
    }

    /// <summary>Whether loaded zones lead from <paramref name="a"/> to <paramref name="b"/> side by side (both loaded).</summary>
    static bool Joined(NavWorld w, ZoneCoordinate a, ZoneCoordinate b)
    {
        var seen = new HashSet<ZoneCoordinate> { a };
        var queue = new Queue<ZoneCoordinate>([a]);
        while (queue.TryDequeue(out var z))
        {
            if (z == b) return true;
            foreach (var next in (ReadOnlySpan<ZoneCoordinate>)[new(z.X - 1, z.Y), new(z.X + 1, z.Y), new(z.X, z.Y - 1), new(z.X, z.Y + 1)])
                if (w.Contains(next) && seen.Add(next)) queue.Enqueue(next);
        }
        return false;
    }

    /// <summary>Whether the point and everything within <see cref="HandoverMargin"/> of it lies on loaded zones.</summary>
    static bool WellInside(NavWorld w, Vector3 p)
    {
        for (int dz = -1; dz <= 1; dz += 2)
            for (int dx = -1; dx <= 1; dx += 2)
                if (!w.Contains(WorldLayout.ZoneOf(p.X + dx * HandoverMargin, p.Z + dz * HandoverMargin))) return false;
        return true;
    }
}
