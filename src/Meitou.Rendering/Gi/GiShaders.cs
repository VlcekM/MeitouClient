namespace Meitou.Rendering.Gi;

/// <summary>The global illumination's shaders (docs/render-gi.md).</summary>
static class GiShaders
{
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
