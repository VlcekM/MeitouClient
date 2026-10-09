namespace Meitou.Rendering;

/// <summary>
/// GLSL of the Meitou light shafts (the <c>shafts</c> switch; docs/render-shafts.md): the haze and the weather's fog darkened where the sun's light
/// is shadowed along the view ray. A froxel grid (cells across the screen × exponential depth slices, the slices tiled into one 2D atlas) holds, per
/// cell, the haze's opacity gained over the slice and how much of the slice the sun reaches (one shadow compare per sample); a second pass sums the
/// slices front to back into the sun-weighted share of the haze up to each slice; the apply pass takes that share off the haze each pixel has
/// (<c>atmoApplyHaze</c> of black: the haze colour times its alpha). Lit air stays the game's haze exactly; nothing is added. All three are
/// full-screen fragment programs over the atmosphere's functions (and through them the shadow receivers), so they read the frame globals.
/// </summary>
static class LightShaftShaders
{
    /// <summary>Slices per row of the atlas.</summary>
    public const int AtlasColumns = 8;

    /// <summary>The grid, the slices' distances and the camera.</summary>
    const string Common = """

        uniform vec4 uShaftGrid;    // x cells across, y cells down, z slices, w slices per atlas row
        uniform vec4 uShaftRange;   // x where the first slice starts, y where the last ends (distance along the ray), z ln(y / x), w the frame (the jitter's seed)
        uniform vec3 uShaftEye, uShaftRight, uShaftUp, uShaftBack;
        uniform vec2 uShaftTan;     // tan(fov/2) * aspect, tan(fov/2)

        // Slice s (continuous) <-> distance along the ray: exponential, as fine near the eye as far away in proportion.
        float shaftDistance(float s) { return uShaftRange.x * exp(s / uShaftGrid.z * uShaftRange.z); }
        float shaftSlice(float dist) { return log(max(dist, 1e-3) / uShaftRange.x) / uShaftRange.z * uShaftGrid.z; }

        // The unit view ray through a point of the screen (0..1), as the fog volumes pass rebuilds it.
        vec3 shaftRay(vec2 uv)
        {
            vec2 ndc = uv * 2.0 - 1.0;
            return normalize(uShaftRight * ndc.x * uShaftTan.x + uShaftUp * ndc.y * uShaftTan.y - uShaftBack);
        }
        """;

    /// <summary>The haze's alpha along a ray (after <see cref="AtmosphereShaders.Functions"/>).</summary>
    const string Alpha = """

        // How much of the haze (and the weather's fog) a ray has gathered by this distance: atmoKenshiHaze's alpha (the ramp, then the fog's
        // ease-in-out curve over it). The simple sky's and the physical haze's alpha by distance alone (the physical one's direction is left out).
        float shaftAlpha(float dist)
        {
            if (uAtmoParams.x < 0.5)
            {
                float f = clamp(dist / uAtmoSimple.w, 0.0, 1.0);
                return f * f * 0.9;
            }
            float alpha;
            if (uAtmoHaze.x > 0.5 && uAtmoAltitude.x < 1.0)
                alpha = min(clamp((dist - uAtmoHaze.y) / max(uAtmoHaze.z - uAtmoHaze.y, 1.0), 0.0, 1.0) * uAtmoAltitude.y, 1.0);
            else
                alpha = min((1.0 - exp(-3.0 * dist / max(uAtmoParams.y, 1.0))) * uAtmoAltitude.y, 1.0);
            if (uAtmoFog.z > 0.0)
            {
                float amount = clamp(dist * uAtmoHaze.w, 0.0, 1.0);
                float curve = (amount < 0.5 ? 2.0 * amount * amount : 1.0 - 2.0 * (amount - 1.0) * (amount - 1.0)) * uAtmoFog.z;
                alpha = clamp(alpha + curve, 0.0, 1.0);
            }
            return alpha;
        }
        """;

    /// <summary>
    /// The first pass, into the atlas (RG16F): per cell, (alpha gained over the slice × the share of its samples the sun reaches, alpha gained). The
    /// samples are stratified over the slice's depth and jittered within the cell, anew each frame (the temporal upscaler averages them). The shadow
    /// term of a point in the air: one compare in the first cascade that holds it (no filter, no surface to offset from), with the Meitou receiver's
    /// fade at the shadow range, its terrain term (mountains out to the horizon) and its landmark map; the Faithful receiver's cascades end at the last split.
    /// </summary>
    public static readonly string Inject = "#version 330 core\n" + AtmosphereShaders.Functions + Common + Alpha + """

        out vec4 fragColour;
        uniform int uShaftSamples;

        float shaftNoise(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }

        float shaftShadow(vec3 world, float noise)
        {
            if (uShadowOrigin.w < 0.5) return 1.0;
            bool meitou = uShadowAtlas.w > 0.5;
            float depth = dot(world - uShadowOrigin.xyz, uShadowForward.xyz);
            float lit = 1.0;
            int count = int(uShadowForward.w);
            if (!meitou || depth < uMsParams.y)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (c >= count) break;
                    if (depth > (meitou ? uMsSlice[c].y : uShadowCascade[c].x)) continue;
                    vec3 t = (uShadowTile[c] * vec4(world - uShadowOrigin.xyz, 1.0)).xyz;
                    if (any(lessThan(t.xy, vec2(0.0))) || any(greaterThan(t.xy, vec2(1.0))) || t.z >= 1.0) continue;
                    vec4 rect = uShadowRect[c];
                    lit = textureLod(uShadowMap, vec3(rect.xy + t.xy * rect.zw, t.z - 0.0005), 0.0);
                    break;
                }
            }
            if (!meitou) return lit;
            lit = mix(lit, 1.0, smoothstep(uMsParams.z, uMsParams.y, depth));
            return min(lit, min(msTerrain(world, depth), msLandmark(world, vec3(0.0), depth, noise)));
        }

        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            ivec2 grid = ivec2(uShaftGrid.xy);
            int column = p.x / grid.x, row = p.y / grid.y;
            int k = row * int(uShaftGrid.w) + column;
            if (k >= int(uShaftGrid.z)) { fragColour = vec4(0.0); return; }
            vec2 cell = vec2(p - ivec2(column, row) * grid);
            float noise = shaftNoise(cell + vec2(float(k) * 17.0, float(k) * 5.0) + uShaftRange.w * 5.588238);
            float a0 = shaftAlpha(shaftDistance(float(k))), a1 = shaftAlpha(shaftDistance(float(k + 1)));
            float lit = 0.0;
            for (int i = 0; i < uShaftSamples; i++)
            {
                float j = fract(noise + float(i) * 0.618034);
                vec2 offset = fract(vec2(noise * 7.31 + float(i) * 0.7548777, noise * 3.17 + float(i) * 0.5698403)) - 0.5;
                vec2 uv = (cell + 0.5 + offset) / uShaftGrid.xy;
                float dist = shaftDistance(float(k) + (float(i) + j) / float(uShaftSamples));
                lit += shaftShadow(uShaftEye + shaftRay(uv) * dist, j);
            }
            float gained = max(a1 - a0, 0.0);
            fragColour = vec4(gained * lit / float(uShaftSamples), gained, 0.0, 0.0);
        }
        """;

    /// <summary>The second pass, into a second atlas (R32F): per cell, the sun's share of the haze gathered from the eye to the far end of its slice
    /// (the first pass's two sums over this slice and every nearer one; 1 where no haze has gathered yet).</summary>
    public static readonly string Integrate = "#version 330 core\n" + Common + """

        out vec4 fragColour;
        uniform sampler2D uShaftInjected;

        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            ivec2 grid = ivec2(uShaftGrid.xy);
            int columns = int(uShaftGrid.w);
            int column = p.x / grid.x, row = p.y / grid.y;
            int k = row * columns + column;
            if (k >= int(uShaftGrid.z)) { fragColour = vec4(1.0); return; }
            ivec2 cell = p - ivec2(column, row) * grid;
            vec2 sum = vec2(0.0);
            for (int i = 0; i <= k; i++)
                sum += texelFetch(uShaftInjected, ivec2(i % columns, i / columns) * grid + cell, 0).rg;
            fragColour = vec4(sum.y > 1e-6 ? clamp(sum.x / sum.y, 0.0, 1.0) : 1.0, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The apply pass over the scene colour, blended as <c>scene * alpha + colour</c> (the fog volumes' state). The pixel's distance is rebuilt as the
    /// fog volumes pass rebuilds it (the near slice's depth, else the far slice's, the water plane where it lies in front); the sun's share there
    /// is read from the second atlas (bilinear across the cells, linear between the slices' far ends). A surface loses
    /// <c>darkening × (1 − share)</c> of its haze colour (a negative colour); the sky, which no haze covers, is scaled by
    /// <c>1 − darkening × (1 − share) × sky weight × alpha at the grid's end</c>.
    /// </summary>
    public static readonly string Apply = "#version 330 core\n" + AtmosphereShaders.Functions + Common + Alpha + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth, uFarDepth, uShaftLight;
        uniform vec2 uNearPlanes, uFarPlanes;
        uniform float uWaterY;
        uniform int uHasFar;
        uniform vec2 uShaftAtlas;   // the atlas's size in texels
        uniform vec4 uShaftParams;  // x darkening (strength × the sun's share of the light), y the sky's weight, z 1: show the share (debug)

        float viewZ(float d, vec2 nf) { float zd = 2.0 * d - 1.0; return nf.x * nf.y / (nf.y - zd * (nf.y - nf.x)); }

        float shaftShare(vec2 cell, int k)
        {
            int columns = int(uShaftGrid.w);
            vec2 tile = vec2(float(k % columns), float(k / columns)) * uShaftGrid.xy;
            return textureLod(uShaftLight, (tile + clamp(cell, vec2(0.5), uShaftGrid.xy - 0.5)) / uShaftAtlas, 0.0).r;
        }

        void main()
        {
            float d = textureLod(uNearDepth, vUv, 0.0).r;
            vec2 planes = uNearPlanes;
            if (d >= 1.0 && uHasFar != 0)
            {
                d = textureLod(uFarDepth, vUv, 0.0).r;
                planes = uFarPlanes;
            }
            vec2 ndc = vUv * 2.0 - 1.0;
            vec3 perZ = uShaftRight * ndc.x * uShaftTan.x + uShaftUp * ndc.y * uShaftTan.y - uShaftBack;
            float len = length(perZ);
            vec3 dir = perZ / len;
            bool sky = d >= 1.0;
            float dist = sky ? uShaftRange.y : viewZ(d, planes) * len;
            if (uWaterY > -1e30 && abs(dir.y) > 1e-6)
            {
                float tw = (uWaterY - uShaftEye.y) / dir.y;
                if (tw > 0.0 && (sky || tw < dist)) { dist = tw; sky = false; }
            }
            // The far ends of the slices: s - 1 is where this distance lies between them (-1 the eye, where the share is 1).
            float s = shaftSlice(min(dist, uShaftRange.y)) - 1.0;
            float share = 1.0;
            if (s > -1.0)
            {
                int last = int(uShaftGrid.z) - 1;
                int k0 = min(int(floor(s)), last);
                float f = clamp(s - float(k0), 0.0, 1.0);
                vec2 cell = vUv * uShaftGrid.xy;
                float s0 = k0 < 0 ? 1.0 : shaftShare(cell, k0);
                float s1 = shaftShare(cell, min(k0 + 1, last));
                share = mix(s0, s1, f);
            }
            float dark = uShaftParams.x * (1.0 - share);
            if (uShaftParams.z > 0.5)   // --shafts-debug: red the share, green the haze's alpha here, blue the haze colour's brightness, over everything
            {
                vec3 h = sky ? vec3(0.0) : atmoApplyHaze(vec3(0.0), uShaftEye, uShaftEye + dir * dist);
                fragColour = vec4(share, shaftAlpha(dist), dot(h, vec3(0.2126, 0.7152, 0.0722)), 0.0);
                return;
            }
            if (sky)
            {
                fragColour = vec4(0.0, 0.0, 0.0, 1.0 - dark * uShaftParams.y * shaftAlpha(uShaftRange.y));
                return;
            }
            vec3 haze = atmoApplyHaze(vec3(0.0), uShaftEye, uShaftEye + dir * dist);
            fragColour = vec4(-dark * haze, 1.0);
        }
        """;
}
