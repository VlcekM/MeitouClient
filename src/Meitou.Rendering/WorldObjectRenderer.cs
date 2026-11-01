using System.Diagnostics;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using SamplerInfo = Meitou.Rendering.Gpu.Shaders.SamplerInfo;

namespace Meitou.Rendering;

/// <summary>
/// Draws the world's placed objects (docs/render-objects.md): buildings, map features and distant towns.
/// <list type="bullet">
/// <item>Zones around the camera are laid out on worker threads (<see cref="ObjectStreamer"/>); meshes decode on workers and upload in
/// steps (<see cref="ObjectMeshCache"/>); textures decode on workers (<see cref="WorldTextureCache"/>). <see cref="Update"/> drives it all.</item>
/// <item>Each instance picks a mesh LOD level by Kenshi's rule (<see cref="MeshLod"/>) and fades between levels and at the draw
/// distance with a dithered cross-fade; instances of one (mesh, material set, level) are drawn with one instanced call per part.</item>
/// <item>Towns with a baked distant mesh, and buildings' own distant meshes elsewhere, take over beyond <see cref="ObjectDistance"/>.</item>
/// <item>TERRAIN-mode map features go through the terrain shader (<see cref="TerrainRenderer.DrawMeshes"/>), one draw each, at their LOD level.</item>
/// </list>
/// <see cref="Draw"/> can be called with any camera (e.g. the water reflection's) between <see cref="Update"/> calls.
/// </summary>
public sealed unsafe partial class WorldObjectRenderer : IDisposable
{
    /// <summary>The native GPU API (docs/renderer-native.md 7.1 step 8; since phase 8 stage 1 the only one this renderer uses).</summary>
    public IGpuContext Gpu { get; }
    readonly NativeFrame nativeFrame;
    readonly ObjProg colourProg, solidProg, depthProg;   // the colour, the colour without a discard (EarlyDepth) and the depth (shadow caster) programs, native
    readonly WorldTextureCache textureCache;
    readonly MaterialResolver resolver;
    /// <summary>Finds the textures of a mesh's parts (the <see cref="DebrisRenderer"/> shares it).</summary>
    public MaterialResolver Resolver => resolver;
    readonly UploadQueue uploads = new();
    readonly ObjectMeshCache meshes;
    readonly ObjectStreamer streamer;
    readonly WorldObjects objects;
    readonly Dictionary<(string, string, string), ObjectMaterialSet> materialSets = [];
    readonly List<TownDraw> towns = [];
    readonly ObjectMaterialSet distantMaterial;
    readonly Dictionary<(GpuObjectMesh, ObjectMaterialSet, int, int), Batch> batchMap = [];
    readonly List<Batch> active = [];
    readonly List<(MeshBindings, int, Matrix4x4)> terrainMeshes = [];   // the TERRAIN-mode instances of a draw (TerrainRenderer.DrawMeshes)
    readonly List<(MeshBindings, int, Matrix4x4)> terrainMeshesCut = [];   // those over an interior mask (InteriorCut)
    /// <summary>MEITOU_LOD_DEBUG: 1 colours solid surfaces by LOD level, 2 draws wireframe only coloured by level (green 0, yellow 1, orange 2, red 3, magenta manual, blue distant stand-ins).</summary>
    readonly int debugLevels = int.TryParse(Environment.GetEnvironmentVariable("MEITOU_LOD_DEBUG"), out int dl) ? dl : 0;

    /// <summary>One batch of instances: the same (mesh, materials, LOD level), drawn with one call per part.</summary>
    sealed class Batch
    {
        public required GpuObjectMesh Mesh;
        public required ObjectMaterialSet Materials;
        public required int Level;
        public required bool Town;
        /// <summary>Back faces culled: the instances are <c>interior</c> parts (docs/formats/zones.md "Interior view"; the game culls building parts' back faces, so a room's ceiling vanishes seen from above).</summary>
        public bool BackCull;
        /// <summary>The placement ghost: 0 not, 1 valid (green), 2 invalid (red), 3 queued (blue) (<see cref="BatchKind.Ghost"/>).</summary>
        public int Ghost;
        public bool Highlight;
        public bool Manual;
        /// <summary>Some instance is mid cross-fade (or a town's per-vertex fade): its draws need the program with the dither <c>discard</c>.</summary>
        public bool Fading;
        /// <summary>Map features overlapping an interior mask (<see cref="InteriorCut"/>): drawn with the cut (the full program, push <c>fadeMode</c> bit 2), never solid.</summary>
        public bool Cut;
        public Matrix4x4[] Data = new Matrix4x4[16];
        public int Count, Offset;
        /// <summary>The nearest instance's distance (to its bounds' edge) this frame: batches are drawn nearest first.</summary>
        public float Near;

        public void Add(in Matrix4x4 m)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = m;
        }
    }

    sealed class TownDraw
    {
        public required DistantTown Town;
        public required ObjectMesh Mesh;
        public Vector3 Centre;
        public float Radius = 2500;
        public bool Resolved;
    }

    /// <summary>The landmarks being collected (all zones laid out once on a worker, about a second), the class that tells them, and what they cost.</summary>
    Task<List<(PlacedMesh Placed, float Radius)>>? landmarkTask;
    readonly LandmarkClass? landmarkClass;
    double landmarkMs;

    /// <param name="landmarks">Collect the world's landmarks (<see cref="LandmarkClass"/>) and keep them apart from the zones, to be drawn to <see cref="LandmarkDistance"/>.
    /// Without it (Faithful) every placement stays in its zone and the object distance rules all.</param>
    internal WorldObjectRenderer(IGpuContext gpu, AssetLocator assets, WorldObjects objects, bool landmarks = false)
    {
        Gpu = gpu;
        this.assets = assets;
        this.objects = objects;
        // Both native programs are made here with every handle resolved, never inside a draw (the GL programs they replace are not made).
        nativeFrame = new NativeFrame(gpu);
        colourProg = new ObjProg(gpu, nativeFrame, BuildingLodShaders.VertexNative(), BuildingLodShaders.FragmentNative(), "objects");
        solidProg = new ObjProg(gpu, nativeFrame, BuildingLodShaders.VertexNative(), BuildingLodShaders.FragmentNative(solid: true), "objects solid");
        depthProg = new ObjProg(gpu, nativeFrame, BuildingLodShaders.VertexNative(), BuildingLodShaders.DepthNative(), "objects depth");
        textureCache = new WorldTextureCache(gpu, assets, "object textures");
        var library = OgreMaterialLibrary.LoadConfigured(objects.Install, out _);
        resolver = new MaterialResolver(objects.Database, library, assets);
        meshes = new ObjectMeshCache(gpu, assets, uploads);
        meshes.Unloaded = gpu => { unloadedMeshes.Add(gpu); RemoveBatches(gpu); };
        streamer = new ObjectStreamer(objects, meshes);
        if (landmarks)
        {
            landmarkClass = new LandmarkClass(assets);
            streamer.WaitForLandmarks = true;
            var collectWatch = Stopwatch.StartNew();
            landmarkTask = Task.Run(() => { var found = landmarkClass.Collect(objects); landmarkMs = collectWatch.Elapsed.TotalMilliseconds; return found; });
        }
        var distant = new SurfaceMaterial { Description = "DistantTown (vertex colour x texture x 1.5)", Diffuse = DistantTowns.DiffuseTexture, VertexColours = true, SpecularMult = 0 };
        var distantDiffuse = textureCache.Get(distant.Diffuse, false);
        if (distantDiffuse is not null) distantDiffuse.KeepAllMips = true;   // its coordinates are the towns' own, not the mesh's: mip streaming leaves it
        distantMaterial = new ObjectMaterialSet([new ObjectPartMaterial(distant, distantDiffuse, null, null, null)]);
        scaffoldGrid = textureCache.Get(ScaffoldTexture, false);
        if (scaffoldGrid is not null) scaffoldGrid.KeepAllMips = true;   // drawn at every site, at its own tiling
        foreach (var t in objects.DistantTowns)
            towns.Add(new TownDraw { Town = t, Mesh = meshes.Get(t.MeshPath, distant: true), Centre = t.Position });
    }

    /// <summary>Interactive use (non-zero): bounded upload work per frame. 0 (screenshots): no limit.</summary>
    public int LoadBudget { get; set; }

    /// <summary>Instances drawn by the last <see cref="Draw"/>.</summary>
    public int DrawnInstances { get; private set; }
    /// <summary>Colour real building parts owned by a BUILDING with <c>is sign</c> yellow while the highlight action is held.</summary>
    public bool HighlightSigns { get; set; }

    /// <summary>Whether a placement's EMISSIVE parts are dark now (its lights are off; <see cref="WorldLamps.EmissiveDark"/>); null: every one glows.</summary>
    public Func<string, bool>? EmissiveDark { get; set; }
    public long DrawnTriangles { get; private set; }
    public int DrawCalls { get; private set; }
    /// <summary>Real instances the last <see cref="Draw"/> left out because of their part distance (<see cref="ObjectRanges.PartRenderingDistance"/>)
    /// although within the object distance (in or out of view; a benchmark statistic).</summary>
    public int PartLimited { get; private set; }

    /// <summary>Sums over every <see cref="Draw"/> since the start, [0, ...] colour (scene slices and reflection), [1, ...] depth (shadow cascades):
    /// instances, triangles, draw calls, part-limited instances, calls of Draw (a benchmark statistic; the caller divides by its frames).</summary>
    public readonly long[,] Totals = new long[2, 5];

    /// <summary>A benchmark statistic: the resolved real instances' bounding radii (world units, scaled) as percentiles, and how many have the
    /// part distance of <see cref="ObjectRanges.ObjectsViewRange"/> (small building parts) rather than none.</summary>
    public string SizeSurvey()
    {
        var radii = streamer.AllZones.SelectMany(z => z.Real).Where(i => i.Gpu is not null).Select(i => i.Radius).Order().ToArray();
        if (radii.Length == 0) return "no resolved instances";
        int limited = streamer.AllZones.SelectMany(z => z.Real).Count(i => i.Gpu is not null && i.Limit < float.MaxValue / 2);
        float P(double q) => radii[Math.Min((int)(radii.Length * q), radii.Length - 1)];
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{radii.Length} resolved real instances, radius p10 {P(0.1):0} / p25 {P(0.25):0} / p50 {P(0.5):0} / p75 {P(0.75):0} / p90 {P(0.9):0} / max {radii[^1]:0}; {limited} with a part distance (<= {ObjectRanges.ObjectsViewRange:0} units)");
    }
    public double LastDrawCpuMs { get; private set; }
    public double LastUpdateMs { get; private set; }

    /// <summary>
    /// Real (full detail) objects are drawn up to this distance (<see cref="MeshLod.Value"/>-like: camera to bounds centre minus radius);
    /// buildings' parts have their own limits too (<see cref="ObjectRanges.PartRenderingDistance"/>). Beyond it distant towns take over.
    /// </summary>
    public float ObjectDistance { get; set; } = 12000;

    /// <summary>Landmarks (<see cref="LandmarkClass"/>) are drawn up to this distance instead of <see cref="ObjectDistance"/> (0: none, they use the object distance).</summary>
    public float LandmarkDistance { get; set; }

    /// <summary>Ogre's camera LOD bias as a distance factor: the LOD value is multiplied by it, so above 1 coarser levels come sooner, below 1 later.</summary>
    public float LodBias { get; set; } = 1;

    /// <summary>The main camera's pixels per radian (set each frame by the world frame): what the generated levels' distances are worked out with (<see cref="ObjectLodGen"/>); 0: not known yet.</summary>
    public float LodPixelsPerRadian { get; set; }


    /// <summary>Colour batches are drawn nearest first (Meitou, with the reach switch: early depth rejection of the shading of what a nearer batch covers); off: in the order they were first met.</summary>
    public bool SortNearestFirst { get; set; }


    /// <summary>Distant towns and buildings' distant meshes are drawn up to this distance (the game: its <c>distant town range</c> in zones).</summary>
    public float DistantRange { get; set; } = ObjectRanges.MaxDistantTownRangeZones * WorldLayout.ZoneSize;

    /// <summary>The memory-pressure guard (<see cref="VramGuard"/>): its range scale shortens <see cref="ObjectDistance"/> and <see cref="DistantRange"/>
    /// while the video memory is nearly used up, and the caches stop loading and evict. Null: no clamp.</summary>
    public VramGuard? Guard
    {
        get => guard;
        set { guard = value; textureCache.Guard = value; meshes.Guard = value; }
    }
    VramGuard? guard;
    /// <summary>The ranges as drawn and streamed: the settings times the guard's scale (exactly the settings while it is 1).</summary>
    float RealRange => ObjectDistance * (guard?.RangeScale ?? 1f);
    /// <summary>How far a landmark is drawn and streamed: <see cref="LandmarkDistance"/> times the guard's scale, never less than <see cref="RealRange"/> (0: the object distance).</summary>
    float LandmarkReach => Math.Max(RealRange, LandmarkDistance * (guard?.RangeScale ?? 1f));
    /// <summary>Whether the landmarks are kept apart from the zones (the renderer was made with them).</summary>
    public bool HasLandmarks => landmarkClass is not null;

    /// <summary>Presentation-only placements (<see cref="ObjectStreamer.Staged"/>): the Meitou title's staging; null in play.</summary>
    public IReadOnlyList<PlacedMesh>? Staged { get => streamer.Staged; set => streamer.Staged = value; }
    float DistantReach => DistantRange * (guard?.RangeScale ?? 1f);

    // ---- runtime buildings, construction sites and the placement ghost (docs/render-objects.md "Construction sites") ----

    /// <summary>A building the player placed: part of its zone from now on (laid out apart from the rest of an already loaded zone).</summary>
    public void AddRuntime(BuildingPlacement placement)
    {
        objects.AddRuntime(placement);
        streamer.AddRuntime(placement);
    }

    /// <summary>A placed building is gone: its parts leave the zone. A town house's override is gone: its zone is laid out again, from the level data.</summary>
    public void RemoveRuntime(string instanceId)
    {
        if (objects.RemoveRuntime(instanceId) is not { } removed) return;
        if (removed.DestroyedOverride is not null) streamer.Reload([removed.Zone]);
        else streamer.RemoveRuntime(instanceId, removed.Zone);
    }

    /// <summary>Forgets every runtime building (a loaded save replaces the world) and lays out the zones that had one again.</summary>
    public void ClearRuntime() => streamer.Reload(objects.ClearRuntime());

    /// <summary>
    /// The sites under construction: placement id to the look's <c>f</c> (docs/game/buildings-production.md "Construction look"). A listed building draws only the part below
    /// 1.2 f of each mesh's height, and a scaffold grid above. Read on the render thread; the host replaces it as it likes.
    /// </summary>
    public IReadOnlyDictionary<string, float>? Construction { get => construction; set => construction = value; }
    IReadOnlyDictionary<string, float>? construction;
    /// <summary>The scaffold grid of the sites (the CONSTRUCTION unit <c>scaffold</c>, docs/formats/runtime-materials.md).</summary>
    const string ScaffoldTexture = "construction.dds";
    readonly WorldTexture? scaffoldGrid;

    /// <summary>One placement ghost: a building's parts laid out at the origin, drawn at <paramref name="World"/> (rotation, then position) in the tint of <paramref name="State"/>.</summary>
    public readonly record struct GhostDraw(IReadOnlyList<PlacedMesh> Parts, Matrix4x4 World, GhostState State);

    /// <summary>The tint of a ghost: the one at the cursor can be placed or not; a queued one waits for Confirm.</summary>
    public enum GhostState { Valid, Invalid, Queued }

    // One slot per ghost drawn: the parts list its instances were made from, those instances, and where and how it is drawn now.
    sealed class GhostSlot
    {
        public object? Parts;
        public PlacedMesh[] Base = [];
        public readonly List<ObjectStreamer.Instance> Instances = [];
        public GhostState State;
    }

    readonly List<GhostSlot> ghostSlots = [];
    bool ghostsDrawn;

    /// <summary>
    /// The placement ghosts of Build mode (docs/render-objects.md "Placement ghost"): each list of parts is laid out once (the same list keeps its instances), the
    /// transforms follow every call. Empty: no ghost.
    /// </summary>
    public void SetGhosts(IReadOnlyList<GhostDraw> ghosts)
    {
        while (ghostSlots.Count > ghosts.Count)
        {
            DropGhost(ghostSlots[^1]);
            ghostSlots.RemoveAt(ghostSlots.Count - 1);
        }
        while (ghostSlots.Count < ghosts.Count) ghostSlots.Add(new());
        for (int s = 0; s < ghosts.Count; s++)
        {
            var (parts, world, state) = ghosts[s];
            var slot = ghostSlots[s];
            if (!ReferenceEquals(parts, slot.Parts))
            {
                DropGhost(slot);
                slot.Parts = parts;
                slot.Base = [.. parts.Where(p => !p.Interior)];
                foreach (var p in slot.Base) slot.Instances.Add(streamer.MakeLoose(p));
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
        ghostsDrawn = ghostSlots.Count > 0;
    }

    void DropGhost(GhostSlot slot)
    {
        foreach (var inst in slot.Instances) streamer.DropLoose(inst);
        slot.Instances.Clear();
        slot.Parts = null;
        slot.Base = [];
    }

    void DrawGhosts(Vector3 eye, Vector4[] frustum, long now)
    {
        foreach (var slot in ghostSlots)
        {
            int tint = slot.State switch { GhostState.Valid => BatchKind.Ghost, GhostState.Invalid => BatchKind.GhostBad, _ => BatchKind.GhostQueued };
            foreach (var inst in slot.Instances)
            {
                if (inst.Gpu is not { } gpu) continue;
                if (!ReferenceEquals(inst.Mesh.Gpu, gpu)) { Unresolve(streamer.Loose, inst); continue; }
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

    /// <summary>Draw nothing but the real objects (no distant meshes): the game with distant towns off.</summary>
    public bool NoDistant { get; set; }

    /// <summary>The resolved real instances (zones and landmarks, not the distant stand-ins) whose bounds come within <paramref name="range"/> of
    /// <paramref name="eye"/>: what the global illumination's acceleration structures hold (docs/render-gi.md).</summary>
    /// <summary>The bindless index of a ray-traced part's diffuse map (0: none or not resident), at the draws' bias so the entry is shared.</summary>
    internal uint RayTexture(ObjectMaterialSet materials, int part)
    {
        if (materials.Parts.Length == 0) return 0;
        uint key = materials.Parts[Math.Min(part, materials.Parts.Length - 1)].Diffuse;
        return key == 0 ? 0 : textureCache.Index(key, Gpu.LodBias, 0);
    }

    /// <summary>
    /// The instances the GI traces: those drawn within <paramref name="range"/>, without the construction sites, whose parts are drawn clipped at the scaffold (the
    /// rays would meet the whole building, the roof of a half-built or ruined one included, and light the open site as a closed room).
    /// </summary>
    internal IEnumerable<ObjectStreamer.Instance> RayInstances(Vector3 eye, float range)
    {
        foreach (var zone in streamer.AllZones)
        {
            if (ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, eye) > range) continue;
            foreach (var inst in zone.Real)
                if (inst.Gpu is not null && inst.Materials is not null && Vector3.Distance(inst.Centre, eye) - inst.Radius <= range && !InteriorHidden(inst.Placed) && !ClosedAround(inst) && !UnderConstruction(inst))
                    yield return inst;
        }
        if (streamer.LandmarkZone is { } landmarks)
            foreach (var inst in landmarks.Real)
                if (inst.Gpu is not null && inst.Materials is not null && Vector3.Distance(inst.Centre, eye) - inst.Radius <= range && !InteriorHidden(inst.Placed) && !ClosedAround(inst)) yield return inst;
    }

    bool UnderConstruction(ObjectStreamer.Instance inst) =>
        construction is { Count: > 0 } sites && !inst.Stand && sites.ContainsKey(inst.Placed.ParentPlacementId ?? inst.Placed.PlacementId);

    /// <summary>Requested work: zone layouts, meshes and textures, including used textures deferred by memory pressure.</summary>
    public int Pending => streamer.Pending + meshes.Pending + uploads.Count + textureCache.AwaitedCount + textureCache.UnresidentUsedCount + unresolvedInRange + (landmarkTask is not null ? 1 : 0);

    int unresolvedInRange;
    /// <summary>What <see cref="Pending"/> is made of, for the loading log.</summary>
    public string PendingDetail => $"zones {streamer.Pending}, meshes {meshes.Pending}, uploads {uploads.Count}, textures {textureCache.AwaitedCount} loading {textureCache.UnresidentUsedCount} deferred, unresolved {unresolvedInRange}, landmarks {(landmarkTask is not null ? 1 : 0)}";
    public List<string> Messages { get; } = [];

    /// <summary>A line about what is loaded, for the log.</summary>
    /// <summary>GPU memory held by streamed meshes and textures, for the stats line and the window title.</summary>
    public long ResidentBytes => meshes.Bytes + textureCache.ResidentBytes;
    public string ResidentDescription => $"{meshes.Bytes / 1048576.0:0} MB in {meshes.Resident} meshes ({meshes.Unloads} unloaded, {meshes.Reloads} reloaded, {meshes.Refined} remade finer, {meshes.Coarsened} coarser), {textureCache.Describe()}";
    public string Describe() =>
        $"{streamer.Loaded} zones, {streamer.Instances:N0} instances ({streamer.Resolved:N0} resolved{(streamer.LandmarkZone is { } lz ? $", of them {lz.Real.Count(i => i.Gpu is not null)} of {lz.Real.Count} landmarks" : "")}), {meshes.Resident}/{meshes.Total} meshes requested or resident, resident: {ResidentDescription}, " +
        $"{towns.Count(t => t.Mesh.Status == ObjectMesh.State.Resident)}/{towns.Count} distant towns; {objects.Describe()}";

    // ------------------------------------------------------------------ streaming

    /// <summary>Render-thread time per frame spent on uploads and resolving.</summary>
    public double BudgetMs { get; set; } = 2;
    /// <summary>The view is under the loading screen (the host sets it each frame): its frames need not be smooth, so uploads and resolving take up to
    /// <see cref="CoveredBudgetMs"/> a frame instead of <see cref="BudgetMs"/>, and zones and instances are taken eight times as fast.</summary>
    public bool Covered { get; set; }
    const double CoveredBudgetMs = 16;

    /// <summary>Follows the eye: lays out zones and loads meshes and textures around it, within <see cref="BudgetMs"/> of render-thread time.</summary>
    /// <summary>The arrival eye of a character flight (<see cref="ObjectStreamer.Ahead"/>): its objects are laid out, loaded and kept, their meshes and textures as fine
    /// as seen from there. A new point re-marks at the next update instead of within the second.</summary>
    public Vector3? Ahead
    {
        get => streamer.Ahead;
        set { if (value is not null && streamer.Ahead is null) lastMark = 0; streamer.Ahead = value; }
    }

    /// <summary>Points kept loaded besides the eye's (<see cref="ObjectStreamer.Kept"/>).</summary>
    public IReadOnlyList<Vector3> Kept { get => streamer.Kept; set => streamer.Kept = value; }

    public void Update(Vector3 eye) => Update(eye, Covered ? Math.Max(BudgetMs, CoveredBudgetMs) : BudgetMs);

    /// <summary>Colour draws of parts whose diffuse map was not resident (drawn grey) and, at the same moment, textures waiting for a finer image,
    /// the largest of each since <see cref="TakePopStats"/> (the benchmark's pop-in lines).</summary>
    int untexturedDraws, untexturedMax, heldNear, coarseDraws, coarseMax;
    /// <summary>Meshes drawn in a reduced form while the full one loads (<see cref="ObjectMeshCache"/>).</summary>
    public int MeshesAwaitingDetail => meshes.Upgrading;
    public (int Untextured, int Refining, int Held, int Coarse) TakePopStats()
    {
        var result = (Math.Max(untexturedMax, untexturedDraws), textureCache.Refining, heldNear, Math.Max(coarseMax, coarseDraws));
        untexturedDraws = untexturedMax = heldNear = coarseDraws = coarseMax = 0;
        return result;
    }

    /// <summary>Tells the object textures' mip streaming (<see cref="MipStreaming"/>) the camera: the render size in pixels and the vertical field of view.</summary>
    public void SetView(int width, int height, float fieldOfView) => SetView(width, height, fieldOfView, Gpu.LodBias);
    public void SetView(int width, int height, float fieldOfView, float lodBias) => textureCache.Mips.SetView(width, height, fieldOfView, lodBias);

    /// <summary>The eye of the last <see cref="Update"/>, and how far ahead of it (distance) the textures are kept ready for (the camera's speed over half a second).</summary>
    Vector3 eyeNow;
    float needMargin;
    /// <summary>How far ahead a mesh is kept ready for: three times <see cref="needMargin"/> (the camera's speed over 1.5 s).</summary>
    float MeshMargin => needMargin * 3;
    /// <summary>A camera move farther than this between two mark passes (a second apart) is a cut or a flight, not travel (the host's flight distance).</summary>
    const float CutDistance = 10000;

    void Update(Vector3 eye, double budgetMs)
    {
        var watch = Stopwatch.StartNew();
        eyeNow = eye;
        meshes.LodBias = LodBias;
        if (LodPixelsPerRadian > 0) meshes.PixelsPerRadian = LodPixelsPerRadian;
        meshes.LookAhead = MeshMargin;
        bool unlimited = budgetMs > 1e8;
        float streamRange = (NoDistant ? RealRange : Math.Max(RealRange, DistantReach)) + WorldLayout.ZoneSize * 0.5f;
        streamer.Paused = guard is { Streaming: false };
        if (landmarkTask is { IsCompleted: true } collected)
        {
            landmarkTask = null;
            if (collected.IsCompletedSuccessfully)
            {
                streamer.Landmarks = landmarkClass;
                streamer.SetLandmarks(collected.Result);
                Console.WriteLine($"landmarks {collected.Result.Count} placements of radius {LandmarkClass.MinRadius:0}+ kept apart from the zones ({landmarkMs:0} ms on the workers)");
            }
            else
            {
                streamer.WaitForLandmarks = false;   // the zones keep them: drawn to the object distance
                Console.WriteLine($"warning   landmarks: {collected.Exception?.GetBaseException().Message}");
            }
        }
        // Under the loading screen (Covered) eight times the zones and resolves a frame.
        int scale = Covered ? 8 : 1;
        streamer.Update(eye, streamRange, unlimited ? 64 : scale, Covered);
        ResolveTowns(eye);
        unresolvedInRange = streamer.Scan(eye, RealRange, DistantReach, NoDistant, Resolve, unlimited ? int.MaxValue : 40 * scale);
        unresolvedInRange += streamer.ScanLandmarks(eye, LandmarkReach, Resolve, unlimited ? int.MaxValue : 10 * scale);
        unresolvedInRange += streamer.ScanLoose(streamer.Loose, eye, RealRange, Resolve, unlimited ? int.MaxValue : 10 * scale);
        meshes.Pump();
        MarkInRange(eye, force: unlimited);
        meshes.Trim();
        if (unloadedMeshes.Count > 0)
        {
            // Instances that resolved to a mesh just unloaded wait for it again (Scan asks for it once they are in range).
            foreach (var zone in streamer.AllZones)
                foreach (var inst in zone.Real.Concat(zone.Stand))
                    if (inst.Gpu is { } resolved && unloadedMeshes.Contains(resolved)) Unresolve(zone, inst);
            if (streamer.LandmarkZone is { } landmarkZone)
                foreach (var inst in landmarkZone.Real)
                    if (inst.Gpu is { } resolved && unloadedMeshes.Contains(resolved)) Unresolve(landmarkZone, inst);
            foreach (var inst in streamer.Loose.Real)
                if (inst.Gpu is { } resolved && unloadedMeshes.Contains(resolved)) Unresolve(streamer.Loose, inst);
            unloadedMeshes.Clear();
        }
        textureCache.Pump(wait: unlimited, max: unlimited ? 16 : 8, budgetMs: Math.Max(budgetMs * 0.5, 0.5));
        lock (textureCache.Messages)
        {
            foreach (var m in Messages.Concat(meshes.Messages).Concat(textureCache.Messages).Take(30)) Console.WriteLine($"warning   {m}");
            Messages.Clear();
            meshes.Messages.Clear();
            textureCache.Messages.Clear();
        }
        uploads.Run(budgetMs);
        LastUpdateMs = watch.Elapsed.TotalMilliseconds;
        if (!unlimited && LastUpdateMs > 6) SpikeLog.Note($"slow objects update [{uploads.LastRun}]", ms: LastUpdateMs);
    }

    readonly HashSet<GpuObjectMesh> unloadedMeshes = [];
    readonly List<ObjectMaterialSet> markedSets = [];
    readonly List<ObjectMesh> markedMeshes = [];
    Vector3 lastMarkEye;
    long lastMarkTime;
    long lastMark;
    int markStamp;

    /// <summary>
    /// Once a second, before <see cref="ObjectMeshCache.Trim"/>: everything within its draw range counts as in use, whether or not the camera looks at
    /// it (instances reading the mesh, materials the textures), so only what is out of range gets unloaded, after the idle time.
    /// </summary>
    void MarkInRange(Vector3 eye, bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - lastMark < 1000) return;
        // How far the camera goes in the half second a finer texture takes to arrive: nearer than that counts as nearer.
        // A flight or a cut is no camera speed: its arrival is known (Ahead), and a margin from it would ask the whole path, or all around the landing, for full detail.
        bool cut = streamer.Ahead is not null || lastMarkTime != 0 && Vector3.Distance(eye, lastMarkEye) > CutDistance;
        if (force || cut) needMargin = 0;
        else if (lastMarkTime != 0) needMargin = Math.Min(Vector3.Distance(eye, lastMarkEye) / Math.Max((now - lastMarkTime) / 1000f, 0.05f) * 0.5f, 30000f);
        (lastMarkEye, lastMarkTime) = (eye, now);
        lastMark = now;
        markStamp++;
        markedSets.Clear();
        markedMeshes.Clear();
        void Mark(ObjectStreamer.Instance inst, float range)
        {
            if (inst.Gpu is null) return;
            float distance = streamer.Near(eye, inst.Centre), value = distance - inst.Radius, limit = Math.Min(range, inst.Limit);   // or from the arrival point (Ahead)
            if (value >= limit)
            {
                // Not drawn, but still within what Scan asks meshes for: kept, or it would be unloaded and asked for again.
                if (distance - ObjectStreamer.RequestMargin <= limit) inst.Mesh.LastUsed = now;
                return;
            }
            inst.Mesh.LastUsed = now;
            if (inst.Materials is not { } set) return;
            if (set.Stamp != markStamp) { set.Stamp = markStamp; set.Touch(); }
            if (inst.Stand) return;
            // During a flight the path asks for nothing finer: needs and mesh levels count only the arrival and the kept points (it keeps what it has).
            if (streamer.Ahead is not null && (value = streamer.LoadNear(eye, inst.Centre) - inst.Radius) >= limit) return;
            if (set.NeedStamp != markStamp) { set.NeedStamp = markStamp; set.Near = set.NearScaled = float.PositiveInfinity; markedSets.Add(set); }
            float near = Math.Max(value - needMargin, 0);
            if (near < set.Near) set.Near = near;
            // A mesh is kept ready for where the camera will be by the next pass and the remake (1.5 s of its speed), a texture for half a second (its two levels of margin do the rest).
            float meshNear = Math.Max(value - MeshMargin, 0);
            var mesh = inst.Mesh;
            if (mesh.NearStamp != markStamp) { mesh.NearStamp = markStamp; mesh.NearPass = meshNear; markedMeshes.Add(mesh); }
            else if (meshNear < mesh.NearPass) mesh.NearPass = meshNear;
            float scaled = near / Math.Max(inst.Radius / Math.Max(inst.Gpu.Radius, 1e-6f), 1e-6f);
            if (scaled < set.NearScaled) set.NearScaled = scaled;
        }
        if (streamer.Ahead is { } arrival)
        {
            // A flight: what the arrival view draws is marked first, its textures loaded ahead of the other streaming work (the flight path's after).
            textureCache.Urgent = true;
            try
            {
                foreach (var zone in streamer.AllZones)
                {
                    foreach (var inst in zone.Real) if (Vector3.Distance(arrival, inst.Centre) - inst.Radius < Math.Min(RealRange, inst.Limit)) Mark(inst, RealRange);
                    if (!NoDistant) foreach (var inst in zone.Stand) if (Vector3.Distance(arrival, inst.Centre) - inst.Radius < DistantReach) Mark(inst, DistantReach);
                }
            }
            finally { textureCache.Urgent = false; }
        }
        foreach (var zone in streamer.AllZones)
        {
            foreach (var inst in zone.Real) Mark(inst, RealRange);
            if (!NoDistant) foreach (var inst in zone.Stand) Mark(inst, DistantReach);
        }
        if (streamer.LandmarkZone is { } landmarkZone)
        {
            float reach = LandmarkReach;
            foreach (var inst in landmarkZone.Real) Mark(inst, reach);
        }
        // Mip streaming: each texture's nearest user, over how large a texel is there.
        foreach (var set in markedSets)
            foreach (var part in set.Parts)
                part.Offer(part.Triplanar ? set.Near : set.NearScaled);
        textureCache.CommitNeeds();
        textureCache.Rebalance();
        // Distance-based streaming of the finest LOD level: each mesh in range, remade when its nearest user needs finer levels (or, after a while, no longer needs them).
        foreach (var mesh in markedMeshes) meshes.Rebalance(mesh, mesh.NearPass);
        if (NoDistant) return;
        foreach (var t in towns)
            if (Vector3.Distance(eye, t.Centre) - t.Radius < DistantReach) t.Mesh.LastUsed = now;
        _ = distantMaterial.Parts[0].Diffuse;
    }

    /// <summary>A mesh was unloaded under an instance that had resolved to it: the instance waits for it again (<see cref="ObjectStreamer.Scan"/> asks for it once in range).</summary>
    static void Unresolve(ObjectStreamer.Zone zone, ObjectStreamer.Instance inst)
    {
        inst.Gpu = null;
        zone.Unresolved.Add(inst);
    }

    /// <summary>Forgets the batches of an unloaded mesh (their key would keep the deleted buffers' wrapper alive).</summary>
    void RemoveBatches(GpuObjectMesh gpu)
    {
        List<(GpuObjectMesh, ObjectMaterialSet, int, int)>? keys = null;
        foreach (var key in batchMap.Keys)
            if (ReferenceEquals(key.Item1, gpu)) (keys ??= []).Add(key);
        if (keys is not null) foreach (var key in keys) batchMap.Remove(key);
    }

    void ResolveTowns(Vector3 eye)
    {
        if (NoDistant) return;
        foreach (var t in towns)
        {
            if (!t.Resolved && t.Mesh.Status == ObjectMesh.State.Resident)
            {
                var g = t.Mesh.Gpu!;
                t.Centre = t.Town.Position + g.Centre;
                t.Radius = g.Radius;
                t.Resolved = true;
            }
            float d = Vector3.Distance(eye, t.Centre);
            if (t.Mesh.Status == ObjectMesh.State.None && d - t.Radius < DistantReach) meshes.Request(t.Mesh, d);
        }
    }

    /// <summary>Waits until everything wanted around <paramref name="eye"/> is laid out, decoded, uploaded and textured (offscreen rendering).</summary>
    public void Settle(Vector3 eye, int timeoutMs = 300000)
    {
        var watch = Stopwatch.StartNew();
        int rounds = 0;
        double? pressureSince = null;
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            guard?.Tick();
            Update(eye, 1e9);
            if (++rounds > 2 && Pending == 0) return;
            // Under memory pressure the guard holds the loading back: what is missing will not come until it ends, so do not wait for it.
            pressureSince = guard is { Pressure: true } ? pressureSince ?? watch.Elapsed.TotalSeconds : null;
            if (pressureSince is { } since && watch.Elapsed.TotalSeconds - since > 5) { Console.WriteLine("warning   object streaming stopped waiting: memory pressure (VramGuard)"); return; }
            Thread.Sleep(1);
        }
        Console.WriteLine($"warning   object streaming did not finish in {timeoutMs / 1000} s");
    }

    // ------------------------------------------------------------------ materials

    /// <summary>Makes the instance drawable once its mesh is resident: bounds, draw distance, material set. False when the mesh holds only coarser
    /// levels than this instance can need (it was loaded for farther ones): it is asked to be remade and the instance waits.</summary>
    bool Resolve(ObjectStreamer.Instance inst)
    {
        var gpu = inst.Mesh.Gpu!;
        var t = inst.Placed.Transform;
        var centre = Vector3.Transform(gpu.Centre, t);
        float scale = MathF.Sqrt(Math.Max(new Vector3(t.M11, t.M12, t.M13).LengthSquared(), Math.Max(new Vector3(t.M21, t.M22, t.M23).LengthSquared(), new Vector3(t.M31, t.M32, t.M33).LengthSquared())));
        float radius = gpu.Radius * scale;
        if (!inst.Stand && gpu.MinLevel > 0 && inst.TerrainMode)
        {
            meshes.Retarget(inst.Mesh, 0);   // the terrain shader wants every level
            return false;
        }
        if (!inst.Stand && gpu.MinLevel > 0)
        {
            float value = Math.Max(streamer.LoadNear(eyeNow, centre) - radius - MeshMargin, 0);
            if (gpu.Curve.LevelFor(value * LodBias) < gpu.MinLevel)
            {
                if (value < 3000) heldNear++;   // an instance that close waiting for its mesh to be remade would be seen (the benchmark counts it)
                meshes.Retarget(inst.Mesh, value * 0.7f);
                return false;
            }
        }
        inst.Gpu = gpu;
        inst.Centre = centre;
        inst.DoorOpen = -1;   // a door part is posed again from its placement (PoseDoor)
        inst.TurretStamp = -1;   // a turret part is posed again (PoseTurret)
        inst.Radius = radius;
        inst.Limit = inst.Placed.Kind == PlacedKind.BuildingPart
            ? ObjectRanges.PartRenderingDistance(gpu.Radius, inst.Placed.Owner.GetInt("function"), ObjectRanges.ObjectsViewRange)
            : float.MaxValue;
        if (inst.Stand) { inst.Materials = distantMaterial; return true; }
        // A part's look comes from the material the layout chose (which differs per town), else from the resolver's candidates.
        var key = inst.Placed.MeshPath;
        var mkey = inst.Placed.Material is { } spec
            ? (key.ToLowerInvariant(), "spec", spec.StringId)
            : (key.ToLowerInvariant(), inst.Placed.Source.StringId, inst.Placed.Owner.StringId);
        bool made = !materialSets.TryGetValue(mkey, out var set);
        if (made) materialSets[mkey] = set = MakeMaterials(inst.Placed, gpu);
        inst.Materials = set!;
        // Mip streaming: this instance's distance is a use of the textures from now on (before the pass that would find it), and a new set's
        // textures are loaded now, knowing it (they were made without loading: deferred). Measured as the mark pass does (from the nearest kept point too),
        // or a kept point's textures load coarse for the far eye and wait in the refine queue.
        float near = Math.Max(streamer.LoadNear(eyeNow, inst.Centre) - inst.Radius - needMargin, 0);
        foreach (var part in set!.Parts) part.OfferNow(part.Triplanar ? near : near / Math.Max(scale, 1e-6f));
        if (made) set.Touch();
        return true;
    }

    /// <summary>One material per part: the resolver's candidate that names the placing record (or its building) best.</summary>
    ObjectMaterialSet MakeMaterials(PlacedMesh placed, GpuObjectMesh gpu)
    {
        var result = new ObjectPartMaterial[gpu.Parts.Count];
        string source = $"'{placed.Source.Name}'", owner = $"'{placed.Owner.Name}'";
        var chosen = placed.Material is { } spec ? BuildingMaterial.FromSpec(spec) : null;
        for (int i = 0; i < gpu.Parts.Count; i++)
        {
            var m = chosen ?? resolver.Candidates(placed.MeshPath, new OgreSubMesh { MaterialName = gpu.Parts[i].MaterialName })
                .OrderByDescending(c => (c.Description.Contains(owner, StringComparison.Ordinal) ? 2 : 0) + (c.Description.Contains(source, StringComparison.Ordinal) ? 1 : 0))
                .FirstOrDefault();
            bool border = m?.BorderAddressing ?? false;
            // A map nothing can read is not loaded (<see cref="Material"/> never reads it): the normal map only with tangents (or for the
            // emissive flag, which looks at its key), the second set only with vertex colours (its blend weight).
            var part = gpu.Parts[i];
            bool wantNormal = part.HasTangents || (m?.Emissive ?? false);
            bool wantSecond = part.HasColours;
            // The textures are made without loading (<c>deferred</c>): <see cref="Resolve"/> tells them how near they are used, then reads their keys.
            var tile = m?.Tile ?? Vector2.One;
            float minTile = Math.Min(tile.X, tile.Y);
            bool triplanar = m?.Triplanar ?? false;
            float texel = minTile > 0 && !triplanar ? part.UvScale / minTile : float.PositiveInfinity;   // triplanar: a surface edge-on to a projection axis has a footprint smaller than a pixel on that axis, so no bound
            result[i] = new ObjectPartMaterial(m, textureCache.Get(m?.Diffuse, border, deferred: true), wantNormal ? textureCache.Get(m?.Normal, border, deferred: true) : null,
                wantSecond ? textureCache.Get(m?.Diffuse2, border, deferred: true) : null, wantSecond && part.HasTangents ? textureCache.Get(m?.Normal2, border, deferred: true) : null,
                texel, triplanar) { Dust = placed.Kind == PlacedKind.BuildingPart };
        }
        return new ObjectMaterialSet(result);
    }

    // ------------------------------------------------------------------ drawing

    static Vector3 LevelColour(int level, bool manual, bool stand) =>
        stand ? new Vector3(0.2f, 0.6f, 1f) : manual ? new Vector3(0.9f, 0.2f, 0.9f) : level switch
        {
            0 => new Vector3(0.1f, 0.85f, 0.2f),
            1 => new Vector3(0.95f, 0.9f, 0.1f),
            2 => new Vector3(0.95f, 0.5f, 0.1f),
            _ => new Vector3(0.95f, 0.15f, 0.1f),
        };

    /// <summary>The fog cull (set by the world frame around the main camera's draw only): instances wholly hidden by the fog the eye is in are skipped.</summary>
    internal FogVolumes? FogCull { get; set; }
    /// <summary>The interior cut (docs/render-interior-cut.md; set by the world frame around its near slice's colour draw, null otherwise): map features over a mask take it.</summary>
    internal InteriorMaskRenderer? InteriorCut { get; set; }

    /// <summary>The water reflection's cull (set around the reflection pass's own draw): an instance (bounding sphere centre and radius) it answers false for is skipped.</summary>
    internal Func<Vector3, float, bool>? MirrorCull { get; set; }

    /// <summary>
    /// The buildings shown in the interior view (docs/render-objects.md, "Closed interiors"): placement id to the viewed floor. Their parts above that floor
    /// are hidden and their <c>interior</c> parts shown; every other building shows its outside (its interior parts hidden). Set by the game each frame.
    /// </summary>
    public IReadOnlyDictionary<string, int> InteriorView { get; set; } = new Dictionary<string, int>();

    /// <summary>The loose items (placement ids) above the shown floor of their building in the interior view: not drawn. Set by the game, which picks by the same answer.</summary>
    public IReadOnlySet<string> HiddenItems { get; set; } = new HashSet<string>();

    /// <summary>The nearest loaded building's movement destination, using collision up to the viewed floor, before the terrain.</summary>
    public Vector3? MoveHit(Vector3 origin, Vector3 direction, int floor, float maxDistance) => FloorHit(origin, direction, floor, maxDistance)?.Position;

    /// <summary><see cref="MoveHit"/> with the building whose collision was hit.</summary>
    public (Vector3 Position, InteriorVolume Room)? FloorHit(Vector3 origin, Vector3 direction, int floor, float maxDistance)
    {
        (Vector3, InteriorVolume)? best = null;
        foreach (var zone in streamer.AllZones)
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
        foreach (var zone in streamer.AllZones)
            foreach (var room in zone.Interiors)
                if (!InteriorView.ContainsKey(room.PlacementId) && room.MaskHit(origin, direction, best ?? maxDistance) is { } t) best = t;
        return best;
    }

    /// <summary>
    /// The nearest distance along the ray at which it meets the collision of a building in <see cref="InteriorView"/> on a floor it shows (as <see cref="MoveHit"/>), within
    /// <paramref name="maxDistance"/>: what lies beyond is not pointed at (game: the pick's walk ends at such a building's hit, <c>FUN_140800250</c>). Null when none is in the way.
    /// </summary>
    public float? ShownInteriorHit(Vector3 origin, Vector3 direction, float maxDistance)
    {
        float? best = null;
        foreach (var zone in streamer.AllZones)
            foreach (var room in zone.Interiors)
                if (InteriorView.TryGetValue(room.PlacementId, out int floor) && room.MoveHit(origin, direction, floor, best ?? maxDistance) is { } hit) best = hit.Distance;
        return best;
    }

    /// <summary>
    /// Whether the instance stands in a loaded building's interior (<see cref="InteriorAt"/>), kept on it until the interiors or its place change: every view
    /// (the camera's, each shadow cascade's, the reflection's) asks it of each piece of furniture.
    /// </summary>
    bool InInterior(ObjectStreamer.Instance inst) => InteriorOf(inst).Inside;

    /// <summary>
    /// Whether the instance takes the indoor GI (docs/render-gi.md): a building's interior part, its layout furniture inside a hull, or a building of its own (furniture or a
    /// station the player placed) standing in another building's hull; a building in its own hull is its outside.
    /// </summary>
    bool IndoorFurniture(ObjectStreamer.Instance inst)
    {
        var p = inst.Placed;
        if (p.Interior) return true;
        if (p.ParentPlacementId is not null) return InInterior(inst);
        return p.Kind == PlacedKind.BuildingPart && InteriorOf(inst) is { Inside: true, Building: { } holder } && holder != p.PlacementId;
    }

    ObjectStreamer.InteriorAnswer InteriorOf(ObjectStreamer.Instance inst)
    {
        var at = inst.Transform.Translation;
        if (inst.Interior is { } known && known.Version == streamer.InteriorVersion && known.At == at) return known;
        bool inside = InteriorAt(at) is not null;
        // Engine choice: the 2-unit margin the host gives characters, so an object standing on the hull's top floor counts as on it. A building placed inside
        // another takes that one's hull where it reaches it (a neighbour's within the margin is not its holder).
        var holder = InteriorAt(at, 2, inst.Placed.ContainerId);
        return inst.Interior = new(streamer.InteriorVersion, at, inside, holder?.PlacementId, holder?.FloorAt(at) ?? 0);
    }

    /// <summary>
    /// Whether a building of its own (a turret on a wall, furniture the player placed) stands above the viewed floor of a building in the interior view, by
    /// where it stands in that building's hull: it is hidden with that floor (game: FUN_140547140 / FUN_14054a8f0, as the host does for characters). A loose item: the game's answer
    /// (<see cref="HiddenItems"/>, FUN_1409fb540).
    /// </summary>
    bool StandsAbove(ObjectStreamer.Instance inst)
    {
        var p = inst.Placed;
        if (p.Kind == PlacedKind.Item) return !AllFloors && HiddenItems.Contains(p.PlacementId);
        if (InteriorView.Count == 0 || AllFloors || p.Kind != PlacedKind.BuildingPart || p.ParentPlacementId is not null || InteriorView.ContainsKey(p.PlacementId)) return false;
        var answer = InteriorOf(inst);
        return answer.Building is { } building && InteriorView.TryGetValue(building, out int floor) && answer.Floor > floor;
    }

    /// <summary>
    /// Whether a building placed inside another (<see cref="PlacedMesh.Contained"/>) is hidden: the building whose hull holds it shows its outside (not in the interior
    /// view; a construction site is open). Game: closing an interior hides its contained objects, in every view (docs/render-objects.md "Closed interiors").
    /// </summary>
    bool ClosedAround(ObjectStreamer.Instance inst)
    {
        if (!inst.Placed.Contained) return false;
        return InteriorOf(inst).Building is { } holder && holder != inst.Placed.PlacementId && !InteriorView.ContainsKey(holder) && construction?.ContainsKey(holder) != true;
    }

    /// <summary>The indoor volume of a loaded building that contains <paramref name="p"/> (widened by <paramref name="margin"/>), or null.</summary>
    public InteriorVolume? InteriorAt(Vector3 p, float margin = 0) => InteriorAt(p, margin, null);

    /// <summary>
    /// <see cref="InteriorAt(Vector3, float)"/>, preferring the building <paramref name="prefer"/> names (a site's id or a level building's instance id,
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
                if (streamer.ZoneAt(new ZoneCoordinate(x, y)) is not { Interiors.Count: > 0 } zone || ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, p) > InteriorOver) continue;
                foreach (var v in zone.Interiors)
                    if (v.Contains(p, margin))
                    {
                        if (prefer is null || IsKeyOf(v.PlacementId, prefer)) return v;
                        first ??= v;
                    }
            }
        return first;
    }

    /// <summary>Whether placement key <paramref name="key"/> is building <paramref name="id"/>'s: a site's key is its id, a level building's ends in ":" and its instance id (<see cref="BuildingKey"/>).</summary>
    static bool IsKeyOf(string key, string id) =>
        key == id || key.Length > id.Length && key[key.Length - id.Length - 1] == ':' && key.EndsWith(id, StringComparison.Ordinal);

    /// <summary>The loaded volume of <paramref name="volume"/>'s building (the same, or its zone's re-laid one), or null once its zone is unloaded or the building removed or destroyed.</summary>
    public InteriorVolume? LoadedInterior(InteriorVolume volume)
    {
        if (volume.Footprint.Length == 0) return null;
        var at = volume.Footprint[0];
        var (lo, hi) = (WorldLayout.ZoneOf(at.X - InteriorOver, at.Y - InteriorOver), WorldLayout.ZoneOf(at.X + InteriorOver, at.Y + InteriorOver));
        for (int x = lo.X; x <= hi.X; x++)
            for (int y = lo.Y; y <= hi.Y; y++)
                if (streamer.ZoneAt(new ZoneCoordinate(x, y)) is { Interiors.Count: > 0 } zone)
                    foreach (var v in zone.Interiors)
                        if (ReferenceEquals(v, volume) || v.PlacementId == volume.PlacementId) return v;
        return null;
    }

    const float InteriorOver = 200, InteriorKeep = 50;

    /// <summary>A point's last <see cref="InteriorAt(Vector3, ref InteriorMemo)"/> answer, kept by the asker (a character): good while the interiors are the same and the point within <c>Clear</c> of where it was asked.</summary>
    public struct InteriorMemo
    {
        internal bool Set;
        internal int Version;
        internal Vector3 At;
        internal float Clear;
        internal InteriorVolume? Volume;
    }

    /// <summary>
    /// <see cref="InteriorAt(Vector3, float)"/> without a margin, asked again only once <paramref name="p"/> has moved farther than the nearest interior's box was (at most 50 units,
    /// over every zone the moved point could ask) or the interiors changed. Standing still inside asks nothing either: there the distance kept is 0.
    /// </summary>
    public InteriorVolume? InteriorAt(Vector3 p, ref InteriorMemo memo)
    {
        // Strictly nearer: a point on a box's edge can be inside.
        if (memo.Set && memo.Version == streamer.InteriorVersion && Vector3.DistanceSquared(p, memo.At) is var moved && (moved == 0 || moved < memo.Clear * memo.Clear)) return memo.Volume;
        float clear = InteriorKeep, reach = InteriorOver + InteriorKeep;
        InteriorVolume? found = null;
        var (lo, hi) = (WorldLayout.ZoneOf(p.X - reach, p.Z - reach), WorldLayout.ZoneOf(p.X + reach, p.Z + reach));
        for (int x = lo.X; x <= hi.X && found is null; x++)
            for (int y = lo.Y; y <= hi.Y && found is null; y++)
            {
                if (streamer.ZoneAt(new ZoneCoordinate(x, y)) is not { Interiors.Count: > 0 } zone) continue;
                float zoneDistance = ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, p);
                if (zoneDistance > reach) continue;
                foreach (var v in zone.Interiors)
                {
                    clear = MathF.Min(clear, v.Distance(p));
                    if (zoneDistance <= InteriorOver && v.Contains(p)) { found = v; break; }
                }
            }
        memo = new InteriorMemo { Set = true, Version = streamer.InteriorVersion, At = p, Clear = found is null ? clear : 0, Volume = found };
        return found;
    }

    /// <summary>
    /// The indoor volume of a loaded building straight above or below the ground point (<paramref name="x"/>, <paramref name="z"/>), whatever the height: what a ray
    /// cast down from far above hits (the game's interior ambience test, docs/audio.md "Interiors"). The highest one when several overlap; null when none.
    /// </summary>
    public InteriorVolume? InteriorUnder(float x, float z)
    {
        InteriorVolume? best = null;
        foreach (var zone in streamer.AllZones)
        {
            if (zone.Interiors.Count == 0 || ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, new Vector3(x, 0, z)) > 200) continue;
            foreach (var v in zone.Interiors)
                if ((best is null || v.YMax > best.YMax) && v.ContainsGround(x, z)) best = v;
        }
        return best;
    }

    /// <summary>
    /// The GroundType a character at <paramref name="feet"/> stands on when the floor under it is a building's: its <c>interior material</c> when <paramref name="inside"/> (the
    /// character is in a building), else its <c>exterior material</c>; -1 when the ground is not a building's or its floors are still being made (docs/audio.md "Surface").
    /// </summary>
    public int FloorMaterialUnder(Vector3 feet, bool inside) => objects.Floors.MaterialUnder(feet, inside);

    /// <summary>The highest ordinary floor part of any building at a point, on floors up to <paramref name="floorOf"/> of its placement key; null for none or still being made (docs/formats/camera.md "Height").</summary>
    public float? FloorHeightAt(float x, float z, Func<string, int> floorOf) => objects.Floors.HighestFloorAt(x, z, floorOf);

    /// <summary>The loaded mask of a building or one of its layout objects (building id / item id).</summary>
    internal InteriorVolume? InteriorFor(string placementId)
    {
        int slash = placementId.IndexOf('/');
        var owner = slash < 0 ? placementId.AsSpan() : placementId.AsSpan(0, slash);
        foreach (var zone in streamer.AllZones)
            foreach (var room in zone.Interiors)
                if (owner.SequenceEqual(room.PlacementId)) return room;
        return null;
    }

    /// <summary>The loaded buildings with interior mask meshes whose footprint comes within <paramref name="range"/> of <paramref name="eye"/> (X/Z), into <paramref name="into"/>.</summary>
    internal void InteriorMasks(Vector3 eye, float range, List<InteriorVolume> into)
    {
        into.Clear();
        var q = new Vector2(eye.X, eye.Z);
        foreach (var zone in streamer.AllZones)
        {
            if (zone.Interiors.Count == 0 || ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, eye) > range + 200) continue;
            foreach (var v in zone.Interiors)
            {
                if (v.MaskMeshes.Count == 0 || v.Footprint.Length == 0) continue;
                float nearest = float.MaxValue;
                foreach (var p in v.Footprint) nearest = MathF.Min(nearest, Vector2.DistanceSquared(p, q));
                if (nearest <= range * range || v.ContainsGround(eye.X, eye.Z)) into.Add(v);
            }
        }
    }

    /// <summary>Finds a file a record names (an install-relative path, or a bare name the asset locator knows), or null.</summary>
    internal string? FindRecordFile(string recordPath) =>
        Meitou.Data.Physics.CollisionPaths.Resolve(objects.Install, recordPath) ?? assets.Find(Path.GetFileName(recordPath.Replace('\\', '/')));

    /// <summary>
    /// Use room-scale probes only while the camera target is in or beside a viewed building and under its cover (<see cref="InteriorVolume.Covered"/>): on a walkable
    /// roof, the hull's top storey, the room grid stood its probes under the roof slab and darkened the roof as a closed room. A construction site (a ruin being
    /// rebuilt included) is open above its clip, so it never takes the room grid.
    /// </summary>
    internal Vector3? GiInteriorFocus(Vector3 target)
    {
        if (InteriorView.Count == 0) return null;
        foreach (var zone in streamer.AllZones)
            foreach (var room in zone.Interiors)
            {
                if (!InteriorView.ContainsKey(room.PlacementId) || construction is { } sites && sites.ContainsKey(room.PlacementId)) continue;
                var p = new Vector3(target.X, Math.Clamp(target.Y, room.YMin, room.YMax), target.Z);
                if (room.Contains(p, 64) && room.Covered(p)) return p;
            }
        return null;
    }

    /// <summary>Draw every floor of the buildings in the interior view (their interior parts stay as they are): the game does so in the water reflection.</summary>
    internal bool AllFloors { get; set; }

    /// <summary>
    /// Whether the interior view leaves a building part out: an interior part (or inside-only furniture) of a building seen from outside, or, in the view, a
    /// part or object above the viewed floor. Floor 0 is never hidden (game: only the floor 1..3 lists are switched). Nor is a door: in the game it is a building
    /// of its own, which the view of the building it belongs to does not switch, and 15 of the base game's door leaves are floor-1 parts (Storm Houses, shacks),
    /// which left the doorway empty at floor 0 (docs/render-objects.md "Doors").
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

    /// <summary>What <see cref="InteriorHidden"/> leaves out whatever the viewed floor: an interior part of a building not in the interior view.</summary>
    bool OutsideHidden(PlacedMesh p) =>
        p.Kind == PlacedKind.BuildingPart && p.Interior && !InteriorView.ContainsKey(p.ParentPlacementId ?? p.PlacementId);

    /// <summary>Draws the objects seen from <paramref name="eye"/> through <paramref name="frustum"/>.</summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour, float fogDistance, TerrainRenderer terrain)
    {
        var cpu = Stopwatch.StartNew();
        long now = Environment.TickCount64;
        float real = RealRange;
        DrawnInstances = 0;
        PartLimited = 0;
        untexturedMax = Math.Max(untexturedMax, untexturedDraws);
        untexturedDraws = 0;
        coarseMax = Math.Max(coarseMax, coarseDraws);
        coarseDraws = 0;
        DrawnTriangles = 0;
        DrawCalls = 0;
        foreach (var b in batchMap.Values) b.Count = 0;
        active.Clear();
        terrainMeshes.Clear();
        terrainMeshesCut.Clear();
        if (depthPass) ShadowCut(ref cutConstants);

        // 1. Cull and choose levels: every instance into the batch of its (mesh, materials, level) with its dither range.
        float realBand = Math.Clamp(real * 0.1f, 50, 1500);
        float distantBand = Math.Clamp(DistantReach * 0.15f, 500, 4000);
        foreach (var zone in landmarksOnly ? [] : streamer.ZonesNear(eye, Math.Max(real, NoDistant ? 0 : DistantReach), frustum))
        {
            if (ObjectStreamer.ZoneDistance(zone.X0, zone.Z0, eye) <= real)
                foreach (var inst in zone.Real) DrawReal(zone, inst, real, eye, frustum, options, now);
            if (NoDistant) continue;
            foreach (var inst in zone.Stand)
            {
                if (inst.Gpu is not { } gpu) continue;
                if (!ReferenceEquals(inst.Mesh.Gpu, gpu)) { Unresolve(zone, inst); continue; }
                float value = Vector3.Distance(inst.Centre, eye) - inst.Radius;
                if (value >= DistantReach || !SphereVisible(frustum, inst.Centre, inst.Radius) || MirrorCull?.Invoke(inst.Centre, inst.Radius) == false || FogCull?.Hidden(inst.Centre, inst.Radius, FogVolumes.CullKind.Objects) == true) continue;
                float w = ObjectRanges.RiseWeight(value, real - realBand, realBand) * ObjectRanges.EdgeWeight(value, DistantReach, distantBand);
                if (w <= 0) continue;
                inst.Mesh.LastUsed = now;
                DrawnInstances++;
                Emit(inst, gpu, value, w);
            }
        }
        // The landmarks (a few hundred, kept apart from the zones): the same test with their own reach, the part distance still applying.
        if (streamer.LandmarkZone is { } landmarkZone)
        {
            float reach = LandmarkReach;
            foreach (var inst in landmarkZone.Real) DrawReal(landmarkZone, inst, reach, eye, frustum, options, now);
        }
        // The crops of the player's farms: drawn as real objects, in the shadow passes too.
        if (farmSlots.Count > 0 && !landmarksOnly) DrawFarms(real, eye, frustum, options, now);
        // The placement ghost (Build mode): its parts at the cursor, dithered and tinted green or red; not in a shadow or the landmarks-only pass.
        if (ghostsDrawn && !depthPass && !landmarksOnly) DrawGhosts(eye, frustum, now);
        if (!NoDistant && !landmarksOnly)
            foreach (var t in towns)
            {
                if (t.Mesh.Gpu is not { } gpu) continue;
                float value = Vector3.Distance(t.Centre, eye) - t.Radius;
                if (value >= DistantReach || !SphereVisible(frustum, t.Centre, t.Radius) || MirrorCull?.Invoke(t.Centre, t.Radius) == false || FogCull?.Hidden(t.Centre, t.Radius, FogVolumes.CullKind.Objects) == true) continue;
                t.Mesh.LastUsed = now;
                var batch = BatchFor(gpu, distantMaterial, 0, BatchKind.Town);
                if (batch.Count == 0) { active.Add(batch); batch.Near = 0; }
                batch.Add(Matrix4x4.CreateTranslation(t.Town.Position));
                DrawnInstances++;
            }

        // Nearest first: what a nearer batch covers fails the early depth test of the farther ones (the colour pass only; the depth passes have no shading to save).
        if (SortNearestFirst && !depthPass) active.Sort(static (a, b) => a.Near.CompareTo(b.Near));
        // 2. The instances: each batch's matrices contiguous, in this frame's constants (one copy; the draws reach a batch by firstInstance).
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        if (total > 0)
        {
            instances = Gpu.Frame.AllocateConstants((ulong)(total * InstanceStride), 16);
            var target = instances.Bytes;
            foreach (var b in active)
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * InstanceStride)..]);
        }

        // 3. Draw: Prepare reads the textures (WorldTexture.Key) and makes the draw list, Record puts it into a native segment of VkGl's pass.
        var prog = depthPass ? depthProg : colourProg;
        int wireMode = debugLevels == 2 ? 2 : options.Wireframe;
        if (active.Count > 0)
        {
            var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance };
            if (!depthPass) InteriorMaskRenderer.Apply(ref view, InteriorCut);
            else (view.CutVolumes, view.CutRanges, view.CutPlanes) = (cutConstants.CutVolumes, cutConstants.CutRanges, cutConstants.CutPlanes);
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
        if (terrainMeshes.Count > 0) DrawCalls += terrain.DrawMeshes(terrainMeshes, depthPass);
        if (terrainMeshesCut.Count > 0) DrawCalls += terrain.DrawMeshes(terrainMeshesCut, cut: InteriorCut);
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
        int kind = depthPass ? 1 : 0;
        (Totals[kind, 0], Totals[kind, 1], Totals[kind, 2], Totals[kind, 3], Totals[kind, 4]) =
            (Totals[kind, 0] + DrawnInstances, Totals[kind, 1] + DrawnTriangles, Totals[kind, 2] + DrawCalls, Totals[kind, 3] + PartLimited, Totals[kind, 4] + 1);
    }


    /// <summary>
    /// One real instance of <see cref="Draw"/>: culled by its range (<paramref name="range"/>, the object distance or, for a landmark, the landmark reach,
    /// and its part distance) and the frustum, then its LOD level and batches. The range is the one <see cref="MarkInRange"/> and the streamer's scans use.
    /// </summary>
    void DrawReal(ObjectStreamer.Zone zone, ObjectStreamer.Instance inst, float range, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, long now)
    {
        if (inst.Gpu is not { } gpu) return;
        if (!ReferenceEquals(inst.Mesh.Gpu, gpu)) { Unresolve(zone, inst); return; }
        // A shadow pass keeps what the floor view hides above the viewed floor, so a lower floor stays shaded under its ceiling (docs/render-objects.md
        // "Closed interiors"): only a closed building's interior parts stay out.
        if ((depthPass ? OutsideHidden(inst.Placed) : InteriorHidden(inst.Placed) || StandsAbove(inst)) || ClosedAround(inst)) return;
        if (inst.Placed.DoorIndex >= 0) PoseDoor(inst, gpu);
        if (inst.Placed.Joints is not null) PoseJoints(inst, gpu);
        float value = Vector3.Distance(inst.Centre, eye) - inst.Radius;
        float limit = Math.Min(range, inst.Limit);
        if (value >= limit || !SphereVisible(frustum, inst.Centre, inst.Radius) || MirrorCull?.Invoke(inst.Centre, inst.Radius) == false || FogCull?.Hidden(inst.Centre, inst.Radius, FogVolumes.CullKind.Objects) == true)
        {
            if (value >= inst.Limit && value < range) PartLimited++;   // stopped by the game's part distance, not the object distance (benchmark)
            return;
        }
        float w = ObjectRanges.EdgeWeight(value, limit, Math.Clamp(limit * 0.1f, 50, 1500));
        if (w <= 0) return;
        float lodValue = shadowLodFloor > 0 ? Math.Max(value, shadowLodFloor) : value;   // a far shadow cascade: no finer a level than ShadowLod.ObjectLodFloor
        inst.Mesh.LastUsed = now;
        DrawnInstances++;
        if (inst.TerrainMode && options.Textures)
        {
            // The terrain shader's path: one draw each, no fading, the level the game would pick.
            int level = gpu.Curve.Select(lodValue * LodBias);
            var g = gpu.Manual[level] ?? gpu;
            int lv = gpu.Manual[level] is null ? level : 0;
            // A TERRAIN-mode feature over an interior mask: drawn with the cut (Feature_Terrain_DX11 clips against the mask, docs/render-interior-cut.md).
            var into = !depthPass && inst.Placed.Kind == PlacedKind.MapFeature && InteriorCut is { Active: true } mask && mask.Overlaps(inst.Centre, inst.Radius) ? terrainMeshesCut : terrainMeshes;
            foreach (var gp in g.Parts)
                if (gp.Count[lv] > 0)
                {
                    into.Add((meshes.PlainMesh(gp, lv), gp.Count[lv], inst.Transform));
                    DrawnTriangles += gp.Count[lv] / 3;
                }
            return;
        }
        Emit(inst, gpu, value, w, lodValue);
    }

    /// <summary>Adds an instance at its LOD level: two batches while blending levels, the upper level taking [0, t·w) of the dither range and the lower [t·w, w).</summary>
    void Emit(ObjectStreamer.Instance inst, GpuObjectMesh gpu, float value, float weight) => Emit(inst, gpu, value, weight, value);

    /// <summary><paramref name="lodValue"/> picks the levels (at least <paramref name="value"/>, the distance, in a far shadow cascade).</summary>
    void Emit(ObjectStreamer.Instance inst, GpuObjectMesh gpu, float value, float weight, float lodValue)
    {
        var blend = gpu.Curve.Blend(lodValue * LodBias);
        // A mesh held without its finest levels (made for farther users, see ObjectMeshCache.Retarget) draws its finest instead, until it is remade.
        if (gpu.MinLevel > 0 && blend.Lower < gpu.MinLevel) coarseDraws++;
        if (gpu.MinLevel > 0) blend = new LodBlend(Math.Max(blend.Lower, gpu.MinLevel), Math.Max(blend.Upper, gpu.MinLevel), blend.T);
        var m = inst.Transform;
        // Spare instance-matrix component: room GI gain without splitting shared furniture batches (1), plus 2 when its building's lights are off, which darkens
        // its EMISSIVE materials (BuildingLodShaders).
        m.M34 = (IndoorFurniture(inst) ? 1 : 0) + (!depthPass && EmissiveDark is { } dark && dark(inst.Placed.ParentPlacementId ?? inst.Placed.PlacementId) ? 2 : 0);
        // Another spare component, M44: 1, or for a construction site BuildingLodShaders.ScaffoldBase + its look's f, or a negative packed tint (PackTint).
        // A site's M34 also carries its part's mesh top and the scaffold tiling (BuildingLodShaders.PackSite).
        bool site = false;
        bool highlight = HighlightSigns && !depthPass && !inst.Stand && inst.Placed.Owner.GetBool("is sign");
        if (construction is { Count: > 0 } && !inst.Stand && construction.TryGetValue(inst.Placed.ParentPlacementId ?? inst.Placed.PlacementId, out float built))
        {
            m.M44 = BuildingLodShaders.ScaffoldBase + Math.Clamp(built, 0f, 1f);
            m.M34 += BuildingLodShaders.PackSite(gpu.Top, inst.Placed.Owner.GetFloat("scaffolding tex scale"));
            site = true;
        }
        else if (inst.Tint < 0) m.M44 = inst.Tint;
        // A map feature (CLIP_INTERIOR, docs/render-interior-cut.md) over a building's interior mask takes the cut, and so does another building's part over
        // the mask of a building in the interior view (CutsIntoViewed); the rest keep the programs without it.
        // In a shadow pass such a part casts nothing inside the viewed buildings' hulls (ShadowCut).
        bool cut = depthPass ? shadowCuts.Count > 0 && CutsShadow(inst)
            : InteriorCut is { Active: true } mask && (inst.Placed.Kind == PlacedKind.MapFeature ? mask.Overlaps(inst.Centre, inst.Radius) : CutsIntoViewed(inst, mask));
        if (!blend.IsBlending)
        {
            Add(gpu, inst.Materials!, blend.Lower, m, 0, weight >= 0.999f ? 2 : weight, value, inst.Placed.Interior, site, highlight: highlight, cut: cut);
            return;
        }
        Add(gpu, inst.Materials!, blend.Upper, m, 0, blend.T * weight, value, inst.Placed.Interior, site, highlight: highlight, cut: cut);
        Add(gpu, inst.Materials!, blend.Lower, m, blend.T * weight, weight, value, inst.Placed.Interior, site, highlight: highlight, cut: cut);
    }

    /// <summary>
    /// Leave out other buildings' parts inside a building in the interior view (Fixes settings, on by default; docs/render-interior-cut.md "Meitou"): the Hub's
    /// town wall runs into the Shinobi Thieves tower (issue #111). Not the parts of a building with a drawn mask (the viewed one's own floors and interior), nor
    /// doors, layout objects or buildings placed inside another.
    /// </summary>
    public bool CutIntoViewed { get; set; } = true;

    bool CutsIntoViewed(ObjectStreamer.Instance inst, InteriorMaskRenderer mask) =>
        CutIntoViewed && InteriorView.Count > 0 && Cuttable(inst) && mask.Overlaps(inst.Centre, inst.Radius, InteriorView) && !mask.Drew(inst.Placed.PlacementId);

    bool Cuttable(ObjectStreamer.Instance inst) =>
        !inst.Stand && inst.Placed is { Kind: PlacedKind.BuildingPart, DoorIndex: < 0, ParentPlacementId: null, Contained: false };

    /// <summary>The interior masks last drawn (the main view's), whose owners a shadow pass leaves uncut as the colour pass does.</summary>
    internal InteriorMaskRenderer? Masks { get; set; }

    /// <summary>The viewed buildings' hulls a shadow pass cuts (<see cref="ShadowCut"/>).</summary>
    readonly List<InteriorVolume> shadowCuts = [];
    ViewConstants cutConstants;

    bool CutsShadow(ObjectStreamer.Instance inst)
    {
        if (!Cuttable(inst) || InteriorView.ContainsKey(inst.Placed.PlacementId) || Masks?.Drew(inst.Placed.PlacementId) == true) return false;
        foreach (var v in shadowCuts)
            if (v.Distance(inst.Centre) <= inst.Radius) return true;
        return false;
    }

    /// <summary>
    /// The shadow side of <see cref="CutIntoViewed"/>: the hulls (convex footprint, height range) of the buildings in the interior view, into <paramref name="view"/>
    /// for the depth program, up to 4 buildings and 32 edges in all; a hull that does not fit is left uncut.
    /// </summary>
    void ShadowCut(ref ViewConstants view)
    {
        shadowCuts.Clear();
        view.CutVolumes = 0;
        if (!CutIntoViewed || InteriorView.Count == 0) return;
        int planes = 0;
        foreach (var id in InteriorView.Keys)
        {
            if (shadowCuts.Count == 4) break;
            if (InteriorFor(id) is not { Footprint: { Length: >= 3 } f } v || planes + f.Length > 32) continue;
            view.CutRanges[shadowCuts.Count] = new Vector4(planes, f.Length, v.YMin, v.YMax);
            for (int i = 0; i < f.Length; i++)
            {
                var e = f[(i + 1) % f.Length] - f[i];
                var n = e.LengthSquared() > 1e-8f ? Vector2.Normalize(new Vector2(-e.Y, e.X)) : Vector2.Zero;   // counter-clockwise: inside on the left
                view.CutPlanes[planes++] = new Vector4(n, Vector2.Dot(n, f[i]), 0);
            }
            shadowCuts.Add(v);
        }
        view.CutVolumes = (uint)shadowCuts.Count;
    }

    void Add(GpuObjectMesh gpu, ObjectMaterialSet materials, int level, Matrix4x4 m, float lo, float hi, float near, bool backCull = false, bool site = false, int ghost = 0, bool highlight = false, bool cut = false)
    {
        var manual = gpu.Manual[level];
        // Fully visible: lo 0 and hi 2 (the shader keeps everything when hi >= 1 and lo is 0). With the early depth test off every batch is "fading": the old merging.
        // A construction site discards the fragments above its clip plane, so it needs the program with a discard as well.
        bool fading = !EarlyDepth.Enabled || lo > 0 || hi < 1 || site;
        var batch = BatchFor(manual ?? gpu, materials, manual is null ? level : 0, (fading ? BatchKind.Fading : BatchKind.Solid) | (backCull ? BatchKind.BackCull : 0) | ghost | (highlight ? BatchKind.Highlight : 0) | (cut ? BatchKind.Cut : 0));
        batch.Manual = manual is not null;
        if (batch.Count == 0) { active.Add(batch); batch.Near = near; } else if (near < batch.Near) batch.Near = near;
        m.M14 = lo;
        m.M24 = hi;
        batch.Add(m);
    }

    /// <summary>What a batch's instances share besides mesh, materials and level: all fully visible (their draws can use the programs without a
    /// <c>discard</c>, see <see cref="EarlyDepth"/>), some dithered by the LOD cross-fade, or a distant town (the fade comes per vertex).</summary>
    static class BatchKind
    {
        public const int Solid = 0, Fading = 1, Town = 2;
        /// <summary>A flag on Solid or Fading: <see cref="Batch.BackCull"/>.</summary>
        public const int BackCull = 4;
        /// <summary>Flags: the placement ghost, valid or invalid (<see cref="Batch.Ghost"/>).</summary>
        public const int Ghost = 8, GhostBad = 16, GhostQueued = 32;
        public const int Highlight = 64;
        /// <summary>A flag: <see cref="Batch.Cut"/>.</summary>
        public const int Cut = 128;
    }

    Batch BatchFor(GpuObjectMesh gpu, ObjectMaterialSet materials, int level, int kind)
    {
        var key = (gpu, materials, level, kind);
        if (!batchMap.TryGetValue(key, out var batch))
            batchMap[key] = batch = new Batch { Mesh = gpu, Materials = materials, Level = level, Town = kind == BatchKind.Town,
                Fading = (kind & ~(BatchKind.BackCull | BatchKind.Ghost | BatchKind.GhostBad | BatchKind.GhostQueued | BatchKind.Highlight | BatchKind.Cut)) != BatchKind.Solid, BackCull = (kind & BatchKind.BackCull) != 0,
                Highlight = (kind & BatchKind.Highlight) != 0, Cut = (kind & BatchKind.Cut) != 0,
                Ghost = (kind & BatchKind.GhostQueued) != 0 ? 3 : (kind & BatchKind.GhostBad) != 0 ? 2 : (kind & BatchKind.Ghost) != 0 ? 1 : 0 };
        return batch;
    }

    static bool SphereVisible(Vector4[] planes, Vector3 centre, float radius)
    {
        foreach (var p in planes)
            if (p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z + p.W < -radius * MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z)) return false;
        return true;
    }

    // ------------------------------------------------------------------ native recording
    // docs/renderer-native.md 3.3 and 7.5, step O: native-model programs (BuildingLodShaders' native variants: the same maths, the values in
    // FrameConstants / ViewConstants / push constants, the textures by bindless index), recorded into the pass VkGl has open
    // (BeginNativeInPass). Draw is split into Prepare (cull, levels, batches, then PrepareDraws: the texture keys through WorldTexture.Key,
    // which can start a reload, and the draw list) and Record (the list into one native segment: the sets and dynamic state once, per draw a
    // push of the changed bytes and the part's own buffers). Since phase 8 stage 1 the meshes and textures are native (ObjectMeshCache,
    // WorldTextureCache): the vertex layout is made once per part, the bindless index kept on the texture per LOD bias. Nothing but commands in
    // the loop, no hidden allocation.

    const int InstanceStride = 64;
    /// <summary>The first location the per-instance rows (the batch matrices) take; the mesh's own inputs are below it.</summary>
    const int RowLocation = BuildingLodShaders.InstanceLocation;

    /// <summary>This call's batch matrices in the frame's constants (a batch at its <see cref="Batch.Offset"/>; the draws reach it by firstInstance).</summary>
    Transient instances;

    /// <summary>One part draw, in the renderer-owned list: the part, its level and instance range, the texture keys (their bindless indices
    /// are patched into <see cref="Push"/> per segment) and the push constants it needs.</summary>
    struct ObjDraw
    {
        public GpuObjectPart Part;
        public int Level;
        public uint FirstInstance, Instances;
        public uint Diffuse, Normal, Diffuse2, Normal2;
        public ObjectPush Push;
        /// <summary>Drawn with the program without a discard (<see cref="EarlyDepth"/>): a batch of fully visible instances, a material without a cut-out.</summary>
        public bool Solid;
        /// <summary>Back faces culled (<see cref="Batch.BackCull"/>, not for a double-sided material).</summary>
        public bool BackCull;
    }

    ObjDraw[] draws = new ObjDraw[256];
    int drawCount;

    /// <summary>A native program of the objects with its vertex inputs from the reflection: the layout and buffers a GL vertex array feeds
    /// it, as VkGl feeds a GL program (<see cref="LegacyProgram.VertexLayout"/>: a disabled attribute reads GL's constant through a stride-0 binding).</summary>
    sealed class ObjProg : IDisposable
    {
        readonly IGpuContext ctx;
        public readonly GpuProgram P;
        readonly int[] locations;
        readonly Meitou.Rendering.Gpu.Shaders.ScalarKind[] kinds;
        /// <summary>The program's own input locations (below the instance rows): 0 .. Own − 1.</summary>
        public readonly int Own;
        VertexLayout? last;

        public ObjProg(IGpuContext ctx, NativeFrame frame, string vertex, string fragment, string name)
        {
            this.ctx = ctx;
            P = frame.Program(vertex, fragment, name);
            var inputs = P.Info.VertexReflection!.Inputs;
            locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
            kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
            Own = locations.Where(l => l < RowLocation).DefaultIfEmpty(-1).Max() + 1;
        }

        public VertexLayout Layout(ReadOnlySpan<LegacyProgram.Attribute?> byLocation)
        {
            Span<VertexInput> inputs = stackalloc VertexInput[locations.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                int loc = locations[i];
                inputs[i] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? new VertexInput((uint)loc, a.Format, a.Stride, a.PerInstance)
                    : new VertexInput((uint)loc, GlConventions.DummyVertexFormat(kinds[i]), ctx.ConstantVertexAttributes ? 0u : 16u, false);
            }
            if (last is { } l && inputs.SequenceEqual(l.Inputs)) return l;
            return last = new VertexLayout(inputs.ToArray());
        }

        /// <summary>Locations 0 .. <paramref name="count"/> − 1 as one array for one bind: each attribute's buffer, GL's constant where none.</summary>
        public GpuBufferBinding[] Buffers(ReadOnlySpan<LegacyProgram.Attribute?> byLocation, int count)
        {
            var result = new GpuBufferBinding[count];
            for (int loc = 0; loc < count; loc++)
            {
                int input = Array.IndexOf(locations, loc);
                result[loc] = loc < byLocation.Length && byLocation[loc] is { } a
                    ? a.Buffer
                    : ctx.ConstantVertexAttributes
                    ? new GpuBufferBinding(ctx.DummyVertex, GlConventions.DummyVertexOffset(input >= 0 ? kinds[input] : Meitou.Rendering.Gpu.Shaders.ScalarKind.Float))
                    : new GpuBufferBinding(ctx.DummyVertexArray(input >= 0 ? kinds[input] : Meitou.Rendering.Gpu.Shaders.ScalarKind.Float), 0);
            }
            return result;
        }

        public void Dispose()
        {
            ctx.Pipelines.Forget(P);
            ctx.Shaders.Dispose(P);
        }
    }

    /// <summary>The pipeline state a segment's draws share (everything of <see cref="GraphicsPipelineDesc"/> but the vertex layout).</summary>
    readonly record struct SegmentPipeline(GpuProgram Program, AttachmentFormats Formats, BlendState Blend, ColorMask Mask,
        PolygonKind Polygon, bool AlphaToCoverage, bool DepthClamp);

    readonly Dictionary<(int Kind, SegmentPipeline State), int> segmentByState = [];
    readonly SegmentPipeline[] lastSegment = new SegmentPipeline[5];
    readonly int[] segmentIds = new int[5];
    int segmentCount;

    /// <summary>A stable number for a segment's pipeline state, so a part compares one int per draw.</summary>
    int SegmentId(int kind, GpuProgram p, PassTargets t, DrawState s)
    {
        var segment = new SegmentPipeline(p, t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp);
        if (segmentIds[kind] != 0 && segment == lastSegment[kind]) return segmentIds[kind];
        if (!segmentByState.TryGetValue((kind, segment), out int id)) segmentByState[(kind, segment)] = id = ++segmentCount;
        lastSegment[kind] = segment;
        return segmentIds[kind] = id;
    }

    /// <summary>A part's native state for <paramref name="p"/>, made on first use from its native buffers (they never change while the part
    /// lives). The batch matrices are four per-instance rows of 64 bytes at locations 7 to 10 (bound once per segment).</summary>
    static void Current(ref ObjectNativeMesh n, GpuObjectPart part, ObjProg p)
    {
        if (n.Layout is not null) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        part.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[RowLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32f, InstanceStride, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = new GpuBufferBinding(part.Indices.Handle, 0, part.Indices.Size);
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    GpuPipeline PipelineFor(ref ObjectNativeMesh n, ObjProg p, int segment, DrawState state, AttachmentFormats formats, string label)
    {
        if (n.SegB == segment) return n.PipeB!.Value;
        var pipeline = Gpu.Pipelines.Get(state.Pipeline(p.P, n.Layout!, PrimitiveType.Triangles, formats, label));
        (n.SegB, n.PipeB) = (n.SegA, n.PipeA);
        (n.SegA, n.PipeA) = (segment, pipeline);
        return pipeline;
    }

    /// <summary>The segment's LOD bias (the upscaler's, <see cref="IGpuContext.LodBias"/>) and GL's stand-in for a texture not resident.</summary>
    float textureBias;
    uint textureStandIn;

    /// <summary>A new segment: the bias in effect now and the stand-in's bindless index (the 2D float array the shaders index).</summary>
    void NewTextureSegment()
    {
        textureBias = Gpu.LodBias;
        textureStandIn = Gpu.StandIn2D;
    }

    /// <summary>The bindless index of a texture by its <see cref="WorldTexture.Key"/> (0: the stand-in), as the GL texture was sampled.</summary>
    uint Texture(uint key) => key == 0 ? textureStandIn : textureCache.Index(key, textureBias, textureStandIn);

    /// <summary>The batches' parts as draws, in order, with the texture keys (reading a key counts as use: here, not in Record).</summary>
    void PrepareDraws(WorldRenderOptions options, bool wire)
    {
        drawCount = 0;
        foreach (var b in active)
        {
            int fade = b.Town ? 1 : 0;
            for (int i = 0; i < b.Mesh.Parts.Count; i++)
            {
                var gp = b.Mesh.Parts[i];
                if (gp.Count[b.Level] == 0) continue;
                if (drawCount == draws.Length) Array.Resize(ref draws, drawCount * 2);
                ref var d = ref draws[drawCount++];
                d = default;
                d.Part = gp; d.Level = b.Level; d.FirstInstance = (uint)b.Offset; d.Instances = (uint)b.Count;
                d.Push.TriplanarScale = 1f / 5000;
                d.Push.FadeMode = fade | (b.Cut ? 2 : 0);
                if (wire || debugLevels == 1)
                {
                    d.Push.Wireframe = 1;
                    d.Push.FlatColour = LevelColour(b.Level, b.Manual, ReferenceEquals(b.Materials, distantMaterial));
                }
                else
                {
                    var pm = b.Materials.Parts[Math.Min(i, b.Materials.Parts.Length - 1)];
                    Material(ref d, gp, pm, options);
                    // The placement ghost: the shader tints a draw whose flat colour is non-zero.
                    if (b.Ghost != 0) d.Push.FlatColour = b.Ghost == 1 ? new Vector3(0.35f, 1f, 0.45f) : b.Ghost == 2 ? new Vector3(1f, 0.3f, 0.25f) : new Vector3(0.5f, 0.7f, 1f);
                    if (b.Highlight) d.Push.FlatColour = new Vector3(1f, 1f, 0f);
                    // A part whose diffuse map is not resident yet is drawn plain grey (pop-in the benchmark counts).
                    if (!depthPass && options.Textures && d.Diffuse == 0 && pm.WantsDiffuse) untexturedDraws++;
                    d.Solid = !depthPass && !b.Fading && !b.Cut && d.Push.AlphaThreshold == 0f;
                    d.BackCull = b.BackCull && pm.Material is not { DoubleSided: true };
                }
                DrawCalls++;
                DrawnTriangles += (long)gp.Count[b.Level] / 3 * b.Count;
            }
        }
        if (SolidFirst && !depthPass && !wire) SolidDrawsFirst();
    }

    /// <summary>The solid draws (early depth test) ahead of the dithered and cut-out ones, each group still nearest first: what they write to the depth
    /// buffer is there for the coarse depth test of the others, whose fragments are shaded before the exact test.</summary>
    void SolidDrawsFirst()
    {
        if (drawScratch.Length < drawCount) drawScratch = new ObjDraw[draws.Length];
        int solid = 0;
        for (int i = 0; i < drawCount; i++) if (draws[i].Solid) solid++;
        if (solid == 0 || solid == drawCount) return;
        int s = 0, o = solid;
        for (int i = 0; i < drawCount; i++) drawScratch[draws[i].Solid ? s++ : o++] = draws[i];
        Array.Copy(drawScratch, draws, drawCount);
    }

    ObjDraw[] drawScratch = [];
    /// <summary>On by default (the <c>solid-first</c> A/B turns it off): see <see cref="SolidDrawsFirst"/>.</summary>
    public static bool SolidFirst { get; set; } = true;

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
        pc.Spare = (pm.Dust ? MeshSurface.Dust : 0u) | (m?.Foliage ?? false ? MeshSurface.Foliage : 0u) | (m?.Dust ?? false ? MeshSurface.TriplanarDust : 0u);
    }

    /// <summary>The draw list in one native segment of VkGl's open pass (colour or depth, solid or wireframe).</summary>
    void RecordDraws(ObjProg prog, bool wire, in ViewConstants view, Vector4 fadeRange)
    {
        if (drawCount == 0) return;
        string label = depthPass ? "objects depth" : wire ? "objects wire" : "objects";
        int kind = (depthPass ? 1 : 0) + (wire ? 2 : 0);
        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state, the sets, and per draw everything resolved (pipeline, buffers, the push
        // block with its bindless indices), into a job that only records.
        var targets = Gpu.CurrentTargets();
        // The objects are drawn double-sided (the GL version turned the cull face off); the wireframe as lines pulled forward
        // (GL's PolygonMode LINE with POLYGON_OFFSET_LINE and PolygonOffset(-1, -1)).
        var state = Gpu.CurrentState() with { Cull = CullMode.None };
        if (wire) state = state with { Polygon = PolygonKind.Line, BiasEnable = true, BiasConstant = -1, BiasSlope = -1 };
        int segment = SegmentId(kind, prog.P, targets, state);
        // The fully visible draws of a colour pass use the program without a discard (own segment, own pipelines per part).
        int solidSegment = !depthPass && !wire ? SegmentId(4, solidProg.P, targets, state) : 0;
        var job = drawJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program, job.Count) = (this, targets, state, prog.P, 0);
        job.Frame = nativeFrame.Prepare(in view);
        for (int a = 0; a < 4; a++) job.Rows[a] = new GpuBufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        NewTextureSegment();
        if (job.Draws.Length < drawCount) job.Draws = new DrawJob.Draw[Math.Max(drawCount, job.Draws.Length * 2)];
        var list = draws;
        for (int i = 0; i < drawCount; i++)
        {
            ref var d = ref list[i];
            ref var pc = ref d.Push;
            pc.FadeRange = fadeRange;
            pc.Diffuse = Texture(d.Diffuse);
            pc.Normal = Texture(d.Normal);
            pc.Diffuse2 = Texture(d.Diffuse2);
            pc.Normal2 = Texture(d.Normal2);
            pc.Spare = (pc.Spare & 0xFFu) | (scaffoldGrid is { Key: not 0 } grid ? (Texture(grid.Key) + 1) << 8 : 0);
            var part = d.Part;
            bool solid = d.Solid && solidSegment != 0;
            ref var n = ref (depthPass ? ref part.DepthNative : ref (solid ? ref part.ColourSolidNative : ref part.ColourNative));
            var drawProg = solid ? solidProg : prog;
            int drawSegment = solid ? solidSegment : segment;
            Current(ref n, part, drawProg);
            job.Draws[i] = new DrawJob.Draw
            {
                Pipeline = n.SegA == drawSegment ? n.PipeA!.Value : PipelineFor(ref n, drawProg, drawSegment, state, targets.Formats, label),
                Vertices = n.Vertices, Elements = new GpuBufferBinding(n.Elements.Buffer, n.Elements.Offset + (ulong)part.Offset[d.Level] * 4),
                Push = pc, IndexCount = (uint)part.Count[d.Level], Instances = d.Instances, FirstInstance = d.FirstInstance,
                Cull = d.BackCull ? CullMode.Back : state.Cull,
            };
        }
        job.Count = drawCount;
        Gpu.Record(label, job);
    }

    readonly JobPool<DrawJob> drawJobs = new();

    /// <summary>One objects segment as prepared (<see cref="RecordDraws"/>): recorded on any thread, the same commands as before wave 4.</summary>
    sealed class DrawJob : RecordJob
    {
        public struct Draw
        {
            public GpuPipeline Pipeline;
            public GpuBufferBinding[] Vertices;
            public GpuBufferBinding Elements;
            public ObjectPush Push;
            public uint IndexCount, Instances, FirstInstance;
            public CullMode Cull;
        }

        public WorldObjectRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public GpuProgram Program = default;
        public FrameBinding Frame;
        public readonly GpuBufferBinding[] Rows = new GpuBufferBinding[4];
        public Draw[] Draws = new Draw[64];
        public int Count;
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
                if (!pushed || !System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<ObjectPush>(in d.Push)).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<ObjectPush>(in last))))
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
            Owner.drawJobs.Return(this);
        }
    }

    // ---- depth only (the sun's shadow map, ShadowPass) ----
    bool depthPass;

    /// <summary>The least LOD value a depth draw takes (<see cref="ShadowLod.ObjectLodFloor"/>): in a far cascade an object is drawn at the level the game picks that far away, never a finer one. 0: the distance rule alone.</summary>
    float shadowLodFloor;

    /// <summary>
    /// Draws the objects' depth for a shadow cascade: <see cref="Draw"/>'s culling, levels and batches (chosen by the camera's
    /// <paramref name="eye"/>) with <see cref="ShadowShaders.MeshDepthFragment"/> (the materials' cut-outs and the caster bias), and the
    /// TERRAIN-mode meshes through the terrain's depth path. <paramref name="coarseTexel"/>: the texel of a far cascade (0: not coarsened), which sets the finest level an instance takes
    /// (<see cref="ShadowLod"/>). Leaves the draw counters describing this call.
    /// </summary>
    public void DrawDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain, float coarseTexel = 0)
    {
        depthPass = true;
        shadowLodFloor = ShadowLod.ObjectLodFloor(coarseTexel, options.TerrainPixelScale);
        try { Draw(viewProjection, eye, frustum, options, Vector3.UnitY, Vector3.Zero, 0, terrain); }
        finally { depthPass = false; shadowLodFloor = 0; }
    }

    bool landmarksOnly;

    /// <summary><see cref="DrawDepth"/> for the landmarks alone (the landmark shadow map, ShadowPass.Meitou): the same reach, levels and batches.</summary>
    public void DrawLandmarksDepth(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, TerrainRenderer terrain)
    {
        landmarksOnly = true;
        try { DrawDepth(viewProjection, eye, frustum, options, terrain); }
        finally { landmarksOnly = false; }
    }

    /// <summary>
    /// The landmarks <see cref="Draw"/> would draw from <paramref name="eye"/> looking any way (resolved, within the landmark reach and their part
    /// distance): their bounding spheres (xyz centre, w radius) into <paramref name="spheres"/>. Returns a key that changes when that set does.
    /// </summary>
    public long LandmarkCasters(Vector3 eye, List<Vector4> spheres)
    {
        spheres.Clear();
        if (streamer.LandmarkZone is not { } zone) return 0;
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

    public void Dispose()
    {
        if (ObjectLodGen.Built + ObjectLodGen.Cached > 0) Console.WriteLine($"object lods  {ObjectLodGen.Built} meshes generated ({System.Diagnostics.Stopwatch.GetElapsedTime(0, ObjectLodGen.BuildTicks).TotalSeconds:0.0} s of worker time), {ObjectLodGen.Cached} read from the cache, {ObjectLodGen.Skipped} too small or not eligible");
        DisposeBuildingIcons();
        colourProg.Dispose();
        solidProg.Dispose();
        depthProg.Dispose();
        nativeFrame.Dispose();
        streamer.Dispose();
        meshes.Dispose();
        textureCache.Dispose();
        objects.Dispose();
    }
}
