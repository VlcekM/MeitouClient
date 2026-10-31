using System.Numerics;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>What <see cref="IWorldPostChain.ObjectMotion"/> gets: the near slice's depth sampler, its planes, the jitter in NDC,
/// and the render height over twice the tangent of half the field of view (pixels per unit at distance 1; 0: unknown).</summary>
public readonly record struct MotionTargets(SampledTexture NearDepth, Vector2 NearPlanes, Vector2 JitterNdc, float FocalPixels = 0);

/// <summary>
/// The frame's post chain as the shared world draw sees it. <see cref="PostProcess"/> is the Vulkan implementation; a backend
/// whose post chain is a smaller seam slice (Metal's <c>PostComposite</c> + <c>PostTaa</c>) implements the same surface, so
/// <see cref="WorldFrame.Draw"/> owns the frame instead of each host keeping a copy of its flow.
/// </summary>
interface IWorldPostChain
{
    /// <summary>The context the chain records through (the scene host's backend).</summary>
    IGpuContext Context { get; }

    /// <summary>The post options this chain reads (tone map, SSAO, shafts, particles, upscaler).</summary>
    PostOptions Options { get; }

    /// <summary>The finished picture target (set by the host before <see cref="End"/>).</summary>
    GpuTexture? Target { get; set; }

    /// <summary>The scene colour and depth targets the host pass draws into.</summary>
    PassTargets SceneTargets { get; }
    /// <summary>The scene targets for the full-size particle pass. GI's albedo target is not needed after its resolve; backends
    /// that made the scene pipeline without that extra attachment return the pair without it.</summary>
    PassTargets SceneTargetsForParticles { get; }
    GpuTexture SceneColour { get; }
    GpuTexture SceneDepth { get; }

    /// <summary>The scene render size after the upscaler's render scale, and the jitter the scene projection uses this frame.</summary>
    int RenderWidth { get; }
    int RenderHeight { get; }
    Vector2 JitterPixels { get; }

    /// <summary>Whether frames are jittered and reconstructed over time (a temporal upscaler).</summary>
    bool Temporal { get; }

    /// <summary>Whether the chain has separate far and near depth slices (Vulkan's 24-bit depth precision); a backend with one
    /// scene depth target returns false and <see cref="WorldFrame"/> draws its single slice.</summary>
    bool DepthSlices { get; }

    /// <summary>Still frames snap adaptation and weather timers.</summary>
    bool InstantAdaptation { get; set; }

    /// <summary>The GI resolve divisor (0: off); a backend without GI ignores it.</summary>
    int GiResolve { get; set; }

    /// <summary>The sky's exposure band, read by the composite's exposure adaptation.</summary>
    (float Min, float Max)? AutoExposure { get; set; }

    /// <summary>The water plane the fog volumes and SSAO reconstruct against (null: no water).</summary>
    float? WaterHeight { get; set; }

    /// <summary>Whether the near slice drew characters (the SSAO character mask).</summary>
    bool CharactersDrawn { get; set; }

    /// <summary>The game's heat haze this frame: its amount, animation hour and far clip.</summary>
    float HeatHazeAmount { get; set; }
    double HeatHazeHours { get; set; }
    float HeatHazeFarClip { get; set; }

    /// <summary>Moving geometry's own motion over the camera's (the grass and characters), called after the velocity pass with
    /// the motion target bound; null when nothing is hooked.</summary>
    Action<MotionTargets>? ObjectMotion { get; set; }

    /// <summary>Whether the chain draws big weather particles into low-resolution targets (Vulkan's Meitou path).</summary>
    bool LowResParticles { get; }

    /// <summary>The frame's particles, whose coverage feeds the temporal upscaler's reactive mask. Set before <see cref="End"/>.</summary>
    ParticleRenderer? Particles { get; set; }

    /// <summary>Begins a frame at the display size; the scene size comes from the chain's render scale.</summary>
    void Begin(int width, int height);

    /// <summary>The main camera, before the slices, for the exposure, fog volumes, shafts and reprojection.</summary>
    void SetCamera(Vector3 eye, Matrix4x4 view, float fieldOfView, float aspect);

    /// <summary>The near depth slice's planes and projection (the depth buffer left at the end of the frame).</summary>
    void SetNearSlice(float near, float far, float fieldOfView, float aspect);

    /// <summary>Before drawing the far slice (Vulkan gives it its own depth target); no-op without <see cref="DepthSlices"/>.</summary>
    void BeginFarSlice(float near, float far);

    /// <summary>Before drawing the near slice (the caller clears the depth after this).</summary>
    void BeginNearSlice(float near, float far);

    /// <summary>The scene targets with the previous frame's fog shading rate, where the backend has one.</summary>
    PassTargets WithShadingRate(PassTargets targets);

    /// <summary>The previous frame's depth pyramid for the foliage cull (default: none).</summary>
    OcclusionView OcclusionFor(Vector3 eye);

    /// <summary>Resolves the GI probes into the scene (no-op without GI).</summary>
    void RunGiResolve();

    /// <summary>Darkens the haze where the sun is shadowed along the view, before the fog volumes.</summary>
    void RunLightShafts(float darkening, float far, float groundY, float airByTime = 1);

    /// <summary>The placed fog volumes over the finished scene; <see cref="ApplySsao"/> draws them and the occlusion.</summary>
    void RunFogVolumes(bool volumes);

    /// <summary>Builds the next frame's fog shading rate from this frame's depth (no-op without the backend feature).</summary>
    void BuildShadingRate(Vector3 eye);

    /// <summary>Applies the pending fog volumes and the scene's SSAO before the particles.</summary>
    void ApplySsao();

    /// <summary>The render size in pixels of one world unit at distance 1; 0 disables the low-resolution particle split.</summary>
    float ParticlePixelsPerUnit(float projectionM22);

    /// <summary>The low-resolution particle pass's targets, prepared by <see cref="BeginParticlesLow"/>.</summary>
    PassTargets ParticleTargets(int divisor);
    void BeginParticlesLow(int divisor);
    void CompositeParticlesLow(int divisor);
    /// <summary>Whether the low-resolution particles are blended after the upscaler (their draws then unjittered); a chain without it blends them into the scene.</summary>
    bool ParticlesAfterUpscale => false;
    /// <summary>This frame's volumetric clouds for the motion vectors (null: none; a chain without the pass keeps the sky's motion).</summary>
    void SetCloudMotion(CloudMotion? clouds) { }
    /// <summary>The Meitou fog's soft fade: the scene's depth copied for the full-size particles (nothing where the chain has no copy).</summary>
    void PrepareSoftParticleDepth() { }
    /// <summary>The scene's depth for one particle size's soft fade, or null (Faithful, or a chain without it: the sprites stay hard-edged).</summary>
    ParticleRenderer.SoftDepth? SoftParticleDepth(int divisor) => null;
    void MarkParticleDraws();
    void MarkParticles();

    /// <summary>Runs the chain's remaining passes into <see cref="Target"/>.</summary>
    void End();
}
