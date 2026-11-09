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
    /// <summary>
    /// Diagnostic A/B for TAA's particle coverage: MEITOU_METAL_NO_PARTICLE_COVERAGE=1 leaves the particle
    /// colour draw in place but stops it invalidating TAA's motion alpha. MetalFX never receives particle coverage.
    /// </summary>
    static readonly bool ParticleCoverageEnabled =
        Environment.GetEnvironmentVariable("MEITOU_METAL_NO_PARTICLE_COVERAGE") is not ("1" or "true");

    /// <summary>
    /// The Metal context the passes record through.
    /// </summary>
    readonly MetalContext _ctx;
    /// <summary>
    /// The shared composite slice: exposure, tone map, grade, fog volumes, SSAO, shafts, FXAA, heat haze and the particle passes.
    /// </summary>
    readonly PostComposite _composite;
    /// <summary>
    /// The shared temporal slice: velocity, then MetalFX or TAA.
    /// </summary>
    readonly PostTaa _taa;
    /// <summary>
    /// The post options the chain reads.
    /// </summary>
    readonly PostOptions _options;
    /// <summary>
    /// Whether TAA (and MetalFX) is off this frame.
    /// </summary>
    readonly Func<bool> _noTaa;
    /// <summary>
    /// Whether a temporal reconstruction runs.
    /// </summary>
    readonly Func<bool> _temporal;
    /// <summary>
    /// The scene projection's jitter in pixels for a frame number, scene width and display width.
    /// </summary>
    readonly Func<long, int, int, Vector2> _jitter;
    /// <summary>
    /// The clock the exposure adaptation's time step is read from.
    /// </summary>
    readonly Stopwatch _adaptationClock = Stopwatch.StartNew();

    /// <summary>
    /// The scene colour target (RGBA16F, the scene size); null before the first frame.
    /// </summary>
    GpuTexture? _sceneColour;
    /// <summary>
    /// The display size of the current frame.
    /// </summary>
    int _displayWidth, _displayHeight;
    /// <summary>
    /// The number of frames begun.
    /// </summary>
    long _frame;
    /// <summary>
    /// The adaptation clock's time at the last <see cref="DeltaSeconds"/>.
    /// </summary>
    double _lastAdaptation;
    /// <summary>
    /// The low-resolution particle targets at 1/2 and 1/4 size.
    /// </summary>
    readonly LowTarget[] _lowTargets = [new(2), new(4)];

    /// <summary>
    /// The main camera's eye.
    /// </summary>
    Vector3 _eye;
    /// <summary>
    /// The main camera's view matrix.
    /// </summary>
    Matrix4x4 _view;
    /// <summary>
    /// The scene's field of view (radians), aspect ratio and near and far planes.
    /// </summary>
    float _fov = 50 * MathF.PI / 180, _aspect = 1, _nearPlane = 1, _farPlane = 1000;
    /// <summary>
    /// Whether <see cref="RunFogVolumes"/> asked for the fog volumes, until <see cref="ApplySsao"/> draws them.
    /// </summary>
    bool _volumesPending;

    /// <summary>
    /// One low-resolution particle size: its accumulation and depth targets, drawn against each block's nearest and farthest depth.
    /// </summary>
    sealed class LowTarget(int divisor)
    {
        /// <summary>
        /// The size divisor (2 or 4).
        /// </summary>
        public readonly int Divisor = divisor;
        /// <summary>
        /// The draw tested against each block's nearest depth, and the one tested against its farthest.
        /// </summary>
        public GpuTexture? Accum, Depth, AccumFar, DepthFar;
        /// <summary>
        /// The pass targets of the near and far draws, cleared at the pass start.
        /// </summary>
        public PassTargets Targets = null!, TargetsFar = null!;
        /// <summary>
        /// Whether the particles were drawn into the targets this frame (only then is their coverage added for TAA).
        /// </summary>
        public bool Drawn;
    }

    /// <summary>
    /// The low-resolution targets for <paramref name="divisor"/>: 1/4 for 4 and over, else 1/2.
    /// </summary>
    LowTarget LowFor(int divisor) => divisor >= 4 ? _lowTargets[1] : _lowTargets[0];

    /// <summary>
    /// A chain over the shared slices; the frame's jitter, TAA and temporal switches come from the callers.
    /// </summary>
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

    /// <summary>
    /// The Metal context.
    /// </summary>
    public IGpuContext Context => _ctx;
    /// <summary>
    /// The post options the chain reads.
    /// </summary>
    public PostOptions Options => _options;
    /// <summary>
    /// The frame's particles (their coverage goes into TAA's motion alpha).
    /// </summary>
    public ParticleRenderer? Particles { get; set; }
    /// <summary>
    /// The finished picture target; the backbuffer unless the host sets another.
    /// </summary>
    public GpuTexture? Target { get; set; }
    /// <summary>
    /// The scene colour with Metal's own depth and albedo targets.
    /// </summary>
    public PassTargets SceneTargets { get; private set; } = null!;
    /// <summary>
    /// The scene colour and depth, without the albedo attachment.
    /// </summary>
    public PassTargets SceneTargetsForParticles { get; private set; } = null!;
    /// <summary>
    /// The scene colour target; throws outside a frame.
    /// </summary>
    public GpuTexture SceneColour => _sceneColour ?? throw new InvalidOperationException("no Metal post frame is open");
    /// <summary>
    /// The context's depth target.
    /// </summary>
    public GpuTexture SceneDepth => _ctx.Depth;

    /// <summary>
    /// The scene colour's width (0 before the first frame).
    /// </summary>
    public int RenderWidth => _sceneColour?.Desc.Width ?? 0;
    /// <summary>
    /// The scene colour's height (0 before the first frame).
    /// </summary>
    public int RenderHeight => _sceneColour?.Desc.Height ?? 0;
    /// <summary>
    /// The scene projection's jitter in pixels this frame.
    /// </summary>
    public Vector2 JitterPixels { get; private set; }
    /// <summary>
    /// Whether a temporal reconstruction runs.
    /// </summary>
    public bool Temporal => _temporal();
    /// <summary>
    /// Still frames snap adaptation and weather timers.
    /// </summary>
    public bool InstantAdaptation { get; set; } = true;
    /// <summary>
    /// The GI resolve divisor; unused (no GI resolve on Metal).
    /// </summary>
    public int GiResolve { get; set; }
    /// <summary>
    /// The sky's exposure band; cleared at each <see cref="Begin"/>.
    /// </summary>
    public (float Min, float Max)? AutoExposure { get; set; }
    /// <summary>
    /// The water plane the fog volumes and SSAO reconstruct against (null: no water); cleared at each <see cref="Begin"/>.
    /// </summary>
    public float? WaterHeight { get; set; }
    /// <summary>
    /// Whether the scene drew characters (the SSAO character mask); true at each <see cref="Begin"/>.
    /// </summary>
    public bool CharactersDrawn { get; set; }
    /// <summary>
    /// The heat haze's amount (the composite's).
    /// </summary>
    public float HeatHazeAmount
    {
        get => _composite.HeatHazeAmount;
        set => _composite.HeatHazeAmount = value;
    }

    /// <summary>
    /// The heat haze's animation hour (the composite's).
    /// </summary>
    public double HeatHazeHours
    {
        get => _composite.HeatHazeHours;
        set => _composite.HeatHazeHours = value;
    }
    /// <summary>
    /// The heat haze's far clip (the composite's).
    /// </summary>
    public float HeatHazeFarClip
    {
        get => _composite.HeatHazeFarClip;
        set => _composite.HeatHazeFarClip = value;
    }
    /// <summary>
    /// Whether big weather particles draw into low-resolution targets (the options' switch).
    /// </summary>
    public bool LowResParticles => Options.LowResParticles;

    /// <summary>
    /// Moving geometry's own motion over the camera's (TAA's hook).
    /// </summary>
    public Action<MotionTargets>? ObjectMotion
    {
        get => _taa.ObjectMotion;
        set => _taa.ObjectMotion = value;
    }

    /// <summary>
    /// Begins a frame: remakes the scene colour at a new scene size, sets the scene targets, the jitter and the mip bias (only while temporal), and
    /// clears the per-frame inputs.
    /// </summary>
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

    /// <summary>
    /// The main camera for the exposure, fog volumes, shafts and reprojection.
    /// </summary>
    public void SetCamera(Vector3 eye, Matrix4x4 view, float fieldOfView, float aspect)
    {
        this._eye = eye;
        this._view = view;
        _fov = fieldOfView;
        this._aspect = aspect;
    }

    /// <summary>
    /// The scene's projection; the scene targets (one depth, ctx.Depth) are set by Begin.
    /// </summary>
    public void BeginScene(float near, float far, float fieldOfView, float aspect)
    {
        _nearPlane = near;
        _farPlane = far;
        _fov = fieldOfView;
        this._aspect = aspect;
    }

    /// <summary>
    /// The targets unchanged: Metal has no fog shading rate.
    /// </summary>
    public PassTargets WithShadingRate(PassTargets targets) => targets;

    /// <summary>
    /// None: Metal has no depth pyramid for the foliage cull.
    /// </summary>
    public OcclusionView OcclusionFor(Vector3 eye) => default;

    /// <summary>
    /// Nothing: Metal has no GI resolve.
    /// </summary>
    public void RunGiResolve() { }

    /// <summary>
    /// Darkens the haze where the sun is shadowed along the view (the composite's light shafts).
    /// </summary>
    public void RunLightShafts(float darkening, float far, float groundY, float airByTime = 1)
    {
        if (_sceneColour is null) return;
        var cmd = (MetalCommandList)_ctx.BeginNative("post shafts");
        _composite.RunLightShafts(cmd, _sceneColour, _ctx.Depth, Inputs, Options, darkening, far, groundY, airByTime, Temporal);
        _ctx.EndNative(cmd);
    }

    /// <summary>
    /// Marks the fog volumes for <see cref="ApplySsao"/>.
    /// </summary>
    public void RunFogVolumes(bool volumes) => _volumesPending = volumes;

    /// <summary>
    /// Vulkan's fog shading-rate image has no Metal equivalent: fog, shafts and the scene render at full rate. This is a
    /// performance difference, not a missing visual pass (MEP-1 decided not to expose a no-op FogRate setting on Metal).
    /// </summary>
    public void BuildShadingRate(Vector3 eye) { }

    /// <summary>
    /// Applies the pending fog volumes and SSAO to the scene colour, when either is on.
    /// </summary>
    public void ApplySsao()
    {
        if (_sceneColour is null || (!_volumesPending && !Options.Ssao)) return;
        var cmd = (MetalCommandList)_ctx.BeginNative("post scene effects");
        _composite.ApplyScene(cmd, _sceneColour, _ctx.Depth, Inputs, _volumesPending, Options.Ssao);
        _ctx.EndNative(cmd);
        _volumesPending = false;
    }

    /// <summary>
    /// The render size in pixels of one world unit at distance 1 while the low-resolution particles are on; else 0.
    /// </summary>
    public float ParticlePixelsPerUnit(float projectionM22) =>
        LowResParticles ? projectionM22 * (_sceneColour?.Desc.Height ?? 0) * 0.5f : 0f;

    /// <summary>
    /// The low-resolution pass's near-depth targets for <paramref name="divisor"/>.
    /// </summary>
    public PassTargets ParticleTargets(int divisor) => LowFor(divisor).Targets;
    /// <summary>
    /// The low-resolution pass's far-depth targets for <paramref name="divisor"/>.
    /// </summary>
    public PassTargets? ParticleTargetsFar(int divisor) => LowFor(divisor).TargetsFar;

    /// <summary>
    /// Makes the low-resolution targets at the size for <paramref name="divisor"/>, writes each block's nearest and farthest scene depth into them,
    /// and sets their cleared pass targets.
    /// </summary>
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
        _composite.ParticleDepth(depthCmd, _ctx.Depth, low.Depth!, _sceneColour.Desc.Width, _sceneColour.Desc.Height, div, low.Accum, layer: 1,
            near: _nearPlane, far: _farPlane);
        _composite.ParticleDepth(depthCmd, _ctx.Depth, low.DepthFar!, _sceneColour.Desc.Width, _sceneColour.Desc.Height, div, low.AccumFar, layer: 2,
            near: _nearPlane, far: _farPlane);
        _ctx.EndNative(depthCmd);
        low.Drawn = false;
        var targets = PassTargets.Of(low.Accum, low.Depth);
        low.Targets = targets with { Colour = targets.Colour with { Load = LoadOp.Clear, Clear = default } };
        var far = PassTargets.Of(low.AccumFar!, low.DepthFar);
        low.TargetsFar = far with { Colour = far.Colour with { Load = LoadOp.Clear, Clear = default } };
    }

    /// <summary>
    /// Composites the low-resolution particles into the scene colour.
    /// </summary>
    public void CompositeParticlesLow(int divisor)
    {
        if (_sceneColour is null) return;
        var low = LowFor(divisor);
        var cmd = (MetalCommandList)_ctx.BeginNative("post particle composite");
        _composite.ParticleComposite(cmd, low.Accum!, low.Depth!, low.AccumFar!, low.DepthFar!, _ctx.Depth, _sceneColour, low.Divisor, _nearPlane,
            _farPlane);
        _ctx.EndNative(cmd);
        low.Drawn = true;
    }

    /// <summary>
    /// Nothing on Metal.
    /// </summary>
    public void MarkParticleDraws() { }
    /// <summary>
    /// Nothing on Metal.
    /// </summary>
    public void MarkParticles() { }

    /// <summary>
    /// Runs TAA (unless it is off), then the composite into <see cref="Target"/>.
    /// </summary>
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
            _composite.ParticleCoverage(cmd, low.Accum, low.Depth, low.AccumFar, low.DepthFar, _ctx.Depth, target, low.Divisor, _nearPlane, _farPlane,
                mask);
        }
    }

    /// <summary>
    /// The scene inputs the composite reads: the camera, projection, characters and water.
    /// </summary>
    PostComposite.SceneInputs Inputs => new(_eye, _view, _fov, _aspect, _nearPlane, _farPlane, CharactersDrawn, WaterHeight);

    /// <summary>
    /// The seconds since the last call, for the exposure adaptation.
    /// </summary>
    float DeltaSeconds()
    {
        double now = _adaptationClock.Elapsed.TotalSeconds;
        float dt = (float)(now - _lastAdaptation);
        _lastAdaptation = now;
        return dt;
    }

    /// <summary>
    /// Releases the scene colour and the low-resolution targets.
    /// </summary>
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
}
#endif
