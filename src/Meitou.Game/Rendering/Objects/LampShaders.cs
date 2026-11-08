namespace Meitou.Rendering;

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
                        vec3 plane = texelFetch(uLightData, ivec2(5 + e, lamp), 0).xyz;
                        if (dot(plane.xy, world.xz) + plane.z < 0.0) { inside = false; break; }
                    }
                    if (!inside) continue;
                }
                #endif
                vec3 l = toLamp / max(d, 1e-4);
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

    /// <summary>The defines <see cref="Lighting"/> needs, ahead of it.</summary>
    public static string Defines => $"#define LAMP_INDEX_WIDTH {WorldLamps.IndexWidth}\n#define LAMP_DIFFUSE {(1 - Meitou.Data.World.KenshiLighting.DielectricSpecular).ToString("0.0########", System.Globalization.CultureInfo.InvariantCulture)}\n";
}
