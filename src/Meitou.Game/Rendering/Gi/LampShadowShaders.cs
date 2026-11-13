using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The lamps' traced shadows (docs/render-lights.md "Traced lamp shadows"): <see cref="Trace"/> finds per pixel which share of the traced lamps'
/// diffuse light occluders take, and how much light that is of, <see cref="Apply"/> takes it off the finished scene and <see cref="Floor"/> keeps
/// the scene above zero.
/// </summary>
static class LampShadowShaders
{
    /// <summary>
    /// Fewest lamps a pixel traces.
    /// </summary>
    public const int MinLamps = 2;

    /// <summary>
    /// Most lamps a pixel traces.
    /// </summary>
    public const int MaxLamps = 2;

    /// <summary>
    /// World units: what a ray leaves out before the lamp, for its fixture (KenshiNative's near-skip), (the penumbra's radius is the lamp's own,
    /// LampTiles.SourceRadius).
    /// </summary>
    const string Constants = """
        const float LAMP_NEAR_SKIP = 15.0;
        const float LAMP_MIN_BIAS = 0.5;

        """;

    /// <summary>
    /// A character's skin takes a lamp as the colour pass gave it (<c>CharacterShaders.lampSkinDiffuse</c>, <c>LAMP_SKIN</c>): N.L and the band the
    /// scattering table adds past the terminator, from the share and the table row the fragment wrote to the surface target (its bits 28-31 and 20-27)
    /// and the albedo's hue. The shading normal stands for the smoothed ones of the red and green lookups. So a blocked lamp's whole term is taken back:
    /// with N.L alone the band stayed, and skin in a lamp's shadow glowed red.
    /// </summary>
    static readonly string SkinTerm = $$"""
        #define LAMP_SKIN
        float lampSkinShare = 0.0, lampSkinRow = 0.0;
        vec3 lampSkinHue = vec3(1.0);
        vec3 lampSkinDiffuse(vec3 n, vec3 l)
        {
            float lambert = clamp(dot(n, l), 0.0, 1.0);
            if (lampSkinShare <= 0.0 || uSkinLut < 0.5) return vec3(lambert);
            float u = (dot(n, l) * 0.5 + 0.5) * {{GlslFloat((Characters.SkinScattering.Width - 1f) / Characters.SkinScattering.Width)}} + {{GlslFloat(0.5f / Characters.SkinScattering.Width)}};
            float v = lampSkinRow * {{GlslFloat((Characters.SkinScattering.Height - 1f) / Characters.SkinScattering.Height)}} + {{GlslFloat(0.5f / Characters.SkinScattering.Height)}};
            vec3 s = textureLod(textures2D[nonuniformEXT(uint(uSkinLut + 0.5))], vec2(u, v), 0.0).rgb;
            float extra = max(max(s.r, max(s.g, s.b)) - lambert, 0.0);
            return vec3(lambert) + extra * lampSkinHue * lampSkinHue * lampSkinShare;
        }
        // The skin a receiver's fragment wrote (0 share: not skin, or no character).
        void lampSkinOf(uvec4 surface)
        {
            lampSkinShare = (surface.w & 524288u) != 0u ? float(surface.w >> 28) / 15.0 : 0.0;
            lampSkinRow = float((surface.w >> 20) & 255u) / 255.0;
            vec3 c = vec3(float(surface.z & 255u), float(surface.z >> 8), float(surface.w & 255u)) / 255.0;
            c *= c;
            lampSkinHue = c / max(max(c.r, max(c.g, c.b)), 1e-3);
        }

        """;

    /// <summary>
    /// A float as a GLSL literal: invariant culture, at least one decimal, at most six.
    /// </summary>
    static string GlslFloat(float value) => value.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>lampLight</c> with <c>LAMP_VISIBILITY</c> turned into a note of each lamp's diffuse term (radiance and <c>diffuseNl</c> are its locals
    /// where the macro expands), so the terms are the world's by construction: attenuation, spot cone, room clip. The strongest <c>LAMP_SLOTS</c>
    /// stay, in order.
    /// </summary>
    const string Picks = """
        int pickLamp[LAMP_SLOTS];
        uint pickEmitter[LAMP_SLOTS];
        float pickWeight[LAMP_SLOTS];
        vec3 pickLight[LAMP_SLOTS];
        float pickSeen[LAMP_SLOTS];   // how much of the lamp the still world lets through (its tile; 1 without tiles)
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
            // What the still world took is already off the drawn light (the tiles, in lampLight): only what is left can be taken again, and a lamp blocked here needs no slot.
            float seen = lampTileVisibility(lamp, l * d, d, rankPlane);
            weight *= seen;
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
                pickSeen[at] = pickSeen[at - 1];
            }
            pickLamp[at] = lamp;
            pickEmitter[at] = uint(s2.w);
            pickWeight[at] = weight;
            pickLight[at] = vec3(0.0);
            pickSeen[at] = seen;
        }
        // The light of a ranked lamp at this surface.
        float lampNote(int lamp, vec3 light)
        {
            for (int i = 0; i < picked; i++)
                if (pickLamp[i] == lamp) pickLight[i] = light * pickSeen[i];
            return 1.0;
        }
        #define LAMP_RANK(lamp, l, d, radius) lampRank(lamp, l, d, radius)
        #define LAMP_VISIBILITY(world, n, toLamp, indoor, emitter) lampNote(lamp, radiance * diffuseNl)

        """;

    /// <summary>
    /// One ray per picked lamp, to its centre: the same every frame (no noise for the upscaler to average, nothing to trail). A lamp is a point of
    /// about a decimetre, so its shadows are near hard.
    /// </summary>
    static readonly string BlockerRays = $$"""
        // The surface target's bits of a receiver: a fragment the resolve lights (bit 8), or a character (bit 19, AtmosphereShaders' kenshiLight).
        #define LAMP_RECEIVER 524544u
        // How far along the ray its blocker is (0: nothing between the surface and the lamp). Ends short of the lamp (its fixture) and at its reach; a lamp's owner is passed through.
        float lampSkip = LAMP_NEAR_SKIP;   // what a ray leaves out before the lamp
        // The receiver is a character (bit 19): its rays leave as the traced sun's do on a body.
        bool lampBody = false;
        // The receiver's point (relative to the scene's origin), the plane it drew and a pixel's width there, for a character's rays.
        vec3 lampAt = vec3(0.0), lampSide = vec3(0.0, 1.0, 0.0);
        float lampPixel = 0.0;
        float lampBlocker(vec3 from, vec3 target, float reach, uint emitter)
        {
            float tMin = 0.0;
            if (lampBody)
            {
                // A character leaves as it does for the traced sun (TracedSunShaders' visibility): close to its skin, past the creases between its facets; and from under a
                // facet turned from the lamp while its smooth shading still takes the light, the ray crosses the body and is asked for blockers from where it leaves it
                // (a dark triangle on a low-poly shoulder otherwise).
                vec3 to = normalize(target - lampAt);
                float facing = dot(lampSide, to);
                if (facing < 0.0)
                {
                    from = lampAt - lampSide * (0.002 + lampPixel * 0.25);
                    rayQueryEXT inside;
                    rayQueryInitializeEXT(inside, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsCullFrontFacingTrianglesEXT, uint(uRayMask.x), from, 0.0, to, {{TracedSunShaders.BodyChordText}});
                    while (rayQueryProceedEXT(inside)) { }
                    if (rayQueryGetIntersectionTypeEXT(inside, true) != gl_RayQueryCommittedIntersectionNoneEXT) tMin = rayQueryGetIntersectionTEXT(inside, true) + 0.002;
                }
                else
                {
                    float clearance = (0.01 + lampPixel * 0.5) / max(facing, 0.2);
                    from = lampAt + lampSide * clearance;
                    tMin = max(clearance, {{TracedSunShaders.BodyContactText}});
                }
            }
            vec3 ray = target - from;
            float d = length(ray), tMax = min(d - lampSkip, reach);
            if (tMax <= tMin) return 0.0;
            uint flags = gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsCullBackFacingTrianglesEXT;
            if (emitter != 0u) flags |= gl_RayFlagsNoOpaqueEXT;
            rayQueryEXT shadow;
            // With the tiles what moves alone (TracedSunShaders.LampMovers), the still world is in the tiles; without them everything the scene holds (TracedSunShaders.LampMask).
            rayQueryInitializeEXT(shadow, uScene, flags, uint(uRayMask.x), from, tMin, ray / d, tMax);
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
        """;

    /// <summary>
    /// <see cref="BlockerRays"/> and <c>lostLight</c>: per pixel, whether occluders block each picked lamp, its light per unit of albedo and the
    /// penumbra's radius their blockers leave.
    /// </summary>
    static readonly string Rays = BlockerRays + $$"""
        // Per traced lamp, in the lamps' index order so a slot means one lamp across the neighbouring pixels: whether occluders block it (r, g; the filter softens these) and its light per
        // unit of albedo (totals), and the penumbra their blockers leave (a: world units, 0 none; PenumbraFilter). Each lamp is filtered apart: a mean of the light, or of one share of two
        // lamps together, follows the slopes of the ground and left them lit or mottled inside a shadow (a thin orange line along a wall's foot, a pale trail moving with a character).
        vec4 lostLight(ivec2 px, out vec3 total0, out vec3 total1)
        {
            total0 = total1 = vec3(0.0);
            uvec4 surface = texelFetch(uAlbedo, px, 0);
            lampBody = (surface.w & 524288u) != 0u;
            lampSkinOf(surface);
            if ((surface.w & LAMP_RECEIVER) == 0u) return vec4(0.0, 0.0, 0.0, -1.0);   // no receiver: the penumbra filter leaves it out
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
            lampAt = world - uOrigin; lampSide = rankPlane; lampPixel = z * uFootprint;
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
                // The lamp's source seen past a blocker at t of the lamp's d: the penumbra's half width at the receiver, weighted by the light that blocker takes.
                float taken = dot(pickLight[i], vec3(0.2126, 0.7152, 0.0722));
                spread += taken * texelFetch(uLightData, ivec2(5, pickLamp[i]), 0).x * t / max(length(target - from) - t, 1.0);
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
    /// Which cells of 64 pixels a lamp's passes work on (<see cref="LampShadowPass"/>): a thread per cell of the area. A cell counts when the box of
    /// a mover's shadow covers it (the CPU's cells) and, with this frame's depth pyramid, when what it shows between its nearest and farthest depth can
    /// lie within the lamp's reach and inside a mover's shadow (the cone from the lamp round the mover's sphere, from the mover on): a sphere round that
    /// part of the cell's frustum against the lamp's sphere and the movers' cones, so never fewer cells than hold a shadowed pixel. The cells that count
    /// and their neighbours (the filter reads round a pixel) are appended to the lamp's list; the count is the indirect dispatch's second number (64
    /// blocks of 8 x 8 pixels a cell along the first).
    /// </summary>
    public const string Classify = """
        #version 460
        layout(local_size_x = 8, local_size_y = 8) in;
        layout(set = 0, binding = 0, std140) uniform Params
        {
            vec4 uLamp;         // xyz: the lamp from the pyramid's eye, w: its reach
            vec4 uRight, uUp, uBack;   // the pyramid's view axes
            vec4 uTanSize;      // tan x, tan y of the half view, the depth's width and height
            ivec4 uCellRect;    // the area's cells: first x, y, one past the last x, y
            ivec4 uInfo;        // x: the list's slot, y: the movers (-1: the cells alone decide), z: where the pyramid's level of 64-pixel blocks starts, w: that level's width (0: no pyramid)
            ivec4 uBase;        // x: where the lists start in the buffer (after the dispatches' arguments)
            uvec4 uCells[16];   // the cells a mover's shadow box covers
            uvec4 uWide[16];    // those and their neighbours
            vec4 uMovers[64];   // xyz from the pyramid's eye, w: the radius
        };
        layout(set = 0, binding = 1, std430) readonly buffer Hiz { vec2 hiz[]; };
        layout(set = 0, binding = 2, std430) buffer CellLists { uint lists[]; };
        #define CELL_WIDTH ((int(uTanSize.z) + 63) / 64)
        #define CELL_HEIGHT ((int(uTanSize.w) + 63) / 64)
        bool cellOn(ivec2 c) { int i = c.y * CELL_WIDTH + c.x; return ((uCells[i / 128][(i / 32) % 4] >> uint(i % 32)) & 1u) != 0u; }
        bool wideOn(ivec2 c) { int i = c.y * CELL_WIDTH + c.x; return ((uWide[i / 128][(i / 32) % 4] >> uint(i % 32)) & 1u) != 0u; }

        // Whether what the cell shows can be in a mover's shadow from the lamp.
        bool reached(ivec2 c)
        {
            if (uInfo.w <= 0 || uInfo.y < 0) return true;
            vec2 depths = hiz[uint(uInfo.z) + uint(c.y * uInfo.w + c.x)];   // the nearest and the farthest distance along the view axis (the sky: infinitely far)
            float lampZ = -dot(uBack.xyz, uLamp.xyz);
            float z0 = max(depths.x, lampZ - uLamp.w), z1 = min(depths.y, lampZ + uLamp.w);
            if (!(z0 <= z1)) return false;
            // A sphere round the cell's frustum between those depths.
            vec2 size = uTanSize.zw;
            vec2 a = (vec2(c) * 64.0 / size * 2.0 - 1.0) * uTanSize.xy, b = (min(vec2(c + 1) * 64.0, size) / size * 2.0 - 1.0) * uTanSize.xy;
            vec2 m = 0.5 * (a + b);
            vec3 axis = uRight.xyz * m.x + uUp.xyz * m.y - uBack.xyz;
            vec3 centre = axis * (0.5 * (z0 + z1));
            float rho = length(axis) * 0.5 * (z1 - z0) + z1 * 0.5 * length(b - a) + 1.0;
            vec3 toCentre = centre - uLamp.xyz;
            float dc = length(toCentre);
            if (dc > uLamp.w + rho) return false;
            for (int k = 0; k < uInfo.y; k++)
            {
                vec3 toMover = uMovers[k].xyz - uLamp.xyz;
                float dm = length(toMover), r = uMovers[k].w;
                if (dc + rho < dm - r) continue;            // wholly nearer the lamp than the mover
                if (dc <= rho || dm <= r) return true;      // the lamp inside either sphere: every direction
                float between = acos(clamp(dot(toCentre, toMover) / (dc * dm), -1.0, 1.0));
                if (between <= asin(min(r / dm, 1.0)) + asin(min(rho / dc, 1.0))) return true;
            }
            return false;
        }

        void main()
        {
            uint slot = uint(uInfo.x);
            // The dispatch's first and third numbers (the buffer's arguments were zeroed before the frame's first list).
            if (gl_GlobalInvocationID.xy == uvec2(0u)) { lists[slot * 4u] = 64u; lists[slot * 4u + 2u] = 1u; }
            ivec2 c = ivec2(gl_GlobalInvocationID.xy) + uCellRect.xy;
            if (c.x >= uCellRect.z || c.y >= uCellRect.w || c.x >= CELL_WIDTH || c.y >= CELL_HEIGHT || !wideOn(c)) return;
            bool keep = false;
            for (int dy = -1; dy <= 1 && !keep; dy++)
                for (int dx = -1; dx <= 1 && !keep; dx++)
                {
                    ivec2 n = c + ivec2(dx, dy);
                    if (n.x < 0 || n.y < 0 || n.x >= CELL_WIDTH || n.y >= CELL_HEIGHT) continue;
                    keep = cellOn(n) && reached(n);
                }
            if (!keep) return;
            uint place = atomicAdd(lists[slot * 4u + 1u], 1u);
            lists[uint(uBase.x) + slot * 2048u + place] = uint(c.x) | (uint(c.y) << 16);
        }
        """;

    /// <summary>
    /// One lamp's rays over an area of the screen (<c>uRect</c>: first x, y, one past the last x, y): whether what moves blocks it (<c>uLost</c> r;
    /// a the penumbra's radius at the receiver in world units, -1 no receiver) and the light it gives per unit of albedo after the still world
    /// (<c>uTotal</c>). One lamp a pass: nothing limits how many a pixel takes, and the filter reads one lamp's flags.
    /// </summary>
    public static readonly string TraceOne = One(false);

    /// <summary>
    /// The light a lamp gives the pixels of an area that its filtered flag (<c>uFlag</c>) marks, per unit of albedo after the still world (the lamp's
    /// tile), times the flag, added to the sum of the lamps (<c>uSum</c>). Only those pixels pay for the lamp's visibility: the rays' kernel asks no
    /// more than whether the lamp reaches the pixel at all.
    /// </summary>
    public static readonly string TraceLight = One(true);

    /// <summary>
    /// The one-lamp kernel over a cell list: <see cref="TraceLight"/> when <paramref name="light"/>, else <see cref="TraceOne"/>.
    /// </summary>
    static string One(bool light) => $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8) in;
        {{Meitou.Game.Rendering.Gpu.BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform sampler2D uNearDepth;
        layout(set = 0, binding = 1) uniform usampler2D uAlbedo;
        layout(set = 0, binding = 2) uniform accelerationStructureEXT uScene;
        {{GiShaders.GeometryStruct}}
        layout(set = 0, binding = 3, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 4, std140) uniform Params
        {
            vec4 uLightGrid;
            vec3 uRight;    float uFootprint;   // a pixel's width at depth 1
            vec3 uUp;       float uPhase;
            vec3 uBack;     float pad0;
            vec3 uGiEye;    float pad1;
            vec3 uOrigin;   float uSkinLut;     // of the traced scene; the skin scattering table's bindless index (0: none)
            vec2 uNearPlanes;
            vec2 uTan;
            vec2 uFullSize;
            vec2 uRayMask;      // x: the instances the rays see, y: the lamp
            vec4 uRect;
            uvec4 uCells[16];
        };
        #define CELLS uCells
        #define CELL_WIDTH ((int(uFullSize.x) + 63) / 64)

        // The cells of 64 pixels this lamp's pass works on (LampShadowPass.CellSize): the mover-free rest of its area costs a test.
        bool cellOn(ivec2 q)
        {
            ivec2 c = q / 64;
            int i = c.y * CELL_WIDTH + c.x;
            return ((CELLS[i / 128][(i / 32) % 4] >> uint(i % 32)) & 1u) != 0u;
        }
        {{(light ? "layout(set = 0, binding = 5, rgba16f) uniform readonly image2D uFlag;\n        layout(set = 0, binding = 9, rgba16f) uniform image2D uSum;"
            : "layout(set = 0, binding = 5, rgba16f) uniform writeonly image2D uLost;")}}
        layout(set = 0, binding = 11) uniform sampler2D uLampTiles;
        layout(set = 0, binding = 12, std430) readonly buffer CellLists { uint lists[]; };
        layout(set = 0, binding = 6) uniform sampler2D uLightCells;
        layout(set = 0, binding = 7) uniform sampler2D uLightIndex;
        layout(set = 0, binding = 8) uniform sampler2D uLightData;
        {{Constants}}{{LampShaders.Defines}}{{LampShaders.TileMapping}}{{LampShaders.TileVisibility}}
        vec3 rankPlane;
        vec3 oneLight = vec3(0.0);
        uint oneEmitter = 0u;
        float lampOneNote(int lamp, vec3 light, vec3 toLamp, float d, uint emitter)
        {
            oneEmitter = emitter;
            oneLight = light{{(light ? " * lampTileVisibility(lamp, toLamp, d, rankPlane)" : "")}};   // the rays: whether the lamp reaches the pixel, whatever stands still in between
            return 1.0;
        }
        #define LAMP_ONLY int(uRayMask.y)
        #define LAMP_VISIBILITY(world, n, toLamp, indoor, emitter) lampOneNote(lamp, radiance * diffuseNl, toLamp, d, emitter)
        {{SkinTerm}}{{LampShaders.Lighting}}{{GiResolveShaders.ReconstructFunctions}}
        {{GiShaders.OctDirection}}
        {{GiShaders.HitBuffers}}
        {{TracedSunShaders.CasterCutout}}
        {{BlockerRays}}
        void main()
        {
            // A workgroup per 8 x 8 pixels of a cell of the lamp's list (Classify; uRect: x the list's slot, y the lists' base): y the cell's place in the list, x the block in the cell.
            uint cell = lists[uint(uRect.y) + uint(uRect.x) * 2048u + gl_WorkGroupID.y];
            ivec2 px = ivec2(cell & 0xFFFFu, cell >> 16) * 64 + ivec2(int(gl_WorkGroupID.x) & 7, int(gl_WorkGroupID.x) >> 3) * 8 + ivec2(gl_LocalInvocationID.xy);
            if (px.x >= int(uFullSize.x) || px.y >= int(uFullSize.y)) return;
            {{(light ? "float flag = imageLoad(uFlag, px).r;\n            if (flag <= 0.0) return;" : "")}}
            vec4 lost = vec4(0.0, 0.0, 0.0, -1.0);
            vec3 total = vec3(0.0);
            uvec4 surface = texelFetch(uAlbedo, px, 0);
            lampBody = (surface.w & 524288u) != 0u;
            lampSkinOf(surface);
            float z = (surface.w & LAMP_RECEIVER) == 0u ? 0.0 : depthAt(px);
            if (z > 0.0)
            {
                vec3 world = positionAt(px, z);
                vec3 n = giOctDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0);
                vec3 diffuse = vec3(0.0), specular = vec3(0.0);
                rankPlane = lampPlane(px, world, n);
                lampLight(world, n, normalize(uGiEye - world), 1.0, 1.0, diffuse, specular);
                {{(light ? "total = oneLight * 3.14159265358979 * LAMP_DIFFUSE;" : """
                if (dot(oneLight, vec3(1.0)) > 0.0)
                {
                    int lamp = int(uRayMask.y);
                    vec3 from = world - uOrigin + rankPlane * max(LAMP_MIN_BIAS, 2.0 * z * uFootprint);
                    lampAt = world - uOrigin; lampSide = rankPlane; lampPixel = z * uFootprint;
                    vec4 p = texelFetch(uLightData, ivec2(0, lamp), 0);
                    vec3 target = p.xyz - uOrigin;
                    float t = lampBlocker(from, target, p.w, oneEmitter);
                    lost = t <= 0.0 ? vec4(0.0) : vec4(1.0, 0.0, 0.0, texelFetch(uLightData, ivec2(5, lamp), 0).x * t / max(length(target - from) - t, 1.0));
                }
                """)}}
            }
            {{(light ? "if (dot(total, vec3(1.0)) > 0.0) imageStore(uSum, px, imageLoad(uSum, px) + vec4(flag * total, 0.0));" : "imageStore(uLost, px, lost);")}}
        }
        """;

    /// <summary>
    /// The lost light of every pixel of the render size into <c>uLost</c> (rgb per unit of albedo; 0 where no lamp is blocked or the surface target
    /// has nothing).
    /// <paramref name="lamps"/>: the most lamps a pixel traces (<see cref="MinLamps"/> to <see cref="MaxLamps"/>).
    /// </summary>
    public static string Trace(int lamps) => $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8) in;
        {{Meitou.Game.Rendering.Gpu.BindlessTable.Declarations(1)}}

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
            vec3 uOrigin;   float uSkinLut;     // of the traced scene; the skin scattering table's bindless index (0: none)
            vec2 uNearPlanes;
            vec2 uTan;
            vec2 uFullSize;
            vec2 uRayMask;      // x: the instances the rays see
        };
        layout(set = 0, binding = 11) uniform sampler2D uLampTiles;
        layout(set = 0, binding = 5, rgba16f) uniform writeonly image2D uLost;
        layout(set = 0, binding = 9, rgba16f) uniform writeonly image2D uTotal;
        layout(set = 0, binding = 10, rgba16f) uniform writeonly image2D uTotal1;
        layout(set = 0, binding = 6) uniform sampler2D uLightCells;
        layout(set = 0, binding = 7) uniform sampler2D uLightIndex;
        layout(set = 0, binding = 8) uniform sampler2D uLightData;
        const int LAMP_SLOTS = {{Math.Clamp(lamps, MinLamps, MaxLamps)}};
        {{Constants}}{{LampShaders.Defines}}{{LampShaders.TileMapping}}{{LampShaders.TileVisibility}}{{Picks}}{{SkinTerm}}{{LampShaders.Lighting}}{{GiResolveShaders.ReconstructFunctions}}
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
    /// Debug (console <c>lamps refview</c>): one lamp's visibility of the still world over the screen, grey, as the tile gives it (mode 0,
    /// <c>uOrigin</c>'s w) or as the truth of 256 rays to points of the lamp's disc (1), for comparing the two.
    /// </summary>
    public static readonly string TraceReference = $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8) in;
        {{Meitou.Game.Rendering.Gpu.BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform sampler2D uNearDepth;
        layout(set = 0, binding = 1) uniform usampler2D uAlbedo;
        layout(set = 0, binding = 2) uniform accelerationStructureEXT uScene;
        {{GiShaders.GeometryStruct}}
        layout(set = 0, binding = 3, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 4, std140) uniform Params
        {
            vec4 uLightGrid;
            vec3 uRight;    float uFootprint;
            vec3 uUp;       float uPhase;
            vec3 uBack;     float pad0;
            vec3 uGiEye;    float pad1;
            vec3 uOrigin;   float uMode;
            vec2 uNearPlanes;
            vec2 uTan;
            vec2 uFullSize;
            vec2 uRayMask;      // x: the instances the rays see, y: the lamp
        };
        layout(set = 0, binding = 5, rgba16f) uniform writeonly image2D uLost;
        layout(set = 0, binding = 11) uniform sampler2D uLampTiles;
        layout(set = 0, binding = 8) uniform sampler2D uLightData;
        {{Constants}}{{LampShaders.Defines}}{{LampShaders.TileMapping}}{{LampShaders.TileVisibility}}
        {{GiResolveShaders.ReconstructFunctions}}
        {{GiShaders.OctDirection}}
        {{GiShaders.HitBuffers}}
        {{TracedSunShaders.CasterCutout}}
        {{BlockerRays}}
        void main()
        {
            ivec2 px = ivec2(gl_GlobalInvocationID.xy);
            if (px.x >= int(uFullSize.x) || px.y >= int(uFullSize.y)) return;
            vec3 shown = vec3(0.0, 0.0, 0.15);
            uvec4 surface = texelFetch(uAlbedo, px, 0);
            lampBody = (surface.w & 524288u) != 0u;
            lampSkinOf(surface);
            float z = (surface.w & LAMP_RECEIVER) == 0u ? 0.0 : depthAt(px);
            int lamp = int(uRayMask.y);
            if (z > 0.0)
            {
                vec3 world = positionAt(px, z);
                vec3 n = giOctDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0);
                vec3 plane = lampPlane(px, world, n);
                vec4 p = texelFetch(uLightData, ivec2(0, lamp), 0);
                vec3 toLamp = p.xyz - world;
                float d = length(toLamp);
                vec4 cone = texelFetch(uLightData, ivec2(2, lamp), 0);
                bool inCone = texelFetch(uLightData, ivec2(1, lamp), 0).w < 0.5 || dot(cone.xyz, -toLamp / max(d, 1e-4)) > cone.w;
                if (d < p.w && inCone && dot(plane, toLamp) > 0.0)
                {
                    float seen;
                    if (uMode < 0.5) seen = lampTileVisibility(lamp, toLamp, d, plane);
                    else
                    {
                        vec3 l = toLamp / d, x, y;
                        lampBasis(l, x, y);
                        float radius = texelFetch(uLightData, ivec2(5, lamp), 0).x;
                        lampSkip = uMode > 1.5 ? 60.0 : max(LAMP_NEAR_SKIP, 8.0 * radius);   // as the tiles (LampTiles.FixtureSources); mode 2: well past the lamp's housing
                        vec3 from = world - uOrigin + plane * max(LAMP_MIN_BIAS, 2.0 * z * uFootprint), centre = p.xyz - uOrigin;
                        lampAt = world - uOrigin; lampSide = plane; lampPixel = z * uFootprint;
                        int lit = 0;
                        for (int k = 0; k < 256; k++)
                        {
                            float a = float(k) * 2.39996323, r = sqrt((float(k) + 0.5) / 256.0) * radius;
                            if (lampBlocker(from, centre + (x * cos(a) + y * sin(a)) * r, p.w, uint(texelFetch(uLightData, ivec2(3, lamp), 0).w)) <= 0.0) lit++;
                        }
                        seen = float(lit) / 256.0;
                    }
                    shown = vec3(seen);
                }
            }
            imageStore(uLost, px, vec4(shown, 1.0));
        }
        """;

    /// <summary>
    /// Debug: shows a target over the scene.
    /// </summary>
    public const string ShowTarget = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uShown;
        void main() { fragColour = vec4(texelFetch(uShown, ivec2(floor(gl_FragCoord.xy)), 0).rgb, 1.0); }
        """;

    /// <summary>
    /// The lamp tiles' fill (LampTiles): one workgroup per 8 texels square of the tiles of a frame, one closest-hit ray per texel from the lamp, the
    /// distance over the lamp's reach (1: nothing). Two rays, one for what stands on the ground and one for the ground (<c>groundExit</c>).
    /// </summary>
    public static readonly string TileFill = $$"""
        #version 460
        #extension GL_EXT_ray_query : require
        #extension GL_EXT_buffer_reference : require
        #extension GL_EXT_buffer_reference_uvec2 : require
        #extension GL_EXT_scalar_block_layout : require
        layout(local_size_x = 8, local_size_y = 8) in;
        {{Meitou.Game.Rendering.Gpu.BindlessTable.Declarations(1)}}

        layout(set = 0, binding = 0) uniform accelerationStructureEXT uScene;
        {{GiShaders.GeometryStruct}}
        layout(set = 0, binding = 1, std430) readonly buffer Geometries { Geometry geometries[]; };
        layout(set = 0, binding = 2, std140) uniform Jobs
        {
            vec4 uHeader;       // x: the jobs
            vec4 uJob[80 * 4];  // position (relative to the scene's origin) and reach; axis and cos of the outer half angle; tile x, y, side, sphere; emitter, first workgroup, workgroups per row, what a ray leaves out at its start
        };
        layout(set = 0, binding = 3, r16) uniform writeonly image2D uAtlas;
        {{LampShaders.TileMapping}}
        {{GiShaders.HitBuffers}}
        {{TracedSunShaders.CasterCutout}}

        // What stands on the ground: the nearest kept surface.
        float objectHit(vec3 from, vec3 dir, float near, float reach, uint emitter)
        {
            uint flags = gl_RayFlagsCullBackFacingTrianglesEXT;
            if (emitter != 0u) flags |= gl_RayFlagsNoOpaqueEXT;
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, flags, {{TracedSunShaders.LampTileObjects}}, from, near, dir, reach);
            while (rayQueryProceedEXT(q))
            {
                int record = rayQueryGetIntersectionInstanceCustomIndexEXT(q, false) + rayQueryGetIntersectionGeometryIndexEXT(q, false);
                if ((emitter == 0u || geometries[record].emitter != emitter) && casterKept(record,
                    rayQueryGetIntersectionPrimitiveIndexEXT(q, false), rayQueryGetIntersectionBarycentricsEXT(q, false), 2.0))
                    rayQueryConfirmIntersectionEXT(q);
            }
            return rayQueryGetIntersectionTypeEXT(q, true) == gl_RayQueryCommittedIntersectionNoneEXT ? reach : rayQueryGetIntersectionTEXT(q, true);
        }

        // The ground: where a ray from the lamp comes out of it, on a slope facing away from the lamp. The grid it is traced on is coarser than the drawn ground, so where the ray goes in
        // says nothing (a graze of the grid would shade the ground behind it): this is the rule of a ray from the surface to the lamp, passing up through the grid and stopped by a rise.
        float groundExit(vec3 from, vec3 dir, float near, float reach)
        {
            rayQueryEXT q;
            rayQueryInitializeEXT(q, uScene, gl_RayFlagsCullFrontFacingTrianglesEXT | gl_RayFlagsOpaqueEXT, {{TracedSunShaders.LampTileGround}}, from, near, dir, reach);
            while (rayQueryProceedEXT(q)) { }
            return rayQueryGetIntersectionTypeEXT(q, true) == gl_RayQueryCommittedIntersectionNoneEXT ? reach : rayQueryGetIntersectionTEXT(q, true);
        }

        void main()
        {
            int low = 0, high = int(uHeader.x) - 1;
            uint group = gl_WorkGroupID.x;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (float(group) >= uJob[mid * 4 + 3].y) low = mid; else high = mid - 1;
            }
            vec4 p = uJob[low * 4], axis = uJob[low * 4 + 1], tile = uJob[low * 4 + 2], more = uJob[low * 4 + 3];
            uint inGroup = group - uint(more.y), perRow = uint(more.z);
            ivec2 texel = ivec2(int(inGroup % perRow), int(inGroup / perRow)) * 8 + ivec2(gl_LocalInvocationID.xy);
            vec2 uv = (vec2(texel) + 0.5) / tile.z;
            vec3 dir;
            if (tile.w > 0.5) dir = lampOctDir(uv);
            else
            {
                vec3 x, y;
                lampBasis(axis.xyz, x, y);
                vec2 q = (uv * 2.0 - 1.0) * lampTileTan(axis.w);
                dir = normalize(axis.xyz + x * q.x + y * q.y);
            }
            float t = min(objectHit(p.xyz, dir, more.w, p.w, uint(more.x)), groundExit(p.xyz, dir, more.w, p.w));
            imageStore(uAtlas, ivec2(tile.xy) + texel, vec4(clamp(t / p.w, 0.0, 1.0)));
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

    /// <summary>
    /// Drawn with a max blend after <see cref="Apply"/>: the stored albedo's rounding must not take a pixel below black.
    /// </summary>
    public const string Floor = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        void main() { fragColour = vec4(0.0); }
        """;
}
