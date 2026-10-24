namespace Meitou.Rendering;

/// <summary>GLSL for the world view's post-processing chain (written from the public techniques, not from Kenshi's shaders).</summary>
static class PostProcessShaders
{
    /// <summary>Full-screen triangle; <c>vUv</c> runs 0..1 over the screen.</summary>
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

    /// <summary>Interleaved gradient noise (Jimenez): a cheap per-pixel value in 0..1.</summary>
    const string Noise = """
        float ign(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }
        """;

    /// <summary>
    /// Screen-space ambient occlusion at half resolution, from depth alone (normals from depth differences). Alchemy-style:
    /// each of 12 spiral taps adds the cosine of the sample direction against the normal over its distance, ignoring
    /// samples further than twice the radius. Writes R = occlusion factor (1 = open), G = view depth for the blur.  The occlusion is scaled by how much of the
    /// surface is still visible through the air in front of it (haze, weather fog, fog volumes: the world position is rebuilt from depth and the
    /// frame globals' own <c>atmoApply</c> evaluated), <c>ao = 1 - (1 - ao) * visibility</c>.
    /// </summary>
    public static readonly string Ssao = "#version 330 core\n" + Noise + AtmosphereShaders.Functions + FogVolumeShaders.Functions + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uDepth;
        uniform sampler2D uMask;            // the scene colour at the render size, whose alpha is 1 on characters (unbound with uCharacterOccluder 1)
        uniform float uCharacterOccluder;   // how much a character counts as an occluder of what is round it (1: as anything else)
        uniform vec2 uTan;      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec2 uNearFar;  // of the near depth slice
        uniform vec2 uSize;     // full-resolution size
        uniform float uRadius, uStrength, uFadeStart, uFadeEnd;
        uniform vec3 uRight, uUp, uBack;   // the camera's axes in the world (view space to world offsets)
        uniform vec3 uSsaoEye;

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

        float viewZ(float d)
        {
            float n = 2.0 * d - 1.0;
            return 2.0 * uNearFar.x * uNearFar.y / (uNearFar.y + uNearFar.x - n * (uNearFar.y - uNearFar.x));
        }
        // A depth texel's own position: its depth with the screen position of its centre. The occlusion runs at half size, so its pixels' centres fall on
        // the corners between depth texels; reading the depth there and placing it at the unsnapped position let rounding pick the texel row by row,
        // which tilted the rebuilt normal on whole rows and banded flat ground seen at an angle.
        vec3 viewPosAt(ivec2 t)
        {
            t = clamp(t, ivec2(0), ivec2(uSize) - 1);
            float z = viewZ(texelFetch(uDepth, t, 0).r);
            vec2 uv = (vec2(t) + 0.5) / uSize;
            return vec3((uv * 2.0 - 1.0) * uTan * z, -z);
        }
        vec3 viewPos(vec2 uv) { return viewPosAt(ivec2(floor(uv * uSize))); }
        void main()
        {
            ivec2 c = ivec2(floor(vUv * uSize));
            float d = texelFetch(uDepth, c, 0).r;
            if (d >= 0.99999) { fragColour = vec4(1.0, 60000.0, 0.0, 1.0); return; }
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
            if (ao < 0.999) ao = mix(1.0, ao, airVisibility(p.x * uRight + p.y * uUp + p.z * uBack));   // 1 - (1 - ao) * visibility
            fragColour = vec4(ao, z, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The placed fog volumes (docs/formats/fogfeatures.md), one pass over the whole scene instead of a term in every world shader. The pixel's
    /// world offset is rebuilt from the depth as SSAO does (the near slice's, else the far slice's; none: the sky, at the far clip D), pulled in to
    /// the water plane where that lies in front (the water writes no depth; its own surface is what the game's G-buffer reads, and the water
    /// shader used to fog it there). The shader accumulates every volume, farthest first, into (colour, transmittance), which the hardware blends
    /// as <c>scene * transmittance + colour</c> (blend one, source alpha; alpha of the scene untouched).
    /// </summary>
    public static readonly string FogVolumes = "#version 330 core\n" + AtmosphereShaders.Functions + LightShaftShaders.FogVolumeShadow + FogVolumeShaders.Functions + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth, uFarDepth;
        uniform vec2 uNearPlanes, uFarPlanes;   // near, far of each depth slice
        uniform vec2 uTan;                      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec3 uRight, uUp, uBack;        // the camera's axes in the world
        uniform float uWaterY;                  // the water plane's height (float min: no water)
        uniform int uHasFar;

        // Distance along the view axis from a depth-buffer value (clip depth 0..1 stored as 0.5..1, as the velocity pass reads it).
        float viewZ(float d, vec2 nf) { float zd = 2.0 * d - 1.0; return nf.x * nf.y / (nf.y - zd * (nf.y - nf.x)); }

        void main()
        {
            float d = textureLod(uNearDepth, vUv, 0.0).r;
            vec2 planes = uNearPlanes;
            if (d >= 1.0 && uHasFar != 0)
            {
                d = textureLod(uFarDepth, vUv, 0.0).r;
                planes = uFarPlanes;
            }
            vec2 ndc = vUv * 2.0 - 1.0;
            vec3 perZ = uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack;   // the offset per unit of view depth
            float len = length(perZ);
            vec3 dir = perZ / len;
            vec3 eye = uFogVolumeEye.xyz;
            float dist = d >= 1.0 ? 1e9 : viewZ(d, planes) * len;   // the sky: the volumes cap it at the far clip D
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
    /// The pixel's ray and distance as the fog volumes and light shafts passes rebuild them, for the passes of <see cref="PostProcess"/>'s merged scene
    /// layers: the near slice's depth, else the far slice's, else the sky (1e9); pulled in to the water plane where that lies in front (sky false
    /// then). The camera comes from the light shafts' uniforms (<see cref="LightShaftShaders.Common"/>, set whether the shafts run or not).
    /// </summary>
    const string SceneDistance = """

        uniform sampler2D uNearDepth, uFarDepth;
        uniform vec2 uNearPlanes, uFarPlanes;   // near, far of each depth slice
        uniform float uWaterY;                  // the water plane's height (float min: no water)
        uniform int uHasFar;
        uniform vec2 uFullSize;                 // the render size in pixels

        float viewZ(float d, vec2 nf) { float zd = 2.0 * d - 1.0; return nf.x * nf.y / (nf.y - zd * (nf.y - nf.x)); }

        float sceneDistance(ivec2 p, out vec3 dir, out bool sky, out float waterWeight)
        {
            float d = texelFetch(uNearDepth, p, 0).r;
            vec2 planes = uNearPlanes;
            if (d >= 1.0 && uHasFar != 0)
            {
                d = texelFetch(uFarDepth, p, 0).r;
                planes = uFarPlanes;
            }
            vec2 ndc = (vec2(p) + 0.5) / uFullSize * 2.0 - 1.0;
            vec3 perZ = uShaftRight * ndc.x * uShaftTan.x + uShaftUp * ndc.y * uShaftTan.y - uShaftBack;   // the offset per unit of view depth
            float len = length(perZ);
            dir = perZ / len;
            sky = d >= 1.0;
            waterWeight = 1.0;
            float dist = sky ? 1e9 : viewZ(d, planes) * len;
            if (uWaterY > -1e30 && abs(dir.y) > 1e-6)
            {
                float tw = (uWaterY - uShaftEye.y) / dir.y;
                // The water's analytic plane must stop at the same far clip as its rendered mesh.
                float waterFar = (uHasFar != 0 ? uFarPlanes.y : uNearPlanes.y) * len;
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
    /// The scene layers at half the render size (<c>fog-half</c> for the fog volumes, <c>shafts-half</c> for the light shafts' apply; docs/render-post.md
    /// "Merged scene layers"): each texel is the full-size pixel at twice its coordinates (not an average: the upsample in <see cref="SceneLayers"/> compares
    /// against that pixel's distance). It holds the layers asked for in the order the merged pass applies them, the shafts' then the fog volumes', as one
    /// (colour, factor) the scene is blended by, <c>scene * a + rgb</c>; without blending. A fog-only texel is the fog's (colour, transmittance).
    /// </summary>
    public static readonly string LayersHalf = "#version 330 core\n" + AtmosphereShaders.Functions + LightShaftShaders.FogVolumeShadow + FogVolumeShaders.Functions
        + LightShaftShaders.Alpha + LightShaftShaders.Air + LightShaftShaders.ApplyFunctions + SceneDistance + """

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
    /// The depth-aware upsample of <see cref="LayersHalf"/> (needs <see cref="SceneDistance"/> before it): of the four nearest half-size texels, weighted
    /// bilinearly and by how close the distance of the pixel each stands for is to this pixel's (relative difference r, weight e^(−20 r)). False where no
    /// texel is within 10 % (a thin edge no texel lies on: twigs against the far fog).
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
    /// The merged scene layers (<c>layer-merge</c>, docs/render-post.md "Merged scene layers"): the light shafts' apply, the fog volumes and the SSAO's
    /// factor in one pass over the scene colour instead of three. Each is a (colour, factor) the scene is blended by as <c>scene * a + rgb</c>; in their
    /// old order (shafts, fog, occlusion) they compose to <c>((scene * a1 + c1) * a2 + c2) * ao</c> = <c>scene * (a1 a2 ao) + (c1 a2 + c2) ao</c>, one
    /// blend of the same state (the occlusion is grey). The shafts' and the fog are computed here, or upsampled from <see cref="LayersHalf"/> (uHalf: which
    /// of them that texture holds, <see cref="HalfUpsample"/>); where the upsample finds no texel, computed here at full size. With uHalf 0 (and the
    /// <c>layers-lean</c> split, <see cref="SceneLayersLean"/>) it is the pass for the pixels the lean one left out.
    /// </summary>
    public static readonly string SceneLayers = "#version 330 core\n" + AtmosphereShaders.Functions + LightShaftShaders.FogVolumeShadow + FogVolumeShaders.Functions
        + LightShaftShaders.Alpha + LightShaftShaders.Air + LightShaftShaders.ApplyFunctions + SceneDistance + HalfUpsample + """

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
    /// The lean half of the merged scene layers (<c>layers-lean</c>, docs/render-post.md "Merged scene layers"), for when <see cref="LayersHalf"/> holds
    /// every pending layer: the upsample (<see cref="HalfUpsample"/>) and the occlusion, nothing else. Where no texel fits (a few hundredths of a percent
    /// of the pixels) it leaves the scene as it is and writes depth 1 into the mask depth buffer, depth 0 elsewhere; <see cref="SceneLayers"/> then
    /// draws those pixels alone (its full-screen triangle, at depth 0.5 in GL clip space, passes "less" only against the 1), computing everything at full size. The full pass carries the shafts' and the fog volumes' code,
    /// which in one shader with the upsample more than doubled the time of the pass (registers: 0.08 against 0.17 ms with the fallback code removed).
    /// </summary>
    public static readonly string SceneLayersLean = "#version 330 core\n" + LightShaftShaders.CommonUniforms + AtmosphereShaders.WaterHorizonFunctions + SceneDistance + HalfUpsample + """

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
    /// Fog shading rate, first pass (<c>fog-vrs</c>, docs/render-post.md "Fog shading rate"): at a quarter of the render size, the distance to the nearest surface of each 4 x 4 block of pixels (red) and to the farthest (green: 1e9 where the block shows sky),
    /// rebuilt from the scene depth as the fog pass does (the near slice's depth, else the far slice's, else the sky at 1e9; where the water plane lies in front, the water's).
    /// </summary>
    public static readonly string FogRateDistance = "#version 330 core\n" + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth, uFarDepth;
        uniform vec2 uNearPlanes, uFarPlanes;   // near, far of each depth slice
        uniform vec2 uTan;                      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec3 uRight, uUp, uBack;        // the camera's axes in the world
        uniform vec3 uEye;
        uniform vec2 uSize;                     // the render size in pixels
        uniform float uWaterY;                  // the water plane's height (float min: no water)
        uniform int uHasFar;

        float viewZ(float d, vec2 nf) { float zd = 2.0 * d - 1.0; return nf.x * nf.y / (nf.y - zd * (nf.y - nf.x)); }

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
                    vec2 planes = uNearPlanes;
                    if (d >= 1.0 && uHasFar != 0)
                    {
                        d = texelFetch(uFarDepth, p, 0).r;
                        planes = uFarPlanes;
                    }
                    vec2 ndc = (vec2(p) + 0.5) / uSize * 2.0 - 1.0;
                    vec3 perZ = uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack;
                    float len = length(perZ);
                    float dist = d >= 1.0 ? 1e9 : viewZ(d, planes) * len;
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
    /// Fog shading rate, second pass: one texel per shading rate tile, the code of the fragment size for the next frame's scene passes. The nearest distance over the tile and a margin of
    /// <c>FogShadingRate.MarginTexels</c> blocks round it (the camera moves between this frame's depth and the frame it is used in) goes through the placed fog volumes
    /// (<c>fogVolumesAccumulate</c>, along the tile centre's ray) and the Kenshi haze and weather fog the world shaders apply; where what is left of the surface
    /// (1 - haze) * transmittance is under <c>uThreshold</c> the rate is 2 x 2 and 4 x 4 (the codes of <c>FogShadingRate.Encode</c>).
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
    /// The low-resolution particles' depth (Meitou): each texel is the nearest (texels with x + y even) or the farthest (odd) of the <c>uDiv</c> x <c>uDiv</c>
    /// block of the scene's depth it covers, written as the depth of a depth-only rendering, so the hardware test of the particle draws still rejects the
    /// sprite area hidden behind the scene (early-Z). Every 2 x 2 footprint of the upsample holds two of each, so a pixel on a thin near object (a character's
    /// limb) finds texels tested against it and a pixel of the background behind it finds texels tested against the background.
    /// </summary>
    public const string ParticleDepth = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uDepth;
        uniform vec2 uFullSize;
        uniform float uDiv;
        void main()
        {
            vec2 base = floor(gl_FragCoord.xy) * uDiv;
            float near = 1.0, far = 0.0;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    if (float(x) < uDiv && float(y) < uDiv)
                    {
                        float d = texelFetch(uDepth, ivec2(min(base + vec2(x, y), uFullSize - 1.0)), 0).r;
                        near = min(near, d);
                        far = max(far, d);
                    }
            ivec2 t = ivec2(gl_FragCoord.xy);
            gl_FragDepth = ((t.x + t.y) & 1) == 0 ? near : far;
            fragColour = vec4(0.0);
        }
        """;

    /// <summary>
    /// The depth-aware upsample of the low-resolution particles' accumulation, shared by <see cref="ParticleComposite"/> and <see cref="ParticleCoverage"/>:
    /// the four nearest texels with their bilinear weights (plus a floor) divided by the square of how far each one's depth is from this pixel's (relative, in
    /// linear depth), so the texels tested against this pixel's own surface (<see cref="ParticleDepth"/>) win and a sprite behind a near edge does not bleed
    /// over it. Pixels whose four texels are empty skip the depth reads.
    /// </summary>
    const string ParticleUpsample = """
        uniform sampler2D uAccum, uLowDepth, uDepth;
        uniform vec2 uLowSize;
        uniform vec2 uNearPlanes;   // near, far of the depth slice the particles were tested against
        uniform float uDiv;
        float linearZ(float d) { float zd = 2.0 * d - 1.0; return uNearPlanes.x * uNearPlanes.y / (uNearPlanes.y - zd * (uNearPlanes.y - uNearPlanes.x)); }
        vec4 particlesAt(vec2 fc)
        {
            vec2 lp = fc / uDiv - 0.5;
            vec2 f = lp - floor(lp);
            ivec2 i0 = ivec2(floor(lp));
            ivec2 hi = ivec2(uLowSize) - 1;
            ivec2 q[4];
            vec4 a[4];
            float total = 0.0;
            for (int i = 0; i < 4; i++)
            {
                q[i] = clamp(i0 + ivec2(i & 1, i >> 1), ivec2(0), hi);
                a[i] = texelFetch(uAccum, q[i], 0);
                total += a[i].a + a[i].r + a[i].g + a[i].b;
            }
            if (total <= 0.0) return vec4(0.0);
            float z0 = linearZ(texelFetch(uDepth, ivec2(fc), 0).r);
            vec4 sum = vec4(0.0);
            float weights = 0.0;
            for (int i = 0; i < 4; i++)
            {
                float bw = ((i & 1) == 0 ? 1.0 - f.x : f.x) * ((i >> 1) == 0 ? 1.0 - f.y : f.y);
                float e = 0.02 + abs(linearZ(texelFetch(uLowDepth, q[i], 0).r) - z0) / z0;
                float w = (bw + 0.05) / (e * e);
                sum += a[i] * w;
                weights += w;
            }
            return sum / weights;
        }
        """;

    /// <summary>
    /// The low-resolution particles' accumulation (rgb = colour added, a = opacity; <see cref="ParticleRenderer"/>) over the scene, as <c>scene * (1 - a) + rgb</c>
    /// (blend one, one minus source alpha; red, green and blue only), upsampled by depth (<see cref="ParticleUpsample"/>).
    /// </summary>
    public const string ParticleComposite = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        """ + "\n" + ParticleUpsample + "\n" + """
        void main() { fragColour = particlesAt(gl_FragCoord.xy); }
        """;

    /// <summary>
    /// The low-resolution particles' coverage for the upscalers' reactive mask: the accumulation's opacity or brightest channel, 30 times, saturating (as
    /// <see cref="ParticleRenderer"/>'s own coverage fragment does per particle), upsampled by depth as the composite is so it does not mark a near edge reactive.
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
            vec4 a = particlesAt(floor(vUv * uFullSize) + 0.5);
            fragColour = vec4(clamp(max(a.a, max(a.r, max(a.g, a.b))) * 30.0, 0.0, uLimit));
        }
        """;

    /// <summary>Depth-aware blur (7 taps) of the occlusion, one direction per pass.</summary>
    /// <summary>
    /// The characters' mask at the occlusion's half size (<see cref="Ssao"/>'s <c>uMask</c>): the scene colour's alpha, which only the characters write, read once
    /// per pixel into R8. The occlusion's twelve taps read the scene colour itself before, at scattered places: 0.2 ms of L2 traffic at 2560 x 1440 (docs/render-post.md).
    /// </summary>
    public const string SsaoMask = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene;
        void main() { fragColour = vec4(textureLod(uScene, vUv, 0.0).a, 0.0, 0.0, 1.0); }
        """;

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

    /// <summary>The finished occlusion as a factor the scene colour is multiplied by (blend zero, source colour; red, green and blue only).</summary>
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
    /// Kenshi's luminance measure (docs/formats/post-processing.md): Rec. 601 luminance, floored at 0.0001, averaged linearly. One texel of the
    /// small luminance target is the mean of a 4 × 4 grid of bilinear taps over its share of the scene; its mipmaps average the rest.
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
    /// Kenshi's adaptation: <c>adapted = last + (mean − last) (1 − exp(−dt · rate))</c>, clamped to <c>[MIN_LUMINANCE, MAX_LUMINANCE]</c>; a 1 × 1 target.
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

    /// <summary>Exposure, occlusion, the game's clip at 1 (no tone curve) and dither: HDR scene in, display colour out.</summary>
    public const string Composite = "#version 330 core\n" + Noise + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene, uAo, uAdapted;   // uAo: the occlusion, the characters' share taken off already (SsaoBlur)
        uniform float uExposure;
        uniform int uUseAo, uDither, uAuto, uTone, uGrade;
        uniform float uToneMix;    // hybrid (uTone 3): 0 the clamp, 1 ACES
        uniform float uSaturation, uContrast;
        uniform vec3 uBalance;     // a white balance on the scene before the tone map (PostOptions.Balance; 1, 1, 1 in play)
        uniform float uNight;      // Meitou night grading strength (the `nightgrade` switch): 0 off; needs the auto exposure's mean (uAuto)
        uniform vec2 uSceneSize;   // the scene's size when it is smaller than the picture (render scale without an upscaler), else 0: sampled with Catmull-Rom then
        uniform float uSharpen;    // after TAA or DLSS (neither sharpens): the upscaler sharpness, 0 off
        const float EXPOSURE_KEY = 0.55;   // hdr.material's EXPOSURE_KEY

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
            vec3 c = raw * exposure * uBalance;
            float ao = texture(uAo, vUv).r;
            if (uUseAo != 0) c *= ao;
            if (uNight > 0.0 && uAuto != 0) c = nightGrade(c, dot(raw, LUM601), adapted.g);
            if (uTone == 1) c = shoulder(c);
            else if (uTone == 2) c = aces(c);
            // hybrid: between the game's clip and ACES, fed 0.7 of the exposure so its mid-tones stay the clip's (0.18 -> 0.17, 0.5 -> 0.49) and only the highlights roll off
            else if (uTone == 3) c = mix(clamp(c, 0.0, 1.0), aces(c * 0.7), uToneMix);
            c = clamp(c, 0.0, 1.0);   // Kenshi has no tone curve: values over 1 clip (docs/formats/post-processing.md)
            if (uGrade != 0)
            {
                c = mix(vec3(dot(c, vec3(0.299, 0.587, 0.114))), c, uSaturation);
                c = clamp((c - 0.5) * uContrast + 0.5, 0.0, 1.0);
            }
            if (uDither != 0) c += (ign(gl_FragCoord.xy) + ign(gl_FragCoord.xy + 17.0) - 1.0) / 255.0;
            fragColour = vec4(c, 1.0);
        }
        """;

    /// <summary>
    /// FXAA on the final LDR picture, with the game's settings (docs/formats/post-processing.md "FXAA"): Lottes' FXAA 3.11 quality
    /// algorithm, green as luma, sub-pixel amount 0.75, edge threshold 0.166, minimum 0.0833, the end-of-edge search in the steps of
    /// quality preset 12 (1, 1.5, 2, 4, 12 pixels). Finds the local contrast, the edge's direction and which side it runs along,
    /// searches both ways for the edge's ends, and moves the lookup across the edge by how far the pixel is from the nearer end,
    /// or by the sub-pixel blend for lone pixels, whichever is larger.
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
    /// pixel the run of that edge is searched both ways (up to 16 pixels) until it ends or a crossing edge breaks it; the crossing edges at the
    /// two ends give the shape (L, U or Z), revectorized as SMAA's area texture is (lines through the ends' half-pixel steps, U shapes
    /// smoothed when short), with the areas computed exactly here instead of from the lookup textures. The area under the line on the pixel's
    /// own side is how much it takes of its neighbour, the area on the other side how much the neighbour takes of it: R / G for the top edge
    /// (this pixel, the one above), B / A for the left edge (this pixel, the one to the left).
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
    /// The game's heat haze on the final LDR picture (docs/formats/post-processing.md "Heat haze"), written from the facts there: a
    /// flow map (at 3.341 × the screen) gives a scroll direction; three layers of a normal map (at 7.341 × the screen, offset by
    /// (0.1, 0.3) and (0.4, 0.7)) scroll along it over phases <c>fract(gameTime · 100 + 0, 0.33, 0.66)</c>, each weighted by a
    /// triangle that is 0 at the ends of its cycle; their summed red and green make a direction only (normalised), moved by
    /// 0.002 · heatHaze · saturate(6 · distance / farClip) in screen units. The picture is the mean of the taps at 1 and 0.7 of
    /// that offset. Texture lookups run in the game's screen coordinates (v from the top), the offset is turned back into ours.
    /// </summary>
    public const string HeatHaze = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uFlow, uPerturbation, uDepth, uImage;
        uniform float uPhase;     // gameTime (game hours since the load) × 100
        uniform float uAmount;    // heatHaze
        uniform vec2 uTan;        // tan(fov/2) * aspect, tan(fov/2)
        uniform vec2 uNearFar;    // of the near depth slice
        uniform float uFarClip;   // the game's far clip D: its G-buffer depth is distance / D
        uniform int uHasDepth;    // 0: no near slice was drawn, everything counts as far

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
            if (uHasDepth != 0 && d < 0.99999)
            {
                float n = 2.0 * d - 1.0;
                float z = 2.0 * uNearFar.x * uNearFar.y / (uNearFar.y + uNearFar.x - n * (uNearFar.y - uNearFar.x));
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
