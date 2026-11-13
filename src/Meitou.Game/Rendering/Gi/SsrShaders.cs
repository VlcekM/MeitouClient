using Meitou.Game.Rendering.Sky;
using System.Globalization;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The indirect specular's reflections (docs/render-gi.md "Reflections"): <see cref="March"/> walks the reflected ray over the scene depth at the
/// GI resolve's size, <see cref="GiShaders.Reflect"/> traces what it missed where the device can, <see cref="Shade"/> turns the two into what
/// replaces the sky's reflection, and <see cref="Apply"/> upsamples that, Fresnel weighted, over the scene. Surfaces come from the GI surface target
/// (bits 10-15: the gloss, <c>kenshiLight</c>).
/// </summary>
static class SsrShaders
{
    /// <summary>
    /// World units a marched ray goes.
    /// </summary>
    public const float MarchRange = 3000;

    /// <summary>
    /// Steps along the marched ray.
    /// </summary>
    public const int MarchSteps = 40;

    /// <summary>
    /// The halvings that refine a marched hit.
    /// </summary>
    public const int MarchRefine = 5;

    /// <summary>
    /// World units a marched ray may be behind the depth and still hit it, at least.
    /// </summary>
    public const float MarchThickness = 6;

    /// <summary>
    /// What the march and the traced rays share: the same pixel, gloss, normal and direction for one block of the render size. A const:
    /// <see cref="GiShaders.Reflect"/> includes it while <see cref="AtmosphereShaders.Functions"/>, which <see cref="Shade"/> includes, may be
    /// initialising. The reflections start at a gloss of 0.25 and replace the sky from 0.45, what a wet surface reaches (<c>makeWet</c> pulls it
    /// towards 0.5).
    /// </summary>
    public const string Common = """

        // The first pixel of a block whose surface stored a gloss (GiResolveShaders.Resolve picks in the same order), x -1: none.
        ivec2 ssrPick(ivec2 base, int div, ivec2 size, out uvec4 surface)
        {
            for (int k = 0; k < 4; k++)
            {
                ivec2 q = min(base + ivec2(k & 1, k >> 1) * (div - 1), size - 1);
                surface = texelFetch(uAlbedo, q, 0);
                if (((surface.w >> 10) & 63u) != 0u) return q;
            }
            surface = uvec4(0u);
            return ivec2(-1);
        }
        // A hit's alpha: 0 none, else its sign says a ray proved the rest open, and its magnitude - 1 holds the share (8 levels) and the distance (64 levels).
        float ssrPackHit(float share, float t, bool open)
        {
            float e = round(clamp(share, 0.0, 1.0) * 7.0) + 8.0 * round(sqrt(clamp(t / 4000.0, 0.0, 1.0)) * 63.0);
            return (open ? -1.0 : 1.0) * (1.0 + e);
        }
        float ssrHitShare(float a) { return a == 0.0 ? 0.0 : mod(abs(a) - 1.0, 8.0) / 7.0; }
        float ssrHitDistance(float a) { if (a == 0.0) return 0.0; float q = floor((abs(a) - 1.0) / 8.0) / 63.0; return q * q * 4000.0; }
        float ssrGloss(uvec4 surface) { return float(((surface.w >> 10) & 63u) - 1u) / 62.0; }
        // How much of the sky's reflection the traced light replaces (the rest keeps the sky, occluded).
        float ssrWeight(float gloss) { return smoothstep(0.25, 0.45, gloss); }
        vec3 ssrNormal(uvec4 surface)
        {
            vec2 p = vec2(surface.xy) / 65535.0 * 2.0 - 1.0;
            vec3 d = vec3(p.x, 1.0 - abs(p.x) - abs(p.y), p.y);
            if (d.y < 0.0) d.xz = (1.0 - abs(d.zx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.z >= 0.0 ? 1.0 : -1.0);
            return normalize(d);
        }
        // The reflected ray: the lobe's dominant direction, as kenshiLight reads the sky's reflection (towards the normal as the surface roughens). The same every frame:
        // a lobe sampled anew each frame leaves the upscaler noise it cannot average away.
        vec3 ssrDirection(vec3 n, vec3 v, float gloss)
        {
            float rough = 1.0 - gloss * 0.99;
            vec3 r = normalize(mix(n, reflect(-v, n), gloss * (sqrt(gloss) + rough)));
            return dot(r, n) > 0.0 ? r : reflect(-v, n);
        }

        """;

    /// <summary>
    /// One texel per block: the scene colour where the reflected ray meets the scene depth (rgb) and how far to trust it (a: the fade towards the
    /// screen's edges and the march's end; 0 no hit). The ray is walked in screen space with its inverse depth linear, then its crossing refined by
    /// halving.
    /// </summary>
    public static readonly string March = "#version 330 core\n" + GiResolveShaders.Reconstruct + "uniform usampler2D uAlbedo;\n" + Common
        + $$"""
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene;
        uniform float uDiv;

        // Where the ray's screen position leaves -1..1, as a share of the segment s0..s1.
        float ssrScreenEnd(vec2 s0, vec2 s1)
        {
            vec2 d = s1 - s0;
            vec2 reach = mix((vec2(-1.0) - s0) / min(d, vec2(-1e-6)), (vec2(1.0) - s0) / max(d, vec2(1e-6)), step(vec2(0.0), d));
            return clamp(min(reach.x, reach.y), 0.0, 1.0);
        }
        ivec2 ssrPixel(vec2 s) { return ivec2((s * 0.5 + 0.5) * uFullSize); }
        // A reflected ray cannot reach a surface from behind it (the depth then shows some other surface in front).
        bool ssrBehind(ivec2 q, vec3 r)
        {
            uvec4 hit = texelFetch(uAlbedo, q, 0);
            return ((hit.w >> 10) & 63u) != 0u && dot(ssrNormal(hit), r) > 0.2;
        }
        float ssrFade(vec2 uv, float travelled)
        {
            vec2 edge = min(uv, 1.0 - uv);
            // The first units are left to the rays: a normal-mapped ray that dips under its own surface hits that surface there.
            float along = smoothstep(4.0, 12.0, travelled) * (1.0 - smoothstep(0.75, 1.0, travelled / {{F(MarchRange)}}));
            return smoothstep(0.0, 0.06, min(edge.x, edge.y)) * along;
        }
        // From world point p along r; returns the trust in the hit (0: none) and its uv.
        float ssrMarch(vec3 p, vec3 r, out vec2 uv, out float hitDistance)
        {
            uv = vec2(0.0);
            hitDistance = 0.0;
            mat3 toView = inverse(mat3(uRight * uTan.x, uUp * uTan.y, -uBack));
            vec3 a = toView * (p - uGiEye), d = toView * r;
            float nearest = uNearPlanes.x * 1.5, len = {{F(MarchRange)}};
            if (d.z < 0.0) len = min(len, (a.z - nearest) / -d.z);
            if (len <= 1.0) return 0.0;
            vec3 b = a + d * len;
            vec2 s0 = a.xy / a.z, s1 = b.xy / b.z;
            float k0 = 1.0 / a.z, k1 = 1.0 / b.z, end = ssrScreenEnd(s0, s1), before = 0.0;
            for (int i = 0; i < {{MarchSteps}}; i++)
            {
                float x = (float(i) + 0.5) / {{F(MarchSteps)}}, t = end * x * x;
                float rayZ = 1.0 / mix(k0, k1, t), sceneZ = depthAt(ssrPixel(mix(s0, s1, t)));
                float gap = rayZ - sceneZ, stepZ = abs(rayZ - 1.0 / mix(k0, k1, before));
                if (sceneZ <= 0.0 || gap <= 0.0 || gap > max({{F(MarchThickness)}}, stepZ * 1.5)) { before = t; continue; }
                float lo = before, hi = t;
                for (int j = 0; j < {{MarchRefine}}; j++)
                {
                    float mid = (lo + hi) * 0.5, z = depthAt(ssrPixel(mix(s0, s1, mid)));
                    if (z > 0.0 && 1.0 / mix(k0, k1, mid) > z) hi = mid; else lo = mid;
                }
                ivec2 q = ssrPixel(mix(s0, s1, hi));
                if (ssrBehind(q, r)) return 0.0;
                uv = (vec2(q) + 0.5) / uFullSize;
                hitDistance = length(positionAt(q, depthAt(q)) - p);
                return ssrFade(uv, hitDistance);
            }
            return 0.0;
        }

        void main()
        {
            fragColour = vec4(0.0);
            uvec4 surface;
            ivec2 px = ssrPick(ivec2(floor(gl_FragCoord.xy)) * int(uDiv), int(uDiv), ivec2(uFullSize), surface);
            if (px.x < 0) return;
            float gloss = ssrGloss(surface);
            float z = ssrWeight(gloss) > 0.0 ? depthAt(px) : 0.0;
            if (z <= 0.0) return;
            vec3 n = ssrNormal(surface), p = positionAt(px, z), v = normalize(uGiEye - p);
            vec3 r = ssrDirection(n, v, gloss);
            vec2 uv;
            float hitDistance;
            float trust = ssrMarch(p + n * (1.0 + z * 0.001), r, uv, hitDistance);
            if (trust > 0.0) fragColour = vec4(texture(uScene, uv).rgb, ssrPackHit(trust, hitDistance, false));
        }
        """;

    /// <summary>
    /// One texel per block of what the sky's reflection on this surface becomes, minus what the lit fragment already put there (the sky's, as
    /// <c>kenshiLight</c> reads it), hazed as the fragment was; per unit of the environment BRDF, which <see cref="Apply"/> multiplies in at full size.
    /// Out: rgb that difference, a the signed view depth as <see cref="GiResolveShaders.Resolve"/>'s. Every surface goes through here: below the gloss
    /// threshold the difference is the specular occlusion alone, the same ratio the lit fragment takes without the resolve, recovered from the
    /// resolve's texel of this block. <c>uHit</c>'s alpha is <c>ssrPackHit</c>'s. A rough surface mirrors a cone: the hits round this texel on the
    /// same surface are averaged over the disc the cone covers at their distance.
    /// </summary>
    public static readonly string Shade = "#version 330 core\n" + AtmosphereShaders.Functions + GiResolveShaders.Reconstruct
        + "uniform usampler2D uAlbedo;\n" + Common + $$"""
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uHit, uGiLow;
        uniform float uDiv, uHasGi;

        // The sky's reflection as kenshiLight reads it, before the BRDF.
        vec3 ssrSky(vec3 n, vec3 v, float gloss, vec3 ambient)
        {
            if (uAtmoMaps.y < 0.5) return vec3(0.0);
            float rough = 1.0 - gloss * 0.99;
            vec3 dominant = mix(n, reflect(-v, n), gloss * (sqrt(gloss) + rough));
            vec4 r = textureLod(uAtmoSpecular, dominant, (1.0 - gloss) * 7.0);
            return r.rgb * r.a * 10.0 * ambient * uAtmoLight.w;
        }
        // The probes' light against the sky's at this texel: GiResolveShaders.Resolve stored (probes - sky) x share x haze, so 1 + its luminance over the sky's is
        // kenshiLight's mix(1, probes / sky, share), the clamp included.
        float ssrOcclusion(ivec2 cell, vec3 n, vec3 transmittance, vec3 ambient)
        {
            if (uHasGi < 0.5) return 1.0;
            vec3 change = texelFetch(uGiLow, cell, 0).rgb / max(transmittance, vec3(1e-3));
            vec3 sky = atmoIrradiance(n) * {{F(1 - KenshiLighting.DielectricSpecular)}} * ambient * uAtmoLight.w;
            const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
            return clamp(1.0 + dot(change, lum) / max(dot(sky, lum), 1e-4), 0.0, 1.0);
        }

        vec3 ssrResolved(vec4 hit, vec3 sky, vec3 occluded)
        {
            float share = ssrHitShare(hit.a);
            return hit.rgb * share + (hit.a < 0.0 ? sky : occluded) * (1.0 - share);
        }
        // The texels of this block's surface within r cells, on a fixed spiral, so the picture is the same every frame.
        vec3 ssrCone(ivec2 cell, vec3 p, vec3 n, float z, float gloss, vec3 sky, vec3 occluded)
        {
            float rough = 1.0 - gloss * 0.99;
            float cellsPerUnit = uFullSize.y / (2.0 * z * uTan.y * uDiv), cone = rough * rough * 0.5;
            ivec2 limit = ivec2(uFullSize / uDiv) - 1;
            const float golden = 2.39996323;
            float seek = min(30.0 * cone * cellsPerUnit, 16.0), total = 0.0, found = 0.0;
            for (int i = 0; i < 16; i++)
            {
                float a = float(i) * golden, d = sqrt((float(i) + 0.5) / 16.0) * seek;
                float packed = texelFetch(uHit, clamp(cell + ivec2(round(vec2(cos(a), sin(a)) * d)), ivec2(0), limit), 0).a;
                if (ssrHitShare(packed) > 0.0) { total += ssrHitDistance(packed); found += 1.0; }
            }
            vec3 sum = ssrResolved(texelFetch(uHit, cell, 0), sky, occluded);
            float radius = found > 0.0 ? min(total / found * cone * cellsPerUnit, 16.0) : 0.0, weight = 1.0;
            if (radius < 0.5) return sum;
            for (int i = 0; i < 32; i++)
            {
                float a = float(i) * golden, f = sqrt((float(i) + 0.5) / 32.0);
                ivec2 c = clamp(cell + ivec2(round(vec2(cos(a), sin(a)) * f * radius)), ivec2(0), limit);
                uvec4 other;
                ivec2 q = ssrPick(c * int(uDiv), int(uDiv), ivec2(uFullSize), other);
                float qz = q.x < 0 ? 0.0 : depthAt(q);
                if (qz <= 0.0 || abs(dot(n, positionAt(q, qz) - p)) > max(1.5, z * 0.01)) continue;
                float w = 1.0 - 0.75 * f * f;
                sum += ssrResolved(texelFetch(uHit, c, 0), sky, occluded) * w;
                weight += w;
            }
            return sum / weight;
        }

        void main()
        {
            ivec2 cell = ivec2(floor(gl_FragCoord.xy));
            uvec4 surface;
            ivec2 px = ssrPick(cell * int(uDiv), int(uDiv), ivec2(uFullSize), surface);
            float z = px.x < 0 ? 0.0 : depthAt(px);
            if (z <= 0.0) { fragColour = vec4(0.0); return; }
            vec3 n = ssrNormal(surface), p = positionAt(px, z), v = normalize(uGiEye - p);
            float gloss = ssrGloss(surface);
            vec3 ambient = atmoAmbientMapAt(p).rgb;
            vec3 transmittance = atmoApply(vec3(1.0), uGiEye, p) - atmoApply(vec3(0.0), uGiEye, p);
            vec3 sky = ssrSky(n, v, gloss, ambient);
            vec3 occluded = sky * ssrOcclusion(cell, n, transmittance, ambient);
            vec3 seen = ssrWeight(gloss) > 0.0 ? mix(occluded, ssrCone(cell, p, n, z, gloss, sky, occluded), ssrWeight(gloss)) : occluded;
            fragColour = vec4((seen - sky) * transmittance, (surface.w & 512u) != 0u ? -z : z);
        }
        """;

    /// <summary>
    /// The low target over the scene (blend one, one; red, green and blue): upsampled by depth, times this pixel's environment BRDF (a specular
    /// colour of 0.04: resolved surfaces are dielectrics).
    /// </summary>
    public static readonly string Apply = "#version 330 core\n" + GiResolveShaders.Reconstruct + AtmosphereShaders.EnvBrdfFunction
        + "\nuniform usampler2D uAlbedo;\n" + Common + $$"""

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uLow;
        uniform vec2 uLowSize;
        uniform float uDiv;

        void main()
        {
            ivec2 px = ivec2(floor(gl_FragCoord.xy));
            uvec4 surface = texelFetch(uAlbedo, px, 0);
            if (((surface.w >> 10) & 63u) == 0u) discard;
            float z = depthAt(px);
            if (z <= 0.0) discard;
        {{GiResolveShaders.Upsample}}
            float nv = clamp(dot(ssrNormal(surface), normalize(uGiEye - positionAt(px, z))), 0.0, 1.0);
            fragColour = vec4(kenshiEnvBrdf(ssrGloss(surface), nv, vec3({{F(KenshiLighting.DielectricSpecular)}})) * add, 0.0);
        }
        """;

    /// <summary>
    /// A float as a GLSL literal: invariant culture, at least one decimal, at most nine.
    /// </summary>
    static string F(float v) => v.ToString("0.0########", CultureInfo.InvariantCulture);
}
