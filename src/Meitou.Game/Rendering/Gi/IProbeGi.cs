using System.Numerics;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// A backend-owned ray-traced probe GI. Vulkan's <see cref="GiProbes"/> and Metal's native implementation share the
/// world shaders' published globals, but their scene and update machinery are backend-specific.
/// </summary>
internal interface IProbeGi : IDisposable
{
    /// <summary>
    /// Whether the world shaders take their ambient from the probes (the <c>gi</c> switch).
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    /// False: indoor surfaces keep the original ambient while outdoor surfaces still use GI.
    /// </summary>
    bool InteriorEnabled { get; set; }

    /// <summary>
    /// False while the frame does not update the probes (the bench's gi-scene switch): the shaders then do not read them.
    /// </summary>
    bool Active { get; set; }

    /// <summary>
    /// False: the probes are updated but the world's shading does not read them (the bench's gi-shade switch, the reads' cost).
    /// </summary>
    bool Shade { get; set; }

    /// <summary>
    /// How much the probes replace the sky's light (1: fully, 0: not at all; <c>--gi-strength</c>).
    /// </summary>
    float Strength { get; set; }

    /// <summary>
    /// True: the lit fragments write their surface and the post chain adds the probes' light (the GI resolve, docs/render-gi.md "Resolve").
    /// </summary>
    bool Resolve { get; set; }

    /// <summary>
    /// True: the probes move out of geometry (the <c>gihybrid</c> switch's relocation; needs <see cref="Resolve"/>).
    /// </summary>
    bool Relocate { get; set; }

    /// <summary>
    /// True while the main view's opaque scene draws with the traced sun: the lit fragments leave the sun out and write it apart (<c>uGiParams.z</c>
    /// bit 4; docs/render-shadows.md "Traced sun").
    /// </summary>
    bool Traced { get; set; }

    /// <summary>
    /// The traced scene holds the sun's casters (while the sun is traced): the objects to <paramref name="range"/>, the small foliage by its size on
    /// screen at <paramref name="pixelsPerRadian"/>.
    /// </summary>
    void SetCasters(bool casters, float range, float pixelsPerRadian);

    /// <summary>
    /// Whether the traced scene is ready for the sun's rays this frame.
    /// </summary>
    bool TracedReady { get; }

    /// <summary>
    /// NDC, as the frame's projection jittered it, to the position relative to the eye (the traced shadows' reconstruction).
    /// </summary>
    Matrix4x4 SceneToEye { get; set; }

    /// <summary>
    /// The traced sun over the opaque scene of <paramref name="post"/> (its pass closed): the rays, the penumbra and the sun they let through added.
    /// </summary>
    void TraceSun(IWorldPostChain post, Vector3 toSun);

    /// <summary>
    /// The shadow pass's sun map for what draws no traced pixel (<see cref="ShadowPass.TracedVolume"/>), after the traced scene's update.
    /// </summary>
    void TraceSunVolume(ShadowPass shadow);

    /// <summary>
    /// Rays each probe traces per update (the "GI 64 probe rays" row).
    /// </summary>
    int Rays { get; set; }

    /// <summary>
    /// The cloud transmission dims the sun at the probes' hits (the "GI cloud shadows" row).
    /// </summary>
    bool CloudShadows { get; set; }

    /// <summary>
    /// Updates the traced scene and the probes for the eye at <paramref name="eye"/> from the terrain heights, the objects, the foliage, the lamps and
    /// the fog volumes; the first cascade is the interior grid round <paramref name="interiorFocus"/> when set.
    /// </summary>
    void Update(Vector3 eye, HeightSnapshot heights, WorldObjectRenderer? objects, FoliageRenderer? foliage, WorldLamps? lamps, FogVolumes? fog,
        Vector3? interiorFocus);

    /// <summary>
    /// A line describing the probes' layout and state, for the log.
    /// </summary>
    string Describe();
}