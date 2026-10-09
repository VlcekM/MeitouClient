using System.Globalization;
using System.Text.RegularExpressions;
using Meitou.Data.World;

namespace Meitou.Rendering.Gi;

/// <summary>
/// The GI resolve (docs/render-gi.md "Resolve"): the probes read once per pixel of a lower-resolution target after the scene instead of in every
/// lit fragment. The lit fragments write their albedo to a second colour target (<see cref="WithAlbedo"/>); <see cref="Resolve"/> reconstructs
/// each low texel's position and normal from the depth and stores what swapping the sky's ambient for the probes' adds per unit of albedo, hazed;
/// <see cref="Apply"/> upsamples that by depth and adds it, times the albedo, to the scene.
/// </summary>
static partial class GiResolveShaders
{
    static string F(float v) => v.ToString("0.0########", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"void\s+main\s*\(\s*\)")]
    private static partial Regex MainRegex();

    [GeneratedRegex(@"(?<!\)\s*)\bout\s+vec4\s+fragColour\s*;")]
    private static partial Regex ColourOutRegex();

    /// <summary>
    /// <paramref name="fragment"/> (a final, native-ported fragment shader) with the albedo output at location 1: its <c>main</c> renamed and a new
    /// one that calls it and then writes <c>giAlbedoOut</c> (set by <c>kenshiLight</c>), so early returns and discards keep working.
    /// <paramref name="inShader"/>: the program keeps reading the probes itself (foliage, grass, characters, impostors) and writes 0;
    /// <paramref name="none"/>: a fragment without <c>kenshiLight</c> (the water) that only clears the target where it draws.
    /// </summary>
    public static string WithAlbedo(string fragment, bool inShader = false, bool none = false)
    {
        if (!MainRegex().IsMatch(fragment)) throw new InvalidOperationException("WithAlbedo: the fragment shader has no main()");
        string body = MainRegex().Replace(fragment, "void giLitMain()", 1);
        // With two outputs every one needs its location: the colour's is 0.
        body = ColourOutRegex().Replace(body, "layout(location = 0) out vec4 fragColour;", 1);
        if (inShader)
        {
            int line = body.IndexOf('\n');
            if (line < 0 || !body.StartsWith("#version", StringComparison.Ordinal)) throw new InvalidOperationException("WithAlbedo: no #version line");
            body = body[..(line + 1)] + "#define GI_IN_SHADER\n" + body[(line + 1)..];
        }
        return body + $$"""

            layout(location = 1) out uvec4 oGiAlbedo;
            void main() { giLitMain(); oGiAlbedo = {{(none ? "uvec4(0u)" : "giAlbedoOut")}}; }

            """;
    }

    /// <summary>The scene's position from the two depth slices, shared by both passes.</summary>
    const string Reconstruct = """
        uniform sampler2D uNearDepth, uFarDepth;
        uniform vec2 uNearPlanes, uFarPlanes;   // near, far of each depth slice
        uniform vec2 uTan;                      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec3 uRight, uUp, uBack;        // the camera's axes in the world
        uniform vec3 uGiEye;
        uniform int uHasFar;
        uniform vec2 uFullSize;                 // the render size

        float viewZ(float d, vec2 nf) { float zd = 2.0 * d - 1.0; return nf.x * nf.y / (nf.y - zd * (nf.y - nf.x)); }
        // The depth along the view axis at a pixel of the render size (near slice, else far; 0: the sky).
        float depthAt(ivec2 px)
        {
            px = clamp(px, ivec2(0), ivec2(uFullSize) - 1);
            float d = texelFetch(uNearDepth, px, 0).r;
            if (d < 1.0) return viewZ(d, uNearPlanes);
            if (uHasFar == 0) return 0.0;
            d = texelFetch(uFarDepth, px, 0).r;
            return d < 1.0 ? viewZ(d, uFarPlanes) : 0.0;
        }
        vec3 positionAt(ivec2 px, float z)
        {
            vec2 ndc = (vec2(px) + 0.5) / uFullSize * 2.0 - 1.0;
            return uGiEye + (uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack) * z;
        }

        """;

    /// <summary>
    /// One texel of the low target per <c>uDiv</c> × <c>uDiv</c> block: the first pixel of the block that takes the probes (its surface flag set),
    /// its position from the depth, its shading normal as the lit fragment wrote it, the probes' irradiance there and the sky's ambient as the
    /// fragment had it. Out: rgb = (probes − sky) × the GI share × the strength × the haze's transmittance, a = the pixel's view depth (−1:
    /// nothing in the block takes the probes).
    /// </summary>
    public static readonly string Resolve = "#version 330 core\n" + AtmosphereShaders.Functions + Reconstruct + $$"""
        in vec2 vUv;
        out vec4 fragColour;
        uniform usampler2D uAlbedo;
        uniform float uDiv;

        void main()
        {
            ivec2 base = ivec2(floor(gl_FragCoord.xy)) * int(uDiv);
            ivec2 px = ivec2(-1);
            uvec4 surface = uvec4(0u);
            for (int k = 0; k < 4 && px.x < 0; k++)
            {
                ivec2 q = min(base + ivec2(k & 1, k >> 1) * (int(uDiv) - 1), ivec2(uFullSize) - 1);
                uvec4 s = texelFetch(uAlbedo, q, 0);
                if ((s.w & 256u) != 0u) { px = q; surface = s; }
            }
            float z = px.x < 0 ? 0.0 : depthAt(px);
            if (z <= 0.0) { fragColour = vec4(0.0, 0.0, 0.0, -1.0); return; }
            vec3 p = positionAt(px, z);
            vec3 n = giOctDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0);
            vec3 v = normalize(uGiEye - p);
            vec4 gi = giIrradiance(p, n, v);
            float share = gi.a * uGiParams.y;
            vec3 sky = atmoIrradiance(n) * {{F(1 - KenshiLighting.DielectricSpecular)}} * atmoAmbientMapAt(p).rgb * uAtmoLight.w;
            // The haze is linear in the colour (colour · T + in-scattering): T from two evaluations, so the added light is hazed as the fragment's was.
            vec3 transmittance = atmoApply(vec3(1.0), uGiEye, p) - atmoApply(vec3(0.0), uGiEye, p);
            fragColour = vec4((gi.rgb - sky) * share * transmittance, z);
        }
        """;

    /// <summary>
    /// The resolve over the scene (blend one, one; red, green and blue): per pixel that takes the probes, its four nearest low texels with their
    /// bilinear weights divided by how far each one's depth is from this pixel's (relative), times the albedo.
    /// </summary>
    public static readonly string Apply = "#version 330 core\n" + Reconstruct + """
        in vec2 vUv;
        out vec4 fragColour;
        uniform usampler2D uAlbedo;
        uniform sampler2D uLow;
        uniform vec2 uLowSize;
        uniform float uDiv;

        void main()
        {
            ivec2 px = ivec2(floor(gl_FragCoord.xy));
            uvec4 surface = texelFetch(uAlbedo, px, 0);
            if ((surface.w & 256u) == 0u) discard;
            float z = depthAt(px);
            if (z <= 0.0) discard;
            vec2 f = (vec2(px) + 0.5) / uDiv - 0.5;
            ivec2 i0 = ivec2(floor(f));
            vec2 w = f - vec2(i0);
            vec3 sum = vec3(0.0);
            float total = 0.0;
            vec3 nearest = vec3(0.0);
            float best = 1e9;
            for (int k = 0; k < 4; k++)
            {
                ivec2 o = ivec2(k & 1, k >> 1);
                vec4 t = texelFetch(uLow, clamp(i0 + o, ivec2(0), ivec2(uLowSize) - 1), 0);
                if (t.a <= 0.0) continue;
                float rel = abs(t.a - z) / z;
                float weight = (o.x == 1 ? w.x : 1.0 - w.x) * (o.y == 1 ? w.y : 1.0 - w.y) / (rel + 0.01);
                sum += t.rgb * weight;
                total += weight;
                if (rel < best) { best = rel; nearest = t.rgb; }
            }
            vec3 add = total > 1e-6 ? sum / total : nearest;
            vec3 albedo = vec3(float(surface.z & 255u), float(surface.z >> 8), float(surface.w & 255u)) / 255.0;
            fragColour = vec4(albedo * albedo * add, 0.0);
        }
        """;
}
