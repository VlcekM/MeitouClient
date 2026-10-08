using System.Numerics;
using System.Text;

using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// The world view's frame buffer and post-processing chain. The scene is drawn into an RGBA16F framebuffer
/// with depth, then: SSAO at half resolution from the depth, a
/// composite pass (exposure, occlusion, clip, dither), then the game's FXAA on the final image when no
/// temporal upscaler runs, then the game's heat haze when the weather has some, ending in <see cref="Target"/>.
/// Usage per frame: <see cref="Begin"/>, draw the scene (calling <see cref="SetNearSlice"/> for the near depth slice), <see cref="End"/>.
/// The scene's hosts draw into <see cref="SceneTargets"/>.
/// Facts about the game's own chain: docs/formats/post-processing.md.
/// </summary>
/// <remarks>
/// Native (docs/renderer-native.md 8, phase 8 stages 2 and 3): every target and texture is a native <see cref="Texture"/> ("post …"), every
/// pass a rendering of its own in a native segment, the timings native timestamps; no GL names (stage 3).
/// </remarks>
public sealed unsafe class PostProcess : IDisposable
{
    /// <summary>A native target with the GL sampler state its GL texture had (linear or nearest, clamped; the luminance's mipmapped
    /// minification).</summary>
    sealed class Target2D(Texture texture, TextureMinFilter min, TextureMagFilter mag)
    {
        public readonly Texture Texture = texture;
        public readonly TextureMinFilter Min = min;
        public readonly TextureMagFilter Mag = mag;
        public int Width => Texture.Desc.Width;
        public int Height => Texture.Desc.Height;
        public Vk.Format Format => Texture.Desc.Format;
        public RenderTarget Attachment => new(Texture.Attachment(), Vk.AttachmentLoadOp.Load, default, Texture.Image);
    }

    /// <summary>The native GPU API (docs/renderer-native.md 7.1 step 8); ports use it instead of looking it up.</summary>
    public GpuContext Gpu { get; }
    public PostOptions Options { get; }
    /// <summary>What the final image goes to: an RGBA8 colour target at least the display size (the window's backbuffer, or an offscreen
    /// texture). Set before <see cref="End"/>.</summary>
    public Texture? Target;
    /// <summary>What the scene's slice is drawn into now (valid after <see cref="Begin"/>): the scene colour and the slice's depth, at the render size.</summary>
    public PassTargets SceneTargets { get; private set; } = null!;
    /// <summary>The scene's depth (the near slice's; valid after <see cref="Begin"/>).</summary>
    public Texture SceneDepth => sceneDepth!.Texture;

    // width × height is the render size (the scene, SSAO); displayWidth × displayHeight the chain after the upscaler (exposure, composite).
    int width, height, displayWidth, displayHeight;
    float allocatedScale;
    UpscalerKind allocatedKind;
    Target2D? sceneColour, sceneDepth;
    // Temporal upscaling: the far slice's own depth (instead of clearing), the motion and depth targets, the display-size history (ping-pong).
    Target2D? farDepth;
    Target2D? motion, upscaleDepth, reactive, historyA, historyB;
    bool historyValid, farSliceDrawn, warnedFallback;
    long frameIndex;
    Target2D? aoA, aoB;
    Target2D? ldr, ldrFxaa;   // the composite's LDR picture that FXAA or the heat haze reads, and FXAA's when the heat haze follows it
    Texture? flowTexture, perturbationTexture;   // the heat haze's FlowHAZE.dds and Perturber.dds (null: not found, no heat haze)
    Target2D? luminance, adaptA, adaptB;   // Kenshi's exposure: the luminance measure (mipmapped) and the adapted value (1 × 1, ping-pong)
    bool adaptedValid;
    readonly System.Diagnostics.Stopwatch adaptClock = new();

    // The native full-screen passes (docs/renderer-native.md 7.1, wave 3 agent E, step P): the same SPIR-V and layout as the GL programs they
    // replaced. Since phase 8 stage 2 each draws in a rendering of its own (8.6). Every handle is resolved once, here.
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
        public readonly SamplerSlot Scene, Ao, Adapted, Mask;
        public readonly UniformHandle Auto, Exposure, UseAo, Dither, Debug, CharacterAo;
        public CompositePass(GpuContext gpu) : base(gpu, PostProcessShaders.Composite, "post composite")
        {
            (Scene, Ao, Adapted, Mask) = (P.Sampler("uScene"), P.Sampler("uAo"), P.Sampler("uAdapted"), P.Sampler("uMask"));
            CharacterAo = P.Uniform("uCharacterAo");
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

    // GPU timestamps (the frame's QueryArena): a set per frame, section k lasting from stamp k-1 to stamp k, read once its frame has been collected.
    const int MaxStamps = 10;
    sealed class StampSet
    {
        public readonly QuerySlot[] Slots = new QuerySlot[MaxStamps];
        public readonly string[] Names = new string[MaxStamps];
        public int Count;
        public long Frame;
    }
    readonly Queue<StampSet> pendingStamps = new();
    readonly Stack<StampSet> freeStamps = new();
    StampSet? stamps;
    Action<CommandList>? stampWriter;
    QuerySlot written;
    readonly Dictionary<string, (double Sum, int Count)> costs = [];

    /// <summary>What every pass draws with: what VkGl made of the GL state the chain set (no culling, depth test or write, bias or blending; all
    /// channels; filled; one sample).</summary>
    static readonly DrawState PassState = new(Vk.CullModeFlags.None, GlConventions.FrontFace(FrontFaceDirection.Ccw), false, false, Vk.CompareOp.LessOrEqual,
        false, 0, 0, BlendState.Off, Vk.ColorComponentFlags.RBit | Vk.ColorComponentFlags.GBit | Vk.ColorComponentFlags.BBit | Vk.ColorComponentFlags.ABit,
        Vk.PolygonMode.Fill, false, false);

    public PostProcess(GpuContext gpu, PostOptions options)
    {
        Gpu = gpu;
        Options = options;
        (ssao, blur, luminancePass, adaptPass) = (new SsaoPass(gpu), new BlurPass(gpu), new LuminancePass(gpu), new AdaptPass(gpu));
        (compositePass, fxaaPass, hazePass) = (new CompositePass(gpu), new FxaaPass(gpu), new HeatHazePass(gpu));
        (velocityPass, taaPass) = (new VelocityPass(gpu), new TaaPass(gpu));
    }

    /// <summary>The near depth slice's planes and the projection: what the depth buffer at the end of the frame holds (the far slice's depth is cleared).</summary>
    public void SetNearSlice(float near, float far, float fieldOfView, float aspectRatio)
    {
        nearPlane = near; farPlane = far; fovY = fieldOfView; aspect = aspectRatio;
        haveNearSlice = true;
    }

    // ---- targets ----

    IEnumerable<Target2D> Targets() =>
        new[] { sceneColour, sceneDepth, farDepth, motion, upscaleDepth, reactive, historyA, historyB, aoA, aoB, ldr, ldrFxaa, luminance, adaptA, adaptB }.OfType<Target2D>();

    void Free()
    {
        foreach (var t in Targets()) t.Texture.Dispose();   // released after the frames in flight
        sceneColour = sceneDepth = farDepth = motion = upscaleDepth = reactive = historyA = historyB = null;
        aoA = aoB = ldr = ldrFxaa = luminance = adaptA = adaptB = null;
        adaptedValid = historyValid = false;
    }

    readonly Dictionary<Vk.Format, bool> storageSupport = [];

    /// <summary>Whether the device can make <paramref name="format"/> a storage image (VkGl gave its float colour textures storage use, which the vendor upscalers write with).</summary>
    bool SupportsStorage(Vk.Format format)
    {
        if (!storageSupport.TryGetValue(format, out bool ok))
        {
            Gpu.Device.Vk.GetPhysicalDeviceFormatProperties(Gpu.Device.PhysicalDevice, format, out var props);
            storageSupport[format] = ok = (props.OptimalTilingFeatures & Vk.FormatFeatureFlags.StorageImageBit) != 0;
        }
        return ok;
    }

    /// <summary>A target of a GL internal format (the format VkGl made of it), with the uses VkGl's image had, one level unless asked.</summary>
    Target2D Make(UploadBatch batch, int w, int h, InternalFormat glFormat, TextureMinFilter min, string name, int levels = 1)
    {
        var format = GlConventions.VkFormat(glFormat);
        var use = TextureUse.Sampled | TextureUse.TransferSrc | TextureUse.TransferDst | (GlConventions.IsDepthFormat(format) ? TextureUse.DepthTarget : TextureUse.ColourTarget);
        if (format is Vk.Format.R16G16B16A16Sfloat or Vk.Format.R32Sfloat or Vk.Format.R16G16Sfloat && SupportsStorage(format)) use |= TextureUse.Storage;
        var texture = batch.Create(new TextureDesc(format, w, h, levels, Use: use, Name: name));
        return new Target2D(texture, min, min == TextureMinFilter.Nearest ? TextureMagFilter.Nearest : TextureMagFilter.Linear);
    }

    void Allocate(int displayW, int displayH)
    {
        Free();
        var up = Options.Upscale;
        (allocatedKind, allocatedScale) = (up.Kind, up.EffectiveScale);
        (displayWidth, displayHeight) = (displayW, displayH);
        var (w, h) = up.RenderSize(displayW, displayH);
        width = w; height = h;

        using (var batch = Gpu.Uploads.Begin())
        {
            // Colour (RGBA16F) and depth (GL's 24 bit, a 32-bit float buffer) the scene is drawn into.
            sceneColour = Make(batch, w, h, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post scene colour");
            sceneDepth = Make(batch, w, h, InternalFormat.DepthComponent24, TextureMinFilter.Nearest, "post scene depth");
            if (up.Temporal)
            {
                farDepth = Make(batch, w, h, InternalFormat.DepthComponent24, TextureMinFilter.Nearest, "post far slice depth");
                motion = Make(batch, w, h, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post motion");
                upscaleDepth = Make(batch, w, h, InternalFormat.R32f, TextureMinFilter.Nearest, "post upscale depth");
                // R32F, not R8: Streamline cannot size an R8_UNORM resource and drops it (DLSS's hint), FSR takes either.
                reactive = Make(batch, w, h, InternalFormat.R32f, TextureMinFilter.Nearest, "post reactive");
                historyA = Make(batch, displayW, displayH, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post taa history");
                historyB = Make(batch, displayW, displayH, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post taa history");
            }
            // FXAA and the heat haze read the composite's LDR picture, as the game's run on its A8R8G8B8 buffers after the HDR composite
            // (both always made, so switching either at run time needs no new targets).
            ldr = Make(batch, displayW, displayH, InternalFormat.Rgba8, TextureMinFilter.Linear, "post ldr");
            ldrFxaa = Make(batch, displayW, displayH, InternalFormat.Rgba8, TextureMinFilter.Linear, "post ldr fxaa");
            int hw = Math.Max((w + 1) / 2, 1), hh = Math.Max((h + 1) / 2, 1);
            aoA = Make(batch, hw, hh, InternalFormat.RG16f, TextureMinFilter.Linear, "post ssao");
            aoB = Make(batch, hw, hh, InternalFormat.RG16f, TextureMinFilter.Linear, "post ssao");
            // Exposure runs after the upscaler, at the display size: the luminance's whole mip chain (GL's, defined by GenerateMipmap), read mipmapped.
            luminance = Make(batch, LuminanceSize, LuminanceSize, InternalFormat.R32f, TextureMinFilter.LinearMipmapNearest, "post luminance", LuminanceLevels);
            adaptA = Make(batch, 1, 1, InternalFormat.RG32f, TextureMinFilter.Linear, "post exposure adapted");
            adaptB = Make(batch, 1, 1, InternalFormat.RG32f, TextureMinFilter.Linear, "post exposure adapted");
        }
    }

    // ---- timing ----

    /// <summary>A timestamp at this point of the frame: into the open segment, or interleaved into the frame (VkGl's pass is not ended).</summary>
    void Stamp(string name)
    {
        var set = stamps;
        if (set is null || set.Count >= MaxStamps) return;
        var arena = Gpu.Frame.Timestamps;
        QuerySlot slot;
        if (segment is { } cmd)
        {
            slot = arena.Allocate();
            cmd.Timestamp(arena, slot);
        }
        else
        {
            stampWriter ??= c => { written = Gpu.Frame.Timestamps.Allocate(); c.Timestamp(Gpu.Frame.Timestamps, written); };
            written = default;
            Gpu.Interleave(stampWriter);
            slot = written;
        }
        set.Slots[set.Count] = slot;
        set.Names[set.Count++] = name;
        if (StageClock.OnClose is not null) StageClock.Sub("post " + name);
    }

    /// <summary>Adds the sections of every frame whose timestamps have been collected (a frame ring after it); drops frames too old to arrive.</summary>
    void Collect()
    {
        var arena = Gpu.Frame.Timestamps;
        while (pendingStamps.TryPeek(out var set))
        {
            bool ready = set.Count > 1;
            for (int i = 0; i < set.Count && ready; i++) ready = arena.TryRead(set.Slots[i], out _);
            if (ready)
            {
                arena.TryRead(set.Slots[0], out ulong previous);
                for (int i = 1; i < set.Count; i++)
                {
                    arena.TryRead(set.Slots[i], out ulong now);
                    string name = set.Names[i];
                    costs.TryGetValue(name, out var c);
                    costs[name] = (c.Sum + (now - previous) / 1e6, c.Count + 1);
                    previous = now;
                }
            }
            else if (Gpu.Frame.Number - set.Frame < 16) break;   // not collected yet
            pendingStamps.Dequeue();
            freeStamps.Push(set);
        }
    }

    /// <summary>Takes in every timing that has arrived. No longer waits (since phase 8 stage 2): a frame's timestamps arrive when its slot of the
    /// frame ring comes round, so the frames still in flight are counted later.</summary>
    public void Flush() => Collect();

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
        Collect();
        stamps = freeStamps.Count > 0 ? freeStamps.Pop() : new StampSet();
        stamps.Count = 0;
        haveNearSlice = farSliceDrawn = false;
        Stamp("start");
        frameIndex++;
        JitterPixels = Temporal ? Jitter.Offset(frameIndex, JitterPhases) : Vector2.Zero;
        // Textures at the display size's detail: log2 of the scale, and further for the vendor upscalers as they recommend (DECISIONS 15).
        Gpu.LodBias = Temporal ? MathF.Log2(width / (float)displayWidth) - (allocatedKind == UpscalerKind.Taa ? 0.5f : 1f) : 0;
        SceneTargets = PassTargets.Of(sceneColour!.Texture, sceneDepth!.Texture);
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

    /// <summary>What <see cref="ObjectMotion"/> gets: the near slice's depth texture (with the sampler the chain reads it with) and planes, and the jitter in NDC.</summary>
    public readonly record struct MotionTargets(SampledTexture NearDepth, Vector2 NearPlanes, Vector2 JitterNdc);

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
        // The clear GL's Clear(DEPTH) made: a rendering of its own clearing the far depth to 1.
        var cmd = Gpu.BeginNative("post far slice clear");
        cmd.BeginRendering(new RenderingDesc(default, farDepth!.Attachment with
        {
            Load = Vk.AttachmentLoadOp.Clear, Clear = new Vk.ClearValue(depthStencil: new Vk.ClearDepthStencilValue(1f, 0)),
        }, width, height));
        cmd.EndRendering();
        Gpu.EndNative(cmd);
        SceneTargets = PassTargets.Of(sceneColour!.Texture, farDepth!.Texture);
        farToPrevious = ToPrevious(near, far);
        farPlanes = new Vector2(near, far);
        farSliceDrawn = true;
    }

    /// <summary>Before drawing the near depth slice (the caller clears the depth after this).</summary>
    public void BeginNearSlice(float near, float far)
    {
        if (!Temporal) return;
        SceneTargets = PassTargets.Of(sceneColour!.Texture, sceneDepth!.Texture);
        nearToPrevious = ToPrevious(near, far);
        nearPlanes = new Vector2(near, far);
    }

    // ---- native passes ----

    CommandList? segment;

    /// <summary>The open native segment of the chain (one is begun when none is open: VkGl's pass ends, a full barrier is placed).</summary>
    CommandList Segment() => segment ??= Gpu.BeginNative("post");

    /// <summary>Ends the open segment (before anything that records through VkGl: the vendor upscalers, the grass-motion guest).</summary>
    void CloseSegment()
    {
        if (segment is null) return;
        Gpu.EndNative(segment);
        segment = null;
    }

    /// <summary>One full-screen triangle with <paramref name="p"/> into <paramref name="target"/> (its uniforms and samplers set before), in a
    /// rendering of its own, followed by a full barrier (VkGl barriered before every pass).</summary>
    readonly Dictionary<(LegacyProgram, Vk.Format), GraphicsPipeline> pipelines = [];

    void Draw(LegacyProgram p, Target2D target) => Draw(p, target.Attachment, target.Format, target.Width, target.Height, target.Width, target.Height);

    void Draw(LegacyProgram p, RenderTarget colour, Vk.Format format, int targetWidth, int targetHeight, int viewportWidth, int viewportHeight)
    {
        var cmd = Segment();
        cmd.BeginRendering(new RenderingDesc(colour, default, targetWidth, targetHeight));
        var s = PassState;
        cmd.SetViewport(new Vk.Viewport(0, 0, viewportWidth, viewportHeight, 0, 1));
        cmd.SetScissor(new Vk.Rect2D(default, new Vk.Extent2D((uint)targetWidth, (uint)targetHeight)));
        cmd.SetRaster(s.Cull, s.Front);
        cmd.SetDepth(s.DepthTest, s.DepthWrite, s.Compare);
        cmd.SetDepthBias(s.BiasEnable, s.BiasConstant, s.BiasSlope);
        if (!pipelines.TryGetValue((p, format), out var pipeline))
            pipelines[(p, format)] = pipeline = Gpu.Pipelines.Get(s.Pipeline(p.Program, p.VertexLayout([]), Vk.PrimitiveTopology.TriangleList,
                new AttachmentFormats(format, Vk.Format.Undefined), p.Name));
        cmd.BindPipeline(pipeline);
        p.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.Barrier(BarrierBatch.Full);
    }

    /// <summary>A target as its GL texture was sampled: its filters, clamped, the upscaler's LOD bias on a mipmapped filter (VkGl's rule,
    /// <see cref="SamplerDesc.FromGl"/>, with the bias as it is now).</summary>
    SampledTexture Sampled(Target2D t) => Sampled(t.Texture, t.Min, t.Mag, TextureWrapMode.ClampToEdge, 1);

    SampledTexture Sampled(Texture t, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap, float anisotropy) =>
        new(Gpu.Samplers.Get(SamplerDesc.FromGl(min, mag, wrap, wrap, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, anisotropy, false, Gpu.LodBias)),
            t.View(), t.Image);

    /// <summary>Binds a target (null: the stand-in VkGl bound for nothing). Call where the GL code bound the unit: the sampler depends on the LOD bias then.</summary>
    void Bind(LegacyProgram p, SamplerSlot slot, Target2D? target)
    {
        if (slot.IsValid) p.Bind(slot, target is null ? Gpu.Dummy(p.SamplerInfo(slot)) : Sampled(target));
    }

    /// <summary>Resolves the scene and runs the chain into <see cref="Target"/>.</summary>
    public void End()
    {
        var o = Options;
        Stamp("scene");
        // The final passes draw into Target (the window's backbuffer, or the caller's texture) with a viewport of the display size.
        var final = PassTargets.Of(Target ?? throw new InvalidOperationException("PostProcess.Target is not set"), null);
        bool needDepth = o.Ssao && haveNearSlice;

        bool ao = needDepth;
        if (ao) RunSsao();
        if (ao) Stamp("ssao");
        bool mask = ao && Options.SsaoCharacterStrength < 1;   // the characters' pixels: the scene colour's alpha (only they write it)
        // The upscaler: the scene at the render size becomes the display-size picture the rest of the chain reads.
        postColour = sceneColour!;
        if (Temporal) { postColour = RunUpscale(); Stamp("upscale"); }
        else ActiveUpscaler = "off";
        bool auto = AutoExposure is not null && luminance is not null;
        if (auto) RunExposure();
        if (auto) Stamp("exposure");

        // Composite: to the target, or to the LDR picture FXAA and / or the heat haze then read (the game's order: FXAA, HeatHaze).
        bool fxaa = o.Fxaa && !Temporal && ldr is not null;
        bool haze = HeatHazeRuns;
        var c = compositePass;
        Bind(c.P, c.Scene, postColour);
        Bind(c.P, c.Ao, aoB);
        Bind(c.P, c.Adapted, auto ? adaptB : null);
        Bind(c.P, c.Mask, mask ? sceneColour : null);
        c.P.Set(c.CharacterAo, mask ? Options.SsaoCharacterStrength : 1f);
        c.P.Set(c.Auto, auto ? 1 : 0);
        c.P.Set(c.Exposure, o.Exposure);
        c.P.Set(c.UseAo, ao ? 1 : 0);
        c.P.Set(c.Dither, o.Dither ? 1 : 0);
        c.P.Set(c.Debug, o.Debug);
        if (fxaa || haze) Draw(c.P, ldr!); else DrawFinal(c.P, final);
        Stamp("composite");
        var picture = ldr;
        if (fxaa)
        {
            var f = fxaaPass;
            Bind(f.P, f.Image, ldr);
            f.P.Set(f.Texel, 1f / displayWidth, 1f / displayHeight);
            if (haze) Draw(f.P, ldrFxaa!); else DrawFinal(f.P, final);
            Stamp("fxaa");
            picture = ldrFxaa;
        }
        if (haze) RunHeatHaze(picture!, final);
        CloseSegment();

        if (stamps is { } set)
        {
            set.Frame = Gpu.Frame.Number;
            pendingStamps.Enqueue(set);
            stamps = null;
        }
        (previousRotation, previousEye, previousFov, previousAspect, previousValid) = (viewRotation, eyeNow, fovNow, aspectNow, Temporal);
    }

    void DrawFinal(LegacyProgram p, PassTargets final) =>
        Draw(p, final.Colour, final.Formats.Colour, final.Width, final.Height, displayWidth, displayHeight);

    Target2D postColour = null!;

    /// <summary>The one projection the upscalers' depth is written for (camera near .. view distance, unjittered).</summary>
    Matrix4x4 FullProjection(float fov, float aspectRatio) => Matrix4x4.CreatePerspectiveFieldOfView(fov, aspectRatio, UpscaleNear, UpscaleFar);

    /// <summary>
    /// The planes of the upscalers' depth: fixed, not the camera's (its near plane follows the eye's height above the ground and the far one the
    /// haze), because FSR decodes last frame's depth with this frame's planes: changing planes read as a disocclusion everywhere.
    /// </summary>
    public const float UpscaleNear = 1, UpscaleFar = 1_000_000;

    /// <summary>Motion vectors, then the vendor upscaler or TAA into the history; returns the display-size picture.</summary>
    Target2D RunUpscale()
    {
        bool reset = !historyValid || !previousValid || Vector3.Distance(eyeNow, previousEye) > 5000;
        float dt = (float)frameClock.Elapsed.TotalSeconds;
        frameClock.Restart();
        Velocity(motion!, 0);
        if (ObjectMotion is { } objectMotion)
        {
            // The guests' host (the grass's motion): the motion target loaded, red and green written, no depth, culling or blending.
            CloseSegment();
            var targets = PassTargets.Of(motion!.Texture, null);
            var cmd = Gpu.BeginNative("object motion");
            cmd.BeginRendering(targets.Rendering);
            Gpu.BeginHostPass(cmd, targets, DrawState.For(targets.Formats, Gpu.Device.DepthClamp,
                mask: Silk.NET.Vulkan.ColorComponentFlags.RBit | Silk.NET.Vulkan.ColorComponentFlags.GBit));
            objectMotion(new MotionTargets(Sampled(sceneDepth!), nearPlanes, new Vector2(2 * JitterPixels.X / width, 2 * JitterPixels.Y / height)));
            cmd.EndRendering();
            Gpu.EndHostPass(cmd);
            Gpu.EndNative(cmd);
        }
        (historyA, historyB) = (historyB, historyA);
        var output = historyB!;
        bool done = false;
        if (ExternalFor(Options.Upscale.Kind) is { } external)
        {
            Velocity(upscaleDepth!, 1);
            if (WaterHeight is not null) Velocity(reactive!, 2);
            bool covered = Particles is { HasCoverage: true };
            if (covered) RunCoverage(reactive!, Vk.ColorComponentFlags.RBit, clear: WaterHeight is null);
            CloseSegment();   // the vendor upscaler records its own segment
            done = external.Dispatch(new UpscaleInputs
            {
                Colour = sceneColour!.Texture, Depth = upscaleDepth!.Texture, Motion = motion!.Texture, Output = output.Texture, Reactive = WaterHeight is null && !covered ? null : reactive!.Texture,
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
            // The particles' coverage into the motion target's alpha (the TAA's reactive channel; the water's analytic value is there already).
            if (Particles is { HasCoverage: true }) RunCoverage(motion!, Vk.ColorComponentFlags.ABit, clear: false);
            var t = taaPass;
            Bind(t.P, t.Colour, sceneColour);
            Bind(t.P, t.Motion, motion);
            Bind(t.P, t.History, historyA);
            t.P.Set(t.RenderSize, (float)width, (float)height);
            t.P.Set(t.DisplaySize, (float)displayWidth, (float)displayHeight);
            t.P.Set(t.Jitter, JitterPixels.X, JitterPixels.Y);
            t.P.Set(t.Blend, 0.1f);
            t.P.Set(t.Reset, reset ? 1 : 0);
            Draw(t.P, output);
            ActiveUpscaler = width == displayWidth && height == displayHeight ? "taa" : $"taa {width}x{height}";
        }
        historyValid = true;
        return output;
    }

    /// <summary>The water plane's height when water is drawn (for the upscalers' reactive mask), else null.</summary>
    public float? WaterHeight { get; set; }
    /// <summary>How much the upscalers trust the current frame on water (FSR's reactive mask; TAA takes half).</summary>
    public const float WaterReactive = 0.5f;

    void Velocity(Target2D target, int mode)
    {
        var v = velocityPass;
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
        Draw(v.P, target);
    }

    /// <summary>The weather's particles, which add their coverage to the upscalers' reactive mask (<see cref="ParticleRenderer.DrawCoverage"/>) so thin fast ones (rain) keep their strength under the temporal history.</summary>
    public ParticleRenderer? Particles { get; set; }

    /// <summary>The particles' coverage into <paramref name="target"/>'s channels <paramref name="mask"/>, added to what is there (or onto zero when <paramref name="clear"/>).</summary>
    void RunCoverage(Target2D target, Vk.ColorComponentFlags mask, bool clear)
    {
        CloseSegment();
        var targets = PassTargets.Of(target.Texture, null);
        if (clear) targets = targets with { Colour = targets.Colour with { Load = Vk.AttachmentLoadOp.Clear, Clear = default } };
        var cmd = Gpu.BeginNative("particle coverage");
        cmd.BeginRendering(targets.Rendering);
        Gpu.BeginHostPass(cmd, targets, DrawState.For(targets.Formats, Gpu.Device.DepthClamp, mask: mask));
        Particles!.DrawCoverage(mask);
        cmd.EndRendering();
        Gpu.EndHostPass(cmd);
        Gpu.EndNative(cmd);
    }

    void RunSsao()
    {
        var a = aoA!; var b = aoB!;
        float tanY = MathF.Tan(fovY * 0.5f);
        var s = ssao;
        Bind(s.P, s.Depth, sceneDepth);
        s.P.Set(s.Tan, tanY * aspect, tanY);
        s.P.Set(s.NearFar, nearPlane, farPlane);
        s.P.Set(s.Size, (float)width, (float)height);
        s.P.Set(s.Radius, Options.SsaoRadius);
        s.P.Set(s.Strength, Options.SsaoStrength);
        // Fade out with distance: the occlusion is a detail effect, and far geometry is hazy and has little depth precision.
        s.P.Set(s.FadeStart, 3000f);
        s.P.Set(s.FadeEnd, 10000f);
        Draw(s.P, a);

        var bl = blur;
        Bind(bl.P, bl.Ao, a);
        bl.P.Set(bl.Step, 1f / a.Width, 0f);
        Draw(bl.P, b);
        Bind(bl.P, bl.Ao, b);
        bl.P.Set(bl.Step, 0f, 1f / a.Height);
        Draw(bl.P, a);
        // The composite reads aoB: swap so the finished result is there.
        (aoA, aoB) = (aoB, aoA);
    }

    // ---- heat haze ----

    /// <summary>The game's shared <c>heatHaze</c> this frame (the caller moves it towards the weather's target, <see cref="Meitou.Data.World.HeatHaze"/>); 0 skips the pass.</summary>
    public float HeatHazeAmount { get; set; }
    /// <summary>The game's <c>gameTime</c>: game hours since the load, which the haze's layers cycle on (× 100).</summary>
    public double HeatHazeHours { get; set; }
    /// <summary>The game's far clip D, which its G-buffer depth is divided by (view distance × 10 = 50000; docs/formats/sky.md).</summary>
    public float HeatHazeFarClip { get; set; } = 10 * Meitou.Data.World.HeatHaze.ViewDistanceSetting;
    /// <summary>Whether the heat-haze textures were found (else the pass never runs).</summary>
    public bool HasHeatHaze => flowTexture is not null && perturbationTexture is not null;
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
            flowTexture = HazeTexture(Meitou.Data.Textures.DdsReader.ReadFile(flow), "post heat haze flow");
            perturbationTexture = HazeTexture(Meitou.Data.Textures.DdsReader.ReadFile(perturbation), "post heat haze perturbation");
        }
        catch (Exception e) when (e is Meitou.Data.Textures.DdsFormatException or IOException)
        {
            Console.WriteLine($"warning   heat haze textures: {e.Message}");
        }
    }

    /// <summary>The file's levels as they are (BC1 blocks), or decoded to RGBA8 for another format; every level the file has.</summary>
    Texture HazeTexture(Meitou.Data.Textures.DdsFile dds, string name)
    {
        bool bc1 = dds.Format == Meitou.Data.Textures.DdsFormat.Bc1;
        var top = dds.Surface(0, 0);
        var format = GlConventions.VkFormat(bc1 ? InternalFormat.CompressedRgbaS3TCDxt1Ext : InternalFormat.Rgba8);
        using var batch = Gpu.Uploads.Begin();
        var texture = batch.Create(new TextureDesc(format, top.Width, top.Height, dds.MipCount, Name: name));
        for (int level = 0; level < dds.MipCount; level++)
        {
            if (bc1)
            {
                var s = dds.Surface(0, level);
                batch.Write(texture, level, 0, new Vk.Rect2D(default, new Vk.Extent2D((uint)s.Width, (uint)s.Height)), dds.Data.AsSpan(s.Offset, s.Length));
            }
            else
            {
                var img = Meitou.Data.Textures.DdsDecoder.Decode(dds, 0, level);
                batch.Write(texture, level, 0, new Vk.Rect2D(default, new Vk.Extent2D((uint)img.Width, (uint)img.Height)), img.Pixels);
            }
        }
        return texture;
    }

    /// <summary>The game's heat haze from <paramref name="source"/> (the finished LDR picture) into <see cref="Target"/>.</summary>
    void RunHeatHaze(Target2D source, PassTargets final)
    {
        // The upscaler's mip bias is for the scene's textures; the haze's maps are sampled as the game does.
        Gpu.LodBias = 0;
        var h = hazePass;
        // GL's state for them: trilinear, anisotropy 16 (Ogre's default in the game), repeating.
        if (h.Flow.IsValid) h.P.Bind(h.Flow, Sampled(flowTexture!, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.Repeat, 16));
        if (h.Perturbation.IsValid) h.P.Bind(h.Perturbation, Sampled(perturbationTexture!, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.Repeat, 16));
        Bind(h.P, h.Depth, sceneDepth);
        Bind(h.P, h.Image, source);
        // The phase in double first: game hours × 100 loses its fraction in float after long sessions.
        double phase = HeatHazeHours * 100;
        h.P.Set(h.Phase, (float)(phase - Math.Floor(phase)));
        h.P.Set(h.Amount, HeatHazeAmount);
        float tanY = MathF.Tan(fovY * 0.5f);
        h.P.Set(h.Tan, tanY * aspect, tanY);
        h.P.Set(h.NearFar, nearPlane, farPlane);
        h.P.Set(h.FarClip, HeatHazeFarClip);
        h.P.Set(h.HasDepth, haveNearSlice ? 1 : 0);
        DrawFinal(h.P, final);
        Stamp("heathaze");
    }

    const int LuminanceSize = 256;
    /// <summary>The luminance's whole mip chain: 256 down to 1.</summary>
    const int LuminanceLevels = 9;
    /// <summary>The game's adaptation rate (`AUTOEXP_ADAPTATION_RATE`, 1 / s).</summary>
    const float AdaptationRate = 0.5f;

    /// <summary>
    /// The game's exposure: when set, the composite scales by 0.55 over the scene's mean luminance, smoothed over time and clamped to the band
    /// (`MIN_LUMINANCE`, `MAX_LUMINANCE`; docs/formats/lighting.md); null keeps the plain <see cref="PostOptions.Exposure"/>, which multiplies either way.
    /// </summary>
    public (float Min, float Max)? AutoExposure { get; set; }
    /// <summary>Skip the smoothing: the exposure settles at once (screenshots, benchmarks).</summary>
    public bool InstantAdaptation { get; set; }

    /// <summary>The last frame's adapted luminance and measured mean (the composite's scale is 0.55 / adapted); waits for the GPU and reads it
    /// back (<see cref="GpuContext.ReadBack"/>), so for reports only, outside a frame.</summary>
    public (float Adapted, float Mean) ReadExposure()
    {
        if (adaptB is null || !adaptedValid) return (float.NaN, float.NaN);
        var v = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(Gpu.ReadBack(adaptB.Texture, 8));   // RG32F
        return (v[0], v[1]);
    }

    void RunExposure()
    {
        var lum = luminance!;
        var lp = luminancePass;
        Bind(lp.P, lp.Scene, postColour);
        lp.P.Set(lp.Cell, 1f / LuminanceSize, 1f / LuminanceSize);
        Draw(lp.P, lum);
        Segment().GenerateMips(lum.Texture);   // VkGl's GenerateMipmap: linear blits level by level, full barriers around
        float dt = (float)adaptClock.Elapsed.TotalSeconds;
        adaptClock.Restart();
        float blend = InstantAdaptation || !adaptedValid ? 1 : 1 - MathF.Exp(-dt * AdaptationRate);
        (adaptA, adaptB) = (adaptB, adaptA);
        var ap = adaptPass;
        Bind(ap.P, ap.Luminance, lum);
        Bind(ap.P, ap.Last, adaptA);
        ap.P.Set(ap.Level, MathF.Log2(LuminanceSize));
        ap.P.Set(ap.Blend, blend);
        var band = AutoExposure!.Value;
        ap.P.Set(ap.Band, band.Min, band.Max);
        Draw(ap.P, adaptB!);
        adaptedValid = true;
    }

    public void Dispose()
    {
        External?.Dispose();
        Free();
        foreach (var p in new FullscreenProgram[] { ssao, blur, luminancePass, adaptPass, compositePass, fxaaPass, hazePass, velocityPass, taaPass }) p.P.Dispose();
        flowTexture?.Dispose();
        perturbationTexture?.Dispose();
    }
}
