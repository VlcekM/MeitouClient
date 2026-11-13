using System.Globalization;
using Meitou.Data.World;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Shadows;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// GLSL of the world view's sky, light and haze in the game's own terms (docs/formats/sky.md, docs/formats/lighting.md): SkyX's scattering integral
/// evaluated per pixel with the game's options (<c>atmoSky</c>), the deferred lighting pass's sun, image-based ambient and specular light
/// (<c>kenshiLight</c>), and the haze every world shader ends with (<c>atmoApply</c>). Colours are the game's HDR values; the post-processing applies
/// the game's exposure. A program reads its uniforms and textures from the frame globals <see cref="SkyRenderer"/> publishes.
/// </summary>
static class AtmosphereShaders
{
    /// <summary>
    /// A float as a GLSL literal (invariant culture, always with a decimal point).
    /// </summary>
    static string F(float v) => v.ToString("0.#########", CultureInfo.InvariantCulture) + (v == MathF.Floor(v) ? ".0" : "");

    /// <summary>
    /// A vector as a GLSL <c>vec3</c> literal.
    /// </summary>
    static string V3(System.Numerics.Vector3 v) => $"vec3({F(v.X)}, {F(v.Y)}, {F(v.Z)})";

    /// <summary>
    /// The water and shafts share the fade before the rendered water's far clip.
    /// </summary>
    internal const string WaterHorizonFunctions = """

        float atmoWaterWeight(float dist, float farClip) { return 1.0 - smoothstep(0.5, 0.9, dist / max(farClip, 1.0)); }
        """;

    /// <summary>
    /// The Meitou night ambient floor (docs/render-sky.md): a texel of the ambient map with its colour raised, hue kept, to the luminance
    /// <c>maps.z - 1</c> (<see cref="SkyRenderer.NightAmbientFloor"/>; 0 by day and in Faithful). <c>maps</c>: the <c>uAtmoMaps</c> values.
    /// </summary>
    internal const string AmbientFloorFunction = """

        vec4 atmoAmbientFloor(vec4 am, vec4 maps)
        {
            float l = dot(am.rgb, vec3(0.2126, 0.7152, 0.0722));
            return vec4(am.rgb * max(1.0, (maps.z - 1.0) / max(l, 1e-3)), am.a);
        }
        """;

    /// <summary>
    /// Lazarov's environment BRDF for any specular colour: <see cref="Functions"/> and the reflections' full-size pass
    /// (<see cref="Gi.SsrShaders.Apply"/>).
    /// </summary>
    internal const string EnvBrdfFunction = """
        // The same for a specular colour of rf0 (LazarovEnvironmentBRDF).
        vec3 kenshiEnvBrdf(float gloss, float nv, vec3 rf0)
        {
            vec4 t = vec4(1.0 / 0.96, 0.475, (0.0275 - 0.25 * 0.04) / 0.96, 0.25) * gloss + vec4(0.0, 0.0, (0.015 - 0.75 * 0.04) / 0.96, 0.75);
            float a0 = t.x * min(t.y, exp2(-9.28 * nv)) + t.z;
            return clamp(a0 + rf0 * (t.w - a0), 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The clouds' shade on the sun at a world point; needs <c>uAtmoCloud</c>, <c>uAtmoCloudWind</c>, <c>uAtmoSun</c>, <c>uAtmoClouds</c> and
    /// <c>uAtmoCloudsTile</c> (<see cref="Functions"/>, and the probe trace, which has them in its own block).
    /// </summary>
    internal static readonly string CloudShadow = $$"""
        // The sun light that gets past the cloud layer to a world point: the sun's ray from the point hits the world-anchored plane, where the sky pass's
        // overhead alpha (the game's second lookup and tile, the horizon band at 1, no fake volume) is read. textureLod only: the grass's vertex stage includes this.
        float atmoCloudShadow(vec3 world)
        {
            if (uAtmoCloud.x <= 0.0) return 1.0;
            if (uAtmoCloud.w > 0.5)
            {
                vec2 hit = world.xz + uAtmoSun.xz * (uAtmoCloud.z - world.y) / max(uAtmoSun.y, 0.03);
                vec2 uv = (hit - uAtmoCloudWind.xy) / uAtmoCloudWind.z;
                float transmission = 1.0;
                if (world.y < uAtmoCloud.z)
                {
                    transmission = 1.0 - uAtmoCloud.y;
                    if (all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0))))
                        transmission = textureLod(uAtmoClouds, uv, 0.0).r;
                }
                return 1.0 - uAtmoCloud.x * (1.0 - transmission);
            }
            vec3 l = uAtmoSun.xyz;
            float h = uAtmoCloud.z;
            vec2 uv = (world.xz + l.xz * (max(h - world.y, 0.0) / max(l.y, 1e-3))) * (0.1 / h);
            float o = uAtmoCloud.y;
            float cloud = textureLod(uAtmoClouds, uv + uAtmoCloudWind.xy + vec2({{F(CloudLayer.SecondLookupShift.X)}}, {{F(CloudLayer.SecondLookupShift.Y)}}), {{F(MeitouClouds.ShadowLod)}}).r;
            float tile = textureLod(uAtmoCloudsTile, uv - uAtmoCloudWind.xy, {{F(MeitouClouds.ShadowLod)}}).r;
            float density = (cloud + o) * {{F(CloudLayer.DensityMultiplier)}} + tile * {{F(CloudLayer.TileWeight)}} + 1.0;
            float alpha = clamp(density * clamp(1.0 - tile + o, 0.0, 1.0), 0.0, 1.0);
            return 1.0 - uAtmoCloud.x * alpha;
        }
        """;

    /// <summary>
    /// What every world shader includes (fragment stage, or the grass's vertex stage).
    /// </summary>
    public static readonly string Functions = $$"""

        const float ATMO_PI = 3.14159265358979;
        uniform vec4 uAtmoSun;        // xyz: direction to the sun (its real height)
        uniform vec4 uAtmoLight;      // xyz: the lighting direction (the sun's with y clamped to 0), w: the environment light's factor
        uniform vec3 uAtmoSunLight;   // sunColour.rgb · sunColour.w (docs/formats/lighting.md); the shader adds the ambient map's alpha · 2
        uniform vec4 uAtmoParams;     // x 1 game sky / 0 simple, y: distance where the physical haze is complete, z: scale height in world units (physical haze), w: 1 the soft grazing sun (GrazingSun)
        uniform vec4 uAtmoTau;        // physical haze: rgb Rayleigh optical depth straight up, a Mie's
        uniform vec3 uAtmoTint;       // the weather's sky colour multiplier (a viewer stand-in, see sky.md)
        uniform vec4 uAtmoFog;        // the weather's fog: x start, y end, z enabled
        uniform vec3 uAtmoFogColour;
        uniform vec4 uAtmoSimple;     // simple sky: fog colour, distance of complete fog
        uniform vec4 uAtmoHaze;       // Kenshi haze: x 1 when on, y start, z end of the atmosphere fog, w weather fog density (1 / distance)
        uniform vec4 uAtmoHazeCloud;  // Kenshi haze: horizonClouds, rgb the cloud colour (a stand-in), a how far the haze is pulled to it
        uniform vec4 uAtmoAltitude;   // viewer: x the physical haze's share (0 the game's haze, 1 the physical), y haze strength (1 the game's), z how far the eye is above the game's camera heights (0 within them, 1 high above)
        uniform vec4 uAtmoMaps;       // x irradiance cube, y specular cube, z ambient map present (0: none, else 1 + the night ambient floor); w half the world's width
        uniform vec4 uAtmoNight;      // Meitou night air (docs/formats/sky.md "Night air"): rgb its colour at full night at the horizon, a its weight (0: Faithful, or by day)
        uniform samplerCube uAtmoIrradiance;
        uniform samplerCube uAtmoSpecular;
        uniform sampler2D uAtmoAmbientMap;
        uniform vec4 uAtmoCloud;      // x strength, y legacy density offset or volume coverage, z base height, w 1 volume transmission map
        uniform vec4 uAtmoCloudWind;  // legacy: xy wind shift; volume: xy map origin, z map span
        uniform sampler2D uAtmoClouds;
        uniform sampler2D uAtmoCloudsTile;
        {{CloudShadow}}
        uniform vec4 uWeatherWet;     // the weather's shared surface values (WeatherSurfaces): x wetness, y rainAmount = saturate(rain / 50), z gameTime (hours), w the water's height
        uniform vec4 uWeatherDust;    // xyz: dustAmount (current, inside, slope; docs/formats/weather.md "Dust")
        uniform sampler2D uWeatherDustNoise;   // the dust noise (Turbulent.dds), sampled at world.xz * 0.002
        uniform sampler2D uWeatherGround;      // the terrain's whole-world ground colour map: the dust colour (the BIOMES `ground colour` where the object is)
        uniform vec4 uGiParams;       // Meitou probe GI: x on, y strength, z 1 post resolve + 2 reflections, w indoor surface gain (0 bypasses indoor GI; compute has its own params)
        uniform vec4 uGiGrid[4];      // per cascade: the grid's place and spacing (Gi.GiShaders.ProbeSampling)
        uniform sampler2D uGiIrradiance, uGiDistance, uGiBase;
        {{Gi.GiShaders.ProbeSampling}}
        uniform vec4 uLightGrid;      // the lamps (docs/render-lights.md): the grid's corner x, z, its cell size, cells per side (0: no lamps)
        uniform sampler2D uLightCells, uLightIndex, uLightData, uLampTiles;
        {{LampShaders.Defines}}{{LampShaders.TileMapping}}{{LampShaders.TileVisibility}}
        #define LAMP_VISIBILITY(world, n, toLamp, indoor, emitter) lampTileVisibility(lamp, toLamp, d, n)
        {{LampShaders.Lighting}}

        // ---- the weather's surfaces (docs/formats/weather.md "Rain and wetness", "Dust") ----
        // common/wet.hlsl makeWet: waterRel = the water's height minus the pixel's, edge the width of the water-line band (0.5 objects and foliage, 2 terrain).
        // The game's darkening term is negative below wet = 0.3 (it would brighten dry ground by up to 0.2 absorbance); the viewer keeps the dry look, so it is clamped at 0.
        void makeWet(inout vec4 frag, float amount, float absorbance, float waterRel, float edge)
        {
            float underwater = clamp((waterRel + edge) / edge, 0.0, 1.0);
            float wet = min(amount + underwater, 1.0);
            float darken = (1.0 - 1.0 / (wet + 0.7)) * absorbance;
            float shine = 2.0 * pow(max(0.0, wet - 0.15 * absorbance), 1.0 + 4.0 * absorbance);
            darken = min(darken + underwater * 0.2, 0.4);
            shine *= clamp((1.0 - underwater) * 100.0, 0.0, 1.0);   // underwater things do not go shiny
            frag.rgb *= 1.0 - max(darken, 0.0) * 0.5;
            frag.a = mix(frag.a, 0.5, clamp(shine, 0.0, 1.0));
        }
        // ---- the wet world (Meitou, docs/formats/weather.md "Wet world"; uWetWorld 1 with the row on) ----
        uniform float uWetClock;   // game seconds wrapping at 4096 (WeatherSurfaces.Seconds)
        uniform float uWetWorld;
        uniform vec4 uWetSplash[8];   // the last feet in the puddles: x, z, the wet clock at the step, the strength (0: none)
        // Three numbers in 0..1 from a cell's whole coordinates (an integer hash: one of floats repeated across a large surface).
        vec3 wetHash(vec3 cell, uint salt)
        {
            uvec3 v = uvec3(ivec3(cell)) + salt * 0x9E3779B9u;
            v = v * 1664525u + 1013904223u;
            v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
            v ^= v >> 16u;
            v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
            return vec3(v) * (1.0 / 4294967296.0);
        }
        // A smooth noise in 0..1 of a position (the hash at the cell's corners, blended).
        float wetNoise(vec3 p)
        {
            vec3 c = floor(p), f = p - c;
            f = f * f * (3.0 - 2.0 * f);
            float x00 = mix(wetHash(c, 7u).x, wetHash(c + vec3(1.0, 0.0, 0.0), 7u).x, f.x);
            float x10 = mix(wetHash(c + vec3(0.0, 1.0, 0.0), 7u).x, wetHash(c + vec3(1.0, 1.0, 0.0), 7u).x, f.x);
            float x01 = mix(wetHash(c + vec3(0.0, 0.0, 1.0), 7u).x, wetHash(c + vec3(1.0, 0.0, 1.0), 7u).x, f.x);
            float x11 = mix(wetHash(c + vec3(0.0, 1.0, 1.0), 7u).x, wetHash(c + vec3(1.0, 1.0, 1.0), 7u).x, f.x);
            return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z);
        }
        // One size of drops standing on a surface: p a position that stays with the surface, in world units (1 = 10 cm); in each cell one drop at most, anywhere
        // in it. n the surface's normal, fall the way down along it (its length the slope), lit the way to the sun along it, all in p's space. As on wet skin and
        // metal no drop is a disc: its outline wobbles, on a slope it hangs (longer above than below), it is a flat cap (it meets the surface at about 40
        // degrees). Without rain the drops stand as they are. In the rain a drop swells from nothing over most of its time, then on a slope lets go: it runs
        // down (up to two cells, faster as it goes, growing thinner), leaving a narrow track that it drains, and is gone at the end of its run as the cell's
        // next drop starts; on the flat it spreads out into the film instead. Nothing appears or vanishes at once.
        // bead: the drop's normal, cover: how much of the fragment it takes, lens: where in the drop the fragment lies along the light (-1 far side, 1 near side).
        void wetDropLayer(vec3 p, float cell, float rMin, float rMax, float hold, float rain, vec3 n, vec3 fall, vec3 lit, inout vec3 bead, inout float cover, inout float lens)
        {
            float steep = length(fall);
            vec3 down = fall / max(steep, 1e-4), side = cross(n, down);
            float runs = rain > 0.0 ? smoothstep(0.15, 0.6, steep) : 0.0;
            int rows = runs > 0.0 ? 3 : 1;   // a running drop comes from up to two cells up the slope
            for (int k = 0; k < rows; k++)
            {
                vec3 base = floor((p - down * (float(k) * cell)) / cell - 0.5);
                for (int i = 0; i < 8; i++)
                {
                    vec3 c = base + vec3(float(i & 1), float((i >> 1) & 1), float(i >> 2));
                    vec3 h = wetHash(c, 0u), j = wetHash(c, 1u);
                    if (h.z > hold) continue;
                    float size = cell * mix(rMin, rMax, h.y * h.y), r = size * sqrt(0.4 + 0.6 * h.x), slide = 0.0, fade = 1.0;
                    if (rain > 0.0)
                    {
                        // The rates divide the clock's wrap (4096 s), so no drop jumps when it wraps.
                        float phase = fract(h.x + uWetClock * (2.0 + floor(j.z * 6.0)) / 64.0);
                        const float grow = 0.72;
                        float u = max(phase - grow, 0.0) / (1.0 - grow);
                        r = size * sqrt(min(phase / grow, 1.0));
                        slide = cell * 2.0 * runs * u * u;
                        r *= mix(1.0 + 0.3 * u, 1.0 - 0.45 * u, runs);
                        fade = mix(1.0 - u, 1.0 - smoothstep(0.7, 1.0, u), runs);
                    }
                    if (r < 1e-4 || fade <= 0.0) continue;
                    vec3 d = p - (c + j) * cell - down * slide;
                    float off = dot(d, n);
                    if (abs(off) > cell * 0.45) continue;
                    vec3 along = d - n * off;
                    float up = -dot(along, down), across = dot(along, side);
                    // The track behind a running drop: a thin ridge of water, narrower towards where the drop started.
                    if (slide > 0.0 && up > 0.0 && up < slide)
                    {
                        float w = r * 0.38 * (1.0 - 0.6 * up / slide), lat = across + r * 0.12 * sin(up * (5.0 / r) + h.z * 6.2832);
                        float track = smoothstep(w, w * 0.5, abs(lat)) * fade * 0.85;
                        if (track > cover) { cover = track; bead = normalize(n + side * (lat / w) * 0.7); lens = dot(side, lit) * lat / w; }
                    }
                    along += r * 0.2 * sin(along.yzx * (2.3 / r) + h.zxy * 6.2832);
                    up = -dot(along, down);
                    if (up > 0.0) along += down * up * (1.0 - 1.0 / (1.0 + 2.2 * steep * h.x));
                    float rho = length(along);
                    if (rho >= r) continue;
                    float mask = smoothstep(r, r * 0.8, rho) * fade, sphere = r * 1.5;
                    if (mask <= cover) continue;
                    cover = mask;
                    bead = normalize(along / sqrt(sphere * sphere - rho * rho) + n);
                    lens = dot(along, lit) / r;
                }
            }
        }
        // The drops on a wet surface, two sizes of them (radius 1.1 to 3.7 mm; 0.4 to 1 mm where a pixel is under 0.6 mm): how much of the fragment a drop takes.
        // p in world units staying with the surface, pixel the length of p a pixel covers (the fragment stage's derivatives: these functions also reach vertex
        // stages); hold how much of the surface holds drops, rain the weather's rainAmount; n, below (the way down) and sun (towards it) in p's space. How many
        // drops stand varies from place to place (two noises, 7 cm and 40 cm), so a large surface is no even field of them.
        float wetDrops(vec3 p, float pixel, float hold, float rain, vec3 n, vec3 below, vec3 sun, out vec3 bead, out float lens)
        {
            bead = n;
            lens = 0.0;
            float shown = 1.0 - smoothstep(0.006, 0.02, pixel);
            if (shown <= 0.0 || hold <= 0.02) return 0.0;
            hold *= mix(0.45, 1.5, wetNoise(p / 0.7)) * mix(0.7, 1.3, wetNoise(p / 4.0));
            vec3 fall = below - n * dot(below, n), lit = sun - n * dot(sun, n);
            lit /= max(length(lit), 1e-3);
            float cover = 0.0;
            wetDropLayer(p, 0.11, 0.1, 0.34, hold, rain, n, fall, lit, bead, cover, lens);
            if (pixel < 0.006) wetDropLayer(p, 0.035, 0.12, 0.3, hold, rain, n, fall, lit, bead, cover, lens);
            return cover * shown;
        }
        // A wet object's surface. p stays with the surface (small numbers: an object's vertex less its origin), pixel the length of p a pixel covers; dryGloss its
        // gloss before the wetness; wet the weather's wetness, rain its rainAmount; sun towards the sun. What does not take the water in (a glossy surface) holds
        // it on top:
        // - on a wall the water runs down unevenly: darker, shinier tracks (the dust noise stretched 30 times down the wall, drifting down in the rain);
        // - what faces the sky keeps a film of it (gloss up to 0.8);
        // - close by, drops stand on it (wetDrops).
        void wetObject(vec3 p, float pixel, float dryGloss, float wet, float rain, vec3 sun, inout vec3 n, inout vec3 albedo, inout float gloss)
        {
            float sheds = 1.0 - clamp(1.0 - 2.0 * dryGloss, 0.0, 1.0);
            float amount = max(wet, rain);
            float wall = 1.0 - smoothstep(0.3, 0.7, abs(n.y));
            if (wall > 0.0)
            {
                vec3 side = normalize(cross(n, vec3(0.0, 1.0, 0.0)) + vec3(1e-4, 0.0, 0.0));
                float track = texture(uWeatherDustNoise, vec2(dot(p, side) * 0.31, p.y * 0.011 + uWetClock * rain * (16.0 / 4096.0))).x;
                float run = smoothstep(0.45, 0.75, track) * wall * amount * mix(0.35, 1.0, sheds);
                albedo *= 1.0 - 0.18 * run;
                gloss = mix(gloss, max(gloss, 0.78), run);
            }
            float film = smoothstep(0.6, 0.95, n.y) * smoothstep(0.3, 0.9, amount) * sheds;
            gloss = mix(gloss, max(gloss, 0.8), film);
            float hold = amount * mix(0.3, 1.0, smoothstep(-0.2, 0.6, n.y)) * mix(0.2, 1.0, sheds);
            vec3 bead;
            float lens, cover = wetDrops(p, pixel, hold, rain, n, vec3(0.0, -1.0, 0.0), sun, bead, lens);
            if (cover <= 0.0) return;
            n = normalize(mix(n, bead, 0.8 * cover));
            gloss = mix(gloss, 0.9, cover);
            albedo *= 1.0 + cover * (0.25 * smoothstep(0.0, 0.9, -lens) - 0.2 * smoothstep(0.1, 0.9, lens));
        }
        // Wet foliage (leaves, blades): the game's wetness stays as it is (its darkening and its gloss, 0.3 on grass and 0.5 on leaves at the most), and
        // the water also shows as drops, seen as glints. (A film of more gloss on top, 0.62, turned a wet field white as if frosted: user report.) In
        // cells of the leaf's own texture coordinates (40 across the texture, so they stay on the leaf as it sways) about a fifth hold a drop when all is
        // wet: a small disc where the gloss is 0.95 and the normal leans a way of its own, so each catches the light from a different side. cells: how
        // many of those cells a pixel covers (the fragment stage's derivatives); the drops go where it is over one.
        void wetFoliage(vec2 uv, float cells, float wet, float rain, inout vec3 n, inout float gloss)
        {
            float amount = max(wet, rain);
            if (amount <= 0.05) return;
            float shown = 1.0 - smoothstep(0.5, 1.2, cells);
            if (shown <= 0.0) return;
            vec2 g = uv * 40.0, c = floor(g);
            vec3 h = wetHash(vec3(c.x, 5.0, c.y), 5u);
            if (h.z > 0.22 * amount) return;
            float r = 0.12 + 0.16 * h.x, drop = smoothstep(r, r * 0.6, length(g - c - 0.3 - 0.4 * h.xy)) * shown;
            n = normalize(n + (wetHash(vec3(c.x, 6.0, c.y), 6u) - 0.5) * 0.9 * drop);
            gloss = mix(gloss, 0.95, drop);
        }
        // The slope (x, z) the rain's impacts give a puddle's surface at xz: in each 16 cm cell a drop lands 1 to 1.75 times a second, somewhere new each
        // time, and its ring widens to 11 cm as it dies down. The slope is the ring's profile differentiated (no height is sampled twice).
        vec2 wetRings(vec2 xz)
        {
            const float cell = 1.6;
            vec2 base = floor(xz / cell), slope = vec2(0.0);
            for (int i = 0; i < 9; i++)
            {
                vec2 c = base + vec2(float(i % 3) - 1.0, float(i / 3) - 1.0);
                vec3 h = wetHash(vec3(c.x, 3.0, c.y), 3u);
                float turn = h.z + uWetClock * (64.0 + floor(h.x * 48.0)) / 64.0, t = fract(turn);
                vec3 at = wetHash(vec3(c.x, floor(turn), c.y), 4u);
                vec2 d = xz - (c + 0.15 + 0.7 * at.xy) * cell;
                float dist = length(d), w = (dist - t * cell * 0.7) / (cell * 0.05);
                slope += d / max(dist, 1e-3) * (-2.0 * w * exp(-w * w)) * (1.0 - t) * (1.0 - t);
            }
            return slope;
        }
        // The slope (x, z) a foot's step gives a puddle's surface at xz: from where it stepped (uWetSplash) a short train of ripples runs out at half a
        // metre a second, flattening over three seconds and with the distance.
        vec2 wetSteps(vec2 xz)
        {
            vec2 slope = vec2(0.0);
            for (int i = 0; i < 8; i++)
            {
                vec4 s = uWetSplash[i];
                if (s.w <= 0.0) continue;
                float age = mod(uWetClock - s.z + 4096.0, 4096.0);
                if (age > 3.0) continue;
                vec2 d = xz - s.xy;
                float dist = length(d), front = 0.6 + age * 5.0, w = (dist - front) / 0.45;
                if (abs(w) > 4.0) continue;
                float left = 1.0 - age / 3.0;
                slope += d / max(dist, 1e-3) * sin(w * 3.0) * exp(-0.35 * w * w) * s.w * left * left / (1.0 + 0.15 * front);
            }
            return slope;
        }
        // Puddles on wet ground (the terrain). Water gathers where level ground lies low: "low" is two noises of the place (8 m and 1.5 m across, a 30 cm
        // one for the outline) less hollow, how far the ground really lies under what is round it (TerrainShaders wetHollow, and the relief's troughs
        // where the ground is displaced): a dip fills first and deepest, a rise stays dry. The water's level rises with the weather's wetness (none under 0.2; at 1 about a third of level ground, less on
        // ground that takes the rain in, absorbance). None on slopes over about 10 degrees. In a puddle the ground shows darker through the water, the
        // surface is level and shines (gloss 0.95), ringed by the rain's impacts while it rains and by the feet that step in it (wetSteps; WetPuddles is this
        // rule on the CPU, which finds those feet); round it a rim of soaked ground. world: the fragment;
        // level: the ground's own normal; pixel: the ground a pixel covers (the fine outline and the rings go where it is over 30 cm and 15 cm).
        void wetGround(vec3 world, vec3 level, float absorbance, float wet, float rain, float pixel, float hollow, inout vec3 n, inout vec4 surface)
        {
            float even = smoothstep(0.955, 0.99, level.y);
            if (even <= 0.0 || wet <= 0.2) return;
            vec3 at = vec3(world.x, 0.0, world.z);
            float low = 0.6 * wetNoise(at / 80.0) + 0.4 * wetNoise(at / 15.0) + 0.1 * (wetNoise(at / 3.0) - 0.5) * (1.0 - smoothstep(1.5, 3.0, pixel)) - hollow;
            float fill = smoothstep(0.2, 1.0, wet) * mix(0.47, 0.3, clamp(absorbance, 0.0, 1.0)) - (1.0 - even) * 0.3;
            float over = fill - low;   // how far the water stands over the ground here (in the noise's units)
            float rim = smoothstep(-0.07, 0.0, over), water = smoothstep(0.0, 0.025, over);
            if (rim <= 0.0) return;
            surface.rgb *= mix(1.0, 0.82, rim) * mix(1.0, mix(0.8, 0.5, smoothstep(0.0, 0.2, over)), water);
            surface.a = mix(mix(surface.a, max(surface.a, 0.6), rim), 0.95, water);
            if (water <= 0.0) return;
            vec3 up = vec3(0.0, 1.0, 0.0);
            float fine = 1.0 - smoothstep(0.6, 1.5, pixel);
            if (fine > 0.0)
            {
                vec2 s = wetSteps(world.xz) * 0.5 * fine;
                if (rain > 0.0) s += wetRings(world.xz) * 0.35 * rain * fine;
                up = normalize(vec3(-s.x, 1.0, -s.y));
            }
            n = normalize(mix(n, up, water));
        }
        // objects.hlsl / triplanar.hlsl: how far a surface is covered by dust (0..1). n: its normal, gloss: the diffuse alpha before `specular mult`, amount: dustAmount.x (.y inside).
        float dustCover(vec3 n, float gloss, vec3 world, float amount)
        {
            float noiseEffect = 2.2 - texture(uWeatherDustNoise, world.xz * 0.002).x * 6.0;
            float slopeEffect = clamp((n.y - 0.7 + uWeatherDust.z) * 4.0, 0.0, 1.0);
            float dust = slopeEffect - gloss * 6.0 + noiseEffect;
            dust = dust * 2.0 + amount - 1.0;
            dust *= min(1.0, amount * 8.0);   // completely gone at amount 0
            return clamp(dust, 0.0, 1.0);
        }
        vec3 dustColour(vec3 world)
        {
            return texture(uWeatherGround, (world.xz + uAtmoMaps.w) / (2.0 * uAtmoMaps.w)).rgb;
        }

        // ---- SkyX (Meitou.Data SkyXModel) ----
        const float SKYX_INNER = {{F(SkyAtmosphere.InnerRadius)}};
        const float SKYX_CAMERA_Y = {{F(SkyAtmosphere.CameraY)}};
        const float SKYX_SCALE = {{F(1 / SkyAtmosphere.Thickness)}};
        const float SKYX_SCALE_DEPTH = {{F(SkyAtmosphere.ScaleDepth)}};
        const float SKYX_SOSD = {{F(1 / SkyAtmosphere.ScaleHeight)}};
        const vec3 SKYX_EXTINCTION = {{V3(SkyAtmosphere.Extinction)}};
        const vec3 SKYX_RAYLEIGH = {{V3(SkyAtmosphere.InverseWaveLength4 * SkyAtmosphere.KrESun)}};   // invλ⁴ · Kr · sun
        const float SKYX_MIE = {{F(SkyAtmosphere.KmESun)}};
        const float SKYX_KR4PI = {{F(SkyAtmosphere.Kr4Pi)}};
        const float SKYX_G = {{F(SkyAtmosphere.PhaseG)}};
        const float SKYX_EXPOSURE = {{F(SkyAtmosphere.Exposure)}};
        const float HAZE_DOME = {{F(KenshiHaze.DomeRadius)}};
        float skyxScale(float c)
        {
            float x = 1.0 - c;
            return SKYX_SCALE_DEPTH * exp(-0.00287 + x * (0.459 + x * (3.83 + x * (-6.80 + x * 5.25))));
        }
        // Attenuated, density-weighted path from SkyX's camera along ray for len SkyX units, 4 samples; thickness: without the attenuation.
        vec3 skyxInScatter(vec3 ray, float len, vec3 sun, out float thickness)
        {
            vec3 start = vec3(0.0, SKYX_CAMERA_Y, 0.0);
            float startOffset = exp(SKYX_SOSD * (SKYX_INNER - SKYX_CAMERA_Y)) * skyxScale(ray.y);
            float step = len * 0.25;
            vec3 sum = vec3(0.0);
            thickness = 0.0;
            for (int i = 0; i < 4; i++)
            {
                vec3 p = start + ray * (step * (float(i) + 0.5));
                float h = length(p), density = exp(SKYX_SOSD * (SKYX_INNER - h));
                float optical = startOffset + density * (skyxScale(dot(sun, p) / h) - skyxScale(dot(ray, p) / h));
                // The floor is a viewer guard (SkyXModel.OpticalFloor): steep rays down from far above the game's camera heights.
                sum += exp(-clamp(optical, {{F(SkyXModel.OpticalFloor)}}, 1e4) * SKYX_EXTINCTION) * (density * step * SKYX_SCALE);
                thickness += density * step * SKYX_SCALE;
            }
            return sum;
        }
        float skyxRayleighPhase(float c) { return 0.75 * (1.0 + 0.5 * c * c); }
        float skyxMiePhase(float c)
        {
            const float g2 = SKYX_G * SKYX_G;
            return 1.5 * ((1.0 - g2) / (2.0 + g2)) * (1.0 + c * c) / pow(1.0 + g2 - 2.0 * SKYX_G * c, 1.5);
        }
        // Meitou's night air (NightAir in Meitou.Data): the glow of the air towards a unit direction, its colour times the twilight weight and the falloff with the
        // direction's height (exp(-y / height), 1 at and below the horizon). The sky adds it to its colour and the haze to the colour it fades the land into, the
        // same function of the direction, so fully hazed land and the sky above it meet. Zero (Faithful, or by day) adds exactly nothing.
        vec3 atmoNightAir(vec3 dir)
        {
            return uAtmoNight.rgb * (uAtmoNight.a * exp(-max(dir.y, 0.0) * {{F(1 / NightAir.FalloffHeight)}}));
        }
        // The share of a distant body's light (a planet) the night air in front of it hides: the haze's own strength at the horizon, thinning with the height as the glow does
        // (NightAir.PlanetVeil). Zero in Faithful and by day, so the body is drawn as the game draws it.
        float atmoNightVeil(vec3 dir)
        {
            return clamp(uAtmoNight.a * uAtmoAltitude.y * exp(-max(dir.y, 0.0) * {{F(1 / NightAir.VeilHeight)}}), 0.0, 1.0);
        }
        // The skydome's HDR colour (SkyX_Skydome.hlsl, HDR branch); night: SkyX's night factor (where the glow and the stars show).
        vec3 atmoSky(vec3 dir, out float night)
        {
            vec3 d = normalize(vec3(dir.x, max(dir.y, 0.0), dir.z));   // below the horizon the horizon's colour (the ground covers it)
            vec3 ray = d + vec3(0.0, SKYX_INNER - SKYX_CAMERA_Y, 0.0);
            float far = length(ray);
            ray /= far;
            float thickness;
            vec3 sum = skyxInScatter(ray, far, uAtmoSun.xyz, thickness);
            float c = -dot(uAtmoSun.xyz, ray);   // SkyX: the cosine towards the eye
            vec3 colour = SKYX_EXPOSURE * (skyxRayleighPhase(c) * SKYX_RAYLEIGH * sum + skyxMiePhase(c) * SKYX_MIE * sum);
            night = clamp(1.0 - max(colour.r, max(colour.g, colour.b)) * 10.0, 0.0, 1.0) * (1.0 - clamp(thickness * SKYX_KR4PI, 0.0, 1.0));
            // SkyX's night glow gives way to the night air as it comes in (uAtmoNight.a 1: the night air alone; 0: the game's glow, exactly).
            colour += night * pow(vec3(0.05, 0.05, 0.1) * (2.0 - 0.75 * clamp(-uAtmoSun.y, 0.0, 1.0)) * pow(1.0 - d.y, 3.0), vec3(2.2)) * (1.0 - uAtmoNight.a) + atmoNightAir(d);
            return colour * uAtmoTint;
        }
        vec3 atmoSky(vec3 dir) { float n; return atmoSky(dir, n); }

        {{ShadowShaders.Functions}}
        // ---- the deferred lighting pass (deferred.hlsl main_fs, lightingFunctions.hlsl; docs/formats/lighting.md) ----
        {{AmbientFloorFunction}}
        vec4 atmoAmbientMapAt(vec3 world)
        {
            if (uAtmoMaps.z < 0.5) return vec4(1.0, 1.0, 1.0, 0.5);
            return atmoAmbientFloor(texture(uAtmoAmbientMap, (world.xz + uAtmoMaps.w) / (2.0 * uAtmoMaps.w)), uAtmoMaps);
        }
        vec3 atmoIrradiance(vec3 n)
        {
            if (uAtmoMaps.x < 0.5) return {{V3(new(1.09f, 1.20f, 1.51f))}};
            vec4 t = textureLod(uAtmoIrradiance, n, {{F(KenshiLighting.IrradianceLevel)}});
            return t.rgb * t.a * 4.0;
        }
        float kenshiEnvBrdf(float gloss, float nv)
        {
            // Lazarov's analytic environment BRDF for a specular colour of 0.04.
            vec4 t = vec4(1.0 / 0.96, 0.475, (0.0275 - 0.25 * 0.04) / 0.96, 0.25) * gloss + vec4(0.0, 0.0, (0.015 - 0.75 * 0.04) / 0.96, 0.75);
            float a0 = t.x * min(t.y, exp2(-9.28 * nv)) + t.z;
            return clamp(a0 + 0.04 * (t.w - a0), 0.0, 1.0);
        }
        {{EnvBrdfFunction}}
        float atmoMetalness = 0.0;   // the surface's metalness (a worn item's colour map blue, CharacterShaders), set before kenshiLight / stageLight; 0 dielectric
        float atmoMetalDiffuse = 0.0;   // how much of a metal's diffuse is kept (Meitou's estimated metal, kenshiLight)
        vec3 atmoSurfaceNormal = vec3(0.0);   // the surface's own normal under its normal map (terrain, objects), set before kenshiLight; zero: none
        // The normal the sun's terms take. Meitou's soft grazing sun (uAtmoParams.w, optional): where the sun grazes the surface itself its bumps shadow each other,
        // which a normal map cannot show, so the map fades out of the sun's light below 24 degrees and is gone at 6 and on a surface facing away.
        vec3 atmoSunNormal(vec3 n)
        {
            if (uAtmoParams.w < 0.5 || dot(atmoSurfaceNormal, atmoSurfaceNormal) < 0.5) return n;
            return normalize(mix(atmoSurfaceNormal, n, smoothstep(0.1, 0.4, dot(atmoSurfaceNormal, uAtmoLight.xyz))));
        }
        float atmoTranslucency = 0.0;   // how much sun a cut-out surface lets through from behind (objects.hlsl TRANSPARENCY)
        // The character editor's stage (a fog distance of -2): the game's rtt.hlsl (docs/characters.md "Character editor"): one punctual light of colour 0.6 (CalcPunctualLight: diffuse
        // pi * N.L * (1 - 0.04), GGX specular for roughness 1 - 0.99 * gloss, specular colour 0.04) plus the environment light of the irradiance and specular cubes
        // (CalcEnvironmentLight), times the exposure 0.5, stored as it is in an 8-bit target (UNORM, as the icons). l: towards the light, v: towards the eye.
        // hdr (Meitou's stage and portraits, a fog distance of -3, CharacterRenderer.Look): unclamped, for the preview resolve's tone map.
        // diffuseNl: the sun's diffuse term in place of the clamped N.L (the Meitou skin, CharacterShaders.skinDiffuse).
        vec3 stageLight(vec3 albedo, vec3 n, vec3 v, vec3 l, float gloss, bool hdr, vec3 diffuseNl)
        {
            const float PI = 3.14159265;
            vec3 specColour = mix(vec3(0.04), albedo, atmoMetalness);   // metalness as in kenshiLight (rtt.hlsl)
            albedo *= 1.0 - atmoMetalness;
            float fresnelDiffuse = clamp(1.0 - dot(specColour, vec3(1.0 / 3.0)), 0.0, 1.0);
            vec3 hv = normalize(v + l);
            float nl = clamp(dot(n, l), 0.0, 1.0), lh = clamp(dot(l, hv), 0.0, 1.0), nh = clamp(dot(n, hv), 0.0, 1.0), nv = clamp(dot(n, v), 0.0, 1.0);
            float rough = 1.0 - gloss * 0.99, alpha = rough * rough, alphaSqr = alpha * alpha;
            float denom = max(1e-6, nh * nh * (alphaSqr - 1.0) + 1.0);
            float D = alphaSqr / (PI * denom * denom);
            float fresnel = exp2((-5.55473 * lh - 6.98316) * lh);
            float k = alpha * 0.5, k2 = k * k, vis = 1.0 / (lh * lh * (1.0 - k2) + k2);
            vec3 sunSpecular = 0.6 * nl * D * (specColour + (1.0 - specColour) * fresnel) * vis / PI;
            vec3 sunDiffuse = PI * diffuseNl * 0.6 * fresnelDiffuse;
            vec3 envDiffuse = atmoIrradiance(n) * fresnelDiffuse;
            vec3 envSpecular = vec3(0.0);
            if (uAtmoMaps.y > 0.5)
            {
                vec3 dominant = mix(n, reflect(-v, n), gloss * (sqrt(gloss) + rough));
                vec4 r = textureLod(uAtmoSpecular, dominant, (1.0 - gloss) * 7.0);
                envSpecular = kenshiEnvBrdf(gloss, nv, specColour) * r.rgb * r.a * 10.0;
            }
            vec3 lit = (albedo * (sunDiffuse + envDiffuse) + sunSpecular + envSpecular) * 0.5;
            return hdr ? lit : clamp(lit, 0.0, 1.0);
        }
        vec3 stageLight(vec3 albedo, vec3 n, vec3 v, vec3 l, float gloss, bool hdr) { return stageLight(albedo, n, v, l, gloss, hdr, vec3(clamp(dot(n, l), 0.0, 1.0))); }
        // What a lit fragment writes to the GI resolve's surface target (GiResolveShaders.WithAlbedo; RGBA32UI, 0: no probes here), set by kenshiLight:
        // x, y the shading normal's octahedral position (16 bits each), z sqrt(albedo) red | green << 8, w blue | takes the probes << 8 | interior << 9 | gloss << 10
        // (with the reflections on: 1 + gloss * 62, else 0) | traced << 16 | receiver << 17 (with the traced sun, docs/render-shadows.md "Traced sun": also from the
        // programs that read the probes themselves) | lamp receiver << 19 (a character, with its normal and albedo, while the lamps are traced).
        uvec4 giAlbedoOut = uvec4(0u);
        bool giInterior = false;
        // The sun term the traced sun's pass adds where its rays reach (location 2, GiResolveShaders.WithAlbedo), before the haze, and the kind of surface, set before kenshiLight:
        // 0 solid, 1 grass, 2 foliage cards, 3 characters; below 0: stays with the cascades (impostors, whose quad is not their surface).
        vec3 tracedSunOut = vec3(0.0);
        int atmoReceiver = 0;
        // How much of the sun at a world point is written apart (tracedSunOut): all of it at every depth while this view traces (uGiParams.z bit 4, uMsFlags.w above 0; the traced
        // sun is the main view's) or its contact shadows take the sun alone (bit 8: with its cascades, PostProcess.ContactSun), on every receiver but the impostors.
        float atmoTracedShare(vec3 world)
        {
            int bits = int(uGiParams.z + 0.5);
            return atmoReceiver >= 0 && ((bits & 8) != 0 || ((bits & 4) != 0 && uMsFlags.w >= 0.5)) ? 1.0 : 0.0;
        }
        float atmoSelfShadow = 1.0;   // a surface's own shadow on the sun (the terrain's relief, GroundRelief), set before kenshiLight; 1 none
        float atmoShadowLift = 0.0;   // world units above the point the sun shadow is looked up from (a ground trough under its flat casters, GroundRelief)
        // A surface lit as Kenshi's main lighting pass lights it (metalness atmoMetalness): HDR colour. v: towards the eye.
        // diffuseNl: the sun's diffuse term in place of the clamped N.L (the Meitou skin, CharacterShaders.skinDiffuse); the overload below passes N.L.
        vec3 kenshiLight(vec3 albedo, vec3 n, vec3 v, float gloss, vec3 world, vec3 diffuseNl)
        {
            // Metalness (deferred.hlsl): the specular colour runs from 0.04 to the albedo, the diffuse albedo to black; diffuse light x (1 - the specular colour's mean).
            // atmoMetalDiffuse (Meitou's estimated metal on buildings; 0 is the game): the reflections come on top of the texture's own light instead of replacing it, and are not as dark as it.
            vec3 specColour = mix(vec3(0.04), mix(albedo, pow(max(albedo, vec3(0.0)), vec3(0.85)), atmoMetalDiffuse), atmoMetalness);
            albedo *= 1.0 - atmoMetalness * (1.0 - atmoMetalDiffuse);
            float fresnelDiffuse = clamp(1.0 - dot(mix(specColour, vec3(0.04), atmoMetalDiffuse), vec3(1.0 / 3.0)), 0.0, 1.0);
            vec4 am = atmoAmbientMapAt(world);
            vec3 l = uAtmoLight.xyz;
            vec3 sunBase = uAtmoSunLight * am.a * 2.0 * atmoCloudShadow(world);
            vec3 shadowPoint = world + vec3(0.0, atmoShadowLift, 0.0);
            float tracedShare = atmoTracedShare(world);
            // The sun shadow term (deferred.hlsl: lightColor * shadow); what the traced sun takes over leaves the colour, shadowed by the characters' layers alone.
            vec3 sun = tracedShare < 1.0 ? sunBase * kenshiShadow(shadowPoint, n) * atmoSelfShadow * (1.0 - tracedShare) : vec3(0.0);
            vec3 tracedSun = vec3(0.0);
            if (tracedShare > 0.0)
            {
                msTraced = (int(uGiParams.z + 0.5) & 8) == 0;   // the rays hold the cascades' casters; the contact shadows' sun keeps its cascades
                tracedSun = sunBase * kenshiShadow(shadowPoint, n) * atmoSelfShadow * tracedShare;
                msTraced = false;
            }
            gloss = clamp(gloss, 0.0, 1.0);
            float roughness = 1.0 - gloss * 0.99, a = roughness * roughness, a2 = a * a;
            vec3 ns = atmoSunNormal(n);
            float nl = clamp(dot(ns, l), 0.0, 1.0);
            vec3 diffuse = ATMO_PI * diffuseNl * sun * fresnelDiffuse;
            vec3 h = normalize(v + l);
            float nh = clamp(dot(ns, h), 0.0, 1.0), lh = clamp(dot(l, h), 0.0, 1.0);
            float denom = max(nh * nh * (a2 - 1.0) + 1.0, 1e-6);
            float D = a2 / (ATMO_PI * denom * denom);
            float fresnel = exp2((-5.55473 * lh - 6.98316) * lh);
            float k = a * 0.5, vis = 1.0 / (lh * lh * (1.0 - k * k) + k * k);
            vec3 sunGlint = nl * D * (specColour * vis + (1.0 - specColour) * fresnel * vis);
            vec3 sunSpecular = sun * sunGlint / ATMO_PI;
            if (tracedShare > 0.0)
            {
                tracedSunOut = albedo * (ATMO_PI * diffuseNl * tracedSun * fresnelDiffuse) + tracedSun * sunGlint / ATMO_PI;
                giAlbedoOut.xy = uvec2((giOct(n) * 0.5 + 0.5) * 65535.0 + 0.5);
                giAlbedoOut.w = (giInterior ? 512u : 0u) | 65536u | (uint(atmoReceiver) << 17);
            }
            float env = uAtmoLight.w;
            vec3 envDiffuse = atmoIrradiance(n) * fresnelDiffuse * am.rgb * env;
            // Meitou probe GI: the probes' irradiance (sky seen past the geometry, light bounced off it) in place of the sky's, and the sky's
            // reflection dimmed as much as the probes dim the sky (an occluded corner reflects no sky).
            float specularOcclusion = 1.0;
            bool useGi = uGiParams.x > 0.5 && (!giInterior || uGiParams.w > 0.5);
            #ifdef GI_IN_SHADER
            // The traced lamp shadows' receivers among the fragments that read the probes themselves: while the lamps are traced (uGiParams.z bit 16) a character writes
            // its normal and albedo with bit 19, which the lamp pass alone reads (docs/render-lights.md "Traced lamp shadows").
            if (atmoReceiver == 3 && (int(uGiParams.z + 0.5) & 16) != 0)
            {
                uvec2 o = uvec2((giOct(n) * 0.5 + 0.5) * 65535.0 + 0.5);
                uvec3 c = uvec3(sqrt(clamp(albedo, 0.0, 1.0)) * 255.0 + 0.5);
                giAlbedoOut = uvec4(o, c.r | (c.g << 8), c.b | 524288u | (giInterior ? 512u : 0u) | (giAlbedoOut.w & 458752u));
            }
            #endif
            #ifndef GI_IN_SHADER
            // The GI resolve (uGiParams.z, docs/render-gi.md "Resolve"): the sky's ambient here, the albedo out for the post pass that swaps in the probes.
            // z is bits: 1 the resolve, 2 the reflections, which want every lit surface and its gloss too (docs/render-gi.md "Reflections"), 4 the traced sun.
            int giFlags = int(uGiParams.z + 0.5);
            if (useGi ? (giFlags & 3) != 0 : (giFlags & 2) != 0)
            {
                uvec2 o = uvec2((giOct(n) * 0.5 + 0.5) * 65535.0 + 0.5);
                uvec3 c = uvec3(sqrt(clamp(albedo, 0.0, 1.0)) * 255.0 + 0.5);
                uint stored = c.b | (useGi ? 256u : 0u) | (giInterior ? 512u : 0u);
                if ((giFlags & 2) != 0) stored |= (uint(gloss * 62.0 + 0.5) + 1u) << 10;
                stored |= giAlbedoOut.w & 458752u;   // the traced sun's bits 16-18, written above
                giAlbedoOut = uvec4(o, c.r | (c.g << 8), stored);
            }
            else
                    #endif
            if (useGi)
            {
                vec4 gi = giIrradiance(world, n, v);
                gi.rgb = giSurfaceIrradiance(gi.rgb, envDiffuse, giInterior);
                gi.a *= uGiParams.y;
                float sky = dot(envDiffuse, vec3(0.2126, 0.7152, 0.0722));
                specularOcclusion = mix(1.0, clamp(dot(gi.rgb, vec3(0.2126, 0.7152, 0.0722)) / max(sky, 1e-4), 0.0, 1.0), gi.a);
                envDiffuse = mix(envDiffuse, gi.rgb, gi.a);
            }
            vec3 envSpecular = vec3(0.0);
            if (uAtmoMaps.y > 0.5)
            {
                float nv = clamp(dot(v, n), 0.0, 1.0);
                vec3 dominant = mix(n, reflect(-v, n), gloss * (sqrt(gloss) + roughness));
                vec4 r = textureLod(uAtmoSpecular, dominant, (1.0 - gloss) * 7.0);
                envSpecular = kenshiEnvBrdf(gloss, nv, specColour) * r.rgb * r.a * 10.0 * am.rgb * env * specularOcclusion;
            }
            vec3 lit = albedo * (diffuse + envDiffuse) + sunSpecular + envSpecular;
            // The game's point and spot lights, lit like the sun (docs/render-lights.md); none while uLightGrid.w is 0.
            if (uLightGrid.w > 0.5)
            {
                vec3 lampDiffuse = vec3(0.0), lampSpecular = vec3(0.0);
                lampLight(world, n, v, a, a2, lampDiffuse, lampSpecular);
                lit += albedo * lampDiffuse + lampSpecular;
            }
            return lit;
        }
        // A surface facing away from the sun takes -N.L times its translucency (lightingFunctions.hlsl CalcPunctualLight, the sun only).
        vec3 kenshiLight(vec3 albedo, vec3 n, vec3 v, float gloss, vec3 world) { float sunNl = clamp(dot(atmoSunNormal(n), uAtmoLight.xyz), -1.0, 1.0); return kenshiLight(albedo, n, v, gloss, world, vec3(sunNl >= 0.0 ? sunNl : -sunNl * atmoTranslucency)); }

        // The distant water fades out before its far clip, revealing the sky already behind it.
        // The shafts use the same weight for their water correction, so it cannot leave a horizon band.
        {{WaterHorizonFunctions}}

        // ---- haze ----
        // Integral of exp(-altitude / scale) along a ray: altitude a0 + slope * s, s in 0..len (scale heights).
        float atmoColumn(float a0, float slope, float len, float scale)
        {
            float k = slope / scale, x = k * len;
            float g = abs(x) < 1e-3 ? len * (1.0 - 0.5 * x) : (1.0 - exp(-x)) / k;
            return exp(-a0 / scale) * g;
        }
        // Kenshi's haze colour (post/atmospherefog.hlsl; KenshiHaze in Meitou.Data): SkyX's Rayleigh in-scattering from SkyX's camera
        // to the point mapped to SkyX units by the dome radius, close points below the eye lifted towards its level, the ray's length
        // at most 1, rays steeper than -0.3 replaced by a fixed one; times the Rayleigh phase and SkyX's exposure.
        vec3 hazeColour(vec3 offset)
        {
            vec3 p = offset / HAZE_DOME;
            float y = p.y + SKYX_INNER;
            y = mix(max(y, SKYX_INNER), y, clamp(dot(p.xz, p.xz), 0.0, 1.0));
            vec3 r = vec3(p.x, y - SKYX_CAMERA_Y, p.z);
            float len = length(r);
            if (len < 1e-9) return vec3(0.0);
            r /= len;
            float phase = skyxRayleighPhase(dot(uAtmoSun.xyz, r));
            if (r.y < -0.3) r = vec3(0.0, -0.3, 0.953);
            float thickness;
            return SKYX_EXPOSURE * phase * SKYX_RAYLEIGH * skyxInScatter(r, min(len, 1.0), uAtmoSun.xyz, thickness);
        }

        // What the haze fades a point into: the game's in-scattered colour plus the Meitou night air towards the point's direction (zero in Faithful and by day,
        // so exactly the game's). `offset` is the point minus the eye, any length.
        vec3 hazeTarget(vec3 offset)
        {
            return hazeColour(offset) + atmoNightAir(offset * inversesqrt(max(dot(offset, offset), 1e-12)));
        }

        // The game's haze: AtmosphereFogMaterial (post/fog.hlsl), the in-scattered colour blended in by a linear ramp between 0.06 D
        // and 0.6 D; the weather's fog (colour, density) by an ease-in-out curve over it. uAtmoAltitude.y (the viewer's haze strength,
        // 1 = the game's) scales the ramp.
        vec3 atmoKenshiHaze(vec3 colour, vec3 ray, float dist)
        {
            float level = clamp((dist - uAtmoHaze.y) / max(uAtmoHaze.z - uAtmoHaze.y, 1.0), 0.0, 1.0);
            level = min(level * uAtmoAltitude.y, 1.0);
            vec3 rgb = mix(hazeTarget(ray), uAtmoHazeCloud.rgb, uAtmoHazeCloud.a);
            float alpha = level;
            if (uAtmoFog.z > 0.0)
            {
                float amount = clamp(dist * uAtmoHaze.w, 0.0, 1.0);
                float curve = (amount < 0.5 ? 2.0 * amount * amount : 1.0 - 2.0 * (amount - 1.0) * (amount - 1.0)) * uAtmoFog.z;
                rgb = mix(rgb, uAtmoFogColour, curve);
                alpha = clamp(alpha + curve, 0.0, 1.0);
            }
            return mix(colour, rgb, alpha);
        }
        // The physical alternative (a viewer model): optical depth of SkyX's air (one density scale height for both) along the ray,
        // in-scattering of the sky's colour in that direction (the horizon's for rays below it); scaled by the haze strength.
        vec3 atmoPhysicalHaze(vec3 colour, vec3 eye, vec3 d, float dist)
        {
            float h = uAtmoParams.z;
            float a0 = max(eye.y, 0.0) / h, len = dist / h;
            if (d.y < 0.0) len = min(len, a0 / -d.y);          // the sea level ends the air below
            float column = atmoColumn(a0, d.y, len, 1.0);
            vec3 haze = 1.0 - exp(-(uAtmoTau.rgb + uAtmoTau.a) * column);
            haze = min(haze * uAtmoAltitude.y, vec3(1.0));
            float far = smoothstep(0.55, 1.0, dist / uAtmoParams.y);   // closes before the far plane and the end of the water
            haze = 1.0 - (1.0 - haze) * (1.0 - far);
            vec3 result = mix(colour, atmoSky(d), haze);
            if (uAtmoFog.z > 0.0)
                result = mix(result, uAtmoFogColour, clamp((dist - uAtmoFog.x) / max(uAtmoFog.y - uAtmoFog.x, 1.0), 0.0, 1.0) * uAtmoFog.z);
            return result;
        }

        // The colour of a lit surface point seen from the eye through the air. With the game's haze, an eye above the game's camera
        // heights (uAtmoAltitude.x > 0, a viewer choice: the game's haze measures from a fixed eye near the ground) blends towards the
        // physical haze; the weight is the same for the whole frame, so only the branch in use is evaluated.
        vec3 atmoApplyHaze(vec3 colour, vec3 eye, vec3 position)
        {
            vec3 ray = position - eye;
            float dist = length(ray);
            if (dist < 1.0) return colour;
            vec3 d = ray / dist;
            if (uAtmoParams.x < 0.5)
            {
                float f = clamp(dist / uAtmoSimple.w, 0.0, 1.0);
                return mix(colour, uAtmoSimple.rgb, f * f * 0.9);
            }
            if (uAtmoHaze.x > 0.5 && uAtmoAltitude.x < 1.0)
            {
                vec3 kenshi = atmoKenshiHaze(colour, ray, dist);
                if (uAtmoAltitude.x <= 0.0) return kenshi;
                return mix(kenshi, atmoPhysicalHaze(colour, eye, d, dist), uAtmoAltitude.x);
            }
            return atmoPhysicalHaze(colour, eye, d, dist);
        }
        // The haze (and the weather's fog) every world shader ends with. The placed fog volumes are not here: the game draws them over the finished,
        // hazed scene, so Meitou does in one full-screen pass (FogVolumeShaders, PostProcessShaders.FogVolumes), not once per fragment.
        vec3 atmoApply(vec3 colour, vec3 eye, vec3 position) { return atmoApplyHaze(colour, eye, position); }

        // The same haze as a colour transform: colour * mul + add. The grass evaluates it per vertex and needs both terms; computing them
        // once here is one atmosphere evaluation instead of the two atmoApply calls it used to make. Keep these twins in step with
        // atmoKenshiHaze and atmoPhysicalHaze above.
        void atmoKenshiHazeCoefficients(vec3 ray, float dist, out vec3 mul, out vec3 add)
        {
            float level = clamp((dist - uAtmoHaze.y) / max(uAtmoHaze.z - uAtmoHaze.y, 1.0), 0.0, 1.0);
            level = min(level * uAtmoAltitude.y, 1.0);
            vec3 rgb = mix(hazeTarget(ray), uAtmoHazeCloud.rgb, uAtmoHazeCloud.a);
            float alpha = level;
            if (uAtmoFog.z > 0.0)
            {
                float amount = clamp(dist * uAtmoHaze.w, 0.0, 1.0);
                float curve = (amount < 0.5 ? 2.0 * amount * amount : 1.0 - 2.0 * (amount - 1.0) * (amount - 1.0)) * uAtmoFog.z;
                rgb = mix(rgb, uAtmoFogColour, curve);
                alpha = clamp(alpha + curve, 0.0, 1.0);
            }
            mul = vec3(1.0 - alpha);
            add = rgb * alpha;
        }
        void atmoPhysicalHazeCoefficients(vec3 eye, vec3 d, float dist, out vec3 mul, out vec3 add)
        {
            float h = uAtmoParams.z;
            float a0 = max(eye.y, 0.0) / h, len = dist / h;
            if (d.y < 0.0) len = min(len, a0 / -d.y);
            float column = atmoColumn(a0, d.y, len, 1.0);
            vec3 haze = 1.0 - exp(-(uAtmoTau.rgb + uAtmoTau.a) * column);
            haze = min(haze * uAtmoAltitude.y, vec3(1.0));
            float far = smoothstep(0.55, 1.0, dist / uAtmoParams.y);
            haze = 1.0 - (1.0 - haze) * (1.0 - far);
            mul = vec3(1.0) - haze;
            add = atmoSky(d) * haze;
            if (uAtmoFog.z > 0.0)
            {
                float fog = clamp((dist - uAtmoFog.x) / max(uAtmoFog.y - uAtmoFog.x, 1.0), 0.0, 1.0) * uAtmoFog.z;
                mul *= 1.0 - fog;
                add = add * (1.0 - fog) + uAtmoFogColour * fog;
            }
        }
        void atmoApplyCoefficients(vec3 eye, vec3 position, out vec3 mul, out vec3 add)
        {
            vec3 ray = position - eye;
            float dist = length(ray);
            if (dist < 1.0) { mul = vec3(1.0); add = vec3(0.0); return; }
            vec3 d = ray / dist;
            if (uAtmoParams.x < 0.5)
            {
                float f = clamp(dist / uAtmoSimple.w, 0.0, 1.0);
                float alpha = f * f * 0.9;
                mul = vec3(1.0 - alpha);
                add = uAtmoSimple.rgb * alpha;
                return;
            }
            if (uAtmoHaze.x > 0.5 && uAtmoAltitude.x < 1.0)
            {
                vec3 kenshiMul, kenshiAdd;
                atmoKenshiHazeCoefficients(ray, dist, kenshiMul, kenshiAdd);
                if (uAtmoAltitude.x <= 0.0) { mul = kenshiMul; add = kenshiAdd; return; }
                vec3 physicalMul, physicalAdd;
                atmoPhysicalHazeCoefficients(eye, d, dist, physicalMul, physicalAdd);
                mul = mix(kenshiMul, physicalMul, uAtmoAltitude.x);
                add = mix(kenshiAdd, physicalAdd, uAtmoAltitude.x);
                return;
            }
            atmoPhysicalHazeCoefficients(eye, d, dist, mul, add);
        }
        """;
}