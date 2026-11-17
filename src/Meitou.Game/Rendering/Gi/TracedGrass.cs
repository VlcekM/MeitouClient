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
    /// The traced blades' map (<see cref="Map"/>): the finest level's cells across, their side in world units, how far the map's centre is
    /// snapped (its corner moves by this much), the offset added to a height before it is stored (so every stored height is a positive float, whose
    /// bits order as the heights do), and its levels (each a quarter of the one before, down to one cell).
    /// </summary>
    public const int MapSide = 512, MapLevels = 10;

    /// <summary>
    /// See <see cref="MapSide"/>.
    /// </summary>
    public const float MapCell = 16, MapSnap = 512, MapOffset = 100000;

    /// <summary>
    /// The map's cells over all its levels.
    /// </summary>
    static int MapTexels
    {
        get
        {
            int n = 0;
            for (int l = 0; l < MapLevels; l++) n += (MapSide >> l) * (MapSide >> l);
            return n;
        }
    }

    /// <summary>
    /// The traced blades' map as the lamps' cell classification reads it (<see cref="Map"/>): the buffer, its corner (x, z) in the world, the finest
    /// cell, the side in cells and the height offset.
    /// </summary>
    public readonly record struct MapView(GpuBuffer Buffer, Vector2 Origin, float Cell, int Side, float Offset);

    /// <summary>
    /// The traced blades' map, world-anchored round the eye (<see cref="MapSide"/> cells of <see cref="MapCell"/>, its corner snapped to
    /// <see cref="MapSnap"/>): per cell the highest top of a traced card over it, as the card stands in its structure (a swaying patch's widened
    /// by twice its sway, the sway it may take before the next rebuild), then the coarser levels each the largest of four. A card collapsed by an
    /// interior mask leaves nothing. Made again when the frame's traced patches or their structures change (<see cref="UpdateMap"/>). The lamps'
    /// pass asks it whether a blade can stand between a lamp and what a screen cell shows (docs/render-lights.md "Lamps among grass"); null while
    /// there is none.
    /// </summary>
    public MapView? Map => _mapReady && _map is not null ? new(_map.Handle, _mapOrigin, MapCell, MapSide, MapOffset) : null;

    /// <summary>
    /// The map (device memory, <see cref="MapTexels"/> uints), and per frame slot the list of the patches it is made from (upload memory).
    /// </summary>
    DeviceBuffer? _map;

    /// <summary>
    /// See <see cref="_map"/>.
    /// </summary>
    BufferAllocation[]? _mapPatches;

    /// <summary>
    /// The map's kernels: the cards into the finest level, a coarser level from the one before.
    /// </summary>
    readonly GpuProgram _markProgram, _mipProgram;

    /// <summary>
    /// See <see cref="_markProgram"/>.
    /// </summary>
    readonly GpuComputePipeline _markPipeline, _mipPipeline;

    /// <summary>
    /// What the map was last made from (<see cref="UpdateMap"/>), whether it holds the frame's patches, and its corner.
    /// </summary>
    int _mapKey;

    /// <summary>
    /// See <see cref="_mapKey"/>.
    /// </summary>
    bool _mapReady;

    /// <summary>
    /// See <see cref="_mapKey"/>.
    /// </summary>
    Vector2 _mapOrigin;

    /// <summary>
    /// The largest sway of the frame's swaying patches.
    /// </summary>
    float _maxSway;

    /// <summary>
    /// A traced patch for the map's kernel: its vertices' address, its cards and its corner (std430: 32 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct MapPatch
    {
        public ulong Vertices;
        public uint Cards, Pad;
        public Vector4 Corner;
    }

    /// <summary>
    /// The map's kernels' constants: the map's corner (x, z), cell and side; the cards' widening and the height offset; a level's offsets and side.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct MapParams
    {
        public Vector4 Grid, Extra;
        public uint FineAt, At, Side, Pad;
    }

    /// <summary>
    /// The cards of the map's patches into its finest level (<see cref="Map"/>), a thread a card, a row of groups a patch.
    /// </summary>
    const string MarkSource = """
        #version 460
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 64) in;
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Corners { vec3 v[]; };
        struct MapPatch { uvec2 vertices; uint cards; uint pad; vec4 corner; };
        layout(set = 0, binding = 0, std430) readonly buffer Patches { MapPatch patches[]; };
        layout(set = 0, binding = 1, std430) buffer Map { uint map[]; };
        layout(set = 0, binding = 2, std140) uniform Params
        {
            vec4 grid;    // the map's corner x, z, its cell, its side
            vec4 extra;   // x: the cards' widening, y: the height offset
            uvec4 level;
        } p;

        void main()
        {
            MapPatch page = patches[gl_WorkGroupID.y];
            uint card = gl_GlobalInvocationID.x;
            if (card >= page.cards) return;
            Corners corners = Corners(page.vertices);
            vec3 lo = corners.v[card * 4u], hi = lo;
            for (uint k = 1u; k < 4u; k++)
            {
                vec3 q = corners.v[card * 4u + k];
                lo = min(lo, q);
                hi = max(hi, q);
            }
            // A card in an interior mask is collapsed to its blade's foot (TracedGrass.Expand): it blocks nothing.
            if (all(lessThan(hi - lo, vec3(1e-4)))) return;
            lo += page.corner.xyz;
            hi += page.corner.xyz;
            int side = int(p.grid.w);
            ivec2 a = max(ivec2(floor((lo.xz - p.extra.x - p.grid.xy) / p.grid.z)), ivec2(0));
            ivec2 b = min(ivec2(floor((hi.xz + p.extra.x - p.grid.xy) / p.grid.z)), ivec2(side - 1));
            uint top = floatBitsToUint(max(hi.y + p.extra.y, 1e-30));
            for (int z = a.y; z <= b.y; z++)
                for (int x = a.x; x <= b.x; x++)
                    atomicMax(map[uint(z * side + x)], top);
        }
        """;

    /// <summary>
    /// A coarser level of the map: each cell the largest of the four under it.
    /// </summary>
    const string MipSource = """
        #version 460
        layout(local_size_x = 8, local_size_y = 8) in;
        layout(set = 0, binding = 0, std430) buffer Map { uint map[]; };
        layout(set = 0, binding = 1, std140) uniform Params
        {
            vec4 grid;
            vec4 extra;
            uvec4 level;   // x: the finer level's first cell, y: this level's, z: this level's side
        } p;

        void main()
        {
            uvec2 q = gl_GlobalInvocationID.xy;
            uint side = p.level.z;
            if (q.x >= side || q.y >= side) return;
            uint fine = side * 2u, at = p.level.x + q.y * 2u * fine + q.x * 2u;
            map[p.level.y + q.y * side + q.x] = max(max(map[at], map[at + 1u]), max(map[at + fine], map[at + fine + 1u]));
        }
        """;

    /// <summary>
    /// Makes the map (<see cref="Map"/>) again when what it holds changed: the frame's traced patches, a structure built again, the snapped corner
    /// or the widening; nothing to do on most frames. Recorded after the frame's expansions, before the lamps' pass reads it.
    /// </summary>
    void UpdateMap(Func<IGpuCommandList> open)
    {
        if (_shown.Count == 0)
        {
            _mapReady = false;
            return;
        }

        float half = MapSide * MapCell / 2;
        float originX = MathF.Floor(_eye.X / MapSnap) * MapSnap + MapSnap / 2 - half;
        float originZ = MathF.Floor(_eye.Z / MapSnap) * MapSnap + MapSnap / 2 - half;
        var origin = new Vector2(originX, originZ);
        float widen = 2 * _maxSway + 1;
        var key = new HashCode();
        key.Add(origin);
        key.Add(widen);
        foreach (var (entry, _) in _shown)
        {
            key.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entry));
            key.Add(entry.Built);
        }
        int made = key.ToHashCode();
        if (_mapReady && made == _mapKey) return;

        _map ??= DeviceBuffer.Create(_ctx, (ulong)MapTexels * 4, BufferUse.Storage | BufferUse.TransferDst, "traced grass map");
        if (_mapPatches is null)
        {
            _mapPatches = new BufferAllocation[_ctx.Device.Frames.Count];
            for (int i = 0; i < _mapPatches.Length; i++)
            {
                _mapPatches[i] = _ctx.Device.Allocator.CreateBuffer((ulong)(MaxInstances * sizeof(MapPatch)), BufferUsageFlags.StorageBufferBit,
                    MemoryKind.Upload, "traced grass map patches");
            }
        }

        var upload = _mapPatches[_ctx.Frame.Slot];
        var patches = new Span<MapPatch>(upload.Mapped, MaxInstances);
        int count = 0, most = 0;
        foreach (var (entry, _) in _shown)
        {
            int cards = (int)(entry.Vertices.Size / (VerticesPerQuad * 12));
            if (cards == 0 || count >= MaxInstances) continue;
            patches[count++] = new MapPatch
            {
                Vertices = _ctx.Device.Address(entry.Vertices.Handle),
                Cards = (uint)cards,
                Corner = new Vector4(entry.Corner, 0),
            };
            most = Math.Max(most, cards);
        }

        var cmd = (CommandList)open();
        cmd.Barrier(BarrierBatch.Full);   // the cards expanded this frame
        cmd.FillBuffer(_map.Handle, 0, _map.Size, 0);
        cmd.Barrier(BarrierBatch.Full);
        var parameters = new MapParams { Grid = new Vector4(origin, MapCell, MapSide), Extra = new Vector4(widen, MapOffset, 0, 0) };
        var mapInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_map.Handle.Index), 0, _map.Size);
        var writes = stackalloc WriteDescriptorSet[3];
        if (count > 0)
        {
            var constants = _ctx.Frame.Constants.Write<MapParams>(new ReadOnlySpan<MapParams>(&parameters, 1), 256);
            var list = new DescriptorBufferInfo(_ctx.Device.BufferAt(upload.Index), 0, (ulong)(count * sizeof(MapPatch)));
            var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(MapParams));
            writes[0] = Buffer(0, DescriptorType.StorageBuffer, &list);
            writes[1] = Buffer(1, DescriptorType.StorageBuffer, &mapInfo);
            writes[2] = Buffer(2, DescriptorType.UniformBuffer, &uniform);
            cmd.BindPipeline(_markPipeline);
            cmd.PushDescriptors(_markProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 3), GpuBindPoint.Compute);
            cmd.Dispatch((uint)((most + 63) / 64), (uint)count);
        }

        uint fineAt = 0;
        for (int l = 1; l < MapLevels; l++)
        {
            int fineSide = MapSide >> (l - 1), side = MapSide >> l;
            uint at = fineAt + (uint)(fineSide * fineSide);
            cmd.Barrier(BarrierBatch.Full);
            parameters.FineAt = fineAt;
            parameters.At = at;
            parameters.Side = (uint)side;
            var constants = _ctx.Frame.Constants.Write<MapParams>(new ReadOnlySpan<MapParams>(&parameters, 1), 256);
            var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(MapParams));
            writes[0] = Buffer(0, DescriptorType.StorageBuffer, &mapInfo);
            writes[1] = Buffer(1, DescriptorType.UniformBuffer, &uniform);
            cmd.BindPipeline(_mipPipeline);
            cmd.PushDescriptors(_mipProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 2), GpuBindPoint.Compute);
            cmd.Dispatch((uint)((side + 7) / 8), (uint)((side + 7) / 8));
            fineAt = at;
        }
        cmd.Barrier(BarrierBatch.Full);

        (_mapKey, _mapReady, _mapOrigin) = (made, true, origin);
    }

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
    /// <summary>
    /// The widest card of the frame's patches (world units).
    /// </summary>
    float _widest;

    /// <summary>
    /// The corners (x, z) of the frame's traced pages that hold blades.
    /// </summary>
    readonly List<Vector2> _pages = [];

    /// <summary>
    /// Whether a traced page with blades comes within <paramref name="radius"/> of <paramref name="centre"/> across the ground, a blade's reach
    /// from its foot more: false says no traced blade stands in that sphere.
    /// </summary>
    public bool Within(Vector3 centre, float radius)
    {
        float reach = radius + _widest + 2 * BlindRange, size = FoliageRenderer.RayGrassPageSize;
        foreach (var corner in _pages)
        {
            float dx = MathF.Max(MathF.Max(corner.X - centre.X, centre.X - corner.X - size), 0);
            float dz = MathF.Max(MathF.Max(corner.Y - centre.Z, centre.Z - corner.Y - size), 0);
            if (dx * dx + dz * dz <= reach * reach) return true;
        }
        return false;
    }

    public TracedGrass(GpuContext ctx)
    {
        this._ctx = ctx;
        _program = ctx.Shaders.Compute(Expand, "traced grass",
            [(0, DescriptorType.StorageBuffer), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer)]);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "traced grass"));
        _indices = DeviceBuffer.Create(ctx, MaxQuads * IndicesPerQuad * 4ul, BufferUse.Index | BufferUse.RayInput, "traced grass indices");
        ctx.Uploads.Write(_indices, 0, MemoryMarshal.AsBytes(CardIndices()));
        _markProgram = ctx.Shaders.Compute(MarkSource, "traced grass map",
            [(0, DescriptorType.StorageBuffer), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer)]);
        _markPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_markProgram, "traced grass map"));
        _mipProgram = ctx.Shaders.Compute(MipSource, "traced grass map level", [(0, DescriptorType.StorageBuffer), (1, DescriptorType.UniformBuffer)]);
        _mipPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_mipProgram, "traced grass map level"));
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
        _pages.Clear();
        _tallest = 0;
        _widest = 0;
        _maxSway = 0;

        CommandList? list = null;
        int swayTriangles = ShowPatches(open, foliage, frame, cuts, ref list);
        if (list is not null)
        {
            list.Barrier(BarrierBatch.Full);
            foreach (var entry in _swayed) entry.Blas.RefitBottom(list, [entry.Geometry]);
        }

        UpdateMap(open);
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
        _mapReady = false;
        _pages.Clear();
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
        _map?.Dispose();
        foreach (var upload in _mapPatches ?? []) _ctx.Device.Allocator.Free(upload);
        _ctx.Shaders.Dispose(_program);
        _ctx.Shaders.Dispose(_markProgram);
        _ctx.Shaders.Dispose(_mipProgram);
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
            if (patch.Blades > 0) _pages.Add(new Vector2(patch.Corner.X, patch.Corner.Z));
            _tallest = MathF.Max(_tallest, patch.Size.W);
            _widest = MathF.Max(_widest, patch.Size.Y);

            if (Sways(patch)) _maxSway = MathF.Max(_maxSway, MathF.Abs(patch.Sway));

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
