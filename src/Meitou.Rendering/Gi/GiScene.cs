using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gi;

/// <summary>What a traced hit reads of its geometry (32 bytes, <c>GiShaders.Geometry</c>): the vertex positions (three floats at the start of each
/// vertex, <see cref="Stride"/> apart) and the triangle's three 32-bit indices, by device address.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GeometryRecord
{
    public ulong Vertices, Indices;
    public uint Stride, Kind, Pad0, Pad1;
    public const int Size = 32;
    /// <summary><see cref="Kind"/>: what the geometry is (the debug views colour by it).</summary>
    public const uint Terrain = 0, Object = 1;
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
    /// <summary>Most instances in the top-level structure, and most geometry records.</summary>
    public const int MaxInstances = 16384, MaxRecords = 65536;
    /// <summary>Most new object structures built in one frame (the rest wait for the next).</summary>
    public int MaxBuildsPerFrame { get; set; } = 32;
    /// <summary>Objects whose bounds come this close to the eye are in the scene.</summary>
    public float Range { get; set; } = 6000;
    /// <summary>A mesh's structure not used for this many frames is dropped.</summary>
    const int KeepFrames = 600;

    readonly GpuContext ctx;
    readonly VulkanDevice device;
    readonly TerrainGrid[] terrain;
    readonly Dictionary<object, MeshEntry> meshes = new(ReferenceEqualityComparer.Instance);
    readonly FrameSlot[] slots;
    readonly List<InstanceRecord> instances = [];
    readonly List<GeometryRecord> records = [];
    readonly List<object> stale = [];
    long frame;

    /// <summary>The world position the traced coordinates are relative to (this frame's).</summary>
    public Vector3 Origin { get; private set; }
    /// <summary>This frame's top-level structure (null before the first <see cref="Update"/>).</summary>
    public AccelerationStructure? Top { get; private set; }
    /// <summary>This frame's geometry records (a storage buffer range).</summary>
    public BufferBinding Records { get; private set; }
    /// <summary>Statistics of the last update.</summary>
    public int InstanceCount { get; private set; }
    public int MeshCount => meshes.Count;
    public int BuiltThisFrame { get; private set; }
    public long MeshBytes { get; private set; }
    /// <summary>Render-thread time of the last <see cref="Update"/>.</summary>
    public float UpdateMs { get; private set; }

    sealed class MeshEntry
    {
        public required AccelerationStructure Blas;
        public required GeometryRecord[] Records;
        // The buffers it was built from: a reshape (BuildingLodMesh) puts new parts into the same GpuObjectMesh and frees these, so the records
        // would point at freed memory (a hit reads its triangle: device lost).
        public required object[] Sources;
        public long LastUsed;
    }

    sealed class FrameSlot
    {
        public required GpuBuffer Instances, Records;
        public required AccelerationStructure Top;
    }

    public GiScene(GpuContext ctx)
    {
        if (!ctx.Device.HasRayQuery) throw new NotSupportedException("the global illumination needs ray queries (VK_KHR_ray_query)");
        this.ctx = ctx;
        device = ctx.Device;
        terrain = [new TerrainGrid(ctx, "gi terrain fine", 256, 16), new TerrainGrid(ctx, "gi terrain coarse", 256, 160)];
        int count = device.Frames.Count;
        slots = new FrameSlot[count];
        const BufferUsageFlags use = BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.StorageBufferBit;
        for (int i = 0; i < count; i++)
            slots[i] = new FrameSlot
            {
                Instances = device.Allocator.CreateBuffer((ulong)(MaxInstances * InstanceRecord.Size), use, MemoryKind.Upload, "gi instances"),
                Records = device.Allocator.CreateBuffer((ulong)(MaxRecords * GeometryRecord.Size), use, MemoryKind.Upload, "gi geometry records"),
                Top = AccelerationStructure.CreateTop(device, MaxInstances, "gi top level"),
            };
    }

    /// <summary>
    /// Records this frame's builds into a native command list: the terrain grids that finished on their worker, new object structures, and the
    /// top-level structure over the terrain and the objects of <paramref name="objects"/> near <paramref name="eye"/>. Ends with a barrier to the
    /// tracing shaders.
    /// </summary>
    public void Update(Vector3 eye, HeightSnapshot heights, WorldObjectRenderer? objects)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        frame++;
        Origin = new Vector3(MathF.Round(eye.X / 64) * 64, MathF.Round(eye.Y / 64) * 64, MathF.Round(eye.Z / 64) * 64);
        var slot = slots[ctx.Frame.Slot];
        instances.Clear();
        records.Clear();
        BuiltThisFrame = 0;
        var cmd = ctx.BeginNative("gi scene");
        cmd.BeginLabel("gi scene");
        // The uploads of this frame (object meshes, terrain grids) come first.
        cmd.Barrier(BarrierBatch.Full);

        foreach (var grid in terrain)
        {
            grid.Update(cmd, eye, heights);
            if (grid.Blas is null) continue;
            var world = Matrix4x4.CreateTranslation(grid.Centre - Origin);
            // Facing culled: the grid is coarser than the drawn terrain, so a surface in a hollow lies under it and its rays must pass up through it
            // (the grid's triangles are counter-clockwise seen from above, which Vulkan's ray tracing takes as the front: RayQueryTests).
            instances.Add(InstanceRecord.Of(world, (uint)records.Count, 0x01, grid.Blas.Address, 0));
            records.Add(grid.Record);
        }

        if (objects is not null)
            foreach (var inst in objects.RayInstances(eye, Range))
            {
                if (instances.Count >= MaxInstances) break;
                var entry = EntryFor(cmd, inst.Gpu!);
                if (entry is null) continue;
                if (records.Count + entry.Records.Length > MaxRecords) break;
                entry.LastUsed = frame;
                var world = inst.Transform;
                world.Translation -= Origin;
                instances.Add(InstanceRecord.Of(world, (uint)records.Count, 0x02, entry.Blas.Address));
                records.AddRange(entry.Records);
            }
        Evict();

        // The new bottom levels must be complete before the top level reads them.
        cmd.Barrier(AccelerationStructure.BuildToTrace);
        CollectionsMarshal.AsSpan(instances).CopyTo(new Span<InstanceRecord>(slot.Instances.Mapped, MaxInstances));
        CollectionsMarshal.AsSpan(records).CopyTo(new Span<GeometryRecord>(slot.Records.Mapped, MaxRecords));
        var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = slot.Instances.Buffer };
        slot.Top.RebuildTop(cmd, device.Vk.GetBufferDeviceAddress(device.Device, in info), (uint)instances.Count);
        cmd.Barrier(AccelerationStructure.BuildToTrace);
        cmd.EndLabel();
        ctx.EndNative(cmd);
        Top = slot.Top;
        Records = new BufferBinding(slot.Records.Buffer, 0, (ulong)(Math.Max(records.Count, 1) * GeometryRecord.Size));
        InstanceCount = instances.Count;
        UpdateMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>The mesh's structure, built now when there is budget left this frame (null: not yet, or nothing to trace).</summary>
    MeshEntry? EntryFor(CommandList cmd, GpuObjectMesh gpu)
    {
        if (BuiltThisFrame >= MaxBuildsPerFrame && !meshes.ContainsKey(gpu)) return null;
        // The finest level the mesh holds: its own parts at that level, or the manual mesh standing in for it.
        int level = Math.Clamp(gpu.MinLevel, 0, Math.Max(gpu.LevelCount - 1, 0));
        var source = gpu.Manual.Length > level && gpu.Manual[level] is { } manual ? manual : gpu;
        int sourceLevel = ReferenceEquals(source, gpu) ? level : 0;
        if (meshes.TryGetValue(gpu, out var entry))
        {
            if (BuiltFrom(entry, source)) return entry;
            Drop(gpu, entry);
            if (BuiltThisFrame >= MaxBuildsPerFrame) return null;
        }
        var sources = source.Parts.Select(p => (object)p.Vertices).Prepend(source).ToArray();
        var geometries = new List<TriangleGeometry>();
        var recs = new List<GeometryRecord>();
        foreach (var part in source.Parts)
        {
            if (part.Count.Length <= sourceLevel) continue;
            int count = part.Count[sourceLevel];
            if (count < 3) continue;
            ulong vertices = part.Vertices.Address, indices = part.Indices.Address + (ulong)part.Offset[sourceLevel] * 4;
            geometries.Add(new TriangleGeometry(vertices, (uint)Vertex.Size, (uint)(part.Vertices.Size / (ulong)Vertex.Size), indices, (uint)(count / 3)));
            recs.Add(new GeometryRecord { Vertices = vertices, Indices = indices, Stride = (uint)Vertex.Size, Kind = GeometryRecord.Object });
        }
        if (geometries.Count == 0) return null;
        var blas = AccelerationStructure.BuildBottom(device, cmd, CollectionsMarshal.AsSpan(geometries), "gi object");
        blas.ReleaseScratch();
        BuiltThisFrame++;
        MeshBytes += (long)blas.Bytes;
        entry = new MeshEntry { Blas = blas, Records = [.. recs], Sources = sources, LastUsed = frame };
        meshes[gpu] = entry;
        return entry;
    }

    void Evict()
    {
        stale.Clear();
        foreach (var (key, entry) in meshes)
            if (frame - entry.LastUsed > KeepFrames) stale.Add(key);
        foreach (var key in stale) Drop(key, meshes[key]);
    }

    static bool BuiltFrom(MeshEntry entry, GpuObjectMesh source)
    {
        var s = entry.Sources;
        if (s.Length != source.Parts.Count + 1 || !ReferenceEquals(s[0], source)) return false;
        for (int i = 0; i < source.Parts.Count; i++)
            if (!ReferenceEquals(s[i + 1], source.Parts[i].Vertices)) return false;
        return true;
    }

    void Drop(object key, MeshEntry entry)
    {
        MeshBytes -= (long)entry.Blas.Bytes;
        entry.Blas.Dispose();
        meshes.Remove(key);
    }

    public string Describe() =>
        $"{UpdateMs:0.00} ms, {InstanceCount} instances, {meshes.Count} object structures ({MeshBytes / (1024 * 1024)} MB), {BuiltThisFrame} built this frame, terrain {string.Join(" + ", terrain.Select(t => t.Describe()))}";

    public void Dispose()
    {
        foreach (var t in terrain) t.Dispose();
        foreach (var e in meshes.Values) e.Blas.Dispose();
        meshes.Clear();
        foreach (var s in slots)
        {
            s.Top.Dispose();
            device.DeferFree(s.Instances);
            device.DeferFree(s.Records);
        }
    }

    /// <summary>
    /// A square height-field grid of <c>cells</c> × <c>cells</c> quads of <c>spacing</c> around a centre, sampled from the height snapshot on a
    /// worker and uploaded, then built into a bottom-level structure. Rebuilt when the eye has left the middle eighth.
    /// </summary>
    sealed class TerrainGrid(GpuContext ctx, string name, int cells, float spacing) : IDisposable
    {
        DeviceBuffer? vertices, indices;
        Task<(Vector3 Centre, Vector3[] Positions)>? job;
        public AccelerationStructure? Blas { get; private set; }
        public Vector3 Centre { get; private set; }
        public GeometryRecord Record { get; private set; }
        // The centre of the newest grid asked for (the one being sampled, or built): Centre moves only when its structure is built, so the
        // instance never places the old grid at the new centre.
        Vector3 requested;
        bool hasRequested;
        float Side => cells * spacing;

        public void Update(CommandList cmd, Vector3 eye, HeightSnapshot heights)
        {
            if (job is { IsCompleted: true })
            {
                var (centre, positions) = job.Result;
                job = null;
                Build(cmd, centre, positions);
            }
            var wanted = new Vector3(MathF.Round(eye.X / spacing) * spacing, 0, MathF.Round(eye.Z / spacing) * spacing);
            bool far = !hasRequested || MathF.Max(MathF.Abs(wanted.X - requested.X), MathF.Abs(wanted.Z - requested.Z)) > Side / 8;
            if (far && job is null)
            {
                hasRequested = true;
                requested = wanted;
                job = Task.Run(() => (wanted, Sample(wanted, heights)));
                if (Blas is null) job.Wait();   // the first grid at once: the first frames see the terrain
                if (Blas is null)
                {
                    var (c, p) = job.Result;
                    job = null;
                    Build(cmd, c, p);
                }
            }
        }

        Vector3[] Sample(Vector3 centre, HeightSnapshot heights)
        {
            int n = cells + 1;
            var p = new Vector3[n * n];
            float half = Side / 2;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    float x = centre.X - half + i * spacing, z = centre.Z - half + j * spacing;
                    p[j * n + i] = new Vector3(x - centre.X, heights.HeightAt(x, z), z - centre.Z);
                }
            return p;
        }

        void Build(CommandList cmd, Vector3 centre, Vector3[] positions)
        {
            int n = cells + 1;
            if (indices is null)
            {
                var idx = new uint[cells * cells * 6];
                int k = 0;
                for (int j = 0; j < cells; j++)
                    for (int i = 0; i < cells; i++)
                    {
                        uint a = (uint)(j * n + i), b = a + 1, c = a + (uint)n, d = c + 1;
                        idx[k++] = a; idx[k++] = c; idx[k++] = b;
                        idx[k++] = b; idx[k++] = c; idx[k++] = d;
                    }
                indices = DeviceBuffer.Create(ctx, (ulong)idx.Length * 4, BufferUse.Index | BufferUse.RayInput, name);
                ctx.Uploads.Write(indices, 0, MemoryMarshal.AsBytes(idx.AsSpan()));
            }
            // A new vertex buffer every rebuild: the last frames' structures were built from the old one, which is freed after them.
            vertices?.Dispose();
            vertices = DeviceBuffer.Create(ctx, (ulong)(positions.Length * 12), BufferUse.Vertex | BufferUse.RayInput, name);
            ctx.Uploads.Write(vertices, 0, MemoryMarshal.AsBytes(positions.AsSpan()));
            cmd.Barrier(BarrierBatch.Full);
            Blas?.Dispose();
            Blas = AccelerationStructure.BuildBottom(ctx.Device, cmd, [new TriangleGeometry(vertices.Address, 12, (uint)positions.Length, indices.Address, (uint)(cells * cells * 2))], name);
            Blas.ReleaseScratch();
            Centre = new Vector3(centre.X, 0, centre.Z);
            Record = new GeometryRecord { Vertices = vertices.Address, Indices = indices.Address, Stride = 12, Kind = GeometryRecord.Terrain };
        }

        public string Describe() => Blas is null ? "none" : $"{cells}² × {spacing:0}";

        public void Dispose()
        {
            job?.Wait();
            Blas?.Dispose();
            vertices?.Dispose();
            indices?.Dispose();
        }
    }
}
