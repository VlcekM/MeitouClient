using Meitou.Game.Rendering.Frame;
namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The hybrid GI's world-space radiance cache (docs/render-gi.md "Hybrid GI"): cells in a hash table keyed by a world position quantised at a size
/// that grows with the eye distance and by the surface's axis, made by the low texels of the GI resolve that land in them, each traced from its own
/// surface point and read back by interpolating the cells round each texel. Nothing is kept in screen space. <see cref="GiCache"/> runs the passes.
/// </summary>
static class GiCacheShaders
{
    /// <summary>
    /// A float as a GLSL literal (invariant culture, always with a decimal point).
    /// </summary>
    static string F(float v) => v.ToString("0.0########", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The per-frame view (<see cref="GiCache"/>'s <c>View</c>), alone or after the trace's probe constants.
    /// </summary>
    const string ViewFields = """
            vec4 vEye;      // xyz the eye, w the low target's divisor
            vec4 vRight, vUp, vBack;
            vec4 vPlanes;   // xy the scene depth's near and far, zw tan(fov/2) * aspect, tan(fov/2) of the drawn window
            vec4 vSize;     // xy the low target, zw the render size
            vec4 vCells;    // x the finest cell, y cell size per unit of eye distance, z the eye distance the cache ends at, w where in a level the next one starts to blend in
            uvec4 vFrame;   // x the frame (from 1), y the age a cell is evicted at, z this frame's subset of the ring

        """;

    /// <summary>
    /// <see cref="ViewFields"/> as the View uniform block at binding 8.
    /// </summary>
    const string ViewBlock = "\nlayout(set = 0, binding = 8, std140) uniform View\n{\n" + ViewFields + "};\n";

    /// <summary>
    /// A cell as the table holds it, and the table's constants.
    /// </summary>
    static readonly string CellStruct = $$"""
        struct Cell
        {
            vec4 light;     // rgb the mean of the ring's subsets, a how many of them there are / RING
            vec4 point;     // xyz the cell's surface point (mean of its texels' positions), w how many frames it has been averaged over (up to 64)
            vec4 normal;    // xyz the surface normal (mean, unnormalised), w the ring's valid subsets as a bit mask
            ivec4 coord;    // xyz the cell, w level | axis << 8
        };
        const uint CAPACITY = {{GiCache.Capacity}}u, WAYS = 8u, RING = {{GiCache.Ring}}u, NONE = 0xffffffffu;

        """;

    /// <summary>
    /// The key hashes and the cell size. Needs <c>vCells</c>.
    /// </summary>
    const string Hashes = """
        uint cacheMix(uint h)
        {
            h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
            return h;
        }
        uint cacheHash(ivec3 c, int level, int axis, uint seed)
        {
            uint h = cacheMix(uint(c.x) ^ seed);
            h = cacheMix(h ^ uint(c.y));
            h = cacheMix(h ^ uint(c.z));
            return cacheMix(h ^ uint(level | (axis << 8)));
        }
        // The first of the eight slots a key may take, and the key as stored (never 0, the empty slot).
        uint cacheBase(ivec3 c, int level, int axis) { return cacheHash(c, level, axis, 0x9e3779b9u) & (CAPACITY - WAYS); }
        uint cacheCheck(ivec3 c, int level, int axis) { return cacheHash(c, level, axis, 0x85ebca6bu) | 1u; }
        float cellSize(int level) { return vCells.x * exp2(float(level)); }

        """;

    /// <summary>
    /// A low texel's surface (as the resolve picks it: the first pixel of its block that takes the probes), its position from the depth and its flat
    /// normal from the depth's slope (KenshiNative: a mapped normal's bumps are noise to the light), and which cells it belongs to: the level by the
    /// eye distance (the next coarser one blending in over the top of each level) and the axis nearest the normal (the second nearest blending in
    /// towards the diagonal), interiors apart.
    /// </summary>
    static readonly string Surface = DepthConvention.Glsl + $$"""
        {{GiShaders.OctDirection}}
        float depthAt(ivec2 px)
        {
            float d = texelFetch(uNearDepth, px, 0).r;
            return depthIsSky(d) ? 0.0 : depthViewZ(d, vPlanes.xy);
        }
        vec3 positionAt(ivec2 px, float z)
        {
            vec2 ndc = (vec2(px) + 0.5) / vSize.zw * 2.0 - 1.0;
            return vEye.xyz + (vRight.xyz * ndc.x * vPlanes.z + vUp.xyz * ndc.y * vPlanes.w - vBack.xyz) * z;
        }
        // On the side with the smaller step at an edge.
        vec3 sideStep(ivec2 px, vec3 p, ivec2 o)
        {
            ivec2 last = ivec2(vSize.zw) - 1, a = clamp(px + o, ivec2(0), last), b = clamp(px - o, ivec2(0), last);
            float za = depthAt(a), zb = depthAt(b);
            vec3 ahead = za > 0.0 ? positionAt(a, za) - p : vec3(1e9), behind = zb > 0.0 ? p - positionAt(b, zb) : vec3(1e9);
            return dot(ahead, ahead) < dot(behind, behind) ? ahead : behind;
        }
        vec3 towardsEye(vec3 g, vec3 p) { return dot(g, vEye.xyz - p) < 0.0 ? -g : g; }
        vec3 planeNormal(vec3 a, vec3 b, vec3 p)
        {
            vec3 g = cross(a, b);
            float size = dot(g, g);
            return size < 1e-12 || size > 1e30 ? vec3(0.0) : towardsEye(g / sqrt(size), p);
        }
        // The surface's flat normal from the depth, on the side with the smaller step. Where it turns more than 45 degrees from the lit fragment's own normal (an edge
        // the step rule got wrong: a silhouette, a crest, a pixel-thin part), the plane through one neighbour along each axis nearest that normal; none near it, the
        // fragment's normal itself. So no texel takes the light of a plane it is not on.
        vec3 flatNormal(ivec2 px, vec3 p, vec3 shading)
        {
            vec3 g = planeNormal(sideStep(px, p, ivec2(1, 0)), sideStep(px, p, ivec2(0, 1)), p);
            if (dot(g, shading) >= 0.7071) return g;
            ivec2 last = ivec2(vSize.zw) - 1;
            vec3 best = shading;
            float fit = 0.7071;
            for (int k = 0; k < 4; k++)
            {
                ivec2 ox = ivec2((k & 1) == 0 ? 1 : -1, 0), oy = ivec2(0, (k & 2) == 0 ? 1 : -1);
                ivec2 a = clamp(px + ox, ivec2(0), last), b = clamp(px + oy, ivec2(0), last);
                float za = depthAt(a), zb = depthAt(b);
                if (za <= 0.0 || zb <= 0.0 || a == px || b == px) continue;
                vec3 c = planeNormal(positionAt(a, za) - p, positionAt(b, zb) - p, p);
                if (dot(c, shading) > fit) { best = c; fit = dot(c, shading); }
            }
            return best;
        }
        // False: nothing in the block takes the probes, or it is past the cache's end.
        bool texelSurface(ivec2 low, out vec3 p, out vec3 n, out bool interior)
        {
            int div = int(vEye.w);
            ivec2 px = ivec2(-1);
            uvec4 surface = uvec4(0u);
            p = n = vec3(0.0);
            interior = false;
            for (int k = 0; k < 4 && px.x < 0; k++)
            {
                ivec2 q = min(low * div + ivec2(k & 1, k >> 1) * (div - 1), ivec2(vSize.zw) - 1);
                uvec4 s = texelFetch(uSurfaceTarget, q, 0);
                if ((s.w & 256u) != 0u) { px = q; surface = s; }
            }
            float z = px.x < 0 ? 0.0 : depthAt(px);
            if (z <= 0.0) return false;
            p = positionAt(px, z);
            if (distance(p, vEye.xyz) >= vCells.z) return false;
            n = flatNormal(px, p, giOctDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0));
            interior = (surface.w & 512u) != 0u;
            return true;
        }
        // The level (and how much of the next coarser one) at eye distance d.
        int cellLevel(float d, out float up)
        {
            float level = log2(max(d * vCells.y, vCells.x) / vCells.x);
            up = smoothstep(vCells.w, 1.0, fract(level));
            return int(floor(level));
        }
        // The axis nearest n (0..5: +x, -x, +y, -y, +z, -z; 6..11 the same indoors), the second nearest and how much of it: half on the diagonal between them, none {{F(GiCache.AxisBlend)}} past it.
        int cellAxis(vec3 n, bool interior, out int second, out float share)
        {
            vec3 a = abs(n);
            int i = a.x >= a.y && a.x >= a.z ? 0 : a.y >= a.z ? 1 : 2;
            int j = i == 0 ? (a.y >= a.z ? 1 : 2) : i == 1 ? (a.x >= a.z ? 0 : 2) : (a.x >= a.y ? 0 : 1);
            int indoor = interior ? 6 : 0;
            second = j * 2 + (n[j] < 0.0 ? 1 : 0) + indoor;
            share = 0.5 * (1.0 - smoothstep(0.0, {{F(GiCache.AxisBlend)}}, a[i] - a[j]));
            return i * 2 + (n[i] < 0.0 ? 1 : 0) + indoor;
        }

        """;

    /// <summary>
    /// Once a frame, first: every cell no texel touched for <c>vFrame.y</c> frames (fewer as the table fills) gives its slot back; the list of this
    /// frame's cells empties.
    /// </summary>
    public static readonly string Evict = "#version 460\nlayout(local_size_x = 64) in;\n" + CellStruct + """
        layout(set = 0, binding = 0, std430) buffer Keys { uint keys[]; };
        layout(set = 0, binding = 1, std430) readonly buffer Stamps { uint stamps[]; };
        layout(set = 0, binding = 2, std430) buffer Control { uint control[]; };   // 0 this frame's cells, 1..3 the trace's dispatch, 4 the cells held
        """ + ViewBlock + """
        void main()
        {
            uint slot = gl_GlobalInvocationID.x;
            if (slot == 0u) control[0] = 0u;
            if (slot >= CAPACITY || keys[slot] == 0u) return;
            uint held = control[4], age = vFrame.y;
            if (held > CAPACITY / 2u) age = min(age, 120u);
            if (held > CAPACITY * 5u / 8u) age = min(age, 30u);
            if (vFrame.x - stamps[slot] <= age) return;
            keys[slot] = 0u;
            atomicAdd(control[4], 0xffffffffu);
        }
        """;

    /// <summary>
    /// Per low texel: the cells it belongs to (up to two levels times two axes), made where missing; its position and normal added into each one's
    /// sums for the frame (12-bit fixed point within the cell); each cell listed once for the trace.
    /// </summary>
    public static readonly string Touch = "#version 460\nlayout(local_size_x = 8, local_size_y = 8) in;\n" + CellStruct + """
        layout(set = 0, binding = 0) uniform usampler2D uSurfaceTarget;
        layout(set = 0, binding = 1) uniform sampler2D uNearDepth;
        layout(set = 0, binding = 2, std430) buffer Keys { uint keys[]; };
        layout(set = 0, binding = 3, std430) buffer Stamps { uint stamps[]; };
        layout(set = 0, binding = 4, std430) buffer Sums { uvec4 sums[]; };     // per cell: position and count, normal
        layout(set = 0, binding = 5, std430) writeonly buffer Cells { Cell cells[]; };
        layout(set = 0, binding = 6, std430) buffer Control { uint control[]; };
        layout(set = 0, binding = 7, std430) writeonly buffer Active { uint listed[]; };
        """ + ViewBlock + Hashes + Surface + """
        uint cacheInsert(ivec3 c, int level, int axis, out bool made)
        {
            made = false;
            uint base = cacheBase(c, level, axis), check = cacheCheck(c, level, axis);
            for (uint k = 0u; k < WAYS; k++)
                if (keys[base + k] == check) return base + k;
            for (uint k = 0u; k < WAYS; k++)
            {
                uint was = atomicCompSwap(keys[base + k], 0u, check);
                if (was == 0u) { made = true; return base + k; }
                if (was == check) return base + k;
            }
            return NONE;
        }
        void touch(vec3 p, vec3 n, int level, int axis)
        {
            float s = cellSize(level);
            ivec3 c = ivec3(floor(p / s));
            bool made;
            uint slot = cacheInsert(c, level, axis, made);
            if (slot == NONE) return;
            if (made)
            {
                cells[slot] = Cell(vec4(0.0), vec4(0.0), vec4(0.0), ivec4(c, level | (axis << 8)));
                atomicAdd(control[4], 1u);
            }
            uvec3 q = uvec3(clamp(p / s - vec3(c), 0.0, 1.0) * 4095.0 + 0.5), m = uvec3((n * 0.5 + 0.5) * 4095.0 + 0.5);
            atomicAdd(sums[slot * 2u].x, q.x);
            atomicAdd(sums[slot * 2u].y, q.y);
            atomicAdd(sums[slot * 2u].z, q.z);
            atomicAdd(sums[slot * 2u].w, 1u);
            atomicAdd(sums[slot * 2u + 1u].x, m.x);
            atomicAdd(sums[slot * 2u + 1u].y, m.y);
            atomicAdd(sums[slot * 2u + 1u].z, m.z);
            // The texel farthest out along the cell's axis (plus one: 0 is none), for the point the cell traces from (the trace's main).
            int along = axis % 6;
            uint out_ = q[along >> 1];
            atomicMax(sums[slot * 2u + 1u].w, ((along & 1) == 0 ? out_ : 4095u - out_) + 1u);
            if (atomicExchange(stamps[slot], vFrame.x) != vFrame.x) listed[atomicAdd(control[0], 1u)] = slot;
        }

        void main()
        {
            ivec2 low = ivec2(gl_GlobalInvocationID.xy);
            if (any(greaterThanEqual(low, ivec2(vSize.xy)))) return;
            vec3 p, n;
            bool interior;
            if (!texelSurface(low, p, n, interior)) return;
            float up, share;
            int second, level = cellLevel(distance(p, vEye.xyz), up);
            int axis = cellAxis(n, interior, second, share);
            for (int l = 0; l < 2; l++)
            {
                if ((l == 0 ? 1.0 - up : up) <= 0.0) continue;
                touch(p, n, level + l, axis);
                if (share > 0.0) touch(p, n, level + l, second);
            }
        }
        """;

    /// <summary>
    /// The trace's dispatch: one workgroup per cell listed this frame.
    /// </summary>
    public const string Arguments = """
        #version 460
        layout(local_size_x = 1) in;
        layout(set = 0, binding = 0, std430) buffer Control { uint control[]; };
        void main()
        {
            control[1] = control[0];
            control[2] = 1u;
            control[3] = 1u;
        }
        """;

    /// <summary>
    /// The trace's declarations; its Params block: <see cref="GiProbes"/>' constants, the rays' reach, then the view.
    /// </summary>
    static readonly string TraceSource = $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = {{GiCache.RaysPerCell}}) in;
        {{Meitou.Game.Rendering.Gpu.BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform accelerationStructureEXT uScene;
        {{GiShaders.GeometryStruct}}
        {{CellStruct}}
        layout(set = 0, binding = 1, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 2, std430) buffer Cells { Cell cells[]; };
        layout(set = 0, binding = 3, std140) uniform Params
        {
            vec4 uGiParams;
            vec4 uGiGrid[4];
            mat4 rotation;
            vec4 origin;        // xyz: the traced scene's origin
            vec4 sunLight;
            vec4 lightDir;
            vec4 maps;
            vec4 albedo;
            vec4 rayLength;
            vec4 uLightGrid;
            vec4 extra;         // y: the far cascade's period
            vec4 cloudSlots[3];
            vec4 cacheReach;    // x the rays' length, y the most one sample may bring
        {{ViewFields}}
        };
        layout(set = 0, binding = 4) uniform sampler2D uGiIrradiance;
        layout(set = 0, binding = 5) uniform sampler2D uGiDistance;
        layout(set = 0, binding = 6) uniform sampler2D uGiBase;
        layout(set = 0, binding = 7) uniform samplerCube uAtmoIrradiance;
        layout(set = 0, binding = 8) uniform sampler2D uAtmoAmbientMap;
        layout(set = 0, binding = 9) uniform sampler2D uGround;
        layout(set = 0, binding = 10) uniform sampler2D uLightCells;
        layout(set = 0, binding = 11) uniform sampler2D uLightIndex;
        layout(set = 0, binding = 12) uniform sampler2D uLightData;
        layout(set = 0, binding = 13, std430) buffer Sums { uvec4 sums[]; };
        layout(set = 0, binding = 14, std430) readonly buffer Active { uint listed[]; };
        layout(set = 0, binding = 17, std430) buffer Ring { uint ring[]; };
        #define LAMP_DIFFUSE_ONLY
        {{GiShaders.LampTileTrace}}
        {{GiShaders.ProbeSampling}}
        {{GiShaders.HitFunctions}}
        {{Hashes}}
        """;

    /// <summary>
    /// One workgroup per cell listed this frame, one ray per invocation from the cell's surface point (its texels' mean, averaged over frames) round
    /// its normal, cosine weighted; the mean into the ring's slot for this frame's subset of directions, and the cell's light the mean of the subsets
    /// it holds. So a still scene's cells hold a fixed set of <see cref="GiCache.Ring"/> × <see cref="GiCache.RaysPerCell"/> rays, each subset
    /// refreshed every <see cref="GiCache.Ring"/> frames.
    /// </summary>
    static readonly string TraceBody = $$"""
        vec3 axisDirection(int axis)
        {
            int a = axis % 6;
            vec3 d = vec3(0.0);
            d[a >> 1] = (a & 1) == 0 ? 1.0 : -1.0;
            return d;
        }
        // Shared exponent, 9 bits a channel (as E5B9G9R9): a subset's light in one word.
        uint packLight(vec3 c)
        {
            c = clamp(c, vec3(0.0), vec3(65408.0));
            float m = max(c.r, max(c.g, c.b));
            if (m < 1.0 / 16384.0) return 0u;
            int e = clamp(int(floor(log2(m))) + 1, -14, 16);
            uvec3 q = uvec3(c * exp2(float(9 - e)) + 0.5);
            if (max(q.r, max(q.g, q.b)) >= 512u && e < 16) { e++; q = uvec3(c * exp2(float(9 - e)) + 0.5); }
            q = min(q, uvec3(511u));
            return q.r | (q.g << 9) | (q.b << 18) | (uint(e + 15) << 27);
        }
        vec3 unpackLight(uint v)
        {
            if (v == 0u) return vec3(0.0);
            return vec3(float(v & 511u), float((v >> 9) & 511u), float((v >> 18) & 511u)) * exp2(float(int(v >> 27) - 15 - 9));
        }
        // Cosine-weighted about n; the basis is Duff et al.'s with y as its pole, continuous on everything that does not face straight down.
        vec3 cosineDirection(vec3 n, vec2 xi)
        {
            float s = n.y >= 0.0 ? 1.0 : -1.0, a = -1.0 / (s + n.y), b = n.x * n.z * a;
            vec3 t = vec3(1.0 + s * n.x * n.x * a, -s * n.x, s * b), u = vec3(b, -n.z, s + n.z * n.z * a);
            float r = sqrt(xi.x), phi = 6.2831853 * xi.y;
            return normalize(t * (r * cos(phi)) + u * (r * sin(phi)) + n * sqrt(max(1.0 - xi.x, 0.0)));
        }
        // The probes as a radiance cache: the light coming from dir, read at the ray's end. Outside them, the ambient a miss sees.
        vec3 cached(vec3 end, vec3 dir)
        {
            vec4 cache = giIrradiance(end, dir, -dir);
            vec3 sky = skyIrradiance(dir) * {{F(1 - Meitou.Data.World.KenshiLighting.DielectricSpecular)}} * ambientMap(end).rgb * lightDir.w;
            return mix(sky, cache.rgb, cache.a);
        }
        // One ray's light: a hit lit as a probe ray's, on either side (the underside of a tent's canvas is a surface: it is drawn one-sided and the probes where the ray
        // started moved from frame to frame, docs/render-gi.md "Radiance cache"); a miss, the probes at its end. Clamped per channel (KenshiNative's firefly clamp).
        vec3 rayLight(vec3 world, vec3 dir)
        {
            rayQueryEXT rq;
            rayQueryInitializeEXT(rq, uScene, gl_RayFlagsCullBackFacingTrianglesEXT, {{TracedSunShaders.GiMask}}, world - origin.xyz, 0.0, dir, cacheReach.x);
            TRACE(rq)
            if (rayQueryGetIntersectionTypeEXT(rq, true) != gl_RayQueryCommittedIntersectionTriangleEXT) return min(cached(world + dir * cacheReach.x, dir), vec3(cacheReach.y));
            float t = rayQueryGetIntersectionTEXT(rq, true);
            int record = rayQueryGetIntersectionInstanceCustomIndexEXT(rq, true) + rayQueryGetIntersectionGeometryIndexEXT(rq, true);
            Geometry g = geometries[record];
            float env = lightDir.w;
        {{GiShaders.HitShade}}
            return min(colour * (sun + ambient + lamps), vec3(cacheReach.y));
        }

        // A subset traced again whose light moved by more than {{F(GiCache.ChangeShare * 100)}} % of the cell's (the same directions: in a still scene it does not move at all): a
        // lamp switched, a sudden sun. The cell then keeps this subset alone and fills the others again over the next frames, instead of fading the change in over the ring.
        bool changed(vec3 before, vec3 now, vec3 mean, bool held)
        {
            const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
            return held && abs(dot(now - before, lum)) > {{F(GiCache.ChangeShare)}} * dot(mean, lum) + 1e-3;
        }

        shared vec3 partial[{{GiCache.RaysPerCell}}];

        // Ray i of subset k: a Fibonacci lattice of the subset's rays, shifted by the subset (R2) and by the cell; every subset is stratified, and they interleave.
        vec2 rayPoint(uint i, uint k, vec2 shift)
        {
            vec2 lattice = vec2((float(i) + 0.5) / float(gl_WorkGroupSize.x), fract(float(i) * 0.6180339887));
            return fract(lattice + fract(vec2(0.7548776662, 0.5698402910) * float(k)) + shift);
        }

        void main()
        {
            uint slot = listed[gl_WorkGroupID.x], i = gl_LocalInvocationID.x;
            Cell cell = cells[slot];
            uvec4 a = sums[slot * 2u], b = sums[slot * 2u + 1u];
            int level = cell.coord.w & 255, axis = cell.coord.w >> 8;
            float s = cellSize(level), count = max(float(a.w), 1.0);
            // The surface point and normal: this frame's mean of the cell's texels, averaged over up to 64 frames.
            vec3 framePoint = (vec3(cell.coord.xyz) + vec3(a.xyz) / (4095.0 * count)) * s;
            // Along the cell's axis, the texel farthest out, not the mean: a cell holds every surface facing its axis (two treads of a stair, a ledge over a
            // floor), and their mean lies between them, under the upper one or inside what joins them, where every ray ends at once and the cell is black.
            if (b.w > 0u)
            {
                int along = axis % 6;
                float outer = float(b.w - 1u) / 4095.0;
                framePoint[along >> 1] = (float(cell.coord[along >> 1]) + ((along & 1) == 0 ? outer : 1.0 - outer)) * s;
            }
            vec3 frameNormal = vec3(b.xyz) / (4095.0 * count) * 2.0 - 1.0;
            float kept = cell.point.w, w = 1.0 / min(kept + 1.0, 64.0);
            vec3 point = kept > 0.0 ? mix(cell.point.xyz, framePoint, w) : framePoint;
            vec3 normalSum = kept > 0.0 ? mix(cell.normal.xyz, frameNormal, w) : frameNormal;
            vec3 n = dot(normalSum, normalSum) > 1e-8 ? normalize(normalSum) : axisDirection(axis);
            // Along the normal the point is this frame's, not the average: a surface that moves (a door, a rotor's blade) leaves the average behind it, inside
            // what moved, where every ray ends at once and the cell is black for as long as the surface moves.
            point += n * dot(n, framePoint - point);
            // Off the surface by what the depth's precision and the traced geometry's difference from the drawn one call for.
            vec3 world = point + n * (0.05 + distance(point, vEye.xyz) * 0.002);
            uint seed = cacheMix(uint(cell.coord.x) * 73856093u ^ uint(cell.coord.y) * 19349663u ^ uint(cell.coord.z) * 83492791u ^ uint(cell.coord.w));
            vec2 shift = vec2(cacheMix(seed), cacheMix(seed ^ 0x68bc21ebu)) / 4294967296.0;
            uint mask = uint(cell.normal.w);
            // A new (or just changed) cell traces its first subsets at once (the ones before this frame's, which the frames after do not repeat).
            int subsets = bitCount(mask) < {{GiCache.FirstSubsets}} ? {{GiCache.FirstSubsets}} : 1;
            for (int j = 0; j < subsets; j++)
            {
                uint k = (vFrame.z + RING - uint(j)) % RING;
                partial[i] = rayLight(world, cosineDirection(n, rayPoint(i, k, shift)));
                barrier();
                for (uint stride = gl_WorkGroupSize.x / 2u; stride > 0u; stride >>= 1u)
                {
                    if (i < stride) partial[i] += partial[i + stride];
                    barrier();
                }
                if (i == 0u)
                {
                    vec3 traced = partial[0] / float(gl_WorkGroupSize.x);
                    if (subsets == 1 && changed(unpackLight(ring[slot * RING + k]), traced, cell.light.rgb, (mask & (1u << k)) != 0u)) mask = 0u;
                    ring[slot * RING + k] = packLight(traced);
                }
                mask |= 1u << k;
                barrier();
            }
            if (i != 0u) return;
            vec3 total = vec3(0.0);
            for (uint k = 0u; k < RING; k++)
                if ((mask & (1u << k)) != 0u) total += unpackLight(ring[slot * RING + k]);
            float valid = float(bitCount(mask));
            cells[slot].light = vec4(total / valid, valid / float(RING));
            cells[slot].point = vec4(point, min(kept + 1.0, 64.0));
            cells[slot].normal = vec4(normalSum, float(mask));
            sums[slot * 2u] = uvec4(0u);
            sums[slot * 2u + 1u] = uvec4(0u);
        }
        """;

    /// <summary>
    /// The trace for the frame's options (<see cref="GiShaders.Relocating"/>, <see cref="GiShaders.Clouded"/>).
    /// </summary>
    public static string Trace(bool relocate, bool clouds)
    {
        string trace = TraceSource + TraceBody;

        if (clouds) trace = GiShaders.Clouded(trace.Replace("vec4 cloudSlots[3];", "", StringComparison.Ordinal));

        return relocate ? GiShaders.Relocating(trace) : trace;
    }

    /// <summary>
    /// Per low texel: the light of the cells round it, as the resolve takes it (rgb the light, a how much). Per level and axis it belongs to, the
    /// eight cells whose centres surround it, each weighted by its trilinear weight, its surface point's distance from the texel's tangent plane
    /// (half a cell: no light across a step or onto a parallel surface), its normal's agreement and how many of its ring's subsets it has; a: the
    /// subsets held (full from <see cref="GiCache.FirstSubsets"/>) times how much of the found cells' trilinear weight those weights keep (full from
    /// <see cref="GiCache.MatchFloor"/>), fading out over the last quarter of the cache's range.
    /// </summary>
    public static readonly string Gather = "#version 460\nlayout(local_size_x = 8, local_size_y = 8) in;\n" + CellStruct + """
        layout(set = 0, binding = 0) uniform usampler2D uSurfaceTarget;
        layout(set = 0, binding = 1) uniform sampler2D uNearDepth;
        layout(set = 0, binding = 2, std430) readonly buffer Keys { uint keys[]; };
        layout(set = 0, binding = 5, std430) readonly buffer Cells { Cell cells[]; };
        layout(set = 0, binding = 6, rgba16f) uniform writeonly image2D uOut;
        """ + ViewBlock + Hashes + Surface + $$"""
        uint cacheFind(ivec3 c, int level, int axis)
        {
            uint base = cacheBase(c, level, axis), check = cacheCheck(c, level, axis);
            for (uint k = 0u; k < WAYS; k++)
                if (keys[base + k] == check) return base + k;
            return NONE;
        }
        // One level and axis, its share of the texel: adds its light (weighted by confidence) and its confidence.
        void gatherCells(vec3 p, vec3 n, int level, int axis, float share, inout vec3 light, inout float sure, inout float match)
        {
            float s = cellSize(level);
            vec3 g = p / s - 0.5;
            ivec3 i0 = ivec3(floor(g));
            vec3 f = g - vec3(i0);
            vec3 sum = vec3(0.0);
            float reach = 0.0, total = 0.0, found = 0.0;
            for (int k = 0; k < 8; k++)
            {
                ivec3 o = ivec3(k & 1, (k >> 1) & 1, k >> 2);
                uint slot = cacheFind(i0 + o, level, axis);
                if (slot == NONE) continue;
                vec4 l = cells[slot].light;
                if (l.a <= 0.0) continue;
                vec3 q = cells[slot].point.xyz, m = cells[slot].normal.xyz;
                vec3 t = mix(1.0 - f, f, vec3(o));
                float facing = dot(m, m) > 1e-8 ? max(dot(n, normalize(m)), 0.0) : 0.0, f2 = facing * facing, f4 = f2 * f2;
                float w = t.x * t.y * t.z * exp(-abs(dot(n, q - p)) / (0.5 * s)) * f4 * f4;
                sum += l.rgb * (w * l.a);
                reach += t.x * t.y * t.z;
                total += w;
                found += w * l.a;
            }
            if (found <= 1e-6) return;
            light += sum * (share / total);
            sure += found * (share / total);
            // Cells found only on another plane or facing away (their weights next to nothing against their trilinear ones) leave the texel to the probes.
            match += share * clamp(found / (reach * {{F(GiCache.MatchFloor)}}), 0.0, 1.0);
        }

        void main()
        {
            ivec2 low = ivec2(gl_GlobalInvocationID.xy);
            if (any(greaterThanEqual(low, ivec2(vSize.xy)))) return;
            vec3 p, n;
            bool interior;
            if (!texelSurface(low, p, n, interior))
            {
                imageStore(uOut, low, vec4(0.0));
                return;
            }
            float d = distance(p, vEye.xyz), up, share;
            int second, level = cellLevel(d, up);
            int axis = cellAxis(n, interior, second, share);
            vec3 light = vec3(0.0);
            float sure = 0.0, match = 0.0;
            for (int l = 0; l < 2; l++)
            {
                float w = l == 0 ? 1.0 - up : up;
                if (w <= 0.0) continue;
                gatherCells(p, n, level + l, axis, w * (1.0 - share), light, sure, match);
                if (share > 0.0) gatherCells(p, n, level + l, second, w * share, light, sure, match);
            }
            float fade = 1.0 - smoothstep(0.75 * vCells.z, vCells.z, d);
            vec4 result = sure > 1e-6 ? vec4(light / sure, fade * match * clamp(sure * {{F((float)GiCache.Ring / GiCache.FirstSubsets)}}, 0.0, 1.0)) : vec4(0.0);
            imageStore(uOut, low, result);
        }
        """;
}
