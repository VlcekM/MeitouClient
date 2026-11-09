using System.Numerics;
using Meitou.Game.Rendering.Objects;
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
    /// Updates the probes for the eye at <paramref name="eye"/> from the terrain heights, the objects, the lamps and the fog volumes.
    /// </summary>
    void Update(Vector3 eye, HeightSnapshot heights, WorldObjectRenderer? objects, WorldLamps? lamps, FogVolumes? fog);

    /// <summary>
    /// A line describing the probes' layout and state, for the log.
    /// </summary>
    string Describe();
}