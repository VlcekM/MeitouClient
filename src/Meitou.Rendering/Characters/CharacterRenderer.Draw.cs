using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    // ------------------------------------------------------------------ drawing

    internal sealed class Batch
    {
        public required GpuObjectPart Part;
        public int Level;
        public bool DoubleSided;
        public CharInstance[] Data = new CharInstance[16];
        public int Count, Offset;

        public void Add(in CharInstance i)
        {
            if (Count == Data.Length) Array.Resize(ref Data, Count * 2);
            Data[Count++] = i;
        }
    }

    // A batch is named by an id per (mesh part, level, sidedness), the same in every view; each view keeps its own Batch per id (ViewBatches).
    readonly Dictionary<(GpuObjectPart, int, bool), int> batchIds = [];
    readonly List<(GpuObjectPart Part, int Level, bool TwoSided)> batchKeys = [];

    /// <summary>One view's batches by id, those it fills, its chunks of gathered instances and its instance rows.</summary>
    sealed class ViewBatches
    {
        public Batch?[] ById = new Batch?[256];
        public readonly List<Batch> Active = [];
        public readonly List<Chunk> Chunks = [];
        public Transient Instances;
        public int Characters, Parts, ChunkCount;
        /// <summary>A shadow cascade's (<see cref="PrepareDepth"/>): the update, GPU frame and frustum it was gathered for.</summary>
        public long Stamp = -1, Frame = -1;
        public Vector4[]? Frustum;

        public void Begin()
        {
            foreach (var b in Active) b.Count = 0;
            Active.Clear();
            Characters = Parts = ChunkCount = 0;
        }

        public Batch Get(int id, CharacterRenderer owner)
        {
            if (id >= ById.Length) Array.Resize(ref ById, Math.Max(id + 1, ById.Length * 2));
            return ById[id] ??= owner.NewBatch(id);
        }
    }

    Batch NewBatch(int id)
    {
        lock (batchIds)
        {
            var (part, level, twoSided) = batchKeys[id];
            return new Batch { Part = part, Level = level, DoubleSided = twoSided };
        }
    }

    readonly ViewBatches mainView = new();
    // The shadow cascades' views by cascade index (PrepareDepth).
    ViewBatches?[] cascadeViews = [];
    bool depthPass, motionPass;

    /// <summary>A view the placed parts are gathered for: its frustum, the shadow LOD floor and smallest caster radius (a depth view), and its batches.</summary>
    struct GatherView
    {
        public Vector4[] Frustum;
        public float LodFloor, MinRadius;
        public ViewBatches Batches;
    }

    readonly GatherView[] oneView = new GatherView[1];
    GatherView[] depthViews = [];

    /// <summary>The fog cull (set once by the world frame; the colour and motion passes use it, not the shadow cascades): characters wholly hidden by the fog the eye is in are skipped.</summary>
    internal FogVolumes? FogCull { get; set; }

    /// <summary>Room masks used to give indoor characters the same indirect-light gain as their surroundings.</summary>
    internal WorldObjectRenderer? InteriorObjects { get; set; }

    /// <summary>
    /// Draws the characters seen from <paramref name="eye"/> through <paramref name="frustum"/> into the open pass. <paramref name="relative"/>, when the caller has it, is the
    /// same view-projection for positions relative to the eye (the view's rotation only): exact, where one made from <paramref name="viewProjection"/> carries the rounding of
    /// the eye's world position (<see cref="DrawView"/>).
    /// </summary>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, Vector3 light, Vector3 fogColour, float fogDistance, Matrix4x4? relative = null) =>
        DrawView(new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = light, FogColour = fogColour, FogDistance = fogDistance }, eye, frustum, relative: relative);

    /// <summary>
    /// Draws the characters' depth for a shadow cascade (<see cref="CharacterShaders.DepthFragment"/>); levels chosen from <paramref name="lodEye"/>, never finer than
    /// <paramref name="lodFloor"/> (<see cref="ShadowLod.ObjectLodFloor"/> of a far cascade's texel; 0: the distance rule alone), as the objects. A part whose bounding
    /// radius is under <paramref name="minRadius"/> (a far cascade: half a texel, so it would cover less than one; 0: none) casts nothing there: hair, beards, small
    /// attachments and weapons leave the far maps, as the foliage's small meshes do (<c>ShadowPass.MinFoliageCasterTexels</c>).
    /// </summary>
    /// <param name="cascade">The shadow cascade's index: its instances as <see cref="PrepareDepth"/> gathered them this frame, when it did (-1: gathered here).</param>
    public void DrawDepth(Matrix4x4 worldToClip, Vector3 lodEye, Vector4[] frustum, float lodFloor = 0, float minRadius = 0, int cascade = -1)
    {
        var prepared = (uint)cascade < (uint)cascadeViews.Length && cascadeViews[cascade] is { } cv && cv.Stamp == updateStamp && cv.Frame == Gpu.Frame.Number && cv.Frustum == frustum ? cv : null;
        depthPass = true;
        try { DrawView(new ViewConstants { ViewProjection = worldToClip, Eye = lodEye }, lodEye, frustum, lodFloor, minRadius, prepared); }
        finally { depthPass = false; }
    }

    /// <summary>
    /// The shadow cascades about to be drawn this frame, gathered together from the update's placed parts (<paramref name="lodEye"/> is its eye; another eye
    /// gathers in each <see cref="DrawDepth"/>): one walk over the characters in parallel chunks, each character and part tested against every cascade, then each
    /// cascade's batches and instance rows filled in parallel. <see cref="DrawDepth"/> with a cascade's index then only records. In the frame, before the cascades.
    /// </summary>
    public void PrepareDepth(Vector3 lodEye, ReadOnlySpan<(int Index, Vector4[] Frustum, float LodFloor, float MinRadius)> cascades)
    {
        if (cascades.IsEmpty || portraitLive >= 0 || partStamp != updateStamp || lodEye != partOrigin) return;
        if (depthViews.Length < cascades.Length) depthViews = new GatherView[cascades.Length];
        foreach (var (index, frustum, lodFloor, minRadius) in cascades)
            if (index >= cascadeViews.Length) Array.Resize(ref cascadeViews, index + 1);
        for (int k = 0; k < cascades.Length; k++)
        {
            var (index, frustum, lodFloor, minRadius) = cascades[k];
            var vb = cascadeViews[index] ??= new ViewBatches();
            vb.Begin();
            (vb.Stamp, vb.Frame, vb.Frustum) = (updateStamp, Gpu.Frame.Number, frustum);
            depthViews[k] = new GatherView { Frustum = frustum, LodFloor = lodFloor, MinRadius = minRadius, Batches = vb };
        }
        int views = cascades.Length;
        int chunkCount = Gather(depthViews, views, depth: true, motion: false);
        var gathered = depthViews;
        Parallel.For(0, views, k => AssignSlots(gathered[k].Batches));
        for (int k = 0; k < views; k++) AllocateRows(depthViews[k].Batches);
        Parallel.For(0, views * chunkCount, n => Scatter(gathered[n / chunkCount].Batches, n % chunkCount));
        for (int k = 0; k < views; k++) depthViews[k] = default;
    }

    void DrawView(in ViewConstants worldView, Vector3 eye, Vector4[] frustum, float lodFloor = 0, float minRadius = 0, ViewBatches? prepared = null, Matrix4x4? relative = null, Matrix4x4? previousRelative = null)
    {
        var cpu = Stopwatch.StartNew();
        // Drawn relative to the view's eye (the vertex program adds it back for the shading): skinned in world floats, an animated vertex far from
        // the origin (the Hub, tens of thousands of units out) rounds to a different step every frame and the character visibly jiggles.
        // The view-projection for those positions is the caller's rotation-only one when it has it: FromOrigin can cancel the eye's coordinates exactly, but not the rounding
        // already in a matrix built with them (about 0.004 units at 50000, a different error every frame: 0.3 px at the closest zoom, and as much noise in the velocity).
        var origin = worldView.Eye;
        var view = worldView;
        view.ViewProjection = relative ?? FromOrigin(origin, worldView.ViewProjection);
        view.PreviousViewProjection = previousRelative ?? FromOrigin(origin, worldView.PreviousViewProjection);
        DrawCalls = 0;
        DrawnTriangles = 0;
        Array.Clear(LevelParts);
        Array.Clear(LevelTriangles);
        var vb = prepared ?? mainView;
        if (prepared is null) Collect(vb, eye, origin, frustum, lodFloor, minRadius);
        (DrawnCharacters, DrawnParts) = (vb.Characters, vb.Parts);
        var active = vb.Active;
        if (active.Count == 0) { LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds; return; }
        // The instances were laid out when the view was gathered (AllocateRows), each batch contiguous and reached by firstInstance.
        var instances = vb.Instances;

        // 3. One draw per batch.
        var prog = motionPass ? motionProg : depthPass ? depthProg : colourProg;
        string label = motionPass ? "characters motion" : depthPass ? "characters depth" : "characters";
        var targets = Gpu.CurrentTargets();
        var state = Gpu.CurrentState() with { Cull = CullMode.None };
        // The colour pass writes alpha 1 (the scene host masks it off for everything else): the SSAO character mask (PostProcess).
        if (!depthPass && !motionPass) state = state with { ColourMask = DrawState.Rgba };
        var job = drawJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program, job.Count) = (this, targets, state, prog.P, 0);
        job.Frame = nativeFrame.Prepare(in view, [bones.Binding, content.Materials.Binding(Gpu.Frame), content.Morphs.Binding(Gpu.Frame), previousBones.Binding]);
        job.Cull = depthPass ? CullMode.None : CullMode.Back;
        for (int a = 0; a < 9; a++) job.Rows[a] = new GpuBufferBinding(instances.Handle, instances.Offset + (ulong)(16 * a));
        job.Push = new CharacterPush { DepthIndex = motionPass ? motionDepth : 0, Fill = new(Fill, 0), SkinLut = SkinShading ? skinScattering.Index : 0 };
        job.RowCount = motionPass ? 9 : 5;
        if (job.Draws.Length < active.Count) job.Draws = new DrawJob.Draw[Math.Max(active.Count, job.Draws.Length * 2)];
        int n = 0;
        foreach (var b in active)
        {
            var part = b.Part;
            ref var native = ref (motionPass ? ref MotionNative(part) : ref depthPass ? ref part.DepthNative : ref part.ColourNative);
            Current(ref native, part, prog);
            job.Draws[n++] = new DrawJob.Draw
            {
                Pipeline = Gpu.Pipelines.Get(state.Pipeline(prog.P, native.Layout!, PrimitiveType.Triangles, targets.Formats, label)),
                Vertices = native.Vertices, Elements = new GpuBufferBinding(native.Elements.Buffer, native.Elements.Offset + (ulong)part.Offset[b.Level] * 4),
                IndexCount = (uint)part.Count[b.Level], Instances = (uint)b.Count, FirstInstance = (uint)b.Offset, TwoSided = b.DoubleSided,
            };
            DrawCalls++;
            DrawnTriangles += (long)part.Count[b.Level] / 3 * b.Count;
            LevelParts[Math.Min(b.Level, 7)] += b.Count;
            LevelTriangles[Math.Min(b.Level, 7)] += (long)part.Count[b.Level] / 3 * b.Count;
        }
        job.Count = n;
        var timer = depthPass ? depthTimer : colourTimer;
        if (portraitLive < 0) timer.Begin();
        Gpu.Record(label, job);
        if (portraitLive < 0) timer.End();
        LastDrawCpuMs = cpu.Elapsed.TotalMilliseconds;
        if (!depthPass && portraitLive < 0) drawCpu.Add(LastDrawCpuMs);
    }

    /// <summary>
    /// 1. and 2. of a view gathered on its own: cull, choose each part's level and group the instances by (mesh part, level, sidedness), then lay out the instance rows.
    /// The world's views from the update's eye take the parts it placed and gather in parallel; a portrait (portraitLive, that one character, unculled) or another eye places its own.
    /// </summary>
    void Collect(ViewBatches vb, Vector3 eye, Vector3 origin, Vector4[] frustum, float lodFloor, float minRadius)
    {
        vb.Begin();
        bool placed = portraitLive < 0 && partStamp == updateStamp && origin == partOrigin && eye == origin && (partMotion || !motionPass);
        if (placed)
        {
            oneView[0] = new GatherView { Frustum = frustum, LodFloor = lodFloor, MinRadius = minRadius, Batches = vb };
            Gather(oneView, 1, depthPass, motionPass);
            oneView[0] = default;
            AssignSlots(vb);
            if (vb.Active.Count == 0) return;
            AllocateRows(vb);
            var view = vb;
            Parallel.For(0, vb.ChunkCount, c => Scatter(view, c));
            return;
        }
        for (int i = portraitLive >= 0 ? portraitLive : 0, end = portraitLive >= 0 ? portraitLive + 1 : liveCount; i < end; i++)
        {
            ref var l = ref live[i];
            if (portraitLive < 0 && (l.PortraitOnly || !FrustumTests.SphereVisible(frustum, l.Centre, l.Radius))) continue;
            if (!depthPass && portraitLive < 0 && FogCull?.Hidden(l.Centre, l.Radius, FogVolumes.CullKind.Characters) == true) continue;
            vb.Characters++;
            bool interior = portraitLive < 0 && l.Interior;
            var world = Relative(l.World, origin);
            float worldScale = world.ScaleLength();
            var parts = l.Asset.Parts;
            for (int p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                if (!part.ShowsWith(l.WeaponDrawn, l.RangedDrawn, l.Shots, l.BackDrawn)) continue;
                Matrix4x4 model = world;
                if (!part.Shared) model = attach[l.AttachBase + part.AttachIndex] * world;
                float modelScale = part.Shared ? worldScale : model.ScaleLength();
                var mesh = part.Mesh;
                if (depthPass && part.BoundsRadius * modelScale < minRadius) continue;
                int level;
                if (!GeneratedLod)
                {
                    // Faithful: the file's own levels by the original's distance_sphere value (its stored radius, bias 1), the shadow passes as the picture
                    // (docs/characters.md "LOD of character meshes").
                    float own = MeshLod.Value(eye - origin, Vector3.Transform(part.BoundsCentre, model), part.FileRadius * modelScale);
                    level = part.FileDistances is { } fileLevels && MeshLod.Select(fileLevels, own) is int f && f > 0 ? part.FileFirst + f - 1 : 0;
                }
                else
                {
                    // The model is relative to the view's eye (above), so the LOD eye is too: with the world eye every part far from the origin took its last level.
                    float value = MeshLod.Value(eye - origin, Vector3.Transform(part.BoundsCentre, model), part.BoundsRadius * modelScale, depthPass ? LodBias * ShadowLodBias : LodBias);
                    // In a shadow map hair is a silhouette, so it takes the rule's own distances there (its levels start CharacterContent.HairLodScale times farther for the picture).
                    if (depthPass) value = Math.Max(part.Material.Shading == CharacterShaders.ShadeHair && !l.Asset.Animal ? value * CharacterContent.HairLodScale : value, lodFloor);
                    level = MeshLod.Select(mesh.Distances, value);
                    if (depthPass && part.ShadowDistances is { } shadowLevels && MeshLod.Select(shadowLevels, value) is int k && k > 0) level = part.ShadowFirst + k - 1;   // the body's shadow-only levels
                }
                bool twoSided = part.Material.DoubleSided;
                bool headless = part.IsBody && l.Asset.Appearance is { Headless: true };
                // A headless body (Meitou decapitation) hides its head and is drawn two-sided: the inside seen through the neck shows as the wound (CharacterShaders.Headless).
                twoSided |= headless;
                uint hidden = (part.IsBody && l.Asset.Appearance is { } look ? (uint)look.HiddenLimbs << 12 | (uint)(look.HiddenParts & 0x3FF) << 18 : 0u) | (uint)part.HideSide << 16 | (headless ? CharacterShaders.Headless : 0u);
                var instance = new CharInstance { Model = part.IsBody ? WithBlood(model, l.Blood) : model, BoneBase = (uint)l.BoneBase, Material = (uint)part.Material.Slot, Flags = (interior ? 1u : 0u) | hidden, MorphBase = part.Morphed ? l.Asset.MorphBase : 0 };
                if (motionPass)
                {
                    var previousWorld = Relative(l.State.PreviousWorld, origin);
                    instance.PreviousModel = part.Shared ? previousWorld : l.State.PreviousAttach[part.AttachIndex] * previousWorld;
                }
                vb.Parts++;
                var gps = mesh.Parts;
                for (int g = 0; g < gps.Count; g++)
                {
                    var gp = gps[g];
                    int lv = Math.Min(level, gp.Count.Length - 1);
                    while (lv > 0 && gp.Count[lv] == 0) lv--;
                    if (gp.Count[lv] == 0) continue;
                    var batch = vb.Get(BatchId(part, g, gp, lv, twoSided), this);
                    if (batch.Count == 0) vb.Active.Add(batch);
                    batch.Add(in instance);
                }
            }
        }
        if (vb.Active.Count == 0) return;
        AllocateRows(vb);
        var target = new Span<byte>(vb.Instances.Pointer, vb.Active.Sum(b => b.Count) * CharInstance.Size);
        foreach (var b in vb.Active)
            MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * CharInstance.Size)..]);
    }

    // ------------------------------------------------------------------ parts placed once a frame

    /// <summary>
    /// One part of a character in range as <see cref="Update"/> placed it, relative to its eye (<see cref="partOrigin"/>): the colour, motion and shadow views are all
    /// drawn from that eye, so they share the matrices and the distance the levels are chosen by.
    /// </summary>
    struct PartFrame
    {
        public Matrix4x4 Model, PreviousModel;
        /// <summary>The model's largest axis scale.</summary>
        public float Scale;
        /// <summary>The LOD values at bias 1 (<see cref="MeshLod.Value"/>): by the bounds radius (the generated levels) and by the file's radius (Faithful).</summary>
        public float Lod, FileLod;
        /// <summary>Shown with the character's weapon state (<see cref="AssetPart.ShowsWith"/>); the rest is not set when not.</summary>
        public bool Shows;
    }

    PartFrame[] partFrames = new PartFrame[1024];
    long partStamp = -1;
    Vector3 partOrigin;
    bool partMotion;

    /// <summary>Places <paramref name="l"/>'s parts relative to <paramref name="origin"/> (and last frame's too, for <paramref name="motion"/>). Any thread: writes its own entries only.</summary>
    void PlaceParts(in Live l, Vector3 origin, bool motion)
    {
        var world = Relative(l.World, origin);
        float worldScale = world.ScaleLength();
        var previousWorld = motion ? Relative(l.State.PreviousWorld, origin) : default;
        var parts = l.Asset.Parts;
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            ref var f = ref partFrames[l.PartBase + p];
            f.Shows = part.ShowsWith(l.WeaponDrawn, l.RangedDrawn, l.Shots, l.BackDrawn);
            if (!f.Shows) continue;
            f.Model = part.Shared ? world : attach[l.AttachBase + part.AttachIndex] * world;
            f.Scale = part.Shared ? worldScale : f.Model.ScaleLength();
            var centre = Vector3.Transform(part.BoundsCentre, f.Model);
            f.Lod = MeshLod.Value(Vector3.Zero, centre, part.BoundsRadius * f.Scale);
            f.FileLod = MeshLod.Value(Vector3.Zero, centre, part.FileRadius * f.Scale);
            if (motion) f.PreviousModel = part.Shared ? previousWorld : l.State.PreviousAttach[part.AttachIndex] * previousWorld;
        }
    }

    /// <summary>A batch's instance as one chunk of characters gathered it: the batch's id, its place there (set in character order) and the instance.</summary>
    struct Gathered
    {
        public int Batch;
        public int Slot;
        public CharInstance Instance;
    }

    /// <summary>What one chunk of <see cref="ChunkCharacters"/> characters adds to a view, in order.</summary>
    sealed class Chunk
    {
        public Gathered[] Items = new Gathered[256];
        public int Count, Characters, Parts;

        public void Add(int batch, in CharInstance instance)
        {
            if (Count == Items.Length) Array.Resize(ref Items, Count * 2);
            ref var g = ref Items[Count++];
            (g.Batch, g.Instance) = (batch, instance);
        }
    }

    const int ChunkCharacters = 16;
    /// <summary>The mesh levels a part's batch cache holds (<see cref="AssetPart.Batches"/>); deeper ones are looked up.</summary>
    const int CachedLevels = 8;

    /// <summary>
    /// The views' instances from the placed parts (<see cref="PartFrame"/>): chunks of characters in parallel, each character culled and its parts' levels chosen for every
    /// view in turn. Returns the number of chunks; the views' batches are filled by <see cref="AssignSlots"/>.
    /// </summary>
    int Gather(GatherView[] views, int viewCount, bool depth, bool motion)
    {
        int count = (liveCount + ChunkCharacters - 1) / ChunkCharacters;
        for (int k = 0; k < viewCount; k++)
        {
            var chunks = views[k].Batches.Chunks;
            while (chunks.Count < count) chunks.Add(new Chunk());
            views[k].Batches.ChunkCount = count;
        }
        Parallel.For(0, count, c => GatherChunk(c, c * ChunkCharacters, Math.Min(liveCount, (c + 1) * ChunkCharacters), views, viewCount, depth, motion));
        return count;
    }

    void GatherChunk(int c, int from, int to, GatherView[] views, int viewCount, bool depth, bool motion)
    {
        for (int k = 0; k < viewCount; k++) { var chunk = views[k].Batches.Chunks[c]; chunk.Count = chunk.Characters = chunk.Parts = 0; }
        float bias = depth ? LodBias * ShadowLodBias : LodBias;
        for (int i = from; i < to; i++)
        {
            ref var l = ref live[i];
            if (l.PortraitOnly) continue;
            uint seen = 0;
            for (int k = 0; k < viewCount; k++)
                if (FrustumTests.SphereVisible(views[k].Frustum, l.Centre, l.Radius) && (depth || FogCull?.Hidden(l.Centre, l.Radius, FogVolumes.CullKind.Characters) != true))
                {
                    seen |= 1u << k;
                    views[k].Batches.Chunks[c].Characters++;
                }
            if (seen == 0) continue;
            var parts = l.Asset.Parts;
            for (int p = 0; p < parts.Count; p++)
            {
                ref readonly var f = ref partFrames[l.PartBase + p];
                if (!f.Shows) continue;
                var part = parts[p];
                var mesh = part.Mesh;
                bool twoSided = part.Material.DoubleSided;
                bool headless = part.IsBody && l.Asset.Appearance is { Headless: true };
                // A headless body (Meitou decapitation) hides its head and is drawn two-sided: the inside seen through the neck shows as the wound (CharacterShaders.Headless).
                twoSided |= headless;
                uint hidden = (part.IsBody && l.Asset.Appearance is { } look ? (uint)look.HiddenLimbs << 12 | (uint)(look.HiddenParts & 0x3FF) << 18 : 0u) | (uint)part.HideSide << 16 | (headless ? CharacterShaders.Headless : 0u);
                var instance = new CharInstance
                {
                    Model = part.IsBody ? WithBlood(f.Model, l.Blood) : f.Model, BoneBase = (uint)l.BoneBase, Material = (uint)part.Material.Slot, Flags = (l.Interior ? 1u : 0u) | hidden,
                    MorphBase = part.Morphed ? l.Asset.MorphBase : 0, PreviousModel = motion ? f.PreviousModel : default,
                };
                // In a shadow map hair is a silhouette, so it takes the rule's own distances there (its levels start CharacterContent.HairLodScale times farther for the picture).
                float lod = f.Lod * bias;
                if (depth && part.Material.Shading == CharacterShaders.ShadeHair && !l.Asset.Animal) lod *= CharacterContent.HairLodScale;
                for (int k = 0; k < viewCount; k++)
                {
                    if ((seen & 1u << k) == 0) continue;
                    ref readonly var v = ref views[k];
                    if (depth && part.BoundsRadius * f.Scale < v.MinRadius) continue;
                    int level;
                    if (!GeneratedLod) level = part.FileDistances is { } fileLevels && MeshLod.Select(fileLevels, f.FileLod) is int fl && fl > 0 ? part.FileFirst + fl - 1 : 0;
                    else
                    {
                        float value = depth ? Math.Max(lod, v.LodFloor) : lod;
                        level = MeshLod.Select(mesh.Distances, value);
                        if (depth && part.ShadowDistances is { } shadowLevels && MeshLod.Select(shadowLevels, value) is int s && s > 0) level = part.ShadowFirst + s - 1;   // the body's shadow-only levels
                    }
                    var chunk = v.Batches.Chunks[c];
                    chunk.Parts++;
                    var gps = mesh.Parts;
                    for (int g = 0; g < gps.Count; g++)
                    {
                        var gp = gps[g];
                        int lv = Math.Min(level, gp.Count.Length - 1);
                        while (lv > 0 && gp.Count[lv] == 0) lv--;
                        if (gp.Count[lv] == 0) continue;
                        chunk.Add(BatchId(part, g, gp, lv, twoSided), in instance);
                    }
                }
            }
        }
    }

    /// <summary>Gives each gathered instance of <paramref name="vb"/> its place in its batch in character order (the order one thread adds them in), and the batches their counts.</summary>
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

    /// <summary>The view's instance rows: its batches ordered (single-sided first, then the double-sided: the cull mode changes once) and laid out one after another in the frame's constants (the draws reach a batch by firstInstance).</summary>
    void AllocateRows(ViewBatches vb)
    {
        var active = vb.Active;
        active.Sort((a, b) => a.DoubleSided.CompareTo(b.DoubleSided));
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        vb.Instances = Gpu.Frame.AllocateConstants((ulong)Math.Max(total, 1) * CharInstance.Size, 16);
    }

    /// <summary>The batch id of <paramref name="part"/>'s mesh part <paramref name="g"/> at level <paramref name="lv"/>: from the part's cache, else made or found under the lock (any thread).</summary>
    int BatchId(AssetPart part, int g, GpuObjectPart gp, int lv, bool twoSided)
    {
        int slot = (g * CachedLevels + lv) * 2 + (twoSided ? 1 : 0);
        if (lv < CachedLevels && part.Batches is { } cached && slot < cached.Length && cached[slot] > 0) return cached[slot] - 1;
        lock (batchIds)
        {
            var key = (gp, lv, twoSided);
            if (!batchIds.TryGetValue(key, out int id))
            {
                batchIds[key] = id = batchKeys.Count;
                batchKeys.Add(key);
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

    /// <summary>Writes chunk <paramref name="c"/>'s gathered instances into the view's instance rows, each at its batch's offset plus its place (any thread).</summary>
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

    /// <summary><paramref name="world"/> with <paramref name="origin"/> taken off its translation.</summary>
    static Matrix4x4 Relative(Matrix4x4 world, Vector3 origin)
    {
        world.Translation -= origin;
        return world;
    }

    /// <summary><paramref name="m"/> (world to clip) for positions relative to <paramref name="origin"/>: translation(origin) × m, in double so the
    /// origin's large coordinates cancel exactly.</summary>
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

    readonly Dictionary<GpuObjectPart, MotionHolder> motionNatives = [];
    sealed class MotionHolder { public ObjectNativeMesh Native; }
    ref ObjectNativeMesh MotionNative(GpuObjectPart part)
    {
        if (!motionNatives.TryGetValue(part, out var h)) motionNatives[part] = h = new MotionHolder();
        return ref h.Native;
    }

    /// <summary>A part's native state for <paramref name="p"/>, made on first use: the vertex layout (the per-instance rows at 7 to 10 and the data at 11 are bound once per segment).</summary>
    static void Current(ref ObjectNativeMesh n, GpuObjectPart part, ReflectedProgram p)
    {
        if (n.Layout is not null) return;
        Span<LegacyProgram.Attribute?> attributes = stackalloc LegacyProgram.Attribute?[16];
        part.Attributes.AsSpan().CopyTo(attributes);
        for (int a = 0; a < 4; a++)
            attributes[CharacterShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32f, CharInstance.Size, true);
        attributes[CharacterShaders.DataLocation] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32ui, CharInstance.Size, true);
        for (int a = 0; a < 4; a++)
            attributes[CharacterShaders.PreviousLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32f, CharInstance.Size, true);
        n.Layout = p.Layout(attributes);
        n.Vertices = p.Buffers(attributes, p.Own);
        n.Elements = new GpuBufferBinding(part.Indices.Handle, 0, part.Indices.Size);
        n.SegA = n.SegB = 0;
        n.PipeA = n.PipeB = null;
    }

    /// <summary>
    /// The matrix with the body's blood amounts in the w of its four rows (the vertex program reads only their xyz): two parts a float, part 2j in the low byte and part 2j + 1 in the next of
    /// row j's w, as an exact integer (CharacterShaders: <c>vBloodAmount</c>).
    /// </summary>
    static Matrix4x4 WithBlood(Matrix4x4 m, ulong blood)
    {
        if (blood == 0) return m;
        static float Pair(ulong b, int j) => (float)((b >> (16 * j)) & 0xFFFF);
        m.M14 = Pair(blood, 0); m.M24 = Pair(blood, 1); m.M34 = Pair(blood, 2); m.M44 = Pair(blood, 3);
        return m;
    }

    readonly JobPool<DrawJob> drawJobs = new();

    /// <summary>One characters segment as prepared (<see cref="DrawView"/>): recorded on any thread.</summary>
    sealed class DrawJob : RecordJob
    {
        public struct Draw
        {
            public GpuPipeline Pipeline;
            public GpuBufferBinding[] Vertices;
            public GpuBufferBinding Elements;
            public uint IndexCount, Instances, FirstInstance;
            public bool TwoSided;
        }

        public CharacterRenderer Owner = null!;
        public PassTargets Targets = null!;
        public DrawState State = null!;
        public GpuProgram Program = default;
        public FrameBinding Frame;
        public CullMode Cull;
        public readonly GpuBufferBinding[] Rows = new GpuBufferBinding[9];
        public int RowCount;
        public CharacterPush Push;
        public Draw[] Draws = new Draw[64];
        public int Count;
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
            Owner.drawJobs.Return(this);
        }
    }
}

static class MatrixExtensions
{
    /// <summary>The largest axis scale of a transform's upper 3x3 (a bounding radius through it).</summary>
    public static float ScaleLength(this in Matrix4x4 m) =>
        MathF.Sqrt(Math.Max(new Vector3(m.M11, m.M12, m.M13).LengthSquared(), Math.Max(new Vector3(m.M21, m.M22, m.M23).LengthSquared(), new Vector3(m.M31, m.M32, m.M33).LengthSquared())));
}
