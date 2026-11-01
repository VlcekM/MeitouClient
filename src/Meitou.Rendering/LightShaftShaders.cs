using System.Globalization;

using Meitou.Data.World;

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

    static readonly string IrradianceLevel = KenshiLighting.IrradianceLevel.ToString("0.0##", CultureInfo.InvariantCulture);

    /// <summary>The grid, the slices' distances and the camera.</summary>
    internal const string Common = CommonUniforms + CommonFunctions;

    /// <summary>The grid and camera uniforms of <see cref="Common"/> (the scan kernel declares the same names in a uniform block).</summary>
    internal const string CommonUniforms = """

        uniform vec4 uShaftGrid;    // x cells across, y cells down, z slices, w slices per atlas row
        uniform vec4 uShaftRange;   // x where the first slice starts, y where the last ends (distance along the ray), z ln(y / x), w the frame (the jitter's seed)
        uniform vec3 uShaftEye, uShaftRight, uShaftUp, uShaftBack;
        uniform vec2 uShaftTan;     // tan(fov/2) * aspect, tan(fov/2)
        """;

    /// <summary>The slices' distances and the view ray of <see cref="Common"/>.</summary>
    internal const string CommonFunctions = """


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
    internal const string Alpha = """

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
    /// The first pass, into the atlas (RGBA16F): per cell, (alpha gained over the slice × the share of its samples the sun reaches, alpha gained, that share). The
    /// samples are stratified over the slice's depth and jittered within the cell, anew each frame (the temporal upscaler averages them). The shadow
    /// term of a point in the air: one compare in the first cascade that holds it (no filter, no surface to offset from), with the Meitou receiver's
    /// fade at the shadow range, its terrain term (mountains out to the horizon) and its landmark map; the Faithful receiver's cascades end at the last split.
    /// </summary>
    public static readonly string Inject = "#version 330 core\n" + AtmosphereShaders.Functions + Common + Alpha + $$"""

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
                for (int c = 0; c < {{ShadowSettings.MaxCascades}}; c++)
                {
                    if (c >= count) break;
                    if (depth > (meitou ? uMsSlice[c].y : uShadowCascade[c].x)) continue;
                    vec3 t = (uShadowTile[c] * vec4(world - uShadowOrigin.xyz, 1.0)).xyz;
                    if (any(lessThan(t.xy, vec2(0.0))) || any(greaterThan(t.xy, vec2(1.0))) || t.z >= 1.0) continue;
                    lit = shadowCascadeLayer(t.xy, c, t.z - 0.0005);
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
            fragColour = vec4(gained * lit / float(uShaftSamples), gained, lit / float(uShaftSamples), 0.0);
        }
        """;

    /// <summary>
    /// The sun's share averaged over frames (docs/render-shafts.md "Temporal"): with a temporal upscaler the injection's samples move every frame, and
    /// TAA, which clamps its history to the picture's neighbourhood, showed their noise as a shimmer in motion. Each cell's centre is taken back into
    /// last frame's grid (its camera and slices), the filtered share read there (bilinear in the slice, linear between slices), clamped to this frame's
    /// 3 x 3 neighbourhood in the slice (no trails where shadows move) and blended with this frame's (<c>uShaftBlend</c> of it; 1: no history). Out:
    /// the injection's layout, the share replaced.
    /// </summary>
    public static readonly string Resolve = "#version 330 core\n" + Common + """

        out vec4 fragColour;
        uniform sampler2D uShaftCurrent, uShaftHistory;
        uniform vec4 uShaftPrevRange;
        uniform vec3 uShaftPrevEye, uShaftPrevRight, uShaftPrevUp, uShaftPrevBack;
        uniform vec2 uShaftPrevTan;
        uniform float uShaftBlend;

        float historyShare(vec2 uv, int slice)
        {
            ivec2 grid = ivec2(uShaftGrid.xy);
            int perRow = int(uShaftGrid.w);
            vec2 tile = vec2(slice % perRow, slice / perRow) * vec2(grid);
            vec2 at = tile + clamp(uv * vec2(grid), vec2(0.5), vec2(grid) - 0.5);
            return textureLod(uShaftHistory, at / vec2(textureSize(uShaftHistory, 0)), 0.0).b;
        }

        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            ivec2 grid = ivec2(uShaftGrid.xy);
            int column = p.x / grid.x, row = p.y / grid.y;
            int k = row * int(uShaftGrid.w) + column;
            vec4 now = texelFetch(uShaftCurrent, p, 0);
            fragColour = now;
            if (k >= int(uShaftGrid.z) || uShaftBlend >= 1.0) return;
            ivec2 cell = p - ivec2(column, row) * grid;
            vec3 point = shaftRay((vec2(cell) + 0.5) / uShaftGrid.xy) * shaftDistance(float(k) + 0.5) + uShaftEye - uShaftPrevEye;
            float z = -dot(point, uShaftPrevBack);
            if (z <= 1e-3) return;
            vec2 uv = vec2(dot(point, uShaftPrevRight) / (z * uShaftPrevTan.x), dot(point, uShaftPrevUp) / (z * uShaftPrevTan.y)) * 0.5 + 0.5;
            float s = log(max(length(point), 1e-3) / uShaftPrevRange.x) / uShaftPrevRange.z * uShaftGrid.z - 0.5;
            if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))) || s < 0.0 || s > uShaftGrid.z - 1.0) return;
            int s0 = int(floor(s));
            float history = mix(historyShare(uv, s0), historyShare(uv, min(s0 + 1, int(uShaftGrid.z) - 1)), s - float(s0));
            float lo = 1.0, hi = 0.0;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    ivec2 q = clamp(cell + ivec2(x, y), ivec2(0), grid - 1);
                    float share = texelFetch(uShaftCurrent, ivec2(column, row) * grid + q, 0).b;
                    lo = min(lo, share);
                    hi = max(hi, share);
                }
            float lit = mix(clamp(history, lo, hi), now.b, uShaftBlend);
            fragColour = vec4(now.g * lit, now.g, lit, 0.0);
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

    /// <summary>The Meitou air layer's density and light (the scatter pass and the apply pass).</summary>
    internal const string Air = AirUniform + AirFunctions;

    /// <summary>The layer's uniform (<see cref="Air"/>).</summary>
    internal const string AirUniform = """

        uniform vec4 uShaftAir;     // x density at the base (per world unit; 0: no layer), y the base height, z the scale height, w the phase's anisotropy g
        """;

    /// <summary>The layer's density (<see cref="Air"/>).</summary>
    internal const string AirFunctions = """

        // The layer's density at a height: full up to the base (the ground under the eye), falling off exponentially above it.
        float shaftAirDensity(float y) { return uShaftAir.x * exp(-max(y - uShaftAir.y, 0.0) / uShaftAir.z); }
        """;

    /// <summary>
    /// The air layer (the <c>shafts</c> switch's second half, <c>--shafts-air</c>; docs/render-shafts.md "Air layer"), into a third atlas (RGBA16F): per cell,
    /// the light the layer scatters towards the eye from the eye to the far end of its slice (rgb) and the layer's transmittance over that path (a).
    /// Each nearer slice adds <c>T · S · (1 − e^(−σΔ))</c> with <c>T</c> the transmittance so far, σ the density at the slice's middle and Δ its length,
    /// and <c>S</c> the in-scattered radiance: the sun (<c>π² · sunLight</c>, the irradiance a white Lambert surface lit by <c>kenshiLight</c> would get)
    /// times the share of the slice the sun reaches (the first pass's third channel) times a Henyey-Greenstein phase (bright towards the sun), plus the
    /// sky's ambient radiance from above (isotropic). What the game's haze covers at that distance is left out (times <c>1 − alpha</c>): the layer sits
    /// in front of the haze, not over it.
    /// </summary>
    public static readonly string Scatter = "#version 330 core\n" + AtmosphereShaders.Functions + Common + Alpha + Air + """

        out vec4 fragColour;
        uniform sampler2D uShaftInjected;

        float shaftPhase(float c)
        {
            float g = uShaftAir.w, g2 = g * g;
            return (1.0 - g2) / (4.0 * ATMO_PI * pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5));
        }

        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            ivec2 grid = ivec2(uShaftGrid.xy);
            int columns = int(uShaftGrid.w);
            int column = p.x / grid.x, row = p.y / grid.y;
            int k = row * columns + column;
            if (k >= int(uShaftGrid.z)) { fragColour = vec4(0.0, 0.0, 0.0, 1.0); return; }
            ivec2 cell = p - ivec2(column, row) * grid;
            vec3 ray = shaftRay((vec2(cell) + 0.5) / uShaftGrid.xy);
            vec3 sun = ATMO_PI * ATMO_PI * uAtmoSunLight * shaftPhase(dot(ray, uAtmoSun.xyz));
            vec3 sky = atmoIrradiance(vec3(0.0, 1.0, 0.0)) * uAtmoLight.w;
            vec3 scattered = vec3(0.0);
            float trans = 1.0;
            for (int i = 0; i <= k; i++)
            {
                float d0 = shaftDistance(float(i)), d1 = shaftDistance(float(i + 1)), mid = 0.5 * (d0 + d1);
                float sigma = shaftAirDensity(uShaftEye.y + ray.y * mid);
                float lit = texelFetch(uShaftInjected, ivec2(i % columns, i / columns) * grid + cell, 0).b;
                float step = 1.0 - exp(-sigma * (d1 - d0));
                scattered += trans * step * (sun * lit + sky) * (1.0 - shaftAlpha(mid));
                trans *= 1.0 - step;
            }
            fragColour = vec4(scattered, trans);
        }
        """;


    /// <summary>
    /// The integrate pass and the scatter pass as one compute kernel (the <c>shafts-scan</c> switch; docs/render-shafts.md "Scan kernel"). One thread per cell column walks the
    /// slices front to back once: the running sums of the first pass's two channels give the sun's share of the haze (the light atlas, R32F), and the same walk carries the air
    /// layer's scattered light and transmittance (the scatter atlas, RGBA16F). The fragment passes made the same sums by looping from the eye to each slice in every cell
    /// (slices squared reads, and for the air layer the same again with three exp() per step); this reads each cell once. The arithmetic is theirs, term for term (the
    /// atmosphere's values come in a uniform block of the same names, so <see cref="Alpha"/> and the other functions are the fragment passes' text).
    /// </summary>
    public static readonly string Scan = $$"""
        #version 450
        layout(local_size_x = 8, local_size_y = 8) in;
        layout(set = 0, binding = 0) uniform sampler2D uShaftInjected;
        layout(set = 0, binding = 1, r32f) writeonly uniform image2D uLightOut;
        layout(set = 0, binding = 2, rgba16f) writeonly uniform image2D uScatterOut;
        layout(set = 0, binding = 3) uniform samplerCube uAtmoIrradiance;
        layout(std140, set = 0, binding = 4) uniform ScanParams
        {
            vec4 uShaftGrid;
            vec4 uShaftRange;
            vec3 uShaftEye;
            vec3 uShaftRight;
            vec3 uShaftUp;
            vec3 uShaftBack;
            vec2 uShaftTan;
            vec4 uShaftAir;
            vec4 uAtmoParams;
            vec4 uAtmoSimple;
            vec4 uAtmoHaze;
            vec4 uAtmoAltitude;
            vec4 uAtmoFog;
            vec4 uAtmoSun;
            vec4 uAtmoSunLight;   // xyz the sun's light, w the environment light's factor (uAtmoLight.w)
            vec4 uAtmoMaps;
        };

        const float ATMO_PI = 3.14159265358979;
        {{CommonFunctions}}
        {{Alpha}}
        {{AirFunctions}}

        float shaftPhase(float c)
        {
            float g = uShaftAir.w, g2 = g * g;
            return (1.0 - g2) / (4.0 * ATMO_PI * pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5));
        }

        void main()
        {
            ivec2 cell = ivec2(gl_GlobalInvocationID.xy);
            ivec2 grid = ivec2(uShaftGrid.xy);
            if (cell.x >= grid.x || cell.y >= grid.y) return;
            int columns = int(uShaftGrid.w), slices = int(uShaftGrid.z);
            bool air = uShaftAir.x > 0.0;
            vec3 ray = shaftRay((vec2(cell) + 0.5) / uShaftGrid.xy);
            vec3 sun = ATMO_PI * ATMO_PI * uAtmoSunLight.xyz * shaftPhase(dot(ray, uAtmoSun.xyz));
            vec3 sky = vec3(0.0);
            if (air)
            {
                if (uAtmoMaps.x < 0.5) sky = vec3(1.09, 1.2, 1.51);
                else { vec4 t = textureLod(uAtmoIrradiance, vec3(0.0, 1.0, 0.0), {{IrradianceLevel}}); sky = t.rgb * t.a * 4.0; }
                sky *= uAtmoSunLight.w;
            }
            vec2 sum = vec2(0.0);
            vec3 scattered = vec3(0.0);
            float trans = 1.0;
            for (int k = 0; k < slices; k++)
            {
                ivec2 at = ivec2(k % columns, k / columns) * grid + cell;
                vec4 v = texelFetch(uShaftInjected, at, 0);
                sum += v.rg;
                imageStore(uLightOut, at, vec4(sum.y > 1e-6 ? clamp(sum.x / sum.y, 0.0, 1.0) : 1.0, 0.0, 0.0, 1.0));
                if (air)
                {
                    float d0 = shaftDistance(float(k)), d1 = shaftDistance(float(k + 1)), mid = 0.5 * (d0 + d1);
                    float sigma = shaftAirDensity(uShaftEye.y + ray.y * mid);
                    float absorbed = 1.0 - exp(-sigma * (d1 - d0));
                    scattered += trans * absorbed * (sun * v.b + sky) * (1.0 - shaftAlpha(mid));
                    trans *= 1.0 - absorbed;
                    imageStore(uScatterOut, at, vec4(scattered, trans));
                }
            }
            // The atlas's last row, past the last slice: what the fragment passes wrote there (nothing reads it).
            int total = columns * ((slices + columns - 1) / columns);
            for (int k = slices; k < total; k++)
            {
                ivec2 at = ivec2(k % columns, k / columns) * grid + cell;
                imageStore(uLightOut, at, vec4(1.0));
                if (air) imageStore(uScatterOut, at, vec4(0.0, 0.0, 0.0, 1.0));
            }
        }
        """;
    /// <summary>
    /// The placed fog volumes' shadow (included by <see cref="PostProcessShaders.FogVolumes"/> before <see cref="FogVolumeShaders.Functions"/>, which
    /// calls it under <c>FOG_VOLUME_SHADOW</c>): the share of a volume's path from <c>near</c> to <c>far</c> the sun reaches, four reads of the first
    /// pass's third channel along it, and the colour scale that makes of it (<c>1 − darkening × (1 − share)</c>). 1 while the shafts are off.
    /// </summary>
    public static readonly string FogVolumeShadow = "\n#define FOG_VOLUME_SHADOW\n" + Common + """

        uniform sampler2D uShaftInjected;
        uniform vec4 uShaftFog;   // x darkening (0: no shadow), yz the atlas's size in texels

        // The sun's share at a distance along the cell's ray: between the two nearest slices' middles (no bands at the slices' edges).
        float fogVolumeShare(vec2 cell, float t)
        {
            if (t > uShaftRange.y) return 1.0;
            int columns = int(uShaftGrid.w), last = int(uShaftGrid.z) - 1;
            float s = clamp(shaftSlice(max(t, uShaftRange.x)) - 0.5, 0.0, float(last));
            int k0 = int(floor(s)), k1 = min(k0 + 1, last);
            vec2 t0 = vec2(float(k0 % columns), float(k0 / columns)) * uShaftGrid.xy, t1 = vec2(float(k1 % columns), float(k1 / columns)) * uShaftGrid.xy;
            return mix(textureLod(uShaftInjected, (t0 + cell) / uShaftFog.yz, 0.0).b, textureLod(uShaftInjected, (t1 + cell) / uShaftFog.yz, 0.0).b, s - float(k0));
        }

        // Only the part of the path the eye sees into counts: a thick volume shows its front, about 1 / density deep (its curve is
        // complete there), so shadow deep inside it or on the ground under it does not show through.
        float fogVolumeLight(vec3 d, float near, float far, float density)
        {
            if (uShaftFog.x <= 0.0) return 1.0;
            float z = -dot(d, uShaftBack);
            if (z <= 1e-4) return 1.0;
            vec2 uv = vec2(dot(d, uShaftRight) / (z * uShaftTan.x), dot(d, uShaftUp) / (z * uShaftTan.y)) * 0.5 + 0.5;
            vec2 cell = clamp(uv * uShaftGrid.xy, vec2(0.5), uShaftGrid.xy - 0.5);
            float seen = min(far, near + 0.7 / max(density, 1e-7));
            float lit = 0.0;
            for (int i = 0; i < 4; i++) lit += fogVolumeShare(cell, mix(near, seen, (float(i) + 0.5) * 0.25));
            return 1.0 - uShaftFog.x * (1.0 - lit * 0.25);
        }
        """;

    /// <summary>
    /// The apply pass over the scene colour, blended as <c>scene * alpha + colour</c> (the fog volumes' state). The pixel's distance is rebuilt as the
    /// fog volumes pass rebuilds it (the near slice's depth, else the far slice's, the water plane where it lies in front); the sun's share there
    /// is read from the second atlas (bilinear across the cells, linear between the slices' far ends). A surface loses
    /// <c>darkening × (1 − share)</c> of its haze colour (a negative colour); the sky, which no haze covers, is scaled by
    /// <c>1 − darkening × (1 − share) × sky weight × alpha at the grid's end</c>.
    /// </summary>
    public static readonly string Apply = "#version 330 core\n" + AtmosphereShaders.Functions + Common + Alpha + Air + ApplyFunctions + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth, uFarDepth;
        uniform vec2 uNearPlanes, uFarPlanes;
        uniform float uWaterY;
        uniform int uHasFar;

        float viewZ(float d, vec2 nf) { float zd = 2.0 * d - 1.0; return nf.x * nf.y / (nf.y - zd * (nf.y - nf.x)); }

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
            float waterWeight = 1.0;
            if (uWaterY > -1e30 && abs(dir.y) > 1e-6)
            {
                float tw = (uWaterY - uShaftEye.y) / dir.y;
                // Water does not write depth. Rebuild its hit only inside the rendered frustum;
                // an infinite plane otherwise turns the sky below the horizon into a hazed surface.
                float waterFar = (uHasFar != 0 ? uFarPlanes.y : uNearPlanes.y) * len;
                if (tw > 0.0 && tw < waterFar && (sky || tw < dist))
                {
                    dist = tw;
                    sky = false;
                    waterWeight = atmoWaterWeight(tw, waterFar / len);
                }
            }
            fragColour = shaftLayer(vUv, dir, dist, sky, waterWeight);
        }
        """;

    /// <summary>
    /// The apply pass's work for one pixel, given its ray and distance (<c>shaftLayer</c>: the colour to add and the factor the scene is kept by), shared by
    /// <see cref="Apply"/> and the merged scene layers pass (<see cref="PostProcessShaders.SceneLayers"/>). Needs <see cref="Common"/>, <see cref="Alpha"/>
    /// and <see cref="Air"/> before it.
    /// </summary>
    internal const string ApplyFunctions = """

        uniform sampler2D uShaftLight, uShaftScatter;
        uniform vec2 uShaftAtlas;   // the atlas's size in texels
        uniform vec4 uShaftParams;  // x darkening (strength × the sun's share of the light), y the sky's weight

        vec2 shaftAt(vec2 cell, int k)
        {
            int columns = int(uShaftGrid.w);
            vec2 tile = vec2(float(k % columns), float(k / columns)) * uShaftGrid.xy;
            return (tile + clamp(cell, vec2(0.5), uShaftGrid.xy - 0.5)) / uShaftAtlas;
        }
        float shaftShare(vec2 cell, int k) { return textureLod(uShaftLight, shaftAt(cell, k), 0.0).r; }
        vec4 shaftScatter(vec2 cell, int k) { return textureLod(uShaftScatter, shaftAt(cell, k), 0.0); }

        // dist: to the surface, the water in front of it, or for the sky (sky true) the grid's end.
        vec4 shaftLayer(vec2 vUv, vec3 dir, float dist, bool sky, float waterWeight)
        {
            // The far ends of the slices: s - 1 is where this distance lies between them (-1 the eye, where the share is 1).
            float s = shaftSlice(min(dist, uShaftRange.y)) - 1.0;
            float share = 1.0;
            vec4 air = vec4(0.0, 0.0, 0.0, 1.0);   // the air layer: light scattered towards the eye, transmittance
            if (s > -1.0)
            {
                int last = int(uShaftGrid.z) - 1;
                int k0 = min(int(floor(s)), last);
                float f = clamp(s - float(k0), 0.0, 1.0);
                vec2 cell = vUv * uShaftGrid.xy;
                float s0 = k0 < 0 ? 1.0 : shaftShare(cell, k0);
                float s1 = shaftShare(cell, min(k0 + 1, last));
                share = mix(s0, s1, f);
                if (uShaftAir.x > 0.0)
                {
                    vec4 a0 = k0 < 0 ? vec4(0.0, 0.0, 0.0, 1.0) : shaftScatter(cell, k0);
                    air = mix(a0, shaftScatter(cell, min(k0 + 1, last)), f);
                }
            }
            float dark = uShaftParams.x * (1.0 - share);
            if (sky) return vec4(air.rgb, (1.0 - dark * uShaftParams.y * shaftAlpha(uShaftRange.y)) * air.a);
            vec3 haze = atmoApplyHaze(vec3(0.0), uShaftEye, uShaftEye + dir * dist);
            // (scene − dark · haze) · T + S: the haze darkened where shadowed, then the air layer in front of it.
            vec4 surface = vec4(air.rgb - dark * haze * air.a, air.a);
            vec4 distantSky = vec4(air.rgb, (1.0 - dark * uShaftParams.y * shaftAlpha(uShaftRange.y)) * air.a);
            return mix(distantSky, surface, waterWeight);
        }
        """;
}
