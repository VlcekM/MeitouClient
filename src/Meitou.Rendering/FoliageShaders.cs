using System.Text.RegularExpressions;

namespace Meitou.Rendering;

/// <summary>
/// The foliage shaders (docs/viewer.md, "Foliage").
/// <list type="bullet">
/// <item>Meshes (trees, bushes, rocks): the viewer's shared mesh shaders (<see cref="Shaders"/>, so lighting and the atmosphere
/// follow whatever is added there) given a per-instance model matrix (attributes 7 to 10, divisor 1) whose row 0 w carries the
/// distance fade (a dither threshold, 2 = fully visible), and an alpha-to-coverage output for the cut-out leaves.</item>
/// <item>Grass: quads built from <c>gl_VertexID</c> around per-blade instances, with Kenshi's grass shader rules (foliage.hlsl):
/// the top edge sways along X by <c>sin(time + x × frequency)</c>, the blade sinks into the ground over the last fifth of its
/// range, the cut-out is diffuse alpha 0.6, the colour is the sprite times the colour map, the normal is straight up.</item>
/// </list>
/// </summary>
static class FoliageShaders
{
    public const int InstanceLocation = 7;

    public static string MeshVertex()
    {
        string v = Shaders.MeshVertex;
        v = Replace(v, @"uniform\s+mat4\s+uModel\s*;", """
            layout(location = 7) in vec4 aInstance0;
            layout(location = 8) in vec4 aInstance1;
            layout(location = 9) in vec4 aInstance2;
            layout(location = 10) in vec4 aInstance3;
            out float vFade;
            #define uModel mat4(vec4(aInstance0.xyz, 0.0), vec4(aInstance1.xyz, 0.0), vec4(aInstance2.xyz, 0.0), vec4(aInstance3.xyz, 1.0))
            """, required: true);
        v = Replace(v, @"void\s+main\s*\(\s*\)\s*\{", """
            void main()
            {
                vFade = aInstance0.w;
            """, required: true);
        return v;
    }

    public static string MeshFragment()
    {
        string f = Shaders.MeshFragment;
        f = Replace(f, @"#version\s+330\s+core", """
            #version 330 core
            in float vFade;
            uniform bool uCoverage;      // alpha to coverage (multisampled target): the cut-out edge becomes the coverage
            float foliageDither() { return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))); }
            """, required: true);
        f = Replace(f, @"void\s+main\s*\(\s*\)\s*\{", """
            void main()
            {
                if (vFade < 1.0 && foliageDither() >= vFade) discard;
            """, required: true);
        // The cut-out: a hard test, or with alpha to coverage a one-pixel ramp around the threshold.
        f = Replace(f, @"if\s*\(\s*uAlphaThreshold\s*>\s*0\.0\s*&&\s*alpha\s*<\s*uAlphaThreshold\s*\)\s*discard\s*;", """
            float foliageCoverage = 1.0;
            if (uAlphaThreshold > 0.0)
            {
                if (uCoverage)
                {
                    foliageCoverage = clamp((alpha - uAlphaThreshold) / max(fwidth(alpha), 1e-4) + 0.5, 0.0, 1.0);
                    if (foliageCoverage <= 0.0) discard;
                }
                else if (alpha < uAlphaThreshold) discard;
            }
            """, required: true);
        f = Replace(f, @"fragColour\s*=\s*vec4\s*\(\s*colour\s*,\s*1\.0\s*\)\s*;", "fragColour = vec4(colour, foliageCoverage);", required: false);
        return f;
    }

    /// <remarks>The aerial perspective is linear in the colour (<c>atmoApply</c> mixes), so it is evaluated per vertex as a multiply and an add: grass covers much of the screen.</remarks>
    public static readonly string GrassVertex = "#version 330 core\n" + AtmosphereShaders.Functions + """
        layout(location = 0) in vec4 aBlade;     // x, y, z, scale 0..1
        layout(location = 1) in float aYaw;
        uniform mat4 uViewProjection;
        uniform vec4 uSize;                      // min width, max width, min height, max height
        uniform bool uCross;                     // CROSSQUADS: two quads at right angles
        uniform float uTime;                     // sway phase (radians)
        uniform float uFrequency;
        uniform float uSway;                     // sway amplitude along X (units)
        uniform vec3 uEye;
        uniform float uRange;                    // the grass layer's range
        uniform float uFogDistance;
        out vec3 vHazeMul;
        out vec3 vHazeAdd;
        out vec3 vWorld;
        out vec2 vUv;
        out float vFade;
        void main()
        {
            int quad = gl_VertexID / 6, corner = gl_VertexID % 6;
            // two triangles: (0,0) (1,0) (1,1)  (0,0) (1,1) (0,1); u along the quad, v = 1 at the ground, 0 at the top
            int cu[6] = int[6](0, 1, 1, 0, 1, 0);
            int cv[6] = int[6](1, 1, 0, 1, 0, 0);
            float s = aBlade.w;
            float width = mix(uSize.x, uSize.y, s), height = mix(uSize.z, uSize.w, s);
            float yaw = aYaw + (quad == 1 ? 1.5707963 : 0.0);
            vec3 side = vec3(cos(yaw), 0.0, sin(yaw));
            float u = float(cu[corner]), v = float(cv[corner]);
            vec3 p = aBlade.xyz + side * (u - 0.5) * width + vec3(0.0, (1.0 - v) * height, 0.0);
            if (v == 0.0) p.x += uSway * sin(uTime + aBlade.x * uFrequency);
            float dist = distance(uEye.xz, aBlade.xz);
            p.y -= height * clamp(5.0 * dist / uRange - 4.0, 0.0, 1.0);   // grows out of the ground over the last fifth of the range
            vWorld = p;
            vUv = vec2(u, v);
            vFade = 1.0;
            gl_Position = uViewProjection * vec4(p, 1.0);
            vHazeMul = vec3(1.0);
            vHazeAdd = vec3(0.0);
            if (uFogDistance > 0.0)
            {
                vHazeAdd = atmoApply(vec3(0.0), uEye, p);
                vHazeMul = atmoApply(vec3(1.0), uEye, p) - vHazeAdd;
            }
        }
        """;

    public static readonly string GrassFragment = "#version 330 core\n" + AtmosphereShaders.Functions + """
        in vec3 vWorld;
        in vec2 vUv;
        in float vFade;
        in vec3 vHazeMul;
        in vec3 vHazeAdd;
        uniform sampler2D uSprite;
        uniform sampler2D uColourMap;
        uniform bool uHasColourMap;
        uniform vec4 uColourBounds;      // x0, z0, x1, z1: the colour map is stretched over the zone
        uniform bool uCoverage;
        uniform vec3 uLightDir;
        uniform vec3 uEye;
        uniform vec3 uFogColour;
        uniform float uFogDistance;
        uniform bool uWireframe;
        out vec4 fragColour;
        void main()
        {
            if (uWireframe) { fragColour = vec4(0.3, 0.9, 0.3, 1.0); return; }
            vec4 d = texture(uSprite, vUv);
            const float threshold = 0.6;
            float coverage = 1.0;
            if (uCoverage)
            {
                coverage = clamp((d.a - threshold) / max(fwidth(d.a), 1e-4) + 0.5, 0.0, 1.0);
                if (coverage <= 0.0) discard;
            }
            else if (d.a < threshold) discard;
            vec3 albedo = d.rgb;
            if (uHasColourMap)
            {
                vec2 cuv = (vWorld.xz - uColourBounds.xy) / (uColourBounds.zw - uColourBounds.xy);
                albedo *= texture(uColourMap, cuv).rgb;
            }
            // Lit as the shared mesh shader lights a surface facing straight up (foliage.hlsl writes the normal (0, 1, 0)).
            vec3 l = normalize(uLightDir);
            float diff = max(l.y, 0.0);
            vec3 ambient = vec3(0.42, 0.45, 0.50);
            vec3 sunLight = vec3(1.0, 0.97, 0.92);
            vec3 colour = albedo * (ambient + diff * sunLight);
            // World view, game sky: the game's deferred lighting of what foliage.hlsl writes for grass: normal straight up, gloss 0 (grass_fs zeroes the
            // diffuse alpha before writing it × 0.6; only rain would raise it).
            if (uFogDistance > 0.0 && uAtmoParams.x > 0.5) colour = kenshiLight(albedo, vec3(0.0, 1.0, 0.0), normalize(uEye - vWorld), 0.0, vWorld);
            colour = colour * vHazeMul + vHazeAdd;
            fragColour = vec4(colour, coverage);
        }
        """;

    static string Replace(string source, string pattern, string replacement, bool required)
    {
        var regex = new Regex(pattern);
        if (!regex.IsMatch(source))
        {
            if (required) throw new InvalidOperationException($"FoliageShaders: '{pattern}' not found in the shared mesh shader; update the patch.");
            return source;
        }
        return regex.Replace(source, _ => replacement, 1);
    }
}
