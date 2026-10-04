using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

/// <summary>
/// Reads the world files (heightmap, zone and level files, legacy height tiles) and checks the facts in
/// docs/formats/terrain.md and docs/formats/zones.md; <see cref="RenderMap"/> draws a top-down map.
/// </summary>
static class WorldSurvey
{
    public static int Run(GameInstall install)
    {
        int problems = 0;
        problems += SurveyLevelFiles(install);
        Console.WriteLine();
        var world = WorldLevelData.Load(install);
        problems += SurveyPlacements(install, world);
        Console.WriteLine();
        problems += SurveyHeightmap(install, world);
        Console.WriteLine();
        problems += SurveyLegacyTiles(install);
        Console.WriteLine();
        problems += SurveyMapFeatures(install);
        Console.WriteLine();
        SurveyOtherFiles(install);
        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "No problems." : $"{problems} problem(s).");
        return problems == 0 ? 0 : 1;
    }

    static int SurveyLevelFiles(GameInstall install)
    {
        var files = Directory.EnumerateFiles(install.DataDirectory, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".zone", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".level", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList();
        var byDir = new SortedDictionary<string, (int Files, int Records, int Instances, int Trailers, string Formats)>(StringComparer.Ordinal);
        var types = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int failures = 0, roundTripped = 0, notRoundTripped = 0, legacy = 0;
        foreach (var path in files)
        {
            var dir = Path.GetRelativePath(install.DataDirectory, Path.GetDirectoryName(path)!).Replace('\\', '/');
            try
            {
                var bytes = File.ReadAllBytes(path);
                var file = LevelFile.Read(bytes);
                if (file.IsLegacy) legacy++;
                else if (file.ToBytes().AsSpan().SequenceEqual(bytes)) roundTripped++;
                else { notRoundTripped++; Console.WriteLine($"  does not round-trip: {path}"); }
                var e = byDir.GetValueOrDefault(dir);
                var formats = e.Formats is null ? $"{file.Format}" : e.Formats.Split(',').Contains($"{file.Format}") ? e.Formats : $"{e.Formats},{file.Format}";
                byDir[dir] = (e.Files + 1, e.Records + file.Data.Records.Count, e.Instances + file.Data.Records.Sum(r => r.Instances.Count), e.Trailers + (file.Trailer is null ? 0 : 1), formats);
                foreach (var r in file.Data.Records)
                {
                    var key = Enum.IsDefined((FcsRecordType)r.Type) ? ((FcsRecordType)r.Type).ToString() : r.Type.ToString(CultureInfo.InvariantCulture);
                    types[key] = types.GetValueOrDefault(key) + 1;
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException)
            {
                failures++;
                Console.WriteLine($"  FAILED {path}: {ex.Message}");
            }
        }
        Console.WriteLine($"Level files (.zone, .level): {files.Count}, {failures} failed; {roundTripped} round-trip byte for byte, {notRoundTripped} don't, {legacy} legacy (not writable)");
        Console.WriteLine($"  {"folder",-34} {"files",6} {"records",8} {"instances",10} {"trailer",8}  formats");
        foreach (var (dir, e) in byDir)
            Console.WriteLine($"  {dir,-34} {e.Files,6} {e.Records,8} {e.Instances,10} {e.Trailers,8}  {e.Formats}");
        Console.WriteLine("  record types:");
        foreach (var (type, n) in types.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"    {type,-32} {n,7}");
        return failures + notRoundTripped;
    }

    static int SurveyPlacements(GameInstall install, WorldLevelData world)
    {
        int problems = 0;
        Console.WriteLine($"World level data: layers {string.Join(", ", world.Layers.Select(l => $"{l} ({l.FileCount} files)"))}");
        var zones = world.Zones.Keys.ToList();
        Console.WriteLine($"  zones with data: {zones.Count} of {WorldLayout.ZoneCount * WorldLayout.ZoneCount}; X {zones.Min(z => z.X)}..{zones.Max(z => z.X)}, Y {zones.Min(z => z.Y)}..{zones.Max(z => z.Y)}; outside grid {zones.Count(z => !z.IsInsideGrid)}");
        Console.WriteLine($"  merge issues: {world.Issues.Count} ({string.Join(", ", world.Issues.GroupBy(i => i.Kind).Select(g => $"{g.Key} {g.Count()}"))})");

        var entries = world.BuildingListEntries().ToList();
        var local = entries.Where(e => e.TargetsZoneRecord).ToList();
        Console.WriteLine($"  building list entries: {entries.Count}; targeting a record of the zone file {local.Count} " +
            $"({string.Join(", ", local.GroupBy(e => world.Zones[e.Zone].Find(e.BuildingId)!.Type).Select(g => $"{g.Key} {g.Count()}"))}), " +
            $"of those at the origin {local.Count(e => e.Position == Vector3.Zero)}");
        var buildings = world.Buildings().ToList();
        int inZone = buildings.Count(b => WorldLayout.ZoneOf(b.Position.X, b.Position.Z) == b.Zone);
        int atOrigin = buildings.Count(b => b.Position == Vector3.Zero);
        Console.WriteLine($"  buildings placed: {buildings.Count}; inside their file's zone {inZone}; at the origin {atOrigin}; elsewhere {buildings.Count - inZone - atOrigin}");
        foreach (var b in buildings.Where(b => b.Position != Vector3.Zero && WorldLayout.ZoneOf(b.Position.X, b.Position.Z) != b.Zone).Take(5))
            Console.WriteLine($"    {b.Zone} {b.InstanceId} -> {b.BuildingId} at {Fmt(b.Position)}");
        if (buildings.Count - inZone - atOrigin > 0) problems++;

        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        problems += Resolve("building targets", buildings.Select(b => b.BuildingId), db, FcsRecordType.BUILDING);
        var towns = world.Towns().ToList();
        problems += Resolve("town targets", towns.Select(t => t.TownId), db, FcsRecordType.TOWN);
        var factions = world.Zones.Values.SelectMany(z => z.OfType(FcsRecordType.GAMESTATE_BUILDING)).Select(s => s.GetString("owner faction ID")).Where(s => s.Length > 0);
        Resolve("building owner factions", factions, db, FcsRecordType.FACTION);

        var townZones = world.TownZones();
        int matched = 0, mismatched = 0, noState = 0, noZones = 0;
        foreach (var t in towns)
        {
            if (!townZones.TryGetValue(t.InstanceId, out var list)) { noState++; continue; }
            if (list.Count == 0) { noZones++; continue; }
            var zone = WorldLayout.ZoneOf(t.Position.X, t.Position.Z);
            if (list.Contains(zone)) { matched++; continue; }
            if (mismatched++ < 5)
                Console.WriteLine($"    town {t.InstanceId} ({t.TownId}) at {Fmt(t.Position)} is in zone {zone}, its state lists {string.Join(" ", list)}" +
                    $" (nearest {list.Min(z => Math.Max(Math.Abs(z.X - zone.X), Math.Abs(z.Y - zone.Y)))} zones away)");
        }
        Console.WriteLine($"  towns placed: {towns.Count}; position inside a zone of its GAMESTATE_TOWN {matched}, outside {mismatched}, state lists no zones {noZones}, no town state {noState}");
        var roads = world.Roads().ToList();
        Console.WriteLine($"  roads: {roads.Count} segments, {roads.Sum(r => r.Points.Count)} points, width {roads.Min(r => r.Width):0.#}..{roads.Max(r => r.Width):0.#}");
        return problems;
    }

    static int Resolve(string what, IEnumerable<string> ids, GameDatabase db, FcsRecordType expected)
    {
        var all = ids.ToList();
        var missing = all.Where(id => db.Find(id) is null).ToList();
        var wrongType = all.Where(id => db.Find(id) is { } r && r.Type != expected).ToList();
        Console.WriteLine($"  {what}: {all.Count} ({all.Distinct().Count()} distinct); not in the game data {missing.Count} ({missing.Distinct().Count()} distinct); not {expected} {wrongType.Count}");
        foreach (var id in missing.Distinct().Take(5)) Console.WriteLine($"    missing: {id}");
        foreach (var id in wrongType.Distinct().Take(5)) Console.WriteLine($"    {id} is {db.Find(id)!.Type}");
        return wrongType.Count; // ids missing from the base game data are a data fact (reported above), not a reader problem
    }

    static int SurveyHeightmap(GameInstall install, WorldLevelData world)
    {
        int problems = 0;
        using var map = TerrainHeightmap.Open(install);
        var img = map.Image;
        Console.WriteLine($"Heightmap {TerrainHeightmap.RelativePath}: {img.Width}x{img.Height}, {img.BitsPerSample}-bit, {(img.LittleEndian ? "little" : "big")}-endian, {img.StripCount} strip(s), data at {img.DataOffset}");
        if (img.Width != WorldLayout.HeightmapSize) { problems++; Console.WriteLine($"  expected {WorldLayout.HeightmapSize} samples per side"); }

        var hist = new long[65536];
        map.ForEachRow((_, row) => { foreach (var v in row) hist[v]++; });
        long total = (long)img.Width * img.Height;
        int Percentile(double p)
        {
            long target = (long)(total * p), acc = 0;
            for (int v = 0; v < hist.Length; v++) { acc += hist[v]; if (acc > target) return v; }
            return 65535;
        }
        int min = Array.FindIndex(hist, h => h > 0), max = Array.FindLastIndex(hist, h => h > 0);
        Console.WriteLine($"  raw min {min}, max {max}, zero samples {hist[0] * 100.0 / total:F1}%, distinct values {hist.Count(h => h > 0)}");
        Console.WriteLine($"  raw percentiles: p25 {Percentile(0.25)}, p50 {Percentile(0.5)}, p75 {Percentile(0.75)}, p99 {Percentile(0.99)}, p99.9 {Percentile(0.999)}");
        Console.WriteLine($"  world heights (x {WorldLayout.MaxHeight}/65535): 0..{WorldLayout.RawToHeight((ushort)max):F1}, median {WorldLayout.RawToHeight((ushort)Percentile(0.5)):F1}");

        var placed = world.Buildings().Where(b => b.WorldY is not null && b.Position != Vector3.Zero).ToList();
        var offsetDiff = placed.Select(b => (double)b.WorldY!.Value - (map.HeightAt(b.Position.X, b.Position.Z) + b.Position.Y)).ToList();
        var plainDiff = placed.Select(b => (double)b.WorldY!.Value - map.HeightAt(b.Position.X, b.Position.Z)).ToList();
        Console.WriteLine($"  building 'world Y pos' - (terrain + instance Y): {Stats(offsetDiff)}");
        Console.WriteLine($"  building 'world Y pos' - terrain:                {Stats(plainDiff)}");
        var roadDiff = world.Roads().SelectMany(r => r.Points).Where(p => Math.Abs(p.X) < WorldLayout.HalfWorldSize && Math.Abs(p.Z) < WorldLayout.HalfWorldSize)
            .Select(p => (double)p.Y - map.HeightAt(p.X, p.Z)).ToList();
        Console.WriteLine($"  road point Y - terrain:                          {Stats(roadDiff)}");
        var townDiff = world.Towns().Select(t => (double)t.Position.Y - map.HeightAt(t.Position.X, t.Position.Z)).ToList();
        Console.WriteLine($"  town Y - terrain:                                {Stats(townDiff)}");
        return problems;
    }

    static string Stats(List<double> values)
    {
        if (values.Count == 0) return "n=0";
        var s = values.Order().ToList();
        double P(double p) => s[(int)Math.Min(s.Count - 1, p * s.Count)];
        return $"n={s.Count} p5 {P(0.05):F2} p25 {P(0.25):F2} median {P(0.5):F2} p75 {P(0.75):F2} p95 {P(0.95):F2}; |d|<0.05 {s.Count(v => Math.Abs(v) < 0.05)}";
    }

    static int SurveyMapFeatures(GameInstall install)
    {
        var file = MapFeatureFile.Open(install);
        var all = file.All().ToList();
        int inZone = all.Count(a => WorldLayout.ZoneOf(a.Feature.Position.X, a.Feature.Position.Z) == a.Zone);
        int yaw = all.Count(a => a.Feature.Rotation.X == 0 && a.Feature.Rotation.Z == 0);
        Console.WriteLine($"Map features {MapFeatureFile.RelativePath}: {file.Zones.Count} zone slots, {file.Zones.Count(z => z.Count > 0)} used, {all.Count} features");
        Console.WriteLine($"  inside their slot's zone {inZone}; pure yaw rotations (read w first) {yaw}; unit quaternions {all.Count(a => Math.Abs(a.Feature.Rotation.Length() - 1) < 1e-3)}");
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        Console.WriteLine($"  targets: {string.Join(", ", all.GroupBy(a => db.Find(a.Feature.StringId)?.Type.ToString() ?? "not in game data").Select(g => $"{g.Key} {g.Count()} ({g.Select(a => a.Feature.StringId).Distinct().Count()} distinct)"))}");
        return 0;
    }

    static int SurveyLegacyTiles(GameInstall install)
    {
        var dir = Path.Combine(install.DataDirectory, "land", "grasssplits");
        if (!Directory.Exists(dir)) { Console.WriteLine("Legacy height tiles: none"); return 0; }
        var tiles = new Dictionary<(int X, int Y), RawHeightTile>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.raw"))
            if (RawHeightTile.TryParseFileName(path, out int x, out int y))
                tiles[(x, y)] = RawHeightTile.ReadFile(path);
        var sizes = tiles.Values.Select(t => t.Size).Distinct().ToList();
        int seamX = 0, seamY = 0, edgesX = 0, edgesY = 0;
        foreach (var ((x, y), t) in tiles)
        {
            int n = t.Size;
            if (tiles.TryGetValue((x + 1, y), out var right))
            {
                edgesX++;
                for (int k = 0; k < n; k++) if (t[n - 1, k] != right[0, k]) { seamX++; break; }
            }
            if (tiles.TryGetValue((x, y + 1), out var below))
            {
                edgesY++;
                for (int k = 0; k < n; k++) if (t[k, n - 1] != below[k, 0]) { seamY++; break; }
            }
        }
        Console.WriteLine($"Legacy height tiles land/grasssplits: {tiles.Count}, sizes {string.Join(",", sizes)}; X {tiles.Keys.Min(k => k.X)}..{tiles.Keys.Max(k => k.X)}, Y {tiles.Keys.Min(k => k.Y)}..{tiles.Keys.Max(k => k.Y)}");
        Console.WriteLine($"  shared edges: X neighbours {edgesX} ({seamX} differ), Y neighbours {edgesY} ({seamY} differ); raw range {tiles.Values.Min(t => t.Samples.Min())}..{tiles.Values.Max(t => t.Samples.Max())}");
        return seamX + seamY;
    }

    static void SurveyOtherFiles(GameInstall install)
    {
        Console.WriteLine("Other world files (header only):");
        foreach (var rel in new[] { "newland/land/blendinfo.dat", "newland/land/features.dat", "newland/land/fogfeatures.dat", "globalPathing.path", "newland/land/areasmap.tga", "newland/land/biomemap.png", "newland/land/blendmap.png" })
        {
            var path = Path.Combine(install.DataDirectory, rel);
            if (!File.Exists(path)) continue;
            using var fs = File.OpenRead(path);
            var head = new byte[16];
            int n = fs.Read(head);
            Console.WriteLine($"  {rel,-32} {fs.Length,10} bytes  {Convert.ToHexString(head, 0, n)}");
        }
    }

    static string Fmt(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X:F0}, {v.Y:F0}, {v.Z:F0})");

    /// <summary>
    /// Writes a top-down PNG of the world: height-shaded terrain (one pixel per <paramref name="step"/>
    /// heightmap samples, north = -Z at the top), the zone grid, roads, buildings and towns.
    /// </summary>
    public static int RenderMap(GameInstall install, string outPng, int step = 16)
    {
        using var map = TerrainHeightmap.Open(install);
        var heights = map.Downsample(step, out int size);
        var sorted = heights.Where(h => h > 0).Order().ToArray();
        float top = sorted.Length == 0 ? 1 : sorted[(int)(sorted.Length * 0.995)];
        var rgb = new byte[size * size * 3];
        double metersPerPixel = WorldLayout.SampleSpacing * step;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                float h = WorldLayout.RawToHeight(heights[i]);
                float hx = WorldLayout.RawToHeight(heights[y * size + Math.Min(x + 1, size - 1)]) - WorldLayout.RawToHeight(heights[y * size + Math.Max(x - 1, 0)]);
                float hy = WorldLayout.RawToHeight(heights[Math.Min(y + 1, size - 1) * size + x]) - WorldLayout.RawToHeight(heights[Math.Max(y - 1, 0) * size + x]);
                // Light from the north-west.
                var normal = Vector3.Normalize(new Vector3(-hx, (float)(2 * metersPerPixel), -hy));
                float shade = Math.Clamp(Vector3.Dot(normal, Vector3.Normalize(new Vector3(-1, 1.2f, -1))), 0.15f, 1f);
                var color = heights[i] == 0 ? new Vector3(40, 60, 90) : Tint(Math.Clamp(heights[i] / top, 0, 1)) * (0.35f + 0.75f * shade);
                rgb[i * 3] = (byte)Math.Clamp(color.X, 0, 255);
                rgb[i * 3 + 1] = (byte)Math.Clamp(color.Y, 0, 255);
                rgb[i * 3 + 2] = (byte)Math.Clamp(color.Z, 0, 255);
            }

        var world = WorldLevelData.Load(install);
        double scale = (size - 1) / (double)WorldLayout.WorldSize;
        (int X, int Y) Px(double wx, double wz) => ((int)Math.Round((wx + WorldLayout.HalfWorldSize) * scale), (int)Math.Round((wz + WorldLayout.HalfWorldSize) * scale));
        void Put(int x, int y, (byte R, byte G, byte B) c, float alpha = 1)
        {
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            int i = (y * size + x) * 3;
            rgb[i] = (byte)(rgb[i] * (1 - alpha) + c.R * alpha);
            rgb[i + 1] = (byte)(rgb[i + 1] * (1 - alpha) + c.G * alpha);
            rgb[i + 2] = (byte)(rgb[i + 2] * (1 - alpha) + c.B * alpha);
        }

        int zonePx = WorldLayout.SamplesPerZone / step;
        for (int k = 0; k <= WorldLayout.ZoneCount; k++)
            for (int t = 0; t < size; t++)
            {
                Put(k * zonePx, t, (255, 255, 255), k % 8 == 0 ? 0.45f : 0.15f);
                Put(t, k * zonePx, (255, 255, 255), k % 8 == 0 ? 0.45f : 0.15f);
            }
        foreach (var zone in world.Zones.Keys)
        {
            var (ox, oz) = WorldLayout.ZoneOrigin(zone);
            var (x0, y0) = Px(ox, oz);
            for (int t = 0; t <= zonePx; t++) { Put(x0 + t, y0, (255, 230, 0), 0.6f); Put(x0, y0 + t, (255, 230, 0), 0.6f); Put(x0 + t, y0 + zonePx, (255, 230, 0), 0.6f); Put(x0 + zonePx, y0 + t, (255, 230, 0), 0.6f); }
        }
        foreach (var road in world.Roads())
            for (int p = 1; p < road.Points.Count; p++)
            {
                var (a, b) = (road.Points[p - 1], road.Points[p]);
                int n = Math.Max(1, (int)(Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) * scale));
                for (int s = 0; s <= n; s++)
                {
                    var q = Vector3.Lerp(a, b, s / (float)n);
                    var (x, y) = Px(q.X, q.Z);
                    Put(x, y, (200, 120, 60), 0.8f);
                }
            }
        foreach (var (_, f) in MapFeatureFile.Open(install).All())
        {
            var (x, y) = Px(f.Position.X, f.Position.Z);
            Put(x, y, (170, 60, 220));
        }
        foreach (var b in world.Buildings().Where(b => b.Position != Vector3.Zero))
        {
            var (x, y) = Px(b.Position.X, b.Position.Z);
            Put(x, y, (255, 40, 40));
        }
        foreach (var t in world.Towns())
        {
            var (x, y) = Px(t.Position.X, t.Position.Z);
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    Put(x + dx, y + dy, Math.Abs(dx) == 2 || Math.Abs(dy) == 2 ? ((byte)0, (byte)0, (byte)0) : ((byte)0, (byte)255, (byte)255));
        }
        WorldPng.Write(outPng, size, size, rgb);
        Console.WriteLine($"Wrote {outPng}: {size}x{size}, {metersPerPixel} world units per pixel. Grey lines: zones (bright every 8);");
        Console.WriteLine("yellow: zones with level data; brown: roads; purple: map features; red: buildings; cyan: towns; blue: height 0.");
        return 0;
    }

    static Vector3 Tint(float t)
    {
        // Low: sand, middle: olive/brown, high: grey-white.
        Vector3[] stops = [new(194, 178, 128), new(150, 140, 90), new(120, 100, 70), new(150, 140, 135), new(240, 240, 240)];
        float f = t * (stops.Length - 1);
        int i = Math.Min((int)f, stops.Length - 2);
        return Vector3.Lerp(stops[i], stops[i + 1], f - i);
    }
}

/// <summary>Minimal 8-bit RGB PNG writer (zlib via System.IO.Compression), for survey images only.</summary>
static class WorldPng
{
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static void Write(string path, int width, int height, byte[] rgb)
    {
        using var fs = File.Create(path);
        fs.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)width);
        BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        Chunk(fs, "IHDR", ihdr);
        var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(rgb, y * width * 3, width * 3);
            }
        Chunk(fs, "IDAT", idat.ToArray());
        Chunk(fs, "IEND", []);
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var buf = new byte[4];
        BigEndian(buf, 0, (uint)data.Length);
        s.Write(buf);
        var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (var b in body) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        BigEndian(buf, 0, ~crc);
        s.Write(buf);
    }

    static void BigEndian(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
}
