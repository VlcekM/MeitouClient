using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// GLSL for the world view's post-processing chain (written from the public techniques, not from Kenshi's shaders).
/// </summary>
static class PostProcessShaders
{
    /// <summary>
    /// Full-screen triangle; <c>vUv</c> runs 0..1 over the screen.
    /// </summary>
    public const string Vertex = """
        #version 330 core
        out vec2 vUv;
        void main()
        {
            vec2 p = vec2((gl_VertexID & 1) * 4.0 - 1.0, (gl_VertexID & 2) * 2.0 - 1.0);
            vUv = p * 0.5 + 0.5;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// <see cref="Vertex"/> for a target whose row 0 is at the top (Metal's: <see cref="Gpu.IGpuContext.RenderTargetsBottomUp"/> false): the
    /// triangle is drawn with clip y negated, so <c>vUv</c> still runs 0..1 from row 0 and <c>gl_FragCoord</c> counts rows from <c>vUv</c> 0, as on
    /// Vulkan. An image such a pass writes is then looked up by <c>clip * 0.5 + 0.5</c> or by world position the way Vulkan's is. Needs a pass that
    /// does not cull (the winding flips).
    /// </summary>
    public static readonly string VertexRowsDown = Vertex.Replace("gl_Position = vec4(p, 0.0, 1.0);", "gl_Position = vec4(p.x, -p.y, 0.0, 1.0);",
        StringComparison.Ordinal);

    /// <summary>
    /// One lookup of <c>uImage</c> at <c>vUv</c>: with a nearest sampler, the picture stretched to the target's size with the nearest pixel.
    /// </summary>
    public const string Stretch = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uImage;
        void main() { fragColour = texture(uImage, vUv); }
        """;

    /// <summary>
    /// Interleaved gradient noise (Jimenez): a cheap per-pixel value in 0..1.
    /// </summary>
    const string Noise = """
        float ign(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }
        """;

    /// <summary>
    /// Screen-space ambient occlusion at half resolution, from depth alone (normals from depth differences). Alchemy-style: each of 12 spiral taps
    /// adds the cosine of the sample direction against the normal over its distance, ignoring samples further than twice the radius. Writes R =
    /// occlusion factor (1 = open), G = view depth for the blur. The occlusion is scaled by how much of the surface is still visible through the air
    /// in front of it (haze, weather fog, fog volumes: the world position is rebuilt from depth and the frame globals' own <c>atmoApply</c>
    /// evaluated), <c>ao = 1 - (1 - ao) * visibility</c>, and by the water in front of it (<c>uWaterY</c>).
    /// </summary>
    public static readonly string Ssao = "#version 330 core\n" + Noise + AtmosphereShaders.Functions + FogVolumeShaders.Functions
        + LightShaftShaders.SunShadow + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uDepth;
        uniform sampler2D uMask;            // the scene colour at the render size, whose alpha is 1 on characters (unbound with uCharacterOccluder 1)
        uniform float uCharacterOccluder;   // how much a character counts as an occluder of what is round it (1: as anything else)
        uniform vec2 uTan;      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec2 uNearFar;  // of the scene depth
        uniform vec2 uSize;     // full-resolution size
        uniform float uRadius, uStrength, uFadeStart, uFadeEnd;
        uniform vec3 uRight, uUp, uBack;   // the camera's axes in the world (view space to world offsets)
        uniform vec3 uSsaoEye;
        uniform float uWaterY;               // the water plane's height (float min: no water)
        uniform vec4 uContact;               // contact shadows: x strength (0 off), y reach, z thickness (world units), w eye distance where they end
        """ + DepthConvention.Glsl + """

        // The share of a surface's occlusion the water in front of it leaves. The water writes no depth, so the floor's occlusion fell on the
        // water's own surface; it fades in over the water's last two units along the ray, as the water's edge does (docs/render-water.md "Edges").
        float waterVisibility(vec3 offset)
        {
            float below = uWaterY - (uSsaoEye.y + offset.y);
            if (uWaterY < -1e30 || below <= 0.0 || uSsaoEye.y <= uWaterY) return 1.0;
            return 1.0 - clamp(length(offset) * below / max(-offset.y, 1e-6) / 2.0, 0.0, 1.0);
        }

        // The share of a surface's own light that reaches the eye through the air. atmoApply (the haze) is linear in the colour it is given, so
        // apply(1) - apply(0) is its transmittance, as the grass uses it; the fog volumes (drawn by the fog pass, not atmoApply) add theirs.
        float airVisibility(vec3 offset)
        {
            vec3 position = uSsaoEye + offset;
            vec3 t = atmoApply(vec3(1.0), uSsaoEye, position) - atmoApply(vec3(0.0), uSsaoEye, position);
            float visible = clamp(dot(t, vec3(0.299, 0.587, 0.114)), 0.0, 1.0);
            float dist = length(offset);
            return dist < 1.0 ? visible : visible * fogVolumesTransmittance(uSsaoEye, offset / dist, dist);
        }

        // A depth texel's own position: its depth with the screen position of its centre. The occlusion runs at half size, so its pixels' centres fall on
        // the corners between depth texels; reading the depth there and placing it at the unsnapped position let rounding pick the texel row by row,
        // which tilted the rebuilt normal on whole rows and banded flat ground seen at an angle.
        vec3 viewPosAt(ivec2 t)
        {
            t = clamp(t, ivec2(0), ivec2(uSize) - 1);
            float z = depthViewZ(texelFetch(uDepth, t, 0).r, uNearFar);
            vec2 uv = (vec2(t) + 0.5) / uSize;
            return vec3((uv * 2.0 - 1.0) * uTan * z, -z);
        }
        vec3 viewPos(vec2 uv) { return viewPosAt(ivec2(floor(uv * uSize))); }

        // Contact shadows (docs/render-post.md "Contact shadows"): how much of the sun the depth hides from a point, 0 none to 1. The ray to the sun is walked on
        // the screen a pixel at a time (two when it is longer than the steps; past twice the steps it is cut short), its depth exact at each pixel (1 / depth is
        // linear on the screen), with no noise: one picture, the same every frame. What hides it stands above the point's own plane (the surface a point lies on
        // never shadows it, at whatever angle it is seen) and has the ray within the thickness behind it, so a near object does not shadow all that lies far
        // behind it on the screen. The shadow fades out over the last part of the reach.
        float contactHit(vec3 p, vec3 l, vec3 n, float z)
        {
            const int N = 48;
            float pixel = 2.0 * uTan.y * z / uSize.y;
            vec3 a = p + n * pixel;
            float reach = uContact.y;
            if (l.z > 0.0) reach = min(reach, 0.9 * (-a.z - uNearFar.x) / l.z);   // the end stays in front of the eye
            if (reach <= 0.0) return 0.0;
            vec2 sa = (a.xy / (-a.z * uTan) * 0.5 + 0.5) * uSize;
            vec3 b = a + l * reach;
            vec2 span = (b.xy / (-b.z * uTan) * 0.5 + 0.5) * uSize - sa;
            float len = max(abs(span.x), abs(span.y));
            if (len > float(2 * N))
            {
                b = a + l * (reach * float(2 * N) / len);
                span = (b.xy / (-b.z * uTan) * 0.5 + 0.5) * uSize - sa;
                len = max(abs(span.x), abs(span.y));
            }
            if (len < 1.0) return 0.0;
            int steps = min(int(ceil(len)), N);
            float ka = -1.0 / a.z, kb = -1.0 / b.z;
            float thickness = uContact.z + 0.01 * z, rise = 2.0 * pixel;
            for (int i = 1; i <= steps; i++)
            {
                float s = float(i) / float(steps);
                vec2 at = sa + span * s;
                if (any(lessThan(at, vec2(0.0))) || any(greaterThanEqual(at, uSize))) return 0.0;
                vec3 q = viewPosAt(ivec2(at));
                float behind = 1.0 / mix(ka, kb, s) + q.z;
                if (behind > 0.0 && behind < thickness && dot(q - p, n) > rise) return 1.0 - smoothstep(0.6, 1.0, s);
            }
            return 0.0;
        }

        // What a surface keeps of its light under a contact shadow that takes hit of its sun, where the sun is not written apart: 1 - (the sun's share of its
        // light) * the hit. The sun's share keeps a contact
        // shadow from going darker than the ambient light, and ends it under clouds and at night. Cascade shadows already took the sun away: nothing then.
        float contactLight(vec3 p, vec3 n, float z, float hit, float noise)
        {
            vec3 l = normalize(vec3(dot(uAtmoLight.xyz, uRight), dot(uAtmoLight.xyz, uUp), dot(uAtmoLight.xyz, uBack)));
            float nl = dot(n, l);
            vec3 world = uSsaoEye + p.x * uRight + p.y * uUp + p.z * uBack;
            vec3 worldNormal = n.x * uRight + n.y * uUp + n.z * uBack;
            vec3 weights = vec3(0.2126, 0.7152, 0.0722);
            float sun = 3.14159265 * nl * dot(uAtmoSunLight, weights) * shaftShadow(world, noise) * atmoCloudShadow(world);
            float ambient = dot(atmoIrradiance(worldNormal), weights) * uAtmoLight.w;
            float share = sun / max(sun + ambient, 1e-4);
            return 1.0 - hit * share;
        }

        void main()
        {
            ivec2 c = ivec2(floor(vUv * uSize));
            float d = texelFetch(uDepth, c, 0).r;
            if (depthIsSky(d)) { fragColour = vec4(1.0, 60000.0, 0.0, 1.0); return; }
            vec3 p = viewPosAt(c);
            float z = -p.z;
            vec3 l = viewPosAt(c - ivec2(1, 0)), r = viewPosAt(c + ivec2(1, 0));
            vec3 u = viewPosAt(c + ivec2(0, 1)), b = viewPosAt(c - ivec2(0, 1));
            vec3 dx = abs(r.z - p.z) < abs(p.z - l.z) ? r - p : p - l;
            vec3 dy = abs(u.z - p.z) < abs(p.z - b.z) ? u - p : p - b;
            vec3 n = normalize(cross(dx, dy));
            if (n.z < 0.0) n = -n;

            float radius = max(uRadius, 0.03 * z); // world radius, at least a few percent of the distance so far objects still get contact shading
            vec2 radiusUv = 0.5 * radius / (uTan * z);
            // Close-ups: at most 6 % of the screen (the cost), and the world radius shrunk with it, so the falloff and the weights below are of
            // the area actually sampled (clamping only the taps packed them closer and strengthened every one).
            float shrink = min(1.0, 0.06 / max(radiusUv.x, radiusUv.y));
            radiusUv *= shrink;
            radius *= shrink;
            // An occluder must rise above the tangent plane by a share of the radius (so the look is the same at every distance) and two
            // pixels' size (the rebuilt normal's error).
            float bias = 0.5 * radius + 4.0 * uTan.y * z / uSize.y;
            float spin = ign(gl_FragCoord.xy) * 6.2831853;
            float sum = 0.0;
            const int N = 12;
            for (int i = 0; i < N; i++)
            {
                float t = (float(i) + 0.5) / float(N);
                float a = spin + float(i) * 2.399963;
                vec2 uv = vUv + vec2(cos(a), sin(a)) * radiusUv * t;
                vec3 v = viewPos(uv) - p;
                float len = length(v);
                float vn = dot(v, n) - bias;
                float range = 1.0 - smoothstep(radius, 2.0 * radius, len);
                float occluder = uCharacterOccluder < 1.0 ? mix(1.0, uCharacterOccluder, textureLod(uMask, uv, 0.0).r) : 1.0;
                sum += max(vn, 0.0) / (len * len + 0.05 * radius * radius) * radius * range * occluder;
            }
            float ao = 1.0 - clamp(uStrength * sum / float(N), 0.0, 1.0);
            ao = mix(ao, 1.0, smoothstep(uFadeStart, uFadeEnd, z));
            if (ao < 0.999)
            {
                vec3 offset = p.x * uRight + p.y * uUp + p.z * uBack;
                ao = mix(1.0, ao, airVisibility(offset) * waterVisibility(offset));   // 1 - (1 - ao) * visibility
            }
            fragColour = vec4(ao, z, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Contact shadows (docs/render-post.md "Contact shadows") at the render size, on their own: the <see cref="Ssao"/> source's functions with a
    /// main of their own, so they run with SSAO, GTAO or no occlusion, keep the detail a half-size pass blurs (armour layers, hair over a face) and
    /// take none of the occlusion's reduction on characters. With the sun written apart (<c>uTermOn</c>, <see cref="PostProcess.ContactSun"/>):
    /// writes the sun term times what the contact shadows leave of it, through the haze, added to the scene. Without: the light kept (1: none taken),
    /// multiplied into the scene colour.
    /// </summary>
    public static readonly string Contact = Ssao[..(Ssao.IndexOf("\nvoid main()", StringComparison.Ordinal) + 1)] + """
        uniform usampler2D uSurface;   // the GI surface target: bit 16 of w is set where the sun was written apart (the traced sun's pixels, or these shadows')
        uniform float uSurfaceOn;      // 1: uSurface is bound
        uniform sampler2D uTerm;       // the sun term written apart
        uniform float uTermOn;         // 1: add the term (the sun is not in the scene colour); 0: multiply the scene colour

        void main()
        {
            ivec2 c = ivec2(floor(gl_FragCoord.xy));
            float d = texelFetch(uDepth, c, 0).r;
            bool apart = uTermOn > 0.5;
            bool marked = uSurfaceOn > 0.5 && (texelFetch(uSurface, c, 0).w & 65536u) != 0u;
            // Added: only where a term was written. Multiplied: not where the traced sun lights, whose rays hold the contact.
            if (apart ? !marked || depthIsSky(d) : marked || depthIsSky(d)) { if (apart) discard; fragColour = vec4(1.0); return; }
            vec3 p = viewPosAt(c);
            float z = -p.z;
            float hit = 0.0;
            vec3 n = vec3(0.0, 0.0, 1.0);
            if (z < uContact.w && uContact.x > 0.0)
            {
                vec3 l = viewPosAt(c - ivec2(1, 0)), r = viewPosAt(c + ivec2(1, 0));
                vec3 u = viewPosAt(c + ivec2(0, 1)), b = viewPosAt(c - ivec2(0, 1));
                vec3 dx = abs(r.z - p.z) < abs(p.z - l.z) ? r - p : p - l;
                vec3 dy = abs(u.z - p.z) < abs(p.z - b.z) ? u - p : p - b;
                n = normalize(cross(dx, dy));
                if (n.z < 0.0) n = -n;
                vec3 sun = normalize(vec3(dot(uAtmoLight.xyz, uRight), dot(uAtmoLight.xyz, uUp), dot(uAtmoLight.xyz, uBack)));
                if (dot(n, sun) > 0.0) hit = uContact.x * contactHit(p, sun, n, z) * (1.0 - smoothstep(0.6 * uContact.w, uContact.w, z));
            }
            vec3 offset = p.x * uRight + p.y * uUp + p.z * uBack;
            if (apart)
            {
                vec3 hazeMul, hazeAdd;
                atmoApplyCoefficients(uSsaoEye, uSsaoEye + offset, hazeMul, hazeAdd);
                fragColour = vec4(texelFetch(uTerm, c, 0).rgb * (1.0 - hit) * hazeMul, 0.0);
                return;
            }
            float kept = hit > 0.0 ? contactLight(p, n, z, hit, ign(gl_FragCoord.xy)) : 1.0;
            if (kept < 0.999) kept = mix(1.0, kept, airVisibility(offset) * waterVisibility(offset));
            fragColour = vec4(vec3(kept), 1.0);
        }
        """;

    /// <summary>
    /// The placed fog volumes (docs/formats/fogfeatures.md), one pass over the whole scene instead of a term in every world shader. The pixel's
    /// world offset is rebuilt from the scene depth as SSAO does (none: the sky, at the far clip D), pulled in to
    /// the water plane where that lies in front (the water writes no depth; its own surface is what the game's G-buffer reads, and the water
    /// shader used to fog it there). The shader accumulates every volume, farthest first, into (colour, transmittance), which the hardware blends
    /// as <c>scene * transmittance + colour</c> (blend one, source alpha; alpha of the scene untouched).
    /// </summary>
    public static readonly string FogVolumes = "#version 330 core\n" + AtmosphereShaders.Functions + LightShaftShaders.FogVolumeShadow
        + FogVolumeShaders.Functions + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth;
        uniform vec2 uNearPlanes;               // near, far of the scene depth
        uniform vec2 uTan;                      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec3 uRight, uUp, uBack;        // the camera's axes in the world
        uniform float uWaterY;                  // the water plane's height (float min: no water)
        """ + DepthConvention.Glsl + """

        void main()
        {
            float d = textureLod(uNearDepth, vUv, 0.0).r;
            vec2 ndc = vUv * 2.0 - 1.0;
            vec3 perZ = uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack;   // the offset per unit of view depth
            float len = length(perZ);
            vec3 dir = perZ / len;
            vec3 eye = uFogVolumeEye.xyz;
            float dist = depthIsSky(d) ? 1e9 : depthViewZ(d, uNearPlanes) * len;   // the sky: the volumes cap it at the far clip D
            if (uWaterY > -1e30 && abs(dir.y) > 1e-6)
            {
                float tw = (uWaterY - eye.y) / dir.y;
                if (tw > 0.0 && tw < dist) dist = tw;
            }
            vec3 add;
            float trans;
            fogVolumesAccumulate(eye, dir, dist, add, trans);
            fragColour = vec4(add, trans);
        }
        """;

    /// <summary>
    /// The pixel's ray and distance as the fog volumes and light shafts passes rebuild them, for the passes of <see cref="PostProcess"/>'s merged
    /// scene layers: the scene depth, else the sky (1e9); pulled in to the water plane where that lies in front (sky false then). The camera comes
    /// from the light shafts' uniforms (<see cref="LightShaftShaders.Common"/>, set whether the shafts run or not).
    /// </summary>
    const string SceneDistance = """

        uniform sampler2D uNearDepth;
        uniform vec2 uNearPlanes;               // near, far of the scene depth
        uniform float uWaterY;                  // the water plane's height (float min: no water)
        uniform vec2 uFullSize;                 // the render size in pixels
        """ + DepthConvention.Glsl + """

        float sceneDistance(ivec2 p, out vec3 dir, out bool sky, out float waterWeight)
        {
            float d = texelFetch(uNearDepth, p, 0).r;
            vec2 ndc = (vec2(p) + 0.5) / uFullSize * 2.0 - 1.0;
            vec3 perZ = uShaftRight * ndc.x * uShaftTan.x + uShaftUp * ndc.y * uShaftTan.y - uShaftBack;   // the offset per unit of view depth
            float len = length(perZ);
            dir = perZ / len;
            sky = depthIsSky(d);
            waterWeight = 1.0;
            float dist = sky ? 1e9 : depthViewZ(d, uNearPlanes) * len;
            if (uWaterY > -1e30 && abs(dir.y) > 1e-6)
            {
                float tw = (uWaterY - uShaftEye.y) / dir.y;
                // The water's analytic plane must stop at the same far clip as its rendered mesh.
                float waterFar = uNearPlanes.y * len;
                if (tw > 0.0 && tw < waterFar && tw < dist)
                {
                    dist = tw;
                    sky = false;
                    waterWeight = atmoWaterWeight(tw, waterFar / len);
                }
            }
            return dist;
        }
        """;

    /// <summary>
    /// The scene layers at half the render size (<c>fog-half</c> for the fog volumes, <c>shafts-half</c> for the light shafts' apply;
    /// docs/render-post.md "Merged scene layers"): each texel is the full-size pixel at twice its coordinates (not an average: the upsample in <see
    /// cref="SceneLayers"/> compares against that pixel's distance). It holds the layers asked for in the order the merged pass applies them, the
    /// shafts' then the fog volumes', as one (colour, factor) the scene is blended by, <c>scene * a + rgb</c>; without blending. A fog-only texel is
    /// the fog's (colour, transmittance).
    /// </summary>
    public static readonly string LayersHalf = "#version 330 core\n" + AtmosphereShaders.Functions + LightShaftShaders.FogVolumeShadow
        + FogVolumeShaders.Functions + LightShaftShaders.Alpha + LightShaftShaders.Air + LightShaftShaders.ApplyFunctions + SceneDistance + """

        out vec4 fragColour;
        uniform vec4 uLayers;   // x 1: the light shafts; y 1: the fog volumes

        void main()
        {
            ivec2 p = min(ivec2(gl_FragCoord.xy) * 2, ivec2(uFullSize) - 1);
            vec3 dir;
            bool sky;
            float waterWeight;
            float dist = sceneDistance(p, dir, sky, waterWeight);
            vec4 layer = vec4(0.0, 0.0, 0.0, 1.0);
            if (uLayers.x > 0.5) layer = shaftLayer((vec2(p) + 0.5) / uFullSize, dir, sky ? uShaftRange.y : dist, sky, waterWeight);
            if (uLayers.y > 0.5)
            {
                vec3 add;
                float trans;
                fogVolumesAccumulate(uFogVolumeEye.xyz, dir, dist, add, trans);
                layer = vec4(layer.rgb * trans + add, layer.a * trans);
            }
            fragColour = layer;
        }
        """;

    /// <summary>
    /// The fog volumes' sun shadow kept apart (docs/render-shafts.md "Fog shadow after the upscaler"): at half the render size as
    /// <see cref="LayersHalf"/> (each texel the full-size pixel at twice its coordinates), what the shadow changes in the volumes' colour, shadowed
    /// minus unshadowed (rgb, mostly negative). The merged pass then blends the volumes unshadowed and <see cref="LateFogShadow"/> adds this over the
    /// upscaler's output.
    /// </summary>
    public static readonly string FogShadowHalf = "#version 330 core\n#define FOG_VOLUME_DELTA\n" + AtmosphereShaders.Functions
        + LightShaftShaders.FogVolumeShadow
        + FogVolumeShaders.Functions + LightShaftShaders.Alpha + LightShaftShaders.Air + LightShaftShaders.ApplyFunctions + SceneDistance + """

        out vec4 fragColour;

        void main()
        {
            ivec2 p = min(ivec2(gl_FragCoord.xy) * 2, ivec2(uFullSize) - 1);
            vec3 dir;
            bool sky;
            float waterWeight;
            float dist = sceneDistance(p, dir, sky, waterWeight);
            vec3 add;
            float trans;
            fogVolumesAccumulate(uFogVolumeEye.xyz, dir, dist, add, trans);
            fragColour = vec4(fogAddLit - add, 1.0);
        }
        """;

    /// <summary>
    /// The depth-aware upsample of <see cref="LayersHalf"/> (needs <see cref="SceneDistance"/> before it): of the four nearest half-size texels,
    /// weighted bilinearly and by how close the distance of the pixel each stands for is to this pixel's (relative difference r, weight e^(−20 r)).
    /// False where no texel is within 10 % (a thin edge no texel lies on: twigs against the far fog).
    /// </summary>
    const string HalfUpsample = """

        uniform sampler2D uLayerHalf;

        bool halfUpsampled(ivec2 p, float dist, out vec4 layer)
        {
            ivec2 size = textureSize(uLayerHalf, 0);
            ivec2 base = p / 2;
            vec2 f = vec2(p - base * 2) * 0.5;   // 0: the pixel a texel stands for, 0.5: between two
            vec4 sum = vec4(0.0);
            float weights = 0.0, bestR = 1e30;
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                {
                    float b = (i == 0 ? 1.0 - f.x : f.x) * (j == 0 ? 1.0 - f.y : f.y);
                    if (b <= 0.0) continue;
                    ivec2 t = min(base + ivec2(i, j), size - 1);
                    vec3 tDir;
                    bool tSky;
                    float tWaterWeight;
                    float tDist = sceneDistance(min(t * 2, ivec2(uFullSize) - 1), tDir, tSky, tWaterWeight);
                    float r = abs(tDist - dist) / max(min(tDist, dist), 1.0);
                    vec4 v = texelFetch(uLayerHalf, t, 0);
                    bestR = min(bestR, r);
                    float w = b * exp(-20.0 * r);
                    sum += v * w;
                    weights += w;
                }
            layer = sum / max(weights, 1e-6);
            return bestR <= 0.1;
        }
        """;

    /// <summary>
    /// The fog volumes' shadow (<see cref="FogShadowHalf"/>) over the upscaler's output (<c>PostProcess.CompositeLateFogShadow</c>): the target
    /// pixel's place in the render picture (the jitter undone), the four render pixels round it each upsampled by its own distance
    /// (<see cref="HalfUpsample"/>), weighted bilinearly; then last frame's, reprojected by the closest of the four's motion, kept within the four's
    /// range and blended in (the four follow the jitter at a depth edge). Written to a target of its own, which <see cref="LateFogShadowApply"/>
    /// adds.
    /// </summary>
    public static readonly string LateFogShadow = "#version 330 core\n" + LightShaftShaders.CommonUniforms + AtmosphereShaders.WaterHorizonFunctions
        + SceneDistance + HalfUpsample + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform vec4 uLate;          // xy render pixels per target pixel, zw the jitter (render pixels)
        uniform vec4 uLateTarget;    // xy the target's size, z 1 with last frame's in uLateHistory, w the share of this frame's
        uniform sampler2D uLateHistory;
        uniform sampler2D uLateMotion;   // the motion target (rg: this frame's place minus last frame's, in 0..1 units)

        void main()
        {
            vec2 rp = gl_FragCoord.xy * uLate.xy - 0.5 + uLate.zw;
            vec2 b = floor(rp), f = rp - b;
            ivec2 last = ivec2(uFullSize) - 1;
            vec4 sum = vec4(0.0), lo = vec4(1e30), hi = vec4(-1e30);
            float closest = 1e30;
            ivec2 qClosest = ivec2(0);
            for (int i = 0; i < 4; i++)
            {
                ivec2 q = clamp(ivec2(b) + ivec2(i & 1, i >> 1), ivec2(0), last);
                vec3 dir;
                bool sky;
                float waterWeight;
                float dist = sceneDistance(q, dir, sky, waterWeight);
                vec4 v;
                halfUpsampled(q, dist, v);
                sum += v * (((i & 1) == 0 ? 1.0 - f.x : f.x) * ((i >> 1) == 0 ? 1.0 - f.y : f.y));
                lo = min(lo, v);
                hi = max(hi, v);
                if (dist < closest) { closest = dist; qClosest = q; }
            }
            if (uLateTarget.z < 0.5) { fragColour = sum; return; }
            vec2 prev = gl_FragCoord.xy / uLateTarget.xy - texelFetch(uLateMotion, qClosest, 0).rg;
            if (any(lessThan(prev, vec2(0.0))) || any(greaterThan(prev, vec2(1.0)))) { fragColour = sum; return; }
            fragColour = mix(clamp(textureLod(uLateHistory, prev, 0.0), lo, hi), sum, uLateTarget.w);
        }
        """;

    /// <summary>
    /// The upscaler's output plus the fog volumes' filtered shadow (<see cref="LateFogShadow"/>), into a target of its own (the TAA's output is its
    /// next history).
    /// </summary>
    public const string LateFogShadowApply = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene;
        uniform sampler2D uShadow;
        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            vec4 scene = texelFetch(uScene, p, 0);
            fragColour = vec4(max(scene.rgb + texelFetch(uShadow, p, 0).rgb, vec3(0.0)), scene.a);
        }
        """;

    /// <summary>
    /// The merged scene layers (<c>layer-merge</c>, docs/render-post.md "Merged scene layers"): the light shafts' apply, the fog volumes and the
    /// SSAO's factor in one pass over the scene colour instead of three. Each is a (colour, factor) the scene is blended by as
    /// <c>scene * a + rgb</c>; in their old order (shafts, fog, occlusion) they compose to <c>((scene * a1 + c1) * a2 + c2) * ao</c> =
    /// <c>scene * (a1 a2 ao) + (c1 a2 + c2) ao</c>, one blend of the same state (the occlusion is grey). The shafts' and the fog are computed here,
    /// or upsampled from <see cref="LayersHalf"/> (uHalf: which of them that texture holds, <see cref="HalfUpsample"/>); where the upsample finds no
    /// texel, computed here at full size. With uHalf 0 (and the <c>layers-lean</c> split, <see cref="SceneLayersLean"/>) it is the pass for the
    /// pixels the lean one left out.
    /// </summary>
    public static readonly string SceneLayers = "#version 330 core\n" + AtmosphereShaders.Functions + LightShaftShaders.FogVolumeShadow
        + FogVolumeShaders.Functions + LightShaftShaders.Alpha + LightShaftShaders.Air + LightShaftShaders.ApplyFunctions + SceneDistance
        + HalfUpsample + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uAo;
        uniform vec4 uLayers;   // x 1: the light shafts; y 1: the fog volumes; z 1: the occlusion
        uniform vec4 uHalf;     // x 1: the half-size texture holds the shafts' layer; y 1: it holds the fog volumes'

        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            vec3 dir;
            bool sky;
            float waterWeight;
            float dist = sceneDistance(p, dir, sky, waterWeight);
            vec4 layer = vec4(0.0, 0.0, 0.0, 1.0);   // scene * a + rgb
            vec4 up = vec4(0.0, 0.0, 0.0, 1.0);
            // Half size, unless no texel near this pixel shows a surface at its distance (a twig against the far fog): then here, at full size.
            bool fromHalf = (uHalf.x > 0.5 || uHalf.y > 0.5) && halfUpsampled(p, dist, up);
            bool shaftsHere = uLayers.x > 0.5 && !(fromHalf && uHalf.x > 0.5), fogHere = uLayers.y > 0.5 && !(fromHalf && uHalf.y > 0.5);
            if (shaftsHere) layer = shaftLayer(vUv, dir, sky ? uShaftRange.y : dist, sky, waterWeight);
            if (fromHalf) layer = uHalf.x > 0.5 ? up : vec4(layer.rgb * up.a + up.rgb, layer.a * up.a);   // the texture's own order: the shafts' first, then the fog
            if (fogHere)
            {
                vec3 add;
                float trans;
                fogVolumesAccumulate(uFogVolumeEye.xyz, dir, dist, add, trans);
                layer = vec4(layer.rgb * trans + add, layer.a * trans);
            }
            if (uLayers.z > 0.5) layer *= texture(uAo, vUv).r;
            fragColour = layer;
        }
        """;

    /// <summary>
    /// The lean half of the merged scene layers (<c>layers-lean</c>, docs/render-post.md "Merged scene layers"), for when <see cref="LayersHalf"/>
    /// holds every pending layer: the upsample (<see cref="HalfUpsample"/>) and the occlusion, nothing else. Where no texel fits (a few hundredths of
    /// a percent of the pixels) it leaves the scene as it is and writes depth 1 into the mask depth buffer, depth 0 elsewhere; <see
    /// cref="SceneLayers"/> then draws those pixels alone (its full-screen triangle, at depth 0.5 in GL clip space, passes "less" only against the
    /// 1), computing everything at full size. The full pass carries the shafts' and the fog volumes' code, which in one shader with the upsample more
    /// than doubled the time of the pass (registers: 0.08 against 0.17 ms with the fallback code removed).
    /// </summary>
    public static readonly string SceneLayersLean = "#version 330 core\n" + LightShaftShaders.CommonUniforms
        + AtmosphereShaders.WaterHorizonFunctions + SceneDistance + HalfUpsample + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uAo;
        uniform vec4 uLayers;   // z 1: the occlusion

        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            vec3 dir;
            bool sky;
            float waterWeight;
            float dist = sceneDistance(p, dir, sky, waterWeight);
            vec4 layer;
            if (!halfUpsampled(p, dist, layer))
            {
                gl_FragDepth = 1.0;
                fragColour = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }
            gl_FragDepth = 0.0;
            if (uLayers.z > 0.5) layer *= texture(uAo, vUv).r;
            fragColour = layer;
        }
        """;

    /// <summary>
    /// Fog shading rate, first pass (<c>fog-vrs</c>, docs/render-post.md "Fog shading rate"): at a quarter of the render size, the distance to the
    /// nearest surface of each 4 x 4 block of pixels (red) and to the farthest (green: 1e9 where the block shows sky), rebuilt from the scene depth
    /// as the fog pass does (no surface: the sky at 1e9; where the water plane lies in front, the water's).
    /// </summary>
    public static readonly string FogRateDistance = "#version 330 core\n" + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth;
        uniform vec2 uNearPlanes;               // near, far of the scene depth
        uniform vec2 uTan;                      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec3 uRight, uUp, uBack;        // the camera's axes in the world
        uniform vec3 uEye;
        uniform vec2 uSize;                     // the render size in pixels
        uniform float uWaterY;                  // the water plane's height (float min: no water)
        """ + DepthConvention.Glsl + """

        void main()
        {
            ivec2 base = ivec2(gl_FragCoord.xy) * 4;
            ivec2 last = ivec2(uSize) - 1;
            float best = 3.0e38, farthest = 0.0;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    ivec2 p = min(base + ivec2(x, y), last);
                    float d = texelFetch(uNearDepth, p, 0).r;
                    vec2 ndc = (vec2(p) + 0.5) / uSize * 2.0 - 1.0;
                    vec3 perZ = uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack;
                    float len = length(perZ);
                    float dist = depthIsSky(d) ? 1e9 : depthViewZ(d, uNearPlanes) * len;
                    vec3 dir = perZ / len;
                    if (uWaterY > -1e30 && abs(dir.y) > 1e-6)
                    {
                        float tw = (uWaterY - uEye.y) / dir.y;
                        if (tw > 0.0 && tw < dist) dist = tw;
                    }
                    best = min(best, dist);
                    farthest = max(farthest, dist);
                }
            fragColour = vec4(best, farthest, 0.0, 0.0);
        }
        """;

    /// <summary>
    /// Fog shading rate, second pass: one texel per shading rate tile, the code of the fragment size for the next frame's scene passes. The nearest
    /// distance over the tile and a margin of
    /// <c>FogShadingRate.MarginTexels</c> blocks round it (the camera moves between this frame's depth and the frame it is used in) goes through the
    /// placed fog volumes (<c>fogVolumesAccumulate</c>, along the tile centre's ray) and the Kenshi haze and weather fog the world shaders apply;
    /// where what is left of the surface (1 - haze) * transmittance is under <c>uThreshold</c> the rate is 2 x 2 and 4 x 4 (the codes of
    /// <c>FogShadingRate.Encode</c>).
    /// </summary>
    public static readonly string FogRate = "#version 330 core\n" + AtmosphereShaders.Functions + FogVolumeShaders.Functions + """

        in vec2 vUv;
        out uvec4 fragColour;
        uniform sampler2D uDistance;            // FogRateDistance: a quarter of the render size
        uniform vec2 uTan;
        uniform vec3 uRight, uUp, uBack;
        uniform vec2 uSize;                     // the render size in pixels
        uniform vec4 uRate;                     // x: the tile in pixels, y: the margin in blocks, z: the largest fragment size (2 or 4), w: unused
        uniform vec2 uThreshold;                // opacity from which 2 x 2, from which 4 x 4

        // The share of a surface dist away the world shaders' haze (atmoKenshiHaze) and the weather's fog hide; 0 where they do not apply (the physical haze, the simple sky).
        float hazeHidden(float dist)
        {
            if (uAtmoParams.x < 0.5 || uAtmoHaze.x < 0.5 || uAtmoAltitude.x > 0.999 || dist > 1.0e8) return 0.0;
            float level = clamp((dist - uAtmoHaze.y) / max(uAtmoHaze.z - uAtmoHaze.y, 1.0), 0.0, 1.0);
            level = min(level * uAtmoAltitude.y, 1.0);
            float alpha = level;
            if (uAtmoFog.z > 0.0)
            {
                float amount = clamp(dist * uAtmoHaze.w, 0.0, 1.0);
                float curve = (amount < 0.5 ? 2.0 * amount * amount : 1.0 - 2.0 * (amount - 1.0) * (amount - 1.0)) * uAtmoFog.z;
                alpha = clamp(alpha + curve, 0.0, 1.0);
            }
            return alpha * (1.0 - clamp(uAtmoAltitude.x, 0.0, 1.0));
        }

        void main()
        {
            ivec2 tile = ivec2(gl_FragCoord.xy);
            int per = int(uRate.x) / 4, margin = int(uRate.y);
            ivec2 a0 = tile * per - margin, last = textureSize(uDistance, 0) - 1;
            float dist = 3.0e38, farthest = 0.0;
            for (int y = 0; y < per + 2 * margin; y++)
                for (int x = 0; x < per + 2 * margin; x++)
                {
                    vec2 block = texelFetch(uDistance, clamp(a0 + ivec2(x, y), ivec2(0), last), 0).rg;
                    dist = min(dist, block.r);
                    farthest = max(farthest, block.g);
                }
            vec2 centre = min((vec2(tile) + 0.5) * uRate.x, uSize - 0.5);
            vec2 ndc = centre / uSize * 2.0 - 1.0;
            vec3 perZ = uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack;
            vec3 dir = normalize(perZ);
            vec3 add;
            float trans;
            fogVolumesAccumulate(uFogVolumeEye.xyz, dir, dist, add, trans);
            float opacity = 1.0 - (1.0 - hazeHidden(dist)) * trans;
            uint code = 0u;
            if (opacity >= uThreshold.x) code = 5u;
            if (opacity >= uThreshold.y && uRate.z > 3.5) code = 10u;
            // A surface's outline against the sky: the haze hides the surface's colour but not the step to the (unhazed) sky, which coarse fragments
            // turn into blocks. Full rate there, unless the fog volumes in front hide the outline too.
            if (dist < 1.0e8 && farthest > 1.0e8 && trans > 0.05) code = 0u;
            fragColour = uvec4(code, 0u, 0u, 0u);
        }
        """;

    /// <summary>
    /// The low-resolution particles' depth (Meitou): each texel is the nearest or the farthest of the <c>uDiv</c> x <c>uDiv</c> block of the scene's
    /// depth it covers, written as the depth of a depth-only rendering, so the hardware test of the particle draws still rejects the sprite area
    /// hidden behind the scene (early-Z). <c>uLayer</c> 1: the nearest, 2: the farthest (the particles are drawn once against each, <see
    /// cref="ParticleUpsample"/>). Where the two lie within 4 % of each other the farthest is written as the near plane's depth, so the second draw
    /// rejects every fragment there (the upsample then takes the first draw's, whose depth matches): it shades only along depth edges, where the two
    /// differ.
    /// </summary>
    public const string ParticleDepth = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uDepth;
        uniform vec2 uFullSize;
        uniform float uDiv;
        uniform int uLayer;
        uniform vec2 uPlanes;   // the scene depth's near and far planes
        """ + DepthConvention.Glsl + """
        void main()
        {
            vec2 base = floor(gl_FragCoord.xy) * uDiv;
            float nearest = DEPTH_SKY, farthest = DEPTH_NEAREST;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    if (float(x) < uDiv && float(y) < uDiv)
                    {
                        float d = texelFetch(uDepth, ivec2(min(base + vec2(x, y), uFullSize - 1.0)), 0).r;
                        nearest = depthNearer(nearest, d);
                        farthest = depthFarther(farthest, d);
                    }
            // All sky, or one surface.
            bool oneSurface = depthIsSky(nearest) || (!depthIsSky(farthest) && depthViewZ(farthest, uPlanes) < depthViewZ(nearest, uPlanes) * 1.04);
            gl_FragDepth = uLayer == 2 ? (oneSurface ? DEPTH_NEAREST : farthest) : nearest;
            fragColour = vec4(gl_FragDepth);   // the same depth as a colour, for the Meitou fog's soft fade (the depth attachment cannot be read while tested)
        }
        """;

    /// <summary>
    /// The scene's depth copied into a colour target at the render size, for the full-size particles' soft fade (the Meitou <c>fog</c> switch).
    /// </summary>
    public const string ParticleSoftDepth = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uDepth;
        void main() { fragColour = vec4(texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r); }
        """;

    /// <summary>
    /// The depth-aware upsample of the low-resolution particles, drawn twice: <c>uAccum</c> tested against each block's nearest depth
    /// (<c>uLowDepth</c>), <c>uAccumFar</c> against its farthest (<c>uLowDepthFar</c>; <see cref="ParticleDepth"/> layers 1 and 2). Each of the four
    /// nearest texels offers both, each weighted bilinearly (plus a floor) and divided by the square of how far its depth is from this pixel's
    /// (relative, linear), so a pixel takes the sprites tested against its own surface from every texel round it: on a ridge the near draw's, on the
    /// land behind it the far draw's (which holds what lies between the ridge and the land). One draw against a checkerboard of the two depths had
    /// those texels only every other one along an edge, and the dust behind a ridge followed it in steps of two texels. With <c>footprint</c>
    /// (blended over a vendor upscaler's unjittered output) the pixel's depth is the nearest of the 2 x 2 render texels round it.
    /// </summary>
    const string ParticleUpsample = """
        uniform sampler2D uAccum, uLowDepth, uAccumFar, uLowDepthFar, uDepth;
        uniform vec2 uLowSize;
        uniform vec2 uNearPlanes;   // near, far of the scene depth the particles were tested against
        uniform float uDiv;
        """ + DepthConvention.Glsl + """
        float linearZ(float d) { return depthViewZ(d, uNearPlanes); }
        vec4 particlesAt(vec2 fc, bool footprint)
        {
            vec2 lp = fc / uDiv - 0.5;
            vec2 f = lp - floor(lp);
            ivec2 i0 = ivec2(floor(lp));
            ivec2 hi = ivec2(uLowSize) - 1;
            ivec2 q[4];
            vec4 an[4], af[4];
            float total = 0.0;
            for (int i = 0; i < 4; i++)
            {
                q[i] = clamp(i0 + ivec2(i & 1, i >> 1), ivec2(0), hi);
                an[i] = texelFetch(uAccum, q[i], 0);
                af[i] = texelFetch(uAccumFar, q[i], 0);
                total += an[i].a + an[i].r + an[i].g + an[i].b + af[i].a + af[i].r + af[i].g + af[i].b;
            }
            if (total <= 0.0) return vec4(0.0);
            float d0 = texelFetch(uDepth, ivec2(fc), 0).r;
            if (footprint)
            {
                ivec2 r0 = ivec2(floor(fc - 0.5)), rhi = textureSize(uDepth, 0) - 1;
                for (int i = 0; i < 4; i++)
                    d0 = depthNearer(i == 0 ? DEPTH_SKY : d0, texelFetch(uDepth, clamp(r0 + ivec2(i & 1, i >> 1), ivec2(0), rhi), 0).r);
            }
            float z0 = linearZ(d0);
            vec4 sum = vec4(0.0);
            float weights = 0.0;
            for (int i = 0; i < 4; i++)
            {
                float bw = ((i & 1) == 0 ? 1.0 - f.x : f.x) * ((i >> 1) == 0 ? 1.0 - f.y : f.y) + 0.05;
                float en = 0.02 + abs(linearZ(texelFetch(uLowDepth, q[i], 0).r) - z0) / z0;
                float ef = 0.02 + abs(linearZ(texelFetch(uLowDepthFar, q[i], 0).r) - z0) / z0;
                float wn = bw / (en * en), wf = bw / (ef * ef);
                sum += an[i] * wn + af[i] * wf;
                weights += wn + wf;
            }
            return sum / weights;
        }
        """;

    /// <summary>
    /// The low-resolution particles over the vendor upscaler's output (<c>LateParticles.CompositeFiltered</c>): both sizes (uHas: their
    /// divisors, 0 for none; each as <see cref="ParticleUpsample"/>, the quarter size over the half), at the surfaces of the four render pixels round
    /// the display pixel (the jitter undone: render pixel i shows the unjittered picture at i + 0.5 - jitter), weighted bilinearly; then the temporal
    /// filter: last frame's (uHistory, reprojected by the closest of the four's motion) kept within the four values and blended in. Off an outline
    /// the four agree, so drifting dust does not trail; on one they follow the jitter, which averages out into the upscaler's smooth edge instead of
    /// a staircase.
    /// </summary>
    public const string ParticleLate = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uAccumH, uLowDepthH, uAccumFarH, uLowDepthFarH, uAccumQ, uLowDepthQ, uAccumFarQ, uLowDepthFarQ;
        uniform sampler2D uDepth;               // the scene's depth at the render size
        uniform sampler2D uScene;               // the scene's colour at the render size (the upscaler's input, no late particles)
        uniform sampler2D uOutput;              // the upscaler's output at the display size (what the layer goes over)
        uniform sampler2D uHistory;             // last frame's result at the display size
        uniform sampler2D uMotion;              // the motion target (rg: this frame's place minus last frame's, 0..1 units)
        uniform vec2 uLowSizeH, uLowSizeQ;
        uniform vec4 uHas;                      // x the half size's divisor, y the quarter's (0: not drawn)
        uniform vec2 uNearPlanes;
        uniform vec4 uLate;                     // xy render pixels per display pixel, zw the jitter (render pixels)
        uniform vec4 uTarget;                   // xy the display size, z 1 with a history, w this frame's share
        """ + DepthConvention.Glsl + """
        float linearZ(float d) { return depthViewZ(d, uNearPlanes); }

        // One size at render position fc for the four surfaces' distances z: each of its four nearest texels offers the near and the far draw, weighted
        // bilinearly (plus a floor) over the square of their depth's distance from the surface's. With `one` (the four surfaces within 4 %: no outline) the
        // first only, copied to the others.
        bool layerAt(sampler2D accum, sampler2D lowDepth, sampler2D accumFar, sampler2D lowDepthFar, vec2 lowSize, float div, vec2 fc, float z[4], bool one, out vec4 v[4])
        {
            vec2 lp = fc / div - 0.5;
            vec2 f = lp - floor(lp);
            ivec2 i0 = ivec2(floor(lp));
            ivec2 hi = ivec2(lowSize) - 1;
            vec4 an[4], af[4];
            float zn[4], zf[4], bw[4];
            float total = 0.0;
            for (int k = 0; k < 4; k++)
            {
                ivec2 q = clamp(i0 + ivec2(k & 1, k >> 1), ivec2(0), hi);
                an[k] = texelFetch(accum, q, 0);
                af[k] = texelFetch(accumFar, q, 0);
                total += dot(an[k], vec4(1.0)) + dot(af[k], vec4(1.0));
                zn[k] = linearZ(texelFetch(lowDepth, q, 0).r);
                zf[k] = linearZ(texelFetch(lowDepthFar, q, 0).r);
                bw[k] = ((k & 1) == 0 ? 1.0 - f.x : f.x) * ((k >> 1) == 0 ? 1.0 - f.y : f.y) + 0.05;
            }
            for (int i = 0; i < 4; i++) v[i] = vec4(0.0);
            if (total <= 0.0) return false;
            for (int i = 0; i < 4; i++)
            {
                if (one && i > 0) { v[i] = v[0]; continue; }
                vec4 sum = vec4(0.0);
                float weights = 0.0;
                for (int k = 0; k < 4; k++)
                {
                    float en = 0.02 + abs(zn[k] - z[i]) / z[i], ef = 0.02 + abs(zf[k] - z[i]) / z[i];
                    float wn = bw[k] / (en * en), wf = bw[k] / (ef * ef);
                    sum += an[k] * wn + af[k] * wf;
                    weights += wn + wf;
                }
                v[i] = sum / weights;
            }
            return true;
        }

        // The half and quarter values as one premultiplied layer, the quarter over the half (the order the scene took them in).
        vec4 joined(vec4 h, vec4 q) { return vec4(h.rgb * (1.0 - q.a) + q.rgb, 1.0 - (1.0 - h.a) * (1.0 - q.a)); }

        // The layer for an outline pixel, so that "over" the upscaler's output C gives b (ln over R) + (1 - b) (lf over K): R and K the mean scene colours of
        // the render pixels round it on the near and the far side (z split at the geometric mean of zn and zf), b the near side's share of C (C projected on
        // K -> R; the four's bilinear share where R and K look alike). C's outline pixels mix the near surface with the far one without its dust, and a plain
        // mix of the two layers let that bare far colour through: the sky's colour round every ridge in front of dust.
        vec4 outlineLayer(ivec2 b, ivec2 last, float zn, float zf, float z[4], float bil[4], vec4 ln, vec4 lf)
        {
            float split = sqrt(zn * zf);
            vec3 r = vec3(0.0), k = vec3(0.0);
            float nr = 0.0, nk = 0.0;
            for (int y = -1; y <= 2; y++)
                for (int x = -1; x <= 2; x++)
                {
                    if ((x == -1 || x == 2) && (y == -1 || y == 2)) continue;
                    ivec2 q = clamp(b + ivec2(x, y), ivec2(0), last);
                    vec3 c = texelFetch(uScene, q, 0).rgb;
                    if (linearZ(texelFetch(uDepth, q, 0).r) < split) { r += c; nr += 1.0; }
                    else { k += c; nk += 1.0; }
                }
            r /= max(nr, 1.0);
            k /= max(nk, 1.0);
            vec3 c = texelFetch(uOutput, ivec2(gl_FragCoord.xy), 0).rgb;
            vec3 rk = r - k;
            float share = 0.0;
            for (int i = 0; i < 4; i++) share += z[i] < split ? bil[i] : 0.0;
            float span = dot(rk, rk);
            float beta = span > 1e-4 ? clamp(dot(c - k, rk) / span, 0.0, 1.0) : share;
            vec4 l = mix(lf, ln, beta);
            l.rgb += beta * (1.0 - beta) * (lf.a - ln.a) * rk;
            l.rgb = max(l.rgb, -c * (1.0 - l.a));   // never below black over C
            return l;
        }

        void main()
        {
            vec2 rp = gl_FragCoord.xy * uLate.xy - 0.5 + uLate.zw;
            vec2 b = floor(rp), f = rp - b;
            ivec2 last = textureSize(uDepth, 0) - 1;
            float z[4], bil[4];
            float closest = DEPTH_SKY;
            ivec2 qClosest = ivec2(0);
            for (int i = 0; i < 4; i++)
            {
                ivec2 q = clamp(ivec2(b) + ivec2(i & 1, i >> 1), ivec2(0), last);
                float d = texelFetch(uDepth, q, 0).r;
                z[i] = linearZ(d);
                bil[i] = ((i & 1) == 0 ? 1.0 - f.x : f.x) * ((i >> 1) == 0 ? 1.0 - f.y : f.y);
                if (i == 0 || depthNearerThan(d, closest)) { closest = d; qClosest = q; }
            }
            vec2 fc = rp + 0.5;
            // The nearest and farthest surface of the 3 x 3 render pixels round the four: the filter's range at an outline. From the four alone, a pixel
            // beside an outline whose four all fell on one side (the jitter decides) clamped its history to that side, and the outline kept a staircase.
            float zNear = 1e30, zFar = 0.0;
            for (int y = -1; y <= 2; y++)
                for (int x = -1; x <= 2; x++)
                {
                    if ((x == -1 || x == 2) && (y == -1 || y == 2)) continue;
                    float zz = linearZ(texelFetch(uDepth, clamp(ivec2(b) + ivec2(x, y), ivec2(0), last), 0).r);
                    zNear = min(zNear, zz);
                    zFar = max(zFar, zz);
                }
            bool one = zFar < zNear * 1.04;   // no outline round the pixel: one surface's evaluation serves them all, and the filter keeps this frame's
            vec4 h[4], qv[4];
            bool anyHalf = uHas.x > 0.5 && layerAt(uAccumH, uLowDepthH, uAccumFarH, uLowDepthFarH, uLowSizeH, uHas.x, fc, z, one, h);
            bool anyQuarter = uHas.y > 0.5 && layerAt(uAccumQ, uLowDepthQ, uAccumFarQ, uLowDepthFarQ, uLowSizeQ, uHas.y, fc, z, one, qv);
            if (!anyHalf) for (int i = 0; i < 4; i++) h[i] = vec4(0.0);
            if (!anyQuarter) for (int i = 0; i < 4; i++) qv[i] = vec4(0.0);
            if (!anyHalf && !anyQuarter) { fragColour = vec4(0.0); return; }
            vec4 sum = vec4(0.0), lo = vec4(1e30), hi = vec4(-1e30);
            for (int i = 0; i < 4; i++)
            {
                vec4 l = joined(h[i], qv[i]);
                sum += l * bil[i];
                lo = min(lo, l);
                hi = max(hi, l);
            }
            if (!one)
            {
                // The layer at the neighbourhood's nearest and farthest surfaces: this frame's value, split by the upscaler's own outline, and the range widened by them.
                float ze[4] = float[4](zNear, zFar, zNear, zFar);
                vec4 he[4], qe[4];
                bool eh = uHas.x > 0.5 && layerAt(uAccumH, uLowDepthH, uAccumFarH, uLowDepthFarH, uLowSizeH, uHas.x, fc, ze, false, he);
                bool eq = uHas.y > 0.5 && layerAt(uAccumQ, uLowDepthQ, uAccumFarQ, uLowDepthFarQ, uLowSizeQ, uHas.y, fc, ze, false, qe);
                vec4 ln = joined(eh ? he[0] : vec4(0.0), eq ? qe[0] : vec4(0.0));
                vec4 lf = joined(eh ? he[1] : vec4(0.0), eq ? qe[1] : vec4(0.0));
                sum = outlineLayer(ivec2(b), last, zNear, zFar, z, bil, ln, lf);
                lo = min(min(lo, sum), min(ln, lf));
                hi = max(max(hi, sum), max(ln, lf));
            }
            if (one || uTarget.z < 0.5) { fragColour = sum; return; }   // one surface: the four agree, the filter would keep this frame's

            vec2 prev = gl_FragCoord.xy / uTarget.xy - texelFetch(uMotion, qClosest, 0).rg;
            if (any(lessThan(prev, vec2(0.0))) || any(greaterThan(prev, vec2(1.0)))) { fragColour = sum; return; }
            fragColour = mix(clamp(textureLod(uHistory, prev, 0.0), lo, hi), sum, uTarget.w);
        }
        """;

    /// <summary>
    /// The filtered layer of <see cref="ParticleLate"/> blended over the upscaler's output (premultiplied "over", red, green and blue).
    /// </summary>
    public const string ParticleLateApply = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uLayer;
        void main() { fragColour = texelFetch(uLayer, ivec2(gl_FragCoord.xy), 0); }
        """;

    /// <summary>
    /// The low-resolution particles' accumulations (rgb = colour added, a = opacity; <see cref="ParticleRenderer"/>) over the scene, as <c>scene * (1
    /// - a) + rgb</c> (blend one, one minus source alpha; red, green and blue only), upsampled by depth (<see cref="ParticleUpsample"/>).
    /// </summary>
    public const string ParticleComposite = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform float uScale;   // render pixels per target pixel: 1 over the scene, render / display over the upscaler's output
        uniform int uFootprint; // 1 over the upscaler's output (unjittered): the nearest of the render depth's 2 x 2 round the pixel
        """ + "\n" + ParticleUpsample + "\n" + """
        void main() { fragColour = particlesAt((floor(gl_FragCoord.xy) + 0.5) * uScale, uFootprint != 0); }
        """;

    /// <summary>
    /// The low-resolution particles' coverage for the upscalers' reactive mask: the accumulations' opacity or brightest channel (no gain: rain and
    /// fire, which have <see cref="ParticleRenderer"/>'s, are always full size), upsampled by depth as the composite is so it does not mark a near
    /// edge reactive.
    /// </summary>
    public const string ParticleCoverage = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform vec2 uFullSize;   // the render size (the target may differ)
        uniform float uLimit;
        """ + "\n" + ParticleUpsample + "\n" + """
        void main()
        {
            vec4 a = particlesAt(floor(vUv * uFullSize) + 0.5, false);
            fragColour = vec4(clamp(max(a.a, max(a.r, max(a.g, a.b))), 0.0, uLimit));
        }
        """;

    /// <summary>
    /// The characters' mask at the occlusion's half size (<see cref="Ssao"/>'s <c>uMask</c>): the scene colour's alpha, which only the characters
    /// write, read once per pixel into R8. The occlusion's twelve taps read the scene colour itself before, at scattered places: 0.2 ms of L2 traffic
    /// at 2560 x 1440 (docs/render-post.md).
    /// </summary>
    public const string SsaoMask = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene;
        void main() { fragColour = vec4(textureLod(uScene, vUv, 0.0).a, 0.0, 0.0, 1.0); }
        """;

    /// <summary>
    /// Depth-aware blur (7 taps) of the occlusion, one direction per pass.
    /// </summary>
    public const string SsaoBlur = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uAo, uMask;   // uMask: the scene colour at the render size, whose alpha is 1 on characters (only they write it)
        uniform vec2 uStep;
        uniform float uCharacterAo;     // the share of the occlusion kept on the characters' own pixels (1: no mask)
        void main()
        {
            vec2 c = texture(uAo, vUv).rg;
            float sum = c.r, weights = 1.0;
            const float w[3] = float[3](0.75, 0.45, 0.2);
            for (int i = 1; i <= 3; i++)
            for (int s = -1; s <= 1; s += 2)
            {
                vec2 t = texture(uAo, vUv + uStep * float(i * s)).rg;
                float dw = exp(-abs(t.g - c.g) / max(c.g * 0.02, 0.05));
                sum += t.r * w[i - 1] * dw;
                weights += w[i - 1] * dw;
            }
            float ao = sum / weights;
            if (uCharacterAo < 1.0) ao = mix(ao, 1.0, texture(uMask, vUv).a * (1.0 - uCharacterAo));
            fragColour = vec4(ao, c.g, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The finished occlusion as a factor the scene colour is multiplied by (blend zero, source colour; red, green and blue only).
    /// </summary>
    public const string SsaoApply = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uAo;
        void main()
        {
            fragColour = vec4(vec3(texture(uAo, vUv).r), 1.0);
        }
        """;

    /// <summary>
    /// Kenshi's luminance measure (docs/formats/post-processing.md): Rec. 601 luminance, floored at 0.0001, averaged linearly. One texel of the small
    /// luminance target is the mean of a 4 × 4 grid of bilinear taps over its share of the scene; its mipmaps average the rest.
    /// </summary>
    public const string Luminance = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene;
        uniform vec2 uCell;   // one texel of this target in the scene's 0..1 coordinates
        void main()
        {
            float sum = 0.0;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    vec2 uv = vUv + (vec2(x, y) - 1.5) * 0.25 * uCell;
                    sum += max(dot(texture(uScene, uv).rgb, vec3(0.299, 0.587, 0.114)), 0.0001);
                }
            fragColour = vec4(sum / 16.0, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Kenshi's adaptation: <c>adapted = last + (mean − last) (1 − exp(−dt · rate))</c>, clamped to <c>[MIN_LUMINANCE, MAX_LUMINANCE]</c>; a 1 × 1
    /// target.
    /// </summary>
    public const string Adapt = """
        #version 330 core
        out vec4 fragColour;
        uniform sampler2D uLuminance, uLast;
        uniform float uLevel, uBlend;   // the luminance target's 1 × 1 level; the share of the new mean (1: no smoothing)
        uniform vec2 uBand;             // MIN_LUMINANCE, MAX_LUMINANCE
        void main()
        {
            float mean = textureLod(uLuminance, vec2(0.5), uLevel).r;
            float last = texture(uLast, vec2(0.5)).r;
            float adapted = mix(last, mean, uBlend);
            // G: the same smoothing of the mean without the band (Meitou's night grading reads it; equal to the mean when the blend is 1)
            float smoothMean = mix(texture(uLast, vec2(0.5)).g, mean, uBlend);
            fragColour = vec4(clamp(adapted, uBand.x, max(uBand.x, uBand.y)), smoothMean, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Meitou bloom (docs/render-post.md "Bloom"), the scene to half size: a 4 × 4 box of texels, each decoded from the scene's gamma to linear
    /// before it is averaged (a bilinear tap would average encoded values and lose over half the light of a lone bright texel), so every texel lands
    /// in four destination pixels at 1/16. The firefly ceiling F compresses a texel's colour so its luminance L tends to F: c (1 + (L/F)^4)^-1/4.
    /// </summary>
    public const string BloomPrefilter = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene;
        uniform vec2 uDestSize;
        uniform float uFirefly;   // the ceiling in linear scene units, 0 off
        void main()
        {
            ivec2 p = ivec2(vUv * uDestSize), last = textureSize(uScene, 0) - 1;
            vec3 sum = vec3(0.0);
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++)
                {
                    vec3 c = pow(max(texelFetch(uScene, clamp(2 * p - 1 + ivec2(i, j), ivec2(0), last), 0).rgb, 0.0), vec3(2.2));
                    if (uFirefly > 0.0)
                    {
                        float r = dot(c, vec3(0.2126, 0.7152, 0.0722)) / uFirefly, r2 = r * r;
                        c *= inversesqrt(sqrt(1.0 + r2 * r2));
                    }
                    sum += c;
                }
            fragColour = vec4(sum / 16.0, 1.0);
        }
        """;

    /// <summary>
    /// Meitou bloom, one level down: Jimenez's 13-tap filter (Call of Duty: Advanced Warfare, 2014), five overlapping 2 × 2 boxes of bilinear taps
    /// with the centre one at half the weight; the 6 × 6 texel footprint never skips a source texel. <c>uTexel</c> is the source's texel.
    /// </summary>
    public const string BloomDown = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uSource;
        uniform vec2 uTexel;
        vec3 tap(float x, float y) { return texture(uSource, vUv + vec2(x, y) * uTexel).rgb; }
        void main()
        {
            vec3 a = tap(-2.0, -2.0), b = tap(0.0, -2.0), c = tap(2.0, -2.0);
            vec3 d = tap(-1.0, -1.0), e = tap(1.0, -1.0);
            vec3 f = tap(-2.0, 0.0), g = tap(0.0, 0.0), h = tap(2.0, 0.0);
            vec3 i = tap(-1.0, 1.0), j = tap(1.0, 1.0);
            vec3 k = tap(-2.0, 2.0), l = tap(0.0, 2.0), m = tap(2.0, 2.0);
            vec3 corners = (a + b + g + f) + (b + c + h + g) + (f + g + l + k) + (g + h + m + l);
            fragColour = vec4((d + e + i + j) * 0.125 + corners * 0.03125, 1.0);
        }
        """;

    /// <summary>
    /// Meitou bloom, one level up: this level times its weight plus the coarser sum through a 3 × 3 tent (1 2 1 per axis, a coarse texel apart) times
    /// its scale: 1, or the coarsest level's weight where the coarser image is that level itself.
    /// </summary>
    public const string BloomUp = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uLevel, uCoarse;
        uniform vec4 uParams;   // own weight, coarse scale, the coarse texel
        void main()
        {
            vec3 coarse = vec3(0.0);
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                    coarse += texture(uCoarse, vUv + vec2(i, j) * uParams.zw).rgb * float((2 - abs(i)) * (2 - abs(j)));
            fragColour = vec4(texture(uLevel, vUv).rgb * uParams.x + coarse * (uParams.y / 16.0), 1.0);
        }
        """;

    /// <summary>
    /// Exposure, occlusion, the game's clip at 1 (no tone curve) and dither: HDR scene in, display colour out.
    /// </summary>
    public const string Composite = "#version 330 core\n" + Noise + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene, uAo, uAdapted;   // uAo: the occlusion, the characters' share taken off already (SsaoBlur)
        uniform float uExposure;
        uniform int uUseAo, uDither, uAuto, uTone, uGrade;
        uniform float uToneMix;    // hybrid (uTone 3): 0 the clamp, 1 ACES
        uniform float uHdrPeak;    // HDR output: the highlights' ceiling (over 1); 0 SDR
        uniform float uSaturation, uContrast;
        uniform vec3 uBalance;     // a white balance on the scene before the tone map (PostOptions.Balance; 1, 1, 1 in play)
        uniform float uNight;      // Meitou night grading strength (the `nightgrade` switch): 0 off; needs the auto exposure's mean (uAuto)
        uniform vec2 uSceneSize;   // the scene's size when it is smaller than the picture (render scale without an upscaler), else 0: sampled with Catmull-Rom then
        uniform float uSharpen;    // after TAA or DLSS (neither sharpens): the upscaler sharpness, 0 off
        uniform sampler2D uBloom;  // Meitou bloom: the linear scene convolved with the point spread function, at a quarter of its size
        uniform float uBloomStrength;   // its strength, 0 off
        const float EXPOSURE_KEY = 0.55;   // hdr.material's EXPOSURE_KEY

        // The bloom through a cubic B-spline in four bilinear taps (Sigg and Hadwiger, GPU Gems 2 ch. 20): one bilinear tap magnifies each quarter-size
        // texel into a square around a small light. Positive weights summing to one, so it neither rings nor adds light; the taps are held half a texel
        // inside the edge.
        vec3 bloomAt(vec2 uv)
        {
            vec2 size = vec2(textureSize(uBloom, 0)), coord = uv * size - 0.5, f = fract(coord), i = coord - f, f2 = f * f, f3 = f2 * f;
            vec2 w0 = (-f3 + 3.0 * f2 - 3.0 * f + 1.0) / 6.0, w1 = (3.0 * f3 - 6.0 * f2 + 4.0) / 6.0;
            vec2 w2 = (-3.0 * f3 + 3.0 * f2 + 3.0 * f + 1.0) / 6.0, w3 = f3 / 6.0;
            vec2 g0 = w0 + w1, g1 = w2 + w3, lo = 0.5 / size, hi = 1.0 - lo;
            vec2 p0 = clamp((i - 1.0 + w1 / g0 + 0.5) / size, lo, hi), p1 = clamp((i + 1.0 + w3 / g1 + 0.5) / size, lo, hi);
            return g0.y * (g0.x * texture(uBloom, p0).rgb + g1.x * texture(uBloom, vec2(p1.x, p0.y)).rgb)
                 + g1.y * (g0.x * texture(uBloom, vec2(p0.x, p1.y)).rgb + g1.x * texture(uBloom, p1).rgb);
        }

        // Catmull-Rom through five bilinear taps (the corners of the 4 x 4 footprint are left out): a sharper plain upscale than bilinear.
        vec3 sceneAt(vec2 uv)
        {
            if (uSceneSize.x <= 0.0) return texture(uScene, uv).rgb;
            vec2 pos = uv * uSceneSize, centre = floor(pos - 0.5) + 0.5, f = pos - centre;
            vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f)), w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
            vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f)), w3 = f * f * (-0.5 + 0.5 * f);
            vec2 w12 = w1 + w2, t0 = (centre - 1.0) / uSceneSize, t3 = (centre + 2.0) / uSceneSize, t12 = (centre + w2 / w12) / uSceneSize;
            vec3 sum = texture(uScene, vec2(t12.x, t0.y)).rgb * (w12.x * w0.y) + texture(uScene, vec2(t0.x, t12.y)).rgb * (w0.x * w12.y)
                     + texture(uScene, t12).rgb * (w12.x * w12.y)
                     + texture(uScene, vec2(t3.x, t12.y)).rgb * (w3.x * w12.y) + texture(uScene, vec2(t12.x, t3.y)).rgb * (w12.x * w3.y);
            float weight = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
            return sum / weight;
        }

        // Ours, not the game's (docs/render-post.md "Tone map and grading"): identity up to K on the brightest channel, then an
        // exponential roll-off towards 1 with the same slope at K, hue kept; Narkowicz's ACES fit; and the hybrid, a mix of the clip and ACES.
        vec3 shoulder(vec3 c)
        {
            const float K = 0.8;
            float m = max(c.r, max(c.g, c.b));
            if (m <= K) return c;
            return c * ((K + (1.0 - K) * (1.0 - exp(-(m - K) / (1.0 - K)))) / m);
        }
        // HDR output: the shoulder with a ceiling of uHdrPeak, so the picture matches SDR below K and its highlights keep going.
        vec3 hdrShoulder(vec3 c)
        {
            const float K = 0.8;
            float m = max(c.r, max(c.g, c.b));
            if (m <= K) return c;
            float span = uHdrPeak - K;
            return c * ((K + span * (1.0 - exp(-(m - K) / span))) / m);
        }
        // HDR output: how much the HDR shoulder leaves of a value over what the SDR shoulder leaves of it.
        float hdrGain(float m)
        {
            const float K = 0.8;
            if (m <= K) return 1.0;
            float span = uHdrPeak - K;
            return (K + span * (1.0 - exp(-(m - K) / span))) / (K + (1.0 - K) * (1.0 - exp(-(m - K) / (1.0 - K))));
        }
        vec3 aces(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }

        // Ours, not the game's (docs/render-post.md "Night grading"; the C# mirror is Meitou.Data.World.NightGrade, a test pins the constants): the scene
        // blended towards its rod luminance times a blue tint, by the adaptation (the mean luminance, log smoothstep 0.06..0.3) and less for pixels far
        // brighter than the mean (log smoothstep of the ratio 1.25..3.2: lamps and the moon keep their colour).
        const vec3 ROD = vec3(0.033, 0.765, 0.202), LUM601 = vec3(0.299, 0.587, 0.114);
        const vec3 NIGHT_TINT = vec3(0.6, 0.75, 1.0) / dot(vec3(0.6, 0.75, 1.0), LUM601);
        const float PURKINJE = 0.5;
        vec3 nightGrade(vec3 c, float sceneLum, float mean)
        {
            float adaptation = 1.0 - smoothstep(log(0.06), log(0.3), log(max(mean, 1e-6)));
            float pixel = 1.0 - smoothstep(log(1.25), log(3.2), log(max(sceneLum, 1e-6) / max(mean, 1e-6)));
            float w = uNight * adaptation * pixel;
            float grey = mix(dot(c, LUM601), dot(c, ROD), PURKINJE);
            return mix(c, grey * NIGHT_TINT, w);
        }

        // The temporal resolve's softness taken back: the pixel pushed away from the mean of its four neighbours by the strength, then kept within
        // their and its own range, so edges gain no halo and flat areas no noise beyond what is there (an unsharp mask limited like CAS).
        vec3 sharpened(vec2 uv)
        {
            // Neighbours clamped to the picture: a fetch outside it is undefined (the border pixels repeat their own).
            ivec2 size = textureSize(uScene, 0), last = size - 1, p = clamp(ivec2(uv * vec2(size)), ivec2(0), last);
            vec3 c = texelFetch(uScene, p, 0).rgb;
            vec3 n = texelFetch(uScene, ivec2(p.x, max(p.y - 1, 0)), 0).rgb, s = texelFetch(uScene, ivec2(p.x, min(p.y + 1, last.y)), 0).rgb;
            vec3 w = texelFetch(uScene, ivec2(max(p.x - 1, 0), p.y), 0).rgb, e = texelFetch(uScene, ivec2(min(p.x + 1, last.x), p.y), 0).rgb;
            vec3 lo = min(c, min(min(n, s), min(w, e))), hi = max(c, max(max(n, s), max(w, e)));
            return clamp(c + uSharpen * (c - 0.25 * (n + s + w + e)), lo, hi);
        }

        void main()
        {
            float exposure = uExposure;
            vec2 adapted = uAuto != 0 ? texture(uAdapted, vec2(0.5)).rg : vec2(1.0);   // the clamped adapted luminance, the smoothed mean
            if (uAuto != 0) exposure *= max(EXPOSURE_KEY / adapted.r, 0.001);   // Kenshi's exposure: key over the adapted luminance
            vec3 raw = max(uSharpen > 0.0 && uSceneSize.x <= 0.0 ? sharpened(vUv) : sceneAt(vUv), 0.0);
            // The bloom mixed in, not added: that share of every pixel's light moves into its neighbourhood, so a uniform image stays itself. In linear
            // light, before the exposure (a scale, which commutes with the gamma), then back to the scene's gamma.
            if (uBloomStrength > 0.0) raw = pow(mix(pow(raw, vec3(2.2)), max(bloomAt(vUv), 0.0), uBloomStrength), vec3(1.0 / 2.2));
            vec3 c = raw * exposure * uBalance;
            float ao = texture(uAo, vUv).r;
            if (uUseAo != 0) c *= ao;
            if (uNight > 0.0 && uAuto != 0) c = nightGrade(c, dot(raw, LUM601), adapted.g);
            float top = uHdrPeak > 1.0 ? uHdrPeak : 1.0;
            // HDR output: the look's own curve, its highlights lifted by what the HDR shoulder gives over the SDR one (1 under the knee, so the picture there is the SDR one).
            if (uHdrPeak > 1.0 && uTone == 2) c = aces(c) * hdrGain(max(c.r, max(c.g, c.b)));
            else if (uHdrPeak > 1.0 && uTone == 3) c = mix(clamp(c, 0.0, 1.0), aces(c * 0.7), uToneMix) * hdrGain(max(c.r, max(c.g, c.b)));
            else if (uHdrPeak > 1.0) c = hdrShoulder(c);
            else if (uTone == 1) c = shoulder(c);
            else if (uTone == 2) c = aces(c);
            // hybrid: between the game's clip and ACES, fed 0.7 of the exposure so its mid-tones stay the clip's (0.18 -> 0.17, 0.5 -> 0.49) and only the highlights roll off
            else if (uTone == 3) c = mix(clamp(c, 0.0, 1.0), aces(c * 0.7), uToneMix);
            c = clamp(c, 0.0, top);   // Kenshi has no tone curve: values over 1 clip (docs/formats/post-processing.md)
            if (uGrade != 0)
            {
                c = mix(vec3(dot(c, vec3(0.299, 0.587, 0.114))), c, uSaturation);
                c = clamp((c - 0.5) * uContrast + 0.5, 0.0, top);
            }
            if (uDither != 0) c += (ign(gl_FragCoord.xy) + ign(gl_FragCoord.xy + 17.0) - 1.0) / 255.0;
            fragColour = vec4(c, 1.0);
        }
        """;

    /// <summary>
    /// FXAA on the final LDR picture, with the game's settings (docs/formats/post-processing.md "FXAA"): Lottes' FXAA 3.11 quality algorithm, green
    /// as luma, sub-pixel amount 0.75, edge threshold 0.166, minimum 0.0833, the end-of-edge search in the steps of quality preset 12 (1, 1.5, 2, 4,
    /// 12 pixels). Finds the local contrast, the edge's direction and which side it runs along, searches both ways for the edge's ends, and moves the
    /// lookup across the edge by how far the pixel is from the nearer end, or by the sub-pixel blend for lone pixels, whichever is larger.
    /// </summary>
    public const string Fxaa = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uImage;
        uniform vec2 uTexel;
        const float SUBPIX = 0.75, EDGE_THRESHOLD = 0.166, EDGE_THRESHOLD_MIN = 0.0833;
        const int STEPS = 5;
        const float STEP_SIZE[STEPS] = float[](1.0, 1.5, 2.0, 4.0, 12.0);

        float luma(vec2 p) { return textureLod(uImage, p, 0.0).g; }
        float lumaAt(vec2 p, vec2 o) { return luma(p + o * uTexel); }

        void main()
        {
            vec2 pos = vUv;
            vec4 centre = textureLod(uImage, pos, 0.0);
            float m = centre.g;
            float n = lumaAt(pos, vec2(0.0, -1.0)), s = lumaAt(pos, vec2(0.0, 1.0));
            float w = lumaAt(pos, vec2(-1.0, 0.0)), e = lumaAt(pos, vec2(1.0, 0.0));
            float hi = max(max(max(n, s), max(w, e)), m), lo = min(min(min(n, s), min(w, e)), m);
            float range = hi - lo;
            if (range < max(EDGE_THRESHOLD_MIN, hi * EDGE_THRESHOLD)) { fragColour = centre; return; }

            float nw = lumaAt(pos, vec2(-1.0, -1.0)), ne = lumaAt(pos, vec2(1.0, -1.0));
            float sw = lumaAt(pos, vec2(-1.0, 1.0)), se = lumaAt(pos, vec2(1.0, 1.0));

            // Direction: second differences across rows (a horizontal edge) against across columns (a vertical one).
            float horizontal = abs(nw - 2.0 * w + sw) + 2.0 * abs(n - 2.0 * m + s) + abs(ne - 2.0 * e + se);
            float vertical = abs(nw - 2.0 * n + ne) + 2.0 * abs(w - 2.0 * m + e) + abs(sw - 2.0 * s + se);
            bool horizontalSpan = horizontal >= vertical;

            // The neighbour across the edge with the larger gradient decides which side the edge lies on.
            float a = horizontalSpan ? n : w, b = horizontalSpan ? s : e;
            float gradientA = abs(a - m), gradientB = abs(b - m);
            float stepAcross = horizontalSpan ? uTexel.y : uTexel.x;
            bool sideA = gradientA >= gradientB;
            if (sideA) stepAcross = -stepAcross;
            float edgeLuma = 0.5 * ((sideA ? a : b) + m);
            float gradient = 0.25 * max(gradientA, gradientB);

            // Walk along the edge, half a pixel over onto it, until the luma leaves the edge's average on either end.
            vec2 along = horizontalSpan ? vec2(uTexel.x, 0.0) : vec2(0.0, uTexel.y);
            vec2 onEdge = pos + (horizontalSpan ? vec2(0.0, 0.5 * stepAcross) : vec2(0.5 * stepAcross, 0.0));
            vec2 posN = onEdge - along * STEP_SIZE[0], posP = onEdge + along * STEP_SIZE[0];
            float endN = luma(posN) - edgeLuma, endP = luma(posP) - edgeLuma;
            bool doneN = abs(endN) >= gradient, doneP = abs(endP) >= gradient;
            for (int i = 1; i < STEPS && !(doneN && doneP); i++)
            {
                if (!doneN) { posN -= along * STEP_SIZE[i]; endN = luma(posN) - edgeLuma; doneN = abs(endN) >= gradient; }
                if (!doneP) { posP += along * STEP_SIZE[i]; endP = luma(posP) - edgeLuma; doneP = abs(endP) >= gradient; }
            }
            float distN = horizontalSpan ? pos.x - posN.x : pos.y - posN.y;
            float distP = horizontalSpan ? posP.x - pos.x : posP.y - pos.y;
            bool nearerN = distN < distP;
            float nearest = min(distN, distP);
            // Only blend when the nearer end turns the other way from the centre (the pixel lies on the edge's stair).
            bool centreBelow = m - edgeLuma < 0.0;
            bool goodSpan = ((nearerN ? endN : endP) < 0.0) != centreBelow;
            float edgeOffset = goodSpan ? 0.5 - nearest / (distN + distP) : 0.0;

            // Sub-pixel aliasing: how far the centre is from its 3 × 3 neighbourhood's weighted mean, relative to the range.
            float mean = (2.0 * (n + s + w + e) + (nw + ne + sw + se)) / 12.0;
            float t = clamp(abs(mean - m) / range, 0.0, 1.0);
            float subpix = (-2.0 * t + 3.0) * t * t;
            float subpixOffset = subpix * subpix * SUBPIX;

            float offset = max(edgeOffset, subpixOffset);
            vec2 p = pos + (horizontalSpan ? vec2(0.0, offset * stepAcross) : vec2(offset * stepAcross, 0.0));
            fragColour = vec4(textureLod(uImage, p, 0.0).rgb, 1.0);
        }
        """;

    /// <summary>
    /// SMAA (Jimenez et al., "SMAA: Enhanced Subpixel Morphological Antialiasing") on the final LDR picture, Meitou's alternative to the game's FXAA
    /// (docs/render-post.md "SMAA"), pass 1 of 3: luma edges. R marks a pixel's left edge, G its top edge, where the luma step to that neighbour
    /// passes the threshold and is at least half of the largest step round it (SMAA's local contrast adaptation).
    /// </summary>
    public const string SmaaEdges = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uImage;
        const float THRESHOLD = 0.1, CONTRAST_ADAPTATION = 2.0;
        ivec2 size;

        float luma(ivec2 p) { return dot(texelFetch(uImage, clamp(p, ivec2(0), size - 1), 0).rgb, vec3(0.2126, 0.7152, 0.0722)); }

        void main()
        {
            size = textureSize(uImage, 0);
            ivec2 p = ivec2(vUv * vec2(size));
            float l = luma(p), left = luma(p + ivec2(-1, 0)), top = luma(p + ivec2(0, -1));
            vec2 delta = abs(l - vec2(left, top));
            vec2 edges = step(THRESHOLD, delta);
            if (edges.x + edges.y == 0.0) { fragColour = vec4(0.0); return; }
            vec2 maxDelta = max(delta, abs(l - vec2(luma(p + ivec2(1, 0)), luma(p + ivec2(0, 1)))));
            maxDelta = max(maxDelta, abs(vec2(left, top) - vec2(luma(p + ivec2(-2, 0)), luma(p + ivec2(0, -2)))));
            edges *= step(max(maxDelta.x, maxDelta.y), CONTRAST_ADAPTATION * delta);
            fragColour = vec4(edges, 0.0, 0.0);
        }
        """;

    /// <summary>
    /// SMAA pass 2: blending weights, orthogonal patterns (as SMAA's low and medium presets: no diagonal or corner patterns). For each edge of a
    /// pixel the run of that edge is searched both ways (up to 16 pixels) until it ends or a crossing edge breaks it; the crossing edges at the two
    /// ends give the shape (L, U or Z), revectorized as SMAA's area texture is (lines through the ends' half-pixel steps, U shapes smoothed when
    /// short), with the areas computed exactly here instead of from the lookup textures. The area under the line on the pixel's own side is how much
    /// it takes of its neighbour, the area on the other side how much the neighbour takes of it: R / G for the top edge (this pixel, the one above),
    /// B / A for the left edge (this pixel, the one to the left).
    /// </summary>
    public const string SmaaWeights = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uEdges;
        const int MAX_SEARCH = 16;
        const float SMOOTH_DISTANCE = 32.0;
        ivec2 size;

        vec2 edge(ivec2 p) { return any(lessThan(p, ivec2(0))) || any(greaterThanEqual(p, size)) ? vec2(0.0) : texelFetch(uEdges, p, 0).rg; }

        // The crossing edges (channel 1 - c) at the boundary stored in b: bit 0 on the run's own row / column, bit 1 on the neighbour's (b + n).
        int crossing(ivec2 b, ivec2 n, int c) { return int(edge(b)[1 - c]) | (int(edge(b + n)[1 - c]) << 1); }

        // The run of the edge in channel c (0 left, 1 top) from p back along -u: the pixels it goes on for, and the crossing edges at its end.
        ivec2 searchBack(ivec2 p, ivec2 u, ivec2 n, int c)
        {
            int k = 0;
            for (; k < MAX_SEARCH; k++)
            {
                ivec2 b = p - k * u;
                if (crossing(b, n, c) != 0 || edge(b - u)[c] == 0.0) break;
            }
            return ivec2(k, crossing(p - k * u, n, c));
        }

        // The same forward along +u: the boundary past pixel p + k u is stored in the next pixel.
        ivec2 searchForward(ivec2 p, ivec2 u, ivec2 n, int c)
        {
            int k = 0;
            for (; k < MAX_SEARCH; k++)
            {
                ivec2 b = p + (k + 1) * u;
                if (crossing(b, n, c) != 0 || edge(b)[c] == 0.0) break;
            }
            return ivec2(k, crossing(p + (k + 1) * u, n, c));
        }

        // The area between the line p1-p2 and the edge (y = 0) over the pixel [x, x + 1], within the line's span: x where the line runs on the
        // pixel's own side (y < 0), y where it runs on the neighbour's.
        vec2 lineArea(vec2 p1, vec2 p2, float x)
        {
            float a = max(x, p1.x), b = min(x + 1.0, p2.x);
            if (b <= a) return vec2(0.0);
            float slope = (p2.y - p1.y) / (p2.x - p1.x);
            float ya = p1.y + slope * (a - p1.x), yb = p1.y + slope * (b - p1.x);
            vec2 s = vec2(0.5 * (ya + yb) * (b - a), 0.0);
            if (ya * yb < 0.0)
            {
                float z = a + (b - a) * ya / (ya - yb);   // where the line crosses the edge: a triangle on each side
                s = vec2(0.5 * ya * (z - a), 0.5 * yb * (b - z));
            }
            return vec2(max(-s.x, 0.0) + max(-s.y, 0.0), max(s.x, 0.0) + max(s.y, 0.0));
        }

        vec2 smoothArea(float d, vec2 a) { return mix(sqrt(2.0 * a) * 0.5, a, clamp(d / SMOOTH_DISTANCE, 0.0, 1.0)); }

        // The pixel `near` pixels from the run's start and `far` from its end, with crossing codes e1 / e2 at the start / end (1 own side, 2 the
        // neighbour's, 3 both): an end with one crossing edge pulls the line half a pixel to that side, an end with none or both leaves it on the edge.
        vec2 area(int e1, int e2, float near, float far)
        {
            float d = near + far + 1.0, h = 0.5 * d;
            float y1 = e1 == 1 ? -0.5 : 0.5, y2 = e2 == 1 ? -0.5 : 0.5;
            bool one1 = e1 == 1 || e1 == 2, one2 = e2 == 1 || e2 == 2;
            if (one1 && one2 && e1 == e2)   // U: the halves of a step out and back
                return smoothArea(d, lineArea(vec2(0.0, y1), vec2(h, 0.0), near)) + smoothArea(d, lineArea(vec2(h, 0.0), vec2(d, y2), near));
            if (one1 && one2) return lineArea(vec2(0.0, y1), vec2(d, y2), near);   // Z
            if (one1 && e2 == 3) return lineArea(vec2(0.0, y1), vec2(d, -y1), near);   // Z, the end with both taken as the other side
            if (e1 == 3 && one2) return lineArea(vec2(0.0, -y2), vec2(d, y2), near);
            if (one1 && e2 == 0) return lineArea(vec2(0.0, y1), vec2(h, 0.0), near);   // L: only the half nearer the step
            if (e1 == 0 && one2) return lineArea(vec2(h, 0.0), vec2(d, y2), near);
            return vec2(0.0);
        }

        void main()
        {
            size = textureSize(uEdges, 0);
            ivec2 p = ivec2(vUv * vec2(size));
            vec2 e = edge(p);
            vec4 w = vec4(0.0);
            if (e.g > 0.0)
            {
                ivec2 a = searchBack(p, ivec2(1, 0), ivec2(0, -1), 1), b = searchForward(p, ivec2(1, 0), ivec2(0, -1), 1);
                w.rg = area(a.y, b.y, float(a.x), float(b.x));
            }
            if (e.r > 0.0)
            {
                ivec2 a = searchBack(p, ivec2(0, 1), ivec2(-1, 0), 0), b = searchForward(p, ivec2(0, 1), ivec2(-1, 0), 0);
                w.ba = area(a.y, b.y, float(a.x), float(b.x));
            }
            fragColour = w;
        }
        """;

    /// <summary>
    /// SMAA pass 3: neighbourhood blending. A pixel's four weights (taking of the right, left, lower and upper neighbour: from the weights of the
    /// pixel to the right and below and its own) pick the axis with the larger one; it is blended with both neighbours on that axis by bilinear
    /// lookups moved that far towards each, weighted by the two.
    /// </summary>
    public const string SmaaBlend = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uImage, uWeights;

        void main()
        {
            ivec2 size = textureSize(uWeights, 0);
            ivec2 p = ivec2(vUv * vec2(size));
            vec2 texel = 1.0 / vec2(size);
            vec4 own = texelFetch(uWeights, p, 0);
            float right = p.x + 1 < size.x ? texelFetch(uWeights, p + ivec2(1, 0), 0).a : 0.0;
            float below = p.y + 1 < size.y ? texelFetch(uWeights, p + ivec2(0, 1), 0).g : 0.0;
            vec4 a = vec4(right, own.b, below, own.r);   // towards the right, left, lower, upper neighbour
            if (dot(a, vec4(1.0)) < 1e-5) { fragColour = vec4(texelFetch(uImage, p, 0).rgb, 1.0); return; }
            bool horizontal = max(a.x, a.y) > max(a.z, a.w);
            vec2 w = horizontal ? a.xy : a.zw;
            vec2 o1 = horizontal ? vec2(a.x * texel.x, 0.0) : vec2(0.0, a.z * texel.y);
            vec2 o2 = horizontal ? vec2(-a.y * texel.x, 0.0) : vec2(0.0, -a.w * texel.y);
            vec2 uv = (vec2(p) + 0.5) * texel;
            vec3 c = w.x * textureLod(uImage, uv + o1, 0.0).rgb + w.y * textureLod(uImage, uv + o2, 0.0).rgb;
            fragColour = vec4(c / (w.x + w.y), 1.0);
        }
        """;

    /// <summary>
    /// The game's heat haze on the final LDR picture (docs/formats/post-processing.md "Heat haze"), written from the facts there: a flow map (at
    /// 3.341 × the screen) gives a scroll direction; three layers of a normal map (at 7.341 × the screen, offset by (0.1, 0.3) and (0.4, 0.7)) scroll
    /// along it over phases <c>fract(gameTime · 100 + 0, 0.33, 0.66)</c>, each weighted by a triangle that is 0 at the ends of its cycle; their
    /// summed red and green make a direction only (normalised), moved by 0.002 · heatHaze · saturate(6 · distance / farClip) in screen units. The
    /// picture is the mean of the taps at 1 and 0.7 of that offset. GpuTexture lookups run in the game's screen coordinates (v from the top), the
    /// offset is turned back into ours.
    /// </summary>
    public const string HeatHaze = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uFlow, uPerturbation, uDepth, uImage;
        uniform float uPhase;     // gameTime (game hours since the load) × 100
        uniform float uAmount;    // heatHaze
        uniform vec2 uTan;        // tan(fov/2) * aspect, tan(fov/2)
        uniform vec2 uNearFar;    // of the scene depth
        uniform float uFarClip;   // the game's far clip D: its G-buffer depth is distance / D
        uniform int uHasDepth;    // 0: no scene depth was drawn, everything counts as far
        """ + DepthConvention.Glsl + """

        vec2 layer(vec2 uv, vec2 direction, float phase)
        {
            float t = fract(phase);
            vec2 n = texture(uPerturbation, uv + direction * t).rg * 2.0 - 1.0;
            return n * (1.0 - abs(t * 2.0 - 1.0));
        }

        void main()
        {
            vec2 g = vec2(vUv.x, 1.0 - vUv.y);
            vec2 direction = texture(uFlow, g * 3.341).rg * 2.0 - 1.0;
            vec2 p = g * 7.341;
            vec2 s = layer(p, direction, uPhase) + layer(p + vec2(0.1, 0.3), direction, uPhase + 0.33)
                   + layer(p + vec2(0.4, 0.7), direction, uPhase + 0.66);
            float len = length(s);
            vec2 dir = len > 1e-6 ? s / len : vec2(0.0);   // the game normalises a possibly zero vector; here it stays still

            // The game's depth is the distance from the eye over its far clip, and "nothing" counts as 1.
            float d = textureLod(uDepth, vUv, 0.0).r;
            float amplitude = 1.0;
            if (uHasDepth != 0 && !depthIsSky(d))
            {
                float z = depthViewZ(d, uNearFar);
                float distance = z * length(vec3((vUv * 2.0 - 1.0) * uTan, 1.0));
                amplitude = clamp(6.0 * distance / uFarClip, 0.0, 1.0);
            }
            vec2 offset = dir * (amplitude * 0.002 * uAmount);
            offset.y = -offset.y;
            vec3 c = mix(texture(uImage, vUv + offset).rgb, texture(uImage, vUv + offset * 0.7).rgb, 0.5);
            fragColour = vec4(c, 1.0);
        }
        """;
}