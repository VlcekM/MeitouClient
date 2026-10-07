using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Navigation;

/// <summary>
/// <c>meitou-tools navmesh</c>: gathers one zone's navmesh input (and, as the builder lands, builds the mesh) and writes debug files
/// (OBJ for a 3D viewer, a top-down PNG). Output goes where the user says, never into the repository.
/// </summary>
static class NavmeshTool
{
    static bool hidePruned;

    public static int Run(GameInstall install, string[] args)
    {
        ZoneCoordinate? zone = null;
        string? town = null, obj = null, png = null;
        float unitsPerPixel = 4;
        bool geometryOnly = false;
        var settings = new NavBuildSettings();
        int repeat = 1;
        float[]? box = null, pathArg = null;
        bool doorsClosed = false, noInteriors = false, noNeighbourSeeds = false; float? toY = null, fromY = null; float[]? near = null;
        int around = 0;
        bool fingerprint = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--zone": { var p = args[++i].Split(','); zone = new(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)); break; }
                case "--town": town = args[++i]; break;
                case "--obj": obj = args[++i]; break;
                case "--png": png = args[++i]; break;
                case "--scale": unitsPerPixel = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--geometry": geometryOnly = true; break;
                case "--cell": settings = settings with { CellSize = float.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--tile": settings = settings with { TileCells = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--near": near = args[++i].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--no-interiors": noInteriors = true; break;
                case "--to-y": toY = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--from-y": fromY = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--hide-pruned": hidePruned = true; break;
                case "--no-neighbour-seeds": noNeighbourSeeds = true; break;
                case "--closed": doorsClosed = true; break;
                case "--watershed": settings = settings with { Watershed = true }; break;
                case "--threads": settings = settings with { Threads = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--around": around = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--path": pathArg = args[++i].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--repeat": repeat = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--fingerprint": fingerprint = true; break;
                case "--box": box = args[++i].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray(); break;
                default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 2;
            }
        }

        var watch = Stopwatch.StartNew();
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var levels = WorldLevelData.Load(install);
        Console.WriteLine($"game data and levels loaded ({watch.ElapsedMilliseconds} ms)");
        if (fingerprint) return Fingerprint(install, db, levels, zone is { } only ? [only] : [new ZoneCoordinate(20, 32), new ZoneCoordinate(21, 32)], settings);
        if (zone is null)
        {
            var match = levels.Towns().Select(t => (Place: t, Record: db.Find(t.TownId)))
                .Where(t => t.Record is not null && t.Record.Name.Contains(town ?? "The Hub", StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Record!.Name.Length).FirstOrDefault();
            if (match.Record is null) { Console.Error.WriteLine("No such town."); return 1; }
            zone = WorldLayout.ZoneOf(match.Place.Position.X, match.Place.Position.Z);
            Console.WriteLine($"town '{match.Record.Name}' at {match.Place.Position.X:0}, {match.Place.Position.Z:0}: zone {zone}");
        }

        using var gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install));
        watch.Restart();
        var g = gatherer.Gather(zone.Value);
        if (NavDebug.Verbose) for (int i = 1; i <= 3; i++) { var sw2 = Stopwatch.StartNew(); gatherer.Gather(new ZoneCoordinate(zone.Value.X + i, zone.Value.Y)); Console.WriteLine($"  warm gather {sw2.ElapsedMilliseconds} ms (terrain {gatherer.Phases.Terrain:0}, buildings {gatherer.Phases.Buildings:0}, foliage {gatherer.Phases.Foliage:0})"); }
        var s = g.Stats;
        Console.WriteLine($"gathered zone {zone} in {watch.ElapsedMilliseconds} ms (terrain {gatherer.Phases.Terrain:0}, buildings {gatherer.Phases.Buildings:0}, foliage {gatherer.Phases.Foliage:0}): {g.TriangleCount} triangles ({s.TerrainTriangles} terrain + {s.WaterTriangles} water, " +
            $"{s.WalkableTriangles} walkable and {s.CuttingTriangles} cutting object triangles), {s.Buildings} buildings, {s.PartsWithCollision} parts with collision, {s.Shapes} shapes, " +
            $"{s.FoliageInstances} foliage objects ({s.FoliageShapes} shapes, {s.FoliageCutters} cutters), {g.Carvers.Count} carvers, {g.Painters.Count} door painters, {g.Seeds.Count} seeds ({s.SeedsDropped} dropped by the ray rule, {s.WallSeeds} wall, {s.DoorSeeds} door), " +
            $"{s.MissingFiles} missing files, building hash {g.BuildingHash:x8}");

        if (NavDebug.Verbose) { foreach (var p in g.Painters) Console.WriteLine($"  painter x {p.Polygon.Min(v => v.X):0}..{p.Polygon.Max(v => v.X):0} z {p.Polygon.Min(v => v.Y):0}..{p.Polygon.Max(v => v.Y):0} y {p.YMin:0}..{p.YMax:0}"); foreach (var sd in g.Seeds) Console.WriteLine($"  seed {sd.X:0},{sd.Y:0},{sd.Z:0}"); }
        if (near is not null) foreach (var line in gatherer.DescribeNear(zone.Value, near[0], near[1], near[2])) Console.WriteLine("  " + line);
        if (geometryOnly)
        {
            if (obj is not null) WriteGeometryObj(g, obj);
            if (png is not null) WriteGeometryPng(g, png, unitsPerPixel, box);
            return 0;
        }

        var built = new List<(ZoneGeometry? Geometry, ZoneNavMesh Mesh)>();
        var interiorMeshes = new List<ZoneNavMesh>();
        NavWorld world = NavWorld.Empty;
        for (int dz = -around; dz <= around; dz++)
            for (int dx = -around; dx <= around; dx++)
            {
                var c = new ZoneCoordinate(zone.Value.X + dx, zone.Value.Y + dz);
                if (!c.IsInsideGrid) continue;
                var geometry = dx == 0 && dz == 0 ? g : gatherer.Gather(c);
                if (!noNeighbourSeeds) geometry.Seeds.AddRange(NeighbourSeeds.Collect(world, c, geometry));
                ZoneNavMesh? mesh = null;
                for (int i = 0; i < (dx == 0 && dz == 0 ? repeat : 1); i++)
                {
                    mesh = NavMeshPipeline.BuildZone(gatherer, geometry, settings, out var t, out var inner, interiors: !noInteriors, log: NavDebug.Verbose ? line => Console.WriteLine(line) : null); interiorMeshes.AddRange(inner);
                    Console.WriteLine($"built {c}: {t.TileCount} tiles in {t.Tiles:0} ms, stitch {t.Stitch:0} ms, prune {t.Prune:0} ms, interiors {t.Interiors:0} ms ({t.InteriorCount}), total {t.Total:0} ms; {t.Polygons} polygons ({t.KeptPolygons} kept), {t.Vertices} vertices");
                    if (dx == 0 && dz == 0)
                        Console.WriteLine($"  cpu ms summed over tiles: raster {t.CpuRaster:0}, compact+areas {t.CpuCompact:0}, regions {t.CpuRegions:0}, contours {t.CpuContours:0}, polygons {t.CpuMesh:0}");
                }
                built.Add((geometry, mesh!));
                if (dx == 0 && dz == 0 && NavDebug.Verbose) foreach (var p in g.Painters) { int n = 0; for (int q = 0; q < mesh!.PolygonCount; q++) { if (mesh.Areas[q] != NavArea.Door) continue; var vv = mesh.Vertices[mesh.Polygons[q][0]]; if (vv.X >= p.Polygon.Min(a => a.X) - 4 && vv.X <= p.Polygon.Max(a => a.X) + 4 && vv.Z >= p.Polygon.Min(a => a.Y) - 4 && vv.Z <= p.Polygon.Max(a => a.Y) + 4) n++; } Console.WriteLine($"  door polygons near painter {p.Polygon.Min(a => a.X):0},{p.Polygon.Min(a => a.Y):0}: {n}"); }
                world = world.With(mesh!.WithoutPruned());
            }

        List<Vector3>? path = null;
        if (pathArg is not null)
        {
            Vector3 At(float x, float z) => new(x, Math.Max(gatherer.TerrainHeight(x, z), WorldWater.Height), z);
            var from = At(pathArg[0], pathArg[1]); if (fromY is { } fy) from = new Vector3(from.X, fy, from.Z);
            var to = At(pathArg[2], pathArg[3]); if (toY is { } ty) to = new Vector3(to.X, ty, to.Z);
            var query = new NavQuery(world);
            var agent = new NavAgent { DoorsClosed = doorsClosed };
            var sw = Stopwatch.StartNew();
            var result = query.FindPath(from, to, agent);
            sw.Stop();
            if (!result.Found) Console.WriteLine($"path {from.X:0},{from.Z:0} -> {to.X:0},{to.Z:0}: none ({sw.Elapsed.TotalMilliseconds:0.0} ms)");
            if (!result.Found)
            {
                bool okStart = world.TryFindPolygon(from, NavQuery.StartSnap, out var sref, out var sp), okGoal = world.TryFindPolygon(to, NavQuery.GoalSnap, out var gref, out var gp);
                Console.WriteLine($"  start polygon {(okStart ? sref.ToString() : "none")}, goal polygon {(okGoal ? gref.ToString() : "none")}");
                if (okGoal) { var gm = world.Mesh(gref.Zone); var gc = gm.Centre(gref.Polygon); Console.WriteLine($"  goal polygon at {gc.X:0},{gc.Y:0},{gc.Z:0} area {gm.Areas[gref.Polygon]}, mesh has {gm.PolygonCount} polygons, goal point {to.X:0},{to.Y:0},{to.Z:0}"); }
                if (okStart)
                {
                    var seen = new HashSet<long> { sref.Key };
                    var queue2 = new Queue<NavRef>();
                    queue2.Enqueue(sref);
                    while (queue2.Count > 0)
                    {
                        var c = queue2.Dequeue();
                        var m = world.Mesh(c.Zone);
                        foreach (int q in m.Neighbours[c.Polygon]) if (q >= 0 && seen.Add(new NavRef(c.Zone, q).Key)) queue2.Enqueue(new NavRef(c.Zone, q));
                        var tl = m.LinksOf(c.Polygon);
                        for (int i = 1; i < tl.Length; i += 2) if (seen.Add(new NavRef(c.Zone, tl[i]).Key)) queue2.Enqueue(new NavRef(c.Zone, tl[i]));
                        foreach (var l in world.LinksOf(c)) if (seen.Add(l.To.Key)) queue2.Enqueue(l.To);
                    }
                    Console.WriteLine($"  {seen.Count} polygons reachable from the start without clearance; goal reachable: {okGoal && seen.Contains(gref.Key)}");
                }
            }
            else
            {
                path = [.. result.Points];
                if (NavDebug.Verbose) foreach (var pt in path) Console.WriteLine($"  {pt.X:0},{pt.Y:0},{pt.Z:0}");
                float length = 0;
                for (int i = 1; i < path.Count; i++) length += Vector3.Distance(path[i - 1], path[i]);
                Console.WriteLine($"path {from.X:0},{from.Z:0} -> {to.X:0},{to.Z:0}: {path.Count} points, length {length:0} (straight {Vector3.Distance(from, to):0}), {sw.Elapsed.TotalMilliseconds:0.0} ms");
            }
        }
        if (obj is not null) WriteMeshObj(built[0].Mesh, obj);
        if (png is not null) WriteMeshPng(built, png, unitsPerPixel, box, path, interiorMeshes);
        return 0;
    }

    /// <summary>
    /// <c>--fingerprint</c>: the refactoring gate. Builds each zone with a fresh gatherer and no neighbours (as <c>--around 0</c> does), prints the
    /// SHA-256 of the cache file it would save, and, when the Hub's two zones are built, the point lists of the paths <c>HubNavmeshTests</c> pins.
    /// </summary>
    static int Fingerprint(GameInstall install, GameDatabase db, WorldLevelData levels, ZoneCoordinate[] zones, NavBuildSettings settings)
    {
        var dir = Path.Combine(Path.GetTempPath(), "meitou-nav-fingerprint-" + Guid.NewGuid().ToString("N"));
        var cache = new NavMeshCache(dir);
        var meshes = new Dictionary<ZoneCoordinate, ZoneNavMesh>();
        try
        {
            foreach (var zone in zones)
            {
                using var gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install));
                var g = gatherer.Gather(zone);
                var mesh = NavMeshPipeline.BuildZone(gatherer, g, settings, out _);
                cache.Save(mesh, g.BuildingHash, NavMeshCache.SettingsHash(settings));
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(cache.PathOf(zone.X, zone.Y))));
                Console.WriteLine($"zone {zone}: {mesh.PolygonCount} polygons ({mesh.KeptCount} kept), {mesh.Vertices.Length} vertices, sha256 {hash}");
                meshes[zone] = mesh;
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        if (zones.Length == 2 && meshes.TryGetValue(new ZoneCoordinate(20, 32), out var west) && meshes.TryGetValue(new ZoneCoordinate(21, 32), out var east))
        {
            foreach (var (name, points) in HubPaths(west, east))
            {
                Console.WriteLine($"path {name}:");
                foreach (var p in points) Console.WriteLine("  " + p);
            }
        }
        return 0;
    }

    /// <summary>The paths of <c>HubNavmeshTests</c>, as round-trip formatted point lists.</summary>
    static IEnumerable<(string Name, List<string> Points)> HubPaths(ZoneNavMesh west, ZoneNavMesh east)
    {
        static List<string> Format(Meitou.Simulation.PathResult path) => path.Found ? path.Points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:R},{p.Y:R},{p.Z:R}")).ToList() : ["none"];
        var house = NavWorld.Empty.With(west.WithoutPruned());
        var houseQuery = new NavQuery(house, new NavDoors());
        var street = new Vector3(-51290, 1566, 2625);
        var floor = new Vector3(-51158, 1579, 2664);
        yield return ("street to Storm House floor", Format(houseQuery.FindPath(street, floor)));
        yield return ("Storm House floor to street", Format(houseQuery.FindPath(floor, street)));
        var hub = NavWorld.Empty.With(west.WithoutPruned()).With(east.WithoutPruned());
        var query = new NavQuery(hub);
        Vector3 At(float x, float z) => new(x, 0, z);
        var outsideWest = At(-53500, 2000);
        var centre = At(-51000, 2900);
        var outsideEast = At(-49500, 3500);
        yield return ("west to centre", Format(query.FindPath(outsideWest, centre)));
        yield return ("centre to east", Format(query.FindPath(centre, outsideEast)));
        yield return ("west to east", Format(query.FindPath(outsideWest, outsideEast)));
        yield return ("west to centre, doors closed", Format(query.FindPath(outsideWest, centre, new NavAgent { DoorsClosed = true })));
    }

    static void WriteGeometryObj(ZoneGeometry g, string path)
    {
        using var w = new StreamWriter(path);
        for (int i = 0; i < g.Vertices.Count; i += 3)
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"v {g.Vertices[i]:0.##} {g.Vertices[i + 1]:0.##} {g.Vertices[i + 2]:0.##}"));
        foreach (var (name, area) in new[] { ("null", NavArea.Null), ("ground", NavArea.Ground), ("water", NavArea.Water) })
        {
            w.WriteLine($"g {name}");
            for (int t = 0; t < g.TriangleCount; t++)
                if (g.Areas[t] == area)
                    w.WriteLine($"f {g.Indices[t * 3] + 1} {g.Indices[t * 3 + 1] + 1} {g.Indices[t * 3 + 2] + 1}");
        }
        Console.WriteLine($"wrote {path}");
    }

    static void WriteGeometryPng(ZoneGeometry g, string path, float unitsPerPixel, float[]? box)
    {
        var r = new TopDownRenderer(box?[0] ?? g.ZoneMin.X, box?[1] ?? g.ZoneMin.Y, box?[2] ?? g.ZoneMax.X, box?[3] ?? g.ZoneMax.Y, unitsPerPixel, (20, 20, 24));
        var v = g.Vertices;
        for (int t = 0; t < g.TriangleCount; t++)
        {
            Vector3 P(int k) => new(v[g.Indices[t * 3 + k] * 3], v[g.Indices[t * 3 + k] * 3 + 1], v[g.Indices[t * 3 + k] * 3 + 2]);
            var colour = g.Areas[t] switch
            {
                NavArea.Ground => ((byte)110, (byte)170, (byte)90),
                NavArea.Water => ((byte)70, (byte)110, (byte)200),
                _ => ((byte)190, (byte)90, (byte)70),
            };
            r.Triangle(P(0), P(1), P(2), colour);
        }
        foreach (var c in g.Carvers)
            for (int i = 0; i < c.Polygon.Length; i++)
            {
                var a = c.Polygon[i];
                var b = c.Polygon[(i + 1) % c.Polygon.Length];
                r.Line(new(a.X, 0, a.Y), new(b.X, 0, b.Y), (255, 255, 0));
            }
        foreach (var c in g.Painters)
            for (int i = 0; i < c.Polygon.Length; i++)
            {
                var a = c.Polygon[i];
                var b = c.Polygon[(i + 1) % c.Polygon.Length];
                r.Line(new(a.X, 0, a.Y), new(b.X, 0, b.Y), (255, 140, 0));
            }
        foreach (var s in g.Seeds) r.Pixel(s.X, s.Z, (255, 255, 255), 2);
        WorldPng.Write(path, r.Width, r.Height, r.Rgb);
        Console.WriteLine($"wrote {path} ({r.Width}x{r.Height})");
    }

    static readonly (string Name, byte Area)[] AreaNames = [("ground", NavArea.Ground), ("water", NavArea.Water), ("door", NavArea.Door)];

    /// <summary>The polygons as an OBJ: one group per area, plus "pruned" for those the seed pass removed.</summary>
    static void WriteMeshObj(ZoneNavMesh m, string path)
    {
        using var w = new StreamWriter(path);
        foreach (var v in m.Vertices) w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"v {v.X:0.##} {v.Y:0.##} {v.Z:0.##}"));
        foreach (var (name, area) in AreaNames)
            Group(name, p => m.Kept[p] && m.Areas[p] == area);
        Group("pruned", p => !m.Kept[p]);
        Console.WriteLine($"wrote {path}");

        void Group(string name, Func<int, bool> pick)
        {
            w.WriteLine($"g {name}");
            for (int p = 0; p < m.PolygonCount; p++)
            {
                if (!pick(p)) continue;
                var poly = m.Polygons[p];
                for (int k = 1; k + 1 < poly.Length; k++)
                    w.WriteLine($"f {poly[0] + 1} {poly[k] + 1} {poly[k + 1] + 1}");
            }
        }
    }

    /// <summary>Top-down image: the gathered geometry dimmed underneath, the kept polygons in colour, pruned ones in red, the path in cyan.</summary>
    static void WriteMeshPng(List<(ZoneGeometry? Geometry, ZoneNavMesh Mesh)> zones, string path, float unitsPerPixel, float[]? box, List<Vector3>? route, List<ZoneNavMesh> interiors)
    {
        float x0 = zones.Min(z => z.Mesh.BoundsMin.X), z0 = zones.Min(z => z.Mesh.BoundsMin.Y), x1 = zones.Max(z => z.Mesh.BoundsMax.X), z1 = zones.Max(z => z.Mesh.BoundsMax.Y);
        var r = new TopDownRenderer(box?[0] ?? x0, box?[1] ?? z0, box?[2] ?? x1, box?[3] ?? z1, unitsPerPixel, (20, 20, 24));
        foreach (var (g, m) in zones)
        {
            if (g is not null && !hidePruned)
            {
                var v = g.Vertices;
                for (int t = 0; t < g.TriangleCount; t++)
                {
                    Vector3 P(int k) => new(v[g.Indices[t * 3 + k] * 3], v[g.Indices[t * 3 + k] * 3 + 1], v[g.Indices[t * 3 + k] * 3 + 2]);
                    var colour = g.Areas[t] == NavArea.Null ? ((byte)95, (byte)55, (byte)50) : ((byte)45, (byte)55, (byte)45);
                    r.Triangle(P(0), P(1), P(2), colour);
                }
            }
            for (int p = 0; p < m.PolygonCount; p++)
            {
                var poly = m.Polygons[p];
                if (hidePruned && !m.Kept[p]) continue;
                var colour = !m.Kept[p] ? ((byte)200, (byte)60, (byte)60) : m.Areas[p] switch
                {
                    NavArea.Water => ((byte)80, (byte)130, (byte)230),
                    NavArea.Door => ((byte)255, (byte)170, (byte)40),
                    _ => ((byte)120, (byte)220, (byte)120),
                };
                for (int k = 1; k + 1 < poly.Length; k++)
                    r.Triangle(m.Vertices[poly[0]] + Vector3.UnitY * 0.5f, m.Vertices[poly[k]] + Vector3.UnitY * 0.5f, m.Vertices[poly[k + 1]] + Vector3.UnitY * 0.5f, colour, 1000);
            }
            if (unitsPerPixel <= 2)
                for (int p = 0; p < m.PolygonCount; p++)
                {
                    var poly = m.Polygons[p];
                    for (int k = 0; k < poly.Length; k++)
                        r.Line(m.Vertices[poly[k]], m.Vertices[poly[(k + 1) % poly.Length]], m.Kept[p] ? ((byte)30, (byte)90, (byte)30) : ((byte)120, (byte)30, (byte)30));
                }
            if (g is not null) foreach (var s in g.Seeds) r.Pixel(s.X, s.Z, (255, 255, 255), 2);
        }
        // Building interiors on top, in teal (their polygons lie over the ground under the house).
        foreach (var m in interiors)
            for (int p = 0; p < m.PolygonCount; p++)
            {
                if (!m.Kept[p]) continue;
                var poly = m.Polygons[p];
                var colour = m.Areas[p] == NavArea.Door ? ((byte)255, (byte)170, (byte)40) : ((byte)60, (byte)200, (byte)190);
                for (int k = 1; k + 1 < poly.Length; k++)
                    r.Triangle(m.Vertices[poly[0]] + Vector3.UnitY * 2f, m.Vertices[poly[k]] + Vector3.UnitY * 2f, m.Vertices[poly[k + 1]] + Vector3.UnitY * 2f, colour, 1000);
                if (unitsPerPixel <= 2)
                    for (int k = 0; k < poly.Length; k++) r.Line(m.Vertices[poly[k]], m.Vertices[poly[(k + 1) % poly.Length]], (20, 90, 85));
            }
        if (route is not null)
        {
            for (int i = 1; i < route.Count; i++)
                for (int w = -1; w <= 1; w++)
                    r.Line(route[i - 1] + new Vector3(w * unitsPerPixel * 0.7f, 0, 0), route[i] + new Vector3(w * unitsPerPixel * 0.7f, 0, 0), (0, 255, 255));
            r.Pixel(route[0].X, route[0].Z, (255, 255, 0), 4);
            r.Pixel(route[^1].X, route[^1].Z, (255, 0, 255), 4);
        }
        WorldPng.Write(path, r.Width, r.Height, r.Rgb);
        Console.WriteLine($"wrote {path} ({r.Width}x{r.Height})");
    }
}
