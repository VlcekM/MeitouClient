namespace Meitou.Rendering;

/// <summary>
/// GLSL of the placed fog volumes (docs/formats/fogfeatures.md "How the game draws them"): the game's <c>fog_planes_fs</c>, <c>fog_sphere_fs</c>
/// and <c>fog_beam_fs</c> (post/fog.hlsl) evaluated per pixel in every world shader, after the haze, as the game blends its volumes over the
/// hazed scene. The game rasterises each volume's hull and reads the G-buffer depth; here each shader passes its own point's distance, and the
/// sky the haze's far distance. <see cref="AtmosphereShaders.Functions"/> embeds this text (it uses <c>hazeColour</c> and the atmosphere's
/// uniforms) and calls <c>fogVolumesApply</c> from <c>atmoApply</c>. The data come from <see cref="FogVolumes"/>: every volume in view, packed
/// into <c>uFogVolumeData</c> farthest first, each starting with a vec4 whose w is its type.
/// </summary>
static class FogVolumeShaders
{
    /// <summary>The vec4s of <c>uFogVolumeData</c> (the frame block's array; its GLSL size is written out in <see cref="NativeShaders.FrameBlock"/>
    /// and <see cref="Functions"/>).</summary>
    public const int MaxData = 512;

    /// <summary>Type tags (the first vec4's w) and the vec4s each type takes.</summary>
    public const int BlockType = 1, SphereType = 2, BeamType = 3;
    public const int BlockLength = 10, SphereLength = 3, BeamLength = 4;

    public static int Length(int type) => type switch { BlockType => BlockLength, SphereType => SphereLength, BeamType => BeamLength, _ => throw new ArgumentOutOfRangeException(nameof(type)) };

    /// <summary>The shader's sphere radius over the stored one: the hull is <c>unit_sphere.mesh</c> (radius 0.5) scaled by 2r, and
    /// <c>fog_sphere_vs</c> measures 0.48 of the unscaled mesh (0.48 × 2r).</summary>
    public const float SphereShaderRadius = 0.96f;
    /// <summary>The shader's beam radius over the stored one: <c>cylinder.mesh</c> scaled by 2r across, <c>fog_beam_vs</c> measures 0.4 of it.</summary>
    public const float BeamShaderRadius = 0.8f;

    /// <summary>
    /// The packed layout (vec4s):
    /// block  (type 1): (box min, 1), (box max, edgeBlur), (colour, density), the seven planes (normal, w);
    /// sphere (type 2): (centre, 2), (shader radius, hull radius, alpha, additive), (colour, density);
    /// beam   (type 3): (start, 3), (unit axis, length), (shader radius, edgeBlur, alpha, additive), (colour, density).
    /// </summary>
    public const string Functions = """

        // ---- placed fog volumes (fogfeatures.dat and the weather's EFFECT_FOG_VOLUME spheres) ----
        uniform vec4 uFogVolumeEye;        // xyz the eye (for the sky pass), w the blocks' light: max(0.08, sunColour.w * 1.6 * saturate(3 sunY + 0.2))
        uniform vec4 uFogVolumeInfo;       // x the vec4s in use, y sunColour.w (the spheres' and beams' light)
        uniform vec4 uFogVolumeData[512];  // the volumes in view, farthest first (FogVolumeShaders: block 10, sphere 3, beam 4 vec4s)

        // post/fog.hlsl fogValue: the ease-in-out curve of saturate(depth * density).
        float fogVolumeCurve(float amount)
        {
            amount = clamp(amount, 0.0, 1.0);
            return amount < 0.5 ? 2.0 * amount * amount : 1.0 - 2.0 * (amount - 1.0) * (amount - 1.0);
        }

        // A block (fog_planes_fs) seen along the unit ray d from the eye to a point dist away: (colour, alpha) to blend over the scene.
        vec4 fogVolumeBlock(int o, vec3 eye, vec3 d, float dist)
        {
            // A box round the part of the block above the lowest visible height first: most pixels miss it.
            vec3 boxMin = uFogVolumeData[o].xyz;
            vec4 boxMaxEdge = uFogVolumeData[o + 1];
            vec3 inv = 1.0 / d;
            vec3 t0 = (boxMin - eye) * inv, t1 = (boxMaxEdge.xyz - eye) * inv;
            vec3 lo = min(t0, t1), hi = max(t0, t1);
            if (max(max(lo.x, lo.y), max(lo.z, 0.0)) > min(min(hi.x, hi.y), min(hi.z, dist))) return vec4(0.0);
            float near = 0.0, far = dist;
            for (int i = 0; i < 7; i++)
            {
                vec4 p = uFogVolumeData[o + 3 + i];
                float dn = dot(p.xyz, d), s = p.w - dot(p.xyz, eye);
                if (abs(dn) < 1e-6) { if (s <= 0.0) return vec4(0.0); continue; }   // parallel: all outside or no limit
                float t = s / dn;
                if (dn < 0.0) near = max(near, t); else far = min(far, t);
            }
            near = min(near, dist);
            if (far <= near) return vec4(0.0);
            vec4 colourDensity = uFogVolumeData[o + 2];
            // Soft edges, measured at the middle of the path through the volume; softer far away.
            float edgeBlur = boxMaxEdge.w * clamp(1.0 / (far * 0.00006), 0.0, 1.0);
            vec3 middle = eye + d * ((far + near) * 0.5);
            float edge = 1.0;
            for (int i = 0; i < 7; i++)
            {
                vec4 p = uFogVolumeData[o + 3 + i];
                edge *= clamp((p.w - dot(p.xyz, middle)) * edgeBlur, 0.0, 1.0);
            }
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

        // A sphere (fog_sphere_fs): the path through it, its far end pulled in by a slow pattern once its near side is beyond 10000.
        vec4 fogVolumeSphere(int o, vec3 eye, vec3 d, float dist)
        {
            vec3 m = eye - uFogVolumeData[o].xyz;
            vec4 shape = uFogVolumeData[o + 1];
            float b = dot(m, d), mm = dot(m, m);
            float disc = b * b - (mm - shape.x * shape.x);
            if (disc <= 0.0) return vec4(0.0);   // the ray misses: the game's path is then zero or less
            float root = sqrt(disc);
            float t0 = clamp(-b - root, 0.0, dist), t1 = clamp(-b + root, 0.0, dist);
            float far = clamp((t0 - 10000.0) * 0.0001, 0.0, 1.0);
            if (far > 0.0)
            {
                // The game samples the pattern where the pixel's hull (the mesh, radius r) is: its near side, seen from outside.
                float hull = -b - sqrt(max(b * b - (mm - shape.y * shape.y), 0.0));
                vec3 w = eye + d * hull;
                float ss = sin(w.y * 0.004) + 1.0 - (cos(w.z * 0.0008) * cos(w.x * 0.0008) - 1.0);
                t1 -= ss * far * 1000.0;
            }
            vec4 colourDensity = uFogVolumeData[o + 2];
            return vec4(colourDensity.rgb * uFogVolumeInfo.y, fogVolumeCurve((t1 - t0) * colourDensity.a) * shape.z);
        }

        // A beam (fog_beam_fs): the path through a cylinder capped at its two ends, faded towards the ends.
        vec4 fogVolumeBeam(int o, vec3 eye, vec3 d, float dist)
        {
            vec3 start = uFogVolumeData[o].xyz - eye;
            vec4 axis = uFogVolumeData[o + 1];
            vec4 shape = uFogVolumeData[o + 2];
            vec3 m = -start;
            float md = dot(m, axis.xyz), nd = dot(d, axis.xyz), mn = dot(m, d);
            float a = max(1.0 - nd * nd, 1e-7);
            float c = dot(m, m) - shape.x * shape.x - md * md;
            float b = mn - nd * md;
            float disc = b * b - a * c;
            if (disc <= 0.0) return vec4(0.0);
            float root = sqrt(disc);
            float t0 = clamp((-b - root) / a, 0.0, dist), t1 = clamp((-b + root) / a, 0.0, dist);
            if (abs(nd) < 1e-7) nd = nd < 0.0 ? -1e-7 : 1e-7;
            float e0 = dot(axis.xyz, start) / nd, e1 = dot(axis.xyz, start + axis.xyz * axis.w) / nd;
            t0 = max(t0, nd > 0.0 ? e0 : e1);
            t1 = min(t1, nd < 0.0 ? e0 : e1);
            if (t1 <= t0) return vec4(0.0);
            float along = dot(d * ((t0 + t1) * 0.5), axis.xyz);
            float edge = clamp((along - e0 * nd) * shape.y, 0.0, 1.0) * clamp((e1 * nd - along) * shape.y, 0.0, 1.0);
            edge = 1.0 - (1.0 - edge) * (1.0 - edge);
            vec4 colourDensity = uFogVolumeData[o + 3];
            return vec4(colourDensity.rgb * uFogVolumeInfo.y, fogVolumeCurve((t1 - t0) * colourDensity.a * edge) * shape.z);
        }

        // Every volume in view over the colour of a point dist away along d, farthest first (each blended as the game's pass: alpha, or added).
        vec3 fogVolumesApply(vec3 colour, vec3 eye, vec3 d, float dist)
        {
            int used = int(uFogVolumeInfo.x);
            int o = 0;
            // The game's depth never exceeds its far clip D (the sky is at D): so a volume beyond D adds nothing (FogVolumes leaves those out).
            dist = min(dist, uAtmoFog.w);
            while (o < used)
            {
                int type = int(uFogVolumeData[o].w);
                if (type == 1)
                {
                    vec4 f = fogVolumeBlock(o, eye, d, dist);
                    colour = mix(colour, f.rgb, f.a);
                    o += 10;
                    continue;
                }
                vec4 f;
                float additive;
                if (type == 2) { f = fogVolumeSphere(o, eye, d, dist); additive = uFogVolumeData[o + 1].w; o += 3; }
                else if (type == 3) { f = fogVolumeBeam(o, eye, d, dist); additive = uFogVolumeData[o + 2].w; o += 4; }
                else break;
                colour = additive > 0.5 ? colour + f.rgb * f.a : mix(colour, f.rgb, f.a);
            }
            return colour;
        }
        """;
}
