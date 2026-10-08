namespace Meitou.Rendering;

/// <summary>
/// GLSL of the placed fog volumes (docs/formats/fogfeatures.md "How the game draws them"): the game's <c>fog_planes_fs</c> (post/fog.hlsl)
/// evaluated per pixel in every world shader, after the haze, as the game blends its volumes over the hazed scene. The game rasterises each
/// volume's hull and reads the G-buffer depth; here each shader passes its own point's distance, and the sky the haze's far distance.
/// <see cref="AtmosphereShaders.Functions"/> embeds this text (it uses <c>hazeColour</c> and the atmosphere's uniforms) and calls
/// <c>fogVolumesApply</c> from <c>atmoApply</c>. The data come from <see cref="FogVolumes"/>.
/// </summary>
static class FogVolumeShaders
{
    /// <summary>Texels per volume row in <c>uFogVolumes</c>: the seven planes (normal, w), (colour, density), (edgeBlur, 0, 0, 0), then the
    /// minimum and maximum corner of a box round the block above the lowest visible height (a quick rejection, not the game's).</summary>
    public const int RowTexels = 11;
    /// <summary>The most volumes one frame evaluates (the two select vectors).</summary>
    public const int MaxActive = 8;

    public const string Functions = """

        // ---- placed fog volumes (fogfeatures.dat blocks) ----
        uniform sampler2D uFogVolumes;
        uniform vec4 uFogVolumeEye;        // xyz the eye (for the sky pass), w the light: max(0.08, sunColour.w * 1.6 * saturate(3 sunY + 0.2))
        uniform vec4 uFogVolumeSelect0;    // 1 + the rows of the volumes to draw, farthest first; 0 ends the list
        uniform vec4 uFogVolumeSelect1;

        // post/fog.hlsl fogValue: the ease-in-out curve of saturate(depth * density).
        float fogVolumeCurve(float amount)
        {
            amount = clamp(amount, 0.0, 1.0);
            return amount < 0.5 ? 2.0 * amount * amount : 1.0 - 2.0 * (amount - 1.0) * (amount - 1.0);
        }

        // One block seen along the unit ray d from the eye to a point dist away: (colour, alpha) to blend over the scene.
        vec4 fogVolumeBlock(int row, vec3 eye, vec3 d, float dist)
        {
            // A box round the part of the block above the lowest visible height first: most pixels miss it (two fetches, no planes).
            vec3 boxMin = texelFetch(uFogVolumes, ivec2(9, row), 0).xyz, boxMax = texelFetch(uFogVolumes, ivec2(10, row), 0).xyz;
            vec3 inv = 1.0 / d;
            vec3 t0 = (boxMin - eye) * inv, t1 = (boxMax - eye) * inv;
            vec3 lo = min(t0, t1), hi = max(t0, t1);
            if (max(max(lo.x, lo.y), max(lo.z, 0.0)) > min(min(hi.x, hi.y), min(hi.z, dist))) return vec4(0.0);
            float near = 0.0, far = dist;
            vec4 planes[7];
            for (int i = 0; i < 7; i++)
            {
                vec4 p = texelFetch(uFogVolumes, ivec2(i, row), 0);
                planes[i] = p;
                float dn = dot(p.xyz, d), s = p.w - dot(p.xyz, eye);
                if (abs(dn) < 1e-6) { if (s <= 0.0) return vec4(0.0); continue; }   // parallel: all outside or no limit
                float t = s / dn;
                if (dn < 0.0) near = max(near, t); else far = min(far, t);
            }
            near = min(near, dist);
            if (far <= near) return vec4(0.0);
            vec4 colourDensity = texelFetch(uFogVolumes, ivec2(7, row), 0);
            // Soft edges, measured at the middle of the path through the volume; softer far away.
            float edgeBlur = texelFetch(uFogVolumes, ivec2(8, row), 0).x * clamp(1.0 / (far * 0.00006), 0.0, 1.0);
            vec3 middle = eye + d * ((far + near) * 0.5);
            float edge = 1.0;
            for (int i = 0; i < 7; i++) edge *= clamp((planes[i].w - dot(planes[i].xyz, middle)) * edgeBlur, 0.0, 1.0);
            edge *= 1.0 + abs(d.y) * 0.9;
            float alpha = fogVolumeCurve((far - near) * colourDensity.a * edge);
            vec4 fog = vec4(colourDensity.rgb * uFogVolumeEye.w, alpha);
            if (near == 0.0) return fog;
            // The eye outside: far volumes give way to the haze at their near side, then the weather's fog over it.
            float toHaze = uAtmoHaze.x > 0.5 ? clamp((near - 12000.0) * 0.0001, 0.0, 1.0) : 0.0;
            vec4 r = fog;
            if (toHaze > 0.0)
            {
                float level = clamp((near - uAtmoHaze.y) / max(uAtmoHaze.z - uAtmoHaze.y, 1.0), 0.0, 1.0);
                level = min(level * uAtmoAltitude.y, 1.0);
                vec4 haze = vec4(mix(hazeColour(d * near), uAtmoHazeCloud.rgb, uAtmoHazeCloud.a), level * alpha);
                r = mix(fog, clamp(haze, 0.0, 4.0), toHaze);
            }
            float global = uAtmoFog.z > 0.0 ? fogVolumeCurve(near * uAtmoHaze.w) * uAtmoFog.z : 0.0;
            r = mix(r, vec4(uAtmoFogColour, global), global);
            r.a = clamp(r.a + global, 0.0, 1.0) * alpha;
            return r;
        }

        // Every selected volume over the colour of a point dist away along d, farthest volume first (each blended like the game's alpha pass).
        vec3 fogVolumesApply(vec3 colour, vec3 eye, vec3 d, float dist)
        {
            for (int k = 0; k < 8; k++)
            {
                float row = k < 4 ? uFogVolumeSelect0[k] : uFogVolumeSelect1[k - 4];
                if (row < 0.5) break;
                vec4 f = fogVolumeBlock(int(row) - 1, eye, d, dist);
                colour = mix(colour, f.rgb, f.a);
            }
            return colour;
        }
        """;
}
