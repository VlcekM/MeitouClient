using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The characters' drawing: batches by (mesh part, level, sidedness), each view's batches gathered in chunks of characters on workers, the parts
/// placed once a frame for all views, and the jobs that record a view's draws.
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    // ------------------------------------------------------------------ drawing

    /// <summary>
    /// One view's instances of one (mesh part, level, sidedness), drawn with one instanced draw.
    /// </summary>
    internal sealed class Batch
    {
        /// <summary>
        /// The mesh part drawn.
        /// </summary>
        public required GpuObjectPart Part;
        /// <summary>
        /// The mesh LOD level drawn (an index into the part's offsets and counts).
        /// </summary>
        public int Level;
        /// <summary>
        /// Drawn without back-face culling.
        /// </summary>
        public bool DoubleSided;
        /// <summary>
        /// The instances gathered this frame (the first <see cref="Count"/>), grown as needed.
        /// </summary>
        public CharInstance[] Data = new CharInstance[16];
        /// <summary>
        /// How many instances are gathered, and where the batch's first sits among the view's instance rows (its draw's first instance).
        /// </summary>
        public int Count, Offset;

        /// <summary>
        /// Appends an instance, growing <see cref="Data"/> as needed.
        /// </summary>
        public void Add(in CharInstance i)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = i;
        }
    }

    // A batch is named by an id per (mesh part, level, sidedness), the same in every view; each view keeps its own Batch per id (ViewBatches).
    /// <summary>
    /// The batch id of each (mesh part, level, sidedness), assigned in first-seen order; locked when a worker adds one.
    /// </summary>
    readonly Dictionary<(GpuObjectPart, int, bool), int> _batchIds = [];
    /// <summary>
    /// The (mesh part, level, sidedness) of each batch id, the inverse of <see cref="_batchIds"/>.
    /// </summary>
    readonly List<(GpuObjectPart Part, int Level, bool TwoSided)> _batchKeys = [];

    /// <summary>
    /// One view's batches by id, those it fills, its chunks of gathered instances and its instance rows.
    /// </summary>
    sealed class ViewBatches
    {
        /// <summary>
        /// The view's batch by id (null: not made yet), grown as needed.
        /// </summary>
        public Batch?[] ById = new Batch?[256];
        /// <summary>
        /// The batches that have instances in this frame's gathering.
        /// </summary>
        public readonly List<Batch> Active = [];
        /// <summary>
        /// The chunks of gathered instances, one per <see cref="ChunkCharacters"/> characters.
        /// </summary>
        public readonly List<Chunk> Chunks = [];
        /// <summary>
        /// The view's instance rows in this frame's host-visible memory, every batch contiguous.
        /// </summary>
        public Transient Instances;
        /// <summary>
        /// The characters and part instances gathered, and how many chunks hold them.
        /// </summary>
        public int Characters, Parts, ChunkCount;
        /// <summary>
        /// A shadow cascade's (<see cref="PrepareDepth"/>): the update, GPU frame and frustum it was gathered for.
        /// </summary>
        public long Stamp = -1, Frame = -1;
        /// <summary>
        /// The frustum a shadow cascade's batches were gathered for (see <see cref="Stamp"/>).
        /// </summary>
        public Vector4[]? Frustum;

        /// <summary>
        /// Empties the active batches and the counts for a new gathering (the batches are kept for their ids).
        /// </summary>
        public void Begin()
        {
            foreach (var b in Active) b.Count = 0;
            Active.Clear();
            Characters = Parts = ChunkCount = 0;
        }

        /// <summary>
        /// The batch of <paramref name="id"/>, made by <paramref name="owner"/> on first use.
        /// </summary>
        public Batch Get(int id, CharacterRenderer owner)
        {
            if (id >= ById.Length) Array.Resize(ref ById, Math.Max(id + 1, ById.Length * 2));
            return ById[id] ??= owner.NewBatch(id);
        }
    }

    /// <summary>
    /// A new batch for the key of <paramref name="id"/> (under the ids' lock: workers add ids).
    /// </summary>
    Batch NewBatch(int id)
    {
        lock (_batchIds)
        {
            var (part, level, twoSided) = _batchKeys[id];
            return new Batch { Part = part, Level = level, DoubleSided = twoSided };
        }
    }

    /// <summary>
    /// The batches of the colour and motion views (and of any view not prepared ahead).
    /// </summary>
    readonly ViewBatches _mainView = new();

    /// <summary>
    /// The shadow cascades' views by cascade index (<see cref="PrepareDepth"/>).
    /// </summary>
    ViewBatches?[] _cascadeViews = [];
    /// <summary>
    /// The pass being drawn: depth only (shadows, prepass) or motion vectors; neither is the colour pass.
    /// </summary>
    bool _depthPass, _motionPass;

    /// <summary>
    /// A view the placed parts are gathered for: its frustum, the shadow LOD floor and smallest caster radius (a depth view), and its batches.
    /// </summary>
    struct GatherView
    {
        /// <summary>
        /// The view's frustum planes.
        /// </summary>
        public Vector4[] Frustum;
        /// <summary>
        /// The view's shadow LOD floor and the smallest caster radius it draws (0 for a colour view).
        /// </summary>
        public float LodFloor, MinRadius;
        /// <summary>
        /// The batches the view's instances are gathered into.
        /// </summary>
        public ViewBatches Batches;
    }

    /// <summary>
    /// Scratch for gathering a single view.
    /// </summary>
    readonly GatherView[] _oneView = new GatherView[1];
    /// <summary>
    /// Scratch for gathering the shadow cascades' views together, grown as needed and cleared after use.
    /// </summary>
    GatherView[] _depthViews = [];

    /// <summary>
    /// The fog cull (set once by the world frame; the colour and motion passes use it, not the shadow cascades): characters wholly hidden by the fog
    /// the eye is in are skipped.
    /// </summary>
    internal FogVolumes? FogCull { get; set; }

    /// <summary>
    /// Room masks used to give indoor characters the same indirect-light gain as their surroundings.
    /// </summary>
    internal WorldObjectRenderer? InteriorObjects { get; set; }

    /// <summary>
    /// Draws the characters seen from <paramref name="eye"/> through <paramref name="frustum"/> into the open pass. <paramref name="relative"/>, when
    /// the caller has it, is the same view-projection for positions relative to the eye (the view's rotation only): exact, where one made from
    /// <paramref name="viewProjection"/> carries the rounding of the eye's world position (<see cref="DrawView"/>).
    /// </summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, Vector3 light, Vector3 fogColour, float fogDistance,
        Matrix4x4? relative = null)
    {
        _shownLight = light;
        DrawView(new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance },
            eye, frustum, relative: relative);
    }

    /// <summary>
    /// Draws the characters' depth for a shadow cascade (<see cref="CharacterShaders.DepthFragment"/>); levels chosen from <paramref name="lodEye"/>,
    /// never finer than <paramref name="lodFloor"/> (<see cref="ShadowLod.ObjectLodFloor"/> of a far cascade's texel; 0: the distance rule alone), as
    /// the objects. A part whose bounding radius is under <paramref name="minRadius"/> (a far cascade: half a texel, so it would cover less than one;
    /// 0: none) casts nothing there: hair, beards, small attachments and weapons leave the far maps, as the foliage's small meshes do
    /// (<c>ShadowPass.MinFoliageCasterTexels</c>).
    /// </summary>
    /// <param name="cascade">The shadow cascade's index: its instances as <see cref="PrepareDepth"/> gathered them this frame, when it did (-1:
    /// gathered here).</param>
    public void DrawDepth(Matrix4x4 worldToClip, Vector3 lodEye, Vector4[] frustum, float lodFloor = 0, float minRadius = 0, int cascade = -1)
    {
        ViewBatches? prepared = null;
        if ((uint)cascade < (uint)_cascadeViews.Length && _cascadeViews[cascade] is { } cv && cv.Stamp == _updateStamp && cv.Frame == Gpu.Frame.Number
            && cv.Frustum == frustum)
        {
            prepared = cv;
        }

        _depthPass = true;
        try
        {
            DrawView(new ViewConstants { ViewProjection = worldToClip, Eye = lodEye }, lodEye, frustum, lodFloor, minRadius, prepared);
        }
        finally
        {
            _depthPass = false;
        }
    }

    /// <summary>
    /// The shadow cascades about to be drawn this frame, gathered together from the update's placed parts (<paramref name="lodEye"/> is its eye;
    /// another eye gathers in each <see cref="DrawDepth"/>): one walk over the characters in parallel chunks, each character and part tested against
    /// every cascade, then each cascade's batches and instance rows filled in parallel. <see cref="DrawDepth"/> with a cascade's index then only
    /// records. In the frame, before the cascades.
    /// </summary>
    public void PrepareDepth(Vector3 lodEye, ReadOnlySpan<(int Index, Vector4[] Frustum, float LodFloor, float MinRadius)> cascades)
    {
        if (cascades.IsEmpty || _portraitLive >= 0 || _partStamp != _updateStamp || lodEye != _partOrigin) return;
        if (_depthViews.Length < cascades.Length) _depthViews = new GatherView[cascades.Length];
        foreach (var (index, frustum, lodFloor, minRadius) in cascades)
            if (index >= _cascadeViews.Length) Array.Resize(ref _cascadeViews, index + 1);
        for (int k = 0; k < cascades.Length; k++)
        {
            var (index, frustum, lodFloor, minRadius) = cascades[k];
            var vb = _cascadeViews[index] ??= new ViewBatches();
            vb.Begin();
            (vb.Stamp, vb.Frame, vb.Frustum) = (_updateStamp, Gpu.Frame.Number, frustum);
            _depthViews[k] = new GatherView { Frustum = frustum, LodFloor = lodFloor, MinRadius = minRadius, Batches = vb };
        }
        int views = cascades.Length;
        int chunkCount = Gather(_depthViews, views, depth: true, motion: false);
        var gathered = _depthViews;
        Parallel.For(0, views, k => AssignSlots(gathered[k].Batches));
        for (int k = 0; k < views; k++) AllocateRows(_depthViews[k].Batches);
        Parallel.For(0, views * chunkCount, n => Scatter(gathered[n / chunkCount].Batches, n % chunkCount));
        for (int k = 0; k < views; k++) _depthViews[k] = default;
    }

    /// <summary>
    /// Draws the characters of one view in the current pass (colour, depth or motion), positions relative to <paramref name="worldView"/>'s eye:
    /// gathers them unless <paramref name="prepared"/> holds a gathered view, then records one draw per batch (<see cref="RecordDraws"/>). Sets the
    /// draw statistics.
    /// </summary>
    void DrawView(in ViewConstants worldView, Vector3 eye, Vector4[] frustum, float lodFloor = 0, float minRadius = 0, ViewBatches? prepared = null,
        Matrix4x4? relative = null, Matrix4x4? previousRelative = null)
    {
        var cpu = Stopwatch.StartNew();

        // Drawn relative to the view's eye (the vertex program adds it back for the shading): skinned in world floats, an animated vertex far from
        // the origin (the Hub, tens of thousands of units out) rounds to a different step every frame and the character visibly jiggles.
        // The view-projection for those positions is the caller's rotation-only one when it has it: FromOrigin can cancel the eye's coordinates
        // exactly, but not the rounding already in a matrix built with them (about 0.004 units at 50000, a different error every frame: 0.3 px at the
        // closest zoom, and as much noise in the velocity).
        var origin = worldView.Eye;
        var view = worldView;
        view.ViewProjection = relative ?? FromOrigin(origin, worldView.ViewProjection);
        view.PreviousViewProjection = previousRelative ?? FromOrigin(origin, worldView.PreviousViewProjection);
        DrawCalls = 0;
        DrawnTriangles = 0;
        Array.Clear(LevelParts);
        Array.Clear(LevelTriangles);

        var vb = prepared ?? _mainView;
        if (prepared is null) Collect(vb, eye, origin, frustum, lodFloor, minRadius);
        (DrawnCharacters, DrawnParts) = (vb.Characters, vb.Parts);
        if (!_depthPass && !_motionPass && _portraitLive < 0) (_countedFrustum, _countedFog) = (frustum, FogCull);
        if (vb.Active.Count > 0) RecordDraws(in view, vb);

        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
        if (!_depthPass && _portraitLive < 0 && vb.Active.Count > 0) _drawCpu.Add(LastDrawCpuMs);
    }

    /// <summary>
    /// 3. One draw per batch, into a job recorded with the pass's program. The instances were laid out when the view was gathered (AllocateRows),
    /// each batch contiguous and reached by firstInstance.
    /// </summary>
    void RecordDraws(in ViewConstants view, ViewBatches vb)
    {
        var active = vb.Active;
        var instances = vb.Instances;
        var prog = _motionPass ? _motionProg : _depthPass ? _depthProg : _colourProg;
        string label = _motionPass ? "characters motion" : _depthPass ? "characters depth" : "characters";
        var targets = Gpu.CurrentTargets();
        var state = Gpu.CurrentState() with { Cull = CullMode.None };

        // The colour pass writes alpha 1 (the scene host masks it off for everything else): the SSAO character mask (PostProcess).
        if (!_depthPass && !_motionPass) state = state with { ColourMask = DrawState.Rgba };

        var job = _drawJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program, job.Count) = (this, targets, state, prog.P, 0);
        job.Frame = _nativeFrame.Prepare(in view, [_bones.Binding, _content.Materials.Binding(Gpu.Frame), _content.Morphs.Binding(Gpu.Frame),
            _previousBones.Binding]);
        job.Cull = _depthPass ? CullMode.None : CullMode.Back;
        for (int a = 0; a < 9; a++) job.Rows[a] = new GpuBufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        job.Push = new CharacterPush { DepthIndex = _motionPass ? _motionDepth : 0, Fill = new(Fill, 0),
            SkinLut = SkinShading ? _skinScattering.Index : 0 };
        job.RowCount = _motionPass ? 9 : 5;
        if (job.Draws.Length < active.Count) job.Draws = new DrawJob.Draw[Math.Max(active.Count, job.Draws.Length * 2)];

        int n = 0;
        foreach (var b in active)
        {
            var part = b.Part;
            ref var native = ref (_motionPass ? ref MotionNative(part) : ref _depthPass ? ref part.DepthNative : ref part.ColourNative);
            Current(ref native, part, prog);
            job.Draws[n++] = new DrawJob.Draw
            {
                Pipeline = Gpu.Pipelines.Get(state.Pipeline(prog.P, native.Layout!, PrimitiveType.Triangles, targets.Formats, label)),
                Vertices = native.Vertices,
                Elements = new GpuBufferBinding(native.Elements.Buffer, native.Elements.Offset + (ulong)part.Offset[b.Level] * 4),
                IndexCount = (uint)part.Count[b.Level],
                Instances = (uint)b.Count,
                FirstInstance = (uint)b.Offset,
                TwoSided = b.DoubleSided,
            };
            CountDraw(part.Count[b.Level], b.Level, b.Count);
        }
        job.Count = n;

        var timer = _depthPass ? _depthTimer : _colourTimer;
        if (_portraitLive < 0) timer.Begin();
        Gpu.Record(label, job);
        if (_portraitLive < 0) timer.End();
    }

    /// <summary>
    /// The statistics of one draw: <paramref name="instances"/> of a level of <paramref name="indices"/> indices.
    /// </summary>
    void CountDraw(int indices, int level, int instances)
    {
        long triangles = (long)indices / 3 * instances;
        DrawCalls++;
        DrawnTriangles += triangles;
        LevelParts[Math.Min(level, 7)] += instances;
        LevelTriangles[Math.Min(level, 7)] += triangles;
    }

    /// <summary>
    /// 1. and 2. of a view gathered on its own: cull, choose each part's level and group the instances by (mesh part, level, sidedness), then lay out
    /// the instance rows. The world's views from the update's eye take the parts it placed and gather in parallel; a portrait (portraitLive, that one
    /// character, unculled) or another eye places its own.
    /// </summary>
    void Collect(ViewBatches vb, Vector3 eye, Vector3 origin, Vector4[] frustum, float lodFloor, float minRadius)
    {
        vb.Begin();
        bool placed = _portraitLive < 0 && _partStamp == _updateStamp && origin == _partOrigin && eye == origin && (_partMotion || !_motionPass);
        if (placed)
        {
            CollectPlaced(vb, frustum, lodFloor, minRadius);
            return;
        }

        int start = _portraitLive >= 0 ? _portraitLive : 0, end = _portraitLive >= 0 ? _portraitLive + 1 : _liveCount;
        for (int i = start; i < end; i++)
        {
            ref var l = ref _live[i];
            if (_portraitLive < 0 && (l.PortraitOnly || !FrustumTests.SphereVisible(frustum, l.Centre, l.Radius)))
                continue;
            if (!_depthPass && _portraitLive < 0 && FogCull?.Hidden(l.Centre, l.Radius, FogVolumes.CullKind.Characters) == true)
                continue;
            vb.Characters++;
            CollectCharacter(vb, in l, eye, origin, lodFloor, minRadius);
        }

        if (vb.Active.Count == 0) return;
        AllocateRows(vb);
        var target = new Span<byte>(vb.Instances.Pointer, vb.Active.Sum(b => b.Count) * CharInstance.Size);
        foreach (var b in vb.Active)
            MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * CharInstance.Size)..]);
    }

    /// <summary>
    /// The view from the parts <see cref="Update"/> placed: gathered in parallel chunks, then the slots and rows laid out and scattered.
    /// </summary>
    void CollectPlaced(ViewBatches vb, Vector4[] frustum, float lodFloor, float minRadius)
    {
        _oneView[0] = new GatherView { Frustum = frustum, LodFloor = lodFloor, MinRadius = minRadius, Batches = vb };
        Gather(_oneView, 1, _depthPass, _motionPass);
        _oneView[0] = default;
        AssignSlots(vb);
        if (vb.Active.Count == 0) return;
        AllocateRows(vb);
        Parallel.For(0, vb.ChunkCount, c => Scatter(vb, c));
    }

    /// <summary>
    /// One character's shown parts placed relative to <paramref name="origin"/>, each part's level chosen and its instance added to the batches.
    /// </summary>
    void CollectCharacter(ViewBatches vb, in Live l, Vector3 eye, Vector3 origin, float lodFloor, float minRadius)
    {
        bool interior = _portraitLive < 0 && l.Interior;
        var world = Relative(l.World, origin);
        float worldScale = world.ScaleLength();
        var parts = l.Asset.Parts;
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            if (!part.ShowsWith(l.WeaponDrawn, l.RangedDrawn, l.Shots, l.BackDrawn)) continue;
            var model = part.Shared ? world : _attach[l.AttachBase + part.AttachIndex] * world;
            float modelScale = part.Shared ? worldScale : model.ScaleLength();
            if (_depthPass && part.BoundsRadius * modelScale < minRadius) continue;

            // The model is relative to the view's eye (above), so the LOD eye is too: with the world eye every part far from the origin took its last
            // level.
            var centre = Vector3.Transform(part.BoundsCentre, model);
            int level;
            if (!GeneratedLod)
                level = FileLevel(part, MeshLod.Value(eye - origin, centre, part.FileRadius * modelScale));
            else
            {
                float value = MeshLod.Value(eye - origin, centre, part.BoundsRadius * modelScale, _depthPass ? LodBias * ShadowLodBias : LodBias);
                if (_depthPass)
                    value = Math.Max(HairInShadow(part, l.Asset) ? value * CharacterContent.HairLodScale : value, lodFloor);
                level = GeneratedLevel(part, value, _depthPass);
            }

            var instance = new CharInstance
            {
                Model = part.IsBody ? WithBlood(model, l.Blood) : model,
                BoneBase = (uint)l.BoneBase,
                Material = (uint)part.Material.Slot,
                Flags = (interior ? 1u : 0u) | HiddenFlags(part, l.Asset),
                MorphBase = part.Morphed ? l.Asset.MorphBase : 0,
            };
            if (_motionPass)
            {
                var previousWorld = Relative(l.State.PreviousWorld, origin);
                instance.PreviousModel = part.Shared ? previousWorld : l.State.PreviousAttach[part.AttachIndex] * previousWorld;
            }

            vb.Parts++;
            var gps = part.Mesh.Parts;
            for (int g = 0; g < gps.Count; g++)
            {
                if (GroupLevel(gps[g], level) is not int lv) continue;
                var batch = vb.Get(BatchId(part, g, gps[g], lv, part.Material.DoubleSided), this);
                if (batch.Count == 0) vb.Active.Add(batch);
                batch.Add(in instance);
            }
        }
    }

    /// <summary>
    /// Faithful: the file's own level for its distance_sphere value (its stored radius, bias 1), the shadow passes as the picture (docs/characters.md
    /// "LOD of character meshes").
    /// </summary>
    static int FileLevel(AssetPart part, float fileLod) =>
        part.FileDistances is { } fileLevels && MeshLod.Select(fileLevels, fileLod) is int f && f > 0 ? part.FileFirst + f - 1 : 0;

    /// <summary>
    /// The generated level for <paramref name="value"/>; in a shadow pass the body's shadow-only levels where it has them.
    /// </summary>
    static int GeneratedLevel(AssetPart part, float value, bool depth)
    {
        int level = MeshLod.Select(part.Mesh.Distances, value);
        if (depth && part.ShadowDistances is { } shadowLevels && MeshLod.Select(shadowLevels, value) is int s && s > 0)
            level = part.ShadowFirst + s - 1;
        return level;
    }

    /// <summary>
    /// In a shadow map hair is a silhouette, so it takes the rule's own distances there (its levels start CharacterContent.HairLodScale times farther
    /// for the picture). Not on animals.
    /// </summary>
    static bool HairInShadow(AssetPart part, CharacterAsset asset) => part.Material.Shading == CharacterShaders.ShadeHair && !asset.Animal;

    /// <summary>
    /// The mesh group's level for <paramref name="level"/>: the nearest coarser-or-equal one it has indices for, null when it has none.
    /// </summary>
    static int? GroupLevel(GpuObjectPart gp, int level)
    {
        int lv = Math.Min(level, gp.Count.Length - 1);
        while (lv > 0 && gp.Count[lv] == 0) lv--;
        return gp.Count[lv] == 0 ? null : lv;
    }

    /// <summary>
    /// The instance flags of what the look hides on a part: the body's hidden limbs and parts, the part's hidden side, headless or head only.
    /// </summary>
    static uint HiddenFlags(AssetPart part, CharacterAsset asset)
    {
        uint flags = (uint)part.HideSide << 16;
        if (!part.IsBody || asset.Appearance is not { } look) return flags;
        flags |= (uint)look.HiddenLimbs << 12 | (uint)(look.HiddenParts & 0x3FF) << 18;
        if (look.Headless) flags |= CharacterShaders.Headless;
        if (look.HeadOnly) flags |= CharacterShaders.HeadOnly;
        return flags;
    }

    // ------------------------------------------------------------------ parts placed once a frame

    /// <summary>
    /// One part of a character in range as <see cref="Update"/> placed it, relative to its eye (<see cref="CharacterRenderer._partOrigin"/>): the
    /// colour, motion and shadow views are all drawn from that eye, so they share the matrices and the distance the levels are chosen by.
    /// </summary>
    struct PartFrame
    {
        /// <summary>
        /// The part's model matrix relative to the eye, and last frame's (with motion only).
        /// </summary>
        public Matrix4x4 Model, PreviousModel;
        /// <summary>
        /// The model's largest axis scale.
        /// </summary>
        public float Scale;
        /// <summary>
        /// The LOD values at bias 1 (<see cref="MeshLod.Value"/>): by the bounds radius (the generated levels) and by the file's radius (Faithful).
        /// </summary>
        public float Lod, FileLod;
        /// <summary>
        /// Shown with the character's weapon state (<see cref="AssetPart.ShowsWith"/>); the rest is not set when not.
        /// </summary>
        public bool Shows;
    }

    /// <summary>
    /// The parts placed this update (by <see cref="Live.PartBase"/>), grown as needed.
    /// </summary>
    PartFrame[] _partFrames = new PartFrame[1024];
    /// <summary>
    /// The update the parts were placed in (-1: never); a view of another update or eye places them itself.
    /// </summary>
    long _partStamp = -1;
    /// <summary>
    /// The eye the parts were placed relative to.
    /// </summary>
    Vector3 _partOrigin;
    /// <summary>
    /// Whether last frame's matrices were placed too.
    /// </summary>
    bool _partMotion;

    /// <summary>
    /// Places <paramref name="l"/>'s parts relative to <paramref name="origin"/> (and last frame's too, for <paramref name="motion"/>). Any thread:
    /// writes its own entries only.
    /// </summary>
    void PlaceParts(in Live l, Vector3 origin, bool motion)
    {
        var world = Relative(l.World, origin);
        float worldScale = world.ScaleLength();
        var previousWorld = motion ? Relative(l.State.PreviousWorld, origin) : default;
        var parts = l.Asset.Parts;
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            ref var f = ref _partFrames[l.PartBase + p];
            f.Shows = part.ShowsWith(l.WeaponDrawn, l.RangedDrawn, l.Shots, l.BackDrawn);
            if (!f.Shows) continue;
            f.Model = part.Shared ? world : _attach[l.AttachBase + part.AttachIndex] * world;
            f.Scale = part.Shared ? worldScale : f.Model.ScaleLength();
            var centre = Vector3.Transform(part.BoundsCentre, f.Model);
            f.Lod = MeshLod.Value(Vector3.Zero, centre, part.BoundsRadius * f.Scale);
            f.FileLod = MeshLod.Value(Vector3.Zero, centre, part.FileRadius * f.Scale);
            if (motion)
                f.PreviousModel = part.Shared ? previousWorld : l.State.PreviousAttach[part.AttachIndex] * previousWorld;
        }
    }

    /// <summary>
    /// A batch's instance as one chunk of characters gathered it: the batch's id, its place there (set in character order) and the instance.
    /// </summary>
    struct Gathered
    {
        /// <summary>
        /// The batch's id.
        /// </summary>
        public int Batch;
        /// <summary>
        /// Its place among the batch's instances (set in character order).
        /// </summary>
        public int Slot;
        /// <summary>
        /// The instance row.
        /// </summary>
        public CharInstance Instance;
    }

    /// <summary>
    /// What one chunk of <see cref="ChunkCharacters"/> characters adds to a view, in order.
    /// </summary>
    sealed class Chunk
    {
        /// <summary>
        /// The gathered instances (the first <see cref="Count"/>), grown as needed.
        /// </summary>
        public Gathered[] Items = new Gathered[256];
        /// <summary>
        /// How many instances, characters and part instances the chunk gathered.
        /// </summary>
        public int Count, Characters, Parts;

        /// <summary>
        /// Appends an instance of batch <paramref name="batch"/> (its slot is set later).
        /// </summary>
        public void Add(int batch, in CharInstance instance)
        {
            if (Count == Items.Length) Array.Resize(ref Items, Count * 2);
            ref var g = ref Items[Count++];
            (g.Batch, g.Instance) = (batch, instance);
        }
    }

    /// <summary>
    /// How many characters one chunk gathers.
    /// </summary>
    const int ChunkCharacters = 16;
    /// <summary>
    /// The mesh levels a part's batch cache holds (<see cref="AssetPart.Batches"/>); deeper ones are looked up.
    /// </summary>
    const int CachedLevels = 8;

    /// <summary>
    /// The views' instances from the placed parts (<see cref="PartFrame"/>): chunks of characters in parallel, each character culled and its parts'
    /// levels chosen for every view in turn. Returns the number of chunks; the views' batches are filled by <see cref="AssignSlots"/>.
    /// </summary>
    int Gather(GatherView[] views, int viewCount, bool depth, bool motion)
    {
        int count = (_liveCount + ChunkCharacters - 1) / ChunkCharacters;
        for (int k = 0; k < viewCount; k++)
        {
            var chunks = views[k].Batches.Chunks;
            while (chunks.Count < count) chunks.Add(new Chunk());
            views[k].Batches.ChunkCount = count;
        }
        Parallel.For(0, count, c => GatherChunk(c, c * ChunkCharacters, Math.Min(_liveCount, (c + 1) * ChunkCharacters), views, viewCount, depth,
            motion));
        return count;
    }

    /// <summary>
    /// Chunk <paramref name="c"/>'s characters (<paramref name="from"/> to <paramref name="to"/>) into each view's chunk: culled per view, then their
    /// parts.
    /// </summary>
    void GatherChunk(int c, int from, int to, GatherView[] views, int viewCount, bool depth, bool motion)
    {
        for (int k = 0; k < viewCount; k++)
        {
            var chunk = views[k].Batches.Chunks[c];
            chunk.Count = chunk.Characters = chunk.Parts = 0;
        }

        float bias = depth ? LodBias * ShadowLodBias : LodBias;
        for (int i = from; i < to; i++)
        {
            ref var l = ref _live[i];
            if (l.PortraitOnly) continue;
            uint seen = SeenBy(in l, c, views, viewCount, depth);
            if (seen == 0) continue;

            var parts = l.Asset.Parts;
            for (int p = 0; p < parts.Count; p++)
            {
                ref readonly var f = ref _partFrames[l.PartBase + p];
                if (f.Shows) GatherPart(in l, parts[p], in f, c, seen, views, viewCount, depth, motion, bias);
            }
        }
    }

    /// <summary>
    /// The views (a bit each) whose frustum holds the character and, outside the shadow passes, whose fog does not hide it; counted in their chunks.
    /// </summary>
    uint SeenBy(in Live l, int c, GatherView[] views, int viewCount, bool depth)
    {
        uint seen = 0;
        for (int k = 0; k < viewCount; k++)
        {
            if (!FrustumTests.SphereVisible(views[k].Frustum, l.Centre, l.Radius)) continue;
            if (!depth && FogCull?.Hidden(l.Centre, l.Radius, FogVolumes.CullKind.Characters) == true) continue;
            seen |= 1u << k;
            views[k].Batches.Chunks[c].Characters++;
        }
        return seen;
    }

    /// <summary>
    /// One placed part's instance, added to the chunk of every view in <paramref name="seen"/> at the level that view chooses.
    /// </summary>
    void GatherPart(in Live l, AssetPart part, in PartFrame f, int c, uint seen, GatherView[] views, int viewCount, bool depth, bool motion,
        float bias)
    {
        var instance = new CharInstance
        {
            Model = part.IsBody ? WithBlood(f.Model, l.Blood) : f.Model,
            BoneBase = (uint)l.BoneBase,
            Material = (uint)part.Material.Slot,
            Flags = (l.Interior ? 1u : 0u) | HiddenFlags(part, l.Asset),
            MorphBase = part.Morphed ? l.Asset.MorphBase : 0,
            PreviousModel = motion ? f.PreviousModel : default,
        };
        float lod = f.Lod * bias;
        if (depth && HairInShadow(part, l.Asset)) lod *= CharacterContent.HairLodScale;

        bool twoSided = part.Material.DoubleSided;
        var gps = part.Mesh.Parts;
        for (int k = 0; k < viewCount; k++)
        {
            if ((seen & 1u << k) == 0) continue;
            ref readonly var v = ref views[k];
            if (depth && part.BoundsRadius * f.Scale < v.MinRadius) continue;
            int level = !GeneratedLod ? FileLevel(part, f.FileLod) : GeneratedLevel(part, depth ? Math.Max(lod, v.LodFloor) : lod, depth);

            var chunk = v.Batches.Chunks[c];
            chunk.Parts++;
            for (int g = 0; g < gps.Count; g++)
                if (GroupLevel(gps[g], level) is int lv) chunk.Add(BatchId(part, g, gps[g], lv, twoSided), in instance);
        }
    }

    /// <summary>
    /// Gives each gathered instance of <paramref name="vb"/> its place in its batch in character order (the order one thread adds them in), and the
    /// batches their counts.
    /// </summary>
    void AssignSlots(ViewBatches vb)
    {
        for (int c = 0; c < vb.ChunkCount; c++)
        {
            var chunk = vb.Chunks[c];
            vb.Characters += chunk.Characters;
            vb.Parts += chunk.Parts;
            for (int k = 0; k < chunk.Count; k++)
            {
                ref var g = ref chunk.Items[k];
                var batch = vb.Get(g.Batch, this);
                if (batch.Count == 0) vb.Active.Add(batch);
                g.Slot = batch.Count++;
            }
        }
    }

    /// <summary>
    /// The view's instance rows: its batches ordered (single-sided first, then the double-sided: the cull mode changes once) and laid out one after
    /// another in the frame's constants (the draws reach a batch by firstInstance).
    /// </summary>
    void AllocateRows(ViewBatches vb)
    {
        var active = vb.Active;
        active.Sort((a, b) => a.DoubleSided.CompareTo(b.DoubleSided));
        int total = 0;
        foreach (var b in active)
        {
            b.Offset = total;
            total += b.Count;
        }

        vb.Instances = Gpu.Frame.AllocateConstants((ulong)Math.Max(total, 1) * CharInstance.Size, 16);
    }

    /// <summary>
    /// The batch id of <paramref name="part"/>'s mesh part <paramref name="g"/> at level <paramref name="lv"/>: from the part's cache, else made or
    /// found under the lock (any thread).
    /// </summary>
    int BatchId(AssetPart part, int g, GpuObjectPart gp, int lv, bool twoSided)
    {
        int slot = (g * CachedLevels + lv) * 2 + (twoSided ? 1 : 0);
        if (lv < CachedLevels && part.Batches is { } cached && slot < cached.Length && cached[slot] > 0)
            return cached[slot] - 1;
        lock (_batchIds)
        {
            var key = (gp, lv, twoSided);
            if (!_batchIds.TryGetValue(key, out int id))
            {
                _batchIds[key] = id = _batchKeys.Count;
                _batchKeys.Add(key);
            }
            if (lv < CachedLevels)
            {
                var cache = part.Batches;
                if (cache is null || slot >= cache.Length)
                {
                    var grown = new int[part.Mesh.Parts.Count * CachedLevels * 2];
                    cache?.CopyTo(grown, 0);
                    part.Batches = cache = grown;
                }
                cache[slot] = id + 1;
            }
            return id;
        }
    }

    /// <summary>
    /// Writes chunk <paramref name="c"/>'s gathered instances into the view's instance rows, each at its batch's offset plus its place (any thread).
    /// </summary>
    void Scatter(ViewBatches vb, int c)
    {
        var chunk = vb.Chunks[c];
        var byId = vb.ById;
        byte* rows = (byte*)vb.Instances.Pointer;
        for (int k = 0; k < chunk.Count; k++)
        {
            ref var g = ref chunk.Items[k];
            *(CharInstance*)(rows + (long)(byId[g.Batch]!.Offset + g.Slot) * CharInstance.Size) = g.Instance;
        }
    }

    /// <summary>
    /// <paramref name="world"/> with <paramref name="origin"/> taken off its translation.
    /// </summary>
    static Matrix4x4 Relative(Matrix4x4 world, Vector3 origin)
    {
        world.Translation -= origin;
        return world;
    }

    /// <summary>
    /// <paramref name="m"/> (world to clip) for positions relative to <paramref name="origin"/>: translation(origin) × m, in double so the origin's
    /// large coordinates cancel exactly.
    /// </summary>
    static Matrix4x4 FromOrigin(Vector3 origin, in Matrix4x4 m)
    {
        double x = origin.X, y = origin.Y, z = origin.Z;
        var r = m;
        r.M41 = (float)(x * m.M11 + y * m.M21 + z * m.M31 + m.M41);
        r.M42 = (float)(x * m.M12 + y * m.M22 + z * m.M32 + m.M42);
        r.M43 = (float)(x * m.M13 + y * m.M23 + z * m.M33 + m.M43);
        r.M44 = (float)(x * m.M14 + y * m.M24 + z * m.M34 + m.M44);
        return r;
    }

    /// <summary>
    /// Each part's native state for the motion program, made on first use.
    /// </summary>
    readonly Dictionary<GpuObjectPart, MotionHolder> _motionNatives = [];
    /// <summary>
    /// A box for a part's motion native state, so it can be returned by reference.
    /// </summary>
    sealed class MotionHolder
    {
        /// <summary>
        /// The part's native state for the motion program.
        /// </summary>
        public ObjectNativeMesh Native;
    }

    /// <summary>
    /// The motion program's native state of <paramref name="part"/> by reference, its holder made on first use.
    /// </summary>
    ref ObjectNativeMesh MotionNative(GpuObjectPart part)
    {
        if (!_motionNatives.TryGetValue(part, out var h)) _motionNatives[part] = h = new MotionHolder();
        return ref h.Native;
    }

    /// <summary>
    /// A part's native state for <paramref name="p"/>, made on first use: the vertex layout (the per-instance rows at 7 to 10 and the data at 11 are
    /// bound once per segment).
    /// </summary>
    static void Current(ref ObjectNativeMesh n, GpuObjectPart part, ReflectedProgram p)
    {
        if (n.Layout is not null) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        part.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[CharacterShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32F, CharInstance.Size, true);
        attributes[CharacterShaders.DataLocation] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32Ui, CharInstance.Size, true);
        for (int a = 0; a < 4; a++)
            attributes[CharacterShaders.PreviousLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32F, CharInstance.Size, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = new GpuBufferBinding(part.Indices.Handle, 0, part.Indices.Size);
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    /// <summary>
    /// The matrix with the body's blood amounts in the w of its four rows (the vertex program reads only their xyz): two parts a float, part 2j in
    /// the low byte and part 2j + 1 in the next of row j's w, as an exact integer (CharacterShaders: <c>vBloodAmount</c>).
    /// </summary>
    static Matrix4x4 WithBlood(Matrix4x4 m, ulong blood)
    {
        if (blood == 0) return m;
        static float Pair(ulong b, int j) => (float)((b >> (16 * j)) & 0xFFFF);
        m.M14 = Pair(blood, 0);
        m.M24 = Pair(blood, 1);
        m.M34 = Pair(blood, 2);
        m.M44 = Pair(blood, 3);
        return m;
    }

    /// <summary>
    /// The pool of draw jobs, each returned after recording.
    /// </summary>
    readonly JobPool<DrawJob> _drawJobs = new();

    /// <summary>
    /// One characters segment as prepared (<see cref="DrawView"/>): recorded on any thread.
    /// </summary>
    sealed class DrawJob : RecordJob
    {
        /// <summary>
        /// One instanced draw of a batch.
        /// </summary>
        public struct Draw
        {
            /// <summary>
            /// The pipeline it is drawn with.
            /// </summary>
            public GpuPipeline Pipeline;
            /// <summary>
            /// The part's vertex buffers.
            /// </summary>
            public GpuBufferBinding[] Vertices;
            /// <summary>
            /// The index buffer at the level's first index.
            /// </summary>
            public GpuBufferBinding Elements;
            /// <summary>
            /// The level's index count, the batch's instance count and its first instance among the rows.
            /// </summary>
            public uint IndexCount, Instances, FirstInstance;
            /// <summary>
            /// Drawn without back-face culling.
            /// </summary>
            public bool TwoSided;
        }

        /// <summary>
        /// The renderer whose pool the job returns to.
        /// </summary>
        public CharacterRenderer Owner = null!;
        /// <summary>
        /// The pass targets (viewport and scissor).
        /// </summary>
        public PassTargets Targets = null!;
        /// <summary>
        /// The pass's depth, bias and raster state.
        /// </summary>
        public DrawState State = null!;
        /// <summary>
        /// The pass's program.
        /// </summary>
        public GpuProgram Program = default;
        /// <summary>
        /// The native frame sets of the segment.
        /// </summary>
        public FrameBinding Frame;
        /// <summary>
        /// The face culling of single-sided draws (none for the depth passes).
        /// </summary>
        public CullMode Cull;
        /// <summary>
        /// The instance row bindings, one per 16-byte column of an instance.
        /// </summary>
        public readonly GpuBufferBinding[] Rows = new GpuBufferBinding[9];
        /// <summary>
        /// How many of <see cref="Rows"/> are bound: 9 for the motion pass (with last frame's), else 5.
        /// </summary>
        public int RowCount;
        /// <summary>
        /// The push constants: the motion depth, the fill light and the skin table.
        /// </summary>
        public CharacterPush Push;
        /// <summary>
        /// The draws (the first <see cref="Count"/>), grown as needed.
        /// </summary>
        public Draw[] Draws = new Draw[64];
        /// <summary>
        /// How many draws are recorded.
        /// </summary>
        public int Count;
        /// <inheritdoc/>
        public override int Size => Count;

        public override void Record(IGpuCommandList cmd)
        {
            var (targets, state) = (Targets, State);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, Program, in Frame);
            cmd.BindVertexBuffers(CharacterShaders.InstanceLocation, Rows.AsSpan(0, RowCount));
            var push = Push;
            cmd.PushConstants(Program, GpuStage.Vertex | GpuStage.Fragment, in push);
            bool? twoSided = null;
            for (int i = 0; i < Count; i++)
            {
                ref readonly var d = ref Draws[i];
                if (twoSided != d.TwoSided)
                {
                    cmd.SetRaster(d.TwoSided ? CullMode.None : Cull, state.Front);
                    twoSided = d.TwoSided;
                }
                cmd.BindPipeline(d.Pipeline);
                cmd.BindVertexBuffers(0, d.Vertices);
                cmd.BindIndexBuffer(d.Elements, IndexKind.U32);
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
}

/// <summary>
/// Helpers on <see cref="Matrix4x4"/> for the characters' drawing.
/// </summary>
static class MatrixExtensions
{
    /// <summary>
    /// The largest axis scale of a transform's upper 3x3 (a bounding radius through it).
    /// </summary>
    public static float ScaleLength(this in Matrix4x4 m) =>
        MathF.Sqrt(Math.Max(new Vector3(m.M11, m.M12, m.M13).LengthSquared(),
            Math.Max(new Vector3(m.M21, m.M22, m.M23).LengthSquared(), new Vector3(m.M31, m.M32, m.M33).LengthSquared())));
}