#include <metal_stdlib>
using namespace metal;
using namespace metal::raytracing;

// A first Metal DDGI pass: terrain + nearby object instances, one probe group per threadgroup.
// The atlas layout and the octahedral border mapping match GiShaders.ProbeSampling/ProbeBlend,
// so the existing world shaders can sample the result unchanged.
constant uint GI_N = 32;
constant uint GI_NY = 8;
constant uint GI_IRR = 8;
constant uint GI_DIST = 16;
constant uint GI_RAYS = 32;

struct ProbeConstants
{
    float4x4 rotation;
    float4 grid[4];
    float4 sunLight;
    float4 lightDir;
    float4 albedo;
    float4 skyFallback;   // w: a sky cube is bound
    float4 maps;          // z: the ground map is present, w: its half-size
    float4 timing;        // x: this frame's phase, y: phases
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

float3 skyLight(texturecube<float, access::sample> sky, sampler skySampler, float3 dir, constant ProbeConstants& p)
{
    if (p.skyFallback.w < 0.5f) return p.skyFallback.xyz;
    float4 t = sky.sample(skySampler, dir, level(3.0f));
    return t.rgb * t.a * 4.0f * p.lightDir.w;
}

kernel void probe_gi(uint group [[threadgroup_position_in_grid]],
                     uint tid [[thread_position_in_threadgroup]],
                     constant ProbeConstants& params [[buffer(0)]],
                     const device float4* instanceInfo [[buffer(1)]],
                     instance_acceleration_structure scene [[buffer(2)]],
                     texture2d<float, access::write> irradiance [[texture(0)]],
                     texture2d<float, access::write> distance [[texture(1)]],
                     texture2d<float, access::read> bases [[texture(2)]],
                     texturecube<float, access::sample> sky [[texture(3)]],
                     sampler skySampler [[sampler(0)]],
                     texture2d<float, access::sample> ground [[texture(4)]],
                     sampler groundSampler [[sampler(1)]])
{
    uint phases = max(uint(params.timing.y), 1u);
    uint probe = group * phases + uint(params.timing.x);
    if (probe >= GI_N * GI_N * GI_NY * 2 || tid >= GI_RAYS) return;

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
    r.max_distance = c == 0 ? 4000.0f : 16000.0f;

    intersector<triangle_data, instancing> isect;
    isect.force_opacity(forced_opacity::opaque);
    auto hit = isect.intersect(r, scene);

    float3 radiance = float3(0.0f);
    float travel = 1e6f;
    bool back = false;
    if (hit.type == intersection_type::triangle)
    {
        travel = hit.distance;
        float4 info = instanceInfo[hit.instance_id];
        float3 n = -dir;
        bool terrain = info.w < 0.5f;
        if (!terrain && !hit.triangle_front_facing)
        {
            back = true;
            travel = -travel;
        }
        else
        {
            float3 sun = float3(0.0f);
            float3 sunDir = params.lightDir.xyz;
            float nl = max(dot(n, sunDir), 0.0f);
            if (nl > 0.0f)
            {
                ray s;
                s.origin = r.origin + dir * hit.distance + n * 0.5f;
                s.direction = sunDir;
                s.min_distance = 0.0f;
                s.max_distance = 20000.0f;
                intersector<triangle_data, instancing> shadow;
                shadow.force_opacity(forced_opacity::opaque);
                auto blocked = shadow.intersect(s, scene);
                if (blocked.type == intersection_type::none) sun = params.sunLight.rgb * nl * 6.0f;
            }
            float3 albedo = info.xyz;
            if (terrain && params.maps.z > 0.5f)
            {
                float3 hitPoint = r.origin + dir * hit.distance;
                float2 uv = (hitPoint.xz + params.maps.w) / (2.0f * params.maps.w);
                albedo = ground.sample(groundSampler, uv).rgb;
            }
            radiance = albedo * (sun + skyLight(sky, skySampler, n, params) * 2.0f) * 3.0f;
        }
    }

    threadgroup float3 sRadiance[GI_RAYS];
    threadgroup float3 sDir[GI_RAYS];
    threadgroup float sDistance[GI_RAYS];
    threadgroup atomic_uint sBack;
    if (tid == 0) atomic_store_explicit(&sBack, 0u, memory_order_relaxed);
    if (back) atomic_fetch_add_explicit(&sBack, 1u, memory_order_relaxed);
    sRadiance[tid] = radiance;
    sDir[tid] = dir;
    sDistance[tid] = travel;
    threadgroup_barrier(mem_flags::mem_threadgroup);

    bool valid = float(atomic_load_explicit(&sBack, memory_order_relaxed)) < 0.25f * float(GI_RAYS);
    int2 irrOrigin = int2(colX, (c * GI_N + colZ) * GI_NY + layer) * int(GI_IRR + 2);
    for (uint t = tid; t < (GI_IRR + 2) * (GI_IRR + 2); t += GI_RAYS)
    {
        int2 texel = int2(t % (GI_IRR + 2), t / (GI_IRR + 2));
        int2 src = inner(texel, GI_IRR) - 1;
        float3 d = octDir((float2(src) + 0.5f) / float(GI_IRR) * 2.0f - 1.0f);
        float3 sum = float3(0.0f);
        float total = 0.0f;
        for (uint i = 0; i < GI_RAYS; i++)
        {
            float w = max(dot(d, sDir[i]), 0.0f);
            sum += sRadiance[i] * w;
            total += w;
        }
        float3 value = total > 1e-4f ? sum / total : float3(0.0f);
        irradiance.write(float4(value, valid ? 1.0f : 0.0f), uint2(irrOrigin + texel));
    }

    float maxDistance = g1.z;
    int2 distOrigin = int2(colX, (c * GI_N + colZ) * GI_NY + layer) * int(GI_DIST + 2);
    for (uint t = tid; t < (GI_DIST + 2) * (GI_DIST + 2); t += GI_RAYS)
    {
        int2 texel = int2(t % (GI_DIST + 2), t / (GI_DIST + 2));
        int2 src = inner(texel, GI_DIST) - 1;
        float3 d = octDir((float2(src) + 0.5f) / float(GI_DIST) * 2.0f - 1.0f);
        float2 sum = float2(0.0f);
        float total = 0.0f;
        for (uint i = 0; i < GI_RAYS; i++)
        {
            float cosine = dot(d, sDir[i]);
            if (cosine < 0.6f) continue;
            float c2 = cosine * cosine, c4 = c2 * c2, c8 = c4 * c4, c16 = c8 * c8, c32 = c16 * c16;
            float w = c32 * c16 * c2;
            float value = min(abs(sDistance[i]), maxDistance) / maxDistance;
            sum += float2(value, value * value) * w;
            total += w;
        }
        float2 value = total > 1e-4f ? sum / total : float2(1.0f, 1.0f);
        distance.write(float4(value, 0.0f, 0.0f), uint2(distOrigin + texel));
    }
}
