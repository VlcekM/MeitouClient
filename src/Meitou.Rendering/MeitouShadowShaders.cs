using System.Globalization;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// GLSL of the Meitou shadows (the <c>shadows</c> enhancement; docs/formats/shadows.md, "Meitou shadows"): the receiver
/// <c>kenshiShadowMeitou</c> that <see cref="ShadowShaders.Functions"/> dispatches to when the receiver block's mode is 1, the terrain
/// shadow sweep and the blocker map pass. The cascade matrices are the faithful block's (<see cref="ShadowShaders.ReceiverBlock"/>, the
/// same layout); what only Meitou needs is in its own block (<see cref="Block"/>, binding <see cref="Binding"/>).
/// </summary>
static class MeitouShadowShaders
{
    public const uint Binding = 8;
    public const string Block = "MeitouShadowReceiver";
    /// <summary>Size of <see cref="Block"/> (std140).</summary>
    public const int BlockBytes = 192;
    /// <summary>Taps of the filter and of the blocker search.</summary>
    public const int FilterTaps = 16, BlockerTaps = 8;

    /// <summary>Texture units of the terrain shadow map and the blocker map: just below the shadow map's (<see cref="ShadowShaders.MapUnit"/>).</summary>
    public static int TerrainUnit => ShadowShaders.MapUnit - 1;
    public static int BlockerUnit => ShadowShaders.MapUnit - 2;

    static string F(float v) => v.ToString("0.#########", CultureInfo.InvariantCulture) + (v == MathF.Floor(v) ? ".0" : "");

    /// <summary>
    /// Included by <see cref="ShadowShaders.Functions"/> after the faithful receiver (it uses that block, the map and
    /// <c>kenshiShadowFaithful</c>). In a vertex stage (the grass's includes the lighting) it falls back to the faithful receiver:
    /// derivatives and the pixel position exist only in fragment stages (the compiler defines MEITOU_FRAGMENT there).
    /// </summary>
    public static readonly string Functions = $$"""

        layout(std140) uniform {{Block}}
        {
            vec4 uMsSlice[4];      // x: first view depth the cascade serves (its blend band included), y: last, z: filter radius (tile UV), w: texel (world)
            vec4 uMsBox[4];        // x: world units per tile UV, y: box depth (world), z: blocker search radius (tile UV), w: penumbra (tile UV) per world unit of blocker distance
            vec4 uMsParams;        // x: noise offset (changes per frame under a temporal upscaler), y: shadow range, z: fade start, w: terrain term fade start
            vec4 uMsTerrain;       // xy: world x, z of the first sample, z: samples per world unit, w: 1 when the terrain term is on
            vec4 uMsTerrain2;      // x: samples per side, y: height bias, z: minimum softness (height), w: softness per unit of occluder distance
            vec4 uMsFlags;         // x: 1 when the blocker map is valid (contact-hardening penumbrae), y: terrain term fade end
        };
        uniform sampler2D uShadowTerrain;   // per world grid sample: (height of the shadow's top, distance to the occluder)
        uniform sampler2D uShadowBlocker;   // the atlas's depth at half resolution, nearest of each 2 × 2 (raw, for the blocker search)

        // The terrain's own shadow beyond the cascades: a world point is shadowed by the land towards the sun when it lies below the
        // top of that land's shadow (precomputed per sun direction, TerrainShadowMap), softened by the occluder's distance.
        float msTerrain(vec3 world, float depth)
        {
            if (uMsTerrain.w < 0.5) return 1.0;
            float w = smoothstep(uMsParams.w, uMsFlags.y, depth);
            if (w <= 0.0) return 1.0;
            vec2 grid = (world.xz - uMsTerrain.xy) * uMsTerrain.z;
            vec2 ht = textureLod(uShadowTerrain, (grid + 0.5) / uMsTerrain2.x, 0.0).rg;
            float soft = uMsTerrain2.z + ht.y * uMsTerrain2.w;
            float lit = smoothstep(-soft, soft, world.y + uMsTerrain2.y - ht.x);
            return mix(1.0, lit, w);
        }

        #ifdef MEITOU_FRAGMENT
        // Interleaved gradient noise (Jimenez 2014): a per-pixel angle whose neighbours differ, so a few taps look like many.
        float msNoise(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }

        // One cascade: the point moved off its surface by a texel-sized normal offset, a blocker search in the half-resolution map
        // (when there is one) for the penumbra's width, then a rotated Vogel disk of bilinear comparisons whose depths follow the
        // receiver's plane. -1 when the point (with the filter) is not in the cascade's box, e.g. one drawn some frames ago. (No out
        // parameters: the interface scan of the Vulkan layer would take a global-scope "out" for a stage output.)
        float msCascade(int c, vec3 world, vec3 ng, vec3 nl, float noise)
        {
            float texel = uMsSlice[c].w;
            float cosL = clamp(-nl.z, 0.0, 1.0);
            vec3 p = world - uShadowOrigin.xyz + ng * (texel * (0.5 + 1.5 * sqrt(1.0 - cosL * cosL)));
            vec3 t = (uShadowTile[c] * vec4(p, 1.0)).xyz;
            vec4 rect = uShadowRect[c];
            float radius = uMsSlice[c].z;
            float reach = uMsBox[c].z + uShadowExtent[c].y * 2.0;
            if (any(lessThan(t.xy, vec2(reach))) || any(greaterThan(t.xy, vec2(1.0 - reach))) || t.z >= 1.0) return -1.0;
            // The receiver's plane in tile units: depth change per unit of tile UV (steep planes clamped).
            float nz = max(cosL, 0.25);
            vec2 slope = vec2(nl.x, nl.y) * (uMsBox[c].x / (nz * uMsBox[c].y));
            float angle = noise * 6.2831853;
            vec2 rot = vec2(cos(angle), sin(angle));
            if (uMsFlags.x > 0.5)
            {
                // Blocker search: the mean depth of what lies nearer the sun than the point, within the widest penumbra.
                float search = uMsBox[c].z, sum = 0.0, found = 0.0;
                for (int k = 0; k < {{BlockerTaps}}; k++)
                {
                    float r = sqrt((float(k) + 0.5) / {{F(BlockerTaps)}});
                    float a = float(k) * 2.3999632;
                    vec2 o = r * vec2(rot.x * cos(a) - rot.y * sin(a), rot.y * cos(a) + rot.x * sin(a)) * search;
                    vec2 uv = clamp(t.xy + o, vec2(0.0), vec2(1.0));
                    float z = textureLod(uShadowBlocker, rect.xy + uv * rect.zw, 0.0).r;
                    float limit = t.z + dot(o, slope) - 0.0005;
                    if (z < limit) { sum += z; found += 1.0; }
                }
                if (found < 0.5) return 1.0;   // nothing between the point and the sun
                float blocker = sum / found;
                radius = clamp(radius + (t.z - blocker) * uMsBox[c].y * uMsBox[c].w, radius, uMsBox[c].z);
            }
            float lit = 0.0;
            for (int k = 0; k < {{FilterTaps}}; k++)
            {
                float r = sqrt((float(k) + 0.5) / {{F(FilterTaps)}});
                float a = float(k) * 2.3999632;
                vec2 o = r * vec2(rot.x * cos(a) - rot.y * sin(a), rot.y * cos(a) + rot.x * sin(a)) * radius;
                vec2 uv = t.xy + o;
                lit += textureLod(uShadowMap, vec3(rect.xy + uv * rect.zw, t.z + dot(o, slope)), 0.0);
            }
            return lit / {{F(FilterTaps)}};
        }

        float kenshiShadowMeitou(vec3 world, vec3 n)
        {
            if (uShadowOrigin.w < 0.5) return 1.0;
            // The surface's own plane (the triangle's, not the shading normal, which may carry a normal map), facing like n.
            vec3 ng = cross(dFdx(world), dFdy(world));
            float len = length(ng);
            ng = len > 1e-20 ? ng / len : n;
            if (dot(ng, n) < 0.0) ng = -ng;
            vec3 nl = mat3(uShadowLight) * ng;
            float noise = msNoise(gl_FragCoord.xy + uMsParams.x);
            float depth = dot(world - uShadowOrigin.xyz, uShadowForward.xyz);
            float csm = 1.0;
            if (depth < uMsParams.y)
            {
                int count = int(uShadowForward.w);
                for (int c = 0; c < 4; c++)
                {
                    if (c >= count || depth > uMsSlice[c].y) continue;
                    float s = msCascade(c, world, ng, nl, noise);
                    if (s < 0.0) continue;   // not in this box: the next, coarser cascade
                    // Over the last part of the slice the next cascade is blended in (it covers that band too): no seam.
                    if (c + 1 < count)
                    {
                        float band = uMsSlice[c + 1].x;
                        float b = clamp((depth - band) / max(uMsSlice[c].y - band, 1e-3), 0.0, 1.0);
                        if (b > 0.0)
                        {
                            float s2 = msCascade(c + 1, world, ng, nl, noise);
                            if (s2 >= 0.0) s = mix(s, s2, b);
                        }
                    }
                    csm = s;
                    break;
                }
                csm = mix(csm, 1.0, smoothstep(uMsParams.z, uMsParams.y, depth));   // no hard edge where the range ends
            }
            return min(csm, msTerrain(world, depth));
        }
        #else
        float kenshiShadowMeitou(vec3 world, vec3 n) { return kenshiShadowFaithful(world, n); }
        #endif

        """;

    /// <summary>
    /// One pass of the terrain shadow sweep over the world height grid (<see cref="TerrainShadowMap"/>): each sample takes the higher of
    /// its own shadow top and that of the sample <c>uStep</c> towards the sun, lowered by the sun's slope over that distance. The first
    /// pass reads the raw heights (the land one sample towards the sun); after n passes every sample has seen 2^n samples.
    /// </summary>
    public const string SweepFragment = """
        #version 330 core
        uniform sampler2D uSource;   // the first pass: raw heights (R16, × 9800); then the previous pass (height, distance)
        uniform int uFirst;
        uniform vec2 uStep;          // samples towards the sun
        uniform float uDrop;         // the sun's fall over that distance (world)
        uniform float uDistance;     // that distance (world)
        uniform float uSize;         // samples per side
        out vec4 fragColour;
        void main()
        {
            vec2 here = gl_FragCoord.xy;
            vec2 there = here + uStep;
            bool outside = any(lessThan(there, vec2(0.5))) || any(greaterThan(there, vec2(uSize - 0.5)));
            if (uFirst == 1)
            {
                float h = outside ? -1e6 : texture(uSource, there / uSize).r * 9800.0 - uDrop;
                fragColour = vec4(h, uDistance, 0.0, 1.0);
                return;
            }
            vec2 a = texture(uSource, here / uSize).rg;
            if (!outside)
            {
                vec2 b = texture(uSource, there / uSize).rg + vec2(-uDrop, uDistance);
                if (b.x > a.x) a = b;
            }
            fragColour = vec4(a, 0.0, 1.0);
        }
        """;

    /// <summary>The blocker map: the atlas's raw depth (comparison off while it runs), the nearest of each 2 × 2 texels.</summary>
    public const string BlockerFragment = """
        #version 330 core
        uniform sampler2D uAtlas;
        uniform float uAtlasSize;
        out vec4 fragColour;
        void main()
        {
            vec2 base = floor(gl_FragCoord.xy) * 2.0;
            float z = 1.0;
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                    z = min(z, texelFetch(uAtlas, ivec2(base) + ivec2(i, j), 0).r);
            fragColour = vec4(z, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>Points a program's Meitou block and samplers at their binding and units (called from <see cref="ShadowShaders.Bind"/>).</summary>
    public static void Bind(IGl gl, uint program)
    {
        uint block = gl.GetUniformBlockIndex(program, Block);
        if (block != uint.MaxValue) gl.UniformBlockBinding(program, block, Binding);
        int terrain = gl.GetUniformLocation(program, "uShadowTerrain");
        int blocker = gl.GetUniformLocation(program, "uShadowBlocker");
        if (terrain >= 0 || blocker >= 0)
        {
            gl.UseProgram(program);
            if (terrain >= 0) gl.Uniform1(terrain, TerrainUnit);
            if (blocker >= 0) gl.Uniform1(blocker, BlockerUnit);
            gl.UseProgram(0);
        }
    }
}
