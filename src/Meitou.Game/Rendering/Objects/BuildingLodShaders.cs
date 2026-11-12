using System.Text.RegularExpressions;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Shadows;

namespace Meitou.Game.Rendering.Objects;

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
/// With <c>uFadeMode</c> bit 1 (distant towns) hi comes per vertex from the distance to <c>uFadeEye</c> instead.
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
            uniform vec3 uEye;
            uniform vec4 uFadeRange;
            out vec2 vRange;
            flat out float vInterior;
            flat out float vScaffold;
            out float vBuildY;
            flat out float vScaffoldTiling;
            out vec3 vWetPos;
            #define uModel mat4(vec4(aInstance0.xyz, 0.0), vec4(aInstance1.xyz, 0.0), vec4(aInstance2.xyz, 0.0), vec4(aInstance3.xyz, 1.0))
            """);
        v = Replace(v, @"void\s+main\s*\(\s*\)\s*\{", """
            void main()
            {
                vRange = vec2(aInstance0.w, aInstance1.w);
                vInterior = mod(aInstance2.w, 4.0);
                vScaffold = aInstance3.w;
                // A site (BuildingLodShaders.PackSite): the vertex's height over its part's mesh top, and the scaffold tiling.
                float site = floor(aInstance2.w / 4.0);
                vBuildY = aPosition.y / max(mod(site, 4096.0) * 0.1, 0.1);
                vScaffoldTiling = floor(site / 4096.0) / 16.0;
                if ((uFadeMode & 1) == 1)
                {
                    float fd = length((uModel * vec4(aPosition, 1.0)).xyz - uFadeEye);
                    vRange = vec2(0.0, smoothstep(uFadeRange.x, uFadeRange.y, fd) * (1.0 - smoothstep(uFadeRange.z, uFadeRange.w, fd)));
                }
            """);
        // Projected relative to the eye (uViewProjection has no translation, WorldObjectRenderer.Draw): the instance's origin less the eye is one
        // subtraction of near values, exact, where the world position at tens of thousands of units steps by 0.004 and a mesh's layers a hair
        // apart (a ruin's plaster over its wall, plates over planks) fought for depth (docs/render-objects.md "Precision").
        v = Replace(v, @"gl_Position\s*=\s*uViewProjection\s*\*\s*p\s*;", """
                vec4 local = skin * vec4(aPosition, 1.0);
                // The vertex less the instance's origin, in the world's orientation, moved by a few units the origin picks (so two of a mesh do not wear the
                // same drops): where the wet world's drops and runs sit (AtmosphereShaders wetObject), small numbers at any place in the world.
                vWetPos = mat3(uModel) * local.xyz + fract(aInstance3.xyz * 0.371) * 7.0;
                gl_Position = uViewProjection * vec4(mat3(uModel) * local.xyz + (aInstance3.xyz - uEye), 1.0);
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
            flat in float vInterior;
            flat in float vScaffold;
            in vec3 vWetPos;
            float gridLine(float v) { float f = fract(v); return min(f, 1.0 - f); }
            float ditherValue() { return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))); }
            vec3 instanceTint(float v) { float p = -v - 1.0; return vec3(mod(p, 256.0), mod(floor(p / 256.0), 256.0), floor(p / 65536.0)) * (1.275 / 255.0); }
            """);
        // A negative spare M44 is a tint (a farm plant's, WorldObjectRenderer.PackTint).
        // A site's grid lies over the built part too, until it fades from the top down past f = 0.8 (above 1 - 5 (f - 0.8) none).
        f = Literal(f, "if (uUseVertexColour) albedo *= vColour.rgb;", """
            if (uUseVertexColour) albedo *= vColour.rgb;
            if (vScaffold < 0.0) albedo *= instanceTint(vScaffold);
            if (vScaffold > 2.0)
            {
                vec4 grid = scaffoldGrid(uv);
                albedo = mix(albedo, grid.rgb, 1.0 - (vScaffold - 10000.0 - 0.8) * 5.0 < vBuildY ? 0.0 : grid.a);
            }
            """);
        f = Replace(f, @"void\s+main\s*\(\s*\)\s*\{", ScaffoldFunctions + "\nvoid main()\n{");
        // The editor's studio (Meitou, previewsun; CharacterRenderer.Studio): the platform lit as the character on it, the character's shadow and its
        // sky's occlusion on it (the view block's interior-cut fields: the native port leaves view.* and the bindless tables as they are).
        f = Replace(f, @"void\s+main\s*\(\s*\)\s*\{", Characters.StudioShaders.Functions + "\nvoid main()\n{");
        // A studio depth draw (a fog distance of -5, the platform casting into the studio's maps): its cut-outs only.
        f = Literal(f, "vec3 l = normalize(uLightDir);", "if (uFogDistance < -4.5) { fragColour = vec4(0.0); return; }\n            vec3 l = normalize(uLightDir);");
        f = Literal(f, "if (uFogDistance < -1.5) colour = stageLight(albedo, n, v, normalize(uLightDir), glossLit, uFogDistance < -2.5);", """
            if (uFogDistance < -3.5)
            {
                vec3 sl = normalize(uLightDir);
                float shade = studioShadow(vWorld, n, sl);
                vec3 sky = studioAmbient(n, studioSky(vWorld, n));
                vec3 sh = normalize(sl + v);
                float sd = max(dot(n, sl), 0.0);
                float sspec = pow(max(dot(n, sh), 0.0), 8.0 + 56.0 * gloss) * gloss * uSpecular * 0.6;
                colour = albedo * (sky + sd * vec3(1.0, 0.92, 0.80) * 1.9 * shade) + sspec * sd * vec3(1.0, 0.92, 0.80) * 1.9 * shade;
            """ + (Environment.GetEnvironmentVariable("MEITOU_STUDIO_DEBUG") == "1" ? "    colour = vec3(shade, studioSky(vWorld, n), 0.0);" : "") + """
            }
            else if (uFogDistance < -1.5) colour = stageLight(albedo, n, v, normalize(uLightDir), glossLit, uFogDistance < -2.5);
            """);
        if (solid) f = Replace(f, @"if\s*\(\s*uAlphaThreshold\s*>\s*0\.0\s*&&\s*alpha\s*<\s*uAlphaThreshold\s*\)\s*discard\s*;", "");
        else
            f = Replace(f, @"void\s+main\s*\(\s*\)\s*\{", CutVolumes + """

            void main()
            {
                if (vRange.y < 1.0 || vRange.x > 0.0)
                {
                    float dv = ditherValue();
                    if (dv < vRange.x || dv >= vRange.y) discard;
                }
                if ((uFadeMode & 2) != 0 && inCutVolume(vWorld)) discard;
            """ + ScaffoldTest);
        // The wet world (Meitou): runs, a film and drops on what the rain wets (the world's view only).
        f = Literal(f, """
            albedo = wetSurface.rgb;
            glossLit = wetSurface.a;
            """, """
            albedo = wetSurface.rgb;
            float dryGloss = glossLit;
            glossLit = wetSurface.a;
            if (uWetWorld > 0.5 && uFogDistance > 0.0 && (uSurface & 2u) == 0u) wetObject(vWetPos, max(length(dFdx(vWetPos)), length(dFdy(vWetPos))), dryGloss, uWeatherWet.x, uWeatherWet.y, normalize(uLightDir), n, albedo, glossLit);
            """);
        // The viewer's plain lighting is overwritten by the game sky's lighting: compute it only where it is used (the same values).
        f = Literal(f, "uniform sampler2D uNormal;\n", "uniform sampler2D uNormal;\nuniform sampler2D uMetal;\nuniform uint uMetalMap;\n");
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
            giInterior = mod(vInterior, 2.0) > 0.5;
            // The game's metalness map (objects.hlsl), then the named metal textures (MetalTextures): what has no colour of its own; rust and paint are saturated.
            atmoMetalness = uMetalMap != 0u ? texture(uMetal, uv).r : 0.0;
            if ((uSurface & 32u) != 0u)
            {
                float top = max(albedo.r, max(albedo.g, albedo.b));
                float bare = smoothstep(0.04, 0.16, top) * (1.0 - smoothstep(0.3, 0.6, 1.0 - min(albedo.r, min(albedo.g, albedo.b)) / max(top, 1e-3))) * 0.3;
                atmoMetalness = max(atmoMetalness, bare);
                atmoMetalDiffuse = 0.85;
                glossLit = mix(glossLit, mix(0.3, 0.75, glossLit), bare);
            }
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
        // An EMISSIVE material glows only while its building's lights are on (the instance's spare component carries 2 when they are off,
        // WorldObjectRenderer.Emit).
        f = Literal(f, "if (uEmissive) colour += albedo * (1.0 - nm.a) * 3.2;", "if (uEmissive && vInterior < 1.5) colour += albedo * (1.0 - nm.a) * 3.2;");

        // The placement ghost (docs/render-objects.md "Placement ghost") draws with a non-zero flat colour (which an ordinary draw leaves at 0): it
        // tints the lit picture.
        f = Literal(f, "fragColour = vec4(colour, 1.0);\n", """
            if (dot(uFlatColour, uFlatColour) > 0.0) colour = mix(colour, colour * uFlatColour * 1.6 + uFlatColour * 0.12, 0.7);
            fragColour = vec4(colour, 1.0);
            """);
        return f;
    }

    /// <summary>
    /// The scaffold of an unfinished building (docs/game/buildings-production.md "Construction look"): <c>vScaffold</c> is the instance matrix's
    /// spare M44, 1 for every ordinary instance and <see cref="ScaffoldBase"/> plus the look's f for a site. Above 1.2 f of the part's mesh top only
    /// the grid (<c>construction.dds</c> at the mesh coordinates x the tiling, alpha above 0.7) is kept; without the texture (not loaded) a
    /// procedural grid stands in.
    /// </summary>
    public const float ScaffoldBase = 10000;

    /// <summary>
    /// What a site adds to the instance's M34 (whose low two bits are its interior and lights flags): 4 x (the part's mesh top in decimetres, 1-4095,
    /// + 4096 x round(16 x <c>scaffolding tex scale</c>), 0-255; 1 when the field is 0 or absent), exact in a float.
    /// </summary>
    public static float PackSite(float top, float tiling)
    {
        int t = Math.Clamp((int)MathF.Round(top * 10), 1, 4095);
        int s = Math.Clamp((int)MathF.Round((tiling > 0 ? tiling : 1) * 16), 0, 255);
        return 4 * (t + 4096 * s);
    }
    /// <summary>
    /// The largest tint channel <see cref="WorldObjectRenderer.PackTint"/> keeps (a plant's brightness goes to 1.1); <c>instanceTint</c> has the same
    /// number.
    /// </summary>
    public const float TintRange = 1.275f;

    /// <summary>
    /// The grid and its helpers (<c>uScaffold</c>: the push spare's upper bits, index + 1, 0 not loaded), at the tiled mesh coordinates
    /// (objects.hlsl).
    /// </summary>
    const string ScaffoldFunctions = """
            in float vBuildY;
            flat in float vScaffoldTiling;
            uniform sampler2D uScaffold;
            vec4 scaffoldGrid(vec2 uv)
            {
                if ((uSurface >> 8u) != 0u) return texture(uScaffold, uv * vScaffoldTiling);
                float cell = 0.12;
                bool onGrid = gridLine(vWorld.y * cell) < 0.06 || gridLine((vWorld.x + vWorld.z) * cell) < 0.06 || gridLine((vWorld.x - vWorld.z) * cell) < 0.06;
                return vec4(0.35, 0.3, 0.25, onGrid ? 1.0 : 0.0);
            }
        """;

    const string ScaffoldTest = """
                if (vScaffold > 2.0 && (vScaffold - 10000.0) * 1.2 < vBuildY && scaffoldGrid(vUv * uTile).a <= 0.7) discard;
        """;

    /// <summary>
    /// The caster's fragment shader with the same clip, so a site's shadow is its built part and scaffold; and the dither range taken at its middle
    /// (a caster has no dither): of an instance's two blending levels exactly one casts, and a distant town casts only where it is drawn (the
    /// camera's distance fade). Without that test the Hub's baked distant mesh, the intact town, cast its roofs' shadows into the ruins at every
    /// distance.
    /// </summary>
    // The view's cut volumes (convex footprint and height range each): a fragment of a cut draw (fadeMode bit 2) inside one is left out. The shadow
    // side of the cut into viewed buildings (WorldObjectRenderer.ShadowCut), and the selection mask's floor (WorldObjectRenderer.DrawSelectionMask);
    // the main view has none.
    const string CutVolumes = """
        uniform int uFadeMode;
        uniform uint uCutVolumes;
        uniform vec4 uCutRanges[4];
        uniform vec4 uCutPlanes[32];
        bool inCutVolume(vec3 p)
        {
            for (uint v = 0u; v < uCutVolumes; v++)
            {
                vec4 r = uCutRanges[v];
                if (p.y < r.z || p.y > r.w) continue;
                bool inside = true;
                for (int i = int(r.x); i < int(r.x + r.y) && inside; i++) inside = dot(uCutPlanes[i].xy, p.xz) >= uCutPlanes[i].z;
                if (inside) return true;
            }
            return false;
        }
        """;

    static string Depth()
    {
        string f = ShadowShaders.MeshDepthFragment;
        f = Replace(f, @"in\s+vec2\s+vUv\s*;", """
            in vec2 vUv;
            in vec3 vWorld;
            in vec2 vRange;
            flat in float vScaffold;
            float gridLine(float v) { float f = fract(v); return min(f, 1.0 - f); }
            """);
        return Replace(f,
            @"void\s+main\s*\(\s*\)\s*\{", "uniform uint uSurface;\n" + ScaffoldFunctions + "\n" + CutVolumes + "\nvoid main()\n{\n    if (vRange.x > 0.5 || vRange.y <= 0.5) discard;\n"
            + "    if ((uFadeMode & 2) != 0 && inCutVolume(vWorld)) discard;\n" + ScaffoldTest);
    }

    // ---- the native model (docs/renderer-native.md 3.3, step O) ----

    /// <summary>
    /// The objects' push constants (<see cref="ObjectPush"/> is the C# side; std430, 128 bytes): <see cref="NativeShaders.MeshPushMembers"/>
    /// without what these programs never vary (the head textures, <c>uHasHead</c> and <c>uSkinned</c>, which the GL code set to 0 once, are
    /// constants false here; <c>uCoverage</c> is foliage's), plus the dither fade's mode and range.
    /// </summary>
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
            uint metalMap;
            uint spare;
            vec4 fadeRange;
        """;

    /// <summary>
    /// What these programs map beyond <see cref="NativeShaders.Map"/>: the fade (the eye is the view's), and the constants.
    /// </summary>
    static readonly Dictionary<string, string> Own = new()
    {
        ["uFadeMode"] = "pc.fadeMode",
        ["uFadeEye"] = "view.eye",
        ["uFadeRange"] = "pc.fadeRange",
        // The interior cut: the view's mask, for the draws of map features over it (fadeMode bit 2, WorldObjectRenderer.Batch.Cut).
        ["uInteriorCut"] = "(view.interiorCut != 0u && (pc.fadeMode & 2) != 0)",
        ["uCutVolumes"] = "view.cutVolumes",
        ["uCutRanges"] = "view.cutRanges",
        ["uCutPlanes"] = "view.cutPlanes",
        ["uSkinned"] = "false",
        ["uHasHead"] = "false",
        // The wireframe is bit 64 of the push spare; its member holds the game's metalness map (its bindless index + 1, 0: none).
        ["uWireframe"] = "((pc.spare & 64u) != 0u)",
        ["uMetalMap"] = "pc.metalMap",
        ["uMetal"] = "textures2D[max(pc.metalMap, 1u) - 1u]",
        // The scaffold grid (construction.dds): the push spare's upper bits hold its bindless index + 1 (WorldObjectRenderer.RecordDraws); read only
        // when not 0.
        ["uScaffold"] = "textures2D[max(pc.spare >> 8u, 1u) - 1u]",
        // Read only under uHasHead (never): any valid bindless entry.
        ["uHeadDiffuse"] = "textures2D[pc.diffuse]",
        ["uHeadNormal"] = "textures2D[pc.normal]",
    };

    public static string VertexNative() => NativeShaders.Port(Vertex(), NativeShaders.Map(Own), PushMembers);
    /// <summary>
    /// The colour program; the full one (not <paramref name="solid"/>) also has the interior cut (<see cref="InteriorMaskRenderer.WithCut"/>).
    /// </summary>
    public static string FragmentNative(bool solid = false) =>
        Gi.GiResolveShaders.WithAlbedo(NativeShaders.Port(solid ? Fragment(true) : InteriorMaskRenderer.WithCut(Fragment()), NativeShaders.Map(Own),
                PushMembers));
    /// <summary>
    /// <see cref="ShadowShaders.MeshDepthFragment"/> for <see cref="VertexNative"/>.
    /// </summary>
    public static string DepthNative() => NativeShaders.Port(Depth(), NativeShaders.Map(Own), PushMembers);

    /// <summary>
    /// The first <paramref name="from"/> in <paramref name="source"/> (text, with the lines' indentation made equal) replaced by
    /// <paramref name="to"/>.
    /// </summary>
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

/// <summary>
/// The C# side of <see cref="BuildingLodShaders.PushMembers"/> (std430 push constants, 128 bytes). GLSL bools are 32-bit (0 / 1).
/// </summary>
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
    [System.Runtime.InteropServices.FieldOffset(104)] public uint MetalMap;
    [System.Runtime.InteropServices.FieldOffset(108)] public uint Spare;
    [System.Runtime.InteropServices.FieldOffset(112)] public System.Numerics.Vector4 FadeRange;
}