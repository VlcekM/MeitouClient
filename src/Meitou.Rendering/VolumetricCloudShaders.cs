namespace Meitou.Rendering;

/// <summary>Independent procedural cloud ray marcher and history filter; no original-game shader code.</summary>
public static class VolumetricCloudShaders
{
    static string F(float v) => v.ToString("0.0########", System.Globalization.CultureInfo.InvariantCulture);
    public static readonly string Common = $$"""
        uniform sampler2D uNoise;
        uniform vec4 uVolume; // coverage, wrapped wind texture shift, frame jitter
        uniform vec3 uEye;
        const float CLOUD_BOTTOM = {{F(Meitou.Data.World.VolumetricClouds.Bottom)}}, CLOUD_TOP = {{F(Meitou.Data.World.VolumetricClouds.Top)}};
        const float CLOUD_EXTINCTION = {{F(Meitou.Data.World.VolumetricClouds.Extinction)}};
        vec3 cloudNoise(vec3 p)
        {
            vec3 q = fract(p) * 64.0;
            float z0 = floor(q.z), z1 = mod(z0 + 1.0, 64.0);
            vec2 xy = q.xy + 1.5;
            vec2 a = (vec2(mod(z0, 8.0), floor(z0 / 8.0)) * 66.0 + xy) / 528.0;
            vec2 b = (vec2(mod(z1, 8.0), floor(z1 / 8.0)) * 66.0 + xy) / 528.0;
            return mix(textureLod(uNoise, a, 0.0).rgb, textureLod(uNoise, b, 0.0).rgb, fract(q.z));
        }
        float cloudDensityAt(vec3 p, bool detail)
        {
            float h = (p.y - CLOUD_BOTTOM) / (CLOUD_TOP - CLOUD_BOTTOM);
            if (h <= 0.0 || h >= 1.0 || uVolume.x <= 0.0) return 0.0;
            // The original wind texture shift corresponds to six times the ground wind at this altitude.
            p.xz += uVolume.yz * {{F(Meitou.Data.World.VolumetricClouds.PatternPeriod)}};
            float weather = cloudNoise(vec3(p.x / {{F(Meitou.Data.World.VolumetricClouds.PatternPeriod)}}, 0.37, p.z / {{F(Meitou.Data.World.VolumetricClouds.PatternPeriod)}})).b;
            float cover = smoothstep(0.85 - 0.75 * uVolume.x, 1.05 - 0.75 * uVolume.x, weather);
            cover = mix(cover, 1.0, smoothstep(0.75, 1.0, uVolume.x));
            if (cover < 0.01) return 0.0;
            vec3 n = cloudNoise(p / {{F(Meitou.Data.World.VolumetricClouds.ShapePeriod)}});
            float profile = smoothstep(0.0, 0.12, h) * (1.0 - smoothstep(0.45, 1.0, h));
            float body = clamp((n.r * profile - (1.0 - cover) * 0.55 - 0.08) / 0.45, 0.0, 1.0);
            float erosion = detail ? cloudNoise(p / {{F(Meitou.Data.World.VolumetricClouds.DetailPeriod)}}).g : 0.5;
            return clamp(body - (1.0 - erosion) * 0.16, 0.0, 1.0);
        }
        bool cloudInterval(vec3 eye, vec3 ray, out vec2 interval)
        {
            interval = vec2(0.0);
            if (abs(ray.y) < 1e-6)
            {
                if (eye.y <= CLOUD_BOTTOM || eye.y >= CLOUD_TOP) return false;
                interval = vec2(0.0, {{F(Meitou.Data.World.VolumetricClouds.MaxDistance)}});
                return true;
            }
            vec2 hits = (vec2(CLOUD_BOTTOM, CLOUD_TOP) - eye.y) / ray.y;
            interval = vec2(max(min(hits.x, hits.y), 0.0), min(max(hits.x, hits.y), {{F(Meitou.Data.World.VolumetricClouds.MaxDistance)}}));
            return interval.y > interval.x;
        }
        float cloudDensity(vec3 p) { return cloudDensityAt(p, true); }
        float cloudOpticalDepth(vec3 p, vec3 light)
        {
            vec2 span;
            if (!cloudInterval(p, light, span)) return 0.0;
            float stepSize = min(span.y, 60000.0) / 4.0;
            float depth = 0.0;
            for (int k = 0; k < 4; k++)
                depth += cloudDensityAt(p + light * ((float(k) + 0.5) * stepSize), false) * stepSize;
            return depth * CLOUD_EXTINCTION;
        }
        """;

    public static readonly string March = "#version 330 core\n" + AtmosphereShaders.Functions + FogVolumeShaders.Functions + Common + """
        in vec2 vUv;
        layout(location = 0) out vec4 fragColour;
        layout(location = 1) out float fragDistance;
        uniform mat4 uInverse;
        uniform vec3 uKey, uLight, uAmbient;
        uniform int uSteps;
        uniform vec4 uFogSkip;   // x: 1 to leave out what the placed fog volumes hide (the main view); yz: the reach in NDC checked round a texel
        // The fog volume pass (PostProcessShaders.FogVolumes) later blends every sky pixel as colour * trans + add, the sky at the far clip.
        // Its curve clamps, so deep in a block trans is exactly 0 and the sky's colour, clouds included, cannot show (docs/render-clouds.md
        // "Fog early-out"). The texel is left out only when trans is 0 along its own ray and along the four rays at the corners of the
        // footprint the composite's bilinear lookup and the projection's jitter can reach, so no full-size pixel that still sees the sky reads it.
        bool fogHidesSky(vec2 ndc)
        {
            if (uFogSkip.x < 0.5 || uFogVolumeInfo.x < 0.5) return false;
            for (int k = 0; k < 5; k++)
            {
                vec2 o = k == 0 ? vec2(0.0) : vec2((k & 1) == 1 ? 1.0 : -1.0, k < 3 ? -1.0 : 1.0) * uFogSkip.yz;
                vec4 a = uInverse * vec4(ndc + o, 0.0, 1.0), b = uInverse * vec4(ndc + o, 1.0, 1.0);
                vec3 d = normalize(b.xyz / b.w - a.xyz / a.w);
                if (fogVolumesTransmittance(uFogVolumeEye.xyz, d, 1e9) > 0.0) return false;
            }
            return true;
        }
        float hg(float cosine, float g)
        {
            return (1.0 - g*g) / pow(max(1.0 + g*g - 2.0*g*cosine, 0.001), 1.5);
        }
        void main()
        {
            vec2 ndc = vUv * 2.0 - 1.0;
            vec4 a = uInverse * vec4(ndc, 0.0, 1.0), b = uInverse * vec4(ndc, 1.0, 1.0);
            vec3 ray = normalize(b.xyz / b.w - a.xyz / a.w);
            vec2 span;
            fragColour = vec4(0.0); fragDistance = 0.0;
            if (!cloudInterval(uEye, ray, span)) return;
            if (fogHidesSky(ndc)) return;
            float ds = min((span.y - span.x) / float(uSteps), 500.0);
            float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))) + uVolume.w);
            float t = span.x + ds * jitter, trans = 1.0, depthMoment = 0.0;
            vec3 sum = vec3(0.0);
            float phase = 0.8 * hg(dot(ray, uLight), 0.6) + 0.2 * hg(dot(ray, uLight), -0.2);
            for (int k = 0; k < 96; k++)
            {
                if (k >= uSteps || t >= span.y || trans < 0.01) break;
                vec3 p = uEye + ray * t;
                float density = cloudDensity(p);
                if (density > 0.001)
                {
                    float optical = cloudOpticalDepth(p, uLight);
                    // A broad second lobe approximates multiple scattering without bleaching shaded cores.
                    float direct = exp(-optical) * phase + 0.45 * exp(-optical * 0.2);
                    vec3 source = uKey * direct + uAmbient * mix(0.45, 1.25, clamp((p.y - CLOUD_BOTTOM) / 7000.0, 0.0, 1.0));
                    source *= min(1.0, 8.0 / max(max(source.r, source.g), max(source.b, 1e-5)));
                    float alpha = 1.0 - exp(-density * ds * CLOUD_EXTINCTION);
                    float weight = trans * alpha;
                    sum += weight * source; depthMoment += weight * t;
                    trans *= 1.0 - alpha;
                }
                t += ds;
            }
            float alpha = 1.0 - trans;
            // Aerial perspective on the cloud itself, before compositing over sky and celestial bodies.
            float distance = depthMoment / max(alpha, 1e-5);
            float haze = 1.0 - exp(-distance / 65000.0);
            vec3 sky = atmoSky(ray);
            vec3 landHaze = mix(hazeTarget(ray * max(distance, 1.0)), uAtmoHazeCloud.rgb, uAtmoHazeCloud.a);
            vec3 air = mix(landHaze, sky, uAtmoAltitude.x);
            // Weather fog remains in the sky composite, applied once after clouds and celestial bodies.
            sum = mix(sum, air * alpha / 1.183216, haze);
            fragColour = vec4(sum * 1.183216, alpha);
            fragDistance = distance;
        }
        """;

    public static readonly string Shadow = "#version 330 core\n" + Common + """
        in vec2 vUv;
        out vec4 fragColour;
        uniform vec3 uLight;
        uniform vec2 uShadowOrigin;
        void main()
        {
            vec3 p = vec3(uShadowOrigin.x + vUv.x * 320000.0, CLOUD_BOTTOM, uShadowOrigin.y + vUv.y * 320000.0);
            float tau = cloudOpticalDepth(p + vec3(0.0, 0.01, 0.0), uLight);
            fragColour = vec4(exp(-tau));
        }
        """;

    public const string Resolve = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uCurrent, uHistory, uDistance;
        uniform mat4 uInverse, uPrevious;
        uniform vec3 uEye, uPreviousEye;
        uniform vec2 uWindDelta;
        uniform float uHistoryWeight;
        void main()
        {
            vec4 now = texture(uCurrent, vUv);
            fragColour = now;
            if (uHistoryWeight <= 0.0 || now.a < 0.001) return;
            vec2 ndc = vUv * 2.0 - 1.0;
            vec4 a = uInverse * vec4(ndc, 0.0, 1.0), b = uInverse * vec4(ndc, 1.0, 1.0);
            vec3 ray = normalize(b.xyz / b.w - a.xyz / a.w);
            vec3 point = uEye + ray * texture(uDistance, vUv).r;
            point.xz += uWindDelta;
            vec4 clip = uPrevious * vec4(point - uPreviousEye, 1.0);
            if (clip.w <= 0.0) return;
            vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
            if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return;
            vec4 history = texture(uHistory, uv);
            vec4 lo = now, hi = now;
            vec2 texel = 1.0 / vec2(textureSize(uCurrent, 0));
            for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
            {
                vec4 n = texture(uCurrent, vUv + vec2(x,y) * texel);
                lo = min(lo, n); hi = max(hi, n);
            }
            float weight = uHistoryWeight * (1.0 - smoothstep(0.05, 0.25, abs(history.a - now.a)));
            history = clamp(history, lo, hi);
            fragColour = mix(now, history, weight);
        }
        """;
}