using System.Numerics;
using Meitou.Data.Ogre;
using Silk.NET.OpenGL;

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
    /// <summary>Index buffers with every level back to back, made on the worker.</summary>
    public List<PreparedPart> Prepared { get; } = [];
}

sealed class PreparedPart
{
    public required ModelPart Part;
    public required uint[] All;
    public required int[] Offset, Count;
    public uint[][]? Levels;
}

/// <summary>One submesh on the GPU: one vertex buffer, one index buffer holding every LOD level back to back.</summary>
sealed class GpuObjectPart
{
    public required int SubMeshIndex;
    public required string MaterialName;
    public required bool HasTangents, HasColours;
    public uint Vao, Vbo, Ebo;
    /// <summary>First index and index count of each level in <see cref="Ebo"/> (a manual level has none: count 0).</summary>
    public required int[] Offset, Count;
    /// <summary>For the terrain shader's mesh path (which draws from index 0): a vertex array per level, made when first asked for.</summary>
    public uint[]? PlainVao;
    public uint[]? PlainEbo;
    public uint[][]? LevelIndices;
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

    public enum State { None, Loading, Uploading, Resident, Failed }
}

/// <summary>
/// Loads the world's meshes: <see cref="Request"/> from anywhere on the render thread, <see cref="Pump"/> once per frame starts
/// the nearest wanted decodes on worker threads and queues the uploads of finished ones.
/// </summary>
sealed unsafe class ObjectMeshCache(IGl gl, AssetLocator assets, UploadQueue uploads, uint instanceBuffer) : IDisposable
{
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
    public int Pending => running.Count + wanted.Count;
    public bool Idle => running.Count == 0 && wanted.Count == 0;

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
        if (wanted.Count == 0) return;
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
            m.Job = BackgroundWork.Run(() => Decode(key, distant, 0, keep));
            running.Add(m);
        }
    }

    DecodedObjectMesh? Decode(string name, bool distant, int depth, bool keep)
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
        var levels = MeshLod.Levels(mesh);
        var manual = new Dictionary<int, DecodedObjectMesh>();
        for (int l = 1; l < levels.Count; l++)
        {
            if (levels[l].ManualMesh is not { } manualName) continue;
            // A manual level shows another mesh file; one that cannot be had ends the chain here.
            var inner = depth == 0 ? Decode(manualName, distant, depth + 1, keep) : null;
            if (inner is null) { levels.RemoveRange(l, levels.Count - l); break; }
            manual[l] = inner;
        }
        var centre = model.Center;
        float radius = model.Radius;
        if (mesh.Bounds is { } b && b.Max.X >= b.Min.X) { centre = (b.Min + b.Max) / 2; radius = Math.Max((b.Max - b.Min).Length() / 2, 1e-3f); }
        var result = new DecodedObjectMesh { Model = model, Levels = levels, Manual = manual, Centre = centre, Radius = radius };
        foreach (var part in model.Parts) result.Prepared.Add(PreparePart(result, part, keep));
        return result;
    }

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
    };

    /// <summary>
    /// What a worker makes of a part before the render thread sees it: the index buffer with every LOD level back to back. Level 0 is the part's
    /// own triangle list; a reduced level is the file's list for this submesh (or the previous level's if the file has none). Strips and fans were
    /// triangulated, so they keep level 0 for every level.
    /// </summary>
    static PreparedPart PreparePart(DecodedObjectMesh decoded, ModelPart part, bool keepLevels)
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
        return new PreparedPart { Part = part, All = all, Offset = offset, Count = count, Levels = keepLevels ? arrays : null };
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
                Vao = gl.GenVertexArray(), Vbo = gl.GenBuffer(), Ebo = gl.GenBuffer(), Offset = prepared.Offset, Count = prepared.Count, LevelIndices = prepared.Levels,
            };
            gl.BindVertexArray(gp.Vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gp.Vbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)vertexBytes, null, BufferUsageARB.StaticDraw);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gp.Ebo);
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)indexBytes, null, BufferUsageARB.StaticDraw);
            gl.BindVertexArray(0);
        }, label + " (allocate)");
        for (int at = 0; at < vertexBytes; at += SlabBytes)
        {
            int start = at, length = Math.Min(SlabBytes, vertexBytes - at);
            uploads.Add(() =>
            {
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, gp!.Vbo);
                fixed (Vertex* p = part.Vertices) gl.BufferSubData(BufferTargetARB.ArrayBuffer, start, (nuint)length, (byte*)p + start);
            }, label + " (vertices)");
        }
        for (int at = 0; at < indexBytes; at += SlabBytes)
        {
            int start = at, length = Math.Min(SlabBytes, indexBytes - at);
            uploads.Add(() =>
            {
                gl.BindVertexArray(gp!.Vao);   // the element buffer binding belongs to the vertex array
                fixed (uint* p = prepared.All) gl.BufferSubData(BufferTargetARB.ElementArrayBuffer, start, (nuint)length, (byte*)p + start);
                gl.BindVertexArray(0);
            }, label + " (indices)");
        }
        uploads.Add(() =>
        {
            gl.BindVertexArray(gp!.Vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gp.Vbo);
            VertexAttributes(part.Vertices.Length > 0);
            // Per-instance matrix rows (attributes 7 to 10), re-pointed at the batch's offset before each draw.
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceBuffer);
            for (uint a = 0; a < 4; a++)
            {
                gl.EnableVertexAttribArray(BuildingLodShaders.InstanceLocation + a);
                gl.VertexAttribPointer(BuildingLodShaders.InstanceLocation + a, 4, VertexAttribPointerType.Float, false, 64, (void*)(16 * a));
                gl.VertexAttribDivisor(BuildingLodShaders.InstanceLocation + a, 1);
            }
            gl.BindVertexArray(0);
            gpu.Parts.Add(gp);
            gpu.Bytes += vertexBytes + indexBytes;
        }, label + " (vertex array)");
    }

    void VertexAttributes(bool any)
    {
        if (!any) return;
        uint stride = (uint)Vertex.Size;
        void Attrib(uint index, int size, int offset)
        {
            gl.EnableVertexAttribArray(index);
            gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)offset);
        }
        // Same layout as Renderer.Upload; bones and weights stay zero (no skinning in the world view).
        Attrib(0, 3, 0);
        Attrib(1, 3, 12);
        Attrib(2, 2, 24);
        Attrib(3, 4, 32);
        Attrib(4, 4, 48);
        gl.EnableVertexAttribArray(5);
        gl.VertexAttribIPointer(5, 4, VertexAttribIType.UnsignedByte, stride, (void*)64);
        Attrib(6, 4, 68);
    }

    /// <summary>
    /// A vertex array for the terrain shader's mesh path, which draws from the first index: level 0 shares the part's buffers,
    /// a reduced level gets an index buffer of its own (made on first use, from the indices kept for this).
    /// </summary>
    public uint PlainVao(GpuObjectPart part, int level)
    {
        part.PlainVao ??= new uint[part.Count.Length];
        part.PlainEbo ??= new uint[part.Count.Length];
        if (part.PlainVao[level] != 0) return part.PlainVao[level];
        if (level > 0 && part.LevelIndices is null) level = 0;
        if (part.PlainVao[level] != 0) return part.PlainVao[level];
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, part.Vbo);
        VertexAttributes(true);
        if (level == 0) gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, part.Ebo);
        else
        {
            uint ebo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, part.LevelIndices![level].AsSpan(), BufferUsageARB.StaticDraw);
            part.PlainEbo[level] = ebo;
        }
        gl.BindVertexArray(0);
        part.PlainVao[level] = vao;
        return vao;
    }

    public int Unloads { get; private set; }
    public int Reloads { get; private set; }
    /// <summary>Unused for this long: unloaded.</summary>
    public double IdleSeconds { get; set; } = StreamingTuning.IdleSeconds;
    /// <summary>Above this the least recently used meshes unused for <see cref="PressureIdleSeconds"/> go too, down to three quarters of it.</summary>
    public double HighWaterMb { get; set; } = StreamingTuning.IdleSeconds > 1e8 ? double.MaxValue : 768;
    public double PressureIdleSeconds { get; set; } = 8;
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
        bool pressure = Bytes > HighWaterMb * 1048576;
        List<ObjectMesh>? victims = null;
        foreach (var m in meshes.Values)
        {
            if (m.Status != ObjectMesh.State.Resident) continue;
            double idle = (now - m.LastUsed) / 1000.0;
            if (idle > IdleSeconds || pressure && idle > PressureIdleSeconds) (victims ??= []).Add(m);
        }
        if (victims is null) return;
        victims.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        long lowWater = (long)(HighWaterMb * 1048576 * 0.75);
        foreach (var m in victims.Take(24))
        {
            if ((now - m.LastUsed) / 1000.0 <= IdleSeconds && Bytes <= lowWater) break;
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
        foreach (var gp in gpu.Parts)
        {
            gl.DeleteVertexArray(gp.Vao);
            gl.DeleteBuffer(gp.Vbo);
            gl.DeleteBuffer(gp.Ebo);
            if (gp.PlainVao is not null) foreach (var v in gp.PlainVao) if (v != 0) gl.DeleteVertexArray(v);
            if (gp.PlainEbo is not null) foreach (var b in gp.PlainEbo) if (b != 0) gl.DeleteBuffer(b);
        }
        foreach (var m in gpu.Manual) if (m is not null) Delete(m);
    }
}

/// <summary>A part's look: the resolved material and its textures (ids are 0 until decoded and uploaded).</summary>
sealed class ObjectPartMaterial(SurfaceMaterial? material, WorldTexture? diffuse, WorldTexture? normal, WorldTexture? diffuse2, WorldTexture? normal2)
{
    public SurfaceMaterial? Material => material;
    public uint Diffuse => diffuse?.Id ?? 0;
    public uint Normal => normal?.Id ?? 0;
    public uint Diffuse2 => diffuse2?.Id ?? 0;
    public uint Normal2 => normal2?.Id ?? 0;
    public bool Swizzled => normal?.Swizzled ?? false;
}

/// <summary>The materials of a mesh's parts for one placement (parts in the mesh's order).</summary>
sealed class ObjectMaterialSet(ObjectPartMaterial[] parts)
{
    public ObjectPartMaterial[] Parts => parts;
    public int Stamp;

    /// <summary>Reading a texture id counts as using the texture (<see cref="WorldTexture.Id"/>); this keeps all of them from being unloaded.</summary>
    public void Touch()
    {
        foreach (var p in parts) _ = (p.Diffuse, p.Normal, p.Diffuse2, p.Normal2);
    }
}
