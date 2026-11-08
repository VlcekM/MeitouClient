using System.Numerics;
using System.Text;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Sky;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The world view's frame buffer and post-processing chain. The scene is drawn into an RGBA16F framebuffer
/// with depth, then: SSAO at half resolution from the depth, a
/// composite pass (exposure, occlusion, clip, dither), then the game's FXAA on the final image when no
/// temporal upscaler runs, then the game's heat haze when the weather has some, ending in <see cref="Target"/>.
/// Usage per frame: <see cref="Begin"/>, draw the scene (calling <see cref="BeginScene"/> with its projection), <see cref="End"/>.
/// The scene depth is one reversed-Z buffer (<see cref="DepthConvention"/>).
/// The scene's hosts draw into <see cref="SceneTargets"/>.
/// Facts about the game's own chain: docs/formats/post-processing.md.
/// </summary>
/// <remarks>
/// Native (docs/renderer-native.md 8, phase 8 stages 2 and 3): every target and texture is a native <see cref="Texture"/> ("post …"), every
/// pass a rendering of its own in a native segment, the timings native timestamps; no GL names (stage 3).
/// </remarks>
public sealed unsafe partial class PostProcess : IDisposable, IWorldPostChain
{
    /// <summary>A native target with the GL sampler state its GL texture had (linear or nearest, clamped; the luminance's mipmapped
    /// minification).</summary>
    sealed class Target2D(GpuTexture texture, TextureMinFilter min, TextureMagFilter mag)
    {
        public readonly GpuTexture Texture = texture;
        public readonly TextureMinFilter Min = min;
        public readonly TextureMagFilter Mag = mag;
        public int Width => Texture.Desc.Width;
        public int Height => Texture.Desc.Height;
        public InternalFormat Format => Texture.Desc.Format;
        public RenderTarget Attachment => new(Texture.Attachment(), LoadOp.Load, default);
    }

    /// <summary>The native GPU API (docs/renderer-native.md 7.1 step 8); ports use it instead of looking it up.</summary>
    public GpuContext Gpu { get; }
    IGpuContext IWorldPostChain.Context => Gpu;
    public PostOptions Options { get; }
    /// <summary>What the final image goes to: an RGBA8 colour target at least the display size (the window's backbuffer, or an offscreen
    /// texture). Set before <see cref="End"/>.</summary>
    public GpuTexture? Target { get; set; }
    /// <summary>What the scene is drawn into (valid after <see cref="Begin"/>): the scene colour and depth, at the render size.</summary>
    public PassTargets SceneTargets { get; private set; } = null!;
    PassTargets IWorldPostChain.SceneTargetsForParticles => SceneTargets;
    /// <summary>The scene's depth (reversed-Z, <see cref="DepthConvention"/>; valid after <see cref="Begin"/>).</summary>
    public GpuTexture SceneDepth => _sceneDepth!.Texture;
    /// <summary>The scene's colour at the render size (valid after <see cref="Begin"/>; the Meitou water copies it for its refraction).</summary>
    public GpuTexture SceneColour => _sceneColour!.Texture;
    /// <summary>The previous frame's depth pyramid for the foliage occlusion cull (<see cref="HizPyramid"/>); built at the end of each frame while <see cref="OcclusionCull"/> is on.</summary>
    public HizPyramid? Hiz { get; private set; }
    /// <summary>On (default): <see cref="End"/> builds the pyramid (<c>--no-occlusion-cull</c> turns it off).</summary>
    public bool OcclusionCull { get; set; } = true;
    /// <summary>What this frame's foliage cull may test against (empty when off, on the first frame, after a camera cut).</summary>
    public OcclusionView OcclusionFor(Vector3 eye) => OcclusionCull && ViewWindow.Current.IsWhole && Hiz is { } hiz ? hiz.ViewFor(eye) : default;

    // width × height is the render size (the scene, SSAO); displayWidth × displayHeight the chain after the upscaler (exposure, composite).
    int _width, _height, _displayWidth, _displayHeight;
    float _allocatedScale;
    UpscalerKind _allocatedKind;
    Target2D? _sceneColour, _sceneDepth;
    // Temporal upscaling: the motion and depth targets, the display-size history (ping-pong).
    Target2D? _motion, _upscaleDepth, _reactive, _historyA, _historyB;
    bool _historyValid, _warnedFallback;
    Target2D? _giAlbedo, _giLow;   // the GI resolve (docs/render-gi.md "Resolve"): the lit fragments' albedo, the probes' light at 1/giDivisor of the render size
    int _giDivisor;
    long _frameIndex;
    Target2D? _aoA, _aoB, _aoMask;
    Target2D? _ldr, _ldrFxaa;   // the composite's LDR picture that FXAA or the heat haze reads, and FXAA's (or SMAA's) when the heat haze follows it
    Target2D? _smaaEdges, _smaaWeights;   // SMAA's edges and blending weights
    GpuTexture? _flowTexture, _perturbationTexture;   // the heat haze's FlowHAZE.dds and Perturber.dds (null: not found, no heat haze)
    Target2D? _luminance, _adaptA, _adaptB;   // Kenshi's exposure: the luminance measure (mipmapped) and the adapted value (1 × 1, ping-pong)
    bool _adaptedValid;
    int _adaptedFrames;
    readonly System.Diagnostics.Stopwatch _adaptClock = new();

    // The native full-screen passes (docs/renderer-native.md 7.1, wave 3 agent E, step P): the same SPIR-V and layout as the GL programs they
    // replaced. Since phase 8 stage 2 each draws in a rendering of its own (8.6). Every handle is resolved once, here.
    readonly SsaoPass _ssao;
    readonly SsaoMaskPass _ssaoMask;
    readonly BlurPass _blur;
    readonly LuminancePass _luminancePass;
    readonly AdaptPass _adaptPass;
    readonly CompositePass _compositePass;
    readonly FxaaPass _fxaaPass;
    readonly SmaaPasses _smaaPasses;
    readonly HeatHazePass _hazePass;
    readonly VelocityPass _velocityPass;
    readonly CloudMotionPass _cloudMotionPass;
    readonly TaaPass _taaPass;
    readonly FogPass _fogPass;
    GiResolvePass? _giResolvePass;   // made the first time the GI resolve runs
    GiApplyPass? _giApplyPass;
    readonly ParticleDepthPass _particleDepthPass;
    readonly ParticleSoftDepthPass _particleSoftDepthPass;
    readonly ParticleCompositePass _particleCompositePass;
    readonly ParticleCoveragePass _particleCoveragePass;
    AoApplyPass? _aoApplyPass;   // made the first time the occlusion goes onto the scene before the particles (ApplySsao)

    /// <summary>One native full-screen program: the shared vertex shader with a fragment shader, and its resolved handles.</summary>
    abstract class FullscreenProgram
    {
        public readonly LegacyProgram P;
        protected FullscreenProgram(GpuContext gpu, string fragment, string name) => P = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, fragment, name);
    }

    sealed class SsaoMaskPass : FullscreenProgram
    {
        public readonly SamplerSlot Scene;
        public SsaoMaskPass(GpuContext gpu) : base(gpu, PostProcessShaders.SsaoMask, "post ssao mask") => Scene = P.Sampler("uScene");
    }

    sealed class BlurPass : FullscreenProgram
    {
        public readonly SamplerSlot Ao, Mask;
        public readonly UniformHandle Step, CharacterAo;
        public BlurPass(GpuContext gpu) : base(gpu, PostProcessShaders.SsaoBlur, "post ssao blur") =>
            (Ao, Mask, Step, CharacterAo) = (P.Sampler("uAo"), P.Sampler("uMask"), P.Uniform("uStep"), P.Uniform("uCharacterAo"));
    }

    /// <summary>The finished occlusion multiplied into the scene colour (<see cref="PostProcessShaders.SsaoApply"/>).</summary>
    sealed class AoApplyPass : FullscreenProgram
    {
        public readonly SamplerSlot Ao;
        public AoApplyPass(GpuContext gpu) : base(gpu, PostProcessShaders.SsaoApply, "post ssao apply") => Ao = P.Sampler("uAo");
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
        public readonly SamplerSlot Scene, Ao, Adapted, Bloom;
        public readonly UniformHandle Auto, Exposure, UseAo, Dither, SceneSize, Sharpen, Tone, ToneMix, Grade, Saturation, Contrast, Night, Balance, BloomStrength;
        public CompositePass(GpuContext gpu) : base(gpu, PostProcessShaders.Composite, "post composite")
        {
            (Scene, Ao, Adapted) = (P.Sampler("uScene"), P.Sampler("uAo"), P.Sampler("uAdapted"));
            (SceneSize, Sharpen) = (P.Uniform("uSceneSize"), P.Uniform("uSharpen"));
            (Auto, Exposure, UseAo, Dither) = (P.Uniform("uAuto"), P.Uniform("uExposure"), P.Uniform("uUseAo"), P.Uniform("uDither"));
            (Tone, ToneMix, Grade, Saturation, Contrast) = (P.Uniform("uTone"), P.Uniform("uToneMix"), P.Uniform("uGrade"), P.Uniform("uSaturation"), P.Uniform("uContrast"));
            (Night, Balance) = (P.Uniform("uNight"), P.Uniform("uBalance"));
            (Bloom, BloomStrength) = (P.Sampler("uBloom"), P.Uniform("uBloomStrength"));
        }
    }

    sealed class FxaaPass : FullscreenProgram
    {
        public readonly SamplerSlot Image;
        public readonly UniformHandle Texel;
        public FxaaPass(GpuContext gpu) : base(gpu, PostProcessShaders.Fxaa, "post fxaa") => (Image, Texel) = (P.Sampler("uImage"), P.Uniform("uTexel"));
    }

    /// <summary>SMAA's three passes (<see cref="PostProcessShaders.SmaaEdges"/>, <see cref="PostProcessShaders.SmaaWeights"/>, <see cref="PostProcessShaders.SmaaBlend"/>).</summary>
    sealed class SmaaPasses
    {
        public readonly LegacyProgram Edges, Weights, Blend;
        public readonly SamplerSlot EdgesImage, WeightsEdges, BlendImage, BlendWeights;
        public SmaaPasses(GpuContext gpu)
        {
            Edges = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, PostProcessShaders.SmaaEdges, "post smaa edges");
            Weights = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, PostProcessShaders.SmaaWeights, "post smaa weights");
            Blend = LegacyProgram.Create(gpu, PostProcessShaders.Vertex, PostProcessShaders.SmaaBlend, "post smaa blend");
            (EdgesImage, WeightsEdges) = (Edges.Sampler("uImage"), Weights.Sampler("uEdges"));
            (BlendImage, BlendWeights) = (Blend.Sampler("uImage"), Blend.Sampler("uWeights"));
        }

        public void Dispose() { Edges.Dispose(); Weights.Dispose(); Blend.Dispose(); }
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

    /// <summary>The volumetric clouds' motion over the sky's (<see cref="UpscaleShaders.CloudMotion"/>).</summary>
    sealed class CloudMotionPass : FullscreenProgram
    {
        public readonly SamplerSlot NearDepth, Clouds, Distance;
        public readonly UniformHandle ToPrev, Planes, JitterNdc, Tan, Right, Up, Back, Wind, Sun, SunMotion;
        public CloudMotionPass(GpuContext gpu) : base(gpu, UpscaleShaders.CloudMotion, "post cloud motion")
        {
            (NearDepth, Clouds, Distance) = (P.Sampler("uNearDepth"), P.Sampler("uClouds"), P.Sampler("uCloudDistance"));
            (ToPrev, Planes, JitterNdc) = (P.Uniform("uToPrev"), P.Uniform("uPlanes"), P.Uniform("uJitterNdc"));
            (Tan, Sun, SunMotion) = (P.Uniform("uTan"), P.Uniform("uSun"), P.Uniform("uSunMotion"));
            (Right, Up, Back, Wind) = (P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uWind"));
        }
    }

    sealed class VelocityPass : FullscreenProgram
    {
        public readonly SamplerSlot NearDepth;
        public readonly UniformHandle NearToPrev, NearPlanes, FullPlanes, JitterNdc, Mode, FlipDepthY, Tan, Right, Up, Back, EyeY, WaterY, WaterReactive;
        public readonly UniformHandle Sun, SunMotionUniform;
        public VelocityPass(GpuContext gpu) : base(gpu, UpscaleShaders.Velocity, "post velocity")
        {
            (Sun, SunMotionUniform) = (P.Uniform("uSun"), P.Uniform("uSunMotion"));
            FlipDepthY = P.Uniform("uFlipDepthY");
            NearDepth = P.Sampler("uNearDepth");
            (NearToPrev, NearPlanes, FullPlanes) = (P.Uniform("uNearToPrev"), P.Uniform("uNearPlanes"), P.Uniform("uFullPlanes"));
            (JitterNdc, Mode, Tan) = (P.Uniform("uJitterNdc"), P.Uniform("uMode"), P.Uniform("uTan"));
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
        public readonly SamplerSlot Depth, Mask;
        public readonly UniformHandle Tan, NearFar, Size, Radius, Strength, FadeStart, FadeEnd, Right, Up, Back, Eye, CharacterOccluder, WaterY;
        public SsaoPass(GpuContext gpu) : base(gpu, PostProcessShaders.Ssao, "post ssao")
        {
            (Depth, Mask, CharacterOccluder, WaterY) = (P.Sampler("uDepth"), P.Sampler("uMask"), P.Uniform("uCharacterOccluder"), P.Uniform("uWaterY"));
            (Tan, NearFar, Size, Radius, Strength, FadeStart, FadeEnd) = (P.Uniform("uTan"), P.Uniform("uNearFar"), P.Uniform("uSize"),
                P.Uniform("uRadius"), P.Uniform("uStrength"), P.Uniform("uFadeStart"), P.Uniform("uFadeEnd"));
            (Right, Up, Back, Eye) = (P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uSsaoEye"));
        }
    }


    /// <summary>The placed fog volumes in one full-screen pass over the scene (<see cref="PostProcessShaders.FogVolumes"/>).</summary>
    sealed class FogPass : FullscreenProgram
    {
        public readonly SamplerSlot NearDepth, ShaftInjected;
        public readonly UniformHandle NearPlanes, Tan, Right, Up, Back, WaterY;
        // The light shafts' grid (LightShaftShaders.FogVolumeShadow): the volumes shadowed where the sun does not reach.
        public readonly UniformHandle ShaftGrid, ShaftRange, ShaftTan, ShaftRight, ShaftUp, ShaftBack, ShaftFog;
        public FogPass(GpuContext gpu) : base(gpu, PostProcessShaders.FogVolumes, "post fog volumes")
        {
            NearDepth = P.Sampler("uNearDepth");
            (NearPlanes, Tan, Right, Up, Back, WaterY) = (P.Uniform("uNearPlanes"), P.Uniform("uTan"),
                P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uWaterY"));
            ShaftInjected = P.Sampler("uShaftInjected");
            (ShaftGrid, ShaftRange, ShaftTan, ShaftFog) = (P.Uniform("uShaftGrid"), P.Uniform("uShaftRange"), P.Uniform("uShaftTan"), P.Uniform("uShaftFog"));
            (ShaftRight, ShaftUp, ShaftBack) = (P.Uniform("uShaftRight"), P.Uniform("uShaftUp"), P.Uniform("uShaftBack"));
        }
    }

    /// <summary>The depth reconstruction both GI resolve passes share (<see cref="Gi.GiResolveShaders"/>).</summary>
    abstract class GiPass : FullscreenProgram
    {
        public readonly SamplerSlot NearDepth, Albedo;
        public readonly UniformHandle NearPlanes, Tan, Right, Up, Back, Eye, FullSize, Div;
        protected GiPass(GpuContext gpu, string fragment, string name) : base(gpu, fragment, name)
        {
            (NearDepth, Albedo) = (P.Sampler("uNearDepth"), P.Sampler("uAlbedo"));
            (NearPlanes, Tan, Right, Up, Back, Eye) = (P.Uniform("uNearPlanes"), P.Uniform("uTan"),
                P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uGiEye"));
            (FullSize, Div) = (P.Uniform("uFullSize"), P.Uniform("uDiv"));
        }
    }

    sealed class GiResolvePass(GpuContext gpu) : GiPass(gpu, Gi.GiResolveShaders.Resolve, "post gi resolve");

    sealed class GiApplyPass : GiPass
    {
        public readonly SamplerSlot Low;
        public readonly UniformHandle LowSize;
        public GiApplyPass(GpuContext gpu) : base(gpu, Gi.GiResolveShaders.Apply, "post gi apply") => (Low, LowSize) = (P.Sampler("uLow"), P.Uniform("uLowSize"));
    }

    /// <summary>The low-resolution particles' depth-only downsample of the scene depth (<see cref="PostProcessShaders.ParticleDepth"/>).</summary>
    sealed class ParticleDepthPass : FullscreenProgram
    {
        public readonly SamplerSlot Depth;
        public readonly UniformHandle FullSize, Div, Layer, Planes;
        public ParticleDepthPass(GpuContext gpu) : base(gpu, PostProcessShaders.ParticleDepth, "post particle depth") =>
            (Depth, FullSize, Div, Layer, Planes) = (P.Sampler("uDepth"), P.Uniform("uFullSize"), P.Uniform("uDiv"), P.Uniform("uLayer"), P.Uniform("uPlanes"));
    }

    /// <summary>The scene's depth copied to a colour target for the soft fade (<see cref="PostProcessShaders.ParticleSoftDepth"/>).</summary>
    sealed class ParticleSoftDepthPass : FullscreenProgram
    {
        public readonly SamplerSlot Depth;
        public ParticleSoftDepthPass(GpuContext gpu) : base(gpu, PostProcessShaders.ParticleSoftDepth, "post particle soft depth") => Depth = P.Sampler("uDepth");
    }

    /// <summary>The low-resolution particles over the scene (<see cref="PostProcessShaders.ParticleComposite"/>).</summary>
    sealed class ParticleCompositePass : FullscreenProgram
    {
        public readonly SamplerSlot Accum, LowDepth, AccumFar, LowDepthFar, Depth;
        public readonly UniformHandle LowSize, NearPlanes, Div, Scale, Footprint;
        public ParticleCompositePass(GpuContext gpu) : base(gpu, PostProcessShaders.ParticleComposite, "post particle composite")
        {
            (Accum, LowDepth, Depth) = (P.Sampler("uAccum"), P.Sampler("uLowDepth"), P.Sampler("uDepth"));
            (AccumFar, LowDepthFar) = (P.Sampler("uAccumFar"), P.Sampler("uLowDepthFar"));
            (LowSize, NearPlanes, Div) = (P.Uniform("uLowSize"), P.Uniform("uNearPlanes"), P.Uniform("uDiv"));
            (Scale, Footprint) = (P.Uniform("uScale"), P.Uniform("uFootprint"));
        }
    }

    /// <summary>The low-resolution particles' coverage for the upscalers' reactive mask (<see cref="PostProcessShaders.ParticleCoverage"/>).</summary>
    sealed class ParticleCoveragePass : FullscreenProgram
    {
        public readonly SamplerSlot Accum, LowDepth, AccumFar, LowDepthFar, Depth;
        public readonly UniformHandle LowSize, NearPlanes, Div, FullSize, Limit;
        public ParticleCoveragePass(GpuContext gpu) : base(gpu, PostProcessShaders.ParticleCoverage, "post particle coverage")
        {
            (Accum, LowDepth, Depth) = (P.Sampler("uAccum"), P.Sampler("uLowDepth"), P.Sampler("uDepth"));
            (AccumFar, LowDepthFar) = (P.Sampler("uAccumFar"), P.Sampler("uLowDepthFar"));
            (LowSize, NearPlanes, Div, FullSize, Limit) = (P.Uniform("uLowSize"), P.Uniform("uNearPlanes"), P.Uniform("uDiv"), P.Uniform("uFullSize"), P.Uniform("uLimit"));
        }
    }

    float _nearPlane = 1, _farPlane = 1000, _fovY = 0.87f, _aspect = 1;
    bool _haveScene;

    // GPU timestamps (the frame's QueryArena): a set per frame, section k lasting from stamp k-1 to stamp k, read once its frame has been collected.
    const int MaxStamps = 16;
    sealed class StampSet
    {
        public readonly QuerySlot[] Slots = new QuerySlot[MaxStamps];
        public readonly string[] Names = new string[MaxStamps];
        public int Count;
        public long Frame;
        public long Tag;
    }
    readonly Queue<StampSet> _pendingStamps = new();
    readonly Stack<StampSet> _freeStamps = new();
    StampSet? _stamps;
    Action<IGpuCommandList>? _stampWriter;
    QuerySlot _written;
    readonly Dictionary<string, (double Sum, int Count)> _costs = [];

    /// <summary>The benchmark harness's frame number for the frame begun next; <see cref="OnCost"/> hands it back with each section's GPU ms once the frame's timestamps have been read.</summary>
    public long CostTag { get; set; }
    public Action<long, string, double>? OnCost { get; set; }

    /// <summary>What every pass draws with: what VkGl made of the GL state the chain set (no culling, depth test or write, bias or blending; all
    /// channels; filled; one sample).</summary>
    static readonly DrawState PassState = new(CullMode.None, FrontFaceDirection.Ccw, false, false, DepthFunction.Lequal,
        false, 0, 0, BlendState.Off, ColorMask.R | ColorMask.G | ColorMask.B | ColorMask.A,
        PolygonKind.Fill, false, false);

    public PostProcess(GpuContext gpu, PostOptions options)
    {
        Gpu = gpu;
        Options = options;
        (_ssao, _blur, _luminancePass, _adaptPass) = (new SsaoPass(gpu), new BlurPass(gpu), new LuminancePass(gpu), new AdaptPass(gpu));
        _ssaoMask = new SsaoMaskPass(gpu);
        (_compositePass, _fxaaPass, _hazePass) = (new CompositePass(gpu), new FxaaPass(gpu), new HeatHazePass(gpu));
        _smaaPasses = new SmaaPasses(gpu);
        (_velocityPass, _taaPass, _fogPass) = (new VelocityPass(gpu), new TaaPass(gpu), new FogPass(gpu));
        _cloudMotionPass = new CloudMotionPass(gpu);
        (_particleDepthPass, _particleCompositePass, _particleCoveragePass) = (new ParticleDepthPass(gpu), new ParticleCompositePass(gpu), new ParticleCoveragePass(gpu));
        _particleSoftDepthPass = new ParticleSoftDepthPass(gpu);
    }

    // ---- targets ----

    IEnumerable<Target2D> Targets() =>
        new[] { _sceneColour, _sceneDepth, _motion, _upscaleDepth, _reactive, _historyA, _historyB, _aoA, _aoB, _aoMask, _ldr, _ldrFxaa, _smaaEdges, _smaaWeights, _luminance, _adaptA, _adaptB, _lowTargets[0].Accum, _lowTargets[0].Depth, _lowTargets[0].Soft, _lowTargets[1].Accum, _lowTargets[1].Depth, _lowTargets[1].Soft, _softDepth, _rateDistance, _rateImage, _neutralRate, _giAlbedo, _giLow, _shaftInjected, _shaftLight, _shaftScatter, _shaftRaw, _shaftHistory }.OfType<Target2D>();

    void Free()
    {
        Hiz?.Invalidate();
        foreach (var t in Targets()) t.Texture.Dispose();   // released after the frames in flight
        _sceneColour = _sceneDepth = _motion = _upscaleDepth = _reactive = _historyA = _historyB = _rateDistance = _rateImage = _neutralRate = null;
        _rateBuiltFrame = long.MinValue;
        _aoA = _aoB = _aoMask = _ldr = _ldrFxaa = _smaaEdges = _smaaWeights = _luminance = _adaptA = _adaptB = null;
        foreach (var l in _lowTargets) l.Accum = l.Depth = l.Soft = null;
        _giAlbedo = _giLow = _shaftInjected = _shaftLight = _shaftScatter = _shaftRaw = _shaftHistory = _softDepth = null;
        FreeBloom();
        _adaptedValid = _historyValid = _shaftHistoryValid = _softDepthReady = false;
        _adaptedFrames = 0;
    }

    readonly Dictionary<InternalFormat, bool> _storageSupport = [];

    /// <summary>Whether the device can make <paramref name="format"/> a storage image (VkGl gave its float colour textures storage use, which the vendor upscalers write with).</summary>
    bool SupportsStorage(InternalFormat format)
    {
        if (!_storageSupport.TryGetValue(format, out bool ok))
        {
            var vk = GlConventions.VkFormat(format);
            Gpu.Device.Vk.GetPhysicalDeviceFormatProperties(Gpu.Device.PhysicalDevice, vk, out var props);
            _storageSupport[format] = ok = (props.OptimalTilingFeatures & Vk.FormatFeatureFlags.StorageImageBit) != 0;
        }
        return ok;
    }

    /// <summary>A target of a GL internal format (the format VkGl made of it), with the uses VkGl's image had, one level unless asked.</summary>
    Target2D Make(IGpuUploadBatch batch, int w, int h, InternalFormat glFormat, TextureMinFilter min, string name, int levels = 1)
    {
        var use = TextureUse.Sampled | TextureUse.TransferSrc | TextureUse.TransferDst | (GlConventions.IsDepthFormat(glFormat) ? TextureUse.DepthTarget : TextureUse.ColourTarget);
        if (glFormat is InternalFormat.Rgba16F or InternalFormat.R32F or InternalFormat.Rg16F or InternalFormat.Rg32F && SupportsStorage(glFormat)) use |= TextureUse.Storage;
        var texture = batch.Create(new TextureDesc(glFormat, w, h, levels, Use: use, Name: name));
        return new Target2D(texture, min, min == TextureMinFilter.Nearest ? TextureMagFilter.Nearest : TextureMagFilter.Linear);
    }

    /// <summary>The targets could not be made (out of video memory): the frame draws no world, and the next <see cref="Begin"/> tries again (the old
    /// targets, released this frame, are freed by then, and the guard has started evicting).</summary>
    public bool Unallocated { get; private set; }
    bool _unallocatedLogged;

    void Allocate(int displayW, int displayH)
    {
        try
        {
            AllocateTargets(displayW, displayH);
            Unallocated = false;
        }
        catch (VulkanException e) when (e.Result is Vk.Result.ErrorOutOfDeviceMemory or Vk.Result.ErrorOutOfHostMemory)
        {
            Free();
            (_displayWidth, _displayHeight, Unallocated) = (0, 0, true);
            if (!_unallocatedLogged) Console.WriteLine($"vram      post targets for {displayW}x{displayH} not made ({e.Message}): the world is not drawn until they are");
            _unallocatedLogged = true;
        }
    }

    void AllocateTargets(int displayW, int displayH)
    {
        Free();
        var up = Options.Upscale;
        (_allocatedKind, _allocatedScale) = (up.Kind, up.EffectiveScale);
        (_displayWidth, _displayHeight) = (displayW, displayH);
        var (w, h) = up.RenderSize(displayW, displayH);
        _width = w; _height = h;

        using (var batch = Gpu.Uploads.Begin())
        {
            // Colour (RGBA16F) and depth (GL's 24 bit, a 32-bit float buffer, reversed-Z: DepthConvention) the scene is drawn into.
            _sceneColour = Make(batch, w, h, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post scene colour");
            _sceneDepth = Make(batch, w, h, InternalFormat.DepthComponent24, TextureMinFilter.Nearest, "post scene depth");
            if (up.Temporal)
            {
                _motion = Make(batch, w, h, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post motion");
                _upscaleDepth = Make(batch, w, h, InternalFormat.R32F, TextureMinFilter.Nearest, "post upscale depth");
                // R32F, not R8: Streamline cannot size an R8_UNORM resource and drops it (DLSS's hint), FSR takes either.
                _reactive = Make(batch, w, h, InternalFormat.R32F, TextureMinFilter.Nearest, "post reactive");
                // The output; the TAA's second history is made when it first runs (a vendor upscaler keeps its own history: one image does).
                _historyB = Make(batch, displayW, displayH, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post taa history");
            }
            // The LDR, FXAA and SMAA targets are made when a frame first needs them (MakeLdrTargets).
            int hw = Math.Max((w + 1) / 2, 1), hh = Math.Max((h + 1) / 2, 1);
            _aoA = Make(batch, hw, hh, InternalFormat.Rg16F, TextureMinFilter.Linear, "post ssao");
            _aoB = Make(batch, hw, hh, InternalFormat.Rg16F, TextureMinFilter.Linear, "post ssao");
            _aoMask = Make(batch, hw, hh, InternalFormat.R8, TextureMinFilter.Linear, "post ssao mask");
            // Exposure runs after the upscaler, at the display size: the luminance's whole mip chain (GL's, defined by GenerateMipmap), read mipmapped.
            _luminance = Make(batch, LuminanceSize, LuminanceSize, InternalFormat.R32F, TextureMinFilter.LinearMipmapNearest, "post luminance", LuminanceLevels);
            _adaptA = Make(batch, 1, 1, InternalFormat.Rg32F, TextureMinFilter.Linear, "post exposure adapted");
            _adaptB = Make(batch, 1, 1, InternalFormat.Rg32F, TextureMinFilter.Linear, "post exposure adapted");
        }
    }

    // ---- timing ----

    /// <summary>A timestamp at this point of the frame: into the open segment, or interleaved into the frame (VkGl's pass is not ended).</summary>
    void Stamp(string name)
    {
        var set = _stamps;
        if (set is null || set.Count >= MaxStamps) return;
        var arena = Gpu.Frame.Timestamps;
        QuerySlot slot;
        if (_segment is { } cmd)
        {
            slot = arena.Allocate();
            cmd.Timestamp(arena, slot);
        }
        else
        {
            _stampWriter ??= c => { _written = Gpu.Frame.Timestamps.Allocate(); c.Timestamp(Gpu.Frame.Timestamps, _written); };
            _written = default;
            Gpu.Interleave(_stampWriter);
            slot = _written;
        }
        set.Slots[set.Count] = slot;
        set.Names[set.Count++] = name;
        if (StageClock.OnClose is not null) StageClock.Sub("post " + name);
    }

    /// <summary>Adds the sections of every frame whose timestamps have been collected (a frame ring after it); drops frames too old to arrive.</summary>
    void Collect()
    {
        var arena = Gpu.Frame.Timestamps;
        while (_pendingStamps.TryPeek(out var set))
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
                    _costs.TryGetValue(name, out var c);
                    _costs[name] = (c.Sum + (now - previous) / 1e6, c.Count + 1);
                    OnCost?.Invoke(set.Tag, name, (now - previous) / 1e6);
                    previous = now;
                }
            }
            else if (Gpu.Frame.Number - set.Frame < 16) break;   // not collected yet
            _pendingStamps.Dequeue();
            _freeStamps.Push(set);
        }
    }

    /// <summary>Takes in every timing that has arrived. No longer waits (since phase 8 stage 2): a frame's timestamps arrive when its slot of the
    /// frame ring comes round, so the frames still in flight are counted later.</summary>
    public void Flush() => Collect();

    /// <summary>Average GPU milliseconds per section since the last call ("scene" is the frame before post-processing), then resets.</summary>
    public IReadOnlyList<(string Name, double Ms)> TakeCosts()
    {
        var list = _costs.Select(kv => (kv.Key, kv.Value.Sum / kv.Value.Count)).ToList();
        _costs.Clear();
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
        if (w != _displayWidth || h != _displayHeight || up.Kind != _allocatedKind || up.EffectiveScale != _allocatedScale)
            Allocate(w, h);
        if (Unallocated) return;
        EnsureGi();
        Collect();
        _stamps = _freeStamps.Count > 0 ? _freeStamps.Pop() : new StampSet();
        _stamps.Count = 0;
        _stamps.Tag = CostTag;
        _haveScene = false;
        foreach (var l in _lowTargets) l.Drawn = l.Late = false;
        _softDepthReady = false;
        CloudMotion = null;   // WorldFrame.Draw sets this frame's; a sky-only frame has none
        _sceneMarked = _aoApplied = false;
        Stamp("start");
        _frameIndex++;
        JitterPixels = Temporal ? Jitter.Offset(_frameIndex, JitterPhases) : Vector2.Zero;
        // Textures at the display size's detail: log2 of the scale, and one further as the vendor upscalers recommend (DECISIONS 15); TAA too.
        Gpu.LodBias = Temporal ? MathF.Log2(_width / (float)_displayWidth) - 1 : 0;
        SceneTargets = PassTargets.Of(_sceneColour!.Texture, _sceneDepth!.Texture, GiAlbedoTarget);
    }

    // ---- temporal upscaling ----

    /// <summary>The size the scene is drawn at this frame (valid after <see cref="Begin"/>): the display size, or less with an upscaler.</summary>
    public int RenderWidth => _width;
    public int RenderHeight => _height;
    /// <summary>Whether frames are jittered and reconstructed over time (an upscaler is on).</summary>
    public bool Temporal => _allocatedKind != UpscalerKind.Off;
    /// <summary>The offset to move this frame's projection by, in render pixels (<see cref="Jitter.Apply"/>); zero without an upscaler.</summary>
    public Vector2 JitterPixels { get; private set; }
    public int JitterPhases => Jitter.PhaseCount(_width, _displayWidth);
    /// <summary>Frames a still picture needs before the history has converged (screenshots).</summary>
    public int WarmupFrames => Options.Upscale.Temporal ? Math.Max(32, 2 * Jitter.PhaseCount(Options.Upscale.RenderSize(1000, 1000).Width, 1000)) : 0;
    /// <summary>The vendor upscaler for <see cref="UpscalerKind.Fsr"/> / <see cref="UpscalerKind.Dlss"/>; without one (or on failure) TAA runs.</summary>
    public IUpscaler? External { get; set; }

    /// <summary>
    /// Draws motion of moving geometry over the camera motion (the swaying grass), called after the velocity pass with the motion target bound,
    /// only red and green written (B and A, depth and reactivity, stay), no depth test, culling or blending.
    /// </summary>
    public Action<MotionTargets>? ObjectMotion { get; set; }

    /// <summary>This frame's volumetric clouds, whose sky pixels get the clouds' motion (<see cref="UpscaleShaders.CloudMotion"/>); null: the sky's.</summary>
    public CloudMotion? CloudMotion { get; set; }

    void IWorldPostChain.SetCloudMotion(CloudMotion? clouds) => CloudMotion = clouds;

    /// <summary>The volumetric clouds' motion over the sky's in the motion target (red and green), by the clouds' opacity.</summary>
    void RunCloudMotion(CloudMotion clouds)
    {
        var c = _cloudMotionPass;
        Bind(c.P, c.NearDepth, _sceneDepth);
        if (c.Clouds.IsValid) c.P.Bind(c.Clouds, clouds.Clouds);
        if (c.Distance.IsValid) c.P.Bind(c.Distance, clouds.Distance);
        c.P.Set(c.ToPrev, _toPrevious);
        c.P.Set(c.Planes, _planes.X, _planes.Y);
        c.P.Set(c.JitterNdc, 2 * JitterPixels.X / _width, 2 * JitterPixels.Y / _height);
        float tanY = MathF.Tan(_fovNow * 0.5f);
        c.P.Set(c.Tan, ViewWindow.Tan(tanY * _aspectNow, tanY));
        var r = _viewRotation;
        c.P.Set(c.Right, r.M11, r.M21, r.M31);
        c.P.Set(c.Up, r.M12, r.M22, r.M32);
        c.P.Set(c.Back, ViewWindow.BackFor(r, _fovNow, _aspectNow));
        c.P.Set(c.Wind, clouds.Wind.X, clouds.Wind.Y);
        c.P.Set(c.Sun, _sunMotion.Sun);
        c.P.Set(c.SunMotion, _sunMotion.Motion);
        Draw(c.P, _motion!.Attachment, _motion.Format, _width, _height, _width, _height,
            PassState with { Blend = new BlendState(true, BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha), ColourMask = ColorMask.R | ColorMask.G });
        CloseSegment();
    }
    /// <summary>Makes the vendor upscaler for <see cref="UpscalerKind.Fsr"/> / <see cref="UpscalerKind.Dlss"/> when it is first asked for (null: not available).</summary>
    public Func<UpscalerKind, IUpscaler?>? UpscalerFactory { get; set; }
    readonly HashSet<UpscalerKind> _unavailable = [];

    /// <summary>The vendor upscaler for <paramref name="kind"/>; DLSS where it is missing (not an NVIDIA card) is FSR, then TAA.</summary>
    IUpscaler? ExternalFor(UpscalerKind kind)
    {
        if (kind is not (UpscalerKind.Fsr or UpscalerKind.Dlss)) return null;
        var external = Create(kind);
        if (external is null && kind == UpscalerKind.Dlss && (external = Create(UpscalerKind.Fsr)) is not null && !_warnedFallback)
        {
            Console.WriteLine("upscaler  DLSS is not available here; using FSR");
            _warnedFallback = true;
        }
        return external;
    }

    IUpscaler? Create(UpscalerKind kind)
    {
        if (External?.Kind == kind) return External;
        if (_unavailable.Contains(kind) || UpscalerFactory is null) return null;
        External?.Dispose();
        External = UpscalerFactory(kind);
        if (External is null) _unavailable.Add(kind);
        else _historyValid = false;
        return External;
    }
    /// <summary>The upscaler that ran last frame (for the overlay).</summary>
    public string ActiveUpscaler { get; private set; } = "off";

    // The camera this frame and last (eye-relative views), and the scene's reprojection and planes.
    Matrix4x4 _viewRotation, _previousRotation;
    Vector3 _eyeNow, _previousEye;
    float _fovNow, _aspectNow, _previousFov, _previousAspect;
    bool _previousValid;
    Matrix4x4 _toPrevious;
    Vector2 _planes;
    readonly System.Diagnostics.Stopwatch _frameClock = new();

    /// <summary>The main camera of this frame, before <see cref="BeginScene"/>: for the motion vectors (reprojection into the previous frame).</summary>
    public void SetCamera(Vector3 eye, Matrix4x4 view, float fieldOfView, float aspectRatio)
    {
        _eyeNow = eye;
        _viewRotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        (_fovNow, _aspectNow) = (fieldOfView, aspectRatio);
    }

    /// <summary>
    /// The matrix from this frame's jittered clip space of the scene to the previous frame's unjittered clip space. Both views are taken
    /// relative to this frame's eye (the previous one shifted by the eye's step), so no 10⁵-unit translations meet in float.
    /// Both projections are the scene's (<see cref="DepthConvention.Perspective"/>), so a stored depth is the clip z.
    /// </summary>
    Matrix4x4 ToPrevious(float near, float far)
    {
        if (!_previousValid) return Matrix4x4.Identity;
        var standard = Matrix4x4.CreatePerspectiveFieldOfView(_fovNow, _aspectNow, near, far);
        var now = DepthConvention.Apply(Jitter.Apply(standard, JitterPixels, _width, _height));
        var previous = DepthConvention.Perspective(_previousFov, _previousAspect, near, far);
        return Reprojection.ClipToPrevious(_viewRotation, _eyeNow, now, _previousRotation, _previousEye, previous);
    }

    /// <summary>The scene's projection (call after <see cref="SetCamera"/>): sets <see cref="SceneTargets"/>, the planes and the reprojection.</summary>
    public void BeginScene(float near, float far, float fieldOfView, float aspectRatio)
    {
        SceneTargets = PassTargets.Of(_sceneColour!.Texture, _sceneDepth!.Texture, GiAlbedoTarget);
        _nearPlane = near; _farPlane = far; _fovY = fieldOfView; _aspect = aspectRatio;
        _planes = new Vector2(near, far);
        if (Temporal) _toPrevious = ToPrevious(near, far);
        _haveScene = true;
    }

    // ---- native passes ----

    IGpuCommandList? _segment;

    /// <summary>The open native segment of the chain (one is begun when none is open: VkGl's pass ends, a full barrier is placed).</summary>
    IGpuCommandList Segment() => _segment ??= Gpu.BeginNative("post");

    /// <summary>Ends the open segment (before anything that records through VkGl: the vendor upscalers, the grass-motion guest).</summary>
    void CloseSegment()
    {
        if (_segment is null) return;
        Gpu.EndNative(_segment);
        _segment = null;
    }

    /// <summary>One full-screen triangle with <paramref name="p"/> into <paramref name="target"/> (its uniforms and samplers set before), in a
    /// rendering of its own, followed by a full barrier (VkGl barriered before every pass).</summary>
    readonly Dictionary<(LegacyProgram, InternalFormat, InternalFormat, DrawState), GpuPipeline> _pipelines = [];

    void Draw(LegacyProgram p, Target2D target) => Draw(p, target.Attachment, target.Format, target.Width, target.Height, target.Width, target.Height);

    void Draw(LegacyProgram p, RenderTarget colour, InternalFormat format, int targetWidth, int targetHeight, int viewportWidth, int viewportHeight, DrawState? state = null)
    {
        DrawPass(p, colour, format, default, InternalFormat.None, targetWidth, targetHeight, viewportWidth, viewportHeight, state);
    }

    /// <summary>As <see cref="Draw(LegacyProgram, RenderTarget, InternalFormat, int, int, int, int, DrawState?)"/>, with an optional depth attachment (the pipeline is cached per program and attachment formats: one state each).</summary>
    void DrawPass(LegacyProgram p, RenderTarget colour, InternalFormat format, RenderTarget depth, InternalFormat depthFormat, int targetWidth, int targetHeight, int viewportWidth, int viewportHeight, DrawState? state)
    {
        var cmd = Segment();
        cmd.BeginLabel(p.Name);   // each pass its own range in a GPU trace (the segment is "post")
        cmd.BeginRendering(new RenderingDesc(colour, depth, targetWidth, targetHeight));
        var s = state ?? PassState;
        cmd.SetViewport(new PixelViewport(0, 0, viewportWidth, viewportHeight, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)targetWidth, (uint)targetHeight));
        cmd.SetRaster(s.Cull, s.Front);
        cmd.SetDepth(s.DepthTest, s.DepthWrite, s.Compare);
        cmd.SetDepthBias(s.BiasEnable, s.BiasConstant, s.BiasSlope);
        if (!_pipelines.TryGetValue((p, format, depthFormat, s), out var pipeline))
            _pipelines[(p, format, depthFormat, s)] = pipeline = Gpu.Pipelines.Get(s.Pipeline(p.Program, p.VertexLayout([]), PrimitiveType.Triangles,
                new AttachmentFormats(format, depthFormat), p.Name));
        cmd.BindPipeline(pipeline);
        p.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.EndLabel();
        cmd.Barrier([GpuDependency.Full]);
    }

    /// <summary>A target as its GL texture was sampled: its filters, clamped, the upscaler's LOD bias on a mipmapped filter (VkGl's rule,
    /// <see cref="SamplerDesc.FromGl"/>, with the bias as it is now).</summary>
    SampledTexture Sampled(Target2D t) => Sampled(t.Texture, t.Min, t.Mag, TextureWrapMode.ClampToEdge, 1);

    SampledTexture Sampled(GpuTexture t, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap, float anisotropy) =>
        new(Gpu.Samplers.Get(SamplerDesc.FromGl(min, mag, wrap, wrap, TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, anisotropy, false, Gpu.LodBias)),
            t.View());

    /// <summary>Binds a target (null: the stand-in VkGl bound for nothing). Call where the GL code bound the unit: the sampler depends on the LOD bias then.</summary>
    void Bind(LegacyProgram p, SamplerSlot slot, Target2D? target)
    {
        if (slot.IsValid) p.Bind(slot, target is null ? Gpu.Dummy(p.SamplerInfo(slot)) : Sampled(target));
    }

    /// <summary>Resolves the scene and runs the chain into <see cref="Target"/>.</summary>
    public void End()
    {
        var o = Options;
        MarkScene();
        // The final passes draw into Target (the window's backbuffer, or the caller's texture) with a viewport of the display size.
        var final = PassTargets.Of(Target ?? throw new InvalidOperationException("PostProcess.Target is not set"), null);
        bool needDepth = o.Ssao && _haveScene;
        if (_shaftsPending || _fogPending) ApplyLayers(false);   // a caller that drew the layers without ApplySsao: the occlusion stays the composite's

        bool ao = needDepth;
        if (ao && !_aoApplied) RunSsao();
        if (ao && !_aoApplied) Stamp("ssao");
        // The upscaler: the scene at the render size becomes the display-size picture the rest of the chain reads.
        _postColour = _sceneColour!;
        if (Temporal) { _postColour = RunUpscale(); Stamp("upscale"); }
        else ActiveUpscaler = "off";
        bool auto = AutoExposure is not null && _luminance is not null;
        if (auto) RunExposure();
        if (auto) Stamp("exposure");
        var bloom = RunBloom(_postColour);

        // Composite: to the target, or to the LDR picture FXAA (or SMAA) and / or the heat haze then read (the game's order: FXAA, HeatHaze).
        MakeLdrTargets(o.Smaa && !Temporal, o.Fxaa && !Temporal, HeatHazeWanted);
        bool smaa = o.Smaa && !Temporal && _smaaWeights is not null;
        bool fxaa = o.Fxaa && !smaa && !Temporal && _ldr is not null;
        bool haze = HeatHazeRuns && (!(fxaa || smaa) || _ldrFxaa is not null);
        var c = _compositePass;
        Bind(c.P, c.Scene, _postColour);
        Bind(c.P, c.Ao, _aoB);
        Bind(c.P, c.Adapted, auto ? _adaptB : null);
        // Without an upscaler the scene may still be smaller than the display (the render scale): the composite then resamples it (Catmull-Rom).
        bool plainScaled = _postColour.Width != _displayWidth || _postColour.Height != _displayHeight;
        c.P.Set(c.SceneSize, plainScaled ? _postColour.Width : 0f, plainScaled ? _postColour.Height : 0f);
        c.P.Set(c.Sharpen, Temporal && _sharpenAfter ? 2 * Math.Clamp(o.Upscale.Sharpness, 0, 1) : 0f);
        c.P.Set(c.Auto, auto ? 1 : 0);
        c.P.Set(c.Exposure, o.Exposure);
        c.P.Set(c.UseAo, ao && !_aoApplied ? 1 : 0);   // applied before the particles: only the debug view still reads it
        c.P.Set(c.Dither, o.Dither ? 1 : 0);
        c.P.Set(c.Tone, (int)o.ToneMapUsed);
        c.P.Set(c.ToneMix, Math.Clamp(o.ToneMix, 0, 1));
        c.P.Set(c.Grade, o.GradeUsed ? 1 : 0);
        c.P.Set(c.Saturation, PostOptions.Saturation);
        c.P.Set(c.Contrast, PostOptions.Contrast);
        c.P.Set(c.Night, auto ? Math.Clamp(o.NightGradeAmount, 0, 1) : 0f);
        c.P.Set(c.Balance, o.Balance);
        Bind(c.P, c.Bloom, bloom);
        c.P.Set(c.BloomStrength, bloom is null ? 0f : Math.Clamp(o.BloomAmount, 0, 1));
        if (fxaa || smaa || haze) Draw(c.P, _ldr!); else DrawFinal(c.P, final);
        Stamp("composite");
        var picture = _ldr;
        if (fxaa)
        {
            var f = _fxaaPass;
            Bind(f.P, f.Image, _ldr);
            f.P.Set(f.Texel, 1f / _displayWidth, 1f / _displayHeight);
            if (haze) Draw(f.P, _ldrFxaa!); else DrawFinal(f.P, final);
            Stamp("fxaa");
            picture = _ldrFxaa;
        }
        if (smaa)
        {
            RunSmaa(haze, final);
            picture = _ldrFxaa;
        }
        if (haze) RunHeatHaze(picture!, final);
        CloseSegment();

        // The depth pyramid of this frame, for the next frame's foliage cull (nothing else reads it).
        if (OcclusionCull && ViewWindow.Current.IsWhole && _haveScene && _sceneDepth is not null)
        {
            Hiz ??= new HizPyramid(Gpu);
            var hizCmd = Gpu.BeginNative("hiz");
            Hiz.Build(hizCmd, _sceneDepth.Texture, _planes, _width, _height, _eyeNow, _viewRotation, _fovNow, _aspectNow);
            Gpu.EndNative(hizCmd);
            Stamp("hiz");
        }
        else Hiz?.Invalidate();

        if (_stamps is { } set)
        {
            set.Frame = Gpu.Frame.Number;
            _pendingStamps.Enqueue(set);
            _stamps = null;
        }
        (_previousRotation, _previousEye, _previousFov, _previousAspect, _previousValid) = (_viewRotation, _eyeNow, _fovNow, _aspectNow, Temporal);
    }

    /// <summary>
    /// FXAA, SMAA and the heat haze read the composite's LDR picture, as the game's run on its A8R8G8B8 buffers after the HDR composite. Each display-size
    /// target is made the first frame that needs it (a temporal upscaler bypasses FXAA and SMAA, so it never has theirs), and kept until the next resize.
    /// Out of video memory, the passes that lack theirs are skipped.
    /// </summary>
    void MakeLdrTargets(bool smaa, bool fxaa, bool haze)
    {
        bool wantLdr = smaa || fxaa || haze, wantSecond = haze && (smaa || fxaa);
        if (_displayWidth <= 0 || (!wantLdr || _ldr is not null) && (!wantSecond || _ldrFxaa is not null) && (!smaa || _smaaWeights is not null)) return;
        try
        {
            using var batch = Gpu.Uploads.Begin();
            if (wantLdr) _ldr ??= Make(batch, _displayWidth, _displayHeight, InternalFormat.Rgba8, TextureMinFilter.Linear, "post ldr");
            if (wantSecond) _ldrFxaa ??= Make(batch, _displayWidth, _displayHeight, InternalFormat.Rgba8, TextureMinFilter.Linear, "post ldr fxaa");
            if (smaa && _smaaWeights is null)
            {
                _smaaEdges ??= Make(batch, _displayWidth, _displayHeight, InternalFormat.Rgba8, TextureMinFilter.Nearest, "post smaa edges");
                _smaaWeights = Make(batch, _displayWidth, _displayHeight, InternalFormat.Rgba8, TextureMinFilter.Nearest, "post smaa weights");
            }
        }
        catch (VulkanException e) when (e.Result is Vk.Result.ErrorOutOfDeviceMemory or Vk.Result.ErrorOutOfHostMemory) { }
    }

    /// <summary>SMAA on the composite's LDR picture (docs/render-post.md "SMAA"): edges, blending weights, then the blend into the heat haze's input or the target.</summary>
    void RunSmaa(bool haze, PassTargets final)
    {
        var s = _smaaPasses;
        Bind(s.Edges, s.EdgesImage, _ldr);
        Draw(s.Edges, _smaaEdges!);
        Bind(s.Weights, s.WeightsEdges, _smaaEdges);
        Draw(s.Weights, _smaaWeights!);
        Bind(s.Blend, s.BlendImage, _ldr);
        Bind(s.Blend, s.BlendWeights, _smaaWeights);
        if (haze) Draw(s.Blend, _ldrFxaa!); else DrawFinal(s.Blend, final);
        Stamp("smaa");
    }

    // ---- fog volumes ----

    bool _sceneMarked;

    /// <summary>Stamps the end of the scene's own passes (the opaque geometry, the water): once a frame, by the caller before <see cref="RunFogVolumes"/>
    /// and the particles, else by <see cref="End"/>. Not inside an open scene rendering.</summary>
    public void MarkScene()
    {
        if (_sceneMarked) return;
        _sceneMarked = true;
        Stamp("scene");
    }

    /// <summary>Stamps the end of the particles drawn after the fog volumes (the caller's, between <see cref="RunFogVolumes"/> and <see cref="End"/>).</summary>
    public void MarkParticles() => Stamp("particles");

    /// <summary>Stamps the end of the low-resolution particles' own draws (before their composite), so the cost of the draws and of the composite are told apart.</summary>
    public void MarkParticleDraws() => Stamp("particle draws");

    /// <summary>Blends the volumes' accumulated (colour, transmittance) over the scene: source one, destination the source alpha; red, green and
    /// blue only, so the alpha (the characters' SSAO mask) stays.</summary>
    static readonly DrawState FogState = PassState with { Blend = new BlendState(true, BlendingFactor.One, BlendingFactor.SrcAlpha), ColourMask = DrawState.Rgb };

    /// <summary>
    /// The placed fog volumes (docs/formats/fogfeatures.md), in one full-screen pass over the finished scene: after the opaque geometry, the water and
    /// the sky, before the particles, as the game draws them (queue 82, reading the G-buffer depth). Each pixel's distance is rebuilt from the
    /// scene's depth, else it is the sky (the far clip D); where the water plane lies in front of it, the water's. Call after
    /// the scene is drawn (the host closed), before <see cref="End"/>. Does nothing when no volume is in view.
    /// </summary>
    public void RunFogVolumes(bool volumes)
    {
        MarkScene();
        if (!volumes || _sceneColour is null || !_haveScene) return;
        if (MergeLayers)
        {
            PrepareFogLayer();
            _shaftFrame.Darkening = 0;
            Stamp("fog");
            CloseSegment();
            return;
        }
        var f = _fogPass;
        Bind(f.P, f.NearDepth, _sceneDepth);
        f.P.Set(f.NearPlanes, _planes.X, _planes.Y);
        float tanY = MathF.Tan(_fovNow * 0.5f);
        f.P.Set(f.Tan, ViewWindow.Tan(tanY * _aspectNow, tanY));
        var r = _viewRotation;
        f.P.Set(f.Right, r.M11, r.M21, r.M31);
        f.P.Set(f.Up, r.M12, r.M22, r.M32);
        f.P.Set(f.Back, ViewWindow.BackFor(r, _fovNow, _aspectNow));
        f.P.Set(f.WaterY, WaterHeight ?? float.MinValue);
        // The light shafts' grid of this frame, if they ran (darkening 0: the volumes as the game draws them).
        var shaft = _shaftFrame;
        Bind(f.P, f.ShaftInjected, shaft.Darkening > 0 ? _shaftInjected : null);
        f.P.Set(f.ShaftGrid, shaft.Grid);
        f.P.Set(f.ShaftRange, shaft.Range);
        f.P.Set(f.ShaftTan, shaft.Tan);
        f.P.Set(f.ShaftRight, r.M11, r.M21, r.M31);
        f.P.Set(f.ShaftUp, r.M12, r.M22, r.M32);
        f.P.Set(f.ShaftBack, r.M13, r.M23, r.M33);
        f.P.Set(f.ShaftFog, shaft.Darkening, _shaftInjected?.Width ?? 1, _shaftInjected?.Height ?? 1, 0);
        _shaftFrame.Darkening = 0;   // the next frame sets it again if the shafts run
        f.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        Draw(f.P, _sceneColour.Attachment, _sceneColour.Format, _width, _height, _width, _height, FogState);
        Stamp("fog");
        CloseSegment();
    }

    // ---- occlusion before the particles ----

    bool _aoApplied;

    /// <summary>The occlusion multiplied in: the scene colour kept by the factor; red, green and blue only (the alpha is the characters' mask).</summary>
    static readonly DrawState AoMultiplyState = PassState with { Blend = new BlendState(true, BlendingFactor.Zero, BlendingFactor.SrcColor), ColourMask = DrawState.Rgb };

    /// <summary>
    /// Runs the SSAO and multiplies it into the scene colour now, so what is drawn after (the weather's particles) is not darkened by the occlusion
    /// of the surfaces behind it; <see cref="End"/> then leaves it out of the composite. Call after <see cref="RunFogVolumes"/>, before the particles.
    /// Does nothing when the SSAO is off (End then does nothing either).
    /// </summary>
    public void ApplySsao()
    {
        MarkScene();
        if (MergeLayers)
        {
            ApplyLayers(Options.Ssao && _haveScene && _sceneColour is not null && _aoA is not null);
            return;
        }
        if (!Options.Ssao || !_haveScene || _sceneColour is null || _aoA is null) return;
        RunSsao();
        var p = _aoApplyPass ??= new AoApplyPass(Gpu);
        Bind(p.P, p.Ao, _aoB);
        Draw(p.P, _sceneColour.Attachment, _sceneColour.Format, _width, _height, _width, _height, AoMultiplyState);
        Stamp("ssao");
        CloseSegment();
        _aoApplied = true;
    }

    // ---- GI resolve (docs/render-gi.md "Resolve") ----

    /// <summary>
    /// The GI resolve's divisor of the render size (0: off, the lit fragments read the probes themselves; 1 full size; 2 half). Set before
    /// <see cref="Begin"/>: while it is above 0 the scene targets carry the albedo target.
    /// </summary>
    public int GiResolve { get; set; }

    GpuTexture? GiAlbedoTarget => GiResolve > 0 ? _giAlbedo?.Texture : null;

    /// <summary>Makes the albedo and low targets for <see cref="GiResolve"/> (again when its divisor or the render size changed).</summary>
    void EnsureGi()
    {
        if (GiResolve <= 0 || (_giAlbedo is not null && _giDivisor == GiResolve)) return;
        _giAlbedo?.Texture.Dispose();
        _giLow?.Texture.Dispose();
        _giDivisor = GiResolve;
        using var batch = Gpu.Uploads.Begin();
        // The lit surfaces' normal, sqrt(albedo) and take-the-probes flag, packed (AtmosphereShaders' giAlbedoOut); the low target signed (the probes' light can be below the sky's) with the view depth in alpha.
        _giAlbedo = new Target2D(batch.Create(new TextureDesc(InternalFormat.Rgba16Ui, _width, _height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "post gi surface")),
            TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        _giLow = Make(batch, (_width + _giDivisor - 1) / _giDivisor, (_height + _giDivisor - 1) / _giDivisor, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post gi resolve");
    }

    /// <summary>
    /// Swaps the sky's ambient for the probes' light on the surfaces that wrote their albedo: the resolve at 1/<see cref="GiResolve"/> of the render
    /// size, then the depth-aware upsample added to the scene colour. After the scene, before the fog volumes (they cover the light too).
    /// </summary>
    public void RunGiResolve()
    {
        if (GiResolve <= 0 || _giAlbedo is null || _giLow is null || _sceneColour is null || !_haveScene) return;
        MarkScene();
        var r = _giResolvePass ??= new GiResolvePass(Gpu);
        var a = _giApplyPass ??= new GiApplyPass(Gpu);
        float tanY = MathF.Tan(_fovNow * 0.5f);
        var rot = _viewRotation;
        foreach (var g in (ReadOnlySpan<GiPass>)[r, a])
        {
            Bind(g.P, g.NearDepth, _sceneDepth);
            Bind(g.P, g.Albedo, _giAlbedo);
            g.P.Set(g.NearPlanes, _planes.X, _planes.Y);
            g.P.Set(g.Tan, ViewWindow.Tan(tanY * _aspectNow, tanY));
            g.P.Set(g.Right, rot.M11, rot.M21, rot.M31);
            g.P.Set(g.Up, rot.M12, rot.M22, rot.M32);
            g.P.Set(g.Back, ViewWindow.BackFor(rot, _fovNow, _aspectNow));
            g.P.Set(g.Eye, _eyeNow.X, _eyeNow.Y, _eyeNow.Z);
            g.P.Set(g.FullSize, (float)_width, (float)_height);
            g.P.Set(g.Div, (float)_giDivisor);
        }
        r.P.ApplyGlobals();   // the atmosphere's and the probes' uniforms, through the frame globals
        Draw(r.P, _giLow);
        Bind(a.P, a.Low, _giLow);
        a.P.Set(a.LowSize, (float)_giLow.Width, (float)_giLow.Height);
        Draw(a.P, _sceneColour.Attachment, _sceneColour.Format, _width, _height, _width, _height, GiAddState);
        Stamp("gi");
        CloseSegment();
    }

    /// <summary>The resolve added to the scene: source one, destination one; red, green and blue only (the alpha is the characters' SSAO mask).</summary>
    static readonly DrawState GiAddState = PassState with { Blend = new BlendState(true, BlendingFactor.One, BlendingFactor.One), ColourMask = DrawState.Rgb };

    // ---- low-resolution particles (Meitou) ----

    /// <summary>One low-resolution size's accumulation target (RGBA16F: colour added, opacity) and depth, at 1/<see cref="Divisor"/> of the render size.</summary>
    sealed class LowTarget(int divisor)
    {
        public readonly int Divisor = divisor;
        /// <summary>The draw tested against each block's nearest depth (<see cref="Depth"/>), and the one tested against its farthest (<see cref="DepthFar"/>).</summary>
        public Target2D? Accum, Depth, AccumFar, DepthFar;
        /// <summary>The depths the draws are tested against, as a colour (R32F): what the Meitou fog's soft fade reads.</summary>
        public Target2D? Soft, SoftFar;
        public bool Drawn;
        /// <summary>Drawn this frame but blended after the upscaler (<see cref="ParticlesAfterUpscale"/>), not yet.</summary>
        public bool Late;
        public PassTargets Targets = null!, TargetsFar = null!;
    }

    readonly LowTarget[] _lowTargets = [new(2), new(4)];

    LowTarget LowFor(int divisor) => divisor >= 4 ? _lowTargets[1] : _lowTargets[0];

    /// <summary>The render size in pixels of one world unit at distance 1 (vertical): what the particles use to tell big sprites from small ones; 0 when the Meitou <c>particles</c> switch is off.</summary>
    public float ParticlePixelsPerUnit(float projectionM22) => LowResParticles ? projectionM22 * _height * 0.5f : 0;

    /// <summary>Whether the weather's alpha and additive particles are drawn at a fraction of the render size (the Meitou <c>particles</c> switch).</summary>
    public bool LowResParticles => Options.LowResParticles && _haveScene;

    /// <summary>What the low-resolution particles of one size are drawn into (valid after <see cref="BeginParticlesLow"/>): the accumulation target, cleared, and its depth.</summary>
    public PassTargets ParticleTargets(int divisor) => LowFor(divisor).Targets;
    /// <summary>The second draw of the same particles: tested against each block's farthest depth (<see cref="PostProcessShaders.ParticleUpsample"/>).</summary>
    public PassTargets? ParticleTargetsFar(int divisor) => LowFor(divisor).TargetsFar;

    /// <summary>
    /// Prepares one low-resolution particle pass (docs/render-post.md "Colour, night and particles"): the target pair at 1/<paramref name="divisor"/>
    /// of the render size (made or remade when the size changed), the scene's depth reduced to it twice (the nearest and the farthest of each block, each as the
    /// depth of a depth-only rendering, for the two draws).
    /// Call after <see cref="RunFogVolumes"/> and before opening the host on <see cref="ParticleTargets"/>.
    /// </summary>
    public void BeginParticlesLow(int divisor)
    {
        var low = LowFor(divisor);
        int div = low.Divisor;
        int lw = Math.Max((_width + div - 1) / div, 1), lh = Math.Max((_height + div - 1) / div, 1);
        if (low.Accum is null || low.Accum.Width != lw || low.Accum.Height != lh)
        {
            foreach (var t in (ReadOnlySpan<Target2D?>)[low.Accum, low.Depth, low.Soft, low.AccumFar, low.DepthFar, low.SoftFar]) t?.Texture.Dispose();
            using var batch = Gpu.Uploads.Begin();
            low.Accum = Make(batch, lw, lh, InternalFormat.Rgba16F, TextureMinFilter.Linear, $"post particle accumulation 1/{div}");
            low.Depth = Make(batch, lw, lh, InternalFormat.DepthComponent24, TextureMinFilter.Nearest, $"post particle depth 1/{div}");
            low.Soft = Make(batch, lw, lh, InternalFormat.R32F, TextureMinFilter.Nearest, $"post particle soft depth 1/{div}");
            low.AccumFar = Make(batch, lw, lh, InternalFormat.Rgba16F, TextureMinFilter.Linear, $"post particle accumulation far 1/{div}");
            low.DepthFar = Make(batch, lw, lh, InternalFormat.DepthComponent24, TextureMinFilter.Nearest, $"post particle depth far 1/{div}");
            low.SoftFar = Make(batch, lw, lh, InternalFormat.R32F, TextureMinFilter.Nearest, $"post particle soft depth far 1/{div}");
        }
        CloseSegment();
        var d = _particleDepthPass;
        Bind(d.P, d.Depth, _sceneDepth);
        d.P.Set(d.FullSize, (float)_width, (float)_height);
        d.P.Set(d.Div, (float)div);
        d.P.Set(d.Planes, _planes.X, _planes.Y);
        foreach (var (layer, soft, depth) in (ReadOnlySpan<(int, Target2D, Target2D)>)[(1, low.Soft!, low.Depth!), (2, low.SoftFar!, low.DepthFar!)])
        {
            d.P.Set(d.Layer, layer);
            var depthTarget = depth.Attachment with { Load = LoadOp.Clear, DepthClear = DepthConvention.Clear };
            DrawPass(d.P, soft.Attachment with { Load = LoadOp.DontCare }, soft.Format, depthTarget,
                depth.Format, lw, lh, lw, lh, PassState with { DepthTest = true, DepthWrite = true, Compare = DepthFunction.Always });
            CloseSegment();
        }
        Stamp("particle depth");
        var targets = PassTargets.Of(low.Accum!.Texture, low.Depth!.Texture);
        low.Targets = targets with { Colour = targets.Colour with { Load = LoadOp.Clear, Clear = default } };
        var far = PassTargets.Of(low.AccumFar!.Texture, low.DepthFar!.Texture);
        low.TargetsFar = far with { Colour = far.Colour with { Load = LoadOp.Clear, Clear = default } };
    }

    Target2D? _softDepth;
    bool _softDepthReady;

    /// <summary>
    /// With the Meitou <c>fog</c> switch, copies the scene's depth into a colour target for the full-size particles' soft fade (they test against the depth
    /// itself, which a draw cannot read while it is attached). Call after <see cref="RunFogVolumes"/> and before the full-size particles.
    /// </summary>
    public void PrepareSoftParticleDepth()
    {
        if (!Options.MeitouFog || !_haveScene) return;
        if (_softDepth is null || _softDepth.Width != _width || _softDepth.Height != _height)
        {
            _softDepth?.Texture.Dispose();
            using var batch = Gpu.Uploads.Begin();
            _softDepth = Make(batch, _width, _height, InternalFormat.R32F, TextureMinFilter.Nearest, "post particle soft depth");
        }
        CloseSegment();
        Bind(_particleSoftDepthPass.P, _particleSoftDepthPass.Depth, _sceneDepth);
        Draw(_particleSoftDepthPass.P, _softDepth.Attachment with { Load = LoadOp.DontCare }, _softDepth.Format, _width, _height, _width, _height);
        CloseSegment();
        _softDepthReady = true;
    }

    /// <summary>The scene's depth for the particles of one size's soft fade (the Meitou <c>fog</c> switch), or null: Faithful, or not prepared this frame.</summary>
    public ParticleRenderer.SoftDepth? SoftParticleDepth(int divisor)
    {
        if (!Options.MeitouFog) return null;
        var t = divisor <= 1 ? (_softDepthReady ? _softDepth : null) : LowFor(divisor).Soft;
        return t is null ? null : new ParticleRenderer.SoftDepth(Sampled(t), _planes.X, _planes.Y);
    }

    /// <summary>The far draw's depth for its soft fade (<see cref="ParticleTargetsFar"/>), or null (Faithful).</summary>
    public ParticleRenderer.SoftDepth? SoftParticleDepthFar(int divisor) =>
        Options.MeitouFog && divisor > 1 && LowFor(divisor).SoftFar is { } t ? new ParticleRenderer.SoftDepth(Sampled(t), _planes.X, _planes.Y) : null;

    /// <summary>
    /// Whether the low-resolution particles are blended over the vendor upscaler's output instead of the scene it upscales (docs/render-post.md "Colour,
    /// night and particles"): DLSS took drifting see-through dust as standing still (its default preset ignores the reactive mask) and trailed it in rows
    /// of streaks. Their draws then use the unjittered projection (<see cref="ParticleRenderer"/>), the output being unjittered.
    /// </summary>
    public bool ParticlesAfterUpscale => LowResParticles && ExternalFor(Options.Upscale.Kind) is not null;

    /// <summary>The accumulation of one size over the scene colour (<see cref="PostProcessShaders.ParticleComposite"/>), after the host drew into <see cref="ParticleTargets"/>;
    /// with <see cref="ParticlesAfterUpscale"/> kept for <see cref="CompositeLateParticles"/>.</summary>
    public void CompositeParticlesLow(int divisor)
    {
        var low = LowFor(divisor);
        if (ParticlesAfterUpscale) { low.Drawn = low.Late = true; return; }
        CompositeLow(low, _sceneColour!, _width, _height);
        low.Drawn = true;
    }

    /// <summary>The low-resolution particles kept for after the upscaler, half size then quarter (the order the scene gets them in), over <paramref name="target"/>.</summary>
    void CompositeLateParticles(Target2D target, int targetWidth, int targetHeight)
    {
        foreach (var low in _lowTargets)
            if (low.Late) { CompositeLow(low, target, targetWidth, targetHeight); low.Late = false; }
    }

    void CompositeLow(LowTarget low, Target2D target, int targetWidth, int targetHeight)
    {
        var c = _particleCompositePass;
        Bind(c.P, c.Accum, low.Accum);
        Bind(c.P, c.LowDepth, low.Depth);
        Bind(c.P, c.AccumFar, low.AccumFar);
        Bind(c.P, c.LowDepthFar, low.DepthFar);
        Bind(c.P, c.Depth, _sceneDepth);
        c.P.Set(c.LowSize, (float)low.Accum!.Width, (float)low.Accum.Height);
        c.P.Set(c.NearPlanes, _planes.X, _planes.Y);
        c.P.Set(c.Div, (float)low.Divisor);
        c.P.Set(c.Scale, (float)_width / targetWidth);
        c.P.Set(c.Footprint, target == _sceneColour ? 0 : 1);   // over the upscaler's unjittered output: the jittered depth's footprint, not one texel
        Draw(c.P, target.Attachment, target.Format, targetWidth, targetHeight, targetWidth, targetHeight, ParticleLowState);
        CloseSegment();
    }

    /// <summary>Premultiplied "over": the accumulation's colour added, the scene kept by one minus its opacity; red, green and blue only (the alpha is the characters' mask).</summary>
    static readonly DrawState ParticleLowState = PassState with { Blend = new BlendState(true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha), ColourMask = DrawState.Rgb };

    void DrawFinal(LegacyProgram p, PassTargets final) =>
        Draw(p, final.Colour, final.Formats.Colour, final.Width, final.Height, _displayWidth, _displayHeight);

    Target2D _postColour = null!;

    /// <summary>The one projection the upscalers' depth is written for (camera near .. view distance, unjittered). Standard 0..1 depth, not the
    /// scene's reversed convention: nothing is drawn with it; the velocity pass re-encodes the scene depth into it and DLSS gets it as
    /// <c>ViewToClip</c> (depth not inverted).</summary>
    Matrix4x4 FullProjection(float fov, float aspectRatio) => ViewWindow.Current.Apply(Matrix4x4.CreatePerspectiveFieldOfView(fov, aspectRatio, UpscaleNear, UpscaleFar));

    /// <summary>
    /// The planes of the upscalers' depth: fixed, not the camera's (its near plane follows the eye's height above the ground and the far one the
    /// haze), because FSR decodes last frame's depth with this frame's planes: changing planes read as a disocclusion everywhere.
    /// </summary>
    public const float UpscaleNear = 1, UpscaleFar = 1_000_000;

    /// <summary>Whether the composite sharpens the upscaled picture (<see cref="UpscaleOptions.Sharpness"/>): after TAA and DLSS, which leave it soft.</summary>
    bool _sharpenAfter;

    /// <summary>Motion vectors, then the vendor upscaler or TAA into the history; returns the display-size picture.</summary>
    Target2D RunUpscale()
    {
        bool reset = !_historyValid || !_previousValid || Vector3.Distance(_eyeNow, _previousEye) > 5000;
        float dt = (float)_frameClock.Elapsed.TotalSeconds;
        _frameClock.Restart();
        // post-merge: the motion, the upscalers' depth and the water's reactivity in one dispatch (the vendor upscalers take the last two; TAA only the motion target).
        // The depth image is made with TAA too (one more R32F store per pixel; the kernel has the value already): frame generation takes motion and depth whatever upscales.
        var external = ExternalFor(Options.Upscale.Kind);
        bool merged = MergeVelocity && MergeSupported;
        PrepareSunMotion();
        if (merged) VelocityMerged(_motion!, _upscaleDepth!, external is null || WaterHeight is null ? null : _reactive!, cleanColour: external is not null);
        else { Velocity(_motion!, 0); if (external is null) Velocity(_upscaleDepth!, 1); }
        if (ObjectMotion is { } objectMotion)
        {
            // The guests' host (the grass's motion): the motion target loaded, red and green written, no depth, culling or blending.
            CloseSegment();
            var targets = PassTargets.Of(_motion!.Texture, null);
            var cmd = Gpu.BeginNative("object motion");
            cmd.BeginRendering(targets.Rendering);
            Gpu.BeginHostPass((CommandList)cmd, targets, DrawState.For(targets.Formats, Gpu.Device.DepthClamp,
                mask: ColorMask.R | ColorMask.G));
            var jitterNdc = new Vector2(2 * JitterPixels.X / _width, 2 * JitterPixels.Y / _height);
            objectMotion(new MotionTargets(Sampled(_sceneDepth!), _planes, jitterNdc, _height / (2 * MathF.Tan(_fovNow * 0.5f))));
            cmd.EndRendering();
            Gpu.EndHostPass(cmd);
            Gpu.EndNative(cmd);
        }
        if (CloudMotion is { } clouds && !reset) RunCloudMotion(clouds);
        if (_historyA is not null) (_historyA, _historyB) = (_historyB, _historyA);
        var output = _historyB!;
        bool done = false;
        _sharpenAfter = true;
        if (external is not null)
        {
            if (!merged) Velocity(_upscaleDepth!, 1);
            if (!merged && WaterHeight is not null) Velocity(_reactive!, 2);
            bool covered = Particles is { HasCoverage: true };
            if (covered) Stamp("velocity");
            if (covered) RunCoverage(_reactive!, ColorMask.R, clear: WaterHeight is null);
            var exposure = external.Kind == UpscalerKind.Dlss ? WriteUpscaleExposure() : null;
            CloseSegment();   // the vendor upscaler records its own segment
            if (covered) Stamp("particle coverage");
            done = external.Dispatch(new UpscaleInputs
            {
                Colour = _sceneColour!.Texture,
                Depth = _upscaleDepth!.Texture,
                Motion = _motion!.Texture,
                Output = output.Texture,
                Reactive = WaterHeight is null && !covered ? null : _reactive!.Texture,
                Exposure = exposure?.Texture,
                Preset = Options.Upscale.DlssPreset,
                RenderWidth = _width,
                RenderHeight = _height,
                DisplayWidth = _displayWidth,
                DisplayHeight = _displayHeight,
                JitterPixels = JitterPixels,
                Near = UpscaleNear,
                Far = UpscaleFar,
                FieldOfView = ViewWindow.FieldOfView(_fovNow),
                DeltaSeconds = Math.Clamp(dt, 0.001f, 0.25f),
                Sharpness = Options.Upscale.Sharpness,
                Reset = reset,
                ViewToClip = FullProjection(_fovNow, _aspectNow),
                ClipToPreviousClip = _previousValid ? Reprojection.ClipToPrevious(_viewRotation, _eyeNow, FullProjection(_fovNow, _aspectNow), _previousRotation, _previousEye, FullProjection(_previousFov, _previousAspect)) : Matrix4x4.Identity,
                Eye = _eyeNow,
                Right = new Vector3(_viewRotation.M11, _viewRotation.M21, _viewRotation.M31),
                Up = new Vector3(_viewRotation.M12, _viewRotation.M22, _viewRotation.M32),
                Forward = -new Vector3(_viewRotation.M13, _viewRotation.M23, _viewRotation.M33),
                Aspect = _aspectNow,
            });
            if (done) (ActiveUpscaler, _sharpenAfter) = (external.Name, external.Kind != UpscalerKind.Fsr);   // FSR sharpens itself (RCAS)
            else
            {
                Console.WriteLine($"upscaler  {external.Name} failed; using TAA from now on");
                _unavailable.Add(external.Kind);
                external.Dispose();
                External = null;
                _warnedFallback = true;
            }
        }
        if (!done)
        {
            if (Options.Upscale.Kind is UpscalerKind.Fsr or UpscalerKind.Dlss or UpscalerKind.MetalFx && !_warnedFallback)
            {
                Console.WriteLine($"upscaler  {Options.Upscale.Kind.ToString().ToUpperInvariant()} is not available here; using TAA");
                _warnedFallback = true;
            }
            // A vendor upscaler that failed this frame left its late particles undrawn: into the scene before the TAA (its output is its next history).
            CompositeLateParticles(_sceneColour!, _width, _height);
            // The particles' coverage into the motion target's alpha (the TAA's reactive channel; the water's analytic value is there already).
            if (Particles is { HasCoverage: true }) RunCoverage(_motion!, ColorMask.A, clear: false);
            if (_historyA is null)
            {
                // The TAA's first frame on these targets: the history it reads (nothing in it yet, so it starts over).
                using (var batch = Gpu.Uploads.Begin())
                    _historyA = Make(batch, _displayWidth, _displayHeight, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post taa history");
                reset = true;
            }
            var t = _taaPass;
            Bind(t.P, t.Colour, _sceneColour);
            Bind(t.P, t.Motion, _motion);
            Bind(t.P, t.History, _historyA);
            t.P.Set(t.RenderSize, (float)_width, (float)_height);
            t.P.Set(t.DisplaySize, (float)_displayWidth, (float)_displayHeight);
            t.P.Set(t.Jitter, JitterPixels.X, JitterPixels.Y);
            t.P.Set(t.Blend, 0.1f);
            t.P.Set(t.Reset, reset ? 1 : 0);
            Draw(t.P, output);
            ActiveUpscaler = _width == _displayWidth && _height == _displayHeight ? "taa" : $"taa {_width}x{_height}";
        }
        // The vendor upscaler's output (not a history Meitou keeps): the low-resolution particles over it, through their own temporal filter.
        CompositeLateParticlesFiltered(output, _displayWidth, _displayHeight, reset);
        _historyValid = true;
        // Frame generation takes the depth and the motion this frame made, whatever upscaled (TAA, FSR); the vendor segment is closed above or no segment is open.
        if (FrameGen is { Wanted: true } frameGen && _upscaleDepth is not null && _motion is not null)
        {
            CloseSegment();
            frameGen.Prepare(new Upscalers.FrameGenInputs
            {
                Depth = _upscaleDepth.Texture,
                Motion = _motion.Texture,
                RenderWidth = _width,
                RenderHeight = _height,
                JitterPixels = JitterPixels,
                Near = UpscaleNear,
                Far = UpscaleFar,
                FieldOfView = ViewWindow.FieldOfView(_fovNow),
                DeltaSeconds = Math.Clamp(dt, 0.001f, 0.25f),
                Reset = reset,
                Eye = _eyeNow,
                Right = new Vector3(_viewRotation.M11, _viewRotation.M21, _viewRotation.M31),
                Up = new Vector3(_viewRotation.M12, _viewRotation.M22, _viewRotation.M32),
                Forward = -new Vector3(_viewRotation.M13, _viewRotation.M23, _viewRotation.M33),
                Aspect = _aspectNow,
                ViewToClip = FullProjection(_fovNow, _aspectNow),
                ClipToPreviousClip = _previousValid ? Reprojection.ClipToPrevious(_viewRotation, _eyeNow, FullProjection(_fovNow, _aspectNow), _previousRotation, _previousEye,
                    FullProjection(_previousFov, _previousAspect)) : Matrix4x4.Identity,
            });
        }
        return output;
    }

    /// <summary>Frame generation (the Frame generation setting, FSR 3's or DLSS's, where it runs): the host sets it each frame; the chain hands it the depth and the motion.</summary>
    public Upscalers.IFrameGenerator? FrameGen { get; set; }

    /// <summary>The water plane's height when water is drawn (for the upscalers' reactive mask), else null.</summary>
    public float? WaterHeight { get; set; }
    /// <summary>How much the upscalers trust the current frame on water (FSR's reactive mask; TAA takes half).</summary>
    public const float WaterReactive = 0.5f;

    void Velocity(Target2D target, int mode)
    {
        var v = _velocityPass;
        Bind(v.P, v.NearDepth, _sceneDepth);
        v.P.Set(v.NearToPrev, _toPrevious);
        v.P.Set(v.NearPlanes, _planes.X, _planes.Y);
        v.P.Set(v.FullPlanes, UpscaleNear, UpscaleFar);
        v.P.Set(v.JitterNdc, 2 * JitterPixels.X / _width, 2 * JitterPixels.Y / _height);
        v.P.Set(v.Mode, mode);
        v.P.Set(v.FlipDepthY, Gpu.RenderTargetsBottomUp ? 0 : 1);
        float tanY = MathF.Tan(_fovNow * 0.5f);
        v.P.Set(v.Tan, ViewWindow.Tan(tanY * _aspectNow, tanY));
        var r = _viewRotation;
        v.P.Set(v.Right, r.M11, r.M21, r.M31);
        v.P.Set(v.Up, r.M12, r.M22, r.M32);
        v.P.Set(v.Back, ViewWindow.BackFor(r, _fovNow, _aspectNow));
        v.P.Set(v.EyeY, _eyeNow.Y);
        v.P.Set(v.WaterY, WaterHeight ?? float.MinValue);
        v.P.Set(v.WaterReactive, WaterReactive);
        v.P.Set(v.Sun, _sunMotion.Sun);
        v.P.Set(v.SunMotionUniform, _sunMotion.Motion);
        Draw(v.P, target);
    }

    /// <summary>The weather's particles, which add their coverage to the upscalers' reactive mask (<see cref="ParticleRenderer.DrawCoverage"/>) so thin fast ones (rain) keep their strength under the temporal history.</summary>
    public ParticleRenderer? Particles { get; set; }

    /// <summary>The particles' coverage into <paramref name="target"/>'s channels <paramref name="mask"/>, added to what is there (or onto zero when <paramref name="clear"/>).</summary>
    void RunCoverage(Target2D target, ColorMask mask, bool clear)
    {
        CloseSegment();
        // Tested against the scene's depth as the particles' colour is, so a fire behind a wall does not mark the wall.
        var targets = PassTargets.Of(target.Texture, _sceneDepth!.Texture);
        if (clear) targets = targets with { Colour = targets.Colour with { Load = LoadOp.Clear, Clear = default } };
        var cmd = Gpu.BeginNative("particle coverage");
        cmd.BeginRendering(targets.Rendering);
        var state = DrawState.For(targets.Formats, Gpu.Device.DepthClamp, depthTest: true, depthWrite: false,
            compare: DepthConvention.SceneCompare, mask: mask);
        Gpu.BeginHostPass((CommandList)cmd, targets, state);
        Particles!.DrawCoverage(mask, depthTest: true, SoftParticleDepth(1));
        cmd.EndRendering();
        Gpu.EndHostPass(cmd);
        Gpu.EndNative(cmd);
        foreach (var low in _lowTargets)
        {
            if (!low.Drawn || low.Late) continue;   // blended after the upscaler: not in what it sees
            // The low-resolution particles' share: from their accumulation, added to what is there (the full-size ones drew theirs above).
            var c = _particleCoveragePass;
            Bind(c.P, c.Accum, low.Accum);
            Bind(c.P, c.LowDepth, low.Depth);
            Bind(c.P, c.AccumFar, low.AccumFar);
            Bind(c.P, c.LowDepthFar, low.DepthFar);
            Bind(c.P, c.Depth, _sceneDepth);
            c.P.Set(c.LowSize, (float)low.Accum!.Width, (float)low.Accum.Height);
            c.P.Set(c.NearPlanes, _planes.X, _planes.Y);
            c.P.Set(c.Div, (float)low.Divisor);
            c.P.Set(c.FullSize, (float)_width, (float)_height);
            c.P.Set(c.Limit, mask == ColorMask.A ? 2f : 1f);   // the TAA takes half the motion target's alpha, so 2 is "all current frame"
            Draw(c.P, target.Attachment, target.Format, target.Width, target.Height, target.Width, target.Height,
                PassState with
                {
                    // The FSR/MetalFX reactive mask is a confidence in [0, 1]; overlap must not add past 1.
                    Blend = mask == ColorMask.R
                        ? new BlendState(true, BlendingFactor.One, BlendingFactor.One, BlendOperation.Max, BlendOperation.Max)
                        : new BlendState(true, BlendingFactor.One, BlendingFactor.One),
                    ColourMask = mask,
                });
        }
        CloseSegment();
    }

    /// <summary>Whether any character was drawn into the scene this frame (set by the world frame after the scene's characters): none means the scene colour's
    /// alpha, the SSAO's character mask, is 0 everywhere, so with <see cref="MaskSkip"/> the blur does not read it.</summary>
    public bool CharactersDrawn { get; set; } = true;
    /// <summary>Meitou: the SSAO blur's read of the character mask left out when no character was drawn (<c>--ab ssao-mask-skip</c>).</summary>
    public bool MaskSkip { get; set; } = true;

    static readonly bool SsaoStamps = Environment.GetEnvironmentVariable("MEITOU_SSAO_STAMPS") == "1";

    void RunSsao()
    {
        var a = _aoA!; var b = _aoB!;
        float tanY = MathF.Tan(_fovY * 0.5f);
        var s = _ssao;
        Bind(s.P, s.Depth, _sceneDepth);
        // Characters count less as occluders (the mask, as the blur's below): a body lying on the ground darkened a halo round it as wide as itself.
        bool characters = _sceneColour is not null && (CharactersDrawn || !MaskSkip);
        if (characters)
        {
            // The characters' mask at this half size first (PostProcessShaders.SsaoMask): the taps below read it instead of the scene colour.
            Bind(_ssaoMask.P, _ssaoMask.Scene, _sceneColour);
            Draw(_ssaoMask.P, _aoMask!);
        }
        Bind(s.P, s.Mask, characters ? _aoMask : null);
        s.P.Set(s.CharacterOccluder, characters ? PostOptions.SsaoCharacterOccluder : 1f);
        s.P.Set(s.Tan, ViewWindow.Tan(tanY * _aspect, tanY));
        s.P.Set(s.NearFar, _nearPlane, _farPlane);
        s.P.Set(s.Size, (float)_width, (float)_height);
        s.P.Set(s.Radius, PostOptions.SsaoRadius);
        s.P.Set(s.Strength, PostOptions.SsaoStrength);
        // Fade out with distance: the occlusion is a detail effect, and far geometry is hazy and has little depth precision.
        s.P.Set(s.FadeStart, PostOptions.SsaoFadeStart);
        s.P.Set(s.FadeEnd, PostOptions.SsaoFadeEnd);
        // The air in front of each surface (haze, weather fog, fog volumes) hides its occlusion: the shader evaluates it at the world position rebuilt from depth.
        var r = _viewRotation;
        s.P.Set(s.Right, r.M11, r.M21, r.M31);
        s.P.Set(s.Up, r.M12, r.M22, r.M32);
        s.P.Set(s.Back, ViewWindow.BackFor(r, _fovY, _aspect));
        s.P.Set(s.Eye, _eyeNow.X, _eyeNow.Y, _eyeNow.Z);
        s.P.Set(s.WaterY, WaterHeight ?? float.MinValue);   // the floor's occlusion fades under the water, which writes no depth
        s.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        Draw(s.P, a);
        if (SsaoStamps) Stamp("ssao gen");

        var bl = _blur;
        Bind(bl.P, bl.Ao, a);
        Bind(bl.P, bl.Mask, null);
        bl.P.Set(bl.Step, 1f / a.Width, 0f);
        bl.P.Set(bl.CharacterAo, 1f);
        Draw(bl.P, b);
        if (SsaoStamps) Stamp("ssao blur x");
        // The second pass also takes the occlusion off the characters' pixels (the scene colour's alpha; only they write it), by SsaoCharacterStrength.
        bool mask = _sceneColour is not null && (CharactersDrawn || !MaskSkip);
        Bind(bl.P, bl.Ao, b);
        Bind(bl.P, bl.Mask, mask ? _sceneColour : null);
        bl.P.Set(bl.Step, 0f, 1f / a.Height);
        bl.P.Set(bl.CharacterAo, mask ? PostOptions.SsaoCharacterStrength : 1f);
        Draw(bl.P, a);
        if (SsaoStamps) Stamp("ssao blur y");
        // The composite reads aoB: swap so the finished result is there.
        (_aoA, _aoB) = (_aoB, _aoA);
    }

    // ---- heat haze ----

    /// <summary>The game's shared <c>heatHaze</c> this frame (the caller moves it towards the weather's target, <see cref="Meitou.Data.World.HeatHaze"/>); 0 skips the pass.</summary>
    public float HeatHazeAmount { get; set; }
    /// <summary>The game's <c>gameTime</c>: game hours since the load, which the haze's layers cycle on (× 100).</summary>
    public double HeatHazeHours { get; set; }
    /// <summary>The game's far clip D, which its G-buffer depth is divided by (view distance × 10 = 50000; docs/formats/sky.md).</summary>
    public float HeatHazeFarClip { get; set; } = 10 * Data.World.HeatHaze.ViewDistanceSetting;
    /// <summary>Whether the heat-haze textures were found (else the pass never runs).</summary>
    public bool HasHeatHaze => _flowTexture is not null && _perturbationTexture is not null;
    /// <summary>Whether this frame ends with the heat haze: it is on, has its textures and an amount.</summary>
    public bool HeatHazeRuns => HeatHazeWanted && _ldr is not null;
    bool HeatHazeWanted => Options.HeatHaze && HasHeatHaze && HeatHazeAmount * Options.HeatHazeScale > 0;

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
            _flowTexture = HazeTexture(Data.Textures.DdsReader.ReadFile(flow), "post heat haze flow");
            _perturbationTexture = HazeTexture(Data.Textures.DdsReader.ReadFile(perturbation), "post heat haze perturbation");
        }
        catch (Exception e) when (e is Data.Textures.DdsFormatException or IOException)
        {
            Console.WriteLine($"warning   heat haze textures: {e.Message}");
        }
    }

    /// <summary>The file's levels as they are (BC1 blocks), or decoded to RGBA8 for another format; every level the file has.</summary>
    GpuTexture HazeTexture(Data.Textures.DdsFile dds, string name)
    {
        bool bc1 = dds.Format == Data.Textures.DdsFormat.Bc1;
        var top = dds.Surface(0, 0);
        var format = bc1 ? InternalFormat.CompressedRgbaS3TcDxt1Ext : InternalFormat.Rgba8;
        using var batch = Gpu.Uploads.Begin();
        var texture = batch.Create(new TextureDesc(format, top.Width, top.Height, dds.MipCount, Name: name));
        for (int level = 0; level < dds.MipCount; level++)
        {
            if (bc1)
            {
                var s = dds.Surface(0, level);
                batch.Write(texture, level, 0, new PixelRect(0, 0, (uint)s.Width, (uint)s.Height), dds.Data.AsSpan(s.Offset, s.Length));
            }
            else
            {
                var img = Data.Textures.DdsDecoder.Decode(dds, 0, level);
                batch.Write(texture, level, 0, new PixelRect(0, 0, (uint)img.Width, (uint)img.Height), img.Pixels);
            }
        }
        return texture;
    }

    /// <summary>The game's heat haze from <paramref name="source"/> (the finished LDR picture) into <see cref="Target"/>.</summary>
    void RunHeatHaze(Target2D source, PassTargets final)
    {
        // The upscaler's mip bias is for the scene's textures; the haze's maps are sampled as the game does.
        Gpu.LodBias = 0;
        var h = _hazePass;
        // GL's state for them: trilinear, anisotropy 16 (Ogre's default in the game), repeating.
        if (h.Flow.IsValid) h.P.Bind(h.Flow, Sampled(_flowTexture!, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.Repeat, 16));
        if (h.Perturbation.IsValid) h.P.Bind(h.Perturbation, Sampled(_perturbationTexture!, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.Repeat, 16));
        Bind(h.P, h.Depth, _sceneDepth);
        Bind(h.P, h.Image, source);
        // The phase in double first: game hours × 100 loses its fraction in float after long sessions.
        double phase = HeatHazeHours * 100;
        h.P.Set(h.Phase, (float)(phase - Math.Floor(phase)));
        h.P.Set(h.Amount, HeatHazeAmount * Options.HeatHazeScale);
        float tanY = MathF.Tan(_fovY * 0.5f);
        h.P.Set(h.Tan, ViewWindow.Tan(tanY * _aspect, tanY));
        h.P.Set(h.NearFar, _nearPlane, _farPlane);
        h.P.Set(h.FarClip, HeatHazeFarClip);
        h.P.Set(h.HasDepth, _haveScene ? 1 : 0);
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

    float? _exposureHold;
    int _exposureHoldFrames;

    /// <summary>Forgets what the temporal passes kept (the upscaler's history, the shafts' history, the previous camera): the next frame starts afresh. A tiled screenshot calls it between tiles.</summary>
    public void ResetHistory() => (_historyValid, _previousValid, _shaftHistoryValid) = (false, false, false);

    /// <summary>
    /// Pins the adapted luminance to <paramref name="adapted"/> for the next <paramref name="frames"/> exposure passes (the band collapses to it), so a picture drawn at another size
    /// (the size change drops the adaptation history) keeps the exposure of the live view: the original's tiled screenshot zeroes the adaptation rate for the same reason.
    /// </summary>
    public void HoldExposure(float adapted, int frames) => (_exposureHold, _exposureHoldFrames) = (adapted, frames);

    (float Min, float Max) ExposureBand()
    {
        if (_exposureHold is not { } held) return AutoExposure!.Value;
        if (--_exposureHoldFrames <= 0) _exposureHold = null;
        return (held, held);
    }

    /// <summary>The last frame's adapted luminance and measured mean (the composite's scale is 0.55 / adapted; the mean is smoothed at the same rate, without the band, and is the raw mean when adaptation is instant); waits for the GPU and reads it
    /// back (<see cref="GpuContext.ReadBack"/>), so for reports only, outside a frame.</summary>
    public (float Adapted, float Mean) ReadExposure()
    {
        if (_adaptB is null || !_adaptedValid) return (float.NaN, float.NaN);
        var v = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(Gpu.ReadBack(_adaptB.Texture, 8));   // RG32F
        return (v[0], v[1]);
    }

    void RunExposure()
    {
        if (MergeExposure && MergeSupported)
        {
            float mdt = (float)_adaptClock.Elapsed.TotalSeconds;
            _adaptClock.Restart();
            // Seed both ping-pong targets before smoothing; the next frame reads the other one.
            float mblend = InstantAdaptation || !_adaptedValid || _adaptedFrames < 2 ? 1 : 1 - MathF.Exp(-mdt * AdaptationRate);
            (_adaptA, _adaptB) = (_adaptB, _adaptA);
            ExposureMerged(mblend, ExposureBand());
            _adaptedValid = true;
            _adaptedFrames = Math.Min(_adaptedFrames + 1, 2);
            return;
        }
        var lum = _luminance!;
        var lp = _luminancePass;
        Bind(lp.P, lp.Scene, _postColour);
        lp.P.Set(lp.Cell, 1f / LuminanceSize, 1f / LuminanceSize);
        Draw(lp.P, lum);
        Segment().GenerateMips(lum.Texture);   // VkGl's GenerateMipmap: linear blits level by level, full barriers around
        float dt = (float)_adaptClock.Elapsed.TotalSeconds;
        _adaptClock.Restart();
        // Seed both ping-pong targets before smoothing; the next frame reads the other one.
        float blend = InstantAdaptation || !_adaptedValid || _adaptedFrames < 2 ? 1 : 1 - MathF.Exp(-dt * AdaptationRate);
        var previous = _adaptB;   // the target written last frame; the swap below makes it adaptA
        (_adaptA, _adaptB) = (_adaptB, _adaptA);
        var ap = _adaptPass;
        Bind(ap.P, ap.Luminance, lum);
        Bind(ap.P, ap.Last, previous);
        ap.P.Set(ap.Level, MathF.Log2(LuminanceSize));
        ap.P.Set(ap.Blend, blend);
        var band = ExposureBand();
        ap.P.Set(ap.Band, band.Min, band.Max);
        Draw(ap.P, _adaptB!);
        _adaptedValid = true;
        _adaptedFrames = Math.Min(_adaptedFrames + 1, 2);
    }

    public void Dispose()
    {
        External?.Dispose();
        Free();
        DisposeShadingRate();
        DisposeShafts();
        DisposeSceneLayers();
        DisposeLateParticles();
        DisposeMerged();
        DisposeUpscaleExposure();
        DisposeBloom();
        Hiz?.Dispose();
        foreach (var p in new FullscreenProgram[] { _ssao, _ssaoMask, _blur, _luminancePass, _adaptPass, _compositePass, _fxaaPass, _hazePass, _velocityPass, _cloudMotionPass, _taaPass, _fogPass, _particleDepthPass, _particleSoftDepthPass, _particleCompositePass, _particleCoveragePass }) p.P.Dispose();
        _smaaPasses.Dispose();
        _giResolvePass?.P.Dispose();
        _giApplyPass?.P.Dispose();
        _aoApplyPass?.P.Dispose();
        _flowTexture?.Dispose();
        _perturbationTexture?.Dispose();
    }
}