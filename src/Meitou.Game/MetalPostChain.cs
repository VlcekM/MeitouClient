#if MEITOU_METAL
using System.Diagnostics;
using System.Numerics;
using Meitou.Metal;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;

namespace Meitou.Game;

/// <summary>
/// Metal's slice of the shared post-chain seam: <see cref="PostComposite"/> (exposure, tone map, grade, fog volumes, SSAO, shafts,
/// FXAA, heat haze) and <see cref="PostTaa"/> (velocity, MetalFX or TAA). <see cref="WorldFrame.Draw"/> drives it like Vulkan's
/// <see cref="PostProcess"/>; the scene colour is the chain's own target, while Metal's frame owns the depth, albedo and backbuffer.
/// </summary>
sealed class MetalPostChain : IWorldPostChain, IDisposable
{
    /// <summary>Diagnostic A/B for TAA's particle coverage: MEITOU_METAL_NO_PARTICLE_COVERAGE=1 leaves the particle
    /// colour draw in place but stops it invalidating TAA's motion alpha. MetalFX never receives particle coverage.</summary>
    static readonly bool ParticleCoverageEnabled =
        Environment.GetEnvironmentVariable("MEITOU_METAL_NO_PARTICLE_COVERAGE") is not ("1" or "true");

    readonly MetalContext ctx;
    readonly PostComposite composite;
    readonly PostTaa taa;
    readonly PostOptions options;
    readonly Func<bool> noTaa;
    readonly Func<bool> temporal;
    readonly Func<long, int, int, Vector2> jitter;
    readonly Stopwatch adaptationClock = Stopwatch.StartNew();

    GpuTexture? sceneColour;
    int displayWidth, displayHeight;
    long frame;
    double lastAdaptation;
    readonly LowTarget[] lowTargets = [new(2), new(4)];

    sealed class LowTarget(int divisor)
    {
        public readonly int Divisor = divisor;
        /// <summary>The draw tested against each block's nearest depth, and the one tested against its farthest.</summary>
        public GpuTexture? Accum, Depth, AccumFar, DepthFar;
        public PassTargets Targets = null!, TargetsFar = null!;
        public bool Drawn;
    }

    LowTarget LowFor(int divisor) => divisor >= 4 ? lowTargets[1] : lowTargets[0];

    public MetalPostChain(MetalContext ctx, PostComposite composite, PostTaa taa, PostOptions options, Func<bool> noTaa, Func<bool> temporal,
        Func<long, int, int, Vector2> jitter)
    {
        this.ctx = ctx;
        this.composite = composite;
        this.taa = taa;
        this.options = options;
        this.noTaa = noTaa;
        this.temporal = temporal;
        this.jitter = jitter;
        CharactersDrawn = true;
    }

    public IGpuContext Context => ctx;
    public PostOptions Options => options;
    public ParticleRenderer? Particles { get; set; }
    public GpuTexture? Target { get; set; }
    public PassTargets SceneTargets { get; private set; } = null!;
    public PassTargets SceneTargetsForParticles { get; private set; } = null!;
    public GpuTexture SceneColour => sceneColour ?? throw new InvalidOperationException("no Metal post frame is open");
    public GpuTexture SceneDepth => ctx.Depth;

    public int RenderWidth => sceneColour?.Desc.Width ?? 0;
    public int RenderHeight => sceneColour?.Desc.Height ?? 0;
    public Vector2 JitterPixels { get; private set; }
    public bool Temporal => temporal();
    public bool DepthSlices => false;
    public bool InstantAdaptation { get; set; } = true;
    public int GiResolve { get; set; }
    public (float Min, float Max)? AutoExposure { get; set; }
    public float? WaterHeight { get; set; }
    public bool CharactersDrawn { get; set; }
    public float HeatHazeAmount
    {
        get => composite.HeatHazeAmount;
        set => composite.HeatHazeAmount = value;
    }

    public double HeatHazeHours
    {
        get => composite.HeatHazeHours;
        set => composite.HeatHazeHours = value;
    }
    public float HeatHazeFarClip
    {
        get => composite.HeatHazeFarClip;
        set => composite.HeatHazeFarClip = value;
    }
    public bool LowResParticles => Options.LowResParticles;

    public Action<MotionTargets>? ObjectMotion
    {
        get => taa.ObjectMotion;
        set => taa.ObjectMotion = value;
    }

    public void Begin(int width, int height)
    {
        displayWidth = Math.Max(width, 1);
        displayHeight = Math.Max(height, 1);
        int sceneWidth = Math.Max(ctx.SceneWidth, 1), sceneHeight = Math.Max(ctx.SceneHeight, 1);
        if (sceneColour is null || sceneColour.Desc.Width != sceneWidth || sceneColour.Desc.Height != sceneHeight)
        {
            sceneColour?.Dispose();
            sceneColour = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, sceneWidth, sceneHeight,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "scene colour"));
        }
        // The scene targets are Metal's own depth and albedo (the GI resolve is off, but the lit programs always write albedo).
        SceneTargets = PassTargets.Of(sceneColour, ctx.Depth, ctx.Albedo);
        // The full-size particles draw after the GI resolve and were built without the albedo attachment on Metal.
        SceneTargetsForParticles = PassTargets.Of(sceneColour, ctx.Depth);
        Target = ctx.Backbuffer;
        foreach (var low in lowTargets) low.Drawn = false;
        frame++;
        JitterPixels = jitter(frame, sceneWidth, displayWidth);
        // As the old Metal loop: mip bias only while a temporal reconstruction runs; spatial and native sample at full detail.
        ctx.LodBias = Temporal ? MathF.Log2(sceneWidth / (float)displayWidth) - 0.5f : 0f;
        AutoExposure = null;
        WaterHeight = null;
        CharactersDrawn = true;
    }

    public void SetCamera(Vector3 eye, Matrix4x4 view, float fieldOfView, float aspect)
    {
        this.eye = eye;
        this.view = view;
        fov = fieldOfView;
        this.aspect = aspect;
    }

    public void SetNearSlice(float near, float far, float fieldOfView, float aspect)
    {
        nearPlane = near;
        farPlane = far;
        fov = fieldOfView;
        this.aspect = aspect;
    }

    public void BeginFarSlice(float near, float far)
    {
        nearPlane = near;
        farPlane = far;
    }

    public void BeginNearSlice(float near, float far)
    {
        nearPlane = near;
        farPlane = far;
    }

    public PassTargets WithShadingRate(PassTargets targets) => targets;

    public OcclusionView OcclusionFor(Vector3 eye) => default;

    public void RunGiResolve() { }

    public void RunLightShafts(float darkening, float far, float groundY, float airByTime = 1)
    {
        if (sceneColour is null) return;
        var cmd = (MetalCommandList)ctx.BeginNative("post shafts");
        composite.RunLightShafts(cmd, sceneColour, ctx.Depth, Inputs, Options, darkening, far, groundY, airByTime, Temporal);
        ctx.EndNative(cmd);
    }

    public void RunFogVolumes(bool volumes) => volumesPending = volumes;

    /// <summary>Vulkan's fog shading-rate image has no Metal equivalent: fog, shafts and the scene render at full rate. This is a
    /// performance difference, not a missing visual pass (MEP-1 decided not to expose a no-op FogRate setting on Metal).</summary>
    public void BuildShadingRate(Vector3 eye) { }

    public void ApplySsao()
    {
        if (sceneColour is null || (!volumesPending && !Options.Ssao)) return;
        var cmd = (MetalCommandList)ctx.BeginNative("post scene effects");
        composite.ApplyScene(cmd, sceneColour, ctx.Depth, Inputs, volumesPending, Options.Ssao);
        ctx.EndNative(cmd);
        volumesPending = false;
    }

    public float ParticlePixelsPerUnit(float projectionM22) =>
        LowResParticles ? projectionM22 * (sceneColour?.Desc.Height ?? 0) * 0.5f : 0f;

    public PassTargets ParticleTargets(int divisor) => LowFor(divisor).Targets;
    public PassTargets? ParticleTargetsFar(int divisor) => LowFor(divisor).TargetsFar;

    public void BeginParticlesLow(int divisor)
    {
        if (sceneColour is null) return;
        var low = LowFor(divisor);
        int div = low.Divisor;
        int w = Math.Max((sceneColour.Desc.Width + div - 1) / div, 1);
        int h = Math.Max((sceneColour.Desc.Height + div - 1) / div, 1);
        if (low.Accum is null || low.Accum.Desc.Width != w || low.Accum.Desc.Height != h)
        {
            low.Accum?.Dispose();
            low.Depth?.Dispose();
            low.AccumFar?.Dispose();
            low.DepthFar?.Dispose();
            low.Accum = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, w, h,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: $"post particle accumulation 1/{div}"));
            low.Depth = ctx.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, w, h,
                Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: $"post particle depth 1/{div}"));
            low.AccumFar = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, w, h,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: $"post particle accumulation far 1/{div}"));
            low.DepthFar = ctx.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, w, h,
                Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: $"post particle depth far 1/{div}"));
        }
        // Twice: each block's nearest depth, then its farthest; the particles are drawn against each (ParticleTargetsFar) and the composite takes
        // each pixel's own surface from either (PostProcessShaders.ParticleUpsample).
        var depthCmd = (MetalCommandList)ctx.BeginNative("post particle depth");
        composite.ParticleDepth(depthCmd, ctx.Depth, low.Depth!, sceneColour.Desc.Width, sceneColour.Desc.Height, div, low.Accum, layer: 1, near: nearPlane, far: farPlane);
        composite.ParticleDepth(depthCmd, ctx.Depth, low.DepthFar!, sceneColour.Desc.Width, sceneColour.Desc.Height, div, low.AccumFar, layer: 2, near: nearPlane, far: farPlane);
        ctx.EndNative(depthCmd);
        low.Drawn = false;
        var targets = PassTargets.Of(low.Accum, low.Depth);
        low.Targets = targets with { Colour = targets.Colour with { Load = LoadOp.Clear, Clear = default } };
        var far = PassTargets.Of(low.AccumFar!, low.DepthFar);
        low.TargetsFar = far with { Colour = far.Colour with { Load = LoadOp.Clear, Clear = default } };
    }

    public void CompositeParticlesLow(int divisor)
    {
        if (sceneColour is null) return;
        var low = LowFor(divisor);
        var cmd = (MetalCommandList)ctx.BeginNative("post particle composite");
        composite.ParticleComposite(cmd, low.Accum!, low.Depth!, low.AccumFar!, low.DepthFar!, ctx.Depth, sceneColour, low.Divisor, nearPlane, farPlane);
        ctx.EndNative(cmd);
        low.Drawn = true;
    }

    public void MarkParticleDraws() { }
    public void MarkParticles() { }

    public void End()
    {
        if (sceneColour is null) throw new InvalidOperationException("no Metal post frame is open");
        var target = Target ?? ctx.Backbuffer;
        GpuTexture resolved = sceneColour;
        if (!noTaa())
        {
            taa.Particles = Particles;
            taa.ParticleCoverage = ParticleCoverageEnabled ? DrawParticleCoverage : null;
            var taaCmd = (MetalCommandList)ctx.BeginNative("post taa");
            resolved = taa.Draw(taaCmd, sceneColour, ctx.Depth, eye, view, fov, aspect, nearPlane, farPlane, JitterPixels,
                displayWidth, displayHeight, DeltaSeconds(), WaterHeight);
            ctx.EndNative(taaCmd);
        }
        var cmd = (MetalCommandList)ctx.BeginNative("post composite");
        composite.Draw(cmd, resolved, ctx.Depth, target, Options, AutoExposure ?? (0f, 0f), Inputs, temporal: !noTaa());
        ctx.EndNative(cmd);
    }

    /// <summary>
    /// TAA's particle coverage: the full-size quads into the motion alpha, plus the low-resolution accumulations.
    /// <see cref="PostTaa"/> calls this after its velocity and object-motion passes; the target is the motion image's
    /// alpha. MetalFX gets no particle coverage: the reactive-mask history rejection made distant objects shimmer.
    /// </summary>
    void DrawParticleCoverage(IGpuCommandList cmd, GpuTexture target, ColorMask mask, bool clear)
    {
        if (Particles is not { } particles) return;
        var targets = PassTargets.Of(target, ctx.Depth);
        if (clear) targets = targets with { Colour = targets.Colour with { Load = LoadOp.Clear, Clear = default } };
        cmd.BeginRendering(targets.Rendering);
        ctx.BeginHostPass((MetalCommandList)cmd, targets,
            DrawState.For(targets.Formats, ctx.DepthClamp, depthTest: true, depthWrite: false, compare: DepthFunction.Lequal, mask: mask));
        particles.DrawCoverage(mask, depthTest: true);
        cmd.EndRendering();
        ctx.EndHostPass(cmd);
        cmd.Order();
        // The low-resolution accumulations go to TAA's motion alpha. This path is never used for MetalFX, whose
        // reactive mask deliberately has no particle coverage.
        foreach (var low in lowTargets)
        {
            if (!low.Drawn || low.Accum is null || low.Depth is null || low.AccumFar is null || low.DepthFar is null) continue;
            composite.ParticleCoverage(cmd, low.Accum, low.Depth, low.AccumFar, low.DepthFar, ctx.Depth, target, low.Divisor, nearPlane, farPlane, mask);
        }
    }

    PostComposite.SceneInputs Inputs => new(eye, view, fov, aspect, nearPlane, farPlane, CharactersDrawn, WaterHeight);

    float DeltaSeconds()
    {
        double now = adaptationClock.Elapsed.TotalSeconds;
        float dt = (float)(now - lastAdaptation);
        lastAdaptation = now;
        return dt;
    }

    public void Dispose()
    {
        sceneColour?.Dispose();
        foreach (var low in lowTargets)
        {
            low.Accum?.Dispose();
            low.Depth?.Dispose();
            low.AccumFar?.Dispose();
            low.DepthFar?.Dispose();
        }
    }

    Vector3 eye;
    Matrix4x4 view;
    float fov = 50 * MathF.PI / 180, aspect = 1, nearPlane = 1, farPlane = 1000;
    bool volumesPending;
}
#endif
