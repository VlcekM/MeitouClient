using System.Globalization;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// GLSL of the world view's sky, light and haze in the game's own terms (docs/formats/sky.md, docs/formats/lighting.md): SkyX's
/// scattering integral evaluated per pixel with the game's options (<c>atmoSky</c>), the deferred lighting pass's sun, image-based
/// ambient and specular light (<c>kenshiLight</c>), and the haze every world shader ends with (<c>atmoApply</c>). Colours are the game's
/// HDR values; the post-processing applies the game's exposure. A program reads its uniforms and textures from the frame globals <see cref="SkyRenderer"/> publishes.
/// </summary>
static class AtmosphereShaders
{
    static string F(float v) => v.ToString("0.#########", CultureInfo.InvariantCulture) + (v == MathF.Floor(v) ? ".0" : "");
    static string V3(System.Numerics.Vector3 v) => $"vec3({F(v.X)}, {F(v.Y)}, {F(v.Z)})";

    /// <summary>What every world shader includes (fragment stage, or the grass's vertex stage).</summary>
    public static readonly string Functions = $$"""

        const float ATMO_PI = 3.14159265358979;
        uniform vec4 uAtmoSun;        // xyz: direction to the sun (its real height)
        uniform vec4 uAtmoLight;      // xyz: the lighting direction (the sun's with y clamped to 0), w: the environment light's factor
        uniform vec3 uAtmoSunLight;   // sunColour.rgb · sunColour.w (docs/formats/lighting.md); the shader adds the ambient map's alpha · 2
        uniform vec4 uAtmoParams;     // x 1 game sky / 0 simple, y: distance where the physical haze is complete, z: scale height in world units (physical haze)
        uniform vec4 uAtmoTau;        // physical haze: rgb Rayleigh optical depth straight up, a Mie's
        uniform vec3 uAtmoTint;       // the weather's sky colour multiplier (a viewer stand-in, see sky.md)
        uniform vec4 uAtmoFog;        // the weather's fog: x start, y end, z enabled
        uniform vec3 uAtmoFogColour;
        uniform vec4 uAtmoSimple;     // simple sky: fog colour, distance of complete fog
        uniform vec4 uAtmoHaze;       // Kenshi haze: x 1 when on, y start, z end of the atmosphere fog, w weather fog density (1 / distance)
        uniform vec4 uAtmoHazeCloud;  // Kenshi haze: horizonClouds, rgb the cloud colour (a stand-in), a how far the haze is pulled to it
        uniform vec4 uAtmoAltitude;   // viewer: x the physical haze's share (0 the game's haze, 1 the physical), y haze strength (1 the game's), z how far the eye is above the game's camera heights (0 within them, 1 high above)
        uniform vec4 uAtmoMaps;       // x irradiance cube, y specular cube, z ambient map present; w half the world's width
        uniform samplerCube uAtmoIrradiance, uAtmoSpecular;
        uniform sampler2D uAtmoAmbientMap;
        uniform vec4 uWeatherWet;     // the weather's shared surface values (WeatherSurfaces): x wetness, y rainAmount = saturate(rain / 50), z gameTime (hours)
        uniform vec4 uWeatherDust;    // xyz: dustAmount (current, inside, slope; docs/formats/weather.md "Dust")
        uniform sampler2D uWeatherDustNoise;   // the dust noise (Turbulent.dds), sampled at world.xz * 0.002
        uniform sampler2D uWeatherGround;      // the terrain's whole-world ground colour map: the dust colour (the BIOMES `ground colour` where the object is)
        uniform vec4 uGiParams;       // Meitou probe GI (docs/render-gi.md): x 1 while the probes light the world, y their strength, w their hysteresis
        uniform vec4 uGiGrid[4];      // per cascade: the grid's place and spacing (Gi.GiShaders.ProbeSampling)
        uniform sampler2D uGiIrradiance, uGiDistance, uGiBase;
        {{Gi.GiShaders.ProbeSampling}}
        uniform vec4 uLightGrid;      // the lamps (docs/render-lights.md): the grid's corner x, z, its cell size, cells per side (0: no lamps)
        uniform sampler2D uLightCells, uLightIndex, uLightData;
        {{LampShaders.Defines}}{{LampShaders.Lighting}}

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
            colour += night * pow(vec3(0.05, 0.05, 0.1) * (2.0 - 0.75 * clamp(-uAtmoSun.y, 0.0, 1.0)) * pow(1.0 - d.y, 3.0), vec3(2.2));
            return colour * uAtmoTint;
        }
        vec3 atmoSky(vec3 dir) { float n; return atmoSky(dir, n); }

        {{ShadowShaders.Functions}}
        // ---- the deferred lighting pass (deferred.hlsl main_fs, lightingFunctions.hlsl; docs/formats/lighting.md) ----
        vec4 atmoAmbientMapAt(vec3 world)
        {
            if (uAtmoMaps.z < 0.5) return vec4(1.0, 1.0, 1.0, 0.5);
            return texture(uAtmoAmbientMap, (world.xz + uAtmoMaps.w) / (2.0 * uAtmoMaps.w));
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
        // A dielectric surface lit as Kenshi's main lighting pass lights it: HDR colour. v: towards the eye.
        vec3 kenshiLight(vec3 albedo, vec3 n, vec3 v, float gloss, vec3 world)
        {
            vec4 am = atmoAmbientMapAt(world);
            vec3 l = uAtmoLight.xyz;
            vec3 sun = uAtmoSunLight * am.a * 2.0 * kenshiShadow(world, n);   // the sun shadow term (deferred.hlsl: lightColor * shadow)
            gloss = clamp(gloss, 0.0, 1.0);
            float roughness = 1.0 - gloss * 0.99, a = roughness * roughness, a2 = a * a;
            float nl = clamp(dot(n, l), 0.0, 1.0);
            vec3 diffuse = ATMO_PI * nl * sun * {{F(1 - KenshiLighting.DielectricSpecular)}};
            vec3 h = normalize(v + l);
            float nh = clamp(dot(n, h), 0.0, 1.0), lh = clamp(dot(l, h), 0.0, 1.0);
            float denom = max(nh * nh * (a2 - 1.0) + 1.0, 1e-6);
            float D = a2 / (ATMO_PI * denom * denom);
            float fresnel = exp2((-5.55473 * lh - 6.98316) * lh);
            float k = a * 0.5, vis = 1.0 / (lh * lh * (1.0 - k * k) + k * k);
            vec3 sunSpecular = sun * (nl * D * (0.04 * vis + 0.96 * fresnel * vis)) / ATMO_PI;
            float env = uAtmoLight.w;
            vec3 envDiffuse = atmoIrradiance(n) * {{F(1 - KenshiLighting.DielectricSpecular)}} * am.rgb * env;
            // Meitou probe GI: the probes' irradiance (sky seen past the geometry, light bounced off it) in place of the sky's, and the sky's
            // reflection dimmed as much as the probes dim the sky (an occluded corner reflects no sky).
            float specularOcclusion = 1.0;
            if (uGiParams.x > 0.5)
            {
                vec4 gi = giIrradiance(world, n, v);
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
                envSpecular = kenshiEnvBrdf(gloss, nv) * r.rgb * r.a * 10.0 * am.rgb * env * specularOcclusion;
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

        // The game's haze: AtmosphereFogMaterial (post/fog.hlsl), the in-scattered colour blended in by a linear ramp between 0.06 D
        // and 0.6 D; the weather's fog (colour, density) by an ease-in-out curve over it. uAtmoAltitude.y (the viewer's haze strength,
        // 1 = the game's) scales the ramp.
        vec3 atmoKenshiHaze(vec3 colour, vec3 ray, float dist)
        {
            float level = clamp((dist - uAtmoHaze.y) / max(uAtmoHaze.z - uAtmoHaze.y, 1.0), 0.0, 1.0);
            level = min(level * uAtmoAltitude.y, 1.0);
            vec3 rgb = mix(hazeColour(ray), uAtmoHazeCloud.rgb, uAtmoHazeCloud.a);
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
        """;
}
