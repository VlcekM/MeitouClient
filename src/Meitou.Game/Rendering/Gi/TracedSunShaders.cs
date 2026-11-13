using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Sky;
using System.Globalization;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The traced sun's shaders (docs/render-shadows.md "Traced sun"). Port of KenshiNative's traced sun shadows (<c>knSunShadowRay</c>,
/// <c>kn_grass_card.hlsl</c>), which starts from a G-buffer and knows each pixel's triangle; here the scene is forward and the receiver is known by
/// its depth and the surface target, so what KenshiNative skips by name is skipped by distance along the ray.
/// </summary>
static class TracedSunShaders
{
    /// <summary>
    /// Instance mask: the fine terrain grid. The masks: <see cref="GiMask"/> is what the probes see, so <see cref="SmallFoliage"/>,
    /// <see cref="Grass"/> and <see cref="Characters"/> are the casters' alone: the lamps' shadow rays see everything (<see cref="LampMask"/>).
    /// </summary>
    public const byte TerrainFine = 0x01;

    /// <summary>
    /// Instance mask: the objects.
    /// </summary>
    public const byte Objects = 0x02;

    /// <summary>
    /// Instance mask: the foliage the probes see.
    /// </summary>
    public const byte Foliage = 0x04;

    /// <summary>
    /// Instance mask: the coarse terrain grid.
    /// </summary>
    public const byte TerrainCoarse = 0x08;

    /// <summary>
    /// Instance mask: the small foliage, the casters' alone.
    /// </summary>
    public const byte SmallFoliage = 0x10;

    /// <summary>
    /// Instance mask: the grass (<see cref="TracedGrass"/>), the casters' alone.
    /// </summary>
    public const byte Grass = 0x20;

    /// <summary>
    /// Instance mask: the characters (<see cref="TracedCharacters"/>), the casters' alone.
    /// </summary>
    public const byte Characters = 0x40;

    /// <summary>
    /// Instance mask: an object that has moved in the last second.
    /// </summary>
    public const byte Moving = 0x80;

    /// <summary>
    /// What the probes see.
    /// </summary>
    public const byte GiMask = TerrainFine | Objects | Foliage | TerrainCoarse | Moving;

    /// <summary>
    /// What the lamps' shadow rays see: everything.
    /// </summary>
    public const byte LampMask = GiMask | SmallFoliage | Grass | Characters;

    /// <summary>
    /// What the per-pixel lamp rays see beside the tiles: what moves (characters, and an object that has moved in the last second,
    /// <see cref="Moving"/>).
    /// </summary>
    public const byte LampMovers = Characters | Moving;

    /// <summary>
    /// What a lamp's cached tile holds (LampTiles): what stands still and does not follow the eye.
    /// </summary>
    public const byte LampTileObjects = Objects | Foliage | SmallFoliage;

    /// <summary>
    /// The ground of a lamp's cached tile, apart as it takes its own ray.
    /// </summary>
    public const byte LampTileGround = TerrainFine | TerrainCoarse;

    /// <summary>
    /// What the surfaces' sun rays see besides the terrain and the grass.
    /// </summary>
    const byte Solids = Objects | Foliage | SmallFoliage | Characters | Moving;

    /// <summary>
    /// What the visibility volume's rays see besides the terrain (<see cref="TracedSunVolume"/>): every solid the surfaces' rays see; no grass (a ray
    /// from the air).
    /// </summary>
    public const byte VolumeSolids = Solids;

    /// <summary>
    /// The fine terrain grid (smooth, 8 apart: <see cref="GiScene"/>) is asked from a point this many units above the surface, plus a share of the
    /// distance (<see cref="FineTerrainLiftPerUnit"/>), along the whole ray: the grid is not the drawn ground (its displacement, the curved outline),
    /// so a ray from the ground itself would hit its own facets, while the 24-unit blind range it had let the ground at a cliff's foot see the sun
    /// through the cliff.
    /// </summary>
    const float FineTerrainLift = 3;

    /// <summary>
    /// The fine terrain grid's lift per unit of distance from the eye.
    /// </summary>
    const float FineTerrainLiftPerUnit = 0.002f;

    /// <summary>
    /// The coarse terrain grid (160 apart) is asked from this far along the ray.
    /// </summary>
    const float CoarseTerrainBlind = 320;

    /// <summary>
    /// World units: the most the drawn ground's relief displaces it from the heightmap (GroundRelief's max depth at the deepest setting), what
    /// <c>terrainOrigin</c> takes back.
    /// </summary>
    const float ReliefAmplitude = 12;

    /// <summary>
    /// On a character (docs/render-shadows.md "Traced sun"): how far a ray from under a turned facet may go before the back of a face blocks it (a
    /// body's thickness).
    /// </summary>
    const float BodyChord = 0.8f;

    /// <summary>
    /// On a character: a shadow edge's least half width (world units; skin spreads light some millimetres).
    /// </summary>
    const float BodyPenumbra = 0.025f;

    /// <summary>
    /// On a character: what a ray from a facet facing the sun skips, past the creases between facets.
    /// </summary>
    const float BodyContact = 0.03f;

    /// <summary>
    /// A shadow ray's cut-out test at a candidate: kept where the map the draw cuts with reaches its threshold (an object part's own pair for the
    /// casters, else the probes'). A grass card's coordinates are its corners' order; a character's map channel is in its record's <c>diffuse</c>
    /// (the probes never see characters). Needs <c>geometries</c>, the bindless table, <c>TexCoord</c>, <c>Triangle</c> and <c>offsetBy</c>.
    /// </summary>
    internal static readonly string CasterCutout = $$"""
        vec2 casterUv(Geometry g, uvec3 tri, vec2 bary)
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
        bool casterKept(int record, int primitive, vec2 bary, float lod)
        {
            Geometry g = geometries[record];
            uint map = g.sunMap != 0u ? g.sunMap : g.alphaMap;
            float cut = g.sunMap != 0u ? g.sunCut : g.alphaThreshold;
            if (cut <= 0.0 || map == 0u) return true;
            uvec3 tri = Triangle(offsetBy(g.indices, uint(primitive) * 12u)).v;
            // What a character wears (its record's tint 204 in red, CharacterRenderer.AddRayPart) casts as cloth: the cut-out read coarsely, kept from a quarter of
            // it on. Hole by hole a hood's weave let single rays through, a stipple of sun on the face under it; a real opening still lets the light pass.
            bool worn = g.kind == {{GeometryRecord.Character}}u && (g.charTint & 255u) == 204u;
            vec4 texel = textureLod(textures2D[nonuniformEXT(map)], casterUv(g, tri, bary), worn ? max(lod, 4.0) : lod);
            return (g.kind == {{GeometryRecord.Character}}u ? texel[g.diffuse & 3u] : texel.a) >= (worn ? 0.25 * cut : cut);
        }
        """;

    /// <summary>
    /// One ray per pixel: the mask's red is what of the sun reaches the pixel (1 where nothing is asked).
    /// </summary>
    public static readonly string Mask = $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8) in;
        {{Meitou.Game.Rendering.Gpu.BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform sampler2D uDepth;
        layout(set = 0, binding = 1) uniform usampler2D uSurface;
        layout(set = 0, binding = 2) uniform sampler2D uTerm;
        layout(set = 0, binding = 3, rgba16f) uniform writeonly image2D uOut;
        layout(set = 0, binding = 4) uniform accelerationStructureEXT uScene;
        {{GiShaders.GeometryStruct}}
        layout(set = 0, binding = 5, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 6, std140) uniform Params
        {
            mat4 inverse;      // NDC, jittered as the frame was -> position relative to the eye
            vec4 eye;          // xyz: the eye relative to the traced origin, w: the tangent of the sun's radius (the penumbra's spread per unit of blocker distance)
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

        """ + CasterCutout + "\n" + $$"""
        #define CANDIDATE_RECORD(q) (rayQueryGetIntersectionInstanceCustomIndexEXT(q, false) + rayQueryGetIntersectionGeometryIndexEXT(q, false))
        #define CANDIDATE_KEPT(q) casterKept(CANDIDATE_RECORD(q), rayQueryGetIntersectionPrimitiveIndexEXT(q, false), rayQueryGetIntersectionBarycentricsEXT(q, false), 1.0)
        #define BLOCKED(q) (rayQueryGetIntersectionTypeEXT(q, true) != gl_RayQueryCommittedIntersectionNoneEXT)

        // Each query returns how far along the ray its blocker is (the penumbra filter's width), 0 when nothing blocks.
        #define HIT_T(q) (BLOCKED(q) ? rayQueryGetIntersectionTEXT(q, true) : 0.0)
        float solidBlocks(vec3 o, vec3 dir, float tMin)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsTerminateOnFirstHitEXT, {{(int)Solids}}, o, tMin, dir, p.sun.w);
            while (rayQueryProceedEXT(q)) if (CANDIDATE_KEPT(q)) rayQueryConfirmIntersectionEXT(q);
            return HIT_T(q);
        }

        // From under a character's facet that is turned from the sun: the ray crosses the body and leaves it by the back of its far side, which must not block (a dark
        // triangle on a low-poly face, where the smooth shading is still lit). The nearest back of a face within a body's thickness is where it leaves; the blockers are
        // looked for from there on (a strand of hair, a hat, a wall).
        float bodyBlocks(vec3 o, vec3 dir)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsCullFrontFacingTrianglesEXT, {{(int)Solids}}, o, 0.0, dir, {{F(BodyChord)}});
            while (rayQueryProceedEXT(q)) { }
            float leaves = HIT_T(q);
            return solidBlocks(o, dir, leaves > 0.0 ? leaves + 0.002 : 0.0);
        }

        // The ground's height fields. Their back faces are skipped as the probes' rays skip them (the drawn ground lies under the grid in a hollow), but an interior's are not: a room
        // dug into a hillside lies under the ground on purpose.
        float terrainBlocks(vec3 o, vec3 dir, uint mask, float tMin, bool interior)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT | (interior ? 0u : gl_RayFlagsCullBackFacingTrianglesEXT), mask, o, tMin, dir, 1e5);
            while (rayQueryProceedEXT(q)) { }
            return HIT_T(q);
        }

        // Where the terrain grids are asked from: the ground displaced by the relief (up to its amplitude above or below the fine grid) is taken back to the grid's height, so a
        // raised crest does not see over a ridge its trough is shadowed by (the grid holds no relief: the relief shades itself, GroundRelief); then lifted by the grid's error.
        // Anything farther from the grid (a roof, a wall's top) leaves from where it is.
        vec3 terrainOrigin(vec3 o, float dist)
        {
            vec3 lift = vec3(0.0, {{F(FineTerrainLift)}} + dist * {{F(FineTerrainLiftPerUnit)}}, 0.0);
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT, {{(int)TerrainFine}}u, o + vec3(0.0, {{F(ReliefAmplitude)}}, 0.0), 0.0,
                vec3(0.0, -1.0, 0.0), {{F(2 * ReliefAmplitude)}});
            while (rayQueryProceedEXT(q)) { }
            if (!BLOCKED(q)) return o + lift;
            // Only down to the grid, never up: a point the grid passes above (a wall's foot against a cliff, a room in a hillside) is under the ground the grid
            // holds, and lifted onto it would see over the cliff.
            float grid = o.y + {{F(ReliefAmplitude)}} - rayQueryGetIntersectionTEXT(q, true);
            return vec3(o.x, min(grid, o.y), o.z) + lift;
        }

        // Grass: only as far as a ray can stay under the tallest blade. A blade's own ray (card) does not see the blades within the blind range, where the drawn card has swayed from
        // its traced copy or stands on its twin; past it the blades count from halfway through the fade.
        float grassBlocks(vec3 o, vec3 dir, float tMin, bool card)
        {
            float start = card ? max(tMin, p.grass.z + p.grass.w * 0.5) : tMin;
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsTerminateOnFirstHitEXT, {{(int)Grass}}, o, start, dir, max(start + 1.0, p.grass.y / max(dir.y, 0.05)));
            while (rayQueryProceedEXT(q)) if (CANDIDATE_KEPT(q)) rayQueryConfirmIntersectionEXT(q);
            return HIT_T(q);
        }

        // One ray a pixel to the sun's centre, the same every frame: x what of the sun reaches the pixel, y how far its blocker is (0: none). The disc's size is the filter's.
        vec2 visibility(ivec2 px)
        {
            uvec4 surface = texelFetch(uSurface, px, 0);
            if ((surface.w & 65536u) == 0u) return vec2(1.0, 0.0);
            vec3 term = texelFetch(uTerm, px, 0).rgb;
            if (max(term.r, max(term.g, term.b)) < 1e-5) return vec2(1.0, 0.0);   // no sun here to shadow: the back of a shape, the night

            vec3 at = pointAt(px);
            float dist = length(at);
            float pixel = dist * p.size.w;
            vec3 dir = p.sun.xyz;
            vec3 n = octDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0);
            uint receiver = (surface.w >> 17u) & 3u;
            bool blade = receiver == 1u, card = receiver == 2u, interior = (surface.w & 512u) != 0u;
            // A card leaves from the face the sun is on (a lit leaf's back); a grass blade's normal says nothing of it; a solid leaves off the plane it drew.
            vec3 side = card ? (dot(n, dir) < 0.0 ? -n : n) : blade ? dir : planeNormal(px, at, n);
            float slope = 1.0 / max(dot(side, dir), 0.2);
            float clearance = (0.01 + pixel * 0.5) * slope;
            vec3 o = p.eye.xyz + at + side * clearance;
            float tMin = card ? max(clearance, 0.5 + pixel * 3.0) : blade ? max(pixel * 2.0, 0.25) : clearance;

            // A character's facet turned from the sun while its shading still takes it (the smooth normal's terminator lies past the facets', skin's light further still).
            // Its facets that face the sun skip what lies within BodyContact: a crease between two facets (a brow on a nose, an eyelid) drew a hair-thin dark line.
            if (receiver == 3u) tMin = max(tMin, {{F(BodyContact)}});
            float t = receiver == 3u && dot(side, dir) < 0.0 ? bodyBlocks(p.eye.xyz + at - side * (0.002 + pixel * 0.25), dir) : solidBlocks(o, dir, tMin);
            if (t <= 0.0)
            {
                vec3 ground = terrainOrigin(o, dist);
                t = terrainBlocks(ground, dir, {{(int)TerrainFine}}u, 0.0, interior);
                if (t <= 0.0) t = terrainBlocks(ground, dir, {{(int)TerrainCoarse}}u, {{F(CoarseTerrainBlind)}}, interior);
            }
            // Grass fades out over the last quarter of its reach, by how much it takes, not by noise.
            if (t <= 0.0 && dist < p.grass.x)
            {
                float g = grassBlocks(o, dir, tMin, blade);
                if (g > 0.0) return vec2(1.0 - clamp((p.grass.x - dist) / (p.grass.x * 0.25), 0.0, 1.0), g);
            }
            return t > 0.0 ? vec2(0.0, t) : vec2(1.0, 0.0);
        }

        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) return;
            // The penumbra filter's input (PenumbraFilter): the visibility, and where blocked the penumbra's radius at the receiver. A pixel the sun does not light (no traced
            // pixel, or turned away from the sun: its term is 0) is no receiver (alpha -1): the filter neither reads it nor spreads it, or a ripple's far slope, unasked and so
            // "lit", would light its sunward neighbours inside a shadow, and a caster's dark side the ground at its foot.
            uvec4 surface = texelFetch(uSurface, px, 0);
            vec3 term = texelFetch(uTerm, px, 0).rgb;
            if ((surface.w & 65536u) == 0u || max(term.r, max(term.g, term.b)) < 1e-5) { imageStore(uOut, px, vec4(1.0, 1.0, 1.0, -1.0)); return; }
            vec2 v = visibility(px);
            // On a character a shadow's edge is at least BodyPenumbra wide: light spreads that far under skin, and the low-poly body's own shadows are polygons.
            float spread = v.y * p.eye.w;
            if (v.y > 0.0 && ((surface.w >> 17u) & 3u) == 3u) spread = max(spread, {{F(BodyPenumbra)}});
            imageStore(uOut, px, vec4(v.xxx, spread));
        }
        """;

    /// <summary>
    /// <c>--debug-shadows 4</c>, over the scene in place of its colour: dark blue where the cascades light the sun (no traced share), else what the
    /// rays let through, green lit and red shadowed, dimmed where the pixel takes no sun (its back, the night).
    /// </summary>
    public static readonly string Debug = "#version 330 core\n" + AtmosphereShaders.Functions + GiResolveShaders.Reconstruct + """
        in vec2 vUv;
        out vec4 fragColour;
        uniform usampler2D uAlbedo;
        uniform sampler2D uTerm;
        uniform sampler2D uMask;

        void main()
        {
            ivec2 px = ivec2(floor(gl_FragCoord.xy));
            if (depthAt(px) <= 0.0) { fragColour = vec4(0.0, 0.0, 0.0, 1.0); return; }
            if ((texelFetch(uAlbedo, px, 0).w & 65536u) == 0u) { fragColour = vec4(0.0, 0.0, 0.35, 1.0); return; }
            float m = texelFetch(uMask, px, 0).r;
            vec3 term = texelFetch(uTerm, px, 0).rgb;
            float lit = max(term.r, max(term.g, term.b)) > 1e-5 ? 1.0 : 0.25;
            fragColour = vec4(vec3(1.0 - m, m, 0.0) * lit, 1.0);
        }
        """;

    /// <summary>
    /// The sun over the scene (blend one, one; red, green and blue): the fragment's term, what the rays let through, and the haze the fragment's own
    /// colour went through (<c>atmoApply</c> is linear in the colour, so its multiplier is the same at this position).
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
            if ((texelFetch(uAlbedo, px, 0).w & 65536u) == 0u) discard;
            float z = depthAt(px);
            if (z <= 0.0) discard;
            vec3 hazeMul, hazeAdd;
            atmoApplyCoefficients(uGiEye, positionAt(px, z), hazeMul, hazeAdd);
            fragColour = vec4(texelFetch(uTerm, px, 0).rgb * texelFetch(uMask, px, 0).r * hazeMul, 0.0);
        }
        """;

    /// <summary>
    /// <see cref="BodyChord"/> as a GLSL constant, for the lamps' rays on a character (<see cref="LampShadowShaders"/>).
    /// </summary>
    internal static string BodyChordText => F(BodyChord);

    /// <summary>
    /// <see cref="BodyContact"/> as a GLSL constant, for the lamps' rays on a character (<see cref="LampShadowShaders"/>).
    /// </summary>
    internal static string BodyContactText => F(BodyContact);

    /// <summary>
    /// A float as a GLSL literal: invariant culture, at least one decimal, at most nine.
    /// </summary>
    static string F(float v) => v.ToString("0.0########", CultureInfo.InvariantCulture);
}
