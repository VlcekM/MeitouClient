namespace Meitou.Rendering.Gi;

/// <summary>
/// The lamps' traced shadows (docs/render-lights.md "Traced lamp shadows"): <see cref="Trace"/> finds per pixel what the lamps' diffuse light lost to
/// occluders, <see cref="Apply"/> takes it off the finished scene and <see cref="Floor"/> keeps the scene above zero.
/// </summary>
static class LampShadowShaders
{
    /// <summary>Most lamps a pixel traces.</summary>
    public const int MinLamps = 2, MaxLamps = 4;

    /// <summary>World units: the lamp's radius (KenshiNative's 10 cm) and what a ray leaves out before the lamp, for its fixture (KenshiNative's near-skip).</summary>
    const string Constants = """
        const float LAMP_RADIUS = 1.0, LAMP_NEAR_SKIP = 15.0;
        const float LAMP_MIN_BIAS = 0.5;

        """;

    /// <summary>
    /// <c>lampLight</c> with <c>LAMP_VISIBILITY</c> turned into a note of each lamp's diffuse term (radiance and <c>diffuseNl</c> are its locals where the macro
    /// expands), so the terms are the world's by construction: attenuation, spot cone, room clip. The strongest <c>LAMP_SLOTS</c> stay, in order.
    /// </summary>
    const string Picks = """
        int pickLamp[LAMP_SLOTS];
        uint pickEmitter[LAMP_SLOTS];
        float pickWeight[LAMP_SLOTS];
        vec3 pickLight[LAMP_SLOTS];
        int picked = 0;

        float lampNote(int lamp, vec3 light, uint emitter)
        {
            float weight = dot(light, vec3(0.2126, 0.7152, 0.0722));
            int at = picked;
            if (picked == LAMP_SLOTS)
            {
                at = LAMP_SLOTS - 1;
                if (weight <= pickWeight[at]) return 1.0;
            }
            else picked++;
            for (; at > 0 && pickWeight[at - 1] < weight; at--)
            {
                pickLamp[at] = pickLamp[at - 1];
                pickEmitter[at] = pickEmitter[at - 1];
                pickWeight[at] = pickWeight[at - 1];
                pickLight[at] = pickLight[at - 1];
            }
            pickLamp[at] = lamp;
            pickEmitter[at] = emitter;
            pickWeight[at] = weight;
            pickLight[at] = light;
            return 1.0;
        }
        #define LAMP_VISIBILITY(world, n, toLamp, indoor, emitter) lampNote(lamp, radiance * diffuseNl, emitter)

        """;

    /// <summary>
    /// One ray per picked lamp (Port of KenshiNative's <c>knLampSample</c>, its lamp-always-soft sectors): a point of the lamp's disc as the surface sees it, the four
    /// pixels of a 2 x 2 block in the disc's four sectors, the block's rotation and radius from the R2 sequence, moved each frame under a temporal resolve.
    /// </summary>
    const string Rays = """
        vec3 lampSample(vec3 lamp, vec3 world, ivec2 px, int id)
        {
            vec3 l = normalize(lamp - world);
            vec3 t = normalize(cross(abs(l.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), l));
            vec3 b = cross(l, t);
            vec2 k = vec2(px >> 1);
            float sector = float((px.x & 1) | ((px.y & 1) << 1));
            float r2a = fract(0.7548776662 * k.x + 0.5698402910 * k.y + 0.6180339887 * (float(id) + uPhase));
            float r2b = fract(0.5698402910 * k.x + 0.7548776662 * k.y + 0.4142135624 * (float(id) + uPhase));
            float angle = (sector + r2a) * 1.5707963268;
            float radius = LAMP_RADIUS * sqrt(fract(r2b + 0.6180339887 * sector));
            return lamp + (t * cos(angle) + b * sin(angle)) * radius;
        }
        // 0: something lies between the surface and the lamp. Ends short of the lamp (its fixture) and at its reach; a lamp's owner is passed through.
        float lampVisible(vec3 from, vec3 target, float reach, uint emitter)
        {
            vec3 ray = target - from;
            float d = length(ray), tMax = min(d - LAMP_NEAR_SKIP, reach);
            if (tMax <= 0.0) return 1.0;
            uint flags = gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsCullBackFacingTrianglesEXT;
            if (emitter != 0u) flags |= gl_RayFlagsNoOpaqueEXT;
            rayQueryEXT shadow;
            rayQueryInitializeEXT(shadow, uScene, flags, 0xFF, from, 0.0, ray / d, tMax);
            while (rayQueryProceedEXT(shadow))
            {
                int record = rayQueryGetIntersectionInstanceCustomIndexEXT(shadow, false) + rayQueryGetIntersectionGeometryIndexEXT(shadow, false);
                if ((emitter == 0u || geometries[record].emitter != emitter) && solid(record,
                    rayQueryGetIntersectionPrimitiveIndexEXT(shadow, false), rayQueryGetIntersectionBarycentricsEXT(shadow, false)))
                    rayQueryConfirmIntersectionEXT(shadow);
            }
            return rayQueryGetIntersectionTypeEXT(shadow, true) == gl_RayQueryCommittedIntersectionNoneEXT ? 1.0 : 0.0;
        }
        // What the occluded lamps took from a pixel, per unit of albedo.
        vec3 lostLight(ivec2 px)
        {
            uvec4 surface = texelFetch(uAlbedo, px, 0);
            if ((surface.w & 256u) == 0u) return vec3(0.0);
            float z = depthAt(px);
            if (z <= 0.0) return vec3(0.0);
            vec3 world = positionAt(px, z);
            vec3 n = giOctDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0);
            vec3 diffuse = vec3(0.0), specular = vec3(0.0);
            lampLight(world, n, normalize(uGiEye - world), 1.0, 1.0, diffuse, specular);
            if (picked == 0) return vec3(0.0);
            // Off the surface by what its depth and the traced mesh can be wrong by: a floor, and two pixels' width there.
            vec3 from = world - uOrigin + n * max(LAMP_MIN_BIAS, 2.0 * z * uFootprint);
            vec3 lost = vec3(0.0);
            for (int i = 0; i < picked; i++)
            {
                vec4 p = texelFetch(uLightData, ivec2(0, pickLamp[i]), 0);
                vec3 target = lampSample(p.xyz, world, px, pickLamp[i]) - uOrigin;
                if (lampVisible(from, target, p.w, pickEmitter[i]) < 0.5) lost += pickLight[i];
            }
            return lost * 3.14159265358979 * LAMP_DIFFUSE;
        }

        """;

    /// <summary>
    /// The lost light of every pixel of the render size into <c>uLost</c> (rgb per unit of albedo; 0 where no lamp is blocked or the surface target has nothing).
    /// <paramref name="lamps"/>: the most lamps a pixel traces (<see cref="MinLamps"/> to <see cref="MaxLamps"/>).
    /// </summary>
    public static string Trace(int lamps) => $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8) in;
        {{Meitou.Rendering.Gpu.BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform sampler2D uNearDepth;
        layout(set = 0, binding = 1) uniform usampler2D uAlbedo;
        layout(set = 0, binding = 2) uniform accelerationStructureEXT uScene;
        {{GiShaders.GeometryStruct}}
        layout(set = 0, binding = 3, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 4, std140) uniform Params
        {
            vec4 uLightGrid;
            vec3 uRight;    float uFootprint;   // a pixel's width at depth 1
            vec3 uUp;       float uPhase;       // the frame's step of the sample pattern
            vec3 uBack;     float pad0;
            vec3 uGiEye;    float pad1;
            vec3 uOrigin;   float pad2;         // of the traced scene
            vec2 uNearPlanes;
            vec2 uTan;
            vec2 uFullSize;
        };
        layout(set = 0, binding = 5, rgba16f) uniform writeonly image2D uLost;
        layout(set = 0, binding = 6) uniform sampler2D uLightCells;
        layout(set = 0, binding = 7) uniform sampler2D uLightIndex;
        layout(set = 0, binding = 8) uniform sampler2D uLightData;
        const int LAMP_SLOTS = {{Math.Clamp(lamps, MinLamps, MaxLamps)}};
        {{Constants}}{{Picks}}{{LampShaders.Defines}}{{LampShaders.Lighting}}{{GiResolveShaders.ReconstructFunctions}}
        {{GiShaders.OctDirection}}
        {{GiShaders.HitBuffers}}
        {{GiShaders.HitCutout}}
        {{Rays}}
        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(uFullSize.x) || px.y >= int(uFullSize.y)) return;
            imageStore(uLost, px, vec4(lostLight(px), 0.0));
        }
        """;

    /// <summary>
    /// The lost light times the albedo and the haze's transmittance (as the fragment's light was hazed; <see cref="GiResolveShaders.Resolve"/>), blended off the
    /// scene (<c>dst - src</c>; red, green and blue).
    /// </summary>
    public static readonly string Apply = "#version 330 core\n" + AtmosphereShaders.Functions + GiResolveShaders.Reconstruct + """
        in vec2 vUv;
        out vec4 fragColour;
        uniform usampler2D uAlbedo;
        uniform sampler2D uLost;

        void main()
        {
            ivec2 px = ivec2(floor(gl_FragCoord.xy));
            vec3 lost = texelFetch(uLost, px, 0).rgb;
            if (max(lost.r, max(lost.g, lost.b)) <= 0.0) discard;
            uvec4 surface = texelFetch(uAlbedo, px, 0);
            vec3 p = positionAt(px, depthAt(px));
            vec3 transmittance = atmoApply(vec3(1.0), uGiEye, p) - atmoApply(vec3(0.0), uGiEye, p);
            vec3 albedo = vec3(float(surface.z & 255u), float(surface.z >> 8), float(surface.w & 255u)) / 255.0;
            fragColour = vec4(albedo * albedo * lost * transmittance, 0.0);
        }
        """;

    /// <summary>Drawn with a max blend after <see cref="Apply"/>: the stored albedo's rounding must not take a pixel below black.</summary>
    public const string Floor = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        void main() { fragColour = vec4(0.0); }
        """;
}
