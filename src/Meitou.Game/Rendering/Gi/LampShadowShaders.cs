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
    /// Most cells of 64 pixels a lamp's passes cover (<c>LampShadowPass</c>' bit masks and lists): a picture of 8192 x 8192. With 2048 a photo
    /// mode picture twice 2560 x 1440 (3600 cells) read its masks past their end and wrote its lists over the next lamp's: shadows in blocks.
    /// </summary>
    public const int MaxCells = 16384;

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
            vec3 c = vec3(float(surface.z & 255u), float((surface.z >> 8) & 255u), float(surface.w & 255u)) / 255.0;
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
        // What a ray to a lamp leaves out before it, as its tile does (LampTiles.SkipOf): the housing of a lamp with an emitter is passed by name, so only
        // the source itself is left out; another lamp's housing and mast by distance.
        float lampSkipOf(int lamp, uint emitter)
        {
            return emitter != 0u ? max({{LampTiles.EmitterSources.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}} * texelFetch(uLightData, ivec2(5, lamp), 0).x, 1.0) : LAMP_NEAR_SKIP;
        }
        // The receiver is a character (bit 19): its rays leave as the traced sun's do on a body.
        bool lampBody = false;
        // The receiver is something worn (bit 20 of a character's pixel that is no skin): a thin shell, not a body.
        bool lampWorn = false;
        // The receiver is hair (bit 21 of what is no character receiver and no metal, CharacterShaders): its ray starts LAMP_HAIR_LIFT towards the lamp,
        // off its own card, and passes every hair card on its way (lampBlocker): its cards shadow each other into black. Lifted past them instead
        // (1.5 units), the ray also started past a hat's brim or a hood's edge a hand's breadth over a beard, which then glowed in a shadowed face.
        const float LAMP_HAIR_LIFT = 0.15;
        bool lampHair = false;
        bool lampHairOf(uvec4 surface) { return (surface.w & 524288u) == 0u && (surface.w & 268435456u) == 0u && (surface.w & 2097152u) != 0u; }
        vec3 lampLift(vec3 from, vec3 target) { return lampHair ? from + normalize(target - from) * LAMP_HAIR_LIFT : from; }
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
                    // What is worn has no body to cross: a cloth turned from the lamp is in its own shadow. Crossed like a shoulder, a hood's two layers let the
                    // lamp through onto the bumps of its inside's weave: the inside of a hood lit from outside.
                    if (lampWorn) return 1e-3;
                    from = lampAt - lampSide * (0.002 + lampPixel * 0.25);
                    rayQueryEXT inside;
                    rayQueryInitializeEXT(inside, uScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsCullFrontFacingTrianglesEXT, uint(uRayMask.x), from, 0.0, to, {{TracedSunShaders.BodyChordText}});
                    while (rayQueryProceedEXT(inside)) { }
                    if (rayQueryGetIntersectionTypeEXT(inside, true) != gl_RayQueryCommittedIntersectionNoneEXT) tMin = rayQueryGetIntersectionTEXT(inside, true) + 0.002;
                    // No way out within a facet's turn: the body itself is in the way (the ribs under a breast, which the ray then crosses whole). The rays below
                    // skip the backs of faces, so they left the body by its far side unseen, and such a facet stayed lit inside the breast's shadow: lit and dark
                    // triangles along the shadow's edge.
                    else return {{TracedSunShaders.BodyChordText}};
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
            if (emitter != 0u || lampHair) flags |= gl_RayFlagsNoOpaqueEXT;
            rayQueryEXT shadow;
            // With the tiles what moves alone (TracedSunShaders.LampMovers), the still world is in the tiles; without them everything the scene holds (TracedSunShaders.LampMask).
            rayQueryInitializeEXT(shadow, uScene, flags, uint(uRayMask.x), from, tMin, ray / d, tMax);
            while (rayQueryProceedEXT(shadow))
            {
                int record = rayQueryGetIntersectionInstanceCustomIndexEXT(shadow, false) + rayQueryGetIntersectionGeometryIndexEXT(shadow, false);
                // Hair's ray passes the hair cards (a character's record whose tint has no alpha, CharacterRenderer.AddRayPart).
                if (lampHair && geometries[record].kind == {{GeometryRecord.Character}}u && (geometries[record].charTint >> 24) == 0u) continue;
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
            lampWorn = lampBody && (surface.w >> 28) == 0u && (surface.w & 1048576u) != 0u;
            lampSkinOf(surface);
            lampHair = lampHairOf(surface);
            if ((surface.w & LAMP_RECEIVER) == 0u && !lampHair) return vec4(0.0, 0.0, 0.0, -1.0);   // no receiver: the penumbra filter leaves it out
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
                lampSkip = lampSkipOf(pickLamp[i], pickEmitter[i]);
                float t = lampBlocker(lampLift(from, target), target, p.w, pickEmitter[i]);
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
    /// Most layers a round works on at once, each in its own slice of the rays' targets (3 bits of a cell's entry).
    /// </summary>
    public const int MaxSlices = 8;

    /// <summary>
    /// Most pixels a round traces on their own at half the size (<see cref="TraceOwn"/>): those past it keep the flags round them.
    /// </summary>
    public const int OwnCap = 1 << 19;

    /// <summary>
    /// Which cells of 64 pixels a lamp's passes work on (<see cref="LampShadowPass"/>): a thread per cell of the area. A cell counts when the box of
    /// a mover's shadow covers it (the CPU's cells) and, with this frame's depth pyramid, when what it shows between its nearest and farthest depth can
    /// lie within the lamp's reach and inside a mover's shadow (the cone from the lamp round the mover's sphere, from the mover on): a sphere round that
    /// part of the cell's frustum against the lamp's sphere and the movers' cones, so never fewer cells than hold a shadowed pixel. The cells that count
    /// and their neighbours (the filter reads round a pixel) go to the lamp's round (<c>LampShadowPass.PlanLayers</c>: the lamps of a layer share no
    /// cell nor neighbouring cells, and a round works on up to <see cref="MaxSlices"/> layers at once, a slice of the rays' targets each):
    /// <list type="bullet">
    /// <item>the round's list of rays: the cell's column and row (8 bits each), the slice (3 bits), the lamp's row (13 bits);</item>
    /// <item>the round's map: per slice and cell, the lamp's row plus one (0: no lamp of that slice works on the cell);</item>
    /// <item>the round's list of cells (column and row), each once whatever the number of its lamps: the light kernel's.</item>
    /// </list>
    /// The counts are the indirect dispatches' second numbers (64 blocks of 8 x 8 pixels a cell along the first, 16 at half the size). The buffer,
    /// in uints, for R rounds of K slices over C cells a screen: the dispatches' arguments (4 R for the rays, 4 R for the half-size rays, 4 R for the
    /// cells, 4 R for the pixels traced on their own, whose fourth number is their count), the cells seen (R C), the maps (R K C), the lists of rays
    /// (R K C), the lists of cells (R C), then the round at work's pixels traced on their own (<see cref="OwnCap"/>).
    /// </summary>
    public static readonly string Classify = $$"""
        #version 460
        layout(local_size_x = 8, local_size_y = 8) in;
        #define MAX_CELLS {{MaxCells}}
        layout(set = 0, binding = 0, std140) uniform Params
        {
            vec4 uLamp;         // xyz: the lamp from the pyramid's eye, w: its reach
            vec4 uRight, uUp, uBack;   // the pyramid's view axes
            vec4 uTanSize;      // tan x, tan y of the half view, the depth's width and height
            ivec4 uCellRect;    // the area's cells: first x, y, one past the last x, y
            ivec4 uInfo;        // x: the lamp's round, y: the movers (-1: the cells alone decide), z: where the pyramid's level of 64-pixel blocks starts, w: that level's width (0: no pyramid)
            ivec4 uBase;        // x: the lamp's slice in its round, y: 1 the lamp stands among traced grass (every cell in its reach), z: the rounds, w: the lamp's row
            ivec4 uLay;         // x: the slices a round
            uvec4 uCells[MAX_CELLS / 128];   // the cells a mover's shadow box covers
            uvec4 uWide[MAX_CELLS / 128];    // those and their neighbours
            vec4 uMovers[256];  // xyz from the pyramid's eye, w: the radius (LampShadowPass.ClassifyMovers)
            vec4 uGrassMap;     // the traced blades' map (TracedGrass.Map): xy its corner (x, z) from the pyramid's eye, z its finest cell, w its side (0: no map, a grass lamp asks every cell)
            vec4 uGrassLift;    // x: the pyramid's eye's height, y: the map's height offset, z: the margin round the receivers, w: the map's levels
            uvec4 uGrassCells[MAX_CELLS / 128];   // a lamp among grass: the cells in its reach (uCells: the movers' shadow boxes)
        };
        layout(set = 0, binding = 1, std430) readonly buffer Hiz { vec2 hiz[]; };
        layout(set = 0, binding = 2, std430) buffer CellLists { uint lists[]; };
        layout(set = 0, binding = 3, std430) readonly buffer GrassMap { uint grassMap[]; };
        #define CELL_WIDTH ((int(uTanSize.z) + 63) / 64)
        #define CELL_HEIGHT ((int(uTanSize.w) + 63) / 64)
        bool cellOn(ivec2 c) { int i = c.y * CELL_WIDTH + c.x; return ((uCells[i / 128][(i / 32) % 4] >> uint(i % 32)) & 1u) != 0u; }
        bool wideOn(ivec2 c) { int i = c.y * CELL_WIDTH + c.x; return ((uWide[i / 128][(i / 32) % 4] >> uint(i % 32)) & 1u) != 0u; }
        bool grassOn(ivec2 c) { int i = c.y * CELL_WIDTH + c.x; return ((uGrassCells[i / 128][(i / 32) % 4] >> uint(i % 32)) & 1u) != 0u; }

        // The traced blades' map (uGrassMap): its level's first cell; a height (from the pyramid's eye) as the map stores heights.
        uint grassLevel(int level)
        {
            uint side = uint(uGrassMap.w), at = 0u;
            for (int k = 0; k < level; k++) at += (side >> uint(k)) * (side >> uint(k));
            return at;
        }
        uint grassHeight(float y) { return floatBitsToUint(max(y + uGrassLift.x + uGrassLift.y, 1e-30)); }

        // The highest blade top (as stored, 0: none) over the box [lo, hi] (x, z from the pyramid's eye), at the finest level whose cells cover it in at most
        // 8 x 8.
        uint grassTopIn(vec2 lo, vec2 hi)
        {
            float cell = uGrassMap.z;
            int levels = int(uGrassLift.w), side0 = int(uGrassMap.w);
            float extent = max(hi.x - lo.x, hi.y - lo.y);
            int level = clamp(int(ceil(log2(max(extent / (7.0 * cell), 1.0)))), 0, levels - 1);
            float size = cell * exp2(float(level));
            int side = side0 >> level;
            ivec2 a = max(ivec2(floor((lo - uGrassMap.xy) / size)), ivec2(0)), b = min(ivec2(floor((hi - uGrassMap.xy) / size)), ivec2(side - 1));
            uint at = grassLevel(level), top = 0u;
            for (int y = a.y; y <= b.y; y++)
                for (int x = a.x; x <= b.x; x++) top = max(top, grassMap[at + uint(y * side + x)]);
            return top;
        }

        // Narrows [k0, k1] to the shares k with base + k slope at most (below) or at least (!below) limit.
        void grassShare(float base, float slope, float limit, bool below, inout float k0, inout float k1)
        {
            if (abs(slope) < 1e-6)
            {
                if (below ? base > limit : base < limit) k0 = 2.0;
                return;
            }
            float k = (limit - base) / slope;
            if (below == (slope > 0.0)) k1 = min(k1, k);
            else k0 = max(k0, k);
        }

        // The hull of grassMayShade: the frustum's box (lowered and widened by the margin), the lamp, the rise from the box's lowest point to the lamp, and
        // the map's levels' first cells.
        vec3 hullLow, hullHigh, hullLamp;
        float hullRise;
        uint hullLevels[12];

        // Whether a map cell (level, t) holds a blade top above the hull over it: the shares of the way to the lamp that bring the frustum's moved box over
        // the cell (none: the hull is not over it), and the lowest height they give.
        bool grassOver(int level, ivec2 t)
        {
            int side = int(abs(uGrassMap.w)) >> level;
            if (any(lessThan(t, ivec2(0))) || any(greaterThanEqual(t, ivec2(side)))) return false;
            uint v = grassMap[hullLevels[level] + uint(t.y * side + t.x)];
            if (v == 0u) return false;
            float size = uGrassMap.z * exp2(float(level));
            vec2 s0 = uGrassMap.xy + vec2(t) * size, s1 = s0 + size;
            float k0 = 0.0, k1 = 1.0;
            for (int i = 0; i < 2; i++)
            {
                grassShare(hullLow.xz[i], hullLamp.xz[i] - hullLow.xz[i], s1[i], true, k0, k1);
                grassShare(hullHigh.xz[i], hullLamp.xz[i] - hullHigh.xz[i], s0[i], false, k0, k1);
            }
            return k0 <= k1 && v >= grassHeight(hullLow.y + min(k0 * hullRise, k1 * hullRise));
        }

        // Whether a traced blade can stand between the lamp and what the cell shows: in the hull of the cell's frustum between the depths z0 and z1 (its
        // corners a, b in the view's tangents) and the lamp, which holds every segment from a receiver to the lamp. The receivers' rays leave a little off
        // them (m: the frustum is taken that much lower and wider: a few units and two pixels' width). A point of the hull is the share k of the way from a
        // point of the frustum to the lamp: across the ground it is in the frustum's box moved that share of the way to the lamp, and its height is at least
        // the frustum's lowest plus that share of the rise to the lamp (grassOver). The map's cells under the hull's box are asked at the level that covers
        // it in at most 8 x 8; one over which the hull can be under a blade top is asked again in its four finer cells, down to the finest: true at the
        // first finest one. Without a map: true.
        bool grassWalk(vec2 a, vec2 b, float z0, float z1)
        {
            float m = uGrassLift.z + 2.0 * z1 * length(uTanSize.xy) * 2.0 / uTanSize.w;
            vec3 f0 = vec3(1e30), f1 = vec3(-1e30);
            for (int k = 0; k < 8; k++)
            {
                vec2 t = vec2((k & 1) == 0 ? a.x : b.x, (k & 2) == 0 ? a.y : b.y);
                vec3 p = (uRight.xyz * t.x + uUp.xyz * t.y - uBack.xyz) * ((k & 4) == 0 ? z0 : z1);
                f0 = min(f0, p);
                f1 = max(f1, p);
            }
            hullLow = f0 - m;
            hullHigh = vec3(f1.x + m, f1.y, f1.z + m);
            hullLamp = uLamp.xyz;
            hullRise = hullLamp.y - m - hullLow.y;
            int levels = int(uGrassLift.w), side0 = int(abs(uGrassMap.w));
            uint first = 0u;
            for (int l = 0; l < levels; l++) { hullLevels[l] = first; first += uint((side0 >> l) * (side0 >> l)); }

            vec2 lo = min(hullLow.xz, hullLamp.xz), hi = max(hullHigh.xz, hullLamp.xz);
            float extent = max(hi.x - lo.x, hi.y - lo.y);
            int start = clamp(int(ceil(log2(max(extent / (7.0 * uGrassMap.z), 1.0)))), 0, levels - 1);
            float size = uGrassMap.z * exp2(float(start));
            ivec2 c0 = ivec2(floor((lo - uGrassMap.xy) / size)), c1 = ivec2(floor((hi - uGrassMap.xy) / size));
            uint stack[32];
            for (int y = c0.y; y <= c1.y; y++)
                for (int x = c0.x; x <= c1.x; x++)
                {
                    if (!grassOver(start, ivec2(x, y))) continue;
                    if (start == 0) return true;
                    int n = 0;
                    stack[n++] = uint(start) | (uint(x) << 4) | (uint(y) << 18);
                    while (n > 0)
                    {
                        uint e = stack[--n];
                        int level = int(e & 15u) - 1;
                        ivec2 t = ivec2(int((e >> 4) & 16383u), int(e >> 18)) * 2;
                        for (int q = 0; q < 4; q++)
                        {
                            ivec2 child = t + ivec2(q & 1, q >> 1);
                            if (!grassOver(level, child)) continue;
                            if (level == 0) return true;
                            if (n < 32) stack[n++] = uint(level) | (uint(child.x) << 4) | (uint(child.y) << 18);
                            else return true;   // deeper than the stack holds: kept
                        }
                    }
                }
            return false;
        }
        bool grassMayShade(vec2 a, vec2 b, float z0, float z1)
        {
            if (uGrassMap.w == 0.0) return true;
            return grassWalk(a, b, z0, z1);
        }

        // Whether what the cell shows can be in a mover's shadow from the lamp (asked where movers is set: uCells) or in a blade's (grass: uGrassCells).
        bool reached(ivec2 c, bool movers, bool grass)
        {
            if (!movers && !grass) return false;
            if (uInfo.w <= 0) return true;
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
            // Among traced grass: where a blade can stand between the lamp and the cell (TracedGrass.Map).
            if (grass && grassMayShade(a, b, z0, z1)) return true;
            if (!movers) return false;
            if (uInfo.y < 0) return true;
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

        shared bool sReached[100];

        void main()
        {
            uint round = uint(uInfo.x), R = uint(uBase.z), K = uint(uLay.x), C = uint(CELL_WIDTH * CELL_HEIGHT);
            uint raysArgs = round * 4u, halfArgs = 4u * R + round * 4u, cellsArgs = 8u * R + round * 4u, ownArgs = 12u * R + round * 4u;
            // The dispatches' first and third numbers (the buffer's arguments were zeroed before the frame's first list): 64 blocks of 8 x 8 a cell, 16 at half the size.
            if (gl_GlobalInvocationID.xy == uvec2(0u))
            {
                lists[raysArgs] = 64u; lists[raysArgs + 2u] = 1u;
                lists[halfArgs] = 16u; lists[halfArgs + 2u] = 1u;
                lists[cellsArgs] = 64u; lists[cellsArgs + 2u] = 1u;
                lists[ownArgs + 1u] = 1u; lists[ownArgs + 2u] = 1u;
            }
            // The group's 8 x 8 cells and the ring round them, each asked once (asked by each of its neighbours, the grass's walk ran nine times a cell).
            ivec2 corner = ivec2(gl_WorkGroupID.xy) * 8 + uCellRect.xy - 1;
            for (uint h = gl_LocalInvocationIndex; h < 100u; h += 64u)
            {
                ivec2 n = corner + ivec2(int(h % 10u), int(h / 10u));
                bool inside = n.x >= 0 && n.y >= 0 && n.x < CELL_WIDTH && n.y < CELL_HEIGHT;
                sReached[h] = inside && reached(n, cellOn(n), uBase.y != 0 && grassOn(n));
            }
            barrier();
            ivec2 c = ivec2(gl_GlobalInvocationID.xy) + uCellRect.xy;
            if (c.x >= uCellRect.z || c.y >= uCellRect.w || c.x >= CELL_WIDTH || c.y >= CELL_HEIGHT || !wideOn(c)) return;
            bool keep = false;
            ivec2 own = ivec2(gl_LocalInvocationID.xy) + 1;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++) keep = keep || sReached[(own.y + dy) * 10 + own.x + dx];
            if (!keep) return;
            uint index = uint(c.y * CELL_WIDTH + c.x), slice = uint(uBase.x), at = uint(c.x) | (uint(c.y) << 8);
            uint seen = 16u * R + round * C, map = 16u * R + R * C + (round * K + slice) * C;
            uint rays = 16u * R + R * C + R * K * C + round * K * C, cells = 16u * R + R * C + 2u * R * K * C + round * C;
            uint place = atomicAdd(lists[raysArgs + 1u], 1u);
            atomicAdd(lists[halfArgs + 1u], 1u);
            lists[rays + place] = at | (slice << 16) | (uint(uBase.w) << 19);
            lists[map + index] = uint(uBase.w) + 1u;
            if (atomicExchange(lists[seen + index], 1u) == 0u) lists[cells + atomicAdd(lists[cellsArgs + 1u], 1u)] = at;
        }
        """;

    /// <summary>
    /// A round's lamps' rays over its list (<c>uRect.x</c>: where it starts), each entry a cell with one lamp and its slice of <c>uLost</c>:
    /// whether what moves blocks the lamp (r) and the penumbra's radius at the receiver in world units (g; 0 none, -1 no receiver). One lamp an
    /// entry: nothing limits how many a pixel takes, and the filter reads one lamp's flags.
    /// </summary>
    public static readonly string TraceOne = RoundRays(false);

    /// <summary>
    /// <see cref="TraceOne"/> over one pixel in four (<c>uRect.zw</c>: which of each 2 x 2 block), into slices of half the size: the rays and the
    /// filter are the lamps' pass's cost, and a lamp's shadow has no detail a pixel wide. <see cref="TraceLightHalf"/> takes it back to the pixels.
    /// The other pixels of a 2 x 2 that are of another surface than the traced one (its kind, its plane) are listed for a ray of their own
    /// (<see cref="TraceOwn"/>, <c>uExact</c>): silhouettes, a beard's strands over skin, things a pixel wide.
    /// </summary>
    public static readonly string TraceOneHalf = RoundRays(true);

    /// <summary>
    /// The light the lamps of a round give each pixel of its cells (<c>uRect.x</c>: where the round's list of cells starts, <c>uRect.y</c> its
    /// map): per lamp working on the pixel's cell (a slice each), where its filtered flag (<c>uFlag</c>) marks the pixel, its light per unit of
    /// albedo after the still world (the lamp's tile) times the flag; the lamps' sum is added to <c>uSum</c>. Only marked pixels pay for a lamp's
    /// visibility: the rays' kernel asks no more than whether the lamp reaches the pixel at all. A pixel is one thread's whatever the number of
    /// its lamps: its depth, plane and surface are read once, and nothing else writes its sum in the pass.
    /// </summary>
    public static readonly string TraceLight = RoundLight(false);

    /// <summary>
    /// <see cref="TraceLight"/> reading the flags of <see cref="TraceOneHalf"/>: the pixel's own ray where that kernel traced one (a pixel of
    /// another surface than the traced one of its 2 x 2), else the mean of the flags round the pixel that lie on its own surface (its kind, its
    /// plane from the depth), a step further where none does.
    /// </summary>
    public static readonly string TraceLightHalf = RoundLight(true);

    /// <summary>
    /// What the rays' and the light's kernels share: the bindings, the constants, one lamp's light (<c>oneLamp</c>) and the blocker rays.
    /// </summary>
    static string Shared(bool light, bool half) => $$"""
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
            vec2 uRayMask;      // x: the instances the rays see
            vec4 uRect;         // x: where the kernel's list starts, y: where the round's map starts, zw: the pixel of each 2 x 2 the half-size rays take
            uvec4 uLay;         // x: the slices a round, y: the cells a screen, z: where the round's own rays' arguments are, w: where their list starts
        };
        #define CELL_WIDTH ((int(uFullSize.x) + 63) / 64)
        {{(light ? "layout(set = 0, binding = 5, rg16f) uniform readonly image2DArray uFlag;\n        layout(set = 0, binding = 9, rgba16f) uniform image2D uSum;\n        layout(set = 0, binding = 10, rgba16f) uniform image2D uPlain;"
            : "layout(set = 0, binding = 5, rg16f) uniform writeonly image2DArray uLost;")}}
        layout(set = 0, binding = 11) uniform sampler2D uLampTiles;
        layout(set = 0, binding = 12, std430) {{(half && !light ? "" : "readonly ")}}buffer CellLists { uint lists[]; };
        {{(half ? $"// Per half-size pixel: its own pixel's kind (bits 6-7, lampKind) and, for the three other pixels of its 2 x 2 in reading order, whether they were traced on their\n        // own (bits 0-2, TraceOwn) and found blocked (bits 3-5).\n        layout(set = 0, binding = 13, r32ui) uniform {(light ? "readonly " : "")}uimage2DArray uExact;\n        #define OWN_CAP {OwnCap}u" : "")}}
        layout(set = 0, binding = 6) uniform sampler2D uLightCells;
        layout(set = 0, binding = 7) uniform sampler2D uLightIndex;
        layout(set = 0, binding = 8) uniform sampler2D uLightData;
        {{Constants}}{{LampShaders.Defines}}{{LampShaders.TileMapping}}{{LampShaders.TileVisibility}}
        // What a pixel is to the lamps: a character (1), hair (2), else the world's. Flags are never read across kinds: a beard's lit card lies in the
        // plane of the shadowed skin seen between its strands.
        uint lampKind(uvec4 surface)
        {
            bool body = (surface.w & 524288u) != 0u;
            return body ? 1u : (surface.w & 268435456u) == 0u && (surface.w & 2097152u) != 0u ? 2u : 0u;
        }
        vec3 rankPlane;
        vec3 oneLight = vec3(0.0);
        float oneReach = 1.0;   // how much of the lamp reaches the pixel past what stands still (the light kernel)
        uint oneEmitter = 0u;
        float lampOneNote(int lamp, vec3 light, vec3 toLamp, float d, uint emitter)
        {
            oneEmitter = emitter;
            {{(light ? "oneReach = lampTileVisibility(lamp, toLamp, d, rankPlane);" : "")}}
            oneLight = light{{(light ? " * oneReach" : "")}};   // the rays: whether the lamp reaches the pixel, whatever stands still in between
            return 1.0;
        }
        int oneLamp = 0;   // the lamp worked on (its row)
        #define LAMP_ONLY oneLamp
        #define LAMP_VISIBILITY(world, n, toLamp, indoor, emitter) lampOneNote(lamp, radiance * diffuseNl, toLamp, d, emitter)
        {{SkinTerm}}{{LampShaders.Lighting}}{{GiResolveShaders.ReconstructFunctions}}
        {{GiShaders.OctDirection}}
        {{GiShaders.HitBuffers}}
        {{TracedSunShaders.CasterCutout}}
        {{(light ? "" : BlockerRays)}}
        """;

    /// <summary>
    /// <c>lostAt</c>: one pixel's ray to the lamp at work.
    /// </summary>
    const string LostAt = """
        // The depth of the last pixel asked of lostAt (0: no receiver) and its point.
        float atZ = 0.0;
        vec3 atWorld = vec3(0.0);
        // Whether what moves blocks the lamp at a pixel (x) and the penumbra's radius there in world units (y; 0 none, -1 no receiver of this lamp).
        vec2 lostAt(ivec2 px, uvec4 surface)
        {
            vec2 lost = vec2(0.0, -1.0);
            lampBody = (surface.w & 524288u) != 0u;
            lampWorn = lampBody && (surface.w >> 28) == 0u && (surface.w & 1048576u) != 0u;
            lampSkinOf(surface);
            lampHair = lampHairOf(surface);
            oneLight = vec3(0.0); oneEmitter = 0u;
            float z = (surface.w & LAMP_RECEIVER) == 0u && !lampHair ? 0.0 : depthAt(px);
            atZ = z;
            if (z > 0.0)
            {
                vec3 world = positionAt(px, z);
                atWorld = world;
                vec3 n = giOctDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0);
                vec3 diffuse = vec3(0.0), specular = vec3(0.0);
                rankPlane = lampPlane(px, world, n);
                lampLight(world, n, normalize(uGiEye - world), 1.0, 1.0, diffuse, specular);
                if (dot(oneLight, vec3(1.0)) > 0.0)
                {
                    int lamp = oneLamp;
                    vec3 from = world - uOrigin + rankPlane * max(LAMP_MIN_BIAS, 2.0 * z * uFootprint);
                    lampAt = world - uOrigin; lampSide = rankPlane; lampPixel = z * uFootprint;
                    vec4 p = texelFetch(uLightData, ivec2(0, lamp), 0);
                    vec3 target = p.xyz - uOrigin;
                    lampSkip = lampSkipOf(lamp, oneEmitter);
                    float t = lampBlocker(lampLift(from, target), target, p.w, oneEmitter);
                    lost = t <= 0.0 ? vec2(0.0) : vec2(1.0, texelFetch(uLightData, ivec2(5, lamp), 0).x * t / max(length(target - from) - t, 1.0));
                }
            }
            return lost;
        }
        """;

    /// <summary>
    /// The rays of the pixels <see cref="TraceOneHalf"/> listed (<c>uLay.zw</c>: the round's count and list), a thread a pixel: whether what moves
    /// blocks the pixel's lamp, into its bits of <c>uExact</c>. In the rays' kernel itself the few pixels of a block with a second ray kept the
    /// block's other threads waiting: 1.5 ms in a crowd.
    /// </summary>
    public static readonly string TraceOwn = Shared(false, true) + LostAt + """
        void main()
        {
            uint t = gl_WorkGroupID.x * 64u + gl_LocalInvocationIndex;
            if (t >= min(lists[uLay.z + 3u], OWN_CAP)) return;
            uint e = lists[uLay.w + t];
            ivec2 q = ivec2(e & 8191u, (e >> 13) & 8191u), cellAt = q >> 6;
            int slice = int(e >> 26);
            uint entry = lists[uint(uRect.y) + uint(slice) * uLay.y + uint(cellAt.y * CELL_WIDTH + cellAt.x)];
            if (entry == 0u) return;
            oneLamp = int(entry) - 1;
            vec2 l = lostAt(q, texelFetch(uAlbedo, q, 0));
            if (l.y < 0.0) return;
            int spot = (q.x & 1) + 2 * (q.y & 1), own = int(uRect.z) + 2 * int(uRect.w), k = spot - (spot > own ? 1 : 0);
            imageAtomicOr(uExact, ivec3(q >> 1, slice), (1u << k) | (l.x > 0.0 ? 8u << k : 0u));
        }
        """;

    /// <summary>
    /// The rays' kernel over a round's list: <see cref="TraceOneHalf"/> when <paramref name="half"/>, else <see cref="TraceOne"/>.
    /// </summary>
    static string RoundRays(bool half) => Shared(false, half) + LostAt + $$"""
        void main()
        {
            // A workgroup per 8 x 8 pixels of an entry of the round's list (Classify): y the entry's place in the list, x the block in its cell.
            uint cell = lists[uint(uRect.x) + gl_WorkGroupID.y];
            oneLamp = int(cell >> 19);
            int slice = int((cell >> 16) & 7u);
            ivec2 cellAt = ivec2(cell & 255u, (cell >> 8) & 255u);
            {{(half ? """
            // Half size: a cell is 4 x 4 blocks of 8 x 8 half pixels, each standing for the pixel uRect.zw of its 2 x 2.
            ivec2 hp = cellAt * 32 + ivec2(int(gl_WorkGroupID.x) & 3, int(gl_WorkGroupID.x) >> 2) * 8 + ivec2(gl_LocalInvocationID.xy);
            ivec2 px = hp * 2 + ivec2(uRect.zw);
            """ : """
            ivec2 px = cellAt * 64 + ivec2(int(gl_WorkGroupID.x) & 7, int(gl_WorkGroupID.x) >> 3) * 8 + ivec2(gl_LocalInvocationID.xy);
            ivec2 hp = px;
            """)}}
            if (hp.x * {{(half ? 2 : 1)}} >= int(uFullSize.x) || hp.y * {{(half ? 2 : 1)}} >= int(uFullSize.y)) return;
            bool inside = px.x < int(uFullSize.x) && px.y < int(uFullSize.y);
            uvec4 surface = inside ? texelFetch(uAlbedo, px, 0) : uvec4(0u);
            vec2 lost = lostAt(px, surface);
            imageStore(uLost, ivec3(hp, slice), vec4(lost, 0.0, 0.0));
            {{(half ? """
            // The three other pixels of the 2 x 2: one that is a receiver of another kind than the traced pixel, or off its plane by more than a pixel and a half
            // (5 mm at least), has no flag round it that is its own surface's (a silhouette, a strand of hair over skin, a thing a pixel wide): it is listed for a
            // ray of its own (TraceOwn).
            uint kind = lampKind(surface), exact = kind << 6;
            bool receiver = atZ > 0.0;
            vec3 at = atWorld, plane = rankPlane;
            float tolerance = max(0.05, 1.5 * atZ * uFootprint);
            for (int spot = 0; spot < 4; spot++)
            {
                ivec2 q = hp * 2 + ivec2(spot & 1, spot >> 1);
                if (q == px || q.x >= int(uFullSize.x) || q.y >= int(uFullSize.y)) continue;
                uvec4 other = texelFetch(uAlbedo, q, 0);
                if ((other.w & LAMP_RECEIVER) == 0u && !lampHairOf(other)) continue;
                bool differs = !receiver || lampKind(other) != kind;
                if (!differs)
                {
                    float zq = depthAt(q);
                    differs = zq > 0.0 && abs(dot(positionAt(q, zq) - at, plane)) > tolerance;
                }
                if (!differs) continue;
                uint place = atomicAdd(lists[uLay.z + 3u], 1u);
                if (place >= OWN_CAP) continue;
                lists[uLay.w + place] = uint(q.x) | (uint(q.y) << 13) | (uint(slice) << 26);
                atomicMax(lists[uLay.z], place / 64u + 1u);
            }
            imageStore(uExact, ivec3(hp, slice), uvec4(exact));
            """ : "")}}
        }
        """;

    /// <summary>
    /// The light's kernel over a round's cells: <see cref="TraceLightHalf"/> when <paramref name="half"/>, else <see cref="TraceLight"/>. It holds
    /// no ray: a ray query in this kernel, even never run, slowed every pixel of it (1.4 ms in a crowd); the pixels that need their own ray have
    /// it from the rays' kernel (<c>uExact</c>).
    /// </summary>
    static string RoundLight(bool half) => Shared(true, half) + $$"""
        // What only the rays' kernel asks of a receiver.
        #define LAMP_RECEIVER 524544u
        bool lampBody = false, lampHair = false;
        bool lampHairOf(uvec4 surface) { return (surface.w & 524288u) == 0u && (surface.w & 268435456u) == 0u && (surface.w & 2097152u) != 0u; }
        // The drawn triangle's plane from the depth, as the rays' kernel's (BlockerRays).
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
        void main()
        {
            // A workgroup per 8 x 8 pixels of a cell of the round's list of cells (Classify): y the cell's place in the list, x the block in the cell.
            uint cell = lists[uint(uRect.x) + gl_WorkGroupID.y];
            ivec2 cellAt = ivec2(cell & 255u, (cell >> 8) & 255u);
            ivec2 px = cellAt * 64 + ivec2(int(gl_WorkGroupID.x) & 7, int(gl_WorkGroupID.x) >> 3) * 8 + ivec2(gl_LocalInvocationID.xy);
            if (px.x >= int(uFullSize.x) || px.y >= int(uFullSize.y)) return;
            uint mapAt = uint(uRect.y) + uint(cellAt.y * CELL_WIDTH + cellAt.x);
            uvec4 surface = texelFetch(uAlbedo, px, 0);
            lampBody = (surface.w & 524288u) != 0u;
            {{(half ? """
            // The half-size flags round the pixel: its own (the pixel the rays took of its 2 x 2), or the 2 or 4 it lies between, all at the same distance.
            ivec2 sub = ivec2(uRect.zw), rel = px - sub, j0 = rel >> 1, odd = rel & 1, halfSize = (ivec2(uFullSize) + 1) / 2;
            """ : "")}}
            bool ready = false;
            uint kind = lampKind(surface);
            float z = 0.0, a = 1.0;
            vec3 world = vec3(0.0), n = vec3(0.0, 1.0, 0.0);
            vec4 lostSum = vec4(0.0), plainSum = vec4(0.0);
            for (int slice = 0; slice < int(uLay.x); slice++)
            {
                uint entry = lists[mapAt + uint(slice) * uLay.y];
                if (entry == 0u) continue;
                oneLamp = int(entry) - 1;
                float flag = 0.0;
                {{(half ? """
                vec2 tap[4];
                int taps = 0, none = 0;
                bool anyHit = false;
                for (int dy = 0; dy <= odd.y; dy++)
                    for (int dx = 0; dx <= odd.x; dx++)
                    {
                        ivec2 j = j0 + ivec2(dx, dy);
                        bool inside = all(greaterThanEqual(j, ivec2(0))) && all(lessThan(j, halfSize));
                        vec2 v = inside ? imageLoad(uFlag, ivec3(j, slice)).rg : vec2(0.0, -1.0);
                        tap[taps++] = v;
                        anyHit = anyHit || v.r > 0.0;
                        if (v.g < 0.0) none++;
                    }
                // Nothing blocked round it, on receivers alone or on none: nothing to take (a character's pixel still notes the lamp's light).
                if (!anyHit && (none == 0 || none == taps) && !lampBody) continue;
                """ : """
                flag = imageLoad(uFlag, ivec3(px, slice)).r;
                // A character's pixel notes the lamp's light though nothing blocks it: the apply wants the share of its lamps' light that is lost.
                if (flag <= 0.0 && !lampBody) continue;
                """)}}
                if (!ready)
                {
                    // The pixel's surface, once for all its lamps.
                    ready = true;
                    lampSkinOf(surface);
                    lampHair = lampHairOf(surface);
                    z = (surface.w & LAMP_RECEIVER) == 0u && !lampHair ? 0.0 : depthAt(px);
                    if (z > 0.0)
                    {
                        world = positionAt(px, z);
                        n = giOctDir(vec2(surface.xy) / 65535.0 * 2.0 - 1.0);
                        rankPlane = lampPlane(px, world, n);
                        // The lamp's specular as the fragment took it: its roughness from the stored gloss (none stored: a middling one).
                        uint glossBits = (surface.w >> 10) & 63u;
                        float roughness = 1.0 - (glossBits != 0u ? float(glossBits - 1u) / 62.0 : 0.5) * 0.99;
                        a = roughness * roughness;
                    }
                }
                if (z <= 0.0) break;
                vec3 diffuse = vec3(0.0), specular = vec3(0.0);
                oneLight = vec3(0.0); oneReach = 1.0; oneEmitter = 0u;
                lampLight(world, n, normalize(uGiEye - world), a, a * a, diffuse, specular);
                vec3 total = oneLight * 3.14159265358979 * LAMP_DIFFUSE;
                if (dot(total, vec3(1.0)) <= 0.0) continue;
                {{(half ? """
                // A pixel traced on its own (not the traced one of its 2 x 2, and of another surface than it; TraceOwn): that ray's answer.
                bool traced = false;
                int spot = (px.x & 1) + 2 * (px.y & 1), own = sub.x + 2 * sub.y;
                if (spot != own)
                {
                    uint exact = imageLoad(uExact, ivec3(px >> 1, slice)).r;
                    int k = spot - (spot > own ? 1 : 0);
                    traced = (exact & (1u << k)) != 0u;
                    if (traced) flag = (exact & (8u << k)) != 0u ? 1.0 : 0.0;
                }
                if (!traced && (anyHit || none != 0))
                {
                    // The flags on the pixel's own surface: of its kind, within a pixel and a half of its plane (5 mm at least).
                    float tolerance = max(0.05, 1.5 * z * uFootprint), weight = 0.0, sum = 0.0;
                    int k = 0;
                    for (int dy = 0; dy <= odd.y; dy++)
                        for (int dx = 0; dx <= odd.x; dx++, k++)
                        {
                            if (tap[k].g < 0.0) continue;
                            ivec2 j = j0 + ivec2(dx, dy), q = j * 2 + sub;
                            if (q != px && (imageLoad(uExact, ivec3(j, slice)).r >> 6) != kind) continue;
                            float zq = q == px ? z : depthAt(q);
                            if (zq <= 0.0 || abs(dot(positionAt(q, zq) - world, rankPlane)) > tolerance) continue;
                            sum += tap[k].r;
                            weight += 1.0;
                        }
                    if (weight > 0.0) flag = sum / weight;
                    else
                    {
                        // No flag round it is this surface's (a crease, the rim of what the rays' kernel took for one surface): the flags a step further that are,
                        // else the one nearest its plane. Not the pixel's own ray (see RoundLight).
                        float best = 1e30, bestFlag = 0.0;
                        for (int dy = -1; dy <= 2; dy++)
                            for (int dx = -1; dx <= 2; dx++)
                            {
                                ivec2 j = j0 + ivec2(dx, dy);
                                if (any(lessThan(j, ivec2(0))) || any(greaterThanEqual(j, halfSize))) continue;
                                vec2 v = imageLoad(uFlag, ivec3(j, slice)).rg;
                                if (v.g < 0.0) continue;
                                ivec2 q = j * 2 + sub;
                                float zq = depthAt(q);
                                if (zq <= 0.0) continue;
                                bool same = (imageLoad(uExact, ivec3(j, slice)).r >> 6) == kind;
                                float off = abs(dot(positionAt(q, zq) - world, rankPlane)) + (same ? 0.0 : 1e3);
                                if (off < 2.0 * tolerance) { sum += v.r; weight += 1.0; }
                                if (off < best) { best = off; bestFlag = v.r; }
                            }
                        flag = weight > 0.0 ? sum / weight : bestFlag;
                    }
                }
                """ : "")}}
                vec3 plain = specular * oneReach;
                lostSum += vec4(flag * total, lampBody ? dot(total, vec3(0.2126, 0.7152, 0.0722)) : 0.0);
                if (lampBody) plainSum += vec4(flag * plain, dot(plain, vec3(0.2126, 0.7152, 0.0722)));
            }
            if (dot(lostSum, vec4(1.0)) > 0.0) imageStore(uSum, px, imageLoad(uSum, px) + lostSum);
            if (dot(plainSum, vec4(1.0)) > 0.0) imageStore(uPlain, px, imageLoad(uPlain, px) + plainSum);
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
            lampWorn = lampBody && (surface.w >> 28) == 0u && (surface.w & 1048576u) != 0u;
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
                        uint emitter = uint(texelFetch(uLightData, ivec2(3, lamp), 0).w);
                        lampSkip = uMode > 1.5 ? 60.0 : emitter != 0u ? lampSkipOf(lamp, emitter) : max(LAMP_NEAR_SKIP, 8.0 * radius);   // as the tiles (LampTiles.SkipOf); mode 2: well past the lamp's housing
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
    public static readonly string Apply = "#version 330 core\n" + AtmosphereShaders.Functions + GiResolveShaders.Reconstruct + GiResolveShaders.TermLuminances + """
        in vec2 vUv;
        out vec4 fragColour;
        uniform usampler2D uAlbedo;
        uniform sampler2D uLost;
        uniform sampler2D uTotal;
        uniform sampler2D uTotal1;
        uniform sampler2D uPlain;      // a character's lamps' specular (the per-lamp trace): rgb what is lost of it, a the luminance of all of it
        uniform sampler2D uSpecular;   // the sun's term target: its alpha holds the luminance of the lamps' light a character's fragment took
        uniform float uSpecularOn;     // 1: uTotal's alpha holds the light of a character's lamps, lost or not (the per-lamp trace)

        void main()
        {
            ivec2 px = ivec2(floor(gl_FragCoord.xy));
            vec2 blocked = texelFetch(uLost, px, 0).rg;
            vec4 sum = texelFetch(uTotal, px, 0);
            vec3 lost = blocked.r * sum.rgb + blocked.g * texelFetch(uTotal1, px, 0).rgb;
            if (max(lost.r, max(lost.g, lost.b)) <= 0.0) discard;
            uvec4 surface = texelFetch(uAlbedo, px, 0);
            vec3 p = positionAt(px, depthAt(px));
            vec3 transmittance = atmoApply(vec3(1.0), uGiEye, p) - atmoApply(vec3(0.0), uGiEye, p);
            vec3 albedo = vec3(float(surface.z & 255u), float((surface.z >> 8) & 255u), float(surface.w & 255u)) / 255.0;
            vec3 taken = albedo * albedo * lost;
            // A character: the share of its lamps' light that is lost (this pass's own terms, lost over all) takes that share of what its fragment took of
            // the lamps, whose luminances it wrote. The diffuse rebuilt here (the stored albedo, the shading normal for skin's smoothed ones) has the right
            // hue and nearly the right level: it is scaled to the fragment's own. The specular (highlights too) goes as the lost lamps' own (uPlain, their
            // share of the lamps' specular and its hue): taken in the diffuse's hue it left cyan patches where skin shines (round the eyes, the nose, the
            // lips), and by the diffuse's lost share in the lost light's hue a green line where a lamp grazes an arm (another lamp's highlight went with
            // the blocked one's red). Without either a rest stayed in a full shadow: a red
            // glow and a blocked lamp's highlight on a face in a hood, a shoulder.
            if (uSpecularOn > 0.5 && (surface.w & 524288u) != 0u && sum.a > 0.0)
            {
                const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
                vec3 own = giTermLuminances(texelFetch(uSpecular, px, 0).a);
                vec4 plain = texelFetch(uPlain, px, 0);
                float lostLevel = dot(lost, lum), share = clamp(lostLevel / sum.a, 0.0, 1.0);
                // The diffuse's level is the channels' mean: matched by luminance, a rebuilt hue short of green took three times as much red for it.
                float rebuilt = dot(taken, vec3(1.0 / 3.0));
                taken = (rebuilt > 1e-7 ? taken * (own.x * share / rebuilt) : vec3(own.x * share)) + (plain.a > 1e-7 ? plain.rgb * (own.y / plain.a) : lost * (own.y * share / max(lostLevel, 1e-7)));
            }
            fragColour = vec4(taken * transmittance, 0.0);
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
