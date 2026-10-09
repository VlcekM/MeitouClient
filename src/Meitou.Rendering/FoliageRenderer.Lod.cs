using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

public sealed unsafe partial class FoliageRenderer
{
    // ---- generated levels for TERRAIN-mode meshes (docs/formats/foliage.md, "Generated levels") ----
    // The game ships no levels for these meshes. When a mesh has become resident a worker works the levels out (or reads them from the disk cache,
    // FoliageLodCache), the render thread uploads them next to the mesh's own buffers, and from then on the GPU cull picks a level per instance.

    /// <summary>
    /// The Meitou <c>lod</c> switch: TERRAIN-mode meshes are drawn with generated coarser levels where the difference would show less than
    /// <see cref="LodTolerance"/> pixels. Faithful draws every instance at full detail. Needs the GPU cull (not its verify mode).
    /// </summary>
    public bool Lod { get; set; } = true;

    /// <summary>Pixels per radian of the main camera's render (set each frame by the world frame): the scale of a perspective view's level choice. 0: no levels.</summary>
    public float LodPixelsPerRadian { get; set; }

    /// <summary>How many pixels (of the render, before the upscaler) a generated level may deviate from the original surface; <c>MEITOU_LOD_PIXELS</c>.</summary>
    public float LodTolerance { get; set; } = Env("MEITOU_LOD_PIXELS", DefaultLodTolerance);

    /// <summary>
    /// The Meitou <c>lod-far</c> switch: past <see cref="LodFarDistance"/> units from the eye the pixel tolerance of the generated levels grows in proportion to the distance (a plant
    /// 10000 units away may deviate 4 x 10000 / <see cref="LodFarDistance"/> pixels), so the far rocks and plants take the coarse levels sooner. Off: the tolerance is the same everywhere.
    /// </summary>
    public bool LodFar { get; set; } = true;

    /// <summary>Where the tolerance starts to grow with the distance (<see cref="LodFar"/>); <c>MEITOU_LOD_FAR</c>.</summary>
    public float LodFarDistance { get; set; } = Env("MEITOU_LOD_FAR", DefaultLodFarDistance);

    public const float DefaultLodFarDistance = 2500f;

    /// <summary>The default <see cref="LodTolerance"/>: the Tab slider "Foliage LOD distance x" shows this divided by the tolerance.</summary>
    public const float DefaultLodTolerance = 4f;

    /// <summary>How many radii of the mesh one radian of shading deviation (<see cref="FoliageLodSet.NormalAngles"/>) counts as in a level's deviation; <c>MEITOU_LOD_NORMAL</c>.</summary>
    public float LodNormalWeight { get; set; } = Env("MEITOU_LOD_NORMAL", 0.02f);

    /// <summary>
    /// The Meitou <c>screen-lod</c> switch (docs/render-foliage.md, "Screen-size LOD"; <see cref="FoliageScreenLod"/>): a foliage mesh whose triangles are under <see cref="ScreenLodTriPixels"/> square pixels
    /// on the screen shows no more detail, so a generated level (TERRAIN-mode meshes) is allowed with up to <see cref="ScreenLodMultiple"/> times the pixel tolerance, and the billboard replaces
    /// the mesh as soon as an instance's triangles are that small, though no nearer than the atlas frame can be magnified. Off: the distances of before (levels by deviation, billboards at the impostor distance).
    /// </summary>
    public bool ScreenLod { get; set; } = true;

    /// <summary>Square pixels under which a mean triangle counts as not seen; <c>MEITOU_SCREEN_LOD_TRI</c>.</summary>
    public float ScreenLodTriPixels { get; set; } = Env("MEITOU_SCREEN_LOD_TRI", 1.5f);

    /// <summary>How many times the pixel tolerance a level may deviate while the level before has sub-pixel triangles; <c>MEITOU_SCREEN_LOD_MULT</c>.</summary>
    public float ScreenLodMultiple { get; set; } = Env("MEITOU_SCREEN_LOD_MULT", 2f);

    /// <summary>The render's pixels per radian for the screen-size rules: the main camera's, or a 1080-line picture's with the 50 degree field of view before the first frame.</summary>
    float ScreenLodPixelsPerRadian => LodPixelsPerRadian > 0 ? LodPixelsPerRadian : Meitou.Rendering.Impostors.ImpostorClass.ReferenceHeight / (2 * MathF.Tan(25 * MathF.PI / 180));

    /// <summary>The deviations the cull and <see cref="LevelNeeded"/> use for a level set in the view being drawn: with <see cref="ScreenLod"/> in a colour view the ones that count the size of the triangles.</summary>
    float[] LevelErrors(RockLod lod) => ScreenLod && lodActive && !lodView.Ortho ? lod.Effective(LodTolerance, ScreenLodTriPixels, ScreenLodMultiple) : lod.Relative;

    float[] RelativeErrors(FoliageLodSet set, float radius)
    {
        var result = new float[set.Levels];
        for (int i = 1; i < result.Length; i++) result[i] = Math.Max(result[i - 1], Math.Max(set.Errors[i] / radius, LodNormalWeight * set.NormalAngles[i]));
        return result;
    }

    /// <summary>How many shadow texels a level may deviate in a cascade; <c>MEITOU_LOD_TEXELS</c>.</summary>
    public float LodShadowTolerance { get; set; } = Env("MEITOU_LOD_TEXELS", 2f);

    /// <summary><c>MEITOU_LOD_ALT=1</c>: levels on every other frame only (the stage names of those frames end in <c>+lod</c>), for timing both in one run.</summary>
    internal static readonly bool LodAlternate = Environment.GetEnvironmentVariable("MEITOU_LOD_ALT") == "1";
    static readonly bool LodLog = Environment.GetEnvironmentVariable("MEITOU_LOD_LOG") == "1";

    /// <summary>The shadow cascade being drawn: its texel in world units (0: unknown, no levels in it).</summary>
    float lodTexel;

    /// <summary>The view being drawn uses generated levels (set by <see cref="Draw"/> before the work list is built).</summary>
    bool lodActive;
    /// <summary>What the current view tells the cull about the levels.</summary>
    FoliageLodView lodView;

    /// <summary>A mesh's generated levels on the GPU: one index buffer per part holding its levels 1 and up, bound with the part's own vertex buffer.</summary>
    sealed class RockLod
    {
        /// <summary>All the levels the builder made (counting 0), and those it made before 2026-10-09 (shading deviation up to 0.6 rad). <see cref="Levels"/> is the former, or the latter while
        /// <see cref="Capped"/> (the lod-far switch off, so one A/B shows the whole change).</summary>
        public required int AllLevels;
        public int LegacyLevels;
        public bool Capped;
        public int Levels => Capped ? LegacyLevels : AllLevels;
        /// <summary>The deviation of each level over the mesh's radius (<c>[0]</c> is 0), never decreasing.</summary>
        public required float[] Relative;
        public required PartLod[] Parts;
        public required FoliageLodSet Set;
        /// <summary>Per level (counting 0) the mean triangle edge over the mesh's radius (<see cref="FoliageScreenLod.SubPixelDistance"/>'s input); 0 for a mesh of unknown area.</summary>
        public float[] TriangleExtent = [];
        float[]? effective;
        (float Tolerance, float Pixels, float Multiple, int Levels) effectiveKey;

        /// <summary>The deviations with the size of the triangles counted (<see cref="FoliageScreenLod.EffectiveErrors"/>), kept until a setting changes.</summary>
        public float[] Effective(float tolerance, float triPixels, float multiple)
        {
            var key = (tolerance, triPixels, multiple, Levels);
            if (effective is null || effectiveKey != key)
            {
                var subPixel = new float[Levels];
                for (int k = 0; k < Levels; k++) subPixel[k] = FoliageScreenLod.SubPixelDistance(TriangleExtent[k], triPixels);
                effective = FoliageScreenLod.EffectiveErrors(Relative.AsSpan(0, Levels).ToArray(), subPixel, tolerance, multiple);
                effectiveKey = key;
            }
            return effective;
        }
        public long Bytes;

        public void Dispose()
        {
            foreach (var p in Parts) p.Indices.Dispose();   // freed after the frames in flight
        }
    }

    sealed class PartLod
    {
        public required DeviceBuffer Indices;
        public required MeshBindings Bindings;
        /// <summary>Per level (index 0 unused): the first index and the index count in <see cref="Indices"/>.</summary>
        public required int[] First, Count;
    }

    sealed record LodResult(FoliageLodSet? Set, bool Cached, double Ms, long FileBytes);

    int lodRunning, lodWaiting;
    /// <summary>The level jobs by mesh file (kept when done).</summary>
    readonly Dictionary<string, Task<LodResult>> lodTasks = [];
    long lodBuilt, lodCacheHits, lodNone, lodFileBytes;
    double lodBuildMs, lodLoadMs;

    /// <summary>Generated levels: the meshes built, read from the cache, found to need none; the cache bytes written; CPU time on the workers (building, reading).</summary>
    public string LodSummary => $"{lodBuilt} built ({lodBuildMs:0} ms), {lodCacheHits} from the cache ({lodLoadMs:0} ms), {lodNone} without a level; {lodFileBytes / 1024} KB written";

    /// <summary>
    /// Starts, collects and uploads the level jobs of the resident TERRAIN-mode meshes (once a frame, from <see cref="Update"/>). One job per mesh file, however many mesh records
    /// name it; a finished job's result stays, so a mesh that is unloaded and loaded again gets its levels at once.
    /// </summary>
    void PumpLods()
    {
        lodWaiting = lodRunning = 0;
        foreach (var t in lodTasks.Values) if (!t.IsCompleted) lodRunning++;
        if (!Lod || !GpuCull || GpuCullVerify) return;
        foreach (var a in assetsByMesh.Values)
        {
            if (!a.Terrain || a.LodDone || !a.Resident || a.Main is null || a.Triangles < FoliageLodBuilder.MinTriangles) continue;
            string name = a.Mesh.MeshPath;
            if (!lodTasks.TryGetValue(name, out var task))
            {
                if (lodRunning >= 3) { lodWaiting++; continue; }
                lodRunning++;
                float radius = a.Radius;
                string label = a.Mesh.Name;
                lodTasks[name] = task = BackgroundWork.Run(() => MakeLods(name, radius, label));
            }
            if (!task.IsCompleted) continue;
            a.LodDone = true;   // an unloaded mesh makes them again with its next residency
            LodResult? result = null;
            try { result = task.Result; }
            catch (AggregateException e) { Messages.Add($"foliage levels {name}: {e.InnerException?.Message ?? e.Message}"); }
            if (result?.Set is { } set && a.Main is { } main && main.Parts.Count == set.Indices.Length) QueueLodUpload(a, main, set);
        }
    }

    LodResult MakeLods(string name, float radius, string label)
    {
        var watch = Stopwatch.StartNew();
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        if (path is null) return new LodResult(null, false, 0, 0);
        var model = Decode(name);
        if (model is null) return new LodResult(null, false, 0, 0);
        int parts = model.Parts.Count(p => p.Indices.Length > 0);
        string cache = FoliageLodCache.PathFor(name, File.ReadAllBytes(path));
        var (hit, set) = FoliageLodCache.TryLoad(cache, parts);
        long bytes = 0;
        if (!hit)
        {
            set = FoliageLodBuilder.Build(model, radius);
            bytes = FoliageLodCache.Save(cache, set, parts);
        }
        double ms = watch.Elapsed.TotalMilliseconds;
        lock (this)
        {
            if (hit) { lodCacheHits++; lodLoadMs += ms; }
            else { lodBuilt++; lodBuildMs += ms; lodFileBytes += bytes; }
            if (set is null) lodNone++;
        }
        if (LodLog)
            Console.WriteLine(set is null
                ? $"lod       {label} ({name}): no level ({model.Parts.Sum(p => p.Indices.Length / 3)} triangles, {(hit ? "cached" : $"built in {ms:0} ms")})"
                : $"lod       {label} ({name}): {string.Join(" > ", set.Triangles.Select((t, i) => i == 0 ? $"{t}" : $"{t} (dev {set.Errors[i] / radius * 100:0.00}% of r {radius:0}, normals {set.NormalAngles[i] * 57.3f:0}�)"))} ({(hit ? "cached" : $"built in {ms:0} ms, {bytes / 1024} KB")})");
        return new LodResult(set, hit, ms, bytes);
    }

    /// <summary>Uploads a mesh's levels (render thread, as a step of the frame's upload queue); dropped when the mesh was unloaded meanwhile.</summary>
    void QueueLodUpload(MeshAsset a, GpuMesh main, FoliageLodSet set)
    {
        uploads.Enqueue(() =>
        {
            if (!a.Resident || !ReferenceEquals(a.Main, main) || a.Lod is not null) return;
            Gpu.EnsureFrame();
            var parts = new PartLod[main.Parts.Count];
            long bytes = 0;
            for (int p = 0; p < parts.Length; p++)
            {
                var levels = set.Indices[p];
                var first = new int[set.Levels];
                var count = new int[set.Levels];
                int total = 0;
                for (int l = 1; l < set.Levels; l++) { first[l] = total; count[l] = levels[l - 1].Length; total += count[l]; }
                var indices = DeviceBuffer.Create(Gpu, (ulong)total * sizeof(uint), BufferUse.Index, MeshAllocationName);
                ulong at = 0;
                for (int l = 1; l < set.Levels; l++)
                {
                    var bytesOfLevel = MemoryMarshal.AsBytes(levels[l - 1].AsSpan());
                    for (int s = 0; s < bytesOfLevel.Length; s += SlabBytes)
                        Gpu.Uploads.Write(indices, at + (ulong)s, bytesOfLevel.Slice(s, Math.Min(SlabBytes, bytesOfLevel.Length - s)));
                    at += (ulong)bytesOfLevel.Length;
                }
                bytes += (long)total * sizeof(uint);
                parts[p] = new PartLod { Indices = indices, Bindings = MeshBindings.Of(VertexAttributes(main.Parts[p].Vertices), indices), First = first, Count = count };
            }
            a.Lod = new RockLod { AllLevels = set.Levels, LegacyLevels = 1 + set.NormalAngles.Skip(1).TakeWhile(x => x <= FoliageLodBuilder.LegacyMaxNormalAngle).Count(),Relative = RelativeErrors(set, a.Radius), TriangleExtent = [.. set.Triangles.Select(t => FoliageScreenLod.LevelExtent(a.TriExtent, a.Triangles, t) / a.Radius)], Parts = parts, Set = set, Bytes = bytes };
            a.Bytes += bytes;
            residentMeshBytes += bytes;
        });
    }

    /// <summary>Drops a mesh's levels with the mesh (unloaded): the next time it is resident they are made, or read from the cache, again.</summary>
    void DropLods(MeshAsset a)
    {
        if (a.Lod is { } lod)
        {
            lod.Dispose();
            a.Bytes -= lod.Bytes;
            residentMeshBytes -= lod.Bytes;
            a.Lod = null;
        }
        a.LodDone = false;
    }

    /// <summary>
    /// Whether a work-list entry can have instances in this view: always for an ordinary one; for a generated level's, only when some instance of the group can pick that level
    /// (a coarser level needs a far enough instance or, in a cascade, a small enough one), and when not all of them pick a still coarser one. Each test is made with the group's
    /// box and radii and a 1 % margin the safe way, so it never leaves out a level that has an instance; a draw it saves costs the GPU a little even with no instance.
    /// </summary>
    bool LevelNeeded(in GpuEntry e, Vector3 eye)
    {
        if (e.Group.Asset.WorkLod is not { } lod || !e.Group.Asset.Terrain || !IsRockBatch(e.Batch)) return true;
        if (!lodActive) return e.Level == 0;
        var g = e.Group;
        var relative = LevelErrors(lod);
        float tolerance = lodView.Tolerance, scale = lodView.Scale, growFrom = lodView.FarDistance;
        // The size at which level k stops being allowed: the largest (radius over distance, or over texel) it takes.
        float far = float.MaxValue, near = 0;
        if (!lodView.Ortho)
        {
            near = Vector3.Distance(eye, Vector3.Clamp(eye, g.BoundMin, g.BoundMax));
            var farthest = Vector3.Max(Vector3.Abs(eye - g.BoundMin), Vector3.Abs(eye - g.BoundMax));
            far = farthest.Length();
        }
        bool Allowed(int level)   // some instance may pick a level at least this coarse
        {
            if (level == 0) return true;
            float need = relative[level] * scale / tolerance;   // radii per radius of the mesh: allowed when distance >= r (1 + need), or in a cascade when r <= tolerance texel / error
            if (lodView.Ortho) return g.MinRadius * relative[level] * scale <= tolerance * 1.01f;
            if (growFrom > 0)
            {
                // The tolerance grows with the distance past growFrom (the cull kernel): allowed when r * error <= tolerance * max(1, gap / growFrom) * gap at the farthest gap.
                float gap = far * 1.01f - g.MinRadius;   // the farthest an instance can be: a larger gap only allows more, the safe side
                return gap > 0 && g.MinRadius * relative[level] * scale <= tolerance * Math.Max(1, gap / growFrom) * gap;
            }
            return far >= g.MinRadius * (1 + need) * 0.99f;
        }
        bool Surpassed(int level)   // every instance picks a coarser level
        {
            if (level + 1 >= lod.Levels) return false;
            float need = relative[level + 1] * scale / tolerance;
            if (!lodView.Ortho && growFrom > 0)
            {
                float gap = near * 0.99f - g.MaxRadius;   // the nearest an instance can be: the nearest is the hardest to satisfy, so a smaller gap is the safe side
                return gap > 0 && g.MaxRadius * relative[level + 1] * scale <= tolerance * Math.Max(1, gap / growFrom) * gap;
            }
            return lodView.Ortho ? g.MaxRadius * relative[level + 1] * scale <= tolerance * 0.99f : near >= g.MaxRadius * (1 + need) * 1.01f;
        }
        return Allowed(e.Level) && !Surpassed(e.Level);
    }

    bool IsRockBatch(int batch) => batch >= gpuOrder.Count && batch < gpuOrder.Count + gpuRockOrder.Count;

    /// <summary>The work lists of this frame have generated levels (the switch, the GPU cull, not its verify mode; with <see cref="LodAlternate"/> every other frame).</summary>
    bool LodWanted => Lod && GpuCull && !GpuCullVerify && !(LodAlternate && (Gpu.Frame.Number & 1) == 1);

    /// <summary>Whether this view picks generated levels, and how it measures them.</summary>
    FoliageLodView CurrentLodView(float eyeY)
    {
        lodActive = false;
        if (!LodWanted) return default;
        if (depthPass)
        {
            if (!(lodTexel > 0) || !(LodShadowTolerance > 0)) return default;
            lodActive = true;
            return new FoliageLodView(0, 1 / lodTexel, LodShadowTolerance, true);
        }
        if (!(LodPixelsPerRadian > 0) || !(LodTolerance > 0)) return default;
        lodActive = true;
        return new FoliageLodView(eyeY, LodPixelsPerRadian, LodTolerance, false, LodFar && LodFarDistance > 0 ? LodFarDistance : 0);
    }
}
