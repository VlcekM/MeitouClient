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

    /// <param name="solid">The variant for fully visible instances of a material without a cut-out: no <c>discard</c> anywhere, so the GPU
    /// tests depth before the fragment shader runs (a shader that may discard gets its depth test late). The same maths otherwise.</param>
    public static string Fragment(bool solid = false)
    {
        string f = Shaders.MeshFragment;
        f = Replace(f, @"#version\s+330\s+core", """
            #version 330 core
            in vec2 vRange;
            float ditherValue() { return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))); }
            """);
        if (solid) f = Replace(f, @"if\s*\(\s*uAlphaThreshold\s*>\s*0\.0\s*&&\s*alpha\s*<\s*uAlphaThreshold\s*\)\s*discard\s*;", "");
        else
            f = Replace(f, @"void\s+main\s*\(\s*\)\s*\{", """
            void main()
            {
                if (vRange.y < 1.0 || vRange.x > 0.0)
                {
                    float dv = ditherValue();
                    if (dv < vRange.x || dv >= vRange.y) discard;
                }
            """);
        // The viewer's plain lighting is overwritten by the game sky's lighting: compute it only where it is used (the same values).
        f = Literal(f, "if (uFogDistance > 0.0 && uAtmoParams.x > 0.5) colour = kenshiLight(albedo, n, v, glossLit, vWorld);\n", "");
        f = Literal(f, """
            vec3 l = normalize(uLightDir);
            vec3 v = normalize(uEye - vWorld);
            float diff = max(dot(n, l), 0.0);
            float hemi = 0.5 + 0.5 * n.y;
            vec3 ambient = mix(vec3(0.22, 0.20, 0.18), vec3(0.42, 0.45, 0.50), hemi);
            vec3 h = normalize(l + v);
            float spec = pow(max(dot(n, h), 0.0), 8.0 + 56.0 * gloss) * gloss * uSpecular * 0.5;
            vec3 sunLight = vec3(1.0, 0.97, 0.92);
            vec3 colour = albedo * (ambient + diff * sunLight) + spec * diff * sunLight;
            """, """
            vec3 v = normalize(uEye - vWorld);
            vec3 colour;
            if (uFogDistance > 0.0 && uAtmoParams.x > 0.5) colour = kenshiLight(albedo, n, v, glossLit, vWorld);
            else
            {
                vec3 l = normalize(uLightDir);
                float diff = max(dot(n, l), 0.0);
                float hemi = 0.5 + 0.5 * n.y;
                vec3 ambient = mix(vec3(0.22, 0.20, 0.18), vec3(0.42, 0.45, 0.50), hemi);
                vec3 h = normalize(l + v);
                float spec = pow(max(dot(n, h), 0.0), 8.0 + 56.0 * gloss) * gloss * uSpecular * 0.5;
                vec3 sunLight = vec3(1.0, 0.97, 0.92);
                colour = albedo * (ambient + diff * sunLight) + spec * diff * sunLight;
            }
            """);
        return f;
    }

    // ---- the native model (docs/renderer-native.md 3.3, step O) ----

    /// <summary>The objects' push constants (<see cref="ObjectPush"/> is the C# side; std430, 128 bytes): <see cref="NativeShaders.MeshPushMembers"/>
    /// without what these programs never vary (the head textures, <c>uHasHead</c> and <c>uSkinned</c>, which the GL code set to 0 once, are
    /// constants false here; <c>uCoverage</c> is foliage's), plus the dither fade's mode and range.</summary>
    public const string PushMembers = """
            vec3 tint;
            float triplanarScale;
            vec3 flatColour;
            float alphaThreshold;
            vec2 tile;
            float specular;
            int alphaSource;
            int alphaChannel;
            int greyChannel;
            uint diffuse;
            uint normal;
            uint diffuse2;
            uint normal2;
            int fadeMode;
            bool normalSwizzled;
            bool hasDiffuse;
            bool hasNormal;
            bool hasDual;
            bool triplanar;
            bool emissive;
            bool useVertexColour;
            bool wireframe;
            uint spare;
            vec4 fadeRange;
        """;

    /// <summary>What these programs map beyond <see cref="NativeShaders.Map"/>: the fade (the eye is the view's), and the constants.</summary>
    static readonly Dictionary<string, string> Own = new()
    {
        ["uFadeMode"] = "pc.fadeMode", ["uFadeEye"] = "view.eye", ["uFadeRange"] = "pc.fadeRange",
        ["uSkinned"] = "false", ["uHasHead"] = "false",
        // Read only under uHasHead (never): any valid bindless entry.
        ["uHeadDiffuse"] = "textures2D[pc.diffuse]", ["uHeadNormal"] = "textures2D[pc.normal]",
    };

    public static string VertexNative() => NativeShaders.Port(Vertex(), NativeShaders.Map(Own), PushMembers);
    public static string FragmentNative(bool solid = false) => NativeShaders.Port(Fragment(solid), NativeShaders.Map(Own), PushMembers);
    /// <summary><see cref="ShadowShaders.MeshDepthFragment"/> for <see cref="VertexNative"/>.</summary>
    public static string DepthNative() => NativeShaders.Port(ShadowShaders.MeshDepthFragment, NativeShaders.Map(Own), PushMembers);

    /// <summary>The first <paramref name="from"/> in <paramref name="source"/> (text, with the lines' indentation made equal) replaced by <paramref name="to"/>.</summary>
    static string Literal(string source, string from, string to)
    {
        static string Flat(string s) => string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()));
        string[] sourceLines = source.Replace("\r\n", "\n").Split('\n'), fromLines = Flat(from).Split('\n');
        if (fromLines[^1].Length == 0) fromLines = fromLines[..^1];
        for (int i = 0; i + fromLines.Length <= sourceLines.Length; i++)
        {
            bool match = true;
            for (int k = 0; k < fromLines.Length && match; k++) match = sourceLines[i + k].Trim() == fromLines[k];
            if (!match) continue;
            var result = new List<string>(sourceLines[..i]);
            result.AddRange(to.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'));
            result.AddRange(sourceLines[(i + fromLines.Length)..]);
            return string.Join("\n", result);
        }
        throw new InvalidOperationException($"BuildingLodShaders: '{fromLines[0]}...' not found in the shared mesh shader; update the patch.");
    }

    static string Replace(string source, string pattern, string replacement)
    {
        var regex = new Regex(pattern);
        if (!regex.IsMatch(source)) throw new InvalidOperationException($"BuildingLodShaders: '{pattern}' not found in the shared mesh shader; update the patch.");
        return regex.Replace(source, _ => replacement, 1);
    }
}

/// <summary>The C# side of <see cref="BuildingLodShaders.PushMembers"/> (std430 push constants, 128 bytes). GLSL bools are 32-bit (0 / 1).</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 128)]
struct ObjectPush
{
    [System.Runtime.InteropServices.FieldOffset(0)] public System.Numerics.Vector3 Tint;
    [System.Runtime.InteropServices.FieldOffset(12)] public float TriplanarScale;
    [System.Runtime.InteropServices.FieldOffset(16)] public System.Numerics.Vector3 FlatColour;
    [System.Runtime.InteropServices.FieldOffset(28)] public float AlphaThreshold;
    [System.Runtime.InteropServices.FieldOffset(32)] public System.Numerics.Vector2 Tile;
    [System.Runtime.InteropServices.FieldOffset(40)] public float Specular;
    [System.Runtime.InteropServices.FieldOffset(44)] public int AlphaSource;
    [System.Runtime.InteropServices.FieldOffset(48)] public int AlphaChannel;
    [System.Runtime.InteropServices.FieldOffset(52)] public int GreyChannel;
    [System.Runtime.InteropServices.FieldOffset(56)] public uint Diffuse;
    [System.Runtime.InteropServices.FieldOffset(60)] public uint Normal;
    [System.Runtime.InteropServices.FieldOffset(64)] public uint Diffuse2;
    [System.Runtime.InteropServices.FieldOffset(68)] public uint Normal2;
    [System.Runtime.InteropServices.FieldOffset(72)] public int FadeMode;
    [System.Runtime.InteropServices.FieldOffset(76)] public uint NormalSwizzled;
    [System.Runtime.InteropServices.FieldOffset(80)] public uint HasDiffuse;
    [System.Runtime.InteropServices.FieldOffset(84)] public uint HasNormal;
    [System.Runtime.InteropServices.FieldOffset(88)] public uint HasDual;
    [System.Runtime.InteropServices.FieldOffset(92)] public uint Triplanar;
    [System.Runtime.InteropServices.FieldOffset(96)] public uint Emissive;
    [System.Runtime.InteropServices.FieldOffset(100)] public uint UseVertexColour;
    [System.Runtime.InteropServices.FieldOffset(104)] public uint Wireframe;
    [System.Runtime.InteropServices.FieldOffset(108)] public uint Spare;
    [System.Runtime.InteropServices.FieldOffset(112)] public System.Numerics.Vector4 FadeRange;
}
