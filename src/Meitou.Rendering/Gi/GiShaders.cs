namespace Meitou.Rendering.Gi;

/// <summary>The global illumination's shaders (docs/render-gi.md).</summary>
static class GiShaders
{
    static string F(float v) => v.ToString("0.0########", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The probe volume's layout and how a surface reads it (docs/render-gi.md "Probes"), shared by every world shader (through
    /// <see cref="AtmosphereShaders.Functions"/>, GLSL 330) and the probe trace (the bounces beyond the first). Needs <c>uGiParams</c>,
    /// <c>uGiGrid[4]</c>, <c>uGiIrradiance</c>, <c>uGiDistance</c> and <c>uGiBase</c> declared before it. Per cascade c: <c>uGiGrid[2c]</c> = the world x and z
    /// of window column 0, the spacing across and up; <c>uGiGrid[2c + 1]</c> = the toroidal offset of window column 0 in x and z, the distance
    /// atlas's normalisation, 0. A probe of window column (i, k), layer l stands at (x0 + i·s, base + (l + 0.5)·sy, z0 + k·s), base the terrain
    /// height of its column (<c>uGiBase</c>).
    /// </summary>
    public static readonly string ProbeSampling = $$"""

        const int GI_N = {{GiProbes.Columns}}, GI_NY = {{GiProbes.Layers}}, GI_IRR = {{GiProbes.IrradianceTexels}}, GI_DIST = {{GiProbes.DistanceTexels}}, GI_CASCADES = {{GiProbes.Cascades}};
        // Octahedral mapping about y: the upper hemisphere in the inner diamond.
        vec2 giOct(vec3 d)
        {
            d /= abs(d.x) + abs(d.y) + abs(d.z);
            vec2 p = d.xz;
            if (d.y < 0.0) p = (1.0 - abs(p.yx)) * vec2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
            return p;
        }
        vec3 giOctDir(vec2 p)
        {
            vec3 d = vec3(p.x, 1.0 - abs(p.x) - abs(p.y), p.y);
            if (d.y < 0.0) d.xz = (1.0 - abs(d.zx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.z >= 0.0 ? 1.0 : -1.0);
            return normalize(d);
        }
        // A probe's tile: x its toroidal column, y (cascade · N + toroidal row) · layers + layer; inner texels plus a border of one.
        vec2 giUv(ivec2 tile, vec3 dir, int inner, vec2 size)
        {
            return (vec2(tile) * float(inner + 2) + 1.0 + (giOct(dir) * 0.5 + 0.5) * float(inner)) / size;
        }
        // One cascade: rgb the irradiance (the lighting pass's envDiffuse), a how much of it to take (0 outside, 1 a cell inside its edge).
        vec4 giCascade(int c, vec3 p, vec3 n, vec3 v)
        {
            vec4 g0 = uGiGrid[c * 2], g1 = uGiGrid[c * 2 + 1];
            float s = g0.z, sy = g0.w;
            vec3 q = p + (n * 0.2 + v * 0.8) * (0.3 * min(s, sy));
            vec2 cell = (q.xz - g0.xy) / s;
            float edge = min(min(cell.x, cell.y), min(float(GI_N - 1) - cell.x, float(GI_N - 1) - cell.y));
            if (edge <= 0.0) return vec4(0.0);
            ivec2 i0 = ivec2(floor(cell));
            vec2 f = cell - vec2(i0);
            vec2 irrSize = vec2(float(GI_N * (GI_IRR + 2)), float(GI_CASCADES * GI_N * GI_NY * (GI_IRR + 2)));
            vec2 distSize = vec2(float(GI_N * (GI_DIST + 2)), float(GI_CASCADES * GI_N * GI_NY * (GI_DIST + 2)));
            vec3 sum = vec3(0.0);
            float total = 0.0;
            for (int k = 0; k < 4; k++)
            {
                ivec2 o = ivec2(k & 1, k >> 1);
                ivec2 w = min(i0 + o, ivec2(GI_N - 1));
                int colX = (int(g1.x) + w.x) % GI_N, colZ = (int(g1.y) + w.y) % GI_N;
                float base = texelFetch(uGiBase, ivec2(colX, c * GI_N + colZ), 0).r;
                float ly = clamp((q.y - base) / sy - 0.5, 0.0, float(GI_NY - 1));
                int l0 = min(int(ly), GI_NY - 2);
                float fy = ly - float(l0);
                float wxz = (o.x == 1 ? f.x : 1.0 - f.x) * (o.y == 1 ? f.y : 1.0 - f.y);
                for (int j = 0; j < 2; j++)
                {
                    int layer = l0 + j;
                    vec3 probe = vec3(g0.x + float(w.x) * s, base + (float(layer) + 0.5) * sy, g0.y + float(w.y) * s);
                    ivec2 tile = ivec2(colX, (c * GI_N + colZ) * GI_NY + layer);
                    float weight = wxz * (j == 1 ? fy : 1.0 - fy);
                    // Probes behind the surface count less (DDGI's smooth backface term).
                    float back = (dot(normalize(probe - p), n) + 1.0) * 0.5;
                    weight *= back * back + 0.2;
                    // Visibility: Chebyshev's bound on the probe's distance moments towards the point.
                    vec3 from = q - probe;
                    float d = length(from);
                    vec2 m = textureLod(uGiDistance, giUv(tile, from / max(d, 1e-4), GI_DIST, distSize), 0.0).rg * vec2(g1.z, g1.z * g1.z);
                    if (d > m.x)
                    {
                        float variance = abs(m.x * m.x - m.y);
                        float cheb = variance / (variance + (d - m.x) * (d - m.x));
                        weight *= max(cheb * cheb * cheb, 0.0);
                    }
                    vec4 irr = textureLod(uGiIrradiance, giUv(tile, n, GI_IRR, irrSize), 0.0);
                    weight *= irr.a;   // 0: a probe inside geometry
                    if (weight < 0.2) weight *= weight * weight * 25.0;
                    sum += irr.rgb * weight;
                    total += weight;
                }
            }
            return total > 1e-5 ? vec4(sum / total, clamp(edge, 0.0, 1.0)) : vec4(0.0);
        }
        // The probes' irradiance at a surface (p: world position, n: its normal, v: towards the eye): rgb, a the share to take (0: none cover it).
        vec4 giIrradiance(vec3 p, vec3 n, vec3 v)
        {
            vec4 a = giCascade(0, p, n, v);
            if (a.a >= 1.0) return a;
            vec4 b = giCascade(1, p, n, v);
            float wa = a.a, wb = b.a * (1.0 - a.a), t = wa + wb;
            return t > 0.0 ? vec4((a.rgb * wa + b.rgb * wb) / t, t) : vec4(0.0);
        }

        """;

    /// <summary>
    /// The probe trace (docs/render-gi.md "Probes"): one workgroup per probe, one ray per invocation (<see cref="GiProbes.Rays"/>, spherical
    /// Fibonacci directions turned by the frame's random rotation). A ray that hits sees the surface lit as the lighting pass lights it, without
    /// specular: a constant albedo (the terrain: the ground colour map) times the sun through a shadow ray and the probes' own irradiance of the
    /// last frame there (the bounces beyond the first). A ray that misses sees the lighting pass's ambient in its direction. Back faces of objects
    /// count as dark and give a negative distance (the blend marks probes that see many as inside).
    /// </summary>
    public static readonly string ProbeTrace = $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = {{GiProbes.Rays}}) in;

        layout(set = 0, binding = 0) uniform accelerationStructureEXT uScene;
        struct Geometry { uvec2 vertices; uvec2 indices; uint stride; uint kind; uint pad0; uint pad1; };
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
            vec4 rayLength;     // x per cascade 0, y cascade 1
        };
        layout(set = 0, binding = 4) uniform sampler2D uGiIrradiance;
        layout(set = 0, binding = 5) uniform sampler2D uGiDistance;
        layout(set = 0, binding = 6) uniform sampler2D uGiBase;
        layout(set = 0, binding = 7) uniform samplerCube uAtmoIrradiance;
        layout(set = 0, binding = 8) uniform sampler2D uAtmoAmbientMap;
        layout(set = 0, binding = 9) uniform sampler2D uGround;
        {{ProbeSampling}}
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Position { vec3 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer Triangle { uvec3 v; };
        uvec2 offsetBy(uvec2 a, uint bytes) { uint lo = a.x + bytes; return uvec2(lo, a.y + (lo < a.x ? 1u : 0u)); }

        vec4 ambientMap(vec3 world)
        {
            if (maps.z < 0.5) return vec4(1.0, 1.0, 1.0, 0.5);
            return textureLod(uAtmoAmbientMap, (world.xz + maps.w) / (2.0 * maps.w), 0.0);
        }
        vec3 skyIrradiance(vec3 n)
        {
            if (maps.x < 0.5) return vec3(1.09, 1.20, 1.51);
            vec4 t = textureLod(uAtmoIrradiance, n, {{F(Meitou.Data.World.KenshiLighting.IrradianceLevel)}});
            return t.rgb * t.a * 4.0;
        }
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
            // This frame's share of the probes: every uGiParams.z-th, from uGiParams.y (the rays are stored by workgroup).
            uint probe = gl_WorkGroupID.x * uint(uGiParams.z) + uint(uGiParams.y), ray = gl_LocalInvocationID.x;
            // probe = ((cascade · N + window row) · N + window column) · layers + layer
            int layer = int(probe % uint(GI_NY)), rest = int(probe / uint(GI_NY));
            int wx = rest % GI_N, wz = (rest / GI_N) % GI_N, c = rest / (GI_N * GI_N);
            vec4 g0 = uGiGrid[c * 2], g1 = uGiGrid[c * 2 + 1];
            int colX = (int(g1.x) + wx) % GI_N, colZ = (int(g1.y) + wz) % GI_N;
            float base = texelFetch(uGiBase, ivec2(colX, c * GI_N + colZ), 0).r;
            vec3 world = vec3(g0.x + float(wx) * g0.z, base + (float(layer) + 0.5) * g0.w, g0.y + float(wz) * g0.z);
            vec3 dir = normalize(mat3(rotation) * fibonacci(ray, gl_WorkGroupSize.x));
            float tMax = c == 0 ? rayLength.x : rayLength.y;

            rayQueryEXT rq;
            rayQueryInitializeEXT(rq, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsCullBackFacingTrianglesEXT, 0xFF, world - origin.xyz, 0.0, dir, tMax);
            while (rayQueryProceedEXT(rq)) { }
            float env = lightDir.w;
            vec4 am = ambientMap(world);
            uint slot = gl_WorkGroupID.x * gl_WorkGroupSize.x + ray;
            if (rayQueryGetIntersectionTypeEXT(rq, true) != gl_RayQueryCommittedIntersectionTriangleEXT)
            {
                rays[slot] = vec4(skyIrradiance(dir) * {{F(1 - Meitou.Data.World.KenshiLighting.DielectricSpecular)}} * am.rgb * env, 1e6);
                return;
            }
            float t = rayQueryGetIntersectionTEXT(rq, true);
            if (!rayQueryGetIntersectionFrontFaceEXT(rq, true))
            {
                rays[slot] = vec4(0.0, 0.0, 0.0, -0.2 * t);
                return;
            }
            int record = rayQueryGetIntersectionInstanceCustomIndexEXT(rq, true) + rayQueryGetIntersectionGeometryIndexEXT(rq, true);
            Geometry g = geometries[record];
            uvec3 tri = Triangle(offsetBy(g.indices, uint(rayQueryGetIntersectionPrimitiveIndexEXT(rq, true)) * 12u)).v;
            vec3 a = Position(offsetBy(g.vertices, tri.x * g.stride)).v;
            vec3 b = Position(offsetBy(g.vertices, tri.y * g.stride)).v;
            vec3 cc = Position(offsetBy(g.vertices, tri.z * g.stride)).v;
            vec3 n = normalize(cross(b - a, cc - a) * mat3(rayQueryGetIntersectionWorldToObjectEXT(rq, true)));
            if (dot(n, dir) > 0.0) n = -n;
            vec3 hit = world + dir * t;
            vec3 colour = albedo.rgb;
            if (g.kind == 0u && albedo.w > 0.5) colour = textureLod(uGround, (hit.xz + maps.w) / (2.0 * maps.w), 0.0).rgb;
            vec4 amHit = ambientMap(hit);
            vec3 l = lightDir.xyz;
            float nl = max(dot(n, l), 0.0);
            vec3 sun = vec3(0.0);
            if (nl > 0.0)
            {
                rayQueryEXT shadow;
                rayQueryInitializeEXT(shadow, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsCullBackFacingTrianglesEXT, 0xFF,
                    hit - origin.xyz + n * (0.5 + t * 0.001), 0.0, l, 1e5);
                while (rayQueryProceedEXT(shadow)) { }
                if (rayQueryGetIntersectionTypeEXT(shadow, true) == gl_RayQueryCommittedIntersectionNoneEXT)
                    sun = {{F(MathF.PI * (1 - Meitou.Data.World.KenshiLighting.DielectricSpecular))}} * nl * sunLight.rgb * amHit.a * 2.0;
            }
            vec4 bounce = giIrradiance(hit, n, -dir);
            vec3 ambient = mix(skyIrradiance(n) * {{F(1 - Meitou.Data.World.KenshiLighting.DielectricSpecular)}} * amHit.rgb * env, bounce.rgb, bounce.a);
            rays[slot] = vec4(colour * (sun + ambient), t);
        }
        """;

    /// <summary>
    /// The probe blend: one workgroup per probe. It reads the probe's rays and blends them into its irradiance tile (cosine weights) and its
    /// distance tile (the mean distance and its square, sharpened weights, over <c>uGiGrid[2c + 1].z</c>), borders included, over the last
    /// values by the hysteresis. A probe whose position changed (the grid scrolled, a new column) starts over. A probe that sees back faces in
    /// over a quarter of its rays is inside geometry: alpha 0, which the surfaces' sampling leaves out.
    /// </summary>
    public static readonly string ProbeBlend = $$"""
        #version 460
        layout(local_size_x = {{GiProbes.Rays}}) in;
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
            vec4 rayLength;
        };
        layout(set = 0, binding = 3, rgba16f) uniform image2D uIrradiance;
        layout(set = 0, binding = 4, rg16f) uniform image2D uDistance;
        layout(set = 0, binding = 5) uniform sampler2D uGiBase;

        shared vec4 sRays[{{GiProbes.Rays}}];
        shared vec3 sDirs[{{GiProbes.Rays}}];
        shared uint sBack;
        shared bool sMoved;

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
            uint probe = gl_WorkGroupID.x * uint(uGiParams.z) + uint(uGiParams.y), id = gl_LocalInvocationID.x, count = gl_WorkGroupSize.x;
            int layer = int(probe % uint(GI_NY)), rest = int(probe / uint(GI_NY));
            int wx = rest % GI_N, wz = (rest / GI_N) % GI_N, c = rest / (GI_N * GI_N);
            vec4 g0 = uGiGrid[c * 2], g1 = uGiGrid[c * 2 + 1];
            int colX = (int(g1.x) + wx) % GI_N, colZ = (int(g1.y) + wz) % GI_N;
            float base = texelFetch(uGiBase, ivec2(colX, c * GI_N + colZ), 0).r;
            vec3 world = vec3(g0.x + float(wx) * g0.z, base + (float(layer) + 0.5) * g0.w, g0.y + float(wz) * g0.z);
            // The probe's state slot is its tile (where it lives in the atlas), so a scrolled window finds the old probe there.
            uint stateSlot = uint((c * GI_N + colZ) * GI_N + colX) * uint(GI_NY) + uint(layer);

            sRays[id] = rays[gl_WorkGroupID.x * count + id];
            sDirs[id] = normalize(mat3(rotation) * fibonacci(id, count));
            if (id == 0u)
            {
                sBack = 0u;
                vec4 last = positions[stateSlot];
                sMoved = last.w < 0.5 || distance(last.xyz, world) > 0.5;
            }
            barrier();
            if (sRays[id].w < 0.0) atomicAdd(sBack, 1u);
            barrier();
            if (id == 0u) positions[stateSlot] = vec4(world, 1.0);
            bool valid = float(sBack) < 0.25 * float(count);
            float hysteresis = sMoved ? 0.0 : uGiParams.w;
            ivec2 tileIndex = ivec2(colX, (c * GI_N + colZ) * GI_NY + layer);
            float maxDistance = g1.z;

            ivec2 irrOrigin = tileIndex * (GI_IRR + 2);
            for (int t = int(id); t < (GI_IRR + 2) * (GI_IRR + 2); t += int(count))
            {
                ivec2 texel = ivec2(t % (GI_IRR + 2), t / (GI_IRR + 2));
                ivec2 src = inner(texel, GI_IRR);
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
                vec3 old = imageLoad(uIrradiance, irrOrigin + texel).rgb;
                imageStore(uIrradiance, irrOrigin + texel, vec4(mix(value, old, hysteresis), valid ? 1.0 : 0.0));
            }

            ivec2 distOrigin = tileIndex * (GI_DIST + 2);
            for (int t = int(id); t < (GI_DIST + 2) * (GI_DIST + 2); t += int(count))
            {
                ivec2 texel = ivec2(t % (GI_DIST + 2), t / (GI_DIST + 2));
                ivec2 src = inner(texel, GI_DIST);
                vec3 dir = octDir((vec2(src - 1) + 0.5) / float(GI_DIST) * 2.0 - 1.0);
                vec2 sum = vec2(0.0);
                float total = 0.0;
                for (uint r = 0u; r < count; r++)
                {
                    float w = pow(max(dot(dir, sDirs[r]), 0.0), 50.0);
                    float d = min(abs(sRays[r].w), maxDistance) / maxDistance;
                    sum += vec2(d, d * d) * w;
                    total += w;
                }
                vec2 value = total > 1e-4 ? sum / total : vec2(1.0);
                vec2 old = imageLoad(uDistance, distOrigin + texel).rg;
                imageStore(uDistance, distOrigin + texel, vec4(mix(value, old, hysteresis), 0.0, 0.0));
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

    /// <summary>
    /// The debug view (<c>--gi-debug</c>): per pixel of the near slice's depth, the surface's position (the depth back through the projection) and a
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
        struct Geometry { uvec2 vertices; uvec2 indices; uint stride; uint kind; uint pad0; uint pad1; };
        layout(set = 0, binding = 3, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 4, std140) uniform Params
        {
            mat4 inverse;      // NDC -> position relative to the eye (the rotation-only view and the near slice's projection, inverted)
            vec4 eye;          // xyz: the eye relative to the traced origin
            vec4 sunDir;       // xyz: towards the sun, w: the bounce rays' length
            vec4 sunColour;
            vec4 skyColour;    // the ambient from above
            vec4 groundColour; // the ambient from below
            uvec4 info;        // width, height, mode, frame
            vec4 extra;        // samples, exposure, albedo
        } p;

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
            vec4 q = p.inverse * vec4(uv * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
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
            rayQueryInitializeEXT(rq, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsCullBackFacingTrianglesEXT, 0xFF, origin, 0.0, dir, tMax);
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
                vec4 q = p.inverse * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
                vec3 dir = normalize(q.xyz / q.w);
                rayQueryEXT rq;
                rayQueryInitializeEXT(rq, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsCullBackFacingTrianglesEXT, 0xFF, p.eye.xyz, 0.0, dir, 1e6);
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
            if (d >= 1.0) { imageStore(uOut, px, vec4(mode == 1u ? vec3(0.25, 0.45, 0.8) : display(p.skyColour.rgb), 1.0)); return; }
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
                rayQueryInitializeEXT(rq, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsCullBackFacingTrianglesEXT, 0xFF, origin, 0.0, dir, rayLength);
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
                rayQueryInitializeEXT(rq, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsCullBackFacingTrianglesEXT, 0xFF, origin, 0.0, dir, rayLength);
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
