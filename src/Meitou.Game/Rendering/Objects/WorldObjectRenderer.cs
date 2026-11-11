using System.Diagnostics;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;

using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using SamplerInfo = Meitou.Game.Rendering.Gpu.Shaders.SamplerInfo;
using ScalarKind = Meitou.Game.Rendering.Gpu.Shaders.ScalarKind;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// Draws the world's placed objects (docs/render-objects.md): buildings, map features and distant towns.
/// <list type="bullet">
/// <item>Zones around the camera are laid out on worker threads (<see cref="ObjectStreamer"/>); meshes decode on workers and upload in
/// steps (<see cref="ObjectMeshCache"/>); textures decode on workers (<see cref="WorldTextureCache"/>). <see cref="Update"/> drives it all.</item>
/// <item>Each instance picks a mesh LOD level by Kenshi's rule (<see cref="MeshLod"/>) and fades between levels and at the draw
/// distance with a dithered cross-fade; instances of one (mesh, material set, level) are drawn with one instanced call per part.</item>
/// <item>Towns with a baked distant mesh, and buildings' own distant meshes elsewhere, take over beyond <see cref="ObjectDistance"/>.</item>
/// <item>TERRAIN-mode map features go through the terrain shader (<see cref="TerrainRenderer.DrawMeshes"/>), one draw each, at their LOD
/// level.</item>
/// </list>
/// <see cref="Draw"/> can be called with any camera (e.g. the water reflection's) between <see cref="Update"/> calls.
/// </summary>
public sealed unsafe partial class WorldObjectRenderer : IDisposable
{
    #region Setup

    /// <summary>
    /// The frame and view constants the three native object programs share; <see cref="RecordDraws"/> prepares a segment's binding from it.
    /// </summary>
    readonly NativeFrame _nativeFrame;

    /// <summary>
    /// The install's asset locator: the caches and the material resolver load through it, and <see cref="MeshBounds"/> finds mesh files with it.
    /// </summary>
    readonly AssetLocator _assets;

    /// <summary>
    /// The colour program, the colour program without a discard (<see cref="EarlyDepth"/>: fully visible batches) and the depth (shadow caster)
    /// program, all native.
    /// </summary>
    readonly ObjProg _colourProg, _solidProg, _depthProg;

    /// <summary>
    /// The objects' texture cache (decoded on workers, mip-streamed; <see cref="Textures"/>).
    /// </summary>
    readonly WorldTextureCache _textureCache;

    /// <summary>
    /// Finds the textures of a mesh's parts (<see cref="Resolver"/>).
    /// </summary>
    readonly MaterialResolver _resolver;

    /// <summary>
    /// The mesh cache's upload steps, run within the frame's budget at the end of each <see cref="Update(Vector3)"/>.
    /// </summary>
    readonly UploadQueue _uploads = new();

    /// <summary>
    /// The meshes: decoded on workers, uploaded in steps through <see cref="_uploads"/>, unloaded after their idle time.
    /// </summary>
    readonly ObjectMeshCache _meshes;

    /// <summary>
    /// Lays out the zones around the camera on worker threads, and keeps the landmarks and the loose instances (ghosts, farm plants).
    /// </summary>
    readonly ObjectStreamer _streamer;

    /// <summary>
    /// The world's placements: level data, runtime buildings, distant towns and the buildings' floors.
    /// </summary>
    readonly WorldObjects _objects;

    /// <summary>
    /// The distant towns (one per <see cref="WorldObjects.DistantTowns"/> entry) with their baked meshes.
    /// </summary>
    readonly List<TownDraw> _towns = [];

    /// <summary>
    /// The one material of distant towns and stand-ins: <see cref="DistantTowns.DiffuseTexture"/> times the vertex colour.
    /// </summary>
    readonly ObjectMaterialSet _distantMaterial;

    /// <summary>
    /// MEITOU_LOD_DEBUG: 1 colours solid surfaces by LOD level, 2 draws wireframe only coloured by level (green 0, yellow 1, orange 2, red 3, magenta
    /// manual, blue distant stand-ins).
    /// </summary>
    readonly int _debugLevels = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_LOD_DEBUG"), out int dl) ? dl : 0;

    /// <summary>
    /// The native GPU API (docs/renderer-native.md 7.1 step 8; since phase 8 stage 1 the only one this renderer uses).
    /// </summary>
    public IGpuContext Gpu { get; }

    /// <summary>
    /// Makes the native programs, the caches and the streamer, starts the landmark collection on a worker when asked, and lists the distant towns.
    /// </summary>
    /// <param name="landmarks">Collect the world's landmarks (<see cref="LandmarkClass"/>) and keep them apart from the zones, to be drawn to
    /// <see cref="LandmarkDistance"/>. Without it (Faithful) every placement stays in its zone and the object distance rules all.</param>
    internal WorldObjectRenderer(IGpuContext gpu, AssetLocator assets, WorldObjects objects, bool landmarks = false)
    {
        Gpu = gpu;
        this._assets = assets;
        this._objects = objects;

        // Both native programs are made here with every handle resolved, never inside a draw (the GL programs they replace are not made).
        _nativeFrame = new NativeFrame(gpu);
        _colourProg = new ObjProg(gpu, _nativeFrame, BuildingLodShaders.VertexNative(), BuildingLodShaders.FragmentNative(), "objects");
        _solidProg = new ObjProg(gpu, _nativeFrame, BuildingLodShaders.VertexNative(),
            BuildingLodShaders.FragmentNative(solid: true), "objects solid");
        _depthProg = new ObjProg(gpu, _nativeFrame, BuildingLodShaders.VertexNative(), BuildingLodShaders.DepthNative(), "objects depth");
        _textureCache = new WorldTextureCache(gpu, assets, "object textures") { Share = TextureShare.For(gpu) };
        var library = assets.Materials;
        _resolver = new MaterialResolver(objects.Database, library, assets);
        _meshes = new ObjectMeshCache(gpu, assets, _uploads);
        _meshes.Unloaded = gpu =>
        {
            _unloadedMeshes.Add(gpu);
            RemoveBatches(gpu);
        };
        _streamer = new ObjectStreamer(objects, _meshes);
        Doors = new DoorPoses(this);
        Joints = new JointPoses(Rotors);
        Farms = new FarmPlants(_streamer);
        if (landmarks)
        {
            _landmarkClass = new LandmarkClass(assets);
            _streamer.WaitForLandmarks = true;
            var collectWatch = Stopwatch.StartNew();
            _landmarkTask = Task.Run(() =>
            {
                var found = _landmarkClass.Collect(objects);
                _landmarkMs = collectWatch.Elapsed.TotalMilliseconds;
                return found;
            });
        }
        var distant = new SurfaceMaterial { Description = "DistantTown (vertex colour x texture x 1.5)", Diffuse = DistantTowns.DiffuseTexture,
            VertexColours = true, SpecularMult = 0 };
        var distantDiffuse = _textureCache.Get(distant.Diffuse, false);
        // its coordinates are the towns' own, not the mesh's: mip streaming leaves it
        if (distantDiffuse is not null) distantDiffuse.KeepAllMips = true;
        _distantMaterial = new ObjectMaterialSet([new ObjectPartMaterial(distant, distantDiffuse, null, null, null)]);
        _scaffoldGrid = _textureCache.Get(ScaffoldTexture, false);
        if (_scaffoldGrid is not null) _scaffoldGrid.KeepAllMips = true;   // drawn at every site, at its own tiling
        foreach (var t in objects.DistantTowns)
            _towns.Add(new TownDraw { Town = t, Mesh = _meshes.Get(t.MeshPath, distant: true), Centre = t.Position });
    }

    /// <summary>
    /// Finds the textures of a mesh's parts (the <see cref="DebrisRenderer"/> shares it).
    /// </summary>
    public MaterialResolver Resolver => _resolver;

    /// <summary>
    /// The doors' open amounts and the door parts' poses.
    /// </summary>
    internal DoorPoses Doors { get; }

    /// <summary>
    /// The parts that turn by themselves; the host advances them once a frame.
    /// </summary>
    public RotatingParts Rotors { get; } = new();

    /// <summary>
    /// The turrets' turns and the poses of the parts with joints (turrets and rotating parts).
    /// </summary>
    internal JointPoses Joints { get; }

    /// <summary>
    /// The crops of the player's farms.
    /// </summary>
    internal FarmPlants Farms { get; }

    /// <summary>
    /// Logs the generated LOD levels' counts and frees the programs, the streamer, the caches and the placements.
    /// </summary>
    public void Dispose()
    {
        if (ObjectLodGen.Built + ObjectLodGen.Cached > 0)
        {
            Console.WriteLine($"object lods  {ObjectLodGen.Built} meshes generated " +
                $"({Stopwatch.GetElapsedTime(0, ObjectLodGen.BuildTicks).TotalSeconds:0.0} s of worker time), " +
                $"{ObjectLodGen.Cached} read from the cache, {ObjectLodGen.Skipped} too small or not eligible");
        }
        DisposeBuildingIcons();
        DisposeSelection();
        _colourProg.Dispose();
        _solidProg.Dispose();
        _depthProg.Dispose();
        _nativeFrame.Dispose();
        _streamer.Dispose();
        _meshes.Dispose();
        _textureCache.Dispose();
        _objects.Dispose();
    }

    /// <summary>
    /// A distant town and its baked mesh, with the bounds taken from the mesh once it is resident.
    /// </summary>
    sealed class TownDraw
    {
        /// <summary>
        /// The town (its position and mesh path).
        /// </summary>
        public required DistantTown Town;

        /// <summary>
        /// Its baked distant mesh in the mesh cache.
        /// </summary>
        public required ObjectMesh Mesh;

        /// <summary>
        /// The bounds' centre in the world: the town's position until resolved, then that plus the mesh's centre.
        /// </summary>
        public Vector3 Centre;

        /// <summary>
        /// The bounds' radius (world units): 2500 until the mesh is resident.
        /// </summary>
        public float Radius = 2500;

        /// <summary>
        /// The bounds were taken from the resident mesh (<see cref="ResolveTowns"/>).
        /// </summary>
        public bool Resolved;
    }

    #endregion

    #region Ranges and switches

    /// <summary>
    /// The guard behind <see cref="Guard"/> (null: no clamp).
    /// </summary>
    VramGuard? _guard;

    /// <summary>
    /// Interactive use (non-zero): bounded upload work per frame. 0 (screenshots): no limit.
    /// </summary>
    public int LoadBudget { get; set; }

    /// <summary>
    /// Colour real building parts owned by a BUILDING with <c>is sign</c> yellow while the highlight action is held.
    /// </summary>
    public bool HighlightSigns { get; set; }

    /// <summary>
    /// Whether a placement's EMISSIVE parts are dark now (its lights are off; <see cref="WorldLamps.EmissiveDark"/>); null: every one glows.
    /// </summary>
    public Func<string, bool>? EmissiveDark { get; set; }

    /// <summary>
    /// Real (full detail) objects are drawn up to this distance (<see cref="MeshLod.Value"/>-like: camera to bounds centre minus radius);
    /// buildings' parts have their own limits too (<see cref="ObjectRanges.PartRenderingDistance"/>). Beyond it distant towns take over.
    /// </summary>
    public float ObjectDistance { get; set; } = 12000;

    /// <summary>
    /// Landmarks (<see cref="LandmarkClass"/>) are drawn up to this distance instead of <see cref="ObjectDistance"/> (0: none, they use the object
    /// distance).
    /// </summary>
    public float LandmarkDistance { get; set; }

    /// <summary>
    /// Ogre's camera LOD bias as a distance factor: the LOD value is multiplied by it, so above 1 coarser levels come sooner, below 1 later.
    /// </summary>
    public float LodBias { get; set; } = 1;

    /// <summary>
    /// The main camera's pixels per radian (set each frame by the world frame): what the generated levels' distances are worked out with
    /// (<see cref="ObjectLodGen"/>); 0: not known yet.
    /// </summary>
    public float LodPixelsPerRadian { get; set; }

    /// <summary>
    /// Colour batches are drawn nearest first (Meitou, with the reach switch: early depth rejection of the shading of what a nearer batch covers);
    /// off: in the order they were first met.
    /// </summary>
    public bool SortNearestFirst { get; set; }

    /// <summary>
    /// Distant towns and buildings' distant meshes are drawn up to this distance (the game: its <c>distant town range</c> in zones).
    /// </summary>
    public float DistantRange { get; set; } = ObjectRanges.MaxDistantTownRangeZones * WorldLayout.ZoneSize;

    /// <summary>
    /// Draw nothing but the real objects (no distant meshes): the game with distant towns off.
    /// </summary>
    public bool NoDistant { get; set; }

    /// <summary>
    /// The memory-pressure guard (<see cref="VramGuard"/>): its range scale shortens <see cref="ObjectDistance"/> and <see cref="DistantRange"/>
    /// while the video memory is nearly used up, and the caches stop loading and evict. Null: no clamp.
    /// </summary>
    public VramGuard? Guard
    {
        get => _guard;
        set
        {
            _guard = value;
            _textureCache.Guard = value;
            _meshes.Guard = value;
        }
    }

    /// <summary>
    /// The ranges as drawn and streamed: the settings times the guard's scale (exactly the settings while it is 1).
    /// </summary>
    float RealRange => ObjectDistance * (_guard?.RangeScale ?? 1f);

    /// <summary>
    /// How far a landmark is drawn and streamed: <see cref="LandmarkDistance"/> times the guard's scale, never less than <see cref="RealRange"/> (0:
    /// the object distance).
    /// </summary>
    float LandmarkReach => Math.Max(RealRange, LandmarkDistance * (_guard?.RangeScale ?? 1f));

    /// <summary>
    /// How far distant towns and stand-ins are drawn and streamed: <see cref="DistantRange"/> times the guard's scale.
    /// </summary>
    float DistantReach => DistantRange * (_guard?.RangeScale ?? 1f);

    /// <summary>
    /// Whether the landmarks are kept apart from the zones (the renderer was made with them).
    /// </summary>
    public bool HasLandmarks => _landmarkClass is not null;

    #endregion

    #region Statistics

    /// <summary>
    /// Sums over every <see cref="Draw"/> since the start, [0, ...] colour (scene and reflection), [1, ...] depth (shadow cascades):
    /// instances, triangles, draw calls, part-limited instances, calls of Draw (a benchmark statistic; the caller divides by its frames).
    /// </summary>
    public readonly long[,] Totals = new long[2, 5];

    /// <summary>
    /// The instances in range still waiting for their mesh after the last update's scans (part of <see cref="Pending"/>).
    /// </summary>
    int _unresolvedInRange;

    /// <summary>
    /// Colour draws of parts whose diffuse map was not resident (drawn grey) and, at the same moment, textures waiting for a finer image,
    /// the largest of each since <see cref="TakePopStats"/> (the benchmark's pop-in lines).
    /// </summary>
    int _untexturedDraws, _untexturedMax, _heldNear, _coarseDraws, _coarseMax;

    /// <summary>
    /// Instances drawn by the last <see cref="Draw"/>.
    /// </summary>
    public int DrawnInstances { get; private set; }

    /// <summary>
    /// The triangles drawn by the last <see cref="Draw"/>.
    /// </summary>
    public long DrawnTriangles { get; private set; }

    /// <summary>
    /// The draw calls of the last <see cref="Draw"/>, the terrain shader's TERRAIN-mode draws included.
    /// </summary>
    public int DrawCalls { get; private set; }

    /// <summary>
    /// Real instances the last <see cref="Draw"/> left out because of their part distance (<see cref="ObjectRanges.PartRenderingDistance"/>)
    /// although within the object distance (in or out of view; a benchmark statistic).
    /// </summary>
    public int PartLimited { get; private set; }

    /// <summary>
    /// The CPU time of the last <see cref="Draw"/> (ms).
    /// </summary>
    public double LastDrawCpuMs { get; private set; }

    /// <summary>
    /// The time of the last <see cref="Update(Vector3)"/> (ms); an interactive one over 6 ms is noted in the spike log.
    /// </summary>
    public double LastUpdateMs { get; private set; }

    /// <summary>
    /// Requested work: zone layouts, meshes and textures, including used textures deferred by memory pressure.
    /// </summary>
    public int Pending => _streamer.Pending + _meshes.Pending + _uploads.Count + _textureCache.AwaitedCount + _textureCache.UnresidentUsedCount
        + _unresolvedInRange + (_landmarkTask is not null ? 1 : 0);

    /// <summary>
    /// What <see cref="Pending"/> is made of, for the loading log.
    /// </summary>
    public string PendingDetail => $"zones {_streamer.Pending}, meshes {_meshes.Pending}, uploads {_uploads.Count}, " +
        $"textures {_textureCache.AwaitedCount} loading {_textureCache.UnresidentUsedCount} deferred, unresolved {_unresolvedInRange}, " +
        $"landmarks {(_landmarkTask is not null ? 1 : 0)}";

    /// <summary>
    /// Warnings to log: each update writes them, with the caches' own (at most 30 in all), and clears them.
    /// </summary>
    public List<string> Messages { get; } = [];

    /// <summary>
    /// GPU memory held by streamed meshes and textures, for the stats line and the window title.
    /// </summary>
    public long ResidentBytes => _meshes.Bytes + _textureCache.ResidentBytes;

    /// <summary>
    /// The objects' texture cache (the VRAM dump lists it).
    /// </summary>
    public WorldTextureCache Textures => _textureCache;

    /// <summary>
    /// The resident meshes and textures in words (megabytes, counts, unloads, reloads, meshes remade finer or coarser), for the log and the
    /// VRAM report.
    /// </summary>
    public string ResidentDescription => $"{_meshes.Bytes / 1048576.0:0} MB in {_meshes.Resident} meshes ({_meshes.Unloads} unloaded, " +
        $"{_meshes.Reloads} reloaded, {_meshes.Refined} remade finer, {_meshes.Coarsened} coarser), {_textureCache.Describe()}";

    /// <summary>
    /// Meshes drawn in a reduced form while the full one loads (<see cref="ObjectMeshCache"/>).
    /// </summary>
    public int MeshesAwaitingDetail => _meshes.Upgrading;

    /// <summary>
    /// A benchmark statistic: the resolved real instances' bounding radii (world units, scaled) as percentiles, and how many have the
    /// part distance of <see cref="ObjectRanges.ObjectsViewRange"/> (small building parts) rather than none.
    /// </summary>
    public string SizeSurvey()
    {
        var radii = _streamer.AllZones.SelectMany(z => z.Real).Where(i => i.Gpu is not null).Select(i => i.Radius).Order().ToArray();
        if (radii.Length == 0) return "no resolved instances";
        int limited = _streamer.AllZones.SelectMany(z => z.Real).Count(i => i.Gpu is not null && i.Limit < float.MaxValue / 2);
        float P(double q) => radii[Math.Min((int)(radii.Length * q), radii.Length - 1)];
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{radii.Length} resolved real instances, radius p10 {P(0.1):0} / p25 {P(0.25):0} / p50 {P(0.5):0} / p75 {P(0.75):0} / p90 {P(0.9):0} " +
            $"/ max {radii[^1]:0}; {limited} with a part distance (<= {ObjectRanges.ObjectsViewRange:0} units)");
    }

    /// <summary>
    /// A line about what is loaded, for the log: zones, instances (resolved, landmarks), meshes, distant towns and the world's placements.
    /// </summary>
    public string Describe() =>
        $"{_streamer.Loaded} zones, {_streamer.Instances:N0} instances ({_streamer.Resolved:N0} resolved" +
        $"{(_streamer.LandmarkZone is { } lz ? $", of them {lz.Real.Count(i => i.Gpu is not null)} of {lz.Real.Count} landmarks" : "")}), " +
        $"{_meshes.Resident}/{_meshes.Total} meshes requested or resident, resident: {ResidentDescription}, " +
        $"{_towns.Count(t => t.Mesh.Status == ObjectMesh.State.Resident)}/{_towns.Count} distant towns; {_objects.Describe()}";

    /// <summary>
    /// The benchmark's pop-in counts, then starts over: the most untextured and coarse-level draws of one pass, the textures refining now, and the
    /// near instances held while their mesh is remade.
    /// </summary>
    public (int Untextured, int Refining, int Held, int Coarse) TakePopStats()
    {
        var result = (Math.Max(_untexturedMax, _untexturedDraws), _textureCache.Refining, heldNear: _heldNear, Math.Max(_coarseMax, _coarseDraws));
        _untexturedDraws = _untexturedMax = _heldNear = _coarseDraws = _coarseMax = 0;
        return result;
    }

    /// <summary>
    /// Adds this pass's instances, triangles, draw calls and part-limited instances to <see cref="Totals"/> (row 0 colour, 1 depth) and counts it.
    /// </summary>
    void AddTotals()
    {
        int kind = _depthPass ? 1 : 0;
        (Totals[kind, 0], Totals[kind, 1], Totals[kind, 2], Totals[kind, 3], Totals[kind, 4]) =
            (Totals[kind, 0] + DrawnInstances, Totals[kind, 1] + DrawnTriangles, Totals[kind, 2] + DrawCalls, Totals[kind, 3] + PartLimited,
                    Totals[kind, 4] + 1);
    }

    #endregion

    #region Runtime buildings and construction sites (docs/render-objects.md "Construction sites")

    /// <summary>
    /// The key prefix of a ground item's placement (see <see cref="WorldObjects.GroundKeyPrefix"/>).
    /// </summary>
    public const string GroundKeyPrefix = WorldObjects.GroundKeyPrefix;

    /// <summary>
    /// The scaffold grid of the sites (the CONSTRUCTION unit <c>scaffold</c>, docs/formats/runtime-materials.md).
    /// </summary>
    const string ScaffoldTexture = "construction.dds";

    /// <summary>
    /// The sites behind <see cref="Construction"/>.
    /// </summary>
    IReadOnlyDictionary<string, float>? _construction;

    /// <summary>
    /// The scaffold grid (<see cref="ScaffoldTexture"/>) with all its mips kept; null when the install has none.
    /// </summary>
    readonly WorldTexture? _scaffoldGrid;

    /// <summary>
    /// A building the player placed: part of its zone from now on (laid out apart from the rest of an already loaded zone).
    /// </summary>
    public void AddRuntime(BuildingPlacement placement)
    {
        _objects.AddRuntime(placement);
        _streamer.AddRuntime(placement);
    }

    /// <summary>
    /// A placed building is gone: its parts leave the zone. A town house's override is gone: its zone is laid out again, from the level data.
    /// </summary>
    public void RemoveRuntime(string instanceId)
    {
        if (_objects.RemoveRuntime(instanceId) is not { } removed) return;
        if (removed.DestroyedOverride is not null) _streamer.Reload([removed.Zone]);
        else _streamer.RemoveRuntime(instanceId, removed.InstanceId, removed.Zone);
    }

    /// <summary>
    /// The level editor cleared a level-data building (<paramref name="on"/> false: put it back): its zone is laid out again.
    /// </summary>
    public void SetCleared(BuildingPlacement placement, bool on)
    {
        if (_objects.SetCleared(placement.Key, on)) _streamer.Reload([placement.Zone]);
    }

    /// <summary>
    /// Forgets every runtime building (a loaded save replaces the world) and lays out the zones that had one again.
    /// </summary>
    public void ClearRuntime() => _streamer.Reload(_objects.ClearRuntime());

    /// <summary>
    /// The sites under construction: placement id to the look's <c>f</c> (docs/game/buildings-production.md "Construction look"). A listed building
    /// draws only the part below 1.2 f of each mesh's height, and a scaffold grid above. Read on the render thread; the host replaces it as it likes.
    /// </summary>
    public IReadOnlyDictionary<string, float>? Construction { get => _construction; set => _construction = value; }

    /// <summary>
    /// Whether a real instance belongs to a construction site (<see cref="Construction"/>), by its building's placement id.
    /// </summary>
    bool UnderConstruction(ObjectStreamer.Instance inst) =>
        _construction is { Count: > 0 } sites && !inst.Stand && sites.ContainsKey(inst.Placed.ParentPlacementId ?? inst.Placed.PlacementId);

    #endregion

    #region Placement ghosts (docs/render-objects.md "Placement ghost")

    /// <summary>
    /// The ghosts drawn, a slot each, kept between <see cref="SetGhosts"/> calls.
    /// </summary>
    readonly List<GhostSlot> _ghostSlots = [];

    /// <summary>
    /// There is at least one ghost: <see cref="Draw"/> draws them.
    /// </summary>
    bool _ghostsDrawn;

    /// <summary>
    /// One placement ghost: a building's parts laid out at the origin, drawn at <paramref name="World"/> (rotation, then position) in the tint of
    /// <paramref name="State"/>.
    /// </summary>
    public readonly record struct GhostDraw(IReadOnlyList<PlacedMesh> Parts, Matrix4x4 World, GhostState State);

    /// <summary>
    /// The tint of a ghost: the one at the cursor can be placed or not; a queued one waits for Confirm.
    /// </summary>
    public enum GhostState
    {
        /// <summary>
        /// At the cursor and placeable: tinted green.
        /// </summary>
        Valid,

        /// <summary>
        /// At the cursor but not placeable: tinted red.
        /// </summary>
        Invalid,

        /// <summary>
        /// Queued, waiting for Confirm: tinted blue.
        /// </summary>
        Queued
    }

    /// <summary>
    /// The placement ghosts of Build mode (docs/render-objects.md "Placement ghost"): each list of parts is laid out once (the same list keeps its
    /// instances), the transforms follow every call. Empty: no ghost.
    /// </summary>
    public void SetGhosts(IReadOnlyList<GhostDraw> ghosts)
    {
        while (_ghostSlots.Count > ghosts.Count)
        {
            DropGhost(_ghostSlots[^1]);
            _ghostSlots.RemoveAt(_ghostSlots.Count - 1);
        }
        while (_ghostSlots.Count < ghosts.Count) _ghostSlots.Add(new());
        for (int s = 0; s < ghosts.Count; s++)
        {
            var (parts, world, state) = ghosts[s];
            var slot = _ghostSlots[s];
            if (!ReferenceEquals(parts, slot.Parts))
            {
                DropGhost(slot);
                slot.Parts = parts;
                slot.Base = [.. parts.Where(p => !p.Interior)];
                foreach (var p in slot.Base) slot.Instances.Add(_streamer.MakeLoose(p));
            }
            slot.State = state;
            for (int i = 0; i < slot.Instances.Count; i++)
            {
                var transform = slot.Base[i].Transform * world;
                var inst = slot.Instances[i];
                inst.Placed = slot.Base[i] with { Transform = transform };
                inst.Transform = transform;
                inst.Centre = inst.Gpu is { } gpu ? Vector3.Transform(gpu.Centre, transform) : transform.Translation;
            }
        }
        _ghostsDrawn = _ghostSlots.Count > 0;
    }

    /// <summary>
    /// Gives a slot's loose instances back to the streamer and empties it.
    /// </summary>
    void DropGhost(GhostSlot slot)
    {
        foreach (var inst in slot.Instances) _streamer.DropLoose(inst);
        slot.Instances.Clear();
        slot.Parts = null;
        slot.Base = [];
    }

    /// <summary>
    /// The ghosts' instances into the batches at their LOD levels, dithered to 65 % coverage and tinted by their state; an instance whose mesh was
    /// unloaded waits for it again.
    /// </summary>
    void DrawGhosts(Vector3 eye, Vector4[] frustum, long now)
    {
        foreach (var slot in _ghostSlots)
        {
            int tint = slot.State switch { GhostState.Valid => BatchKind.Ghost, GhostState.Invalid => BatchKind.GhostBad,
                _ => BatchKind.GhostQueued };
            foreach (var inst in slot.Instances)
            {
                if (inst.Gpu is not { } gpu) continue;
                if (!ReferenceEquals(inst.Mesh.Gpu, gpu))
                {
                    Unresolve(_streamer.Loose, inst);
                    continue;
                }
                float value = Vector3.Distance(inst.Centre, eye) - inst.Radius;
                if (!SphereVisible(frustum, inst.Centre, inst.Radius)) continue;
                inst.Mesh.LastUsed = now;
                DrawnInstances++;
                int level = Math.Max(gpu.Curve.Select(Math.Max(value, 0) * LodBias), gpu.MinLevel);

                // A dither coverage of 65 %: the building shows through as a screen-door.
                Add(gpu, inst.Materials!, level, inst.Transform, 0, 0.65f, value, ghost: tint);
            }
        }
    }

    /// <summary>
    /// One slot per ghost drawn: the parts list its instances were made from, those instances, and the tint it is drawn in now.
    /// </summary>
    sealed class GhostSlot
    {
        /// <summary>
        /// The parts list the instances were made from (compared by reference; null: none).
        /// </summary>
        public object? Parts;

        /// <summary>
        /// The parts laid out at the origin, the interior parts left out.
        /// </summary>
        public PlacedMesh[] Base = [];

        /// <summary>
        /// One loose instance per part of <see cref="Base"/>, at the ghost's transform.
        /// </summary>
        public readonly List<ObjectStreamer.Instance> Instances = [];

        /// <summary>
        /// The tint the ghost is drawn in now.
        /// </summary>
        public GhostState State;
    }

    #endregion

    #region Streaming

    /// <summary>
    /// The render-thread time per frame (ms) that uploads and resolving take at most while <see cref="Covered"/>.
    /// </summary>
    const double CoveredBudgetMs = 16;

    /// <summary>
    /// A camera move farther than this between two mark passes (a second apart) is a cut or a flight, not travel (the host's flight distance).
    /// </summary>
    const float CutDistance = 10000;

    /// <summary>
    /// The landmarks being collected (all zones laid out once on a worker, about a second), the class that tells them, and what they cost.
    /// </summary>
    Task<List<(PlacedMesh Placed, float Radius)>>? _landmarkTask;

    /// <summary>
    /// The class that tells the landmarks (null: none kept apart, every placement stays in its zone).
    /// </summary>
    readonly LandmarkClass? _landmarkClass;

    /// <summary>
    /// The worker time the landmark collection took (ms), for the log line.
    /// </summary>
    double _landmarkMs;

    /// <summary>
    /// The eye of the last <see cref="Update(Vector3, double)"/>.
    /// </summary>
    Vector3 _eyeNow;

    /// <summary>
    /// How far ahead of the eye (world units) the textures are kept ready for: the camera's speed over half a second, at most 30000; 0 after a cut or
    /// during a flight (<see cref="UpdateNeedMargin"/>).
    /// </summary>
    float _needMargin;

    /// <summary>
    /// The meshes the cache unloaded since the last update: the instances resolved to them wait for them again.
    /// </summary>
    readonly HashSet<GpuObjectMesh> _unloadedMeshes = [];

    /// <summary>
    /// The material sets marked in this mark pass: their nearest needs are offered to the textures' mip streaming.
    /// </summary>
    readonly List<ObjectMaterialSet> _markedSets = [];

    /// <summary>
    /// The meshes marked in this mark pass: their nearest needs decide their finest LOD level.
    /// </summary>
    readonly List<ObjectMesh> _markedMeshes = [];

    /// <summary>
    /// The eye at the last mark pass, which <see cref="UpdateNeedMargin"/> measures the camera's speed from.
    /// </summary>
    Vector3 _lastMarkEye;

    /// <summary>
    /// When the last mark pass ran (<see cref="Environment.TickCount64"/> ms), for the speed measure; 0: none yet.
    /// </summary>
    long _lastMarkTime;

    /// <summary>
    /// When the last mark pass ran (ms): the next comes a second later; the <see cref="Ahead"/> setter zeroes it to mark a new arrival at once.
    /// </summary>
    long _lastMark;

    /// <summary>
    /// The mark pass's number: a material set or mesh stamped with it was already counted in this pass.
    /// </summary>
    int _markStamp;

    /// <summary>
    /// Render-thread time per frame spent on uploads and resolving.
    /// </summary>
    public double BudgetMs { get; set; } = 2;

    /// <summary>
    /// The view is under the loading screen (the host sets it each frame): its frames need not be smooth, so uploads and resolving take up to
    /// <see cref="CoveredBudgetMs"/> a frame instead of <see cref="BudgetMs"/>, and zones and instances are taken eight times as fast.
    /// </summary>
    public bool Covered { get; set; }

    /// <summary>
    /// The arrival eye of a character flight (<see cref="ObjectStreamer.Ahead"/>): its objects are laid out, loaded and kept, their meshes and
    /// textures as fine as seen from there. A new point re-marks at the next update instead of within the second.
    /// </summary>
    public Vector3? Ahead
    {
        get => _streamer.Ahead;
        set
        {
            if (value is not null && _streamer.Ahead is null) _lastMark = 0;
            _streamer.Ahead = value;
        }
    }

    /// <summary>
    /// Points kept loaded besides the eye's (<see cref="ObjectStreamer.Kept"/>).
    /// </summary>
    public IReadOnlyList<Vector3> Kept { get => _streamer.Kept; set => _streamer.Kept = value; }

    /// <summary>
    /// The ground's height under the eye, for the zones' loading (<see cref="ObjectStreamer.GroundUnderEye"/>).
    /// </summary>
    public float GroundUnderEye { get => _streamer.GroundUnderEye; set => _streamer.GroundUnderEye = value; }

    /// <summary>
    /// The camera's distance to its target (<see cref="ObjectStreamer.KeptViewDistance"/>).
    /// </summary>
    public float KeptViewDistance { get => _streamer.KeptViewDistance; set => _streamer.KeptViewDistance = value; }

    /// <summary>
    /// Places kept loaded (<see cref="WorldTextureCache.Areas"/>): the texture and mesh marks scale with it.
    /// </summary>
    public int Areas { set { _textureCache.Areas = value; _meshes.Areas = value; } }

    /// <summary>
    /// Presentation-only placements (<see cref="ObjectStreamer.Staged"/>): the Meitou title's staging; null in play.
    /// </summary>
    public IReadOnlyList<PlacedMesh>? Staged { get => _streamer.Staged; set => _streamer.Staged = value; }

    /// <summary>
    /// How far ahead a mesh is kept ready for: three times <see cref="_needMargin"/> (the camera's speed over 1.5 s).
    /// </summary>
    float MeshMargin => _needMargin * 3;

    /// <summary>
    /// Follows the eye: lays out zones and loads meshes and textures around it, within <see cref="BudgetMs"/> of render-thread time
    /// (<see cref="CoveredBudgetMs"/> under the loading screen).
    /// </summary>
    public void Update(Vector3 eye) => Update(eye, Covered ? Math.Max(BudgetMs, CoveredBudgetMs) : BudgetMs);

    /// <summary>
    /// Tells the object textures' mip streaming (<see cref="MipStreaming"/>) the camera: the render size in pixels and the vertical field of view.
    /// </summary>
    public void SetView(int width, int height, float fieldOfView) => SetView(width, height, fieldOfView, Gpu.LodBias);

    /// <summary>
    /// <see cref="SetView(int, int, float)"/> with the LOD bias given instead of the GPU's current one.
    /// </summary>
    public void SetView(int width, int height, float fieldOfView, float lodBias) => _textureCache.Mips.SetView(width, height, fieldOfView, lodBias);

    /// <summary>
    /// Waits until everything wanted around <paramref name="eye"/> is laid out, decoded, uploaded and textured (offscreen rendering).
    /// </summary>
    public void Settle(Vector3 eye, int timeoutMs = 300000)
    {
        var watch = Stopwatch.StartNew();
        int rounds = 0;
        double? pressureSince = null;
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            _guard?.Tick();
            Update(eye, 1e9);
            if (++rounds > 2 && Pending == 0) return;

            // Under memory pressure the guard holds the loading back: what is missing will not come until it ends, so do not wait for it.
            pressureSince = _guard is { Pressure: true } ? pressureSince ?? watch.Elapsed.TotalSeconds : null;
            if (pressureSince is { } since && watch.Elapsed.TotalSeconds - since > 5)
            {
                Console.WriteLine("warning   object streaming stopped waiting: memory pressure (VramGuard)");
                return;
            }
            Thread.Sleep(1);
        }
        Console.WriteLine($"warning   object streaming did not finish in {timeoutMs / 1000} s");
    }

    /// <summary>
    /// Where the last need pass measured from, for the VRAM dump: the eye, the margin for its speed, the arrival point and the kept points.
    /// </summary>
    public void WriteViewReport(TextWriter w)
    {
        static string P(Vector3 v) => $"{v.X:0}, {v.Y:0}, {v.Z:0}";
        w.WriteLine($"object needs measured from: eye {P(_lastMarkEye)}, speed margin {_needMargin:0}" +
            $"{(_streamer.Ahead is { } a ? $", arrival {P(a)}" : "")}");
        w.WriteLine($"  kept points ({_streamer.Kept.Count}, merged, detail from at least {_streamer.KeptViewDistance:0}): " +
            $"{string.Join("; ", _streamer.Kept.Select(P))}");
        w.WriteLine();
    }

    /// <summary>
    /// One update within <paramref name="budgetMs"/> (above 1e8: no limits, as <see cref="Settle"/> asks): the collected landmarks taken, the zones
    /// laid out, instances resolved, marks made, meshes and textures uploaded, warnings logged.
    /// </summary>
    void Update(Vector3 eye, double budgetMs)
    {
        var watch = Stopwatch.StartNew();
        _selectionDraws.Clear();   // kept for one frame's outline at most: the meshes they name may go now
        _eyeNow = eye;
        _meshes.LodBias = LodBias;
        if (LodPixelsPerRadian > 0) _meshes.PixelsPerRadian = LodPixelsPerRadian;
        _meshes.LookAhead = MeshMargin;
        bool unlimited = budgetMs > 1e8;
        float streamRange = (NoDistant ? RealRange : Math.Max(RealRange, DistantReach)) + WorldLayout.ZoneSize * 0.5f;
        _streamer.Paused = _guard is { Streaming: false };
        _streamer.PausedReach = _guard is { StreamsNear: true } ? VramGuard.NearReach : 0;
        if (_landmarkTask is { IsCompleted: true } collected)
        {
            _landmarkTask = null;
            if (collected.IsCompletedSuccessfully)
            {
                _streamer.Landmarks = _landmarkClass;
                _streamer.SetLandmarks(collected.Result);
                Console.WriteLine($"landmarks {collected.Result.Count} placements of radius {LandmarkClass.MinRadius:0}+ kept apart from the zones " +
                    $"({_landmarkMs:0} ms on the workers)");
            }
            else
            {
                _streamer.WaitForLandmarks = false;   // the zones keep them: drawn to the object distance
                Console.WriteLine($"warning   landmarks: {collected.Exception?.GetBaseException().Message}");
            }
        }

        // Under the loading screen (Covered) eight times the zones and resolves a frame.
        int scale = Covered ? 8 : 1;
        _streamer.Update(eye, streamRange, unlimited ? 64 : scale, Covered);
        ResolveTowns(eye);
        _unresolvedInRange = _streamer.Scan(eye, RealRange, DistantReach, NoDistant, Resolve, unlimited ? int.MaxValue : 40 * scale);
        _unresolvedInRange += _streamer.ScanLandmarks(eye, LandmarkReach, Resolve, unlimited ? int.MaxValue : 10 * scale);
        _unresolvedInRange += _streamer.ScanLoose(_streamer.Loose, eye, RealRange, Resolve, unlimited ? int.MaxValue : 10 * scale);
        _meshes.Pump();
        MarkInRange(eye, force: unlimited);
        _meshes.Trim();
        if (_unloadedMeshes.Count > 0)
        {
            // Instances that resolved to a mesh just unloaded wait for it again (Scan asks for it once they are in range).
            foreach (var zone in _streamer.AllZones)
                foreach (var inst in zone.Real.Concat(zone.Stand))
                    if (inst.Gpu is { } resolved && _unloadedMeshes.Contains(resolved)) Unresolve(zone, inst);
            if (_streamer.LandmarkZone is { } landmarkZone)
                foreach (var inst in landmarkZone.Real)
                    if (inst.Gpu is { } resolved && _unloadedMeshes.Contains(resolved)) Unresolve(landmarkZone, inst);
            foreach (var inst in _streamer.Loose.Real)
                if (inst.Gpu is { } resolved && _unloadedMeshes.Contains(resolved)) Unresolve(_streamer.Loose, inst);
            _unloadedMeshes.Clear();
        }
        _textureCache.Pump(wait: unlimited, max: unlimited ? 16 : 8, budgetMs: Math.Max(budgetMs * 0.5, 0.5));
        lock (_textureCache.Messages)
        {
            foreach (var m in Messages.Concat(_meshes.Messages).Concat(_textureCache.Messages).Take(30)) Console.WriteLine($"warning   {m}");
            Messages.Clear();
            _meshes.Messages.Clear();
            _textureCache.Messages.Clear();
        }
        _uploads.Run(budgetMs);
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
        if (!unlimited && LastUpdateMs > 6) SpikeLog.Note($"slow objects update [{_uploads.LastRun}]", ms: LastUpdateMs);
    }

    /// <summary>
    /// Once a second, before <see cref="ObjectMeshCache.Trim"/>: everything within its draw range counts as in use, whether or not the camera looks
    /// at it (instances reading the mesh, materials the textures), so only what is out of range gets unloaded, after the idle time.
    /// </summary>
    void MarkInRange(Vector3 eye, bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastMark < 1000) return;

        UpdateNeedMargin(eye, now, force);
        (_lastMarkEye, _lastMarkTime) = (eye, now);
        _lastMark = now;
        _markStamp++;
        _markedSets.Clear();
        _markedMeshes.Clear();
        if (_streamer.Ahead is { } arrival) MarkArrival(arrival, eye, now);
        foreach (var zone in _streamer.AllZones)
        {
            foreach (var inst in zone.Real) MarkInstance(inst, RealRange, eye, now);
            if (!NoDistant) foreach (var inst in zone.Stand) MarkInstance(inst, DistantReach, eye, now);
        }
        if (_streamer.LandmarkZone is { } landmarkZone)
        {
            float reach = LandmarkReach;
            foreach (var inst in landmarkZone.Real) MarkInstance(inst, reach, eye, now);
        }

        // Mip streaming: each texture's nearest user, over how large a texel is there.
        foreach (var set in _markedSets)
            foreach (var part in set.Parts)
                part.Offer(part.Triplanar ? set.Near : set.NearScaled);
        _textureCache.CommitNeeds();
        _textureCache.Rebalance();

        // Distance-based streaming of the finest LOD level: each mesh in range, remade when its nearest user needs finer levels (or, after a while,
        // no longer needs them).
        foreach (var mesh in _markedMeshes) _meshes.Rebalance(mesh, mesh.NearPass);
        if (NoDistant) return;
        foreach (var t in _towns)
            if (Vector3.Distance(eye, t.Centre) - t.Radius < DistantReach) t.Mesh.LastUsed = now;
        _ = _distantMaterial.Parts[0].Diffuse;
    }

    /// <summary>
    /// How far the camera goes in the half second a finer texture takes to arrive: nearer than that counts as nearer. A flight or a cut is no
    /// camera speed: its arrival is known (Ahead), and a margin from it would ask the whole path, or all around the landing, for full detail.
    /// </summary>
    void UpdateNeedMargin(Vector3 eye, long now, bool force)
    {
        bool cut = _streamer.Ahead is not null || _lastMarkTime != 0 && Vector3.Distance(eye, _lastMarkEye) > CutDistance;
        if (force || cut) _needMargin = 0;
        else if (_lastMarkTime != 0)
            _needMargin = Math.Min(Vector3.Distance(eye, _lastMarkEye) / Math.Max((now - _lastMarkTime) / 1000f, 0.05f) * 0.5f, 30000f);
    }

    /// <summary>
    /// A flight: what the arrival view draws is marked first, its textures loaded ahead of the other streaming work (the flight path's after).
    /// </summary>
    void MarkArrival(Vector3 arrival, Vector3 eye, long now)
    {
        _textureCache.Urgent = true;
        try
        {
            foreach (var zone in _streamer.AllZones)
            {
                foreach (var inst in zone.Real)
                {
                    if (Vector3.Distance(arrival, inst.Centre) - inst.Radius < Math.Min(RealRange, inst.Limit)) MarkInstance(inst, RealRange, eye, now);
                }
                if (!NoDistant)
                {
                    foreach (var inst in zone.Stand)
                    {
                        if (Vector3.Distance(arrival, inst.Centre) - inst.Radius < DistantReach) MarkInstance(inst, DistantReach, eye, now);
                    }
                }
            }
        }
        finally
        {
            _textureCache.Urgent = false;
        }
    }

    /// <summary>
    /// Marks an instance within <paramref name="range"/> of the eye as in use (its mesh, its materials' textures) and records how near its
    /// materials and mesh are needed.
    /// </summary>
    void MarkInstance(ObjectStreamer.Instance inst, float range, Vector3 eye, long now)
    {
        if (inst.Gpu is null) return;
        // or from the arrival point (Ahead)
        float distance = _streamer.Near(eye, inst.Centre), value = distance - inst.Radius, limit = Math.Min(range, inst.Limit);
        if (value >= limit)
        {
            // Not drawn, but still within what Scan asks meshes for: kept, or it would be unloaded and asked for again.
            if (distance - ObjectStreamer.RequestMargin <= limit) inst.Mesh.LastUsed = now;
            return;
        }
        inst.Mesh.LastUsed = now;
        if (inst.Materials is not { } set) return;
        if (set.Stamp != _markStamp)
        {
            set.Stamp = _markStamp;
            set.Touch();
        }
        if (inst.Stand) return;

        // During a flight the path asks for nothing finer: needs and mesh levels count only the arrival and the kept points (it keeps what it
        // has).
        if (_streamer.Ahead is not null && (value = _streamer.LoadNear(eye, inst.Centre) - inst.Radius) >= limit) return;
        if (set.NeedStamp != _markStamp)
        {
            set.NeedStamp = _markStamp;
            set.Near = set.NearScaled = float.PositiveInfinity;
            _markedSets.Add(set);
        }
        value = _streamer.DetailNear(eye, inst.Centre) - inst.Radius;
        float near = Math.Max(value - _needMargin, 0);
        if (near < set.Near) set.Near = near;

        // A mesh is kept ready for where the camera will be by the next pass and the remake (1.5 s of its speed), a texture for half a second
        // (its two levels of margin do the rest).
        float meshNear = Math.Max(value - MeshMargin, 0);
        var mesh = inst.Mesh;
        if (mesh.NearStamp != _markStamp)
        {
            mesh.NearStamp = _markStamp;
            mesh.NearPass = meshNear;
            _markedMeshes.Add(mesh);
        }
        else if (meshNear < mesh.NearPass) mesh.NearPass = meshNear;
        float scaled = near / Math.Max(inst.Radius / Math.Max(inst.Gpu.Radius, 1e-6f), 1e-6f);
        if (scaled < set.NearScaled) set.NearScaled = scaled;
    }

    /// <summary>
    /// A mesh was unloaded under an instance that had resolved to it: the instance waits for it again (<see cref="ObjectStreamer.Scan"/> asks for it
    /// once in range).
    /// </summary>
    static void Unresolve(ObjectStreamer.Zone zone, ObjectStreamer.Instance inst)
    {
        inst.Gpu = null;
        zone.Unresolved.Add(inst);
    }

    /// <summary>
    /// Forgets the batches of an unloaded mesh (their key would keep the deleted buffers' wrapper alive).
    /// </summary>
    void RemoveBatches(GpuObjectMesh gpu)
    {
        List<(GpuObjectMesh, ObjectMaterialSet, int, int)>? keys = null;
        foreach (var key in _batchMap.Keys)
            if (ReferenceEquals(key.Item1, gpu)) (keys ??= []).Add(key);
        if (keys is not null) foreach (var key in keys) _batchMap.Remove(key);
    }

    /// <summary>
    /// Takes each distant town's bounds from its mesh once resident, and asks for the meshes of the towns within the distant reach.
    /// </summary>
    void ResolveTowns(Vector3 eye)
    {
        if (NoDistant) return;
        foreach (var t in _towns)
        {
            if (!t.Resolved && t.Mesh.Status == ObjectMesh.State.Resident)
            {
                var g = t.Mesh.Gpu!;
                t.Centre = t.Town.Position + g.Centre;
                t.Radius = g.Radius;
                t.Resolved = true;
            }
            float d = Vector3.Distance(eye, t.Centre);
            if (t.Mesh.Status == ObjectMesh.State.None && d - t.Radius < DistantReach) _meshes.Request(t.Mesh, d);
        }
    }

    #endregion

    #region Materials

    /// <summary>
    /// The material sets by (lower-case mesh path, placing record id or "spec", owner record id or the chosen material's id): made once by
    /// <see cref="Resolve"/> and shared by every instance with the same key.
    /// </summary>
    readonly Dictionary<(string, string, string), ObjectMaterialSet> _materialSets = [];

    /// <summary>
    /// Makes the instance drawable once its mesh is resident: bounds, draw distance, material set. False when the mesh holds only coarser
    /// levels than this instance can need (it was loaded for farther ones): it is asked to be remade and the instance waits.
    /// </summary>
    bool Resolve(ObjectStreamer.Instance inst)
    {
        var gpu = inst.Mesh.Gpu!;
        var t = inst.Placed.Transform;
        var centre = Vector3.Transform(gpu.Centre, t);
        float scale = MathF.Sqrt(Math.Max(new Vector3(t.M11, t.M12, t.M13).LengthSquared(),
            Math.Max(new Vector3(t.M21, t.M22, t.M23).LengthSquared(), new Vector3(t.M31, t.M32, t.M33).LengthSquared())));
        float radius = gpu.Radius * scale;
        if (!inst.Stand && gpu.MinLevel > 0 && inst.TerrainMode)
        {
            _meshes.Retarget(inst.Mesh, 0);   // the terrain shader wants every level
            return false;
        }
        if (!inst.Stand && gpu.MinLevel > 0)
        {
            float value = Math.Max(_streamer.LoadNear(_eyeNow, centre) - radius - MeshMargin, 0);
            if (gpu.Curve.LevelFor(value * LodBias) < gpu.MinLevel)
            {
                if (value < 3000) _heldNear++;   // an instance that close waiting for its mesh to be remade would be seen (the benchmark counts it)
                _meshes.Retarget(inst.Mesh, value * 0.7f);
                return false;
            }
        }
        inst.Gpu = gpu;
        inst.Centre = centre;
        inst.DoorOpen = -1;   // a door part is posed again from its placement (DoorPoses.Pose)
        inst.TurretStamp = -1;   // a turret part is posed again (JointPoses.Pose)
        inst.Radius = radius;
        inst.Limit = inst.Placed.Kind == PlacedKind.BuildingPart
            ? ObjectRanges.PartRenderingDistance(gpu.Radius, inst.Placed.Owner.GetInt("function"), ObjectRanges.ObjectsViewRange)
            : float.MaxValue;
        if (inst.Stand)
        {
            inst.Materials = _distantMaterial;
            return true;
        }

        // A part's look comes from the material the layout chose (which differs per town), else from the resolver's candidates.
        var key = inst.Placed.MeshPath;
        var mkey = inst.Placed.Material is { } spec
            ? (key.ToLowerInvariant(), "spec", spec.StringId)
            : (key.ToLowerInvariant(), inst.Placed.Source.StringId, inst.Placed.Owner.StringId);
        bool made = !_materialSets.TryGetValue(mkey, out var set);
        if (made) _materialSets[mkey] = set = MakeMaterials(inst.Placed, gpu);
        inst.Materials = set!;

        // Mip streaming: this instance's distance is a use of the textures from now on (before the pass that would find it), and a new set's textures
        // are loaded now, knowing it (they were made without loading: deferred). Measured as the mark pass does (from the nearest kept point too), or
        // a kept point's textures load coarse for the far eye and wait in the refine queue.
        float near = Math.Max(_streamer.DetailNear(_eyeNow, inst.Centre) - inst.Radius - _needMargin, 0);
        foreach (var part in set!.Parts) part.OfferNow(part.Triplanar ? near : near / Math.Max(scale, 1e-6f));
        if (made) set.Touch();
        return true;
    }

    /// <summary>
    /// One material per part: the resolver's candidate that names the placing record (or its building) best.
    /// </summary>
    ObjectMaterialSet MakeMaterials(PlacedMesh placed, GpuObjectMesh gpu)
    {
        var result = new ObjectPartMaterial[gpu.Parts.Count];
        string source = $"'{placed.Source.Name}'", owner = $"'{placed.Owner.Name}'";
        var chosen = placed.Material is { } spec ? BuildingMaterial.FromSpec(spec) : null;
        for (int i = 0; i < gpu.Parts.Count; i++)
        {
            var m = chosen ?? _resolver.Candidates(placed.MeshPath, new OgreSubMesh { MaterialName = gpu.Parts[i].MaterialName })
                .OrderByDescending(c => (c.Description.Contains(owner, StringComparison.Ordinal) ? 2 : 0) + (c.Description.Contains(source,
                        StringComparison.Ordinal) ? 1 : 0))
                .FirstOrDefault();
            bool border = m?.BorderAddressing ?? false;

            // A map nothing can read is not loaded (<see cref="Material"/> never reads it): the normal map only with tangents (or for the
            // emissive flag, which looks at its key), the second set only with vertex colours (its blend weight).
            var part = gpu.Parts[i];
            bool wantNormal = part.HasTangents || (m?.Emissive ?? false);
            bool wantSecond = part.HasColours;

            // The textures are made without loading (<c>deferred</c>): <see cref="Resolve"/> tells them how near they are used, then reads their
            // keys.
            var tile = m?.Tile ?? Vector2.One;
            float minTile = Math.Min(tile.X, tile.Y);
            bool triplanar = m?.Triplanar ?? false;
            // triplanar: a surface edge-on to a projection axis has a footprint smaller than a pixel on that axis, so no bound
            float texel = minTile > 0 && !triplanar ? part.UvScale / minTile : float.PositiveInfinity;
            result[i] = new ObjectPartMaterial(m, _textureCache.Get(m?.Diffuse, border, deferred: true),
                wantNormal ? _textureCache.Get(m?.Normal, border, deferred: true) : null,
                wantSecond ? _textureCache.Get(m?.Diffuse2, border, deferred: true) : null,
                        wantSecond && part.HasTangents ? _textureCache.Get(m?.Normal2, border, deferred: true) : null,
                texel, triplanar)
            { Dust = placed.Kind == PlacedKind.BuildingPart };
        }
        return new ObjectMaterialSet(result);
    }

    #endregion

    #region GI and asset queries

    /// <summary>
    /// The bindless index of a ray-traced part's diffuse map (0: none or not resident), at the draws' bias so the entry is shared.
    /// </summary>
    internal uint RayTexture(ObjectMaterialSet materials, int part)
    {
        if (materials.Parts.Length == 0) return 0;
        uint key = materials.Parts[Math.Min(part, materials.Parts.Length - 1)].Diffuse;
        return key == 0 ? 0 : _textureCache.Index(key, Gpu.LodBias, 0);
    }

    /// <summary>
    /// The instances the GI traces (what the global illumination's acceleration structures hold, docs/render-gi.md): the resolved real instances of
    /// the zones and landmarks, not the distant stand-ins, whose bounds come within <paramref name="range"/> of <paramref name="eye"/>, without the
    /// construction sites, whose parts are drawn clipped at the scaffold (the rays would meet the whole building, the roof of a half-built or ruined
    /// one included, and light the open site as a closed room).
    /// </summary>
    internal IEnumerable<ObjectStreamer.Instance> RayInstances(Vector3 eye, float range)
    {
        foreach (var zone in _streamer.AllZones)
        {
            if (ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, eye) > range) continue;
            foreach (var inst in zone.Real)
                if (inst.Gpu is not null && inst.Materials is not null && Vector3.Distance(inst.Centre,
                        eye) - inst.Radius <= range && !InteriorHidden(inst.Placed) && !ClosedAround(inst) && !UnderConstruction(inst))
                    yield return inst;
        }
        if (_streamer.LandmarkZone is { } landmarks)
            foreach (var inst in landmarks.Real)
                if (inst.Gpu is not null && inst.Materials is not null && Vector3.Distance(inst.Centre,
                        eye) - inst.Radius <= range && !InteriorHidden(inst.Placed) && !ClosedAround(inst)) yield return inst;
    }

    /// <summary>
    /// The top of a mesh (its bounds' maximum y), from its file's bounds chunk; 0 when not found.
    /// </summary>
    public float MeshTop(string meshPath) => MeshBounds(meshPath).Item2.Y;

    /// <summary>
    /// A mesh's local bounds from its file's bounds chunk (empty when the file is not found).
    /// </summary>
    (Vector3, Vector3) MeshBounds(string name)
    {
        var path = _assets.Find(name) ?? _assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        return path is not null && OgreMeshReader.TryReadBounds(path, out var b) && b.Max.X >= b.Min.X ? (b.Min, b.Max) : default;
    }

    /// <summary>
    /// Finds a file a record names (an install-relative path, or a bare name the asset locator knows), or null.
    /// </summary>
    internal string? FindRecordFile(string recordPath) =>
        Data.Physics.CollisionPaths.Resolve(_objects.Install, recordPath) ?? _assets.Find(Path.GetFileName(recordPath.Replace('\\', '/')));

    #endregion

    #region Interiors

    /// <summary>
    /// How far beyond its zone a building's interior volume may reach (a building can stand over the zone's edge), and the most a memo keeps an
    /// answer for as its point moves (world units).
    /// </summary>
    const float InteriorOver = 200, InteriorKeep = 50;

    /// <summary>
    /// The buildings shown in the interior view (docs/render-objects.md, "Closed interiors"): placement id to the viewed floor. Their parts above
    /// that floor are hidden and their <c>interior</c> parts shown; every other building shows its outside (its interior parts hidden). Set by the
    /// game each frame.
    /// </summary>
    public IReadOnlyDictionary<string, int> InteriorView { get; set; } = new Dictionary<string, int>();

    /// <summary>
    /// The loose items (placement ids) above the shown floor of their building in the interior view: not drawn. Set by the game, which picks by the
    /// same answer.
    /// </summary>
    public IReadOnlySet<string> HiddenItems { get; set; } = new HashSet<string>();

    /// <summary>
    /// Draw every floor of the buildings in the interior view (their interior parts stay as they are): the game does so in the water reflection.
    /// </summary>
    internal bool AllFloors { get; set; }

    /// <summary>
    /// The nearest loaded building's movement destination, using collision up to the viewed floor, before the terrain.
    /// </summary>
    public Vector3? MoveHit(Vector3 origin, Vector3 direction, int floor, float maxDistance) => FloorHit(origin, direction, floor,
        maxDistance)?.Position;

    /// <summary>
    /// <see cref="MoveHit"/> with the building whose collision was hit.
    /// </summary>
    public (Vector3 Position, InteriorVolume Room)? FloorHit(Vector3 origin, Vector3 direction, int floor, float maxDistance)
    {
        (Vector3, InteriorVolume)? best = null;
        foreach (var zone in _streamer.AllZones)
            foreach (var room in zone.Interiors)
                if (room.MoveHit(origin, direction, floor, maxDistance) is { } hit)
                {
                    maxDistance = hit.Distance;
                    best = (hit.Position, room);
                }
        return best;
    }

    /// <summary>
    /// The nearest distance along the ray at which it enters a loaded building seen from outside (not in <see cref="InteriorView"/>), within
    /// <paramref name="maxDistance"/>: characters and items beyond it are hidden from the pointer. Null when none is in the way.
    /// </summary>
    public float? ClosedBuildingHit(Vector3 origin, Vector3 direction, float maxDistance)
    {
        float? best = null;
        foreach (var zone in _streamer.AllZones)
            foreach (var room in zone.Interiors)
                if (!InteriorView.ContainsKey(room.PlacementId) && room.MaskHit(origin, direction, best ?? maxDistance) is { } t) best = t;
        return best;
    }

    /// <summary>
    /// The nearest distance along the ray at which it meets the collision of a building in <see cref="InteriorView"/> on a floor it shows (as
    /// <see cref="MoveHit"/>), within
    /// <paramref name="maxDistance"/>: what lies beyond is not pointed at (game: the pick's walk ends at such a building's hit,
    /// <c>FUN_140800250</c>). Null when none is in the way.
    /// </summary>
    public float? ShownInteriorHit(Vector3 origin, Vector3 direction, float maxDistance)
    {
        float? best = null;
        foreach (var zone in _streamer.AllZones)
            foreach (var room in zone.Interiors)
                if (InteriorView.TryGetValue(room.PlacementId, out int floor) && room.MoveHit(origin, direction, floor,
                        best ?? maxDistance) is { } hit) best = hit.Distance;
        return best;
    }

    /// <summary>
    /// The indoor volume of a loaded building that contains <paramref name="p"/> (widened by <paramref name="margin"/>), or null.
    /// </summary>
    public InteriorVolume? InteriorAt(Vector3 p, float margin = 0) => InteriorAt(p, margin, null);

    /// <summary>
    /// <see cref="InteriorAt(Vector3, float)"/> without a margin, asked again only once <paramref name="p"/> has moved farther than the nearest
    /// interior's box was (at most 50 units, over every zone the moved point could ask) or the interiors changed. Standing still inside asks nothing
    /// either: there the distance kept is 0.
    /// </summary>
    public InteriorVolume? InteriorAt(Vector3 p, ref InteriorMemo memo)
    {
        // Strictly nearer: a point on a box's edge can be inside.
        if (memo.Set && memo.Version == _streamer.InteriorVersion && Vector3.DistanceSquared(p,
                memo.At) is var moved && (moved == 0 || moved < memo.Clear * memo.Clear)) return memo.Volume;
        float clear = InteriorKeep, reach = InteriorOver + InteriorKeep;
        InteriorVolume? found = null;
        var (lo, hi) = (WorldLayout.ZoneOf(p.X - reach, p.Z - reach), WorldLayout.ZoneOf(p.X + reach, p.Z + reach));
        for (int x = lo.X; x <= hi.X && found is null; x++)
            for (int y = lo.Y; y <= hi.Y && found is null; y++)
            {
                if (_streamer.ZoneAt(new ZoneCoordinate(x, y)) is not { Interiors.Length: > 0 } zone) continue;
                float zoneDistance = ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, p);
                if (zoneDistance > reach) continue;
                foreach (var v in zone.Interiors)
                {
                    clear = MathF.Min(clear, v.Distance(p));
                    if (zoneDistance <= InteriorOver && v.Contains(p))
                    {
                        found = v;
                        break;
                    }
                }
            }
        memo = new InteriorMemo { Set = true, Version = _streamer.InteriorVersion, At = p, Clear = found is null ? clear : 0, Volume = found };
        return found;
    }

    /// <summary>
    /// The loaded volume of <paramref name="volume"/>'s building (the same, or its zone's re-laid one), or null once its zone is unloaded or the
    /// building removed or destroyed.
    /// </summary>
    public InteriorVolume? LoadedInterior(InteriorVolume volume)
    {
        if (volume.Footprint.Length == 0) return null;
        var at = volume.Footprint[0];
        var (lo, hi) = (WorldLayout.ZoneOf(at.X - InteriorOver, at.Y - InteriorOver), WorldLayout.ZoneOf(at.X + InteriorOver, at.Y + InteriorOver));
        for (int x = lo.X; x <= hi.X; x++)
            for (int y = lo.Y; y <= hi.Y; y++)
                if (_streamer.ZoneAt(new ZoneCoordinate(x, y)) is { Interiors.Length: > 0 } zone)
                    foreach (var v in zone.Interiors)
                        if (ReferenceEquals(v, volume) || v.PlacementId == volume.PlacementId) return v;
        return null;
    }

    /// <summary>
    /// The indoor volume of a loaded building straight above or below the ground point (<paramref name="x"/>, <paramref name="z"/>), whatever the
    /// height: what a ray cast down from far above hits (the game's interior ambience test, docs/audio.md "Interiors"). The highest one when several
    /// overlap; null when none.
    /// </summary>
    public InteriorVolume? InteriorUnder(float x, float z)
    {
        InteriorVolume? best = null;
        foreach (var zone in _streamer.AllZones)
        {
            if (zone.Interiors.Length + zone.GateHulls.Length == 0 || ObjectStreamer.ZoneDistance(zone.X0, zone.Z0,
                    new Vector3(x, 0, z)) > 200) continue;
            foreach (var v in zone.Interiors)
                if ((best is null || v.YMax > best.YMax) && v.ContainsGround(x, z)) best = v;
            foreach (var v in zone.GateHulls)
                if ((best is null || v.YMax > best.YMax) && v.ContainsGround(x, z)) best = v;
        }
        return best;
    }

    /// <summary>
    /// The GroundType a character at <paramref name="feet"/> stands on when the floor under it is a building's: its <c>interior material</c> when
    /// <paramref name="inside"/> (the character is in a building), else its <c>exterior material</c>; -1 when the ground is not a building's or its
    /// floors are still being made (docs/audio.md "Surface").
    /// </summary>
    public int FloorMaterialUnder(Vector3 feet, bool inside) => _objects.Floors.MaterialUnder(feet, inside);

    /// <summary>
    /// The highest ordinary floor part of any building at a point, on floors up to <paramref name="floorOf"/> of its placement key; null for none or
    /// still being made (docs/formats/camera.md "Height").
    /// </summary>
    public float? FloorHeightAt(float x, float z, Func<string, int> floorOf) => _objects.Floors.HighestFloorAt(x, z, floorOf);

    /// <summary>
    /// The loaded mask of a building or one of its layout objects (building id / item id).
    /// </summary>
    internal InteriorVolume? InteriorFor(string placementId)
    {
        int slash = placementId.IndexOf('/');
        var owner = slash < 0 ? placementId.AsSpan() : placementId.AsSpan(0, slash);
        foreach (var zone in _streamer.AllZones)
            foreach (var room in zone.Interiors)
                if (owner.SequenceEqual(room.PlacementId)) return room;
        return null;
    }

    /// <summary>
    /// The loaded buildings with interior mask meshes whose footprint comes within <paramref name="range"/> of <paramref name="eye"/> (X/Z), into
    /// <paramref name="into"/>.
    /// </summary>
    internal void InteriorMasks(Vector3 eye, float range, List<InteriorVolume> into)
    {
        into.Clear();
        var q = new Vector2(eye.X, eye.Z);
        foreach (var zone in _streamer.AllZones)
        {
            if (zone.Interiors.Length == 0 || ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, eye) > range + 200) continue;
            foreach (var v in zone.Interiors)
            {
                if (v.MaskMeshes.Count == 0 || v.Footprint.Length == 0) continue;
                float nearest = float.MaxValue;
                foreach (var p in v.Footprint) nearest = MathF.Min(nearest, Vector2.DistanceSquared(p, q));
                if (nearest <= range * range || v.ContainsGround(eye.X, eye.Z)) into.Add(v);
            }
        }
    }

    /// <summary>
    /// Use room-scale probes only while the camera target is in or beside a viewed building and under its cover
    /// (<see cref="InteriorVolume.Covered"/>): on a walkable roof, the hull's top storey, the room grid stood its probes under the roof slab and
    /// darkened the roof as a closed room. A construction site (a ruin being rebuilt included) is open above its clip, so it never takes the room
    /// grid.
    /// </summary>
    internal Vector3? GiInteriorFocus(Vector3 target)
    {
        if (InteriorView.Count == 0) return null;
        foreach (var zone in _streamer.AllZones)
            foreach (var room in zone.Interiors)
            {
                if (!InteriorView.ContainsKey(room.PlacementId) || _construction is { } sites && sites.ContainsKey(room.PlacementId)) continue;
                var p = new Vector3(target.X, Math.Clamp(target.Y, room.YMin, room.YMax), target.Z);
                if (room.Contains(p, 64) && room.Covered(p)) return p;
            }
        return null;
    }

    /// <summary>
    /// Whether the instance stands in a loaded building's interior (<see cref="InteriorAt(Vector3, float)"/>), kept on it until the interiors or its
    /// place change: every view (the camera's, each shadow cascade's, the reflection's) asks it of each piece of furniture.
    /// </summary>
    bool InInterior(ObjectStreamer.Instance inst) => InteriorOf(inst).Inside;

    /// <summary>
    /// Whether the instance takes the indoor GI (docs/render-gi.md): a building's interior part, its layout furniture inside a hull, or a building of
    /// its own (furniture or a station the player placed) standing in another building's hull; a building in its own hull is its outside.
    /// </summary>
    bool IndoorFurniture(ObjectStreamer.Instance inst)
    {
        var p = inst.Placed;
        if (p.Interior) return true;
        if (p.ParentPlacementId is not null) return InInterior(inst);
        return p.Kind == PlacedKind.BuildingPart && InteriorOf(inst) is { Inside: true, Building: { } holder } && holder != p.PlacementId;
    }

    /// <summary>
    /// Where an instance stands among the interiors (inside a hull or not, the building holding it and its floor), kept on the instance until the
    /// interiors or its place change.
    /// </summary>
    ObjectStreamer.InteriorAnswer InteriorOf(ObjectStreamer.Instance inst)
    {
        var at = inst.Transform.Translation;
        if (inst.Interior is { } known && known.Version == _streamer.InteriorVersion && known.At == at) return known;
        bool inside = InteriorAt(at) is not null;

        // Engine choice: the 2-unit margin the host gives characters, so an object standing on the hull's top floor counts as on it. A building
        // placed inside another takes that one's hull where it reaches it (a neighbour's within the margin is not its holder).
        var holder = InteriorAt(at, 2, inst.Placed.ContainerId);
        return inst.Interior = new(_streamer.InteriorVersion, at, inside, holder?.PlacementId, holder?.FloorAt(at) ?? 0);
    }

    /// <summary>
    /// Whether a building of its own (a turret on a wall, furniture the player placed) stands above the viewed floor of a building in the interior
    /// view, by where it stands in that building's hull: it is hidden with that floor (game: FUN_140547140 / FUN_14054a8f0, as the host does for
    /// characters). A loose item: the game's answer (<see cref="HiddenItems"/>, FUN_1409fb540).
    /// </summary>
    bool StandsAbove(ObjectStreamer.Instance inst)
    {
        var p = inst.Placed;
        if (p.Kind == PlacedKind.Item) return !AllFloors && HiddenItems.Contains(p.PlacementId);
        if (InteriorView.Count == 0 || AllFloors || p.Kind != PlacedKind.BuildingPart || p.ParentPlacementId is not null
            || InteriorView.ContainsKey(p.PlacementId)) return false;
        var answer = InteriorOf(inst);
        return answer.Building is { } building && InteriorView.TryGetValue(building, out int floor) && answer.Floor > floor;
    }

    /// <summary>
    /// Whether a building placed inside another (<see cref="PlacedMesh.Contained"/>) is hidden: the building whose hull holds it shows its outside
    /// (not in the interior view; a construction site is open). Game: closing an interior hides its contained objects, in every view
    /// (docs/render-objects.md "Closed interiors").
    /// </summary>
    bool ClosedAround(ObjectStreamer.Instance inst)
    {
        if (!inst.Placed.Contained) return false;
        return InteriorOf(inst).Building is { } holder && holder != inst.Placed.PlacementId && !InteriorView.ContainsKey(holder)
            && _construction?.ContainsKey(holder) != true;
    }

    /// <summary>
    /// <see cref="InteriorAt(Vector3, float)"/>, preferring the building <paramref name="prefer"/> names (a site's id or a level building's instance
    /// id,
    /// <see cref="PlacedMesh.ContainerId"/>) when its volume is one of those containing the point.
    /// </summary>
    InteriorVolume? InteriorAt(Vector3 p, float margin, string? prefer)
    {
        InteriorVolume? first = null;

        // The zones within 200 units of the point (a building may stand over its zone's edge): the one it is in and at most three beside it.
        var (lo, hi) = (WorldLayout.ZoneOf(p.X - InteriorOver, p.Z - InteriorOver), WorldLayout.ZoneOf(p.X + InteriorOver, p.Z + InteriorOver));
        for (int x = lo.X; x <= hi.X; x++)
            for (int y = lo.Y; y <= hi.Y; y++)
            {
                if (_streamer.ZoneAt(new ZoneCoordinate(x, y)) is not { Interiors.Length: > 0 } zone || ObjectStreamer.ZoneDistance(zone.X0, zone.Z0,
                        p) > InteriorOver) continue;
                foreach (var v in zone.Interiors)
                    if (v.Contains(p, margin))
                    {
                        if (prefer is null || IsKeyOf(v.PlacementId, prefer)) return v;
                        first ??= v;
                    }
            }
        return first;
    }

    /// <summary>
    /// Whether placement key <paramref name="key"/> is building <paramref name="id"/>'s: a site's key is its id, a level building's ends in ":" and
    /// its instance id (<see cref="BuildingKey"/>).
    /// </summary>
    static bool IsKeyOf(string key, string id) =>
        key == id || key.Length > id.Length && key[key.Length - id.Length - 1] == ':' && key.EndsWith(id, StringComparison.Ordinal);

    /// <summary>
    /// Whether the interior view leaves a building part out: an interior part (or inside-only furniture) of a building seen from outside, or, in the
    /// view, a part or object above the viewed floor. Floor 0 is never hidden (game: only the floor 1..3 lists are switched). Nor is a door: in the
    /// game it is a building of its own, which the view of the building it belongs to does not switch, and 15 of the base game's door leaves are
    /// floor-1 parts (Storm Houses, shacks), which left the doorway empty at floor 0 (docs/render-objects.md "Doors").
    /// </summary>
    bool InteriorHidden(PlacedMesh p)
    {
        if (p.Kind != PlacedKind.BuildingPart) return false;
        if (p.DoorIndex >= 0 && !p.Interior) return false;
        if (InteriorView.Count == 0 || !p.Interior && (p.Floor == 0 || AllFloors)) return p.Interior;

        // Layout objects (signs, furniture) go with their building.
        if (!InteriorView.TryGetValue(p.ParentPlacementId ?? p.PlacementId, out int floor)) return p.Interior;
        return p.Floor > floor && !AllFloors;
    }

    /// <summary>
    /// What <see cref="InteriorHidden"/> leaves out whatever the viewed floor: an interior part of a building not in the interior view.
    /// </summary>
    bool OutsideHidden(PlacedMesh p) =>
        p.Kind == PlacedKind.BuildingPart && p.Interior && !InteriorView.ContainsKey(p.ParentPlacementId ?? p.PlacementId);

    /// <summary>
    /// A point's last <see cref="InteriorAt(Vector3, ref InteriorMemo)"/> answer, kept by the asker (a character): good while the interiors are the
    /// same and the point within <c>Clear</c> of where it was asked.
    /// </summary>
    public struct InteriorMemo
    {
        /// <summary>
        /// An answer is held.
        /// </summary>
        internal bool Set;

        /// <summary>
        /// The streamer's interior version the answer was found under.
        /// </summary>
        internal int Version;

        /// <summary>
        /// Where the answer was asked.
        /// </summary>
        internal Vector3 At;

        /// <summary>
        /// How far the point may move with the answer still good (0 inside a volume).
        /// </summary>
        internal float Clear;

        /// <summary>
        /// The answer: the volume the point was in, or null.
        /// </summary>
        internal InteriorVolume? Volume;
    }

    #endregion

    #region Drawing

    /// <summary>
    /// The batches by (mesh, material set, level, <see cref="BatchKind"/> flags), kept between passes (emptied at each start; an unloaded mesh's
    /// are removed).
    /// </summary>
    readonly Dictionary<(GpuObjectMesh, ObjectMaterialSet, int, int), Batch> _batchMap = [];

    /// <summary>
    /// The batches holding instances in this pass, in the order first met (or nearest first with <see cref="SortNearestFirst"/>).
    /// </summary>
    readonly List<Batch> _active = [];

    /// <summary>
    /// The TERRAIN-mode instances of a pass (part mesh, index count, transform), drawn through <see cref="TerrainRenderer.DrawMeshes"/>.
    /// </summary>
    readonly List<(MeshBindings, int, Matrix4x4)> _terrainMeshes = [];   // the TERRAIN-mode instances of a draw (TerrainRenderer.DrawMeshes)

    /// <summary>
    /// Those TERRAIN-mode instances over an interior mask (<see cref="InteriorCut"/>): drawn with the cut.
    /// </summary>
    readonly List<(MeshBindings, int, Matrix4x4)> _terrainMeshesCut = [];   // those over an interior mask (InteriorCut)

    /// <summary>
    /// The fog cull (set by the world frame around the main camera's draw only): instances wholly hidden by the fog the eye is in are skipped.
    /// </summary>
    internal FogVolumes? FogCull { get; set; }

    /// <summary>
    /// The interior cut (docs/render-interior-cut.md; set by the world frame around its scene's colour draw, null otherwise): map features over a
    /// mask take it.
    /// </summary>
    internal InteriorMaskRenderer? InteriorCut { get; set; }

    /// <summary>
    /// The water reflection's cull (set around the reflection pass's own draw): an instance (bounding sphere centre and radius) it answers false for
    /// is skipped.
    /// </summary>
    internal Func<Vector3, float, bool>? MirrorCull { get; set; }

    /// <summary>
    /// Draws the objects seen from <paramref name="eye"/> through <paramref name="frustum"/>.
    /// </summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour,
        float fogDistance, TerrainRenderer terrain)
    {
        var cpu = Stopwatch.StartNew();
        long now = Environment.TickCount64;
        float real = RealRange;
        BeginDraw();

        // 1. Cull and choose levels: every instance into the batch of its (mesh, materials, level) with its dither range.
        float realBand = Math.Clamp(real * 0.1f, 50, 1500);
        float distantBand = Math.Clamp(DistantReach * 0.15f, 500, 4000);
        foreach (var zone in _landmarksOnly ? [] : _streamer.ZonesNear(eye, Math.Max(real, NoDistant ? 0 : DistantReach), frustum))
        {
            if (ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, eye) <= real)
                foreach (var inst in zone.Real) DrawReal(zone, inst, real, eye, frustum, options, now);
            if (NoDistant) continue;
            foreach (var inst in zone.Stand) DrawDistant(zone, inst, eye, frustum, now, real, realBand, distantBand);
        }

        // The landmarks (a few hundred, kept apart from the zones): the same test with their own reach, the part distance still applying.
        if (_streamer.LandmarkZone is { } landmarkZone)
        {
            float reach = LandmarkReach;
            foreach (var inst in landmarkZone.Real) DrawReal(landmarkZone, inst, reach, eye, frustum, options, now);
        }

        // The crops of the player's farms: drawn as real objects, in the shadow passes too.
        if (Farms.Count > 0 && !_landmarksOnly) DrawFarms(real, eye, frustum, options, now);

        // The placement ghost (Build mode): its parts at the cursor, dithered and tinted green or red; not in a shadow or the landmarks-only pass.
        if (_ghostsDrawn && !_depthPass && !_landmarksOnly) DrawGhosts(eye, frustum, now);
        if (!NoDistant && !_landmarksOnly) DrawTowns(eye, frustum, now);

        // Nearest first: what a nearer batch covers fails the early depth test of the farther ones (the colour pass only; the depth passes have no
        // shading to save).
        if (SortNearestFirst && !_depthPass) _active.Sort(static (a, b) => a.Near.CompareTo(b.Near));

        // 2. The instances.
        UploadInstances();

        // 3. Draw: Prepare reads the textures (WorldTexture.Key) and makes the draw list, Record puts it into a native segment of VkGl's pass.
        var prog = _depthPass ? _depthProg : _colourProg;
        int wireMode = _debugLevels == 2 ? 2 : options.Wireframe;
        if (_active.Count > 0)
        {
            var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour,
                FogDistance = fogDistance };
            if (!_depthPass) InteriorMaskRenderer.Apply(ref view, InteriorCut);
            else (view.CutVolumes, view.CutRanges, view.CutPlanes) = (_cutConstants.CutVolumes, _cutConstants.CutRanges, _cutConstants.CutPlanes);
            var fadeRange = new Vector4(real - realBand, real, DistantReach - distantBand, DistantReach);
            if (wireMode != 2)
            {
                PrepareDraws(options, wire: false);
                RecordDraws(prog, wire: false, in view, fadeRange);
            }
            if (wireMode != 0)
            {
                PrepareDraws(options, wire: true);
                RecordDraws(prog, wire: true, in view, fadeRange);
            }
        }
        if (_terrainMeshes.Count > 0) DrawCalls += terrain.DrawMeshes(_terrainMeshes, _depthPass);
        if (_terrainMeshesCut.Count > 0) DrawCalls += terrain.DrawMeshes(_terrainMeshesCut, cut: InteriorCut);
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
        AddTotals();
        _selecting.Clear();
    }

    /// <summary>
    /// Starts a pass: the counters zeroed (their maxima kept), the batches emptied, the shadow pass's cut and the selection begun.
    /// </summary>
    void BeginDraw()
    {
        DrawnInstances = 0;
        PartLimited = 0;
        _untexturedMax = Math.Max(_untexturedMax, _untexturedDraws);
        _untexturedDraws = 0;
        _coarseMax = Math.Max(_coarseMax, _coarseDraws);
        _coarseDraws = 0;
        DrawnTriangles = 0;
        DrawCalls = 0;
        foreach (var b in _batchMap.Values) b.Count = 0;
        _active.Clear();
        _terrainMeshes.Clear();
        _terrainMeshesCut.Clear();
        if (_depthPass) ShadowCut(ref _cutConstants);
        BeginSelection();
    }

    /// <summary>
    /// A zone's distant stand-in: drawn within the distant reach when visible, faded in past the real range and out at the reach's edge.
    /// </summary>
    void DrawDistant(ObjectStreamer.Zone zone, ObjectStreamer.Instance inst, Vector3 eye, Vector4[] frustum, long now, float real, float realBand,
        float distantBand)
    {
        if (inst.Gpu is not { } gpu) return;
        if (!ReferenceEquals(inst.Mesh.Gpu, gpu))
        {
            Unresolve(zone, inst);
            return;
        }
        float value = Vector3.Distance(inst.Centre, eye) - inst.Radius;
        if (value >= DistantReach || !SphereVisible(frustum, inst.Centre, inst.Radius) || MirrorCull?.Invoke(inst.Centre, inst.Radius) == false
            || FogCull?.Hidden(inst.Centre, inst.Radius, FogVolumes.CullKind.Objects) == true) return;
        float w = ObjectRanges.RiseWeight(value, real - realBand, realBand) * ObjectRanges.EdgeWeight(value, DistantReach, distantBand);
        if (w <= 0) return;
        inst.Mesh.LastUsed = now;
        DrawnInstances++;
        Emit(inst, gpu, value, w);
    }

    /// <summary>
    /// The towns' distant meshes within the distant reach and visible, into the town batch.
    /// </summary>
    void DrawTowns(Vector3 eye, Vector4[] frustum, long now)
    {
        foreach (var t in _towns)
        {
            if (t.Mesh.Gpu is not { } gpu) continue;
            float value = Vector3.Distance(t.Centre, eye) - t.Radius;
            if (value >= DistantReach || !SphereVisible(frustum, t.Centre, t.Radius) || MirrorCull?.Invoke(t.Centre, t.Radius) == false
                || FogCull?.Hidden(t.Centre, t.Radius, FogVolumes.CullKind.Objects) == true) continue;
            t.Mesh.LastUsed = now;
            var batch = BatchFor(gpu, _distantMaterial, 0, BatchKind.Town);
            if (batch.Count == 0)
            {
                _active.Add(batch);
                batch.Near = 0;
            }
            batch.Add(Matrix4x4.CreateTranslation(t.Town.Position));
            DrawnInstances++;
        }
    }

    /// <summary>
    /// The active batches' matrices, each batch's contiguous, into this frame's constants (one copy; the draws reach a batch by firstInstance).
    /// </summary>
    void UploadInstances()
    {
        int total = 0;
        foreach (var b in _active)
        {
            b.Offset = total;
            total += b.Count;
        }
        if (total == 0) return;

        _instances = Gpu.Frame.AllocateConstants((ulong)(total * InstanceStride), 16);
        var target = _instances.Bytes;
        foreach (var b in _active)
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * InstanceStride)..]);
    }

    /// <summary>
    /// One real instance of <see cref="Draw"/>: culled by its range (<paramref name="range"/>, the object distance or, for a landmark, the landmark
    /// reach, and its part distance) and the frustum, then its LOD level and batches. The range is the one <see cref="MarkInRange"/> and the
    /// streamer's scans use.
    /// </summary>
    void DrawReal(ObjectStreamer.Zone zone, ObjectStreamer.Instance inst, float range, Vector3 eye, Vector4[] frustum, WorldRenderOptions options,
        long now)
    {
        if (inst.Gpu is not { } gpu) return;
        if (!ReferenceEquals(inst.Mesh.Gpu, gpu))
        {
            Unresolve(zone, inst);
            return;
        }

        // A shadow pass keeps what the floor view hides above the viewed floor, so a lower floor stays shaded under its ceiling
        // (docs/render-objects.md "Closed interiors"): only a closed building's interior parts stay out.
        if ((_depthPass ? OutsideHidden(inst.Placed) : InteriorHidden(inst.Placed) || StandsAbove(inst)) || ClosedAround(inst)) return;
        if (inst.Placed.DoorIndex >= 0) Doors.Pose(inst, gpu);
        if (inst.Placed.Joints is not null) Joints.Pose(inst, gpu);
        float value = Vector3.Distance(inst.Centre, eye) - inst.Radius;
        float limit = Math.Min(range, inst.Limit);
        if (value >= limit || !SphereVisible(frustum, inst.Centre, inst.Radius) || MirrorCull?.Invoke(inst.Centre,
                inst.Radius) == false || FogCull?.Hidden(inst.Centre, inst.Radius, FogVolumes.CullKind.Objects) == true)
        {
            if (value >= inst.Limit && value < range) PartLimited++;   // stopped by the game's part distance, not the object distance (benchmark)
            return;
        }
        float w = ObjectRanges.EdgeWeight(value, limit, Math.Clamp(limit * 0.1f, 50, 1500));
        if (w <= 0) return;
        // a far shadow cascade: no finer a level than ShadowLod.ObjectLodFloor
        float lodValue = _shadowLodFloor > 0 ? Math.Max(value, _shadowLodFloor) : value;
        inst.Mesh.LastUsed = now;
        DrawnInstances++;
        if (inst.TerrainMode && options.Textures)
        {
            // The terrain shader's path: one draw each, no fading, the level the game would pick.
            int level = gpu.Curve.Select(lodValue * LodBias);
            var g = gpu.Manual[level] ?? gpu;
            int lv = gpu.Manual[level] is null ? level : 0;

            // A TERRAIN-mode feature over an interior mask: drawn with the cut (Feature_Terrain_DX11 clips against the mask,
            // docs/render-interior-cut.md).
            var into = !_depthPass && inst.Placed.Kind == PlacedKind.MapFeature && InteriorCut is { Active: true } mask && mask.Overlaps(inst.Centre,
                inst.Radius) ? _terrainMeshesCut : _terrainMeshes;
            foreach (var gp in g.Parts)
                if (gp.Count[lv] > 0)
                {
                    into.Add((_meshes.PlainMesh(gp, lv), gp.Count[lv], inst.Transform));
                    DrawnTriangles += gp.Count[lv] / 3;
                }
            return;
        }
        Emit(inst, gpu, value, w, lodValue);
    }

    /// <summary>
    /// The farms' shown plants, as real instances of <see cref="Draw"/>.
    /// </summary>
    void DrawFarms(float range, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, long now)
    {
        foreach (var slot in Farms.Slots)
            for (int i = 0; i < slot.Instances.Count; i++)
                if (slot.Shown[i]) DrawReal(_streamer.Loose, slot.Instances[i], range, eye, frustum, options, now);
    }

    /// <summary>
    /// Adds an instance at its LOD level (<see cref="AddLevels"/>).
    /// </summary>
    void Emit(ObjectStreamer.Instance inst, GpuObjectMesh gpu, float value, float weight) => Emit(inst, gpu, value, weight, value);

    /// <summary>
    /// <paramref name="lodValue"/> picks the levels (at least <paramref name="value"/>, the distance, in a far shadow cascade).
    /// </summary>
    void Emit(ObjectStreamer.Instance inst, GpuObjectMesh gpu, float value, float weight, float lodValue)
    {
        var blend = gpu.Curve.Blend(lodValue * LodBias);

        // A mesh held without its finest levels (made for farther users, see ObjectMeshCache.Retarget) draws its finest instead, until it is remade.
        if (gpu.MinLevel > 0 && blend.Lower < gpu.MinLevel) _coarseDraws++;
        if (gpu.MinLevel > 0) blend = new LodBlend(Math.Max(blend.Lower, gpu.MinLevel), Math.Max(blend.Upper, gpu.MinLevel), blend.T);
        var m = inst.Transform;

        // Spare instance-matrix component: room GI gain without splitting shared furniture batches (1), plus 2 when its building's lights are off,
        // which darkens its EMISSIVE materials (BuildingLodShaders).
        m.M34 = (IndoorFurniture(inst) ? 1 : 0)
            + (!_depthPass && EmissiveDark is { } dark && dark(inst.Placed.ParentPlacementId ?? inst.Placed.PlacementId) ? 2 : 0);

        // Another spare component, M44: 1, or for a construction site BuildingLodShaders.ScaffoldBase + its look's f, or a negative packed tint
        // (FarmPlants.PackTint). A site's M34 also carries its part's mesh top and the scaffold tiling (BuildingLodShaders.PackSite).
        bool site = false;
        bool highlight = HighlightSigns && !_depthPass && !inst.Stand && inst.Placed.Owner.GetBool("is sign");
        if (_construction is { Count: > 0 } && !inst.Stand && _construction.TryGetValue(inst.Placed.ParentPlacementId ?? inst.Placed.PlacementId,
                out float built))
        {
            m.M44 = BuildingLodShaders.ScaffoldBase + Math.Clamp(built, 0f, 1f);
            m.M34 += BuildingLodShaders.PackSite(gpu.Top, inst.Placed.Owner.GetFloat("scaffolding tex scale"));
            site = true;
        }
        else if (inst.Tint < 0) m.M44 = inst.Tint;

        // A map feature (CLIP_INTERIOR, docs/render-interior-cut.md) over a building's interior mask takes the cut, and so does another building's
        // part over the mask of a building in the interior view (CutsIntoViewed); the rest keep the programs without it. In a shadow pass such a part
        // casts nothing inside the viewed buildings' hulls (ShadowCut).
        bool cut = _depthPass
            ? _shadowCuts.Count > 0 && CutsShadow(inst)
            : InteriorCut is { Active: true } mask
                && (inst.Placed.Kind == PlacedKind.MapFeature ? mask.Overlaps(inst.Centre, inst.Radius) : CutsIntoViewed(inst, mask));
        if (_selecting.Count > 0 && SelectingKey(inst.Placed) is { } selectedKey)
        {
            _selectionDraws.Add(new(selectedKey, gpu, inst.Materials!, blend, m, weight, value, BackCulled(inst.Placed), site, inst.Centre,
                    inst.Radius));
        }
        AddLevels(gpu, inst.Materials!, blend, m, weight, value, BackCulled(inst.Placed), site, highlight, cut);
    }

    /// <summary>
    /// Whether a placement's back faces are culled: a building part's in the colour passes (the game's <c>objects.material</c> keeps Ogre's
    /// default clockwise culling, so a single-sided wall between the camera and an opened floor is not seen, docs/render-objects.md), an interior
    /// part's in the shadow passes too.
    /// </summary>
    bool BackCulled(PlacedMesh p) => p.Interior || !_depthPass && p.Kind == PlacedKind.BuildingPart;

    /// <summary>
    /// Adds an instance at its LOD level: two batches while blending levels, the upper level taking [0, t·w) of the dither range and the lower [t·w,
    /// w).
    /// </summary>
    void AddLevels(GpuObjectMesh gpu, ObjectMaterialSet materials, LodBlend blend, Matrix4x4 m, float weight, float near, bool backCull, bool site,
        bool highlight = false, bool cut = false)
    {
        if (!blend.IsBlending)
        {
            Add(gpu, materials, blend.Lower, m, 0, weight >= 0.999f ? 2 : weight, near, backCull, site, highlight: highlight, cut: cut);
            return;
        }
        Add(gpu, materials, blend.Upper, m, 0, blend.T * weight, near, backCull, site, highlight: highlight, cut: cut);
        Add(gpu, materials, blend.Lower, m, blend.T * weight, weight, near, backCull, site, highlight: highlight, cut: cut);
    }

    /// <summary>
    /// Adds an instance's matrix to its batch at a LOD level (a manual level's own mesh, at level 0), its dither range [<paramref name="lo"/>,
    /// <paramref name="hi"/>) in M14/M24 (lo 0 and hi 2: fully visible).
    /// </summary>
    void Add(GpuObjectMesh gpu, ObjectMaterialSet materials, int level, Matrix4x4 m, float lo, float hi, float near, bool backCull = false,
        bool site = false, int ghost = 0, bool highlight = false, bool cut = false)
    {
        var manual = gpu.Manual[level];

        // Fully visible: lo 0 and hi 2 (the shader keeps everything when hi >= 1 and lo is 0). With the early depth test off every batch is "fading":
        // the old merging. A construction site discards the fragments above its clip plane, so it needs the program with a discard as well.
        bool fading = !EarlyDepth.Enabled || lo > 0 || hi < 1 || site;
        var batch = BatchFor(manual ?? gpu, materials, manual is null ? level : 0,
            (fading ? BatchKind.Fading : BatchKind.Solid) | (backCull ? BatchKind.BackCull : 0) | ghost | (highlight ? BatchKind.Highlight : 0)
            | (cut ? BatchKind.Cut : 0));
        batch.Manual = manual is not null;
        if (batch.Count == 0)
        {
            _active.Add(batch);
            batch.Near = near;
        }
        else if (near < batch.Near) batch.Near = near;
        m.M14 = lo;
        m.M24 = hi;
        batch.Add(m);
    }

    /// <summary>
    /// The batch for (mesh, materials, level, kind flags), made on first use with the flags decoded into its fields.
    /// </summary>
    Batch BatchFor(GpuObjectMesh gpu, ObjectMaterialSet materials, int level, int kind)
    {
        var key = (gpu, materials, level, kind);
        if (!_batchMap.TryGetValue(key, out var batch))
        {
            _batchMap[key] = batch = new Batch
            {
                Mesh = gpu,
                Materials = materials,
                Level = level,
                Town = kind == BatchKind.Town,
                Fading = (kind & ~(BatchKind.BackCull | BatchKind.Ghost | BatchKind.GhostBad | BatchKind.GhostQueued | BatchKind.Highlight
                    | BatchKind.Cut)) != BatchKind.Solid,
                BackCull = (kind & BatchKind.BackCull) != 0,
                Highlight = (kind & BatchKind.Highlight) != 0,
                Cut = (kind & BatchKind.Cut) != 0,
                Ghost = (kind & BatchKind.GhostQueued) != 0 ? 3 : (kind & BatchKind.GhostBad) != 0 ? 2 : (kind & BatchKind.Ghost) != 0 ? 1 : 0
            };
        }
        return batch;
    }

    /// <summary>
    /// Whether a sphere is inside or crossing every frustum plane (the planes' normals need not be unit length).
    /// </summary>
    static bool SphereVisible(Vector4[] planes, Vector3 centre, float radius)
    {
        foreach (var p in planes)
            if (p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z + p.W < -radius * MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z)) return false;
        return true;
    }

    /// <summary>
    /// One batch of instances: the same (mesh, materials, LOD level), drawn with one call per part.
    /// </summary>
    sealed class Batch
    {
        /// <summary>
        /// The mesh drawn: a manual LOD level's own mesh when the level is manual.
        /// </summary>
        public required GpuObjectMesh Mesh;

        /// <summary>
        /// One material per part of <see cref="Mesh"/>.
        /// </summary>
        public required ObjectMaterialSet Materials;

        /// <summary>
        /// The LOD level drawn (0 for a manual level's own mesh).
        /// </summary>
        public required int Level;

        /// <summary>
        /// A distant town's batch (<see cref="BatchKind.Town"/>): the fade comes per vertex.
        /// </summary>
        public required bool Town;

        /// <summary>
        /// Back faces culled: the instances are <c>interior</c> parts (docs/formats/zones.md "Interior view"; the game culls building parts' back
        /// faces, so a room's ceiling vanishes seen from above).
        /// </summary>
        public bool BackCull;

        /// <summary>
        /// The placement ghost: 0 not, 1 valid (green), 2 invalid (red), 3 queued (blue) (<see cref="BatchKind.Ghost"/>).
        /// </summary>
        public int Ghost;

        /// <summary>
        /// Drawn plain yellow: a sign's part while <see cref="HighlightSigns"/>.
        /// </summary>
        public bool Highlight;

        /// <summary>
        /// The level is a manual LOD level's own mesh (the LOD debug colours it magenta).
        /// </summary>
        public bool Manual;

        /// <summary>
        /// Some instance is mid cross-fade (or a town's per-vertex fade): its draws need the program with the dither <c>discard</c>.
        /// </summary>
        public bool Fading;

        /// <summary>
        /// Map features overlapping an interior mask (<see cref="InteriorCut"/>): drawn with the cut (the full program, push <c>fadeMode</c> bit 2),
        /// never solid.
        /// </summary>
        public bool Cut;

        /// <summary>
        /// The instances' matrices (M14/M24 the dither range, M34 and M44 spare data, see
        /// <see cref="Emit(ObjectStreamer.Instance, GpuObjectMesh, float, float, float)"/>), the first <see cref="Count"/> used; grows by doubling.
        /// </summary>
        public Matrix4x4[] Data = new Matrix4x4[16];

        /// <summary>
        /// The instances in <see cref="Data"/> this pass, and where the batch's matrices start in the frame's instance
        /// constants (<see cref="UploadInstances"/>).
        /// </summary>
        public int Count, Offset;

        /// <summary>
        /// The nearest instance's distance (to its bounds' edge) this frame: batches are drawn nearest first.
        /// </summary>
        public float Near;

        /// <summary>
        /// Appends an instance's matrix, doubling <see cref="Data"/> when full.
        /// </summary>
        public void Add(in Matrix4x4 m)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = m;
        }
    }

    /// <summary>
    /// What a batch's instances share besides mesh, materials and level: all fully visible (their draws can use the programs without a
    /// <c>discard</c>, see <see cref="EarlyDepth"/>), some dithered by the LOD cross-fade, or a distant town (the fade comes per vertex).
    /// </summary>
    static class BatchKind
    {
        /// <summary>
        /// The batch kinds: all fully visible, some dithered by the LOD cross-fade, a distant town.
        /// </summary>
        public const int Solid = 0, Fading = 1, Town = 2;

        /// <summary>
        /// A flag on Solid or Fading: <see cref="Batch.BackCull"/>.
        /// </summary>
        public const int BackCull = 4;

        /// <summary>
        /// Flags: the placement ghost, valid or invalid (<see cref="Batch.Ghost"/>).
        /// </summary>
        public const int Ghost = 8, GhostBad = 16, GhostQueued = 32;

        /// <summary>
        /// A flag: <see cref="Batch.Highlight"/>.
        /// </summary>
        public const int Highlight = 64;

        /// <summary>
        /// A flag: <see cref="Batch.Cut"/>.
        /// </summary>
        public const int Cut = 128;
    }

    #endregion

    #region Interior cut

    /// <summary>
    /// The viewed buildings' hulls a shadow pass cuts (<see cref="ShadowCut"/>).
    /// </summary>
    readonly List<InteriorVolume> _shadowCuts = [];

    /// <summary>
    /// The shadow cut's hulls for the depth program's view block, made by <see cref="ShadowCut"/> at each depth pass's start.
    /// </summary>
    ViewConstants _cutConstants;

    /// <summary>
    /// Leave out other buildings' parts inside a building in the interior view (Fixes settings, on by default; docs/render-interior-cut.md "Meitou"):
    /// the Hub's town wall runs into the Shinobi Thieves tower (issue #111). Not the parts of a building with a drawn mask (the viewed one's own
    /// floors and interior), nor doors, layout objects or buildings placed inside another.
    /// </summary>
    public bool CutIntoViewed { get; set; } = true;

    /// <summary>
    /// The interior masks last drawn (the main view's), whose owners a shadow pass leaves uncut as the colour pass does.
    /// </summary>
    internal InteriorMaskRenderer? Masks { get; set; }

    /// <summary>
    /// Whether a building part takes the cut over the mask of a building in the interior view (<see cref="CutIntoViewed"/>): it overlaps one of those
    /// masks and its own building drew none.
    /// </summary>
    bool CutsIntoViewed(ObjectStreamer.Instance inst, InteriorMaskRenderer mask) =>
        CutIntoViewed && InteriorView.Count > 0 && Cuttable(inst) && mask.Overlaps(inst.Centre, inst.Radius, InteriorView)
        && !mask.Drew(inst.Placed.PlacementId);

    /// <summary>
    /// A part the cut may leave out: a building's own part, not a door, layout object, contained building or distant stand-in.
    /// </summary>
    bool Cuttable(ObjectStreamer.Instance inst) =>
        !inst.Stand && inst.Placed is { Kind: PlacedKind.BuildingPart, DoorIndex: < 0, ParentPlacementId: null, Contained: false };

    /// <summary>
    /// Whether a building part casts nothing inside the viewed buildings' hulls in a shadow pass: cuttable, not itself viewed or drawn with a mask,
    /// and its bounds reach a hull.
    /// </summary>
    bool CutsShadow(ObjectStreamer.Instance inst)
    {
        if (!Cuttable(inst) || InteriorView.ContainsKey(inst.Placed.PlacementId) || Masks?.Drew(inst.Placed.PlacementId) == true) return false;
        foreach (var v in _shadowCuts)
            if (v.Distance(inst.Centre) <= inst.Radius) return true;
        return false;
    }

    /// <summary>
    /// The shadow side of <see cref="CutIntoViewed"/>: the hulls (convex footprint, height range) of the buildings in the interior view, into
    /// <paramref name="view"/> for the depth program, up to 4 buildings and 32 edges in all; a hull that does not fit is left uncut.
    /// </summary>
    void ShadowCut(ref ViewConstants view)
    {
        _shadowCuts.Clear();
        view.CutVolumes = 0;
        if (!CutIntoViewed || InteriorView.Count == 0) return;
        int planes = 0;
        foreach (var id in InteriorView.Keys)
        {
            if (_shadowCuts.Count == 4) break;
            if (InteriorFor(id) is not { Footprint: { Length: >= 3 } f } v || planes + f.Length > 32) continue;
            view.CutRanges[_shadowCuts.Count] = new Vector4(planes, f.Length, v.YMin, v.YMax);
            for (int i = 0; i < f.Length; i++)
            {
                var e = f[(i + 1) % f.Length] - f[i];
                // counter-clockwise: inside on the left
                var n = e.LengthSquared() > 1e-8f ? Vector2.Normalize(new Vector2(-e.Y, e.X)) : Vector2.Zero;
                view.CutPlanes[planes++] = new Vector4(n, Vector2.Dot(n, f[i]), 0);
            }
            _shadowCuts.Add(v);
        }
        view.CutVolumes = (uint)_shadowCuts.Count;
    }

    #endregion

    #region Native recording

    // docs/renderer-native.md 3.3 and 7.5, step O: native-model programs (BuildingLodShaders' native variants: the same maths, the values in
    // FrameConstants / ViewConstants / push constants, the textures by bindless index), recorded into the pass VkGl has open
    // (BeginNativeInPass). Draw is split into Prepare (cull, levels, batches, then PrepareDraws: the texture keys through WorldTexture.Key,
    // which can start a reload, and the draw list) and Record (the list into one native segment: the sets and dynamic state once, per draw a
    // push of the changed bytes and the part's own buffers). Since phase 8 stage 1 the meshes and textures are native (ObjectMeshCache,
    // WorldTextureCache): the vertex layout is made once per part, the bindless index kept on the texture per LOD bias. Nothing but commands in
    // the loop, no hidden allocation.

    /// <summary>
    /// The bytes per instance in the frame's constants: one 4x4 float matrix.
    /// </summary>
    const int InstanceStride = 64;

    /// <summary>
    /// The first location the per-instance rows (the batch matrices) take; the mesh's own inputs are below it.
    /// </summary>
    const int RowLocation = BuildingLodShaders.InstanceLocation;

    /// <summary>
    /// This call's batch matrices in the frame's constants (a batch at its <see cref="Batch.Offset"/>; the draws reach it by firstInstance).
    /// </summary>
    Transient _instances;

    /// <summary>
    /// The draw list <see cref="PrepareDraws"/> makes (grows by doubling).
    /// </summary>
    ObjDraw[] _draws = new ObjDraw[256];

    /// <summary>
    /// The draws in <see cref="_draws"/> this pass.
    /// </summary>
    int _drawCount;

    /// <summary>
    /// The segment ids by (segment kind, pipeline state).
    /// </summary>
    readonly Dictionary<(int Kind, SegmentPipeline State), int> _segmentByState = [];

    /// <summary>
    /// The last state per segment kind (0 colour, 1 depth, 2 colour wireframe, 3 depth wireframe, 4 solid colour), for the quick compare.
    /// </summary>
    readonly SegmentPipeline[] _lastSegment = new SegmentPipeline[5];

    /// <summary>
    /// The id of <see cref="_lastSegment"/> per kind (0: none yet).
    /// </summary>
    readonly int[] _segmentIds = new int[5];

    /// <summary>
    /// The segment ids handed out so far.
    /// </summary>
    int _segmentCount;

    /// <summary>
    /// The segment's LOD bias (the upscaler's, <see cref="IGpuContext.LodBias"/>) and GL's stand-in for a texture not resident.
    /// </summary>
    float _textureBias;

    /// <summary>
    /// The stand-in's bindless index (<see cref="IGpuContext.StandIn2D"/>) in this segment.
    /// </summary>
    uint _textureStandIn;

    /// <summary>
    /// Scratch for the reorder of <see cref="SolidDrawsFirst"/>, as long as <see cref="_draws"/> when used.
    /// </summary>
    ObjDraw[] _drawScratch = [];

    /// <summary>
    /// The pooled segment jobs; a job returns itself once recorded.
    /// </summary>
    readonly JobPool<DrawJob> _drawJobs = new();

    /// <summary>
    /// On by default (the <c>solid-first</c> A/B turns it off): see <see cref="SolidDrawsFirst"/>.
    /// </summary>
    public static bool SolidFirst { get; set; } = true;

    /// <summary>
    /// The batches' parts as draws, in order, with the texture keys (reading a key counts as use: here, not in Record).
    /// </summary>
    void PrepareDraws(WorldRenderOptions options, bool wire)
    {
        _drawCount = 0;
        foreach (var b in _active)
        {
            int fade = b.Town ? 1 : 0;
            for (int i = 0; i < b.Mesh.Parts.Count; i++)
            {
                var gp = b.Mesh.Parts[i];
                if (gp.Count[b.Level] == 0) continue;
                if (_drawCount == _draws.Length) Array.Resize(ref _draws, _drawCount * 2);
                ref var d = ref _draws[_drawCount++];
                d = default;
                d.Part = gp;
                d.Level = b.Level;
                d.FirstInstance = (uint)b.Offset;
                d.Instances = (uint)b.Count;
                d.Push.TriplanarScale = 1f / 5000;
                d.Push.FadeMode = fade | (b.Cut ? 2 : 0);
                if (wire || _debugLevels == 1)
                {
                    d.Push.Wireframe = 1;
                    d.Push.FlatColour = LevelColour(b.Level, b.Manual, ReferenceEquals(b.Materials, _distantMaterial));
                }
                else
                {
                    var pm = b.Materials.Parts[Math.Min(i, b.Materials.Parts.Length - 1)];
                    Material(ref d, gp, pm, options);

                    // The placement ghost: the shader tints a draw whose flat colour is non-zero.
                    if (b.Ghost != 0) d.Push.FlatColour = b.Ghost == 1 ? new Vector3(0.35f, 1f, 0.45f) : b.Ghost == 2 ? new Vector3(1f, 0.3f,
                            0.25f) : new Vector3(0.5f, 0.7f, 1f);
                    if (b.Highlight) d.Push.FlatColour = new Vector3(1f, 1f, 0f);

                    // A part whose diffuse map is not resident yet is drawn plain grey (pop-in the benchmark counts).
                    if (!_depthPass && options.Textures && d.Diffuse == 0 && pm.WantsDiffuse) _untexturedDraws++;
                    d.Solid = !_depthPass && !b.Fading && !b.Cut && d.Push.AlphaThreshold == 0f;
                    d.BackCull = b.BackCull && pm.Material is not { DoubleSided: true };
                }
                DrawCalls++;
                DrawnTriangles += (long)gp.Count[b.Level] / 3 * b.Count;
            }
        }
        if (SolidFirst && !_depthPass && !wire) SolidDrawsFirst();
    }

    /// <summary>
    /// The solid draws (early depth test) ahead of the dithered and cut-out ones, each group still nearest first: what they write to the depth
    /// buffer is there for the coarse depth test of the others, whose fragments are shaded before the exact test.
    /// </summary>
    void SolidDrawsFirst()
    {
        if (_drawScratch.Length < _drawCount) _drawScratch = new ObjDraw[_draws.Length];
        int solid = 0;
        for (int i = 0; i < _drawCount; i++) if (_draws[i].Solid) solid++;
        if (solid == 0 || solid == _drawCount) return;
        int s = 0, o = solid;
        for (int i = 0; i < _drawCount; i++) _drawScratch[_draws[i].Solid ? s++ : o++] = _draws[i];
        Array.Copy(_drawScratch, _draws, _drawCount);
    }

    /// <summary>
    /// A part's material into a draw: the texture keys the options and the part's vertex streams allow, and the push block's material fields.
    /// </summary>
    static void Material(ref ObjDraw d, GpuObjectPart gp, ObjectPartMaterial pm, WorldRenderOptions options)
    {
        var m = pm.Material;
        uint diffuse = pm.Diffuse;
        bool textured = options.Textures && diffuse != 0;
        uint normalId = textured ? pm.Normal : 0;
        bool normal = options.NormalMaps && textured && normalId != 0 && gp.HasTangents;
        uint diffuse2 = textured ? pm.Diffuse2 : 0;
        bool dual = textured && diffuse2 != 0 && gp.HasColours;
        d.Diffuse = textured ? diffuse : 0;
        d.Normal = normalId;
        d.Diffuse2 = dual ? diffuse2 : 0;
        d.Normal2 = dual ? pm.Normal2 : 0;
        var alpha = m?.Alpha ?? AlphaSource.None;
        if (!textured || alpha == AlphaSource.NormalAlpha && normalId == 0) alpha = AlphaSource.None;
        var tile = m?.Tile ?? Vector2.One;
        ref var pc = ref d.Push;
        pc.NormalSwizzled = pm.Swizzled ? 1u : 0u;
        pc.HasDiffuse = textured ? 1u : 0u;
        pc.HasNormal = normal ? 1u : 0u;
        pc.HasDual = dual ? 1u : 0u;
        pc.Triplanar = textured && (m?.Triplanar ?? false) ? 1u : 0u;
        pc.Tile = tile;
        pc.AlphaSource = (int)alpha;
        pc.AlphaChannel = Math.Clamp(m?.AlphaChannel ?? 3, 0, 3);
        pc.GreyChannel = textured ? m?.GreyChannel ?? -1 : -1;
        pc.Tint = m?.Tint ?? Vector3.One;
        pc.AlphaThreshold = alpha == AlphaSource.None ? 0f : m!.AlphaThreshold;
        pc.Emissive = textured && normalId != 0 && (m?.Emissive ?? false) ? 1u : 0u;
        pc.UseVertexColour = gp.HasColours && (m?.VertexColours ?? false) ? 1u : 0u;
        pc.Specular = textured ? m?.SpecularMult ?? 1 : 0.3f;
        pc.Spare = (pm.Dust ? MeshSurface.Dust : 0u) | (m?.Foliage ?? false ? MeshSurface.Foliage : 0u)
            | (m?.Dust ?? false ? MeshSurface.TriplanarDust : 0u);
    }

    /// <summary>
    /// The LOD debug colour (MEITOU_LOD_DEBUG): green level 0, yellow 1, orange 2, red 3, magenta a manual level, blue a distant stand-in.
    /// </summary>
    static Vector3 LevelColour(int level, bool manual, bool stand) =>
        stand ? new Vector3(0.2f, 0.6f, 1f) : manual ? new Vector3(0.9f, 0.2f, 0.9f) : level switch
        {
            0 => new Vector3(0.1f, 0.85f, 0.2f),
            1 => new Vector3(0.95f, 0.9f, 0.1f),
            2 => new Vector3(0.95f, 0.5f, 0.1f),
            _ => new Vector3(0.95f, 0.15f, 0.1f),
        };

    /// <summary>
    /// The draw list in one native segment of VkGl's open pass (colour or depth, solid or wireframe).
    /// </summary>
    void RecordDraws(ObjProg prog, bool wire, in ViewConstants view, Vector4 fadeRange)
    {
        if (_drawCount == 0) return;
        string label = _depthPass ? "objects depth" : wire ? "objects wire" : "objects";
        int kind = (_depthPass ? 1 : 0) + (wire ? 2 : 0);

        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state, the sets, and per draw everything resolved (pipeline, buffers, the push
        // block with its bindless indices), into a job that only records.
        var targets = Gpu.CurrentTargets();

        // The objects are drawn double-sided (the GL version turned the cull face off); the wireframe as lines pulled forward
        // (GL's PolygonMode LINE with POLYGON_OFFSET_LINE and PolygonOffset(-1, -1), toward the viewer in the scene's depth convention).
        var state = Gpu.CurrentState() with { Cull = CullMode.None };
        if (wire)
        {
            var (biasConstant, biasSlope) = DepthConvention.TowardViewer(1);
            state = state with { Polygon = PolygonKind.Line, BiasEnable = true, BiasConstant = biasConstant, BiasSlope = biasSlope };
        }
        int segment = SegmentId(kind, prog.P, targets, state);

        // The fully visible draws of a colour pass use the program without a discard (own segment, own pipelines per part).
        int solidSegment = !_depthPass && !wire ? SegmentId(4, _solidProg.P, targets, state) : 0;
        var job = _drawJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program, job.Count) = (this, targets, state, prog.P, 0);
        job.Frame = _nativeFrame.Prepare(in view);
        for (int a = 0; a < 4; a++) job.Rows[a] = new GpuBufferBinding(_instances.Handle, _instances.Offset + (ulong)(16 * a));
        NewTextureSegment();
        if (job.Draws.Length < _drawCount) job.Draws = new DrawJob.Draw[Math.Max(_drawCount, job.Draws.Length * 2)];
        var list = _draws;
        for (int i = 0; i < _drawCount; i++)
        {
            ref var d = ref list[i];
            ref var pc = ref d.Push;
            pc.FadeRange = fadeRange;
            pc.Diffuse = Texture(d.Diffuse);
            pc.Normal = Texture(d.Normal);
            pc.Diffuse2 = Texture(d.Diffuse2);
            pc.Normal2 = Texture(d.Normal2);
            pc.Spare = (pc.Spare & 0xFFu) | (_scaffoldGrid is { Key: not 0 } grid ? (Texture(grid.Key) + 1) << 8 : 0);
            var part = d.Part;
            bool solid = d.Solid && solidSegment != 0;
            ref var n = ref (_depthPass ? ref part.DepthNative : ref (solid ? ref part.ColourSolidNative : ref part.ColourNative));
            var drawProg = solid ? _solidProg : prog;
            int drawSegment = solid ? solidSegment : segment;
            Current(ref n, part, drawProg);
            job.Draws[i] = new DrawJob.Draw
            {
                Pipeline = n.SegA == drawSegment ? n.PipeA!.Value : PipelineFor(ref n, drawProg, drawSegment, state, targets.Formats, label),
                Vertices = n.Vertices,
                Elements = new GpuBufferBinding(n.Elements.Buffer, n.Elements.Offset + (ulong)part.Offset[d.Level] * 4),
                Push = pc,
                IndexCount = (uint)part.Count[d.Level],
                Instances = d.Instances,
                FirstInstance = d.FirstInstance,
                Cull = d.BackCull ? CullMode.Back : state.Cull,
            };
        }
        job.Count = _drawCount;
        Gpu.Record(label, job);
    }

    /// <summary>
    /// A stable number for a segment's pipeline state, so a part compares one int per draw.
    /// </summary>
    int SegmentId(int kind, GpuProgram p, PassTargets t, DrawState s)
    {
        var segment = new SegmentPipeline(p, t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp);
        if (_segmentIds[kind] != 0 && segment == _lastSegment[kind]) return _segmentIds[kind];
        if (!_segmentByState.TryGetValue((kind, segment), out int id)) _segmentByState[(kind, segment)] = id = ++_segmentCount;
        _lastSegment[kind] = segment;
        return _segmentIds[kind] = id;
    }

    /// <summary>
    /// A part's native state for <paramref name="p"/>, made on first use from its native buffers (they never change while the part
    /// lives). The batch matrices are four per-instance rows of 64 bytes at locations 7 to 10 (bound once per segment).
    /// </summary>
    static void Current(ref ObjectNativeMesh n, GpuObjectPart part, ObjProg p)
    {
        if (n.Layout is not null) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        part.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[RowLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32F, InstanceStride, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = new GpuBufferBinding(part.Indices.Handle, 0, part.Indices.Size);
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    /// <summary>
    /// The part's pipeline for a segment, made on a miss: the part keeps its last two (A the newest, B the one before).
    /// </summary>
    GpuPipeline PipelineFor(ref ObjectNativeMesh n, ObjProg p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegB == segment) return n.PipeB!.Value;
        var pipeline = Gpu.Pipelines.Get(state.Pipeline(p.P, n.Layout!, PrimitiveType.Triangles, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    /// <summary>
    /// A new segment: the bias in effect now and the stand-in's bindless index (the 2D float array the shaders index).
    /// </summary>
    void NewTextureSegment()
    {
        _textureBias = Gpu.LodBias;
        _textureStandIn = Gpu.StandIn2D;
    }

    /// <summary>
    /// The bindless index of a texture by its <see cref="WorldTexture.Key"/> (0: the stand-in), as the GL texture was sampled.
    /// </summary>
    uint Texture(uint key) => key == 0 ? _textureStandIn : _textureCache.Index(key, _textureBias, _textureStandIn);

    /// <summary>
    /// One part draw, in the renderer-owned list: the part, its level and instance range, the texture keys (their bindless indices
    /// are patched into <see cref="Push"/> per segment) and the push constants it needs.
    /// </summary>
    struct ObjDraw
    {
        /// <summary>
        /// The part drawn.
        /// </summary>
        public GpuObjectPart Part;

        /// <summary>
        /// The part's LOD level (its index range).
        /// </summary>
        public int Level;

        /// <summary>
        /// The batch's first instance in the instance constants, and its instance count.
        /// </summary>
        public uint FirstInstance, Instances;

        /// <summary>
        /// The texture keys (<see cref="WorldTexture.Key"/>, 0: none) of the diffuse and normal maps and of the second set; <see cref="RecordDraws"/>
        /// turns them into bindless indices.
        /// </summary>
        public uint Diffuse, Normal, Diffuse2, Normal2;

        /// <summary>
        /// The push constants (the texture indices patched in when recorded).
        /// </summary>
        public ObjectPush Push;

        /// <summary>
        /// Drawn with the program without a discard (<see cref="EarlyDepth"/>): a batch of fully visible instances, a material without a cut-out.
        /// </summary>
        public bool Solid;

        /// <summary>
        /// Back faces culled (<see cref="Batch.BackCull"/>, not for a double-sided material).
        /// </summary>
        public bool BackCull;
    }

    /// <summary>
    /// A native program of the objects with its vertex inputs from the reflection: the layout and buffers a GL vertex array feeds it, as VkGl feeds a
    /// GL program (<see cref="LegacyProgram.VertexLayout"/>: a disabled attribute reads GL's constant through a stride-0 binding).
    /// </summary>
    sealed class ObjProg : IDisposable
    {
        /// <summary>
        /// The context whose shaders and pipelines hold the program.
        /// </summary>
        readonly IGpuContext _ctx;

        /// <summary>
        /// The program.
        /// </summary>
        public readonly GpuProgram P;

        /// <summary>
        /// The vertex stage's input locations, one per slot (a matrix input takes several).
        /// </summary>
        readonly int[] _locations;

        /// <summary>
        /// The scalar kind of each entry of <see cref="_locations"/>.
        /// </summary>
        readonly Meitou.Game.Rendering.Gpu.Shaders.ScalarKind[] _kinds;

        /// <summary>
        /// The program's own input locations (below the instance rows): 0 .. Own − 1.
        /// </summary>
        public readonly int Own;

        /// <summary>
        /// The last layout made, returned again while the inputs are equal.
        /// </summary>
        VertexLayout? _last;

        /// <summary>
        /// Makes the program in <paramref name="frame"/> and reads its vertex inputs from the reflection.
        /// </summary>
        public ObjProg(IGpuContext ctx, NativeFrame frame, string vertex, string fragment, string name)
        {
            this._ctx = ctx;
            P = frame.Program(vertex, fragment, name);
            var inputs = P.Info.VertexReflection!.Inputs;
            _locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
            _kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
            Own = _locations.Where(l => l < RowLocation).DefaultIfEmpty(-1).Max() + 1;
        }

        /// <summary>
        /// The vertex layout for the attributes by location; a missing one reads GL's constant (a stand-in format through a stride-0 binding, or 16
        /// without constant attributes). The last layout when the inputs are equal.
        /// </summary>
        public VertexLayout Layout(ReadOnlySpan<LegacyProgram.Attribute?> byLocation)
        {
            Span<VertexInput> inputs = stackalloc VertexInput[_locations.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                int loc = _locations[i];
                inputs[i] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? new VertexInput((uint)loc, a.Format, a.Stride, a.PerInstance)
                    : new VertexInput((uint)loc, GlConventions.DummyVertexFormat(_kinds[i]), _ctx.ConstantVertexAttributes ? 0u : 16u, false);
            }
            if (_last is { } l && inputs.SequenceEqual(l.Inputs)) return l;
            return _last = new VertexLayout(inputs.ToArray());
        }

        /// <summary>
        /// Locations 0 .. <paramref name="count"/> − 1 as one array for one bind: each attribute's buffer, GL's constant where none.
        /// </summary>
        public GpuBufferBinding[] Buffers(ReadOnlySpan<LegacyProgram.Attribute?> byLocation, int count)
        {
            var result = new GpuBufferBinding[count];
            for (int loc = 0; loc < count; loc++)
            {
                int input = Array.IndexOf(_locations, loc);
                result[loc] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? a.Buffer
                    : _ctx.ConstantVertexAttributes
                    ? new GpuBufferBinding(_ctx.DummyVertex, GlConventions.DummyVertexOffset(input >= 0 ? _kinds[input] : ScalarKind.Float))
                    : new GpuBufferBinding(_ctx.DummyVertexArray(input >= 0 ? _kinds[input] : ScalarKind.Float), 0);
            }
            return result;
        }

        /// <summary>
        /// Forgets the program's pipelines and frees it.
        /// </summary>
        public void Dispose()
        {
            _ctx.Pipelines.Forget(P);
            _ctx.Shaders.Dispose(P);
        }
    }

    /// <summary>
    /// The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).
    /// </summary>
    readonly record struct SegmentPipeline(GpuProgram Program, AttachmentFormats Formats, BlendState Blend, ColorMask Mask,
        PolygonKind Polygon, bool AlphaToCoverage, bool DepthClamp);

    /// <summary>
    /// One objects segment as prepared (<see cref="RecordDraws"/>): recorded on any thread, the same commands as before wave 4.
    /// </summary>
    sealed class DrawJob : RecordJob
    {
        /// <summary>
        /// One draw as recorded: everything resolved.
        /// </summary>
        public struct Draw
        {
            /// <summary>
            /// The part's pipeline for the segment.
            /// </summary>
            public GpuPipeline Pipeline;

            /// <summary>
            /// The part's vertex buffers by location.
            /// </summary>
            public GpuBufferBinding[] Vertices;

            /// <summary>
            /// The index buffer at the level's first index.
            /// </summary>
            public GpuBufferBinding Elements;

            /// <summary>
            /// The push constants, texture indices included.
            /// </summary>
            public ObjectPush Push;

            /// <summary>
            /// The level's index count, and the batch's instance count and first instance.
            /// </summary>
            public uint IndexCount, Instances, FirstInstance;

            /// <summary>
            /// The face culling (the segment's, or back faces for an interior part).
            /// </summary>
            public CullMode Cull;
        }

        /// <summary>
        /// The renderer whose pool the job goes back to.
        /// </summary>
        public WorldObjectRenderer Owner = null!;

        /// <summary>
        /// The pass's targets (viewport, scissor).
        /// </summary>
        public PassTargets Targets = null!;

        /// <summary>
        /// The pass's draw state (raster, depth, bias).
        /// </summary>
        public DrawState State = null!;

        /// <summary>
        /// The program the push constants and the frame sets are for.
        /// </summary>
        public GpuProgram Program = default;

        /// <summary>
        /// The frame and view constants' binding (<see cref="NativeFrame.Prepare"/>).
        /// </summary>
        public FrameBinding Frame;

        /// <summary>
        /// The four instance-row bindings: the batch matrices' rows, 16 bytes apart.
        /// </summary>
        public readonly GpuBufferBinding[] Rows = new GpuBufferBinding[4];

        /// <summary>
        /// The draws (grown by <see cref="RecordDraws"/>).
        /// </summary>
        public Draw[] Draws = new Draw[64];

        /// <summary>
        /// The draws in <see cref="Draws"/>.
        /// </summary>
        public int Count;
        /// <inheritdoc/>
        public override int Size => Count;

        public override void Record(IGpuCommandList cmd)
        {
            var (targets, state) = (Targets, State);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetRaster(state.Cull, state.Front);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, Program, in Frame);
            cmd.BindVertexBuffers(RowLocation, Rows);
            ObjectPush last = default;
            bool pushed = false;
            var cull = state.Cull;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                cmd.BindPipeline(d.Pipeline);
                if (d.Cull != cull) cmd.SetRaster(cull = d.Cull, state.Front);
                cmd.BindVertexBuffers(0, d.Vertices);
                cmd.BindIndexBuffer(d.Elements, IndexKind.U32);
                if (!pushed || !System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<ObjectPush>(in d.Push))
                    .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<ObjectPush>(in last))))
                {
                    cmd.PushConstants(Program, GpuStage.Vertex | GpuStage.Fragment, in d.Push);
                    last = d.Push;
                    pushed = true;
                }
                cmd.DrawIndexed(d.IndexCount, d.Instances, 0, 0, d.FirstInstance);
            }
        }

        public override void Release()
        {
            Array.Clear(Draws, 0, Count);
            Count = 0;
            Owner._drawJobs.Return(this);
        }
    }

    #endregion

    #region Depth only (the sun's shadow map, ShadowPass)

    /// <summary>
    /// <see cref="Draw"/> is drawing a depth pass (<see cref="DrawDepth"/>): the depth program, no ghosts, the shadow cut.
    /// </summary>
    bool _depthPass;

    /// <summary>
    /// The least LOD value a depth draw takes (<see cref="ShadowLod.ObjectLodFloor"/>): in a far cascade an object is drawn at the level the game
    /// picks that far away, never a finer one. 0: the distance rule alone.
    /// </summary>
    float _shadowLodFloor;

    /// <summary>
    /// <see cref="Draw"/> draws the landmarks alone (<see cref="DrawLandmarksDepth"/>).
    /// </summary>
    bool _landmarksOnly;

    /// <summary>
    /// Draws the objects' depth for a shadow cascade: <see cref="Draw"/>'s culling, levels and batches (chosen by the camera's
    /// <paramref name="eye"/>) with <see cref="ShadowShaders.MeshDepthFragment"/> (the materials' cut-outs and the caster bias), and the TERRAIN-mode
    /// meshes through the terrain's depth path. <paramref name="coarseTexel"/>: the texel of a far cascade (0: not coarsened), which sets the finest
    /// level an instance takes (<see cref="ShadowLod"/>). Leaves the draw counters describing this call.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain,
        float coarseTexel = 0)
    {
        _depthPass = true;
        _shadowLodFloor = ShadowLod.ObjectLodFloor(coarseTexel, options.TerrainPixelScale);
        try { Draw(viewProjection, eye, frustum, options, Vector3.UnitY, Vector3.Zero, 0, terrain); }
        finally
        {
            _depthPass = false;
            _shadowLodFloor = 0;
        }
    }

    /// <summary>
    /// <see cref="DrawDepth"/> for the landmarks alone (the landmark shadow map, ShadowPass.Meitou): the same reach, levels and batches.
    /// </summary>
    public void DrawLandmarksDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain)
    {
        _landmarksOnly = true;
        try { DrawDepth(viewProjection, eye, frustum, options, terrain); }
        finally { _landmarksOnly = false; }
    }

    /// <summary>
    /// The landmarks <see cref="Draw"/> would draw from <paramref name="eye"/> looking any way (resolved, within the landmark reach and their part
    /// distance): their bounding spheres (xyz centre, w radius) into <paramref name="spheres"/>. Returns a key that changes when that set does.
    /// </summary>
    public long LandmarkCasters(Vector3 eye, List<Vector4> spheres)
    {
        spheres.Clear();
        if (_streamer.LandmarkZone is not { } zone) return 0;
        float reach = LandmarkReach;
        long key = 17;
        for (int i = 0; i < zone.Real.Count; i++)
        {
            var inst = zone.Real[i];
            if (inst.Gpu is not { } gpu || !ReferenceEquals(inst.Mesh.Gpu, gpu)) continue;
            if (Vector3.Distance(inst.Centre, eye) - inst.Radius >= Math.Min(reach, inst.Limit)) continue;
            spheres.Add(new Vector4(inst.Centre, inst.Radius));
            key = key * 31 + i + 1;
        }
        return key;
    }

    #endregion
}