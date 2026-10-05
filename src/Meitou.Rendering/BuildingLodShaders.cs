using System.Text.RegularExpressions;

namespace Meitou.Rendering;

/// <summary>
/// The world's object shaders: the viewer's shared mesh shaders (<see cref="Shaders"/>, so fog, lighting and whatever is
/// added to them follow) given a per-instance model matrix (attributes 7 to 10, divisor 1) and a dithered cross-fade.
/// The patches are made on the source text at run time and fail loudly when an anchor has moved.
/// </summary>
/// <remarks>
/// Instance layout: the four rows of the System.Numerics matrix (the column-vector form GL reads when the matrix is uploaded
/// whole), whose last column is constant, so row 0's w and row 1's w carry <c>lo</c> and <c>hi</c>: a fragment is kept when
/// its dither value <c>d</c> in [0, 1) satisfies <c>lo &lt;= d &lt; hi</c>. A fully visible instance has lo 0 and hi 2.
/// Two draws of the two LOD levels of one instance split [0, 1) between them, so they never both cover a pixel.
/// With <c>uFadeMode</c> 1 (distant towns) hi comes per vertex from the distance to <c>uFadeEye</c> instead.
/// </remarks>
static class BuildingLodShaders
{
    public const int InstanceLocation = 7;

    public static string Vertex()
    {
        string v = Shaders.MeshVertex;
        v = Replace(v, @"uniform\s+mat4\s+uModel\s*;", """
            layout(location = 7) in vec4 aInstance0;
            layout(location = 8) in vec4 aInstance1;
            layout(location = 9) in vec4 aInstance2;
            layout(location = 10) in vec4 aInstance3;
            uniform int uFadeMode;
            uniform vec3 uFadeEye;
            uniform vec4 uFadeRange;
            out vec2 vRange;
            #define uModel mat4(vec4(aInstance0.xyz, 0.0), vec4(aInstance1.xyz, 0.0), vec4(aInstance2.xyz, 0.0), vec4(aInstance3.xyz, 1.0))
            """);
        v = Replace(v, @"void\s+main\s*\(\s*\)\s*\{", """
            void main()
            {
                vRange = vec2(aInstance0.w, aInstance1.w);
                if (uFadeMode == 1)
                {
                    float fd = length((uModel * vec4(aPosition, 1.0)).xyz - uFadeEye);
                    vRange = vec2(0.0, smoothstep(uFadeRange.x, uFadeRange.y, fd) * (1.0 - smoothstep(uFadeRange.z, uFadeRange.w, fd)));
                }
            """);
        return v;
    }

    public static string Fragment()
    {
        string f = Shaders.MeshFragment;
        f = Replace(f, @"#version\s+330\s+core", """
            #version 330 core
            in vec2 vRange;
            float ditherValue() { return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))); }
            """);
        f = Replace(f, @"void\s+main\s*\(\s*\)\s*\{", """
            void main()
            {
                if (vRange.y < 1.0 || vRange.x > 0.0)
                {
                    float dv = ditherValue();
                    if (dv < vRange.x || dv >= vRange.y) discard;
                }
            """);
        return f;
    }

    static string Replace(string source, string pattern, string replacement)
    {
        var regex = new Regex(pattern);
        if (!regex.IsMatch(source)) throw new InvalidOperationException($"BuildingLodShaders: '{pattern}' not found in the shared mesh shader; update the patch.");
        return regex.Replace(source, _ => replacement, 1);
    }
}
