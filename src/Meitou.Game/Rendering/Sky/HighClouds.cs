using System.Numerics;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The <c>highclouds</c> switch (docs/render-clouds.md "High clouds"): altocumulus 10 km up and cirrus 26 km up, drawn over the sky, stars
/// and planets and under the game's clouds; its sliders and its GLSL.
/// </summary>
public static class HighClouds
{
    /// <summary>
    /// Whether the switch is on.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// The altocumulus amount's default.
    /// </summary>
    public const float DefaultAmount = 50;

    /// <summary>
    /// The altocumulus cover: 0 clear, 100 overcast.
    /// </summary>
    public static float Amount { get; set; } = DefaultAmount;

    /// <summary>
    /// The altocumulus density's default.
    /// </summary>
    public const float DefaultDensity = 100;

    /// <summary>
    /// The altocumulus opacity: 0 invisible, 100 full.
    /// </summary>
    public static float Density { get; set; } = DefaultDensity;

    /// <summary>
    /// The cirrus amount's default.
    /// </summary>
    public const float DefaultCirrusAmount = 50;

    /// <summary>
    /// The cirrus cover: 0 none, 100 the most.
    /// </summary>
    public static float CirrusAmount { get; set; } = DefaultCirrusAmount;

    /// <summary>
    /// The cirrus density's default.
    /// </summary>
    public const float DefaultCirrusDensity = 50;

    /// <summary>
    /// The cirrus opacity: 0 invisible, 100 full.
    /// </summary>
    public static float CirrusDensity { get; set; } = DefaultCirrusDensity;

    /// <summary>
    /// <c>uHighClouds</c>: the densities and amounts as 0..1, zero while off.
    /// </summary>
    public static Vector4 Uniform => Enabled ? new Vector4(Density, Amount, CirrusDensity, CirrusAmount) / 100 : Vector4.Zero;

    /// <summary>
    /// The clock's speed relative to the frame clock.
    /// </summary>
    public const double Pace = 0.1;

    /// <summary>
    /// The clock's restart period in seconds (<c>HC_PERIOD</c>).
    /// </summary>
    public const double Period = 3600;

    /// <summary>
    /// The GLSL: <c>highClouds(dir, col, skyBehind, key, keyDir, ambient)</c> returns <c>col</c> with the clouds over it.
    /// </summary>
    public const string Functions = """
        // x, y: altocumulus density and amount; z, w: cirrus density and amount (0..1; density 0 is not drawn).
        uniform vec4 uHighClouds;

        // The clock in seconds, modulo HC_PERIOD.
        uniform float uHighCloudSeconds;

        // Two wisp shapes in R and G.
        uniform sampler2D uHighCirrus;

        // The Shadertoy's pattern constants.
        const float HC_SCALE = 1.1, HC_SPEED = 0.03, HC_DARK = 0.5, HC_LIGHT = 0.3, HC_ALPHA = 8.0, HC_TINT = 0.5;
        const mat2 HC_OCTAVE = mat2(1.6, 1.2, -1.2, 1.6);

        // 10 km up on a 6371 km Earth; 1 pattern unit = 8 km, stretched along the wind; 150 km haze; 1 km fake thickness.
        const float HC_HEIGHT = 10.0, HC_EARTH_R = 6371.0, HC_UNIT = 8.0, HC_ALONG = 0.6, HC_WIND_ANGLE = 0.245;
        const float HC_SOFT = 2.2, HC_COVER0 = -0.15, HC_HAZE_KM = 150.0, HC_THICK_KM = 1.0;

        // Lighting: opacity gain, edge light, silver lining, sunset glow, night core and night gain.
        const float HC_DENSITY = 1.5, HC_FORM = 0.1, HC_SILVER = 0.12, HC_GLOW_GAIN = 0.35, HC_NIGHT_CORE = 0.65, HC_NIGHT_GAIN = 1.2;
        const vec3 HC_LUMA = vec3(0.2126, 0.7152, 0.0722);

        // Cirrus: 26 km up in 21 x 7 km cells, 60 % filled at amount 0.5, drifting at half the altocumulus speed.
        const float HC_CIRRUS_HEIGHT = 26.0, HC_CIRRUS_CELL = 7.0, HC_CIRRUS_STRETCH = 3.0, HC_CIRRUS_COVER = 0.6, HC_CIRRUS_WIND_ANGLE = 0.32;
        const float HC_CIRRUS_OPACITY = 0.19, HC_CIRRUS_WIND = 0.5, HC_CIRRUS_HAZE_KM = 250.0, HC_CIRRUS_GLOW_HEIGHT = 22.8;

        // The clock restarts every HC_PERIOD seconds to keep float precision, crossfading over the last HC_FADE.
        const float HC_PERIOD = 3600.0, HC_FADE = 120.0;

        // Simplex noise, as in the Shadertoy.
        vec2 hcHash(vec2 p)
        {
            p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
            return -1.0 + 2.0 * fract(sin(p) * 43758.5453123);
        }

        float hcNoise(vec2 p)
        {
            const float K1 = 0.366025404, K2 = 0.211324865;
            vec2 i = floor(p + (p.x + p.y) * K1);
            vec2 a = p - i + (i.x + i.y) * K2;
            vec2 o = a.x > a.y ? vec2(1.0, 0.0) : vec2(0.0, 1.0);
            vec2 b = a - o + K2;
            vec2 c = a - 1.0 + 2.0 * K2;
            vec3 h = max(0.5 - vec3(dot(a, a), dot(b, b), dot(c, c)), 0.0);
            vec3 n = h * h * h * h * vec3(dot(a, hcHash(i)), dot(b, hcHash(i + o)), dot(c, hcHash(i + 1.0)));
            return dot(n, vec3(70.0));
        }

        // An octave sum; octaves finer than the pixel footprint fp fade out.
        float hcOctaves(vec2 uv, float time, float w, float k, float fp, int count, bool ridged)
        {
            float sum = 0.0;
            for (int i = 0; i < count; i++)
            {
                float n = w * hcNoise(uv);
                sum += (ridged ? abs(n) : n) * smoothstep(0.9, 0.3, fp);
                uv = HC_OCTAVE * uv + time;
                w *= k;
                fp *= 2.0;
            }
            return sum;
        }

        // Henyey-Greenstein without the 1/(4 pi).
        float hcPhase(float g, float cosTheta)
        {
            return (1.0 - g * g) / pow(max(1.0 + g * g - 2.0 * g * cosTheta, 1e-4), 1.5);
        }

        // v in a frame turned by angle.
        vec2 hcRotate(vec2 v, float angle)
        {
            float cs = cos(angle), sn = sin(angle);
            return vec2(cs * v.x + sn * v.y, -sn * v.x + cs * v.y);
        }

        // v in the wind's frame, stretched along it.
        vec2 hcWind(vec2 v)
        {
            return hcRotate(v, HC_WIND_ANGLE) * vec2(HC_ALONG, 1.0);
        }

        // The distance (km) along a ray to a shell h km up on a curved Earth.
        float hcShellDist(float y, float h)
        {
            float b = HC_EARTH_R * max(y, 0.0);
            return h * (2.0 * HC_EARTH_R + h) / (sqrt(b * b + h * (2.0 * HC_EARTH_R + h)) + b);
        }

        // Sunlight at the cloud's height: reddened after sunset, so high clouds stay lit after the ground is dark.
        vec3 hcHeightSun(vec3 dir, float dist, vec3 key, float lit)
        {
            vec3 p = vec3(0.0, HC_EARTH_R, 0.0) + dir * dist;
            vec3 s = normalize(uAtmoSun.xyz + vec3(0.0, 1e-6, 0.0));
            float b = dot(p, s);
            float lowest = (b > 0.0 ? length(p) : sqrt(max(dot(p, p) - b * b, 0.0))) - HC_EARTH_R;
            vec3 through = exp(-vec3(5.8e-3, 13.5e-3, 33.1e-3) * 565.0 * exp(-max(lowest, 0.0) / 8.0)) * smoothstep(-1.0, 1.5, lowest);
            vec3 glow = dot(uAtmoSunLight, HC_LUMA) * through * HC_GLOW_GAIN * smoothstep(0.1, 0.0, s.y);
            return max(key * lit, glow);
        }

        // The cover offset: the amount's curve plus slow 10-25 km weather patches.
        float hcCover(vec2 pw, float seconds, float amount)
        {
            float w = 0.6 * hcNoise(pw / 3.0 + vec2(7.3, 2.1) + vec2(0.004, 0.001) * seconds)
                + 0.4 * hcNoise(pw / 1.3 + vec2(1.7, 9.4) - vec2(0.002, 0.005) * seconds);
            float weather = smoothstep(0.0, 1.0, 0.5 + 2.6 * w);
            float vary = 1.0 - smoothstep(0.6, 1.0, amount);
            float curve = (amount - 0.5) * 1.2 + 1.6 * smoothstep(0.5, 1.0, amount) - 0.8 * smoothstep(0.5, 0.0, amount);
            return HC_COVER0 + curve + (weather - 0.5) * 1.6 * vary;
        }

        // The opacity: a soft ramp, thin edges eroded by the detail; clear at amount 0, solid at 1.
        float hcOpacity(float f, float c, float c1, float amount)
        {
            float a = smoothstep(0.0, HC_SOFT, f + c);
            a *= mix(1.0, smoothstep(0.05, 0.45, c1 + 0.6 * a), 1.0 - a);
            a *= smoothstep(0.0, 0.25, amount);
            return max(a, smoothstep(0.75, 1.0, amount) * (0.93 + 0.07 * smoothstep(0.1, 0.6, c1)));
        }

        // The outline of the two largest octaves at p.
        float hcOutline(vec2 p, float q, float time, float cover)
        {
            vec2 uv = p * HC_SCALE - (q - time);
            float n0 = hcNoise(uv);
            float n1 = hcNoise(HC_OCTAVE * uv + time);
            float r = abs(0.8 * n0) + abs(0.56 * n1);
            float f = 0.7 * n0 + 0.42 * n1;
            f *= r + f;
            return smoothstep(0.0, HC_SOFT, cover + HC_ALPHA * f * r);
        }

        // The fake thickness towards the horizon: lights top edges and shades bases.
        float hcThickness(vec3 dir, vec2 p, float dist, float q, float time, float cover, float c1, inout float a)
        {
            vec2 shift = hcWind(dir.xz) / HC_UNIT;
            float topA = hcOutline(p + shift * (hcShellDist(dir.y, HC_HEIGHT + HC_THICK_KM) - dist), q, time, cover);
            float baseA = hcOutline(p + shift * (hcShellDist(dir.y, HC_HEIGHT - HC_THICK_KM) - dist), q, time, cover);
            float coreA = hcOutline(p, q, time, cover);
            float lowView = smoothstep(0.5, 0.15, dir.y);
            a += (1.0 - a) * 0.45 * lowView * max(topA - coreA, 0.0) * smoothstep(0.1, 0.5, c1 + 0.2);
            return 1.0 + lowView * (0.15 * max(topA - baseA, 0.0) - 0.25 * max(baseA - topA, 0.0));
        }

        // The key light, or the sun's when the cloud pass has none; returns its direction.
        vec3 hcKey(inout vec3 key, vec3 keyDir)
        {
            if (dot(key, key) < 1e-8)
            {
                key = uAtmoSunLight;
                keyDir = uAtmoSun.xyz;
            }
            return normalize(keyDir + vec3(0.0, 1e-6, 0.0));
        }

        // The ambient, half of it the open sky behind.
        vec3 hcAmbient(vec3 ambient, vec3 skyBehind)
        {
            vec3 behind = skyBehind / sqrt(SKYX_EXPOSURE);
            return dot(ambient, ambient) > 1e-10 ? mix(ambient, behind, 0.5) : behind;
        }

        // The night sky behind a cloud, a little greyer.
        vec3 hcGreySky(vec3 skyBehind)
        {
            return mix(vec3(dot(skyBehind, HC_LUMA)), skyBehind, 0.6);
        }

        // 0 by day, 1 at night.
        float hcNightFade()
        {
            return smoothstep(-0.02, -0.14, uAtmoSun.y);
        }

        // The day colour: phase-lit key, rims, silver lining and ambient, fading into the sky with distance.
        vec3 hcDay(vec3 dir, float dist, vec3 skyBehind, vec3 key, vec3 l, float lit, vec3 ambient, float th, float c, float thShade, float haze)
        {
            float exposure = sqrt(SKYX_EXPOSURE);
            vec3 amb = hcAmbient(ambient, skyBehind);
            float cosSun = dot(dir, l);
            float rim = 0.245 * HC_FORM * (1.0 - th) * (hcPhase(0.8, cosSun) - 1.0);
            float edgeBand = smoothstep(0.02, 0.2, th) * (1.0 - smoothstep(0.25, 0.6, th));
            float silver = HC_SILVER * edgeBand * hcPhase(0.9, cosSun);
            vec3 direct = hcHeightSun(dir, dist, key, lit) * (0.9 * (1.0 - 0.3 * HC_FORM * th * th) + max(rim, 0.0) + silver);
            vec3 light = (direct + amb * (1.0 - 0.15 * HC_FORM * th)) * exposure;

            float occ = clamp(HC_DARK + HC_LIGHT * c, 0.0, 1.0);
            vec3 hueSource = dot(ambient, ambient) > 1e-10 ? ambient : skyBehind;
            vec3 skyHue = max(mix(vec3(1.0), hueSource / max(dot(hueSource, HC_LUMA), 1e-5), 1.3), 0.0);
            vec3 shade = mix(vec3(1.0), skyHue, clamp(1.0 - occ, 0.0, 1.0));
            vec3 cloud = HC_TINT * skyBehind + light * vec3(1.1, 1.1, 0.9) * occ * thShade * shade;
            return mix(skyBehind, cloud, mix(0.45, 1.0, haze));
        }

        // The night colour: about the open sky's brightness, darker cores, plus the planet's light.
        vec3 hcNight(vec3 skyBehind, vec3 key, float lit, float th, float haze)
        {
            vec3 greySky = hcGreySky(skyBehind);
            vec3 nightKey = key * lit * 0.45 * sqrt(SKYX_EXPOSURE) * (1.0 - 0.4 * th);
            vec3 cloud = (greySky * mix(1.0, HC_NIGHT_CORE, smoothstep(0.1, 0.9, th)) + nightKey) * HC_NIGHT_GAIN;
            return mix(skyBehind * 0.8, cloud, mix(0.6, 1.0, haze));
        }

        // The altocumulus at the clock's time over col.
        vec3 hcLayer(vec3 dir, vec3 col, vec3 skyBehind, vec3 key, vec3 keyDir, vec3 ambient, float seconds)
        {
            if (uHighClouds.x <= 0.0) return col;

            skyBehind = min(skyBehind, col);

            float dist = hcShellDist(dir.y, HC_HEIGHT);
            vec2 pw = dir.xz * (dist / HC_UNIT);
            vec2 p = hcWind(pw);
            float time = seconds * HC_SPEED;
            float fp = max(length(fwidth(p)), 1e-5) * HC_SCALE;
            float q = hcOctaves(p * HC_SCALE * 0.5, 0.0, 0.1, 0.4, 0.0, 7, false);
            vec2 uv = p * HC_SCALE - (q - time);
            float r = hcOctaves(uv, time, 0.8, 0.7, fp, 8, true);
            float f = hcOctaves(uv, time, 0.7, 0.6, fp, 8, false);
            f *= r + f;
            float c1 = hcOctaves(p * HC_SCALE * 3.0 - (q - time * 3.0), time * 3.0, 0.4, 0.6, fp * 3.0, 7, true);
            float c = hcOctaves(p * HC_SCALE * 2.0 - (q - time * 2.0), time * 2.0, 0.4, 0.6, fp * 2.0, 7, false) + c1;

            float amount = clamp(uHighClouds.y, 0.0, 1.0);
            float cover = hcCover(pw, seconds, amount);
            float a = hcOpacity(cover + HC_ALPHA * f * r, c, c1, amount);
            float th = clamp(a, 0.0, 1.0);
            float thShade = hcThickness(dir, p, dist, q, time, cover, c1, a);

            // Far clouds fade into the haze by day; at night thin parts fill in to hide the stars.
            float haze = exp(-dist / HC_HAZE_KM);
            float hazeFade = mix(0.25, 1.0, haze);
            float night = hcNightFade();
            a = clamp(a * HC_DENSITY, 0.0, 1.0) * smoothstep(0.0, 0.04, dir.y) * hazeFade;
            a = mix(a, smoothstep(0.0, 0.45, a / hazeFade), night) * uHighClouds.x;

            vec3 l = hcKey(key, keyDir);
            float lit = smoothstep(-0.06, -0.005, l.y);
            vec3 cloud = mix(hcDay(dir, dist, skyBehind, key, l, lit, ambient, th, c, thShade, haze), hcNight(skyBehind, key, lit, th, haze), night);

            // Stars and planets are dimmed by half under the cloud.
            return mix(skyBehind + (col - skyBehind) * (1.0 - 0.5 * a), cloud, a);
        }

        // Four hash values in 0..1.
        vec4 hcHash4(vec2 c)
        {
            vec4 p = vec4(dot(c, vec2(127.1, 311.7)), dot(c, vec2(269.5, 183.3)), dot(c, vec2(419.2, 371.9)), dot(c, vec2(53.7, 97.1)));
            return fract(sin(p) * 43758.5453123);
        }

        // One cell's wisp at g (cells), placed, sized and timed by the cell's hash; 0 for an empty cell.
        float hcWisp(vec2 cell, vec2 g, vec2 gx, vec2 gy, float seconds, float cover)
        {
            vec4 h = hcHash4(cell);
            if (h.x > cover) return 0.0;

            float life = 0.5 + 0.5 * sin(seconds * 0.006 * (0.6 + h.w) + h.z * 6.2832);
            float fade = smoothstep(0.15, 0.75, life);
            if (fade <= 0.0) return 0.0;

            float size = 0.6 + 0.4 * h.w;
            float tilt = (h.y - 0.5) * 0.5;
            mat2 m = mat2(cos(tilt), sin(tilt), -sin(tilt), cos(tilt)) / size;
            vec2 l = m * (g - (cell + 0.5 + (h.yz - 0.5) * 0.8));
            if (max(abs(l.x), abs(l.y)) >= 1.0) return 0.0;

            vec4 t = textureGrad(uHighCirrus, l * 0.5 + 0.5, m * gx * 0.5, m * gy * 0.5);
            float v = h.z < 0.5 ? t.r : t.g;
            return v * fade * (0.6 + 0.6 * h.x / cover) * smoothstep(1.0, 0.55, length(l));
        }

        // The cirrus opacity at pw (km): the wisps of the 3 x 3 cells around it.
        float hcCirrusDensity(vec2 pw, float seconds, vec2 dpx, vec2 dpy)
        {
            // Drift with the altocumulus, scaled by height and HC_CIRRUS_WIND.
            vec2 drift = hcRotate(-(HC_SPEED / HC_SCALE) * vec2(1.0 / HC_ALONG, 1.0), -HC_WIND_ANGLE);
            pw -= seconds * drift * HC_UNIT * (HC_CIRRUS_HEIGHT / HC_HEIGHT) * HC_CIRRUS_WIND;

            vec2 cellSize = HC_CIRRUS_CELL * vec2(HC_CIRRUS_STRETCH, 1.0);
            vec2 g = hcRotate(pw, HC_CIRRUS_WIND_ANGLE) / cellSize;
            vec2 gx = hcRotate(dpx, HC_CIRRUS_WIND_ANGLE) / cellSize;
            vec2 gy = hcRotate(dpy, HC_CIRRUS_WIND_ANGLE) / cellSize;
            vec2 c0 = floor(g);
            // The share of cells with a wisp: 0, HC_CIRRUS_COVER at 0.5, all at 1.
            float amount = clamp(uHighClouds.w, 0.0, 1.0);
            float cover = amount < 0.5 ? 2.0 * amount * HC_CIRRUS_COVER : mix(HC_CIRRUS_COVER, 1.0, 2.0 * amount - 1.0);
            float a = 0.0;
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                    a = 1.0 - (1.0 - a) * (1.0 - hcWisp(c0 + vec2(i, j), g, gx, gy, seconds, cover));
            return a;
        }

        // The cirrus colour and opacity over skyBehind.
        vec4 hcCirrus(vec3 dir, vec3 skyBehind, vec3 key, vec3 keyDir, vec3 ambient, float seconds)
        {
            if (uHighClouds.z <= 0.0) return vec4(0.0);

            float dist = hcShellDist(dir.y, HC_CIRRUS_HEIGHT);
            vec2 pw = dir.xz * dist;
            float a = hcCirrusDensity(pw, seconds, dFdx(pw), dFdy(pw));
            a = clamp(a * HC_CIRRUS_OPACITY, 0.0, 1.0) * smoothstep(0.0, 0.05, dir.y) * mix(0.2, 1.0, exp(-dist / HC_CIRRUS_HAZE_KM)) * uHighClouds.z;
            if (a <= 0.0) return vec4(0.0);

            vec3 l = hcKey(key, keyDir);
            float lit = smoothstep(-0.07, -0.01, l.y);
            float exposure = sqrt(SKYX_EXPOSURE);
            vec3 sun = hcHeightSun(dir, hcShellDist(dir.y, HC_CIRRUS_GLOW_HEIGHT), key, lit);
            vec3 day = 0.4 * skyBehind + (sun * 0.85 + hcAmbient(ambient, skyBehind)) * exposure;
            vec3 greySky = hcGreySky(skyBehind);
            vec3 night = (greySky * 0.95 + key * lit * 0.3 * exposure) * HC_NIGHT_GAIN;
            return vec4(mix(day, night, hcNightFade()), a);
        }

        // The cirrus, then the altocumulus over it (lit from the sky without the cirrus).
        vec3 hcSky(vec3 dir, vec3 col, vec3 skyBehind, vec3 key, vec3 keyDir, vec3 ambient, float seconds)
        {
            vec4 cirrus = hcCirrus(dir, min(skyBehind, col), key, keyDir, ambient, seconds);
            return hcLayer(dir, mix(col, cirrus.rgb, cirrus.a), skyBehind, key, keyDir, ambient, seconds);
        }

        vec3 highClouds(vec3 dir, vec3 col, vec3 skyBehind, vec3 key, vec3 keyDir, vec3 ambient)
        {
            if ((uHighClouds.x <= 0.0 && uHighClouds.z <= 0.0) || dir.y <= 0.0) return col;

            float s = uHighCloudSeconds;
            vec3 sky = hcSky(dir, col, skyBehind, key, keyDir, ambient, s);
            if (s <= HC_PERIOD - HC_FADE) return sky;

            vec3 restarted = hcSky(dir, col, skyBehind, key, keyDir, ambient, s - HC_PERIOD);
            return mix(sky, restarted, smoothstep(HC_PERIOD - HC_FADE, HC_PERIOD, s));
        }

        """;
}
