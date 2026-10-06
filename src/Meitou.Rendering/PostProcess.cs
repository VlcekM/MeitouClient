using System.Numerics;
using System.Text;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The world view's frame buffer and post-processing chain. The scene is drawn into an RGBA16F framebuffer
/// with depth, then: SSAO at half resolution from the depth, a
/// composite pass (exposure, occlusion, clip, dither), then the game's FXAA on the final image when no
/// temporal upscaler runs, then the game's heat haze when the weather has some, ending in <see cref="Target"/>.
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
    /// <summary>The native GPU API next to <c>gl</c> (docs/renderer-native.md 7.1 step 8); ports use it instead of looking it up.</summary>
    public GpuContext Gpu { get; }
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
    Target2D? aoA, aoB;
    Target2D? ldr, ldrFxaa;   // the composite's LDR picture that FXAA or the heat haze reads, and FXAA's when the heat haze follows it
    uint flowTexture, perturbationTexture;   // the heat haze's FlowHAZE.dds and Perturber.dds (0: not found, no heat haze)
    Target2D? luminance, adaptA, adaptB;   // Kenshi's exposure: the luminance measure (mipmapped) and the adapted value (1 × 1, ping-pong)
    bool adaptedValid;
    readonly System.Diagnostics.Stopwatch adaptClock = new();

    // Native full-screen passes (docs/renderer-native.md 7.1, wave 3 agent E, step P): the same SPIR-V and layout as the GL programs they replace,
    // one segment per draw in VkGl's pass (the framebuffer and viewport are still GL's, which is what CurrentTargets reads and what the foliage's
    // grass-motion guest needs in the velocity pass). Every handle is resolved once, here.
    readonly IGlInterop interop;
    readonly SsaoPass ssao;
    readonly BlurPass blur;
    readonly LuminancePass luminancePass;
    readonly AdaptPass adaptPass;
    readonly CompositePass compositePass;
    readonly FxaaPass fxaaPass;
    readonly HeatHazePass hazePass;
    readonly VelocityPass velocityPass;
    readonly TaaPass taaPass;

    /// <summary>One native full-screen program: the shared vertex shader with a fragment shader, and its resolved handles.</summary>
    abstract class FullscreenProgram
    {
        public readonly LegacyProgram P;
        protected FullscreenProgram(GpuContext gpu, string fragment, string name) => P = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, fragment, name);
    }

    sealed class BlurPass : FullscreenProgram
    {
        public readonly SamplerSlot Ao;
        public readonly UniformHandle Step;
        public BlurPass(GpuContext gpu) : base(gpu, PostProcessShaders.SsaoBlur, "post ssao blur") => (Ao, Step) = (P.Sampler("uAo"), P.Uniform("uStep"));
    }

    sealed class LuminancePass : FullscreenProgram
    {
        public readonly SamplerSlot Scene;
        public readonly UniformHandle Cell;
        public LuminancePass(GpuContext gpu) : base(gpu, PostProcessShaders.Luminance, "post luminance") => (Scene, Cell) = (P.Sampler("uScene"), P.Uniform("uCell"));
    }

    sealed class AdaptPass : FullscreenProgram
    {
        public readonly SamplerSlot Luminance, Last;
        public readonly UniformHandle Level, Blend, Band;
        public AdaptPass(GpuContext gpu) : base(gpu, PostProcessShaders.Adapt, "post adapt")
        {
            (Luminance, Last) = (P.Sampler("uLuminance"), P.Sampler("uLast"));
            (Level, Blend, Band) = (P.Uniform("uLevel"), P.Uniform("uBlend"), P.Uniform("uBand"));
        }
    }

    sealed class CompositePass : FullscreenProgram
    {
        public readonly SamplerSlot Scene, Ao, Adapted;
        public readonly UniformHandle Auto, Exposure, UseAo, Dither, Debug;
        public CompositePass(GpuContext gpu) : base(gpu, PostProcessShaders.Composite, "post composite")
        {
            (Scene, Ao, Adapted) = (P.Sampler("uScene"), P.Sampler("uAo"), P.Sampler("uAdapted"));
            (Auto, Exposure, UseAo, Dither, Debug) = (P.Uniform("uAuto"), P.Uniform("uExposure"), P.Uniform("uUseAo"), P.Uniform("uDither"), P.Uniform("uDebug"));
        }
    }

    sealed class FxaaPass : FullscreenProgram
    {
        public readonly SamplerSlot Image;
        public readonly UniformHandle Texel;
        public FxaaPass(GpuContext gpu) : base(gpu, PostProcessShaders.Fxaa, "post fxaa") => (Image, Texel) = (P.Sampler("uImage"), P.Uniform("uTexel"));
    }

    sealed class HeatHazePass : FullscreenProgram
    {
        public readonly SamplerSlot Flow, Perturbation, Depth, Image;
        public readonly UniformHandle Phase, Amount, Tan, NearFar, FarClip, HasDepth;
        public HeatHazePass(GpuContext gpu) : base(gpu, PostProcessShaders.HeatHaze, "post heat haze")
        {
            (Flow, Perturbation, Depth, Image) = (P.Sampler("uFlow"), P.Sampler("uPerturbation"), P.Sampler("uDepth"), P.Sampler("uImage"));
            (Phase, Amount, Tan, NearFar, FarClip, HasDepth) = (P.Uniform("uPhase"), P.Uniform("uAmount"), P.Uniform("uTan"), P.Uniform("uNearFar"),
                P.Uniform("uFarClip"), P.Uniform("uHasDepth"));
        }
    }

    sealed class VelocityPass : FullscreenProgram
    {
        public readonly SamplerSlot NearDepth, FarDepth;
        public readonly UniformHandle NearToPrev, FarToPrev, NearPlanes, FarPlanes, FullPlanes, JitterNdc, HasFar, Mode, Tan, Right, Up, Back, EyeY, WaterY, WaterReactive;
        public VelocityPass(GpuContext gpu) : base(gpu, UpscaleShaders.Velocity, "post velocity")
        {
            (NearDepth, FarDepth) = (P.Sampler("uNearDepth"), P.Sampler("uFarDepth"));
            (NearToPrev, FarToPrev, NearPlanes, FarPlanes, FullPlanes) = (P.Uniform("uNearToPrev"), P.Uniform("uFarToPrev"), P.Uniform("uNearPlanes"),
                P.Uniform("uFarPlanes"), P.Uniform("uFullPlanes"));
            (JitterNdc, HasFar, Mode, Tan) = (P.Uniform("uJitterNdc"), P.Uniform("uHasFar"), P.Uniform("uMode"), P.Uniform("uTan"));
            (Right, Up, Back, EyeY, WaterY, WaterReactive) = (P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uEyeY"),
                P.Uniform("uWaterY"), P.Uniform("uWaterReactive"));
        }
    }

    sealed class TaaPass : FullscreenProgram
    {
        public readonly SamplerSlot Colour, Motion, History;
        public readonly UniformHandle RenderSize, DisplaySize, Jitter, Blend, Reset;
        public TaaPass(GpuContext gpu) : base(gpu, UpscaleShaders.Taa, "post taa")
        {
            (Colour, Motion, History) = (P.Sampler("uColour"), P.Sampler("uMotion"), P.Sampler("uHistory"));
            (RenderSize, DisplaySize, Jitter, Blend, Reset) = (P.Uniform("uRenderSize"), P.Uniform("uDisplaySize"), P.Uniform("uJitter"), P.Uniform("uBlend"), P.Uniform("uReset"));
        }
    }

    sealed class SsaoPass : FullscreenProgram
    {
        public readonly SamplerSlot Depth;
        public readonly UniformHandle Tan, NearFar, Size, Radius, Strength, FadeStart, FadeEnd;
        public SsaoPass(GpuContext gpu) : base(gpu, PostProcessShaders.Ssao, "post ssao")
        {
            Depth = P.Sampler("uDepth");
            (Tan, NearFar, Size, Radius, Strength, FadeStart, FadeEnd) = (P.Uniform("uTan"), P.Uniform("uNearFar"), P.Uniform("uSize"),
                P.Uniform("uRadius"), P.Uniform("uStrength"), P.Uniform("uFadeStart"), P.Uniform("uFadeEnd"));
        }
    }

    float nearPlane = 1, farPlane = 1000, fovY = 0.87f, aspect = 1;
    bool haveNearSlice;

    // GPU timestamps: one set per frame in flight, section k lasts from stamp k-1 to stamp k.
    const int Slots = 4, MaxStamps = 10;
    readonly uint[,] stamps = new uint[Slots, MaxStamps];
    readonly string[,] stampNames = new string[Slots, MaxStamps];
    readonly int[] stampCount = new int[Slots];
    readonly bool[] pending = new bool[Slots];
    int slot;
    readonly Dictionary<string, (double Sum, int Count)> costs = [];

    public PostProcess(IGl gl, GpuContext gpu, PostOptions options)
    {
        this.gl = gl;
        Gpu = gpu;
        Options = options;
        interop = gpu.Interop ?? throw new InvalidOperationException("PostProcess needs the native seam (VkGl)");
        (ssao, blur, luminancePass, adaptPass) = (new SsaoPass(gpu), new BlurPass(gpu), new LuminancePass(gpu), new AdaptPass(gpu));
        (compositePass, fxaaPass, hazePass) = (new CompositePass(gpu), new FxaaPass(gpu), new HeatHazePass(gpu));
        (velocityPass, taaPass) = (new VelocityPass(gpu), new TaaPass(gpu));
        for (int s = 0; s < Slots; s++)
            for (int i = 0; i < MaxStamps; i++) stamps[s, i] = gl.GenQuery();
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
        foreach (var t in new[] { aoA, aoB, ldr, ldrFxaa, luminance, adaptA, adaptB, motion, upscaleDepth, reactive, historyA, historyB }) if (t is not null) Release(t);
        aoA = aoB = ldr = ldrFxaa = luminance = adaptA = adaptB = motion = upscaleDepth = reactive = historyA = historyB = null;
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

        // FXAA and the heat haze read the composite's LDR picture, as the game's run on its A8R8G8B8 buffers after the HDR composite
        // (both always made, so switching either at run time needs no new targets).
        ldr = MakeTarget(displayW, displayH, InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
        ldrFxaa = MakeTarget(displayW, displayH, InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
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
        if (StageClock.OnClose is not null) StageClock.Sub("post " + name);
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

        // Composite: to the target, or to the LDR picture FXAA and / or the heat haze then read (the game's order: FXAA, HeatHaze).
        bool fxaa = o.Fxaa && !Temporal && ldr is not null;
        bool haze = HeatHazeRuns;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fxaa || haze ? ldr!.Framebuffer : Target);
        gl.Viewport(0, 0, (uint)displayWidth, (uint)displayHeight);
        var c = compositePass;
        Bind(c.P, c.Scene, postColour);
        Bind(c.P, c.Ao, aoB?.Texture ?? 0);
        Bind(c.P, c.Adapted, auto ? adaptB!.Texture : 0);
        c.P.Set(c.Auto, auto ? 1 : 0);
        c.P.Set(c.Exposure, o.Exposure);
        c.P.Set(c.UseAo, ao ? 1 : 0);
        c.P.Set(c.Dither, o.Dither ? 1 : 0);
        c.P.Set(c.Debug, o.Debug);
        Fullscreen(c.P);
        Stamp("composite");
        var picture = ldr;
        if (fxaa)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, haze ? ldrFxaa!.Framebuffer : Target);
            var f = fxaaPass;
            Bind(f.P, f.Image, ldr!.Texture);
            f.P.Set(f.Texel, 1f / displayWidth, 1f / displayHeight);
            Fullscreen(f.P);
            Stamp("fxaa");
            picture = ldrFxaa;
        }
        if (haze) RunHeatHaze(picture!);

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
            var t = taaPass;
            Bind(t.P, t.Colour, sceneColour);
            Bind(t.P, t.Motion, motion!.Texture);
            Bind(t.P, t.History, historyA!.Texture);
            t.P.Set(t.RenderSize, (float)width, (float)height);
            t.P.Set(t.DisplaySize, (float)displayWidth, (float)displayHeight);
            t.P.Set(t.Jitter, JitterPixels.X, JitterPixels.Y);
            t.P.Set(t.Blend, 0.1f);
            t.P.Set(t.Reset, reset ? 1 : 0);
            Fullscreen(t.P);
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
        var v = velocityPass;
        Pass(target);
        Bind(v.P, v.NearDepth, sceneDepth);
        Bind(v.P, v.FarDepth, farDepth);
        v.P.Set(v.NearToPrev, nearToPrevious);
        v.P.Set(v.FarToPrev, farToPrevious);
        v.P.Set(v.NearPlanes, nearPlanes.X, nearPlanes.Y);
        v.P.Set(v.FarPlanes, farPlanes.X, farPlanes.Y);
        v.P.Set(v.FullPlanes, UpscaleNear, UpscaleFar);
        v.P.Set(v.JitterNdc, 2 * JitterPixels.X / width, 2 * JitterPixels.Y / height);
        v.P.Set(v.HasFar, farSliceDrawn ? 1 : 0);
        v.P.Set(v.Mode, mode);
        float tanY = MathF.Tan(fovNow * 0.5f);
        v.P.Set(v.Tan, tanY * aspectNow, tanY);
        var r = viewRotation;
        v.P.Set(v.Right, r.M11, r.M21, r.M31);
        v.P.Set(v.Up, r.M12, r.M22, r.M32);
        v.P.Set(v.Back, r.M13, r.M23, r.M33);
        v.P.Set(v.EyeY, eyeNow.Y);
        v.P.Set(v.WaterY, WaterHeight ?? float.MinValue);
        v.P.Set(v.WaterReactive, WaterReactive);
        Fullscreen(v.P);
    }

    void Pass(Target2D target)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, target.Framebuffer);
        gl.Viewport(0, 0, (uint)target.Width, (uint)target.Height);
    }

    /// <summary>The texture a sampler reads, as VkGl would bind it now (0: GL's stand-in). Call where the GL code bound the unit: the view and
    /// sampler VkGl hands out depend on the texture's defined levels and on the LOD bias at this moment.</summary>
    void Bind(LegacyProgram p, SamplerSlot slot, uint texture)
    {
        if (slot.IsValid) p.Bind(slot, interop.Sampled(texture, p.SamplerInfo(slot)));
    }

    /// <summary>One full-screen triangle with <paramref name="p"/> into the framebuffer GL has bound, in VkGl's open pass (a segment per draw: the
    /// targets differ from pass to pass). Uniforms and samplers are set before the call; the draw state is GL's, as VkGl would draw with it.</summary>
    void Fullscreen(LegacyProgram p)
    {
        var cmd = interop.BeginNativeInPass(p.Name);
        var t = interop.CurrentTargets();
        var state = interop.CurrentState();
        state.Record(cmd, t);
        cmd.BindPipeline(Gpu.Pipelines.Get(state.Pipeline(p.Program, p.VertexLayout([]), Silk.NET.Vulkan.PrimitiveTopology.TriangleList, t.Formats, p.Name)));
        p.Flush(cmd);
        cmd.Draw(3);
        interop.EndNative(cmd);
    }

    void RunSsao()
    {
        var a = aoA!; var b = aoB!;
        float tanY = MathF.Tan(fovY * 0.5f);
        var s = ssao;
        Pass(a);
        Bind(s.P, s.Depth, sceneDepth);
        s.P.Set(s.Tan, tanY * aspect, tanY);
        s.P.Set(s.NearFar, nearPlane, farPlane);
        s.P.Set(s.Size, (float)width, (float)height);
        s.P.Set(s.Radius, Options.SsaoRadius);
        s.P.Set(s.Strength, Options.SsaoStrength);
        // Fade out with distance: the occlusion is a detail effect, and far geometry is hazy and has little depth precision.
        s.P.Set(s.FadeStart, 3000f);
        s.P.Set(s.FadeEnd, 10000f);
        Fullscreen(s.P);

        var bl = blur;
        Pass(b);
        Bind(bl.P, bl.Ao, a.Texture);
        bl.P.Set(bl.Step, 1f / a.Width, 0f);
        Fullscreen(bl.P);
        Pass(a);
        Bind(bl.P, bl.Ao, b.Texture);
        bl.P.Set(bl.Step, 0f, 1f / a.Height);
        Fullscreen(bl.P);
        // The composite reads aoB: swap so the finished result is there.
        (aoA, aoB) = (aoB, aoA);
    }

    // ---- heat haze ----

    /// <summary>The game's shared <c>heatHaze</c> this frame (the caller moves it towards the weather's target, <see cref="Meitou.Data.World.HeatHaze"/>); 0 skips the pass.</summary>
    public float HeatHazeAmount { get; set; }
    /// <summary>The game's <c>gameTime</c>: game hours since the load, which the haze's layers cycle on (× 100).</summary>
    public double HeatHazeHours { get; set; }
    /// <summary>The game's far clip D, which its G-buffer depth is divided by (view distance × 10 = 50000; docs/formats/sky.md).</summary>
    public float HeatHazeFarClip { get; set; } = 50000;
    /// <summary>Whether the heat-haze textures were found (else the pass never runs).</summary>
    public bool HasHeatHaze => flowTexture != 0 && perturbationTexture != 0;
    /// <summary>Whether this frame ends with the heat haze: it is on, has its textures and an amount.</summary>
    public bool HeatHazeRuns => Options.HeatHaze && HasHeatHaze && HeatHazeAmount > 0 && ldr is not null;

    /// <summary>
    /// Loads the heat haze's two textures from the install (<c>materials/FlowHAZE.dds</c> and <c>Perturber.dds</c>, BC1 2048² with
    /// their 12 mips), sampled as the game's default filtering: trilinear, anisotropy 16, repeating.
    /// </summary>
    public void LoadHeatHaze(AssetLocator assets)
    {
        try
        {
            if (assets.Find("FlowHAZE.dds") is not { } flow || assets.Find("Perturber.dds") is not { } perturbation)
            {
                Console.WriteLine("warning   heat haze: FlowHAZE.dds or Perturber.dds not found, no heat haze");
                return;
            }
            flowTexture = HazeTexture(Meitou.Data.Textures.DdsReader.ReadFile(flow));
            perturbationTexture = HazeTexture(Meitou.Data.Textures.DdsReader.ReadFile(perturbation));
        }
        catch (Exception e) when (e is Meitou.Data.Textures.DdsFormatException or IOException)
        {
            Console.WriteLine($"warning   heat haze textures: {e.Message}");
        }
    }

    uint HazeTexture(Meitou.Data.Textures.DdsFile dds)
    {
        uint t = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, t);
        if (dds.Format == Meitou.Data.Textures.DdsFormat.Bc1)
        {
            for (int level = 0; level < dds.MipCount; level++)
            {
                var s = dds.Surface(0, level);
                fixed (byte* p = &dds.Data[s.Offset])
                    gl.CompressedTexImage2D(TextureTarget.Texture2D, level, InternalFormat.CompressedRgbaS3TCDxt1Ext, (uint)s.Width, (uint)s.Height, 0, (uint)s.Length, p);
            }
        }
        else
        {
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            for (int level = 0; level < dds.MipCount; level++)
            {
                var img = Meitou.Data.Textures.DdsDecoder.Decode(dds, 0, level);
                gl.TexImage2D<byte>(TextureTarget.Texture2D, level, InternalFormat.Rgba8, (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, img.Pixels.AsSpan());
            }
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, dds.MipCount - 1);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, 16f);   // TEXTURE_MAX_ANISOTROPY: Ogre's default 16 in the game
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return t;
    }

    /// <summary>The game's heat haze from <paramref name="source"/> (the finished LDR picture) into <see cref="Target"/>.</summary>
    void RunHeatHaze(Target2D source)
    {
        // The upscaler's mip bias is for the scene's textures; the haze's maps are sampled as the game does.
        if (gl is ITextureLodBias lod) lod.TextureLodBias = 0;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Target);
        gl.Viewport(0, 0, (uint)displayWidth, (uint)displayHeight);
        var h = hazePass;
        Bind(h.P, h.Flow, flowTexture);
        Bind(h.P, h.Perturbation, perturbationTexture);
        Bind(h.P, h.Depth, sceneDepth);
        Bind(h.P, h.Image, source.Texture);
        // The phase in double first: game hours × 100 loses its fraction in float after long sessions.
        double phase = HeatHazeHours * 100;
        h.P.Set(h.Phase, (float)(phase - Math.Floor(phase)));
        h.P.Set(h.Amount, HeatHazeAmount);
        float tanY = MathF.Tan(fovY * 0.5f);
        h.P.Set(h.Tan, tanY * aspect, tanY);
        h.P.Set(h.NearFar, nearPlane, farPlane);
        h.P.Set(h.FarClip, HeatHazeFarClip);
        h.P.Set(h.HasDepth, haveNearSlice ? 1 : 0);
        Fullscreen(h.P);
        Stamp("heathaze");
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
        var lp = luminancePass;
        Bind(lp.P, lp.Scene, postColour);
        lp.P.Set(lp.Cell, 1f / LuminanceSize, 1f / LuminanceSize);
        Fullscreen(lp.P);
        gl.BindTexture(TextureTarget.Texture2D, lum.Texture);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        float dt = (float)adaptClock.Elapsed.TotalSeconds;
        adaptClock.Restart();
        float blend = InstantAdaptation || !adaptedValid ? 1 : 1 - MathF.Exp(-dt * AdaptationRate);
        (adaptA, adaptB) = (adaptB, adaptA);
        Pass(adaptB!);
        var ap = adaptPass;
        Bind(ap.P, ap.Luminance, lum.Texture);
        Bind(ap.P, ap.Last, adaptA!.Texture);
        ap.P.Set(ap.Level, MathF.Log2(LuminanceSize));
        ap.P.Set(ap.Blend, blend);
        var band = AutoExposure!.Value;
        ap.P.Set(ap.Band, band.Min, band.Max);
        Fullscreen(ap.P);
        adaptedValid = true;
    }

    public void Dispose()
    {
        External?.Dispose();
        Free();
        foreach (var p in new FullscreenProgram[] { ssao, blur, luminancePass, adaptPass, compositePass, fxaaPass, hazePass, velocityPass, taaPass }) p.P.Dispose();
        foreach (var t in new[] { flowTexture, perturbationTexture }) if (t != 0) gl.DeleteTexture(t);
        for (int s = 0; s < Slots; s++)
            for (int i = 0; i < MaxStamps; i++) gl.DeleteQuery(stamps[s, i]);
    }
}
