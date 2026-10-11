using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The simple colour model the world view's sky had before the atmosphere (<c>--simple-sky</c>, key B): zenith and horizon colours
/// from the sun's height, a warm band at sunrise and sunset, the sun disc. Also the record that carries the sky's colours to
/// the water shader. The game's sky (<see cref="SkyRenderer"/>, docs/formats/sky.md) fills it from its own numbers.
/// </summary>
public readonly record struct SkyColours(Vector3 Sun, Vector3 Zenith, Vector3 Horizon, Vector3 SunColour, float Twilight)
{
    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }

    public static SkyColours For(Vector3 sun)
    {
        float e = sun.Y;
        float day = Smooth(-0.12f, 0.2f, e);
        float twilight = 1 - Smooth(0, 0.35f, MathF.Abs(e + 0.02f));
        var zenith = Vector3.Lerp(new(0.01f, 0.015f, 0.035f), new(0.20f, 0.40f, 0.72f), day);
        var horizon = Vector3.Lerp(new(0.03f, 0.035f, 0.06f), new(0.70f, 0.76f, 0.82f), day);
        horizon = Vector3.Lerp(horizon, new(0.90f, 0.55f, 0.32f), twilight * 0.55f * Smooth(-0.15f, 0.0f, e));
        var sunColour = Vector3.Lerp(new(1.0f, 0.55f, 0.3f), new(1.0f, 0.96f, 0.9f), Smooth(0, 0.4f, e)) * Smooth(-0.04f, 0.04f, e);
        return new SkyColours(sun, zenith, horizon, sunColour, twilight);
    }

    /// <summary>Light for the world shaders; <paramref name="fogDistance"/> is where the haze is complete.</summary>
    public WorldLighting Lighting(float fogDistance)
    {
        // Below the horizon the shaders still get a direction (lit faces stay lit) but no sun colour.
        var dir = Sun.Y > 0.02f ? Sun : Vector3.Normalize(new Vector3(Sun.X, 0.02f, Sun.Z));
        var ambientSky = (Horizon * 0.7f + Zenith * 0.3f) * 0.5f + new Vector3(0.01f);
        var ambientGround = Horizon * 0.28f + new Vector3(0.01f);
        return new WorldLighting(dir, SunColour, ambientSky, ambientGround, Horizon, fogDistance);
    }
}

/// <summary>
/// The world view's sky, light and haze (docs/formats/sky.md, docs/formats/lighting.md). Game mode (<see cref="Physical"/>): SkyX's
/// sky drawn per pixel with the game's options, the sun's colour taken from SkyX the way the game's sky controller takes it, the
/// deferred lighting pass's image-based light (<c>mp_irradiance.dds</c>, <c>mp_specularity.dds</c>) and per-biome ambient map, and the
/// exposure band of the game's HDR composite (<see cref="MinLuminance"/>, <see cref="MaxLuminance"/>). Simple mode is the old colour
/// model. A shader that wants any of it includes <see cref="AtmosphereShaders.Functions"/> and gets its uniforms from <see cref="Apply"/>.
/// </summary>
public sealed unsafe partial class SkyRenderer : IDisposable
{
    /// <summary>The renderer whose uniforms <see cref="Apply"/> sets (there is one per world view).</summary>
    public static SkyRenderer? Active { get; private set; }

    /// <summary>GLSL for shaders that look up the sky colour for a direction: <c>skyColour(dir, disc)</c> (what the water reflects).</summary>
    public static readonly string SkyFunctions = AtmosphereShaders.Functions + """

        uniform vec3 uSkySun;
        uniform vec3 uSkyZenith;
        uniform vec3 uSkyHorizon;
        uniform vec3 uSkySunColour;
        uniform float uSkyTwilight;
        vec3 skySimple(vec3 dir, bool disc)
        {
            float up = clamp(dir.y, 0.0, 1.0);
            float t = pow(up, 0.45);
            vec3 c = mix(uSkyHorizon, uSkyZenith, t);
            float toward = max(dot(dir, uSkySun), 0.0);
            c += vec3(1.0, 0.6, 0.3) * pow(toward, 6.0) * uSkyTwilight * 0.45 * (1.0 - t);
            c += uSkySunColour * pow(toward, 64.0) * 0.25;
            if (disc) c += uSkySunColour * smoothstep(0.99955, 0.9998, toward) * 3.0;
            return c;
        }
        vec3 skyColour(vec3 dir, bool disc) { return uAtmoParams.x > 0.5 ? atmoSky(dir) : skySimple(dir, disc); }
        """;

    const string Vertex = """
        #version 330 core
        out vec2 vNdc;
        void main()
        {
            vec2 p = vec2((gl_VertexID & 1) * 4.0 - 1.0, (gl_VertexID & 2) * 2.0 - 1.0);
            vNdc = p;
            gl_Position = vec4(p, 1.0, 1.0);
        }
        """;

    const string SimpleFragment = "#version 330 core\n" + """

        in vec2 vNdc;
        out vec4 fragColour;
        uniform mat4 uInverseViewProjection;
        uniform vec3 uSkySun;
        uniform vec3 uSkyZenith;
        uniform vec3 uSkyHorizon;
        uniform vec3 uSkySunColour;
        uniform float uSkyTwilight;
        void main()
        {
            vec4 a = uInverseViewProjection * vec4(vNdc, 0.0, 1.0);
            vec4 b = uInverseViewProjection * vec4(vNdc, 1.0, 1.0);
            vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
            float up = clamp(dir.y, 0.0, 1.0);
            float t = pow(up, 0.45);
            vec3 c = mix(uSkyHorizon, uSkyZenith, t);
            float toward = max(dot(dir, uSkySun), 0.0);
            c += vec3(1.0, 0.6, 0.3) * pow(toward, 6.0) * uSkyTwilight * 0.45 * (1.0 - t);
            c += uSkySunColour * pow(toward, 64.0) * 0.25;
            c += uSkySunColour * smoothstep(0.99955, 0.9998, toward) * 3.0;
            fragColour = vec4(c, 1.0);
        }
        """;

    // SkyX's skydome per pixel (HDR): the scattered light, its night glow and starfield; no sun disc (the game's sun is the Mie lobe).
    static string F(float v) => v.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) + (v == MathF.Floor(v) ? ".0" : "");

    /// <summary>The procedural stars' look, in pixels (the Meitou <c>stars</c> switch): the core's Gaussian sigma, and for stars brighter than <see cref="StarHaloFrom"/> a wider halo (sigma <see cref="StarHaloSigma"/> + <see cref="StarHaloGrow"/> × brightness, a share <see cref="StarHaloShare"/> of the brightness above the threshold, at most <see cref="StarHaloMax"/>).</summary>
    public const float StarSigma = 0.7f, StarCoreReach = 2.2f, StarHaloFrom = 0.4f, StarHaloSigma = 1.8f, StarHaloGrow = 1.6f, StarHaloShare = 0.25f, StarHaloMax = 0.5f;

    // The Meitou night sky (the `stars` switch; docs/formats/sky.md "Meitou night sky"): procedural point stars on the celestial sphere, evaluated per pixel.
    // StarField / NightAtmosphere (Meitou.Data) are the reference for every number here.
    static string Vec3(Vector3 v) => $"vec3({F(v.X)}, {F(v.Y)}, {F(v.Z)})";
    static string Vec4(float a, float b, float c, float d) => $"vec4({F(a)}, {F(b)}, {F(c)}, {F(d)})";

    static readonly string StarShader = $$"""

        uniform vec4 uStarsMeitou;     // x: 1 stars computed, -1 Meitou but the sun is up (none), 0 the game's texture; y: layers drawn; z: seconds (twinkle); w: star gain
        uniform vec4 uCelX, uCelY, uCelZ;   // the celestial axes in world coordinates (the rows of the world -> celestial turn)
        const float STAR_SIGMA = {{F(StarSigma)}};
        const float STAR_GAMMA = {{F(StarField.BrightnessGamma)}};
        const vec3 EXTINCTION = {{Vec3(NightAtmosphere.ExtinctionPerAirMass)}};
        const vec4 STAR_A[3] = vec4[3]({{string.Join(", ", StarField.Layers.Select(l => Vec4(l.Cells, l.Count / (4 * MathF.PI), l.BrightestMagnitude, MathF.Pow(10, l.Slope * (l.FaintestMagnitude - l.BrightestMagnitude)))))}});
        const vec4 STAR_B[3] = vec4[3]({{string.Join(", ", StarField.Layers.Select(l => Vec4(1 / (l.Slope * MathF.Log(10)), 0, l.ReachPixels, 0)))}});
        const vec3 STAR_COLOUR[{{StarColours.TableSize}}] = vec3[{{StarColours.TableSize}}]({{string.Join(", ", StarColours.Table().Select(Vec3))}});

        // pcg3d, as StarField.Pcg3d.
        uvec3 starHash(uvec3 v)
        {
            v = v * 1664525u + 1013904223u;
            v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
            v ^= v >> 16u;
            v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
            return v;
        }
        // The one-word hash (PCG, O'Neill) of a cell id that decides whether the cell has a star: StarField.Pcg1.
        uint starHash1(uint v)
        {
            uint state = v * 747796405u + 2891336453u;
            uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
            return (word >> 22u) ^ word;
        }
        // The cube face of a direction (the largest component's axis) and the position on it, -1..1: StarField.Face.
        int starFace(vec3 d, out vec2 uv, out float m)
        {
            vec3 a = abs(d);
            if (a.x >= a.y && a.x >= a.z) { m = a.x; uv = d.yz / m; return d.x > 0.0 ? 0 : 1; }
            if (a.y >= a.z) { m = a.y; uv = d.xz / m; return d.y > 0.0 ? 2 : 3; }
            m = a.z; uv = d.xy / m;
            return d.z > 0.0 ? 4 : 5;
        }
        vec3 starFaceDir(int face, vec2 q)
        {
            float s = (face & 1) == 0 ? 1.0 : -1.0;
            int k = face >> 1;
            return k == 0 ? vec3(s, q.x, q.y) : (k == 1 ? vec3(q.x, s, q.y) : vec3(q.x, q.y, s));
        }
        vec2 starPick(vec3 v, int k) { return k == 0 ? v.yz : (k == 1 ? v.xz : v.xy); }
        float starComp(vec3 v, int k) { return k == 0 ? v.x : (k == 1 ? v.y : v.z); }

        // The light of the procedural stars at one pixel (texture units): the two or four cells of each layer nearest the pixel, a star a Gaussian of
        // STAR_SIGMA pixels with its energy kept (peak = brightness / (2 pi sigma^2)), the brightest with a wider halo. toPixels maps the face's units to pixels.
        vec3 starPoints(int face, vec2 uv, mat2 toPixels, int layers, float seconds, float twinkle)
        {
            vec3 sum = vec3(0.0);
            float pixelUv = inversesqrt(abs(toPixels[0].x * toPixels[1].y - toPixels[1].x * toPixels[0].y));   // face units per pixel
            for (int L = 0; L < layers; L++)
            {
                vec4 A = STAR_A[L], B = STAR_B[L];
                float n = A.x;
                int ni = int(n);
                vec2 g = (uv * 0.5 + 0.5) * n;
                vec2 cell = floor(g);
                vec2 fr = g - cell;
                ivec2 base = ivec2(cell);
                // A neighbouring cell's star can reach this pixel only from within B.z pixels of the shared edge (a star's spot is cut off there): look at the
                // neighbours on the sides that near, usually none or one, up to the diagonal one. A bright layer's cells are large, its reach (the halo) long.
                float reach = min(0.5, B.z * pixelUv * n * 0.5);
                ivec2 nb = ivec2(fr.x < reach ? -1 : (fr.x > 1.0 - reach ? 1 : 0), fr.y < reach ? -1 : (fr.y > 1.0 - reach ? 1 : 0));
                for (int k = 0; k < 4; k++)
                {
                    if (((k & 1) != 0 && nb.x == 0) || ((k & 2) != 0 && nb.y == 0)) continue;
                    ivec2 ic = base + ivec2((k & 1) != 0 ? nb.x : 0, (k & 2) != 0 ? nb.y : 0);
                    if (ic.x < 0 || ic.y < 0 || ic.x >= ni || ic.y >= ni) continue;
                    vec2 centre = (vec2(ic) + 0.5) * (2.0 / n) - 1.0;
                    // The cell's chance of a star (StarField.CellChance) against a one-word hash of the cell (StarField.Pcg1); the full three-word hash only for the stars.
                    float w3 = 1.0 + dot(centre, centre);
                    float chance = A.y * (4.0 / (n * n)) * inversesqrt(w3) / w3;
                    float draw = float(starHash1(((uint(L * 6 + face) * 256u + uint(ic.y)) * 256u + uint(ic.x))) >> 16u) * (1.0 / 65536.0);
                    if (draw >= chance) continue;
                    uvec3 h = starHash(uvec3(uint(ic.x), uint(ic.y), uint(face + 8 * L)));
                    vec2 j =vec2(float(h.y & 0xFFFFu), float(h.y >> 16u)) * (1.0 / 65536.0);
                    vec2 lo = vec2(ic.x == 0 ? {{F(StarField.FaceEdgeMargin)}} : {{F(StarField.CellMargin)}}, ic.y == 0 ? {{F(StarField.FaceEdgeMargin)}} : {{F(StarField.CellMargin)}});
                    vec2 hi = vec2(ic.x == ni - 1 ? {{F(1 - StarField.FaceEdgeMargin)}} : {{F(1 - StarField.CellMargin)}}, ic.y == ni - 1 ? {{F(1 - StarField.FaceEdgeMargin)}} : {{F(1 - StarField.CellMargin)}});
                    vec2 suv = (vec2(ic) + mix(lo, hi, j)) * (2.0 / n) - 1.0;
                    vec2 px = toPixels * (suv - uv);
                    float r2 = dot(px, px);
                    if (r2 > B.z * B.z) continue;   // out of the layer's reach: no need for the star's brightness
                    float mag = A.z + log(1.0 + float(h.z & 0xFFFFu) * (1.0 / 65536.0) * (A.w - 1.0)) * B.x;
                    float b = pow(10.0, -0.4 * STAR_GAMMA * mag);
                    if (r2 > (b > {{F(StarHaloFrom)}} ? 400.0 : {{F(StarCoreReach * StarCoreReach)}})) continue;
                    float sigma2 = STAR_SIGMA * STAR_SIGMA;
                    float e = b * exp(-r2 / (2.0 * sigma2)) / (6.2831853 * sigma2);
                    if (b > {{F(StarHaloFrom)}})
                    {
                        float hs = {{F(StarHaloSigma)}} + {{F(StarHaloGrow)}} * b, hs2 = hs * hs;
                        e += min((b - {{F(StarHaloFrom)}}) * {{F(StarHaloShare)}}, {{F(StarHaloMax)}}) * b * exp(-r2 / (2.0 * hs2)) / (6.2831853 * hs2);
                    }
                    // Twinkle: low stars only (the amplitude is 0 high up), two slow sines at random rates, fainter stars more.
                    if (twinkle > 0.0)
                    {
                        float p1 = float(h.x >> 16u) * (1.0 / 65536.0), p2 = fract(p1 * 37.17);
                        e *= max(1.0 + twinkle * (1.0 - 0.5 * min(b, 1.0)) * (0.6 * sin(seconds * (4.0 + 5.0 * p1) + 40.0 * p1) + 0.4 * sin(seconds * (9.0 + 7.0 * p2) + 40.0 * p2)), 0.0);
                    }
                    sum += STAR_COLOUR[int(float(h.z >> 16u) * ({{F(StarColours.TableSize / 65536f)}}))] * e;
                }
            }
            return sum;
        }

        """;

    static readonly string SkyFragment = "#version 330 core\n" + AtmosphereShaders.Functions + StarShader + $$"""

        in vec2 vNdc;
        out vec4 fragColour;
        uniform mat4 uInverseViewProjection;
        uniform vec4 uSkyExtra;        // y: cloud density c (0 skips the cloud pass), z: the starfield's shift (texture units, wrapped); x, w unused
        uniform vec4 uPlanetBody0, uPlanetBody1;   // the game's two planets (docs/formats/sky.md "Planets"): xyz towards the centre, w the sine of the angular radius
        uniform vec4 uPlanetSpin;      // cos and sin of each planet's turn about y: (cos 0, sin 0, cos 1, sin 1)
        uniform vec4 uCloudLight;      // rgb: zenithLight, a: Darkness
        uniform vec4 uCloudSun;        // rgb: sunColour.rgb, a: DensityOffset
        uniform vec4 uCloudWind;       // xy: the wind offset x 0.00005 (wrapped to 0..1)
        uniform vec4 uCloudMeitou;     // x: 2 prepared volume, 1 legacy lit layer, 0 Faithful; yzw legacy parallax
        uniform vec4 uCloudKey;        // Meitou clouds: rgb the key light (sun, and the planet at night) in sunColour's unit
        uniform vec4 uCloudKeyDir;     // Meitou clouds: xyz towards the key light
        uniform vec4 uCloudAmbient;    // Meitou clouds: rgb the sky's mean light on the clouds (before the exposure's square root)
        uniform sampler2D uStars, uPlanet0, uPlanet1, uClouds, uCloudsNormal, uCloudsTile, uCloudsSmooth, uCloudsTileSmooth;   // the last two: the Meitou clouds' smoothed copies (MeitouClouds.SmoothRed)
        uniform sampler2D uVolumeImage;
        uniform vec4 uHas;             // stars, planet 0, cloud textures (all three), planet 1 present
        // moon.hlsl: planet01.mesh's texture lit by the real sun direction, plus the skydome's Rayleigh colour towards it (no Mie, no night glow), opaque.
        // The sphere is hit analytically; its object normal (the mesh turns about y) gives the mesh's UV sphere coordinate. rgba: colour and coverage.
        vec4 planet(vec3 dir, vec4 body, float cs, float sn, sampler2D tex)
        {
            float b = dot(dir, body.xyz), sr = body.w;
            float s = sqrt(max(1.0 - b * b, 0.0));   // the sine of the angle from the centre
            float t = b - sqrt(max(sr * sr - s * s, 0.0));
            vec3 n = (dir * t - body.xyz) / sr;
            n = normalize(n);
            vec3 o = vec3(n.x * cs - n.z * sn, n.y, n.x * sn + n.z * cs);
            vec2 uv = vec2({{F(SkyPlanet.UOffset)}} - atan(o.z, o.x) / (2.0 * ATMO_PI), acos(clamp(o.y, -1.0, 1.0)) / ATMO_PI);
            // The u seam: the gradients of whichever of u and u shifted by a half turn does not jump here (else the seam's pixels take the smallest mip).
            float u2 = fract(uv.x + 0.5) - 0.5;
            vec2 gx = vec2(abs(dFdx(uv.x)) < abs(dFdx(u2)) ? dFdx(uv.x) : dFdx(u2), dFdx(uv.y));
            vec2 gy = vec2(abs(dFdy(uv.x)) < abs(dFdy(u2)) ? dFdy(uv.x) : dFdy(u2), dFdy(uv.y));
            float cover = clamp((sr - s) / max(fwidth(s), 1e-6) + 0.5, 0.0, 1.0) * step(0.0, b);
            if (cover <= 0.0) return vec4(0.0);
            vec3 albedo = textureGrad(tex, uv, gx, gy).rgb;
            vec3 d = normalize(vec3(dir.x, max(dir.y, 0.0), dir.z));
            vec3 ray = d + vec3(0.0, SKYX_INNER - SKYX_CAMERA_Y, 0.0);
            float far = length(ray), thickness;
            ray /= far;
            vec3 scatter = SKYX_EXPOSURE * skyxRayleighPhase(dot(uAtmoSun.xyz, ray)) * SKYX_RAYLEIGH * skyxInScatter(ray, far, uAtmoSun.xyz, thickness);
            return vec4(albedo * clamp(dot(n, uAtmoSun.xyz), 0.0, 1.0) + scatter, cover);
        }
        // The cloud layer's density and tile: the game's textures, or with the Meitou clouds their smoothed copies (MeitouClouds.SmoothRed: the BC1 blocks' steps
        // showed as grain and contour bands once the clouds were lit). The condition is on a uniform, so the derivatives stay defined.
        float cloudsRed(vec2 uv) { if (uCloudMeitou.x > 0.5) return texture(uCloudsSmooth, uv).r; return texture(uClouds, uv).r; }
        float cloudsTileRed(vec2 uv) { if (uCloudMeitou.x > 0.5) return texture(uCloudsTileSmooth, uv).r; return texture(uCloudsTile, uv).r; }
        void main()
        {
            vec4 a = uInverseViewProjection * vec4(vNdc, 0.0, 1.0);
            vec4 b = uInverseViewProjection * vec4(vNdc, 1.0, 1.0);
            vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
            float night;
            vec3 col = atmoSky(dir, night);
            vec3 skyBehind = col;   // the Meitou clouds' share of ambient from the sky right behind them
            float aboveHorizon = smoothstep(-0.02, 0.06, dir.y);

            // Stars: SkyX_Starfield.dds (SkyX's HDR form: nightmult · texture · (0.35 + saturate(−sunY · 0.45)) · 2) on the dome's own texture coordinates
            // (Starfield.Uv: azimuthal-equidistant round the zenith, the horizon a circle of radius 0.4 round (0.4, 0.4)), shifted along both axes with the
            // game hours. Sampled outside the branch below so the gradients are defined (t · normalize(xz) is continuous, also at the zenith).
            // The Meitou stars (uStarsMeitou.x: 1) replace that texture with the procedural sky of the celestial sphere (docs/formats/sky.md "Meitou night sky"): the
            // direction turned into celestial coordinates, its cube face position and the map from face units to pixels (all with derivatives, so outside the
            // non-uniform branches; these conditions are on uniforms), then the point stars, dimmed and reddened by the air.
            vec3 stars = vec3(0.0);
            bool computeStars = uStarsMeitou.x > 0.5;
            int starFaceIndex = 0;
            vec2 starUv = vec2(0.0);
            mat2 starToPixels = mat2(1.0);
            if (computeStars)
            {
                vec3 cdir = vec3(dot(uCelX.xyz, dir), dot(uCelY.xyz, dir), dot(uCelZ.xyz, dir));
                vec3 dcx = dFdx(cdir), dcy = dFdy(cdir);
                float starMajor;
                starFaceIndex = starFace(cdir, starUv, starMajor);
                int sk = starFaceIndex >> 1;
                float sgn = (starFaceIndex & 1) == 0 ? 1.0 : -1.0;
                vec2 dux = (starPick(dcx, sk) - starUv * (sgn * starComp(dcx, sk))) / starMajor;
                vec2 duy = (starPick(dcy, sk) - starUv * (sgn * starComp(dcy, sk))) / starMajor;
                float det = dux.x * duy.y - duy.x * dux.y;
                starToPixels = abs(det) > 1e-14 ? mat2(duy.y, -dux.y, -duy.x, dux.x) / det : mat2(1e6);
            }
            else if (uStarsMeitou.x == 0.0)
            {
                float zenith = acos(clamp(dir.y, 0.0, 1.0)) / (0.5 * ATMO_PI);
                vec2 around = dot(dir.xz, dir.xz) > 1e-12 ? normalize(dir.xz) : vec2(0.0);
                stars = texture(uStars, vec2({{F(Starfield.Centre)}}) + {{F(Starfield.Radius)}} * zenith * around + uSkyExtra.z).rgb;
            }
            if (night > 0.0 && dir.y > 0.0 && (computeStars || uHas.x > 0.5))
            {
                if (computeStars)
                {
                    float altitude = degrees(asin(clamp(dir.y, 0.0, 1.0)));
                    float airMass = 1.0 / (dir.y + 0.50572 * pow(altitude + 6.07995, -1.6364));
                    vec3 transmission = exp(-EXTINCTION * (airMass - 1.0)) * smoothstep({{F(NightAtmosphere.HorizonLow)}}, {{F(NightAtmosphere.HorizonHigh)}}, altitude);
                    float twinkle = clamp((airMass - {{F(NightAtmosphere.ScintillationFreeAirMass)}}) / {{F(NightAtmosphere.ScintillationRange)}}, 0.0, 1.0) * {{F(NightAtmosphere.ScintillationMax)}};
                    stars = starPoints(starFaceIndex, starUv, starToPixels, int(uStarsMeitou.y), uStarsMeitou.z, twinkle) * uStarsMeitou.w * transmission;
                }
                col += night * aboveHorizon * stars * (0.35 + clamp(-uAtmoSun.y * 0.45, 0.0, 1.0)) * 2.0;
            }
            // The planets: render queue 6 with the priorities 1 and 2, after the skydome (queue 5) and before the cloud entity (queue 6, priority 100), so the
            // clouds pass in front of them. The large one first, then the small one, which covers the large one's edge where the discs overlap (about half a degree).
            if (uHas.y > 0.5)
            {
                vec4 p = planet(dir, uPlanetBody0, uPlanetSpin.x, uPlanetSpin.y, uPlanet0);
                col = mix(col, p.rgb, p.a);
            }
            if (uHas.w > 0.5)
            {
                vec4 p = planet(dir, uPlanetBody1, uPlanetSpin.z, uPlanetSpin.w, uPlanet1);
                col = mix(col, p.rgb, p.a);
            }
            // Clouds: SkyX's cloud layer on the dome (SkyX_Clouds.hlsl, docs/formats/clouds.md), after the sky, the stars and the planets;
            // alpha-blended over them in HDR. The dome direction below the horizon is evaluated at a hair above it (alpha is then the horizon value).
            if (uCloudMeitou.x > 1.5)
            {
                vec4 cloud = texture(uVolumeImage, vNdc * 0.5 + 0.5);
                col = col * (1.0 - cloud.a) + cloud.rgb;
                // Match the far land's Kenshi haze target. The planar layer supplied this horizon band;
                // a finite volume misses shallow rays, which otherwise exposes a crisp seam at hazed mountains.
                vec3 haze = mix(hazeTarget(dir * max(uAtmoFog.w, uAtmoHaze.z)), uAtmoHazeCloud.rgb, uAtmoHazeCloud.a);
                // Full cloud cover is opaque at the horizon even with Meitou surface haze at 0.93.
                float closure = max(min(uAtmoAltitude.y, 1.0), uAtmoHazeCloud.a);
                float horizon = (1.0 - smoothstep(0.05, 0.15, dir.y)) * (1.0 - uAtmoAltitude.x)
                              * closure * smoothstep(0.0, 0.05, uSkyExtra.y);
                col = mix(col, haze, horizon);
            }
            else if (uHas.z > 0.5 && uSkyExtra.y > 0.0)
            {
                vec3 d = normalize(vec3(dir.x, max(dir.y, 0.0005), dir.z));
                float o = uCloudSun.w;
                const float MULT = {{F(CloudLayer.DensityMultiplier)}}, SCALE = {{F(CloudLayer.Scale)}}, HEIGHT = {{F(CloudLayer.Height)}};
                vec2 wind = uCloudWind.xy;
                const vec2 SHIFT = vec2({{F(CloudLayer.SecondLookupShift.X)}}, {{F(CloudLayer.SecondLookupShift.Y)}});
                // The plane hit: the cloud point is d · height / d.y, the texture coordinate its xz · scale.
                vec2 uv = d.xz * (HEIGHT / d.y) * SCALE * uCloudMeitou.y + uCloudMeitou.zw;
                float density = cloudsRed(uv + wind);
                vec3 normal = -(2.0 * texture(uCloudsNormal, uv + wind).rgb - 1.0);
                normal = vec3(normal.x, normal.z, normal.y);   // the shader swaps y and z
                density = clamp((density + o) * MULT, 0.0, 1.0);
                // The fake volume: the direction bent along the normal map, the plane raised where the cloud is thin.
                vec3 nd = normalize(d + {{F(CloudLayer.VolumetricDisplacement)}} * d.y * vec3(normal.x, 0.0, normal.z));
                float vh = (HEIGHT + HEIGHT * (1.0 - density) * {{F(CloudLayer.HeightVolume)}} * d.y) / nd.y;
                uv = nd.xz * vh * SCALE * uCloudMeitou.y + uCloudMeitou.zw;
                density = (cloudsRed(uv + wind + SHIFT) + o) * MULT;
                float tile = cloudsTileRed(uv - wind);
                density += tile * {{F(CloudLayer.TileWeight)}};
                vec3 pixel = uCloudLight.rgb + uCloudSun.rgb * (1.0 - density * 0.1);
                float top = 1.0, dark = 1.0;   // the colour's ceiling (the game's 1, saturate; the Meitou clouds' brighter edges towards the sun) and the share of Darkness
                if (uCloudMeitou.x > 0.5)
                {
                    // Meitou clouds (MeitouClouds in Meitou.Data, docs/formats/clouds.md "Meitou clouds"): the same layer lit by the key light through the cloud's
                    // depth towards it, the lesser of the path up through the layer (the pixel's density over the light's height) and the path sideways across it
                    // (density taps along the light's direction on the plane), with a two-lobed phase, plus the sky's light as the ambient.
                    vec3 L = uCloudKeyDir.xyz;
                    vec2 lxz = dot(L.xz, L.xz) > 1e-8 ? normalize(L.xz) : vec2(0.0);
                    vec2 at = uv + wind + SHIFT;
                    float side = 0.0;
                    for (int k = 1; k <= {{MeitouClouds.LightSteps}}; k++)
                        side += clamp((texture(uCloudsSmooth, at + lxz * ({{F(MeitouClouds.LightStep)}} * float(k))).r + o) * MULT, 0.0, 1.0);
                    float thick = max(density, 0.0);
                    // A light below the layer (the sun just set, the cloud still sees it) shines on the underside the eye sees: a short path.
                    float up = L.y > 0.0 ? thick * min(1.0 / L.y, {{F(MeitouClouds.MaxSlant)}}) : thick * 0.25;
                    float across = (side + 0.5 * clamp(thick, 0.0, 1.0)) * {{F(MeitouClouds.SidePath)}};
                    float depth = min(up, across);
                    float through = {{F(1 - MeitouClouds.MultipleShare)}} * exp(-{{F(MeitouClouds.Extinction)}} * depth)
                                  + {{F(MeitouClouds.MultipleShare)}} * exp(-{{F(MeitouClouds.Extinction * MeitouClouds.MultipleExtinction)}} * depth);
                    float c = dot(d, L);
                    float phase = {{F(MeitouClouds.ForwardShare)}} * {{F(1 - MeitouClouds.ForwardG * MeitouClouds.ForwardG)}} / pow(max({{F(1 + MeitouClouds.ForwardG * MeitouClouds.ForwardG)}} - {{F(2 * MeitouClouds.ForwardG)}} * c, 1e-6), 1.5)
                                + {{F(1 - MeitouClouds.ForwardShare)}} * {{F(1 - MeitouClouds.BackwardG * MeitouClouds.BackwardG)}} / pow(max({{F(1 + MeitouClouds.BackwardG * MeitouClouds.BackwardG)}} - {{F(2 * MeitouClouds.BackwardG)}} * c, 1e-6), 1.5);
                    vec3 ambient = mix(uCloudAmbient.rgb, skyBehind / sqrt(SKYX_EXPOSURE), {{F(MeitouClouds.AmbientFromSky)}});
                    ambient = mix(ambient, vec3(dot(ambient, vec3(0.2126, 0.7152, 0.0722))), uCloudAmbient.a)
                                 * (1.0 - {{F(MeitouClouds.BaseDarkening)}} * clamp(0.5 * thick, 0.0, 1.0));
                    pixel = uCloudKey.rgb * (through * phase * {{F(MeitouClouds.KeyGain)}}) + ambient * {{F(MeitouClouds.AmbientGain)}};
                    top = 8.0;
                    dark = {{F(MeitouClouds.DarknessShare)}};
                }
                // The horizon band: from 0.05 to 0.15 of d.y the layer gives way to the uniform alpha o + 0.5 (horizonClouds.a).
                float band = clamp(10.0 * clamp(d.y - {{F(CloudLayer.DistanceAttenuation)}}, 0.0, 1.0), 0.0, 1.0);
                density += band;
                pixel *= 1.0 - clamp(density, 0.0, 1.0) * uCloudLight.a * dark;
                // Below d.y = 0.05 the alpha is the uniform horizon value, but the colour above still followed the texture lookups, whose uv runs to hundreds of units
                // there (height / d.y): minified to a few texels they sparkle (a row of white ticks along the horizon). The colour gives way to the plain
                // horizonClouds colour below d.y = 0.05 (from 0.01 up; above 0.05 the layer is untouched): the game's own horizon cloud colour, darkened
                // like the layer (an undarkened target drew a white band at c = 1, docs/formats/sky.md), and the colour the haze is pulled to.
                vec3 plain = clamp(uCloudLight.rgb + uCloudSun.rgb * (1.0 - 0.1 * (o + 0.2) * MULT), 0.0, 1.0) * (1.0 - uCloudLight.a);
                pixel = mix(plain, pixel, clamp((d.y - 0.01) / 0.04, 0.0, 1.0));
                float alpha = density * clamp(1.0 - tile + o, 0.0, 1.0);
                alpha = mix(o + 0.5, alpha, band);
                col = mix(col, clamp(pixel, 0.0, top) * sqrt(SKYX_EXPOSURE), clamp(alpha, 0.0, 1.0));
            }
            // The game's fog pass runs over the sky too (post/fog.hlsl atmosphere_fog_fs): a pixel with no geometry has distance = farClip, its atmosphere term is
            // dropped and the weather's term alone remains, alpha = ease-in-out(saturate(farClip / fog distance)) x fogColour.a, colour = fog colour x sunColour.w
            // (uAtmoFogColour). So a weather whose fog completes before the far clip (dust storms 25000, Ashlands 35000, farClip 50000) replaces the whole sky with a flat fog colour.
            if (uAtmoFog.z > 0.0 && uAtmoHaze.w > 0.0)
            {
                float amount = clamp(uAtmoFog.w * uAtmoHaze.w, 0.0, 1.0);
                float curve = (amount < 0.5 ? 2.0 * amount * amount : 1.0 - 2.0 * (amount - 1.0) * (amount - 1.0)) * uAtmoFog.z;
                col = mix(col, uAtmoFogColour, curve);
            }
            fragColour = vec4(col, 1.0);
        }
        """;

    /// <summary>The native GPU API (phase 8 stage 2: the sky makes no GL call; docs/renderer-native.md 8.6).</summary>
    public GpuContext Gpu { get; }
    // Native programs (docs/renderer-native.md 7.1, wave 3 agent D): the same SPIR-V as the GL programs they replace.
    readonly SkyProg simple, sky;
    // Native textures with the GL sampler state their GL versions had (phase 8 stage 2); null when the file was not found.
    SampledImage? starsTexture, cloudsTexture, cloudsNormalTexture, cloudsTileTexture, cloudsSmooth, cloudsTileSmooth, irradianceCube, specularCube, ambientMap;
    // The game's two planets' textures (SkyPlanet.All order); null when the file was not found.
    readonly SampledImage?[] planetTextures = new SampledImage?[SkyPlanet.All.Length];
    readonly PassTimer skyTimer;
    readonly List<double> gpuSamples = [];
    double gpuTotal;
    int gpuCount;

    /// <summary>The game's sky and light (default) or the old simple colour model.</summary>
    public bool Physical { get; set; } = true;
    /// <summary>Haze: Kenshi's own (default, docs/formats/sky.md "Haze") or the physical integral over SkyX's air.</summary>
    public bool KenshiHaze { get; set; } = true;
    /// <summary>
    /// Kenshi's far distance D (<c>view distance × 10</c>, 50000 with the install's setting); its haze ramps in between 0.06 D and
    /// 0.6 D. Independent of the viewer's far clip: past it the game's own formula goes on.
    /// </summary>
    public float HazeDistance { get; set; } = Meitou.Data.World.KenshiHaze.FarDistance(Meitou.Data.World.KenshiHaze.ViewDistanceSetting);
    /// <summary>
    /// The viewer's haze strength (not the game's; 1 = the game's haze): scales how far the atmosphere haze is blended in (the game's ramp,
    /// or the physical haze's amount), not the weather's fog.
    /// </summary>
    public float HazeStrength { get; set; } = Enhancements.MeitouHazeStrength;
    /// <summary>
    /// The Meitou <c>night</c> switch, "night air" (viewer, not the game's; docs/formats/sky.md "Night air"): the game's haze colour is SkyX's sunlit
    /// in-scattering, which is black once the sun is down, so its ramp turns everything past 0.6 D black against a black sky. With this on the air has a
    /// faint radiance of its own at night (<see cref="Meitou.Data.World.NightAir"/>: airglow and the planet's light scattered by the air): the haze
    /// (its ramp the game's) fades the far land into it and the sky adds the same colour above the horizon, falling off with the height, in place of
    /// SkyX's own night glow. False: the game's black haze and glow.
    /// </summary>
    public bool NightAir { get; set; } = true;

    /// <summary>
    /// The Meitou <c>planetshine</c> switch (viewer, not the game's; docs/formats/sky.md "Planetshine"): the big planet lights the land at night. The
    /// published light (direction, colour: <c>uAtmoLight</c>, <c>uAtmoSunLight</c>, the <see cref="WorldLighting"/>, the shadows' direction) becomes the sun's
    /// and the planet's together, the planet's alone once the sun is down; the ambient and the sky stay the game's. False: the game's.
    /// </summary>
    public bool Planetshine { get; set; } = true;

    /// <summary>The planetshine's strength: a multiple of the physical value (<see cref="Meitou.Data.World.Planetshine"/>).</summary>
    public float PlanetshineStrength { get; set; } = Enhancements.MeitouPlanetshineStrength;

    /// <summary>The planet's mean colour (<see cref="Meitou.Data.World.Planetshine.MeanAlbedo(Meitou.Data.Textures.DdsFile)"/>), read when the texture loads.</summary>
    public Vector3 PlanetAlbedo { get; private set; } = new(Meitou.Data.World.Planetshine.FallbackAlbedo);

    /// <summary>The planet's light counts as light for the shadows when its luminance is above this share of the reference sun light (a new planet casts nothing worth drawing).</summary>
    const float PlanetLitFloor = 1e-4f;

    /// <summary>
    /// The sun height the shadow pass tests against its cut-off (<see cref="ShadowPass.MinSunHeight"/>): the real sun's, but while the planet
    /// is lighting the land (planetshine on, some light of it) never under the horizon, so the shadows go on all night along the published direction.
    /// </summary>
    public float ShadowSunHeight => state.PlanetLit ? MathF.Max(state.Sun.Y, 0) : state.Sun.Y;

    /// <summary>
    /// The eye's height above the highest ground or water within the game's longest camera boom (<see cref="KenshiCamera.MaxDistance"/>) around it,
    /// set each frame by <see cref="SetEye"/>. The game's camera never gets more than <see cref="KenshiCamera.MaxHeightAbovePivot"/> above its
    /// pivot, which sits on the ground within that distance, so in-game this stays under about 1840 (plus a roof the pivot may stand on).
    /// </summary>
    public float EyeClearance { get; private set; }
    /// <summary>
    /// The viewer's height band (its own choice; no game behaviour exists up there) over which the game's haze, which measures from SkyX's
    /// fixed eye near the ground, gives way to the physical haze: none below <see cref="AltitudeBandStart"/>, all of it above <see cref="AltitudeBandEnd"/>.
    /// </summary>
    public const float AltitudeBandStart = 5100, AltitudeBandEnd = 18700;
    /// <summary>0 within the game's camera heights, rising to 1 across the altitude band (smoothstep of <see cref="EyeClearance"/>).</summary>
    public float AltitudeWeight
    {
        get
        {
            float t = Math.Clamp((EyeClearance - AltitudeBandStart) / (AltitudeBandEnd - AltitudeBandStart), 0, 1);
            return t * t * (3 - 2 * t);
        }
    }

    /// <summary>
    /// Measures the eye's <see cref="EyeClearance"/>: its height above the highest of the ground (<paramref name="groundAt"/>) and
    /// <paramref name="floor"/> (the water level, or −∞) at the eye and on two rings round it out to the game's longest boom.
    /// </summary>
    public void SetEye(Vector3 eye, Func<float, float, float> groundAt, float floor)
    {
        float top = MathF.Max(groundAt(eye.X, eye.Z), floor);
        foreach (float r in (ReadOnlySpan<float>)[KenshiCamera.MaxDistance * 0.5f, KenshiCamera.MaxDistance])
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4;
                top = MathF.Max(top, groundAt(eye.X + r * MathF.Cos(a), eye.Z + r * MathF.Sin(a)));
            }
        EyeClearance = eye.Y - top;
    }

    /// <summary>Game hours since the load (the starfield's shift: SkyX's time runs at game speed, from the sky's creation, docs/formats/sky.md "Stars").</summary>
    public double StarHours { get; set; }
    /// <summary>The game's day and hour, which turn the planets (<see cref="SkyPlanet.Spin"/>) and, with the Meitou <c>stars</c> switch, the stars (<see cref="CelestialSphere"/>).</summary>
    public int Day { get; set; }
    public float Hour { get; set; } = 13;

    /// <summary>The sun's path (latitude, sunrise, sunset): the celestial pole of the Meitou stars is the axis it turns about. The game's CONSTANTS by default (54°, 5, 23).</summary>
    public SkyClock Clock { get; set; } = new(54, 5, 23);

    /// <summary>
    /// The Meitou <c>stars</c> switch (viewer design, not the game's; docs/formats/sky.md "Meitou night sky"): a procedural night sky, point stars
    /// turning about the sun's axis, in place of the game's starfield texture. False: the game's texture on its own mapping.
    /// </summary>
    public bool MeitouStars
    {
        get => meitouStars;
        set => meitouStars = value;
    }
    bool meitouStars = true;

    /// <summary>
    /// The Meitou <c>clouds</c> switch: a world-anchored procedural volume lit by the sun and planet (docs/render-clouds.md).
    /// False: the game's flat cloud layer. Call PrepareClouds before opening each scene host.
    /// </summary>
    public bool LitClouds { get; set; } = true;

    /// <summary>
    /// The Meitou <c>cloudshadows</c> switch (viewer design; the game has no cloud shadows, docs/formats/clouds.md "Meitou cloud shadows"): the cloud layer, anchored
    /// to the world at <see cref="Meitou.Data.World.MeitouClouds.PlaneHeight"/>, shades the sun light on the land and the water. False: none.
    /// </summary>
    public bool CloudShadows { get; set; } = true;

    /// <summary>The eye in world units, for the Meitou clouds' parallax (the sky pass's matrix is a rotation only); the frame sets it.</summary>
    public Vector3 Eye { get; set; }

    /// <summary>Cloud shadows: strength, density offset (legacy) or coverage (volume), base height, and mode (0 legacy / 1 volume).</summary>
    Vector4 CloudShadowUniform()
    {
        if (VolumeShadows)
            return new Vector4(MeitouClouds.ShadowAt(state.Sun.Y), CloudDensity, VolumetricClouds.Bottom, 1);
        bool on = CloudShadows && Physical && cloudsSmooth is not null && cloudsTileSmooth is not null && CloudDensity > 0;
        float strength = on ? MeitouClouds.ShadowAt(state.Sun.Y) : 0;
        return new Vector4(strength, CloudLayer.DensityOffset(CloudDensity), MeitouClouds.PlaneHeight, 0);
    }

    /// <summary>The gain on the Meitou point stars' light, in the starfield texture's units.</summary>
    public float StarGain { get; set; } = DefaultStarGain;
    public const float DefaultStarGain = 20;

    /// <summary>Seconds on the viewer's clock for the stars' twinkle (set by the frame; 0 holds them still for a picture).</summary>
    public float StarSeconds { get; set; }

    /// <summary>How many of <see cref="StarField.Layers"/> the sky draws: all on a discrete GPU, the brighter ones on an integrated one (the faint background is sub-pixel).</summary>
    public int StarLayers { get; set; } = StarField.Layers.Length;

    /// <summary>The physical haze only: world units in one density scale height of SkyX's air (the game's world unit is Unknown; a viewer choice).</summary>
    public float ScaleHeightUnits { get; set; } = 40000;
    public SkyWeather Weather { get; set; } = SkyWeather.Default;
    /// <summary>Test override of the cloud density c (<c>--clouds</c>); wins over <see cref="CloudDensityInput"/> and the weather's.</summary>
    public float? CloudCoverage { get; set; }
    /// <summary>
    /// The cloud density c the weather system gives (after the sky's 30 s transition), when it drives the sky; null takes the forced
    /// <see cref="Weather"/> record's. Clamped to 0..1 as the game's sky controller does.
    /// </summary>
    public float? CloudDensityInput { get; set; }
    /// <summary>The sky colour multiplier the weather system gives (after the transition); null takes the forced <see cref="Weather"/> record's.</summary>
    public Vector3? SkyColourMultiplierInput { get; set; }
    /// <summary>The clouds' drift velocity (the weather's wind direction.xz × speed, world units per second); <see cref="StepClouds"/> accumulates it.</summary>
    public Vector2 CloudWind { get; set; }
    /// <summary>The cloud density in use, 0..1.</summary>
    public float CloudDensity => Math.Clamp(CloudCoverage ?? CloudDensityInput ?? Weather.CloudDensity, 0, 1);
    /// <summary>The sky colour multiplier in use.</summary>
    public Vector3 SkyColourMultiplier => SkyColourMultiplierInput ?? Weather.SkyColourMultiplier;
    /// <summary>
    /// The weather fog the weather system gives (the camera's blend over up to four regions, <see cref="Meitou.Data.World.WeatherState"/>): the weight 0..1 (the game's
    /// <c>fogColour.a</c>), the colour and the distance where it is complete; null takes the forced <see cref="Weather"/> record's.
    /// </summary>
    public (float Weight, Vector3 Colour, float Distance)? FogInput { get; set; }

    (double X, double Z) cloudOffset;
    readonly Stopwatch cloudClock = new();
    /// <summary>The accumulated wind offset of the cloud layer (world units, the sky controller's; the shader shifts the textures by it × 0.00005).</summary>
    public (double X, double Z) CloudOffset => cloudOffset;

    /// <summary>Advances the clouds' drift by <paramref name="dt"/> seconds of the caller's frame clock (0 holds them still).</summary>
    public void StepClouds(float dt) => cloudOffset = CloudLayer.Advance(cloudOffset, CloudWind, dt);

    /// <summary>
    /// Advances the drift by the real time since the last call (at most 0.25 s, game speed 1), or holds it still for a still picture
    /// (<paramref name="held"/>: <c>--screenshot</c>), the viewer's frame clock as the heat haze's.
    /// </summary>
    public void StepClouds(bool held)
    {
        float dt = held || !cloudClock.IsRunning ? 0 : (float)Math.Min(cloudClock.Elapsed.TotalSeconds, 0.25);
        cloudClock.Restart();
        StepClouds(dt);
    }
    /// <summary>CONSTANTS <c>exposure min</c> / <c>exposure max</c> / <c>night darkness</c>: the composite's luminance band.</summary>
    public ExposureConstants Exposure { get; set; } = ExposureConstants.Default;
    /// <summary>This frame's <c>MIN_LUMINANCE</c> / <c>MAX_LUMINANCE</c> (game mode); the composite scales by 0.55 over the scene's mean luminance clamped to them.</summary>
    public float MinLuminance => state.MinLuminance;
    public float MaxLuminance => Exposure.Max;
    /// <summary>CPU time of the last <see cref="Prepare"/> in ms.</summary>
    public double PrepareMs { get; private set; }
    /// <summary>Which of the game's lighting textures were found (irradiance cube, specular cube, ambient map).</summary>
    public string LightingTextures => $"irradiance {(irradianceCube is not null ? "yes" : "no")}, specularity {(specularCube is not null ? "yes" : "no")}, ambient map {(ambientMap is not null ? "yes" : "no")}";

    struct State
    {
        public Vector3 Sun, SunLight, LightDirection, CloudSun, CloudZenith;   // the last two: sunColour.rgb and zenithLight of the cloud pass
        public Vector3 CloudKey, CloudKeyDirection, CloudAmbient;   // the Meitou clouds' key light (colour, direction) and ambient (MeitouClouds)
        public float Environment, MinLuminance, FogDistance;
        public SkyColours Colours;
        public WorldLighting Light;
        public Vector4 Night;           // the night air (Meitou.Data NightAir): rgb its colour at full night, w its weight (0: none)
        public bool Valid, PlanetLit;   // PlanetLit: planetshine adds light of the planet in this state (ShadowSunHeight)
    }
    State state;
    Vector3 builtSun = new(float.NaN);
    (bool On, float Strength, Vector3 Albedo, bool Air) builtPlanetshine;
    SkyWeather? builtWeather;
    Vector3 builtTint;
    bool builtPhysical;

    public SkyRenderer(GpuContext gpu, AssetLocator? assets = null)
    {
        Gpu = gpu;
        simple = new SkyProg(gpu, Vertex, SimpleFragment, "sky simple");
        sky = new SkyProg(gpu, Vertex, SkyFragment, "sky");
        skyTimer = new PassTimer(gpu);
        // MEITOU_FORCE_INTEGRATED=1 (VulkanDevice.IsIntegrated) shows the integrated GPU's star layers on any card; MEITOU_STAR_LAYERS=0..3 sets them.
        if (gpu.Device.IsIntegrated) StarLayers = StarField.IntegratedLayers;
        if (Environment.GetEnvironmentVariable("MEITOU_STAR_LAYERS") is { } layers && int.TryParse(layers, out int count)) StarLayers = Math.Clamp(count, 0, StarField.Layers.Length);
        if (assets is not null)
        {
            LoadTextures(assets);
            Console.WriteLine($"sky       textures: starfield {(starsTexture is not null ? "yes" : "no")}, planets {string.Join(" ", SkyPlanet.All.Select((p, i) => $"{p.Material} {(planetTextures[i] is not null ? "yes" : "no")}"))}, clouds {(cloudsTexture is not null ? "yes" : "no")}");
        }
        Active = this;
        PublishGlobals();
        ShadowsOffGlobals.Publish(gpu);
    }

    void LoadTextures(AssetLocator assets)
    {
        try
        {
            // The whole 4096² file (BC1, 13 levels), repeating: the dome's coordinates wrap as the starfield shifts. The game samples its top level only
            // (`filtering linear linear none`); the viewer filters trilinearly, which takes the top level at the game's screen sizes and keeps a smaller view from sparkling.
            if (assets.Find("SkyX_Starfield.dds") is { } stars) starsTexture = SampledImage.FromDds(Gpu, DdsReader.ReadFile(stars), repeat: true, "sky stars");
            for (int i = 0; i < SkyPlanet.All.Length; i++)
                if (assets.Find(SkyPlanet.All[i].Texture) is { } planet)
                {
                    var dds = DdsReader.ReadFile(planet);
                    planetTextures[i] = SampledImage.FromDds(Gpu, dds, repeat: true, $"sky planet {SkyPlanet.All[i].Material}");
                    if (i == 0) PlanetAlbedo = Meitou.Data.World.Planetshine.MeanAlbedo(dds);   // the planetshine's colour, from a low level of the map
                }
            if (assets.Find("Clouds.dds") is { } clouds)
            {
                var img = TextureLoader.LoadFile(clouds, allMips: false).Levels[0];
                cloudsTexture = SampledImage.Rgba8(Gpu, img, repeat: true, mipmaps: true, "sky clouds");
                cloudsSmooth = SampledImage.Rgba8(Gpu, new RgbaImage(img.Width, img.Height, MeitouClouds.SmoothRed(img.Pixels, img.Width, img.Height)), repeat: true, mipmaps: true, "sky clouds smooth");
            }
            if (assets.Find("CloudsNormal.dds") is { } cloudsNormal)
            {
                var img = TextureLoader.LoadFile(cloudsNormal, allMips: false).Levels[0];
                cloudsNormalTexture = SampledImage.Rgba8(Gpu, img, repeat: true, mipmaps: true, "sky clouds normal");
            }
            if (assets.Find("CloudsTile.dds") is { } cloudsTile)
            {
                var img = TextureLoader.LoadFile(cloudsTile, allMips: false).Levels[0];
                cloudsTileTexture = SampledImage.Rgba8(Gpu, img, repeat: true, mipmaps: true, "sky clouds tile");
                cloudsTileSmooth = SampledImage.Rgba8(Gpu, new RgbaImage(img.Width, img.Height, MeitouClouds.SmoothRed(img.Pixels, img.Width, img.Height)), repeat: true, mipmaps: true, "sky clouds tile smooth");
            }
            if (assets.Find("mp_irradiance.dds") is { } irradiance) irradianceCube = SampledImage.Cube(Gpu, DdsReader.ReadFile(irradiance), "sky irradiance");
            if (assets.Find("mp_specularity.dds") is { } specularity) specularCube = SampledImage.Cube(Gpu, DdsReader.ReadFile(specularity), "sky specularity");
        }
        catch (Exception e) when (e is DdsFormatException or IOException or InvalidOperationException)
        {
            Console.WriteLine($"warning   sky textures: {e.Message}");
        }
    }

    /// <summary>
    /// The ambient map, built the game's way from <c>biomemap.png</c> and the BIOMES records (<see cref="AmbientMap"/>), and the CONSTANTS
    /// exposure values.
    /// </summary>
    public void LoadWorld(GameInstall install, GameDatabase db)
    {
        Exposure = ExposureConstants.FromDatabase(db);
        try
        {
            var biomes = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BiomeMap)));
            var map = AmbientMap.Build(db, biomes);
            ambientMap?.Dispose();
            ambientMap = SampledImage.Rgba8(Gpu, map, repeat: false, mipmaps: false, "sky ambient map");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException)
        {
            Console.WriteLine($"warning   ambient map: {e.Message}");
        }
        Console.WriteLine($"lighting  {LightingTextures}; exposure band {Exposure.Min:0.##}..{Exposure.Max:0.##}, night darkness {Exposure.NightDarkness:0.##}");
    }

    // ---- per frame -------------------------------------------------------------------------------------

    /// <summary>
    /// Brings the sky up to date for this frame's sun and returns the colours and light the world shaders use.
    /// <paramref name="fogDistance"/> is where the physical haze is complete.
    /// </summary>
    public (SkyColours Colours, WorldLighting Light) Prepare(Vector3 sun, float eyeHeight, float fogDistance)
    {
        var watch = Stopwatch.StartNew();
        if (!Physical)
        {
            var simple = SkyColours.For(sun);
            var light = simple.Lighting(fogDistance);
            state = new State { Sun = sun, Colours = simple, Light = light, FogDistance = fogDistance, Valid = true, MinLuminance = Exposure.Min,
                CloudSun = KenshiLighting.SunColour(sun), CloudZenith = CloudLayer.ZenithLight(sun, SkyColourMultiplier),
                CloudKey = KenshiLighting.SunColour(sun), CloudKeyDirection = sun, CloudAmbient = MeitouClouds.Ambient(sun, SkyColourMultiplier, Vector4.Zero) };
            builtPhysical = false;
            PrepareMs = watch.Elapsed.TotalMilliseconds;
            return (simple, light);
        }
        // SkyX's sky does not depend on the eye's height (its camera is fixed); the light only changes with the sun and the weather.
        var planetshine = (Planetshine, PlanetshineStrength, PlanetAlbedo, NightAir);
        if (!builtPhysical || !ReferenceEquals(builtWeather, Weather) || builtTint != SkyColourMultiplier || (sun - builtSun).LengthSquared() > 1e-10f || builtPlanetshine != planetshine)
        {
            builtSun = sun;
            builtWeather = Weather;
            builtTint = SkyColourMultiplier;
            builtPlanetshine = planetshine;
            builtPhysical = true;
            var sunLight = KenshiLighting.SunLight(sun);
            var lightDir = KenshiLighting.LightDirection(sun);
            // The ambient stays the game's: its factor comes from the sun's own (clamped) direction, never the planet's, whose height of 12 degrees would
            // raise the night ambient fivefold.
            float env = KenshiLighting.EnvironmentFactor(lightDir);
            // Meitou planetshine: from the end of the sun's light the planet is the light (docs/formats/sky.md "Planetshine"): its colour in the sun light's own
            // unit (the sun at the zenith, undimmed by the air in between, is the reference), added to the sun's, the direction weighted by their brightness.
            bool planetLit = false;
            var cloudPlanet = Vector3.Zero;   // the planet's light the Meitou clouds take (MeitouClouds.Key), with the planetshine
            float reference = Meitou.Data.World.Planetshine.Luminance(KenshiLighting.SunLight(Vector3.UnitY));
            // What the water's glint is lit by (WorldLighting.GlintDirection / GlintColour): the sun's own radiance and the planet disc's radiance as the sky pass draws it
            // (unboosted: the strength-scaled light below is an irradiance made 60 times brighter than the disc, which blew the glint out). Left default (the sun's) by day.
            Vector3 glintDirection = default, glintColour = default;
            if (Planetshine && Meitou.Data.World.Planetshine.Weight(sun.Y) is var weight and > 0)
            {
                var planetLight = Meitou.Data.World.Planetshine.Light(sun, SkyPlanet.All[0], PlanetAlbedo, reference, PlanetshineStrength) * weight;
                cloudPlanet = planetLight;
                var disc = Meitou.Data.World.Planetshine.DiscRadiance(sun, SkyPlanet.All[0], PlanetAlbedo) * weight;
                (glintDirection, glintColour) = Meitou.Data.World.Planetshine.Combine(lightDir, sunLight * (MathF.PI * (1 - KenshiLighting.DielectricSpecular)),
                    SkyPlanet.All[0].Towards, disc);
                (lightDir, sunLight) = Meitou.Data.World.Planetshine.Combine(lightDir, sunLight, SkyPlanet.All[0].Towards, planetLight);
                planetLit = Meitou.Data.World.Planetshine.Luminance(planetLight) > PlanetLitFloor * reference;
            }
            // Meitou night air (docs/formats/sky.md "Night air"): the colour at full night from the airglow and the planet's light (the strength-scaled one, whatever the
            // planetshine switch says), and its weight by the sun's height; the shaders add it to the haze's colour and to the sky's.
            var night = Vector4.Zero;
            if (NightAir && Meitou.Data.World.NightAir.Weight(sun.Y) is var airWeight and > 0)
            {
                var air = Meitou.Data.World.NightAir.Colour(Meitou.Data.World.Planetshine.Light(sun, SkyPlanet.All[0], PlanetAlbedo, reference, PlanetshineStrength));
                night = new Vector4(air, airWeight);
            }
            var (cloudKeyDirection, cloudKey) = MeitouClouds.Key(sun, SkyPlanet.All[0].Towards, cloudPlanet);
            var tint = Vector3.One;   // the game tints only the cloud light (zenithLight, below), never the skydome (docs/formats/sky.md)
            var zenith = SkyXModel.Colour(Vector3.UnitY, sun) * tint;
            var flat = new Vector2(sun.X, sun.Z);
            flat = flat.LengthSquared() > 1e-8f ? Vector2.Normalize(flat) : Vector2.UnitX;
            var horizon = (SkyXModel.Colour(new Vector3(flat.X, 0.05f, flat.Y), sun) + SkyXModel.Colour(new Vector3(-flat.X, 0.05f, -flat.Y), sun)) * 0.5f * tint;
            float twilight = 1 - Math.Clamp(MathF.Abs(sun.Y + 0.02f) / 0.35f, 0, 1);
            // For the code that still takes a sun colour and an ambient (the water): a white surface facing the sun reflects π · 0.96 · light;
            // the ambient is the irradiance cube's up and down faces (docs/formats/lighting.md) times the environment factor.
            var sunRadiance = sunLight * (MathF.PI * (1 - KenshiLighting.DielectricSpecular));
            var ambientSky = new Vector3(1.09f, 1.20f, 1.51f) * (env * 0.96f);
            var ambientGround = new Vector3(1.11f, 1.05f, 0.96f) * (env * 0.96f);
            state = new State
            {
                Sun = sun, SunLight = sunLight, LightDirection = lightDir, Environment = env, FogDistance = fogDistance,
                CloudSun = KenshiLighting.SunColour(sun), CloudZenith = CloudLayer.ZenithLight(sun, SkyColourMultiplier),
                CloudKey = cloudKey, CloudKeyDirection = cloudKeyDirection, CloudAmbient = MeitouClouds.Ambient(sun, SkyColourMultiplier, night),
                MinLuminance = KenshiLighting.MinLuminance(sun.Y, Exposure.Min, Exposure.NightDarkness), PlanetLit = planetLit,
                Colours = new SkyColours(sun, zenith, horizon, sunRadiance, twilight),
                Light = new WorldLighting(lightDir, sunRadiance, ambientSky, ambientGround, horizon, fogDistance, glintDirection, glintColour), Valid = true, Night = night,
            };
        }
        else
        {
            state.Light = state.Light with { FogDistance = fogDistance };
            state.FogDistance = fogDistance;
        }
        PrepareMs = watch.Elapsed.TotalMilliseconds;
        return (state.Colours, state.Light);
    }

    /// <summary>The values of <see cref="AtmosphereShaders.Functions"/>' loose uniforms (what <see cref="Apply"/> sets), by GLSL name minus <c>uAtmo</c>.</summary>
    public readonly record struct AtmosphereUniforms(Vector4 Tau, Vector4 Params, Vector4 Sun, Vector4 Light, Vector3 SunLight, Vector3 Tint, Vector4 Fog,
        Vector3 FogColour, Vector4 Simple, Vector4 Haze, Vector4 HazeCloud, Vector4 Altitude, Vector4 Maps, Vector4 Night);

    /// <summary>The weather fog's own ease-in-out term (<c>1 - 2 (1 - a)^2</c>) counts as complete from 0.9998, as <see cref="FogVolumes.CullAlpha"/> does.</summary>
    const float FogComplete = 0.9998f;

    /// <summary>
    /// The distance from the eye beyond which the weather's fog replaces what a world shader's <c>atmoApply</c> would show by the fog colour, so a
    /// surface (and the sky behind it) there adds nothing; null when no distance does (<see cref="FogCullDistanceOf"/>).
    /// </summary>
    public float? FogCullDistance => state.Valid ? FogCullDistanceOf(Uniforms()) : null;

    /// <summary>
    /// The least distance <c>D</c> such that every surface at <c>dist &gt;= D</c> comes out of <c>atmoApply</c> as the weather's fog colour (to 0.0002 of the
    /// difference), whatever it is, and the sky pass (the pixel's alternative when the surface is not drawn) gives the same colour; null when there is none.
    /// <para>Why the fog term and not the haze's transmittance: the Kenshi haze returns <c>mix(colour, rgb, alpha)</c>; <c>alpha = 1</c> removes the
    /// surface but leaves <c>rgb</c>, the haze colour, which depends on the distance below the dome radius and differs from the sky pass's colour. Only
    /// the weather fog (<c>rgb = mix(hazeRgb, fogColour, curve)</c>) at <c>curve = 1</c> makes the pixel independent of what is drawn. It also makes
    /// <c>alpha &gt;= 0.9998</c>, so the transmittance is below 1/255 in every channel. Verified from <see cref="AtmosphereShaders.Functions"/>:
    /// in the Kenshi branch alpha, curve and the weather term depend on the distance alone (the ray direction enters only <c>rgb</c>), so there is no
    /// vertical dependence to bound; the physical branch's column of air only lowers the transmittance, and its fog ramp is applied after it.</para>
    /// Null when: the simple sky is on (it never fogs fully); the fog is off or its weight is under 0.9998 (a partial weight leaves part of the
    /// surface); or the sky pass's own fog is not complete at the far clip (<c>Fog.W</c>, the distance of a pixel with no geometry). With the Kenshi
    /// haze in use the distance is where its curve reaches 0.9998 (0.99 of the fog distance); with the physical haze where the linear ramp reaches
    /// 0.9998 of the way; when both are blended (<c>Altitude.X</c> between 0 and 1) the larger.
    /// </summary>
    public static float? FogCullDistanceOf(in AtmosphereUniforms u)
    {
        if (u.Params.X < 0.5f || u.Fog.Z < FogComplete || !(u.Haze.W > 0)) return null;
        float curveStart = 1f - MathF.Sqrt((1f - FogComplete) / 2f);   // the curve's input at which it reaches FogComplete: 0.99
        if (u.Fog.W * u.Haze.W < curveStart) return null;
        float fogDistance = 1f / u.Haze.W;
        bool kenshi = u.Haze.X > 0.5f && u.Altitude.X < 1f;
        bool physical = !kenshi || u.Altitude.X > 0f;
        float distance = 0;
        if (kenshi) distance = fogDistance * curveStart;
        if (physical) distance = MathF.Max(distance, u.Fog.X + FogComplete * MathF.Max(u.Fog.Y - u.Fog.X, 1f));
        return distance;
    }

    /// <summary>Where the haze's alpha stops growing (the light shafts' grid ends there; docs/render-shafts.md); null before the sky has a state.</summary>
    public float? HazeCompleteDistance => state.Valid ? HazeCompleteOf(Uniforms()) : null;

    /// <summary>
    /// The distance at which <c>atmoKenshiHaze</c>'s alpha reaches 1: the ramp's end stretched by the haze strength, or sooner where a full-weight weather
    /// fog completes (1 / density); the physical haze's complete distance; the simple sky's. At most the far clip (<c>Fog.W</c>).
    /// </summary>
    public static float HazeCompleteOf(in AtmosphereUniforms u)
    {
        if (u.Params.X < 0.5f) return u.Simple.W;
        float d = u.Haze.X > 0.5f && u.Altitude.X < 1f ? u.Haze.Y + (u.Haze.Z - u.Haze.Y) / MathF.Max(u.Altitude.Y, 1e-3f) : u.Params.Y;
        if (u.Fog.Z >= 0.999f && u.Haze.W > 0) d = MathF.Min(d, 1f / u.Haze.W);
        return MathF.Min(d, u.Fog.W);
    }

    /// <summary>The atmosphere uniforms for the current state (valid only while <c>state.Valid</c>).</summary>
    AtmosphereUniforms Uniforms()
    {
        var s = state;
        var tau = SkyAtmosphere.RayleighZenithDepth;
        var tint = Vector3.One;   // uAtmoTint: the skydome is not tinted in the game
        var w = Weather;
        // The fog the weather system gives (weight, colour, distance), else the forced record's (on or off, complete at fog distance max).
        var (fogWeight, fogRgb, fogDistance) = FogInput ?? (w.FogEnabled ? 1f : 0f, w.FogColour, w.FogMax);
        float fogStart = FogInput is null ? w.FogMin : (w.FogMax > 0 ? fogDistance * w.FogMin / w.FogMax : 0f);
        // The weather fog's colour times sunColour.w, the daylight scale (the game's global fog term).
        var fog = fogRgb * (Physical ? KenshiLighting.Daylight(s.Sun.Y) : 1f);
        var hc = s.Colours.Horizon;
        float hazeStart = HazeDistance * Meitou.Data.World.KenshiHaze.StartFraction;
        float hazeEnd = MathF.Min(HazeDistance, HazeDistance * Meitou.Data.World.KenshiHaze.EndFraction);
        var (cloud, pull) = HorizonClouds(s);
        return new AtmosphereUniforms(
            new Vector4(tau.X, tau.Y, tau.Z, SkyAtmosphere.MieZenithDepth),
            new Vector4(Physical ? 1f : 0f, MathF.Max(s.FogDistance, 1), ScaleHeightUnits, 0),
            new Vector4(s.Sun.X, s.Sun.Y, s.Sun.Z, 0),
            new Vector4(s.LightDirection.X, s.LightDirection.Y, s.LightDirection.Z, s.Environment),
            new Vector3(s.SunLight.X, s.SunLight.Y, s.SunLight.Z),
            new Vector3(tint.X, tint.Y, tint.Z),
            new Vector4(fogStart, fogDistance, fogWeight, HazeDistance),   // w: the far clip D, the distance of a pixel with no geometry (the sky)
            new Vector3(fog.X, fog.Y, fog.Z),
            new Vector4(hc.X, hc.Y, hc.Z, MathF.Max(s.FogDistance, 1)),
            // The weather fog is complete at a distance between `fog distance min` and `max` by the wind; the viewer has no wind and takes max.
            new Vector4(KenshiHaze ? 1f : 0f, hazeStart, hazeEnd, fogWeight > 0 && fogDistance > 1 ? 1f / fogDistance : 0f),
            new Vector4(cloud.X, cloud.Y, cloud.Z, pull),
            new Vector4(KenshiHaze ? AltitudeWeight : 1f, MathF.Max(HazeStrength, 0), AltitudeWeight, 0),
            new Vector4(irradianceCube is not null ? 1f : 0f, specularCube is not null ? 1f : 0f, ambientMap is not null ? 1f : 0f, AmbientMap.HalfWorld),
            s.Night);
    }

    /// <summary>
    /// Publishes the atmosphere to the frame globals (docs/renderer-native.md 4.3): the uniform values (what the GL <c>Apply</c> set on a
    /// program), and the three textures, natively (phase 8 stage 2; before, the GL texture units <c>BindUnits</c> bound them to). Both appear
    /// once the sky has a state (<see cref="Prepare(Vector3, float, float)"/>), as the GL code set them from then on; before that the readers get
    /// the stand-in. A missing file gives the stand-in too (GL's unbound unit).
    /// </summary>
    void PublishGlobals()
    {
        var g = Gpu.Globals;
        bool Valid() => state.Valid;
        g.Publish("uAtmoIrradiance", () => state.Valid && irradianceCube is { } t ? t.Sampled() : default);
        g.Publish("uAtmoSpecular", () => state.Valid && specularCube is { } t ? t.Sampled() : default);
        g.Publish("uAtmoAmbientMap", () => state.Valid && ambientMap is { } t ? t.Sampled() : default);
        g.PublishUniform("uAtmoTau", () => Published().Tau, Valid);
        g.PublishUniform("uAtmoParams", () => Published().Params, Valid);
        g.PublishUniform("uAtmoSun", () => Published().Sun, Valid);
        g.PublishUniform("uAtmoLight", () => Published().Light, Valid);
        g.PublishUniform("uAtmoSunLight", () => Published().SunLight, Valid);
        g.PublishUniform("uAtmoTint", () => Published().Tint, Valid);
        g.PublishUniform("uAtmoFog", () => Published().Fog, Valid);
        g.PublishUniform("uAtmoFogColour", () => Published().FogColour, Valid);
        g.PublishUniform("uAtmoSimple", () => Published().Simple, Valid);
        g.PublishUniform("uAtmoHaze", () => Published().Haze, Valid);
        g.PublishUniform("uAtmoHazeCloud", () => Published().HazeCloud, Valid);
        g.PublishUniform("uAtmoAltitude", () => Published().Altitude, Valid);
        g.PublishUniform("uAtmoMaps", () => Published().Maps, Valid);
        g.PublishUniform("uAtmoNight", () => Published().Night, Valid);
        g.PublishUniform("uAtmoCloud", CloudShadowUniform, Valid);
        g.PublishUniform("uAtmoCloudWind", () => { var w = CloudLayer.TextureShift(cloudOffset); return VolumeShadows ? new Vector4(volume!.ShadowOrigin.X, volume.ShadowOrigin.Y, VolumetricClouds.ShadowSpan, 0) : new Vector4(w.X, w.Y, 0, 0); }, Valid);
        g.Publish("uAtmoClouds", () => VolumeShadows ? volume!.ShadowMap : state.Valid && cloudsSmooth is { } t ? t.Sampled() : default);
        g.Publish("uAtmoCloudsTile", () => state.Valid && cloudsTileSmooth is { } t ? t.Sampled() : default);
    }

    // The values the getters above share, computed once per ApplyGlobals call (FrameGlobals.ApplyCount) instead of once per name.
    AtmosphereUniforms published;
    int publishedAt = -1;

    AtmosphereUniforms Published()
    {
        int at = Gpu.Globals.ApplyCount;
        if (at != publishedAt) (published, publishedAt) = (Uniforms(), at);
        return published;
    }

    /// <summary>
    /// <c>horizonClouds</c> (docs/formats/sky.md): the pull is <c>saturate(DensityOffset + 0.5)</c>, the colour is the cloud pass's own light,
    /// <c>saturate(sun (1 − 0.1 (offset + 0.2) · 3) + zenithLight) (1 − darkness) √exposure</c>, from the same c as the layer.
    /// </summary>
    (Vector3 Colour, float Pull) HorizonClouds(State s)
    {
        float c = CloudDensity;
        return (CloudLayer.HorizonColour(s.CloudSun, s.CloudZenith, c), CloudLayer.HorizonAlpha(c));
    }

    // ---- drawing ---------------------------------------------------------------------------------------

    /// <summary>
    /// The sky pass: a full-screen triangle into the pass VkGl has open (the scene's, or the reflection's multisampled one). Prepare sets the
    /// program's uniforms and textures; Record is one native segment (docs/renderer-native.md 7.5) with the pass's state, depth test and write off.
    /// No GL state is changed (phase 8 stage 2): the GL code turned the depth test and write off around the draw and back on after it, and
    /// every caller turns the depth test on after the sky itself (docs/renderer-native.md 8.6).
    /// </summary>
    public void Draw(Matrix4x4 viewProjection, SkyColours colours)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse)) return;
        Poll();   // a timestamp pair is readable only for a few frames after its own (the arena's ring), so collect them as they come
        skyTimer.Begin();
        var program = Physical && state.Valid ? sky : simple;
        Prepare(program, inverse, colours);
        Record(program);
        skyTimer.End();
    }

    void Prepare(SkyProg program, in Matrix4x4 inverse, SkyColours colours)
    {
        var p = program.P;
        p.Set(program.InverseViewProjection, in inverse);
        program.Colours.Set(p, colours);
        if (program == sky) SetSkyUniforms(program);
        p.ApplyGlobals();   // the atmosphere's uniforms and textures, through the frame globals
    }

    void Record(SkyProg program)
    {
        var cmd = Gpu.BeginGuest(program == sky ? "sky" : "sky simple");
        var targets = Gpu.CurrentTargets();
        // What the GL code's Disable(DepthTest) and DepthMask(false) made of the pass's state.
        var drawState = Gpu.CurrentState() with { DepthTest = false, DepthWrite = false };
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(drawState.Cull, drawState.Front);
        cmd.SetDepth(drawState.DepthTest, drawState.DepthWrite, drawState.Compare);
        cmd.SetDepthBias(drawState.BiasEnable, drawState.BiasConstant, drawState.BiasSlope);
        cmd.BindPipeline(program.Segment.Get(drawState, targets.Formats, null));
        program.P.Flush(cmd);
        cmd.Draw(3);
        Gpu.EndGuest(cmd);
    }

    void SetSkyUniforms(SkyProg program)
    {
        var s = state;
        var p = program.P;
        float coverage = CloudDensity;
        // The Meitou stars: nothing to draw once the sun is high (SkyX's night factor is 0 by day; the game's texture is not drawn then either), so the pass skips them.
        bool meitou = MeitouStars;
        bool night = meitou && s.Sun.Y < 0.3f;
        p.Set(program.StarsMeitou, meitou ? (night ? 1 : -1) : 0, StarLayers, StarSeconds, StarGain);
        var frame = CelestialSphere.Frame(Clock, Day, Hour);
        p.Set(program.CelX, frame.X.X, frame.X.Y, frame.X.Z, 0);
        p.Set(program.CelY, frame.Y.X, frame.Y.Y, frame.Y.Z, 0);
        p.Set(program.CelZ, frame.Z.X, frame.Z.Y, frame.Z.Z, 0);
        p.Set(program.Extra, 0, coverage, Starfield.Shift(StarHours), 0);
        // The planets: fixed directions and sizes, each turned by the game's day and hour.
        Span<float> spin = stackalloc float[4];
        for (int i = 0; i < 2; i++)
        {
            var planet = SkyPlanet.All[i];
            var towards = planet.Towards;
            p.Set(program.Planet[i], towards.X, towards.Y, towards.Z, planet.SinRadius);
            float a = planet.Spin(Day, Hour);
            (spin[2 * i], spin[2 * i + 1]) = (MathF.Cos(a), MathF.Sin(a));
        }
        p.Set(program.PlanetSpin, spin[0], spin[1], spin[2], spin[3]);
        // The cloud pass: zenithLight and Darkness, sunColour.rgb and DensityOffset, the wind offset's texture shift.
        var zenith = s.CloudZenith;
        p.Set(program.CloudLight, zenith.X, zenith.Y, zenith.Z, CloudLayer.Darkness(coverage));
        p.Set(program.CloudSun, s.CloudSun.X, s.CloudSun.Y, s.CloudSun.Z, CloudLayer.DensityOffset(coverage));
        var shift = CloudLayer.TextureShift(cloudOffset);
        p.Set(program.CloudWind, shift.X, shift.Y, 0, 0);
        var key = s.CloudKey; var keyDir = s.CloudKeyDirection; var ambient = s.CloudAmbient;
        // The Meitou clouds are anchored to the world (the eye's parallax, MeitouClouds.Parallax), so the cloud overhead is the one whose shadow falls here; the game's follow the eye.
        var (parallaxScale, parallaxOffset) = LitClouds ? MeitouClouds.Parallax(Eye.X, Eye.Y, Eye.Z) : (1f, Vector2.Zero);
        p.Set(program.CloudMeitou, LitClouds ? (volumeImage.IsNull ? 1 : 2) : 0, parallaxScale, parallaxOffset.X, parallaxOffset.Y);
        p.Set(program.CloudKey, key.X, key.Y, key.Z, 0);
        p.Set(program.CloudKeyDir, keyDir.X, keyDir.Y, keyDir.Z, 0);
        p.Set(program.CloudAmbient, ambient.X, ambient.Y, ambient.Z, MeitouClouds.AmbientGreyAt(coverage));
        bool clouds = cloudsTexture is not null && cloudsNormalTexture is not null && cloudsTileTexture is not null;
        p.Set(program.Has, starsTexture is not null ? 1f : 0f, planetTextures[0] is not null ? 1f : 0f, clouds ? 1f : 0f, planetTextures[1] is not null ? 1f : 0f);
        // A missing texture reads GL's stand-in, which an unbound sampler does too.
        if (starsTexture is not null) p.Bind(program.Stars, starsTexture.Sampled());
        if (planetTextures[0] is { } planet0) p.Bind(program.Planet0, planet0.Sampled());
        if (planetTextures[1] is { } planet1) p.Bind(program.Planet1, planet1.Sampled());
        if (cloudsTexture is not null) p.Bind(program.Clouds, cloudsTexture.Sampled());
        if (cloudsNormalTexture is not null) p.Bind(program.CloudsNormal, cloudsNormalTexture.Sampled());
        if (cloudsTileTexture is not null) p.Bind(program.CloudsTile, cloudsTileTexture.Sampled());
        if (cloudsSmooth is not null) p.Bind(program.CloudsSmooth, cloudsSmooth.Sampled());
        if (cloudsTileSmooth is not null) p.Bind(program.CloudsTileSmooth, cloudsTileSmooth.Sampled());
        if (!volumeImage.IsNull) p.Bind(program.VolumeImage, volumeImage);
    }

    /// <summary>
    /// Records <paramref name="count"/> sky passes back to back and returns the CPU time each took to record (ms; no table any more), for the
    /// screenshot mode's cost report (<c>MEITOU_SKY_BENCH</c>). The GL version waited for the GPU (<c>Finish</c>); the native API has no such
    /// wait inside a frame, so the GPU's share shows in <see cref="GpuMs"/> instead.
    /// </summary>
    public (double SkyPassMs, double TableMs) Benchmark(Matrix4x4 viewProjection, int count = 100)
    {
        var colours = state.Colours;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) Draw(viewProjection, colours);
        return (watch.Elapsed.TotalMilliseconds / count, 0);
    }

    /// <summary>Collects finished GPU timings: native timestamps, read once their frame's slot comes round. Nothing waits, also with
    /// <paramref name="wait"/> (kept for callers; the GL queries could block).</summary>
    public void Poll(bool wait = false)
    {
        _ = wait;
        gpuSamples.Clear();
        skyTimer.Poll(gpuSamples);
        foreach (double ms in gpuSamples)
        {
            if (ms <= 0 || ms >= 1000) continue;
            gpuTotal += ms;
            gpuCount++;
            if (gpuCount > 120) { gpuTotal *= 0.5; gpuCount /= 2; }
        }
    }

    public string DescribeCost() =>
        Physical ? $"CPU {PrepareMs:0.00} ms (last prepare); GPU sky pass {GpuMs:0.00} ms/frame (SkyX per pixel)" + (volume is null || !VolumeEnabled ? "" : CloudsFogged ? $"; clouds not marched (the weather fog covers the sky), shadow map {volume.ShadowGpuMs:0.00} ms" : $"; GPU clouds {volume.MainGpuMs:0.00}, reflection {volume.ReflectionGpuMs:0.00}, shadow map {volume.ShadowGpuMs:0.00} ms")
                 : $"simple sky: GPU {GpuMs:0.00} ms/frame";

    /// <summary>Mean GPU time of a sky pass over the last hundred or so (a running mean, halved now and then).</summary>
    public double GpuMs => gpuCount == 0 ? 0 : gpuTotal / gpuCount;

    public void Dispose()
    {
        if (Active == this) Active = null;
        foreach (var t in new[] { starsTexture, cloudsTexture, cloudsNormalTexture, cloudsTileTexture, cloudsSmooth, cloudsTileSmooth, irradianceCube, specularCube, ambientMap }) t?.Dispose();
        foreach (var t in planetTextures) t?.Dispose();
        volumeShadowsReady = false;
        volume?.Dispose();
        volume = null;
        simple.Dispose();
        sky.Dispose();
    }
}

/// <summary>The handles of the sky colour uniforms of <see cref="SkyRenderer.SkyFunctions"/> in a native program, resolved once.</summary>
public readonly record struct SkyColourHandles(UniformHandle Sun, UniformHandle Zenith, UniformHandle Horizon, UniformHandle SunColour, UniformHandle Twilight)
{
    public static SkyColourHandles Resolve(LegacyProgram p) =>
        new(p.Uniform("uSkySun"), p.Uniform("uSkyZenith"), p.Uniform("uSkyHorizon"), p.Uniform("uSkySunColour"), p.Uniform("uSkyTwilight"));

    /// <summary>What the GL <c>SetUniforms</c> set on a program; the atmosphere's uniforms come from <see cref="LegacyProgram.ApplyGlobals"/>.</summary>
    public void Set(LegacyProgram p, SkyColours c)
    {
        p.Set(Sun, c.Sun.X, c.Sun.Y, c.Sun.Z);
        p.Set(Zenith, c.Zenith.X, c.Zenith.Y, c.Zenith.Z);
        p.Set(Horizon, c.Horizon.X, c.Horizon.Y, c.Horizon.Z);
        p.Set(SunColour, c.SunColour.X, c.SunColour.Y, c.SunColour.Z);
        p.Set(Twilight, c.Twilight);
    }
}

/// <summary>
/// The pipelines of one native program's draws (docs/renderer-native.md 7.5): remembers the last two segment states (the scene's pass and the
/// reflection's multisampled one alternate) and the vertex export they were made for, so a draw compares a few fields instead of building a
/// pipeline description.
/// </summary>
internal sealed class NativeSegment(GpuContext gpu, LegacyProgram program, Silk.NET.Vulkan.PrimitiveTopology topology, string label)
{
    readonly record struct Key(AttachmentFormats Formats, BlendState Blend, Silk.NET.Vulkan.ColorComponentFlags Mask, Silk.NET.Vulkan.PolygonMode Polygon,
        bool AlphaToCoverage, bool DepthClamp);

    Key key0, key1;
    GraphicsPipeline? pipeline0, pipeline1;
    VertexArrayBindings? source0, source1;

    /// <summary>The pipeline for <paramref name="state"/> into <paramref name="formats"/>, with the vertex inputs of <paramref name="vertices"/> (null: none).</summary>
    public GraphicsPipeline Get(DrawState state, AttachmentFormats formats, VertexArrayBindings? vertices)
    {
        var key = new Key(formats, state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage, state.DepthClamp);
        if (pipeline0 is not null && key == key0 && ReferenceEquals(vertices, source0)) return pipeline0;
        if (pipeline1 is not null && key == key1 && ReferenceEquals(vertices, source1)) return pipeline1;
        var made = gpu.Pipelines.Get(state.Pipeline(program.Program, program.VertexLayout(vertices is null ? default : vertices.Attributes), topology, formats, label));
        (key1, pipeline1, source1) = (key0, pipeline0, source0);
        (key0, pipeline0, source0) = (key, made, vertices);
        return made;
    }
}

/// <summary>A native sky program with its handles resolved at load.</summary>
sealed class SkyProg : IDisposable
{
    public readonly LegacyProgram P;
    public readonly NativeSegment Segment;
    public readonly UniformHandle InverseViewProjection, Extra, PlanetSpin, CloudLight, CloudSun, CloudWind, CloudMeitou, CloudKey, CloudKeyDir, CloudAmbient, Has, StarsMeitou, CelX, CelY, CelZ;
    public readonly UniformHandle[] Planet;
    public readonly SkyColourHandles Colours;
    public readonly SamplerSlot Stars, Planet0, Planet1, Clouds, CloudsNormal, CloudsTile, CloudsSmooth, CloudsTileSmooth, VolumeImage;
    public readonly Meitou.Rendering.Gpu.Shaders.SamplerInfo? StarsInfo, CloudsInfo;

    public SkyProg(GpuContext gpu, string vertex, string fragment, string name)
    {
        P = LegacyProgram.Create(gpu, vertex, fragment, name);
        Segment = new NativeSegment(gpu, P, Silk.NET.Vulkan.PrimitiveTopology.TriangleList, name);
        InverseViewProjection = P.Uniform("uInverseViewProjection");
        Colours = SkyColourHandles.Resolve(P);
        Extra = P.Uniform("uSkyExtra");
        Planet = [P.Uniform("uPlanetBody0"), P.Uniform("uPlanetBody1")];
        PlanetSpin = P.Uniform("uPlanetSpin");
        CloudLight = P.Uniform("uCloudLight");
        CloudSun = P.Uniform("uCloudSun");
        CloudWind = P.Uniform("uCloudWind");
        CloudMeitou = P.Uniform("uCloudMeitou");
        CloudKey = P.Uniform("uCloudKey");
        CloudKeyDir = P.Uniform("uCloudKeyDir");
        CloudAmbient = P.Uniform("uCloudAmbient");
        Has = P.Uniform("uHas");
        StarsMeitou = P.Uniform("uStarsMeitou");
        CelX = P.Uniform("uCelX");
        CelY = P.Uniform("uCelY");
        CelZ = P.Uniform("uCelZ");
        Stars = P.Sampler("uStars");
        Planet0 = P.Sampler("uPlanet0");
        Planet1 = P.Sampler("uPlanet1");
        Clouds = P.Sampler("uClouds");
        CloudsNormal = P.Sampler("uCloudsNormal");
        CloudsTile = P.Sampler("uCloudsTile");
        CloudsSmooth = P.Sampler("uCloudsSmooth");
        CloudsTileSmooth = P.Sampler("uCloudsTileSmooth");
        VolumeImage = P.Sampler("uVolumeImage");
        if (Stars.IsValid) StarsInfo = P.SamplerInfo(Stars);
        if (Clouds.IsValid) CloudsInfo = P.SamplerInfo(Clouds);
    }

    public void Dispose() => P.Dispose();
}

/// <summary>
/// (Phase 8 stage 2.) A native texture with the sampler state of the GL texture it replaces: <see cref="SamplerDesc.FromGl"/> with the GL
/// parameters and, on a mipmapped filter, the upscaler's LOD bias (<see cref="GpuContext.LodBias"/>), as VkGl's <c>SamplerFor</c> made it; the
/// view covers the levels VkGl's covered. For <see cref="LegacyProgram.Bind"/> and the frame globals (no bindless entry of its own).
/// Render thread only.
/// </summary>
internal sealed class SampledImage : IDisposable
{
    readonly GpuContext ctx;
    readonly TextureMinFilter min;
    readonly TextureMagFilter mag;
    readonly TextureWrapMode wrap, wrapR;
    float cachedBias = float.NaN;
    SampledTexture cached;

    SampledImage(GpuContext ctx, Texture texture, TextureMinFilter min, TextureMagFilter mag, TextureWrapMode wrap, TextureWrapMode wrapR)
    {
        this.ctx = ctx;
        Texture = texture;
        (this.min, this.mag, this.wrap, this.wrapR) = (min, mag, wrap, wrapR);
    }

    public Texture Texture { get; }

    /// <summary>The sampler and view a draw samples it with now (what VkGl's <c>Sampled</c> gave for the GL texture).</summary>
    public SampledTexture Sampled()
    {
        float bias = ctx.LodBias;
        if (!(bias == cachedBias))
        {
            var sampler = ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, wrap, wrap, wrapR, false, DepthFunction.Lequal, false, 1, false, bias));
            (cached, cachedBias) = (new SampledTexture(sampler, Texture.View(), Texture.Image), bias);
        }
        return cached;
    }

    static Silk.NET.Vulkan.Rect2D Rect(int width, int height) => new(new(0, 0), new((uint)width, (uint)height));

    /// <summary>
    /// What <c>WorldGl.Texture2D</c> made: RGBA8 from top-first rows, with <paramref name="mipmaps"/> the whole chain made on the GPU as GL's
    /// GenerateMipmap made it in VkGl (<see cref="CommandList.GenerateMips"/>) and trilinear filtering, else one level, linear; repeating or
    /// clamped to the edge (S and T; R stays GL's default, repeat).
    /// </summary>
    public static SampledImage Rgba8(GpuContext ctx, RgbaImage image, bool repeat, bool mipmaps, string name) =>
        Rgba8(ctx, image.Width, image.Height, image.Pixels, repeat, mipmaps, name);

    /// <inheritdoc cref="Rgba8(GpuContext, RgbaImage, bool, bool, string)"/>
    public static SampledImage Rgba8(GpuContext ctx, int width, int height, byte[] rgba, bool repeat, bool mipmaps, string name)
    {
        int levels = mipmaps ? 1 + (int)Math.Floor(Math.Log2(Math.Max(Math.Max(width, height), 1))) : 1;
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, width, height, levels,
            Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc, Name: name));
        batch.Write(t, 0, 0, Rect(width, height), rgba.AsSpan(0, width * height * 4));
        if (mipmaps) batch.Commands.GenerateMips(t);
        return new SampledImage(ctx, t, mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear, TextureMagFilter.Linear,
            repeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat);
    }

    /// <summary>
    /// A 2D DDS file with every level it has: BC1 blocks as they are (half a byte a texel), any other format decoded to RGBA8; trilinear,
    /// repeating or clamped to the edge. The sky's starfield and planet maps (4096² and 4096 × 2048 BC1: 11 and 6 MB instead of 85 and 43 MB as RGBA8).
    /// </summary>
    public static SampledImage FromDds(GpuContext ctx, Meitou.Data.Textures.DdsFile dds, bool repeat, string name)
    {
        bool bc1 = dds.Format == Meitou.Data.Textures.DdsFormat.Bc1;
        var format = bc1 ? Silk.NET.Vulkan.Format.BC1RgbaUnormBlock : Silk.NET.Vulkan.Format.R8G8B8A8Unorm;
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(format, dds.Width, dds.Height, dds.MipCount, Name: name));
        for (int level = 0; level < dds.MipCount; level++)
        {
            if (bc1)
            {
                var s = dds.Surface(0, level);
                batch.Write(t, level, 0, Rect(s.Width, s.Height), dds.Data.AsSpan(s.Offset, s.Length));
            }
            else
            {
                var img = Meitou.Data.Textures.DdsDecoder.Decode(dds, 0, level);
                batch.Write(t, level, 0, Rect(img.Width, img.Height), img.Pixels);
            }
        }
        var filter = dds.MipCount > 1 ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear;
        return new SampledImage(ctx, t, filter, TextureMagFilter.Linear, repeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat);
    }

    /// <summary>RGBA32F, one level, linear, clamped to the edge: the GL texture <c>WaterRenderer</c> made for its biome parameter maps.</summary>
    public static SampledImage Rgba32F(GpuContext ctx, Vector4[] data, int width, int height, string name)
    {
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, width, height, Name: name));
        batch.Write(t, 0, 0, Rect(width, height), System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan(0, width * height)));
        return new SampledImage(ctx, t, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat);
    }

    /// <summary>
    /// A cube map from a DDS file with all its mips (RGBA8), faces in the DDS order (+X, −X, +Y, −Y, +Z, −Z) as layers 0..5 (Vulkan's and GL's
    /// face order); trilinear, clamped to the edge on S, T and R (Vulkan's cube sampling is always seamless, as GL's was with
    /// <c>TEXTURE_CUBE_MAP_SEAMLESS</c>). The GL version's <c>MAX_LEVEL</c> was the file's last level, so its view had the file's levels too.
    /// </summary>
    public static SampledImage Cube(GpuContext ctx, DdsFile dds, string name)
    {
        if (!dds.IsCubemap || dds.ImageCount < 6) throw new InvalidOperationException($"not a cube map: {dds}");
        var first = DdsDecoder.Decode(dds, 0, 0);
        using var batch = ctx.Uploads.Begin();
        var t = batch.Create(new TextureDesc(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, first.Width, first.Height, dds.MipCount, Kind: TextureKind.Cube, Name: name));
        for (int face = 0; face < 6; face++)
            for (int level = 0; level < dds.MipCount; level++)
            {
                var img = face == 0 && level == 0 ? first : DdsDecoder.Decode(dds, face, level);
                batch.Write(t, level, face, Rect(img.Width, img.Height), img.Pixels);
            }
        return new SampledImage(ctx, t, TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
    }


    public void Dispose() => Texture.Dispose();
}

/// <summary>
/// (Phase 8 stage 2.) The shadow blocks a world program reads when there is no <see cref="ShadowPass"/> (<c>--no-shadows</c>), as frame
/// globals: zeros (shadows off), what <c>ShadowShaders.Bind</c> put on the GL binding points through the unused GL program the terrain
/// linked for it. Published only where nothing is yet; a <see cref="ShadowPass"/> publishes its own over them whenever it is made. The shadow
/// textures stay unpublished: readers get the stand-in, as an empty GL unit gave. Called by the sky, which every world view has.
/// </summary>
static class ShadowsOffGlobals
{
    public static void Publish(GpuContext gpu)
    {
        var g = gpu.Globals;
        foreach (var (name, bytes) in new[] { (ShadowShaders.ReceiverBlock, ShadowPass.ReceiverBytes), (ShadowShaders.CasterBlock, 16), (MeitouShadowShaders.Block, MeitouShadowShaders.BlockBytes) })
            if (g.Block(name) is null) g.Publish(name, new FrameBlock(gpu, bytes).Binding);
    }
}
