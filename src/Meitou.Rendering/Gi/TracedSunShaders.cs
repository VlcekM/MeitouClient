using System.Globalization;

namespace Meitou.Rendering.Gi;

/// <summary>
/// The traced sun's shaders (docs/render-shadows.md "Traced sun"). Port of KenshiNative's traced sun shadows (<c>knSunShadowRay</c>, <c>kn_grass_card.hlsl</c>), which
/// starts from a G-buffer and knows each pixel's triangle; here the scene is forward and the receiver is known by its depth and the surface target, so what KenshiNative
/// skips by name is skipped by distance along the ray.
/// </summary>
static class TracedSunShaders
{
    /// <summary>Instance masks in the traced scene. <see cref="GiMask"/> is what the probes see, so <see cref="SmallFoliage"/> and <see cref="Grass"/> are the sun's alone.</summary>
    public const byte TerrainFine = 0x01, Objects = 0x02, Foliage = 0x04, TerrainCoarse = 0x08, SmallFoliage = 0x10, Grass = 0x20;
    public const byte GiMask = TerrainFine | Objects | Foliage | TerrainCoarse;
    const byte Solids = Objects | Foliage | SmallFoliage;

    /// <summary>Terrain nearer than this along a ray is not asked: the traced grids are coarser than the drawn ground, and a cell's own facets are not the ground's.</summary>
    const float FineTerrainBlind = 24, CoarseTerrainBlind = 320;

    static string F(float v) => v.ToString("0.0########", CultureInfo.InvariantCulture);

    /// <summary>One ray per pixel: the mask's red is what of the sun reaches the pixel (1 where nothing is asked).</summary>
    public static readonly string Mask = $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8) in;
        {{Meitou.Rendering.Gpu.BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform sampler2D uDepth;
        layout(set = 0, binding = 1) uniform usampler2D uSurface;
        layout(set = 0, binding = 2) uniform sampler2D uTerm;
        layout(set = 0, binding = 3, rgba8) uniform writeonly image2D uOut;
        layout(set = 0, binding = 4) uniform accelerationStructureEXT uScene;
        struct Geometry { uvec2 vertices; uvec2 indices; uint stride; uint kind; uint diffuse; uint alphaMap; float alphaThreshold; uint emitter; uint sunMap; float sunCut; };
        layout(set = 0, binding = 5, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 6, std140) uniform Params
        {
            mat4 inverse;      // NDC, jittered as the frame was -> position relative to the eye
            vec4 eye;          // xyz: the eye relative to the traced origin, w: the tangent of the sun's radius (0: its centre alone)
            vec4 sun;          // xyz: towards the sun, w: the ray's length
            vec4 size;         // width, height, the frame, a pixel's width per unit of depth
            vec4 grass;        // x: how far from the eye grass is traced (0: none), y: the blades' tallest, z: what a blade's own ray does not see of other blades, w: the fade past that
        } p;
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer TexCoord { vec2 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Triangle { uvec3 v; };
        uvec2 offsetBy(uvec2 a, uint bytes) { uint lo = a.x + bytes; return uvec2(lo, a.y + (lo < a.x ? 1u : 0u)); }
        """ + DepthConvention.Glsl + $$"""

        vec3 octDir(vec2 e)
        {
            vec3 d = vec3(e.x, 1.0 - abs(e.x) - abs(e.y), e.y);
            if (d.y < 0.0) d.xz = (1.0 - abs(d.zx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.z >= 0.0 ? 1.0 : -1.0);
            return normalize(d);
        }

        // The position, relative to the eye, of what a pixel drew.
        vec3 pointAt(ivec2 px)
        {
            ivec2 q = clamp(px, ivec2(0), ivec2(p.size.xy) - 1);
            vec4 h = p.inverse * vec4((vec2(q) + 0.5) / p.size.xy * 2.0 - 1.0, texelFetch(uDepth, q, 0).r, 1.0);
            return h.xyz / h.w;
        }

        // The drawn triangle's plane from the depth, along the neighbours of the smaller step, facing like n (n itself where the depth says nothing: a sky neighbour on both sides).
        vec3 planeNormal(ivec2 px, vec3 at, vec3 n)
        {
            vec3 l = at - pointAt(px - ivec2(1, 0)), r = pointAt(px + ivec2(1, 0)) - at;
            vec3 d = at - pointAt(px - ivec2(0, 1)), u = pointAt(px + ivec2(0, 1)) - at;
            vec3 g = cross(dot(r, r) < dot(l, l) ? r : l, dot(u, u) < dot(d, d) ? u : d);
            float len = length(g);
            if (!(len > 1e-8)) return n;
            g /= len;
            return dot(g, n) < 0.0 ? -g : g;
        }

        // A candidate on a cut-out surface: kept where the map the draw cuts with reaches its threshold (the objects' own pair, for the sun alone, else the probes').
        vec2 hitUv(Geometry g, uvec3 tri, vec2 bary)
        {
            vec2 ua, ub, uc;
            if (g.kind == {{GeometryRecord.Grass}}u)
            {
                // A card's vertices are its four corners in order, u the first bit, v the second (TracedGrass).
                ua = vec2(tri.x & 1u, (tri.x >> 1u) & 1u);
                ub = vec2(tri.y & 1u, (tri.y >> 1u) & 1u);
                uc = vec2(tri.z & 1u, (tri.z >> 1u) & 1u);
            }
            else
            {
                ua = TexCoord(offsetBy(g.vertices, tri.x * g.stride + {{GpuVertex.UvOffset}}u)).v;
                ub = TexCoord(offsetBy(g.vertices, tri.y * g.stride + {{GpuVertex.UvOffset}}u)).v;
                uc = TexCoord(offsetBy(g.vertices, tri.z * g.stride + {{GpuVertex.UvOffset}}u)).v;
            }
            return ua * (1.0 - bary.x - bary.y) + ub * bary.x + uc * bary.y;
        }
        bool kept(int record, int primitive, vec2 bary)
        {
            Geometry g = geometries[record];
            uint map = g.sunMap != 0u ? g.sunMap : g.alphaMap;
            float cut = g.sunMap != 0u ? g.sunCut : g.alphaThreshold;
            if (cut <= 0.0 || map == 0u) return true;
            uvec3 tri = Triangle(offsetBy(g.indices, uint(primitive) * 12u)).v;
            return textureLod(textures2D[nonuniformEXT(map)], hitUv(g, tri, bary), 1.0).a >= cut;
        }
        #define CANDIDATE_RECORD(q) (rayQueryGetIntersectionInstanceCustomIndexEXT(q, false) + rayQueryGetIntersectionGeometryIndexEXT(q, false))
        #define CANDIDATE_KEPT(q) kept(CANDIDATE_RECORD(q), rayQueryGetIntersectionPrimitiveIndexEXT(q, false), rayQueryGetIntersectionBarycentricsEXT(q, false))
        #define BLOCKED(q) (rayQueryGetIntersectionTypeEXT(q, true) != gl_RayQueryCommittedIntersectionNoneEXT)

        bool solidBlocks(vec3 o, vec3 dir, float tMin)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsTerminateOnFirstHitEXT, {{(int)Solids}}, o, tMin, dir, p.sun.w);
            while (rayQueryProceedEXT(q)) if (CANDIDATE_KEPT(q)) rayQueryConfirmIntersectionEXT(q);
            return BLOCKED(q);
        }

        // The ground's height fields. Their back faces are skipped as the probes' rays skip them (the drawn ground lies under the grid in a hollow), but an interior's are not: a room
        // dug into a hillside lies under the ground on purpose.
        bool terrainBlocks(vec3 o, vec3 dir, uint mask, float tMin, bool interior)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT | (interior ? 0u : gl_RayFlagsCullBackFacingTrianglesEXT), mask, o, tMin, dir, 1e5);
            while (rayQueryProceedEXT(q)) { }
            return BLOCKED(q);
        }

        // Grass: only as far as a ray can stay under the tallest blade, and fading out with the distance it is traced to. A blade's own ray (card) does not see the blades within the
        // blind range, where the drawn card has swayed from its traced copy or stands on its twin, and takes the next ones in by a share of the noise (KenshiNative's card fade).
        bool grassBlocks(vec3 o, vec3 dir, float tMin, bool card, float dist, float noise)
        {
            if (noise * p.grass.x < dist * 4.0 - p.grass.x * 3.0) return false;
            float start = card ? max(tMin, p.grass.z) : tMin;
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsTerminateOnFirstHitEXT, {{(int)Grass}}, o, start, dir, max(start + 1.0, p.grass.y / max(dir.y, 0.05)));
            while (rayQueryProceedEXT(q))
            {
                float share = card ? smoothstep(p.grass.z, p.grass.z + p.grass.w, rayQueryGetIntersectionTEXT(q, false)) : 1.0;
                if (noise < share && CANDIDATE_KEPT(q)) rayQueryConfirmIntersectionEXT(q);
            }
            return BLOCKED(q);
        }

        // Interleaved gradient noise over the screen, moved each frame: what one ray a pixel leaves, the temporal upscaler averages.
        float pixelNoise(ivec2 px, float shift) { return fract(52.9829189 * fract(dot(vec2(px) + shift * p.size.z, vec2(0.06711056, 0.00583715)))); }

        // A direction inside the sun's disc.
        vec3 discRay(ivec2 px)
        {
            vec3 l = p.sun.xyz;
            if (p.eye.w <= 0.0) return l;
            vec3 t = normalize(abs(l.y) < 0.9 ? cross(l, vec3(0.0, 1.0, 0.0)) : cross(l, vec3(1.0, 0.0, 0.0)));
            float r = sqrt(pixelNoise(px, 5.588238)) * p.eye.w, a = 6.2831853 * pixelNoise(px + ivec2(37, 17), 3.141593);
            return normalize(l + (t * cos(a) + cross(l, t) * sin(a)) * r);
        }

        float visibility(ivec2 px)
        {
            uvec4 surface = texelFetch(uSurface, px, 0);
            if ((surface.w & 1024u) == 0u) return 1.0;
            vec3 term = texelFetch(uTerm, px, 0).rgb;
            if (max(term.r, max(term.g, term.b)) < 1e-5) return 1.0;   // no sun here to shadow: the back of a shape, the night

            vec3 at = pointAt(px);
            float dist = length(at);
            float pixel = dist * p.size.w;
            vec3 dir = discRay(px);
            vec3 n = octDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0);
            uint receiver = (surface.w >> 11u) & 3u;
            bool blade = receiver == 1u, card = receiver == 2u, interior = (surface.w & 512u) != 0u;
            // A card leaves from the face the sun is on (a lit leaf's back); a grass blade's normal says nothing of it; a solid leaves off the plane it drew.
            vec3 side = card ? (dot(n, dir) < 0.0 ? -n : n) : blade ? dir : planeNormal(px, at, n);
            float slope = 1.0 / max(dot(side, dir), 0.2);
            float clearance = (0.01 + pixel * 0.5) * slope;
            vec3 o = p.eye.xyz + at + side * clearance;
            float tMin = card ? max(clearance, 0.5 + pixel * 3.0) : blade ? max(pixel * 2.0, 0.25) : clearance;

            if (solidBlocks(o, dir, tMin)) return 0.0;
            if (terrainBlocks(o, dir, {{(int)TerrainFine}}u, {{F(FineTerrainBlind)}}, interior)) return 0.0;
            if (terrainBlocks(o, dir, {{(int)TerrainCoarse}}u, {{F(CoarseTerrainBlind)}}, interior)) return 0.0;
            if (dist < p.grass.x && grassBlocks(o, dir, tMin, blade, dist, pixelNoise(px + ivec2(91, 53), 7.123457))) return 0.0;
            return 1.0;
        }

        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) return;
            imageStore(uOut, px, vec4(visibility(px)));
        }
        """;

    /// <summary>
    /// The sun over the scene (blend one, one; red, green and blue): the fragment's term, what the rays let through, and the haze the fragment's own colour went through
    /// (<c>atmoApply</c> is linear in the colour, so its multiplier is the same at this position).
    /// </summary>
    public static readonly string Add = "#version 330 core\n" + AtmosphereShaders.Functions + GiResolveShaders.Reconstruct + """
        in vec2 vUv;
        out vec4 fragColour;
        uniform usampler2D uAlbedo;
        uniform sampler2D uTerm;
        uniform sampler2D uMask;

        void main()
        {
            ivec2 px = ivec2(floor(gl_FragCoord.xy));
            if ((texelFetch(uAlbedo, px, 0).w & 1024u) == 0u) discard;
            float z = depthAt(px);
            if (z <= 0.0) discard;
            vec3 hazeMul, hazeAdd;
            atmoApplyCoefficients(uGiEye, positionAt(px, z), hazeMul, hazeAdd);
            fragColour = vec4(texelFetch(uTerm, px, 0).rgb * texelFetch(uMask, px, 0).r * hazeMul, 0.0);
        }
        """;
}
