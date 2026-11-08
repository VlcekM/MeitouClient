using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;

using Meitou.Game.Rendering.Gpu;
using Silk.NET.Vulkan;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Shadows;

/// <summary>
/// The Meitou shadows (the <c>shadows</c> enhancement, docs/formats/shadows.md "Meitou shadows"): the same cascade array and caster draws as the
/// game's CSM, but five cascades (<see cref="MeitouCascades"/>) fitted over the visible depths (<see cref="MeitouShadowFit"/>), drawn on a staggered
/// schedule (<see cref="Cadence"/>: 1, 2, 4, 16, 32 frames with five cascades, each kept with the matrices it was drawn with), a soft receiver with
/// cascade blending and a fade at the range's end, contact-hardening penumbrae from a half-resolution blocker map, and the terrain's own
/// shadow over the whole world beyond the range (<see cref="TerrainShadowMap"/>), and the landmarks' beyond it however far they are (the landmark map).
/// </summary>
public sealed unsafe partial class ShadowPass
{
    /// <summary>The <c>shadows</c> switch: Meitou (true, the default) or the game's CSM.</summary>
    public bool Meitou { get; set; } = true;
    /// <summary>A temporal anti-aliasing or upscaling pass runs: the filter's noise changes every frame (it is static otherwise).</summary>
    public bool Temporal { get; set; }
    /// <summary>Contact-hardening penumbrae (the blocker search); off: a fixed small filter.</summary>
    public bool ContactHardening { get; set; } = true;
    /// <summary>
    /// The receiver's filter tier (<c>--shadow-filter</c>, Tab): 2 every cascade with the blocker search and 16 taps; 1 the two nearest so, the far ones
    /// 8 taps at the least radius; 0 every cascade 8 taps at the least radius, no blocker map (low-end GPUs).
    /// </summary>
    public int FilterQuality { get => filterQuality; set => filterQuality = Math.Clamp(value, 0, 2); }
    int filterQuality = 2;
    /// <summary>How many cascades, nearest first, filter in full (blocker search, 16 taps) at the <see cref="FilterQuality"/>.</summary>
    int FullFilterCascades => filterQuality switch { 0 => 0, 1 => Math.Max(1, MeitouCascades - 2), _ => ShadowSettings.MaxCascades };
    /// <summary>The Meitou shadows' number of cascades (1 to <see cref="ShadowSettings.MaxCascades"/>; <see cref="MeitouShadowFit.DefaultCascades"/>). The Faithful CSM keeps the game's <see cref="ShadowSettings.Cascades"/>.</summary>
    public int MeitouCascades { get => meitouCascades; set => meitouCascades = Math.Clamp(value, 1, ShadowSettings.MaxCascades); }
    int meitouCascades = MeitouShadowFit.DefaultCascades;
    /// <summary>The sun's angular diameter the penumbrae are made for (radians): a little more than the real sun's 0.53°.</summary>
    public float SunAngle { get; set; } = 0.7f * MathF.PI / 180;
    /// <summary>Filter radius without a blocker search, and the least with one, in texels of the cascade.</summary>
    public float FilterTexels { get; set; } = 1.5f;
    /// <summary>The widest penumbra radius in world units (a tall caster far above the ground); never less than <see cref="FilterTexels"/>.</summary>
    public float MaxPenumbra { get; set; } = 3;
    /// <summary>A sudden turn of the sun larger than this (radians) redraws every cascade at once.</summary>
    public float SunJump { get; set; } = 2 * MathF.PI / 180;

    /// <summary>How often each cascade was drawn (Meitou), for the log.</summary>
    public readonly int[] CascadeDraws = new int[ShadowSettings.MaxCascades];
    /// <summary>Per cascade why it was drawn (Meitou): the camera moved so it no longer covers the view, the sun turned, its cadence came, the splits or a sun jump redrew all.</summary>
    public readonly int[,] RedrawReasons = new int[ShadowSettings.MaxCascades, 4];
    /// <summary>How often every cascade was redrawn at once because the splits changed (index 0) or the sun jumped (1).</summary>
    public readonly int[] AllReasons = new int[2];
    public int MeitouFrames { get; private set; }

    readonly ShadowCascade?[] stored = new ShadowCascade?[ShadowSettings.MaxCascades];
    readonly Vector3[] storedSunDir = new Vector3[ShadowSettings.MaxCascades];

    /// <summary>
    /// Where the cascades' texel grids are anchored (<see cref="MeitouShadowFit.Fit"/>): at the point the camera looks at (the eye without a focus), so
    /// a turning sun slides the first cascade's grid under what is watched only by the distance from here times the angle (anchored at the eye, 300
    /// to 1000 units back, every shadow edge round the squad crawled by a fifth of a texel a frame while time ran). It moves only while the sun turns
    /// and the point is more than <see cref="AnchorReach"/> away (a move shifts the grids by a fraction of a texel, once); with the sun still the
    /// grids do not slide at all, so it stays.
    /// </summary>
    (double X, double Y, double Z)? anchor;
    Vector3 lastFrameSun;
    /// <summary>The watched point's distance from the anchor before a turning sun moves it: a first-cascade grid then slides at most about (this + its radius) × the turn per frame.</summary>
    public const double AnchorReach = 200;
    /// <summary>How often the grid anchor moved (Meitou), for the log.</summary>
    public int AnchorMoves { get; private set; }

    (double X, double Y, double Z) Anchor(Vector3 point, Vector3 toSun)
    {
        bool turning = toSun != lastFrameSun;
        lastFrameSun = toSun;
        if (anchor is not { } a)
            return (anchor = (Math.Round(point.X), Math.Round(point.Y), Math.Round(point.Z))).Value;
        double dx = point.X - a.X, dy = point.Y - a.Y, dz = point.Z - a.Z;
        if (turning && dx * dx + dy * dy + dz * dz > AnchorReach * AnchorReach)
        {
            anchor = (Math.Round(point.X), Math.Round(point.Y), Math.Round(point.Z));
            AnchorMoves++;
        }
        return anchor.Value;
    }

    /// <summary>
    /// Frames between the redraws of cascade <paramref name="i"/> of <paramref name="count"/> when nothing makes it stale (Meitou): the nearest every
    /// frame, the last three every 4th, 16th and 32nd frame (a far cascade's casters hardly move and its texels are big; 1,2,4,4 was the schedule
    /// for four before 2026-10-08), any between every 2nd (with five: 1, 2, 4, 16, 32). <see cref="Phase"/> puts the redraws on different frames.
    /// </summary>
    public static int Cadence(int i, int count) => i == 0 ? 1 : (count - 1 - i) switch { 0 => 32, 1 => 16, 2 => 4, _ => 2 };

    /// <summary>
    /// The frame (modulo its <see cref="Cadence"/>) on which each of <paramref name="count"/> cascades is redrawn: nearest first, each takes the phase
    /// that collides least with the nearer far cascades' over 32 frames (they share <see cref="FarBudget"/>), the old <c>i · 5 mod cadence</c> where
    /// that is as good (so four cascades keep the phases 0, 1, 10, 15 they had; five get 0, 1, 2, 0, 20).
    /// </summary>
    public static int[] Phases(int count)
    {
        var phase = new int[count];
        for (int i = 1; i < count; i++)
        {
            int cadence = Cadence(i, count), preferred = i * 5 % cadence, fewest = int.MaxValue;
            for (int k = 0; k < cadence; k++)
            {
                int p = (preferred + k) % cadence, hits = 0;
                for (int f = p; f < 32; f += cadence)
                    for (int j = 1; j < i; j++)
                        if (f % Cadence(j, count) == phase[j]) { hits++; break; }
                if (hits < fewest) (phase[i], fewest) = (p, hits);
            }
        }
        return phase;
    }
    int[] phases = [];

    /// <summary>How far (radians) the sun may turn before a cascade is redrawn: a caster 150 units tall shifts its shadow by half a texel; at most 2°, at least 0.25°.</summary>
    static float SunTolerance(ShadowCascade c) => (float)Math.Clamp(0.5 * c.Texel / 150, 0.25 * Math.PI / 180, 2 * Math.PI / 180);
    /// <summary>
    /// How many far cascades (1 and up, 1.1 to 1.8 ms each on the swamp) one frame may draw; the other redraws wait, the longest first, and are forced after
    /// <see cref="MaxWait"/> frames. 0: no limit (the schedule before 2026-10-09, where up to three coincided in one frame); the <c>shadow-spread</c> A/B switch.
    /// </summary>
    public int FarBudget { get; set; } = ShadowSchedule.DefaultBudget;
    /// <summary>The most frames a deferred redraw waits.</summary>
    public int MaxWait { get; set; } = ShadowSchedule.DefaultMaxWait;
    readonly bool[] owed = new bool[ShadowSettings.MaxCascades];
    readonly int[] waited = new int[ShadowSettings.MaxCascades];
    float[]? storedSplits;
    /// <summary>How far the shadow range may drift (share of the drawn one) before every cascade is drawn again.</summary>
    const float RangeTolerance = 0.1f;
    Vector3 storedSun;
    int storedMapSize, meitouFrame;
    bool meitouValid;
    // The camera's flight (docs/render-shadows.md "Flights"): a far cascade drawn before a flight is stale until drawn again, and the published cascades stop before the first stale one.
    readonly bool[] stale = new bool[ShadowSettings.MaxCascades];
    /// <summary>The host flies the camera this frame (a character flight or squad tour hop, set by the frame before <see cref="Render"/>): the far cascades are not drawn.</summary>
    public bool CameraFlying { get; set; }
    /// <summary>Frames the far cascades were skipped for the camera's flight (the log).</summary>
    public int FlightFrames { get; private set; }
    readonly FrameBlock meitouBlock;
    ushort[]? coarse;
    int coarseSize;
    TerrainShadowMap? terrainMap;
    SampledTexture? boundTerrain;   // the terrain shadow map the receivers sample, as the GL code bound it to its unit (kept while no new one is)
    GpuTexture? blocker;
    SampledTexture blockerSampled;
    bool blockerPublished;
    LegacyProgram? blockerNative;
    SamplerSlot blockerAtlasSlot;
    UniformHandle blockerSizeHandle, blockerLayerHandle;

    /// <summary>The half-resolution blocker map (native; null until the first frame with contact hardening).</summary>
    public GpuTexture? BlockerMap => blocker;

    /// <summary>The whole-world height grid (WorldScene.Coarse) for the terrain shadow beyond the range; uploaded when first needed.</summary>
    public void SetTerrain(ushort[] heights, int size) => (coarse, coarseSize) = (heights, size);

    /// <summary>Foliage meshes smaller than this many texels of a cascade (radius × largest scale) cast nothing into it (Meitou): their shadow would be a
    /// blur of a few texels under the soft filter, at the price of their triangles and cut-out fragments.</summary>
    public const float MinFoliageCasterTexels = 2;
    /// <summary>A character part (hair, a weapon, a hat) narrower than this many texels casts nothing into a coarse cascade (<c>CharacterRenderer.DrawDepth</c>).</summary>
    public const float MinCharacterCasterTexels = 1;
    /// <summary>The cull of <see cref="MinCharacterCasterTexels"/> (the <c>char-casters</c> A/B switch): on by default.</summary>
    public static bool CharacterCasterCull { get; set; } = true;

    /// <summary>The smallest foliage mesh <paramref name="cascade"/> draws (world units; 0 with the faithful shadows: every mesh).</summary>
    public float MinFoliageCaster(ShadowCascade cascade) => Meitou ? (float)cascade.Texel * MinFoliageCasterTexels : 0;

    /// <summary>One line on the schedule: how often each cascade was drawn, and the terrain map's rebuilds.</summary>
    public string DescribeMeitou() =>
        $"drawn {string.Join("/", CascadeDraws.Take(MeitouCascades))} times in {MeitouFrames} frames (because the view left the box / the sun turned / the cadence / all at once: {string.Join(" ", Enumerable.Range(0, MeitouCascades).Select(i => $"c{i} {RedrawReasons[i, 0]}/{RedrawReasons[i, 1]}/{RedrawReasons[i, 2]}/{RedrawReasons[i, 3]}"))}); " +
        $"all at once: splits changed {AllReasons[0]}, sun jumped {AllReasons[1]}; far cascades skipped for the camera's flight in {FlightFrames} frames; " +
        $"terrain shadow rebuilt {terrainMap?.Builds ?? 0} times (cpu {terrainMap?.LastBuildCpuMs ?? 0:0.00} ms); " +
        $"landmark shadow map drawn {LandmarkDraws} times ({LandmarkCount} landmarks, {(landmarkBox is { } b ? $"{b.Texel:0} units per texel" : "off")})";


    /// <summary>Collects the landmarks to cast into the landmark map: their bounding spheres (xyz centre, w radius) into the list, and a key
    /// that changes when that set does (WorldObjectRenderer.LandmarkCasters).</summary>
    public delegate long LandmarkCollect(List<Vector4> spheres);
    /// <summary>The landmarks for <see cref="Render"/>: how to collect them and how to draw their depth.</summary>
    public readonly record struct LandmarkCasters(LandmarkCollect Collect, CasterDraw Draw);

    /// <summary>The landmark shadow map's side (texels). Over the landmarks of the default reach (150000 around the camera) a texel is about 75 units;
    /// a landmark is 2000 or more in radius.</summary>
    public const int LandmarkMapSize = 4096;
    GpuTexture? landmarkMap;
    SampledTexture landmarkSampled;
    bool landmarkPublished;
    ShadowCascade? landmarkBox;
    long landmarkKey;
    Vector3 landmarkSun;
    readonly List<Vector4> landmarkSpheres = [];
    /// <summary>How often the landmark map was drawn, and the landmarks in it the last time (for the log).</summary>
    public int LandmarkDraws { get; private set; }
    public int LandmarkCount { get; private set; }

    /// <summary>
    /// The landmark shadow map (docs/formats/shadows.md, "Landmark shadows"): every landmark drawn (resolved, within the landmark reach) into one
    /// depth map along the sun, fitted around them all (<see cref="MeitouShadowFit.FitLandmarks"/>), so a landmark casts its shadow however far it
    /// is; the receivers read it beyond the cascades. Drawn again only when the set of landmarks changes or the sun has turned (as the terrain map).
    /// </summary>
    void UpdateLandmarks(ShadowView view, Vector3 toSun, LandmarkCasters? landmarks)
    {
        if (landmarks is not { } l) { landmarkBox = null; return; }
        long key = l.Collect(landmarkSpheres);
        if (landmarkSpheres.Count == 0) { landmarkBox = null; return; }
        if (landmarkBox is not null && key == landmarkKey && Vector3.Dot(toSun, landmarkSun) > MathF.Cos(TerrainShadowMap.RebuildAngle)) return;
        var box = MeitouShadowFit.FitLandmarks(toSun, landmarkSpheres, LandmarkMapSize)!;
        if (landmarkMap is null)
        {
            // A map of its own (4096², larger than a cascade layer), read through its own sampler (uShadowLandmark), not a layer of the cascade array.
            landmarkMap = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, LandmarkMapSize, LandmarkMapSize, Use: TextureUse.Sampled | TextureUse.DepthTarget, Name: "landmark shadow map"));
            landmarkSampled = Sampled(landmarkMap, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, compare: true);
        }
        var host = BeginHost("landmark shadows", clear: true, landmarkMap);
        SetTile(0, 0, LandmarkMapSize);
        SetCasterBias(new Vector4(box.FixedBias, KenshiShadows.SlopeBias, KenshiShadows.MaxSlopeBias, 1f));
        l.Draw(box, box.WorldToClip(), box.CullPlanes(), view.Eye);
        EndHost(host);
        (landmarkBox, landmarkKey, landmarkSun) = (box, key, toSun);
        LandmarkDraws++;
        LandmarkCount = landmarkSpheres.Count;
    }

    void RenderMeitou(ShadowView view, Vector3 toSun, CasterDraw draw, LandmarkCasters? landmarks, CasterPrepare? prepare)
    {
        var watch = Stopwatch.StartNew();
        Array.Clear(PhaseMs);
        int count = MeitouCascades;
        Resize(Settings.TileSize, count);
        if (phases.Length != count) phases = Phases(count);
        float near = MeitouShadowFit.QuantizedNear(view.Near);
        var splits = MeitouShadowFit.Splits(near, Math.Max(EffectiveRange, near * 4), count, view.Focus);
        // (A range that varies from frame to frame, as the memory guard's or a fog clamp makes it, redraws them all only past RangeTolerance.)
        // The splits move with the camera's near plane; a cascade drawn with other splits keeps its own slice (the receiver reads each one's) and is drawn again when the
        // new slice no longer fits it (Covers below), so only a change of the range or of the number of cascades redraws them all.
        bool splitsChanged = storedSplits is null || storedSplits.Length != splits.Length || Math.Abs(splits[^1] - storedSplits[^1]) > RangeTolerance * storedSplits[^1];
        bool sunJumped = Vector3.Dot(toSun, storedSun) < MathF.Cos(SunJump);
        bool all = !meitouValid || splitsChanged || storedMapSize != Settings.MapSize || sunJumped;
        if (meitouValid && all) { if (splitsChanged) AllReasons[0]++; if (sunJumped) AllReasons[1]++; }
        var gridAnchor = Anchor(view.Focus > 0 ? view.Eye + Vector3.Normalize(view.Forward) * view.Focus : view.Eye, toSun);
        int frame = meitouFrame++;
        MeitouFrames++;
        Span<bool> drawNow = stackalloc bool[ShadowSettings.MaxCascades], want = stackalloc bool[ShadowSettings.MaxCascades], mustDraw = stackalloc bool[ShadowSettings.MaxCascades];
        for (int i = 0; i < count; i++)
        {
            // A cascade is drawn again when its slice no longer fits its box (the camera moved or turned), when the sun has turned further than
            // its texels allow, and at the latest after its cadence (casters stream in and move; docs/formats/shadows.md "Cached far cascades").
            bool due = frame % Cadence(i, count) == phases[i];
            bool stale = stored[i] is not null && !all && !due;
            bool uncovered = stored[i] is not null && !MeitouShadowFit.Covers(stored[i]!, view, splits, SearchRadius(stored[i]!) + 2 * stored[i]!.Texel);
            bool sunTurned = stored[i] is not null && Vector3.Dot(toSun, storedSunDir[i]) < MathF.Cos(SunTolerance(stored[i]!));
            want[i] = all || stored[i] is null || due || uncovered || sunTurned || owed[i] || this.stale[i];   // stale: drawn before a flight, wherever its box is now
            mustDraw[i] = stored[i] is null;
            if (stale && uncovered) RedrawReasons[i, 0]++;
            if (stale && !uncovered && sunTurned) RedrawReasons[i, 1]++;
            if (due && !all) RedrawReasons[i, 2]++;
            if (all) RedrawReasons[i, 3]++;
        }
        // At most FarBudget far cascades are drawn per frame; the others wait (longest first) and are owed, forced after MaxWait frames. The far
        // cascades are the last three (the costly ones); the nearer ones are never deferred (ShadowSchedule.NearCascades).
        int nearCount = ShadowSchedule.NearCascades(count);
        ShadowSchedule.Pick(want[..count], mustDraw[..count], waited, FarBudget, MaxWait, drawNow, nearCount);
        // While the host flies the camera (a character flight, a squad tour hop) the far cascades are not drawn: a map drawn now covers the wrong ground the next frame, and the
        // ones drawn before the flight shadow the ground kilometres back, so they are stale and not published until drawn again, once the camera lands (uncovered, over the budget's frames).
        bool flying = CameraFlying;
        if (flying)
        {
            FlightFrames++;
            for (int i = nearCount; i < count; i++)
            {
                stale[i] = true;
                if (mustDraw[i]) continue;
                drawNow[i] = false;
                waited[i] = 0;
            }
        }
        for (int i = 0; i < count; i++) owed[i] = want[i] && !drawNow[i] && !flying;

        timer.Begin();
        int drawn = 0;
        for (int i = 0; i < count; i++)
            if (drawNow[i] && MeitouShadowFit.Fit(view, toSun, Effective, splits, i, gridAnchor) is var fitted)
                casterViews[drawn++] = new CasterView(fitted, fitted.WorldToClip(), fitted.CullPlanes(), view.Eye);
        prepare?.Invoke(casterViews.AsSpan(0, drawn));
        // Each cascade drawn is a rendering of its own layer, cleared by the load op; the other layers keep what they hold.
        for (int k = 0; k < drawn; k++)
        {
            var (c, worldToClip, planes, lodEye) = casterViews[k];
            int i = c.Index;
            var host = BeginHost("shadow cascade", clear: true, layer: i);
            SetTile(0, 0, atlasSize);
            SetCasterBias(new Vector4(c.FixedBias, MeitouShadowFit.Slope, c.MaxSlopeBias, 1f));
            draw(c, worldToClip, planes, lodEye);
            EndHost(host);
            stored[i] = c;
            storedSunDir[i] = toSun;
            stale[i] = false;
            CascadeDraws[i]++;
        }
        Array.Clear(casterViews);
        bool blockers = ContactHardening && filterQuality > 0 && UpdateBlockers(drawNow, count);
        if (coarse is not null)
        {
            terrainMap ??= new TerrainShadowMap(Gpu, coarse, coarseSize);
        }
        // The terrain map (rebuilt when the sun has moved).
        terrainMap?.Update(toSun);
        UpdateLandmarks(view, toSun, landmarks);
        timer.End();
        if (all) { storedSun = toSun; storedSplits = splits; storedMapSize = Settings.MapSize; }
        meitouValid = true;
        // The receivers read the cascades before the first stale one (the receiver's range ends with the last published; the terrain and landmark maps go on).
        int published = count;
        for (int i = 0; i < count; i++) if (stale[i]) { published = i; break; }
        var cascades = new ShadowCascade[published];
        for (int i = 0; i < published; i++) cascades[i] = stored[i]!;
        Cascades = cascades;
        PublishMeitou(view, cascades, toSun, blockers);
        CpuMs = watch.Elapsed.TotalMilliseconds;
        cpuSamples.Add(CpuMs);
    }

    /// <summary>
    /// Rebuilds the blocker map's layers of the cascades drawn this frame: one native segment with a rendering per layer (the layers not drawn
    /// keep what they hold), the cascade array read raw through a plain sampler (no comparison), the fullscreen triangle.
    /// </summary>
    bool UpdateBlockers(ReadOnlySpan<bool> drawn, int count)
    {
        int size = atlasSize / 2;
        bool fresh = false;   // a new map: every layer
        if (blocker is null || blocker.Desc.Width != size || blocker.Desc.Layers != count)
        {
            blocker?.Dispose();
            blocker = Gpu.CreateTexture(new TextureDesc(InternalFormat.R32f, size, size, Layers: count, Kind: TextureKind.Texture2DArray,
                Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "shadow blocker map"));
            blockerSampled = Sampled(blocker, TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge);
            blockerPublished = false;
            fresh = true;
        }
        if (blockerNative is null)
        {
            // Native (docs/renderer-native.md 7.1, wave 3 agent B, step P): VkGl's SPIR-V and layout, made here once, never inside a draw.
            var p = blockerNative = LegacyProgram.Create(Gpu, ShadowShaders.FullscreenVertex, MeitouShadowShaders.BlockerFragment, "shadow blockers");
            (blockerAtlasSlot, blockerSizeHandle, blockerLayerHandle) = (p.Sampler("uAtlas"), p.Uniform("uAtlasSize"), p.Uniform("uLayer"));
        }
        RecordBlockers(drawn, count, fresh);
        return true;
    }

    /// <summary>The blocker pass's state, as the GL version's was: no culling, depth or blending, every colour channel written.</summary>
    static readonly DrawState BlockerState = new(CullMode.None, FrontFaceDirection.Ccw, false, false, DepthFunction.Lequal, false, 0, 0,
        BlendState.Off, ColorMask.R | ColorMask.G | ColorMask.B | ColorMask.A,
        PolygonKind.Fill, false, false);

    void RecordBlockers(ReadOnlySpan<bool> drawn, int count, bool fresh)
    {
        var p = blockerNative!;
        var map = blocker!;
        p.Set(blockerSizeHandle, (float)atlasSize);
        var cmd = Gpu.BeginNative("shadow blockers");
        var formats = new AttachmentFormats(map.Desc.Format, InternalFormat.None, 1);
        int side = map.Desc.Width;
        var whole = new PixelRect(0, 0, (uint)side, (uint)side);
        var pipeline = Gpu.Pipelines.Get(BlockerState.Pipeline(p.Program, p.VertexLayout([]), PrimitiveType.Triangles, formats, "shadow blockers"));
        for (int i = 0; i < count; i++)
        {
            if (!drawn[i] && !fresh) continue;
            var target = new RenderTarget(map.Attachment(0, i), LoadOp.Load, default);
            cmd.BeginRendering(new RenderingDesc(target, default, side, side));
            BlockerState.Record(cmd, new PassTargets(target, default, formats, side, side, new PixelViewport(0, 0, side, side, 0, 1), whole));
            cmd.BindPipeline(pipeline);
            // Each layer is a new encoder: bind the atlas for it, not only once before the loop.
            p.Bind(blockerAtlasSlot, atlasPlain);
            p.Set(blockerLayerHandle, i);
            p.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
        }
        Gpu.EndNative(cmd);
    }

    /// <summary>The blocker search's and the filter's widest radius in a cascade (world units).</summary>
    double SearchRadius(ShadowCascade c) => Math.Max(MaxPenumbra, FilterTexels * 1.5 * c.Texel);

    void PublishMeitou(ShadowView view, ShadowCascade[] cascades, Vector3 toSun, bool blockers)
    {
        var data = new float[ReceiverBytes / 4];
        var origin = view.Eye;
        var ms = new float[MeitouShadowShaders.BlockBytes / 4];
        int tile = Settings.TileSize;
        float range = cascades[^1].FarDepth;
        float sunTan = MathF.Tan(SunAngle * 0.5f);
        for (int i = 0; i < cascades.Length; i++)
        {
            var c = cascades[i];
            float world = c.Size.X;   // world units per unit of tile UV
            Put(data, i * 16, ReceiverTile(c, origin));
            Put(data, RectAt + i * 4, c.Tile);
            Put(data, CascadeAt + i * 4, new Vector4(c.FarDepth, c.FilterRadius, c.Size.X, c.Size.Y));
            Put(data, ExtentAt + i * 4, new Vector4(c.Size.Z, 1f / tile, 0, 0));
            Put(ms, i * 4, new Vector4(c.NearDepth, c.FarDepth, FilterTexels / tile, (float)c.Texel));
            Put(ms, ShadowSettings.MaxCascades * 4 + i * 4, new Vector4(world, c.Size.Z, (float)(SearchRadius(c) / world), sunTan / world));
        }
        Put(data, LightAt, cascades[0].Rotation);
        Put(data, OriginAt, new Vector4(origin, 1));
        Put(data, ForwardAt, new Vector4(Vector3.Normalize(view.Forward), cascades.Length));
        Put(data, AtlasAt, new Vector4(atlasSize, atlasSize, 0, 1));   // x, y: one layer (no virtual atlas: cascade i is layer i); w: the Meitou receiver
        const int P = MeitouShadowShaders.ParamsOffset;
        float noise = Temporal ? meitouFrame % 64 * 5.588238f : 0;
        Put(ms, P, new Vector4(noise, range, range * 0.85f, range * 0.55f));
        bool terrain = terrainMap?.Sampled is not null;
        float half = WorldLayout.HalfWorldSize;
        if (terrainMap is { } map)
        {
            float horizontal = MathF.Sqrt(toSun.X * toSun.X + toSun.Z * toSun.Z);
            float cosElevation = Math.Max(horizontal, 0.05f);
            Put(ms, P + 4, new Vector4(-half, -half, 1 / map.Spacing, terrain ? 1 : 0));
            // The penumbra's height across: occluder distance × the sun's angular radius over the cosine of its height.
            Put(ms, P + 8, new Vector4(map.Size, 20, 6, sunTan / cosElevation));
        }
        Put(ms, P + 12, new Vector4(blockers ? 1 : 0, range * 0.9f, FullFilterCascades, 0));
        if (landmarkBox is { } box)
        {
            // The landmark map: (p - origin) -> (u, v, depth); its filter radius (UV) and normal offset (world: one and a half texels).
            Put(ms, P + 16, ReceiverTile(box, origin));
            Put(ms, P + 32, new Vector4(box.FilterRadius, (float)(box.Texel * 1.5), 0, 1));
            landmarkPublished = true;
        }
        Upload(data);
        meitouBlock.Set(ms);
        // The maps the receivers sample from now on (the GL code bound them to their units here; a unit kept what it held).
        if (terrain) boundTerrain = terrainMap!.Sampled;
        if (blockers) blockerPublished = true;
    }

    void DisposeMeitou()
    {
        terrainMap?.Dispose();
        boundTerrain = null;
        blocker?.Dispose();
        blockerNative?.Dispose();
        landmarkPublished = false;
        landmarkMap?.Dispose();
    }
}
