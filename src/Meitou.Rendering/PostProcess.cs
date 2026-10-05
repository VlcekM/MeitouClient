using System.Numerics;
using System.Text;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The world view's frame buffer and post-processing chain. The scene is drawn into an RGBA16F framebuffer
/// with depth, then: SSAO at half resolution from the depth, a
/// composite pass (exposure, occlusion, clip, dither), then the game's FXAA on the final image when no
/// temporal upscaler runs, ending in <see cref="Target"/>.
/// Usage per frame: <see cref="Begin"/>, draw the scene (calling <see cref="SetNearSlice"/> for the near depth slice), <see cref="End"/>.
/// Anything that draws into another framebuffer in between must rebind the one it found (<see cref="SceneFramebuffer"/>).
/// Facts about the game's own chain: docs/formats/post-processing.md.
/// </summary>
public sealed unsafe class PostProcess : IDisposable
{
    sealed class Target2D
    {
        public uint Texture, Framebuffer;
        public int Width, Height;
    }

    readonly IGl gl;
    public PostOptions Options { get; }
    /// <summary>Framebuffer the final image goes to: 0 for the window, or an RGBA8 framebuffer of the same size.</summary>
    public uint Target;
    /// <summary>The framebuffer the scene is drawn into (valid after <see cref="Begin"/>).</summary>
    public uint SceneFramebuffer { get; private set; }

    // width × height is the render size (the scene, SSAO); displayWidth × displayHeight the chain after the upscaler (exposure, composite).
    int width, height, displayWidth, displayHeight;
    float allocatedScale;
    UpscalerKind allocatedKind;
    uint sceneFbo, sceneColour, sceneDepth;
    // Temporal upscaling: the far slice's own depth (instead of clearing), the motion and depth targets, the display-size history (ping-pong).
    uint farFbo, farDepth;
    Target2D? motion, upscaleDepth, reactive, historyA, historyB;
    bool historyValid, farSliceDrawn, warnedFallback;
    long frameIndex;
    readonly uint progVelocity, progTaa;
    Target2D? aoA, aoB;
    Target2D? ldr;   // the composite's LDR picture that FXAA reads (when it runs)
    Target2D? luminance, adaptA, adaptB;   // Kenshi's exposure: the luminance measure (mipmapped) and the adapted value (1 × 1, ping-pong)
    bool adaptedValid;
    readonly System.Diagnostics.Stopwatch adaptClock = new();
    readonly uint vao;
    readonly uint progSsao, progBlur, progComposite, progLuminance, progAdapt, progFxaa;
    readonly Dictionary<(uint, string), int> uniforms = [];

    float nearPlane = 1, farPlane = 1000, fovY = 0.87f, aspect = 1;
    bool haveNearSlice;

    // GPU timestamps: one set per frame in flight, section k lasts from stamp k-1 to stamp k.
    const int Slots = 4, MaxStamps = 8;
    readonly uint[,] stamps = new uint[Slots, MaxStamps];
    readonly string[,] stampNames = new string[Slots, MaxStamps];
    readonly int[] stampCount = new int[Slots];
    readonly bool[] pending = new bool[Slots];
    int slot;
    readonly Dictionary<string, (double Sum, int Count)> costs = [];

    public PostProcess(IGl gl, PostOptions options)
    {
        this.gl = gl;
        Options = options;
        vao = gl.GenVertexArray();
        progSsao = Program(PostProcessShaders.Ssao);
        progBlur = Program(PostProcessShaders.SsaoBlur);
        progComposite = Program(PostProcessShaders.Composite);
        progFxaa = Program(PostProcessShaders.Fxaa);
        progLuminance = Program(PostProcessShaders.Luminance);
        progAdapt = Program(PostProcessShaders.Adapt);
        progVelocity = Program(UpscaleShaders.Velocity);
        progTaa = Program(UpscaleShaders.Taa);
        for (int s = 0; s < Slots; s++)
            for (int i = 0; i < MaxStamps; i++) stamps[s, i] = gl.GenQuery();
    }

    uint Program(string fragment) => WorldGl.Program(gl, PostProcessShaders.Vertex, fragment);

    int U(uint p, string name)
    {
        if (!uniforms.TryGetValue((p, name), out int location)) uniforms[(p, name)] = location = gl.GetUniformLocation(p, name);
        return location;
    }

    /// <summary>The near depth slice's planes and the projection: what the depth buffer at the end of the frame holds (the far slice's depth is cleared).</summary>
    public void SetNearSlice(float near, float far, float fieldOfView, float aspectRatio)
    {
        nearPlane = near; farPlane = far; fovY = fieldOfView; aspect = aspectRatio;
        haveNearSlice = true;
    }

    // ---- targets ----

    void Free()
    {
        if (sceneFbo != 0) { gl.DeleteFramebuffer(sceneFbo); gl.DeleteTexture(sceneColour); gl.DeleteTexture(sceneDepth); sceneFbo = 0; }
        if (farFbo != 0) { gl.DeleteFramebuffer(farFbo); gl.DeleteTexture(farDepth); farFbo = 0; }
        foreach (var t in new[] { aoA, aoB, ldr, luminance, adaptA, adaptB, motion, upscaleDepth, reactive, historyA, historyB }) if (t is not null) Release(t);
        aoA = aoB = ldr = luminance = adaptA = adaptB = motion = upscaleDepth = reactive = historyA = historyB = null;
        adaptedValid = historyValid = false;
    }

    void Release(Target2D t) { gl.DeleteFramebuffer(t.Framebuffer); gl.DeleteTexture(t.Texture); }

    Target2D MakeTarget(int w, int h, InternalFormat format, PixelFormat pf, PixelType type)
    {
        var t = new Target2D { Width = w, Height = h };
        t.Texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, t.Texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)w, (uint)h, 0, pf, type, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        t.Framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, t.Framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, t.Texture, 0);
        return t;
    }

    void Allocate(int displayW, int displayH)
    {
        Free();
        var up = Options.Upscale;
        (allocatedKind, allocatedScale) = (up.Kind, up.EffectiveScale);
        (displayWidth, displayHeight) = (displayW, displayH);
        var (w, h) = up.RenderSize(displayW, displayH);
        width = w; height = h;

        // Colour (RGBA16F) and depth (24 bit) textures the scene is drawn into.
        sceneColour = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, sceneColour);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.HalfFloat, null);
        SamplerState(TextureMinFilter.Linear);
        sceneDepth = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, sceneDepth);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)w, (uint)h, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
        SamplerState(TextureMinFilter.Nearest);
        sceneFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, sceneColour, 0);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, sceneDepth, 0);
        Check("scene");

        if (up.Temporal)
        {
            farDepth = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, farDepth);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)w, (uint)h, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
            SamplerState(TextureMinFilter.Nearest);
            farFbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, farFbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, sceneColour, 0);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, farDepth, 0);
            Check("far slice");
            motion = MakeTarget(w, h, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat);
            Nearest(motion);
            upscaleDepth = MakeTarget(w, h, InternalFormat.R32f, PixelFormat.Red, PixelType.Float);
            Nearest(upscaleDepth);
            // R32F, not R8: Streamline cannot size an R8_UNORM resource and drops it (DLSS's hint), FSR takes either.
            reactive = MakeTarget(w, h, InternalFormat.R32f, PixelFormat.Red, PixelType.Float);
            Nearest(reactive);
            historyA = MakeTarget(displayW, displayH, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat);
            historyB = MakeTarget(displayW, displayH, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat);
        }

        // FXAA reads the composite's LDR picture, as the game's runs on its A8R8G8B8 buffer after the HDR composite.
        if (!up.Temporal) ldr = MakeTarget(displayW, displayH, InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
        int hw = Math.Max((w + 1) / 2, 1), hh = Math.Max((h + 1) / 2, 1);
        aoA = MakeTarget(hw, hh, InternalFormat.RG16f, PixelFormat.RG, PixelType.HalfFloat);
        aoB = MakeTarget(hw, hh, InternalFormat.RG16f, PixelFormat.RG, PixelType.HalfFloat);
        // Exposure runs after the upscaler, at the display size.
        luminance = MakeTarget(LuminanceSize, LuminanceSize, InternalFormat.R32f, PixelFormat.Red, PixelType.Float);
        gl.BindTexture(TextureTarget.Texture2D, luminance.Texture);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapNearest);
        adaptA = MakeTarget(1, 1, InternalFormat.RG32f, PixelFormat.RG, PixelType.Float);
        adaptB = MakeTarget(1, 1, InternalFormat.RG32f, PixelFormat.RG, PixelType.Float);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    void SamplerState(TextureMinFilter filter)
    {
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)filter);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)(filter == TextureMinFilter.Nearest ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    void Nearest(Target2D t)
    {
        gl.BindTexture(TextureTarget.Texture2D, t.Texture);
        SamplerState(TextureMinFilter.Nearest);
    }

    void Check(string what)
    {
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"Post-processing framebuffer '{what}' incomplete: {status}");
    }

    // ---- timing ----

    void Stamp(string name)
    {
        int n = stampCount[slot];
        if (n >= MaxStamps) return;
        gl.QueryCounter(stamps[slot, n], QueryCounterTarget.Timestamp);
        stampNames[slot, n] = name;
        stampCount[slot] = n + 1;
    }

    void Collect(int s, bool wait)
    {
        if (!pending[s]) return;
        int n = stampCount[s];
        if (!wait)
        {
            gl.GetQueryObject(stamps[s, n - 1], QueryObjectParameterName.ResultAvailable, out int ready);
            if (ready == 0) return;
        }
        gl.GetQueryObject(stamps[s, 0], QueryObjectParameterName.Result, out ulong previous);
        for (int i = 1; i < n; i++)
        {
            gl.GetQueryObject(stamps[s, i], QueryObjectParameterName.Result, out ulong now);
            string name = stampNames[s, i]!;
            costs.TryGetValue(name, out var c);
            costs[name] = (c.Sum + (now - previous) / 1e6, c.Count + 1);
            previous = now;
        }
        pending[s] = false;
    }

    /// <summary>Waits for every outstanding timing (after a <c>glFinish</c>); for the offscreen path.</summary>
    public void Flush() { for (int s = 0; s < Slots; s++) Collect(s, true); }

    /// <summary>Average GPU milliseconds per section since the last call ("scene" is the frame before post-processing), then resets.</summary>
    public IReadOnlyList<(string Name, double Ms)> TakeCosts()
    {
        var list = costs.Select(kv => (kv.Key, kv.Value.Sum / kv.Value.Count)).ToList();
        costs.Clear();
        return list;
    }

    public string DescribeCosts()
    {
        var sb = new StringBuilder();
        foreach (var (name, ms) in TakeCosts()) sb.Append(sb.Length > 0 ? ", " : "").Append(name).Append(' ').Append(ms.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // ---- frame ----

    /// <summary>Binds the scene framebuffer (recreating it when the size or the upscaler changed) and clears nothing: the caller does.</summary>
    public void Begin(int w, int h)
    {
        var up = Options.Upscale;
        if (w != displayWidth || h != displayHeight || up.Kind != allocatedKind || up.EffectiveScale != allocatedScale)
            Allocate(w, h);
        slot = (slot + 1) % Slots;
        Collect(slot, false);
        pending[slot] = false;
        stampCount[slot] = 0;
        haveNearSlice = farSliceDrawn = false;
        Stamp("start");
        frameIndex++;
        JitterPixels = Temporal ? Jitter.Offset(frameIndex, JitterPhases) : Vector2.Zero;
        // Textures at the display size's detail: log2 of the scale, and further for the vendor upscalers as they recommend (DECISIONS 15).
        if (gl is ITextureLodBias lod) lod.TextureLodBias = Temporal ? MathF.Log2(width / (float)displayWidth) - (allocatedKind == UpscalerKind.Taa ? 0.5f : 1f) : 0;
        SceneFramebuffer = sceneFbo;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, SceneFramebuffer);
        gl.Viewport(0, 0, (uint)width, (uint)height);
    }

    // ---- temporal upscaling ----

    /// <summary>The size the scene is drawn at this frame (valid after <see cref="Begin"/>): the display size, or less with an upscaler.</summary>
    public int RenderWidth => width;
    public int RenderHeight => height;
    /// <summary>Whether frames are jittered and reconstructed over time (an upscaler is on).</summary>
    public bool Temporal => allocatedKind != UpscalerKind.Off;
    /// <summary>The offset to move this frame's projection by, in render pixels (<see cref="Jitter.Apply"/>); zero without an upscaler.</summary>
    public Vector2 JitterPixels { get; private set; }
    public int JitterPhases => Jitter.PhaseCount(width, displayWidth);
    /// <summary>Frames a still picture needs before the history has converged (screenshots).</summary>
    public int WarmupFrames => Options.Upscale.Temporal ? Math.Max(32, 2 * Jitter.PhaseCount(Options.Upscale.RenderSize(1000, 1000).Width, 1000)) : 0;
    /// <summary>The vendor upscaler for <see cref="UpscalerKind.Fsr"/> / <see cref="UpscalerKind.Dlss"/>; without one (or on failure) TAA runs.</summary>
    public IUpscaler? External { get; set; }

    /// <summary>What <see cref="ObjectMotion"/> gets: the near slice's depth texture and planes, and the jitter in NDC.</summary>
    public readonly record struct MotionTargets(uint NearDepth, Vector2 NearPlanes, Vector2 JitterNdc);

    /// <summary>
    /// Draws motion of moving geometry over the camera motion (the swaying grass), called after the velocity pass with the motion target bound,
    /// only red and green written (B and A, depth and reactivity, stay), no depth test, culling or blending.
    /// </summary>
    public Action<MotionTargets>? ObjectMotion { get; set; }
    /// <summary>Makes the vendor upscaler for <see cref="UpscalerKind.Fsr"/> / <see cref="UpscalerKind.Dlss"/> when it is first asked for (null: not available).</summary>
    public Func<UpscalerKind, IUpscaler?>? UpscalerFactory { get; set; }
    readonly HashSet<UpscalerKind> unavailable = [];

    IUpscaler? ExternalFor(UpscalerKind kind)
    {
        if (kind is not (UpscalerKind.Fsr or UpscalerKind.Dlss)) return null;
        if (External?.Kind == kind) return External;
        if (unavailable.Contains(kind) || UpscalerFactory is null) return null;
        External?.Dispose();
        External = UpscalerFactory(kind);
        if (External is null) unavailable.Add(kind);
        else historyValid = false;
        return External;
    }
    /// <summary>The upscaler that ran last frame (for the overlay).</summary>
    public string ActiveUpscaler { get; private set; } = "off";

    // The camera this frame and last (eye-relative views), and the per-slice reprojection.
    Matrix4x4 viewRotation, previousRotation;
    Vector3 eyeNow, previousEye;
    float fovNow, aspectNow, previousFov, previousAspect;
    bool previousValid;
    Matrix4x4 nearToPrevious, farToPrevious;
    Vector2 nearPlanes, farPlanes;
    readonly System.Diagnostics.Stopwatch frameClock = new();

    /// <summary>The main camera of this frame, before the slices: for the motion vectors (reprojection into the previous frame).</summary>
    public void SetCamera(Vector3 eye, Matrix4x4 view, float fieldOfView, float aspectRatio)
    {
        eyeNow = eye;
        viewRotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        (fovNow, aspectNow) = (fieldOfView, aspectRatio);
    }

    /// <summary>
    /// The matrix from this frame's jittered clip space of a slice to the previous frame's unjittered clip space. Both views are taken
    /// relative to this frame's eye (the previous one shifted by the eye's step), so no 10⁵-unit translations meet in float.
    /// </summary>
    Matrix4x4 ToPrevious(float near, float far)
    {
        if (!previousValid) return Matrix4x4.Identity;
        var projection = Jitter.Apply(Matrix4x4.CreatePerspectiveFieldOfView(fovNow, aspectNow, near, far), JitterPixels, width, height);
        return Reprojection.ClipToPrevious(viewRotation, eyeNow, projection, previousRotation, previousEye, Matrix4x4.CreatePerspectiveFieldOfView(previousFov, previousAspect, near, far));
    }

    /// <summary>Before drawing the far depth slice: with an upscaler it gets its own depth buffer (cleared here) so its depth survives for the motion vectors.</summary>
    public void BeginFarSlice(float near, float far)
    {
        if (!Temporal) return;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, farFbo);
        gl.Clear(ClearBufferMask.DepthBufferBit);
        farToPrevious = ToPrevious(near, far);
        farPlanes = new Vector2(near, far);
        farSliceDrawn = true;
    }

    /// <summary>Before drawing the near depth slice (the caller clears the depth after this).</summary>
    public void BeginNearSlice(float near, float far)
    {
        if (!Temporal) return;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFbo);
        nearToPrevious = ToPrevious(near, far);
        nearPlanes = new Vector2(near, far);
    }

    /// <summary>Resolves the scene and runs the chain into <see cref="Target"/>.</summary>
    public void End()
    {
        var o = Options;
        Stamp("scene");
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.Blend);
        gl.DepthMask(false);
        bool needDepth = o.Ssao && haveNearSlice;
        gl.BindVertexArray(vao);

        bool ao = needDepth;
        if (ao) RunSsao();
        if (ao) Stamp("ssao");
        // The upscaler: the scene at the render size becomes the display-size picture the rest of the chain reads.
        postColour = sceneColour;
        if (Temporal) { postColour = RunUpscale(); Stamp("upscale"); }
        else ActiveUpscaler = "off";
        bool auto = AutoExposure is not null && luminance is not null;
        if (auto) RunExposure();
        if (auto) Stamp("exposure");

        // Composite: to the target, or to the LDR picture FXAA then reads.
        bool fxaa = o.Fxaa && !Temporal && ldr is not null;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fxaa ? ldr!.Framebuffer : Target);
        gl.Viewport(0, 0, (uint)displayWidth, (uint)displayHeight);
        gl.UseProgram(progComposite);
        Bind(0, postColour); gl.Uniform1(U(progComposite, "uScene"), 0);
        Bind(1, aoB?.Texture ?? 0); gl.Uniform1(U(progComposite, "uAo"), 1);
        Bind(3, auto ? adaptB!.Texture : 0); gl.Uniform1(U(progComposite, "uAdapted"), 3);
        gl.Uniform1(U(progComposite, "uAuto"), auto ? 1 : 0);
        gl.Uniform1(U(progComposite, "uExposure"), o.Exposure);
        gl.Uniform1(U(progComposite, "uUseAo"), ao ? 1 : 0);
        gl.Uniform1(U(progComposite, "uDither"), o.Dither ? 1 : 0);
        gl.Uniform1(U(progComposite, "uDebug"), o.Debug);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        Stamp("composite");
        if (fxaa)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, Target);
            gl.UseProgram(progFxaa);
            Bind(0, ldr!.Texture); gl.Uniform1(U(progFxaa, "uImage"), 0);
            gl.Uniform2(U(progFxaa, "uTexel"), 1f / displayWidth, 1f / displayHeight);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Stamp("fxaa");
        }

        pending[slot] = true;
        (previousRotation, previousEye, previousFov, previousAspect, previousValid) = (viewRotation, eyeNow, fovNow, aspectNow, Temporal);

        gl.BindVertexArray(0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
    }

    uint postColour;

    /// <summary>The one projection the upscalers' depth is written for (camera near .. view distance, unjittered).</summary>
    Matrix4x4 FullProjection(float fov, float aspectRatio) => Matrix4x4.CreatePerspectiveFieldOfView(fov, aspectRatio, UpscaleNear, UpscaleFar);

    /// <summary>
    /// The planes of the upscalers' depth: fixed, not the camera's (its near plane follows the eye's height above the ground and the far one the
    /// haze), because FSR decodes last frame's depth with this frame's planes: changing planes read as a disocclusion everywhere.
    /// </summary>
    public const float UpscaleNear = 1, UpscaleFar = 1_000_000;

    /// <summary>Motion vectors, then the vendor upscaler or TAA into the history; returns the display-size picture.</summary>
    uint RunUpscale()
    {
        bool reset = !historyValid || !previousValid || Vector3.Distance(eyeNow, previousEye) > 5000;
        float dt = (float)frameClock.Elapsed.TotalSeconds;
        frameClock.Restart();
        Velocity(motion!, 0);
        if (ObjectMotion is { } objectMotion)
        {
            Pass(motion!);
            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.Blend);
            gl.ColorMask(true, true, false, false);
            objectMotion(new MotionTargets(sceneDepth, nearPlanes, new Vector2(2 * JitterPixels.X / width, 2 * JitterPixels.Y / height)));
            gl.ColorMask(true, true, true, true);
        }
        (historyA, historyB) = (historyB, historyA);
        var output = historyB!;
        bool done = false;
        if (ExternalFor(Options.Upscale.Kind) is { } external)
        {
            Velocity(upscaleDepth!, 1);
            if (WaterHeight is not null) Velocity(reactive!, 2);
            done = external.Dispatch(new UpscaleInputs
            {
                Colour = sceneColour, Depth = upscaleDepth!.Texture, Motion = motion!.Texture, Output = output.Texture, Reactive = WaterHeight is null ? 0 : reactive!.Texture,
                RenderWidth = width, RenderHeight = height, DisplayWidth = displayWidth, DisplayHeight = displayHeight,
                JitterPixels = JitterPixels, Near = UpscaleNear, Far = UpscaleFar, FieldOfView = fovNow,
                DeltaSeconds = Math.Clamp(dt, 0.001f, 0.25f), Sharpness = Options.Upscale.Sharpness, Reset = reset,
                ViewToClip = FullProjection(fovNow, aspectNow), ClipToPreviousClip = previousValid ? Reprojection.ClipToPrevious(viewRotation, eyeNow, FullProjection(fovNow, aspectNow), previousRotation, previousEye, FullProjection(previousFov, previousAspect)) : Matrix4x4.Identity,
                Eye = eyeNow, Right = new Vector3(viewRotation.M11, viewRotation.M21, viewRotation.M31), Up = new Vector3(viewRotation.M12, viewRotation.M22, viewRotation.M32),
                Forward = -new Vector3(viewRotation.M13, viewRotation.M23, viewRotation.M33), Aspect = aspectNow,
            });
            if (done) ActiveUpscaler = external.Name;
            else
            {
                Console.WriteLine($"upscaler  {external.Name} failed; using TAA from now on");
                unavailable.Add(external.Kind);
                external.Dispose();
                External = null;
                warnedFallback = true;
            }
        }
        if (!done)
        {
            if (Options.Upscale.Kind is UpscalerKind.Fsr or UpscalerKind.Dlss && !warnedFallback)
            {
                Console.WriteLine($"upscaler  {Options.Upscale.Kind.ToString().ToUpperInvariant()} is not available here; using TAA");
                warnedFallback = true;
            }
            Pass(output);
            gl.UseProgram(progTaa);
            Bind(0, sceneColour); gl.Uniform1(U(progTaa, "uColour"), 0);
            Bind(1, motion!.Texture); gl.Uniform1(U(progTaa, "uMotion"), 1);
            Bind(2, historyA!.Texture); gl.Uniform1(U(progTaa, "uHistory"), 2);
            gl.Uniform2(U(progTaa, "uRenderSize"), (float)width, (float)height);
            gl.Uniform2(U(progTaa, "uDisplaySize"), (float)displayWidth, (float)displayHeight);
            gl.Uniform2(U(progTaa, "uJitter"), JitterPixels.X, JitterPixels.Y);
            gl.Uniform1(U(progTaa, "uBlend"), 0.1f);
            gl.Uniform1(U(progTaa, "uReset"), reset ? 1 : 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            ActiveUpscaler = width == displayWidth && height == displayHeight ? "taa" : $"taa {width}x{height}";
        }
        historyValid = true;
        return output.Texture;
    }

    /// <summary>The water plane's height when water is drawn (for the upscalers' reactive mask), else null.</summary>
    public float? WaterHeight { get; set; }
    /// <summary>How much the upscalers trust the current frame on water (FSR's reactive mask; TAA takes half).</summary>
    public const float WaterReactive = 0.5f;

    void Velocity(Target2D target, int mode)
    {
        Pass(target);
        gl.UseProgram(progVelocity);
        Bind(0, sceneDepth); gl.Uniform1(U(progVelocity, "uNearDepth"), 0);
        Bind(1, farDepth); gl.Uniform1(U(progVelocity, "uFarDepth"), 1);
        var toNear = nearToPrevious; var toFar = farToPrevious;
        gl.UniformMatrix4(U(progVelocity, "uNearToPrev"), 1, false, (float*)&toNear);
        gl.UniformMatrix4(U(progVelocity, "uFarToPrev"), 1, false, (float*)&toFar);
        gl.Uniform2(U(progVelocity, "uNearPlanes"), nearPlanes.X, nearPlanes.Y);
        gl.Uniform2(U(progVelocity, "uFarPlanes"), farPlanes.X, farPlanes.Y);
        gl.Uniform2(U(progVelocity, "uFullPlanes"), UpscaleNear, UpscaleFar);
        gl.Uniform2(U(progVelocity, "uJitterNdc"), 2 * JitterPixels.X / width, 2 * JitterPixels.Y / height);
        gl.Uniform1(U(progVelocity, "uHasFar"), farSliceDrawn ? 1 : 0);
        gl.Uniform1(U(progVelocity, "uMode"), mode);
        float tanY = MathF.Tan(fovNow * 0.5f);
        gl.Uniform2(U(progVelocity, "uTan"), tanY * aspectNow, tanY);
        var r = viewRotation;
        gl.Uniform3(U(progVelocity, "uRight"), r.M11, r.M21, r.M31);
        gl.Uniform3(U(progVelocity, "uUp"), r.M12, r.M22, r.M32);
        gl.Uniform3(U(progVelocity, "uBack"), r.M13, r.M23, r.M33);
        gl.Uniform1(U(progVelocity, "uEyeY"), eyeNow.Y);
        gl.Uniform1(U(progVelocity, "uWaterY"), WaterHeight ?? float.MinValue);
        gl.Uniform1(U(progVelocity, "uWaterReactive"), WaterReactive);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    void Bind(int unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    void Pass(Target2D target)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, target.Framebuffer);
        gl.Viewport(0, 0, (uint)target.Width, (uint)target.Height);
    }

    void RunSsao()
    {
        var a = aoA!; var b = aoB!;
        float tanY = MathF.Tan(fovY * 0.5f);
        Pass(a);
        gl.UseProgram(progSsao);
        Bind(0, sceneDepth); gl.Uniform1(U(progSsao, "uDepth"), 0);
        gl.Uniform2(U(progSsao, "uTan"), tanY * aspect, tanY);
        gl.Uniform2(U(progSsao, "uNearFar"), nearPlane, farPlane);
        gl.Uniform2(U(progSsao, "uSize"), (float)width, (float)height);
        gl.Uniform1(U(progSsao, "uRadius"), Options.SsaoRadius);
        gl.Uniform1(U(progSsao, "uStrength"), Options.SsaoStrength);
        // Fade out with distance: the occlusion is a detail effect, and far geometry is hazy and has little depth precision.
        gl.Uniform1(U(progSsao, "uFadeStart"), 3000f);
        gl.Uniform1(U(progSsao, "uFadeEnd"), 10000f);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        gl.UseProgram(progBlur);
        Pass(b);
        Bind(0, a.Texture); gl.Uniform1(U(progBlur, "uAo"), 0);
        gl.Uniform2(U(progBlur, "uStep"), 1f / a.Width, 0f);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        Pass(a);
        Bind(0, b.Texture);
        gl.Uniform2(U(progBlur, "uStep"), 0f, 1f / a.Height);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        // The composite reads aoB: swap so the finished result is there.
        (aoA, aoB) = (aoB, aoA);
    }

    const int LuminanceSize = 256;
    /// <summary>The game's adaptation rate (`AUTOEXP_ADAPTATION_RATE`, 1 / s).</summary>
    const float AdaptationRate = 0.5f;

    /// <summary>
    /// The game's exposure: when set, the composite scales by 0.55 over the scene's mean luminance, smoothed over time and clamped to the band
    /// (`MIN_LUMINANCE`, `MAX_LUMINANCE`; docs/formats/lighting.md); null keeps the plain <see cref="PostOptions.Exposure"/>, which multiplies either way.
    /// </summary>
    public (float Min, float Max)? AutoExposure { get; set; }
    /// <summary>Skip the smoothing: the exposure settles at once (screenshots, benchmarks).</summary>
    public bool InstantAdaptation { get; set; }

    /// <summary>The last frame's adapted luminance and measured mean (the composite's scale is 0.55 / adapted); reads the GPU back, so for reports only.</summary>
    public (float Adapted, float Mean) ReadExposure()
    {
        if (adaptB is null || !adaptedValid) return (float.NaN, float.NaN);
        float* v = stackalloc float[4];
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, adaptB.Framebuffer);
        gl.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, v);
        return (v[0], v[1]);
    }

    void RunExposure()
    {
        var lum = luminance!;
        Pass(lum);
        gl.UseProgram(progLuminance);
        Bind(0, postColour); gl.Uniform1(U(progLuminance, "uScene"), 0);
        gl.Uniform2(U(progLuminance, "uCell"), 1f / LuminanceSize, 1f / LuminanceSize);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.BindTexture(TextureTarget.Texture2D, lum.Texture);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        float dt = (float)adaptClock.Elapsed.TotalSeconds;
        adaptClock.Restart();
        float blend = InstantAdaptation || !adaptedValid ? 1 : 1 - MathF.Exp(-dt * AdaptationRate);
        (adaptA, adaptB) = (adaptB, adaptA);
        Pass(adaptB!);
        gl.UseProgram(progAdapt);
        Bind(0, lum.Texture); gl.Uniform1(U(progAdapt, "uLuminance"), 0);
        Bind(1, adaptA!.Texture); gl.Uniform1(U(progAdapt, "uLast"), 1);
        gl.Uniform1(U(progAdapt, "uLevel"), MathF.Log2(LuminanceSize));
        gl.Uniform1(U(progAdapt, "uBlend"), blend);
        var band = AutoExposure!.Value;
        gl.Uniform2(U(progAdapt, "uBand"), band.Min, band.Max);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        adaptedValid = true;
    }

    public void Dispose()
    {
        External?.Dispose();
        Free();
        gl.DeleteVertexArray(vao);
        foreach (var p in new[] { progSsao, progBlur, progComposite, progLuminance, progAdapt, progFxaa, progVelocity, progTaa }) gl.DeleteProgram(p);
        for (int s = 0; s < Slots; s++)
            for (int i = 0; i < MaxStamps; i++) gl.DeleteQuery(stamps[s, i]);
    }
}
