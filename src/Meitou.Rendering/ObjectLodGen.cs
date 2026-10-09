using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Meitou.Data.Ogre;

namespace Meitou.Rendering;

/// <summary>
/// Generated mesh levels for placed objects and buildings (the <c>object-lod-gen</c> A/B switch; docs/render-objects.md, "Generated levels"). Most building parts ship with no
/// reduced level or only 4000 / 8000 ones, so a platform of 13 000 triangles is drawn whole up to 4000 units, where its triangles are far under a pixel. The levels are made by
/// <see cref="Characters.MeshSimplifier"/> (texture-coordinate aware, so a level indexes the part's own vertices) with <see cref="FoliageLodBuilder"/>'s measures, cached on disk
/// (<see cref="Root"/>), and put among the file's levels by the distance the screen-size rule gives (<see cref="Distances"/>, the rule of <see cref="FoliageScreenLod"/>).
/// </summary>
public static class ObjectLodGen
{
    /// <summary>The switch: on by default (<c>MEITOU_OBJECT_LOD_GEN=0</c> starts it off and nothing is generated); off draws the file's levels only.</summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("MEITOU_OBJECT_LOD_GEN") != "0";

    /// <summary>Bumped when the simplifier, the steps or the error measure change (the disk cache's key).</summary>
    public const int Version = 4;

    /// <summary>The steps (share of a part's triangles), floor per part, limits and the texture-coordinate guard objects are built with.</summary>
    public static readonly FoliageLodBuilder.Settings Settings = new([0.5f, 0.25f, 0.12f, 0.06f], 24, 0.25f, 3.2f, 0.85f, true, true);

    /// <summary>Meshes with fewer triangles (all parts) get no levels.</summary>
    public const int MinTriangles = 400;

    /// <summary>How many pixels a generated level may deviate from the original surface (<c>MEITOU_OBJECT_LOD_PIXELS</c>).</summary>
    public static float Tolerance { get; set; } = Env("MEITOU_OBJECT_LOD_PIXELS", 1.5f);

    /// <summary>Square pixels under which a mean triangle counts as not seen, and the multiple of the tolerance a level may deviate by then (as the foliage's <c>screen-lod</c>).</summary>
    public static float TriPixels { get; set; } = Env("MEITOU_OBJECT_LOD_TRI", 1.5f);
    public static float Multiple { get; set; } = Env("MEITOU_OBJECT_LOD_MULT", 2f);

    /// <summary>How many radii one radian of shading deviation counts as in a level's deviation (as <c>FoliageRenderer.LodNormalWeight</c>).</summary>
    public static float NormalWeight { get; set; } = Env("MEITOU_OBJECT_LOD_NORMAL", 0.004f);

    /// <summary>The disk cache of the levels, <c>%LOCALAPPDATA%\Meitou\objlods</c> (<c>MEITOU_OBJECT_LOD_CACHE</c>).</summary>
    public static string Root { get; set; } = Environment.GetEnvironmentVariable("MEITOU_OBJECT_LOD_CACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "objlods");

    /// <summary>Counters for the load-time report: meshes built, read from the cache, and the milliseconds the builds took (all threads summed).</summary>
    public static int Built, Cached, Skipped;
    public static long BuildTicks;

    static float Env(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v > 0 ? v : fallback;

    /// <summary>One level of a mesh's merged list: from the file or generated, which of those, where it starts and how many triangles it has.</summary>
    public readonly record struct Entry(bool Generated, int Source, float Distance, int Triangles);

    /// <summary>The merged levels of a mesh and the two ways to pick among them (<see cref="LodCurve"/>).</summary>
    public sealed class Plan
    {
        public required Entry[] Levels;
        public required LodCurve Curve;
        public required FoliageLodSet Set;
    }

    static readonly ConcurrentDictionary<string, Plan?> Plans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The plan of a mesh (null: it gets no generated levels), made once per mesh and kept, so a mesh remade with another finest level keeps its level numbers.
    /// Only a mesh of unskinned parts without manual levels qualifies. <paramref name="pixelsPerRadian"/> is the render's, as it is when the mesh first loads.
    /// </summary>
    public static Plan? PlanFor(string key, string path, Model model, float radius, IReadOnlyList<MeshLodLevel> fileLevels, float pixelsPerRadian)
    {
        if (!Enabled) return null;
        if (Plans.TryGetValue(key, out var known)) return known;
        var plan = Make(path, model, radius, fileLevels, pixelsPerRadian);
        Plans[key] = plan;
        return plan;
    }

    static Plan? Make(string path, Model model, float radius, IReadOnlyList<MeshLodLevel> fileLevels, float pixelsPerRadian)
    {
        var parts = model.Parts.Where(p => p.Indices.Length > 0).ToList();
        if (parts.Count == 0 || parts.Any(p => p.Skinned) || fileLevels.Any(l => l.ManualMesh is not null)) { Interlocked.Increment(ref Skipped); return null; }
        int full = parts.Sum(p => p.Indices.Length / 3);
        if (full < MinTriangles) { Interlocked.Increment(ref Skipped); return null; }
        var set = Load(path, model, radius);
        if (set is null) return null;
        var file = FileTriangles(model, fileLevels);
        var generated = Distances(set, TriangleExtent(parts), radius, pixelsPerRadian);
        return Merge(fileLevels.Select(l => l.Distance).ToArray(), file, generated, set.Triangles, set);
    }

    static FoliageLodSet? Load(string path, Model model, float radius)
    {
        string file;
        try { file = FoliageLodCache.PathFor(path, File.ReadAllBytes(path), Root, Version); }
        catch (IOException) { return null; }
        var parts = model.Parts.Count(p => p.Indices.Length > 0);
        var (hit, cached) = FoliageLodCache.TryLoad(file, parts, Version);
        if (hit) { Interlocked.Increment(ref Cached); return cached; }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var built = FoliageLodBuilder.Build(model, radius, Settings);
        Interlocked.Add(ref BuildTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref Built);
        FoliageLodCache.Save(file, built, parts, Version);
        return built;
    }

    /// <summary>The mean triangle edge of a mesh's parts (mesh units): the square root of their mean area.</summary>
    public static float TriangleExtent(IReadOnlyList<ModelPart> parts)
    {
        double area = 0;
        long triangles = 0;
        foreach (var part in parts)
            for (int t = 0; t + 2 < part.Indices.Length; t += 3)
            {
                var a = part.Vertices[part.Indices[t]].Position;
                area += Vector3.Cross(part.Vertices[part.Indices[t + 1]].Position - a, part.Vertices[part.Indices[t + 2]].Position - a).Length() * 0.5;
                triangles++;
            }
        return FoliageScreenLod.TriangleExtent(area, (int)Math.Min(triangles, int.MaxValue));
    }

    /// <summary>The triangles of each file level (level 0 first), summed over the parts, a part without indices of its own at a level keeping the level before's (as the upload does).</summary>
    public static int[] FileTriangles(Model model, IReadOnlyList<MeshLodLevel> levels)
    {
        var result = new int[levels.Count];
        foreach (var part in model.Parts)
        {
            int previous = part.Indices.Length;
            result[0] += previous / 3;
            for (int l = 1; l < levels.Count; l++)
            {
                var own = part.SubMeshIndex < levels[l].Indices.Count ? levels[l].Indices[part.SubMeshIndex] : null;
                if (own is { Length: > 0 } && own.Length % 3 == 0) previous = own.Length;
                result[l] += previous / 3;
            }
        }
        return result;
    }

    /// <summary>
    /// The value (the game's LOD distance: camera to the bounds' centre minus the radius) from which each generated level is used, level 0 first (0): the screen-size rule
    /// of the foliage (<see cref="FoliageScreenLod.EffectiveErrors"/>): a level is used where its deviation (and shading deviation weighed as radii) is under
    /// <see cref="Tolerance"/> pixels, or under <see cref="Multiple"/> times that where the level before has triangles under <see cref="TriPixels"/> square pixels.
    /// For an instance of scale 1; the distance to the centre is <c>error * radius * pixelsPerRadian / tolerance</c>, less the radius.
    /// </summary>
    public static float[] Distances(FoliageLodSet set, float triangleExtent, float radius, float pixelsPerRadian)
    {
        int n = set.Levels;
        var relative = new float[n];
        var subPixel = new float[n];
        for (int k = 0; k < n; k++)
        {
            if (k > 0) relative[k] = Math.Max(relative[k - 1], Math.Max(set.Errors[k] / radius, NormalWeight * set.NormalAngles[k]));
            subPixel[k] = FoliageScreenLod.SubPixelDistance(FoliageScreenLod.LevelExtent(triangleExtent, set.Triangles[0], set.Triangles[k]) / radius, TriPixels);
        }
        var errors = FoliageScreenLod.EffectiveErrors(relative, subPixel, Tolerance, Multiple);
        var result = new float[n];
        for (int k = 1; k < n; k++)
        {
            float distance = errors[k] * radius * pixelsPerRadian / Tolerance - radius;
            result[k] = Math.Max(distance, result[k - 1] + 1e-3f);
        }
        return result;
    }

    /// <summary>
    /// The merged level list. <paramref name="file"/> are the file's level distances (level 0 first) with their triangles, <paramref name="generated"/> the generated ones (level 0 is the original,
    /// the same mesh). Two ways through it: the generated one takes, by ascending distance, every level (file or generated) with fewer triangles than the one before it (a generated level at
    /// most <c>0.85</c> of it); the plain one only the file's levels (the switch off). The merged list holds both, by distance.
    /// </summary>
    public static Plan Merge(float[] fileDistances, int[] fileTriangles, float[] generatedDistances, int[] generatedTriangles, FoliageLodSet set)
    {
        var candidates = new List<Entry>();
        for (int k = 1; k < fileDistances.Length; k++) candidates.Add(new Entry(false, k, fileDistances[k], fileTriangles[k]));
        for (int k = 1; k < generatedDistances.Length; k++) candidates.Add(new Entry(true, k, generatedDistances[k], generatedTriangles[k]));
        candidates = [.. candidates.OrderBy(c => c.Distance).ThenBy(c => c.Triangles)];
        var viaGenerated = new HashSet<Entry>();
        int last = fileTriangles[0];
        foreach (var c in candidates)
            if (c.Generated ? c.Triangles <= last * 0.85 : c.Triangles < last) { viaGenerated.Add(c); last = c.Triangles; }
        var merged = new List<Entry> { new(false, 0, 0, fileTriangles[0]) };
        merged.AddRange(candidates.Where(c => !c.Generated || viaGenerated.Contains(c)));
        var mapGenerated = new List<int> { 0 };
        var mapPlain = new List<int> { 0 };
        for (int i = 1; i < merged.Count; i++)
        {
            if (viaGenerated.Contains(merged[i])) mapGenerated.Add(i);
            if (!merged[i].Generated) mapPlain.Add(i);
        }
        return new Plan
        {
            Levels = [.. merged],
            Set = set,
            Curve = new LodCurve([.. merged.Select(m => m.Distance)], [.. merged.Select(m => m.Generated)], [.. mapGenerated.Select(i => merged[i].Distance)], [.. mapGenerated], [.. mapPlain.Select(i => merged[i].Distance)], [.. mapPlain]),
        };
    }

    /// <summary>The merged level list as <see cref="MeshLodLevel"/>s: the file's levels as they are, the generated ones with the set's index lists by submesh.</summary>
    public static List<MeshLodLevel> Apply(Plan plan, Model model, IReadOnlyList<MeshLodLevel> fileLevels)
    {
        var parts = model.Parts.Where(p => p.Indices.Length > 0).ToList();
        int submeshes = Math.Max(fileLevels[0].Indices.Count, model.Parts.Count == 0 ? 0 : model.Parts.Max(p => p.SubMeshIndex) + 1);
        var result = new List<MeshLodLevel>(plan.Levels.Length) { fileLevels[0] };
        for (int i = 1; i < plan.Levels.Length; i++)
        {
            var e = plan.Levels[i];
            if (!e.Generated) { result.Add(fileLevels[e.Source]); continue; }
            var indices = new uint[]?[submeshes];
            for (int p = 0; p < parts.Count; p++) indices[parts[p].SubMeshIndex] = plan.Set.Indices[p][e.Source - 1];
            result.Add(new MeshLodLevel(e.Distance, indices));
        }
        return result;
    }
}

/// <summary>
/// How an object picks among the levels of its mesh: the level distances (the game's LOD value), either all of the merged list (<see cref="ObjectLodGen.Enabled"/>) or the file's only,
/// each with the merged index of its levels. A mesh without generated levels has one list.
/// </summary>
public sealed class LodCurve(float[] all, bool[] isGenerated, float[] generated, int[] generatedMap, float[] plain, int[] plainMap)
{
    /// <summary>A mesh with only the distances it came with.</summary>
    public static LodCurve Of(float[] distances)
    {
        int[] identity = [.. Enumerable.Range(0, distances.Length)];
        return new LodCurve(distances, new bool[distances.Length], distances, identity, distances, identity);
    }

    /// <summary>The distances of every merged level.</summary>
    public float[] All => all;
    float[] Active => ObjectLodGen.Enabled ? generated : plain;
    int[] Map => ObjectLodGen.Enabled ? generatedMap : plainMap;

    /// <summary>The merged level for a LOD value (<see cref="MeshLod.Select(ReadOnlySpan{float}, float)"/>).</summary>
    public int Select(float value) => Map[MeshLod.Select(Active, value)];

    /// <summary>
    /// The pair of levels to draw (<see cref="MeshLod.Blend"/>), as merged level numbers. A generated level is never cross-faded (its deviation is held under a pixel or two, and the
    /// dither of two near-identical levels shows as grain), so a pair with one switches at the distance, as the foliage's levels do.
    /// </summary>
    public LodBlend Blend(float value)
    {
        var b = MeshLod.Blend(Active, value);
        var map = Map;
        int lower = map[b.Lower], upper = map[b.Upper];
        if (b.IsBlending && (isGenerated[lower] || isGenerated[upper])) { int level = map[MeshLod.Select(Active, value)]; return new LodBlend(level, level, 0); }
        return new LodBlend(lower, upper, b.T);
    }

    /// <summary>
    /// The finest merged level that either way of picking can draw at a LOD value (<see cref="ObjectMeshCache.LevelFor"/>): what a mesh has to hold, so that switching
    /// <see cref="ObjectLodGen.Enabled"/> (the A/B switch) does not make every mesh remake itself.
    /// </summary>
    public int LevelFor(float value)
    {
        float v = Math.Max(value, 0) / 1.07f;
        return Math.Min(generatedMap[MeshLod.Select(generated, v)], plainMap[MeshLod.Select(plain, v)]);
    }
}
