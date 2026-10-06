using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Impostors;

namespace Meitou.Rendering;

/// <summary>
/// Far billboards (docs/impostors.md "Drawing"; the <c>impostors</c> switch). A foliage mesh with an impostor atlas is drawn, from its transition
/// distance on, as one camera-facing quad per instance sampling the atlas (<see cref="ImpostorDraw"/>), chosen per instance by the same cull
/// that places the meshes (its kernels, or <see cref="FoliageCull.CullGroup"/> on the CPU), crossfaded with the mesh over a band before the
/// transition by complementary dither. Shadow cascades get the impostors as casters beyond the transition (a depth-only quad facing the sun).
/// Atlases are loaded from the disk cache or baked on first use (<see cref="ImpostorBaker"/>), on demand when a group of the mesh comes
/// within reach of its transition, and unloaded with the meshes when unused.
/// </summary>
public sealed partial class FoliageRenderer
{
    /// <summary>The default transition distance of large meshes (units along the ground; the Tab slider "Impostor distance").</summary>
    public const float DefaultImpostorDistance = 4000;
    /// <summary>Medium-class atlases (128-pixel frames, radius 48 to 160) switch at this share of <see cref="ImpostorDistance"/>: 1, since at 0.5
    /// (2000) bushes visibly thickened against their meshes (docs/impostors.md "Drawing").</summary>
    public const float ImpostorMediumShare = 1f;
    /// <summary>The crossfade band before the transition, as a share of it.</summary>
    public const float ImpostorBand = 0.1f;

    /// <summary>The <c>impostors</c> switch (Enhancements): Meitou (default) draws far instances as impostors; Faithful never loads or draws one.</summary>
    public bool Impostors { get; set; } = true;
    /// <summary>The transition distance of large meshes; medium ones at <see cref="ImpostorMediumShare"/> of it.</summary>
    public float ImpostorDistance { get; set; } = DefaultImpostorDistance;
    /// <summary><c>MEITOU_IMPOSTOR_DEPTH=1</c>: impostors write the depth of their reconstructed surface (no early depth test).</summary>
    static readonly bool ImpostorDepthWrite = Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_DEPTH") == "1";
    /// <summary><c>MEITOU_IMPOSTOR_CASTERS=0</c>: the cascades keep the meshes (no impostor casters).</summary>
    static readonly bool ImpostorCasters = Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_CASTERS") != "0";

    enum ImpostorStage { Loading, Baking, Uploading, Ready, None }

    sealed class ImpostorState
    {
        public ImpostorStage Stage;
        public Task<(ImpostorSource? Source, ImpostorMeshes? Meshes, ImpostorClass? Class, ImpostorAtlas? Atlas)>? Load;
        public ImpostorSource? Source;
        public ImpostorMeshes? Meshes;
        public ImpostorClass Class;
        public ImpostorAtlas? Atlas;
        public ImpostorTextures? Textures;
        public long LastUsed;
        /// <summary>The work list that last numbered this mesh's impostor batch, and its number among the impostor batches.</summary>
        public int Stamp, Index;
    }

    readonly ImpostorCache impostorCache = new();
    ImpostorBaker? impostorBaker;
    ImpostorBakeJob? impostorBake;
    /// <summary>Atlas bytes uploaded per frame while settling; <see cref="impostorUploadsWaiting"/>: more wait for the next frame.</summary>
    const long SettleUploadBytes = 64L << 20, UploadBytesPerFrame = 12L << 20;
    bool impostorUploadsWaiting;
    MeshAsset? impostorBakeAsset;
    ImpostorDraw? impostorDraw;
    readonly Queue<MeshAsset> impostorBakes = new();
    readonly List<MeshAsset> impostorWork = [];
    long impostorBytes, lastImpostorScan;
    int impostorsBaked, impostorsLoaded, impostorUnloads;

    /// <summary>Atlases loading, waiting to bake, baking or uploading (part of <see cref="Pending"/>).</summary>
    int ImpostorPending => impostorWork.Count + impostorBakes.Count + (impostorBake is null ? 0 : 1);

    string ImpostorDescription => $"{assetsByMesh.Values.Count(a => a.Impostor?.Stage == ImpostorStage.Ready)} impostor atlases {impostorBytes / 1048576.0:0} MB " +
        $"({impostorsLoaded} from the cache, {impostorsBaked} baked, {impostorUnloads} unloaded)";

    /// <summary>The ground distance from which a mesh is its impostor (infinite without a resident atlas or with the switch off).</summary>
    float TransitionOf(MeshAsset a) => Impostors && a.Impostor is { Stage: ImpostorStage.Ready } s
        ? s.Class.FramePixels >= ImpostorClass.Large.FramePixels ? ImpostorDistance : ImpostorDistance * ImpostorMediumShare
        : float.PositiveInfinity;

    /// <summary>
    /// A group's range for a view with its impostor, and which parts the view draws (<see cref="FoliageCull.MeshPart"/>,
    /// <see cref="FoliageCull.ImpostorPart"/>): the meshes when the zone has ground before the transition, the impostors when it has ground in
    /// or beyond the crossfade band (with a margin for instances on the zone's edge). No impostor when the transition is not before the range's
    /// own fade band (the two fades never overlap) or the view takes none.
    /// </summary>
    (FoliageGroupRange Range, int Parts) WithImpostor(FoliageGroupRange r, MeshAsset a, float range, float band, ZoneState zone, Vector3 eye, bool view)
    {
        if (!view) return (r, FoliageCull.MeshPart);
        float t = TransitionOf(a);
        if (!(t <= range - band)) return (r, FoliageCull.MeshPart);
        float b = t * ImpostorBand;
        const float Edge = 64;
        float near = ZoneDistance(zone.Zone, eye), far = ZoneFarDistance(zone, eye);
        int parts = (near - Edge < t ? FoliageCull.MeshPart : 0) | (far + Edge >= t - b ? FoliageCull.ImpostorPart : 0);
        if (parts == 0) parts = FoliageCull.MeshPart;
        return (r.WithTransition(t, b), parts);
    }

    static float ZoneFarDistance(ZoneState z, Vector3 eye)
    {
        float dx = Math.Max(Math.Abs(eye.X - z.X0), Math.Abs(eye.X - (z.X0 + WorldLayout.ZoneSize)));
        float dz = Math.Max(Math.Abs(eye.Z - z.Z0), Math.Abs(eye.Z - (z.Z0 + WorldLayout.ZoneSize)));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Once a second (every update while settling): asks for the atlases of meshes with a group within reach of the shortest transition, marks
    /// the resident ones in use, unloads those unused for <see cref="IdleSeconds"/>; then steps the loads, the one bake in progress and the uploads.
    /// </summary>
    void UpdateImpostors(Vector3 eye, bool settling)
    {
        long now = Environment.TickCount64;
        if (Impostors && (settling || now - lastImpostorScan >= 1000))
        {
            lastImpostorScan = now;
            float shortest = ImpostorDistance * ImpostorMediumShare * (1 - ImpostorBand);
            foreach (var state in zones.Values)
            {
                if (!state.Ready) continue;
                float near = ZoneDistance(state.Zone, eye), far = ZoneFarDistance(state, eye);
                if (far < shortest) continue;
                foreach (var g in state.Groups)
                {
                    var a = g.Asset;
                    // Only meshes that can be impostors here: the group reaches beyond the transition (its range ends after it and its
                    // fade band, as WithImpostor requires) and this zone has ground from the band on.
                    var (range, band) = RangeOf(g);
                    if (!a.Resident || !a.HasBounds || near > range || range - band < ImpostorDistance) continue;
                    if (a.Impostor is { } s) { s.LastUsed = now; continue; }
                    RequestImpostor(a, now);
                }
            }
        }
        if (now - lastImpostorScan < 2000 || settling)
            foreach (var a in assetsByMesh.Values)
                if (a.Impostor is { Stage: ImpostorStage.Ready } s && (now - s.LastUsed) / 1000.0 > ImpostorIdleSeconds) UnloadImpostor(a);
        if (impostorWork.Count == 0 && impostorBakes.Count == 0 && impostorBake is null) return;
        GlBridge.EnsureFrame(Gpu);
        long uploaded = 0;
        impostorUploadsWaiting = false;
        for (int i = 0; i < impostorWork.Count; i++)
        {
            var a = impostorWork[i];
            var s = a.Impostor!;
            if (s.Stage == ImpostorStage.Loading)
            {
                if (!s.Load!.IsCompleted) continue;
                var (source, meshes, cls, atlas) = s.Load.IsCompletedSuccessfully ? s.Load.Result : default;
                s.Load = null;
                if (source is null || meshes is null || cls is not { } c) { s.Stage = ImpostorStage.None; impostorWork.RemoveAt(i--); continue; }
                (s.Source, s.Meshes, s.Class) = (source, meshes, c);
                if (atlas is null) { s.Stage = ImpostorStage.Baking; impostorBakes.Enqueue(a); impostorWork.RemoveAt(i--); continue; }
                impostorsLoaded++;
                s.Atlas = atlas;
                s.Stage = ImpostorStage.Uploading;
            }
            if (s.Stage != ImpostorStage.Uploading) continue;
            // At most a budget of atlas levels a frame, over all atlases: the copy into the staging is render-thread time (a flight that
            // reaches a new biome asks for dozens at once), and while settling, when no frames end, the staging lives in the frame's
            // constant chunks, which are kept for good.
            long budget = settling ? SettleUploadBytes : UploadBytesPerFrame;
            if (uploaded >= budget) { impostorUploadsWaiting = settling; continue; }
            if (UploadImpostor(a, budget, ref uploaded)) impostorWork.RemoveAt(i--);
        }
        StepBake(settling);
    }

    void RequestImpostor(MeshAsset a, long now)
    {
        if (a.Impostor is not null) return;
        if (a.Terrain || ImpostorSource.Ineligible(a.Mesh) is not null) { a.Impostor = new ImpostorState { Stage = ImpostorStage.None }; return; }
        var mesh = a.Mesh;
        var locator = assets;
        var cache = impostorCache;
        var s = a.Impostor = new ImpostorState { Stage = ImpostorStage.Loading, LastUsed = now };
        var messages = Messages;
        s.Load = BackgroundWork.Run<(ImpostorSource?, ImpostorMeshes?, ImpostorClass?, ImpostorAtlas?)>(() =>
        {
            var source = ImpostorSource.From(mesh, locator);
            if (source is null) return default;
            var meshes = ImpostorMeshes.Load(source, messages);
            if (meshes is null) return (source, null, null, null);
            var cls = ImpostorClass.For(meshes.Radius * mesh.MaxScale);
            if (cls is not { } c) return (source, meshes, null, null);
            var atlas = cache.TryLoad(source);
            if (atlas is not null && (atlas.FramePixels != c.FramePixels || atlas.Grid != c.Grid)) atlas = null;
            return (source, meshes, c, atlas);
        });
        impostorWork.Add(a);
    }

    /// <summary>The picture height the atlas resolution is chosen for (<see cref="LevelsToSkip"/>): 1080 lines (above it the nearest impostors are slightly softer).</summary>
    const float ImpostorReferenceHeight = 1080;
    /// <summary>Impostor atlases unused this long are unloaded (shorter than the meshes' <see cref="IdleSeconds"/>: an atlas is 9 to 36 MB).</summary>
    const double ImpostorIdleSeconds = 20;

    /// <summary>
    /// The top levels an atlas never needs (docs/impostors.md 7, "VRAM"): an instance is an impostor from the transition on, where its sphere
    /// (radius × the mesh's largest scale) spans r·H / (tan(fov / 2)·T) pixels at the reference height and the camera's 50° field of view;
    /// a level whose frames are at least that big is kept, larger ones are left out.
    /// </summary>
    internal static int LevelsToSkip(ImpostorAtlas atlas, float maxScale, float transition)
    {
        float pixels = atlas.Radius * maxScale * ImpostorReferenceHeight / (MathF.Tan(25 * MathF.PI / 180) * Math.Max(transition, 1));
        int skip = 0;
        while (skip < atlas.Levels - 1 && (atlas.FramePixels >> (skip + 1)) >= pixels) skip++;
        return skip;
    }

    /// <summary>The atlas's textures, level by level within the frame's upload budget; true when resident.</summary>
    bool UploadImpostor(MeshAsset a, long budget, ref long uploaded)
    {
        var s = a.Impostor!;
        using var batch = Gpu.Uploads.Begin();
        // The normal and depth maps one level coarser than the albedo: they shape the lighting, which varies slowly over a crown.
        int skip = LevelsToSkip(s.Atlas!, a.Mesh.MaxScale, ImpostorDistance);
        s.Textures ??= new ImpostorTextures(Gpu, s.Atlas!, batch, skip, skip + 1);
        // At least one step (a level larger than the budget still goes), then while the budget lasts.
        do
        {
            uploaded += s.Textures.NextStepBytes;
            s.Textures.UploadStep(batch);
        } while (!s.Textures.Complete && uploaded + s.Textures.NextStepBytes <= budget);
        if (!s.Textures.Complete) return false;
        s.Stage = ImpostorStage.Ready;
        s.Atlas = null;   // the CPU copy is not needed any more (a reload reads the cache again)
        s.Meshes = null;
        impostorBytes += s.Textures.Bytes;
        residentStamp++;
        return true;
    }

    /// <summary>The bake in progress, a step a frame (rows recorded, read back, filtered and encoded on workers); the next one when it is done.</summary>
    void StepBake(bool settling)
    {
        if (impostorBake is null)
        {
            if (impostorBakes.Count == 0) return;
            var next = impostorBakes.Dequeue();
            var s = next.Impostor!;
            impostorBaker ??= new ImpostorBaker(Gpu, textures);
            impostorBake = impostorBaker.Begin(s.Source!, s.Meshes!, s.Class);
            impostorBakeAsset = next;
        }
        impostorBake.RowsPerStep = settling ? impostorBake.Size.Grid : 2;
        if (!impostorBake.Step()) return;
        var a = impostorBakeAsset!;
        var st = a.Impostor!;
        if (impostorBake.Result is { } atlas)
        {
            impostorsBaked++;
            var source = st.Source!;
            var cache = impostorCache;
            BackgroundWork.Run(() =>
            {
                try { cache.Save(source, atlas); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            });
            st.Atlas = atlas;
            st.Stage = ImpostorStage.Uploading;
            impostorWork.Add(a);
        }
        else
        {
            Messages.Add($"impostor {a.Mesh.Name}: {impostorBake.Error?.Message}");
            st.Stage = ImpostorStage.None;
        }
        impostorBake.Dispose();
        (impostorBake, impostorBakeAsset) = (null, null);
    }

    void UnloadImpostor(MeshAsset a)
    {
        var s = a.Impostor!;
        if (s.Textures is not null)
        {
            impostorBytes -= s.Textures.Bytes;
            s.Textures.Dispose();
        }
        a.Impostor = null;   // asked for again (from the cache) when needed
        impostorUnloads++;
        residentStamp++;
    }

    void DisposeImpostors()
    {
        impostorBake?.Dispose();
        foreach (var a in assetsByMesh.Values)
        {
            if (a.Impostor is not { } s) continue;
            try { s.Load?.Wait(); } catch (AggregateException) { }
            s.Textures?.Dispose();
        }
        impostorBaker?.Dispose();
        impostorDraw?.Dispose();
    }

    // ---- drawing ----

    /// <summary>The impostor batches of the current view, in work-list order (the GPU path: after the mesh and rock batches).</summary>
    readonly List<MeshAsset> gpuImpostorOrder = [];
    /// <summary>The CPU path's impostor batches (the cull's emission order), and its batches by mesh.</summary>
    readonly List<Batch> impostorActive = [];
    readonly Dictionary<MeshAsset, Batch> impostorBatches = [];

    struct ImpostorDrawItem
    {
        public MeshAsset Asset;
        public int Batch;
        public uint FirstInstance, Instances;
    }

    /// <summary>The current view's impostor draws: one per impostor batch shown (the GPU path) or with instances (the CPU path).</summary>
    readonly List<ImpostorDrawItem> impostorDraws = [];

    Batch ImpostorBatchOf(MeshAsset a)
    {
        if (!impostorBatches.TryGetValue(a, out var batch)) impostorBatches[a] = batch = new Batch { Asset = a };
        if (batch.Stamp != drawStamp)
        {
            batch.Stamp = drawStamp;
            batch.Count = 0;
            batch.Index = impostorActive.Count;
            impostorActive.Add(batch);
        }
        return batch;
    }

    void EmitImpostors(MeshAsset a, ReadOnlySpan<Matrix4x4> visible)
    {
        if (visible.Length == 0) return;
        DrawnInstances += visible.Length;
        var batch = ImpostorBatchOf(a);
        if (batch.Count + visible.Length > batch.Data.Length) Array.Resize(ref batch.Data, Math.Max(batch.Data.Length * 2, batch.Count + visible.Length));
        visible.CopyTo(batch.Data.AsSpan(batch.Count));
        batch.Count += visible.Length;
    }

    /// <summary>The impostor draws: per impostor batch shown (GPU) or not empty (CPU).</summary>
    void PrepareImpostors(bool gpu)
    {
        impostorDraws.Clear();
        if (gpu)
        {
            int first = gpuOrder.Count + gpuRockOrder.Count;
            for (int i = 0; i < gpuImpostorOrder.Count; i++)
                if (gpuBatchShown[first + i]) impostorDraws.Add(new ImpostorDrawItem { Asset = gpuImpostorOrder[i], Batch = first + i });
        }
        else
            foreach (var b in impostorActive)
                if (b.Count > 0) impostorDraws.Add(new ImpostorDrawItem { Asset = b.Asset, Batch = b.Index, FirstInstance = (uint)b.Offset, Instances = (uint)b.Count });
        DrawCalls += impostorDraws.Count;
    }

    /// <summary>The impostor draws appended to a mesh segment's job (after its mesh draws; no culling, the rows already bound): a colour view's
    /// lit program, or a cascade's caster facing the sun (the direction towards it from the cascade's matrix).</summary>
    void AddImpostorDraws(MeshJob job, bool depth, Matrix4x4 viewProjection, PassTargets targets, DrawState state, bool coverage, bool indirect, int argsFirst)
    {
        impostorDraw ??= new ImpostorDraw(Gpu, nativeFrame);
        var drawState = state with { Cull = Silk.NET.Vulkan.CullModeFlags.None };
        var program = depth ? ImpostorProgram.Caster : ImpostorDepthWrite ? ImpostorProgram.DepthWrite : ImpostorProgram.Plain;
        var pipeline = impostorDraw.Pipeline(program, drawState, targets.Formats);
        var up = new Vector3(viewProjection.M12, viewProjection.M22, viewProjection.M32);
        up = up.LengthSquared() > 0 ? Vector3.Normalize(up) : Vector3.UnitY;
        var forward = new Vector3(viewProjection.M13, viewProjection.M23, viewProjection.M33);
        var view = depth && forward.LengthSquared() > 0 ? new Vector4(-Vector3.Normalize(forward), 1) : Vector4.Zero;
        var quad = impostorDraw.Quad;
        BufferBinding[] none = [];
        for (int k = 0; k < impostorDraws.Count; k++)
        {
            var d = impostorDraws[k];
            var t = d.Asset.Impostor!.Textures!;
            var atlas = t.Atlas;
            var push = new ImpostorPush
            {
                Sphere = new Vector4(atlas.Centre, atlas.Radius), CameraUp = up, Grid = atlas.Grid,
                Albedo = t.Index(0, textureBias), Normal = t.Index(1, textureBias), Depth = t.Index(2, textureBias),
                Coverage = coverage ? 1u : 0u, View = view,
            };
            MeshPush bytes = default;
            System.Runtime.CompilerServices.Unsafe.As<MeshPush, ImpostorPush>(ref bytes) = push;
            job.Add(new MeshJob.Draw
            {
                SetRaster = k == 0, Cull = drawState.Cull, Pipeline = pipeline, Vertices = none, Elements = quad, Push = bytes,
                Args = indirect ? gpuResult.Args : default, ArgsOffset = gpuResult.ArgsOffset + (ulong)(argsFirst + k) * 20,
                IndexCount = 6, Instances = d.Instances, FirstInstance = d.FirstInstance,
            });
        }
    }
}
