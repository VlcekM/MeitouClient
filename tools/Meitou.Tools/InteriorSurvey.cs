using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;

/// <summary>
/// <c>meitou-tools navmesh --survey-interiors zones|towns [--name text] [--png dir] [--section x,z] [--border]</c>: for every building interior of
/// ours, samples the zone's kept polygons and the original tile's interior groups and exterior over its hull every <see cref="Step"/> units and
/// counts the area both have, only ours has (by height over the hull's base) and only the original has; sums it by building type and lists each
/// placement whose type contains <c>--name</c>. Each interior is built with the layout the tile was built with (<see cref="ChooseLayouts"/>).
/// <c>--png</c> writes per listed placement a top-down image of each side coloured by height over the base, a difference image, both meshes as OBJ,
/// and two vertical sections through the hull's middle or <c>--section</c>. Hulls over the zone's border are skipped unless <c>--border</c> (their
/// upper storeys may be reached only through the neighbour zone).
/// Output goes where the user says, never into the repository.
/// </summary>
static class InteriorSurvey
{
    #region Survey

    /// <summary>
    /// Per building type: the placements surveyed, the sampled area each side has (both, ours only, ours above the base, the original only) and the
    /// placement with the most ours-only area.
    /// </summary>
    sealed class Tally
    {
        /// <summary>
        /// The placements surveyed and the sample counts: on both meshes, ours only, ours only above the base (upper storeys), the original's only.
        /// </summary>
        public int Placements, Both, Ours, OursUpper, Original;

        /// <summary>
        /// The placement (zone and key, with its area) with the most ours-only samples.
        /// </summary>
        public string Worst = "";

        /// <summary>
        /// That placement's ours-only sample count.
        /// </summary>
        public int WorstOurs;
    }

    /// <summary>
    /// Whether hulls over the zone's border are surveyed too (<c>--border</c>).
    /// </summary>
    static bool s_border;

    /// <summary>
    /// The level placements by building key, for their interior hashes.
    /// </summary>
    static Dictionary<string, BuildingPlacement> s_placements = [];

    /// <summary>
    /// The standing placements skipped because the tile has them ruined (<see cref="RebuiltAtLoad"/>).
    /// </summary>
    static readonly List<string> Rebuilt = [];

    /// <summary>
    /// Surveys the zones: per zone each standing placement with an interior is compared (<see cref="SurveyZone"/>), then the totals per building type
    /// are printed. <paramref name="name"/> limits it to one building, <paramref name="png"/> writes pictures for it. Returns the exit code.
    /// </summary>
    public static int Run(GameInstall install, GameDatabase db, WorldLevelData levels, List<ZoneCoordinate> zones, NavBuildSettings settings,
            string? name, string? png,
        Vector2? sectionAt = null, bool overBorder = false)
    {
        s_section = sectionAt;
        s_border = overBorder;
        s_placements = levels.Buildings().GroupBy(b => b.Key).ToDictionary(g => g.Key, g => g.First());
        using var gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install));
        var tallies = new Dictionary<string, Tally>();
        foreach (var zone in zones) SurveyZone(gatherer, install, zone, settings, tallies, name, png);
        Console.WriteLine($"{"building",-40} {"n",4} {"both",8} {"ours",8} {"upper",8} {"orig",8} {"ours%",6} {"orig%",6}  worst");
        foreach (var (type, t) in tallies.OrderByDescending(t => t.Value.Ours))
        {
            float total = Math.Max(1, t.Both + t.Original);
            Console.WriteLine($"{type,-40} {t.Placements,4} {t.Both * Step * Step,8:0} "
                + $"{t.Ours * Step * Step,8:0} {t.OursUpper * Step * Step,8:0} {t.Original * Step * Step,8:0} "
                + $"{100 * t.Ours / total,6:0.0} {100 * t.Original / total,6:0.0}  {t.Worst}");
        }
        if (Rebuilt.Count > 0)
            Console.WriteLine($"skipped {Rebuilt.Count} standing placements the tile has as ruins (rebuilt at load): {string.Join(" ", Rebuilt)}");
        return 0;
    }

    /// <summary>
    /// Surveys one zone: builds our navmesh and reads the original tile, then samples each standing interior hull of the zone and adds it to its
    /// type's tally.
    /// </summary>
    static void SurveyZone(
        ZoneGeometryGatherer gatherer,
        GameInstall install,
        ZoneCoordinate zone,
        NavBuildSettings settings,
        Dictionary<string, Tally> tallies,
        string? name,
        string? png)
    {
        var groups = OriginalNavTiles.LoadInteriors(install, zone);
        if (groups.Count == 0) return;
        var sites = InteriorJoinSweep.Sites(gatherer, zone, settings);
        var owners = groups.Select(gr => (Group: gr, Site: InteriorJoinSweep.Owner(gr, sites))).ToList();
        var layouts = ChooseLayouts(gatherer, zone, owners);
        var g = gatherer.Gather(zone);
        var full = NavMeshPipeline.BuildZone(gatherer, g, settings, out _, out var inner, interiors: true);
        var keep = ZoneKeptClosure.Resolve(new Dictionary<ZoneCoordinate, ZoneNavMesh> { [zone] = full }, settings)[zone];
        var exterior = OriginalNavTiles.LoadExterior(install, zone);
        var everything = full.WithKept(keep);
        int offset = full.PolygonCount - inner.Sum(m => m.Mesh.PolygonCount);
        foreach (var interior in inner)
        {
            var mesh = interior.Mesh.WithKept(keep[offset..(offset + interior.Mesh.PolygonCount)]);
            offset += interior.Mesh.PolygonCount;
            var site = sites.FirstOrDefault(s => s.Key == interior.Key);
            if (site is null || site.Destroyed || !s_border && CrossesBorder(site, zone)) continue;
            if (RebuiltAtLoad(site, groups))
            {
                Rebuilt.Add(site.Key);
                continue;
            }
            var faces = owners.Where(o => o.Site == site)
                .SelectMany(o => o.Group.Polygons.Where(p => p.Length >= 3).Select(p => p.Select(i => o.Group.Vertices[i]).ToArray()))
                .ToList();
            if (faces.Count == 0) continue;
            // Both sides' whole walkable ground over the hull, exterior included: the original carves its exterior with the hull's convex volume,
            // narrower near the ground than the footprint ours cuts straight down, so a ledge round the walls is its exterior's and our interior's.
            var ground = faces.Concat(ExteriorFaces(exterior, site)).ToList();
            var t = tallies.TryGetValue(site.Name, out var found) ? found : tallies[site.Name] = new Tally();
            var (both, ours, upper, original) = Sample(site, everything, ground);
            t.Placements++;
            t.Both += both;
            t.Ours += ours;
            t.OursUpper += upper;
            t.Original += original;
            if (ours > t.WorstOurs) (t.WorstOurs, t.Worst) = (ours, $"{zone}:{site.Key} {ours * Step * Step:0}");
            if (name is null || !site.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            var hull = site.Hull.Polygon;
            Console.Write($"  {zone}:{site.Key} {site.Name} at {hull.Average(p => p.X):0},{hull.Average(p => p.Y):0}");
            string layoutName = layouts.TryGetValue(site.Key, out var layout) ? layout is "" ? "no layout" : layout : "its layout";
            Console.WriteLine($" ({layoutName}): both {both * Step * Step:0}, ours {ours * Step * Step:0} ({upper * Step * Step:0} upper), "
                + $"original {original * Step * Step:0}");
            if (png is not null) Pictures(gatherer, zone, settings, site, everything, ground, png);
        }
    }

    /// <summary>
    /// The interior layout of each building as the original's tile was built with it: homes reroll their layouts per game, so the tile's furniture
    /// holes can be another layout's. Per site, the layout whose objects stand most often over a hole in the original's faces (no face within reach
    /// of the object's base), if that is at least half of them; else none (the tile has no furniture holes). Set as the gatherer's override.
    /// </summary>
    static Dictionary<string, string> ChooseLayouts(ZoneGeometryGatherer gatherer, ZoneCoordinate zone,
            List<(OriginalInterior Group, InteriorJoinSweep.Site? Site)> owners)
    {
        var chosen = new Dictionary<string, string>();
        foreach (var site in gatherer.InteriorSites(zone))
        {
            var faces = owners
                .Where(o => o.Site?.Key == site.Placement.Key)
                .SelectMany(o => o.Group.Polygons.Where(p => p.Length >= 3).Select(p => p.Select(i => o.Group.Vertices[i]).ToArray()))
                .ToList();
            if (faces.Count == 0) continue;
            string best = "";
            float bestScore = 0.5f;
            foreach (var (name, objects) in gatherer.InteriorLayouts(site))
            {
                if (objects.Count == 0) continue;
                float score = objects.Count(o => !faces.Any(f => HeightOn(f, o.X, o.Z) is { } y && MathF.Abs(y - o.Y) <= 6)) / (float)objects.Count;
                if (score > bestScore) (best, bestScore) = (name, score);
            }
            chosen[site.Placement.Key] = best;
        }
        foreach (var (key, name) in chosen) gatherer.InteriorLayoutOverrides[key] = name;
        return chosen;
    }

    /// <summary>
    /// Whether the tile holds the standing building's interior as a ruin (a group under its destroyed interior hash): the original finds no group for
    /// it at load and builds it standing (docs/game/pathfinding.md, Lost Town), so the tile's faces are not what the game walks there.
    /// </summary>
    static bool RebuiltAtLoad(InteriorJoinSweep.Site site, List<OriginalInterior> groups)
    {
        if (!s_placements.TryGetValue(site.Key, out var placement)) return false;
        uint ruin = OriginalNavTiles.InteriorHash(placement, destroyed: true);
        return groups.Any(g => g.Hash == ruin);
    }

    /// <summary>
    /// Whether the hull reaches over the zone's border: the part beyond is the neighbour zone's interior, joined through its kept border, which one
    /// zone alone does not see (a stair over the line leaves the storeys above pruned).
    /// </summary>
    static bool CrossesBorder(InteriorJoinSweep.Site site, ZoneCoordinate zone)
    {
        var (x, z) = WorldLayout.ZoneOrigin(zone);
        float size = (float)WorldLayout.ZoneSize;
        return site.Hull.Polygon.Any(p => p.X < x || p.X > x + size || p.Y < z || p.Y > z + size);
    }

    #endregion

    #region Pictures and sampling

    /// <summary>
    /// The sampling grid's spacing over a hull, in world units.
    /// </summary>
    const float Step = 2;

    /// <summary>
    /// Heights within this of each other are the same surface (as <see cref="OriginalNavTiles.CoverInteriors"/>).
    /// </summary>
    const float SameSurface = 12;

    /// <summary>
    /// Ours-only area this far over the base counts as upper storeys.
    /// </summary>
    const float Upper = 20;

    /// <summary>
    /// Where <c>--section x,z</c> puts the vertical sections; the hull's middle when null.
    /// </summary>
    static Vector2? s_section;

    /// <summary>
    /// Writes the pictures of one hull: the difference map, the two vertical sections and the polygons as OBJ files.
    /// </summary>
    static void Pictures(ZoneGeometryGatherer gatherer, ZoneCoordinate zone, NavBuildSettings settings, InteriorJoinSweep.Site site, ZoneNavMesh mesh,
            List<Vector3[]> faces, string png)
    {
        string stem = Path.Combine(png, $"{zone.X}.{zone.Y}-{Safe(site.Key)}");
        Draw(site, mesh, faces, stem);
        Difference(site, mesh, faces, stem);
        WriteObj(stem + ".orig.obj", faces);
        WriteObj(stem + ".ours.obj",
                Enumerable.Range(0,
                        mesh.PolygonCount).Where(p => mesh.Kept[p]).Select(p => mesh.Polygons[p].Select(i => mesh.Vertices[i]).ToArray()));
        var source = gatherer.InteriorSites(zone).First(s => s.Placement.Key == site.Key);
        if (gatherer.GatherInterior(source, NavMeshPipeline.InteriorTileStep * settings.CellSize, agentHeight: settings.AgentHeight) is { } geometry)
            Sections(site, geometry, mesh, faces, stem);
    }

    /// <summary>
    /// The original exterior's polygons whose box meets the hull's.
    /// </summary>
    static IEnumerable<Vector3[]> ExteriorFaces(OriginalNavMesh? exterior, InteriorJoinSweep.Site site)
    {
        if (exterior is null) yield break;
        var hull = site.Hull.Polygon;
        float x0 = hull.Min(p => p.X), x1 = hull.Max(p => p.X), z0 = hull.Min(p => p.Y), z1 = hull.Max(p => p.Y);
        foreach (var poly in exterior.Polygons)
        {
            if (poly.Length < 3) continue;
            var f = poly.Select(i => exterior.Vertices[i]).ToArray();
            if (f.Max(v => v.X) >= x0 && f.Min(v => v.X) <= x1 && f.Max(v => v.Z) >= z0 && f.Min(v => v.Z) <= z1)
                yield return f;
        }
    }

    /// <summary>
    /// Writes <c>path.diff.png</c>, one 4-pixel square a sample from above: red where only ours walks (brighter higher), blue where only the original
    /// does, grey where both do; a mismatch outdraws a match.
    /// </summary>
    static void Difference(InteriorJoinSweep.Site site, ZoneNavMesh mesh, List<Vector3[]> faces, string path)
    {
        var hull = site.Hull.Polygon;
        float x0 = hull.Min(p => p.X), z0 = hull.Min(p => p.Y);
        int w = (int)MathF.Ceiling((hull.Max(p => p.X) - x0) / Step), h = (int)MathF.Ceiling((hull.Max(p => p.Y) - z0) / Step);
        var rank = new int[w * h];
        var rgb = new byte[w * h * 48];
        float floorY = site.Hull.YMin, span = Math.Max(1, site.Hull.YMax - floorY);
        Sample(site, mesh, faces, (x, z, kind, y) =>
        {
            int cx = Math.Clamp((int)((x - x0) / Step), 0, w - 1), cz = Math.Clamp((int)((z - z0) / Step), 0, h - 1);
            int r = kind == 'b' ? 1 : 2 + (int)(Math.Clamp((y - floorY) / span, 0, 1) * 100);
            if (r <= rank[cx + cz * w]) return;
            rank[cx + cz * w] = r;
            byte v = (byte)(90 + Math.Min(165, (r - 2) * 165 / 100));
            var c = kind == 'b' ? ((byte)70, (byte)70, (byte)70) : kind == 'o' ? (v, (byte)30, (byte)30) : ((byte)30, (byte)60, v);
            for (int k = 0; k < 16; k++)
            {
                int i = ((cz * 4 + k / 4) * w * 4 + cx * 4 + k % 4) * 3;
                (rgb[i], rgb[i + 1], rgb[i + 2]) = c;
            }
        });
        WorldPng.Write(path + ".diff.png", w * 4, h * 4, rgb);
    }

    /// <summary>
    /// Writes polygons as an OBJ file, each with its own vertices.
    /// </summary>
    static void WriteObj(string path, IEnumerable<Vector3[]> polygons)
    {
        using var w = new StreamWriter(path);
        int n = 0;
        foreach (var f in polygons)
        {
            foreach (var v in f)
                w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"v {v.X:0.##} {v.Y:0.##} {v.Z:0.##}"));
            w.WriteLine("f " + string.Join(" ", Enumerable.Range(n + 1, f.Length)));
            n += f.Length;
        }
    }

    /// <summary>
    /// <paramref name="key"/> with every character but letters, digits and '-' made '_' (a file name).
    /// </summary>
    static string Safe(string key) => string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));

    /// <summary>
    /// Calls <paramref name="seen"/> for each sample point's surfaces: 'b' both, 'o' only ours, 't' only the original's, with its height.
    /// </summary>
    static (int Both, int Ours, int Upper, int Original) Sample(InteriorJoinSweep.Site site, ZoneNavMesh mesh, List<Vector3[]> faces,
        Action<float, float, char, float>? seen = null)
    {
        var hull = site.Hull.Polygon;
        float x0 = hull.Min(p => p.X), x1 = hull.Max(p => p.X), z0 = hull.Min(p => p.Y), z1 = hull.Max(p => p.Y);
        float floorY = site.Hull.YMin;
        int both = 0, ours = 0, upper = 0, original = 0;
        var polys = new List<int>();
        var theirs = new List<float>();
        for (float z = z0 + Step / 2; z < z1; z += Step)
        {
            for (float x = x0 + Step / 2; x < x1; x += Step)
            {
                if (!NavGeometry.InsideConvex(hull, new Vector2(x, z), 0)) continue;
                polys.Clear();
                mesh.PolygonsAt(x, z, polys);
                theirs.Clear();
                foreach (var f in faces) if (HeightOn(f, x, z) is { } y) theirs.Add(y);
                foreach (int p in polys)
                {
                    float y = mesh.HeightAt(p, x, z);
                    if (theirs.Any(o => MathF.Abs(o - y) <= SameSurface))
                    {
                        both++;
                        seen?.Invoke(x, z, 'b', y);
                    }
                    else
                    {
                        ours++;
                        seen?.Invoke(x, z, 'o', y);
                        if (y - floorY > Upper) upper++;
                    }
                }
                foreach (float o in theirs)
                {
                    if (!polys.Any(p => MathF.Abs(mesh.HeightAt(p, x, z) - o) <= SameSurface))
                    {
                        original++;
                        seen?.Invoke(x, z, 't', o);
                    }
                }
            }
        }
        return (both, ours, upper, original);
    }

    /// <summary>
    /// The height of a convex face (a fan from its first corner) at (x, z), or null when the point is outside it.
    /// </summary>
    static float? HeightOn(Vector3[] f, float x, float z)
    {
        for (int k = 1; k + 1 < f.Length; k++)
        {
            if (NavGeometry.Barycentric(x, z, f[0], f[k], f[k + 1], out float u, out float v, out float w) && u >= -1e-4f && v >= -1e-4f
                    && w >= -1e-4f)
                return u * f[0].Y + v * f[k].Y + w * f[k + 1].Y;
        }
        return null;
    }

    /// <summary>
    /// Writes <c>path.x.png</c> and <c>path.z.png</c>: vertical sections through the hull's middle across X and across Z at 4 pixels a unit, the
    /// gathered triangles (walkable green, cutting grey), the original's faces (magenta) and our kept polygons (cyan, a unit higher).
    /// </summary>
    static void Sections(InteriorJoinSweep.Site site, ZoneGeometry g, ZoneNavMesh mesh, List<Vector3[]> faces, string path)
    {
        var hull = site.Hull.Polygon;
        float cx = s_section?.X ?? (hull.Min(p => p.X) + hull.Max(p => p.X)) / 2, cz = s_section?.Y ?? (hull.Min(p => p.Y) + hull.Max(p => p.Y)) / 2;
        foreach (bool alongX in new[] { true, false })
        {
            float lo = alongX ? hull.Min(p => p.X) : hull.Min(p => p.Y), hi = alongX ? hull.Max(p => p.X) : hull.Max(p => p.Y);
            var r = new TopDownRenderer(lo - 4, -site.Hull.YMax - 4, hi + 4, -site.Hull.YMin + 4, 0.25f, (20, 20, 24));
            // A section of the plane z = cz (alongX) or x = cx, drawn as (along, -height).
            void Cut(IReadOnlyList<Vector3> poly, (byte, byte, byte) colour, float lift)
            {
                var hits = new List<Vector3>();
                for (int k = 0; k < poly.Count; k++)
                {
                    var a = poly[k];
                    var b = poly[(k + 1) % poly.Count];
                    float da = alongX ? a.Z - cz : a.X - cx, db = alongX ? b.Z - cz : b.X - cx;
                    if (da * db > 0 || da == db) continue;
                    var q = Vector3.Lerp(a, b, da / (da - db));
                    hits.Add(new Vector3(alongX ? q.X : q.Z, 0, -q.Y - lift));
                }
                if (hits.Count >= 2) r.Line(hits[0], hits[1], colour);
            }
            var v = g.Vertices;
            for (int t = 0; t < g.TriangleCount; t++)
            {
                Vector3 P(int k) => new(v[g.Indices[t * 3 + k] * 3], v[g.Indices[t * 3 + k] * 3 + 1], v[g.Indices[t * 3 + k] * 3 + 2]);
                Cut([P(0), P(1), P(2)], g.Areas[t] == NavArea.Null ? ((byte)110, (byte)110, (byte)110) : ((byte)60, (byte)200, (byte)60), 0);
            }
            foreach (var f in faces) Cut(f, (230, 60, 230), -0.5f);
            for (int p = 0; p < mesh.PolygonCount; p++)
                if (mesh.Kept[p]) Cut(mesh.Polygons[p].Select(i => mesh.Vertices[i]).ToArray(), (60, 220, 255), 1);
            WorldPng.Write(path + (alongX ? ".x.png" : ".z.png"), r.Width, r.Height, r.Rgb);
        }
    }

    /// <summary>
    /// Writes <c>path.orig.png</c> and <c>path.ours.png</c>: the hull's box from above at 4 pixels a unit, coloured by height over the hull's base.
    /// </summary>
    static void Draw(InteriorJoinSweep.Site site, ZoneNavMesh mesh, List<Vector3[]> faces, string path)
    {
        var hull = site.Hull.Polygon;
        float x0 = hull.Min(p => p.X) - 4, x1 = hull.Max(p => p.X) + 4, z0 = hull.Min(p => p.Y) - 4, z1 = hull.Max(p => p.Y) + 4;
        float floorY = site.Hull.YMin, span = Math.Max(1, site.Hull.YMax - site.Hull.YMin);
        (byte, byte, byte) Colour(float y)
        {
            float t = Math.Clamp((y - floorY) / span, 0, 1);
            return ((byte)(40 + 215 * t), (byte)(200 - 120 * MathF.Abs(t - 0.5f) * 2), (byte)(255 - 215 * t));
        }
        var original = new TopDownRenderer(x0, z0, x1, z1, 0.25f, (20, 20, 24));
        foreach (var f in faces)
        {
            float y = f.Average(v => v.Y);
            for (int k = 1; k + 1 < f.Length; k++) original.Triangle(f[0], f[k], f[k + 1], Colour(y));
            for (int k = 0; k < f.Length; k++)
                original.Line(f[k] + Vector3.UnitY * 50, f[(k + 1) % f.Length] + Vector3.UnitY * 50, (0, 0, 0));
        }
        var ours = new TopDownRenderer(x0, z0, x1, z1, 0.25f, (20, 20, 24));
        for (int p = 0; p < mesh.PolygonCount; p++)
        {
            if (!mesh.Kept[p]) continue;
            var poly = mesh.Polygons[p].Select(i => mesh.Vertices[i]).ToArray();
            var c = Colour(mesh.Centre(p).Y);
            for (int k = 1; k + 1 < poly.Length; k++) ours.Triangle(poly[0], poly[k], poly[k + 1], c);
            for (int k = 0; k < poly.Length; k++)
                ours.Line(poly[k] + Vector3.UnitY * 50, poly[(k + 1) % poly.Length] + Vector3.UnitY * 50, (0, 0, 0));
        }
        foreach (var r in new[] { original, ours })
        {
            for (int k = 0; k < hull.Length; k++)
            {
                r.Line(new Vector3(hull[k].X, 1e5f, hull[k].Y), new Vector3(hull[(k + 1) % hull.Length].X, 1e5f, hull[(k + 1) % hull.Length].Y),
                        (255, 255, 255));
            }
        }
        WorldPng.Write(path + ".orig.png", original.Width, original.Height, original.Rgb);
        WorldPng.Write(path + ".ours.png", ours.Width, ours.Height, ours.Rgb);
    }

    #endregion
}
