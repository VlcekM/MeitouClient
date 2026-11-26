using System.Numerics;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// The grass in the Metal traced sun's scene (docs/render-shadows.md "Traced sun", docs/render-gi.md "Metal"): <see cref="TracedGrass"/>'s rules
/// (the patches near the eye nearest first, the budgets, the interior masks' cuts, the near patches' sway) with its expansion kernel
/// (<see cref="TracedGrass.Expand"/>), a bottom level a patch. The blades map of the lamps' tiles is not made: Metal's lamp shadows trace every pixel.
/// Render thread only.
/// </summary>
sealed unsafe class MetalTracedGrass(MetalContext ctx) : IDisposable
{
    #region State

    /// <summary>
    /// Each patch's structure, by its key.
    /// </summary>
    readonly Dictionary<object, Entry> _entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// This frame's patches near the eye, nearest first.
    /// </summary>
    readonly List<FoliageRenderer.RayGrassPatch> _patches = [];

    /// <summary>
    /// The patches in this frame's scene, with their sprites.
    /// </summary>
    readonly List<(Entry Entry, uint Sprite)> _shown = [];

    /// <summary>
    /// This frame's expansions: the patch, its entry, the blades and cards, the sway (a length of 0: at rest), whether its structure is built
    /// (else refitted) after them, and the page's masks.
    /// </summary>
    readonly List<(FoliageRenderer.RayGrassPatch Patch, Entry Entry, int Blades, int Cards, Vector4 Sway, bool Build, InteriorMaskCut[] Cuts)>
        _jobs = [];

    /// <summary>
    /// The masks that may hold a card of the page being placed.
    /// </summary>
    readonly List<InteriorMaskCut> _pageCuts = [];

    /// <summary>
    /// The expansion kernel and its pipeline (made on first use).
    /// </summary>
    (MetalProgram Program, GpuComputePipeline Pipeline) _expand;

    /// <summary>
    /// The index buffer every patch shares (<see cref="TracedGrass.CardIndices"/>).
    /// </summary>
    MTLBuffer _indices;

    /// <summary>
    /// Bytes the entries' structures and vertices hold.
    /// </summary>
    long _bytes;

    /// <summary>
    /// The memory allowance for this update's grass patches.
    /// </summary>
    long _budget = TracedGrass.MaxBytes;

    /// <summary>
    /// This update's number, how far the pages are traced, the tallest blade traced and the eye.
    /// </summary>
    long _frame;

    /// <summary>
    /// See <see cref="_frame"/>.
    /// </summary>
    float _range = TracedGrass.MinRange, _tallest;

    /// <summary>
    /// See <see cref="_frame"/>.
    /// </summary>
    Vector3 _eye;

    /// <summary>
    /// A patch's structure and the vertices it is built over.
    /// </summary>
    sealed class Entry : IDisposable
    {
        /// <summary>
        /// The bottom level (null until built), its descriptor and, when it sways, its refit scratch.
        /// </summary>
        public MTLAccelerationStructure? Structure;

        /// <summary>
        /// See <see cref="Structure"/>.
        /// </summary>
        public MTLPrimitiveAccelerationStructureDescriptor Descriptor;

        /// <summary>
        /// See <see cref="Structure"/>.
        /// </summary>
        public MTLBuffer RefitScratch;

        /// <summary>
        /// The cards' corners, relative to <see cref="Corner"/>.
        /// </summary>
        public required MTLBuffer Vertices;

        /// <summary>
        /// The cards' geometry over <see cref="Vertices"/>.
        /// </summary>
        public required MTLAccelerationStructureTriangleGeometryDescriptor Geometry;

        /// <summary>
        /// The patch's record for the hit shaders (its sprite set each frame).
        /// </summary>
        public required GeometryRecord Record;

        /// <summary>
        /// The page's corner, the blades built over, the hash of the masks built with, whether it can be refitted (the patch sways).
        /// </summary>
        public required Vector3 Corner;

        /// <summary>
        /// See <see cref="Corner"/>.
        /// </summary>
        public required int Blades, CutHash;

        /// <summary>
        /// See <see cref="Corner"/>.
        /// </summary>
        public required bool Updatable;

        /// <summary>
        /// The update it was built in and last shown in.
        /// </summary>
        public long Built, LastUsed;

        /// <summary>
        /// The bytes it holds.
        /// </summary>
        public long Bytes => (long)Vertices.Length + (Structure is { } s ? (long)s.Size : 0);

        /// <summary>
        /// Releases the structure, its descriptors, its scratch and the vertices.
        /// </summary>
        public void Dispose()
        {
            Structure?.Dispose();
            if (Descriptor.NativePtr != IntPtr.Zero) Descriptor.Dispose();
            Geometry.Dispose();
            if (RefitScratch.NativePtr != IntPtr.Zero) RefitScratch.Dispose();
            Vertices.Dispose();
        }
    }

    #endregion

    #region Update

    /// <summary>
    /// The mask kernel's grass constants (<see cref="TracedGrass.RayParamsFor"/>; zero while nothing is traced).
    /// </summary>
    public Vector4 RayParams => _shown.Count == 0 ? default : TracedGrass.RayParamsFor(_range, _tallest);

    /// <summary>
    /// True when this update swayed a structure (the scene's top level is built again over it).
    /// </summary>
    public bool Moved { get; private set; }

    /// <summary>
    /// The patches near the eye shown: built when due and in budget (a built one whose blades or masks changed built again), the near ones' cards
    /// swayed and their structures refitted; the kernel first, then <paramref name="builds"/>.
    /// </summary>
    public void Update(MetalBuilds builds, FoliageRenderer foliage, Vector3 eye, IReadOnlyList<InteriorMaskCut> cuts, float reach)
    {
        _frame++;
        _range = Math.Clamp(reach, TracedGrass.MinRange, TracedGrass.MaxRange);
        _eye = eye;
        _budget = TracedGrass.BudgetFor(ctx.VideoMemoryBudget, foliage.Guard?.Pressure == true);
        MakeRoom(-1);
        foliage.RayGrass(eye, _range, _patches);
        _shown.Clear();
        _jobs.Clear();
        _tallest = 0;

        ShowPatches(foliage, cuts);
        Moved = _jobs.Count > 0;
        if (Moved) ExpandAndFit(builds, foliage);
        Evict();
    }

    /// <summary>
    /// Each patch of the frame shown, a job for each built or swayed.
    /// </summary>
    void ShowPatches(FoliageRenderer foliage, IReadOnlyList<InteriorMaskCut> cuts)
    {
        int builds = 0, triangles = 0, swayTriangles = 0;
        float phase = foliage.RayGrassPhase;
        foreach (var patch in _patches)
        {
            if (_shown.Count >= TracedGrass.MaxInstances) break;

            var entry = Current(patch, cuts, ref builds, ref triangles);
            if (entry is null) continue;

            entry.LastUsed = _frame;
            _shown.Add((entry, patch.Sprite));
            _tallest = MathF.Max(_tallest, patch.Size.W);

            int cards = patch.Cross ? 2 : 1, swayCost = entry.Blades * cards * 2;
            bool swayable = TracedGrass.Sways(patch) && entry.Updatable && entry.Built != _frame
                && entry.Blades == Math.Min(patch.Blades, TracedGrass.MaxBlades);
            if (!swayable || swayTriangles + swayCost > TracedGrass.MaxSwayTriangles) continue;

            swayTriangles += swayCost;
            TracedGrass.CollectCuts(patch, cuts, _pageCuts);
            var sway = new Vector4(patch.Sway, phase, FoliageRenderer.RayGrassFrequency, 0);
            _jobs.Add((patch, entry, entry.Blades, cards, sway, false, [.. _pageCuts]));
        }
    }

    /// <summary>
    /// The patch's entry: its own when its blades and masks are those it was built over, else a new one queued to be built when the frame's budget
    /// allows (the old one, or none, until then).
    /// </summary>
    Entry? Current(in FoliageRenderer.RayGrassPatch patch, IReadOnlyList<InteriorMaskCut> cuts, ref int builds, ref int triangles)
    {
        int blades = Math.Min(patch.Blades, TracedGrass.MaxBlades);
        int hash = TracedGrass.CollectCuts(patch, cuts, _pageCuts);
        bool have = _entries.TryGetValue(patch.Key, out var entry);

        // A patch come within the sway's reach is built again to be refittable.
        if (have && entry!.Blades == blades && entry.CutHash == hash && (entry.Updatable || !TracedGrass.Sways(patch)))
            return entry;

        int cards = patch.Cross ? 2 : 1, newTriangles = blades * cards * 2;
        if (builds >= TracedGrass.MaxBuilds || triangles + newTriangles > TracedGrass.MaxBuildTriangles) return entry;
        if (_bytes >= _budget && !MakeRoom(patch.Distance)) return entry;

        builds++;
        triangles += newTriangles;
        if (have) Drop(patch.Key, entry!);

        var made = NewEntry(patch, blades, cards, hash);
        _entries[patch.Key] = made;
        _jobs.Add((patch, made, blades, cards, default, true, [.. _pageCuts]));
        return made;
    }

    /// <summary>
    /// Drops the farthest structures no nearer patch asked for this update until the memory is under its limit; false when none is left to drop.
    /// </summary>
    bool MakeRoom(float distance)
    {
        while (_bytes >= _budget)
        {
            object? farthestKey = null;
            Entry? farthest = null;
            float far = distance;
            foreach (var (key, e) in _entries)
            {
                if (e.LastUsed == _frame) continue;

                float d = Vector2.Distance(new Vector2(e.Corner.X, e.Corner.Z), new Vector2(_eye.X, _eye.Z));
                if (d > far) (farthestKey, farthest, far) = (key, e, d);
            }

            if (farthest is null) return false;

            Drop(farthestKey!, farthest);
        }

        return true;
    }

    /// <summary>
    /// A vertex buffer for the patch's cards, its geometry and record (the structure is built after the kernel fills it).
    /// </summary>
    Entry NewEntry(in FoliageRenderer.RayGrassPatch patch, int blades, int cards, int hash)
    {
        EnsureIndices();
        int quads = blades * cards;
        ulong vertexBytes = (ulong)Math.Max(quads, 1) * TracedGrass.VerticesPerQuad * 12;
        var vertices = ctx.Device.NewBuffer(vertexBytes, MTLResourceOptions.ResourceStorageModePrivate);
        vertices.Label = (NSString)"metal traced grass vertices";
        var geometry = new MTLAccelerationStructureTriangleGeometryDescriptor
        {
            VertexBuffer = vertices,
            VertexStride = 12,
            VertexFormat = MTLAttributeFormat.Float3,
            IndexBuffer = _indices,
            IndexType = MTLIndexType.UInt32,
            TriangleCount = (ulong)quads * 2,
            Opaque = false,
        };
        var record = new GeometryRecord
        {
            Vertices = vertices.GpuAddress, Indices = _indices.GpuAddress, Stride = 12, Kind = GeometryRecord.Grass,
            AlphaThreshold = TracedGrass.SpriteThreshold,
        };
        var entry = new Entry
        {
            Vertices = vertices, Geometry = geometry, Record = record, Corner = patch.Corner, Blades = blades, CutHash = hash,
            Updatable = TracedGrass.Sways(patch), Built = _frame, LastUsed = _frame,
        };
        _bytes += entry.Bytes;
        return entry;
    }

    #endregion

    #region Kernel and structures

    /// <summary>
    /// The expansion kernel over every job, then each job's structure built or refitted (after the frame's other bottom levels).
    /// </summary>
    void ExpandAndFit(MetalBuilds builds, FoliageRenderer foliage)
    {
        builds.End();
        Expand(foliage);
        foreach (var job in _jobs)
        {
            var entry = job.Entry;
            if (job.Build) Build(builds, entry);
            else builds.Refit(entry.Structure!.Value, entry.Descriptor, entry.RefitScratch);
        }
    }

    /// <summary>
    /// The kernel once a job (<see cref="TracedGrass.Expand"/>: the arena's blades into the job's vertices, the page's masks cut away), the arena
    /// and the targets made resident.
    /// </summary>
    void Expand(FoliageRenderer foliage)
    {
        _expand = _expand.Program is null ? MakeExpand() : _expand;
        var (arena, _) = foliage.RayGrassBlades;
        var cmd = (MetalCommandList)ctx.BeginNative("traced grass");
        cmd.BindPipeline(_expand.Pipeline);
        var e = cmd.ComputeEncoder();
        e.SetBuffer(ctx.BufferAt(arena), 0, 0);
        foreach (var (patch, entry, blades, cards, sway, _, cuts) in _jobs)
        {
            _pageCuts.Clear();
            _pageCuts.AddRange(cuts);
            var parameters = TracedGrass.ExpandParams(patch, blades, cards, sway, _pageCuts);
            var constants = ctx.Frame.AllocateConstants((ulong)sizeof(TracedGrass.Params), 256);
            *(TracedGrass.Params*)constants.Pointer = parameters;
            e.SetBuffer(entry.Vertices, 0, 1);
            e.SetBuffer(ctx.BufferAt(constants.Handle), constants.Offset, 2);
            cmd.Dispatch((uint)((blades + 63) / 64));
        }

        cmd.EndCompute();
        ctx.EndNative(cmd);
    }

    /// <summary>
    /// The structure of <paramref name="entry"/>: refittable when the patch sways, else built for fast rays (the old one after the frames in flight).
    /// </summary>
    void Build(MetalBuilds builds, Entry entry)
    {
        _bytes -= entry.Bytes;
        if (entry.Updatable)
            (entry.Structure, entry.Descriptor, entry.RefitScratch) = builds.Refittable("metal traced grass", [entry.Geometry]);
        else entry.Structure = builds.Bottom("metal traced grass", [entry.Geometry]);
        entry.Built = _frame;
        _bytes += entry.Bytes;
    }

    /// <summary>
    /// The expansion kernel.
    /// </summary>
    (MetalProgram, GpuComputePipeline) MakeExpand()
    {
        var shaders = (MetalShaders)ctx.Shaders;
        var program = shaders.ComputeDiscrete(TracedGrass.Expand, "metal traced grass");
        return (shaders.Backend(program), ctx.Pipelines.Get(new ComputePipelineDesc(program, "metal traced grass")));
    }

    /// <summary>
    /// The card indices every patch shares (two triangles a card over its corners in order).
    /// </summary>
    void EnsureIndices()
    {
        if (_indices.NativePtr != IntPtr.Zero) return;

        var list = TracedGrass.CardIndices();
        _indices = ctx.Device.NewBuffer((ulong)list.Length * 4, MTLResourceOptions.ResourceStorageModeShared);
        _indices.Label = (NSString)"metal traced grass indices";
        list.AsSpan().CopyTo(new Span<uint>((void*)_indices.Contents, list.Length));
    }

    #endregion

    #region Scene

    /// <summary>
    /// The shown patches' instances (relative to <paramref name="origin"/>, at their corners) and records, with this frame's sprites (a texture can
    /// move in the table): cut-outs, so not opaque.
    /// </summary>
    public void Emit(Action<MetalBottomLevel, Matrix4x4, byte, uint> add, List<GeometryRecord> records, Vector3 origin, int maxInstances,
        int maxRecords, Func<int> instances, uint options)
    {
        foreach (var (entry, sprite) in _shown)
        {
            if (entry.Structure is not { } structure) continue;
            if (instances() >= maxInstances || records.Count >= maxRecords) break;

            var record = entry.Record;
            record.AlphaTexture = sprite;
            var level = new MetalBottomLevel(structure, [record], [], [structure.NativePtr, entry.Vertices.NativePtr, _indices.NativePtr], []);
            add(level, Matrix4x4.CreateTranslation(entry.Corner - origin), TracedSunShaders.Grass, options);
            records.Add(record);
        }
    }

    /// <summary>
    /// The traced grass went off: its structures go (the kernel and the indices stay).
    /// </summary>
    public void Release()
    {
        _shown.Clear();
        foreach (var key in _entries.Keys.ToArray()) Drop(key, _entries[key]);
    }

    /// <summary>
    /// A line for the log.
    /// </summary>
    public string Describe() => $"{_entries.Count} grass structures ({_bytes >> 20} MB), {_shown.Count} traced";

    /// <summary>
    /// Drops the entries unused for more than <see cref="TracedGrass.KeepFrames"/> updates.
    /// </summary>
    void Evict()
    {
        foreach (var key in _entries.Where(e => _frame - e.Value.LastUsed > TracedGrass.KeepFrames).Select(e => e.Key).ToList())
            Drop(key, _entries[key]);
    }

    /// <summary>
    /// An entry's structure and vertices go (after the frames in flight).
    /// </summary>
    void Drop(object key, Entry entry)
    {
        _bytes -= entry.Bytes;
        ctx.DeferDelete(entry.Dispose);
        _entries.Remove(key);
    }

    /// <summary>
    /// Releases the structures and the indices.
    /// </summary>
    public void Dispose()
    {
        Release();
        if (_indices.NativePtr != IntPtr.Zero) ctx.DeferDelete(_indices.Dispose);
    }

    #endregion
}
