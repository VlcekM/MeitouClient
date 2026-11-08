#if MEITOU_METAL
using System.Diagnostics;
using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Metal;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Rendering.Metal;

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

    readonly MetalContext _ctx;
    readonly PostComposite _composite;
    readonly PostTaa _taa;
    readonly PostOptions _options;
    readonly Func<bool> _noTaa;
    readonly Func<bool> _temporal;
    readonly Func<long, int, int, Vector2> _jitter;
    readonly Stopwatch _adaptationClock = Stopwatch.StartNew();

    GpuTexture? _sceneColour;
    int _displayWidth, _displayHeight;
    long _frame;
    double _lastAdaptation;
    readonly LowTarget[] _lowTargets = [new(2), new(4)];

    sealed class LowTarget(int divisor)
    {
        public readonly int Divisor = divisor;
        /// <summary>The draw tested against each block's nearest depth, and the one tested against its farthest.</summary>
        public GpuTexture? Accum, Depth, AccumFar, DepthFar;
        public PassTargets Targets = null!, TargetsFar = null!;
        public bool Drawn;
    }

    LowTarget LowFor(int divisor) => divisor >= 4 ? _lowTargets[1] : _lowTargets[0];

    public MetalPostChain(MetalContext ctx, PostComposite composite, PostTaa taa, PostOptions options, Func<bool> noTaa, Func<bool> temporal,
        Func<long, int, int, Vector2> jitter)
    {
        this._ctx = ctx;
        this._composite = composite;
        this._taa = taa;
        this._options = options;
        this._noTaa = noTaa;
        this._temporal = temporal;
        this._jitter = jitter;
        CharactersDrawn = true;
    }

    public IGpuContext Context => _ctx;
    public PostOptions Options => _options;
    public ParticleRenderer? Particles { get; set; }
    public GpuTexture? Target { get; set; }
    public PassTargets SceneTargets { get; private set; } = null!;
    public PassTargets SceneTargetsForParticles { get; private set; } = null!;
    public GpuTexture SceneColour => _sceneColour ?? throw new InvalidOperationException("no Metal post frame is open");
    public GpuTexture SceneDepth => _ctx.Depth;

    public int RenderWidth => _sceneColour?.Desc.Width ?? 0;
    public int RenderHeight => _sceneColour?.Desc.Height ?? 0;
    public Vector2 JitterPixels { get; private set; }
    public bool Temporal => _temporal();
    public bool InstantAdaptation { get; set; } = true;
    public int GiResolve { get; set; }
    public (float Min, float Max)? AutoExposure { get; set; }
    public float? WaterHeight { get; set; }
    public bool CharactersDrawn { get; set; }
    public float HeatHazeAmount
    {
        get => _composite.HeatHazeAmount;
        set => _composite.HeatHazeAmount = value;
    }

    public double HeatHazeHours
    {
        get => _composite.HeatHazeHours;
        set => _composite.HeatHazeHours = value;
    }
    public float HeatHazeFarClip
    {
        get => _composite.HeatHazeFarClip;
        set => _composite.HeatHazeFarClip = value;
    }
    public bool LowResParticles => Options.LowResParticles;

    public Action<MotionTargets>? ObjectMotion
    {
        get => _taa.ObjectMotion;
        set => _taa.ObjectMotion = value;
    }

    public void Begin(int width, int height)
    {
        _displayWidth = Math.Max(width, 1);
        _displayHeight = Math.Max(height, 1);
        int sceneWidth = Math.Max(_ctx.SceneWidth, 1), sceneHeight = Math.Max(_ctx.SceneHeight, 1);
        if (_sceneColour is null || _sceneColour.Desc.Width != sceneWidth || _sceneColour.Desc.Height != sceneHeight)
        {
            _sceneColour?.Dispose();
            _sceneColour = _ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, sceneWidth, sceneHeight,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "scene colour"));
        }
        // The scene targets are Metal's own depth and albedo (the GI resolve is off, but the lit programs always write albedo).
        SceneTargets = PassTargets.Of(_sceneColour, _ctx.Depth, _ctx.Albedo);
        // The full-size particles draw after the GI resolve and were built without the albedo attachment on Metal.
        SceneTargetsForParticles = PassTargets.Of(_sceneColour, _ctx.Depth);
        Target = _ctx.Backbuffer;
        foreach (var low in _lowTargets) low.Drawn = false;
        _frame++;
        JitterPixels = _jitter(_frame, sceneWidth, _displayWidth);
        // As the old Metal loop: mip bias only while a temporal reconstruction runs; spatial and native sample at full detail.
        _ctx.LodBias = Temporal ? MathF.Log2(sceneWidth / (float)_displayWidth) - 0.5f : 0f;
        AutoExposure = null;
        WaterHeight = null;
        CharactersDrawn = true;
    }

    public void SetCamera(Vector3 eye, Matrix4x4 view, float fieldOfView, float aspect)
    {
        this._eye = eye;
        this._view = view;
        _fov = fieldOfView;
        this._aspect = aspect;
    }

    /// <summary>The scene's projection; the scene targets (one depth, ctx.Depth) are set by Begin.</summary>
    public void BeginScene(float near, float far, float fieldOfView, float aspect)
    {
        _nearPlane = near;
        _farPlane = far;
        _fov = fieldOfView;
        this._aspect = aspect;
    }

    public PassTargets WithShadingRate(PassTargets targets) => targets;

    public OcclusionView OcclusionFor(Vector3 eye) => default;

    public void RunGiResolve() { }

    public void RunLightShafts(float darkening, float far, float groundY, float airByTime = 1)
    {
        if (_sceneColour is null) return;
        var cmd = (MetalCommandList)_ctx.BeginNative("post shafts");
        _composite.RunLightShafts(cmd, _sceneColour, _ctx.Depth, Inputs, Options, darkening, far, groundY, airByTime, Temporal);
        _ctx.EndNative(cmd);
    }

    public void RunFogVolumes(bool volumes) => _volumesPending = volumes;

    /// <summary>Vulkan's fog shading-rate image has no Metal equivalent: fog, shafts and the scene render at full rate. This is a
    /// performance difference, not a missing visual pass (MEP-1 decided not to expose a no-op FogRate setting on Metal).</summary>
    public void BuildShadingRate(Vector3 eye) { }

    public void ApplySsao()
    {
        if (_sceneColour is null || (!_volumesPending && !Options.Ssao)) return;
        var cmd = (MetalCommandList)_ctx.BeginNative("post scene effects");
        _composite.ApplyScene(cmd, _sceneColour, _ctx.Depth, Inputs, _volumesPending, Options.Ssao);
        _ctx.EndNative(cmd);
        _volumesPending = false;
    }

    public float ParticlePixelsPerUnit(float projectionM22) =>
        LowResParticles ? projectionM22 * (_sceneColour?.Desc.Height ?? 0) * 0.5f : 0f;

    public PassTargets ParticleTargets(int divisor) => LowFor(divisor).Targets;
    public PassTargets? ParticleTargetsFar(int divisor) => LowFor(divisor).TargetsFar;

    public void BeginParticlesLow(int divisor)
    {
        if (_sceneColour is null) return;
        var low = LowFor(divisor);
        int div = low.Divisor;
        int w = Math.Max((_sceneColour.Desc.Width + div - 1) / div, 1);
        int h = Math.Max((_sceneColour.Desc.Height + div - 1) / div, 1);
        if (low.Accum is null || low.Accum.Desc.Width != w || low.Accum.Desc.Height != h)
        {
            low.Accum?.Dispose();
            low.Depth?.Dispose();
            low.AccumFar?.Dispose();
            low.DepthFar?.Dispose();
            low.Accum = _ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, w, h,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: $"post particle accumulation 1/{div}"));
            low.Depth = _ctx.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, w, h,
                Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: $"post particle depth 1/{div}"));
            low.AccumFar = _ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, w, h,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: $"post particle accumulation far 1/{div}"));
            low.DepthFar = _ctx.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, w, h,
                Use: TextureUse.DepthTarget | TextureUse.Sampled, Name: $"post particle depth far 1/{div}"));
        }
        // Twice: each block's nearest depth, then its farthest; the particles are drawn against each (ParticleTargetsFar) and the composite takes
        // each pixel's own surface from either (PostProcessShaders.ParticleUpsample).
        var depthCmd = (MetalCommandList)_ctx.BeginNative("post particle depth");
        _composite.ParticleDepth(depthCmd, _ctx.Depth, low.Depth!, _sceneColour.Desc.Width, _sceneColour.Desc.Height, div, low.Accum, layer: 1, near: _nearPlane, far: _farPlane);
        _composite.ParticleDepth(depthCmd, _ctx.Depth, low.DepthFar!, _sceneColour.Desc.Width, _sceneColour.Desc.Height, div, low.AccumFar, layer: 2, near: _nearPlane, far: _farPlane);
        _ctx.EndNative(depthCmd);
        low.Drawn = false;
        var targets = PassTargets.Of(low.Accum, low.Depth);
        low.Targets = targets with { Colour = targets.Colour with { Load = LoadOp.Clear, Clear = default } };
        var far = PassTargets.Of(low.AccumFar!, low.DepthFar);
        low.TargetsFar = far with { Colour = far.Colour with { Load = LoadOp.Clear, Clear = default } };
    }

    public void CompositeParticlesLow(int divisor)
    {
        if (_sceneColour is null) return;
        var low = LowFor(divisor);
        var cmd = (MetalCommandList)_ctx.BeginNative("post particle composite");
        _composite.ParticleComposite(cmd, low.Accum!, low.Depth!, low.AccumFar!, low.DepthFar!, _ctx.Depth, _sceneColour, low.Divisor, _nearPlane, _farPlane);
        _ctx.EndNative(cmd);
        low.Drawn = true;
    }

    public void MarkParticleDraws() { }
    public void MarkParticles() { }

    public void End()
    {
        if (_sceneColour is null) throw new InvalidOperationException("no Metal post frame is open");
        var target = Target ?? _ctx.Backbuffer;
        GpuTexture resolved = _sceneColour;
        if (!_noTaa())
        {
            _taa.Particles = Particles;
            _taa.ParticleCoverage = ParticleCoverageEnabled ? DrawParticleCoverage : null;
            var taaCmd = (MetalCommandList)_ctx.BeginNative("post taa");
            resolved = _taa.Draw(taaCmd, _sceneColour, _ctx.Depth, _eye, _view, _fov, _aspect, _nearPlane, _farPlane, JitterPixels,
                _displayWidth, _displayHeight, DeltaSeconds(), WaterHeight);
            _ctx.EndNative(taaCmd);
        }
        var cmd = (MetalCommandList)_ctx.BeginNative("post composite");
        _composite.Draw(cmd, resolved, _ctx.Depth, target, Options, AutoExposure ?? (0f, 0f), Inputs, temporal: !_noTaa());
        _ctx.EndNative(cmd);
    }

    /// <summary>
    /// TAA's particle coverage: the full-size quads into the motion alpha, plus the low-resolution accumulations.
    /// <see cref="PostTaa"/> calls this after its velocity and object-motion passes; the target is the motion image's
    /// alpha. MetalFX gets no particle coverage: the reactive-mask history rejection made distant objects shimmer.
    /// </summary>
    void DrawParticleCoverage(IGpuCommandList cmd, GpuTexture target, ColorMask mask, bool clear)
    {
        if (Particles is not { } particles) return;
        var targets = PassTargets.Of(target, _ctx.Depth);
        if (clear) targets = targets with { Colour = targets.Colour with { Load = LoadOp.Clear, Clear = default } };
        cmd.BeginRendering(targets.Rendering);
        _ctx.BeginHostPass((MetalCommandList)cmd, targets,
            DrawState.For(targets.Formats, _ctx.DepthClamp, depthTest: true, depthWrite: false, compare: DepthConvention.SceneCompare, mask: mask));
        particles.DrawCoverage(mask, depthTest: true);
        cmd.EndRendering();
        _ctx.EndHostPass(cmd);
        cmd.Order();
        // The low-resolution accumulations go to TAA's motion alpha. This path is never used for MetalFX, whose
        // reactive mask deliberately has no particle coverage.
        foreach (var low in _lowTargets)
        {
            if (!low.Drawn || low.Accum is null || low.Depth is null || low.AccumFar is null || low.DepthFar is null) continue;
            _composite.ParticleCoverage(cmd, low.Accum, low.Depth, low.AccumFar, low.DepthFar, _ctx.Depth, target, low.Divisor, _nearPlane, _farPlane, mask);
        }
    }

    PostComposite.SceneInputs Inputs => new(_eye, _view, _fov, _aspect, _nearPlane, _farPlane, CharactersDrawn, WaterHeight);

    float DeltaSeconds()
    {
        double now = _adaptationClock.Elapsed.TotalSeconds;
        float dt = (float)(now - _lastAdaptation);
        _lastAdaptation = now;
        return dt;
    }

    public void Dispose()
    {
        _sceneColour?.Dispose();
        foreach (var low in _lowTargets)
        {
            low.Accum?.Dispose();
            low.Depth?.Dispose();
            low.AccumFar?.Dispose();
            low.DepthFar?.Dispose();
        }
    }

    Vector3 _eye;
    Matrix4x4 _view;
    float _fov = 50 * MathF.PI / 180, _aspect = 1, _nearPlane = 1, _farPlane = 1000;
    bool _volumesPending;
}
#endif
