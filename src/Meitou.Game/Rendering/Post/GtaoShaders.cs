using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Frame;
namespace Meitou.Game.Rendering.Post;

/// <summary>
/// Ground-truth ambient occlusion (the <c>gtao</c> switch; docs/render-post.md "GTAO"), the alternative to <see cref="PostProcessShaders.Ssao"/>: a
/// horizon search per view-aligned slice with the closed-form visibility integral (Jimenez et al. 2016). Port of KenshiNative's <c>fx_gtao_ps</c>,
/// adapted to a depth-only forward scene. <see cref="Gather"/> writes what the SSAO writes (R the visibility, G the view depth), so
/// <see cref="PostProcessShaders.SsaoBlur"/> filters it unchanged; <see cref="Apply"/> then upsamples it by depth and multiplies the scene with the
/// coloured multi-bounce term.
/// </summary>
static class GtaoShaders
{
    /// <summary>
    /// The surface target as the gather and the apply read it (<c>giAlbedoOut</c>): bit 8 of w says a lit fragment wrote it.
    /// </summary>
    const string SurfaceSampler = """

        uniform usampler2D uSurface;   // the GI resolve's surface target (AtmosphereShaders' giAlbedoOut)

        """;

    /// <summary>
    /// The gather's world: every vector is an offset from the eye along the camera's world axes, as the SSAO's air visibility rebuilds it.
    /// </summary>
    const string GatherDeclarations = """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uPacked;          // the view depth at the render size (Pack): 0 the sky, negative on a character's pixel; its coarser levels by DepthLevel
        uniform vec2 uLevels;               // x: the coarsest level a tap may read (0: the pixels alone), y: what a tap's reach in pixels gives up before the level rises
        uniform float uCharacterOccluder;   // how much a character counts as an occluder (1: as anything else)
        uniform vec2 uTan;      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec2 uNearFar;  // of the scene depth
        uniform vec2 uSize;     // the render size
        uniform vec4 uReach;    // x the radius in world units; y, z the most and the least pixels it spans; w the share of it at full weight
        uniform vec4 uMarch;    // x slices, y steps along each side, z how much a passed horizon is kept (1: occluders are solid behind), w the noise's offset
        uniform float uFadeStart, uFadeEnd;
        uniform vec3 uRight, uUp, uBack;    // the camera's axes in the world
        uniform vec3 uGtaoEye;
        uniform float uWaterY;              // the water plane's height (float min: no water)

        """;

    /// <summary>
    /// The same rules as <see cref="PostProcessShaders.Ssao"/>'s, kept apart so the two programs change independently.
    /// </summary>
    const string Visibility = """

        // The share of a surface's occlusion the water in front of it leaves (docs/render-water.md "Edges").
        float waterVisibility(vec3 offset)
        {
            float below = uWaterY - (uGtaoEye.y + offset.y);
            if (uWaterY < -1e30 || below <= 0.0 || uGtaoEye.y <= uWaterY) return 1.0;
            return 1.0 - clamp(length(offset) * below / max(-offset.y, 1e-6) / 2.0, 0.0, 1.0);
        }

        // The share of a surface's own light that reaches the eye through the haze, the weather's fog and the fog volumes.
        float airVisibility(vec3 offset)
        {
            vec3 position = uGtaoEye + offset;
            vec3 t = atmoApply(vec3(1.0), uGtaoEye, position) - atmoApply(vec3(0.0), uGtaoEye, position);
            float visible = clamp(dot(t, vec3(0.299, 0.587, 0.114)), 0.0, 1.0);
            float dist = length(offset);
            return dist < 1.0 ? visible : visible * fogVolumesTransmittance(uGtaoEye, offset / dist, dist);
        }

        """;

    /// <summary>
    /// The gather's geometry: the noise, the depth and offset lookups, the depth normal, one tap's horizon update and a slice's visibility integral.
    /// </summary>
    const string Geometry = """

        const float GTAO_PI = 3.14159265, GTAO_HALF_PI = 1.57079633;

        float ign(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }

        float viewZAt(ivec2 t)
        {
            t = clamp(t, ivec2(0), ivec2(uSize) - 1);
            return abs(texelFetch(uPacked, t, 0).r);
        }

        vec3 offsetAt(ivec2 t, float z)
        {
            vec2 ndc = (vec2(t) + 0.5) / uSize * 2.0 - 1.0;
            return (uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack) * z;
        }

        // The face's normal from the depth: each axis' neighbour on the nearer side, so a silhouette does not tilt it.
        vec3 depthNormal(ivec2 c, vec3 p, float z, vec3 view)
        {
            ivec2 dx = ivec2(1, 0), dy = ivec2(0, 1);
            float zl = viewZAt(c - dx), zr = viewZAt(c + dx), zb = viewZAt(c - dy), zu = viewZAt(c + dy);
            vec3 x = abs(zr - z) < abs(z - zl) ? offsetAt(c + dx, zr) - p : p - offsetAt(c - dx, zl);
            vec3 y = abs(zu - z) < abs(z - zb) ? offsetAt(c + dy, zu) - p : p - offsetAt(c - dy, zb);
            vec3 n = normalize(cross(x, y));
            return dot(n, view) < 0.0 ? -n : n;
        }

        // One tap's update of a side's horizon cosine: past the radius it fades to the surface's own horizon (low), a character counts by its share,
        // and a horizon the march has passed decays (uMarch.z), so a thin occluder is not a wall running away from the eye for ever.
        float horizon(ivec2 t, vec3 p, vec3 view, float radius, float low, float best, int level)
        {
            if (any(lessThan(t, ivec2(0))) || any(greaterThanEqual(t, ivec2(uSize)))) return best;
            float packed = texelFetch(uPacked, min(t >> level, textureSize(uPacked, level) - 1), level).r;
            if (packed == 0.0) return best;
            vec3 v = offsetAt(t, abs(packed)) - p;
            float len = length(v);
            if (len < 1e-4) return best;
            float weight = clamp((radius - len) / max(radius * (1.0 - uReach.w), 1e-4), 0.0, 1.0);
            if (uCharacterOccluder < 1.0)
                weight *= packed < 0.0 ? uCharacterOccluder : 1.0;
            float h = mix(low, dot(v, view) / len, weight);
            return h > best ? h : max(low, mix(h, best, uMarch.z));
        }

        // One side's arc of the slice's visibility integral.
        float arc(float h, float n, float cosN, float sinN) { return 0.25 * (cosN + 2.0 * h * sinN - cos(2.0 * h - n)); }

        // The visibility seen by a slice (x) and what the same slice sees with nothing on the surface (y). Their ratio over the slices is the
        // occlusion: projLen * (arcs) alone only averages to 1 on open ground over many slices, and with three it would mottle a flat floor.
        vec2 sliceVisibility(ivec2 c, vec3 p, vec3 view, vec3 n, vec2 screen, float radius, float radiusPixels, float jitter)
        {
            vec3 dir = normalize(uRight * screen.x * uTan.x / uSize.x + uUp * screen.y * uTan.y / uSize.y);
            vec3 ortho = normalize(dir - view * dot(dir, view));
            vec3 axis = normalize(cross(ortho, view));
            vec3 proj = n - axis * dot(n, axis);
            float projLen = length(proj);
            if (projLen < 1e-4) return vec2(0.0);
            float angle = (dot(ortho, proj) >= 0.0 ? 1.0 : -1.0) * acos(clamp(dot(proj / projLen, view), -1.0, 1.0));
            float cosN = cos(angle), sinN = sin(angle);
            float lowUp = cos(angle + GTAO_HALF_PI), lowDown = cos(angle - GTAO_HALF_PI);
            float up = lowUp, down = lowDown;
            int steps = int(uMarch.y);
            for (int j = 0; j < steps; j++)
            {
                // Squared: the taps gather where the contact is; each at least one pixel further out, so none rereads the texel it started from.
                float f = (float(j) + jitter) / float(steps);
                float reach = max(f * f * radiusPixels, float(j + 1));
                ivec2 o = ivec2(round(screen * reach));
                // A far tap reads a coarser level of the depth (XeGTAO): its texels lie far apart in the picture, in the pyramid they lie together.
                int level = int(clamp(log2(reach) - uLevels.y, 0.0, uLevels.x) + 0.5);
                up = horizon(c + o, p, view, radius, lowUp, up, level);
                down = horizon(c - o, p, view, radius, lowDown, down, level);
            }
            float h1 = acos(clamp(up, -1.0, 1.0)), h0 = -acos(clamp(down, -1.0, 1.0));
            return vec2(projLen * (arc(h0, angle, cosN, sinN) + arc(h1, angle, cosN, sinN)), projLen * (cosN + angle * sinN));
        }

        """;

    /// <summary>
    /// The gather's depth: each pixel's view depth, 0 on the sky, negative where the scene's alpha marks a character. One lookup a tap gives the
    /// gather the depth and the mask (the mask was a second, filtered lookup of a half-size copy: 36 a pixel at 3 x 6), and the depth needs no
    /// conversion there.
    /// </summary>
    public static readonly string Pack = "#version 330 core\n" + """
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uDepth;
        uniform sampler2D uScene;   // its alpha: 1 on characters
        uniform vec2 uNearFar;
        """ + DepthConvention.Glsl + """
        void main()
        {
            ivec2 c = ivec2(gl_FragCoord.xy);
            float d = texelFetch(uDepth, c, 0).r;
            if (depthIsSky(d)) { fragColour = vec4(0.0); return; }
            float z = depthViewZ(d, uNearFar);
            fragColour = vec4(texelFetch(uScene, c, 0).a > 0.5 ? -z : z, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// A coarser level of <see cref="Pack"/>'s depth, from the level before it (XeGTAO's depth filter): the four texels' depths averaged by how near
    /// each is to the farthest of them, full weight within the part of the gather's radius that counts in full and none a radius nearer, so a level
    /// holds the surface behind and a thin near thing does not smear over it. The sky when one of the four is the sky; a character's when two or more
    /// are.
    /// </summary>
    public const string DepthLevel = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uPacked;
        uniform vec4 uReach;    // as the gather's: x the radius in world units; y, z the most and the least pixels it spans; w the share of it at full weight
        uniform vec2 uFrom;     // x: the level read, y: a pixel's width per unit of depth
        void main()
        {
            int level = int(uFrom.x);
            ivec2 c = ivec2(gl_FragCoord.xy) * 2, last = textureSize(uPacked, level) - 1;
            vec4 d = vec4(texelFetch(uPacked, min(c, last), level).r, texelFetch(uPacked, min(c + ivec2(1, 0), last), level).r,
                texelFetch(uPacked, min(c + ivec2(0, 1), last), level).r, texelFetch(uPacked, min(c + ivec2(1, 1), last), level).r);
            if (any(equal(d, vec4(0.0)))) { fragColour = vec4(0.0); return; }
            vec4 z = abs(d);
            float far = max(max(z.x, z.y), max(z.z, z.w));
            float pixel = uFrom.y * far;
            float radius = clamp(uReach.x / pixel, uReach.z, max(uReach.y, uReach.z)) * pixel;
            float fade = max(radius * (1.0 - uReach.w), 1e-4);
            vec4 w = clamp((radius - (far - z)) / fade, 0.0, 1.0);
            float depth = dot(w, z) / max(dot(w, vec4(1.0)), 1e-6);
            float characters = dot(vec4(lessThan(d, vec4(0.0))), vec4(1.0));
            fragColour = vec4(characters >= 2.0 ? -depth : depth, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The gather's entry point: the slices' visibilities over a pixel's radius, faded with distance and scaled by the air and water in front of it;
    /// writes R the visibility, G the view depth.
    /// </summary>
    const string GatherMain = """

        void main()
        {
            ivec2 c = ivec2(floor(vUv * uSize));
            float z = abs(texelFetch(uPacked, c, 0).r);
            if (z == 0.0) { fragColour = vec4(1.0, 60000.0, 0.0, 1.0); return; }
            vec3 p = offsetAt(c, z);
            vec3 view = normalize(-p);
            vec3 n = normalAt(c, p, z, view);

            // The radius in pixels between its bounds, and in the world what that span is: the falloff is of the area actually searched.
            float pixelSize = 2.0 * uTan.y * z / uSize.y;
            float radiusPixels = clamp(uReach.x / pixelSize, uReach.z, max(uReach.y, uReach.z));
            float radius = radiusPixels * pixelSize;

            // Half a turn: a slice searches both ways. The march's offset is decorrelated from the angle, or per-pixel noise becomes a pattern the blur keeps.
            float noise = ign(gl_FragCoord.xy + uMarch.w);
            float jitter = fract(noise + 0.6180339887);
            int slices = int(uMarch.x);
            vec2 sum = vec2(0.0);
            for (int s = 0; s < slices; s++)
            {
                float phi = (float(s) + noise) * GTAO_PI / float(slices);
                sum += sliceVisibility(c, p, view, n, vec2(cos(phi), sin(phi)), radius, radiusPixels, jitter);
            }
            float ao = sum.y > 1e-4 ? clamp(sum.x / sum.y, 0.0, 1.0) : 1.0;
            ao = mix(ao, 1.0, smoothstep(uFadeStart, uFadeEnd, z));
            if (ao < 0.999) ao = mix(1.0, ao, airVisibility(p) * waterVisibility(p));   // 1 - (1 - ao) * visibility
            fragColour = vec4(ao, z, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The normal from the depth alone.
    /// </summary>
    const string NormalFromDepth = """

        vec3 normalAt(ivec2 c, vec3 p, float z, vec3 view) { return depthNormal(c, p, z, view); }

        """;

    /// <summary>
    /// The lit fragment's shading normal where it wrote one and it faces the eye (a normal map can tilt it past the silhouette); else the depth's.
    /// </summary>
    const string NormalFromSurface = SurfaceSampler + """

        vec3 normalAt(ivec2 c, vec3 p, float z, vec3 view)
        {
            uvec4 s = texelFetch(uSurface, c, 0);
            if ((s.w & 256u) != 0u)
            {
                vec3 n = giOctDir(vec2(s.xy) / 65535.0 * 2.0 - 1.0);
                if (dot(n, view) > 0.05) return n;
            }
            return depthNormal(c, p, z, view);
        }

        """;

    /// <summary>
    /// The horizon search. <paramref name="surface"/>: the program reads the surface target for its normals.
    /// </summary>
    public static string Gather(bool surface) => "#version 330 core\n" + AtmosphereShaders.Functions + FogVolumeShaders.Functions
        + GatherDeclarations + DepthConvention.Glsl + Visibility + Geometry + (surface ? NormalFromSurface : NormalFromDepth) + GatherMain;

    /// <summary>
    /// The apply's inputs: the blurred occlusion, the scene depth, the two sizes and the contrast curve.
    /// </summary>
    const string ApplyDeclarations = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uAo;      // blurred: R the visibility, G the view depth
        uniform sampler2D uDepth;
        uniform vec2 uSize;         // the render size
        uniform vec2 uAoSize;       // the occlusion's size
        uniform vec2 uNearFar;      // of the scene depth
        uniform vec4 uCurve;        // x the contrast's exponent, y the strength, z the albedo where no fragment wrote one, w the half-size gather's shift in its texels

        """;

    /// <summary>
    /// The apply's entry point: the depth-weighted upsample of the occlusion, its contrast curve and the multi-bounce colour.
    /// </summary>
    const string ApplyMain = """

        // The occlusion's four nearest texels, weighted by how near each one's depth is to this pixel's; none near: open, never the wrong surface's occlusion.
        float visibilityAt(ivec2 px, float z)
        {
            vec2 f = (vec2(px) + 0.5) * uAoSize / uSize - 0.5 - uCurve.w;
            ivec2 i0 = ivec2(floor(f));
            vec2 w = f - vec2(i0);
            float sum = 0.0, total = 0.0;
            for (int k = 0; k < 4; k++)
            {
                ivec2 o = ivec2(k & 1, k >> 1);
                vec2 t = texelFetch(uAo, clamp(i0 + o, ivec2(0), ivec2(uAoSize) - 1), 0).rg;
                vec2 b = mix(1.0 - w, w, vec2(o));
                float weight = b.x * b.y * exp(-abs(t.g - z) / max(z * 0.02, 0.05));
                sum += t.r * weight;
                total += weight;
            }
            return total > 1e-4 ? sum / total : 1.0;
        }

        // Light that bounced between the surfaces of an occluded spot (Jimenez et al. 2016, the cubic fit): per channel of the albedo, so a red wall's corner stays red.
        vec3 multiBounce(float v, vec3 albedo)
        {
            vec3 a = 2.0404 * albedo - 0.3324;
            vec3 b = -4.7951 * albedo + 0.6417;
            vec3 c = 2.7552 * albedo + 0.6903;
            return min(max(vec3(v), ((v * a + b) * v + c) * v), vec3(1.0));
        }

        void main()
        {
            ivec2 px = ivec2(floor(gl_FragCoord.xy));
            float d = texelFetch(uDepth, px, 0).r;
            if (depthIsSky(d)) { fragColour = vec4(1.0); return; }
            float v = visibilityAt(px, depthViewZ(d, uNearFar));
            v = mix(1.0, pow(max(v, 0.0), uCurve.x), uCurve.y);   // the curve after the blur: the blur averages visibilities
            fragColour = vec4(multiBounce(v, albedoAt(px)), 1.0);
        }
        """;

    /// <summary>
    /// The constant albedo (<c>uCurve.z</c>) everywhere, for a program without the surface target.
    /// </summary>
    const string AlbedoConstant = """

        vec3 albedoAt(ivec2 px) { return vec3(uCurve.z); }

        """;

    /// <summary>
    /// The albedo the lit fragment wrote (its square root in eight bits); the constant on what wrote none (foliage, characters, water).
    /// </summary>
    const string AlbedoFromSurface = SurfaceSampler + """

        vec3 albedoAt(ivec2 px)
        {
            uvec4 s = texelFetch(uSurface, px, 0);
            if ((s.w & 256u) == 0u) return vec3(uCurve.z);
            vec3 c = vec3(float(s.z & 255u), float(s.z >> 8u), float(s.w & 255u)) / 255.0;
            return c * c;
        }

        """;

    /// <summary>
    /// The finished occlusion as a colour the scene is multiplied by. <paramref name="surface"/>: the program reads the surface target for its
    /// albedo.
    /// </summary>
    public static string Apply(bool surface) => ApplyDeclarations + DepthConvention.Glsl + (surface ? AlbedoFromSurface : AlbedoConstant) + ApplyMain;
}
