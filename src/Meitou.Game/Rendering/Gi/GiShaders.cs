using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The global illumination's shaders (docs/render-gi.md).
/// </summary>
static class GiShaders
{
    /// <summary>
    /// <paramref name="v"/> as a GLSL float literal (invariant culture, always with a decimal point).
    /// </summary>
    static string F(float v) => v.ToString("0.0########", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The direction of an octahedral position (<c>giOct</c>); the surface target stores the normal so.</summary>
    internal const string OctDirection = """
        vec3 giOctDir(vec2 p)
        {
            vec3 d = vec3(p.x, 1.0 - abs(p.x) - abs(p.y), p.y);
            if (d.y < 0.0) d.xz = (1.0 - abs(d.zx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.z >= 0.0 ? 1.0 : -1.0);
            return normalize(d);
        }
        """;

    /// <summary>A hit record in GLSL (<see cref="GeometryRecord"/>).</summary>
    internal const string GeometryStruct = """
        struct Geometry { uvec2 vertices; uvec2 indices; uint stride; uint kind; uint diffuse; uint alphaMap; float alphaThreshold; uint emitter; uint sunMap; float sunCut; uint charDiffuse; uint charHead; uint charTint; uint pad; };
        """;

    /// <summary>The vertex and index data of a hit, by device address.</summary>
    internal const string HitBuffers = """
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Position { vec3 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer TexCoord { vec2 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Triangle { uvec3 v; };
        uvec2 offsetBy(uvec2 a, uint bytes) { uint lo = a.x + bytes; return uvec2(lo, a.y + (lo < a.x ? 1u : 0u)); }
        """;

    /// <summary>The cut-out test of a candidate hit. Needs <see cref="HitBuffers"/>, <c>geometries</c> and the bindless table.</summary>
    internal static readonly string HitCutout = $$"""
        vec2 hitUv(Geometry g, uvec3 tri, vec2 bary)
        {
            // The vertex texture coordinates are at byte 24 (Vertex.Uv).
            vec2 ua = TexCoord(offsetBy(g.vertices, tri.x * g.stride + {{GpuVertex.UvOffset}}u)).v;
            vec2 ub = TexCoord(offsetBy(g.vertices, tri.y * g.stride + {{GpuVertex.UvOffset}}u)).v;
            vec2 uc = TexCoord(offsetBy(g.vertices, tri.z * g.stride + {{GpuVertex.UvOffset}}u)).v;
            return ua * (1.0 - bary.x - bary.y) + ub * bary.x + uc * bary.y;
        }
        // A candidate hit on a cut-out geometry (leaves): kept where its map's alpha reaches the threshold, as the draws cut it.
        bool solid(int record, int primitive, vec2 bary)
        {
            Geometry g = geometries[record];
            if (g.alphaThreshold <= 0.0 || g.alphaMap == 0u) return true;
            uvec3 tri = Triangle(offsetBy(g.indices, uint(primitive) * 12u)).v;
            return textureLod(textures2D[nonuniformEXT(g.alphaMap)], hitUv(g, tri, bary), 2.0).a >= g.alphaThreshold;
        }
        """;

    /// <summary>
    /// The probe volume's layout and how a surface reads it (docs/render-gi.md "Probes"), shared by every world shader (through
    /// <see cref="AtmosphereShaders.Functions"/>, GLSL 330) and the probe trace (the bounces beyond the first). Needs <c>uGiParams</c>,
    /// <c>uGiGrid[4]</c>, <c>uGiIrradiance</c>, <c>uGiDistance</c> and <c>uGiBase</c> declared before it. Per cascade c: <c>uGiGrid[2c]</c> = the
    /// world x and z of window column 0, the spacing across and up; <c>uGiGrid[2c + 1]</c> = the toroidal offset of window column 0 in x and z, the
    /// distance atlas's normalisation, 0. A probe of window column (i, k), layer l stands at (x0 + i·s, base + (l + 0.5)·sy, z0 + k·s), base the
    /// terrain height of its column (<c>uGiBase</c>).
    /// </summary>
    public static readonly string ProbeSampling = $$"""

        const int GI_N = {{GiProbes.Columns}}, GI_NY = {{GiProbes.Layers}}, GI_IRR = {{GiProbes.IrradianceTexels}}, GI_DIST = {{GiProbes.DistanceTexels}}, GI_CASCADES = {{GiProbes.Cascades}};
        const float GI_MIN_WEIGHT = 0.02;   // the smallest trilinear weight a probe is read at
        // Octahedral mapping about y: the upper hemisphere in the inner diamond.
        vec2 giOct(vec3 d)
        {
            d /= abs(d.x) + abs(d.y) + abs(d.z);
            vec2 p = d.xz;
            if (d.y < 0.0) p = (1.0 - abs(p.yx)) * vec2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
            return p;
        }
        {{OctDirection}}
        // A probe's tile: x its toroidal column, y (cascade · N + toroidal row) · layers + layer; inner texels plus a border of one.
        // oct: the direction's octahedral position in 0..1 (giOct · 0.5 + 0.5); invSize: 1 / the atlas's size.
        vec2 giUv(ivec2 tile, vec2 oct, int inner, vec2 invSize)
        {
            return (vec2(tile) * float(inner + 2) + 1.0 + oct * float(inner)) * invSize;
        }
        // One cascade: rgb the irradiance (the lighting pass's envDiffuse), a how much of it to take (0 outside, 1 a cell inside its edge).
        // push: how many cells further along the normal to look up (a retry when every probe round the point is inside geometry).
        vec4 giCascade(int c, vec3 p, vec3 n, vec3 v, float push)
        {
            vec4 g0 = uGiGrid[c * 2], g1 = uGiGrid[c * 2 + 1];
            float s = g0.z, sy = g0.w;
            vec3 q = p + (n * 0.2 + v * 0.8) * (0.3 * min(s, sy)) + n * (push * min(s, sy));
            vec2 cell = (q.xz - g0.xy) / s;
            float edge = min(min(cell.x, cell.y), min(float(GI_N - 1) - cell.x, float(GI_N - 1) - cell.y));
            if (edge <= 0.0) return vec4(0.0);
            ivec2 i0 = ivec2(floor(cell));
            vec2 f = cell - vec2(i0);
            const vec2 irrInv = 1.0 / vec2(float(GI_N * (GI_IRR + 2)), float(GI_CASCADES * GI_N * GI_NY * (GI_IRR + 2)));
            const vec2 distInv = 1.0 / vec2(float(GI_N * (GI_DIST + 2)), float(GI_CASCADES * GI_N * GI_NY * (GI_DIST + 2)));
            // The normal's octahedral position is the same in every probe's tile: once per point, not per probe.
            vec2 octN = giOct(n) * 0.5 + 0.5;
            uvec2 offset = uvec2(g1.xy);
            vec3 sum = vec3(0.0);
            float total = 0.0, coverage = 0.0;
            for (int k = 0; k < 4; k++)
            {
                ivec2 o = ivec2(k & 1, k >> 1);
                ivec2 w = min(i0 + o, ivec2(GI_N - 1));
                int colX = int((offset.x + uint(w.x)) % uint(GI_N)), colZ = int((offset.y + uint(w.y)) % uint(GI_N));
                float base = texelFetch(uGiBase, ivec2(colX, c * GI_N + colZ), 0).r;
                float ly = clamp((q.y - base) / sy - 0.5, 0.0, float(GI_NY - 1));
                int l0 = min(int(ly), GI_NY - 2);
                float fy = ly - float(l0);
                float wxz = (o.x == 1 ? f.x : 1.0 - f.x) * (o.y == 1 ? f.y : 1.0 - f.y);
                // A dense room grid is short: fade to the outer cascade above its last layer, rather than stretching that layer upwards.
                float heightShare = clamp(float(GI_NY) - (q.y - base) / sy, 0.0, 1.0);
                coverage += wxz * heightShare;
                if (heightShare <= 0.0) continue;
                for (int j = 0; j < 2; j++)
                {
                    int layer = l0 + j;
                    float weight = wxz * heightShare * (j == 1 ? fy : 1.0 - fy);
                    // A corner the point is nearly opposite of adds next to nothing: skip its fetches.
                    if (weight < GI_MIN_WEIGHT) continue;
                    vec3 probe = vec3(g0.x + float(w.x) * s, base + (float(layer) + 0.5) * sy, g0.y + float(w.y) * s);
                    ivec2 tile = ivec2(colX, (c * GI_N + colZ) * GI_NY + layer);
                    // Probes behind the surface count less (DDGI's smooth backface term).
                    vec3 toProbe = probe - p;
                    float back = (dot(toProbe, n) * inversesqrt(max(dot(toProbe, toProbe), 1e-8)) + 1.0) * 0.5;
                    weight *= back * back + 0.2;
                    #ifndef GI_NO_VISIBILITY
                    // Visibility: Chebyshev's bound on the probe's distance moments towards the point.
                    vec3 from = q - probe;
                    float d = length(from);
                    vec2 m = textureLod(uGiDistance, giUv(tile, giOct(from) * 0.5 + 0.5, GI_DIST, distInv), 0.0).rg * vec2(g1.z, g1.z * g1.z);
                    if (d > m.x)
                    {
                        float variance = abs(m.x * m.x - m.y);
                        float cheb = variance / (variance + (d - m.x) * (d - m.x));
                        weight *= max(cheb * cheb * cheb, 0.0);
                    }
                    #endif
                    vec4 irr = textureLod(uGiIrradiance, giUv(tile, octN, GI_IRR, irrInv), 0.0);
                    weight *= irr.a;   // 0: a probe inside geometry
                    if (weight < 0.2) weight *= weight * weight * 25.0;
                    sum += irr.rgb * weight;
                    total += weight;
                }
            }
            return total > 1e-5 ? vec4(sum / total, clamp(edge, 0.0, 1.0) * coverage) : vec4(0.0);
        }
        // The probes' irradiance at a surface (p: world position, n: its normal, v: towards the eye): rgb, a the share to take (0: none cover it).
        vec4 giIrradiance(vec3 p, vec3 n, vec3 v)
        {
            vec4 a = giCascade(0, p, n, v, 0.0);
            // A room grid's probes in the walls, floor and furniture are invalid: next to them the point can have none valid round it, and the coarse
            // cascade would paint the corner dark. Look again a cell into the room before falling back.
            if (a.a <= 0.0) a = giCascade(0, p, n, v, 1.0);
            if (a.a >= 1.0) return a;
            vec4 b = giCascade(1, p, n, v, 0.0);
            float wa = a.a, wb = b.a * (1.0 - a.a), t = wa + wb;
            return t > 0.0 ? vec4((a.rgb * wa + b.rgb * wb) / t, t) : vec4(0.0);
        }

        // Presentation gain only: never feed amplified light back into the probe solver.
        // Indoors the light is at least the game's ambient luminance up to InteriorFloorCap (the probes see corners as dark), more
        // where the GI is brighter. Its colour turns from the ambient's to the GI's as the GI's share of the ambient rises from 0.1 to 0.25 (in the
        // Hub bar the lamps' surroundings read 0.15-0.3, the rest 0.05-0.15): warm round the lamps, neutral where they barely reach.
        // uGiParams.w <= 1 leaves the GI as it is.
        const float InteriorFloorCap = 0.6;
        vec3 giSurfaceIrradiance(vec3 irradiance, vec3 sky, bool interior)
        {
            if (!interior || uGiParams.w <= 1.0) return irradiance;
            const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
            float value = max(dot(irradiance, lum), 1e-6), ambient = max(dot(sky, lum), 1e-6);
            // The floor is capped: by day the sky's ambient is several times the night's and lit shut rooms evenly, in the sky's colour. Past the cap its hue
            // goes grey as the cap bites (the night's ambient is under it: unchanged).
            float floorValue = min(ambient, InteriorFloorCap);
            vec3 skyHue = mix(sky / ambient, vec3(1.0), 1.0 - floorValue / ambient);
            vec3 hue = mix(skyHue, irradiance / value, smoothstep(0.1, 0.25, value / floorValue));
            return hue * max(value, floorValue);
        }

        """;

    /// <summary>
    /// Whether a probe sleeps this update (docs/render-gi.md "Sleeping probes"), the same answer in the trace and the blend: its state (w 2: no
    /// surface within reach at its last blend, at the same place) and the round of its updates (<c>origin.w</c> the frame, <c>uGiParams.z</c>
    /// the phases): it wakes every <c>rayLength.z</c>-th round (0: never sleeps). Needs <c>uGiParams</c>, <c>origin</c> and <c>rayLength</c>.
    /// </summary>
    const string Sleep = """
        bool giAsleep(vec4 last, vec3 world, uint stateSlot)
        {
            if (rayLength.z < 0.5 || last.w < 1.5 || distance(last.xyz, world) > 0.5) return false;
            uint round = uint(origin.w) / uint(uGiParams.z);
            return (round + stateSlot) % uint(rayLength.z) != 0u;
        }

        """;

    /// <summary>What a shader that shades ray hits needs (the probe trace, <see cref="GiCacheShaders.Trace"/>): the vertex fetches, the ambient lookups, the cut-out test and the lamps' shadow rays. Needs its names in scope: <c>geometries</c>, <c>maps</c>, <c>origin</c>, <c>uScene</c>.</summary>
    /// <summary>
    /// The lamps' visibility at the hits of the traced light: the lamp's tile when it has one (every lamp, the same still world the direct light sees, one filtered lookup), else <c>giLampVisibility</c>,
    /// the old rule (a ray for an interior lamp, none for the others). Binding 18 is the tiles.
    /// </summary>
    internal static readonly string LampTileTrace = $$"""
        layout(set = 0, binding = 18) uniform sampler2D uLampTiles;
        float giLampVisibility(vec3 world, vec3 n, vec3 toLamp, int indoor, uint emitter);
        #define LAMP_TILE_CHEAP
        {{LampShaders.Defines}}{{LampShaders.TileMapping}}{{LampShaders.TileVisibility}}
        float giLampSeen(int lamp, vec3 world, vec3 n, vec3 toLamp, float d, int indoor, uint emitter)
        {
            return texelFetch(uLightData, ivec2(4, lamp), 0).w != 0.0 ? lampTileVisibility(lamp, toLamp, d, n) : giLampVisibility(world, n, toLamp, indoor, emitter);
        }
        #define LAMP_VISIBILITY(world, n, toLamp, indoor, emitter) giLampSeen(lamp, world, n, toLamp, d, indoor, emitter)
        {{LampShaders.Lighting}}
        """;

    internal static readonly string HitFunctions = $$"""
        {{HitBuffers}}

        {{AtmosphereShaders.AmbientFloorFunction}}
        vec4 ambientMap(vec3 world)
        {
            if (maps.z < 0.5) return vec4(1.0, 1.0, 1.0, 0.5);
            return atmoAmbientFloor(textureLod(uAtmoAmbientMap, (world.xz + maps.w) / (2.0 * maps.w), 0.0), maps);
        }
        vec3 skyIrradiance(vec3 n)
        {
            if (maps.x < 0.5) return vec3(1.09, 1.20, 1.51);
            vec4 t = textureLod(uAtmoIrradiance, n, {{F(Meitou.Data.World.KenshiLighting.IrradianceLevel)}});
            return t.rgb * t.a * 4.0;
        }
        {{HitCutout}}
        #define TRACE(q) while (rayQueryProceedEXT(q)) { if (solid(rayQueryGetIntersectionInstanceCustomIndexEXT(q, false) + rayQueryGetIntersectionGeometryIndexEXT(q, false), rayQueryGetIntersectionPrimitiveIndexEXT(q, false), rayQueryGetIntersectionBarycentricsEXT(q, false))) rayQueryConfirmIntersectionEXT(q); }

        float giLampVisibility(vec3 world, vec3 n, vec3 toLamp, int indoor, uint emitter)
        {
            if (indoor == 0) return 1.0;
            float d = length(toLamp);
            if (d <= 1.0) return 1.0;
            rayQueryEXT shadow;
            rayQueryInitializeEXT(shadow, uScene, gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsCullBackFacingTrianglesEXT | gl_RayFlagsNoOpaqueEXT, {{TracedSunShaders.GiMask}},
                world - origin.xyz + n * 0.5, 0.0, toLamp / d, d - 1.0);
            while (rayQueryProceedEXT(shadow))
            {
                int record = rayQueryGetIntersectionInstanceCustomIndexEXT(shadow, false) + rayQueryGetIntersectionGeometryIndexEXT(shadow, false);
                // Light positions can be embedded in their fixture. The fixture must not extinguish its own source.
                if ((emitter == 0u || geometries[record].emitter != emitter) && solid(record,
                    rayQueryGetIntersectionPrimitiveIndexEXT(shadow, false), rayQueryGetIntersectionBarycentricsEXT(shadow, false)))
                    rayQueryConfirmIntersectionEXT(shadow);
            }
            return rayQueryGetIntersectionTypeEXT(shadow, true) == gl_RayQueryCommittedIntersectionNoneEXT ? 1.0 : 0.0;
        }
        """;

    /// <summary>
    /// The light at a committed hit on the front (or on foliage), inside a shader's <c>main</c>: from <c>rq</c>, <c>dir</c>, <c>t</c>, <c>world</c> and the record <c>g</c>, the
    /// hit's <c>colour</c>, <c>sun</c>, <c>ambient</c> and <c>lamps</c>; its radiance towards the ray is <c>colour * (sun + ambient + lamps)</c>.
    /// </summary>
    internal static readonly string HitShade = HitShadeAt("4.0");

    /// <summary>What a ray finds at its hit, its textures read at mip <paramref name="lod"/>: the probes want a coarse one, a reflection a fine one.</summary>
    static string HitShadeAt(string lod) => $$"""
            uvec3 tri = Triangle(offsetBy(g.indices, uint(rayQueryGetIntersectionPrimitiveIndexEXT(rq, true)) * 12u)).v;
            vec3 a = Position(offsetBy(g.vertices, tri.x * g.stride)).v;
            vec3 b = Position(offsetBy(g.vertices, tri.y * g.stride)).v;
            vec3 cc = Position(offsetBy(g.vertices, tri.z * g.stride)).v;
            vec3 n = normalize(cross(b - a, cc - a) * mat3(rayQueryGetIntersectionWorldToObjectEXT(rq, true)));
            if (dot(n, dir) > 0.0) n = -n;
            vec3 hit = world + dir * t;
            vec3 colour = albedo.rgb;
            if (g.kind == 0u && albedo.w > 0.5) colour = textureLod(uGround, (hit.xz + maps.w) / (2.0 * maps.w), 0.0).rgb;
            else if (g.kind == {{GeometryRecord.Character}}u)
            {
                vec2 hitTex = hitUv(g, tri, rayQueryGetIntersectionBarycentricsEXT(rq, true));
                vec4 tint = unpackUnorm4x8(g.charTint);
                vec3 own = g.charDiffuse != 0u ? textureLod(textures2D[nonuniformEXT(g.charDiffuse)], hitTex, {{lod}}).rgb : vec3(g.charTint != 0u ? 1.0 : 0.0);
                if (g.charHead != 0u) own += textureLod(textures2D[nonuniformEXT(g.charHead)], hitTex, {{lod}}).rgb;
                if (tint.a < 0.5) own = vec3(max(own.r, max(own.g, own.b)));
                if (g.charTint != 0u) colour = own * tint.rgb;
            }
            else if (g.kind != 0u && g.diffuse != 0u)
                // The object's or plant's own diffuse map at the hit, a coarse mip: the bounce takes its colour.
                colour = textureLod(textures2D[nonuniformEXT(g.diffuse)], hitUv(g, tri, rayQueryGetIntersectionBarycentricsEXT(rq, true)), {{lod}}).rgb;
            vec4 amHit = ambientMap(hit);
            vec3 l = lightDir.xyz;
            float nl = max(dot(n, l), 0.0);
            vec3 sun = vec3(0.0);
            if (nl > 0.0)
            {
                rayQueryEXT shadow;
                rayQueryInitializeEXT(shadow, uScene, gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsCullBackFacingTrianglesEXT, {{TracedSunShaders.GiMask}},
                    hit - origin.xyz + n * (0.5 + t * 0.001), 0.0, l, 1e5);
                TRACE(shadow)
                if (rayQueryGetIntersectionTypeEXT(shadow, true) == gl_RayQueryCommittedIntersectionNoneEXT)
                    sun = {{F(MathF.PI * (1 - Meitou.Data.World.KenshiLighting.DielectricSpecular))}} * nl * sunLight.rgb * amHit.a * 2.0;
            }
            vec4 bounce = giIrradiance(hit, n, -dir);
            vec3 ambient = mix(skyIrradiance(n) * {{F(1 - Meitou.Data.World.KenshiLighting.DielectricSpecular)}} * amHit.rgb * env, bounce.rgb, bounce.a);
            // The lamps around the hit; indoor sources are visibility-tested so occluded lamp light cannot bounce through walls.
            vec3 lamps = vec3(0.0), lampSpecular = vec3(0.0);
            if (uLightGrid.w > 0.5) lampLight(hit, n, -dir, 1.0, 1.0, lamps, lampSpecular);
        """;

    /// <summary>The extensions of a shader that traces rays and reads hit records.</summary>
    const string TraceExtensions = """
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        """;

    /// <summary>The traced scene and its hit records.</summary>
    static readonly string TraceScene = $$"""
        {{Meitou.Game.Rendering.Gpu.BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform accelerationStructureEXT uScene;
        {{GeometryStruct}}
        layout(set = 0, binding = 1, std430) readonly buffer Geometries { Geometry geometries[]; };
        """;

    /// <summary>The trace's constants (<see cref="GiProbes"/>' <c>Constants</c>).</summary>
    const string TraceParams = """
        layout(set = 0, binding = 3, std140) uniform Params
        {
            vec4 uGiParams;
            vec4 uGiGrid[4];
            mat4 rotation;      // the frame's random rotation of the ray directions
            vec4 origin;        // xyz: the traced scene's origin, w: the frame
            vec4 sunLight;      // rgb: uAtmoSunLight
            vec4 lightDir;      // uAtmoLight (xyz the lighting direction, w the environment light's factor)
            vec4 maps;          // uAtmoMaps
            vec4 albedo;        // rgb: the objects' albedo, w: the terrain ground colour map present
            vec4 rayLength;     // x per cascade 0, y cascade 1, z the sleepers' period
            vec4 uLightGrid;    // the lamps (WorldLamps; w 0: none)
            vec4 extra;         // y: the far cascade's period
        };
        """;

    /// <summary>What a ray needs of the world at a hit: the probes, the lamps, the textures, the cut-out test (TRACE) and the lamps' visibility.</summary>
    static readonly string TraceShared = $$"""
        layout(set = 0, binding = 4) uniform sampler2D uGiIrradiance;
        layout(set = 0, binding = 5) uniform sampler2D uGiDistance;
        layout(set = 0, binding = 6) uniform sampler2D uGiBase;
        layout(set = 0, binding = 7) uniform samplerCube uAtmoIrradiance;
        layout(set = 0, binding = 8) uniform sampler2D uAtmoAmbientMap;
        layout(set = 0, binding = 9) uniform sampler2D uGround;
        layout(set = 0, binding = 10) uniform sampler2D uLightCells;
        layout(set = 0, binding = 11) uniform sampler2D uLightIndex;
        layout(set = 0, binding = 12) uniform sampler2D uLightData;
        #define LAMP_DIFFUSE_ONLY
        {{LampTileTrace}}
        // The bounces read the probes without the visibility test: a quarter of the fetches, and the light seen in a bounce is blurred anyway.
        #define GI_NO_VISIBILITY
        {{ProbeSampling}}
        {{HitFunctions}}
        """;

    /// <summary>
    /// The probe trace (docs/render-gi.md "Probes"): one workgroup per probe, one ray per invocation (<see cref="GiProbes.Rays"/>, spherical
    /// Fibonacci directions turned by the frame's random rotation). A ray that hits sees the surface lit as the lighting pass lights it, without
    /// specular: a constant albedo (the terrain: the ground colour map) times the sun through a shadow ray and the probes' own irradiance of the
    /// last frame there (the bounces beyond the first). A ray that misses sees the lighting pass's ambient in its direction. Back faces of objects
    /// count as dark and give a negative distance (the blend marks probes that see many as inside).
    /// </summary>
    public static string ProbeTrace(int rays) => $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = {{rays}}) in;
        {{Gpu.BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform accelerationStructureEXT uScene;
        {{GeometryStruct}}
        layout(set = 0, binding = 1, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 2, std430) writeonly buffer RayResults { vec4 rays[]; };
        layout(set = 0, binding = 3, std140) uniform Params
        {
            vec4 uGiParams;
            vec4 uGiGrid[4];
            mat4 rotation;      // the frame's random rotation of the ray directions
            vec4 origin;        // xyz: the traced scene's origin, w: the frame
            vec4 sunLight;      // rgb: uAtmoSunLight
            vec4 lightDir;      // uAtmoLight (xyz the lighting direction, w the environment light's factor)
            vec4 maps;          // uAtmoMaps
            vec4 albedo;        // rgb: the objects' albedo, w: the terrain ground colour map present
            vec4 rayLength;     // x per cascade 0, y cascade 1, z the sleepers' period
            vec4 uLightGrid;    // the lamps (WorldLamps; w 0: none)
            vec4 extra;         // y: the far cascade's period
        };
        layout(set = 0, binding = 13, std430) readonly buffer ProbeState { vec4 positions[]; };
        layout(set = 0, binding = 14, std430) readonly buffer ProbeList { uint probeList[]; };
        {{Sleep}}
        layout(set = 0, binding = 4) uniform sampler2D uGiIrradiance;
        layout(set = 0, binding = 5) uniform sampler2D uGiDistance;
        layout(set = 0, binding = 6) uniform sampler2D uGiBase;
        layout(set = 0, binding = 7) uniform samplerCube uAtmoIrradiance;
        layout(set = 0, binding = 8) uniform sampler2D uAtmoAmbientMap;
        layout(set = 0, binding = 9) uniform sampler2D uGround;
        layout(set = 0, binding = 10) uniform sampler2D uLightCells;
        layout(set = 0, binding = 11) uniform sampler2D uLightIndex;
        layout(set = 0, binding = 12) uniform sampler2D uLightData;
        #define LAMP_DIFFUSE_ONLY
        {{LampTileTrace}}
        // The bounces read the probes without the visibility test: a quarter of the fetches, and the light seen in a bounce is blurred anyway.
        #define GI_NO_VISIBILITY
        {{ProbeSampling}}
        {{HitFunctions}}

        vec3 fibonacci(uint i, uint n)
        {
            float k = float(i) + 0.5;
            float y = 1.0 - 2.0 * k / float(n);
            float r = sqrt(max(1.0 - y * y, 0.0));
            float a = k * 2.39996323;
            return vec3(cos(a) * r, y, sin(a) * r);
        }

        void main()
        {
            // This frame's probes: the CPU's list (GiProbes.BuildList; bit 31 a slow update, the rays are stored by workgroup).
            uint probe = probeList[gl_WorkGroupID.x] & 0x3FFFFFFFu, ray = gl_LocalInvocationID.x;
            // probe = ((cascade · N + window row) · N + window column) · layers + layer
            int layer = int(probe % uint(GI_NY)), rest = int(probe / uint(GI_NY));
            int wx = rest % GI_N, wz = (rest / GI_N) % GI_N, c = rest / (GI_N * GI_N);
            vec4 g0 = uGiGrid[c * 2], g1 = uGiGrid[c * 2 + 1];
            int colX = (int(g1.x) + wx) % GI_N, colZ = (int(g1.y) + wz) % GI_N;
            float base = texelFetch(uGiBase, ivec2(colX, c * GI_N + colZ), 0).r;
            vec3 world = vec3(g0.x + float(wx) * g0.z, base + (float(layer) + 0.5) * g0.w, g0.y + float(wz) * g0.z);
            uint stateSlot = uint((c * GI_N + colZ) * GI_N + colX) * uint(GI_NY) + uint(layer);
            if (giAsleep(positions[stateSlot], world, stateSlot)) return;   // the whole workgroup: the blend skips it too
            vec3 dir = normalize(mat3(rotation) * fibonacci(ray, gl_WorkGroupSize.x));
            float tMax = c == 0 ? rayLength.x : rayLength.y;

            rayQueryEXT rq;
            rayQueryInitializeEXT(rq, uScene, gl_RayFlagsCullBackFacingTrianglesEXT, {{TracedSunShaders.GiMask}}, world - origin.xyz, 0.0, dir, tMax);
            TRACE(rq)
            float env = lightDir.w;
            vec4 am = ambientMap(world);
            uint slot = gl_WorkGroupID.x * gl_WorkGroupSize.x + ray;
            if (rayQueryGetIntersectionTypeEXT(rq, true) != gl_RayQueryCommittedIntersectionTriangleEXT)
            {
                rays[slot] = vec4(skyIrradiance(dir) * {{F(1 - Data.World.KenshiLighting.DielectricSpecular)}} * am.rgb * env, 1e6);
                return;
            }
            float t = rayQueryGetIntersectionTEXT(rq, true);
            int record = rayQueryGetIntersectionInstanceCustomIndexEXT(rq, true) + rayQueryGetIntersectionGeometryIndexEXT(rq, true);
            Geometry g = geometries[record];
            // The back of an object is inside it (dark; the blend counts these). Foliage is open and double-sided: its back is a surface.
            if (!rayQueryGetIntersectionFrontFaceEXT(rq, true) && g.kind != {{GeometryRecord.Foliage}}u)
            {
                rays[slot] = vec4(0.0, 0.0, 0.0, -0.2 * t);
                return;
            }
        {{HitShade}}
            rays[slot] = vec4(colour * (sun + ambient + lamps), t);
        }
        """;

    /// <summary>
    /// The probe blend: one workgroup per probe. It reads the probe's rays and blends them into its irradiance tile (cosine weights) and its
    /// distance tile (the mean distance and its square, sharpened weights, over <c>uGiGrid[2c + 1].z</c>), borders included, over the last
    /// values by the hysteresis, including validity. A probe whose position changed starts over. Back faces in a quarter of the rays
    /// lower its validity towards zero, so a noisy occupancy estimate cannot switch a surface's light on/off each update.
    /// </summary>
    public static string ProbeBlend(int rays) => $$"""
        #version 460
        layout(local_size_x = {{rays}}) in;
        const int GI_N = {{GiProbes.Columns}}, GI_NY = {{GiProbes.Layers}}, GI_IRR = {{GiProbes.IrradianceTexels}}, GI_DIST = {{GiProbes.DistanceTexels}};
        layout(set = 0, binding = 0, std430) readonly buffer RayResults { vec4 rays[]; };
        layout(set = 0, binding = 1, std430) buffer ProbeState { vec4 positions[]; };
        layout(set = 0, binding = 2, std140) uniform Params
        {
            vec4 uGiParams;     // y: this frame's phase, z: the phases, w: the hysteresis per update
            vec4 uGiGrid[4];
            mat4 rotation;
            vec4 origin;
            vec4 sunLight;
            vec4 lightDir;
            vec4 maps;
            vec4 albedo;
            vec4 rayLength;     // z: the sleepers' period, w: the hidden probes' period
            vec4 uLightGrid;
            vec4 extra;         // y: the far cascade's period
        };
        layout(set = 0, binding = 3, rgba16f) uniform image2D uIrradiance;
        layout(set = 0, binding = 4, rg16f) uniform image2D uDistance;
        layout(set = 0, binding = 5) uniform sampler2D uGiBase;
        layout(set = 0, binding = 6, std430) readonly buffer ProbeList { uint probeList[]; };
        {{Sleep}}
        shared vec4 sRays[{{rays}}];
        shared vec3 sDirs[{{rays}}];
        shared uint sBack, sNear;
        shared bool sMoved;
        // The inner texels' new values: the borders copy them (only the inner texels are blended).
        shared vec4 sIrradiance[GI_IRR * GI_IRR];
        shared vec2 sDistance[GI_DIST * GI_DIST];

        vec3 fibonacci(uint i, uint n)
        {
            float k = float(i) + 0.5;
            float y = 1.0 - 2.0 * k / float(n);
            float r = sqrt(max(1.0 - y * y, 0.0));
            float a = k * 2.39996323;
            return vec3(cos(a) * r, y, sin(a) * r);
        }
        vec3 octDir(vec2 p)
        {
            vec3 d = vec3(p.x, 1.0 - abs(p.x) - abs(p.y), p.y);
            if (d.y < 0.0) d.xz = (1.0 - abs(d.zx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.z >= 0.0 ? 1.0 : -1.0);
            return normalize(d);
        }
        // A tile texel (border included) to the inner texel whose value it holds.
        ivec2 inner(ivec2 t, int size)
        {
            int last = size + 1;
            if ((t.x == 0 || t.x == last) && (t.y == 0 || t.y == last)) return ivec2(t.x == 0 ? size : 1, t.y == 0 ? size : 1);
            if (t.y == 0) return ivec2(last - t.x, 1);
            if (t.y == last) return ivec2(last - t.x, size);
            if (t.x == 0) return ivec2(1, last - t.y);
            if (t.x == last) return ivec2(size, last - t.y);
            return t;
        }

        void main()
        {
            uint entry = probeList[gl_WorkGroupID.x], probe = entry & 0x3FFFFFFFu, id = gl_LocalInvocationID.x, count = gl_WorkGroupSize.x;
            int layer = int(probe % uint(GI_NY)), rest = int(probe / uint(GI_NY));
            int wx = rest % GI_N, wz = (rest / GI_N) % GI_N, c = rest / (GI_N * GI_N);
            vec4 g0 = uGiGrid[c * 2], g1 = uGiGrid[c * 2 + 1];
            int colX = (int(g1.x) + wx) % GI_N, colZ = (int(g1.y) + wz) % GI_N;
            float base = texelFetch(uGiBase, ivec2(colX, c * GI_N + colZ), 0).r;
            vec3 world = vec3(g0.x + float(wx) * g0.z, base + (float(layer) + 0.5) * g0.w, g0.y + float(wz) * g0.z);
            // The probe's state slot is its tile (where it lives in the atlas), so a scrolled window finds the old probe there.
            uint stateSlot = uint((c * GI_N + colZ) * GI_N + colX) * uint(GI_NY) + uint(layer);
            vec4 last = positions[stateSlot];
            if (giAsleep(last, world, stateSlot)) return;   // the trace wrote no rays for it: its tiles stay as they are

            sRays[id] = rays[gl_WorkGroupID.x * count + id];
            sDirs[id] = normalize(mat3(rotation) * fibonacci(id, count));
            if (id == 0u)
            {
                sBack = 0u;
                sNear = 0u;
                sMoved = last.w < 0.5 || distance(last.xyz, world) > 0.5;
            }
            barrier();
            // A surface within reach: a point some pixel's lookup (its cell, plus the lookup's normal and view offset) or a bounce weights this probe
            // from. Back faces count (geometry around it). Nothing within reach: the probe sleeps until a later update finds something.
            float reach = length(vec3(g0.z, g0.w, g0.z)) + 0.3 * min(g0.z, g0.w);
            if (sRays[id].w < 0.0) atomicAdd(sBack, 1u);
            if (sRays[id].w < reach) atomicOr(sNear, 1u);
            barrier();
            if (id == 0u) positions[stateSlot] = vec4(world, sNear != 0u ? 1.0 : 2.0);
            bool valid = float(sBack) < 0.25 * float(count);
            // A hidden probe (bit 31) is updated every rayLength.w-th round: the hysteresis of that many updates in one.
            float hysteresis = sMoved ? 0.0 : ((entry >> 31) != 0u ? pow(uGiParams.w, rayLength.w) : (entry >> 30 & 1u) != 0u ? pow(uGiParams.w, extra.y) : uGiParams.w);
            ivec2 tileIndex = ivec2(colX, (c * GI_N + colZ) * GI_NY + layer);
            float maxDistance = g1.z;

            ivec2 irrOrigin = tileIndex * (GI_IRR + 2);
            for (int t = int(id); t < GI_IRR * GI_IRR; t += int(count))
            {
                ivec2 src = ivec2(t % GI_IRR, t / GI_IRR) + 1;
                vec3 dir = octDir((vec2(src - 1) + 0.5) / float(GI_IRR) * 2.0 - 1.0);
                vec3 sum = vec3(0.0);
                float total = 0.0;
                for (uint r = 0u; r < count; r++)
                {
                    float w = max(dot(dir, sDirs[r]), 0.0);
                    sum += sRays[r].rgb * w;
                    total += w;
                }
                vec3 value = total > 1e-4 ? sum / total : vec3(0.0);
                vec4 old = imageLoad(uIrradiance, irrOrigin + src);
                vec4 blended = mix(vec4(value, valid ? 1.0 : 0.0), old, hysteresis);
                if (!valid && blended.a < 0.001) blended.a = 0.0;
                sIrradiance[t] = blended;
            }

            ivec2 distOrigin = tileIndex * (GI_DIST + 2);
            for (int t = int(id); t < GI_DIST * GI_DIST; t += int(count))
            {
                ivec2 src = ivec2(t % GI_DIST, t / GI_DIST) + 1;
                vec3 dir = octDir((vec2(src - 1) + 0.5) / float(GI_DIST) * 2.0 - 1.0);
                vec2 sum = vec2(0.0);
                float total = 0.0;
                for (uint r = 0u; r < count; r++)
                {
                    // cos^50 is below 1e-11 under 0.6 (and 64 rays always have one above it): skip those rays.
                    float cosine = dot(dir, sDirs[r]);
                    if (cosine < 0.6) continue;
                    float c2 = cosine * cosine, c4 = c2 * c2, c8 = c4 * c4, c16 = c8 * c8, c32 = c16 * c16;
                    float w = c32 * c16 * c2;   // cos^50
                    float d = min(abs(sRays[r].w), maxDistance) / maxDistance;
                    sum += vec2(d, d * d) * w;
                    total += w;
                }
                vec2 value = total > 1e-4 ? sum / total : vec2(1.0);
                vec2 old = imageLoad(uDistance, distOrigin + src).rg;
                sDistance[t] = mix(value, old, hysteresis);
            }
            barrier();

            // Every texel of the tiles, the borders from the inner texel they copy (bilinear filtering across the octahedron's seams).
            for (int t = int(id); t < (GI_IRR + 2) * (GI_IRR + 2); t += int(count))
            {
                ivec2 texel = ivec2(t % (GI_IRR + 2), t / (GI_IRR + 2)), src = inner(texel, GI_IRR) - 1;
                imageStore(uIrradiance, irrOrigin + texel, sIrradiance[src.y * GI_IRR + src.x]);
            }
            for (int t = int(id); t < (GI_DIST + 2) * (GI_DIST + 2); t += int(count))
            {
                ivec2 texel = ivec2(t % (GI_DIST + 2), t / (GI_DIST + 2)), src = inner(texel, GI_DIST) - 1;
                imageStore(uDistance, distOrigin + texel, vec4(sDistance[src.y * GI_DIST + src.x], 0.0, 0.0));
            }
        }
        """;

    /// <summary>
    /// The probe invalidation, run before the trace on a frame whose grids scrolled (or whose bases changed): one invocation per probe. A probe
    /// whose tile still holds another position (the column that left the grid on the far side) gets alpha 0 over its irradiance tile, so neither
    /// the surfaces nor the bounces read the old values until its own update starts it over (with the round robin, up to Phases − 1 frames later).
    /// </summary>
    public static readonly string ProbeInvalidate = $$"""
        #version 460
        layout(local_size_x = 64) in;
        const int GI_N = {{GiProbes.Columns}}, GI_NY = {{GiProbes.Layers}}, GI_IRR = {{GiProbes.IrradianceTexels}};
        layout(set = 0, binding = 0, std430) readonly buffer ProbeState { vec4 positions[]; };
        layout(set = 0, binding = 1, std140) uniform Params { vec4 uGiParams; vec4 uGiGrid[4]; };
        layout(set = 0, binding = 2, rgba16f) uniform image2D uIrradiance;
        layout(set = 0, binding = 3) uniform sampler2D uGiBase;

        void main()
        {
            uint probe = gl_GlobalInvocationID.x;
            int layer = int(probe % uint(GI_NY)), rest = int(probe / uint(GI_NY));
            int wx = rest % GI_N, wz = (rest / GI_N) % GI_N, c = rest / (GI_N * GI_N);
            if (c >= {{GiProbes.Cascades}}) return;
            vec4 g0 = uGiGrid[c * 2], g1 = uGiGrid[c * 2 + 1];
            int colX = (int(g1.x) + wx) % GI_N, colZ = (int(g1.y) + wz) % GI_N;
            float base = texelFetch(uGiBase, ivec2(colX, c * GI_N + colZ), 0).r;
            vec3 world = vec3(g0.x + float(wx) * g0.z, base + (float(layer) + 0.5) * g0.w, g0.y + float(wz) * g0.z);
            uint stateSlot = uint((c * GI_N + colZ) * GI_N + colX) * uint(GI_NY) + uint(layer);
            vec4 last = positions[stateSlot];
            if (last.w < 0.5 || distance(last.xyz, world) <= 0.5) return;
            ivec2 origin = ivec2(colX, (c * GI_N + colZ) * GI_NY + layer) * (GI_IRR + 2);
            for (int y = 0; y < GI_IRR + 2; y++)
                for (int x = 0; x < GI_IRR + 2; x++)
                {
                    ivec2 t = origin + ivec2(x, y);
                    imageStore(uIrradiance, t, vec4(imageLoad(uIrradiance, t).rgb, 0.0));
                }
        }
        """;

    const string CascadeAnchor = "vec4 giCascade(int c, vec3 p, vec3 n, vec3 v, float push)";
    const string TileAnchor = "ivec2 tile = ivec2(colX, (c * GI_N + colZ) * GI_NY + layer);";
    const string SleepAnchor = "if (giAsleep(positions[stateSlot], world, stateSlot)) return;   // the whole workgroup: the blend skips it too";

    /// <summary>
    /// A relocated probe's offset from its grid place (docs/render-gi.md "Probe relocation"): three bytes of half a cell, 127 none, as one exact
    /// integer in <c>uGiBase</c>'s rows below the column bases, one texel per atlas tile (<see cref="ProbeOffsetPublish"/> writes them).
    /// </summary>
    const string OffsetLookup = """
        vec3 giProbeOffset(ivec2 tile, float s, float sy)
        {
            uint code = uint(texelFetch(uGiBase, ivec2(tile.x, GI_CASCADES * GI_N + tile.y), 0).r);
            return (vec3(float(code & 255u), float((code >> 8) & 255u), float(code >> 16)) / 127.0 - 1.0) * 0.5 * vec3(s, sy, s);
        }

        """;

    /// <summary>
    /// <paramref name="shader"/> (any text holding <see cref="ProbeSampling"/>) reading probes at their relocated places while <c>uGiGrid[2c + 1].w</c>
    /// is set. The text of <see cref="ProbeSampling"/> stays as it is so that programs that never read relocated probes do not change.
    /// </summary>
    public static string Relocating(string shader)
    {
        if (!shader.Contains(CascadeAnchor, StringComparison.Ordinal)) return shader;
        if (!shader.Contains(TileAnchor, StringComparison.Ordinal)) throw new InvalidOperationException("Relocating: no probe tile line");
        return shader.Replace(CascadeAnchor, OffsetLookup + CascadeAnchor, StringComparison.Ordinal)
            .Replace(TileAnchor, TileAnchor + "\n                    if (g1.w > 0.5) probe += giProbeOffset(tile, s, sy);", StringComparison.Ordinal);
    }

    const string ParamsEnd = "vec4 extra;         // y: the far cascade's period";
    const string SamplersEnd = "layout(set = 0, binding = 12) uniform sampler2D uLightData;";
    const string HitSun = "* nl * sunLight.rgb * amHit.a * 2.0;";

    /// <summary>
    /// A hit shader (<see cref="ProbeTrace(int)"/>'s sun line and block) with the cloud transmission on the sun at its hits: the same function the lit
    /// fragments use (docs/render-clouds.md), the map at bindings 15 and 16, the three values after <c>extra</c>.
    /// </summary>
    internal static string Clouded(string trace)
    {
        foreach (string anchor in (ReadOnlySpan<string>)[ParamsEnd, SamplersEnd, HitSun])
            if (!trace.Contains(anchor, StringComparison.Ordinal)) throw new InvalidOperationException("Clouded: no line " + anchor);
        const string extraSamplers = "\nlayout(set = 0, binding = 15) uniform sampler2D uAtmoClouds;\nlayout(set = 0, binding = 16) uniform sampler2D uAtmoCloudsTile;\n";
        return trace.Replace(ParamsEnd, ParamsEnd + "\n    vec4 uAtmoCloud;\n    vec4 uAtmoCloudWind;\n    vec4 uAtmoSun;", StringComparison.Ordinal)
            .Replace(SamplersEnd, SamplersEnd + extraSamplers + AtmosphereShaders.CloudShadow, StringComparison.Ordinal)
            .Replace(HitSun, HitSun[..^1] + " * atmoCloudShadow(hit);", StringComparison.Ordinal);
    }

    /// <summary>The probe trace for the frame's options: rays leaving from the relocated places (the bounces reading those places too), the cloud shadows on the hits.</summary>
    public static string ProbeTrace(int rays, bool relocate, bool clouds)
    {
        string trace = ProbeTrace(rays);
        if (clouds) trace = Clouded(trace);
        if (!relocate) return trace;
        if (!trace.Contains(SleepAnchor, StringComparison.Ordinal)) throw new InvalidOperationException("ProbeTrace: no sleep line");
        return Relocating(trace).Replace(SleepAnchor,
            SleepAnchor + "\n            world += giProbeOffset(ivec2(colX, (c * GI_N + colZ) * GI_NY + layer), g0.z, g0.w);", StringComparison.Ordinal);
    }

    /// <summary>
    /// Every probe once a frame while relocation is on: a probe whose tile holds another place (it scrolled) drops its offset; every offset goes into
    /// this frame slot's <c>uGiBase</c> rows, where the surfaces and the trace read it (<see cref="Relocating"/>).
    /// </summary>
    public static readonly string ProbeOffsetPublish = $$"""
        #version 460
        layout(local_size_x = 64) in;
        const int GI_N = {{GiProbes.Columns}}, GI_NY = {{GiProbes.Layers}}, GI_CASCADES = {{GiProbes.Cascades}};
        layout(set = 0, binding = 0, std430) readonly buffer ProbeState { vec4 positions[]; };
        layout(set = 0, binding = 1, std140) uniform Params { vec4 uGiParams; vec4 uGiGrid[4]; };
        layout(set = 0, binding = 2, r32f) uniform image2D uBases;
        layout(set = 0, binding = 3, std430) buffer ProbeOffsets { vec4 offsets[]; };

        void main()
        {
            uint probe = gl_GlobalInvocationID.x;
            int layer = int(probe % uint(GI_NY)), rest = int(probe / uint(GI_NY));
            int wx = rest % GI_N, wz = (rest / GI_N) % GI_N, c = rest / (GI_N * GI_N);
            if (c >= GI_CASCADES) return;
            vec4 g0 = uGiGrid[c * 2], g1 = uGiGrid[c * 2 + 1];
            int colX = (int(g1.x) + wx) % GI_N, colZ = (int(g1.y) + wz) % GI_N;
            float base = imageLoad(uBases, ivec2(colX, c * GI_N + colZ)).r;
            vec3 world = vec3(g0.x + float(wx) * g0.z, base + (float(layer) + 0.5) * g0.w, g0.y + float(wz) * g0.z);
            uint stateSlot = uint((c * GI_N + colZ) * GI_N + colX) * uint(GI_NY) + uint(layer);
            vec4 last = positions[stateSlot];
            if (last.w < 0.5 || distance(last.xyz, world) > 0.5) offsets[stateSlot] = vec4(0.0);
            vec3 share = clamp(offsets[stateSlot].xyz / (0.5 * vec3(g0.z, g0.w, g0.z)), -1.0, 1.0);
            uvec3 code = uvec3(round((share + 1.0) * 127.0));
            imageStore(uBases, ivec2(colX, GI_CASCADES * GI_N + (c * GI_N + colZ) * GI_NY + layer), vec4(float(code.x | (code.y << 8) | (code.z << 16))));
        }
        """;

    /// <summary>
    /// The probe relocation (RTXGI's rule, docs/render-gi.md "Probe relocation"), before the blend while the probes' states are the ones the trace read. Inside
    /// geometry (over a quarter of the rays hit back faces): through the closest back face. Too close to a front face: away from it, towards the
    /// farthest open direction when that lies on the other side. Clear of everything: back towards the grid place, at most a quarter of the wanted clearance
    /// an update, without crossing the closest surface. A move past 0.45 of a cell is not made: such a probe stays invalid (the blend's back-face count).
    /// </summary>
    public static string ProbeRelocate(int rays) => $$"""
        #version 460
        layout(local_size_x = 1) in;
        const int GI_N = {{GiProbes.Columns}}, GI_NY = {{GiProbes.Layers}}, RAYS = {{rays}};
        layout(set = 0, binding = 0, std430) readonly buffer RayResults { vec4 rays[]; };
        layout(set = 0, binding = 1, std430) readonly buffer ProbeState { vec4 positions[]; };
        layout(set = 0, binding = 2, std140) uniform Params
        {
            vec4 uGiParams;
            vec4 uGiGrid[4];
            mat4 rotation;
            vec4 origin;
            vec4 sunLight;
            vec4 lightDir;
            vec4 maps;
            vec4 albedo;
            vec4 rayLength;
        };
        layout(set = 0, binding = 3) uniform sampler2D uGiBase;
        layout(set = 0, binding = 4, std430) readonly buffer ProbeList { uint probeList[]; };
        layout(set = 0, binding = 5, std430) buffer ProbeOffsets { vec4 offsets[]; };
        {{Sleep}}
        const float BACK_SHARE = 0.25, MAX_OFFSET = 0.45;

        vec3 fibonacci(uint i, uint n)
        {
            float k = float(i) + 0.5;
            float y = 1.0 - 2.0 * k / float(n);
            float r = sqrt(max(1.0 - y * y, 0.0));
            float a = k * 2.39996323;
            return vec3(cos(a) * r, y, sin(a) * r);
        }
        vec3 rayDir(uint r) { return normalize(mat3(rotation) * fibonacci(r, uint(RAYS))); }

        // The offset this update wants; offset itself when it wants no move.
        vec3 relocated(uint entry, vec3 offset, float clearance)
        {
            uint backs = 0u;
            int back = -1, near = -1, far = -1;
            float backDistance = 1e9, nearDistance = 1e9, farDistance = -1.0;
            for (uint r = 0u; r < uint(RAYS); r++)
            {
                float t = rays[entry * uint(RAYS) + r].w;
                if (t < 0.0)
                {
                    backs++;
                    t *= -5.0;   // the trace stores a back face at -0.2 t
                    if (t < backDistance) { backDistance = t; back = int(r); }
                    continue;
                }
                if (t < nearDistance) { nearDistance = t; near = int(r); }
                if (t > farDistance) { farDistance = t; far = int(r); }
            }
            if (back >= 0 && float(backs) > BACK_SHARE * float(RAYS))
                return offset + rayDir(uint(back)) * (backDistance + 0.5 * clearance);
            if (near >= 0 && nearDistance < clearance)
            {
                // The farthest ray is the closest one when the probe sees one surface: no way to tell where to go.
                if (dot(rayDir(uint(near)), rayDir(uint(far))) > 0.0) return offset;
                return offset + rayDir(uint(far)) * min(farDistance, 0.5 * clearance);
            }
            float reach = length(offset);
            float home = min(min(nearDistance - clearance, reach), 0.25 * clearance);
            return reach > 1e-3 && home > 0.0 ? offset - offset / reach * home : offset;
        }

        void main()
        {
            uint entry = gl_GlobalInvocationID.x, probe = probeList[entry] & 0x3FFFFFFFu;
            int layer = int(probe % uint(GI_NY)), rest = int(probe / uint(GI_NY));
            int wx = rest % GI_N, wz = (rest / GI_N) % GI_N, c = rest / (GI_N * GI_N);
            vec4 g0 = uGiGrid[c * 2], g1 = uGiGrid[c * 2 + 1];
            int colX = (int(g1.x) + wx) % GI_N, colZ = (int(g1.y) + wz) % GI_N;
            float base = texelFetch(uGiBase, ivec2(colX, c * GI_N + colZ), 0).r;
            vec3 world = vec3(g0.x + float(wx) * g0.z, base + (float(layer) + 0.5) * g0.w, g0.y + float(wz) * g0.z);
            uint stateSlot = uint((c * GI_N + colZ) * GI_N + colX) * uint(GI_NY) + uint(layer);
            if (giAsleep(positions[stateSlot], world, stateSlot)) return;   // the trace wrote no rays for it
            vec3 spacing = vec3(g0.z, g0.w, g0.z);
            vec3 offset = offsets[stateSlot].xyz, wanted = relocated(entry, offset, 0.2 * min(g0.z, g0.w));
            vec3 share = wanted / spacing;
            if (dot(share, share) < MAX_OFFSET * MAX_OFFSET) offsets[stateSlot] = vec4(wanted, 0.0);
        }
        """;

    /// <summary>The longest reflection ray (the near cascade's reach; the traced objects end at 6000): it fades to the sky over its last quarter, the hit being unhazed.</summary>
    public const float ReflectionRange = 4000;

    /// <summary>
    /// The reflections' rays (docs/render-gi.md "Reflections"): one per glossy texel of <c>uHit</c>, the march's ray (<see cref="SsrShaders.March"/>) from the block's first
    /// glossy pixel, against the traced scene and its characters, its hit lit as a probe ray's: a hit replaces the march (world space first); a miss keeps what the march
    /// found (grass, water and particles are not traced) and proves the sky open past it. Writes a below 0 (<see cref="SsrShaders.Shade"/>). No specular at the hits.
    /// </summary>
    public static readonly string Reflect = $$"""
        #version 460
        {{TraceExtensions}}
        layout(local_size_x = 8, local_size_y = 8) in;
        {{TraceScene}}
        layout(set = 0, binding = 2, rgba16f) uniform image2D uHit;
        {{TraceParams}}
        {{TraceShared}}

        layout(set = 0, binding = 13) uniform usampler2D uAlbedo;
        layout(set = 0, binding = 14) uniform sampler2D uNearDepth;
        layout(set = 0, binding = 15, std140) uniform Camera
        {
            vec4 eye, right, up, back;
            vec4 view;         // xy: tan(fov/2) * aspect, tan(fov/2), zw: near, far
            vec4 sizes;        // xy: the render size, z: the block's size
        } cam;
        {{DepthConvention.Glsl}}
        {{SsrShaders.Common}}

        void main()
        {
            ivec2 cell = ivec2(gl_GlobalInvocationID.xy);
            if (any(greaterThanEqual(cell, imageSize(uHit)))) return;
            vec4 marched = imageLoad(uHit, cell);
            uvec4 surface;
            ivec2 px = ssrPick(cell * int(cam.sizes.z), int(cam.sizes.z), ivec2(cam.sizes.xy), surface);
            if (px.x < 0) return;
            float gloss = ssrGloss(surface);
            if (ssrWeight(gloss) <= 0.0) return;
            float depth = texelFetch(uNearDepth, px, 0).r;
            if (depthIsSky(depth)) return;
            float z = depthViewZ(depth, cam.view.zw);
            vec2 ndc = (vec2(px) + 0.5) / cam.sizes.xy * 2.0 - 1.0;
            vec3 surfacePoint = cam.eye.xyz + (cam.right.xyz * ndc.x * cam.view.x + cam.up.xyz * ndc.y * cam.view.y - cam.back.xyz) * z;
            vec3 surfaceNormal = ssrNormal(surface);
            vec3 dir = ssrDirection(surfaceNormal, normalize(cam.eye.xyz - surfacePoint), gloss);
            vec3 world = surfacePoint + surfaceNormal * (1.0 + z * 0.001);

            rayQueryEXT rq;
            rayQueryInitializeEXT(rq, uScene, gl_RayFlagsCullBackFacingTrianglesEXT, {{TracedSunShaders.GiMask | TracedSunShaders.Characters}}, world - origin.xyz, 0.0, dir, {{F(ReflectionRange)}});
            TRACE(rq)
            if (rayQueryGetIntersectionTypeEXT(rq, true) != gl_RayQueryCommittedIntersectionTriangleEXT)
            {
                imageStore(uHit, cell, vec4(marched.rgb, ssrPackHit(ssrHitShare(marched.a), ssrHitDistance(marched.a), true)));
                return;
            }
            float t = rayQueryGetIntersectionTEXT(rq, true);
            int record = rayQueryGetIntersectionInstanceCustomIndexEXT(rq, true) + rayQueryGetIntersectionGeometryIndexEXT(rq, true);
            Geometry g = geometries[record];
            float env = lightDir.w;
        {{HitShadeAt("1.0")}}
            float reach = 1.0 - smoothstep(0.75, 1.0, t / {{F(ReflectionRange)}});
            imageStore(uHit, cell, vec4(colour * (sun + ambient + lamps), ssrPackHit(reach, t, true)));
        }
        """;


    /// <summary>
    /// The debug view (<c>--gi-debug</c>): per pixel of the scene depth, the surface's position (the depth back through the projection) and a
    /// normal from the depth's own slope, then rays into <see cref="GiScene"/>'s structure. Mode 1: one cosine-weighted ray, coloured by its hit
    /// distance (blue: it reached the sky). Mode 2: a grey "clay" picture lit by the sun (a shadow ray) and by what the bounce rays find: sunlit,
    /// ambient-lit grey surfaces where they hit, the sky's ambient where they miss (one bounce, no denoiser). Mode 3: rays from the eye through
    /// each pixel, coloured by the hit's geometric normal: the traced scene itself, with no raster involved.
    /// </summary>
    public const string Debug = """
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8) in;

        layout(set = 0, binding = 0) uniform sampler2D uDepth;
        layout(set = 0, binding = 1, rgba16f) uniform writeonly image2D uOut;
        layout(set = 0, binding = 2) uniform accelerationStructureEXT uScene;
        struct Geometry { uvec2 vertices; uvec2 indices; uint stride; uint kind; uint diffuse; uint alphaMap; float alphaThreshold; uint pad0; uint pad1; uint pad2; uint pad3; uint pad4; uint pad5; uint pad6; };
        layout(set = 0, binding = 3, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 4, std140) uniform Params
        {
            mat4 inverse;      // NDC -> position relative to the eye (the rotation-only view and the scene's projection, inverted)
            vec4 eye;          // xyz: the eye relative to the traced origin
            vec4 sunDir;       // xyz: towards the sun, w: the bounce rays' length
            vec4 sunColour;
            vec4 skyColour;    // the ambient from above
            vec4 groundColour; // the ambient from below
            uvec4 info;        // width, height, mode, frame
            vec4 extra;        // samples, exposure, albedo
        } p;
        """ + DepthConvention.Glsl + """

        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Position { vec3 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Triangle { uvec3 v; };

        uvec2 offsetBy(uvec2 a, uint bytes)
        {
            uint lo = a.x + bytes;
            return uvec2(lo, a.y + (lo < a.x ? 1u : 0u));
        }

        uint hash(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
            return x;
        }

        float random(inout uint state) { state = hash(state); return float(state >> 8) * (1.0 / 16777216.0); }

        vec3 cosineDirection(vec3 n, inout uint state)
        {
            float u = random(state), v = random(state);
            float r = sqrt(u), a = 6.2831853 * v;
            vec3 t = normalize(abs(n.y) < 0.99 ? cross(n, vec3(0, 1, 0)) : cross(n, vec3(1, 0, 0)));
            vec3 b = cross(n, t);
            return normalize(t * (r * cos(a)) + b * (r * sin(a)) + n * sqrt(max(1.0 - u, 0.0)));
        }

        vec3 ambient(vec3 n) { return mix(p.groundColour.rgb, p.skyColour.rgb, 0.5 + 0.5 * n.y); }

        vec3 surface(vec2 uv)
        {
            float d = textureLod(uDepth, uv, 0.0).r;
            vec4 q = p.inverse * vec4(uv * 2.0 - 1.0, d, 1.0);   // the stored depth is the clip depth
            return q.xyz / q.w;
        }

        // The committed hit's geometric normal in world space, facing the ray.
        vec3 hitNormal(rayQueryEXT rq, vec3 dir, out uint kind)
        {
            int record = rayQueryGetIntersectionInstanceCustomIndexEXT(rq, true) + rayQueryGetIntersectionGeometryIndexEXT(rq, true);
            Geometry g = geometries[record];
            kind = g.kind;
            uvec3 tri = Triangle(offsetBy(g.indices, uint(rayQueryGetIntersectionPrimitiveIndexEXT(rq, true)) * 12u)).v;
            vec3 a = Position(offsetBy(g.vertices, tri.x * g.stride)).v;
            vec3 b = Position(offsetBy(g.vertices, tri.y * g.stride)).v;
            vec3 c = Position(offsetBy(g.vertices, tri.z * g.stride)).v;
            vec3 n = cross(b - a, c - a);
            mat4x3 w2o = rayQueryGetIntersectionWorldToObjectEXT(rq, true);
            n = normalize(n * mat3(w2o));
            return dot(n, dir) > 0.0 ? -n : n;
        }

        bool visible(vec3 origin, vec3 dir, float tMax)
        {
            rayQueryEXT rq;
            rayQueryInitializeEXT(rq, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsCullBackFacingTrianglesEXT, 0x0F, origin, 0.0, dir, tMax);
            while (rayQueryProceedEXT(rq)) { }
            return rayQueryGetIntersectionTypeEXT(rq, true) == gl_RayQueryCommittedIntersectionNoneEXT;
        }

        vec3 display(vec3 c) { return pow(vec3(1.0) - exp(-c * p.extra.y), vec3(1.0 / 2.2)); }

        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(p.info.x) || px.y >= int(p.info.y)) return;
            vec2 texel = 1.0 / vec2(p.info.xy);
            vec2 uv = (vec2(px) + 0.5) * texel;
            uint mode = p.info.z;
            uint state = hash(uint(px.x) * 1973u + uint(px.y) * 9277u + p.info.w * 26699u);
            float rayLength = p.sunDir.w;

            if (mode == 3u)
            {
                vec4 q = p.inverse * vec4(uv * 2.0 - 1.0, DEPTH_NEAREST, 1.0);   // any point on the ray: the eye is the origin
                vec3 dir = normalize(q.xyz / q.w);
                rayQueryEXT rq;
                rayQueryInitializeEXT(rq, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsCullBackFacingTrianglesEXT, 0x0F, p.eye.xyz, 0.0, dir, 1e6);
                while (rayQueryProceedEXT(rq)) { }
                vec3 colour = vec3(0.0);
                if (rayQueryGetIntersectionTypeEXT(rq, true) == gl_RayQueryCommittedIntersectionTriangleEXT)
                {
                    uint kind;
                    vec3 n = hitNormal(rq, dir, kind);
                    colour = (n * 0.5 + 0.5) * (kind == 0u ? 0.6 : 1.0);
                }
                imageStore(uOut, px, vec4(colour, 1.0));
                return;
            }

            float d = textureLod(uDepth, uv, 0.0).r;
            if (depthIsSky(d)) { imageStore(uOut, px, vec4(mode == 1u ? vec3(0.25, 0.45, 0.8) : display(p.skyColour.rgb), 1.0)); return; }
            vec3 pos = surface(uv);
            // The normal from the neighbours on the side with the smaller step (an edge would bend it).
            vec3 l = surface(uv - vec2(texel.x, 0)), r = surface(uv + vec2(texel.x, 0));
            vec3 dn = surface(uv - vec2(0, texel.y)), up = surface(uv + vec2(0, texel.y));
            vec3 dx = abs(dot(r - pos, r - pos)) < abs(dot(pos - l, pos - l)) ? r - pos : pos - l;
            vec3 dy = abs(dot(up - pos, up - pos)) < abs(dot(pos - dn, pos - dn)) ? up - pos : pos - dn;
            vec3 n = normalize(cross(dx, dy));
            if (dot(n, pos) > 0.0) n = -n;
            float dist = length(pos);
            vec3 origin = p.eye.xyz + pos + n * (0.05 + dist * 0.002);

            if (mode == 1u)
            {
                vec3 dir = cosineDirection(n, state);
                rayQueryEXT rq;
                rayQueryInitializeEXT(rq, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsCullBackFacingTrianglesEXT, 0x0F, origin, 0.0, dir, rayLength);
                while (rayQueryProceedEXT(rq)) { }
                vec3 colour = vec3(0.25, 0.45, 0.8);
                if (rayQueryGetIntersectionTypeEXT(rq, true) == gl_RayQueryCommittedIntersectionTriangleEXT)
                {
                    float t = rayQueryGetIntersectionTEXT(rq, true) / rayLength;
                    colour = mix(vec3(1.0, 0.9, 0.2), vec3(0.8, 0.1, 0.05), sqrt(t));
                }
                imageStore(uOut, px, vec4(colour, 1.0));
                return;
            }

            float albedo = p.extra.z;
            vec3 sun = normalize(p.sunDir.xyz);
            vec3 direct = p.sunColour.rgb * max(dot(n, sun), 0.0) * (dot(n, sun) > 0.0 && visible(origin, sun, 1e5) ? 1.0 : 0.0);
            int samples = max(int(p.extra.x), 1);
            vec3 bounce = vec3(0.0);
            for (int s = 0; s < samples; s++)
            {
                vec3 dir = cosineDirection(n, state);
                rayQueryEXT rq;
                rayQueryInitializeEXT(rq, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsCullBackFacingTrianglesEXT, 0x0F, origin, 0.0, dir, rayLength);
                while (rayQueryProceedEXT(rq)) { }
                if (rayQueryGetIntersectionTypeEXT(rq, true) == gl_RayQueryCommittedIntersectionTriangleEXT)
                {
                    uint kind;
                    vec3 hn = hitNormal(rq, dir, kind);
                    vec3 hit = origin + dir * rayQueryGetIntersectionTEXT(rq, true);
                    vec3 hitOrigin = hit + hn * (0.05 + rayQueryGetIntersectionTEXT(rq, true) * 0.001);
                    float lit = dot(hn, sun) > 0.0 && visible(hitOrigin, sun, 1e5) ? max(dot(hn, sun), 0.0) : 0.0;
                    // The hit surface: grey, lit by the sun and by the flat ambient (the bounces beyond the first).
                    bounce += albedo * (p.sunColour.rgb * lit + ambient(hn));
                }
                else bounce += ambient(dir);
            }
            bounce /= float(samples);
            imageStore(uOut, px, vec4(display(albedo * (direct + bounce)), 1.0));
        }
        """;
}