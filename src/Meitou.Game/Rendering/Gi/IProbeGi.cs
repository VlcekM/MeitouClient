using System.Numerics;

namespace Meitou.Rendering.Gi;

/// <summary>
/// A backend-owned ray-traced probe GI. Vulkan's <see cref="GiProbes"/> and Metal's native implementation share the
/// world shaders' published globals, but their scene and update machinery are backend-specific.
/// </summary>
internal interface IProbeGi : IDisposable
{
    bool Enabled { get; set; }
    bool InteriorEnabled { get; set; }
    bool Active { get; set; }
    bool Shade { get; set; }
    float Strength { get; set; }
    void Update(Vector3 eye, HeightSnapshot heights, WorldObjectRenderer? objects, WorldLamps? lamps, FogVolumes? fog);
    string Describe();
}
