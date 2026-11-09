namespace Meitou.Rendering.Gi;

/// <summary>
/// The lamps' traced shadows (docs/render-lights.md "Traced lamp shadows"): <see cref="Trace"/> finds per pixel which share of the traced lamps' diffuse light occluders take, and how
/// much light that is of, <see cref="Apply"/> takes it off the finished scene and <see cref="Floor"/> keeps the scene above zero.
/// </summary>
static class LampShadowShaders
{
    /// <summary>Most lamps a pixel traces.</summary>
    public const int MinLamps = 2, MaxLamps = 2;

    /// <summary>World units: what a ray leaves out before the lamp, for its fixture (KenshiNative's near-skip), and the lamp's radius its penumbra is filtered for (a flame or a bulb in its glass, 20 cm).</summary>
    const string Constants = """
        const float LAMP_NEAR_SKIP = 15.0, LAMP_SIZE = 2.0;
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

        float lampAttenuation(float d, float radius);

        vec3 rankPlane;   // the drawn surface's plane at the pixel (lampPlane)

        // Ranks a lamp reaching the point by its light on the drawn plane (attenuation, cone, the plane's turn): not the shading normal, whose bumps would change which lamps a pixel traces,
        // the neighbouring pixels' slots would then hold other lamps and the filter would draw their borders along the ripples.
        void lampRank(int lamp, vec3 l, float d, float radius)
        {
            vec4 c = texelFetch(uLightData, ivec2(1, lamp), 0);
            vec4 s2 = texelFetch(uLightData, ivec2(3, lamp), 0);
            float weight = dot(c.rgb, vec3(0.2126, 0.7152, 0.0722)) * lampAttenuation(d, radius) * max(dot(rankPlane, l), 0.02);
            if (c.w > 0.5)
            {
                vec4 s = texelFetch(uLightData, ivec2(2, lamp), 0);
                float cosAngle = dot(s.xyz, -l);
                if (cosAngle <= s.w) return;
                weight *= s2.x - s.w < 1e-5 ? 1.0 : clamp(pow(clamp((cosAngle - s.w) / (s2.x - s.w), 0.0, 1.0), s2.y), 0.0, 1.0);
            }
            if (weight <= 0.0) return;
            int at = picked;
            if (picked == LAMP_SLOTS)
            {
                at = LAMP_SLOTS - 1;
                if (weight <= pickWeight[at]) return;
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
            pickEmitter[at] = uint(s2.w);
            pickWeight[at] = weight;
            pickLight[at] = vec3(0.0);
        }
        // The light of a ranked lamp at this surface.
        float lampNote(int lamp, vec3 light)
        {
            for (int i = 0; i < picked; i++)
                if (pickLamp[i] == lamp) pickLight[i] = light;
            return 1.0;
        }
        #define LAMP_RANK(lamp, l, d, radius) lampRank(lamp, l, d, radius)
        #define LAMP_VISIBILITY(world, n, toLamp, indoor, emitter) lampNote(lamp, radiance * diffuseNl)

        """;

    /// <summary>
    /// One ray per picked lamp, to its centre: the same every frame (no noise for the upscaler to average, nothing to trail). A lamp is a point of about a decimetre, so its
    /// shadows are near hard.
    /// </summary>
    static readonly string Rays = $$"""
        // How far along the ray its blocker is (0: nothing between the surface and the lamp). Ends short of the lamp (its fixture) and at its reach; a lamp's owner is passed through.
        float lampBlocker(vec3 from, vec3 target, float reach, uint emitter)
        {
            vec3 ray = target - from;
            float d = length(ray), tMax = min(d - LAMP_NEAR_SKIP, reach);
            if (tMax <= 0.0) return 0.0;
            uint flags = gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsCullBackFacingTrianglesEXT;
            if (emitter != 0u) flags |= gl_RayFlagsNoOpaqueEXT;
            rayQueryEXT shadow;
            // Everything the scene holds: what the probes see, the small foliage, the grass and the characters (TracedSunShaders.LampMask).
            rayQueryInitializeEXT(shadow, uScene, flags, {{TracedSunShaders.LampMask}}, from, 0.0, ray / d, tMax);
            while (rayQueryProceedEXT(shadow))
            {
                int record = rayQueryGetIntersectionInstanceCustomIndexEXT(shadow, false) + rayQueryGetIntersectionGeometryIndexEXT(shadow, false);
                if ((emitter == 0u || geometries[record].emitter != emitter) && casterKept(record,
                    rayQueryGetIntersectionPrimitiveIndexEXT(shadow, false), rayQueryGetIntersectionBarycentricsEXT(shadow, false), 2.0))
                    rayQueryConfirmIntersectionEXT(shadow);
            }
            return rayQueryGetIntersectionTypeEXT(shadow, true) == gl_RayQueryCommittedIntersectionNoneEXT ? 0.0 : max(rayQueryGetIntersectionTEXT(shadow, true), 1e-3);
        }
        // The drawn triangle's plane from the depth (the neighbours of the smaller step), facing like n: the ray leaves off it, not off the normal map's bumps, whose
        // pixel-to-pixel turns moved the origins and frayed the shadows' edges.
        vec3 lampPlane(ivec2 px, vec3 at, vec3 n)
        {
            vec3 q[4];
            ivec2 o[4] = ivec2[4](ivec2(-1, 0), ivec2(1, 0), ivec2(0, -1), ivec2(0, 1));
            for (int k = 0; k < 4; k++)
            {
                ivec2 s = clamp(px + o[k], ivec2(0), ivec2(uFullSize) - 1);
                float zk = depthAt(s);
                q[k] = zk > 0.0 ? positionAt(s, zk) : at;
            }
            vec3 l = at - q[0], r = q[1] - at, d = at - q[2], u = q[3] - at;
            vec3 g = cross(dot(r, r) < dot(l, l) && dot(r, r) > 0.0 ? r : l, dot(u, u) < dot(d, d) && dot(u, u) > 0.0 ? u : d);
            float len = length(g);
            if (!(len > 1e-8)) return n;
            g /= len;
            return dot(g, n) < 0.0 ? -g : g;
        }
        // Per traced lamp, in the lamps' index order so a slot means one lamp across the neighbouring pixels: whether occluders block it (r, g; the filter softens these) and its light per
        // unit of albedo (totals), and the penumbra their blockers leave (a: world units, 0 none; PenumbraFilter). Each lamp is filtered apart: a mean of the light, or of one share of two
        // lamps together, follows the slopes of the ground and left them lit or mottled inside a shadow (a thin orange line along a wall's foot, a pale trail moving with a character).
        vec4 lostLight(ivec2 px, out vec3 total0, out vec3 total1)
        {
            total0 = total1 = vec3(0.0);
            uvec4 surface = texelFetch(uAlbedo, px, 0);
            if ((surface.w & 256u) == 0u) return vec4(0.0, 0.0, 0.0, -1.0);   // no receiver: the penumbra filter leaves it out
            float z = depthAt(px);
            if (z <= 0.0) return vec4(0.0, 0.0, 0.0, -1.0);
            vec3 world = positionAt(px, z);
            vec3 n = giOctDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0);
            vec3 diffuse = vec3(0.0), specular = vec3(0.0);
            rankPlane = lampPlane(px, world, n);
            lampLight(world, n, normalize(uGiEye - world), 1.0, 1.0, diffuse, specular);
            // No lamp lights it (turned away from them all, or out of their reach): no receiver either. Read as unblocked, a caster's side turned from the lamp lit the
            // shadowed ground at its foot through the penumbra filter, as the sun's did (PenumbraFilter).
            vec3 anyLight = vec3(0.0);
            for (int i = 0; i < picked; i++) anyLight += pickLight[i];
            if (dot(anyLight, vec3(1.0)) <= 0.0) return vec4(0.0, 0.0, 0.0, -1.0);
            // Off the surface's plane by what its depth and the traced mesh can be wrong by: a floor, and two pixels' width there.
            vec3 from = world - uOrigin + rankPlane * max(LAMP_MIN_BIAS, 2.0 * z * uFootprint);
            if (picked == 2 && pickLamp[0] > pickLamp[1])
            {
                int lamp = pickLamp[0]; pickLamp[0] = pickLamp[1]; pickLamp[1] = lamp;
                uint emitter = pickEmitter[0]; pickEmitter[0] = pickEmitter[1]; pickEmitter[1] = emitter;
                vec3 light = pickLight[0]; pickLight[0] = pickLight[1]; pickLight[1] = light;
            }
            vec3 lit = vec3(0.0);
            vec2 blocked = vec2(0.0);
            float spread = 0.0, takenAll = 0.0;
            for (int i = 0; i < picked; i++)
            {
                lit += pickLight[i];
                vec4 p = texelFetch(uLightData, ivec2(0, pickLamp[i]), 0);
                vec3 target = p.xyz - uOrigin;   // the lamp's centre, the same every frame; its size is the filter's
                float t = lampBlocker(from, target, p.w, pickEmitter[i]);
                if (t <= 0.0) continue;
                blocked[i] = 1.0;
                // A lamp LAMP_SIZE across seen past a blocker at t of the lamp's d: the penumbra's half width at the receiver, weighted by the light that blocker takes.
                float taken = dot(pickLight[i], vec3(0.2126, 0.7152, 0.0722));
                spread += taken * LAMP_SIZE * t / max(length(target - from) - t, 1.0);
                takenAll += taken;
            }
            // One radius serves all the lamps of a pixel: a faint lamp's far blocker must not set it for the strong lamp's fine shadows (a character's shadow from a faint lamp, too weak
            // to see, blurred the ripples' own shadows along it: a pale mottled trail moving with the character).
            float litAll = dot(lit, vec3(0.2126, 0.7152, 0.0722));
            spread = takenAll > 0.15 * litAll && takenAll > 0.0 ? spread / takenAll : 0.0;
            total0 = pickLight[0] * 3.14159265358979 * LAMP_DIFFUSE;
            if (picked > 1) total1 = pickLight[1] * 3.14159265358979 * LAMP_DIFFUSE;
            // The pair of lamps the slots hold, as a small whole number (a half float keeps it exact): the filter reads only the taps holding the same pair.
            return vec4(blocked, float(((pickLamp[0] + 1) * 37 + (picked > 1 ? (pickLamp[1] + 1) * 101 : 0)) % 1021), spread);
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
        layout(set = 0, binding = 9, rgba16f) uniform writeonly image2D uTotal;
        layout(set = 0, binding = 10, rgba16f) uniform writeonly image2D uTotal1;
        layout(set = 0, binding = 6) uniform sampler2D uLightCells;
        layout(set = 0, binding = 7) uniform sampler2D uLightIndex;
        layout(set = 0, binding = 8) uniform sampler2D uLightData;
        const int LAMP_SLOTS = {{Math.Clamp(lamps, MinLamps, MaxLamps)}};
        {{Constants}}{{Picks}}{{LampShaders.Defines}}{{LampShaders.Lighting}}{{GiResolveShaders.ReconstructFunctions}}
        {{GiShaders.OctDirection}}
        {{GiShaders.HitBuffers}}
        {{TracedSunShaders.CasterCutout}}
        {{Rays}}
        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(uFullSize.x) || px.y >= int(uFullSize.y)) return;
            vec3 total0, total1;
            imageStore(uLost, px, lostLight(px, total0, total1));
            imageStore(uTotal, px, vec4(total0, 0.0));
            imageStore(uTotal1, px, vec4(total1, 0.0));
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
        uniform sampler2D uTotal;
        uniform sampler2D uTotal1;

        void main()
        {
            ivec2 px = ivec2(floor(gl_FragCoord.xy));
            vec2 blocked = texelFetch(uLost, px, 0).rg;
            vec3 lost = blocked.r * texelFetch(uTotal, px, 0).rgb + blocked.g * texelFetch(uTotal1, px, 0).rgb;
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
