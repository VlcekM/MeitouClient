using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Objects;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The grass in the traced sun's scene (docs/render-shadows.md "Traced sun"): a page's patch near the eye becomes a bottom-level structure of its
/// cards at rest, one rigid instance under a mask of its own (the probes never see it), built within a budget and dropped when unused; the patches
/// near the eye follow the drawn blades' sway (their vertices expanded again and their structures refitted each frame, <see cref="SwayReach"/>).
/// The blades live on the GPU only, so a kernel expands them (<see cref="Expand"/>); a blade in an interior mask collapses to a point. A card's
/// vertices are its corners in order, which is all a hit needs for its coordinates. Render thread only.
/// </summary>
sealed unsafe class TracedGrass : IDisposable
{
    /// <summary>
    /// How far from the eye, across the ground, pages are traced at least: the scene's reach (where the traced sun's share ends) within
    /// <see cref="MinRange"/> and <see cref="MaxRange"/>.
    /// </summary>
    public const float MinRange = 1200;

    /// <summary>
    /// How far from the eye, across the ground, pages are traced at most.
    /// </summary>
    public const float MaxRange = 2400;

    /// <summary>
    /// The budget: structures built a frame.
    /// </summary>
    const int MaxBuilds = 4;

    /// <summary>
    /// The budget: triangles built a frame.
    /// </summary>
    const int MaxBuildTriangles = 600_000;

    /// <summary>
    /// The budget: the frames an unused structure is kept.
    /// </summary>
    const int KeepFrames = 600;

    /// <summary>
    /// The budget: the bytes held.
    /// </summary>
    const long MaxBytes = 512L << 20;

    /// <summary>
    /// Most patches traced.
    /// </summary>
    public const int MaxInstances = 1024;

    /// <summary>
    /// Most blades of one patch (the first ones: the density's order keeps a prefix even).
    /// </summary>
    const int MaxBlades = 1 << 16;

    /// <summary>
    /// Most cards of one patch: the shared index buffer's size.
    /// </summary>
    const int MaxQuads = MaxBlades * 2;

    /// <summary>
    /// A card's corners.
    /// </summary>
    const int VerticesPerQuad = 4;

    /// <summary>
    /// A card's indices: two triangles.
    /// </summary>
    const int IndicesPerQuad = 6;

    /// <summary>
    /// Most masks a page's kernel takes.
    /// </summary>
    const int MaxCuts = 32;

    /// <summary>
    /// How far from the eye, across the ground, a patch's cards sway with the drawn blades. Past it the cards hold still: a few units of sway are not
    /// seen in a far shadow.
    /// </summary>
    const float SwayReach = 800;

    /// <summary>
    /// The triangles refitted a frame at most (nearest first); past them the cards hold still.
    /// </summary>
    const int MaxSwayTriangles = 1_000_000;

    /// <summary>
    /// World units: what a blade's own ray does not see of other cards (a card's sway from its traced copy, a twin on top of it).
    /// </summary>
    const float BlindRange = 4;

    /// <summary>
    /// World units: the fade to all other cards past <see cref="BlindRange"/>.
    /// </summary>
    const float BlindFade = 8;

    /// <summary>
    /// The sprite's alpha a hit keeps from.
    /// </summary>
    const float SpriteThreshold = 0.6f;

    /// <summary>
    /// How far from the eye, across the ground, pages are traced this frame.
    /// </summary>
    float _range = MinRange;

    /// <summary>
    /// The device the structures and buffers live on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The expansion kernel's program.
    /// </summary>
    readonly GpuProgram _program;

    /// <summary>
    /// The expansion kernel's pipeline.
    /// </summary>
    readonly GpuComputePipeline _pipeline;

    /// <summary>
    /// The index buffer every patch shares (<see cref="CardIndices"/>).
    /// </summary>
    readonly DeviceBuffer _indices;

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
    /// The keys of the entries unused for too long.
    /// </summary>
    readonly List<object> _stale = [];

    /// <summary>
    /// The entries swayed and refitted this frame.
    /// </summary>
    readonly List<Entry> _swayed = [];

    /// <summary>
    /// The masks that may hold a card of the page being placed.
    /// </summary>
    readonly List<InteriorMaskCut> _pageCuts = [];

    /// <summary>
    /// Bytes the entries' structures and vertices hold.
    /// </summary>
    long _bytes;

    /// <summary>
    /// The tallest blade of this frame's patches.
    /// </summary>
    float _tallest;

    /// <summary>
    /// This frame's eye.
    /// </summary>
    Vector3 _eye;

    /// <summary>
    /// A patch's blades into its vertices, one group per 64: <c>blades[(first + b) * 5..]</c> is x, y, z, scale 0..1, yaw (<c>FoliageGrassField</c>,
    /// as the blade program reads them), the card as that program places it with no sinking and with the sway of <c>sway</c> (length, phase,
    /// frequency; a length of 0: at rest), relative to the corner. A blade whose foot or top is in a mask's box leaves no area.
    /// </summary>
    public const string Expand = """
        #version 460
        layout(local_size_x = 64) in;
        layout(set = 0, binding = 0, std430) readonly buffer Blades { float blades[]; };
        layout(set = 0, binding = 1, std430) writeonly buffer Vertices { float vertices[]; };
        layout(set = 0, binding = 2, std140) uniform Params
        {
            vec4 size;       // min width, max width, min height, max height
            vec4 corner;     // what the vertices are relative to
            vec4 sway;       // the top edge's sway: length, phase, frequency (the blade program's)
            uvec4 counts;    // blades, cards a blade, mask boxes, the first blade
            mat4 toBox[32];
            vec4 boxMin[32];
            vec4 boxMax[32];
        } p;

        bool inMask(vec3 world)
        {
            for (uint i = 0u; i < p.counts.z; i++)
            {
                vec3 b = (p.toBox[i] * vec4(world, 1.0)).xyz;
                if (all(greaterThanEqual(b, p.boxMin[i].xyz)) && all(lessThanEqual(b, p.boxMax[i].xyz))) return true;
            }
            return false;
        }

        void main()
        {
            uint b = gl_GlobalInvocationID.x;
            if (b >= p.counts.x) return;
            uint at = (p.counts.w + b) * 5u;
            vec3 root = vec3(blades[at], blades[at + 1u], blades[at + 2u]);
            float scale = blades[at + 3u];
            float width = mix(p.size.x, p.size.y, scale), height = mix(p.size.z, p.size.w, scale);
            bool cut = inMask(root) || inMask(root + vec3(0.0, height, 0.0));
            for (uint q = 0u; q < p.counts.y; q++)
            {
                float yaw = blades[at + 4u] + (q == 1u ? 1.5707963 : 0.0);
                vec3 side = vec3(cos(yaw), 0.0, sin(yaw));
                for (uint c = 0u; c < 4u; c++)
                {
                    float u = float(c & 1u), v = float(c >> 1u);
                    vec3 place = cut ? root : root + side * (u - 0.5) * width + vec3(0.0, (1.0 - v) * height, 0.0);
                    if (!cut && v == 0.0) place.x += p.sway.x * sin(p.sway.y + root.x * p.sway.z);
                    uint to = ((b * p.counts.y + q) * 4u + c) * 3u;
                    vec3 local = place - p.corner.xyz;
                    vertices[to] = local.x;
                    vertices[to + 1u] = local.y;
                    vertices[to + 2u] = local.z;
                }
            }
        }
        """;

    /// <summary>
    /// A patch's structure and the vertices it is built over.
    /// </summary>
    sealed class Entry
    {
        /// <summary>
        /// The bottom level over the patch's cards.
        /// </summary>
        public required AccelerationStructure Blas;

        /// <summary>
        /// The cards' corners, relative to <see cref="Corner"/>.
        /// </summary>
        public required DeviceBuffer Vertices;

        /// <summary>
        /// The patch's record for the hit shaders (its sprite set each frame).
        /// </summary>
        public required GeometryRecord Record;

        /// <summary>
        /// The page's corner the vertices are relative to.
        /// </summary>
        public required Vector3 Corner;

        /// <summary>
        /// Blades the structure was built over.
        /// </summary>
        public required int Blades;

        /// <summary>
        /// The hash of the masks the structure was built with.
        /// </summary>
        public required int CutHash;

        /// <summary>
        /// The cards' geometry over <see cref="Vertices"/>.
        /// </summary>
        public required TriangleGeometry Geometry;

        /// <summary>
        /// Whether the structure can be refitted (the patch sways).
        /// </summary>
        public required bool Updatable;

        /// <summary>
        /// The frame the structure was built.
        /// </summary>
        public long Built;

        /// <summary>
        /// The frame the patch was last shown.
        /// </summary>
        public long LastUsed;
    }

    /// <summary>
    /// The kernel's constants, as <see cref="Expand"/>'s <c>Params</c> reads them.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        /// <summary>
        /// Min width, max width, min height, max height.
        /// </summary>
        public Vector4 Size;

        /// <summary>
        /// What the vertices are relative to.
        /// </summary>
        public Vector4 Corner;

        /// <summary>
        /// The top edge's sway: length, phase, frequency.
        /// </summary>
        public Vector4 Sway;

        /// <summary>
        /// Blades to expand.
        /// </summary>
        public uint Blades;

        /// <summary>
        /// Cards a blade.
        /// </summary>
        public uint Cards;

        /// <summary>
        /// Mask boxes.
        /// </summary>
        public uint Cuts;

        /// <summary>
        /// The patch's first blade in the arena.
        /// </summary>
        public uint First;

        /// <summary>
        /// Each mask's world-to-box matrix.
        /// </summary>
        public fixed float ToBox[MaxCuts * 16];

        /// <summary>
        /// Each mask box's minimum corner.
        /// </summary>
        public fixed float BoxMin[MaxCuts * 4];

        /// <summary>
        /// Each mask box's maximum corner.
        /// </summary>
        public fixed float BoxMax[MaxCuts * 4];
    }

    /// <summary>
    /// The expansion kernel and the shared index buffer.
    /// </summary>
    public TracedGrass(GpuContext ctx)
    {
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(Expand, "traced grass",
            [(0, DescriptorType.StorageBuffer), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer)]);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "traced grass"));
        _indices = DeviceBuffer.Create(ctx, MaxQuads * IndicesPerQuad * 4ul, BufferUse.Index | BufferUse.RayInput, "traced grass indices");
        ctx.Uploads.Write(_indices, 0, MemoryMarshal.AsBytes(CardIndices()));
    }

    /// <summary>
    /// The mask pass's constants (<see cref="GiScene.GrassRayParams"/>).
    /// </summary>
    public Vector4 RayParams => _shown.Count == 0
        ? default
        : new Vector4(_range, _tallest, BlindRange, BlindFade);

    /// <summary>
    /// The triangles refitted this frame (0: every structure is as it was, the scene's top level need not follow).
    /// </summary>
    public int SwayedTriangles { get; private set; }

    /// <summary>
    /// The patches near the eye that are not built yet, nearest first, within the frame's budget; a built one whose blades or masks changed is built
    /// again.
    /// </summary>
    public void Update(Func<IGpuCommandList> open, FoliageRenderer foliage, Vector3 eye, long frame, IReadOnlyList<InteriorMaskCut> cuts, float reach)
    {
        _range = Math.Clamp(reach, MinRange, MaxRange);
        _eye = eye;
        foliage.RayGrass(eye, _range, _patches);
        _shown.Clear();
        _swayed.Clear();
        _tallest = 0;

        CommandList? list = null;
        int swayTriangles = ShowPatches(open, foliage, frame, cuts, ref list);
        if (list is not null)
        {
            list.Barrier(BarrierBatch.Full);
            foreach (var entry in _swayed) entry.Blas.RefitBottom(list, [entry.Geometry]);
        }

        SwayedTriangles = swayTriangles;
        Evict(frame);
    }

    /// <summary>
    /// The frame's patches as rigid instances, with this frame's sprites in their records (a texture can move in the table).
    /// </summary>
    public void Emit(List<InstanceRecord> instances, List<GeometryRecord> records, Vector3 origin, int maxInstances, int maxRecords)
    {
        foreach (var (entry, sprite) in _shown)
        {
            if (instances.Count >= maxInstances || records.Count >= maxRecords) break;

            var world = Matrix4x4.CreateTranslation(entry.Corner - origin);
            instances.Add(InstanceRecord.Of(world, (uint)records.Count, TracedSunShaders.Grass, entry.Blas.Address));
            var record = entry.Record;
            record.AlphaTexture = sprite;
            records.Add(record);
        }
    }

    /// <summary>
    /// The traced sun went off: its structures go (the kernel and the indices stay for the next time).
    /// </summary>
    public void Release()
    {
        _shown.Clear();
        foreach (var key in _entries.Keys.ToArray()) Drop(key, _entries[key]);
    }

    /// <summary>
    /// The structures held, traced and swaying this frame, for the console.
    /// </summary>
    public string Describe()
    {
        string held = $"{_entries.Count} grass structures ({_bytes / (1024 * 1024)} MB), {_shown.Count} traced";
        string swaying = $"{_swayed.Count} swaying ({SwayedTriangles / 1000}k triangles)";

        return held + ", " + swaying;
    }

    /// <summary>
    /// The structures, the shared index buffer and the kernel go.
    /// </summary>
    public void Dispose()
    {
        foreach (var e in _entries.Values)
        {
            e.Blas.Dispose();
            e.Vertices.Dispose();
        }

        _entries.Clear();
        _indices.Dispose();
        _ctx.Shaders.Dispose(_program);
    }

    /// <summary>
    /// Two triangles a card, over its corners in order (u the first bit, v the second).
    /// </summary>
    static ReadOnlySpan<uint> CardIndices()
    {
        var list = new uint[MaxQuads * IndicesPerQuad];
        for (int q = 0; q < MaxQuads; q++)
        {
            uint b = (uint)(q * VerticesPerQuad);
            list[q * 6] = b;
            list[q * 6 + 1] = b + 1;
            list[q * 6 + 2] = b + 3;
            list[q * 6 + 3] = b;
            list[q * 6 + 4] = b + 3;
            list[q * 6 + 5] = b + 2;
        }

        return list;
    }

    /// <summary>
    /// Each patch of the frame shown, built when due and in budget, its cards expanded again with the sway when it sways and its structure can
    /// follow; <paramref name="list"/> is opened for the first sway. Returns the triangles to refit.
    /// </summary>
    int ShowPatches(Func<IGpuCommandList> open, FoliageRenderer foliage, long frame, IReadOnlyList<InteriorMaskCut> cuts, ref CommandList? list)
    {
        int builds = 0, triangles = 0, swayTriangles = 0;
        float phase = foliage.RayGrassPhase;
        foreach (var patch in _patches)
        {
            if (_shown.Count >= MaxInstances) break;

            var entry = Current(open, foliage, patch, cuts, frame, ref builds, ref triangles);
            if (entry is null) continue;

            entry.LastUsed = frame;
            _shown.Add((entry, patch.Sprite));
            _tallest = MathF.Max(_tallest, patch.Size.W);

            // Current left this patch's masks in pageCuts. A structure built this frame stands as built; a held one of another blade count does not
            // match the arena any more.
            int cards = patch.Cross ? 2 : 1, swayCost = entry.Blades * cards * 2;
            if (!Sways(patch) || !entry.Updatable || entry.Built == frame || entry.Blades != Math.Min(patch.Blades, MaxBlades)
                || swayTriangles + swayCost > MaxSwayTriangles)
                continue;

            swayTriangles += swayCost;
            list ??= (CommandList)open();
            RecordExpand(list, foliage.RayGrassBlades, entry.Vertices, patch, entry.Blades, cards,
                new Vector4(patch.Sway, phase, FoliageRenderer.RayGrassFrequency, 0));
            _swayed.Add(entry);
        }

        return swayTriangles;
    }

    /// <summary>
    /// Whether the patch's cards follow the drawn blades' sway.
    /// </summary>
    static bool Sways(in FoliageRenderer.RayGrassPatch patch) => patch.Sway != 0 && patch.Distance < SwayReach;

    /// <summary>
    /// The patch's entry: its own when its blades and masks are those it was built over, else built again when the frame's budget allows (the old
    /// one, or none, until then).
    /// </summary>
    Entry? Current(Func<IGpuCommandList> open, FoliageRenderer foliage, in FoliageRenderer.RayGrassPatch patch, IReadOnlyList<InteriorMaskCut> cuts,
        long frame, ref int builds, ref int triangles)
    {
        int blades = Math.Min(patch.Blades, MaxBlades);
        int hash = CollectCuts(patch, cuts);
        bool have = _entries.TryGetValue(patch.Key, out var entry);

        // A patch come within the sway's reach is built again to be refittable.
        if (have && entry!.Blades == blades && entry.CutHash == hash && (entry.Updatable || !Sways(patch))) return entry;

        int newTriangles = blades * (patch.Cross ? 2 : 1) * 2;
        if (builds >= MaxBuilds || triangles + newTriangles > MaxBuildTriangles) return entry;   // the old one holds until there is budget

        // At the memory's limit the nearest patches keep theirs: the patches come nearest first, so the farthest structure not yet asked for this
        // frame goes for this one. Refused instead, a field of dense grass filled the memory and a patch walked up to stayed without shadows, flat,
        // until the ones left behind aged out.
        if (_bytes >= MaxBytes && !MakeRoom(frame, patch.Distance)) return entry;

        builds++;
        triangles += newTriangles;
        if (have) Drop(patch.Key, entry!);

        return Build(open, foliage, patch, blades, hash, frame);
    }

    /// <summary>
    /// Drops the farthest structures that no patch nearer than <paramref name="distance"/> has asked for this frame until the memory is under its
    /// limit; false when none is left to drop.
    /// </summary>
    bool MakeRoom(long frame, float distance)
    {
        while (_bytes >= MaxBytes)
        {
            object? farthestKey = null;
            Entry? farthest = null;
            float far = distance;
            foreach (var (key, e) in _entries)
            {
                if (e.LastUsed == frame) continue;   // asked for this frame: nearer than this patch

                float d = Vector2.Distance(new Vector2(e.Corner.X, e.Corner.Z), new Vector2(_eye.X, _eye.Z));
                if (d > far) (farthestKey, farthest, far) = (key, e, d);
            }

            if (farthest is null) return false;

            Drop(farthestKey!, farthest);
        }

        return true;
    }

    /// <summary>
    /// The masks that may hold a card of this page, into <see cref="_pageCuts"/>, and their hash.
    /// </summary>
    int CollectCuts(in FoliageRenderer.RayGrassPatch patch, IReadOnlyList<InteriorMaskCut> cuts)
    {
        _pageCuts.Clear();
        var hash = new HashCode();
        foreach (var cut in cuts)
        {
            if (_pageCuts.Count >= MaxCuts || !Reaches(cut, patch.Corner, FoliageRenderer.RayGrassPageSize)) continue;

            _pageCuts.Add(cut);
            hash.Add(cut);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Whether the page's square reaches the mask's sphere.
    /// </summary>
    static bool Reaches(in InteriorMaskCut cut, Vector3 corner, float size)
    {
        float dx = MathF.Max(MathF.Max(corner.X - cut.Centre.X, cut.Centre.X - corner.X - size), 0);
        float dz = MathF.Max(MathF.Max(corner.Z - cut.Centre.Z, cut.Centre.Z - corner.Z - size), 0);

        return dx * dx + dz * dz <= cut.Radius * cut.Radius;
    }

    /// <summary>
    /// The patch's cards expanded at rest and a structure built over them, refittable when the patch sways.
    /// </summary>
    Entry Build(Func<IGpuCommandList> open, FoliageRenderer foliage, in FoliageRenderer.RayGrassPatch patch, int blades, int hash, long frame)
    {
        int cards = patch.Cross ? 2 : 1, quads = blades * cards;
        var vertices = DeviceBuffer.Create(_ctx, (ulong)quads * VerticesPerQuad * 12, BufferUse.Storage | BufferUse.RayInput, "traced grass vertices");
        var cmd = (CommandList)open();
        RecordExpand(cmd, foliage.RayGrassBlades, vertices, patch, blades, cards, default);
        cmd.Barrier(BarrierBatch.Full);

        ulong vertexAddress = _ctx.Device.Address(vertices.Handle), indexAddress = _ctx.Device.Address(_indices.Handle);
        var geometry = new TriangleGeometry(vertexAddress, 12, (uint)(quads * VerticesPerQuad), indexAddress, (uint)(quads * 2), false);
        bool updatable = Sways(patch);
        var blas = AccelerationStructure.BuildBottom(_ctx.Device, cmd, [geometry], "traced grass", updatable);
        if (!updatable) blas.ReleaseScratch();

        _bytes += (long)(blas.Bytes + vertices.Size);
        var entry = new Entry
        {
            Blas = blas,
            Vertices = vertices,
            Corner = patch.Corner,
            Blades = blades,
            CutHash = hash,
            LastUsed = frame,
            Built = frame,
            Geometry = geometry,
            Updatable = updatable,
            Record = new GeometryRecord
            {
                Vertices = vertexAddress,
                Indices = indexAddress,
                Stride = 12,
                Kind = GeometryRecord.Grass,
                AlphaThreshold = SpriteThreshold,
            },
        };
        _entries[patch.Key] = entry;

        return entry;
    }

    /// <summary>
    /// The kernel over the patch's blades into <paramref name="vertices"/>, with the page's masks and <paramref name="sway"/>.
    /// </summary>
    void RecordExpand(CommandList cmd, (GpuBuffer Buffer, ulong Bytes) blades, DeviceBuffer vertices, in FoliageRenderer.RayGrassPatch patch,
        int count, int cards, Vector4 sway)
    {
        var parameters = new Params
        {
            Size = patch.Size,
            Corner = new Vector4(patch.Corner, 0),
            Sway = sway,
            Blades = (uint)count,
            Cards = (uint)cards,
            Cuts = (uint)_pageCuts.Count,
            First = patch.FirstBlade,
        };
        for (int i = 0; i < _pageCuts.Count; i++)
        {
            var cut = _pageCuts[i];
            new Span<Matrix4x4>(parameters.ToBox, MaxCuts)[i] = cut.ToBox;
            new Span<Vector4>(parameters.BoxMin, MaxCuts)[i] = new Vector4(cut.Min, 0);
            new Span<Vector4>(parameters.BoxMax, MaxCuts)[i] = new Vector4(cut.Max, 0);
        }

        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        var arena = new DescriptorBufferInfo(_ctx.Device.BufferAt(blades.Buffer.Index), 0, blades.Bytes);
        var target = new DescriptorBufferInfo(_ctx.Device.BufferAt(vertices.Handle.Index), 0, vertices.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));
        var writes = stackalloc WriteDescriptorSet[3];
        writes[0] = Buffer(0, DescriptorType.StorageBuffer, &arena);
        writes[1] = Buffer(1, DescriptorType.StorageBuffer, &target);
        writes[2] = Buffer(2, DescriptorType.UniformBuffer, &uniform);

        cmd.BindPipeline(_pipeline);
        cmd.PushDescriptors(_program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 3), GpuBindPoint.Compute);
        cmd.Dispatch((uint)((count + 63) / 64));
    }

    /// <summary>
    /// A storage or uniform buffer's descriptor write at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Buffer(uint binding, DescriptorType type, DescriptorBufferInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = type,
        PBufferInfo = info,
    };

    /// <summary>
    /// The entries unused for more than <see cref="KeepFrames"/> go.
    /// </summary>
    void Evict(long frame)
    {
        _stale.Clear();
        foreach (var (key, entry) in _entries)
            if (frame - entry.LastUsed > KeepFrames) _stale.Add(key);

        foreach (var key in _stale) Drop(key, _entries[key]);
    }

    /// <summary>
    /// An entry's structure and vertices go.
    /// </summary>
    void Drop(object key, Entry entry)
    {
        _bytes -= (long)(entry.Blas.Bytes + entry.Vertices.Size);
        entry.Blas.Dispose();
        entry.Vertices.Dispose();
        _entries.Remove(key);
    }
}
