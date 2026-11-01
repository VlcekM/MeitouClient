using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Navigation;

/// <summary>
/// <c>meitou-tools navmesh</c>: gathers one zone's navmesh input, builds the mesh (exterior and building interiors) and writes debug files
/// (OBJ for a 3D viewer, a top-down PNG). Output goes where the user says, never into the repository.
/// </summary>
static class NavmeshTool
{
    // --verbose: painters, seeds, interiors, door joins and path points are listed, and --near lists every field of the building records.
    // --near also lists the map features, carvers and solids reaching within the radius; --probe shows one point's column or a map of ground against the original.
    static bool verbose;

    public static int Run(GameInstall install, string[] args)
    {
        ZoneCoordinate? zone = null;
        string? town = null, obj = null, png = null;
        float unitsPerPixel = 4;
        bool geometryOnly = false;
        var settings = new NavBuildSettings();
        int repeat = 1;
        float[]? box = null, pathArg = null;
        bool doorsClosed = false, noInteriors = false; float? toY = null, fromY = null; float[]? near = null, probe = null, route = null; string? probeArg = null;
        int around = 0;
        bool fingerprint = false, featureTops = false, ragdoll = false;
        string? interiorKey = null;
        bool compareTowns = false;
        bool ownOnly = false;
        var compare = new List<ZoneCoordinate>();
        var compareInteriors = new List<ZoneCoordinate>();
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
                case "--near": near = args[++i].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--no-interiors": noInteriors = true; break;
                case "--probe": probeArg = args[++i]; probe = probeArg.Split(';')[0].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--to-y": toY = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--from-y": fromY = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--closed": doorsClosed = true; break;
                case "--around": around = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--path": pathArg = args[++i].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--repeat": repeat = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--fingerprint": fingerprint = true; break;
                case "--feature-tops": featureTops = true; break;
                case "--ragdoll": ragdoll = true; break;
                case "--route": route = args[++i].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--compare":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        var p = args[++i].Split(',');
                        compare.Add(new(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)));
                    }
                    break;
                case "--compare-interiors":
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        if (args[++i] == "towns") { compareTowns = true; continue; }
                        var p = args[i].Split(',');
                        compareInteriors.Add(new(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)));
                    }
                    break;
                case "--verbose": verbose = true; break;
                case "--interior": interiorKey = args[++i]; break;
                case "--climb": settings = settings with { MaxClimb = float.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--cell": settings = settings with { CellSize = float.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--own-only": ownOnly = true; break;
                case "--tile": settings = settings with { TileCells = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--min-region": settings = settings with { MinRegionArea = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--merge-region": settings = settings with { MergeRegionArea = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--simplify": settings = settings with { MaxSimplificationError = float.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--box": box = args[++i].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray(); break;
                default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 2;
            }
        }

        var watch = Stopwatch.StartNew();
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var levels = WorldLevelData.Load(install, includeInteriors: true);
        Console.WriteLine($"game data and levels loaded ({watch.ElapsedMilliseconds} ms)");
        if (compare.Count > 0) return Compare(install, db, levels, compare, settings);
        if (compareTowns)
            compareInteriors.AddRange(levels.Towns().Select(t => WorldLayout.ZoneOf(t.Position.X, t.Position.Z)).Distinct().Except(compareInteriors).OrderBy(z => z.X).ThenBy(z => z.Y));
        if (compareInteriors.Count > 0) return CompareInteriors(install, db, levels, compareInteriors, settings, around);
        if (route is { Length: 4 }) return Route(install, db, levels, route);
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

        var collision = new CollisionCache(install);
        using var gatherer = new ZoneGeometryGatherer(install, db, levels, collision);
        watch.Restart();
        if (ragdoll)
        {
            // What ragdolls land on in the zone (RagdollGeometry): building and map feature triangles.
            var all = new List<Vector3>();
            var sw = Stopwatch.StartNew();
            gatherer.RagdollTriangles(zone.Value, all);
            long ms = sw.ElapsedMilliseconds;
            var features = new List<Vector3>();
            gatherer.AddRagdollFeatures(zone.Value, features);
            Console.WriteLine($"ragdoll triangles in {zone}: {all.Count / 3} ({features.Count / 3} of them map features), gathered in {ms} ms");
            return 0;
        }
        var g = gatherer.Gather(zone.Value);
        if (verbose) for (int i = 1; i <= 3; i++) { var sw2 = Stopwatch.StartNew(); gatherer.Gather(new ZoneCoordinate(zone.Value.X + i, zone.Value.Y)); Console.WriteLine($"  warm gather {sw2.ElapsedMilliseconds} ms (terrain {gatherer.Phases.Terrain:0}, buildings {gatherer.Phases.Buildings:0}, foliage {gatherer.Phases.Foliage:0})"); }
        var s = g.Stats;
        Console.WriteLine($"gathered zone {zone} in {watch.ElapsedMilliseconds} ms (terrain {gatherer.Phases.Terrain:0}, buildings {gatherer.Phases.Buildings:0}, foliage {gatherer.Phases.Foliage:0}): {g.TriangleCount} triangles ({s.TerrainTriangles} terrain + {s.WaterTriangles} water, " +
            $"{s.WalkableTriangles} walkable and {s.CuttingTriangles} cutting object triangles), {s.Buildings} buildings, {s.PartsWithCollision} parts with collision, {s.Shapes} shapes, " +
            $"{s.FoliageInstances} foliage objects ({s.FoliageShapes} shapes, {s.FoliageMeshes} of them triangle meshes, {s.FoliageCutters} cutters), {s.MapFeatures} map features, {g.Carvers.Count} carvers, {g.Painters.Count} door painters, {g.Seeds.Count} seeds ({s.SeedsDropped} dropped by the ray rule, {s.WallSeeds} wall, {s.DoorSeeds} door), " +
            $"{s.MissingFiles} missing files, building hash {g.BuildingHash:x8}");

        if (verbose) { foreach (var p in g.Painters) Console.WriteLine($"  painter x {p.Polygon.Min(v => v.X):0}..{p.Polygon.Max(v => v.X):0} z {p.Polygon.Min(v => v.Y):0}..{p.Polygon.Max(v => v.Y):0} y {p.YMin:0}..{p.YMax:0}"); foreach (var sd in g.Seeds) Console.WriteLine($"  seed {sd.X:0},{sd.Y:0},{sd.Z:0}"); }
        if (near is not null)
        {
            foreach (var line in DescribeNear(gatherer, db, collision, zone.Value, near[0], near[1], near[2])) Console.WriteLine("  " + line);
            foreach (var line in DescribeFeaturesNear(install, db, collision, zone.Value, near[0], near[1], near[2])) Console.WriteLine("  " + line);
            foreach (var line in DescribeVolumesNear(g, near[0], near[1], near[2])) Console.WriteLine("  " + line);
        }
        if (interiorKey is not null) return DescribeOneInterior(gatherer, zone.Value, interiorKey, settings, obj, ownOnly);
        if (geometryOnly)
        {
            if (obj is not null) WriteGeometryObj(g, obj);
            if (png is not null) WriteGeometryPng(g, png, unitsPerPixel, box);
            return 0;
        }

        var built = new List<(ZoneGeometry? Geometry, ZoneNavMesh Mesh)>();
        var interiorMeshes = new List<ZoneNavMesh>();
        NavWorld world = NavWorld.Empty;
        var full = new Dictionary<ZoneCoordinate, ZoneNavMesh>();
        for (int dz = -around; dz <= around; dz++)
            for (int dx = -around; dx <= around; dx++)
            {
                var c = new ZoneCoordinate(zone.Value.X + dx, zone.Value.Y + dz);
                if (!c.IsInsideGrid) continue;
                var geometry = dx == 0 && dz == 0 ? g : gatherer.Gather(c);
                ZoneNavMesh? mesh = null;
                for (int i = 0; i < (dx == 0 && dz == 0 ? repeat : 1); i++)
                {
                    mesh = NavMeshPipeline.BuildZone(gatherer, geometry, settings, out var t, out var inner, interiors: !noInteriors, log: verbose ? line => Console.WriteLine(line) : null); interiorMeshes.AddRange(inner.Select(i => i.Mesh));
                    Console.WriteLine($"built {c}: {t.TileCount} tiles in {t.Tiles:0} ms, stitch {t.Stitch:0} ms, prune {t.Prune:0} ms, interiors {t.Interiors:0} ms ({t.InteriorCount}), total {t.Total:0} ms; {t.Polygons} polygons ({t.KeptPolygons} kept), {t.Vertices} vertices");
                    if (dx == 0 && dz == 0)
                        Console.WriteLine($"  cpu ms summed over tiles: raster {t.CpuRaster:0}, compact+areas {t.CpuCompact:0}, regions {t.CpuRegions:0}, contours {t.CpuContours:0}, polygons {t.CpuMesh:0}, detail {t.CpuDetail:0}");
                }
                built.Add((geometry, mesh!));
                if (dx == 0 && dz == 0 && verbose) foreach (var p in g.Painters) { int n = 0; for (int q = 0; q < mesh!.PolygonCount; q++) { if (mesh.Areas[q] != NavArea.Door) continue; var vv = mesh.Vertices[mesh.Polygons[q][0]]; if (vv.X >= p.Polygon.Min(a => a.X) - 4 && vv.X <= p.Polygon.Max(a => a.X) + 4 && vv.Z >= p.Polygon.Min(a => a.Y) - 4 && vv.Z <= p.Polygon.Max(a => a.Y) + 4) n++; } Console.WriteLine($"  door polygons near painter {p.Polygon.Min(a => a.X):0},{p.Polygon.Min(a => a.Y):0}: {n}"); }
                full[c] = mesh!;
            }
        // The kept regions are resolved over the built zones as the game does for the loaded ones (NavMeshService): own seeds, the border rule toward
        // zones not built here, and the neighbours' kept border edges.
        foreach (var (c, keep) in ZoneKeptClosure.Resolve(full, settings)) world = world.With(full[c].WithKept(keep).WithoutPruned());
        if (featureTops) foreach (var line in FeatureTops(install, db, collision, gatherer, g, full[zone.Value], world)) Console.WriteLine(line);
        if (probe is { Length: 2 })
            foreach (var point in probeArg!.Split(';').Select(t => t.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray()))
                foreach (var line in Probe(install, gatherer, g, full[zone.Value], world, point[0], point[1])) Console.WriteLine("  " + line);
        if (probe is { Length: >= 4 }) foreach (var line in ProbeGrid(install, gatherer, g, full[zone.Value], world, probe[0], probe[1], probe[2], probe[3])) Console.WriteLine(line);

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
                    var seen = new Dictionary<long, NavRef?> { [sref.Key] = null };
                    var queue2 = new Queue<NavRef>();
                    queue2.Enqueue(sref);
                    while (queue2.Count > 0)
                    {
                        var c = queue2.Dequeue();
                        var m = world.Mesh(c.Zone);
                        foreach (int q in m.NeighboursOf(c.Polygon)) if (seen.TryAdd(new NavRef(c.Zone, q).Key, c)) queue2.Enqueue(new NavRef(c.Zone, q));
                        foreach (var l in world.LinksOf(c)) if (seen.TryAdd(l.To.Key, c)) queue2.Enqueue(l.To);
                    }
                    Console.WriteLine($"  {seen.Count} polygons reachable from the start without clearance; goal reachable: {okGoal && seen.ContainsKey(gref.Key)}");
                    // --verbose: the chain of polygon centres the clearance-free search took to the goal.
                    if (verbose && okGoal && seen.ContainsKey(gref.Key))
                        for (NavRef? c = gref; c is { } r; c = seen[r.Key])
                        {
                            var cc = world.Mesh(r.Zone).Centre(r.Polygon);
                            Console.WriteLine($"    chain polygon {r.Polygon} at {cc.X:0},{cc.Y:0},{cc.Z:0}");
                        }
                }
            }
            else
            {
                path = [.. result.Points];
                if (verbose) foreach (var pt in path) Console.WriteLine($"  {pt.X:0},{pt.Y:0},{pt.Z:0}");
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
    /// <c>--interior key</c>: gathers and builds one building's interior alone, lists its seeds and connected components, and with <c>--obj path</c> writes its
    /// gathered triangles (<c>path.geometry.obj</c>) and its polygons (<c>path</c>) for a 3D viewer or a script.
    /// </summary>
    static int DescribeOneInterior(ZoneGeometryGatherer gatherer, ZoneCoordinate zone, string key, NavBuildSettings settings, string? obj, bool ownOnly)
    {
        var site = gatherer.InteriorSites(zone).FirstOrDefault(s => s.Placement.Key == key);
        if (site is null) { Console.Error.WriteLine($"No interior {key} in zone {zone}; keys: {string.Join(", ", gatherer.InteriorSites(zone).Select(s => s.Placement.Key))}"); return 1; }
        var g = gatherer.GatherInterior(site, NavMeshPipeline.InteriorTileStep * settings.CellSize, agentHeight: settings.AgentHeight, ownOnly: ownOnly, trackParts: true);
        if (g is null) { Console.Error.WriteLine("No usable hull."); return 1; }
        settings = settings with { TileCells = (int)MathF.Round((g.ZoneMax.X - g.ZoneMin.X) / settings.CellSize) };
        foreach (var (owner, part, path, first, last) in g.PartRanges)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            int walkable = 0;
            for (int t = first; t < last; t++)
            {
                if (g.Areas[t] != NavArea.Null) walkable++;
                for (int k = 0; k < 3; k++) { float y = g.Vertices[g.Indices[t * 3 + k] * 3 + 1]; lo = Math.Min(lo, y); hi = Math.Max(hi, y); }
            }
            Console.WriteLine($"  part {owner} / {part}: triangles {first}..{last - 1} ({walkable} walkable), y {lo:0}..{hi:0}, {Path.GetFileName(path)}");
        }
        Console.WriteLine($"  hull y {g.InteriorHull!.YMin:0}..{g.InteriorHull.YMax:0}: {string.Join(" ", g.InteriorHull.Polygon.Select(p => $"({p.X:0},{p.Y:0})"))}");
        foreach (var p in g.Painters) Console.WriteLine($"  painter {p.Owner}: y {p.YMin:0}..{p.YMax:0}: {string.Join(" ", p.Polygon.Select(v => $"({v.X:0},{v.Y:0})"))}");
        Console.WriteLine($"interior {key} ({site.Record.Name}): box {g.ZoneMin.X:0},{g.ZoneMin.Y:0}..{g.ZoneMax.X:0},{g.ZoneMax.Y:0}, {g.TriangleCount} triangles ({g.Stats.WalkableTriangles} walkable, {g.Stats.CuttingTriangles} cutting), {g.Stats.Shapes} shapes, {g.Carvers.Count} carvers, {g.Painters.Count} painters, {g.Seeds.Count} seeds, {g.NodeSeeds.Count} node seeds");
        var mesh = ZoneNavMeshBuilder.Build(g, settings with { Threads = 1 }, out _);
        NavMeshPipeline.DescribeInterior(g, mesh, Console.WriteLine);
        for (int p = 0; p < mesh.PolygonCount; p++)
            if (mesh.Areas[p] == NavArea.Door)
                Console.WriteLine($"  door polygon {p} at {mesh.Centre(p).X:0},{mesh.Centre(p).Y:0},{mesh.Centre(p).Z:0}: door {(mesh.DoorOf is { } d && d[p] >= 0 ? mesh.DoorIds![d[p]] : "none")}");
        if (obj is not null)
        {
            WriteGeometryObj(g, Path.ChangeExtension(obj, ".geometry.obj"));
            WriteMeshObj(mesh, obj);
        }
        return 0;
    }

    /// <summary>
    /// <c>--route x0,z0,x1,z1</c>: the long-range route (<see cref="CoarseRoute"/>) between two points with the buildings' footprints, then with the map features' too, as the game
    /// builds it: its length, its turns and the samples of it inside a feature footprint.
    /// </summary>
    static int Route(GameInstall install, GameDatabase db, WorldLevelData levels, float[] r)
    {
        using var map = TerrainHeightmap.Open(install);
        Func<float, float, float> terrain = (x, z) => (float)map.HeightAt(x, z);
        Func<double, double, float> placement = (x, z) => (float)map.HeightAt(x, z);
        var raw = map.Downsample(2, out int size);
        var grid = new HeightWindow(0, 0, 2, size, size, raw);
        Func<float, float, float> slope = (x, z) => grid.TryHeightAt(x, z, out float h) ? h : terrain(x, z);
        var footprints = new BuildingFootprints(install, db, levels);
        var buildings = footprints.Obstacles(levels.Buildings(), levels, placement);
        var watch = Stopwatch.StartNew();
        var features = footprints.FeatureObstacles(placement);
        Console.WriteLine($"{buildings.Count} building footprints, {features.Count} feature rectangles ({watch.ElapsedMilliseconds} ms)");
        var from = new Vector3(r[0], terrain(r[0], r[1]), r[1]);
        var to = new Vector3(r[2], terrain(r[2], r[3]), r[3]);
        foreach (var (name, hulls) in new[] { ("buildings", buildings), ("buildings and features", (IReadOnlyList<Vector2[]>)[.. buildings, .. features]) })
        {
            var coarse = new CoarseRoute(terrain, () => slope, () => hulls);
            var points = coarse.Find(from, to, Meitou.Simulation.WaterCost.Uniform(1));
            float length = 0;
            int inside = 0, samples = 0;
            for (int i = 1; i < points.Count; i++)
            {
                length += Vector2.Distance(new(points[i - 1].X, points[i - 1].Z), new(points[i].X, points[i].Z));
                int n = (int)MathF.Ceiling(Vector2.Distance(new(points[i - 1].X, points[i - 1].Z), new(points[i].X, points[i].Z)) / 16);
                for (int k = 0; k < n; k++)
                {
                    var q = Vector3.Lerp(points[i - 1], points[i], k / (float)n);
                    samples++;
                    foreach (var f in features)
                        if (q.X >= f[0].X && q.X <= f[1].X && q.Z >= f[0].Y && q.Z <= f[2].Y) { inside++; break; }
                }
            }
            Console.WriteLine($"route with {name}: {points.Count} points, length {length:0}, {inside} of {samples} samples (every 16) inside a feature footprint");
            if (verbose) foreach (var q in points) Console.WriteLine($"  {q.X:0},{q.Y:0},{q.Z:0}");
        }
        return 0;
    }

    /// <summary>
    /// <c>--probe x,z</c>: what the zone has in the column at a point: the terrain, the gathered triangles over it, the carvers and solids holding it (with the
    /// heights the solid spans there), our polygons (unpruned, and whether the kept set holds them) and the original tile's polygons.
    /// </summary>
    static IEnumerable<string> Probe(GameInstall install, ZoneGeometryGatherer gatherer, ZoneGeometry g, ZoneNavMesh mesh, NavWorld world, float x, float z)
    {
        yield return $"probe {x:0},{z:0}: terrain {gatherer.TerrainHeight(x, z):0.0}";
        for (int t = 0; t < g.TriangleCount; t++)
        {
            if (t < g.BuildingFirstTriangle) continue;
            Vector3 V(int k) { int i = g.Indices[t * 3 + k]; return new(g.Vertices[i * 3], g.Vertices[i * 3 + 1], g.Vertices[i * 3 + 2]); }
            var a = V(0); var b = V(1); var c = V(2);
            if (!InTriangle(x, z, a, b, c, out float y)) continue;
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            yield return $"triangle {t} y {y:0.0} area {g.Areas[t]} normal.y {n.Y:0.00}{(t < g.BuildingLastTriangle ? " building" : " feature or foliage")}";
        }
        foreach (var cv in g.Carvers)
            if (Inside(cv.Polygon, new(x, z))) yield return $"carver y {cv.YMin:0}..{cv.YMax:0}{(cv.Owner is null ? "" : " " + cv.Owner)}";
        foreach (var s in g.Solids)
        {
            if (x < s.Min.X || x > s.Max.X || z < s.Min.Y || z > s.Max.Y) continue;
            float lo = float.MinValue, hi = float.MaxValue;
            foreach (var pl in s.Planes)
            {
                float k = pl.X * x + pl.Z * z + pl.W; // inside: pl.Y * y + k < 0
                if (MathF.Abs(pl.Y) < 1e-6f) { if (k >= 0) { lo = 1; hi = 0; } continue; }
                float y = -k / pl.Y;
                if (pl.Y > 0) hi = MathF.Min(hi, y); else lo = MathF.Max(lo, y);
            }
            yield return lo < hi ? $"solid inside y {lo:0.0}..{hi:0.0} {s.Owner}" : $"solid (box only) {s.Owner}";
        }
        foreach (var s in g.MeshSolids)
            if (x >= s.Min.X && x <= s.Max.X && z >= s.Min.Y && z <= s.Max.Y) yield return $"mesh solid (box) {s.Owner}";
        var polys = new List<int>();
        mesh.PolygonsAt(x, z, polys);
        var kept = world.Find(g.Zone)!;
        var keptPolys = new List<int>();
        kept.PolygonsAt(x, z, keptPolys);
        foreach (int p in polys)
        {
            float h = mesh.HeightAt(p, x, z);
            bool inKept = keptPolys.Any(q => MathF.Abs(kept.HeightAt(q, x, z) - h) < 0.5f);
            yield return $"ours polygon {p} y {h:0.0} area {mesh.Areas[p]}, own seeds keep it {mesh.Kept[p]}, kept after the closure {inKept}";
        }
        if (OriginalNavTiles.LoadExterior(install, g.Zone) is { } original)
            for (int p = 0; p < original.Polygons.Length; p++)
            {
                var poly = original.Polygons[p];
                for (int k = 1; k + 1 < poly.Length; k++)
                    if (InTriangle(x, z, original.Vertices[poly[0]], original.Vertices[poly[k]], original.Vertices[poly[k + 1]], out float y))
                    {
                        yield return $"original polygon {p} y {y:0.0}";
                        break;
                    }
            }
        else yield return "no original tile";

        static bool InTriangle(float x, float z, Vector3 a, Vector3 b, Vector3 c, out float y)
        {
            y = 0;
            if (!NavGeometry.Barycentric(x, z, a, b, c, out float u, out float v, out float w) || u < 0 || v < 0 || w < 0) return false;
            y = u * a.Y + v * b.Y + w * c.Y;
            return true;
        }
    }

    /// <summary>
    /// <c>--probe x,z,radius,step</c>: a map of the square round a point, one character per sample: <c>B</c> both meshes have a kept polygon within 12 of the
    /// terrain (ground), <c>o</c> only the original has ground there, <c>m</c> only ours, <c>p</c> the original has ground and ours has an unkept polygon there,
    /// <c>.</c> neither; upper case <c>X</c> marks the centre. Then the counts.
    /// </summary>
    static IEnumerable<string> ProbeGrid(GameInstall install, ZoneGeometryGatherer gatherer, ZoneGeometry g, ZoneNavMesh mesh, NavWorld world, float cx, float cz, float radius, float step)
    {
        var original = OriginalNavTiles.LoadExterior(install, g.Zone);
        if (original is null) { yield return "no original tile"; yield break; }
        var kept = world.Find(g.Zone)!;
        var buckets = Buckets(original);
        int both = 0, onlyOriginal = 0, onlyOurs = 0, pruned = 0;
        var list = new List<int>();
        for (float z = cz - radius; z <= cz + radius; z += step)
        {
            var row = new System.Text.StringBuilder();
            for (float x = cx - radius; x <= cx + radius; x += step)
            {
                float ground = gatherer.TerrainHeight(x, z);
                bool o = OriginalAt(original, buckets, x, z, ground, 12);
                list.Clear(); kept.PolygonsAt(x, z, list);
                bool m = list.Any(q => MathF.Abs(kept.HeightAt(q, x, z) - ground) < 12);
                list.Clear(); mesh.PolygonsAt(x, z, list);
                bool any = list.Any(q => MathF.Abs(mesh.HeightAt(q, x, z) - ground) < 12);
                char c = o && m ? 'B' : o ? (any ? 'p' : 'o') : m ? 'm' : '.';
                if (o && m) both++; else if (o && any) pruned++; else if (o) onlyOriginal++; else if (m) onlyOurs++;
                if (MathF.Abs(x - cx) < step / 2 && MathF.Abs(z - cz) < step / 2) c = char.ToUpperInvariant(c) == c ? 'X' : char.ToUpperInvariant(c);
                row.Append(c);
            }
            yield return row.ToString();
        }
        yield return $"ground samples (within 12 of the terrain): both {both}, original only {onlyOriginal}, original and ours unkept {pruned}, ours only {onlyOurs}";
    }

    /// <summary>
    /// <c>--feature-tops</c>: for each map feature standing in the zone, samples every 8 units over its box where its topmost face is walkable (up to 40°) and
    /// above the terrain by more than 12, and counts the samples where the original's tile, our mesh (unpruned) and our kept mesh have a polygon within 8 of it.
    /// </summary>
    static IEnumerable<string> FeatureTops(GameInstall install, GameDatabase db, CollisionCache collision, ZoneGeometryGatherer gatherer, ZoneGeometry g, ZoneNavMesh mesh, NavWorld world)
    {
        var original = OriginalNavTiles.LoadExterior(install, g.Zone);
        if (original is null) { yield return "no original tile"; yield break; }
        var buckets = Buckets(original);
        var kept = world.Find(g.Zone)!;
        var list = new List<int>();
        var file = MapFeatureFile.Open(install);
        int index = g.Zone.Y * WorldLayout.ZoneCount + g.Zone.X;
        float cos = MathF.Cos(40 * MathF.PI / 180);
        foreach (var f in file.Zones[index])
        {
            if (db.Find(f.StringId) is not { } record || record.GetPath("collision") is not { Length: > 0 } path || collision.Get(path) is not { } prepared) continue;
            var node = WorldObjectLayout.InstanceTransform(f.Position, f.Rotation, f.Scale);
            var tris = new List<Vector3>();
            foreach (var shape in prepared.Shapes)
                foreach (int i in shape.Indices) tris.Add(Vector3.Transform(shape.Vertices[i], node));
            float bx0 = tris.Min(v => v.X), bx1 = tris.Max(v => v.X), bz0 = tris.Min(v => v.Z), bz1 = tris.Max(v => v.Z), by0 = tris.Min(v => v.Y), by1 = tris.Max(v => v.Y);
            int samples = 0, orig = 0, ours = 0, ourKept = 0;
            for (float z = MathF.Ceiling(bz0 / 8) * 8; z <= bz1; z += 8)
                for (float x = MathF.Ceiling(bx0 / 8) * 8; x <= bx1; x += 8)
                {
                    float top = float.MinValue; bool walk = false;
                    for (int t = 0; t < tris.Count; t += 3)
                    {
                        var a = tris[t]; var b = tris[t + 1]; var c = tris[t + 2];
                        if (!NavGeometry.Barycentric(x, z, a, b, c, out float u, out float v, out float w) || u < 0 || v < 0 || w < 0) continue;
                        float y = u * a.Y + v * b.Y + w * c.Y;
                        if (y <= top) continue;
                        top = y;
                        var n = Vector3.Cross(b - a, c - a);
                        walk = n.Y / n.Length() >= cos;
                    }
                    if (!walk || top < gatherer.TerrainHeight(x, z) + 12) continue;
                    samples++;
                    bool o = OriginalAt(original, buckets, x, z, top, 8);
                    if (o) orig++;
                    list.Clear(); mesh.PolygonsAt(x, z, list);
                    bool any = list.Any(q => MathF.Abs(mesh.HeightAt(q, x, z) - top) < 8);
                    if (any) ours++;
                    list.Clear(); kept.PolygonsAt(x, z, list);
                    bool k = list.Any(q => MathF.Abs(kept.HeightAt(q, x, z) - top) < 8);
                    if (k) ourKept++;
                }
            if (samples > 0)
                yield return $"feature {record.Name} at {f.Position.X:0},{f.Position.Y:0},{f.Position.Z:0} x {bx0:0}..{bx1:0} z {bz0:0}..{bz1:0} y {by0:0}..{by1:0}: {samples} walkable top samples above ground, original {orig}, ours {ours}, ours kept {ourKept}";
        }
    }

    /// <summary>An original tile's polygons by the 64-unit cell of their box on (X, Z), for point lookups.</summary>
    static Dictionary<(int, int), List<int>> Buckets(OriginalNavMesh original)
    {
        var buckets = new Dictionary<(int, int), List<int>>();
        for (int p = 0; p < original.Polygons.Length; p++)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (int v in original.Polygons[p]) { x0 = MathF.Min(x0, original.Vertices[v].X); x1 = MathF.Max(x1, original.Vertices[v].X); z0 = MathF.Min(z0, original.Vertices[v].Z); z1 = MathF.Max(z1, original.Vertices[v].Z); }
            for (int i = (int)MathF.Floor(x0 / 64); i <= (int)MathF.Floor(x1 / 64); i++)
                for (int j = (int)MathF.Floor(z0 / 64); j <= (int)MathF.Floor(z1 / 64); j++)
                {
                    if (!buckets.TryGetValue((i, j), out var l)) buckets[(i, j)] = l = [];
                    l.Add(p);
                }
        }
        return buckets;
    }

    /// <summary>Whether the original tile has a polygon over (x, z) (<see cref="Buckets"/>) whose height there is within <paramref name="within"/> of y.</summary>
    static bool OriginalAt(OriginalNavMesh original, Dictionary<(int, int), List<int>> buckets, float x, float z, float y, float within)
    {
        foreach (int p in buckets.GetValueOrDefault(((int)MathF.Floor(x / 64), (int)MathF.Floor(z / 64))) ?? [])
        {
            var poly = original.Polygons[p];
            for (int k = 1; k + 1 < poly.Length; k++)
                if (NavGeometry.Barycentric(x, z, original.Vertices[poly[0]], original.Vertices[poly[k]], original.Vertices[poly[k + 1]], out float u, out float v, out float w) && u >= 0 && v >= 0 && w >= 0
                    && MathF.Abs(u * original.Vertices[poly[0]].Y + v * original.Vertices[poly[k]].Y + w * original.Vertices[poly[k + 1]].Y - y) < within) return true;
        }
        return false;
    }

    static bool Inside(Vector2[] poly, Vector2 q)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if (poly[i].Y > q.Y != poly[j].Y > q.Y && q.X < (poly[j].X - poly[i].X) * (q.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
        return inside;
    }

    /// <summary>The carvers and solids that reach within a radius of a point (<c>--near</c>): what cuts the mesh there when no building near the point explains it.</summary>
    static IEnumerable<string> DescribeVolumesNear(ZoneGeometry g, float x, float z, float radius)
    {
        var p = new Vector2(x, z);
        foreach (var c in g.Carvers)
        {
            if (!c.Polygon.Any(v => Vector2.Distance(v, p) <= radius) && !Inside(c.Polygon, p)) continue;
            yield return $"carver {c.Polygon.Length} vertices x {c.Polygon.Min(v => v.X):0}..{c.Polygon.Max(v => v.X):0} z {c.Polygon.Min(v => v.Y):0}..{c.Polygon.Max(v => v.Y):0} y {c.YMin:0}..{c.YMax:0}{(c.Owner is null ? "" : " owner " + c.Owner)}";
        }
        foreach (var s in g.Solids)
            if (s.Min.X - radius <= x && x <= s.Max.X + radius && s.Min.Y - radius <= z && z <= s.Max.Y + radius)
                yield return $"solid x {s.Min.X:0}..{s.Max.X:0} z {s.Min.Y:0}..{s.Max.Y:0} ({s.Planes.Length} planes){(s.Owner is null ? "" : " " + s.Owner)}";
    }

    /// <summary>The map features (<c>features.dat</c>) of the zone and its neighbours within a radius of a point (<c>--near</c>), with their collision.</summary>
    static IEnumerable<string> DescribeFeaturesNear(GameInstall install, GameDatabase db, CollisionCache collision, ZoneCoordinate zone, float x, float z, float radius)
    {
        var file = MapFeatureFile.Open(install);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int index = (zone.Y + dz) * WorldLayout.ZoneCount + zone.X + dx;
                if (index < 0 || index >= file.Zones.Count) continue;
                foreach (var f in file.Zones[index])
                {
                    if (Vector2.Distance(new(f.Position.X, f.Position.Z), new(x, z)) > radius) continue;
                    var record = db.Find(f.StringId);
                    string path = record?.GetPath("collision") ?? "";
                    var prepared = path.Length > 0 ? collision.Get(path) : null;
                    yield return $"feature {record?.Name} [{f.StringId}] at {f.Position.X:0},{f.Position.Y:0},{f.Position.Z:0} scale {f.Scale.X:0.##},{f.Scale.Y:0.##},{f.Scale.Z:0.##} rotation {f.Rotation.X:0.###},{f.Rotation.Y:0.###},{f.Rotation.Z:0.###},{f.Rotation.W:0.###} collision {(path.Length == 0 ? "none" : path)}" +
                        (prepared is null ? "" : $" ({prepared.Shapes.Length} shapes, {prepared.Shapes.Count(s => s.IsConvex)} convex; " + string.Join("; ", prepared.Shapes.Select(s => $"{s.Indices.Length / 3} triangles, {OpenEdges(s)} open edges, local y {s.Vertices.Min(v => v.Y):0}..{s.Vertices.Max(v => v.Y):0}")) + ")");
                }
            }
    }

    /// <summary>Edges of a shape's triangles (vertices welded within 0.01) that only one triangle uses: 0 for a closed mesh.</summary>
    static int OpenEdges(PreparedShape s)
    {
        var id = new Dictionary<(int, int, int), int>();
        int Weld(Vector3 v) { var k = ((int)MathF.Round(v.X * 100), (int)MathF.Round(v.Y * 100), (int)MathF.Round(v.Z * 100)); if (!id.TryGetValue(k, out int i)) id[k] = i = id.Count; return i; }
        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t < s.Indices.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = Weld(s.Vertices[s.Indices[t + k]]), b = Weld(s.Vertices[s.Indices[t + (k + 1) % 3]]);
                var e = a < b ? (a, b) : (b, a);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        return edges.Values.Count(c => c == 1);
    }

    /// <summary>The buildings within a radius of a point, with how many of their parts have collision (<c>--near x,z,radius</c>).</summary>
    static IEnumerable<string> DescribeNear(ZoneGeometryGatherer gatherer, GameDatabase db, CollisionCache collision, ZoneCoordinate zone, float x, float z, float radius)
    {
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var min = new Vector2((float)ox, (float)oz);
        foreach (var (b, record, position, destroyed) in gatherer.NearBuildings(zone, min, min + new Vector2(WorldLayout.ZoneSize), ZoneGeometryGatherer.DefaultMargin))
        {
            if (Vector2.Distance(new(position.X, position.Z), new(x, z)) > radius) continue;
            var parts = WorldObjectLayout.Building(db, record, b.InstanceId, position, b.Rotation, new BuildingState(destroyed) { WithInterior = !destroyed });
            int withCollision = parts.Count(p => p.Source.GetPath("xml collision").Length > 0 && collision.Get(p.Source.GetPath("xml collision")) is not null);
            if (verbose) yield return "    fields " + string.Join(", ", record.Ints.Select(kv => $"{kv.Key}={kv.Value}").Concat(record.Floats.Select(kv => $"{kv.Key}={kv.Value}")).Concat(record.Bools.Where(kv => kv.Value).Select(kv => kv.Key)));
            yield return $"{record.Name} [{record.StringId}] key {b.Key} at {position.X:0},{position.Y:0},{position.Z:0} rotation {b.Rotation.X:0.###},{b.Rotation.Y:0.###},{b.Rotation.Z:0.###},{b.Rotation.W:0.###} mode {record.GetInt("path mode", (int)PathMode.Obstacle)} gateway {record.GetBool("is gateway")} parts {parts.Count} with collision {withCollision} interior masks {record.GetReferences("interior mask").Count()} destroyed {destroyed}";
            if (verbose)
                foreach (var p in parts)
                    yield return $"    part {p.Source.Name} [{p.Source.StringId}] floor {p.Source.GetInt("building floor")}{(p.Source.GetBool("is stairs") ? " stairs" : "")}{(p.Source.GetBool("passable") ? " passable" : "")}{(p.Source.GetBool("is unwalkable roof") ? " unwalkable-roof" : "")} mesh {p.MeshPath} collision {p.Source.GetPath("xml collision")} at {p.Transform.Translation.X:0},{p.Transform.Translation.Y:0},{p.Transform.Translation.Z:0}";
        }
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

    /// <summary>Sample spacing of <c>--compare</c> (units).</summary>
    const float CompareStep = 8;

    /// <summary>How far below our top layer at a point another of ours has to lie to count as a floor under a floor (<c>--compare</c>).</summary>
    const float ExtraLayer = 10;

    /// <summary>
    /// <c>--compare</c>: builds each zone alone (exterior, interiors joined) and samples it every <see cref="CompareStep"/> units against the original's exterior tile
    /// (<see cref="OriginalNavTiles"/>), in the zone's own box. Columns: points with kept polygons on both sides, only ours, only the original's (as a share of the
    /// original's), ours-only points that lie in a building's footprint (a solid or carver box), and points where ours has more layers than the original, one of them
    /// more than <see cref="ExtraLayer"/> below our top one (floors under floors: enclaves). Interiors are separate meshes in the original's tiles and are not read, so the columns count
    /// interior floors of ours as ours-only.
    /// </summary>
    static int Compare(GameInstall install, GameDatabase db, WorldLevelData levels, List<ZoneCoordinate> zones, NavBuildSettings settings)
    {
        using var gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install));
        Console.WriteLine($"{"zone",-7} {"kept",7} {"both",8} {"ours",7} {"orig",7} {"ours%",6} {"orig%",6} {"in bldg",8} {"extra",6}");
        foreach (var zone in zones)
        {
            var original = OriginalNavTiles.LoadExterior(install, zone);
            if (original is null) { Console.WriteLine($"{zone,-7} no original tile"); continue; }
            var g = gatherer.Gather(zone);
            var mesh = ZoneKeptClosure.ResolveAlone(NavMeshPipeline.BuildZone(gatherer, g, settings, out _, interiors: true), settings);
            var footprints = g.Solids.Select(s => (s.Min, s.Max)).Concat(g.Carvers.Where(c => c.Polygon.Length > 0).Select(c => (Min: c.Polygon.Aggregate(Vector2.Min), Max: c.Polygon.Aggregate(Vector2.Max))))
                .Where(b => b.Max.X - b.Min.X < 1000 && b.Max.Y - b.Min.Y < 1000).ToList();
            var originalIndex = new OriginalIndex(original);
            int both = 0, oursOnly = 0, originalOnly = 0, inBuilding = 0, extra = 0;
            var ours = new List<float>();
            var theirs = new List<float>();
            for (float x = g.ZoneMin.X + CompareStep / 2; x < g.ZoneMax.X; x += CompareStep)
                for (float z = g.ZoneMin.Y + CompareStep / 2; z < g.ZoneMax.Y; z += CompareStep)
                {
                    ours.Clear();
                    mesh.HeightsAt(x, z, ours);
                    originalIndex.HeightsAt(x, z, theirs);
                    if (ours.Count > 0 && theirs.Count > 0)
                    {
                        both++;
                        // More layers than the original, one well under our top one: a floor under a floor (the original's big polygons float over
                        // hollows, so its heights themselves are not compared).
                        if (ours.Count > theirs.Count && ours.Min() < ours.Max() - ExtraLayer)
                        {
                            extra++;
                            if (verbose && extra % 100 == 1) Console.WriteLine($"  extra layer at {x:0},{z:0}: ours {string.Join("/", ours.Select(y => y.ToString("0")))}, original {string.Join("/", theirs.Select(y => y.ToString("0")))}, terrain {gatherer.TerrainHeight(x, z):0}");
                        }
                    }
                    else if (ours.Count > 0)
                    {
                        oursOnly++;
                        foreach (var (min, max) in footprints)
                            if (x >= min.X && x <= max.X && z >= min.Y && z <= max.Y) { inBuilding++; break; }
                    }
                    else if (theirs.Count > 0) originalOnly++;
                }
            int originalTotal = Math.Max(1, both + originalOnly);
            Console.WriteLine($"{zone,-7} {mesh.KeptCount,7} {both,8} {oursOnly,7} {originalOnly,7} {100.0 * oursOnly / originalTotal,6:0.0} {100.0 * originalOnly / originalTotal,6:0.0} {inBuilding,8} {extra,6}");
        }
        return 0;
    }

    /// <summary>
    /// <c>--compare-interiors</c>: builds each zone (interiors joined) and scores the original tile's interior groups against it
    /// (<see cref="OriginalNavTiles.CoverInteriors"/>). Lists the groups under 90 % with their position and the uncovered faces' height range.
    /// </summary>
    static int CompareInteriors(GameInstall install, GameDatabase db, WorldLevelData levels, List<ZoneCoordinate> zones, NavBuildSettings settings, int around)
    {
        using var gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install));
        foreach (var zone in zones)
        {
            var groups = OriginalNavTiles.LoadInteriors(install, zone);
            if (groups.Count == 0) { Console.WriteLine($"{zone}: no interior groups"); continue; }
            var g = gatherer.Gather(zone);
            var full = NavMeshPipeline.BuildZone(gatherer, g, settings, out _, out var inner, interiors: true);
            ZoneNavMesh mesh;
            bool[]? resolved = null;
            if (around == 0) mesh = ZoneKeptClosure.ResolveAlone(full, settings);
            else
            {
                // With --around n the zone's neighbours are built and resolved with it, as when they are loaded together in play.
                var loaded = new Dictionary<ZoneCoordinate, ZoneNavMesh> { [zone] = full };
                for (int dz = -around; dz <= around; dz++)
                    for (int dx = -around; dx <= around; dx++)
                        if (dx != 0 || dz != 0)
                        {
                            var n = new ZoneCoordinate(zone.X + dx, zone.Y + dz);
                            loaded[n] = NavMeshPipeline.BuildZone(gatherer, gatherer.Gather(n), settings, out _, interiors: true);
                        }
                resolved = ZoneKeptClosure.Resolve(loaded, settings)[zone];
                mesh = full.WithKept(resolved).WithoutPruned();
            }
            var all = full.WithKept(Enumerable.Repeat(true, full.PolygonCount).ToArray());
            // Each of our interiors' hull, to name the building an original group lies in (the one holding most of its face centres).
            var sites = InteriorJoinSweep.Sites(gatherer, zone, settings);
            var coverage = OriginalNavTiles.CoverInteriors(groups, mesh, g.ZoneMin, g.ZoneMax);
            if (verbose) FurnitureOverOriginal(gatherer, zone, groups, settings);
            int faces = coverage.Sum(c => c.Faces), covered = coverage.Sum(c => c.Covered);
            Console.WriteLine($"{zone}: {groups.Count} interior groups, {covered}/{faces} faces covered ({(faces > 0 ? 100.0 * covered / faces : 100):0.0} %)");
            PrintJoins(InteriorJoinSweep.Compare(gatherer, zone, groups, sites, InteriorJoinSweep.Reached(zone, full, inner, settings, resolved), settings));
            foreach (var c in coverage)
                if (c.Faces > 0 && c.Covered < 0.9 * c.Faces)
                {
                    var site = InteriorJoinSweep.Owner(c.Group, sites);
                    string owner = site is not null ? $"{site.Key} {SiteName(site)}, {InteriorJoinSweep.Inside(c.Group, site)} faces in its hull" : "no interior of ours";
                    Console.WriteLine($"    {c.Group.Uid} at {c.Group.Origin.X:0},{c.Group.Origin.Z:0} ({owner}): {c.Covered}/{c.Faces} faces covered, missing y {c.MissingLow:0}..{c.MissingHigh:0}");
                    Console.WriteLine($"      uncovered by height over the group's lowest face: {DescribeUncovered(c.Group, mesh, all, g.ZoneMin, g.ZoneMax)}");
                }
        }
        return 0;
    }

    static string SiteName(InteriorJoinSweep.Site site) => site.Destroyed ? site.Name + " (destroyed)" : site.Name;

    /// <summary>
    /// <c>--compare-interiors</c> joins (<see cref="InteriorJoinSweep.Compare"/>): lists the mismatches, connected in the original but not reached in ours
    /// (sealed), and reached in ours but unconnected in the original.
    /// </summary>
    static void PrintJoins(List<InteriorJoinSweep.Join> joins)
    {
        var lines = new List<string>();
        foreach (var (group, site, original, join) in joins)
        {
            if (site is null) { lines.Add($"    {group.Uid} at {group.Origin.X:0},{group.Origin.Z:0}: no interior of ours ({(original ? "connected" : "unconnected")} in the original)"); continue; }
            if (original && !join) lines.Add($"    SEALED {group.Uid}: {site.Key} {SiteName(site)} is not reached from the street; the original connects it ({group.Connections} connections)");
            else if (!original && join) lines.Add($"    {group.Uid}: {site.Key} {SiteName(site)} is reached from the street; the original does not connect it");
        }
        Console.WriteLine($"  joins: {joins.Count} original groups, {joins.Count(j => j.Original)} connected; {joins.Count(j => j.Site is not null)} matched to ours, {joins.Count(j => j.Ours)} reached");
        foreach (var line in lines) Console.WriteLine(line);
    }

    /// <summary>
    /// An original interior group's uncovered faces (<see cref="OriginalNavTiles.Covers"/>) in 0..40, 40..70, 70..120 and 120+ unit bands over its lowest
    /// face, each split into pruned (a polygon of ours lies there that the seeds did not keep) and absent (no polygon of ours there even before pruning);
    /// with <c>--verbose</c> each face.
    /// </summary>
    static string DescribeUncovered(OriginalInterior group, ZoneNavMesh kept, ZoneNavMesh all, Vector2 zoneMin, Vector2 zoneMax)
    {
        float[] bands = [40, 70, 120, float.MaxValue];
        var absent = new int[bands.Length];
        var pruned = new int[bands.Length];
        var centres = group.Polygons.Where(p => p.Length >= 3).Select(p => p.Aggregate(Vector3.Zero, (a, i) => a + group.Vertices[i]) / p.Length)
            .Where(c => c.X >= zoneMin.X && c.X < zoneMax.X && c.Z >= zoneMin.Y && c.Z < zoneMax.Y).ToList();
        if (centres.Count == 0) return "";
        float floor = centres.Min(c => c.Y);
        var heights = new List<float>();
        foreach (var c in centres)
        {
            if (OriginalNavTiles.Covers(kept, c, heights)) continue;
            int band = Array.FindIndex(bands, b => c.Y - floor < b);
            bool isPruned = OriginalNavTiles.Covers(all, c, heights);
            (isPruned ? pruned : absent)[band]++;
            if (!verbose) continue;
            heights.Clear();
            all.HeightsAt(c.X, c.Z, heights);
            int nearest = kept.Nearest(c.X, c.Y, c.Z, 50, out float d);
            Console.WriteLine($"        {(isPruned ? "pruned" : "absent")} face at {c.X:0},{c.Y:0},{c.Z:0} (+{c.Y - floor:0}); ours there y {(heights.Count == 0 ? "none" : string.Join(" ", heights.Order().Select(y => $"{y:0}")))}; nearest kept {(nearest < 0 ? "none" : $"{d:0.0} away, y {kept.Centre(nearest).Y:0}")}");
        }
        string[] names = ["0..40", "40..70", "70..120", "120+"];
        return $"floor y {floor:0}; " + string.Join(", ", names.Select((n, i) => $"{n}: {pruned[i]} pruned {absent[i]} absent"));
    }

    /// <summary>
    /// <c>--compare-interiors --verbose</c>: per interior, how many of its furniture pieces (cutting shapes of other buildings) have their footprint's centre
    /// on an original interior face within 12 of their base. Near 0: the original's tile was built with this layout (it cuts the same holes); near all: with
    /// another or none (docs/game/pathfinding.md, "Interior coverage").
    /// </summary>
    static void FurnitureOverOriginal(ZoneGeometryGatherer gatherer, ZoneCoordinate zone, List<OriginalInterior> groups, NavBuildSettings settings)
    {
        var faces = new List<(Vector3[] Poly, Vector3 C)>();
        foreach (var gr in groups) foreach (var p in gr.Polygons) if (p.Length >= 3) { var pts = p.Select(i => gr.Vertices[i]).ToArray(); faces.Add((pts, pts.Aggregate(Vector3.Zero, (a, b) => a + b) / pts.Length)); }
        bool On(float x, float y, float z) => faces.Any(f => MathF.Abs(f.C.Y - y) <= 12 && InsidePoly(f.Poly, x, z));
        foreach (var site in gatherer.InteriorSites(zone))
        {
            var g = gatherer.GatherInterior(site, NavMeshPipeline.InteriorTileStep * settings.CellSize, trackParts: true);
            if (g is null) continue;
            int n = 0, on = 0;
            foreach (var (owner, part, path, first, last) in g.PartRanges)
            {
                if (owner == site.Record.Name || last <= first) continue;
                bool walk = false; float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue, y0 = float.MaxValue;
                for (int t = first; t < last; t++) { if (g.Areas[t] != NavArea.Null) walk = true; for (int k = 0; k < 3; k++) { int vi = g.Indices[t * 3 + k] * 3; x0 = MathF.Min(x0, g.Vertices[vi]); x1 = MathF.Max(x1, g.Vertices[vi]); y0 = MathF.Min(y0, g.Vertices[vi + 1]); z0 = MathF.Min(z0, g.Vertices[vi + 2]); z1 = MathF.Max(z1, g.Vertices[vi + 2]); } }
                if (walk) continue;
                n++;
                if (On((x0 + x1) / 2, y0, (z0 + z1) / 2)) on++;
            }
            if (n > 0) Console.WriteLine($"  furniture {site.Placement.Key} {site.Record.Name}: {on}/{n} furniture centres on an original interior face");
        }

        static bool InsidePoly(Vector3[] poly, float x, float z)
        {
            bool c = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if (poly[i].Z > z != poly[j].Z > z && x < (poly[j].X - poly[i].X) * (z - poly[i].Z) / (poly[j].Z - poly[i].Z) + poly[i].X) c = !c;
            return c;
        }
    }

    /// <summary>The original tile's polygons on a 64-unit grid, for point queries.</summary>
    sealed class OriginalIndex
    {
        const float Cell = 64;
        readonly OriginalNavMesh mesh;
        readonly Dictionary<(int, int), List<int>> cells = [];

        public OriginalIndex(OriginalNavMesh mesh)
        {
            this.mesh = mesh;
            for (int p = 0; p < mesh.Polygons.Length; p++)
            {
                var v = mesh.Polygons[p].Select(i => mesh.Vertices[i]).ToArray();
                for (int cx = (int)MathF.Floor(v.Min(a => a.X) / Cell); cx <= (int)MathF.Floor(v.Max(a => a.X) / Cell); cx++)
                    for (int cz = (int)MathF.Floor(v.Min(a => a.Z) / Cell); cz <= (int)MathF.Floor(v.Max(a => a.Z) / Cell); cz++)
                    {
                        if (!cells.TryGetValue((cx, cz), out var list)) cells[(cx, cz)] = list = [];
                        list.Add(p);
                    }
            }
        }

        /// <summary>The heights at (x, z) of every polygon containing the point (fan interpolation).</summary>
        public void HeightsAt(float x, float z, List<float> heights)
        {
            heights.Clear();
            if (!cells.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var list)) return;
            foreach (int p in list)
            {
                var poly = mesh.Polygons[p];
                var a = mesh.Vertices[poly[0]];
                for (int k = 1; k + 1 < poly.Length; k++)
                {
                    var b = mesh.Vertices[poly[k]];
                    var c = mesh.Vertices[poly[k + 1]];
                    if (NavGeometry.Barycentric(x, z, a, b, c, out float u, out float v, out float w) && u >= -1e-4f && v >= -1e-4f && w >= -1e-4f)
                    {
                        heights.Add(u * a.Y + v * b.Y + w * c.Y);
                        break;
                    }
                }
            }
        }
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
        if (g.PartRanges.Count > 0)
        {
            // One object per part (its name has no spaces), faces in triangle order, the material naming the area.
            int next = 0;
            byte? area = null;
            for (int t = 0; t < g.TriangleCount; t++)
            {
                while (next < g.PartRanges.Count && g.PartRanges[next].First <= t)
                {
                    var r = g.PartRanges[next++];
                    w.WriteLine($"o {next - 1}:{r.Part.Replace(' ', '_')}");
                    area = null;
                }
                if (area != g.Areas[t]) { area = g.Areas[t]; w.WriteLine($"usemtl {(area == NavArea.Null ? "null" : area == NavArea.Ground ? "ground" : "water")}"); }
                w.WriteLine($"f {g.Indices[t * 3] + 1} {g.Indices[t * 3 + 1] + 1} {g.Indices[t * 3 + 2] + 1}");
            }
        }
        else
            foreach (var (name, area) in new[] { ("null", NavArea.Null), ("ground", NavArea.Ground), ("water", NavArea.Water), ("water-waist", NavArea.WaterWaist), ("water-deep", NavArea.WaterDeep) })
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
                NavArea.Water or NavArea.WaterWaist or NavArea.WaterDeep => ((byte)70, (byte)110, (byte)200),
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

    static readonly (string Name, byte Area)[] AreaNames = [("ground", NavArea.Ground), ("water", NavArea.Water), ("water-waist", NavArea.WaterWaist), ("water-deep", NavArea.WaterDeep), ("door", NavArea.Door)];

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
            if (g is not null)
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
                var colour = !m.Kept[p] ? ((byte)200, (byte)60, (byte)60) : m.Areas[p] switch
                {
                    NavArea.Water or NavArea.WaterWaist or NavArea.WaterDeep => ((byte)80, (byte)130, (byte)230),
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
