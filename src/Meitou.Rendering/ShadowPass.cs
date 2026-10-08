using System.Diagnostics;
using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// The sun's shadow map as the game's CSM mode draws it (docs/formats/shadows.md): four cascades fitted by <see cref="ShadowCascades"/>
/// (Meitou.Data, backend-independent) in the tiles of one square depth atlas, the casters drawn by the renderers' depth-only entry points
/// through <see cref="CasterDraw"/>. This class is the thin GPU part: the atlas, the caster state, the uniform blocks the receivers read
/// (<see cref="ShadowShaders.Functions"/>), timing and the debug views.
/// </summary>
public sealed unsafe partial class ShadowPass : IDisposable
{
    /// <summary>Draws the casters of one cascade: the matrix maps absolute world positions to the tile's clip space (depth 0..1), the planes cull (no near plane), the eye picks levels of detail.</summary>
    public delegate void CasterDraw(ShadowCascade cascade, Matrix4x4 worldToClip, Vector4[] planes, Vector3 lodEye);

    /// <summary>The native GPU API (docs/renderer-native.md 7.1 step 8; all native since phase 8 stage 3: the atlas's host hands the casters
    /// its targets and state, 8.9).</summary>
    public GpuContext Gpu { get; }
    // The receiver block (the faithful layout, also the Meitou receiver's cascades) and the casters' bias of the cascade being drawn.
    readonly FrameBlock receiver;
    readonly FrameBlock casterBias;
    readonly PassTimer timer;
    readonly List<double> gpuSamples = [], cpuSamples = [];
    Texture? atlas, noise;
    SampledTexture atlasShadow, atlasPlain, noiseSampled;
    bool atlasPublished, noiseUploaded;
    byte[]? noisePixels;
    (int Width, int Height) noiseSize;
    int atlasSize;
    LegacyProgram? debugProgram, atlasProgram;   // the debug views (native, made on first use)
    UniformHandle debugInverse, debugEye, debugMode;
    SamplerSlot debugDepth, atlasSlot;
    Texture? sceneDepth;
    SampledTexture sceneDepthSampled;

    /// <param name="assets">Where to find the game's <c>white-noise.png</c> (the receiver's jitter); without it a hash stands in.</param>
    public ShadowPass(GpuContext gpu, AssetLocator? assets = null)
    {
        Gpu = gpu;
        receiver = new FrameBlock(gpu, ReceiverBytes);
        casterBias = new FrameBlock(gpu, 16);
        meitouBlock = new FrameBlock(gpu, MeitouShadowShaders.BlockBytes);
        timer = new PassTimer(gpu);
        LoadNoise(assets);
        PublishGlobals();
        Disable();   // a valid block (shadows off) before the first frame
    }

    /// <summary>
    /// The shadow blocks and maps as frame globals (docs/renderer-native.md 4.3), native: they replace what <see cref="ShadowShaders"/> publishes
    /// from GL's binding points and units (still the source without a ShadowPass: --no-shadows, the model viewer). A texture appears where the GL
    /// code bound it to its unit (the atlas with the receiver block, the noise from the first frame, the terrain and blocker maps with the Meitou
    /// block when they are on); before that the readers get the stand-in.
    /// </summary>
    void PublishGlobals()
    {
        var g = Gpu.Globals;
        g.Publish(ShadowShaders.ReceiverBlock, receiver.Binding);
        g.Publish(ShadowShaders.CasterBlock, casterBias.Binding);
        g.Publish("uShadowMap", () => atlasPublished ? atlasShadow : default);
        g.Publish("uShadowNoise", () => { UploadNoise(); return noiseSampled; });   // a segment being prepared: a frame is open
        g.Publish(MeitouShadowShaders.Block, meitouBlock.Binding);
        // The terrain shadow map with its own sampler (linear, clamped; TerrainShadowMap).
        g.Publish("uShadowTerrain", () => boundTerrain ?? default);
        g.Publish("uShadowBlocker", () => blockerPublished ? blockerSampled : default);
        g.Publish("uShadowLandmark", () => landmarkPublished ? landmarkSampled : default);   // the landmark shadow map (comparison, linear, clamped)
    }

    /// <summary>A texture with the sampler VkGl made from these GL parameters (the same rule, <see cref="SamplerDesc.FromGl"/>). None of these
    /// textures is mipmapped, so the LOD bias never applies; wrap R is GL's default.</summary>
    SampledTexture Sampled(Texture t, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap, bool compare = false) =>
        new(Gpu.Samplers.Get(SamplerDesc.FromGl(min, mag, wrap, wrap, TextureWrapMode.Repeat, compare, DepthFunction.Lequal, false, 1, false, 0)), t.View(), t.Image);

    /// <summary>The atlas side, the range and the number of cascades (the game's <c>shadow quality</c> and <c>Shadow Range</c>).</summary>
    public ShadowSettings Settings { get; set; } = new();
    /// <summary>The memory-pressure guard (<see cref="VramGuard"/>): its range scale shortens the shadow range (never below the game's default, or
    /// the setting when that is shorter) while the video memory is nearly used up. Null: no clamp.</summary>
    public VramGuard? Guard { get; set; }
    /// <summary>The range the cascades are fitted to and the casters are culled by: <see cref="Settings"/>' range, clamped by the guard.</summary>
    public float EffectiveRange
    {
        get
        {
            float range = Settings.Range, scale = Guard?.RangeScale ?? 1f;
            return scale >= 1f ? range : Math.Max(Math.Min(range, KenshiShadows.DefaultRange), range * scale);
        }
    }
    ShadowSettings Effective => EffectiveRange is var range && range == Settings.Range ? Settings : Settings with { Range = range };
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Below this height of the real sun there is no shadow map. As the game: it keeps drawing the map along the lighting direction (whose
    /// height is clamped to 0 under the horizon) while the sun still has a colour, down to −0.2.
    /// </summary>
    public float MinSunHeight { get; set; } = KenshiLighting.SunColourCutoff;
    /// <summary>Whether the game's noise texture was found (else the receiver's jitter is a hash).</summary>
    public bool HasNoise => noise is not null;
    /// <summary>The cascades of the last frame (null when nothing was drawn).</summary>
    public ShadowCascade[]? Cascades { get; private set; }
    /// <summary>The atlas (native; null before the first frame).</summary>
    public Texture? AtlasTexture => atlas;
    public double CpuMs { get; private set; }
    public double GpuMs { get; private set; }
    /// <summary>What the caster callback drew in the last frame (for the log).</summary>
    public string CasterStats { get; set; } = "";
    /// <summary>CPU time of the callback's parts (terrain, objects, foliage) summed over the cascades of the last frame.</summary>
    public readonly double[] PhaseMs = new double[3];

    /// <summary>Size of the receiver block (std140) the shaders declare in <see cref="ShadowShaders.Functions"/>.</summary>
    public const int ReceiverBytes = 560;

    /// <summary>
    /// Draws the cascades for this camera and light, then publishes them to the receivers.
    /// <paramref name="toSun"/> is the lighting direction (towards the sun, height clamped to 0 under the horizon), <paramref name="sunHeight"/>
    /// the real sun's height (the map is drawn while it is at least <see cref="MinSunHeight"/>; by default the lighting direction's).
    /// <paramref name="landmarks"/>: the landmarks for the Meitou shadows' landmark map (they cast shadows however far they are).
    /// </summary>
    public void Render(ShadowView view, Vector3 toSun, CasterDraw draw, float? sunHeight = null, LandmarkCasters? landmarks = null)
    {
        Poll();
        UploadNoise();
        Cascades = null;
        if (!Enabled || (sunHeight ?? toSun.Y) < MinSunHeight || toSun.LengthSquared() < 1e-8f) { Disable(); return; }
        if (Meitou) { RenderMeitou(view, Vector3.Normalize(toSun), draw, landmarks); return; }
        meitouValid = false;
        var watch = Stopwatch.StartNew();
        Array.Clear(PhaseMs);
        Resize(Settings.MapSize);
        var cascades = ShadowCascades.Fit(view, Vector3.Normalize(toSun), Effective);
        timer.Begin();

        var host = BeginHost("shadow atlas", clear: true);
        foreach (var c in cascades)
        {
            if (c.Unused) continue;   // in front of the camera's near plane: nothing on screen reads it (a viewer saving)
            int x = (int)MathF.Round(c.Tile.X * atlasSize), y = (int)MathF.Round(c.Tile.Y * atlasSize), s = Settings.TileSize;
            SetTile(x, y, s);
            SetCasterBias(new Vector4(c.FixedBias, KenshiShadows.SlopeBias, KenshiShadows.MaxSlopeBias, 0));
            draw(c, c.WorldToClip(), c.CullPlanes(), view.Eye);
        }
        EndHost(host);
        timer.End();
        Cascades = cascades;
        Publish(view, cascades);
        CpuMs = watch.Elapsed.TotalMilliseconds;
        cpuSamples.Add(CpuMs);
    }

    /// <summary>The casters' bias block of the cascade about to be drawn: the guests' segments take it when they are prepared (in the
    /// draw callback), a new slice after each write, as VkGl renamed the GL buffer written between their draws.</summary>
    void SetCasterBias(in Vector4 bias) => casterBias.Set([bias.X, bias.Y, bias.Z, bias.W]);

    /// <summary>
    /// Opens the atlas's rendering as a native host (docs/renderer-native.md 4.5): the caller has the atlas framebuffer bound and the GL state its
    /// guests read set, and the guests (terrain, objects, foliage, all native) record into the returned list's rendering instance through
    /// <c>BeginNativeInPass</c>, which finds the host. <paramref name="clear"/>: the whole atlas is cleared by the load op (else it is loaded).
    /// </summary>
    CommandList BeginHost(string label, bool clear, Texture? target = null)
    {
        var cmd = Gpu.BeginNative(label);
        var t = PassTargets.Of(null, target ?? atlas!);
        var depth = clear ? t.Depth with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)) } : t.Depth;
        // Wave 4 (docs/renderer-native.md 6): the cascades' segments are secondaries, recorded on the job threads when the host ends.
        bool secondaries = Recording.Secondaries;
        cmd.BeginRendering(new RenderingDesc(default, depth, t.Width, t.Height), secondaries);
        Gpu.BeginHostPass(cmd, t, CasterState(t.Formats));
        if (secondaries) Gpu.Frame.Parallel.Begin(cmd, t.Formats, ShadowStage);
        return cmd;
    }

    /// <summary>
    /// What the casters draw with (what VkGl made of the GL state the GL code set for them): depth only, tested with less and written, clamped
    /// (casters between the box and the sun are flattened onto its near side, not clipped), no culling (each caster sets its own), no colour.
    /// </summary>
    DrawState CasterState(in AttachmentFormats formats) =>
        DrawState.For(formats, Gpu.Device.DepthClamp, depthTest: true, depthWrite: true, compare: CompareOp.Less, mask: 0, depthClamp: true);

    /// <summary>The cascade's tile of the atlas: the viewport and scissor the casters draw with.</summary>
    void SetTile(int x, int y, int s) =>
        Gpu.SetPassViewport(new Viewport(x, y, s, s, 0, 1), new Rect2D(new Offset2D(x, y), new Extent2D((uint)s, (uint)s)));

    /// <summary>The <see cref="StageClock"/> stage of the shadow casters' jobs.</summary>
    const int ShadowStage = 12;

    /// <summary>A partial clear of the atlas (a Meitou tile) in its place among the guests (<see cref="GpuContext.ClearDepth"/>).</summary>
    void ClearTile(CommandList host, Rect2D rect) => Gpu.ClearDepth(host, 1f, rect);

    void EndHost(CommandList cmd)
    {
        if (Gpu.Frame.Parallel.Open) Gpu.Frame.Parallel.End();
        cmd.EndRendering();
        Gpu.EndHostPass(cmd);
        Gpu.EndNative(cmd);
    }

    /// <summary>Marks the shadows off for the receivers (the term is 1 everywhere).</summary>
    public void Disable()
    {
        meitouValid = false;
        var data = new float[ReceiverBytes / 4];
        Upload(data);
    }

    void Publish(ShadowView view, ShadowCascade[] cascades)
    {
        var data = new float[ReceiverBytes / 4];
        var origin = view.Eye;
        for (int i = 0; i < cascades.Length && i < 4; i++)
        {
            var c = cascades[i];
            Put(data, i * 16, c.OriginToTile(origin));
            Put(data, 64 + i * 4, c.Tile);
            Put(data, 80 + i * 4, new Vector4(c.SelectDepth, c.FilterRadius, c.Size.X, c.Size.Y));
            Put(data, 96 + i * 4, new Vector4(c.Size.Z, 1f / Settings.TileSize, 0, 0));
        }
        Put(data, 112, cascades[0].Rotation);
        Put(data, 128, new Vector4(origin, 1));
        Put(data, 132, new Vector4(Vector3.Normalize(view.Forward), Math.Min(cascades.Length, 4)));
        Put(data, 136, new Vector4(atlasSize, 0, noise is not null ? 1 : 0, 0));
        Upload(data);
    }

    /// <summary>The receiver block; the atlas is published to the receivers from now on (the GL code bound it to its unit here).</summary>
    void Upload(float[] data)
    {
        receiver.Set(data);
        if (atlas is not null) atlasPublished = true;
    }

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
            noise = Texture.Create(Gpu, new TextureDesc(Format.R8G8B8A8Unorm, image.Width, image.Height, Name: "shadow noise"));
            noiseSize = (image.Width, image.Height);
            noisePixels = image.Pixels;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            Console.WriteLine($"warning   shadow noise: {e.Message}");
        }
    }

    /// <summary>The noise's texels, in the first frame (an upload goes with a frame); published from then on.</summary>
    void UploadNoise()
    {
        if (noiseUploaded || noise is null || noisePixels is null || !Gpu.Frame.Open) return;
        Gpu.Uploads.Write(noise, 0, 0, new Rect2D(default, new Extent2D((uint)noiseSize.Width, (uint)noiseSize.Height)), noisePixels);
        noiseSampled = Sampled(noise, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.Repeat);
        noiseUploaded = true;
        noisePixels = null;
    }

    static void Put(float[] d, int at, Matrix4x4 m)
    {
        // System.Numerics row-major memory is what GL reads as the column-major column-vector form (as WorldGl.Matrix).
        d[at] = m.M11; d[at + 1] = m.M12; d[at + 2] = m.M13; d[at + 3] = m.M14;
        d[at + 4] = m.M21; d[at + 5] = m.M22; d[at + 6] = m.M23; d[at + 7] = m.M24;
        d[at + 8] = m.M31; d[at + 9] = m.M32; d[at + 10] = m.M33; d[at + 11] = m.M34;
        d[at + 12] = m.M41; d[at + 13] = m.M42; d[at + 14] = m.M43; d[at + 15] = m.M44;
    }

    static void Put(float[] d, int at, Vector4 v) { d[at] = v.X; d[at + 1] = v.Y; d[at + 2] = v.Z; d[at + 3] = v.W; }

    void Resize(int size)
    {
        if (size == atlasSize && atlas is not null) return;
        FreeAtlas();
        atlasSize = size;
        atlas = Texture.Create(Gpu, new TextureDesc(Format.D32Sfloat, size, size, Use: TextureUse.Sampled | TextureUse.DepthTarget, Name: "shadow atlas"));
        // Linear comparison: the faithful receiver reads texel centres, where the weights are 1, 0, 0, 0 (the game's point sampling);
        // other receivers may use the bilinear weights. The plain sampler (no comparison) is the blocker pass's and the debug view's.
        atlasShadow = Sampled(atlas, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, compare: true);
        atlasPlain = Sampled(atlas, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
    }

    void FreeAtlas()
    {
        if (atlas is null) return;
        atlas.Dispose();
        (atlas, atlasPublished) = (null, false);
    }

    // ------------------------------------------------------------------ cost

    /// <summary>Collects finished GPU timings: native timestamps, read once their frame's slot comes round. Nothing waits, also with
    /// <paramref name="wait"/> (kept for callers; the GL queries could block): a pass only a frame old has no time yet.</summary>
    public void Poll(bool wait = false)
    {
        _ = wait;
        if (timer.Poll(gpuSamples) is { } ms) GpuMs = ms;
    }

    /// <summary>Mean and 95th percentile of the pass's GPU and CPU time over the frames drawn (the first left out: shaders warm up).</summary>
    public string DescribeStats()
    {
        Poll();
        static string One(List<double> values)
        {
            var s = values.Skip(1).OrderBy(x => x).ToList();
            if (s.Count == 0) return "none";
            return $"mean {s.Average():0.00}, p95 {s[Math.Min((int)(s.Count * 0.95), s.Count - 1)]:0.00}, max {s[^1]:0.00}";
        }
        return $"{cpuSamples.Count} frames: gpu {One(gpuSamples)} ms, cpu {One(cpuSamples)} ms";
    }

    // ------------------------------------------------------------------ debug views

    /// <summary>Copies the scene's depth (the near slice, what the depth buffer holds at the end of the frame) for <see cref="DrawDebug"/>. Call before the post-processing resolves.</summary>
    public void CaptureDepth(Texture scene, int width, int height)
    {
        if (sceneDepth is null || width != sceneDepth.Desc.Width || height != sceneDepth.Desc.Height)
        {
            sceneDepth?.Dispose();
            sceneDepth = Texture.Create(Gpu, new TextureDesc(Format.D32Sfloat, width, height, Name: "shadow debug scene depth"));
            sceneDepthSampled = Sampled(sceneDepth, TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.Repeat);
        }
        var source = scene.Image;
        var cmd = Gpu.BeginNative("shadow debug depth copy");
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 1),
            Extent = new Extent3D((uint)width, (uint)height, 1),
        };
        Gpu.Device.Vk.CmdCopyImage(cmd.Handle, source, ImageLayout.General, sceneDepth.Image, ImageLayout.General, 1, &region);
        Gpu.EndNative(cmd);
    }

    /// <summary>
    /// Draws a debug view into <paramref name="target"/> after the frame: 1 the cascade maps in a corner, 2 the shadow term of the
    /// scene's surfaces tinted by cascade, 3 the term multiplied over the picture. <paramref name="nearProjection"/>: the near slice's
    /// projection (the captured depth's).
    /// </summary>
    public void DrawDebug(int mode, Texture target, int width, int height, Matrix4x4 view, Matrix4x4 nearProjection, Vector3 eye)
    {
        if (mode <= 0) return;
        if (debugProgram is null)
        {
            // Native (docs/renderer-native.md 7.1, wave 3 agent F, step P): VkGl's SPIR-V and layout, made once; the receiver's blocks and
            // textures come from the frame globals.
            var d = debugProgram = LegacyProgram.Create(Gpu, ShadowShaders.FullscreenVertex, ShadowShaders.DebugFragment, "shadow debug");
            (debugInverse, debugEye, debugMode, debugDepth) = (d.Uniform("uInverse"), d.Uniform("uEye"), d.Uniform("uMode"), d.Sampler("uSceneDepth"));
            var a = atlasProgram = LegacyProgram.Create(Gpu, ShadowShaders.FullscreenVertex, ShadowShaders.AtlasFragment, "shadow atlas debug");
            atlasSlot = a.Sampler("uAtlas");
        }
        // The draws' state: every channel, no depth test or write, no culling, no blending (what the post-processing leaves), the multiply for
        // mode 3, blending off for the atlas after the scene view.
        var t = PassTargets.Of(target, null);
        var state = DrawState.For(t.Formats, Gpu.Device.DepthClamp);
        bool scene = mode >= 2 && sceneDepth is not null;
        var cmd = Gpu.BeginNative("shadow debug");
        cmd.BeginRendering(t.Rendering);
        if (scene)
        {
            var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
            Matrix4x4.Invert(rotation * nearProjection, out var inverse);
            var p = debugProgram;
            p.Set(debugInverse, inverse);
            p.Set(debugEye, eye);
            p.Set(debugMode, mode);
            p.Bind(debugDepth, sceneDepthSampled);
            var s = mode == 3 ? state with { Blend = new BlendState(true, BlendFactor.DstColor, BlendFactor.Zero) } : state;
            DrawFullscreen(cmd, p, t, s, new Viewport(0, 0, width, height, 0, 1), "shadow debug");
        }
        if ((mode == 1 || mode == 2) && atlas is not null)
        {
            // The atlas in the lower right corner, plain depth values (a plain sampler never compares).
            int side = Math.Min(width, height) / (mode == 1 ? 2 : 3);
            var p = atlasProgram!;
            p.Bind(atlasSlot, atlasPlain);
            DrawFullscreen(cmd, p, t, scene ? state with { Blend = BlendState.Off } : state, new Viewport(width - side - 8, 8, side, side, 0, 1), "shadow atlas debug");
        }
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    /// <summary>One fullscreen triangle of <paramref name="p"/> in the debug views' rendering.</summary>
    void DrawFullscreen(CommandList cmd, LegacyProgram p, PassTargets t, DrawState state, Viewport viewport, string label)
    {
        state.Record(cmd, t with { Viewport = viewport });
        cmd.BindPipeline(Gpu.Pipelines.Get(state.Pipeline(p.Program, p.VertexLayout([]), PrimitiveTopology.TriangleList, t.Formats, label)));
        p.Flush(cmd);
        cmd.Draw(3);
    }

    public void Dispose()
    {
        DisposeMeitou();
        FreeAtlas();
        noise?.Dispose();
        sceneDepth?.Dispose();
        debugProgram?.Dispose();
        atlasProgram?.Dispose();
    }
}
