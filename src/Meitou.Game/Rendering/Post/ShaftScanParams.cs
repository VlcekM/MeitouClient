using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The light shafts' scan kernel's uniform block (<see cref="Sky.LightShaftShaders.Scan"/>, <see cref="Sky.LightShaftShaders.ScanImages"/>; std140:
/// every member takes a 16-byte slot), shared by the Vulkan chain (<see cref="LightShafts"/>) and the Metal one (<see cref="PostComposite"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct ShaftScanParams
{
    /// <summary>
    /// The block's members in order, each a vec4.
    /// </summary>
    public Vector4 Grid, Range, Eye, Right, Up, Back, Tan, Air, AtmoParams, AtmoSimple, AtmoHaze, AtmoAltitude, AtmoFog, AtmoSun, SunLight, AtmoMaps;

    /// <summary>
    /// The block for one frame: the grid and its slices, the camera's axes (<paramref name="up"/> as the passes that rebuild a ray take it), the
    /// half-fov tangents, the air layer (<paramref name="air"/>, default without it) and the atmosphere's frame globals.
    /// </summary>
    public static ShaftScanParams For(FrameGlobals g, Vector4 grid, Vector4 range, Vector3 eye, Vector3 right, Vector3 up, Vector3 back,
        Vector2 tan, Vector4 air)
    {
        var sunLight = ReadGlobal<Vector3>(g, "uAtmoSunLight");
        return new ShaftScanParams
        {
            Grid = grid,
            Range = range,
            Eye = new Vector4(eye, 0),
            Right = new Vector4(right, 0),
            Up = new Vector4(up, 0),
            Back = new Vector4(back, 0),
            Tan = new Vector4(tan, 0, 0),
            Air = air,
            AtmoParams = ReadGlobal<Vector4>(g, "uAtmoParams"),
            AtmoSimple = ReadGlobal<Vector4>(g, "uAtmoSimple"),
            AtmoHaze = ReadGlobal<Vector4>(g, "uAtmoHaze"),
            AtmoAltitude = ReadGlobal<Vector4>(g, "uAtmoAltitude"),
            AtmoFog = ReadGlobal<Vector4>(g, "uAtmoFog"),
            AtmoSun = ReadGlobal<Vector4>(g, "uAtmoSun"),
            SunLight = new Vector4(sunLight, ReadGlobal<Vector4>(g, "uAtmoLight").W),
            AtmoMaps = ReadGlobal<Vector4>(g, "uAtmoMaps"),
        };
    }

    /// <summary>
    /// A frame global's value as <typeparamref name="T"/> (default when it is not set).
    /// </summary>
    static T ReadGlobal<T>(FrameGlobals g, string name) where T : unmanaged
    {
        T value = default;
        g.UniformValue(name)?.TryRead(MemoryMarshal.AsBytes(new Span<T>(ref value)));
        return value;
    }
}
