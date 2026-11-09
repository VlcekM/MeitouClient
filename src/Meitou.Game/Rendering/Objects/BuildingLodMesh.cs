using System.Numerics;
using Meitou.Data.Ogre;

using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// A mesh as a worker thread decoded it: level 0's vertices and indices, the LOD levels, and the meshes of manual levels.
/// </summary>
sealed class DecodedObjectMesh
{
    /// <summary>
    /// Level 0 as built from the file (welded when <see cref="MeshWeld"/> is on; a distant mesh's colours brightened).
    /// </summary>
    public required Model Model;

    /// <summary>
    /// The LOD levels (the file's, with the generated ones merged in), level 0 first; cut short where a manual level's mesh could not be had.
    /// </summary>
    public required List<MeshLodLevel> Levels;

    /// <summary>
    /// Manual levels (an extra mesh file each) by level number.
    /// </summary>
    public required Dictionary<int, DecodedObjectMesh> Manual;

    /// <summary>
    /// The bounds' centre in the mesh's own space.
    /// </summary>
    public required Vector3 Centre;

    /// <summary>
    /// The bounding radius in the mesh's own space.
    /// </summary>
    public required float Radius;

    /// <summary>
    /// The mesh's highest local Y (the scaffold clip of an unfinished building scales with it).
    /// </summary>
    public float Top;

    /// <summary>
    /// The local bounding box: the file's bounds, else the vertices'.
    /// </summary>
    public Vector3 Min, Max;

    /// <summary>
    /// How the levels are picked (<see cref="LodCurve"/>); null: the distances as they are.
    /// </summary>
    public LodCurve? Curve;

    /// <summary>
    /// The finest level held (0: all of them): see <see cref="ObjectMeshCache.LevelFor"/>.
    /// </summary>
    public int MinLevel;

    /// <summary>
    /// Index buffers with every level back to back, made on the worker.
    /// </summary>
    public List<PreparedPart> Prepared { get; } = [];
}

/// <summary>
/// A part as the worker prepared it for the upload: its vertices packed and its index buffer with every level back to back.
/// </summary>
sealed class PreparedPart
{
    /// <summary>
    /// The part, with only the vertices the held levels use when the finest level is above 0.
    /// </summary>
    public required ModelPart Part;

    /// <summary>
    /// Every level's indices back to back.
    /// </summary>
    public required uint[] All;

    /// <summary>
    /// Each level's first index and index count in <see cref="All"/> (0 for a level not held).
    /// </summary>
    public required int[] Offset, Count;

    /// <summary>
    /// Each level's own index list, kept for the terrain shader's mesh path (null when not asked for).
    /// </summary>
    public uint[][]? Levels;

    /// <summary>
    /// The vertices in the GPU's object layout (<see cref="GpuVertex"/>), packed on the decoding thread.
    /// </summary>
    public byte[] Packed = [];

    /// <summary>
    /// World length per texture-coordinate unit that nearly all of the part's surface is at least as stretched as (<see cref="MeshTexelScale.Of"/>).
    /// </summary>
    public float UvScale = float.PositiveInfinity;
}

/// <summary>
/// One submesh on the GPU: one vertex buffer, one index buffer holding every LOD level back to back (native buffers, "object meshes").
/// </summary>
sealed class GpuObjectPart
{
    /// <summary>
    /// The submesh's index in the file.
    /// </summary>
    public required int SubMeshIndex;

    /// <summary>
    /// The submesh's material name.
    /// </summary>
    public required string MaterialName;

    /// <summary>
    /// The vertices have tangents (a normal map can be read) and colours (vertex colouring, the second texture set's blend weight).
    /// </summary>
    public required bool HasTangents, HasColours;

    /// <summary>
    /// The vertex buffer (<see cref="Stride"/> bytes per vertex) and the index buffer of every level.
    /// </summary>
    public required DeviceBuffer Vertices, Indices;

    /// <summary>
    /// Bytes per vertex in <see cref="Vertices"/>: <see cref="GpuVertex.ObjectSize"/>, or <see cref="GpuVertex.CharacterSize"/> for a character's
    /// part.
    /// </summary>
    public int Stride = GpuVertex.ObjectSize;

    /// <summary>
    /// The vertex attributes by location (<see cref="GpuVertex"/>; empty for a part without vertices).
    /// </summary>
    public LegacyProgram.Attribute?[] Attributes = [];

    /// <summary>
    /// First index and index count of each level in <see cref="Indices"/> (a manual level has none: count 0).
    /// </summary>
    public required int[] Offset, Count;

    /// <summary>
    /// For the terrain shader's mesh path (which draws from index 0): the mesh per level, made when first asked for, and the index
    /// buffers of the reduced levels.
    /// </summary>
    public MeshBindings?[]? PlainMesh;

    /// <summary>
    /// Index buffers of the reduced levels for <see cref="PlainMesh"/>, made on first use (level 0 shares <see cref="Indices"/>).
    /// </summary>
    public DeviceBuffer?[]? PlainEbo;

    /// <summary>
    /// Each level's own index list (<see cref="PreparedPart.Levels"/>), kept when the mesh is drawn through the terrain shader.
    /// </summary>
    public uint[][]? LevelIndices;

    /// <summary>
    /// World length per texture-coordinate unit (<see cref="MeshTexelScale"/>): how large a texel is, which says how far away a
    /// texture's top mips stop mattering (infinity: unknown, they always matter).
    /// </summary>
    public float UvScale = float.PositiveInfinity;

    /// <summary>
    /// What a native draw needs, per program (<see cref="WorldObjectRenderer"/>).
    /// </summary>
    public ObjectNativeMesh ColourNative, DepthNative, ColourSolidNative;
}

/// <summary>
/// A part's native state for one program: its vertex layout and own vertex buffers, its element buffer, and the pipelines for the
/// last two segment states (the reflection's multisampled target alternates with the scene's).
/// </summary>
struct ObjectNativeMesh
{
    /// <summary>
    /// The vertex layout for the program (null: not made yet).
    /// </summary>
    public VertexLayout? Layout;

    /// <summary>
    /// The part's own vertex buffers by location, GL's constant where none.
    /// </summary>
    public GpuBufferBinding[] Vertices;

    /// <summary>
    /// The element buffer.
    /// </summary>
    public GpuBufferBinding Elements;

    /// <summary>
    /// The segment ids of the last two pipelines (A the newest).
    /// </summary>
    public int SegA, SegB;

    /// <summary>
    /// The pipelines for <see cref="SegA"/> and <see cref="SegB"/>.
    /// </summary>
    public GpuPipeline? PipeA, PipeB;
}

/// <summary>
/// A mesh on the GPU: its parts with every level's indices, its LOD distances and pick curve, the manual levels' meshes, bounds and size.
/// </summary>
sealed class GpuObjectMesh
{
    /// <summary>
    /// The submeshes, in the file's order.
    /// </summary>
    public List<GpuObjectPart> Parts { get; } = [];

    /// <summary>
    /// The LOD levels' distances, level 0 first.
    /// </summary>
    public required float[] Distances;

    /// <summary>
    /// How a draw picks among the levels (the merged list with the generated ones, or the file's only: <see cref="ObjectLodGen.Enabled"/>).
    /// </summary>
    public LodCurve Curve { get => _curve ??= LodCurve.Of(Distances); set => _curve = value; }

    /// <summary>
    /// The curve behind <see cref="Curve"/> (made from <see cref="Distances"/> on first read when not set).
    /// </summary>
    LodCurve? _curve;

    /// <summary>
    /// For each level: the mesh to draw instead (manual levels), else null.
    /// </summary>
    public required GpuObjectMesh?[] Manual;

    /// <summary>
    /// The bounds' centre in the mesh's own space.
    /// </summary>
    public required Vector3 Centre;

    /// <summary>
    /// The bounding radius in the mesh's own space.
    /// </summary>
    public required float Radius;

    /// <summary>
    /// The mesh's highest local Y (the scaffold clip of an unfinished building scales with it).
    /// </summary>
    public float Top;

    /// <summary>
    /// The local bounding box (<see cref="DecodedObjectMesh.Min"/>).
    /// </summary>
    public Vector3 Min, Max;

    /// <summary>
    /// The bytes of the own parts' buffers (the manual levels' counted on their own meshes).
    /// </summary>
    public long Bytes;

    /// <summary>
    /// The finest LOD level whose triangles are held (<see cref="ObjectMeshCache.LevelFor"/>): a draw at a finer level uses this one instead.
    /// </summary>
    public int MinLevel;

    /// <summary>
    /// The number of LOD levels.
    /// </summary>
    public int LevelCount => Distances.Length;
}

/// <summary>
/// A mesh file of the world: loaded on demand, decoded on a worker thread, uploaded in steps on the render thread.
/// </summary>
sealed class ObjectMesh
{
    /// <summary>
    /// The mesh file's name as the records give it (the cache's key, case-insensitive).
    /// </summary>
    public required string Key;

    /// <summary>
    /// A building's or town's distant mesh: vertex colours ×1.5 (the game's DistantTown shader multiplies by that).
    /// </summary>
    public bool Distant;

    /// <summary>
    /// The mesh is also drawn through the terrain shader's mesh path, which needs per-level vertex arrays.
    /// </summary>
    public bool KeepLevelIndices;

    /// <summary>
    /// Where the mesh is: not loaded, decoding, uploading, resident or failed.
    /// </summary>
    public State Status;

    /// <summary>
    /// The decode running on a worker (null when none).
    /// </summary>
    public Task<DecodedObjectMesh?>? Job;

    /// <summary>
    /// The mesh on the GPU while resident.
    /// </summary>
    public GpuObjectMesh? Gpu;

    /// <summary>
    /// When the mesh was last drawn (or became resident), <see cref="Environment.TickCount64"/>; what <see cref="ObjectMeshCache.Trim"/> unloads by.
    /// </summary>
    public long LastUsed;

    /// <summary>
    /// The bytes the mesh and its manual levels hold on the GPU while resident.
    /// </summary>
    public long Bytes;

    /// <summary>
    /// The mesh was resident before: a new upload counts as a reload.
    /// </summary>
    public bool EverLoaded;

    /// <summary>
    /// Distance-based streaming of the finest LOD (<see cref="ObjectMeshCache.Retarget"/>): this pass's nearest user (LOD value before the bias), the
    /// mark pass that found it, when the mesh started to be finer than needed, and the reshape decode in flight.
    /// </summary>
    public float NearPass = float.PositiveInfinity;

    /// <summary>
    /// The mark pass that set <see cref="NearPass"/>.
    /// </summary>
    public int NearStamp;

    /// <summary>
    /// When the mesh started to hold finer levels than its users need (<see cref="Environment.TickCount64"/> ms; 0: not).
    /// </summary>
    public long TooFineSince;

    /// <summary>
    /// The reshape decode in flight (<see cref="ObjectMeshCache.Retarget"/>; null: none).
    /// </summary>
    public Task<DecodedObjectMesh?>? Reshape;

    /// <summary>
    /// The reshape in flight is for finer levels (counted in <see cref="ObjectMeshCache.Upgrading"/>).
    /// </summary>
    public bool Upgrade;

    /// <summary>
    /// The nearest placement wanted while it waited for its first decode (raw distance), for the level it is made with.
    /// </summary>
    public float WantedNear = float.PositiveInfinity;

    /// <summary>
    /// Where a mesh is in its life: not loaded (or unloaded), decoding on a worker, uploading in steps, resident, or failed to read.
    /// </summary>
    public enum State
    {
        /// <summary>
        /// Not loaded (or unloaded): a request queues its decode.
        /// </summary>
        None,

        /// <summary>
        /// Decoding on a worker.
        /// </summary>
        Loading,

        /// <summary>
        /// Decoded; its upload steps are queued.
        /// </summary>
        Uploading,

        /// <summary>
        /// On the GPU and drawable.
        /// </summary>
        Resident,

        /// <summary>
        /// The file was not found or could not be read.
        /// </summary>
        Failed
    }
}

/// <summary>
/// Loads the world's meshes: <see cref="Request"/> from anywhere on the render thread, <see cref="Pump"/> once per frame starts
/// the nearest wanted decodes on worker threads and queues the uploads of finished ones.
/// </summary>
sealed unsafe class ObjectMeshCache(IGpuContext gpuContext, AssetLocator assets, UploadQueue uploads) : IDisposable
{
    /// <summary>
    /// The buffers' allocation name (the F12 VRAM pie groups by it).
    /// </summary>
    const string AllocationName = "object meshes";

    /// <summary>
    /// The buffers' extra use: inputs of the ray-tracing acceleration structures when the device has ray queries.
    /// </summary>
    BufferUse RayInput => gpuContext.HasRayQuery ? BufferUse.RayInput : 0;

    /// <summary>
    /// Every mesh asked for, by file name (case-insensitive).
    /// </summary>
    readonly Dictionary<string, ObjectMesh> _meshes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The meshes wanted for a decode, by load order (smaller first): taken and cleared at each <see cref="Pump"/>.
    /// </summary>
    readonly Dictionary<ObjectMesh, float> _wanted = [];

    /// <summary>
    /// The meshes decoding on workers.
    /// </summary>
    readonly List<ObjectMesh> _running = [];

    /// <summary>
    /// The frames <see cref="Pump"/> has run.
    /// </summary>
    int _frame;

    /// <summary>
    /// Decodes running at once.
    /// </summary>
    public int MaxJobs { get; set; } = 4;

    /// <summary>
    /// Warnings for the log (missing or unreadable meshes); the renderer writes and clears them.
    /// </summary>
    public List<string> Messages { get; } = [];

    /// <summary>
    /// The meshes resident now.
    /// </summary>
    public int Resident => _meshes.Values.Count(m => m.Status == ObjectMesh.State.Resident);

    /// <summary>
    /// Every mesh asked for so far.
    /// </summary>
    public int Total => _meshes.Count;

    /// <summary>
    /// The bytes the resident meshes hold on the GPU.
    /// </summary>
    public long Bytes { get; private set; }

    /// <summary>
    /// Decodes running or wanted, and meshes whose upload is queued.
    /// </summary>
    public int Pending => _running.Count + _wanted.Count + _reshaping.Count + _reshapeQueued;

    /// <summary>
    /// Nothing decodes, waits for a decode or waits for its reshaped upload.
    /// </summary>
    public bool Idle => _running.Count == 0 && _wanted.Count == 0 && _reshaping.Count == 0 && _reshapeQueued == 0;

    // ---- distance-based streaming of the finest LOD level (docs/renderer-native.md 8.12) ----

    /// <summary>
    /// The meshes with a reshape decode in flight.
    /// </summary>
    readonly List<ObjectMesh> _reshaping = [];

    /// <summary>
    /// Reshaped meshes whose upload is queued (until the swap).
    /// </summary>
    int _reshapeQueued;

    /// <summary>
    /// Meshes remade with another finest level held: finer (something came near), coarser (nothing near needs it), and the finer ones in flight.
    /// </summary>
    public int Refined { get; private set; }

    /// <summary>
    /// Meshes remade without the finer levels nothing near needs.
    /// </summary>
    public int Coarsened { get; private set; }

    /// <summary>
    /// Reshapes for finer levels in flight.
    /// </summary>
    public int Upgrading { get; private set; }

    /// <summary>
    /// Seconds a mesh must have held finer levels than needed before it is remade without them (under the guard's pressure: no wait).
    /// </summary>
    public double CoarsenAfterSeconds { get; set; } = 10;

    /// <summary>
    /// Reshape decodes at once (the worker threads are few and shared with the foliage's layouts).
    /// </summary>
    public int MaxReshapes { get; set; } = 2;

    /// <summary>
    /// The LOD bias the renderer draws with (the level a mesh is held at follows the LOD value times it).
    /// </summary>
    public float LodBias { get; set; } = 1;

    /// <summary>
    /// The render's pixels per radian, which the generated levels' distances are worked out with when a mesh loads (<see cref="ObjectLodGen"/>); the
    /// 1080-line, 50 degree picture until the first frame.
    /// </summary>
    public float PixelsPerRadian { get; set; } = 1080 / (2 * MathF.Tan(25 * MathF.PI / 180));

    /// <summary>
    /// How far ahead of the camera a mesh is kept ready for (the renderer's <c>MeshMargin</c>: the camera's speed over 1.5 s), taken off the distance
    /// a new mesh is made for.
    /// </summary>
    public float LookAhead { get; set; }

    /// <summary>
    /// The nearest user's LOD value <paramref name="near"/> (before the LOD bias) of a resident mesh in this mark pass: remakes it with finer levels
    /// when it needs them, or, after a while, without the finer ones nothing needs.
    /// </summary>
    public void Rebalance(ObjectMesh m, float near)
    {
        if (m.Status != ObjectMesh.State.Resident || m.Gpu is not { } gpu || m.Reshape is not null || m.Distant) return;
        if (m.KeepLevelIndices)
        {
            // A map feature drawn through the terrain shader wants every level (it was marked after the mesh was made for farther users).
            if (gpu.MinLevel > 0 && Retarget(m, 0))
            {
                Refined++;
                Upgrading++;
                m.Upgrade = true;
            }
            return;
        }
        int need = gpu.Curve.LevelFor(near * LodBias);
        if (need < gpu.MinLevel)
        {
            // One level more than needed, so a camera on its way in does not ask again at the next boundary.
            if (Retarget(m, near * 0.7f))
            {
                Refined++;
                Upgrading++;
                m.Upgrade = true;
            }
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

    /// <summary>
    /// Starts a decode of a resident mesh that keeps the levels a user at LOD value <paramref name="near"/> can draw; false when not started (jobs
    /// busy, memory pressure, not resident).
    /// </summary>
    public bool Retarget(ObjectMesh m, float near)
    {
        if (m.Status != ObjectMesh.State.Resident || m.Reshape is not null || m.Distant) return false;
        if (_reshaping.Count >= MaxReshapes || Guard is { Streaming: false } && m.Gpu!.MinLevel < m.Gpu.Curve.LevelFor(near * LodBias)) return false;
        string key = m.Key;
        float bias = LodBias;
        bool keep = m.KeepLevelIndices;
        m.Reshape = BackgroundWork.Run(() => Decode(key, false, 0, keep, near, false, bias));
        _reshaping.Add(m);
        return true;
    }

    /// <summary>
    /// The reshape decodes that finished: the new parts are queued for upload, and swapped in place of the old once uploaded.
    /// </summary>
    void PumpReshapes()
    {
        for (int i = 0; i < _reshaping.Count; i++)
        {
            var m = _reshaping[i];
            if (!m.Reshape!.IsCompleted) continue;
            _reshaping.RemoveAt(i--);
            DecodedObjectMesh? decoded = null;
            try { decoded = m.Reshape.Result; }
            catch (AggregateException e) { Messages.Add($"mesh {m.Key}: {e.InnerException?.Message ?? e.Message}"); }
            if (decoded is null || m.Gpu is not { } old || old.MinLevel == decoded.MinLevel)
            {
                EndReshape(m);
                continue;
            }
            QueueReshape(m, decoded);
        }
    }

    /// <summary>
    /// A reshape is over (swapped, dropped or failed): its decode is forgotten and an upgrade's count taken back.
    /// </summary>
    void EndReshape(ObjectMesh m)
    {
        m.Reshape = null;
        if (m.Upgrade)
        {
            m.Upgrade = false;
            Upgrading--;
        }
    }

    /// <summary>
    /// The mesh named <paramref name="key"/>, made (not loaded) on first ask; <paramref name="distant"/> on its first ask makes it a distant mesh.
    /// </summary>
    public ObjectMesh Get(string key, bool distant = false)
    {
        if (!_meshes.TryGetValue(key, out var m)) _meshes[key] = m = new ObjectMesh { Key = key, Distant = distant };
        return m;
    }

    /// <summary>
    /// Asks for a mesh to be loaded; smaller <paramref name="priority"/> (distance) goes first.
    /// </summary>
    public void Request(ObjectMesh mesh, float priority) => Request(mesh, priority, priority);

    /// <summary>
    /// Asks for a mesh wanted from <paramref name="near"/> (the distance its levels are made for), loaded in the order of <paramref name="order"/>
    /// (smaller first; <c>ObjectStreamer.Rank</c>, which puts a flight's arrival first).
    /// </summary>
    public void Request(ObjectMesh mesh, float near, float order)
    {
        if (mesh.Status != ObjectMesh.State.None) return;
        _wanted[mesh] = _wanted.TryGetValue(mesh, out var old) ? Math.Min(old, order) : order;
        mesh.WantedNear = Math.Min(mesh.WantedNear, near + WantedSlack);
    }

    /// <summary>
    /// What <see cref="ObjectMesh.WantedNear"/> adds to a request's distance: the scan takes 600 off the camera's distance to the placement.
    /// </summary>
    const float WantedSlack = 600;

    /// <summary>
    /// Starts decodes (nearest first) and queues the uploads of finished ones.
    /// </summary>
    public void Pump()
    {
        _frame++;
        for (int i = 0; i < _running.Count; i++)
        {
            var m = _running[i];
            if (!m.Job!.IsCompleted) continue;
            _running.RemoveAt(i--);
            DecodedObjectMesh? decoded = null;
            try { decoded = m.Job.Result; }
            catch (AggregateException e) { Messages.Add($"mesh {m.Key}: {e.InnerException?.Message ?? e.Message}"); }
            m.Job = null;
            if (decoded is null)
            {
                m.Status = ObjectMesh.State.Failed;
                continue;
            }
            m.Status = ObjectMesh.State.Uploading;
            QueueUpload(m, decoded);
        }
        PumpReshapes();
        if (_wanted.Count == 0) return;

        // Memory pressure: only what is wanted within the guard's near reach is decoded, and nothing past its priority ceiling (the scan asks again).
        bool nearOnly = Guard is { Streaming: false };
        if (Guard is { StreamsNear: false })
        {
            _wanted.Clear();
            return;
        }
        var order = _wanted.OrderBy(p => p.Value).Select(p => p.Key).ToList();
        _wanted.Clear();
        foreach (var m in order)
        {
            if (_running.Count >= MaxJobs) break;
            if (m.Status != ObjectMesh.State.None || nearOnly && m.WantedNear - WantedSlack > VramGuard.NearReach) continue;
            m.Status = ObjectMesh.State.Loading;
            string key = m.Key;
            bool distant = m.Distant;
            bool keep = m.KeepLevelIndices;
            float near = Math.Max(m.WantedNear - LookAhead, 0), bias = LodBias;
            m.WantedNear = float.PositiveInfinity;
            m.Job = BackgroundWork.Run(() => Decode(key, distant, 0, keep, near, true, bias));
            _running.Add(m);
        }
    }

    /// <param name="near">How near the mesh is wanted, for the finest LOD level worth holding (<see cref="LevelFor"/>): the camera's distance to the
    /// nearest placement (<paramref name="nearIsPlacement"/>: the mesh's size is not known yet, six radii are taken off) or, else, the nearest LOD
    /// value (before the LOD bias). Infinity: every level.</param>
    DecodedObjectMesh? Decode(string name, bool distant, int depth, bool keep, float near, bool nearIsPlacement, float lodBias)
    {
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        if (path is null)
        {
            lock (Messages) Messages.Add($"mesh not found: {name}");
            return null;
        }
        OgreMesh mesh;
        try
        {
            mesh = OgreMeshReader.ReadFile(path);
        }
        catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException)
        {
            lock (Messages) Messages.Add($"mesh {name}: {e.Message}");
            return null;
        }
        bool weld = MeshWeld.Enabled;
        if (weld) OgreMeshWeld.Apply(mesh);
        var model = Model.Build(mesh, null);
        if (distant) Brighten(model);
        var (centre, radius, top, min, max) = Bounds(mesh, model);
        var levels = MeshLod.Levels(mesh);

        // Generated levels among the file's (docs/render-objects.md, "Generated levels"): a mesh drawn through the normal path, not a distant one,
        // without a skeleton.
        LodCurve? curve = null;
        if (depth == 0 && !distant && !keep && mesh.SkeletonName is null && ObjectLodGen.PlanFor(name + MeshWeld.CacheVariant(weld), path, model,
                radius, levels, PixelsPerRadian, MeshWeld.CacheVariant(weld)) is { } plan)
        {
            levels = ObjectLodGen.Apply(plan, model, levels);
            curve = plan.Curve;
        }
        curve ??= LodCurve.Of([.. levels.Select(l => l.Distance)]);

        // The finest level anything near enough can draw: the levels above it are not decoded or uploaded (the vertices only they use, and their
        // indices and manual meshes, stay out). Only a mesh drawn through the normal path (not the terrain shader's, which wants every level) and
        // not a distant one, and not an inner (manual) mesh, which is drawn at its level 0.
        int minLevel = 0;
        if (depth == 0 && !distant && !keep && float.IsFinite(near))
            minLevel = curve.LevelFor((nearIsPlacement ? near - 6 * radius : near) * lodBias);
        var manual = DecodeManualLevels(levels, minLevel, distant, depth, keep, lodBias);
        if (minLevel >= levels.Count) minLevel = levels.Count - 1;   // the chain ended earlier than the distances said
        var result = new DecodedObjectMesh { Model = model, Levels = levels, Manual = manual, Centre = centre, Radius = radius, Top = top, Min = min,
                Max = max, Curve = curve, MinLevel = minLevel };
        foreach (var part in model.Parts) result.Prepared.Add(PreparePart(result, part, keep));
        foreach (var prepared in result.Prepared) prepared.Packed = GpuVertex.PackObject(prepared.Part.Vertices);
        return result;
    }

    /// <summary>
    /// Multiplies a distant mesh's vertex colours by 1.5 (the alpha kept).
    /// </summary>
    static void Brighten(Model model)
    {
        foreach (var part in model.Parts)
        {
            for (int i = 0; i < part.Vertices.Length; i++)
            {
                var colour = part.Vertices[i].Colour;
                part.Vertices[i].Colour = new Vector4(colour.X * 1.5f, colour.Y * 1.5f, colour.Z * 1.5f, colour.W);
            }
        }
    }

    /// <summary>
    /// The mesh's centre, radius, top and box: from the file's bounds when it has them, else the model's sphere and its vertices' box.
    /// </summary>
    static (Vector3 Centre, float Radius, float Top, Vector3 Min, Vector3 Max) Bounds(OgreMesh mesh, Model model)
    {
        var centre = model.Center;
        float radius = model.Radius, top = centre.Y + radius;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        if (mesh.Bounds is { } b && b.Max.X >= b.Min.X)
        {
            centre = (b.Min + b.Max) / 2;
            radius = Math.Max((b.Max - b.Min).Length() / 2, 1e-3f);
            top = b.Max.Y;
            (min, max) = (b.Min, b.Max);
        }
        else
        {
            foreach (var part in model.Parts)
                foreach (var v in part.Vertices)
                {
                    min = Vector3.Min(min, v.Position);
                    max = Vector3.Max(max, v.Position);
                }
        }
        return (centre, radius, top, min, max);
    }

    /// <summary>
    /// The manual levels' meshes, decoded from the finest level wanted on (an inner mesh only at depth 0); a level whose mesh cannot be had ends
    /// the chain there (the levels from it are removed).
    /// </summary>
    Dictionary<int, DecodedObjectMesh> DecodeManualLevels(List<MeshLodLevel> levels, int minLevel, bool distant, int depth, bool keep,
        float lodBias)
    {
        var manual = new Dictionary<int, DecodedObjectMesh>();
        for (int l = 1; l < levels.Count; l++)
        {
            if (levels[l].ManualMesh is not { } manualName) continue;

            // A manual level shows another mesh file; one that cannot be had ends the chain here.
            DecodedObjectMesh? inner = null;
            if (depth == 0)
            {
                if (l >= minLevel) inner = Decode(manualName, distant, depth + 1, keep, float.PositiveInfinity, false, lodBias);
                // a level above the finest wanted: its file is there, it is not read
                else if (assets.Find(manualName) is not null || assets.Find(Path.GetFileName(manualName.Replace('\\', '/'))) is not null) continue;
            }
            if (inner is null)
            {
                levels.RemoveRange(l, levels.Count - l);
                break;
            }
            manual[l] = inner;
        }
        return manual;
    }

    /// <summary>
    /// The finest LOD level that can be drawn at a LOD value of <paramref name="value"/> (already times the LOD bias): the level
    /// <see cref="MeshLod.Select(ReadOnlySpan{float}, float)"/> picks, or the one above while <see cref="MeshLod.Blend"/> still fades it in
    /// (the band is 6% of the distance; taken as 7%).
    /// </summary>
    public static int LevelFor(ReadOnlySpan<float> distances, float value) => MeshLod.Select(distances, Math.Max(value, 0) / 1.07f);

    /// <summary>
    /// Queues a decoded mesh's upload: its parts and its manual levels' parts in steps, then a step that makes it resident and counts its bytes.
    /// </summary>
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

    /// <summary>
    /// The GPU mesh of a decoded one, its manual levels' too, without parts yet (the uploads add them).
    /// </summary>
    static GpuObjectMesh Prepare(DecodedObjectMesh d) => new()
    {
        Distances = d.Levels.Select(l => l.Distance).ToArray(),
        Curve = d.Curve ?? LodCurve.Of([.. d.Levels.Select(l => l.Distance)]),
        Manual = Enumerable.Range(0, d.Levels.Count).Select(l => d.Manual.TryGetValue(l, out var m) ? Prepare(m) : null).ToArray(),
        Centre = d.Centre,
        Radius = d.Radius,
        Top = d.Top,
        Min = d.Min,
        Max = d.Max,
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
                    if (remap[index] < 0)
                    {
                        remap[index] = kept.Count;
                        kept.Add(part.Vertices[index]);
                    }
            for (int l = 0; l < levelCount; l++)
            {
                if (l < minLevel)
                {
                    arrays[l] = [];
                    continue;
                }
                var source = arrays[l];
                var mapped = new uint[source.Length];
                for (int i = 0; i < mapped.Length; i++) mapped[i] = (uint)remap[source[i]];
                arrays[l] = mapped;
            }
            part = new ModelPart
            {
                SubMeshIndex = part.SubMeshIndex,
                MaterialName = part.MaterialName,
                Vertices = kept.ToArray(),
                Indices = arrays[minLevel],
                HasUv = part.HasUv,
                HasTangents = part.HasTangents,
                HasColours = part.HasColours,
                Skinned = part.Skinned,
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

    /// <summary>
    /// The most bytes one upload step writes (512 KB).
    /// </summary>
    const int SlabBytes = 512 << 10;

    /// <summary>
    /// A part's upload as steps of at most about 512 KB: allocate the buffers, fill them in slabs, set up the vertex array.
    /// </summary>
    void QueuePart(GpuObjectMesh gpu, PreparedPart prepared, string label)
    {
        var part = prepared.Part;
        GpuObjectPart? gp = null;
        int vertexBytes = prepared.Packed.Length, indexBytes = prepared.All.Length * 4;
        uploads.Add(() =>
        {
            gp = new GpuObjectPart
            {
                SubMeshIndex = part.SubMeshIndex,
                MaterialName = part.MaterialName,
                HasTangents = part.HasTangents,
                HasColours = part.HasColours,
                // With ray queries the global illumination's acceleration structures are built from these same buffers (docs/render-gi.md).
                Vertices = DeviceBuffer.Create(gpuContext, (ulong)vertexBytes, BufferUse.Vertex | RayInput, AllocationName),
                Indices = DeviceBuffer.Create(gpuContext, (ulong)indexBytes, BufferUse.Index | RayInput, AllocationName),
                Offset = prepared.Offset,
                Count = prepared.Count,
                LevelIndices = prepared.Levels,
                UvScale = prepared.UvScale,
            };
        }, label + " (allocate)");
        for (int at = 0; at < vertexBytes; at += SlabBytes)
        {
            int start = at, length = Math.Min(SlabBytes, vertexBytes - at);
            uploads.Add(() =>
            {
                gpuContext.EnsureFrame();
                using var batch = gpuContext.Uploads.Begin();
                batch.Write(gp!.Vertices, (ulong)start, prepared.Packed.AsSpan(start, length));
            }, label + " (vertices)");
        }
        for (int at = 0; at < indexBytes; at += SlabBytes)
        {
            int start = at, length = Math.Min(SlabBytes, indexBytes - at);
            uploads.Add(() =>
            {
                gpuContext.EnsureFrame();
                using var batch = gpuContext.Uploads.Begin();
                batch.Write(gp!.Indices, (ulong)start,
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(prepared.All.AsSpan()).Slice(start, length));
            }, label + " (indices)");
        }
        uploads.Add(() =>
        {
            // The per-instance matrix rows (attributes 7 to 10) are bound per segment, from the frame's constants.
            gp!.Attributes = part.Vertices.Length > 0 ? GpuVertex.ObjectAttributes(gp.Vertices) : [];
            gpu.Parts.Add(gp);
            gpu.Bytes += vertexBytes + indexBytes;
        }, label + " (vertex array)");
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
            using (var batch = gpuContext.Uploads.Begin())
                batch.Write(elements, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.AsSpan()));
            part.PlainEbo[level] = elements;
        }
        return part.PlainMesh[level] = MeshBindings.Of(part.Attributes, elements);
    }

    /// <summary>
    /// Meshes unloaded by <see cref="Trim"/> so far.
    /// </summary>
    public int Unloads { get; private set; }

    /// <summary>
    /// Meshes uploaded again after an unload so far.
    /// </summary>
    public int Reloads { get; private set; }

    /// <summary>
    /// Unused for this long: unloaded.
    /// </summary>
    public double IdleSeconds { get; set; } = 60;

    /// <summary>
    /// Above this the least recently used meshes unused for <see cref="PressureIdleSeconds"/> go too, down to three quarters of it.
    /// </summary>
    public double HighWaterMb { get; set; } = 768;

    /// <summary>
    /// Past the high-water mark: meshes unused for this long (seconds) go too, least recently used first.
    /// </summary>
    public double PressureIdleSeconds { get; set; } = 8;

    /// <summary>
    /// The high-water mark as a share of the driver's video memory budget (<see cref="WorldTextureCache.HighWaterShare"/>): 768 MB on the 11.4 GB
    /// card it was tuned on.
    /// </summary>
    public double HighWaterShare { get; set; } = 768 / 11453.0;

    /// <summary>
    /// The high-water mark in effect (MB): <see cref="HighWaterMb"/> or <see cref="HighWaterShare"/> of the budget, scaled for <see cref="Areas"/>
    /// and the guard (<see cref="WorldTextureCache.EffectiveMark"/>).
    /// </summary>
    public double MarkMb => WorldTextureCache.EffectiveMark(HighWaterMb, HighWaterShare, Guard, Areas, MaxAreaShare);

    /// <summary>
    /// Places kept loaded (<see cref="WorldTextureCache.Areas"/>): the mark scales the same way, its share of the budget at most
    /// <see cref="MaxAreaShare"/>.
    /// </summary>
    public int Areas { get; set; } = 1;

    /// <summary>
    /// In proportion to the textures' half: the marks add up, and past the budget the guard's pressure blurs everything.
    /// </summary>
    const double MaxAreaShare = 0.2;

    /// <summary>
    /// The memory-pressure guard (<see cref="VramGuard"/>): under pressure only meshes wanted within <see cref="VramGuard.NearReach"/> are decoded
    /// and meshes idle for <see cref="GuardIdleSeconds"/> go.
    /// </summary>
    public VramGuard? Guard { get; set; }

    /// <summary>
    /// Under the guard's pressure: meshes unused for this long (seconds) go.
    /// </summary>
    public double GuardIdleSeconds { get; set; } = 2;

    /// <summary>
    /// Called with every GPU mesh (manual levels included) that was deleted, so users of it can forget it.
    /// </summary>
    public Action<GpuObjectMesh>? Unloaded { get; set; }

    /// <summary>
    /// When <see cref="Trim"/> last ran (<see cref="Environment.TickCount64"/> ms).
    /// </summary>
    long _lastTrim;

    /// <summary>
    /// Deletes the GPU buffers of meshes not drawn for <see cref="IdleSeconds"/> (earlier, least recently used first, while over the high-water
    /// mark). The mesh goes back to <see cref="ObjectMesh.State.None"/>: whoever wants it again <see cref="Request"/>s it and it is decoded and
    /// uploaded afresh. At most once a second, at most 24 meshes a call.
    /// </summary>
    public void Trim()
    {
        long now = Environment.TickCount64;
        if (now - _lastTrim < 1000) return;
        _lastTrim = now;
        bool guarded = Guard?.Pressure ?? false;
        bool pressure = Bytes > MarkMb * 1048576;
        List<ObjectMesh>? victims = null;
        foreach (var m in _meshes.Values)
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

    /// <summary>
    /// Waits for every running decode (offscreen rendering).
    /// </summary>
    public void WaitForJobs()
    {
        foreach (var m in _running.ToArray()) { try { m.Job!.Wait(); } catch (AggregateException) { } }
        foreach (var m in _reshaping.ToArray()) { try { m.Reshape!.Wait(); } catch (AggregateException) { } }
    }

    /// <summary>
    /// Waits for the running decodes and frees every resident mesh's buffers.
    /// </summary>
    public void Dispose()
    {
        WaitForJobs();
        foreach (var mesh in _meshes.Values)
            if (mesh.Gpu is { } gpu) Delete(gpu);
    }

    /// <summary>
    /// Frees a GPU mesh's buffers and its manual levels', telling <see cref="Unloaded"/> of each first.
    /// </summary>
    void Delete(GpuObjectMesh gpu)
    {
        Unloaded?.Invoke(gpu);
        DeleteBuffers(gpu);
        foreach (var m in gpu.Manual) if (m is not null) Delete(m);
    }

    /// <summary>
    /// The buffers of the mesh's own parts (freed after the frames in flight).
    /// </summary>
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
    /// put in place of the old ones in the same <see cref="GpuObjectMesh"/> (the instances and batches that hold it keep drawing, the old parts until
    /// the swap). The mesh is the same in every other way (bounds, distances), so nothing waits for it.
    /// </summary>
    void QueueReshape(ObjectMesh mesh, DecodedObjectMesh decoded)
    {
        var fresh = Prepare(decoded);
        for (int i = 0; i < decoded.Prepared.Count; i++) QueuePart(fresh, decoded.Prepared[i], $"mesh {mesh.Key} part {i} (reshaped)");
        foreach (var (level, inner) in decoded.Manual)
        {
            var innerGpu = fresh.Manual[level]!;
            for (int i = 0; i < inner.Prepared.Count; i++)
                QueuePart(innerGpu, inner.Prepared[i], $"mesh {mesh.Key} manual {level} part {i} (reshaped)");
        }
        _reshapeQueued++;
        uploads.Add(() =>
        {
            _reshapeQueued--;
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

/// <summary>
/// A part's look: the resolved material and its textures (keys are 0 until decoded and uploaded: <see cref="WorldTexture.Key"/>).
/// </summary>
sealed class ObjectPartMaterial(SurfaceMaterial? material, WorldTexture? diffuse, WorldTexture? normal, WorldTexture? diffuse2, WorldTexture? normal2,
    float texelScale = float.PositiveInfinity, bool triplanar = false)
{
    /// <summary>
    /// The resolved material (null: none found, drawn plain).
    /// </summary>
    public SurfaceMaterial? Material => material;

    /// <summary>
    /// The DUST define: a building part always has it (docs/formats/runtime-materials.md), a map feature when its material says so.
    /// </summary>
    public bool Dust { get; init; }

    /// <summary>
    /// The diffuse map's key (0: none, or not resident yet).
    /// </summary>
    public uint Diffuse => diffuse?.Key ?? 0;

    /// <summary>
    /// The normal map's key (0: none, or not resident yet).
    /// </summary>
    public uint Normal => normal?.Key ?? 0;

    /// <summary>
    /// The second set's diffuse map key (0: none, or not resident yet).
    /// </summary>
    public uint Diffuse2 => diffuse2?.Key ?? 0;

    /// <summary>
    /// The second set's normal map key (0: none, or not resident yet).
    /// </summary>
    public uint Normal2 => normal2?.Key ?? 0;

    /// <summary>
    /// The normal map is stored with X in alpha and Y in green (<see cref="WorldTexture.Swizzled"/>).
    /// </summary>
    public bool Swizzled => normal?.Swizzled ?? false;

    /// <summary>
    /// World length one unit of the texture coordinates the material samples spans, before the instance's scale (the part's
    /// <see cref="MeshTexelScale"/> over the material's tiling; for a triplanar material the world's own mapping): how large a texel is.
    /// Infinity when unknown (the textures keep every mip).
    /// </summary>
    public float TexelScale => texelScale;

    /// <summary>
    /// The coordinates come from the world position, not the mesh: the instance's scale does not enter.
    /// </summary>
    public bool Triplanar => triplanar;

    /// <summary>
    /// The part has a diffuse map (resident or not).
    /// </summary>
    public bool WantsDiffuse => diffuse is not null;

    /// <summary>
    /// The textures' need (<see cref="WorldTexture.Offer"/>) for a user at <paramref name="distance"/> (already over the instance's scale unless
    /// triplanar).
    /// </summary>
    public void Offer(float distance)
    {
        float need = distance / texelScale;
        diffuse?.Offer(need);
        normal?.Offer(need);
        diffuse2?.Offer(need);
        normal2?.Offer(need);
    }

    /// <summary>
    /// As <see cref="Offer"/> for a user that just appeared.
    /// </summary>
    public void OfferNow(float distance)
    {
        float need = distance / texelScale;
        diffuse?.OfferNow(need);
        normal?.OfferNow(need);
        diffuse2?.OfferNow(need);
        normal2?.OfferNow(need);
    }

    /// <summary>
    /// Every texture is resident or known to be missing (a picture drawn now will not change as textures arrive).
    /// </summary>
    public bool TexturesSettled => Settled(diffuse) && Settled(normal) && Settled(diffuse2) && Settled(normal2);

    /// <summary>
    /// Whether a texture is absent, resident or known to be missing.
    /// </summary>
    static bool Settled(WorldTexture? t) => t is null || t.Native is not null || t.State == WorldTexture.Residency.Missing;
}

/// <summary>
/// The materials of a mesh's parts for one placement (parts in the mesh's order).
/// </summary>
sealed class ObjectMaterialSet(ObjectPartMaterial[] parts)
{
    /// <summary>
    /// The materials, one per part of the mesh.
    /// </summary>
    public ObjectPartMaterial[] Parts => parts;

    /// <summary>
    /// The mark pass that last touched the textures (<see cref="Touch"/> once a pass).
    /// </summary>
    public int Stamp;

    /// <summary>
    /// This mark pass's nearest user: its distance, and its distance over its scale (<see cref="ObjectPartMaterial.Triplanar"/> parts take the
    /// first).
    /// </summary>
    public float Near, NearScaled;

    /// <summary>
    /// The mark pass that set <see cref="Near"/> and <see cref="NearScaled"/>.
    /// </summary>
    public int NeedStamp;

    /// <summary>
    /// Reading a texture key counts as using the texture (<see cref="WorldTexture.Key"/>); this keeps all of them from being unloaded.
    /// </summary>
    public void Touch()
    {
        foreach (var p in parts) _ = (p.Diffuse, p.Normal, p.Diffuse2, p.Normal2);
    }
}