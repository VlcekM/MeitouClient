using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Terrain;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// The Metal traced scene (docs/render-gi.md "Metal"): what <see cref="GiScene"/> puts in Vulkan's, in Metal structures. The fine and coarse
/// terrain grids, the objects within <see cref="GiScene.ProbeRange"/> with their parts' textures, emitters and interior bits, and the foliage block,
/// each instance relative to <see cref="Origin"/> under <see cref="TracedSunShaders"/>' masks, its user ID its first <see cref="GeometryRecord"/>
/// (the shaders' custom index). The top level's user-ID instances index this frame's bottom levels.
/// Render thread only.
/// </summary>
internal sealed unsafe class MetalGiScene : IDisposable
{
    #region Scene

    /// <summary>
    /// The most instances and geometry records of a frame (<see cref="GiScene"/>'s).
    /// </summary>
    const int MaxInstances = GiScene.MaxInstances, MaxRecords = GiScene.MaxRecords;

    /// <summary>
    /// The bytes of a user-ID instance descriptor (<c>MTLAccelerationStructureUserIDInstanceDescriptor</c>).
    /// </summary>
    const int InstanceBytes = 68;

    /// <summary>
    /// The origin follows the eye in 64-unit steps once it is this far away; the objects are those round the eye snapped to this grid.
    /// </summary>
    const float OriginSlack = 1024, SelectionSnap = 128;

    /// <summary>
    /// The most bottom levels built in a frame; a structure unused this many updates is dropped.
    /// </summary>
    const int MaxBuildsPerFrame = 32, KeepFrames = 600;

    /// <summary>
    /// The foliage the probes see: at least this radius, within this range.
    /// </summary>
    const float FoliageMinSize = 150, FoliageRange = 4000;

    /// <summary>
    /// The top level is built again this many updates after its last build even when its instances are the same.
    /// </summary>
    const int TopRebuildEvery = 300;

    /// <summary>
    /// <c>MTLAccelerationStructureInstanceOptions</c>: no facing culling, opaque. Metal's default winding already takes a mesh's outward side (and
    /// the terrain grid's top) as the front, as Vulkan does: with its counter-clockwise option every object's outside was a back face to the
    /// probes, dark and counted as inside (the dark patches on a roof), and the grid was culled from above (**Verified**, ray statistics).
    /// </summary>
    const uint NoCulling = 1, Opaque = 4, NotOpaque = 8;

    /// <summary>
    /// The small foliage the traced sun adds: from this radius, at most this many instances (<see cref="GiScene"/>'s).
    /// </summary>
    const float SmallFoliageMinSize = 6;

    /// <summary>
    /// See <see cref="SmallFoliageMinSize"/>.
    /// </summary>
    const int SmallFoliageInstances = 5000;

    /// <summary>
    /// The objects' share of the instances: the rest is the foliage's, small foliage included.
    /// </summary>
    const int ObjectInstances = MaxInstances - FoliageReserve - SmallFoliageInstances - TracedCharacters.MaxCharacters - TracedGrass.MaxInstances;

    /// <summary>
    /// True while the sun is traced: the scene holds the casters too (the objects to <see cref="Range"/>, the small foliage, the cut parts' maps).
    /// </summary>
    public bool Casters { get; set; }

    /// <summary>
    /// True while the characters' gathered light wants the characters in the scene (they are there with <see cref="Casters"/> too).
    /// </summary>
    public bool Characters { get; set; }

    /// <summary>
    /// How far the objects are traced while <see cref="Casters"/> (the shadows' range; at least the probes').
    /// </summary>
    public float Range { get; set; } = GiScene.ProbeRange;

    /// <summary>
    /// The frame's lens: pixels per radian at the render height (small casters count by their size on screen).
    /// </summary>
    public float PixelsPerRadian { get; set; } = 1080 / (2 * MathF.Tan(25 * MathF.PI / 180));

    /// <summary>
    /// The angle a small caster's bounds must span (<see cref="GiScene.CasterPixels"/> pixels at the frame's lens).
    /// </summary>
    float CasterAngle => 3f / (2 * MathF.Max(PixelsPerRadian, 1));

    /// <summary>
    /// The backend.
    /// </summary>
    readonly MetalContext _ctx;

    /// <summary>
    /// This frame's builds.
    /// </summary>
    readonly MetalBuilds _builds;

    /// <summary>
    /// The fine grid (512² quads 8 apart along the smooth ground) and the coarse one (256² quads 160 apart), as Vulkan's.
    /// </summary>
    readonly MetalTerrainGrid[] _terrain;

    /// <summary>
    /// The objects' and the foliage's bottom levels by mesh.
    /// </summary>
    readonly Dictionary<object, MetalBottomLevel> _meshes = [];

    /// <summary>
    /// Per frame slot: the instance descriptors, the records, the top level and its scratch, what it was built over.
    /// </summary>
    readonly Slot[] _slots;

    /// <summary>
    /// This frame's instance descriptors and records.
    /// </summary>
    readonly List<Instance> _instances = [];

    /// <summary>
    /// See <see cref="_instances"/>.
    /// </summary>
    readonly List<GeometryRecord> _records = [];

    /// <summary>
    /// What this frame's instances reach by address, once each.
    /// </summary>
    readonly HashSet<nint> _resident = [];

    /// <summary>
    /// This frame's bottom levels in the order the instances index them, and each one's index.
    /// </summary>
    readonly List<MTLAccelerationStructure> _bottoms = [];

    /// <summary>
    /// See <see cref="_bottoms"/>.
    /// </summary>
    readonly Dictionary<nint, uint> _bottomIndex = [];

    /// <summary>
    /// The update count.
    /// </summary>
    long _frame;

    /// <summary>
    /// Whether <see cref="Origin"/> was placed.
    /// </summary>
    bool _originSet;

    /// <summary>
    /// The traced scene's origin: instances are placed relative to it (the shaders trace from <c>world - origin</c>).
    /// </summary>
    public Vector3 Origin { get; private set; }

    /// <summary>
    /// This frame's top level (null before the first build).
    /// </summary>
    public MTLAccelerationStructure? Top { get; private set; }

    /// <summary>
    /// This frame's geometry records and their count.
    /// </summary>
    public MTLBuffer Records => _slots[_ctx.Frame.Slot].Records;

    /// <summary>
    /// What a trace of this frame reaches by address (bottom levels, the records' vertex and index buffers).
    /// </summary>
    public nint[] Resident { get; private set; } = [];

    /// <summary>
    /// The instances in this frame's top level.
    /// </summary>
    public int InstanceCount { get; private set; }

    /// <summary>
    /// The foliage instances in this frame's top level.
    /// </summary>
    public int FoliageInstances { get; private set; }

    /// <summary>
    /// Makes the terrain grids and the slots' buffers and top levels (sized for <see cref="MaxInstances"/>).
    /// </summary>
    public MetalGiScene(MetalContext ctx)
    {
        _ctx = ctx;
        _builds = new MetalBuilds(ctx);
        _terrain = [new MetalTerrainGrid(ctx, "metal gi terrain fine", 512, 8), new MetalTerrainGrid(ctx, "metal gi terrain coarse", 256, 160)];
        _slots = new Slot[MetalContext.FrameRingDepth];
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new Slot(ctx);
    }

    /// <summary>
    /// This frame's scene for <paramref name="eye"/>: the terrain grids, the objects and foliage near it (new bottom levels within the frame's budget),
    /// and the slot's top level when its instances changed.
    /// </summary>
    public void Update(Vector3 eye, HeightSnapshot heights, WorldObjectRenderer? objects, FoliageRenderer? foliage, WorldLamps? lamps,
        Characters.CharacterRenderer? characters)
    {
        _frame++;
        _builds.Begin();
        MoveOrigin(eye);
        _instances.Clear();
        _records.Clear();
        _resident.Clear();
        _bottoms.Clear();
        _bottomIndex.Clear();

        AddTerrain(eye, heights);
        if (objects is not null) AddObjects(eye, objects, lamps);
        if (foliage is not null) AddFoliageBlock(eye, foliage);
        UpdateGrass(eye, foliage);
        UpdateCharacters(eye, characters);
        Evict();

        var slot = _slots[_ctx.Frame.Slot];
        CollectionsMarshal.AsSpan(_records).CopyTo(new Span<GeometryRecord>((void*)slot.Records.Contents, MaxRecords));
        BuildTop(slot);
        _builds.End();

        Top = slot.Top;
        Resident = [.. _resident];
        InstanceCount = _instances.Count;
    }

    /// <summary>
    /// The origin follows the eye in 64-unit steps, once it is <see cref="OriginSlack"/> away (the instances then stay the same while the eye walks).
    /// </summary>
    void MoveOrigin(Vector3 eye)
    {
        var d = Vector3.Abs(eye - Origin);
        if (_originSet && MathF.Max(d.X, MathF.Max(d.Y, d.Z)) <= OriginSlack) return;

        Origin = new Vector3(MathF.Round(eye.X / 64) * 64, MathF.Round(eye.Y / 64) * 64, MathF.Round(eye.Z / 64) * 64);
        _originSet = true;
    }

    /// <summary>
    /// The terrain grids' instances, each grid updated first: culled from below (a ray from under the ground passes up through it).
    /// </summary>
    void AddTerrain(Vector3 eye, HeightSnapshot heights)
    {
        foreach (var grid in _terrain)
        {
            grid.Update(_builds, eye, heights);
            if (grid.Bottom is not { } bottom) continue;

            var mask = grid.Fine ? TracedSunShaders.TerrainFine : TracedSunShaders.TerrainCoarse;
            Add(bottom, Matrix4x4.CreateTranslation(grid.Centre - Origin), mask, Opaque);
            _records.Add(bottom.Records[0]);
        }
    }

    /// <summary>
    /// The objects within <see cref="GiScene.ProbeRange"/> of the eye snapped to <see cref="SelectionSnap"/>, while instances and records last.
    /// </summary>
    void AddObjects(Vector3 eye, WorldObjectRenderer objects, WorldLamps? lamps)
    {
        var snapped = new Vector3(MathF.Round(eye.X / SelectionSnap), MathF.Round(eye.Y / SelectionSnap), MathF.Round(eye.Z / SelectionSnap))
            * SelectionSnap;

        float range = Casters ? MathF.Max(Range, GiScene.ProbeRange) : GiScene.ProbeRange;
        foreach (var inst in objects.RayInstances(snapped, range + SelectionSnap * 0.87f))
        {
            if (_instances.Count >= ObjectInstances) break;

            var entry = ObjectEntry(inst.Gpu!);
            if (entry is null) continue;
            if (_records.Count + entry.Records.Length > MaxRecords) break;

            entry.LastUsed = _frame;
            AddObject(inst, entry, objects, lamps);
        }
    }

    /// <summary>
    /// The instances kept for the foliage block.
    /// </summary>
    const int FoliageReserve = 14336;

    /// <summary>
    /// An object's instance and its parts' records with this frame's textures, emitter and interior bits; a part cut by its map makes the instance
    /// report candidates.
    /// </summary>
    void AddObject(ObjectStreamer.Instance inst, MetalBottomLevel entry, WorldObjectRenderer objects, WorldLamps? lamps)
    {
        var world = inst.Transform;
        world.Translation -= Origin;
        uint first = (uint)_records.Count;
        uint interior = (objects.RayInterior(inst) ? 1u : 0u) | (objects.RayHidden(inst) ? 2u : 0u);
        uint emitter = lamps?.Emitter(inst.Placed.PlacementId) ?? 0;
        bool cut = false;

        for (int k = 0; k < entry.Records.Length; k++)
        {
            var record = entry.Records[k];
            record.Texture = objects.RayTexture(inst.Materials!, entry.Parts[k]);
            record.Emitter = emitter;
            record.Interior = interior;
            if (Casters) (record.SunMap, record.SunCut) = objects.RayCut(inst.Materials!, entry.Parts[k]);
            cut |= record.SunCut > 0;
            _records.Add(record);
        }

        // A part its material cuts makes the instance report candidates, whose records hold the map for the sun.
        Add(entry, world, TracedSunShaders.Objects, cut ? NoCulling | NotOpaque : NoCulling, first);
    }

    /// <summary>
    /// The mesh's bottom level, built now when the frame's budget allows (null: not yet, or nothing to trace).
    /// </summary>
    MetalBottomLevel? ObjectEntry(GpuObjectMesh gpu)
    {
        int level = Math.Clamp(gpu.MinLevel, 0, Math.Max(gpu.LevelCount - 1, 0));
        var source = gpu.Manual.Length > level && gpu.Manual[level] is { } manual ? manual : gpu;
        int sourceLevel = ReferenceEquals(source, gpu) ? level : 0;

        if (_meshes.TryGetValue(gpu, out var entry))
        {
            if (BuiltFrom(entry, source)) return entry;

            Drop(gpu, entry);
        }

        if (_builds.Count >= MaxBuildsPerFrame) return null;

        return BuildObject(gpu, source, sourceLevel);
    }

    /// <summary>
    /// A mesh's bottom level from <paramref name="source"/>'s parts at <paramref name="level"/>, a record per part.
    /// </summary>
    MetalBottomLevel? BuildObject(GpuObjectMesh gpu, GpuObjectMesh source, int level)
    {
        var geometries = new List<MTLAccelerationStructureTriangleGeometryDescriptor>();
        var records = new List<GeometryRecord>();
        var parts = new List<int>();
        var resident = new List<nint>();

        for (int p = 0; p < source.Parts.Count; p++)
        {
            var part = source.Parts[p];
            if (part.Count.Length <= level || part.Count[level] < 3) continue;

            var vertices = _ctx.BufferAt(part.Vertices.Handle);
            var indices = _ctx.BufferAt(part.Indices.Handle);
            ulong offset = (ulong)part.Offset[level] * 4;
            geometries.Add(Geometry(vertices, (ulong)part.Stride, indices, offset, part.Count[level] / 3, opaque: true));
            records.Add(new GeometryRecord
            {
                Vertices = vertices.GpuAddress, Indices = indices.GpuAddress + offset, Stride = (uint)part.Stride, Kind = GeometryRecord.Object,
            });
            parts.Add(p);
            resident.Add(vertices.NativePtr);
            resident.Add(indices.NativePtr);
        }

        var sources = source.Parts.Select(p => (object)p.Vertices).Prepend(source).ToArray();
        return Keep(gpu, "metal gi object", geometries, records, parts, resident, sources);
    }

    /// <summary>
    /// Builds <paramref name="geometries"/> into a bottom level kept under <paramref name="key"/>; null when there are none.
    /// </summary>
    MetalBottomLevel? Keep(object key, string name, List<MTLAccelerationStructureTriangleGeometryDescriptor> geometries, List<GeometryRecord> records,
        List<int> parts, List<nint> resident, object[] sources)
    {
        if (geometries.Count == 0) return null;

        var structure = _builds.Bottom(name, geometries);
        resident.Insert(0, structure.NativePtr);
        var entry = new MetalBottomLevel(structure, [.. records], [.. parts], [.. resident.Distinct()], []) { Sources = sources, LastUsed = _frame };
        _meshes[key] = entry;
        return entry;
    }

    /// <summary>
    /// A triangle geometry: positions at the start of each vertex of <paramref name="vertices"/>, 32-bit indices from <paramref name="indexOffset"/>.
    /// </summary>
    static MTLAccelerationStructureTriangleGeometryDescriptor Geometry(MTLBuffer vertices, ulong stride, MTLBuffer indices, ulong indexOffset,
        int triangles, bool opaque) => new()
    {
        VertexBuffer = vertices,
        VertexStride = stride,
        VertexFormat = MTLAttributeFormat.Float3,
        IndexBuffer = indices,
        IndexBufferOffset = indexOffset,
        IndexType = MTLIndexType.UInt32,
        TriangleCount = (ulong)triangles,
        Opaque = opaque,
    };

    /// <summary>
    /// Whether <paramref name="entry"/> was built from <paramref name="source"/> and its current parts' vertex buffers.
    /// </summary>
    static bool BuiltFrom(MetalBottomLevel entry, GpuObjectMesh source)
    {
        var s = entry.Sources;
        if (s.Length != source.Parts.Count + 1 || !ReferenceEquals(s[0], source)) return false;

        for (int i = 0; i < source.Parts.Count; i++)
            if (!ReferenceEquals(s[i + 1], source.Parts[i].Vertices)) return false;

        return true;
    }

    /// <summary>
    /// An instance of <paramref name="bottom"/> placed by <paramref name="world"/> (relative to the origin), its first record
    /// <paramref name="firstRecord"/> (the current count when omitted), and what it reaches made resident.
    /// </summary>
    void Add(MetalBottomLevel bottom, in Matrix4x4 world, byte mask, uint options, uint? firstRecord = null)
    {
        if (!_bottomIndex.TryGetValue(bottom.Structure.NativePtr, out uint index))
        {
            index = (uint)_bottoms.Count;
            _bottomIndex[bottom.Structure.NativePtr] = index;
            _bottoms.Add(bottom.Structure);
        }

        _instances.Add(Instance.Of(world, options, mask, firstRecord ?? (uint)_records.Count, index));
        foreach (var r in bottom.Resident) _resident.Add(r);
    }

    #endregion

    #region Foliage block

    /// <summary>
    /// The foliage block: rebuilt now and then (the foliage does not move), not every frame (the walk over every zone is not cheap).
    /// </summary>
    readonly List<(MetalBottomLevel Entry, Matrix4x4 World, FoliageRayMesh Mesh, byte Mask)> _foliage = [];

    /// <summary>
    /// The candidates of a rebuild.
    /// </summary>
    readonly List<(FoliageRayMesh Mesh, Matrix4x4 Transform, float Distance)> _rayFoliage = [];

    /// <summary>
    /// The eye, update and foliage generation the block was gathered at, and whether every mesh had its structure.
    /// </summary>
    (Vector3 Eye, long Frame, int Generation, bool Complete) _foliageState = (default, long.MinValue / 2, -1, false);

    /// <summary>
    /// Whether the block was gathered with <see cref="Casters"/>, and the caster angle its small foliage was chosen with.
    /// </summary>
    (bool Casters, float Angle) _foliageCasters;

    /// <summary>
    /// The foliage block's instances, with this frame's textures in their records (rebuilt when stale: every 120 updates, when the eye moved 250
    /// units, when the foliage changed, soon after a block that lacked meshes).
    /// </summary>
    void AddFoliageBlock(Vector3 eye, FoliageRenderer foliage)
    {
        var s = _foliageState;
        bool stale = _frame - s.Frame >= 120 || Vector3.DistanceSquared(eye, s.Eye) > 250f * 250f || foliage.RayGeneration != s.Generation
            || !s.Complete && _frame - s.Frame >= 2 || _foliageCasters.Casters != Casters
            || Casters && MathF.Abs(CasterAngle - _foliageCasters.Angle) > 0.15f * _foliageCasters.Angle;
        if (stale) RebuildFoliage(eye, foliage);

        int count = 0;
        foreach (var (entry, world, mesh, mask) in _foliage)
        {
            if (_instances.Count >= MaxInstances || _records.Count + entry.Records.Length > MaxRecords) break;

            entry.LastUsed = _frame;
            AddFoliage(entry, world, mesh, mask, foliage);
            count++;
        }

        FoliageInstances = count;
    }

    /// <summary>
    /// One foliage instance and its records with this frame's textures (a texture may unload or move in the table between rebuilds).
    /// </summary>
    void AddFoliage(MetalBottomLevel entry, Matrix4x4 world, FoliageRayMesh mesh, byte mask, FoliageRenderer foliage)
    {
        uint first = (uint)_records.Count;
        foliage.RefreshRayTextures(mesh);

        for (int k = 0; k < entry.Records.Length; k++)
        {
            var record = entry.Records[k];
            (record.Texture, record.AlphaTexture) = mesh.Textures[entry.Parts[k]];
            _records.Add(record);
        }

        world.Translation -= Origin;
        Add(entry, world, mask, NoCulling, first);
    }

    /// <summary>
    /// Trees, bushes and rocks big enough to matter for the probes, nearest first while the budget lasts.
    /// </summary>
    void RebuildFoliage(Vector3 eye, FoliageRenderer foliage)
    {
        _foliage.Clear();
        _rayFoliage.Clear();

        // The traced sun's trees reach as far as its shadows, nearest first while the budget lasts; the probes' rays are shorter than either.
        float range = Casters ? MathF.Max(FoliageRange, Range) : FoliageRange;
        foliage.RayInstances(eye, range, FoliageMinSize, _rayFoliage);
        _rayFoliage.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        bool complete = GatherFoliage(TracedSunShaders.Foliage, FoliageReserve);

        // The foliage the probes leave out, for the sun alone (a bush casts a shadow as a tree does): the largest on screen first.
        if (Casters)
        {
            _rayFoliage.Clear();
            foliage.RayInstances(eye, MathF.Max(Range, FoliageRange), SmallFoliageMinSize, _rayFoliage, FoliageMinSize, CasterAngle);
            _rayFoliage.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            complete &= GatherFoliage(TracedSunShaders.SmallFoliage, FoliageReserve + SmallFoliageInstances);
        }

        _foliageState = (eye, _frame, foliage.RayGeneration, complete);
        _foliageCasters = (Casters, CasterAngle);
    }

    /// <summary>
    /// The candidates of <see cref="_rayFoliage"/> into the block under <paramref name="mask"/> while it holds fewer than <paramref name="room"/>;
    /// false when a mesh's structure was not built yet.
    /// </summary>
    bool GatherFoliage(byte mask, int room)
    {
        bool complete = true;

        foreach (var (mesh, transform, _) in _rayFoliage)
        {
            if (_foliage.Count >= room) break;

            var entry = FoliageEntry(mesh);
            if (entry is null)
            {
                complete = false;
                continue;
            }

            var world = transform;
            world.M14 = 0;
            _foliage.Add((entry, world, mesh, mask));
        }

        return complete;
    }

    /// <summary>
    /// A foliage mesh's bottom level (its key is the resident mesh, which a reload replaces); cut-out parts (leaves) are not opaque: the trace tests
    /// their alpha at each candidate hit.
    /// </summary>
    MetalBottomLevel? FoliageEntry(FoliageRayMesh mesh)
    {
        if (_meshes.TryGetValue(mesh.Key, out var entry)) return entry;
        if (_builds.Count >= MaxBuildsPerFrame) return null;

        var geometries = new List<MTLAccelerationStructureTriangleGeometryDescriptor>();
        var records = new List<GeometryRecord>();
        var parts = new List<int>();
        var resident = new List<nint>();

        for (int p = 0; p < mesh.Parts.Length; p++)
        {
            var part = mesh.Parts[p];
            if (part.IndexCount < 3) continue;

            var vertices = _ctx.BufferAt(part.Vertices.Handle);
            var indices = _ctx.BufferAt(part.Indices.Handle);
            geometries.Add(Geometry(vertices, GpuVertex.ObjectSize, indices, 0, part.IndexCount / 3, part.AlphaThreshold <= 0));
            records.Add(new GeometryRecord
            {
                Vertices = vertices.GpuAddress, Indices = indices.GpuAddress, Stride = GpuVertex.ObjectSize, AlphaThreshold = part.AlphaThreshold,
                Kind = mesh.Rock ? GeometryRecord.Terrain : GeometryRecord.Foliage,
            });
            parts.Add(p);
            resident.Add(vertices.NativePtr);
            resident.Add(indices.NativePtr);
        }

        return Keep(mesh.Key, "metal gi foliage", geometries, records, parts, resident, [mesh.Key]);
    }

    #endregion

    #region Grass

    /// <summary>
    /// How far from the eye the grass is traced (<see cref="TracedGrass"/>): where the traced sun's share ends, set before <see cref="Update"/>.
    /// </summary>
    public float GrassReach { get; set; } = TracedGrass.MinRange;

    /// <summary>
    /// Whether the traced sun sees grass at all (<c>PostOptions.TracedGrass</c>; with the casters).
    /// </summary>
    public bool GrassShadows { get; set; }

    /// <summary>
    /// The drawn interior masks' boxes: the grass in them casts nothing.
    /// </summary>
    public IReadOnlyList<InteriorMaskCut> InteriorCuts { get; set; } = [];

    /// <summary>
    /// The traced sun's grass (made the first frame that traces it).
    /// </summary>
    MetalTracedGrass? _grass;

    /// <summary>
    /// The mask kernel's grass constants (<see cref="TracedGrass.RayParams"/>; zero without traced grass).
    /// </summary>
    public Vector4 GrassRayParams => Casters && GrassShadows && _grass is not null ? _grass.RayParams : default;

    /// <summary>
    /// The grass near the eye while the scene holds the casters and the grass's shadows are on: built and swayed, its instances after the foliage
    /// (cut-outs); dropped otherwise.
    /// </summary>
    void UpdateGrass(Vector3 eye, FoliageRenderer? foliage)
    {
        if (!Casters || !GrassShadows || foliage is null)
        {
            _grass?.Release();
            return;
        }

        _grass ??= new MetalTracedGrass(_ctx);
        _grass.Update(_builds, foliage, eye, InteriorCuts, GrassReach);
        _grass.Emit((level, world, mask, options) => Add(level, world, mask, options), _records, Origin, MaxInstances, MaxRecords,
            () => _instances.Count, NoCulling | NotOpaque);
    }

    #endregion

    #region Characters

    /// <summary>
    /// The characters the traced sun, the lamps and the characters' gather see (made the first time they are wanted).
    /// </summary>
    MetalTracedCharacters? _people;

    /// <summary>
    /// How many characters the scene holds this frame.
    /// </summary>
    public int CharactersShown => _people?.Shown ?? 0;

    /// <summary>
    /// The characters while the scene holds the casters or <see cref="Characters"/>: skinned, their structures refitted, their instances last;
    /// dropped otherwise.
    /// </summary>
    void UpdateCharacters(Vector3 eye, Characters.CharacterRenderer? characters)
    {
        if (!(Casters || Characters) || characters is null)
        {
            _people?.Release();
            return;
        }

        _people ??= new MetalTracedCharacters(_ctx);
        _people.Update(_builds, characters, eye, Range, CasterAngle);
        _people.Emit((level, world, mask, options) => Add(level, world, mask, options), _records, Origin, MaxInstances, MaxRecords,
            () => _instances.Count);
    }

    #endregion

    #region Top level

    /// <summary>
    /// The slot's top level over this frame's instances, built again only when they differ from its last build's (or every
    /// <see cref="TopRebuildEvery"/> updates).
    /// </summary>
    void BuildTop(Slot slot)
    {
        var bytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_instances));
        bool same = slot.Built is { } built && built.Length == bytes.Length && bytes.SequenceEqual(built) && _frame - slot.BuiltFrame < TopRebuildEvery
            && !(_people?.Moved ?? false) && !(_grass?.Moved ?? false);
        if (same) return;

        bytes.CopyTo(new Span<byte>((void*)slot.Instances.Contents, MaxInstances * InstanceBytes));
        var descriptor = new MTLInstanceAccelerationStructureDescriptor
        {
            InstanceCount = (ulong)_instances.Count,
            InstanceDescriptorBuffer = slot.Instances,
            InstanceDescriptorStride = InstanceBytes,
            InstanceDescriptorType = MTLAccelerationStructureInstanceDescriptorType.UserID,
            InstancedAccelerationStructures = MetalBuilds.ArrayOf(_bottoms.Select(b => b.NativePtr)),
            InstanceTransformationMatrixLayout = MTLMatrixLayout.ColumnMajor,
            Usage = MTLAccelerationStructureUsage.PreferFastIntersection,
        };
        _builds.Top(slot.Top, descriptor, slot.Scratch);
        slot.Built = bytes.ToArray();
        slot.BuiltFrame = _frame;
    }

    /// <summary>
    /// A user-ID instance descriptor (<c>MTLAccelerationStructureUserIDInstanceDescriptor</c>, 68 bytes): the transform as four columns of three
    /// floats, the options, the mask, the intersection function offset, the bottom level's index and the user ID.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct Instance
    {
        /// <summary>
        /// The transform's columns.
        /// </summary>
        public fixed float Transform[12];

        /// <summary>
        /// The options, the mask, the intersection function table offset, the bottom level's index and the user ID (the instance's first record).
        /// </summary>
        public uint Options, Mask, FunctionOffset, Structure, UserId;

        /// <summary>
        /// An instance of <paramref name="structure"/> placed by <paramref name="m"/> (a row-vector matrix: its rows are Metal's columns).
        /// </summary>
        public static Instance Of(in Matrix4x4 m, uint options, byte mask, uint userId, uint structure)
        {
            var i = new Instance { Options = options, Mask = mask, UserId = userId, Structure = structure };
            ReadOnlySpan<float> t = [m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43];
            for (int k = 0; k < 12; k++) i.Transform[k] = t[k];

            return i;
        }
    }

    /// <summary>
    /// A frame slot's buffers and top level.
    /// </summary>
    sealed class Slot : IDisposable
    {
        /// <summary>
        /// The instance descriptors and the records (shared memory, written by the CPU for this slot's frame).
        /// </summary>
        public readonly MTLBuffer Instances, Records;

        /// <summary>
        /// The top level (sized for every instance) and its build scratch.
        /// </summary>
        public readonly MTLAccelerationStructure Top;

        /// <summary>
        /// See <see cref="Top"/>.
        /// </summary>
        public readonly MTLBuffer Scratch;

        /// <summary>
        /// The instances the top level was last built over, and the update.
        /// </summary>
        public byte[]? Built;

        /// <summary>
        /// See <see cref="Built"/>.
        /// </summary>
        public long BuiltFrame;

        /// <summary>
        /// Makes the buffers and a top level sized for <see cref="MaxInstances"/>.
        /// </summary>
        public Slot(MetalContext ctx)
        {
            Instances = ctx.Device.NewBuffer((ulong)(MaxInstances * InstanceBytes), MTLResourceOptions.ResourceStorageModeShared);
            Records = ctx.Device.NewBuffer((ulong)(MaxRecords * GeometryRecord.Size), MTLResourceOptions.ResourceStorageModeShared);
            Instances.Label = (NSString)"metal gi instances";
            Records.Label = (NSString)"metal gi records";
            var sizing = new MTLInstanceAccelerationStructureDescriptor
            {
                InstanceCount = MaxInstances,
                InstanceDescriptorBuffer = Instances,
                InstanceDescriptorStride = InstanceBytes,
                InstanceDescriptorType = MTLAccelerationStructureInstanceDescriptorType.UserID,
                Usage = MTLAccelerationStructureUsage.PreferFastIntersection,
            };
            var sizes = ctx.Device.AccelerationStructureSizes(sizing);
            Top = ctx.Device.NewAccelerationStructure(sizes.accelerationStructureSize);
            Top.Label = (NSString)"metal gi top";
            Scratch = ctx.Device.NewBuffer(Math.Max(sizes.buildScratchBufferSize, 1), MTLResourceOptions.ResourceStorageModePrivate);
        }

        /// <summary>
        /// Releases the buffers and the top level.
        /// </summary>
        public void Dispose()
        {
            Instances.Dispose();
            Records.Dispose();
            Top.Dispose();
            Scratch.Dispose();
        }
    }

    #endregion

    #region Eviction

    /// <summary>
    /// Drops the structures not in the scene for <see cref="KeepFrames"/> updates (after the frames in flight).
    /// </summary>
    void Evict()
    {
        var stale = _meshes.Where(e => _frame - e.Value.LastUsed > KeepFrames).Select(e => e.Key).ToList();
        foreach (var key in stale) Drop(key, _meshes[key]);
    }

    /// <summary>
    /// Forgets <paramref name="entry"/> and frees it after the frames in flight.
    /// </summary>
    void Drop(object key, MetalBottomLevel entry)
    {
        _meshes.Remove(key);
        _ctx.DeferDelete(entry.Dispose);
    }

    /// <summary>
    /// A line describing the scene, for the log.
    /// </summary>
    public string Describe() =>
        $"{InstanceCount} instances ({FoliageInstances} foliage), {_meshes.Count} structures ({_builds.Bytes / (1024 * 1024)} MB built), "
        + $"terrain {string.Join(" + ", _terrain.Select(t => t.Describe()))}{(_people is { } p ? ", " + p.Describe() : "")}"
        + (_grass is { } g ? ", " + g.Describe() : "");

    /// <summary>
    /// Frees the grids, the structures and the slots.
    /// </summary>
    public void Dispose()
    {
        foreach (var t in _terrain) t.Dispose();
        _people?.Dispose();
        _grass?.Dispose();
        foreach (var e in _meshes.Values) e.Dispose();
        _meshes.Clear();
        foreach (var s in _slots) s.Dispose();
    }

    #endregion
}
