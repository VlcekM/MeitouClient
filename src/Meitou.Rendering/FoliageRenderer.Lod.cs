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
    public float LodTolerance { get; set; } = Env("MEITOU_LOD_PIXELS", 3f);

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
        public required int Levels;
        /// <summary>The deviation of each level over the mesh's radius (<c>[0]</c> is 0), never decreasing.</summary>
        public required float[] Relative;
        public required PartLod[] Parts;
        public required FoliageLodSet Set;
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
    long lodBuilt, lodCacheHits, lodNone, lodFileBytes;
    double lodBuildMs, lodLoadMs;

    /// <summary>Generated levels: the meshes built, read from the cache, found to need none; the cache bytes written; CPU time on the workers (building, reading).</summary>
    public string LodSummary => $"{lodBuilt} built ({lodBuildMs:0} ms), {lodCacheHits} from the cache ({lodLoadMs:0} ms), {lodNone} without a level; {lodFileBytes / 1024} KB written";

    /// <summary>Starts, collects and uploads the level jobs of the resident TERRAIN-mode meshes (once a frame, from <see cref="Update"/>).</summary>
    void PumpLods()
    {
        lodWaiting = 0;
        if (!Lod || !GpuCull || GpuCullVerify) return;
        foreach (var a in assetsByMesh.Values)
        {
            if (!a.Terrain) continue;
            if (a.LodJob is { IsCompleted: true } job)
            {
                a.LodJob = null;
                LodResult? result = null;
                try { result = job.Result; }
                catch (AggregateException e) { Messages.Add($"foliage levels {a.Mesh.MeshPath}: {e.InnerException?.Message ?? e.Message}"); }
                lodRunning--;
                a.LodDone = a.Resident;   // an unloaded mesh makes them again with its next residency
                if (result?.Set is { } set && a.Resident && a.Main is { } main && main.Parts.Count == set.Indices.Length) QueueLodUpload(a, main, set);
            }
            else if (a.LodJob is null && !a.LodDone && a.Resident && a.Main is not null && a.Triangles >= 2 * FoliageLodBuilder.Floor)
            {
                if (lodRunning >= 3) { lodWaiting++; continue; }
                string name = a.Mesh.MeshPath;
                float radius = a.Radius;
                lodRunning++;
                a.LodJob = BackgroundWork.Run(() => MakeLods(name, radius, a.Mesh.Name));
            }
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
                : $"lod       {label} ({name}): {string.Join(" > ", set.Triangles.Select((t, i) => i == 0 ? $"{t}" : $"{t} (dev {set.Errors[i] / radius * 100:0.00}% of r {radius:0})"))} ({(hit ? "cached" : $"built in {ms:0} ms, {bytes / 1024} KB")})");
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
            a.Lod = new RockLod { Levels = set.Levels, Relative = [.. set.Errors.Select(e => e / a.Radius)], Parts = parts, Set = set, Bytes = bytes };
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
        float tolerance = lodView.Tolerance, scale = lodView.Scale;
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
            float need = lod.Relative[level] * scale / tolerance;   // radii per radius of the mesh: allowed when distance >= r (1 + need), or in a cascade when r <= tolerance texel / error
            return lodView.Ortho ? g.MinRadius * lod.Relative[level] * scale <= tolerance * 1.01f : far >= g.MinRadius * (1 + need) * 0.99f;
        }
        bool Surpassed(int level)   // every instance picks a coarser level
        {
            if (level + 1 >= lod.Levels) return false;
            float need = lod.Relative[level + 1] * scale / tolerance;
            return lodView.Ortho ? g.MaxRadius * lod.Relative[level + 1] * scale <= tolerance * 0.99f : near >= g.MaxRadius * (1 + need) * 1.01f;
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
        return new FoliageLodView(eyeY, LodPixelsPerRadian, LodTolerance, false);
    }
}
