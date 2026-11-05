namespace Meitou.Rendering.Gi;

/// <summary>
/// The hybrid near field (docs/render-gi.md "Hybrid GI"): short rays per texel of the GI resolve's low target, the probes as their radiance cache, an accumulation
/// with reprojection and an edge-stopped filter. Port of KenshiNative's ideas for its traced GI (its tangent-plane edge weight, the wide iterations held by what
/// the rays found, the per-sample clamp); shading the hits and the history are Meitou's, as the probes already hold the far light.
/// </summary>
static class GiNearShaders
{
    /// <summary>A low texel's surface as stored in the surface images: world position, and the normal's octahedral position (12 bits each) plus one as an exact integer,
    /// negative for an interior; 0: nothing in the block takes the probes.</summary>
    const string SurfaceCode = """
        vec3 surfaceOctDir(vec2 p)
        {
            vec3 d = vec3(p.x, 1.0 - abs(p.x) - abs(p.y), p.y);
            if (d.y < 0.0) d.xz = (1.0 - abs(d.zx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.z >= 0.0 ? 1.0 : -1.0);
            return normalize(d);
        }
        vec3 surfaceNormal(float code)
        {
            uint bits = uint(abs(code)) - 1u;
            return surfaceOctDir(vec2(float(bits & 4095u), float(bits >> 12)) / 4095.0 * 2.0 - 1.0);
        }

        """;

    /// <summary>
    /// How much a texel speaks for another on the same surface (KenshiNative's <c>knGiEdgeWeight</c>): the distance from the centre's tangent plane, not relative depth (it
    /// refuses ground seen at a grazing angle), measured in <c>texel</c> world units, times the normals' agreement. An interior and an exterior texel never mix.
    /// </summary>
    const string EdgeWeight = SurfaceCode + """
        float edgeWeight(vec4 centre, vec3 n, vec4 tap, float texel)
        {
            if (tap.w == 0.0 || (tap.w < 0.0) != (centre.w < 0.0)) return 0.0;
            float plane = abs(dot(n, tap.xyz - centre.xyz));
            float facing = clamp(dot(n, surfaceNormal(tap.w)), 0.0, 1.0);
            float f2 = facing * facing, f4 = f2 * f2, f8 = f4 * f4;
            return exp(-plane / max(texel, 1e-6)) * f8 * f8;
        }

        """;

    /// <summary>The trace's Params block: <see cref="GiProbes"/>' constants, then the near field's (<see cref="GiNearField"/>).</summary>
    static readonly string Source = $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8) in;
        {{Meitou.Rendering.Gpu.BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform accelerationStructureEXT uScene;
        struct Geometry { uvec2 vertices; uvec2 indices; uint stride; uint kind; uint diffuse; uint alphaMap; float alphaThreshold; uint emitter; uint pad1; uint pad2; };
        layout(set = 0, binding = 1, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 2, rgba16f) uniform writeonly image2D uRaw;
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
            vec4 nearReach;     // x the rays' length, y where a hit counts as near (the filter's guide), z the eye distance the field ends at, w the most one sample may bring
            vec4 nearFrame;     // x the frame, y the low target's divisor, zw the render size
            vec4 nearPlanes;    // xy the scene depth's near and far, zw tan(fov/2) * aspect, tan(fov/2)
            vec4 nearEye, nearRight, nearUp, nearBack;
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
        layout(set = 0, binding = 13) uniform usampler2D uSurfaceTarget;
        layout(set = 0, binding = 14) uniform sampler2D uNearDepth;
        layout(set = 0, binding = 17, rgba32f) uniform writeonly image2D uSurface;
        #define LAMP_DIFFUSE_ONLY
        float giLampVisibility(vec3 world, vec3 n, vec3 toLamp, int indoor, uint emitter);
        #define LAMP_VISIBILITY(world, n, toLamp, indoor, emitter) giLampVisibility(world, n, toLamp, indoor, emitter)
        {{LampShaders.Defines}}{{LampShaders.Lighting}}
        {{GiShaders.ProbeSampling}}
        {{GiShaders.HitFunctions}}
        {{DepthConvention.Glsl}}

        // The view depth at a pixel of the render size (0: the sky), and its position: as GiResolveShaders' reconstruction.
        float depthAt(ivec2 px)
        {
            float d = texelFetch(uNearDepth, px, 0).r;
            return depthIsSky(d) ? 0.0 : depthViewZ(d, nearPlanes.xy);
        }
        vec3 positionAt(ivec2 px, float z)
        {
            vec2 ndc = (vec2(px) + 0.5) / nearFrame.zw * 2.0 - 1.0;
            return nearEye.xyz + (nearRight.xyz * ndc.x * nearPlanes.z + nearUp.xyz * ndc.y * nearPlanes.w - nearBack.xyz) * z;
        }
        // The surface's flat normal from the depth (KenshiNative lobes and filters on the smooth normal: a mapped normal's bumps are noise to both), on the side with the smaller step at an edge.
        vec3 sideStep(ivec2 px, vec3 p, ivec2 o)
        {
            ivec2 last = ivec2(nearFrame.zw) - 1, a = clamp(px + o, ivec2(0), last), b = clamp(px - o, ivec2(0), last);
            float za = depthAt(a), zb = depthAt(b);
            vec3 ahead = za > 0.0 ? positionAt(a, za) - p : vec3(1e9), behind = zb > 0.0 ? p - positionAt(b, zb) : vec3(1e9);
            return dot(ahead, ahead) < dot(behind, behind) ? ahead : behind;
        }
        vec3 flatNormal(ivec2 px, vec3 p, vec3 shading)
        {
            vec3 g = cross(sideStep(px, p, ivec2(1, 0)), sideStep(px, p, ivec2(0, 1)));
            float size = dot(g, g);
            if (size < 1e-12 || size > 1e30) return shading;
            g /= sqrt(size);
            return dot(g, nearEye.xyz - p) < 0.0 ? -g : g;
        }
        // The block's surface, as the resolve picks it: the first pixel of the block that takes the probes. False: none.
        bool blockSurface(ivec2 low, out ivec2 px, out uvec4 surface)
        {
            int div = int(nearFrame.y);
            px = ivec2(-1);
            surface = uvec4(0u);
            for (int k = 0; k < 4 && px.x < 0; k++)
            {
                ivec2 q = min(low * div + ivec2(k & 1, k >> 1) * (div - 1), ivec2(nearFrame.zw) - 1);
                uvec4 s = texelFetch(uSurfaceTarget, q, 0);
                if ((s.w & 256u) != 0u) { px = q; surface = s; }
            }
            return px.x >= 0;
        }
        float surfaceCode(vec3 n, bool interior)
        {
            uvec2 o = uvec2((giOct(n) * 0.5 + 0.5) * 4095.0 + 0.5);
            float code = float((o.x | (o.y << 12)) + 1u);
            return interior ? -code : code;
        }

        // Per texel a fixed point, moved along R2 each frame: the accumulation sees every sample once in many frames.
        vec2 sampleAt(uvec2 p, uint frame)
        {
            uvec2 v = p * 1664525u + 1013904223u;
            v.x += v.y * 1664525u; v.y += v.x * 1664525u;
            v ^= v >> 16u;
            v.x += v.y * 1664525u; v.y += v.x * 1664525u;
            v ^= v >> 16u;
            return fract(vec2(v) / 4294967296.0 + vec2(0.7548776662, 0.5698402910) * float(frame & 4095u));
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

        void main()
        {
            ivec2 low = ivec2(gl_GlobalInvocationID.xy);
            if (any(greaterThanEqual(low, imageSize(uRaw)))) return;
            ivec2 px;
            uvec4 surface;
            float z = blockSurface(low, px, surface) ? depthAt(px) : 0.0;
            if (z <= 0.0)
            {
                imageStore(uSurface, low, vec4(0.0));
                imageStore(uRaw, low, vec4(0.0));
                return;
            }
            vec3 p = positionAt(px, z), n = flatNormal(px, p, giOctDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0));
            imageStore(uSurface, low, vec4(p, surfaceCode(n, (surface.w & 512u) != 0u)));
            if (z >= nearReach.z)
            {
                imageStore(uRaw, low, vec4(0.0));
                return;
            }
            vec3 dir = cosineDirection(n, sampleAt(uvec2(low), uint(nearFrame.x)));
            // Off the surface by what the depth's precision and the traced geometry's difference from the drawn one call for.
            vec3 world = p + n * (0.05 + z * 0.002);
            rayQueryEXT rq;
            rayQueryInitializeEXT(rq, uScene, gl_RayFlagsCullBackFacingTrianglesEXT, 0xFF, world - origin.xyz, 0.0, dir, nearReach.x);
            TRACE(rq)
            vec3 light;
            float near = 0.0;
            if (rayQueryGetIntersectionTypeEXT(rq, true) != gl_RayQueryCommittedIntersectionTriangleEXT) light = cached(world + dir * nearReach.x, dir);
            else
            {
                float t = rayQueryGetIntersectionTEXT(rq, true);
                int record = rayQueryGetIntersectionInstanceCustomIndexEXT(rq, true) + rayQueryGetIntersectionGeometryIndexEXT(rq, true);
                Geometry g = geometries[record];
                // A ray that starts inside an object (a drawn level of it, not the traced one) learns nothing: the cache, not black.
                if (!rayQueryGetIntersectionFrontFaceEXT(rq, true) && g.kind != {{GeometryRecord.Foliage}}u) light = cached(world, dir);
                else
                {
                    float env = lightDir.w;
        {{GiShaders.HitShade}}
                    light = colour * (sun + ambient + lamps);
                    near = t < nearReach.y ? 1.0 : 0.0;
                }
            }
            imageStore(uRaw, low, vec4(min(light, vec3(nearReach.w)), near));
        }
        """;

    static string F(float v) => v.ToString("0.0########", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The trace for the frame's options (<see cref="GiShaders.Relocating"/>, <see cref="GiShaders.Clouded"/>).</summary>
    public static string Trace(bool relocate, bool clouds)
    {
        string trace = Source;
        if (clouds) trace = GiShaders.Clouded(trace.Replace("vec4 cloudSlots[3];", "", StringComparison.Ordinal));
        return relocate ? GiShaders.Relocating(trace) : trace;
    }

    /// <summary>
    /// The accumulation: each texel's point back in last frame's picture, the surface images' four texels round it weighted by the bilinear weights and
    /// <see cref="EdgeWeight"/>; with any left, this frame's sample takes 1 / (samples so far) of the mean, down to 1 / <c>size.z</c>. Out: rgb the light, a the share of near hits; the sample count apart.
    /// </summary>
    public const string Temporal = """
        #version 450
        layout(local_size_x = 8, local_size_y = 8) in;
        layout(set = 0, binding = 0, rgba32f) uniform readonly image2D uSurface;
        layout(set = 0, binding = 1, rgba32f) uniform readonly image2D uPreviousSurface;
        layout(set = 0, binding = 2, rgba16f) uniform readonly image2D uRaw;
        layout(set = 0, binding = 3, rgba16f) uniform readonly image2D uPreviousHistory;
        layout(set = 0, binding = 4, r16f) uniform readonly image2D uPreviousLength;
        layout(set = 0, binding = 5, rgba16f) uniform writeonly image2D uHistory;
        layout(set = 0, binding = 6, r16f) uniform writeonly image2D uLength;
        layout(set = 0, binding = 7, std140) uniform Params
        {
            vec4 size;      // x, y the low target, z the most samples a texel keeps, w 1 with a history
            vec4 texel;     // x a low texel's width in world units at distance 1
            vec4 eye, back; // this frame's, for the fade
            vec4 previousEye, previousRight, previousUp, previousBack;
            vec4 previousTan;
        };

        """ + EdgeWeight + """
        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (any(greaterThanEqual(px, ivec2(size.xy)))) return;
            vec4 surface = imageLoad(uSurface, px);
            vec4 raw = imageLoad(uRaw, px);
            vec4 history = vec4(0.0);
            float samples = 0.0;
            if (surface.w != 0.0 && size.w > 0.5)
            {
                vec3 n = surfaceNormal(surface.w);
                vec3 v = surface.xyz - previousEye.xyz;
                float z = dot(v, -previousBack.xyz);
                vec2 ndc = vec2(dot(v, previousRight.xyz) / max(z * previousTan.x, 1e-6), dot(v, previousUp.xyz) / max(z * previousTan.y, 1e-6));
                if (z > 0.0 && all(lessThan(abs(ndc), vec2(1.0))))
                {
                    vec2 f = (ndc * 0.5 + 0.5) * size.xy - 0.5;
                    ivec2 i0 = ivec2(floor(f));
                    vec2 w = f - vec2(i0);
                    float tolerance = max(length(surface.xyz - eye.xyz), 1.0) * texel.x, total = 0.0;
                    for (int k = 0; k < 4; k++)
                    {
                        ivec2 o = ivec2(k & 1, k >> 1), q = clamp(i0 + o, ivec2(0), ivec2(size.xy) - 1);
                        float weight = (o.x == 1 ? w.x : 1.0 - w.x) * (o.y == 1 ? w.y : 1.0 - w.y);
                        weight *= edgeWeight(surface, n, imageLoad(uPreviousSurface, q), tolerance);
                        if (weight <= 1e-4) continue;
                        history += imageLoad(uPreviousHistory, q) * weight;
                        samples += imageLoad(uPreviousLength, q).r * weight;
                        total += weight;
                    }
                    if (total > 1e-3) { history /= total; samples /= total; }
                    else { history = vec4(0.0); samples = 0.0; }
                }
            }
            samples = surface.w == 0.0 ? 0.0 : min(samples + 1.0, size.z);
            imageStore(uHistory, px, samples > 0.0 ? mix(history, raw, 1.0 / samples) : vec4(0.0));
            imageStore(uLength, px, vec4(samples));
        }
        """;

    /// <summary>
    /// One a-trous iteration (non-separable, a 3 x 3 kernel, as KenshiNative's: a separable one turns a bright sample into a rectangle) over the accumulated light, held to the surface by
    /// <see cref="EdgeWeight"/>. The wide iteration is also held by the share of near hits, as ground under an awning and the open ground next to it are one plane (KenshiNative's
    /// <c>gi-denoise</c>: unguided, the wide steps flatten that contact shade; the narrow ones need no guide). On the last iteration a becomes how much of this
    /// texel the resolve takes: full after a few samples, fading out towards the end of the field.
    /// </summary>
    public const string Filter = """
        #version 450
        layout(local_size_x = 8, local_size_y = 8) in;
        layout(set = 0, binding = 0, rgba32f) uniform readonly image2D uSurface;
        layout(set = 0, binding = 1, rgba16f) uniform readonly image2D uIn;
        layout(set = 0, binding = 2, r16f) uniform readonly image2D uLength;
        layout(set = 0, binding = 3, rgba16f) uniform writeonly image2D uOut;
        layout(set = 0, binding = 4, std140) uniform Params
        {
            vec4 size;      // x, y the low target, z the most samples a texel keeps
            vec4 texel;     // x a low texel's width in world units at distance 1, y the eye distance the field ends at
            vec4 eye, back;
        };
        layout(push_constant) uniform Iteration { int spread; int guided; int last; };

        """ + EdgeWeight + """
        const float KERNEL[3] = float[3](0.25, 0.5, 0.25);

        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (any(greaterThanEqual(px, ivec2(size.xy)))) return;
            vec4 surface = imageLoad(uSurface, px);
            if (surface.w == 0.0) { imageStore(uOut, px, vec4(0.0)); return; }
            vec3 n = surfaceNormal(surface.w);
            vec4 centre = imageLoad(uIn, px);
            float distanceToEye = length(surface.xyz - eye.xyz), tolerance = distanceToEye * texel.x * float(spread);
            vec4 sum = vec4(0.0);
            float total = 0.0;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    ivec2 q = clamp(px + ivec2(x, y) * spread, ivec2(0), ivec2(size.xy) - 1);
                    float weight = KERNEL[x + 1] * KERNEL[y + 1] * edgeWeight(surface, n, imageLoad(uSurface, q), tolerance);
                    if (weight <= 0.0) continue;
                    vec4 tap = imageLoad(uIn, q);
                    if (guided != 0) weight *= exp(-16.0 * abs(centre.a - tap.a));
                    sum += tap * weight;
                    total += weight;
                }
            vec4 result = total > 1e-5 ? sum / total : centre;
            if (last != 0)
            {
                float z = dot(surface.xyz - eye.xyz, -back.xyz);
                float fade = 1.0 - smoothstep(0.75 * texel.y, texel.y, z);
                result.a = fade * clamp(imageLoad(uLength, px).r / 4.0, 0.0, 1.0);
            }
            imageStore(uOut, px, result);
        }
        """;
}
