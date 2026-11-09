using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Terrain;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// What a traced hit reads of its geometry (48 bytes, <c>GiShaders.Geometry</c>): the vertex positions (three floats at the start of each vertex,
/// <see cref="Stride"/> apart) and the triangle's three 32-bit indices, by device address.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GeometryRecord
{
    /// <summary>
    /// The device addresses of the vertices and of the indices.
    /// </summary>
    public ulong Vertices, Indices;

    /// <summary>
    /// The bytes from one vertex to the next, and what the geometry is (<see cref="Terrain"/>, <see cref="Object"/> or <see cref="Foliage"/>).
    /// </summary>
    public uint Stride, Kind;

    /// <summary>
    /// The surface's diffuse map in the bindless table (0: none, the probes take a constant albedo), written each frame per instance.
    /// </summary>
    public uint Texture;

    /// <summary>
    /// For a cut-out geometry (not opaque in its structure: leaves): the map whose alpha cuts it and the threshold (a hit below it is passed
    /// through).
    /// </summary>
    public uint AlphaTexture;

    /// <summary>
    /// The alpha below which a hit on a cut-out geometry is passed through.
    /// </summary>
    public float AlphaThreshold;

    /// <summary>
    /// The frame's interior lamp owner (0: no emitter), excluded from its own lamp's shadow query.
    /// </summary>
    public uint Emitter, Pad1, Pad2;

    /// <summary>
    /// The record's size in bytes.
    /// </summary>
    public const int Size = 48;

    /// <summary>
    /// <see cref="Kind"/>: what the geometry is (the debug views colour by it; a terrain hit takes the ground colour map).
    /// </summary>
    public const uint Terrain = 0, Object = 1, Foliage = 2;
}

/// <summary>
/// The scene the global illumination traces (docs/render-gi.md "The traced scene"): the terrain as two height-field grids around the eye (fine and
/// coarse, built from the CPU height snapshot on a worker and rebuilt when the eye has moved an eighth of their side), one bottom-level structure
/// per object mesh (from the mesh's own vertex and index buffers, its finest held level) and a top-level structure over everything within
/// <see cref="Range"/>, rebuilt every frame. Positions are relative to <see cref="Origin"/> (the eye, rounded), so floats keep their precision
/// far from the world's centre. Render thread only.
/// </summary>
internal sealed unsafe class GiScene : IDisposable
{
    #region Settings

    /// <summary>
    /// Most instances in the top-level structure, and most geometry records.
    /// </summary>
    public const int MaxInstances = 16384, MaxRecords = 65536;

    /// <summary>
    /// Most new object structures built in one frame (the rest wait for the next).
    /// </summary>
    public int MaxBuildsPerFrame { get; set; } = 32;

    /// <summary>
    /// Objects whose bounds come this close to the eye are in the scene.
    /// </summary>
    public float Range { get; set; } = 6000;

    /// <summary>
    /// Foliage (trees, bushes, rocks) whose bounding radius is at least <see cref="FoliageMinSize"/> (0: none) and whose bounds come within <see
    /// cref="FoliageRange"/> of the eye is in the scene.
    /// </summary>
    public float FoliageRange { get; set; } = 4000;

    /// <summary>
    /// The smallest bounding radius of foliage in the scene (0: no foliage).
    /// </summary>
    public float FoliageMinSize { get; set; } = 150;

    /// <summary>
    /// True (default; the bench's gi-top-reuse switch): the origin and the object set change in coarse steps and a top level whose instances are
    /// those of its slot's last build is not built again.
    /// </summary>
    public bool ReuseTop { get; set; } = true;

    /// <summary>
    /// How far the eye may leave the origin before the origin follows (rounded to 64), and the grid the object set is selected on.
    /// </summary>
    const float OriginSlack = 1024, SelectionSnap = 128;

    /// <summary>
    /// True when the last <see cref="Update"/> left the top level as it was.
    /// </summary>
    public bool TopReused { get; private set; }

    /// <summary>
    /// True: the top level is built for tracing (the build takes longer); the default while it is built rarely (<see cref="ReuseTop"/>), the bench's
    /// gi-top-trace switch.
    /// </summary>
    public bool TopFastTrace { get; set; } = true;

    /// <summary>
    /// True (default; the bench's gi-terrain-refit switch): a terrain grid that must follow the eye is refitted in place (same triangles, new
    /// heights; every `FullRebuildEvery`-th time rebuilt) instead of rebuilt into a new structure.
    /// </summary>
    public bool RefitTerrain { get; set; } = true;

    /// <summary>
    /// True (default; the bench's gi-lazy-scene switch; needs <see cref="ReuseTop"/>): everything that changes the traced scene changes at once, when
    /// the eye crosses into another cell of
    /// <see cref="EpochSize"/>: the object selection, the foliage block, the origin and the terrain grids that have finished sampling. Between those
    /// commits the instances stay as they are and the top level is not built. A moving camera used to change one of them nearly every frame (the
    /// selection snap 128 units, the foliage 250, a terrain grid 512 and its completion later).
    /// </summary>
    public bool LazyScene { get; set; } = true;

    /// <summary>
    /// True (default; the bench's gi-top-shared switch): one top-level structure for all frame slots. The frame's full barrier orders each frame's
    /// builds after the traces of the frames before, so the slots need none of their own, and a changed scene is built once, not once per slot.
    /// </summary>
    public bool SharedTop { get; set; } = true;

    #endregion

    #region Scene state

    /// <summary>
    /// The cell of the eye (a cube of this many units) that <see cref="LazyScene"/> commits on.
    /// </summary>
    const float EpochSize = 1024;

    /// <summary>
    /// The <see cref="EpochSize"/> cell of the eye at the last commit (<see cref="LazyScene"/>).
    /// </summary>
    (int X, int Y, int Z) _epochCell;

    /// <summary>
    /// Whether a cell was committed yet.
    /// </summary>
    bool _epochSet;

    /// <summary>
    /// Whether <see cref="Origin"/> was placed yet.
    /// </summary>
    bool _originSet;

    /// <summary>
    /// This frame's native list of the scene (null until <see cref="Open"/>).
    /// </summary>
    CommandList? _seg;

    /// <summary>
    /// Whether this frame's list is cut into timed parts (the pass meter is on).
    /// </summary>
    bool _split;

    /// <summary>
    /// The foliage the block is rebuilt from, nearest first (reused between rebuilds).
    /// </summary>
    readonly List<(FoliageRayMesh Mesh, Matrix4x4 Transform, float Distance)> _rayFoliage = [];

    /// <summary>
    /// A mesh's structure not used for this many frames is dropped.
    /// </summary>
    const int KeepFrames = 600;

    /// <summary>
    /// The device context the builds are recorded on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The device the structures and buffers live on.
    /// </summary>
    readonly VulkanDevice _device;

    /// <summary>
    /// The terrain grids: fine and coarse.
    /// </summary>
    readonly TerrainGrid[] _terrain;

    /// <summary>
    /// The object meshes' bottom-level structures, by mesh.
    /// </summary>
    readonly Dictionary<object, MeshEntry> _meshes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Per frame slot: its instances and records buffers and its top level.
    /// </summary>
    readonly FrameSlot[] _slots;

    /// <summary>
    /// This frame's top-level instances.
    /// </summary>
    readonly List<InstanceRecord> _instances = [];

    /// <summary>
    /// This frame's geometry records, indexed by the instances' custom index.
    /// </summary>
    readonly List<GeometryRecord> _records = [];

    /// <summary>
    /// The meshes found unused for too long by <see cref="Evict"/> (reused).
    /// </summary>
    readonly List<object> _stale = [];

    /// <summary>
    /// The updates run so far, by which the meshes' use is told.
    /// </summary>
    long _frame;

    /// <summary>
    /// The world position the traced coordinates are relative to (this frame's).
    /// </summary>
    public Vector3 Origin { get; private set; }

    /// <summary>
    /// This frame's top-level structure (null before the first <see cref="Update"/>).
    /// </summary>
    public AccelerationStructure? Top { get; private set; }

    /// <summary>
    /// This frame's geometry records (a storage buffer range).
    /// </summary>
    public GpuBufferBinding Records { get; private set; }

    /// <summary>
    /// The instances in the last update's top level.
    /// </summary>
    public int InstanceCount { get; private set; }

    /// <summary>
    /// The object and foliage meshes with a bottom-level structure.
    /// </summary>
    public int MeshCount => _meshes.Count;

    /// <summary>
    /// The bottom-level structures built in the last update.
    /// </summary>
    public int BuiltThisFrame { get; private set; }

    /// <summary>
    /// The bytes of the object and foliage structures held.
    /// </summary>
    public long MeshBytes { get; private set; }

    /// <summary>
    /// The foliage instances in the foliage block.
    /// </summary>
    public int FoliageInstances { get; private set; }

    /// <summary>
    /// Render-thread time of the last <see cref="Update"/>.
    /// </summary>
    public float UpdateMs { get; private set; }

    /// <summary>
    /// A mesh's bottom-level structure and the geometry records of its parts.
    /// </summary>
    sealed class MeshEntry
    {
        /// <summary>
        /// The mesh's bottom-level structure.
        /// </summary>
        public required AccelerationStructure Blas;

        /// <summary>
        /// The records of the mesh's geometries, copied per instance.
        /// </summary>
        public required GeometryRecord[] Records;

        /// <summary>
        /// Per record, the part of the mesh it is (its material in the instance's set).
        /// </summary>
        public required int[] Parts;

        /// <summary>
        /// The buffers it was built from: a reshape (BuildingLodMesh) puts new parts into the same GpuObjectMesh and frees these, so the records
        /// would point at freed memory (a hit reads its triangle: device lost).
        /// </summary>
        public required object[] Sources;

        /// <summary>
        /// The update (<see cref="_frame"/>) the structure was last in the scene.
        /// </summary>
        public long LastUsed;
    }

    /// <summary>
    /// One frame slot's upload buffers and top level.
    /// </summary>
    sealed class FrameSlot
    {
        /// <summary>
        /// The slot's top-level instances and geometry records (upload memory).
        /// </summary>
        public required BufferAllocation Instances, Records;

        /// <summary>
        /// The slot's top-level structure.
        /// </summary>
        public required AccelerationStructure Top;

        /// <summary>
        /// The instances (bytes) the slot's top level was last built over (ReuseTop).
        /// </summary>
        public byte[]? BuiltBytes;

        /// <summary>
        /// The bytes of <see cref="BuiltBytes"/> in use (-1: none).
        /// </summary>
        public int BuiltLength;

        /// <summary>
        /// Whether the slot's top level was last built for tracing (<see cref="TopFastTrace"/>).
        /// </summary>
        public bool BuiltTrace;
    }

    #endregion

    #region Update

    /// <summary>
    /// Makes the terrain grids and, per frame slot, the instances and records buffers and a top level; needs ray queries.
    /// </summary>
    public GiScene(GpuContext ctx)
    {
        if (!ctx.Device.HasRayQuery)
            throw new NotSupportedException("the global illumination needs ray queries (VK_KHR_ray_query)");
        this._ctx = ctx;
        _device = ctx.Device;
        _terrain = [new TerrainGrid(ctx, "gi terrain fine", 256, 16), new TerrainGrid(ctx, "gi terrain coarse", 256, 160)];
        int count = _device.Frames.Count;
        _slots = new FrameSlot[count];
        const BufferUsageFlags use = BufferUsageFlags.ShaderDeviceAddressBit
            | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.StorageBufferBit;
        for (int i = 0; i < count; i++)
        {
            _slots[i] = new FrameSlot
            {
                Instances = _device.Allocator.CreateBuffer((ulong)(MaxInstances * InstanceRecord.Size), use, MemoryKind.Upload, "gi instances"),
                Records = _device.Allocator.CreateBuffer((ulong)(MaxRecords * GeometryRecord.Size), use, MemoryKind.Upload, "gi geometry records"),
                Top = AccelerationStructure.CreateTop(_device, MaxInstances, "gi top level"),
            };
        }
    }

    /// <summary>
    /// Records this frame's builds into a native command list: the terrain grids that finished on their worker, new object structures, and the
    /// top-level structure over the terrain and the objects of <paramref name="objects"/> near <paramref name="eye"/>. Ends with a barrier to the
    /// tracing shaders.
    /// </summary>
    public void Update(Vector3 eye, HeightSnapshot heights, WorldObjectRenderer? objects, FoliageRenderer? foliage = null, WorldLamps? lamps = null)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _frame++;

        // The scene's step (LazyScene): commit is true on the frames where the eye is in another cell than on the last commit (always, with the old
        // behaviour).
        bool lazy = ReuseTop && LazyScene, commit = true;
        if (lazy) commit = EnterCell(eye);
        MoveOrigin(eye, commit);
        var slot = _slots[_ctx.Frame.Slot];

        // The top level and what it was built over: the first slot's for all of them, or each slot's own.
        var topSlot = SharedTop ? _slots[0] : slot;
        _instances.Clear();
        _records.Clear();
        BuiltThisFrame = 0;
        _seg = null;

        // With the pass meter (MEITOU_PASS_STATS=1) the native list is cut after its barrier and before the top level, to time the barrier, the
        // bottom-level builds and the top level apart.
        _split = StageClock.OnClose is not null;
        if (_split) Open();

        AddTerrain(eye, heights, lazy, commit);
        AddObjects(eye, objects, lamps, lazy);
        if (foliage is not null && FoliageMinSize > 0) AddFoliage(eye, foliage, lazy, commit);
        Evict();
        BuildTop(slot, topSlot);

        CollectionsMarshal.AsSpan(_records).CopyTo(new Span<GeometryRecord>(slot.Records.Mapped, MaxRecords));
        if (_seg is { } open)
        {
            open.Barrier(AccelerationStructure.BuildToTrace);
            open.EndLabel();
            _ctx.EndNative(open);
            _seg = null;
        }

        Top = topSlot.Top;
        Records = new GpuBufferBinding(slot.Records.Handle, 0, (ulong)(Math.Max(_records.Count, 1) * GeometryRecord.Size));
        InstanceCount = _instances.Count;
        UpdateMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>
    /// Whether the eye is in another <see cref="EpochSize"/> cell than on the last commit (the cell then becomes the commit's).
    /// </summary>
    bool EnterCell(Vector3 eye)
    {
        var cell = ((int)MathF.Round(eye.X / EpochSize), (int)MathF.Round(eye.Y / EpochSize), (int)MathF.Round(eye.Z / EpochSize));
        bool commit = !_epochSet || cell != _epochCell;
        if (commit)
        {
            _epochCell = cell;
            _epochSet = true;
        }

        return commit;
    }

    /// <summary>
    /// The origin follows the eye in steps of OriginSlack, not 64 units: the instances (their transforms are relative to it) then stay the same
    /// while the eye walks, and the top level of a frame whose instances are those of its slot's last build is not built again (ReuseTop).
    /// </summary>
    void MoveOrigin(Vector3 eye, bool commit)
    {
        var rounded = new Vector3(MathF.Round(eye.X / 64) * 64, MathF.Round(eye.Y / 64) * 64, MathF.Round(eye.Z / 64) * 64);
        if (!ReuseTop || !_originSet || commit && MathF.Max(MathF.Abs(eye.X - Origin.X), MathF.Max(MathF.Abs(eye.Y - Origin.Y),
                MathF.Abs(eye.Z - Origin.Z))) > OriginSlack)
        {
            Origin = rounded;
            _originSet = true;
        }
    }

    /// <summary>
    /// The terrain grids' instances and records, each grid updated first (its build recorded into the scene's list when it finished).
    /// </summary>
    void AddTerrain(Vector3 eye, HeightSnapshot heights, bool lazy, bool commit)
    {
        foreach (var grid in _terrain)
        {
            grid.Refit = RefitTerrain;
            grid.RequestDivisor = lazy && grid.Fine ? 16 : 8;
            grid.Update(Open, eye, heights, commit);

            // A refitted bottom level is not what the slots' top levels were built over: every one of them is built again before it is traced.
            if (grid.Refitted)
            {
                foreach (var s in _slots)
                {
                    s.BuiltBytes = null;
                    s.BuiltLength = -1;
                }
            }

            if (grid.Blas is null) continue;
            var world = Matrix4x4.CreateTranslation(grid.Centre - Origin);

            // Facing culled: the grid is coarser than the drawn terrain, so a surface in a hollow lies under it and its rays must pass up through it
            // (the grid's triangles are counter-clockwise seen from above, which Vulkan's ray tracing takes as the front: RayQueryTests).
            _instances.Add(InstanceRecord.Of(world, (uint)_records.Count, 0x01, grid.Blas.Address, 0));
            _records.Add(grid.Record);
        }
    }

    /// <summary>
    /// The objects near the eye: their instances, and their parts' records with this frame's textures and emitters.
    /// </summary>
    void AddObjects(Vector3 eye, WorldObjectRenderer? objects, WorldLamps? lamps, bool lazy)
    {
        // The objects are those near the eye snapped to a grid of SelectionSnap (the range widened by the snap's half diagonal, so every object
        // within Range is in): the set then changes when the
        // snapped eye does, not whenever an object crosses the range, and the top level can be left as it is.
        var snapped = new Vector3(MathF.Round(eye.X / SelectionSnap), MathF.Round(eye.Y / SelectionSnap), MathF.Round(eye.Z / SelectionSnap))
            * SelectionSnap;
        var selectionEye = lazy ? new Vector3(_epochCell.X, _epochCell.Y, _epochCell.Z) * EpochSize : ReuseTop ? snapped : eye;
        float selectionRange = ReuseTop ? Range + SelectionSnap * 0.87f : Range;
        if (objects is null) return;
        foreach (var inst in objects.RayInstances(selectionEye, selectionRange))
        {
            if (_instances.Count >= MaxInstances) break;
            var entry = EntryFor(inst.Gpu!);
            if (entry is null) continue;
            if (_records.Count + entry.Records.Length > MaxRecords) break;
            entry.LastUsed = _frame;
            var world = inst.Transform;
            world.Translation -= Origin;
            _instances.Add(InstanceRecord.Of(world, (uint)_records.Count, 0x02, entry.Blas.Address));
            for (int k = 0; k < entry.Records.Length; k++)
            {
                var record = entry.Records[k];
                record.Texture = objects.RayTexture(inst.Materials!, entry.Parts[k]);
                record.Emitter = lamps?.Emitter(inst.Placed.PlacementId) ?? 0;
                _records.Add(record);
            }
        }
    }

    /// <summary>
    /// The foliage's cached block (rebuilt when stale), moved to this frame's origin, its records with this frame's texture indices (a texture
    /// can be unloaded or move in the table between rebuilds; the structures cannot, FoliageRenderer.RayGeneration rebuilds the block when a
    /// mesh goes).
    /// </summary>
    void AddFoliage(Vector3 eye, FoliageRenderer foliage, bool lazy, bool commit)
    {
        if (FoliageStale(eye, foliage, lazy, commit)) RebuildFoliage(eye, foliage);
        int firstRecord = _records.Count;
        if (firstRecord + _foliageRecords.Count > MaxRecords) return;

        var shift = _foliageOrigin - Origin;
        int count = Math.Min(_foliageBlock.Count, MaxInstances - _instances.Count);
        for (int i = 0; i < count; i++)
        {
            var r = _foliageBlock[i];
            r.Transform[3] += shift.X;
            r.Transform[7] += shift.Y;
            r.Transform[11] += shift.Z;
            r.CustomIndexAndMask += (uint)firstRecord;
            _instances.Add(r);
        }

        for (int k = 0; k < _foliageRecords.Count; k++)
        {
            var record = _foliageRecords[k];
            var (mesh, part) = _foliageSources[k];
            foliage.RefreshRayTextures(mesh);
            (record.Texture, record.AlphaTexture) = mesh.Textures[part];
            _records.Add(record);
        }
    }

    /// <summary>
    /// The top level, over this frame's instances: built again only when they are not those of its slot's last build (the eye still or walking,
    /// the origin and the object set hold, see ReuseTop). The instances are plain data, so equal bytes are an equal structure.
    /// </summary>
    void BuildTop(FrameSlot slot, FrameSlot topSlot)
    {
        var bytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_instances));
        bool same = ReuseTop && topSlot.BuiltTrace == TopFastTrace && topSlot.BuiltBytes is { } built
            && topSlot.BuiltLength == bytes.Length && bytes.SequenceEqual(built.AsSpan(0, bytes.Length));
        TopReused = same;
        if (same) return;

        var cmd = (CommandList)Open();

        // The new bottom levels must be complete before the top level reads them.
        cmd.Barrier(AccelerationStructure.BuildToTrace);
        if (_split)
        {
            cmd.EndLabel();
            _ctx.EndNative(cmd);
            StageClock.Sub("gi scene blas");
            cmd = _seg = (CommandList)_ctx.BeginNative("gi scene");
            cmd.BeginLabel("gi scene");
        }

        CollectionsMarshal.AsSpan(_instances).CopyTo(new Span<InstanceRecord>(slot.Instances.Mapped, MaxInstances));
        var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = slot.Instances.Buffer };
        topSlot.Top.RebuildTop(cmd, _device.Vk.GetBufferDeviceAddress(_device.Device, in info), (uint)_instances.Count, TopFastTrace);
        if (ReuseTop)
        {
            if (topSlot.BuiltBytes is null || topSlot.BuiltBytes.Length < bytes.Length)
                topSlot.BuiltBytes = new byte[Math.Max(bytes.Length * 5 / 4, 4096)];
            bytes.CopyTo(topSlot.BuiltBytes);
            topSlot.BuiltLength = bytes.Length;
        }
        else topSlot.BuiltBytes = null;
        topSlot.BuiltTrace = TopFastTrace;
    }

    /// <summary>
    /// This frame's native list of the scene, opened on first use (a frame that builds nothing records none) with the barrier for the frame's
    /// uploads.
    /// </summary>
    CommandList Open()
    {
        if (_seg is not null) return _seg;
        var c = (CommandList)_ctx.BeginNative("gi scene");
        c.BeginLabel("gi scene");
        c.Barrier(BarrierBatch.Full);
        if (_split)
        {
            c.EndLabel();
            _ctx.EndNative(c);
            StageClock.Sub("gi scene barrier");
            c = (CommandList)_ctx.BeginNative("gi scene");
            c.BeginLabel("gi scene");
        }
        return _seg = c;
    }

    /// <summary>
    /// The mesh's structure, built now when there is budget left this frame (null: not yet, or nothing to trace).
    /// </summary>
    MeshEntry? EntryFor(GpuObjectMesh gpu)
    {
        if (BuiltThisFrame >= MaxBuildsPerFrame && !_meshes.ContainsKey(gpu)) return null;

        // The finest level the mesh holds: its own parts at that level, or the manual mesh standing in for it.
        int level = Math.Clamp(gpu.MinLevel, 0, Math.Max(gpu.LevelCount - 1, 0));
        var source = gpu.Manual.Length > level && gpu.Manual[level] is { } manual ? manual : gpu;
        int sourceLevel = ReferenceEquals(source, gpu) ? level : 0;
        if (_meshes.TryGetValue(gpu, out var entry))
        {
            if (BuiltFrom(entry, source)) return entry;
            Drop(gpu, entry);
            if (BuiltThisFrame >= MaxBuildsPerFrame) return null;
        }
        var sources = source.Parts.Select(p => (object)p.Vertices).Prepend(source).ToArray();
        var geometries = new List<TriangleGeometry>();
        var recs = new List<GeometryRecord>();
        var partOf = new List<int>();
        for (int p = 0; p < source.Parts.Count; p++)
        {
            var part = source.Parts[p];
            if (part.Count.Length <= sourceLevel) continue;
            int count = part.Count[sourceLevel];
            if (count < 3) continue;
            ulong vertices = _ctx.Device.Address(part.Vertices.Handle),
                indices = _ctx.Device.Address(part.Indices.Handle) + (ulong)part.Offset[sourceLevel] * 4;
            geometries.Add(new TriangleGeometry(vertices, (uint)part.Stride, (uint)(part.Vertices.Size / (ulong)part.Stride), indices,
                (uint)(count / 3)));
            recs.Add(new GeometryRecord { Vertices = vertices, Indices = indices, Stride = (uint)part.Stride, Kind = GeometryRecord.Object });
            partOf.Add(p);
        }
        return Build(gpu, sources, geometries, recs, partOf, "gi object");
    }

    /// <summary>
    /// A foliage mesh's structure (FoliageRayMesh: its key is the resident GpuMesh, which a reload replaces, so it never changes under it).
    /// </summary>
    MeshEntry? FoliageEntryFor(FoliageRayMesh mesh)
    {
        if (_meshes.TryGetValue(mesh.Key, out var entry)) return entry;
        if (BuiltThisFrame >= MaxBuildsPerFrame) return null;
        var geometries = new List<TriangleGeometry>();
        var recs = new List<GeometryRecord>();
        var partOf = new List<int>();
        for (int p = 0; p < mesh.Parts.Length; p++)
        {
            var part = mesh.Parts[p];
            if (part.IndexCount < 3) continue;
            ulong vertices = _ctx.Device.Address(part.Vertices.Handle), indices = _ctx.Device.Address(part.Indices.Handle);

            // Cut-out parts (leaves) are not opaque: the trace tests their alpha at each candidate hit.
            geometries.Add(new TriangleGeometry(vertices, GpuVertex.ObjectSize, (uint)(part.Vertices.Size / GpuVertex.ObjectSize), indices,
                (uint)(part.IndexCount / 3), part.AlphaThreshold <= 0));
            recs.Add(new GeometryRecord
            {
                Vertices = vertices,
                Indices = indices,
                Stride = GpuVertex.ObjectSize,
                AlphaThreshold = part.AlphaThreshold,
                Kind = mesh.Rock ? GeometryRecord.Terrain : GeometryRecord.Foliage,
            });
            partOf.Add(p);
        }
        return Build(mesh.Key, [mesh.Key], geometries, recs, partOf, "gi foliage");
    }

    #endregion

    #region Foliage block

    /// <summary>
    /// The foliage block's instances: rebuilt now and then (the foliage does not move), not every frame (FoliageRenderer.RayInstances walks every
    /// zone).
    /// </summary>
    readonly List<InstanceRecord> _foliageBlock = [];

    /// <summary>
    /// The foliage block's geometry records.
    /// </summary>
    readonly List<GeometryRecord> _foliageRecords = [];

    /// <summary>
    /// Per foliage record, the mesh and the part it is.
    /// </summary>
    readonly List<(FoliageRayMesh Mesh, int Part)> _foliageSources = [];

    /// <summary>
    /// The origin the block's transforms are relative to, and the eye it was gathered at.
    /// </summary>
    Vector3 _foliageOrigin, _foliageEye;

    /// <summary>
    /// The update the block was rebuilt in.
    /// </summary>
    long _foliageFrame = long.MinValue / 2;

    /// <summary>
    /// The foliage's ray generation the block was gathered from.
    /// </summary>
    int _foliageGeneration = -1;

    /// <summary>
    /// Whether every foliage mesh of the block had its structure (false: rebuilt again soon).
    /// </summary>
    bool _foliageComplete;

    /// <summary>
    /// Whether the foliage block is due again: every 120 updates, on a commit (else when the eye moved 250 units), when the foliage changed, and soon
    /// after a block that lacked meshes.
    /// </summary>
    bool FoliageStale(Vector3 eye, FoliageRenderer foliage, bool lazy, bool commit) =>
        _frame - _foliageFrame >= 120
        || (lazy ? commit : Vector3.DistanceSquared(eye, _foliageEye) > 250f * 250f)
        || foliage.RayGeneration != _foliageGeneration
        || !_foliageComplete && _frame - _foliageFrame >= 2;

    /// <summary>
    /// Trees, bushes and rocks big enough to matter for the probes (FoliageRenderer.RayInstances), nearest first while the budget lasts.
    /// </summary>
    void RebuildFoliage(Vector3 eye, FoliageRenderer foliage)
    {
        _foliageBlock.Clear();
        _foliageRecords.Clear();
        _foliageSources.Clear();
        _rayFoliage.Clear();
        foliage.RayInstances(eye, FoliageRange, FoliageMinSize, _rayFoliage);
        _rayFoliage.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        (_foliageOrigin, _foliageEye, _foliageFrame, _foliageGeneration, _foliageComplete) = (Origin, eye, _frame, foliage.RayGeneration, true);
        foreach (var (mesh, transform, _) in _rayFoliage)
        {
            if (_foliageBlock.Count >= MaxInstances - 2048) break;   // room for the terrain and the objects
            var entry = FoliageEntryFor(mesh);
            if (entry is null)
            {
                _foliageComplete = false;
                continue;
            }

            entry.LastUsed = _frame;
            var world = transform;
            world.M14 = 0;
            world.Translation -= _foliageOrigin;
            _foliageBlock.Add(InstanceRecord.Of(world, (uint)_foliageRecords.Count, 0x04, entry.Blas.Address));
            for (int k = 0; k < entry.Records.Length; k++)
            {
                _foliageRecords.Add(entry.Records[k]);
                _foliageSources.Add((mesh, entry.Parts[k]));
            }
        }
        FoliageInstances = _foliageBlock.Count;
    }

    /// <summary>
    /// Builds the bottom-level structure of <paramref name="geometries"/> into the scene's list and keeps it under <paramref name="key"/> with its
    /// records; null when there are none.
    /// </summary>
    MeshEntry? Build(object key, object[] sources, List<TriangleGeometry> geometries, List<GeometryRecord> recs, List<int> partOf, string name)
    {
        if (geometries.Count == 0) return null;
        var cmd = (CommandList)Open();
        var blas = AccelerationStructure.BuildBottom(_device, cmd, CollectionsMarshal.AsSpan(geometries), name);
        blas.ReleaseScratch();
        BuiltThisFrame++;
        MeshBytes += (long)blas.Bytes;
        var entry = new MeshEntry { Blas = blas, Records = [.. recs], Parts = [.. partOf], Sources = sources, LastUsed = _frame };
        _meshes[key] = entry;
        return entry;
    }

    #endregion

    #region Eviction and disposal

    /// <summary>
    /// Drops the meshes' structures not in the scene for <see cref="KeepFrames"/> updates.
    /// </summary>
    void Evict()
    {
        _stale.Clear();
        foreach (var (key, entry) in _meshes)
            if (_frame - entry.LastUsed > KeepFrames) _stale.Add(key);
        foreach (var key in _stale) Drop(key, _meshes[key]);
    }

    /// <summary>
    /// Whether <paramref name="entry"/> was built from <paramref name="source"/> and its current parts' vertex buffers.
    /// </summary>
    static bool BuiltFrom(MeshEntry entry, GpuObjectMesh source)
    {
        var s = entry.Sources;
        if (s.Length != source.Parts.Count + 1 || !ReferenceEquals(s[0], source)) return false;
        for (int i = 0; i < source.Parts.Count; i++)
            if (!ReferenceEquals(s[i + 1], source.Parts[i].Vertices)) return false;
        return true;
    }

    /// <summary>
    /// Frees <paramref name="entry"/>'s structure and forgets it.
    /// </summary>
    void Drop(object key, MeshEntry entry)
    {
        MeshBytes -= (long)entry.Blas.Bytes;
        entry.Blas.Dispose();
        _meshes.Remove(key);
    }

    /// <summary>
    /// A line describing the last update, the structures held and the terrain grids, for the log.
    /// </summary>
    public string Describe() =>
        $"{UpdateMs:0.00} ms{(TopReused ? " (top level as it was)" : "")}, {InstanceCount} instances ({FoliageInstances} foliage), "
        + $"{_meshes.Count} object structures ({MeshBytes / (1024 * 1024)} MB), {BuiltThisFrame} built this frame, "
        + $"terrain {string.Join(" + ", _terrain.Select(t => t.Describe()))}";

    /// <summary>
    /// Frees the terrain grids, the mesh structures and the slots' buffers and top levels.
    /// </summary>
    public void Dispose()
    {
        foreach (var t in _terrain) t.Dispose();
        foreach (var e in _meshes.Values) e.Blas.Dispose();
        _meshes.Clear();
        foreach (var s in _slots)
        {
            s.Top.Dispose();
            _device.DeferFree(s.Instances);
            _device.DeferFree(s.Records);
        }
    }

    #endregion

    #region Terrain grid

    /// <summary>
    /// A square height-field grid of <c>cells</c> × <c>cells</c> quads of <c>spacing</c> around a centre, sampled from the height snapshot on a
    /// worker and uploaded, then built into a bottom-level structure. Rebuilt when the eye has left the middle eighth.
    /// </summary>
    sealed class TerrainGrid(GpuContext ctx, string name, int cells, float spacing) : IDisposable
    {
        /// <summary>
        /// The grid's vertex and index buffers (null until the first build).
        /// </summary>
        DeviceBuffer? _vertices, _indices;

        /// <summary>
        /// The sampling running on a worker (null: none).
        /// </summary>
        Task<(Vector3 Centre, Vector3[] Positions)>? _job;

        /// <summary>
        /// The grid's bottom-level structure (null before the first build).
        /// </summary>
        public AccelerationStructure? Blas { get; private set; }

        /// <summary>
        /// The centre the structure was built at.
        /// </summary>
        public Vector3 Centre { get; private set; }

        /// <summary>
        /// The grid's geometry record.
        /// </summary>
        public GeometryRecord Record { get; private set; }

        /// <summary>
        /// The centre of the newest grid asked for (the one being sampled, or built): Centre moves only when its structure is built, so the instance
        /// never places the old grid at the new centre.
        /// </summary>
        Vector3 _requested;

        /// <summary>
        /// Whether a grid was asked for yet.
        /// </summary>
        bool _hasRequested;

        /// <summary>
        /// Set by the scene each frame: refit the structure in place when the grid moves (see <see cref="GiScene.RefitTerrain"/>).
        /// </summary>
        public bool Refit { get; set; }

        /// <summary>
        /// The grid is rebuilt when the eye is further than a <c>RequestDivisor</c>-th of its side from the centre sampled last (8; 16 for the fine
        /// grid with <see cref="GiScene.LazyScene"/>, whose refit is cheap and waits for the scene's commit).
        /// </summary>
        public int RequestDivisor { get; set; } = 8;

        /// <summary>
        /// Whether this is the fine grid (spacing under 100).
        /// </summary>
        public bool Fine => spacing < 100;

        /// <summary>
        /// True when the last <see cref="Update"/> refitted the structure in place.
        /// </summary>
        public bool Refitted { get; private set; }

        /// <summary>
        /// A refitted grid is rebuilt every this many refits.
        /// </summary>
        const int FullRebuildEvery = 16;

        /// <summary>
        /// The refits since the last build.
        /// </summary>
        int _refitsSinceBuild;

        /// <summary>
        /// Whether the structure was built to be refitted.
        /// </summary>
        bool _updatable;

        /// <summary>
        /// The grid's side in world units.
        /// </summary>
        float Side => cells * spacing;

        /// <summary>
        /// The most frames a finished grid waits for the scene's commit.
        /// </summary>
        const int MaxWait = 8;

        /// <summary>
        /// The frames the finished grid has waited.
        /// </summary>
        int _waited;

        /// <summary>
        /// One array for every sampling (an LOH array a rebuild otherwise): one job at a time, and Build copies it into the upload staging before the
        /// next starts.
        /// </summary>
        Vector3[]? _samples;

        /// <summary>
        /// Installs a finished grid (on <paramref name="install"/>, the first or one that waited <see cref="MaxWait"/> frames) and starts sampling a
        /// new one when the eye left the middle of the last; the first grid is made at once.
        /// </summary>
        public void Update(Func<IGpuCommandList> open, Vector3 eye, HeightSnapshot heights, bool install = true)
        {
            Refitted = false;

            // A finished grid is installed on the scene's commit, or at once when there is none yet, or when it has waited for a commit for MaxWait
            // frames (a camera that stopped).
            if (_job is { IsCompleted: true } && ++_waited >= MaxWait) install = true;
            if (_job is { IsCompleted: true } && (install || Blas is null))
            {
                var (centre, positions) = _job.Result;
                _job = null;
                _waited = 0;
                Build((CommandList)open(), centre, positions);
            }
            var wanted = new Vector3(MathF.Round(eye.X / spacing) * spacing, 0, MathF.Round(eye.Z / spacing) * spacing);
            bool far = !_hasRequested || MathF.Max(MathF.Abs(wanted.X - _requested.X), MathF.Abs(wanted.Z - _requested.Z)) > Side / RequestDivisor;
            if (far && _job is null)
            {
                _hasRequested = true;
                _requested = wanted;
                _job = Task.Run(() => (wanted, Sample(wanted, heights)));
                if (Blas is null) _job.Wait();   // the first grid at once: the first frames see the terrain
                if (Blas is null)
                {
                    var (c, p) = _job.Result;
                    _job = null;
                    Build((CommandList)open(), c, p);
                }
            }
        }

        /// <summary>
        /// The heights around <paramref name="centre"/>, a vertex per grid corner, relative to it.
        /// </summary>
        Vector3[] Sample(Vector3 centre, HeightSnapshot heights)
        {
            int n = cells + 1;
            var p = _samples ??= new Vector3[n * n];
            float half = Side / 2;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    float x = centre.X - half + i * spacing, z = centre.Z - half + j * spacing;
                    p[j * n + i] = new Vector3(x - centre.X, heights.HeightAt(x, z), z - centre.Z);
                }
            }

            return p;
        }

        /// <summary>
        /// Builds the grid at <paramref name="centre"/> from <paramref name="positions"/>: refitted in place when it can be, else into new buffers
        /// and a new structure.
        /// </summary>
        void Build(CommandList cmd, Vector3 centre, Vector3[] positions)
        {
            int n = cells + 1;
            if (_indices is null)
            {
                var idx = new uint[cells * cells * 6];
                int k = 0;
                for (int j = 0; j < cells; j++)
                {
                    for (int i = 0; i < cells; i++)
                    {
                        uint a = (uint)(j * n + i), b = a + 1, c = a + (uint)n, d = c + 1;
                        idx[k++] = a; idx[k++] = c; idx[k++] = b;
                        idx[k++] = b; idx[k++] = c; idx[k++] = d;
                    }
                }

                _indices = DeviceBuffer.Create(ctx, (ulong)idx.Length * 4, BufferUse.Index | BufferUse.RayInput, name);
                ctx.Uploads.Write(_indices, 0, MemoryMarshal.AsBytes(idx.AsSpan()));
            }
            if (Refit && _updatable && Blas is not null && _vertices is not null
                && _vertices.Size == (ulong)(positions.Length * 12) && _refitsSinceBuild < FullRebuildEvery)
            {
                // The same triangles over new heights: written into the vertex buffer in place and the structure refitted. The frame's full barrier
                // (FrameLoop) orders both after the frames that traced the old ones; the vertex buffer's address, and so the geometry record, stay.
                ctx.Uploads.Write(_vertices, 0, MemoryMarshal.AsBytes(positions.AsSpan()));
                cmd.Barrier(BarrierBatch.Full);
                Blas.RefitBottom(cmd, [new TriangleGeometry(ctx.Device.Address(_vertices.Handle), 12, (uint)positions.Length,
                    ctx.Device.Address(_indices.Handle), (uint)(cells * cells * 2))]);
                _refitsSinceBuild++;
                Refitted = true;
                Centre = new Vector3(centre.X, 0, centre.Z);
                return;
            }

            // A new vertex buffer every rebuild: the last frames' structures were built from the old one, which is freed after them.
            _vertices?.Dispose();
            _vertices = DeviceBuffer.Create(ctx, (ulong)(positions.Length * 12), BufferUse.Vertex | BufferUse.RayInput, name);
            ctx.Uploads.Write(_vertices, 0, MemoryMarshal.AsBytes(positions.AsSpan()));
            cmd.Barrier(BarrierBatch.Full);
            Blas?.Dispose();
            _updatable = Refit;
            _refitsSinceBuild = 0;
            Blas = AccelerationStructure.BuildBottom(ctx.Device, cmd, [new TriangleGeometry(ctx.Device.Address(_vertices.Handle), 12,
                (uint)positions.Length, ctx.Device.Address(_indices.Handle), (uint)(cells * cells * 2))], name, _updatable);
            if (!_updatable) Blas.ReleaseScratch();
            Centre = new Vector3(centre.X, 0, centre.Z);
            Record = new GeometryRecord
            {
                Vertices = ctx.Device.Address(_vertices.Handle), Indices = ctx.Device.Address(_indices.Handle),
                Stride = 12, Kind = GeometryRecord.Terrain,
            };
        }

        /// <summary>
        /// The grid's size, for the log ("none" before it is built).
        /// </summary>
        public string Describe() => Blas is null ? "none" : $"{cells}² × {spacing:0}";

        /// <summary>
        /// Waits for the sampling and frees the structure and buffers.
        /// </summary>
        public void Dispose()
        {
            _job?.Wait();
            Blas?.Dispose();
            _vertices?.Dispose();
            _indices?.Dispose();
        }
    }

    #endregion
}