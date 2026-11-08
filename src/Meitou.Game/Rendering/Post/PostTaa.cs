using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The seam's temporal anti-aliasing (the first slice of the post chain's widening): the game's velocity and TAA shaders
/// (<see cref="UpscaleShaders.Velocity"/>, <see cref="UpscaleShaders.Taa"/>). The camera's projection is jittered by
/// <see cref="Jitter"/> (the caller applies the same offset to the scene's projection), the velocity pass writes the reprojection
/// into a render-size RGBA16F motion target, and the TAA pass reconstructs the jittered scene into a display-size history pair.
/// The vendor upscalers and the full <see cref="PostProcess"/> chain stay Vulkan-typed.
/// </summary>
sealed class PostTaa : IDisposable
{
    const float Blend = 0.1f;

    readonly IGpuContext _ctx;
    readonly LegacyProgram _velocity, _taa;
    readonly UniformHandle _nearToPrevious, _nearPlanes, _fullPlanes, _jitterNdc, _mode,
        _flipDepthY, _tan, _right, _up, _back, _eyeY, _waterY, _waterReactive, _sun, _sunMotion;
    readonly SamplerSlot _nearDepth;
    readonly UniformHandle _renderSize, _displaySize, _jitter, _blend, _reset;
    readonly SamplerSlot _colour, _motionSlot, _history;
    readonly Dictionary<(GpuProgram Program, InternalFormat Format), GraphicsPipelineDesc> _pipelines = [];
    readonly SampledTexture _pointSampler, _linearSampler;
    readonly SunMotionTracker _sunTracker = new();

    GpuTexture? _motion, _reactive, _historyA, _historyB, _externalOutput;
    int _width, _height, _displayWidth, _displayHeight;
    bool _historyValid, _previousValid, _externalValid;
    IUpscaler? _external;
    Vector3 _previousEye;
    Matrix4x4 _previousRotation;
    float _previousFov, _previousAspect;

    /// <summary>The frame's particles, whose coverage is added to the upscalers' reactive mask (see <see cref="ParticleCoverage"/>).</summary>
    public ParticleRenderer? Particles { get; set; }

    /// <summary>
    /// Draws the frame's particle coverage into <c>target</c>, whose mask is <c>mask</c>; <c>clear</c> zeroes the
    /// target first (the reactive mask when there is no water value to keep). <see cref="MetalPostChain"/> supplies
    /// this, because it owns the particle renderer and the low-resolution targets. It is used by TAA only: MetalFX
    /// deliberately gets no particle coverage because the reactive history rejection made distant objects shimmer.
    /// </summary>
    public Action<IGpuCommandList, GpuTexture, ColorMask, bool>? ParticleCoverage { get; set; }

    /// <summary>
    /// An upscaler outside the TAA (the Metal host's MetalFX): when set, the velocity pass still runs and the external
    /// scaler receives the scene, depth and motion in <see cref="UpscaleInputs"/>; TAA runs when it is null or fails.
    /// </summary>
    /// <summary>Moving geometry's own motion over the camera's, called after the velocity pass.</summary>
    public Action<MotionTargets>? ObjectMotion { get; set; }

    public IUpscaler? External
    {
        get => _external;
        set
        {
            if (ReferenceEquals(_external, value)) return;
            _external = value;
            _externalValid = false;
            _historyValid = false;
        }
    }

    public PostTaa(IGpuContext ctx)
    {
        this._ctx = ctx;
        // The TAA and velocity passes are engine textures in the engine's own orientation: no flip (the composite flips at the present).
        _velocity = LegacyProgram.Create(ctx, PostProcessShaders.Vertex, UpscaleShaders.Velocity, "post velocity");
        _taa = LegacyProgram.Create(ctx, PostProcessShaders.Vertex, UpscaleShaders.Taa, "post taa");
        var v = _velocity;
        _nearDepth = v.Sampler("uNearDepth");
        _nearToPrevious = v.Uniform("uNearToPrev");
        _nearPlanes = v.Uniform("uNearPlanes");
        _fullPlanes = v.Uniform("uFullPlanes");
        _jitterNdc = v.Uniform("uJitterNdc");
        _mode = v.Uniform("uMode");
        _flipDepthY = v.Uniform("uFlipDepthY");
        _tan = v.Uniform("uTan");
        _right = v.Uniform("uRight");
        _up = v.Uniform("uUp");
        _back = v.Uniform("uBack");
        _eyeY = v.Uniform("uEyeY");
        _waterY = v.Uniform("uWaterY");
        _waterReactive = v.Uniform("uWaterReactive");
        _sun = v.Uniform("uSun");
        _sunMotion = v.Uniform("uSunMotion");
        var t = _taa;
        _colour = t.Sampler("uColour");
        _motionSlot = t.Sampler("uMotion");
        _history = t.Sampler("uHistory");
        _renderSize = t.Uniform("uRenderSize");
        _displaySize = t.Uniform("uDisplaySize");
        _jitter = t.Uniform("uJitter");
        _blend = t.Uniform("uBlend");
        _reset = t.Uniform("uReset");
        _pointSampler = new SampledTexture(Sampled(TextureMinFilter.Nearest, TextureMagFilter.Nearest), default);
        _linearSampler = new SampledTexture(Sampled(TextureMinFilter.Linear, TextureMagFilter.Linear), default);
    }

    GpuSampler Sampled(TextureMinFilter min, TextureMagFilter mag) =>
        _ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat,
            false, DepthFunction.Lequal, false, 1, false, 0));

    /// <summary>The offset frame <paramref name="frame"/> moves the projection by, in render pixels (the phase count covers every
    /// display pixel at the render scale).</summary>
    public static Vector2 Offset(long frame, int renderWidth, int displayWidth) =>
        Jitter.Offset(frame, Jitter.PhaseCount(renderWidth, displayWidth));

    /// <summary>
    /// Resolves the scene into the history: <paramref name="scene"/> and <paramref name="depth"/> are the frame's render-size targets,
    /// <paramref name="jitterPixels"/> is the offset the scene's projection used. Returns the TAA picture (at the scene's size).
    /// </summary>
    public GpuTexture Draw(IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, Vector3 eye, Matrix4x4 view, float fov, float aspect,
        float near, float far, Vector2 jitterPixels, int outputWidth, int outputHeight, float deltaSeconds = 1f / 60f, float? waterHeight = null)
    {
        Ensure(scene.Desc.Width, scene.Desc.Height, outputWidth, outputHeight);
        EnsureExternalOutput();
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        bool cut = _previousValid && Vector3.Distance(eye, _previousEye) > 5000;
        if (cut) _sunTracker.Reset();
        // The scene's projections (DepthConvention: the stored depth is the clip z); the jitter first, the convention last.
        var standard = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, near, far);
        var projection = DepthConvention.Apply(Jitter.Apply(standard, jitterPixels, _width, _height));
        var previousProjection = _previousValid ? DepthConvention.Perspective(_previousFov, _previousAspect, near, far) : projection;
        var toPrevious = _previousValid
            ? Reprojection.ClipToPrevious(rotation, eye, projection, _previousRotation, _previousEye, previousProjection)
            : Matrix4x4.Identity;

        // The motion: the scene's depth carried into the previous frame.
        var v = _velocity;
        v.Bind(_nearDepth, _pointSampler with { View = depth.View() });
        v.Set(_nearToPrevious, toPrevious);
        v.Set(_nearPlanes, near, far);
        v.Set(_fullPlanes, 1f, 1_000_000f);
        v.Set(_jitterNdc, 2 * jitterPixels.X / _width, 2 * jitterPixels.Y / _height);
        v.Set(_mode, 0);
        v.Set(_flipDepthY, _ctx.RenderTargetsBottomUp ? 0 : 1);
        float tanY = MathF.Tan(fov * 0.5f);
        v.Set(_tan, ViewWindow.Tan(tanY * aspect, tanY));
        v.Set(_right, rotation.M11, rotation.M21, rotation.M31);
        v.Set(_up, rotation.M12, rotation.M22, rotation.M32);
        v.Set(_back, ViewWindow.Back(rotation, tanY * aspect, tanY));
        v.Set(_eyeY, eye.Y);
        v.Set(_waterY, waterHeight ?? float.MinValue);
        v.Set(_waterReactive, 0.5f);
        var sunData = _sunTracker.Prepare(_ctx.Globals, true, _previousValid && !cut, rotation, fov, aspect);
        v.Set(_sun, sunData.Sun);
        v.Set(_sunMotion, sunData.Motion);
        Fullscreen(cmd, _velocity, _motion!, _width, _height);
        if (External is not null && _reactive is not null)
        {
            // The reactive pass is a second encoder of the same program: re-bind the depth views, or the new encoder starts with them missing.
            v.Bind(_nearDepth, _pointSampler with { View = depth.View() });
            v.Set(_waterY, waterHeight ?? float.MinValue);
            v.Set(_waterReactive, 0.5f);
            v.Set(_mode, 2);
            Fullscreen(cmd, _velocity, _reactive, _width, _height);
        }
        if (ObjectMotion is { } objectMotion)
        {
            var targets = PassTargets.Of(_motion, null);
            cmd.BeginRendering(targets.Rendering);
            _ctx.BeginHostPass(cmd, targets, DrawState.For(targets.Formats, false, mask: ColorMask.R | ColorMask.G));
            objectMotion(new MotionTargets(new SampledTexture(_pointSampler.Sampler, depth.View()), new Vector2(near, far),
                new Vector2(2 * jitterPixels.X / _width, 2 * jitterPixels.Y / _height), _height / (2 * MathF.Tan(fov * 0.5f))));
            _ctx.EndHostPass(cmd);
            cmd.EndRendering();
            cmd.Order();
        }

        // Weather particles are thin and fast: without their own coverage in the motion alpha the TAA history keeps
        // about 90% of what was behind them and rain/dust almost disappears. The full-size quads write themselves;
        // the low-resolution accumulations are upsampled by PostComposite, as on Vulkan. MetalFX gets no particle
        // coverage: its reactive-mask history rejection made distant objects shimmer around rain/dust.
        bool covered = Particles is { HasCoverage: true } && ParticleCoverage is not null;

        if (External is { } upscaler && _externalOutput is { } externalTarget)
        {
            // MetalFX reads the scene's own depth: its projections follow the scene's convention.
            var fullProjection = DepthConvention.Perspective(fov, aspect, near, far);
            var previousFullProjection = _previousValid
                ? DepthConvention.Perspective(_previousFov, _previousAspect, near, far)
                : fullProjection;
            bool resetExternal = cut || !_externalValid || !_previousValid;
            bool done = upscaler.Dispatch(new UpscaleInputs
            {
                Colour = scene,
                Depth = depth,
                Motion = _motion!,
                Output = externalTarget,
                Reactive = _reactive,
                RenderWidth = _width,
                RenderHeight = _height,
                DisplayWidth = _displayWidth,
                DisplayHeight = _displayHeight,
                JitterPixels = jitterPixels,
                Near = near,
                Far = far,
                FieldOfView = fov,
                DeltaSeconds = Math.Clamp(deltaSeconds, 0.001f, 0.25f),
                Sharpness = 0,
                Reset = resetExternal,
                ViewToClip = fullProjection,
                ClipToPreviousClip = _previousValid
                    ? Reprojection.ClipToPrevious(rotation, eye, fullProjection, _previousRotation, _previousEye, previousFullProjection)
                    : Matrix4x4.Identity,
                Eye = eye,
                Right = new Vector3(rotation.M11, rotation.M21, rotation.M31),
                Up = new Vector3(rotation.M12, rotation.M22, rotation.M32),
                Forward = -new Vector3(rotation.M13, rotation.M23, rotation.M33),
                Aspect = aspect,
            });
            if (done)
            {
                _externalValid = true;
                _historyValid = false;   // returning to TAA starts a fresh history
                _previousEye = eye;
                _previousRotation = rotation;
                _previousFov = fov;
                _previousAspect = aspect;
                _previousValid = true;
                return externalTarget;
            }
            Console.WriteLine($"upscaler  {upscaler.Name} failed; using TAA");
            upscaler.Dispose();
            External = null;
            // The history was skipped while the external upscaler was selected: make it before the TAA path below uses it.
            Ensure(_width, _height, _displayWidth, _displayHeight);
        }

        // The TAA: this frame's scene and motion against the previous history, into the other history target.
        if (covered)
            ParticleCoverage!(cmd, _motion!, ColorMask.A, false);   // the TAA reads the motion target's alpha
        var previous = _historyA!;
        var output = _historyB!;
        var t = _taa;
        t.Bind(_colour, _linearSampler with { View = scene.View() });
        t.Bind(_motionSlot, _pointSampler with { View = _motion!.View() });
        t.Bind(_history, _linearSampler with { View = previous.View() });
        t.Set(_renderSize, (float)_width, (float)_height);
        t.Set(_displaySize, (float)_displayWidth, (float)_displayHeight);
        t.Set(_jitter, jitterPixels.X, jitterPixels.Y);
        t.Set(_blend, Blend);
        t.Set(_reset, cut || !_historyValid || !_previousValid ? 1 : 0);
        Fullscreen(cmd, _taa, output, _displayWidth, _displayHeight);

        _historyA = output;
        _historyB = previous;
        _historyValid = true;
        _previousEye = eye;
        _previousRotation = rotation;
        _previousFov = fov;
        _previousAspect = aspect;
        _previousValid = true;
        return output;
    }

    void Ensure(int w, int h, int dw, int dh)
    {
        // MetalFX owns the temporal history. TAA's two display-size RGBA16F histories are only needed when it falls back.
        bool wantHistory = External is null;
        if (_motion is not null && _width == w && _height == h && _displayWidth == dw && _displayHeight == dh
            && (_historyA is not null) == wantHistory) return;
        DisposeTargets();
        _width = w;
        _height = h;
        _displayWidth = dw;
        _displayHeight = dh;
        _motion = _ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, w, h,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa motion"));
        _reactive = _ctx.CreateTexture(new TextureDesc(InternalFormat.R32F, w, h,
            Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa reactive"));
        if (wantHistory)
        {
            _historyA = _ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, dw, dh,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa history"));
            _historyB = _ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, dw, dh,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post taa history"));
        }
        _historyValid = false;
        _externalValid = false;
        _previousValid = false;
        _sunTracker.Reset();
    }

    void EnsureExternalOutput()
    {
        if (External is null) return;
        if (_externalOutput is not null && _externalOutput.Desc.Width == _displayWidth && _externalOutput.Desc.Height == _displayHeight) return;
        _externalOutput?.Dispose();
        _externalOutput = _ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, _displayWidth, _displayHeight,
            Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.Storage, Name: "post external upscale", Private: true));
        _externalValid = false;
    }

    /// <summary>One fullscreen triangle of <paramref name="program"/> into <paramref name="target"/>.</summary>
    void Fullscreen(IGpuCommandList cmd, LegacyProgram program, GpuTexture target, int w, int h)
    {
        var formats = new AttachmentFormats(target.Desc.Format, InternalFormat.None);
        if (!_pipelines.TryGetValue((program.Program, formats.Colour), out var desc))
        {
            var state = DrawState.For(formats, false);
            _pipelines[(program.Program, formats.Colour)] = desc = state.Pipeline(program.Program, program.VertexLayout([]), PrimitiveType.Triangles,
                formats, program.Name);
        }
        cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), LoadOp.DontCare, default), default, w, h));
        cmd.SetViewport(new PixelViewport(0, 0, w, h, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)w, (uint)h));
        cmd.SetRaster(CullMode.None, FrontFaceDirection.Ccw);
        cmd.SetDepth(false, false, DepthFunction.Always);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(_ctx.Pipelines.Get(desc));
        program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.Order();
    }

    void DisposeTargets()
    {
        _motion?.Dispose();
        _reactive?.Dispose();
        _historyA?.Dispose();
        _historyB?.Dispose();
        _externalOutput?.Dispose();
        _motion = _reactive = _historyA = _historyB = _externalOutput = null;
    }

    public void Dispose()
    {
        _external?.Dispose();
        _external = null;
        DisposeTargets();
        _velocity.Dispose();
        _taa.Dispose();
    }
}