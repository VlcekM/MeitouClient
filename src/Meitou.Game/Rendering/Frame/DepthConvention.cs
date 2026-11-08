using System.Numerics;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The scene depth's convention, in one place (docs/renderer-native.md): clip depth is 0..1 on both backends (no GL mapping), and on
/// Vulkan the scene depth is reversed (cleared to 0, the near plane at 1, compared with GREATER/GEQUAL, far = the view distance), which
/// keeps a 32-bit float buffer's precision even from a few units out to the horizon. Metal stays conventional (cleared to 1, near at 0).
/// Only scene targets follow it (the scene, the reflection, the hover mask, the markers and the depths derived from the scene's);
/// shadow maps, previews, icons and bakes keep their own conventional depth. <see cref="Glsl"/> is the shaders' twin.
/// </summary>
public static class DepthConvention
{
    /// <summary>
    /// Whether the scene depth is reversed. True by default (this process's backend is Vulkan); only <c>MetalContext</c>'s constructor
    /// sets it false, in the macOS process. Nothing else may set it.
    /// </summary>
    public static bool Reversed { get; set; } = true;

    /// <summary>The cleared scene depth: no surface, the sky (0 reversed, 1 conventional).</summary>
    public static float Clear => Reversed ? 0f : 1f;

    /// <summary>The scene compare op for a GL-semantics one: Less/Greater and Lequal/Gequal swap when reversed; Equal, Notequal, Always
    /// and Never are symmetric.</summary>
    public static DepthFunction Compare(DepthFunction gl) => !Reversed ? gl : gl switch
    {
        DepthFunction.Less => DepthFunction.Greater,
        DepthFunction.Greater => DepthFunction.Less,
        DepthFunction.Lequal => DepthFunction.Gequal,
        DepthFunction.Gequal => DepthFunction.Lequal,
        _ => gl,
    };

    /// <summary>The scene's compare op: less-or-equal in GL terms.</summary>
    public static DepthFunction SceneCompare => Compare(DepthFunction.Lequal);

    /// <summary>Depth bias (constant, slope factor) that pulls a draw <paramref name="units"/> toward the viewer: <c>(+u, +u)</c> reversed,
    /// <c>(−u, −u)</c> conventional.</summary>
    public static (float Constant, float Slope) TowardViewer(float units) => Reversed ? (units, units) : (-units, -units);

    /// <summary>
    /// <paramref name="m"/> with clip z replaced by w − z (column 3 := column 4 − column 3; row vectors, <c>clip = v * M</c>): a standard
    /// 0..1 projection's near plane goes to depth 1 and its far plane to 0. A linear map of clip z, so apply it last (after the jitter
    /// and an oblique clip plane).
    /// </summary>
    public static Matrix4x4 Reverse(Matrix4x4 m)
    {
        m.M13 = m.M14 - m.M13;
        m.M23 = m.M24 - m.M23;
        m.M33 = m.M34 - m.M33;
        m.M43 = m.M44 - m.M43;
        return m;
    }

    /// <summary>A standard 0..1 projection in this backend's scene convention (<see cref="Reverse"/> when <see cref="Reversed"/>).</summary>
    public static Matrix4x4 Apply(Matrix4x4 standard) => Reversed ? Reverse(standard) : standard;

    /// <summary>The scene's perspective projection: <see cref="Matrix4x4.CreatePerspectiveFieldOfView"/> in this backend's convention.
    /// Reversed: depth <c>n (f − z) / ((f − n) z)</c>, 1 at the near plane, 0 at the far one.</summary>
    public static Matrix4x4 Perspective(float fieldOfView, float aspect, float near, float far) =>
        Apply(Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, aspect, near, far));

    /// <summary>The clip z of the near plane in the scene convention (1 reversed, 0 conventional).</summary>
    public static float NearZ => Reversed ? 1f : 0f;

    /// <summary>The clip z of the far plane in the scene convention (0 reversed, 1 conventional).</summary>
    public static float FarZ => Reversed ? 0f : 1f;

    /// <summary>View distance along the axis of a stored depth <paramref name="d"/> drawn with planes <paramref name="near"/>, <paramref name="far"/>.</summary>
    public static float ViewZ(float d, float near, float far) =>
        Reversed ? near * far / (near + d * (far - near)) : near * far / (far - d * (far - near));

    /// <summary>The stored depth of view distance <paramref name="z"/> (the inverse of <see cref="ViewZ"/>).</summary>
    public static float DepthOf(float z, float near, float far) =>
        Reversed ? near * (far - z) / ((far - near) * z) : far * (z - near) / (z * (far - near));

    /// <summary>Whether a stored depth is the cleared value (no surface: the sky).</summary>
    public static bool IsSky(float d) => Reversed ? d <= 0f : d >= 1f;

    /// <summary>View-depth error of one float ulp of the stored depth at view distance <paramref name="z"/>.</summary>
    public static float Precision(float z, float near) => Reversed ? 1.2e-7f * z : 1.2e-7f * z * z / MathF.Max(near, 1e-3f);

    /// <summary>
    /// The GLSL twin of this class, pasted into a shader after its uniforms (<c>+ DepthConvention.Glsl +</c>); compiles under
    /// <c>#version 330 core</c> and <c>450</c>, guarded so a second paste is harmless. <c>MEITOU_DEPTH_REVERSED</c> is defined by the Vulkan
    /// shader compiler (<c>ShaderCompileOptions.DepthReversed</c>), absent on Metal. <c>nf</c> = (near, far) of the projection the depth
    /// was drawn with.
    /// </summary>
    public const string Glsl = "\n" + """
        #ifndef MEITOU_DEPTH_CONVENTION
        #define MEITOU_DEPTH_CONVENTION 1
        #ifdef MEITOU_DEPTH_REVERSED
        const float DEPTH_SKY = 0.0;        // the cleared value: no surface
        const float DEPTH_NEAREST = 1.0;    // the near plane
        bool depthIsSky(float d) { return d <= 0.0; }
        bool depthNearerThan(float a, float b) { return a > b; }
        float depthNearer(float a, float b) { return max(a, b); }
        float depthFarther(float a, float b) { return min(a, b); }
        float depthViewZ(float d, vec2 nf) { return nf.x * nf.y / (nf.x + d * (nf.y - nf.x)); }
        float depthOf(float z, vec2 nf) { return nf.x * (nf.y - z) / ((nf.y - nf.x) * z); }
        float depthStep(float z, vec2 nf) { return 1.2e-7 * z; }   // view-depth error of one float ulp at z
        #else
        const float DEPTH_SKY = 1.0;
        const float DEPTH_NEAREST = 0.0;
        bool depthIsSky(float d) { return d >= 1.0; }
        bool depthNearerThan(float a, float b) { return a < b; }
        float depthNearer(float a, float b) { return min(a, b); }
        float depthFarther(float a, float b) { return max(a, b); }
        float depthViewZ(float d, vec2 nf) { return nf.x * nf.y / (nf.y - d * (nf.y - nf.x)); }
        float depthOf(float z, vec2 nf) { return nf.y * (z - nf.x) / (z * (nf.y - nf.x)); }
        float depthStep(float z, vec2 nf) { return 1.2e-7 * z * z / max(nf.x, 1e-3); }
        #endif
        #endif
        """ + "\n";
}
