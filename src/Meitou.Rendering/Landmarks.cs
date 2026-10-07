using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// Which placed objects are landmarks (docs/renderer-native.md 8.19): meshes so large in the world that they are worth drawing across the whole map
/// (the giant satellite and ring wrecks, huge skeletons, towers). A placement is one when its world bounding radius (the mesh's bounds radius, as
/// <see cref="ObjectMeshCache"/> decodes it, times the placement's largest scale) reaches <see cref="MinRadius"/>. The radius is read from the
/// <c>.mesh</c> file's bounds chunk without loading the mesh (<see cref="OgreMeshReader.TryReadBounds"/>), so a placement far outside any streaming range
/// can be classified; a file without a readable bounds chunk is read whole.
/// </summary>
sealed class LandmarkClass(AssetLocator assets)
{
    /// <summary>
    /// A placement is a landmark from this world bounding radius up (units). Chosen from the whole-world survey (<c>MEITOU_LANDMARK_SURVEY=1</c>, docs/renderer-native.md 8.19):
    /// no building part of an ordinary building reaches 1500 (the largest, the Ancient Factory's shed, is 1488) and the usual map features stay below 1000;
    /// 2000 keeps the giants (the Skylink satellite, the rib cages, the tower cores, the ring and dangler wrecks) and leaves out the cliff blocks.
    /// </summary>
    public const float MinRadius = 2000;

    readonly ConcurrentDictionary<string, float> radii = new(StringComparer.OrdinalIgnoreCase);
    int probes, wholeReads, missing;

    public int Probes => probes;
    public int WholeReads => wholeReads;
    public int Missing => missing;

    /// <summary>The mesh's local bounds radius (0: the file was not found or unreadable). Safe from any thread; cached by path.</summary>
    public float MeshRadius(string meshPath) => radii.GetOrAdd(meshPath, Probe);

    float Probe(string name)
    {
        Interlocked.Increment(ref probes);
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        if (path is null) { Interlocked.Increment(ref missing); return 0; }
        if (OgreMeshReader.TryReadBounds(path, out var b) && b.Max.X >= b.Min.X) return Math.Max((b.Max - b.Min).Length() / 2, 1e-3f);
        Interlocked.Increment(ref wholeReads);
        try
        {
            var mesh = OgreMeshReader.ReadFile(path);
            if (mesh.Bounds is { } bounds && bounds.Max.X >= bounds.Min.X) return Math.Max((bounds.Max - bounds.Min).Length() / 2, 1e-3f);
            return Model.Build(mesh, null).Radius;
        }
        catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException)
        {
            Interlocked.Increment(ref missing);
            return 0;
        }
    }

    /// <summary>The largest scale of a placement's transform (what <see cref="WorldObjectRenderer"/> multiplies a mesh's radius by).</summary>
    public static float Scale(in Matrix4x4 t) =>
        MathF.Sqrt(Math.Max(new Vector3(t.M11, t.M12, t.M13).LengthSquared(), Math.Max(new Vector3(t.M21, t.M22, t.M23).LengthSquared(), new Vector3(t.M31, t.M32, t.M33).LengthSquared())));

    /// <summary>The placement's world bounding radius.</summary>
    public float WorldRadius(PlacedMesh p) => MeshRadius(p.MeshPath) * Scale(p.Transform);

    public bool IsLandmark(PlacedMesh p) => WorldRadius(p) >= MinRadius;

    /// <summary>
    /// Every landmark placement of the world (all populated zones laid out once, in parallel: about a second), with its world radius, in zone order.
    /// Worlds are small (the base game: 26 000 placements in 913 zones), so this is cheaper than streaming zones out to a landmark distance, and it
    /// finds a landmark long before the camera is anywhere near it.
    /// </summary>
    public List<(PlacedMesh Placed, float Radius)> Collect(WorldObjects objects)
    {
        var byZone = new ConcurrentDictionary<ZoneCoordinate, List<(PlacedMesh, float)>>();
        Parallel.ForEach(objects.PopulatedZones.Where(z => z.IsInsideGrid), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 3) }, zone =>
        {
            List<(PlacedMesh, float)>? found = null;
            foreach (var p in objects.BuildZone(zone).Items)
            {
                float radius = WorldRadius(p);
                if (radius >= MinRadius) (found ??= []).Add((p, radius));
            }
            if (found is not null) byZone[zone] = found;
        });
        return [.. byZone.OrderBy(z => z.Key.X).ThenBy(z => z.Key.Y).SelectMany(z => z.Value)];
    }

    /// <summary>
    /// <c>MEITOU_LANDMARK_SURVEY=1</c>: lays out every populated zone, reads each mesh's bounds, and writes the placements by world radius (the largest meshes with
    /// their counts, a histogram, the cost of the pass, and the probe checked against the full decode of the largest meshes).
    /// </summary>
    public static void Survey(WorldObjects objects, AssetLocator assets, TextWriter output)
    {
        var watch = Stopwatch.StartNew();
        var cls = new LandmarkClass(assets);
        var zones = objects.PopulatedZones.Where(z => z.IsInsideGrid).ToList();
        var rows = new ConcurrentBag<(string Mesh, PlacedKind Kind, string Owner, float Local, float World, float X, float Z)>();
        int placements = 0;
        Parallel.ForEach(zones, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 2) }, zone =>
        {
            var laid = objects.BuildZone(zone);
            Interlocked.Add(ref placements, laid.Items.Count);
            foreach (var p in laid.Items)
            {
                float local = cls.MeshRadius(p.MeshPath), world = local * Scale(p.Transform);
                rows.Add((p.MeshPath, p.Kind, p.Owner.Name, local, world, p.Transform.M41, p.Transform.M43));
            }
        });
        double layoutMs = watch.Elapsed.TotalMilliseconds;
        var all = rows.ToArray();
        var radiiSorted = all.Select(r => r.World).Order().ToArray();
        float P(double q) => radiiSorted[Math.Min((int)(radiiSorted.Length * q), radiiSorted.Length - 1)];
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"survey    {zones.Count} populated zones, {placements} placements, {all.Select(r => r.Mesh).Distinct(StringComparer.OrdinalIgnoreCase).Count()} distinct meshes; layout + bounds {layoutMs:0} ms " +
            $"({cls.Probes} probes, {cls.WholeReads} read whole, {cls.Missing} not found)"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"survey    world radius p50 {P(0.5):0} / p90 {P(0.9):0} / p99 {P(0.99):0} / p99.9 {P(0.999):0} / max {radiiSorted[^1]:0}"));
        foreach (float t in new[] { 250f, 500, 750, 1000, 1500, 2000, 3000, 5000, 8000, 12000, 20000 })
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"survey    >= {t,6:0}: {all.Count(r => r.World >= t),5} placements of {all.Where(r => r.World >= t).Select(r => r.Mesh).Distinct(StringComparer.OrdinalIgnoreCase).Count(),3} meshes"));
        output.WriteLine("survey    meshes by largest world radius (mesh, kind, placements, local radius, world radius min..max, one owner):");
        var byMesh = all.GroupBy(r => r.Mesh, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Mesh: g.Key, Kind: g.First().Kind, Count: g.Count(), Local: g.First().Local, Min: g.Min(r => r.World), Max: g.Max(r => r.World), Owner: g.First().Owner))
            .OrderByDescending(m => m.Max).Take(120).ToList();
        foreach (var m in byMesh)
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"survey    {(m.Max >= MinRadius ? "*" : " ")} {m.Mesh,-48} {m.Kind,-13} x{m.Count,-4} local {m.Local,7:0} world {m.Min,7:0}..{m.Max,7:0}  {m.Owner}"));
        // The probe against the full decode on the biggest meshes (and a few ordinary ones).
        int checkedMeshes = 0, differing = 0;
        foreach (var m in byMesh.Take(40).Concat(byMesh.TakeLast(5)))
        {
            var path = assets.Find(m.Mesh) ?? assets.Find(Path.GetFileName(m.Mesh.Replace('\\', '/')));
            if (path is null) continue;
            var mesh = OgreMeshReader.ReadFile(path);
            float full = mesh.Bounds is { } b && b.Max.X >= b.Min.X ? Math.Max((b.Max - b.Min).Length() / 2, 1e-3f) : Model.Build(mesh, null).Radius;
            checkedMeshes++;
            if (MathF.Abs(full - m.Local) > 1e-3f * Math.Max(full, 1)) { differing++; output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"survey    PROBE DIFFERS {m.Mesh}: probe {m.Local:0.###}, decode {full:0.###}")); }
        }
        output.WriteLine($"survey    probe against the full decode: {checkedMeshes} meshes checked, {differing} differ");
        int landmarks = all.Count(r => r.World >= MinRadius);
        output.WriteLine($"survey    at {MinRadius:0}: {landmarks} landmark placements");
        output.WriteLine($"survey    the landmarks (world radius >= {MinRadius:0}) by mesh: qualifying placements of the mesh's total, radius range");
        foreach (var g in all.GroupBy(r => r.Mesh, StringComparer.OrdinalIgnoreCase).Where(g => g.Any(r => r.World >= MinRadius)).OrderByDescending(g => g.Max(r => r.World)))
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"survey      {g.Key,-60} {g.Count(r => r.World >= MinRadius),3} of {g.Count(),3}  {g.Where(r => r.World >= MinRadius).Min(r => r.World),6:0}..{g.Max(r => r.World),6:0}  {g.First().Kind}"));
        output.WriteLine("survey    positions of the largest placements (x, z, world radius, mesh):");
        foreach (var r in all.OrderByDescending(r => r.World).Take(25))
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"survey      {r.X,9:0} {r.Z,9:0}  {r.World,7:0}  {r.Mesh}"));
    }
}
