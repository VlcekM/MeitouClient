using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Half-resolution clouds, independent view histories and a world-space sun-transmission map.
/// </summary>
public sealed class VolumetricCloudRenderer : IDisposable
{
    /// <summary>
    /// The context the images, programs and passes go through.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The clouds' noise atlas (<see cref="VolumetricClouds.CreateAtlas"/>).
    /// </summary>
    readonly SampledImage _noise;

    /// <summary>
    /// The programs: the half-resolution ray march, the history resolve and the sun-transmission map.
    /// </summary>
    readonly LegacyProgram _march, _resolve, _shadow;

    /// <summary>
    /// The native segments the three programs are recorded in.
    /// </summary>
    readonly NativeSegment _marchSegment, _resolveSegment, _shadowSegment;

    /// <summary>
    /// Times the main view's, the reflection's and the shadow map's passes on the GPU.
    /// </summary>
    readonly PassTimer _mainTimer, _mirrorTimer, _shadowTimer;

    /// <summary>
    /// The GPU times read back (scratch).
    /// </summary>
    readonly List<double> _samples = [];

    /// <summary>
    /// The main view's clouds' GPU time (ms, the last read back).
    /// </summary>
    public double MainGpuMs { get; private set; }

    /// <summary>
    /// The water reflection's clouds' GPU time (ms).
    /// </summary>
    public double ReflectionGpuMs { get; private set; }

    /// <summary>
    /// The cloud shadow map's GPU time (ms).
    /// </summary>
    public double ShadowGpuMs { get; private set; }

    /// <summary>
    /// The main view's history and the water reflection's.
    /// </summary>
    readonly ViewHistory _main = new(), _mirror = new();

    /// <summary>
    /// The world-space sun-transmission map (512² R16F; made on the first <see cref="RenderShadow"/>).
    /// </summary>
    GpuTexture? _shadowMap;

    /// <summary>
    /// The world x, z of the shadow map's corner (snapped to its texels, centred on the eye).
    /// </summary>
    public Vector2 ShadowOrigin { get; private set; }

    /// <summary>
    /// The shadow map with its linear, edge-clamped sampler (none before the first <see cref="RenderShadow"/>).
    /// </summary>
    public SampledTexture ShadowMap => _shadowMap is null ? default : Sample(_shadowMap);

    /// <summary>
    /// The slab's base height (the cloud height setting); the top follows at <see cref="VolumetricClouds.Thickness"/> above it.
    /// </summary>
    public float Bottom { get; set; } = VolumetricClouds.Bottom;

    /// <summary>
    /// Texels (half-size) round a texel's centre whose rays must all be hidden for the fog early-out to leave it out.
    /// </summary>
    public const float FogSkipReach = 1.5f;

    /// <summary>
    /// One view's clouds over time: its images and what they were made with, so the next frame can tell whether to reproject them.
    /// </summary>
    sealed class ViewHistory : IDisposable
    {
        /// <summary>
        /// This frame's marched clouds and their opacity-weighted distance (half resolution), and the two resolved images (A the last, B the next).
        /// </summary>
        public GpuTexture? Current, Distance, A, B;

        /// <summary>
        /// The view-projection the history was made with.
        /// </summary>
        public Matrix4x4 Previous;

        /// <summary>
        /// The eye, key light, light, ambient and forward direction the history was made with.
        /// </summary>
        public Vector3 Eye, Key, Light, Ambient, Forward;

        /// <summary>
        /// The wind offset the history was made with.
        /// </summary>
        public Vector2 Wind;

        /// <summary>
        /// The coverage and slab base the history was made with.
        /// </summary>
        public float Coverage, Bottom;

        /// <summary>
        /// The frame the history was made in (-10: none, it restarts).
        /// </summary>
        public long Frame = -10;

        /// <summary>
        /// Frees the images; the history restarts.
        /// </summary>
        public void Dispose()
        {
            Current?.Dispose();
            Distance?.Dispose();
            A?.Dispose();
            B?.Dispose();
            Current = Distance = A = B = null;
            Frame = -10;
        }
    }

    /// <summary>
    /// Makes the noise atlas, the three programs and their segments, and the timers.
    /// </summary>
    public VolumetricCloudRenderer(IGpuContext gpu)
    {
        this._gpu = gpu;
        _mainTimer = new(gpu);
        _mirrorTimer = new(gpu);
        _shadowTimer = new(gpu);
        _noise = SampledImage.Rgba8(gpu, VolumetricClouds.AtlasSize, VolumetricClouds.AtlasSize, VolumetricClouds.CreateAtlas(), false, false,
            "cloud noise atlas");
        _march = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, VolumetricCloudShaders.March, "cloud march");
        _resolve = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, VolumetricCloudShaders.Resolve, "cloud history");
        _shadow = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, VolumetricCloudShaders.Shadow, "cloud shadow map");
        _marchSegment = new(gpu, _march, PrimitiveType.Triangles, "cloud march");
        _resolveSegment = new(gpu, _resolve, PrimitiveType.Triangles, "cloud history");
        _shadowSegment = new(gpu, _shadow, PrimitiveType.Triangles, "cloud shadow map");
    }

    /// <summary>
    /// A sampled colour target, made in an upload batch.
    /// </summary>
    GpuTexture Make(int width, int height, InternalFormat format, string name)
    {
        using var upload = _gpu.Uploads.Begin();
        return upload.Create(new TextureDesc(format, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: name));
    }

    /// <summary>
    /// A texture with the linear, edge-clamped sampler.
    /// </summary>
    SampledTexture Sample(GpuTexture t)
    {
        var sampler = _gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Linear, TextureMagFilter.Linear));
        return new(sampler, t.View());
    }

    /// <summary>
    /// Sets what every cloud program reads: the noise atlas, the coverage and wind (and this frame's jitter), the eye and the cloud slab.
    /// </summary>
    void Common(LegacyProgram p, float coverage, Vector2 wind, Vector3 eye)
    {
        p.Bind(p.Sampler("uNoise"), _noise.Sampled());
        p.Set(p.Uniform("uVolume"), coverage, wind.X, wind.Y, (_gpu.Frame.Number % 16) * 0.618033989f);
        p.Set(p.Uniform("uEye"), eye);
        p.Set(p.Uniform("uCloudSlab"), Bottom, Bottom + VolumetricClouds.Thickness);
    }

    /// <summary>
    /// One fullscreen triangle of <paramref name="p"/> into <paramref name="target"/> (and <paramref name="extra"/>), as its own native rendering.
    /// </summary>
    void Draw(LegacyProgram p, NativeSegment segment, GpuTexture target, GpuTexture? extra = null)
    {
        var targets = PassTargets.Of(target, null, extra);
        var state = DrawState.For(targets.Formats, false);
        var cmd = _gpu.BeginNative(p.Name);
        cmd.BeginRendering(targets.Rendering);
        state.Record(cmd, targets);
        cmd.BindPipeline(segment.Get(state, targets.Formats, null));
        p.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.Order();
        _gpu.EndNative(cmd);
    }

    /// <summary>
    /// Both views' histories restart (the clouds' settings jumped).
    /// </summary>
    public void Invalidate()
    {
        _main.Frame = _mirror.Frame = -10;
        MainMotion = null;
    }

    /// <summary>
    /// The main view's history only (its sky left out this frame; the reflection keeps marching).
    /// </summary>
    public void InvalidateMain()
    {
        _main.Frame = -10;
        MainMotion = null;
    }

    /// <summary>
    /// The main view's clouds for the upscalers' motion vectors (<see cref="PostProcess"/>'s cloud motion pass, docs/render-clouds.md "Temporal, fog
    /// and shadows"): the resolved image (opacity in alpha), the opacity-weighted distance along each ray, the wind's shift since the last frame in
    /// world units and the frame they were made in; null when the main view's history restarted.
    /// </summary>
    public CloudMotion? MainMotion { get; private set; }

    /// <summary>
    /// Before opening the scene host. The reflection has its own size, eye and history. <paramref name="fogSkip"/>: texels whose sky the placed fog
    /// volumes hide completely are not marched (the main view only, and only when the fog volume pass runs over this frame's sky).
    /// </summary>
    public SampledTexture Render(Matrix4x4 viewProjection, Vector3 eye, int width, int height, float coverage, Vector2 wind,
        Vector3 key, Vector3 light, Vector3 ambient, bool reflection, bool fogSkip = false)
    {
        if (_gpu.PassOpen) throw new InvalidOperationException("Prepare clouds before opening the scene pass");
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return default;
        var h = reflection ? _mirror : _main;
        var timer = reflection ? _mirrorTimer : _mainTimer;
        if (!reflection) MainMotion = null;
        _samples.Clear();
        if (timer.Poll(_samples) is { } ms)
        {
            if (reflection) ReflectionGpuMs = ms;
            else MainGpuMs = ms;
        }
        timer.Begin();
        int w = Math.Max(1, (width + 1) / 2), ht = Math.Max(1, (height + 1) / 2);
        EnsureHistory(h, w, ht);
        Common(_march, coverage, wind, eye);
        _march.Set(_march.Uniform("uInverse"), in inverse);
        _march.Set(_march.Uniform("uKey"), key);
        _march.Set(_march.Uniform("uLight"), light);
        _march.Set(_march.Uniform("uAmbient"), ambient);
        _march.ApplyGlobals();
        _march.Set(_march.Uniform("uSteps"), _gpu.IsIntegrated ? 64 : 96);
        // The reach round a texel's centre: the bilinear lookup of the full-size sky reads a texel from up to one texel away, and the fog pass's
        // ray is the unjittered pixel centre (half a full-size pixel, a quarter texel, off); 1.5 texels with room to spare.
        _march.Set(_march.Uniform("uFogSkip"), fogSkip ? 1 : 0, FogSkipReach * 2f / w, FogSkipReach * 2f / ht, 0);
        Draw(_march, _marchSegment, h.Current!, h.Distance);

        Vector2 windDelta = wind - h.Wind;
        windDelta -= new Vector2(MathF.Round(windDelta.X), MathF.Round(windDelta.Y));
        var rayA = Vector4.Transform(new Vector4(0, 0, 0, 1), inverse);
        var rayB = Vector4.Transform(new Vector4(0, 0, 1, 1), inverse);
        var forward = Vector3.Normalize(new Vector3(rayB.X, rayB.Y, rayB.Z) / rayB.W - new Vector3(rayA.X, rayA.Y, rayA.Z) / rayA.W);
        bool valid = ReprojectionValid(h, eye, coverage, key, light, ambient, forward, windDelta);
        _resolve.Bind(_resolve.Sampler("uCurrent"), Sample(h.Current!));
        // Bind the current image on the first frame: never read an uninitialised history attachment.
        _resolve.Bind(_resolve.Sampler("uHistory"), Sample(valid ? h.A! : h.Current!));
        _resolve.Bind(_resolve.Sampler("uDistance"), Sample(h.Distance!));
        _resolve.Set(_resolve.Uniform("uInverse"), in inverse);
        _resolve.Set(_resolve.Uniform("uPrevious"), in h.Previous);
        _resolve.Set(_resolve.Uniform("uEye"), eye);
        _resolve.Set(_resolve.Uniform("uPreviousEye"), h.Eye);
        _resolve.Set(_resolve.Uniform("uWindDelta"), windDelta.X * VolumetricClouds.PatternPeriod,
            windDelta.Y * VolumetricClouds.PatternPeriod);
        _resolve.Set(_resolve.Uniform("uHistoryWeight"), valid ? 0.85f : 0);
        Draw(_resolve, _resolveSegment, h.B!);
        (h.A, h.B) = (h.B, h.A);
        if (!reflection && valid)
            MainMotion = new CloudMotion(Sample(h.A!), Sample(h.Distance!), windDelta * VolumetricClouds.PatternPeriod, _gpu.Frame.Number);
        (h.Previous, h.Eye, h.Key, h.Light, h.Ambient, h.Forward, h.Wind, h.Coverage, h.Bottom, h.Frame) =
            (viewProjection, eye, key, light, ambient, forward, wind, coverage, Bottom, _gpu.Frame.Number);
        timer.End();
        return Sample(h.A!);
    }

    /// <summary>
    /// Remakes the history's images at <paramref name="w"/> × <paramref name="ht"/> when their size changed (the history is lost).
    /// </summary>
    void EnsureHistory(ViewHistory h, int w, int ht)
    {
        if (h.Current is not null && h.Current.Desc.Width == w && h.Current.Desc.Height == ht) return;
        h.Dispose();
        h.Current = Make(w, ht, InternalFormat.Rgba16F, "cloud current");
        h.Distance = Make(w, ht, InternalFormat.R32F, "cloud distance");
        h.A = Make(w, ht, InternalFormat.Rgba16F, "cloud history a");
        h.B = Make(w, ht, InternalFormat.Rgba16F, "cloud history b");
    }

    /// <summary>
    /// Whether the history may be reprojected this frame: recent, from a close eye and the same settings and view.
    /// </summary>
    bool ReprojectionValid(ViewHistory h, Vector3 eye, float coverage, Vector3 key, Vector3 light, Vector3 ambient, Vector3 forward,
        Vector2 windDelta)
    {
        return _gpu.Frame.Number - h.Frame <= 2 && Vector3.DistanceSquared(eye, h.Eye) < 4000000
            && MathF.Abs(coverage - h.Coverage) < 0.03f && Bottom == h.Bottom && Vector3.DistanceSquared(key, h.Key) < 0.0025f
            && Vector3.Dot(light, h.Light) > 0.999f && Vector3.Dot(forward, h.Forward) > 0.9f
            && Vector3.DistanceSquared(ambient, h.Ambient) < 0.0025f && windDelta.LengthSquared() < 0.0001f;
    }

    /// <summary>
    /// Draws the world-space sun-transmission map around the eye (before the scene pass opens).
    /// </summary>
    public void RenderShadow(Vector3 eye, float coverage, Vector2 wind, Vector3 sun)
    {
        if (_gpu.PassOpen) throw new InvalidOperationException("Prepare cloud shadows before opening the scene pass");
        _samples.Clear();
        if (_shadowTimer.Poll(_samples) is { } ms) ShadowGpuMs = ms;
        _shadowTimer.Begin();
        _shadowMap ??= Make(512, 512, InternalFormat.R16F, "cloud shadow map");
        const float texel = VolumetricClouds.ShadowSpan / 512;
        ShadowOrigin = new(MathF.Floor(eye.X / texel) * texel - VolumetricClouds.ShadowSpan * 0.5f,
                           MathF.Floor(eye.Z / texel) * texel - VolumetricClouds.ShadowSpan * 0.5f);
        Common(_shadow, coverage, wind, eye);
        _shadow.Set(_shadow.Uniform("uLight"), sun);
        _shadow.Set(_shadow.Uniform("uShadowOrigin"), ShadowOrigin.X, ShadowOrigin.Y);
        Draw(_shadow, _shadowSegment, _shadowMap);
        _shadowTimer.End();
    }

    /// <summary>
    /// Frees both histories, the shadow map, the noise atlas and the programs.
    /// </summary>
    public void Dispose()
    {
        _main.Dispose();
        _mirror.Dispose();
        _shadowMap?.Dispose();
        _shadowMap = null;
        _noise.Dispose();
        _march.Dispose();
        _resolve.Dispose();
        _shadow.Dispose();
    }
}

/// <summary>
/// One frame's volumetric clouds for the motion vectors (<see cref="VolumetricCloudRenderer.MainMotion"/>).
/// </summary>
public readonly record struct CloudMotion(SampledTexture Clouds, SampledTexture Distance, Vector2 Wind, long Frame);