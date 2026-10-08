using System.Numerics;
using Meitou.Data.Ogre;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>A mesh as a worker thread decoded it: level 0's vertices and indices, the LOD levels, and the meshes of manual levels.</summary>
sealed class DecodedObjectMesh
{
    public required Model Model;
    public required List<MeshLodLevel> Levels;
    /// <summary>Manual levels (an extra mesh file each) by level number.</summary>
    public required Dictionary<int, DecodedObjectMesh> Manual;
    public required Vector3 Centre;
    public required float Radius;
    /// <summary>The finest level held (0: all of them): see <see cref="ObjectMeshCache.LevelFor"/>.</summary>
    public int MinLevel;
    /// <summary>Index buffers with every level back to back, made on the worker.</summary>
    public List<PreparedPart> Prepared { get; } = [];
}

sealed class PreparedPart
{
    public required ModelPart Part;
    public required uint[] All;
    public required int[] Offset, Count;
    public uint[][]? Levels;
    /// <summary>World length per texture-coordinate unit that nearly all of the part's surface is at least as stretched as (<see cref="MeshTexelScale.Of"/>).</summary>
    public float UvScale = float.PositiveInfinity;
}

/// <summary>One submesh on the GPU: one vertex buffer, one index buffer holding every LOD level back to back (native buffers, "object meshes").</summary>
sealed class GpuObjectPart
{
    public required int SubMeshIndex;
    public required string MaterialName;
    public required bool HasTangents, HasColours;
    public required DeviceBuffer Vertices, Indices;
    /// <summary>The vertex attributes at locations 0 to 6 as the GL vertex array had them (all null for a part without vertices).</summary>
    public LegacyProgram.Attribute?[] Attributes = [];
    /// <summary>First index and index count of each level in <see cref="Indices"/> (a manual level has none: count 0).</summary>
    public required int[] Offset, Count;
    /// <summary>For the terrain shader's mesh path (which draws from index 0): the mesh per level, made when first asked for, and the index
    /// buffers of the reduced levels.</summary>
    public MeshBindings?[]? PlainMesh;
    public DeviceBuffer?[]? PlainEbo;
    public uint[][]? LevelIndices;
    /// <summary>World length per texture-coordinate unit (<see cref="MeshTexelScale"/>): how large a texel is, which says how far away a
    /// texture's top mips stop mattering (infinity: unknown, they always matter).</summary>
    public float UvScale = float.PositiveInfinity;
    /// <summary>What a native draw needs, per program (<see cref="WorldObjectRenderer"/>).</summary>
    public ObjectNativeMesh ColourNative, DepthNative;
}

/// <summary>A part's native state for one program: its vertex layout and own vertex buffers, its element buffer, and the pipelines for the
/// last two segment states (the reflection's multisampled target alternates with the scene's).</summary>
struct ObjectNativeMesh
{
    public VertexLayout? Layout;
    public BufferBinding[] Vertices;
    public BufferBinding Elements;
    public int SegA, SegB;
    public GraphicsPipeline? PipeA, PipeB;
}

sealed class GpuObjectMesh
{
    public List<GpuObjectPart> Parts { get; } = [];
    public required float[] Distances;
    /// <summary>For each level: the mesh to draw instead (manual levels), else null.</summary>
    public required GpuObjectMesh?[] Manual;
    public required Vector3 Centre;
    public required float Radius;
    public long Bytes;
    /// <summary>The finest LOD level whose triangles are held (<see cref="ObjectMeshCache.LevelFor"/>): a draw at a finer level uses this one instead.</summary>
    public int MinLevel;
    public int LevelCount => Distances.Length;
}

/// <summary>A mesh file of the world: loaded on demand, decoded on a worker thread, uploaded in steps on the render thread.</summary>
sealed class ObjectMesh
{
    public required string Key;
    /// <summary>A building's or town's distant mesh: vertex colours ×1.5 (the game's DistantTown shader multiplies by that).</summary>
    public bool Distant;
    /// <summary>The mesh is also drawn through the terrain shader's mesh path, which needs per-level vertex arrays.</summary>
    public bool KeepLevelIndices;
    public State Status;
    public Task<DecodedObjectMesh?>? Job;
    public GpuObjectMesh? Gpu;
    /// <summary>When the mesh was last drawn (or became resident), <see cref="Environment.TickCount64"/>; what <see cref="ObjectMeshCache.Trim"/> unloads by.</summary>
    public long LastUsed;
    public long Bytes;
    public bool EverLoaded;
    /// <summary>Distance-based streaming of the finest LOD (<see cref="ObjectMeshCache.Retarget"/>): this pass's nearest user (LOD value before the bias), the
    /// mark pass that found it, when the mesh started to be finer than needed, and the reshape decode in flight.</summary>
    public float NearPass = float.PositiveInfinity;
    public int NearStamp;
    public long TooFineSince;
    public Task<DecodedObjectMesh?>? Reshape;
    /// <summary>The reshape in flight is for finer levels (counted in <see cref="ObjectMeshCache.Upgrading"/>).</summary>
    public bool Upgrade;
    /// <summary>The nearest placement wanted while it waited for its first decode (raw distance), for the level it is made with.</summary>
    public float WantedNear = float.PositiveInfinity;

    public enum State { None, Loading, Uploading, Resident, Failed }
}

/// <summary>
/// Loads the world's meshes: <see cref="Request"/> from anywhere on the render thread, <see cref="Pump"/> once per frame starts
/// the nearest wanted decodes on worker threads and queues the uploads of finished ones.
/// </summary>
sealed unsafe class ObjectMeshCache(GpuContext gpuContext, AssetLocator assets, UploadQueue uploads) : IDisposable
{
    /// <summary>The buffers' allocation name (the F12 VRAM pie groups by it).</summary>
    const string AllocationName = "object meshes";

    readonly Dictionary<string, ObjectMesh> meshes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<ObjectMesh, float> wanted = [];
    readonly List<ObjectMesh> running = [];
    int frame;

    /// <summary>Decodes running at once.</summary>
    public int MaxJobs { get; set; } = 4;
    public List<string> Messages { get; } = [];
    public int Resident => meshes.Values.Count(m => m.Status == ObjectMesh.State.Resident);
    public int Total => meshes.Count;
    public long Bytes { get; private set; }
    /// <summary>Decodes running or wanted, and meshes whose upload is queued.</summary>
    public int Pending => running.Count + wanted.Count + reshaping.Count + reshapeQueued;
    public bool Idle => running.Count == 0 && wanted.Count == 0 && reshaping.Count == 0 && reshapeQueued == 0;

    // ---- distance-based streaming of the finest LOD level (docs/renderer-native.md 8.12) ----

    readonly List<ObjectMesh> reshaping = [];
    /// <summary>Reshaped meshes whose upload is queued (until the swap).</summary>
    int reshapeQueued;
    /// <summary>Meshes remade with another finest level held: finer (something came near), coarser (nothing near needs it), and the finer ones in flight.</summary>
    public int Refined { get; private set; }
    public int Coarsened { get; private set; }
    public int Upgrading { get; private set; }
    /// <summary>Seconds a mesh must have held finer levels than needed before it is remade without them (under the guard's pressure: no wait).</summary>
    public double CoarsenAfterSeconds { get; set; } = 10;
    /// <summary>Reshape decodes at once (the worker threads are few and shared with the foliage's layouts).</summary>
    public int MaxReshapes { get; set; } = 2;
    /// <summary>The LOD bias the renderer draws with (the level a mesh is held at follows the LOD value times it).</summary>
    public float LodBias { get; set; } = 1;
    /// <summary>How far ahead of the camera a mesh is kept ready for (the renderer's <c>MeshMargin</c>: the camera's speed over 1.5 s), taken off the distance a new mesh is made for.</summary>
    public float LookAhead { get; set; }
    /// <summary><c>MEITOU_MESH_STREAM=0</c> loads every level of every mesh, for comparisons.</summary>
    public static readonly bool FarForms = Environment.GetEnvironmentVariable("MEITOU_MESH_STREAM") != "0";

    /// <summary>The nearest user's LOD value <paramref name="near"/> (before the LOD bias) of a resident mesh in this mark pass: remakes it with finer levels
    /// when it needs them, or, after a while, without the finer ones nothing needs.</summary>
    public void Rebalance(ObjectMesh m, float near)
    {
        if (!FarForms || m.Status != ObjectMesh.State.Resident || m.Gpu is not { } gpu || m.Reshape is not null || m.Distant) return;
        if (m.KeepLevelIndices)
        {
            // A map feature drawn through the terrain shader wants every level (it was marked after the mesh was made for farther users).
            if (gpu.MinLevel > 0 && Retarget(m, 0)) { Refined++; Upgrading++; m.Upgrade = true; }
            return;
        }
        int need = LevelFor(gpu.Distances, near * LodBias);
        if (need < gpu.MinLevel)
        {
            // One level more than needed, so a camera on its way in does not ask again at the next boundary.
            if (Retarget(m, near * 0.7f)) { Refined++; Upgrading++; m.Upgrade = true; }
        }
        else if (need > gpu.MinLevel)
        {
            long now = Environment.TickCount64;
            if (m.TooFineSince == 0) m.TooFineSince = now;
            if ((Guard?.Pressure ?? false) || now - m.TooFineSince > CoarsenAfterSeconds * 1000)
                if (Retarget(m, near)) Coarsened++;
        }
        else m.TooFineSince = 0;
    }

    /// <summary>Starts a decode of a resident mesh that keeps the levels a user at LOD value <paramref name="near"/> can draw; false when not started (jobs busy, memory pressure, not resident).</summary>
    public bool Retarget(ObjectMesh m, float near)
    {
        if (!FarForms || m.Status != ObjectMesh.State.Resident || m.Reshape is not null || m.Distant) return false;
        if (reshaping.Count >= MaxReshapes || Guard is { Streaming: false } && m.Gpu!.MinLevel < LevelFor(m.Gpu.Distances, near * LodBias)) return false;
        string key = m.Key;
        float bias = LodBias;
        bool keep = m.KeepLevelIndices;
        m.Reshape = BackgroundWork.Run(() => Decode(key, false, 0, keep, near, false, bias));
        reshaping.Add(m);
        return true;
    }

    /// <summary>The reshape decodes that finished: the new parts are queued for upload, and swapped in place of the old once uploaded.</summary>
    void PumpReshapes()
    {
        for (int i = 0; i < reshaping.Count; i++)
        {
            var m = reshaping[i];
            if (!m.Reshape!.IsCompleted) continue;
            reshaping.RemoveAt(i--);
            DecodedObjectMesh? decoded = null;
            try { decoded = m.Reshape.Result; }
            catch (AggregateException e) { Messages.Add($"mesh {m.Key}: {e.InnerException?.Message ?? e.Message}"); }
            if (decoded is null || m.Gpu is not { } old || old.MinLevel == decoded.MinLevel) { EndReshape(m); continue; }
            QueueReshape(m, decoded);
        }
    }

    void EndReshape(ObjectMesh m)
    {
        m.Reshape = null;
        if (m.Upgrade) { m.Upgrade = false; Upgrading--; }
    }

    public ObjectMesh Get(string key, bool distant = false)
    {
        if (!meshes.TryGetValue(key, out var m)) meshes[key] = m = new ObjectMesh { Key = key, Distant = distant };
        return m;
    }

    /// <summary>Asks for a mesh to be loaded; smaller <paramref name="priority"/> (distance) goes first.</summary>
    public void Request(ObjectMesh mesh, float priority)
    {
        if (mesh.Status != ObjectMesh.State.None) return;
        wanted[mesh] = wanted.TryGetValue(mesh, out var old) ? Math.Min(old, priority) : priority;
        mesh.WantedNear = Math.Min(mesh.WantedNear, priority + 600);   // the scan takes 600 off the camera's distance to the placement
    }

    /// <summary>Starts decodes (nearest first) and queues the uploads of finished ones.</summary>
    public void Pump()
    {
        frame++;
        for (int i = 0; i < running.Count; i++)
        {
            var m = running[i];
            if (!m.Job!.IsCompleted) continue;
            running.RemoveAt(i--);
            DecodedObjectMesh? decoded = null;
            try { decoded = m.Job.Result; }
            catch (AggregateException e) { Messages.Add($"mesh {m.Key}: {e.InnerException?.Message ?? e.Message}"); }
            m.Job = null;
            if (decoded is null) { m.Status = ObjectMesh.State.Failed; continue; }
            m.Status = ObjectMesh.State.Uploading;
            QueueUpload(m, decoded);
        }
        PumpReshapes();
        if (wanted.Count == 0) return;
        if (Guard is { Streaming: false }) { wanted.Clear(); return; }   // memory pressure: nothing new is decoded (the scan asks again)
        var order = wanted.OrderBy(p => p.Value).Select(p => p.Key).ToList();
        wanted.Clear();
        foreach (var m in order)
        {
            if (running.Count >= MaxJobs) break;
            if (m.Status != ObjectMesh.State.None) continue;
            m.Status = ObjectMesh.State.Loading;
            string key = m.Key;
            bool distant = m.Distant;
            bool keep = m.KeepLevelIndices;
            float near = FarForms ? Math.Max(m.WantedNear - LookAhead, 0) : float.PositiveInfinity, bias = LodBias;
            m.WantedNear = float.PositiveInfinity;
            m.Job = BackgroundWork.Run(() => Decode(key, distant, 0, keep, near, true, bias));
            running.Add(m);
        }
    }

    /// <param name="near">How near the mesh is wanted, for the finest LOD level worth holding (<see cref="LevelFor"/>): the camera's distance to the nearest
    /// placement (<paramref name="nearIsPlacement"/>: the mesh's size is not known yet, six radii are taken off) or, else, the nearest LOD value
    /// (before the LOD bias). Infinity: every level.</param>
    DecodedObjectMesh? Decode(string name, bool distant, int depth, bool keep, float near, bool nearIsPlacement, float lodBias)
    {
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        if (path is null) { lock (Messages) Messages.Add($"mesh not found: {name}"); return null; }
        OgreMesh mesh;
        try { mesh = OgreMeshReader.ReadFile(path); }
        catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException)
        {
            lock (Messages) Messages.Add($"mesh {name}: {e.Message}");
            return null;
        }
        var model = Model.Build(mesh, null);
        if (distant)
            foreach (var part in model.Parts)
                for (int i = 0; i < part.Vertices.Length; i++)
                    part.Vertices[i].Colour = new Vector4(part.Vertices[i].Colour.X * 1.5f, part.Vertices[i].Colour.Y * 1.5f, part.Vertices[i].Colour.Z * 1.5f, part.Vertices[i].Colour.W);
        var centre = model.Center;
        float radius = model.Radius;
        if (mesh.Bounds is { } b && b.Max.X >= b.Min.X) { centre = (b.Min + b.Max) / 2; radius = Math.Max((b.Max - b.Min).Length() / 2, 1e-3f); }
        var levels = MeshLod.Levels(mesh);
        // The finest level anything near enough can draw: the levels above it are not decoded or uploaded (the vertices only they use, and their
        // indices and manual meshes, stay out). Only a mesh drawn through the normal path (not the terrain shader's, which wants every level) and
        // not a distant one, and not an inner (manual) mesh, which is drawn at its level 0.
        int minLevel = 0;
        if (depth == 0 && !distant && !keep && float.IsFinite(near))
        {
            var distances = new float[levels.Count];
            for (int l = 0; l < levels.Count; l++) distances[l] = levels[l].Distance;
            minLevel = LevelFor(distances, (nearIsPlacement ? near - 6 * radius : near) * lodBias);
        }
        var manual = new Dictionary<int, DecodedObjectMesh>();
        for (int l = 1; l < levels.Count; l++)
        {
            if (levels[l].ManualMesh is not { } manualName) continue;
            // A manual level shows another mesh file; one that cannot be had ends the chain here.
            DecodedObjectMesh? inner = null;
            if (depth == 0)
            {
                if (l >= minLevel) inner = Decode(manualName, distant, depth + 1, keep, float.PositiveInfinity, false, lodBias);
                else if (assets.Find(manualName) is not null || assets.Find(Path.GetFileName(manualName.Replace('\\', '/'))) is not null) continue;   // a level above the finest wanted: its file is there, it is not read
            }
            if (inner is null) { levels.RemoveRange(l, levels.Count - l); break; }
            manual[l] = inner;
        }
        if (minLevel >= levels.Count) minLevel = levels.Count - 1;   // the chain ended earlier than the distances said
        var result = new DecodedObjectMesh { Model = model, Levels = levels, Manual = manual, Centre = centre, Radius = radius, MinLevel = minLevel };
        foreach (var part in model.Parts) result.Prepared.Add(PreparePart(result, part, keep));
        return result;
    }

    /// <summary>
    /// The finest LOD level that can be drawn at a LOD value of <paramref name="value"/> (already times the LOD bias): the level
    /// <see cref="MeshLod.Select(ReadOnlySpan{float}, float)"/> picks, or the one above while <see cref="MeshLod.Blend"/> still fades it in
    /// (the band is 6% of the distance; taken as 7%).
    /// </summary>
    public static int LevelFor(ReadOnlySpan<float> distances, float value) => MeshLod.Select(distances, Math.Max(value, 0) / 1.07f);

    void QueueUpload(ObjectMesh mesh, DecodedObjectMesh decoded)
    {
        var gpu = Prepare(decoded);
        for (int i = 0; i < decoded.Prepared.Count; i++) QueuePart(gpu, decoded.Prepared[i], $"mesh {mesh.Key} part {i}");
        foreach (var (level, inner) in decoded.Manual)
        {
            var innerGpu = gpu.Manual[level]!;
            for (int i = 0; i < inner.Prepared.Count; i++) QueuePart(innerGpu, inner.Prepared[i], $"mesh {mesh.Key} manual {level} part {i}");
        }
        uploads.Add(() =>
        {
            mesh.Gpu = gpu;
            mesh.Status = ObjectMesh.State.Resident;
            mesh.Bytes = gpu.Bytes + gpu.Manual.Sum(m => m?.Bytes ?? 0);
            mesh.LastUsed = Environment.TickCount64;
            if (mesh.EverLoaded) Reloads++;
            mesh.EverLoaded = true;
            Bytes += mesh.Bytes;
        }, $"mesh {mesh.Key} ready");
    }

    static GpuObjectMesh Prepare(DecodedObjectMesh d) => new()
    {
        Distances = d.Levels.Select(l => l.Distance).ToArray(),
        Manual = Enumerable.Range(0, d.Levels.Count).Select(l => d.Manual.TryGetValue(l, out var m) ? Prepare(m) : null).ToArray(),
        Centre = d.Centre,
        Radius = d.Radius,
        MinLevel = d.MinLevel,
    };

    /// <summary>
    /// What a worker makes of a part before the render thread sees it: the index buffer with every LOD level back to back. Level 0 is the part's
    /// own triangle list; a reduced level is the file's list for this submesh (or the previous level's if the file has none). Strips and fans were
    /// triangulated, so they keep level 0 for every level.
    /// </summary>
    internal static PreparedPart PreparePart(DecodedObjectMesh decoded, ModelPart part, bool keepLevels)
    {
        int levelCount = decoded.Levels.Count;
        var arrays = new uint[levelCount][];
        arrays[0] = part.Indices;
        for (int l = 1; l < levelCount; l++)
        {
            var level = decoded.Levels[l];
            uint[]? own = level.ManualMesh is null && part.SubMeshIndex < level.Indices.Count ? level.Indices[part.SubMeshIndex] : null;
            arrays[l] = level.ManualMesh is not null ? [] : own is { Length: > 0 } && own.Length % 3 == 0 ? own : arrays[l - 1];
        }
        float uvScale = MeshTexelScale.Of(part);   // of the whole part: the textures' mip streaming needs it however few levels are held
        int minLevel = decoded.MinLevel;
        if (minLevel > 0)
        {
            // Only the levels from minLevel on: the vertices they use, in the order the first of them reaches them, and their indices remapped;
            // the levels above hold nothing (a draw that asks for one draws nothing: the renderer never does, see GpuObjectMesh.MinLevel).
            var remap = new int[part.Vertices.Length];
            Array.Fill(remap, -1);
            var kept = new List<Vertex>();
            for (int l = minLevel; l < levelCount; l++)
                foreach (uint index in arrays[l])
                    if (remap[index] < 0) { remap[index] = kept.Count; kept.Add(part.Vertices[index]); }
            for (int l = 0; l < levelCount; l++)
            {
                if (l < minLevel) { arrays[l] = []; continue; }
                var source = arrays[l];
                var mapped = new uint[source.Length];
                for (int i = 0; i < mapped.Length; i++) mapped[i] = (uint)remap[source[i]];
                arrays[l] = mapped;
            }
            part = new ModelPart
            {
                SubMeshIndex = part.SubMeshIndex, MaterialName = part.MaterialName, Vertices = kept.ToArray(), Indices = arrays[minLevel],
                HasUv = part.HasUv, HasTangents = part.HasTangents, HasColours = part.HasColours, Skinned = part.Skinned,
            };
        }
        var offset = new int[levelCount];
        var count = new int[levelCount];
        int total = 0;
        for (int l = 0; l < levelCount; l++)
        {
            offset[l] = total;
            count[l] = arrays[l].Length;
            total += arrays[l].Length;
        }
        var all = new uint[total];
        for (int l = 0; l < levelCount; l++) arrays[l].CopyTo(all, offset[l]);
        return new PreparedPart { Part = part, All = all, Offset = offset, Count = count, Levels = keepLevels ? arrays : null, UvScale = uvScale };
    }

    const int SlabBytes = 512 << 10;

    /// <summary>A part's upload as steps of at most about 512 KB: allocate the buffers, fill them in slabs, set up the vertex array.</summary>
    void QueuePart(GpuObjectMesh gpu, PreparedPart prepared, string label)
    {
        var part = prepared.Part;
        GpuObjectPart? gp = null;
        int vertexBytes = part.Vertices.Length * Vertex.Size, indexBytes = prepared.All.Length * 4;
        uploads.Add(() =>
        {
            gp = new GpuObjectPart
            {
                SubMeshIndex = part.SubMeshIndex, MaterialName = part.MaterialName, HasTangents = part.HasTangents, HasColours = part.HasColours,
                Vertices = DeviceBuffer.Create(gpuContext, (ulong)vertexBytes, BufferUse.Vertex, AllocationName),
                Indices = DeviceBuffer.Create(gpuContext, (ulong)indexBytes, BufferUse.Index, AllocationName),
                Offset = prepared.Offset, Count = prepared.Count, LevelIndices = prepared.Levels, UvScale = prepared.UvScale,
            };
        }, label + " (allocate)");
        for (int at = 0; at < vertexBytes; at += SlabBytes)
        {
            int start = at, length = Math.Min(SlabBytes, vertexBytes - at);
            uploads.Add(() =>
            {
                gpuContext.EnsureFrame();
                gpuContext.Uploads.Write(gp!.Vertices, (ulong)start, System.Runtime.InteropServices.MemoryMarshal.AsBytes(part.Vertices.AsSpan()).Slice(start, length));
            }, label + " (vertices)");
        }
        for (int at = 0; at < indexBytes; at += SlabBytes)
        {
            int start = at, length = Math.Min(SlabBytes, indexBytes - at);
            uploads.Add(() =>
            {
                gpuContext.EnsureFrame();
                gpuContext.Uploads.Write(gp!.Indices, (ulong)start, System.Runtime.InteropServices.MemoryMarshal.AsBytes(prepared.All.AsSpan()).Slice(start, length));
            }, label + " (indices)");
        }
        uploads.Add(() =>
        {
            // The per-instance matrix rows (attributes 7 to 10) are bound per segment, from the frame's constants.
            gp!.Attributes = part.Vertices.Length > 0 ? VertexAttributes(gp.Vertices) : [];
            gpu.Parts.Add(gp);
            gpu.Bytes += vertexBytes + indexBytes;
        }, label + " (vertex array)");
    }

    /// <summary>The vertex layout of <see cref="Vertex"/> (as <c>Renderer.Upload</c>; bones and weights stay zero: no skinning in the world view).</summary>
    internal static readonly MeshAttribute[] Layout =
    [
        new(0, 3, 0), new(1, 3, 12), new(2, 2, 24), new(3, 4, 32), new(4, 4, 48), new(5, 4, 64, Integer: true), new(6, 4, 68),
    ];

    /// <summary>The attributes at locations 0 to 6 as VkGl exported the GL vertex array (one binding per attribute, at its offset).</summary>
    internal static LegacyProgram.Attribute?[] VertexAttributes(DeviceBuffer vertices)
    {
        var result = new LegacyProgram.Attribute?[Layout.Length];
        foreach (var a in Layout)
        {
            var format = a.Integer ? GlConventions.VertexFormat(GLEnum.UnsignedByte, a.Size, false, true) : GlConventions.VertexFormat(GLEnum.Float, a.Size, false, false);
            result[a.Location] = new LegacyProgram.Attribute(new BufferBinding(vertices.Handle, (ulong)a.Offset), format, (uint)Vertex.Size, false);
        }
        return result;
    }

    /// <summary>
    /// The part as the terrain shader's mesh path takes it, which draws from the first index: level 0 shares the part's buffers, a reduced level
    /// gets an index buffer of its own (made on first use, from the indices kept for this).
    /// </summary>
    public MeshBindings PlainMesh(GpuObjectPart part, int level)
    {
        part.PlainMesh ??= new MeshBindings?[part.Count.Length];
        part.PlainEbo ??= new DeviceBuffer?[part.Count.Length];
        if (part.PlainMesh[level] is { } made) return made;
        if (level > 0 && part.LevelIndices is null) level = 0;
        if (part.PlainMesh[level] is { } level0) return level0;
        var elements = part.Indices;
        if (level > 0)
        {
            var indices = part.LevelIndices![level];
            elements = DeviceBuffer.Create(gpuContext, (ulong)indices.Length * 4, BufferUse.Index, AllocationName);
            gpuContext.EnsureFrame();
            gpuContext.Uploads.Write(elements, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.AsSpan()));
            part.PlainEbo[level] = elements;
        }
        return part.PlainMesh[level] = MeshBindings.Of(VertexAttributes(part.Vertices), elements);
    }

    public int Unloads { get; private set; }
    public int Reloads { get; private set; }
    /// <summary>Unused for this long: unloaded.</summary>
    public double IdleSeconds { get; set; } = StreamingTuning.IdleSeconds;
    /// <summary>Above this the least recently used meshes unused for <see cref="PressureIdleSeconds"/> go too, down to three quarters of it.</summary>
    public double HighWaterMb { get; set; } = StreamingTuning.IdleSeconds > 1e8 ? double.MaxValue : 768;
    public double PressureIdleSeconds { get; set; } = 8;
    /// <summary>The high-water mark as a share of the driver's video memory budget (<see cref="WorldTextureCache.HighWaterShare"/>): 768 MB on the 11.4 GB card it was tuned on.</summary>
    public double HighWaterShare { get; set; } = 768 / 11453.0;
    public double MarkMb => WorldTextureCache.EffectiveMark(HighWaterMb, HighWaterShare, Guard);
    /// <summary>The memory-pressure guard (<see cref="VramGuard"/>): under pressure nothing new is decoded and meshes idle for <see cref="GuardIdleSeconds"/> go.</summary>
    public VramGuard? Guard { get; set; }
    public double GuardIdleSeconds { get; set; } = 2;
    /// <summary>Called with every GPU mesh (manual levels included) that was deleted, so users of it can forget it.</summary>
    public Action<GpuObjectMesh>? Unloaded { get; set; }
    long lastTrim;

    /// <summary>
    /// Deletes the GPU buffers of meshes not drawn for <see cref="IdleSeconds"/> (earlier, least recently used first, while over the high-water
    /// mark). The mesh goes back to <see cref="ObjectMesh.State.None"/>: whoever wants it again <see cref="Request"/>s it and it is decoded and
    /// uploaded afresh. At most once a second, at most 24 meshes a call.
    /// </summary>
    public void Trim()
    {
        long now = Environment.TickCount64;
        if (now - lastTrim < 1000) return;
        lastTrim = now;
        bool guarded = Guard?.Pressure ?? false;
        bool pressure = Bytes > MarkMb * 1048576;
        List<ObjectMesh>? victims = null;
        foreach (var m in meshes.Values)
        {
            if (m.Status != ObjectMesh.State.Resident || m.Reshape is not null) continue;
            double idle = (now - m.LastUsed) / 1000.0;
            if (idle > IdleSeconds || pressure && idle > PressureIdleSeconds || guarded && idle > GuardIdleSeconds) (victims ??= []).Add(m);
        }
        if (victims is null) return;
        victims.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        long lowWater = (long)(MarkMb * 1048576 * 0.75);
        foreach (var m in victims.Take(guarded ? 72 : 24))
        {
            if ((now - m.LastUsed) / 1000.0 <= IdleSeconds && !guarded && Bytes <= lowWater) break;
            var gpu = m.Gpu!;
            Delete(gpu);
            Bytes -= m.Bytes;
            m.Bytes = 0;
            m.Gpu = null;
            m.Status = ObjectMesh.State.None;
            Unloads++;
        }
    }

    /// <summary>Waits for every running decode (offscreen rendering).</summary>
    public void WaitForJobs()
    {
        foreach (var m in running.ToArray()) { try { m.Job!.Wait(); } catch (AggregateException) { } }
        foreach (var m in reshaping.ToArray()) { try { m.Reshape!.Wait(); } catch (AggregateException) { } }
    }

    public void Dispose()
    {
        WaitForJobs();
        foreach (var mesh in meshes.Values)
            if (mesh.Gpu is { } gpu) Delete(gpu);
    }

    void Delete(GpuObjectMesh gpu)
    {
        Unloaded?.Invoke(gpu);
        DeleteBuffers(gpu);
        foreach (var m in gpu.Manual) if (m is not null) Delete(m);
    }

    /// <summary>The buffers of the mesh's own parts (freed after the frames in flight).</summary>
    static void DeleteBuffers(GpuObjectMesh gpu)
    {
        foreach (var gp in gpu.Parts)
        {
            if (gp.PlainEbo is not null) foreach (var b in gp.PlainEbo) b?.Dispose();
            gp.Vertices.Dispose();
            gp.Indices.Dispose();
        }
    }

    /// <summary>
    /// The new parts of a resident mesh remade with another finest level (<see cref="Retarget"/>) are uploaded like a new mesh's, then, in one step,
    /// put in place of the old ones in the same <see cref="GpuObjectMesh"/> (the instances and batches that hold it keep drawing, the old parts until the
    /// swap). The mesh is the same in every other way (bounds, distances), so nothing waits for it.
    /// </summary>
    void QueueReshape(ObjectMesh mesh, DecodedObjectMesh decoded)
    {
        var fresh = Prepare(decoded);
        for (int i = 0; i < decoded.Prepared.Count; i++) QueuePart(fresh, decoded.Prepared[i], $"mesh {mesh.Key} part {i} (reshaped)");
        foreach (var (level, inner) in decoded.Manual)
        {
            var innerGpu = fresh.Manual[level]!;
            for (int i = 0; i < inner.Prepared.Count; i++) QueuePart(innerGpu, inner.Prepared[i], $"mesh {mesh.Key} manual {level} part {i} (reshaped)");
        }
        reshapeQueued++;
        uploads.Add(() =>
        {
            reshapeQueued--;
            if (mesh.Gpu is not { } old || mesh.Status != ObjectMesh.State.Resident || old.Manual.Length != fresh.Manual.Length)
            {
                DeleteBuffers(fresh);
                foreach (var m in fresh.Manual) if (m is not null) { DeleteBuffers(m); }
                EndReshape(mesh);
                return;
            }
            long before = mesh.Bytes;
            DeleteBuffers(old);
            for (int l = 0; l < old.Manual.Length; l++)
            {
                if (old.Manual[l] is { } gone) Delete(gone);   // its batches go (Unloaded); the instances hold the outer mesh, not this
                old.Manual[l] = fresh.Manual[l];
            }
            old.Parts.Clear();
            old.Parts.AddRange(fresh.Parts);
            old.Bytes = fresh.Bytes;
            old.MinLevel = fresh.MinLevel;
            mesh.Bytes = old.Bytes + old.Manual.Sum(m => m?.Bytes ?? 0);
            Bytes += mesh.Bytes - before;
            mesh.TooFineSince = 0;
            EndReshape(mesh);
        }, $"mesh {mesh.Key} reshaped");
    }
}

/// <summary>A part's look: the resolved material and its textures (keys are 0 until decoded and uploaded: <see cref="WorldTexture.Key"/>).</summary>
sealed class ObjectPartMaterial(SurfaceMaterial? material, WorldTexture? diffuse, WorldTexture? normal, WorldTexture? diffuse2, WorldTexture? normal2,
    float texelScale = float.PositiveInfinity, bool triplanar = false)
{
    public SurfaceMaterial? Material => material;
    /// <summary>The DUST define: a building part always has it (docs/formats/runtime-materials.md), a map feature when its material says so.</summary>
    public bool Dust { get; init; }
    public uint Diffuse => diffuse?.Key ?? 0;
    public uint Normal => normal?.Key ?? 0;
    public uint Diffuse2 => diffuse2?.Key ?? 0;
    public uint Normal2 => normal2?.Key ?? 0;
    public bool Swizzled => normal?.Swizzled ?? false;

    /// <summary>World length one unit of the texture coordinates the material samples spans, before the instance's scale (the part's
    /// <see cref="MeshTexelScale"/> over the material's tiling; for a triplanar material the world's own mapping): how large a texel is.
    /// Infinity when unknown (the textures keep every mip).</summary>
    public float TexelScale => texelScale;
    /// <summary>The coordinates come from the world position, not the mesh: the instance's scale does not enter.</summary>
    public bool Triplanar => triplanar;
    /// <summary>The part has a diffuse map (resident or not).</summary>
    public bool WantsDiffuse => diffuse is not null;

    /// <summary>The textures' need (<see cref="WorldTexture.Offer"/>) for a user at <paramref name="distance"/> (already over the instance's scale unless triplanar).</summary>
    public void Offer(float distance)
    {
        float need = distance / texelScale;
        diffuse?.Offer(need);
        normal?.Offer(need);
        diffuse2?.Offer(need);
        normal2?.Offer(need);
    }

    /// <summary>As <see cref="Offer"/> for a user that just appeared.</summary>
    public void OfferNow(float distance)
    {
        float need = distance / texelScale;
        diffuse?.OfferNow(need);
        normal?.OfferNow(need);
        diffuse2?.OfferNow(need);
        normal2?.OfferNow(need);
    }
}

/// <summary>The materials of a mesh's parts for one placement (parts in the mesh's order).</summary>
sealed class ObjectMaterialSet(ObjectPartMaterial[] parts)
{
    public ObjectPartMaterial[] Parts => parts;
    public int Stamp;
    /// <summary>This mark pass's nearest user: its distance, and its distance over its scale (<see cref="ObjectPartMaterial.Triplanar"/> parts take the first).</summary>
    public float Near, NearScaled;
    public int NeedStamp;

    /// <summary>Reading a texture key counts as using the texture (<see cref="WorldTexture.Key"/>); this keeps all of them from being unloaded.</summary>
    public void Touch()
    {
        foreach (var p in parts) _ = (p.Diffuse, p.Normal, p.Diffuse2, p.Normal2);
    }
}
