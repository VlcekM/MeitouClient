using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>Half-resolution clouds, independent view histories and a world-space sun-transmission map.</summary>
public sealed class VolumetricCloudRenderer : IDisposable
{
    readonly GpuContext gpu;
    readonly SampledImage noise;
    readonly LegacyProgram march, resolve, shadow;
    readonly NativeSegment marchSegment, resolveSegment, shadowSegment;
    readonly PassTimer mainTimer, mirrorTimer, shadowTimer;
    readonly List<double> samples = [];
    public double MainGpuMs { get; private set; }
    public double ReflectionGpuMs { get; private set; }
    public double ShadowGpuMs { get; private set; }
    readonly ViewHistory main = new(), mirror = new();
    Texture? shadowMap;
    public Vector2 ShadowOrigin { get; private set; }
    public SampledTexture ShadowMap => shadowMap is null ? default : Sample(shadowMap);

    sealed class ViewHistory : IDisposable
    {
        public Texture? Current, Distance, A, B;
        public Matrix4x4 Previous;
        public Vector3 Eye, Key, Light, Ambient, Forward;
        public Vector2 Wind;
        public float Coverage;
        public long Frame = -10;
        public void Dispose()
        {
            Current?.Dispose(); Distance?.Dispose(); A?.Dispose(); B?.Dispose();
            Current = Distance = A = B = null;
            Frame = -10;
        }
    }

    public VolumetricCloudRenderer(GpuContext gpu)
    {
        this.gpu = gpu;
        mainTimer = new(gpu); mirrorTimer = new(gpu); shadowTimer = new(gpu);
        noise = SampledImage.Rgba8(gpu, VolumetricClouds.AtlasSize, VolumetricClouds.AtlasSize, VolumetricClouds.CreateAtlas(), false, false, "cloud noise atlas");
        march = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, VolumetricCloudShaders.March, "cloud march");
        resolve = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, VolumetricCloudShaders.Resolve, "cloud history");
        shadow = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, VolumetricCloudShaders.Shadow, "cloud shadow map");
        marchSegment = new(gpu, march, Vk.PrimitiveTopology.TriangleList, "cloud march");
        resolveSegment = new(gpu, resolve, Vk.PrimitiveTopology.TriangleList, "cloud history");
        shadowSegment = new(gpu, shadow, Vk.PrimitiveTopology.TriangleList, "cloud shadow map");
    }

    Texture Make(int width, int height, Vk.Format format, string name)
    {
        using var upload = gpu.Uploads.Begin();
        return upload.Create(new TextureDesc(format, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: name));
    }

    SampledTexture Sample(Texture t)
    {
        var sampler = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        return new(sampler, t.View(), t.Image);
    }

    void Common(LegacyProgram p, float coverage, Vector2 wind, Vector3 eye)
    {
        p.Bind(p.Sampler("uNoise"), noise.Sampled());
        p.Set(p.Uniform("uVolume"), coverage, wind.X, wind.Y, (gpu.Frame.Number % 16) * 0.618033989f);
        p.Set(p.Uniform("uEye"), eye);
    }

    void Draw(LegacyProgram p, NativeSegment segment, Texture target, Texture? extra = null)
    {
        var targets = PassTargets.Of(target, null, extra);
        var state = DrawState.For(targets.Formats, false);
        var cmd = gpu.BeginNative(p.Name);
        cmd.BeginRendering(targets.Rendering);
        state.Record(cmd, targets);
        cmd.BindPipeline(segment.Get(state, targets.Formats, null));
        p.Flush(cmd); cmd.Draw(3); cmd.EndRendering();
        cmd.Barrier(BarrierBatch.Full);
        gpu.EndNative(cmd);
    }

    public void Invalidate() { main.Frame = mirror.Frame = -10; }

    /// <summary>Before opening the scene host. The reflection has its own size, eye and history.</summary>
    public SampledTexture Render(Matrix4x4 viewProjection, Vector3 eye, int width, int height, float coverage, Vector2 wind,
        Vector3 key, Vector3 light, Vector3 ambient, bool reflection)
    {
        if (gpu.PassOpen) throw new InvalidOperationException("Prepare clouds before opening the scene pass");
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return default;
        var h = reflection ? mirror : main;
        var timer = reflection ? mirrorTimer : mainTimer;
        samples.Clear();
        if (timer.Poll(samples) is { } ms) { if (reflection) ReflectionGpuMs = ms; else MainGpuMs = ms; }
        timer.Begin();
        int w = Math.Max(1, (width + 1) / 2), ht = Math.Max(1, (height + 1) / 2);
        if (h.Current is null || h.Current.Desc.Width != w || h.Current.Desc.Height != ht)
        {
            h.Dispose();
            h.Current = Make(w, ht, Vk.Format.R16G16B16A16Sfloat, "cloud current");
            h.Distance = Make(w, ht, Vk.Format.R32Sfloat, "cloud distance");
            h.A = Make(w, ht, Vk.Format.R16G16B16A16Sfloat, "cloud history a");
            h.B = Make(w, ht, Vk.Format.R16G16B16A16Sfloat, "cloud history b");
        }
        Common(march, coverage, wind, eye);
        march.Set(march.Uniform("uInverse"), in inverse);
        march.Set(march.Uniform("uKey"), key); march.Set(march.Uniform("uLight"), light); march.Set(march.Uniform("uAmbient"), ambient);
        march.ApplyGlobals();
        march.Set(march.Uniform("uSteps"), gpu.Device.IsIntegrated ? 64 : 96);
        Draw(march, marchSegment, h.Current, h.Distance);

        Vector2 windDelta = wind - h.Wind;
        windDelta -= new Vector2(MathF.Round(windDelta.X), MathF.Round(windDelta.Y));
        var rayA = Vector4.Transform(new Vector4(0, 0, 0, 1), inverse);
        var rayB = Vector4.Transform(new Vector4(0, 0, 1, 1), inverse);
        var forward = Vector3.Normalize(new Vector3(rayB.X, rayB.Y, rayB.Z) / rayB.W - new Vector3(rayA.X, rayA.Y, rayA.Z) / rayA.W);
        bool valid = gpu.Frame.Number - h.Frame <= 2 && Vector3.DistanceSquared(eye, h.Eye) < 4000000
            && MathF.Abs(coverage - h.Coverage) < 0.03f && Vector3.DistanceSquared(key, h.Key) < 0.0025f
            && Vector3.Dot(light, h.Light) > 0.999f && Vector3.Dot(forward, h.Forward) > 0.9f
            && Vector3.DistanceSquared(ambient, h.Ambient) < 0.0025f && windDelta.LengthSquared() < 0.0001f;
        resolve.Bind(resolve.Sampler("uCurrent"), Sample(h.Current));
        // Bind the current image on the first frame: never read an uninitialised history attachment.
        resolve.Bind(resolve.Sampler("uHistory"), Sample(valid ? h.A! : h.Current));
        resolve.Bind(resolve.Sampler("uDistance"), Sample(h.Distance!));
        resolve.Set(resolve.Uniform("uInverse"), in inverse); resolve.Set(resolve.Uniform("uPrevious"), in h.Previous);
        resolve.Set(resolve.Uniform("uEye"), eye); resolve.Set(resolve.Uniform("uPreviousEye"), h.Eye);
        resolve.Set(resolve.Uniform("uWindDelta"), windDelta.X * VolumetricClouds.PatternPeriod, windDelta.Y * VolumetricClouds.PatternPeriod);
        resolve.Set(resolve.Uniform("uHistoryWeight"), valid ? 0.85f : 0);
        Draw(resolve, resolveSegment, h.B!);
        (h.A, h.B) = (h.B, h.A);
        (h.Previous, h.Eye, h.Key, h.Light, h.Ambient, h.Forward, h.Wind, h.Coverage, h.Frame) =
            (viewProjection, eye, key, light, ambient, forward, wind, coverage, gpu.Frame.Number);
        timer.End();
        return Sample(h.A!);
    }

    public void RenderShadow(Vector3 eye, float coverage, Vector2 wind, Vector3 sun)
    {
        if (gpu.PassOpen) throw new InvalidOperationException("Prepare cloud shadows before opening the scene pass");
        samples.Clear();
        if (shadowTimer.Poll(samples) is { } ms) ShadowGpuMs = ms;
        shadowTimer.Begin();
        shadowMap ??= Make(512, 512, Vk.Format.R16Sfloat, "cloud shadow map");
        const float texel = VolumetricClouds.ShadowSpan / 512;
        ShadowOrigin = new(MathF.Floor(eye.X / texel) * texel - VolumetricClouds.ShadowSpan * 0.5f,
                           MathF.Floor(eye.Z / texel) * texel - VolumetricClouds.ShadowSpan * 0.5f);
        Common(shadow, coverage, wind, eye);
        shadow.Set(shadow.Uniform("uLight"), sun);
        shadow.Set(shadow.Uniform("uShadowOrigin"), ShadowOrigin.X, ShadowOrigin.Y);
        Draw(shadow, shadowSegment, shadowMap);
        shadowTimer.End();
    }

    public void Dispose()
    {
        main.Dispose(); mirror.Dispose(); shadowMap?.Dispose(); shadowMap = null; noise.Dispose();
        march.Dispose(); resolve.Dispose(); shadow.Dispose();
    }
}