namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The lamps' GLSL (docs/render-lights.md), shared by the world's lighting (<c>kenshiLight</c>) and the probe trace. It needs the declarations
/// of <c>uLightGrid</c> (x, z of the grid's corner, its cell size, its cells per side: 0 while there are no lamps) and the samplers
/// <c>uLightCells</c> (RG32F per cell: first entry, count), <c>uLightIndex</c> (R32F entries: lamp numbers, <see cref="WorldLamps.IndexWidth"/>
/// per row) and <c>uLightData</c> (RGBA32F, a row per lamp: position and radius; radiance and type; spot direction and cos of the outer half
/// angle; cos of the inner half angle, falloff and room edge count; room heights and inward footprint planes).
/// <c>LAMP_DIFFUSE_ONLY</c> leaves the specular out; <c>LAMP_VISIBILITY</c> supplies the trace's shadow query for interior sources; <c>LAMP_SKIN</c>
/// (the characters) takes the diffuse term from <c>vec3 lampSkinDiffuse(vec3 n, vec3 l)</c>, which the including shader defines.
/// </summary>
static class LampShaders
{
    /// <summary>
    /// A lamp tile's mapping (Gi.LampTiles): a sphere by octahedral directions, a cone by its plane, a little wider than the cone so a lookup at the
    /// rim stays inside. For the fill and for the lookup.
    /// </summary>
    public const string TileMapping = """
        const float LAMP_TILE_GUARD = 1.04;
        void lampBasis(vec3 a, out vec3 x, out vec3 y)
        {
            float s = a.z >= 0.0 ? 1.0 : -1.0, e = -1.0 / (s + a.z), f = a.x * a.y * e;
            x = vec3(1.0 + s * a.x * a.x * e, s * f, -s * a.x);
            y = vec3(f, s + a.y * a.y * e, -a.y);
        }
        float lampTileTan(float cosOuter) { return sqrt(max(1.0 - cosOuter * cosOuter, 0.0)) / max(cosOuter, 0.05) * LAMP_TILE_GUARD; }
        vec2 lampOctWrap(vec2 v) { return (1.0 - abs(v.yx)) * vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0); }
        vec2 lampOctUv(vec3 d)
        {
            vec2 p = d.xz / (abs(d.x) + abs(d.y) + abs(d.z));
            if (d.y < 0.0) p = lampOctWrap(p);
            return p * 0.5 + 0.5;
        }
        vec3 lampOctDir(vec2 uv)
        {
            vec2 p = uv * 2.0 - 1.0;
            float y = 1.0 - abs(p.x) - abs(p.y);
            if (y < 0.0) p = lampOctWrap(p);
            return normalize(vec3(p.x, y, p.y));
        }

        """;

    /// <summary>
    /// <c>lampTileVisibility</c>: how much of a lamp reaches a point, from its tile (<c>uLampTiles</c>; column 4 of the lamp's row: z the place, w
    /// the side, negative for a sphere, 0 none; column 5: x the lamp's radius). The penumbra is the lamp's own: its radius seen past the blockers a
    /// fixed spiral of lookups finds. A lookup r texels off counts only what stands nearer the lamp than the point by what the surface can gain over
    /// r texels on its slope: its own near edge is not a blocker, and a real blocker of that penumbra stands far nearer still. The depth of the
    /// surface's plane at each lookup was tried and dropped: at a graze it swings with the normal, and drew the ripples, then the terrain's facets,
    /// into the penumbra (**Observed**, spotlight-cross). Nothing here changes from frame to frame.
    /// </summary>
    public const string TileVisibility = """
        float lampTileAt(ivec2 corner, int side, ivec2 q)
        {
            return texelFetch(uLampTiles, corner + clamp(q, ivec2(0), ivec2(side - 1)), 0).r;
        }
        // Whether a point at depth receiver gets past the blockers, filtered over a texel.
        float lampTileLit(ivec2 corner, int side, vec2 st, float receiver)
        {
            vec2 at = st - 0.5, base = floor(at), f = at - base;
            ivec2 q = clamp(ivec2(base), ivec2(0), ivec2(side - 1)), r = clamp(ivec2(base) + 1, ivec2(0), ivec2(side - 1));
            float l00 = step(receiver, texelFetch(uLampTiles, corner + ivec2(q.x, q.y), 0).r);
            float l10 = step(receiver, texelFetch(uLampTiles, corner + ivec2(r.x, q.y), 0).r);
            float l01 = step(receiver, texelFetch(uLampTiles, corner + ivec2(q.x, r.y), 0).r);
            float l11 = step(receiver, texelFetch(uLampTiles, corner + ivec2(r.x, r.y), 0).r);
            return mix(mix(l00, l10, f.x), mix(l01, l11, f.x), f.y);
        }
        float lampTileVisibility(int lamp, vec3 toLamp, float d, vec3 n)
        {
            vec4 t = texelFetch(uLightData, ivec2(4, lamp), 0);
            int side = int(abs(t.w));
            if (side < 1) return 1.0;
            vec4 m = texelFetch(uLightData, ivec2(5, lamp), 0);
            float reach = texelFetch(uLightData, ivec2(0, lamp), 0).w, radius = m.x;
            vec3 l = toLamp / max(d, 1e-4);
            vec2 uv;
            float texelPerDepth;
            if (t.w < 0.0)
            {
                uv = lampOctUv(-l);
                texelPerDepth = 3.5 / float(side);
            }
            else
            {
                vec4 s = texelFetch(uLightData, ivec2(2, lamp), 0);
                float span = lampTileTan(s.w);
                vec3 x, y;
                lampBasis(s.xyz, x, y);
                float z = max(dot(-l, s.xyz), 1e-3);
                uv = vec2(dot(-l, x), dot(-l, y)) / z / span * 0.5 + 0.5;
                texelPerDepth = 2.0 * span / float(side);
            }
            float texel = texelPerDepth * d;
            float nl = clamp(dot(n, l), 0.03, 1.0);
            float slope = min(sqrt(1.0 - nl * nl) / nl, 3.0), margin = 0.5 + texel * (1.0 + slope);
            int place = int(t.z), row = place / 128;
            ivec2 corner = ivec2(place - row * 128, row) * LAMP_TILE_UNIT;
            vec2 st = uv * float(side);
            #if defined(LAMP_SKIN) || defined(LAMP_TILE_CHEAP)
            // A character's skin, a hit of the traced light: one filtered lookup. Small, busy surfaces that overlap by the dozen, a light blurred anyway.
            return lampTileLit(corner, side, st, (d - margin - 2.0) / reach);
            #endif

            // Is anything in the way of any part of the lamp, and how far off it: the region a blocker near the lamp can cover, read by a spiral.
            float search = clamp(radius / (texelPerDepth * max(0.1 * d, 15.0)), 1.5, 48.0);
            float sum = 0.0, held = 0.0;
            for (int k = 0; k < 48; k++)
            {
                float a = float(k) * 2.39996323, r = sqrt((float(k) + 0.5) / 48.0) * search;
                vec2 p = st + vec2(cos(a), sin(a)) * r;
                float v = lampTileAt(corner, side, ivec2(floor(p)));
                if (v < 0.9999 && v < (d - margin - r * texel * slope) / reach) { sum += v; held += 1.0; }
            }
            if (held < 0.5) return 1.0;
            float blocker = sum / held * reach;
            float spread = clamp(radius * max(d - blocker, 0.0) / max(blocker, 1.0) / texel, 1.5, 64.0);

            // The filter: a fixed spiral of lookups over the penumbra, more for a wide one.
            float lit = 0.0;
            if (spread < 2.5)
            {
                // A narrow edge: a few filtered lookups keep it smooth.
                for (int k = 0; k < 4; k++)
                {
                    float a = float(k) * 1.57079633 + 0.6, r = 0.75 * spread;
                    lit += lampTileLit(corner, side, st + vec2(cos(a), sin(a)) * r, (d - margin - r * texel * slope) / reach);
                }
                return lit / 4.0;
            }
            // A wide one: a spiral of filtered lookups (a single texel per lookup steps along the texels' grid, in plates at a close view).
            int taps = spread > 8.0 ? 64 : 32;
            for (int k = 0; k < taps; k++)
            {
                float a = float(k) * 2.39996323, r = sqrt((float(k) + 0.5) / float(taps)) * spread;
                lit += lampTileLit(corner, side, st + vec2(cos(a), sin(a)) * r, (d - margin - r * texel * slope) / reach);
            }
            return lit / float(taps);
        }

        """;

    /// <summary>
    /// The lamp functions (the class summary's inputs and switches): the game's attenuation, and <c>lampLight</c>, which adds the lamps around a
    /// surface point like the sun.
    /// </summary>
    public const string Lighting = """
        #ifndef LAMP_VISIBILITY
        #define LAMP_VISIBILITY(world, n, toLamp, indoor, emitter) 1.0
        #endif
        // The game's light attenuation (docs/formats/lights.md, WorldLights.Attenuation): not inverse-square, 1 at the lamp, 0 at its radius.
        float lampAttenuation(float d, float radius)
        {
            float x = clamp(d / radius, 0.0, 1.0);
            if (x < 0.649) return 0.2 + 0.8 * (1.0 - x) * (1.0 - x) * (1.0 - x);
            if (x < 0.8) return 0.242 - 3.0 * (x - 0.6) * (x - 0.6);
            return 3.0 * (x - 1.0) * (x - 1.0);
        }
        #ifdef LAMP_SKIN
        vec3 lampSkinDiffuse(vec3 n, vec3 l);
        #endif
        // The lamps around a surface point, added like the sun (diffuse to be multiplied by the albedo, and the specular), unshadowed as in the game.
        void lampLight(vec3 world, vec3 n, vec3 v, float a, float a2, inout vec3 diffuse, inout vec3 specular)
        {
            ivec2 cell = ivec2(floor((world.xz - uLightGrid.xy) / uLightGrid.z));
            int cells = int(uLightGrid.w);
            if (cell.x < 0 || cell.y < 0 || cell.x >= cells || cell.y >= cells) return;
            vec2 entry = texelFetch(uLightCells, cell, 0).rg;
            int first = int(entry.x), count = int(entry.y);
            for (int i = 0; i < count; i++)
            {
                int k = first + i;
                int lamp = int(texelFetch(uLightIndex, ivec2(k % LAMP_INDEX_WIDTH, k / LAMP_INDEX_WIDTH), 0).r);
                #ifdef LAMP_ONLY
                if (lamp != LAMP_ONLY) continue;
                #endif
                vec4 p = texelFetch(uLightData, ivec2(0, lamp), 0);
                vec3 toLamp = p.xyz - world;
                float d = length(toLamp);
                if (d >= p.w) continue;
                vec4 s2 = texelFetch(uLightData, ivec2(3, lamp), 0);
                int edges = int(s2.z);
                #ifndef LAMP_DIFFUSE_ONLY
                // Raster lamps have no shadow rays. The trace uses actual visibility instead, including light through openings.
                if (edges > 0)
                {
                    vec2 height = texelFetch(uLightData, ivec2(4, lamp), 0).xy;
                    if (world.y < height.x || world.y > height.y) continue;
                    bool inside = true;
                    for (int e = 0; e < edges; e++)
                    {
                        vec3 plane = texelFetch(uLightData, ivec2(6 + e, lamp), 0).xyz;
                        if (dot(plane.xy, world.xz) + plane.z < 0.0) { inside = false; break; }
                    }
                    if (!inside) continue;
                }
                #endif
                vec3 l = toLamp / max(d, 1e-4);
                #ifdef LAMP_RANK
                LAMP_RANK(lamp, l, d, p.w);
                #endif
                float nl = dot(n, l);
                #ifdef LAMP_SKIN
                // The characters' Meitou skin (CharacterShaders.lampSkinDiffuse): the lamp's diffuse term wraps past the terminator as the sun's does.
                vec3 diffuseNl = lampSkinDiffuse(n, l);
                if (max(diffuseNl.r, max(diffuseNl.g, diffuseNl.b)) <= 0.0) continue;
                nl = max(nl, 0.0);
                #else
                if (nl <= 0.0) continue;
                vec3 diffuseNl = vec3(nl);
                #endif
                vec4 c = texelFetch(uLightData, ivec2(1, lamp), 0);
                vec3 radiance = c.rgb * lampAttenuation(d, p.w);
                if (c.w > 0.5)
                {
                    vec4 s = texelFetch(uLightData, ivec2(2, lamp), 0);
                    float cosAngle = dot(s.xyz, -l);
                    if (cosAngle <= s.w) continue;
                    radiance *= s2.x - s.w < 1e-5 ? 1.0 : clamp(pow(clamp((cosAngle - s.w) / (s2.x - s.w), 0.0, 1.0), s2.y), 0.0, 1.0);
                }
                radiance *= LAMP_VISIBILITY(world, n, toLamp, edges, uint(s2.w));
                diffuse += 3.14159265358979 * diffuseNl * radiance * LAMP_DIFFUSE;
                #ifndef LAMP_DIFFUSE_ONLY
                vec3 h = normalize(v + l);
                float nh = clamp(dot(n, h), 0.0, 1.0), lh = clamp(dot(l, h), 0.0, 1.0);
                float denom = max(nh * nh * (a2 - 1.0) + 1.0, 1e-6);
                float D = a2 / (3.14159265358979 * denom * denom);
                float fresnel = exp2((-5.55473 * lh - 6.98316) * lh);
                float k2 = a * 0.5, vis = 1.0 / (lh * lh * (1.0 - k2 * k2) + k2 * k2);
                specular += radiance * (nl * D * (0.04 * vis + 0.96 * fresnel * vis)) / 3.14159265358979;
                #endif
            }
        }

        """;

    /// <summary>
    /// The defines <see cref="Lighting"/> needs, ahead of it.
    /// </summary>
    public static string Defines => $"#define LAMP_INDEX_WIDTH {WorldLamps.IndexWidth}\n" +
        $"#define LAMP_TILE_UNIT {Gi.LampTiles.MinTile}\n" +
        $"#define LAMP_DIFFUSE {(1 - Data.World.KenshiLighting.DielectricSpecular).ToString("0.0########",
            System.Globalization.CultureInfo.InvariantCulture)}\n";
}
