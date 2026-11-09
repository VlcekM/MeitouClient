using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Shadows;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// Generated levels for TERRAIN-mode meshes (docs/formats/foliage.md, "Generated levels"). The game ships no levels for these meshes. When a mesh has
/// become resident a worker works the levels out (or reads them from the disk cache, FoliageLodCache), the render thread uploads them next to the
/// mesh's own buffers, and from then on the GPU cull picks a level per instance.
/// </summary>
public sealed unsafe partial class FoliageRenderer
{
    /// <summary>
    /// The Meitou <c>lod</c> switch: TERRAIN-mode meshes are drawn with generated coarser levels where the difference would show less than
    /// <see cref="LodTolerance"/> pixels. Faithful draws every instance at full detail. Needs the GPU cull.
    /// </summary>
    public bool Lod { get; set; } = true;

    /// <summary>
    /// Pixels per radian of the main camera's render (set each frame by the world frame): the scale of a perspective view's level choice. 0: no
    /// levels.
    /// </summary>
    public float LodPixelsPerRadian { get; set; }

    /// <summary>
    /// How many pixels (of the render, before the upscaler) a generated level may deviate from the original surface.
    /// </summary>
    public float LodTolerance { get; set; } = DefaultLodTolerance;

    /// <summary>
    /// The Meitou <c>lod-far</c> switch: past <see cref="LodFarDistance"/> units from the eye the pixel tolerance of the generated levels grows in
    /// proportion to the distance (a plant 10000 units away may deviate 4 x 10000 / <see cref="LodFarDistance"/> pixels), so the far rocks and plants
    /// take the coarse levels sooner. Off: the tolerance is the same everywhere.
    /// </summary>
    public bool LodFar { get; set; } = true;

    /// <summary>
    /// Where the tolerance starts to grow with the distance (<see cref="LodFar"/>).
    /// </summary>
    public float LodFarDistance { get; set; } = DefaultLodFarDistance;

    /// <summary>
    /// The default <see cref="LodFarDistance"/>, in world units.
    /// </summary>
    public const float DefaultLodFarDistance = 2500f;

    /// <summary>
    /// The default <see cref="LodTolerance"/>: the Tab slider "Foliage LOD distance x" shows this divided by the tolerance.
    /// </summary>
    public const float DefaultLodTolerance = 4f;

    /// <summary>
    /// How many radii of the mesh one radian of shading deviation (<see cref="FoliageLodSet.NormalAngles"/>) counts as in a level's deviation.
    /// </summary>
    public float LodNormalWeight { get; set; } = 0.02f;

    /// <summary>
    /// The Meitou <c>screen-lod</c> switch (docs/render-foliage.md, "Screen-size LOD"; <see cref="FoliageScreenLod"/>): a foliage mesh whose
    /// triangles are under <see cref="ScreenLodTriPixels"/> square pixels on the screen shows no more detail, so a generated level (TERRAIN-mode
    /// meshes) is allowed with up to <see cref="ScreenLodMultiple"/> times the pixel tolerance, and the billboard replaces the mesh as soon as an
    /// instance's triangles are that small, though no nearer than the atlas frame can be magnified. Off: the distances of before (levels by
    /// deviation, billboards at the impostor distance).
    /// </summary>
    public bool ScreenLod { get; set; } = true;

    /// <summary>
    /// Square pixels under which a mean triangle counts as not seen.
    /// </summary>
    public float ScreenLodTriPixels { get; set; } = 1.5f;

    /// <summary>
    /// How many times the pixel tolerance a level may deviate while the level before has sub-pixel triangles.
    /// </summary>
    public float ScreenLodMultiple { get; set; } = 2f;

    /// <summary>
    /// The render's pixels per radian for the screen-size rules: the main camera's, or a 1080-line picture's with the 50 degree field of view before
    /// the first frame.
    /// </summary>
    float ScreenLodPixelsPerRadian => LodPixelsPerRadian > 0
        ? LodPixelsPerRadian
        : Meitou.Game.Rendering.Impostors.ImpostorClass.ReferenceHeight / (2 * MathF.Tan(25 * MathF.PI / 180));

    /// <summary>
    /// The deviations the cull and <see cref="LevelNeeded"/> use for a level set in the view being drawn: with <see cref="ScreenLod"/> in a colour
    /// view the ones that count the size of the triangles.
    /// </summary>
    float[] LevelErrors(FoliageRockLod lod) => ScreenLod && _lodActive && !_lodView.Ortho ? lod.Effective(LodTolerance, ScreenLodTriPixels,
        ScreenLodMultiple) : lod.Relative;

    /// <summary>
    /// Each level's deviation relative to <paramref name="radius"/>: the larger of its geometric error over the radius and
    /// <see cref="LodNormalWeight"/> times its normal angle, never less than the level before's (0 for level 0).
    /// </summary>
    float[] RelativeErrors(FoliageLodSet set, float radius)
    {
        var result = new float[set.Levels];
        for (int i = 1; i < result.Length; i++) result[i] = Math.Max(result[i - 1], Math.Max(set.Errors[i] / radius,
                LodNormalWeight * set.NormalAngles[i]));
        return result;
    }

    /// <summary>
    /// How many shadow texels a level may deviate in a cascade.
    /// </summary>
    public float LodShadowTolerance { get; set; } = 2f;

    /// <summary>
    /// The shadow cascade being drawn: its texel in world units (0: unknown, no levels in it).
    /// </summary>
    float _lodTexel;
    /// <summary>
    /// The cascade being drawn is a far one with coarser casters (<see cref="ShadowLod.RockTolerance"/>).
    /// </summary>
    bool _lodCoarse;

    /// <summary>
    /// The view being drawn uses generated levels (set by <see cref="Draw"/> before the work list is built).
    /// </summary>
    bool _lodActive;
    /// <summary>
    /// What the current view tells the cull about the levels.
    /// </summary>
    FoliageLodView _lodView;

    /// <summary>
    /// The level jobs of the TERRAIN-mode meshes and what they did.
    /// </summary>
    readonly FoliageLodJobs _lodJobs;

    /// <summary>
    /// Generated levels: the meshes built, read from the cache, found to need none; the cache bytes written; CPU time on the workers (building,
    /// reading).
    /// </summary>
    public string LodSummary => _lodJobs.Summary;

    /// <summary>
    /// Starts, collects and uploads the level jobs of the resident TERRAIN-mode meshes (once a frame, from <see cref="Update"/>;
    /// <see cref="FoliageLodJobs.Pump"/>).
    /// </summary>
    void PumpLods()
    {
        // The upload path needs a frame the host owns; Metal's generated levels are not wired yet.
        _lodJobs.Pump(Lod && GpuCull && Gpu is GpuContext, _assetsByMesh.Values);
        foreach (var (a, main, set) in _lodJobs.Finished) QueueLodUpload(a, main, set);
    }

    /// <summary>
    /// Uploads a mesh's levels (render thread, as a step of the frame's upload queue); dropped when the mesh was unloaded meanwhile.
    /// </summary>
    void QueueLodUpload(FoliageMeshAsset a, FoliageGpuMesh main, FoliageLodSet set)
    {
        _uploads.Enqueue(() =>
        {
            if (!a.Resident || !ReferenceEquals(a.Main, main) || a.Lod is not null) return;
            Gpu.EnsureFrame();
            var parts = new FoliagePartLod[main.Parts.Count];
            long bytes = 0;
            for (int p = 0; p < parts.Length; p++)
            {
                var levels = set.Indices[p];
                var first = new int[set.Levels];
                var count = new int[set.Levels];
                int total = 0;
                for (int l = 1; l < set.Levels; l++)
                {
                    first[l] = total;
                    count[l] = levels[l - 1].Length;
                    total += count[l];
                }
                var indices = DeviceBuffer.Create(Gpu, (ulong)total * sizeof(uint), BufferUse.Index, MeshAllocationName);
                ulong at = 0;
                for (int l = 1; l < set.Levels; l++)
                {
                    var bytesOfLevel = MemoryMarshal.AsBytes(levels[l - 1].AsSpan());
                    for (int s = 0; s < bytesOfLevel.Length; s += SlabBytes)
                    {
                        using var batch = Gpu.Uploads.Begin();
                        batch.Write(indices, at + (ulong)s, bytesOfLevel.Slice(s, Math.Min(SlabBytes, bytesOfLevel.Length - s)));
                    }
                    at += (ulong)bytesOfLevel.Length;
                }
                bytes += (long)total * sizeof(uint);
                var bindings = MeshBindings.Of(VertexAttributes(main.Parts[p].Vertices), indices);
                parts[p] = new FoliagePartLod { Indices = indices, Bindings = bindings, First = first, Count = count };
            }
            a.Lod = new FoliageRockLod
            {
                AllLevels = set.Levels,
                LegacyLevels = 1 + set.NormalAngles.Skip(1).TakeWhile(x => x <= FoliageLodBuilder.LegacyMaxNormalAngle).Count(),
                Relative = RelativeErrors(set, a.Radius),
                TriangleExtent = [.. set.Triangles.Select(t => FoliageScreenLod.LevelExtent(a.TriExtent, a.Triangles, t) / a.Radius)],
                Parts = parts,
                Set = set,
                Bytes = bytes
            };
            a.Bytes += bytes;
            _residentMeshBytes += bytes;
        });
    }

    /// <summary>
    /// Drops a mesh's levels with the mesh (unloaded): the next time it is resident they are made, or read from the cache, again.
    /// </summary>
    void DropLods(FoliageMeshAsset a)
    {
        if (a.Lod is { } lod)
        {
            lod.Dispose();
            a.Bytes -= lod.Bytes;
            _residentMeshBytes -= lod.Bytes;
            a.Lod = null;
        }
        a.LodDone = false;
    }

    /// <summary>
    /// Whether a work-list entry can have instances in this view: always for an ordinary one; for a generated level's, only when some instance of the
    /// group can pick that level (a coarser level needs a far enough instance or, in a cascade, a small enough one), and when not all of them pick a
    /// still coarser one. Each test is made with the group's box and radii and a 1 % margin the safe way, so it never leaves out a level that has an
    /// instance; a draw it saves costs the GPU a little even with no instance.
    /// </summary>
    bool LevelNeeded(in FoliageGpuEntry e, Vector3 eye)
    {
        if (e.Group.Asset.WorkLod is not { } lod || !e.Group.Asset.Terrain || !IsRockBatch(e.Batch)) return true;
        if (!_lodActive) return e.Level == 0;
        var g = e.Group;
        var relative = LevelErrors(lod);
        float tolerance = _lodView.Tolerance, scale = _lodView.Scale, growFrom = _lodView.FarDistance;

        // The size at which level k stops being allowed: the largest (radius over distance, or over texel) it takes.
        float far = float.MaxValue, near = 0;
        if (!_lodView.Ortho)
        {
            near = Vector3.Distance(eye, Vector3.Clamp(eye, g.BoundMin, g.BoundMax));
            var farthest = Vector3.Max(Vector3.Abs(eye - g.BoundMin), Vector3.Abs(eye - g.BoundMax));
            far = farthest.Length();
        }
        bool Allowed(int level)   // some instance may pick a level at least this coarse
        {
            if (level == 0) return true;

            // radii per radius of the mesh: allowed when distance >= r (1 + need), or in a cascade when r <= tolerance texel / error
            float need = relative[level] * scale / tolerance;
            if (_lodView.Ortho) return g.MinRadius * relative[level] * scale <= tolerance * 1.01f;
            if (growFrom > 0)
            {
                // The tolerance grows with the distance past growFrom (the cull kernel): allowed when r * error <= tolerance * max(1, gap / growFrom)
                // * gap at the farthest gap.
                float gap = far * 1.01f - g.MinRadius;   // the farthest an instance can be: a larger gap only allows more, the safe side
                return gap > 0 && g.MinRadius * relative[level] * scale <= tolerance * Math.Max(1, gap / growFrom) * gap;
            }
            return far >= g.MinRadius * (1 + need) * 0.99f;
        }
        bool Surpassed(int level)   // every instance picks a coarser level
        {
            if (level + 1 >= lod.Levels) return false;
            float need = relative[level + 1] * scale / tolerance;
            if (!_lodView.Ortho && growFrom > 0)
            {
                // the nearest an instance can be: the nearest is the hardest to satisfy, so a smaller gap is the safe side
                float gap = near * 0.99f - g.MaxRadius;
                return gap > 0 && g.MaxRadius * relative[level + 1] * scale <= tolerance * Math.Max(1, gap / growFrom) * gap;
            }
            return _lodView.Ortho ? g.MaxRadius * relative[level + 1] * scale <= tolerance * 0.99f : near >= g.MaxRadius * (1 + need) * 1.01f;
        }
        return Allowed(e.Level) && !Surpassed(e.Level);
    }

    /// <summary>
    /// Whether <paramref name="batch"/> is one of the TERRAIN-mode rock batches (they follow the mesh batches in the GPU order).
    /// </summary>
    bool IsRockBatch(int batch) => batch >= _gpuOrder.Count && batch < _gpuOrder.Count + _gpuRockOrder.Count;

    /// <summary>
    /// The work lists of this frame have generated levels (the switch and the GPU cull).
    /// </summary>
    bool LodWanted => Lod && GpuCull && Gpu is GpuContext;

    /// <summary>
    /// Whether this view picks generated levels, and how it measures them.
    /// </summary>
    FoliageLodView CurrentLodView(float eyeY)
    {
        _lodActive = false;
        if (!LodWanted) return default;
        if (_depthPass)
        {
            if (!(_lodTexel > 0) || !(LodShadowTolerance > 0)) return default;
            _lodActive = true;
            return new FoliageLodView(0, 1 / _lodTexel, ShadowLod.RockTolerance(LodShadowTolerance, _lodCoarse), true);
        }
        if (!(LodPixelsPerRadian > 0) || !(LodTolerance > 0)) return default;
        _lodActive = true;
        return new FoliageLodView(eyeY, LodPixelsPerRadian, LodTolerance, false, LodFar && LodFarDistance > 0 ? LodFarDistance : 0);
    }
}