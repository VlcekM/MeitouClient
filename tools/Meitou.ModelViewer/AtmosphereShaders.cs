using System.Globalization;
using Meitou.Data.World;

namespace Meitou.ModelViewer;

/// <summary>
/// GLSL of the world view's atmosphere (docs/formats/sky.md): O'Neil's single-scattering sky with SkyX's constants, as lookup
/// tables (<see cref="TransmittanceWidth"/> × <see cref="TransmittanceHeight"/> transmittance to the top of the air, built once;
/// a <see cref="SkyViewWidth"/> × <see cref="SkyViewHeight"/> sky radiance table per sun direction and eye height, built per frame),
/// plus the aerial perspective every world shader calls: <c>atmoApply(colour, eye, position)</c>.
/// Lengths in the tables are density scale heights; the shaders convert world units with <c>uAtmoParams.z</c>.
/// </summary>
static class AtmosphereShaders
{
    public const int TransmittanceWidth = 256, TransmittanceHeight = 64, SkyViewWidth = 128, SkyViewHeight = 96;

    static string F(float v) => v.ToString("0.#########", CultureInfo.InvariantCulture) + (v == MathF.Floor(v) ? ".0" : "");

    /// <summary>The geometry constants and the transmittance table's parametrisation (shared by the table builders and the lookups).</summary>
    static readonly string Common = $$"""
        const float ATMO_PI = 3.14159265358979;
        const float ATMO_RG = {{F(SkyAtmosphere.PlanetRadius)}};    // planet radius in scale heights
        const float ATMO_RT = {{F(SkyAtmosphere.TopRadius)}};       // top of the atmosphere
        uniform vec4 uAtmoTau;        // rgb: Rayleigh optical depth straight up from sea level; a: Mie's
        uniform vec4 uAtmoParams;     // x 1 physical / 0 simple, y: distance where the haze is complete, z: scale height in world units, w: Mie scale height / air's
        uniform vec3 uAtmoOzone;      // ozone absorption at the layer's peak (per scale height); the layer is a tent 2.0 +- 1.4 scale heights up
        float atmoOzone(float alt) { return max(1.0 - abs(alt - {{F(AtmosphereSettings.OzoneCentre)}}) / {{F(AtmosphereSettings.OzoneHalfWidth)}}, 0.0); }
        vec2 atmoTransmittanceUv(float r, float mu)
        {
            float h = sqrt(ATMO_RT * ATMO_RT - ATMO_RG * ATMO_RG);
            float rho = sqrt(max(r * r - ATMO_RG * ATMO_RG, 0.0));
            float d = max(-r * mu + sqrt(max(r * r * (mu * mu - 1.0) + ATMO_RT * ATMO_RT, 0.0)), 0.0);
            float dMin = ATMO_RT - r, dMax = rho + h;
            return vec2((d - dMin) / (dMax - dMin), rho / h);
        }
        vec2 atmoLutUv(vec2 u, vec2 size) { return (u * (size - 1.0) + 0.5) / size; }
        """;

    /// <summary>Builds the transmittance table: a full-screen pass into an RGBA16F texture.</summary>
    public static readonly string TransmittanceFragment = "#version 330 core\n" + Common + $$"""

        out vec4 fragColour;
        void main()
        {
            vec2 u = (gl_FragCoord.xy - 0.5) / vec2({{TransmittanceWidth - 1}}.0, {{TransmittanceHeight - 1}}.0);
            float h = sqrt(ATMO_RT * ATMO_RT - ATMO_RG * ATMO_RG);
            float rho = h * u.y;
            float r = sqrt(rho * rho + ATMO_RG * ATMO_RG);
            float dMin = ATMO_RT - r, dMax = rho + h;
            float d = dMin + u.x * (dMax - dMin);
            float mu = d == 0.0 ? 1.0 : clamp((h * h - rho * rho - d * d) / (2.0 * r * d), -1.0, 1.0);
            bool hits = mu < 0.0 && r * r * (mu * mu - 1.0) + ATMO_RG * ATMO_RG >= 0.0;
            vec3 depth = vec3(0.0);
            const int N = 48;
            float ds = d / float(N);
            for (int i = 0; i < N; i++)
            {
                float s = (float(i) + 0.5) * ds;
                float alt = sqrt(r * r + 2.0 * r * mu * s + s * s) - ATMO_RG;
                depth += (uAtmoTau.rgb * exp(-alt) + vec3(uAtmoTau.a * exp(-alt / uAtmoParams.w)) + uAtmoOzone * atmoOzone(alt)) * ds;
            }
            fragColour = vec4(hits ? vec3(0.0) : exp(-depth), 1.0);
        }
        """;

    /// <summary>Builds the sky-view table (azimuth from the sun × elevation above the horizon) for one eye height and sun elevation.</summary>
    public static readonly string SkyViewFragment = "#version 330 core\n" + Common + $$"""

        out vec4 fragColour;
        uniform sampler2D uAtmoTransmittance;
        uniform float uEyeRadius;      // planet radius + the eye's height, scale heights
        uniform float uSunElevation;   // radians
        uniform float uHorizon;        // elevation of the horizon seen from the eye (radians, <= 0)
        uniform vec2 uGain;            // sun scale / 4 times the sky gain (Rayleigh) and times 1 (Mie)
        float rayleighPhase(float c) { return 0.75 * (1.0 + 0.5 * c * c); }
        float miePhase(float c, float g) { return 1.5 * ((1.0 - g * g) / (2.0 + g * g)) * (1.0 + c * c) / pow(1.0 + g * g - 2.0 * g * c, 1.5); }
        uniform float uMieG;
        void main()
        {
            vec2 q = (gl_FragCoord.xy - 0.5) / vec2({{SkyViewWidth - 1}}.0, {{SkyViewHeight - 1}}.0);
            float az = q.x * ATMO_PI;
            float el = uHorizon + q.y * q.y * (ATMO_PI * 0.5 - uHorizon);
            vec3 d = vec3(cos(el) * cos(az), sin(el), cos(el) * sin(az));
            vec3 sun = vec3(cos(uSunElevation), sin(uSunElevation), 0.0);
            float r0 = uEyeRadius;
            float b = r0 * d.y;
            float tMax = max(-b + sqrt(max(b * b - (r0 * r0 - ATMO_RT * ATMO_RT), 0.0)), 0.0);
            float disc = b * b - (r0 * r0 - ATMO_RG * ATMO_RG);
            if (d.y < 0.0 && disc >= 0.0) tMax = -b - sqrt(disc);
            float c = dot(d, sun);
            float pr = rayleighPhase(c), pm = miePhase(c, uMieG);
            vec3 depthToEye = vec3(0.0), sum = vec3(0.0);
            const int N = 48;
            float previous = 0.0;
            for (int i = 0; i < N; i++)
            {
                float f = (float(i) + 0.5) / float(N), f1 = (float(i) + 1.0) / float(N);
                float t = tMax * f * f, end = tMax * f1 * f1, ds = end - previous;
                previous = end;
                vec3 p = vec3(0.0, r0, 0.0) + d * t;
                float r = length(p), alt = r - ATMO_RG;
                float dr = exp(-alt), dm = exp(-alt / uAtmoParams.w);
                vec3 sigma = uAtmoTau.rgb * dr + vec3(uAtmoTau.a * dm) + uAtmoOzone * atmoOzone(alt);
                vec3 scatter = uAtmoTau.rgb * (dr * pr * uGain.x) + vec3(uAtmoTau.a * dm * pm * uGain.y);
                vec3 stepDepth = sigma * ds;
                vec3 toEye = exp(-(depthToEye + stepDepth * 0.5));
                depthToEye += stepDepth;
                vec3 sunT = texture(uAtmoTransmittance, atmoLutUv(atmoTransmittanceUv(r, dot(p, sun) / r), vec2({{TransmittanceWidth}}.0, {{TransmittanceHeight}}.0))).rgb;
                sum += scatter * toEye * sunT * ds;
            }
            fragColour = vec4(sum, 1.0);
        }
        """;

    /// <summary>
    /// What every world shader includes (fragment stage): <c>atmoSkyEnc(dir)</c> the sky colour towards a direction,
    /// <c>atmoApply(colour, eye, position)</c> the aerial perspective (transmittance by distance and height, in-scattering of
    /// the sky colour towards the point, the weather's fog), and the plain squared-distance fog when <c>--simple-sky</c> is on.
    /// Colours are display-referred (see docs/formats/sky.md): the viewer's lighting is, and the post-processing only scales and
    /// tone-maps. The program must be given its uniforms with <see cref="SkyRenderer.Apply"/>.
    /// </summary>
    public static readonly string Functions = Common + $$"""

        uniform sampler2D uAtmoSkyView;
        uniform vec4 uAtmoSun;        // xyz: direction to the sun; w: elevation of the horizon seen from the eye (radians)
        uniform vec3 uAtmoSunH;       // unit horizontal direction of the sun (x, z in xy)
        uniform vec3 uAtmoTint;       // the weather's sky colour multiplier
        uniform vec4 uAtmoFog;        // the weather's fog: x start, y end, z enabled
        uniform vec3 uAtmoFogColour;
        uniform vec4 uAtmoSimple;     // simple sky: fog colour, distance of complete fog
        uniform vec4 uAtmoHaze;       // Kenshi haze (docs/formats/sky.md "Haze"): x 1 when on, y start, z end of the atmosphere fog, w weather fog density (1 / distance)
        uniform vec3 uAtmoSunLight;   // sunlight at the eye: transmittance × sun scale (linear); a surface facing the sun gets albedo × this
        uniform vec3 uAtmoAmbient;    // light from the sky above, display-referred
        uniform vec3 uAtmoAmbientGround;   // light bounced up from the ground
        const float ATMO_GAMMA = {{F(AtmosphereModel.DisplayGamma)}};
        // As AtmosphereModel.Encode: radiance to display-referred colour.
        vec3 atmoEncode(vec3 l)
        {
            l = max(l, 0.0);
            vec3 perChannel = pow(l, vec3(1.0 / ATMO_GAMMA));
            float lum = dot(l, vec3(0.2126, 0.7152, 0.0722));
            if (lum < 1e-9) return perChannel;
            vec3 c = mix(perChannel, l * (pow(lum, 1.0 / ATMO_GAMMA) / lum), {{F(AtmosphereModel.HuePreserved)}});
            float cool = clamp(((c.b - c.r) / max(c.b, 1e-3) - 0.05) / 0.25, 0.0, 1.0);
            c *= mix(vec3(1.0), vec3({{F(AtmosphereModel.WhiteBalance.X)}}, {{F(AtmosphereModel.WhiteBalance.Y)}}, {{F(AtmosphereModel.WhiteBalance.Z)}}), cool);
            return mix(c, vec3(dot(c, vec3(0.2126, 0.7152, 0.0722))), {{F(AtmosphereModel.Desaturation)}} * cool);
        }

        vec3 atmoSkyLinear(vec3 dir)
        {
            float eh = uAtmoSun.w;
            float el = max(asin(clamp(dir.y, -1.0, 1.0)), eh + 0.002);   // the ground covers what is below: the horizon's colour stands in
            float v = sqrt(clamp((el - eh) / (ATMO_PI * 0.5 - eh), 0.0, 1.0));
            float hl = length(dir.xz);
            float az = hl > 1e-4 ? acos(clamp(dot(dir.xz / hl, uAtmoSunH.xy), -1.0, 1.0)) : 0.0;
            vec2 uv = atmoLutUv(vec2(az / ATMO_PI, v), vec2({{SkyViewWidth}}.0, {{SkyViewHeight}}.0));
            return texture(uAtmoSkyView, uv).rgb * uAtmoTint;
        }
        // Display-referred sky colour; night: how dark the sky is (1 = night), for stars.
        vec3 atmoSkyEnc(vec3 dir, out float night)
        {
            vec3 l = atmoSkyLinear(dir);
            night = clamp(1.0 - max(l.r, max(l.g, l.b)) * 25.0, 0.0, 1.0);
            vec3 c = atmoEncode(l);
            // SkyX's night glow (SkyX_Skydome.hlsl): a deep blue, strongest at the horizon.
            c += night * vec3(0.05, 0.05, 0.1) * (2.0 - 0.75 * clamp(-uAtmoSun.y, 0.0, 1.0)) * pow(1.0 - clamp(dir.y, 0.0, 1.0), 3.0);
            return c;
        }
        vec3 atmoSkyEnc(vec3 dir) { float n; return atmoSkyEnc(dir, n); }

        // Integral of exp(-altitude / scale) along a ray: altitude a0 + slope * s, s in 0..len (scale heights).
        float atmoColumn(float a0, float slope, float len, float scale)
        {
            float k = slope / scale, x = k * len;
            float g = abs(x) < 1e-3 ? len * (1.0 - 0.5 * x) : (1.0 - exp(-x)) / k;
            return exp(-a0 / scale) * g;
        }

        // Aerial perspective: the colour of a lit surface point seen from the eye through the air.
        vec3 atmoApply(vec3 colour, vec3 eye, vec3 position)
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
            if (uAtmoHaze.x > 0.5)
            {
                // Kenshi's own haze (AtmosphereFogMaterial, post/fog.hlsl): the scatter colour towards the point is blended in by a
                // linear ramp between two distances; the weather's fog (colour, density) by an ease-in-out curve over it.
                float level = clamp((dist - uAtmoHaze.y) / max(uAtmoHaze.z - uAtmoHaze.y, 1.0), 0.0, 1.0);
                vec3 rgb = atmoSkyEnc(d);
                float alpha = level;
                if (uAtmoFog.z > 0.5)
                {
                    float amount = clamp(dist * uAtmoHaze.w, 0.0, 1.0);
                    float curve = amount < 0.5 ? 2.0 * amount * amount : 1.0 - 2.0 * (amount - 1.0) * (amount - 1.0);
                    rgb = mix(rgb, uAtmoFogColour, curve);
                    alpha = clamp(alpha + curve, 0.0, 1.0);
                }
                return mix(colour, rgb, alpha);
            }
            float h = uAtmoParams.z;
            float a0 = max(eye.y, 0.0) / h, len = dist / h;
            if (d.y < 0.0) len = min(len, a0 / -d.y);          // the sea level ends the air below
            float column = atmoColumn(a0, d.y, len, 1.0);
            float columnMie = atmoColumn(a0, d.y, len, uAtmoParams.w);
            vec3 haze = 1.0 - exp(-(uAtmoTau.rgb * column + vec3(uAtmoTau.a * columnMie)));
            // The far plane and the end of the water are hidden: the haze closes in completely before them.
            float far = smoothstep(0.55, 1.0, dist / uAtmoParams.y);
            haze = 1.0 - (1.0 - haze) * (1.0 - far);
            vec3 result = mix(colour, atmoSkyEnc(d), haze);
            if (uAtmoFog.z > 0.5)
                result = mix(result, uAtmoFogColour, clamp((dist - uAtmoFog.x) / max(uAtmoFog.y - uAtmoFog.x, 1.0), 0.0, 1.0));
            return result;
        }
        """;
}
