#include <metal_stdlib>
using namespace metal;
using namespace metal::raytracing;

// The Metal probe update (docs/render-gi.md "Metal"): one threadgroup per probe, one ray per thread, then the blend into the probe's tiles. The
// light a ray finds and the blend follow GiShaders.ProbeTrace/ProbeBlend; the atlas layout and the octahedral borders match ProbeSampling, so the
// world shaders read the result unchanged.
constant uint GI_N = 32;
constant uint GI_NY = 8;
constant uint GI_IRR = 8;
constant uint GI_DIST = 16;
constant uint GI_RAYS = 32;

// 1 - KenshiLighting.DielectricSpecular, and pi times it (the sun's term).
constant float DIFFUSE = 0.96f;
constant float PI_DIFFUSE = 3.01592894f;
// KenshiLighting.IrradianceLevel: the sky cube's mip the ambient reads.
constant float IRRADIANCE_LEVEL = 3.0f;

struct ProbeConstants
{
    float4x4 rotation;    // the frame's random rotation of the ray directions
    float4 grid[4];
    float4 sunLight;      // rgb: uAtmoSunLight
    float4 lightDir;      // uAtmoLight: xyz the lighting direction, w the environment light's factor
    float4 albedo;        // rgb: the objects' albedo, w: the terrain ground colour map present
    float4 maps;          // uAtmoMaps
    float4 timing;        // x: this frame's phase, y: phases, z: the hysteresis of this update, w: the cascades' ray lengths' ratio (unused)
    float4 rayLength;     // x: cascade 0, y: cascade 1
};

// A bottom level's geometry: its positions (three floats at the start of each vertex) and 32-bit indices, by GPU address.
struct GeometryRecord
{
    device const uchar* vertices;
    device const uint* indices;
    uint stride;
    uint kind;            // 0 terrain, 1 object
};

float3 fibonacci(uint i, uint n)
{
    float k = float(i) + 0.5f;
    float y = 1.0f - 2.0f * k / float(n);
    float r = sqrt(max(1.0f - y * y, 0.0f));
    float a = k * 2.39996323f;
    return float3(cos(a) * r, y, sin(a) * r);
}

float3 octDir(float2 p)
{
    float3 d = float3(p.x, 1.0f - abs(p.x) - abs(p.y), p.y);
    if (d.y < 0.0f)
        d.xz = (1.0f - abs(d.zx)) * float2(d.x >= 0.0f ? 1.0f : -1.0f, d.z >= 0.0f ? 1.0f : -1.0f);
    return normalize(d);
}

// A tile texel (border included) to the inner texel whose value it holds.
int2 inner(int2 t, int size)
{
    int last = size + 1;
    if ((t.x == 0 || t.x == last) && (t.y == 0 || t.y == last))
        return int2(t.x == 0 ? size : 1, t.y == 0 ? size : 1);
    if (t.y == 0) return int2(last - t.x, 1);
    if (t.y == last) return int2(last - t.x, size);
    if (t.x == 0) return int2(1, last - t.y);
    if (t.x == last) return int2(size, last - t.y);
    return t;
}

// The lighting pass's sky irradiance (AtmosphereShaders atmoIrradiance).
float3 skyIrradiance(texturecube<float, access::sample> sky, sampler s, float3 n, constant ProbeConstants& p)
{
    if (p.maps.x < 0.5f) return float3(1.09f, 1.20f, 1.51f);
    float4 t = sky.sample(s, n, level(IRRADIANCE_LEVEL));
    return t.rgb * t.a * 4.0f;
}

// The ambient map at a point, with its floor (AtmosphereShaders atmoAmbientMapAt): rgb the ambient's colour, a the sun's share.
float4 ambientMap(texture2d<float, access::sample> map, sampler s, float3 world, constant ProbeConstants& p)
{
    if (p.maps.z < 0.5f) return float4(1.0f, 1.0f, 1.0f, 0.5f);
    float4 am = map.sample(s, (world.xz + p.maps.w) / (2.0f * p.maps.w), level(0.0f));
    float l = dot(am.rgb, float3(0.2126f, 0.7152f, 0.0722f));
    return float4(am.rgb * max(1.0f, (p.maps.z - 1.0f) / max(l, 1e-3f)), am.a);
}

// The hit triangle's world normal, from its vertices (cross(b - a, c - a), the meshes' outward side) and the instance's transform.
float3 hitNormal(const device GeometryRecord& g, uint primitive, float4x3 worldToObject)
{
    uint3 tri = uint3(g.indices[primitive * 3], g.indices[primitive * 3 + 1], g.indices[primitive * 3 + 2]);
    float3 a = float3(*(device const packed_float3*)(g.vertices + tri.x * g.stride));
    float3 b = float3(*(device const packed_float3*)(g.vertices + tri.y * g.stride));
    float3 c = float3(*(device const packed_float3*)(g.vertices + tri.z * g.stride));
    float3x3 m = float3x3(worldToObject[0], worldToObject[1], worldToObject[2]);
    return normalize(cross(b - a, c - a) * m);
}

kernel void probe_gi(uint group [[threadgroup_position_in_grid]],
                     uint tid [[thread_position_in_threadgroup]],
                     constant ProbeConstants& params [[buffer(0)]],
                     const device uint* firstRecord [[buffer(1)]],
                     instance_acceleration_structure scene [[buffer(2)]],
                     const device GeometryRecord* records [[buffer(3)]],
                     texture2d<float, access::read> irradianceOld [[texture(0)]],
                     texture2d<float, access::write> irradiance [[texture(1)]],
                     texture2d<float, access::read> distanceOld [[texture(2)]],
                     texture2d<float, access::write> distance [[texture(3)]],
                     texture2d<float, access::read> bases [[texture(4)]],
                     texturecube<float, access::sample> sky [[texture(5)]],
                     texture2d<float, access::sample> ground [[texture(6)]],
                     texture2d<float, access::sample> ambient [[texture(7)]],
                     sampler skySampler [[sampler(0)]],
                     sampler mapSampler [[sampler(1)]])
{
    uint phases = max(uint(params.timing.y), 1u);
    uint probe = group * phases + uint(params.timing.x);
    if (probe >= GI_N * GI_N * GI_NY * 2 || tid >= GI_RAYS) return;

    // probe = ((cascade · N + window row) · N + window column) · layers + layer
    uint layer = probe % GI_NY;
    uint rest = probe / GI_NY;
    uint wx = rest % GI_N, wz = (rest / GI_N) % GI_N, c = rest / (GI_N * GI_N);
    float4 g0 = params.grid[c * 2], g1 = params.grid[c * 2 + 1];
    uint colX = (uint(g1.x) + wx) % GI_N, colZ = (uint(g1.y) + wz) % GI_N;
    float base = bases.read(uint2(colX, c * GI_N + colZ)).r;
    float3 world = float3(g0.x + float(wx) * g0.z, base + (float(layer) + 0.5f) * g0.w, g0.y + float(wz) * g0.z);
    float3 dir = normalize((params.rotation * float4(fibonacci(tid, GI_RAYS), 0.0f)).xyz);

    ray r;
    r.origin = world;
    r.direction = dir;
    r.min_distance = 0.0f;
    r.max_distance = c == 0 ? params.rayLength.x : params.rayLength.y;

    // The terrain's back faces are culled (a ray from under the ground passes through it); the objects' instances disable culling, so a ray
    // inside one finds its back.
    intersector<triangle_data, instancing, world_space_data> isect;
    isect.force_opacity(forced_opacity::opaque);
    isect.set_triangle_cull_mode(triangle_cull_mode::back);
    auto hit = isect.intersect(r, scene);

    float env = params.lightDir.w;
    float3 radiance;
    float travel;

    if (hit.type != intersection_type::triangle)
    {
        // A miss sees the lighting pass's ambient in its direction.
        float4 am = ambientMap(ambient, mapSampler, world, params);
        radiance = skyIrradiance(sky, skySampler, dir, params) * DIFFUSE * am.rgb * env;
        travel = 1e6f;
    }
    else if (!hit.triangle_front_facing)
    {
        // The back of an object is inside it: dark, and a negative distance (the blend counts these).
        radiance = float3(0.0f);
        travel = -0.2f * hit.distance;
    }
    else
    {
        const device GeometryRecord& g = records[firstRecord[hit.instance_id] + hit.geometry_id];
        float3 n = hitNormal(g, hit.primitive_id, hit.world_to_object_transform);
        if (dot(n, dir) > 0.0f) n = -n;

        float3 point = world + dir * hit.distance;
        float3 colour = params.albedo.rgb;
        if (g.kind == 0u && params.albedo.w > 0.5f)
            colour = ground.sample(mapSampler, (point.xz + params.maps.w) / (2.0f * params.maps.w), level(0.0f)).rgb;

        float4 amHit = ambientMap(ambient, mapSampler, point, params);
        float3 l = params.lightDir.xyz;
        float nl = max(dot(n, l), 0.0f);
        float3 sun = float3(0.0f);
        if (nl > 0.0f)
        {
            ray s;
            s.origin = point + n * (0.5f + hit.distance * 0.001f);
            s.direction = l;
            s.min_distance = 0.0f;
            s.max_distance = 1e5f;
            intersector<triangle_data, instancing> shadow;
            shadow.force_opacity(forced_opacity::opaque);
            shadow.set_triangle_cull_mode(triangle_cull_mode::back);
            shadow.accept_any_intersection(true);
            if (shadow.intersect(s, scene).type == intersection_type::none)
                sun = PI_DIFFUSE * nl * params.sunLight.rgb * amHit.a * 2.0f;
        }

        // The ambient outdoors is the sky's (the further bounces through the probes are not traced on Metal yet).
        float3 skyAmbient = skyIrradiance(sky, skySampler, n, params) * DIFFUSE * amHit.rgb * env;
        radiance = colour * (sun + skyAmbient);
        travel = hit.distance;
    }

    threadgroup float3 sRadiance[GI_RAYS];
    threadgroup float3 sDir[GI_RAYS];
    threadgroup float sDistance[GI_RAYS];
    threadgroup atomic_uint sBack;
    threadgroup float4 sIrradiance[GI_IRR * GI_IRR];
    threadgroup float2 sDist[GI_DIST * GI_DIST];
    if (tid == 0) atomic_store_explicit(&sBack, 0u, memory_order_relaxed);
    threadgroup_barrier(mem_flags::mem_threadgroup);

    if (travel < 0.0f) atomic_fetch_add_explicit(&sBack, 1u, memory_order_relaxed);
    sRadiance[tid] = radiance;
    sDir[tid] = dir;
    sDistance[tid] = travel;
    threadgroup_barrier(mem_flags::mem_threadgroup);

    // Back faces in a quarter of the rays mark the probe as inside; the blend over the last values (the hysteresis) keeps a noisy estimate from
    // switching a surface's light on and off.
    bool valid = float(atomic_load_explicit(&sBack, memory_order_relaxed)) < 0.25f * float(GI_RAYS);
    float hysteresis = params.timing.z;
    int2 tile = int2(colX, (c * GI_N + colZ) * GI_NY + layer);

    int2 irrOrigin = tile * int(GI_IRR + 2);
    for (uint t = tid; t < GI_IRR * GI_IRR; t += GI_RAYS)
    {
        int2 src = int2(t % GI_IRR, t / GI_IRR) + 1;
        float3 d = octDir((float2(src - 1) + 0.5f) / float(GI_IRR) * 2.0f - 1.0f);
        float3 sum = float3(0.0f);
        float total = 0.0f;
        for (uint i = 0; i < GI_RAYS; i++)
        {
            float w = max(dot(d, sDir[i]), 0.0f);
            sum += sRadiance[i] * w;
            total += w;
        }
        float3 value = total > 1e-4f ? sum / total : float3(0.0f);
        float4 old = irradianceOld.read(uint2(irrOrigin + src));
        float4 blended = mix(float4(value, valid ? 1.0f : 0.0f), old, hysteresis);
        if (!valid && blended.a < 0.001f) blended.a = 0.0f;
        sIrradiance[t] = blended;
    }

    float maxDistance = g1.z;
    int2 distOrigin = tile * int(GI_DIST + 2);
    for (uint t = tid; t < GI_DIST * GI_DIST; t += GI_RAYS)
    {
        int2 src = int2(t % GI_DIST, t / GI_DIST) + 1;
        float3 d = octDir((float2(src - 1) + 0.5f) / float(GI_DIST) * 2.0f - 1.0f);
        float2 sum = float2(0.0f);
        float total = 0.0f;
        for (uint i = 0; i < GI_RAYS; i++)
        {
            // cos^50 is below 1e-11 under 0.6: skip those rays.
            float cosine = dot(d, sDir[i]);
            if (cosine < 0.6f) continue;
            float c2 = cosine * cosine, c4 = c2 * c2, c8 = c4 * c4, c16 = c8 * c8, c32 = c16 * c16;
            float w = c32 * c16 * c2;
            float value = min(abs(sDistance[i]), maxDistance) / maxDistance;
            sum += float2(value, value * value) * w;
            total += w;
        }
        float2 value = total > 1e-4f ? sum / total : float2(1.0f, 1.0f);
        float2 old = distanceOld.read(uint2(distOrigin + src)).rg;
        sDist[t] = mix(value, old, hysteresis);
    }
    threadgroup_barrier(mem_flags::mem_threadgroup);

    // Every texel of the tiles, the borders from the inner texel they copy (bilinear filtering across the octahedron's seams).
    for (uint t = tid; t < (GI_IRR + 2) * (GI_IRR + 2); t += GI_RAYS)
    {
        int2 texel = int2(t % (GI_IRR + 2), t / (GI_IRR + 2)), src = inner(texel, GI_IRR) - 1;
        irradiance.write(sIrradiance[src.y * GI_IRR + src.x], uint2(irrOrigin + texel));
    }
    for (uint t = tid; t < (GI_DIST + 2) * (GI_DIST + 2); t += GI_RAYS)
    {
        int2 texel = int2(t % (GI_DIST + 2), t / (GI_DIST + 2)), src = inner(texel, GI_DIST) - 1;
        distance.write(float4(sDist[src.y * GI_DIST + src.x], 0.0f, 0.0f), uint2(distOrigin + texel));
    }
}
