using System.Diagnostics;
using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Sky;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Shadows;

/// <summary>
/// The sun's shadow map as the game's CSM mode draws it (docs/formats/shadows.md): four cascades fitted by <see cref="ShadowCascades"/> (Meitou.Data,
/// backend-independent) in the tiles of the game's square atlas, the one layer of a 2D depth array (the Meitou shadows put each of their cascades in
/// a layer of its own, the size of a tile), the casters drawn by the renderers' depth-only entry points through
/// <see cref="CasterDraw"/>. This class is the thin GPU part: the cascade array,
/// the caster state, the uniform blocks the receivers read (<see cref="ShadowShaders.Functions"/>), timing and the debug views.
/// </summary>
public sealed unsafe partial class ShadowPass : IDisposable
{
    /// <summary>
    /// Draws the casters of one cascade: the matrix maps absolute world positions to the tile's clip space (depth 0..1), the planes cull (no near
    /// plane), the eye picks levels of detail.
    /// </summary>
    public delegate void CasterDraw(ShadowCascade cascade, Matrix4x4 worldToClip, Vector4[] planes, Vector3 lodEye);

    /// <summary>
    /// A cascade drawn this frame, as <see cref="CasterDraw"/> gets it (the same <c>Planes</c> array).
    /// </summary>
    public readonly record struct CasterView(ShadowCascade Cascade, Matrix4x4 WorldToClip, Vector4[] Planes, Vector3 LodEye);

    /// <summary>
    /// Called once a frame before the cascades are drawn, with every cascade about to be (a caster that works for them all at once: the characters).
    /// </summary>
    public delegate void CasterPrepare(ReadOnlySpan<CasterView> cascades);

    /// <summary>
    /// The size of the receiver block (std140) the shaders declare in <see cref="ShadowShaders.Functions"/>: four per-cascade arrays of
    /// <see cref="ShadowSettings.MaxCascades"/> (a mat4 and three vec4 each), a mat4 and three vec4.
    /// </summary>
    public const int ReceiverBytes = (28 * ShadowSettings.MaxCascades + 28) * 4;

    /// <summary>
    /// The <see cref="StageClock"/> stage of the shadow casters' jobs.
    /// </summary>
    const int ShadowStage = 12;

    #region Cascades

    /// <summary>
    /// The cascades about to be drawn this frame (the first ones used; cleared after drawing).
    /// </summary>
    readonly CasterView[] _casterViews = new CasterView[ShadowSettings.MaxCascades];

    /// <summary>
    /// The native GPU API (docs/renderer-native.md 7.1 step 8; all native since phase 8 stage 3: the atlas's host hands the casters its targets and
    /// state, 8.9).
    /// </summary>
    public IGpuContext Gpu { get; }

    /// <summary>
    /// The cascade array (null before the first frame).
    /// </summary>
    GpuTexture? _atlas;

    /// <summary>
    /// The cascade array with the comparison sampler (the receivers') and with the plain one (the blocker pass's and the debug view's).
    /// </summary>
    SampledTexture _atlasShadow, _atlasPlain;

    /// <summary>
    /// The receivers read the cascade array (it is published once the receiver block was uploaded with it).
    /// </summary>
    bool _atlasPublished;

    /// <summary>
    /// One layer's side (texels) and the layers of the cascade array (Faithful: the game's atlas, one layer; Meitou: a tile-sized layer per cascade).
    /// </summary>
    int _atlasSize, _atlasLayers;

    /// <param name="assets">Where to find the game's <c>white-noise.png</c> (the receiver's jitter); without it a hash stands in.</param>
    public ShadowPass(IGpuContext gpu, AssetLocator? assets = null)
    {
        Gpu = gpu;
        _receiver = new FrameBlock(gpu, ReceiverBytes);
        _casterBias = new FrameBlock(gpu, 16);
        _meitouBlock = new FrameBlock(gpu, MeitouShadowShaders.BlockBytes);
        _blockers = new ShadowBlockerMap(gpu);
        _landmarks = new LandmarkShadowMap(gpu);
        _timer = new PassTimer(gpu);
        LoadNoise(assets);
        PublishGlobals();
        Disable();   // a valid block (shadows off) before the first frame
    }

    /// <summary>
    /// Draws the cascades for this camera and light, then publishes them to the receivers.
    /// <paramref name="toSun"/> is the lighting direction (towards the sun, height clamped to 0 under the horizon), <paramref name="sunHeight"/>
    /// the real sun's height (the map is drawn while it is at least <see cref="MinSunHeight"/>; by default the lighting direction's).
    /// <paramref name="landmarks"/>: the landmarks for the Meitou shadows' landmark map (they cast shadows however far they are). <paramref
    /// name="prepare"/>: called with the cascades about to be drawn, before the first.
    /// </summary>
    public void Render(ShadowView view, Vector3 toSun, CasterDraw draw, float? sunHeight = null, LandmarkCasters? landmarks = null,
        CasterPrepare? prepare = null)
    {
        Poll();
        UploadNoise();
        Cascades = null;
        TracedCascades = 0;
        TracedDepth = 0;
        if (!Enabled || (sunHeight ?? toSun.Y) < MinSunHeight || toSun.LengthSquared() < 1e-8f)
        {
            Disable();
            return;
        }

        if (Meitou && TracedWhole)
        {
            RenderTraced(view, Vector3.Normalize(toSun), landmarks);
            return;
        }

        if (Meitou)
        {
            RenderMeitou(view, Vector3.Normalize(toSun), draw, landmarks, prepare);
            return;
        }

        _meitouValid = false;
        var watch = Stopwatch.StartNew();
        Array.Clear(PhaseMs);

        // The game's atlas as the array's one layer, the tiles where the game puts them: a cascade drawn at another place in its target
        // rasterises a few edge texels differently (window coordinates round differently), so per-cascade layers could not be pixel-identical.
        Resize(Settings.MapSize, 1);
        var cascades = ShadowCascades.Fit(view, Vector3.Normalize(toSun), Effective);
        _timer.Begin();

        int drawn = 0;
        foreach (var c in cascades)
        {
            // In front of the camera's near plane: nothing on screen reads it (a viewer saving).
            if (!c.Unused) _casterViews[drawn++] = new CasterView(c, c.WorldToClip(), c.CullPlanes(), view.Eye);
        }

        prepare?.Invoke(_casterViews.AsSpan(0, drawn));
        var host = BeginHost("shadow atlas", clear: true);
        for (int k = 0; k < drawn; k++)
        {
            var (c, worldToClip, planes, lodEye) = _casterViews[k];
            int x = (int)MathF.Round(c.Tile.X * _atlasSize), y = (int)MathF.Round(c.Tile.Y * _atlasSize), s = Settings.TileSize;
            SetTile(x, y, s);
            SetCasterBias(new Vector4(c.FixedBias, KenshiShadows.SlopeBias, KenshiShadows.MaxSlopeBias, 1f));
            draw(c, worldToClip, planes, lodEye);
        }

        Array.Clear(_casterViews);
        EndHost(host);
        _timer.End();
        Cascades = cascades;
        Publish(view, cascades);
        CpuMs = watch.Elapsed.TotalMilliseconds;
        _cpuSamples.Add(CpuMs);
    }

    /// <summary>
    /// The casters' bias block of the cascade about to be drawn: the guests' segments take it when they are prepared (in the draw callback), a new
    /// slice after each write, as VkGl renamed the GL buffer written between their draws.
    /// </summary>
    void SetCasterBias(in Vector4 bias) => _casterBias.Set([bias.X, bias.Y, bias.Z, bias.W]);

    /// <summary>
    /// Opens a rendering of one cascade's layer (or of <paramref name="target"/>, the landmark map) as a native host (docs/renderer-native.md 4.5):
    /// the guests (terrain, objects, foliage, all native) record into the returned list's rendering instance through <c>BeginNativeInPass</c>, which
    /// finds the host. <paramref name="clear"/>: the layer is cleared by the load op (else it is loaded).
    /// </summary>
    IGpuCommandList BeginHost(string label, bool clear, GpuTexture? target = null, int layer = 0)
    {
        var cmd = Gpu.BeginNative(label);
        var map = target ?? _atlas!;
        var t = PassTargets.Of(null, map);
        if (layer != 0) t = t with { Depth = new RenderTarget(map.Attachment(0, layer), LoadOp.Load, default) };
        var depth = clear ? t.Depth with { Load = LoadOp.Clear, DepthClear = 1f } : t.Depth;

        // Wave 4 (docs/renderer-native.md 6): the cascades' segments are secondaries, recorded on the job threads when the host ends.
        bool secondaries = Gpu.RecordsSecondaries;
        cmd.BeginRendering(new RenderingDesc(default, depth, t.Width, t.Height), secondaries);
        Gpu.BeginHostPass(cmd, t, CasterState(t.Formats));
        if (secondaries && Gpu is GpuContext parallel && cmd is CommandList vkCmd)
            parallel.Frame.Parallel.Begin(vkCmd, t.Formats, ShadowStage);
        return cmd;
    }

    /// <summary>
    /// What the casters draw with (what VkGl made of the GL state the GL code set for them): depth only, tested with less and written, clamped
    /// (casters between the box and the sun are flattened onto its near side, not clipped), no culling (each caster sets its own), no colour.
    /// </summary>
    DrawState CasterState(in AttachmentFormats formats) =>
        DrawState.For(formats, Gpu.DepthClamp, depthTest: true, depthWrite: true, compare: DepthFunction.Less, mask: 0, depthClamp: true);

    /// <summary>
    /// The viewport and scissor the casters draw with (a whole layer, or the landmark map).
    /// </summary>
    void SetTile(int x, int y, int s) =>
        Gpu.SetPassViewport(new PixelViewport(x, y, s, s, 0, 1), new PixelRect(x, y, (uint)s, (uint)s));

    /// <summary>
    /// Ends a cascade host: the job threads' secondaries closed, the rendering and the host pass ended.
    /// </summary>
    void EndHost(IGpuCommandList cmd)
    {
        if (Gpu is GpuContext parallel && parallel.Frame.Parallel.Open) parallel.Frame.Parallel.End();
        cmd.EndRendering();
        Gpu.EndHostPass(cmd);
        Gpu.EndNative(cmd);
    }

    /// <summary>
    /// Marks the shadows off for the receivers (the term is 1 everywhere).
    /// </summary>
    public void Disable()
    {
        _meitouValid = false;
        var data = new float[ReceiverBytes / 4];
        Upload(data);
    }

    /// <summary>
    /// The cascade array: <paramref name="layers"/> layers of <paramref name="size"/>² (made again when either changes, e.g. the F5 toggle between
    /// the Faithful and Meitou counts).
    /// </summary>
    void Resize(int size, int layers)
    {
        if (size == _atlasSize && layers == _atlasLayers && _atlas is not null) return;

        FreeAtlas();
        (_atlasSize, _atlasLayers) = (size, layers);
        _atlas = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, size, size, Layers: layers, Kind: TextureKind.Texture2DArray,
            Use: TextureUse.Sampled | TextureUse.DepthTarget, Name: "shadow atlas"));

        // Linear comparison: the faithful receiver reads texel centres, where the weights are 1, 0, 0, 0 (the game's point sampling);
        // other receivers may use the bilinear weights. The plain sampler (no comparison) is the blocker pass's and the debug view's.
        _atlasShadow = Sampled(_atlas, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, compare: true);
        _atlasPlain = Sampled(_atlas, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
    }

    /// <summary>
    /// Frees the cascade array; the receivers get the stand-in until it is made and published again.
    /// </summary>
    void FreeAtlas()
    {
        if (_atlas is null) return;

        _atlas.Dispose();
        (_atlas, _atlasPublished) = (null, false);
    }

    #endregion

    #region Receiver

    /// <summary>
    /// The receiver block (the faithful layout, also the Meitou receiver's cascades).
    /// </summary>
    readonly FrameBlock _receiver;

    /// <summary>
    /// The casters' bias block of the cascade being drawn (<see cref="SetCasterBias"/>).
    /// </summary>
    readonly FrameBlock _casterBias;

    /// <summary>
    /// Float offsets of the receiver block's members: the per-cascade tile rectangles, cascade data and extents after the tile matrices, then the
    /// light rotation, the origin, the forward direction and the atlas sizes.
    /// </summary>
    const int RectAt = 16 * ShadowSettings.MaxCascades, CascadeAt = 20 * ShadowSettings.MaxCascades, ExtentAt = 24 * ShadowSettings.MaxCascades,
        LightAt = 28 * ShadowSettings.MaxCascades, OriginAt = LightAt + 16, ForwardAt = LightAt + 20, AtlasAt = LightAt + 24;

    /// <summary>
    /// The shadow blocks and maps as frame globals (docs/renderer-native.md 4.3), native: they replace what <see cref="ShadowShaders"/> publishes
    /// from GL's binding points and units (still the source without a ShadowPass: the model viewer). A texture appears where the GL code bound it to
    /// its unit (the atlas with the receiver block, the noise from the first frame, the terrain and blocker maps with the Meitou block when they are
    /// on); before that the readers get the stand-in.
    /// </summary>
    void PublishGlobals()
    {
        var g = Gpu.Globals;
        g.Publish(ShadowShaders.ReceiverBlock, _receiver.Binding);
        g.Publish(ShadowShaders.CasterBlock, _casterBias.Binding);
        g.Publish("uShadowMap", () => _atlasPublished ? _atlasShadow : default);
        // a segment being prepared: a frame is open
        g.Publish("uShadowNoise", () =>
        {
            UploadNoise();
            return _noiseSampled;
        });
        g.Publish(MeitouShadowShaders.Block, _meitouBlock.Binding);

        // The terrain shadow map with its own sampler (linear, clamped; TerrainShadowMap).
        g.Publish("uShadowTerrain", () => _boundTerrain ?? default);
        // Traced, the binding carries the sun's visibility volume instead (ShadowPass.Traced: no cascades, so no blocker map).
        g.Publish("uShadowBlocker", () => _volumePublished ? _volumeSampled : _blockers.Published);
        // The landmark shadow map (comparison, linear, clamped).
        g.Publish("uShadowLandmark", () => _landmarks.Published);
    }

    /// <summary>
    /// A texture with the sampler VkGl made from these GL parameters (the same rule, <see cref="SamplerDesc.FromGl"/>). None of these textures is
    /// mipmapped, so the LOD bias never applies; wrap R is GL's default.
    /// </summary>
    SampledTexture Sampled(GpuTexture t, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap, bool compare = false) =>
        Sampled(Gpu, t, min, mag, wrap, compare);

    /// <summary>
    /// <see cref="Sampled(GpuTexture, TextureMinFilter, TextureMagFilter, TextureWrapMode, bool)"/> on <paramref name="gpu"/>.
    /// </summary>
    internal static SampledTexture Sampled(IGpuContext gpu, GpuTexture t, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap,
        bool compare = false) =>
        new(gpu.Samplers.Get(SamplerDesc.FromGl(min, mag, wrap, wrap, TextureWrapMode.Repeat, compare, DepthFunction.Lequal, false, 1, false, 0)),
            t.View());

    /// <summary>
    /// The receiver's tile matrix for this backend. The cascade maps are rendered with a world-to-clip matrix whose y points up. Vulkan's framebuffer
    /// is bottom-up (the present flips the backbuffer once), so its raw target has light-space +y at the texture's top (v = 1), which is what <see
    /// cref="ShadowCascade.OriginToTile"/> produces. Metal's render targets are top-down (they land in the backbuffer the right way up), so
    /// light-space +y is at v = 0: flip the tile's v row or every world-derived shadow lookup is mirrored.
    /// </summary>
    Matrix4x4 ReceiverTile(ShadowCascade c, Vector3 origin)
    {
        var m = c.OriginToTile(origin);
        if (Gpu.RenderTargetsBottomUp) return m;

        // v' = 1 - v, for the row-vector matrix: p * m.
        return m with { M12 = -m.M12, M22 = -m.M22, M32 = -m.M32, M42 = 1f - m.M42 };
    }

    /// <summary>
    /// Fills the receiver block from this frame's cascades (tile matrices relative to the eye, tiles, selection depths and filter radii, sizes) and
    /// the light, eye and atlas, and uploads it.
    /// </summary>
    void Publish(ShadowView view, ShadowCascade[] cascades)
    {
        var data = new float[ReceiverBytes / 4];
        var origin = view.Eye;
        int count = Math.Min(cascades.Length, ShadowSettings.MaxCascades);
        for (int i = 0; i < count; i++)
        {
            var c = cascades[i];
            Put(data, i * 16, ReceiverTile(c, origin));
            Put(data, RectAt + i * 4, c.Tile);
            Put(data, CascadeAt + i * 4, new Vector4(c.SelectDepth, c.FilterRadius, c.Size.X, c.Size.Y));
            Put(data, ExtentAt + i * 4, new Vector4(c.Size.Z, 1f / Settings.TileSize, 0, 0));
        }

        Put(data, LightAt, cascades[0].Rotation);
        Put(data, OriginAt, new Vector4(origin, 1));
        Put(data, ForwardAt, new Vector4(Vector3.Normalize(view.Forward), count));

        // x: the game's atlas (layer 0 of the array), y: one tile.
        Put(data, AtlasAt, new Vector4(_atlasSize, Settings.TileSize, _noise is not null ? 1 : 0, 0));
        Upload(data);
    }

    /// <summary>
    /// The receiver block; the atlas is published to the receivers from now on (the GL code bound it to its unit here).
    /// </summary>
    void Upload(float[] data)
    {
        _receiver.Set(data);
        if (_atlas is not null) _atlasPublished = true;
    }

    /// <summary>
    /// A matrix into the block at float offset <paramref name="at"/>, in the order GL reads as the column-vector form.
    /// </summary>
    static void Put(float[] d, int at, Matrix4x4 m)
    {
        // System.Numerics row-major memory is what GL reads as the column-major column-vector form (as WorldGl.Matrix).
        d[at] = m.M11;
        d[at + 1] = m.M12;
        d[at + 2] = m.M13;
        d[at + 3] = m.M14;
        d[at + 4] = m.M21;
        d[at + 5] = m.M22;
        d[at + 6] = m.M23;
        d[at + 7] = m.M24;
        d[at + 8] = m.M31;
        d[at + 9] = m.M32;
        d[at + 10] = m.M33;
        d[at + 11] = m.M34;
        d[at + 12] = m.M41;
        d[at + 13] = m.M42;
        d[at + 14] = m.M43;
        d[at + 15] = m.M44;
    }

    /// <summary>
    /// A vector into the block at float offset <paramref name="at"/>.
    /// </summary>
    static void Put(float[] d, int at, Vector4 v)
    {
        d[at] = v.X;
        d[at + 1] = v.Y;
        d[at + 2] = v.Z;
        d[at + 3] = v.W;
    }

    #endregion

    #region Settings and range

    /// <summary>
    /// The atlas side, the range and the number of cascades (the game's <c>shadow quality</c> and <c>Shadow Range</c>).
    /// </summary>
    public ShadowSettings Settings { get; set; } = new();

    /// <summary>
    /// The memory-pressure guard (<see cref="VramGuard"/>): its range scale shortens the shadow range (never below the game's default, or the setting
    /// when that is shorter) while the video memory is nearly used up. Null: no clamp.
    /// </summary>
    public VramGuard? Guard { get; set; }

    /// <summary>
    /// The distance past which the weather fog hides everything (<see cref="FogVolumes.AtmosphereDistance"/>), set each frame by the caller; shortens
    /// the range in Meitou mode.
    /// </summary>
    public float? RangeCap { get; set; }

    /// <summary>
    /// On: the range is also capped at the placed fog block's hide distance (<see cref="SetFogHide"/>); the <c>shadow-fogcap</c> A/B switch.
    /// </summary>
    public bool FogCapEnabled { get; set; } = true;

    /// <summary>
    /// The hysteresis behind <see cref="FogCap"/>.
    /// </summary>
    readonly ShadowFogCap _fogCapState = new();

    /// <summary>
    /// The cap from the placed fog block this frame (<see cref="ShadowFogCap"/>), null for none.
    /// </summary>
    public float? FogCap { get; private set; }

    /// <summary>
    /// The sun's shadows are drawn (off: the receivers' term is 1 everywhere).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Below this height of the real sun there is no shadow map. As the game: it keeps drawing the map along the lighting direction (whose height is
    /// clamped to 0 under the horizon) while the sun still has a colour, down to −0.2.
    /// </summary>
    public float MinSunHeight { get; set; } = KenshiLighting.SunColourCutoff;

    /// <summary>
    /// The cascades of the last frame (null when nothing was drawn).
    /// </summary>
    public ShadowCascade[]? Cascades { get; private set; }

    /// <summary>
    /// The cascade array: the game's atlas as its one layer (Faithful) or a layer per cascade (Meitou) (native; null before the first frame).
    /// </summary>
    public GpuTexture? AtlasTexture => _atlas;

    /// <summary>
    /// How many cascades the mode in use draws: the game's (<see cref="ShadowSettings.Cascades"/>, 4) for the Faithful CSM, <see
    /// cref="MeitouCascades"/> for the Meitou shadows.
    /// </summary>
    public int CascadeCount => Meitou ? MeitouCascades : Math.Clamp(Settings.Cascades, 1, ShadowSettings.MaxCascades);

    /// <summary>
    /// Whether the game's noise texture was found (else the receiver's jitter is a hash).
    /// </summary>
    public bool HasNoise => _noise is not null;

    /// <summary>
    /// The range the cascades are fitted to and the casters are culled by: <see cref="Settings"/>' range, clamped by the guard.
    /// </summary>
    public float EffectiveRange
    {
        get
        {
            float range = Settings.Range, scale = Guard?.RangeScale ?? 1f;

            // Beyond the weather fog's distance nothing shows (FogVolumes.AtmosphereDistance), and beyond the placed fog block's hide distance
            // (FogVolumes.ShadowHideDistance) either.
            if (Meitou && RangeCap is { } cap) range = MathF.Min(range, cap);
            if (Meitou && FogCap is { } fog) range = MathF.Min(range, fog);
            return scale >= 1f ? range : Math.Max(Math.Min(range, KenshiShadows.DefaultRange), range * scale);
        }
    }

    /// <summary>
    /// <see cref="Settings"/> with <see cref="EffectiveRange"/> (the settings themselves while the range is unclamped).
    /// </summary>
    ShadowSettings Effective => EffectiveRange is var range && range == Settings.Range ? Settings : Settings with { Range = range };

    /// <summary>
    /// Called once a frame with <see cref="FogVolumes.ShadowHideDistance"/> (null: none) and <see cref="FogVolumes.ShadowHidePending"/>.
    /// </summary>
    internal void SetFogHide(float? hide, bool pending = false)
    {
        if (!FogCapEnabled)
        {
            _fogCapState.Reset();
            FogCap = null;
            return;
        }

        FogCap = _fogCapState.Update(hide, pending);
    }

    #endregion

    #region Noise

    /// <summary>
    /// The game's jitter texture, <c>data/materials/white-noise.png</c> (64² RGB), which <c>Main_Lighting_CSM</c> binds with
    /// <c>filtering none</c> and the default wrap. The receiver reads it with texelFetch (its own point sampling and wrap), so the
    /// texture's sampler state does not matter. Read from the install at run time, never shipped. Uploaded in the first frame.
    /// </summary>
    void LoadNoise(AssetLocator? assets)
    {
        if (assets?.Find(KenshiShadows.NoiseTexture) is not { } path) return;

        try
        {
            var image = TextureLoader.LoadFile(path, allMips: false).Levels[0];
            _noise = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, image.Width, image.Height, Name: "shadow noise"));
            _noiseSize = (image.Width, image.Height);
            _noisePixels = image.Pixels;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            Console.WriteLine($"warning   shadow noise: {e.Message}");
        }
    }

    /// <summary>
    /// The noise's texels, in the first frame (an upload goes with a frame); published from then on.
    /// </summary>
    void UploadNoise()
    {
        if (_noiseUploaded || _noise is null || _noisePixels is null || !Gpu.Frame.Open) return;

        using (var batch = Gpu.Uploads.Begin())
            batch.Write(_noise, 0, 0, new PixelRect(0, 0, (uint)_noiseSize.Width, (uint)_noiseSize.Height), _noisePixels);
        _noiseSampled = Sampled(_noise, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.Repeat);
        _noiseUploaded = true;
        _noisePixels = null;
    }

    /// <summary>
    /// The game's noise texture (null: not found, a hash stands in).
    /// </summary>
    GpuTexture? _noise;

    /// <summary>
    /// The noise with its sampler, published once uploaded.
    /// </summary>
    SampledTexture _noiseSampled;

    /// <summary>
    /// The noise's texels were uploaded.
    /// </summary>
    bool _noiseUploaded;

    /// <summary>
    /// The noise's texels until the upload (null after it).
    /// </summary>
    byte[]? _noisePixels;

    /// <summary>
    /// The noise's size in texels.
    /// </summary>
    (int Width, int Height) _noiseSize;

    #endregion

    #region Cost

    /// <summary>
    /// Times the pass on the GPU.
    /// </summary>
    readonly PassTimer _timer;

    /// <summary>
    /// The GPU and CPU times of the frames drawn (ms), for <see cref="DescribeStats"/>.
    /// </summary>
    readonly List<double> _gpuSamples = [], _cpuSamples = [];

    /// <summary>
    /// The CPU time of the last frame's pass (ms).
    /// </summary>
    public double CpuMs { get; private set; }

    /// <summary>
    /// The GPU time of the last pass read back (ms).
    /// </summary>
    public double GpuMs { get; private set; }

    /// <summary>
    /// What the caster callback drew in the last frame (for the log).
    /// </summary>
    public string CasterStats { get; set; } = "";

    /// <summary>
    /// CPU time of the callback's parts (terrain, objects, foliage) summed over the cascades of the last frame.
    /// </summary>
    public readonly double[] PhaseMs = new double[3];

    /// <summary>
    /// Collects finished GPU timings: native timestamps, read once their frame's slot comes round. Nothing waits, also with
    /// <paramref name="wait"/> (kept for callers; the GL queries could block): a pass only a frame old has no time yet.
    /// </summary>
    public void Poll(bool wait = false)
    {
        _ = wait;
        if (_timer.Poll(_gpuSamples) is { } ms) GpuMs = ms;
    }

    /// <summary>
    /// Mean and 95th percentile of the pass's GPU and CPU time over the frames drawn (the first left out: shaders warm up).
    /// </summary>
    public string DescribeStats()
    {
        Poll();
        static string One(List<double> values)
        {
            var s = values.Skip(1).OrderBy(x => x).ToList();
            if (s.Count == 0) return "none";

            return $"mean {s.Average():0.00}, p95 {s[Math.Min((int)(s.Count * 0.95), s.Count - 1)]:0.00}, max {s[^1]:0.00}";
        }

        return $"{_cpuSamples.Count} frames: gpu {One(_gpuSamples)} ms, cpu {One(_cpuSamples)} ms";
    }

    #endregion

    #region Debug views

    /// <summary>
    /// The scene depth copied for the debug view (<see cref="CaptureDepth"/>).
    /// </summary>
    GpuTexture? _sceneDepth;

    /// <summary>
    /// The copied depth with its nearest sampler.
    /// </summary>
    SampledTexture _sceneDepthSampled;

    /// <summary>
    /// The debug views' programs: the scene's shadow term and the cascade maps (native, made on first use).
    /// </summary>
    LegacyProgram? _debugProgram, _atlasProgram;   // the debug views (native, made on first use)

    /// <summary>
    /// The debug programs' uniforms: the scene view's inverse view-projection, eye and mode, the atlas view's layer count and grid.
    /// </summary>
    UniformHandle _debugInverse, _debugEye, _debugMode, _atlasLayersHandle, _atlasGridHandle;

    /// <summary>
    /// The debug programs' samplers: the scene depth and the cascade array.
    /// </summary>
    SamplerSlot _debugDepth, _atlasSlot;

    /// <summary>
    /// Copies the scene's depth (in <see cref="DepthConvention"/>, what the depth buffer holds at the end of the frame) for <see cref="DrawDebug"/>.
    /// Call before the post-processing resolves.
    /// </summary>
    public void CaptureDepth(GpuTexture scene, int width, int height)
    {
        // The debug view is Vulkan-only so far (its raw depth copy has no seam call yet).
        if (Gpu is not GpuContext vk) return;

        var source = vk.Device.NativeImage(scene);
        var cmd = vk.BeginNative("shadow debug depth copy");
        bool sized = _sceneDepth is not null && width == _sceneDepth.Desc.Width && height == _sceneDepth.Desc.Height;
        if (!sized || _sceneDepth!.Desc.Format != scene.Desc.Format)
        {
            // Made in the copy's own commands (its layout set there, before the copy) and as a depth target, as the water's copy
            // (WaterRenderer.CaptureScene): made apart, the copy read blocks of undefined depth.
            _sceneDepth?.Dispose();
            _sceneDepth = vk.CreateTexture(new TextureDesc(scene.Desc.Format, width, height,
                Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.DepthTarget, Name: "shadow debug scene depth"),
                ((CommandList)cmd).Handle);
            _sceneDepthSampled = Sampled(_sceneDepth, TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.Repeat);
        }

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 1),
            Extent = new Extent3D((uint)width, (uint)height, 1),
        };
        vk.Device.Vk.CmdCopyImage(((CommandList)cmd).Handle, source, ImageLayout.General, vk.Device.NativeImage(_sceneDepth), ImageLayout.General, 1,
            &region);
        vk.EndNative(cmd);
    }

    /// <summary>
    /// Draws a debug view into <paramref name="target"/> after the frame: 1 the cascade maps in a corner, 2 the shadow term of the scene's surfaces
    /// tinted by cascade, 3 the term multiplied over the picture. <paramref name="sceneProjection"/>: the scene's projection (the captured depth's,
    /// in <see cref="DepthConvention"/>).
    /// </summary>
    public void DrawDebug(int mode, GpuTexture target, int width, int height, Matrix4x4 view, Matrix4x4 sceneProjection, Vector3 eye)
    {
        if (mode <= 0) return;

        if (_debugProgram is null)
        {
            // Native (docs/renderer-native.md 7.1, wave 3 agent F, step P): VkGl's SPIR-V and layout, made once; the receiver's blocks and
            // textures come from the frame globals.
            var d = _debugProgram = LegacyProgram.Create(Gpu, ShadowShaders.FullscreenVertex, ShadowShaders.DebugFragment, "shadow debug");
            (_debugInverse, _debugEye, _debugMode, _debugDepth) =
                (d.Uniform("uInverse"), d.Uniform("uEye"), d.Uniform("uMode"), d.Sampler("uSceneDepth"));
            var a = _atlasProgram = LegacyProgram.Create(Gpu, ShadowShaders.FullscreenVertex, ShadowShaders.AtlasFragment, "shadow atlas debug");
            (_atlasSlot, _atlasLayersHandle, _atlasGridHandle) = (a.Sampler("uAtlas"), a.Uniform("uLayers"), a.Uniform("uGrid"));
        }

        // The draws' state: every channel, no depth test or write, no culling, no blending (what the post-processing leaves), the multiply for
        // mode 3, blending off for the atlas after the scene view.
        var t = PassTargets.Of(target, null);
        var state = DrawState.For(t.Formats, Gpu.DepthClamp);
        bool scene = mode >= 2 && _sceneDepth is not null;
        var cmd = Gpu.BeginNative("shadow debug");
        cmd.BeginRendering(t.Rendering);
        if (scene) DrawSceneView(cmd, mode, t, state, width, height, view, sceneProjection, eye);
        if ((mode == 1 || mode == 2) && _atlas is not null) DrawAtlasView(cmd, mode, t, state, scene, width, height);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    /// <summary>
    /// The shadow term of the scene's surfaces, tinted by cascade (mode 2) or multiplied over the picture (mode 3).
    /// </summary>
    void DrawSceneView(IGpuCommandList cmd, int mode, PassTargets t, DrawState state, int width, int height, Matrix4x4 view,
        Matrix4x4 sceneProjection, Vector3 eye)
    {
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        Matrix4x4.Invert(rotation * sceneProjection, out var inverse);
        var p = _debugProgram!;
        p.Set(_debugInverse, inverse);
        p.Set(_debugEye, eye);
        p.Set(_debugMode, mode);
        p.Bind(_debugDepth, _sceneDepthSampled);
        var s = mode == 3 ? state with { Blend = new BlendState(true, BlendingFactor.DstColor, BlendingFactor.Zero) } : state;
        DrawFullscreen(cmd, p, t, s, new PixelViewport(0, 0, width, height, 0, 1), "shadow debug");
    }

    /// <summary>
    /// The cascade layers in the lower right corner as the game's atlas, plain depth values (a plain sampler never compares).
    /// </summary>
    void DrawAtlasView(IGpuCommandList cmd, int mode, PassTargets t, DrawState state, bool scene, int width, int height)
    {
        int side = Math.Min(width, height) / (mode == 1 ? 2 : 3);
        var p = _atlasProgram!;
        p.Bind(_atlasSlot, _atlasPlain);
        p.Set(_atlasLayersHandle, _atlasLayers);
        p.Set(_atlasGridHandle, _atlasLayers == 1 ? Settings.Grid : (int)Math.Ceiling(Math.Sqrt(_atlasLayers)));
        var atlasState = scene ? state with { Blend = BlendState.Off } : state;
        var viewport = new PixelViewport(width - side - 8, 8, side, side, 0, 1);
        DrawFullscreen(cmd, p, t, atlasState, viewport, "shadow atlas debug");
    }

    /// <summary>
    /// One fullscreen triangle of <paramref name="p"/> in the debug views' rendering.
    /// </summary>
    void DrawFullscreen(IGpuCommandList cmd, LegacyProgram p, PassTargets t, DrawState state, PixelViewport viewport, string label)
    {
        state.Record(cmd, t with { Viewport = viewport });
        cmd.BindPipeline(Gpu.Pipelines.Get(state.Pipeline(p.Program, p.VertexLayout([]), PrimitiveType.Triangles, t.Formats, label)));
        p.Flush(cmd);
        cmd.Draw(3);
    }

    #endregion

    /// <summary>
    /// Frees the Meitou maps, the cascade array, the noise, the debug depth and the debug programs.
    /// </summary>
    public void Dispose()
    {
        DisposeMeitou();
        FreeAtlas();
        _noise?.Dispose();
        _sceneDepth?.Dispose();
        _debugProgram?.Dispose();
        _atlasProgram?.Dispose();
    }
}
