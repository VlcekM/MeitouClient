using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// The Meitou shadows (the <c>shadows</c> enhancement, docs/formats/shadows.md "Meitou shadows"): the same atlas and caster draws as the
/// game's CSM, but cascades fitted over the visible depths (<see cref="MeitouShadowFit"/>), drawn on a staggered schedule (the first every
/// frame, the second every other frame, the last two every fourth, each kept with the matrices it was drawn with), a soft receiver with
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
    /// <summary>The sun's angular diameter the penumbrae are made for (radians): a little more than the real sun's 0.53°.</summary>
    public float SunAngle { get; set; } = 0.7f * MathF.PI / 180;
    /// <summary>Filter radius without a blocker search, and the least with one, in texels of the cascade.</summary>
    public float FilterTexels { get; set; } = 1.0f;
    /// <summary>The widest penumbra radius in world units (a tall caster far above the ground); never less than <see cref="FilterTexels"/>.</summary>
    public float MaxPenumbra { get; set; } = 3;
    /// <summary>A sudden turn of the sun larger than this (radians) redraws every cascade at once.</summary>
    public float SunJump { get; set; } = 2 * MathF.PI / 180;

    /// <summary>How often each cascade was drawn (Meitou), for the log.</summary>
    public readonly int[] CascadeDraws = new int[4];
    public int MeitouFrames { get; private set; }

    readonly ShadowCascade?[] stored = new ShadowCascade?[4];
    float[]? storedSplits;
    Vector3 storedSun;
    int storedMapSize, meitouFrame;
    bool meitouValid;
    readonly FrameBlock meitouBlock;
    ushort[]? coarse;
    int coarseSize;
    TerrainShadowMap? terrainMap;
    SampledTexture? boundTerrain;   // the terrain shadow map the receivers sample, as the GL code bound it to its unit (kept while no new one is)
    Texture? blocker;
    SampledTexture blockerSampled;
    bool blockerPublished;
    LegacyProgram? blockerNative;
    SamplerSlot blockerAtlasSlot;
    UniformHandle blockerSizeHandle;

    /// <summary>The half-resolution blocker map (native; null until the first frame with contact hardening).</summary>
    public Texture? BlockerMap => blocker;

    /// <summary>The whole-world height grid (WorldScene.Coarse) for the terrain shadow beyond the range; uploaded when first needed.</summary>
    public void SetTerrain(ushort[] heights, int size) => (coarse, coarseSize) = (heights, size);

    /// <summary>Foliage meshes smaller than this many texels of a cascade (radius × largest scale) cast nothing into it (Meitou): their shadow would be a
    /// blur of a few texels under the soft filter, at the price of their triangles and cut-out fragments.</summary>
    public const float MinFoliageCasterTexels = 2;

    /// <summary>The smallest foliage mesh <paramref name="cascade"/> draws (world units; 0 with the faithful shadows: every mesh).</summary>
    public float MinFoliageCaster(ShadowCascade cascade) => Meitou ? (float)cascade.Texel * MinFoliageCasterTexels : 0;

    /// <summary>One line on the schedule: how often each cascade was drawn, and the terrain map's rebuilds.</summary>
    public string DescribeMeitou() =>
        $"drawn {string.Join("/", CascadeDraws)} times in {MeitouFrames} frames; terrain shadow rebuilt {terrainMap?.Builds ?? 0} times (cpu {terrainMap?.LastBuildCpuMs ?? 0:0.00} ms); " +
        $"landmark shadow map drawn {LandmarkDraws} times ({LandmarkCount} landmarks, {(landmarkBox is { } b ? $"{b.Texel:0} units per texel" : "off")})";


    /// <summary>Collects the landmarks to cast into the landmark map: their bounding spheres (xyz centre, w radius) into the list, and a key
    /// that changes when that set does (WorldObjectRenderer.LandmarkCasters).</summary>
    public delegate long LandmarkCollect(List<Vector4> spheres);
    /// <summary>The landmarks for <see cref="Render"/>: how to collect them and how to draw their depth.</summary>
    public readonly record struct LandmarkCasters(LandmarkCollect Collect, CasterDraw Draw);

    /// <summary>The landmark shadow map's side (texels). Over the landmarks of the default reach (150000 around the camera) a texel is about 75 units;
    /// a landmark is 2000 or more in radius.</summary>
    public const int LandmarkMapSize = 4096;
    Texture? landmarkMap;
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
            landmarkMap = Texture.Create(Gpu, new TextureDesc(Format.D32Sfloat, LandmarkMapSize, LandmarkMapSize, Use: TextureUse.Sampled | TextureUse.DepthTarget, Name: "landmark shadow map"));
            landmarkSampled = Sampled(landmarkMap, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, compare: true);
        }
        var host = BeginHost("landmark shadows", clear: true, landmarkMap);
        SetTile(0, 0, LandmarkMapSize);
        SetCasterBias(new Vector4(box.FixedBias, KenshiShadows.SlopeBias, KenshiShadows.MaxSlopeBias, 0));
        l.Draw(box, box.WorldToClip(), box.CullPlanes(), view.Eye);
        EndHost(host);
        (landmarkBox, landmarkKey, landmarkSun) = (box, key, toSun);
        LandmarkDraws++;
        LandmarkCount = landmarkSpheres.Count;
    }
    void RenderMeitou(ShadowView view, Vector3 toSun, CasterDraw draw, LandmarkCasters? landmarks)
    {
        var watch = Stopwatch.StartNew();
        Array.Clear(PhaseMs);
        Resize(Settings.MapSize);
        int count = Math.Min(Settings.Cascades, 4);
        float near = MeitouShadowFit.QuantizedNear(view.Near);
        var splits = MeitouShadowFit.Splits(near, Math.Max(EffectiveRange, near * 4), count);
        bool all = !meitouValid || storedSplits is null || !splits.AsSpan().SequenceEqual(storedSplits) || storedMapSize != Settings.MapSize
            || Vector3.Dot(toSun, storedSun) < MathF.Cos(SunJump);
        int frame = meitouFrame++;
        MeitouFrames++;
        Span<bool> drawNow = stackalloc bool[4];
        int drawing = 0;
        for (int i = 0; i < count; i++)
        {
            // The first cascade every frame, the second every other, the third and fourth every fourth frame on alternate frames.
            bool due = i == 0 || (i == 1 ? frame % 2 == 0 : frame % 4 == (i == 2 ? 1 : 3));
            drawNow[i] = all || stored[i] is null || due
                || !MeitouShadowFit.Covers(stored[i]!, view, splits, SearchRadius(stored[i]!) + 2 * stored[i]!.Texel);
            if (drawNow[i]) drawing++;
        }

        timer.Begin();
        // The whole atlas is cleared by the load op when every cascade is drawn; else each drawn tile is cleared inside the pass.
        var host = BeginHost("shadow cascades", clear: drawing == count);
        for (int i = 0; i < count; i++)
        {
            if (!drawNow[i]) continue;
            var c = MeitouShadowFit.Fit(view, toSun, Effective, splits, i);
            int x = (int)MathF.Round(c.Tile.X * atlasSize), y = (int)MathF.Round(c.Tile.Y * atlasSize), s = Settings.TileSize;
            SetTile(x, y, s);
            if (drawing != count)   // only this tile: the others keep what they hold
                ClearTile(host, new Silk.NET.Vulkan.Rect2D(new Silk.NET.Vulkan.Offset2D(x, y), new Silk.NET.Vulkan.Extent2D((uint)s, (uint)s)));
            SetCasterBias(new Vector4(c.FixedBias, KenshiShadows.SlopeBias, KenshiShadows.MaxSlopeBias, 0));
            draw(c, c.WorldToClip(), c.CullPlanes(), view.Eye);
            stored[i] = c;
            CascadeDraws[i]++;
        }
        EndHost(host);
        bool blockers = ContactHardening && UpdateBlockers(drawNow, count);
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
        var cascades = new ShadowCascade[count];
        for (int i = 0; i < count; i++) cascades[i] = stored[i]!;
        Cascades = cascades;
        PublishMeitou(view, cascades, toSun, blockers);
        CpuMs = watch.Elapsed.TotalMilliseconds;
        cpuSamples.Add(CpuMs);
    }

    /// <summary>
    /// Rebuilds the blocker map's tiles of the cascades drawn this frame: one native segment with its own rendering on the map (loaded: the
    /// tiles not drawn keep what they hold), the atlas read raw through a plain sampler (no comparison), per tile its viewport and the
    /// fullscreen triangle.
    /// </summary>
    bool UpdateBlockers(ReadOnlySpan<bool> drawn, int count)
    {
        long timing = StepTiming.Now();
        int size = atlasSize / 2;
        bool fresh = false;   // a new map: every tile
        if (blocker is null || blocker.Desc.Width != size)
        {
            blocker?.Dispose();
            blocker = Texture.Create(Gpu, new TextureDesc(Format.R32Sfloat, size, size, Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "shadow blocker map"));
            blockerSampled = Sampled(blocker, TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge);
            blockerPublished = false;
            fresh = true;
        }
        if (blockerNative is null)
        {
            // Native (docs/renderer-native.md 7.1, wave 3 agent B, step P): VkGl's SPIR-V and layout, made here once, never inside a draw.
            var p = blockerNative = LegacyProgram.Create(Gpu, ShadowShaders.FullscreenVertex, MeitouShadowShaders.BlockerFragment, "shadow blockers");
            (blockerAtlasSlot, blockerSizeHandle) = (p.Sampler("uAtlas"), p.Uniform("uAtlasSize"));
        }
        int tile = Settings.TileSize / 2, grid = Settings.Grid;
        RecordBlockers(drawn, count, fresh, tile, grid, out int tiles);
        StepTiming.Add(StepTiming.Blocker, timing, tiles);
        return true;
    }

    /// <summary>The blocker pass's state, as the GL version's was: no culling, depth or blending, every colour channel written.</summary>
    static readonly DrawState BlockerState = new(CullModeFlags.None, FrontFace.Clockwise, false, false, CompareOp.LessOrEqual, false, 0, 0,
        BlendState.Off, ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        Silk.NET.Vulkan.PolygonMode.Fill, false, false);

    void RecordBlockers(ReadOnlySpan<bool> drawn, int count, bool fresh, int tile, int grid, out int tiles)
    {
        tiles = 0;
        var p = blockerNative!;
        var map = blocker!;
        p.Set(blockerSizeHandle, (float)atlasSize);
        p.Bind(blockerAtlasSlot, atlasPlain);
        var cmd = Gpu.BeginNative("shadow blockers");
        var target = new RenderTarget(map.Attachment(), AttachmentLoadOp.Load, default, map.Image);
        var formats = new AttachmentFormats(map.Desc.Format, Format.Undefined, 1);
        int side = map.Desc.Width;
        cmd.BeginRendering(new RenderingDesc(target, default, side, side));
        var whole = new Rect2D(default, new Extent2D((uint)side, (uint)side));
        BlockerState.Record(cmd, new PassTargets(target, default, formats, side, side, new Viewport(0, 0, side, side, 0, 1), whole));
        cmd.BindPipeline(Gpu.Pipelines.Get(BlockerState.Pipeline(p.Program, p.VertexLayout([]), PrimitiveTopology.TriangleList, formats, "shadow blockers")));
        p.Flush(cmd);
        for (int i = 0; i < count; i++)
        {
            if (!drawn[i] && !fresh) continue;
            cmd.SetViewport(new Viewport(i % grid * tile, i / grid * tile, tile, tile, 0, 1));
            cmd.Draw(3);
            tiles++;
        }
        cmd.EndRendering();
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
            Put(data, i * 16, c.OriginToTile(origin));
            Put(data, 64 + i * 4, c.Tile);
            Put(data, 80 + i * 4, new Vector4(c.FarDepth, c.FilterRadius, c.Size.X, c.Size.Y));
            Put(data, 96 + i * 4, new Vector4(c.Size.Z, 1f / tile, 0, 0));
            Put(ms, i * 4, new Vector4(c.NearDepth, c.FarDepth, FilterTexels / tile, (float)c.Texel));
            Put(ms, 16 + i * 4, new Vector4(world, c.Size.Z, (float)(SearchRadius(c) / world), sunTan / world));
        }
        Put(data, 112, cascades[0].Rotation);
        Put(data, 128, new Vector4(origin, 1));
        Put(data, 132, new Vector4(Vector3.Normalize(view.Forward), cascades.Length));
        Put(data, 136, new Vector4(atlasSize, 0, 0, 1));   // w: the Meitou receiver
        float noise = Temporal ? meitouFrame % 64 * 5.588238f : 0;
        Put(ms, 32, new Vector4(noise, range, range * 0.85f, range * 0.55f));
        bool terrain = terrainMap?.Sampled is not null;
        float half = WorldLayout.HalfWorldSize;
        if (terrainMap is { } map)
        {
            float horizontal = MathF.Sqrt(toSun.X * toSun.X + toSun.Z * toSun.Z);
            float cosElevation = Math.Max(horizontal, 0.05f);
            Put(ms, 36, new Vector4(-half, -half, 1 / map.Spacing, terrain ? 1 : 0));
            // The penumbra's height across: occluder distance × the sun's angular radius over the cosine of its height.
            Put(ms, 40, new Vector4(map.Size, 20, 6, sunTan / cosElevation));
        }
        Put(ms, 44, new Vector4(blockers ? 1 : 0, range * 0.9f, 0, 0));
        if (landmarkBox is { } box)
        {
            // The landmark map: (p - origin) -> (u, v, depth); its filter radius (UV) and normal offset (world: one and a half texels).
            Put(ms, 48, box.OriginToTile(origin));
            Put(ms, 64, new Vector4(box.FilterRadius, (float)(box.Texel * 1.5), 0, 1));
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
