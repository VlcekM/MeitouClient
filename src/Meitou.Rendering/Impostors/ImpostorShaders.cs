using System.Text.RegularExpressions;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// GLSL of the impostors (docs/impostors.md):
/// <list type="bullet">
/// <item><see cref="Functions"/>: the sampling API for any impostor shader (the hemi-octahedral map, frame selection, the three-frame blend
/// with per-pixel frame-plane projection and optional parallax) returning the surface the lighting needs.</item>
/// <item><see cref="Vertex"/> / <see cref="Fragment"/>: a complete instanced impostor program on the foliage's instance ABI (the per-instance
/// matrix at locations 7 to 10, the fade in row 0 w), lit exactly as the mesh shader lights a surface: the reference the GPU-driven foliage
/// path adopts.</item>
/// <item><see cref="BakeFragment"/>: the shared mesh shader with an output switch, so the baked maps are what the mesh shader computes.</item>
/// </list>
/// </summary>
public static class ImpostorShaders
{
    /// <summary>The sampling functions (GLSL 3.30; no uniforms of their own).</summary>
    public const string Functions = """

        // ---- impostors (Meitou.Rendering.Impostors.ImpostorShaders, docs/impostors.md) ----
        // Upper-hemisphere direction (object space, y up; below the horizon clamped to it) to hemi-octahedral UV in [0, 1]².
        vec2 impostorEncode(vec3 d)
        {
            d.y = max(d.y, 0.0);
            float s = abs(d.x) + d.y + abs(d.z);
            if (s < 1e-20) return vec2(0.5);
            vec2 xz = d.xz / s;
            return vec2(xz.x + xz.y, xz.x - xz.y) * 0.5 + 0.5;
        }
        vec3 impostorDecode(vec2 uv)
        {
            vec2 e = uv * 2.0 - 1.0;
            vec2 xz = vec2(e.x + e.y, e.x - e.y) * 0.5;
            return normalize(vec3(xz.x, 1.0 - abs(xz.x) - abs(xz.y), xz.y));
        }
        // The frame-space normal from the normal map (octahedral, full sphere).
        vec3 impostorDecodeNormal(vec2 p)
        {
            vec3 n = vec3(p, 1.0 - abs(p.x) - abs(p.y));
            float t = max(-n.z, 0.0);
            n.xy += vec2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
            return normalize(n);
        }
        // A frame's image axes; dir points from the mesh to the frame's viewer.
        void impostorBasis(vec3 dir, out vec3 right, out vec3 up)
        {
            vec3 reference = dir.y > 0.999 ? vec3(0.0, 0.0, -1.0) : vec3(0.0, 1.0, 0.0);
            right = normalize(cross(reference, dir));
            up = cross(dir, right);
        }
        // The three frames (grid cells) to blend for a view direction (object space, towards the eye) and their weights (sum 1).
        void impostorSelect(vec3 view, float grid, out vec2 a, out vec2 b, out vec2 c, out vec3 w)
        {
            vec2 g = impostorEncode(view) * (grid - 1.0);
            vec2 cell = clamp(floor(g), vec2(0.0), vec2(grid - 2.0));
            vec2 f = g - cell;
            b = cell + vec2(1.0, 0.0);
            c = cell + vec2(0.0, 1.0);
            if (f.x + f.y <= 1.0) { a = cell; w = vec3(1.0 - f.x - f.y, f.x, f.y); }
            else { a = cell + vec2(1.0); w = vec3(f.x + f.y - 1.0, 1.0 - f.y, 1.0 - f.x); }
        }
        // Where the ray (object space: origin, direction) meets the plane `height` object units in front of frame cell's plane through the
        // centre: frame-local UV (in [0, 1]² inside the frame) and the point.
        vec2 impostorFrameUv(vec3 dir, vec3 right, vec3 up, vec3 centre, float radius, vec3 origin, vec3 ray, float height, out vec3 point)
        {
            float t = (dot(centre - origin, dir) + height) / dot(ray, dir);
            point = origin + ray * t;
            vec3 local = point - centre;
            return vec2(dot(local, right), dot(local, up)) / (2.0 * radius) + 0.5;
        }
        // What the lighting needs, blended from the three frames. normal and position are in object space; coverage is the blended
        // cut-out (cut at 0.5); gloss is the gloss × specular the mesh shader passes to kenshiLight.
        struct ImpostorSurface { vec3 albedo; float coverage; vec3 normal; float gloss; vec3 position; };
        // grid: frames per atlas side; centre, radius: the baked bounding sphere (object space); origin: the eye in object space; ray: from the
        // eye to this pixel's point on the billboard (object space, any length); cells and weights from impostorSelect (constant per instance);
        // parallax: one depth step per frame before sampling (3 more fetches; better agreement between the frames).
        ImpostorSurface impostorSample(sampler2D albedoMap, sampler2D normalMap, sampler2D depthMap, float grid, vec3 centre, float radius,
                                       vec3 origin, vec3 ray, vec2 cellA, vec2 cellB, vec2 cellC, vec3 weights, bool parallax)
        {
            ImpostorSurface s = ImpostorSurface(vec3(0.0), 0.0, vec3(0.0), 0.0, vec3(0.0));
            vec2 cells[3] = vec2[3](cellA, cellB, cellC);
            float total = 0.0;
            vec3 fallback = vec3(0.0);
            for (int k = 0; k < 3; k++)
            {
                float w = weights[k];
                if (w <= 0.0) continue;   // the weights are the same over the whole billboard: uniform control flow for the derivatives
                vec3 dir = impostorDecode(cells[k] / (grid - 1.0)), right, up, point;
                impostorBasis(dir, right, up);
                vec2 local = impostorFrameUv(dir, right, up, centre, radius, origin, ray, 0.0, point);
                if (parallax)
                {
                    float h = texture(depthMap, (cells[k] + clamp(local, 0.0, 1.0)) / grid).r * 2.0 - 1.0;
                    local = impostorFrameUv(dir, right, up, centre, radius, origin, ray, h * radius, point);
                }
                vec2 uv = (cells[k] + clamp(local, 0.0, 1.0)) / grid;
                vec4 a = texture(albedoMap, uv);
                vec3 nf = impostorDecodeNormal(texture(normalMap, uv).rg * 2.0 - 1.0);
                vec2 dg = texture(depthMap, uv).rg;
                bool inside = all(greaterThanEqual(local, vec2(0.0))) && all(lessThanEqual(local, vec2(1.0)));
                float cw = w * (inside ? a.a : 0.0);
                float t = (dot(centre - origin, dir) + (dg.r * 2.0 - 1.0) * radius) / dot(ray, dir);
                s.coverage += cw;
                s.albedo += a.rgb * cw;
                s.normal += (right * nf.x + up * nf.y + dir * nf.z) * cw;
                s.gloss += dg.g * cw;
                s.position += (origin + ray * t) * cw;
                fallback += point * w;
                total += w;
            }
            if (s.coverage > 1e-5)
            {
                s.albedo /= s.coverage;
                s.gloss /= s.coverage;
                s.position /= s.coverage;
                s.normal = normalize(s.normal);
            }
            else
            {
                s.normal = -normalize(ray);
                s.position = fallback / max(total, 1e-5);
            }
            return s;
        }

        """;

    /// <summary>
    /// The instanced impostor vertex shader: six vertices per instance (<c>gl_VertexID</c>, no vertex buffer), the same per-instance matrix as
    /// <see cref="FoliageShaders.MeshVertex"/> (locations 7 to 10, row 0 w the fade). The billboard faces the eye, spans the bounding sphere's
    /// silhouette, and carries the object-space ray to the fragment shader; the three frames are chosen once per instance.
    /// </summary>
    public const string Vertex = """
        #version 330 core
        layout(location = 7) in vec4 aInstance0;
        layout(location = 8) in vec4 aInstance1;
        layout(location = 9) in vec4 aInstance2;
        layout(location = 10) in vec4 aInstance3;
        uniform mat4 uViewProjection;
        uniform vec3 uEye;
        uniform vec3 uCameraUp;          // the camera's up axis (world): the billboard's roll
        uniform vec4 uImpostor;          // xyz: the baked sphere's centre (object space), w: its radius (object units)
        uniform float uImpostorGrid;
        out vec3 vObjectPoint;
        flat out vec3 vObjectEye;
        flat out vec2 vCellA;
        flat out vec2 vCellB;
        flat out vec2 vCellC;
        flat out vec3 vWeights;
        flat out float vFade;
        flat out vec4 vModel0;
        flat out vec4 vModel1;
        flat out vec4 vModel2;
        flat out vec4 vModel3;
        """ + Functions + """
        void main()
        {
            mat4 model = mat4(vec4(aInstance0.xyz, 0.0), vec4(aInstance1.xyz, 0.0), vec4(aInstance2.xyz, 0.0), vec4(aInstance3.xyz, 1.0));
            vFade = aInstance0.w;
            mat3 basis = mat3(model);
            float scale = length(basis[0]);
            vec3 translation = model[3].xyz;
            vec3 centre = (model * vec4(uImpostor.xyz, 1.0)).xyz;
            float radius = uImpostor.w * scale;
            vec3 toEye = uEye - centre;
            float dist = max(length(toEye), radius * 1.05);
            vec3 v = normalize(toEye);
            // object space = transpose(basis) / scale² (rotation and uniform scale)
            mat3 inverse = transpose(basis) / (scale * scale);
            impostorSelect(normalize(inverse * v), uImpostorGrid, vCellA, vCellB, vCellC, vWeights);
            int corner = gl_VertexID % 6;
            int cu[6] = int[6](0, 1, 1, 0, 1, 0);
            int cv[6] = int[6](0, 0, 1, 0, 1, 1);
            vec2 c = vec2(float(cu[corner]), float(cv[corner])) * 2.0 - 1.0;
            vec3 right = normalize(cross(uCameraUp, v));
            vec3 up = cross(v, right);
            float extent = radius * dist / sqrt(dist * dist - radius * radius);   // the sphere's silhouette at the centre's plane
            vec3 p = centre + (right * c.x + up * c.y) * extent;
            vObjectPoint = inverse * (p - translation);
            vObjectEye = inverse * (uEye - translation);
            vModel0 = model[0]; vModel1 = model[1]; vModel2 = model[2]; vModel3 = model[3];
            gl_Position = uViewProjection * vec4(p, 1.0);
        }
        """;

    /// <summary>The fragment shader without a depth write (early depth testing stays on).</summary>
    public static readonly string Fragment = BuildFragment(depthWrite: false);

    /// <summary>The fragment shader writing the blended surface's depth (depth-correct intersections and shadows; turns early depth testing off).</summary>
    public static readonly string FragmentWithDepth = BuildFragment(depthWrite: true);

    static string BuildFragment(bool depthWrite) => "#version 330 core\n" + AtmosphereShaders.Functions + Functions + """
        in vec3 vObjectPoint;
        flat in vec3 vObjectEye;
        flat in vec2 vCellA;
        flat in vec2 vCellB;
        flat in vec2 vCellC;
        flat in vec3 vWeights;
        flat in float vFade;
        flat in vec4 vModel0;
        flat in vec4 vModel1;
        flat in vec4 vModel2;
        flat in vec4 vModel3;
        uniform sampler2D uImpostorAlbedo;
        uniform sampler2D uImpostorNormal;
        uniform sampler2D uImpostorDepth;
        uniform vec4 uImpostor;
        uniform float uImpostorGrid;
        uniform bool uImpostorParallax;
        uniform int uImpostorDebug;
        uniform mat4 uViewProjection;
        uniform bool uCoverage;          // alpha to coverage (multisampled target), as the foliage meshes
        uniform vec3 uLightDir;
        uniform vec3 uEye;
        uniform vec3 uFogColour;
        uniform float uFogDistance;
        out vec4 fragColour;
        float foliageDither() { return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))); }
        void main()
        {
            // The fade: as the meshes (a dither threshold, 2 = whole); negative = the complement of the mesh's dither (the crossfade).
            float dither = foliageDither();
            if (vFade < 0.0 ? dither < -vFade : (vFade < 1.0 && dither >= vFade)) discard;
            ImpostorSurface s = impostorSample(uImpostorAlbedo, uImpostorNormal, uImpostorDepth, uImpostorGrid, uImpostor.xyz, uImpostor.w,
                vObjectEye, vObjectPoint - vObjectEye, vCellA, vCellB, vCellC, vWeights, uImpostorParallax);
            if (uImpostorDebug == 1) { vec3 dA = impostorDecode(vCellA / (uImpostorGrid - 1.0)), rA, uA, pA; impostorBasis(dA, rA, uA); vec2 lA = impostorFrameUv(dA, rA, uA, uImpostor.xyz, uImpostor.w, vObjectEye, vObjectPoint - vObjectEye, 0.0, pA); fragColour = vec4(lA, vCellA.x / 11.0, 1.0); return; }
            if (uImpostorDebug >= 3 && uImpostorDebug < 10) { vec3 dA = impostorDecode(vCellA / (uImpostorGrid - 1.0)), rA, uA, pA; impostorBasis(dA, rA, uA); vec2 lA = impostorFrameUv(dA, rA, uA, uImpostor.xyz, uImpostor.w, vObjectEye, vObjectPoint - vObjectEye, 0.0, pA); vec4 t = textureLod(uImpostorAlbedo, (vCellA + clamp(lA, 0.0, 1.0)) / uImpostorGrid, float(uImpostorDebug - 3)); fragColour = vec4(t.rgb * t.a, 1.0); return; }
            if (uImpostorDebug == 2) { fragColour = vec4(vec3(s.coverage), 1.0); return; }
            if (uImpostorDebug == 10 && s.coverage >= 0.5) { fragColour = vec4(s.albedo, 1.0); return; }
            if (uImpostorDebug == 11 && s.coverage >= 0.5) { fragColour = vec4(s.normal * 0.5 + 0.5, 1.0); return; }
            float coverage = 1.0;
            if (uCoverage)
            {
                coverage = clamp((s.coverage - 0.5) / max(fwidth(s.coverage), 1e-4) + 0.5, 0.0, 1.0);
                if (coverage <= 0.0) discard;
            }
            else if (s.coverage < 0.5) discard;
            mat4 model = mat4(vModel0, vModel1, vModel2, vModel3);
            vec3 world = (model * vec4(s.position, 1.0)).xyz;
            vec3 n = normalize(mat3(model) * s.normal);
            float gloss = s.gloss;
            // The mesh shader's lighting (Shaders.MeshFragment), from the same inputs.
            vec3 l = normalize(uLightDir);
            vec3 v = normalize(uEye - world);
            float diff = max(dot(n, l), 0.0);
            float hemi = 0.5 + 0.5 * n.y;
            vec3 ambient = mix(vec3(0.22, 0.20, 0.18), vec3(0.42, 0.45, 0.50), hemi);
            vec3 h = normalize(l + v);
            float spec = pow(max(dot(n, h), 0.0), 8.0 + 56.0 * gloss) * gloss * 0.5;
            vec3 sunLight = vec3(1.0, 0.97, 0.92);
            vec3 colour = s.albedo * (ambient + diff * sunLight) + spec * diff * sunLight;
            if (uFogDistance > 0.0 && uAtmoParams.x > 0.5) colour = kenshiLight(s.albedo, n, v, gloss, world);
            if (uFogDistance > 0.0) colour = atmoApply(colour, uEye, world);
            fragColour = vec4(colour, coverage);
        """ + (depthWrite ? """
            vec4 clip = uViewProjection * vec4(world, 1.0);
            gl_FragDepth = clamp(clip.z / clip.w * 0.5 + 0.5, 0.0, 1.0);
        """ : "") + """
        }
        """;

    /// <summary>
    /// The bake: <see cref="Shaders.MeshFragment"/> with an output switch inserted after the normal mapping and before the lighting, so the
    /// albedo, normal and gloss are exactly what the mesh shader would light. Pass 0 writes the albedo, 1 the normal in the frame's basis
    /// (× 0.5 + 0.5), 2 the depth along the frame direction and the gloss × specular; alpha 1 wherever the cut-out keeps the pixel.
    /// </summary>
    public static string BakeFragment()
    {
        string f = Shaders.MeshFragment;
        f = Replace(f, @"#version\s+330\s+core", """
            #version 330 core
            uniform int uImpostorPass;
            uniform vec4 uImpostorSphere;     // centre, radius (object space = world space while baking)
            uniform vec3 uImpostorDir;
            uniform vec3 uImpostorRight;
            uniform vec3 uImpostorUp;
            """);
        f = Replace(f, @"vec3\s+l\s*=\s*normalize\s*\(\s*uLightDir\s*\)\s*;", """
            if (uImpostorPass == 0) { fragColour = vec4(albedo, 1.0); return; }
            if (uImpostorPass == 1) { fragColour = vec4(vec3(dot(n, uImpostorRight), dot(n, uImpostorUp), dot(n, uImpostorDir)) * 0.5 + 0.5, 1.0); return; }
            if (uImpostorPass == 2)
            {
                float depth = dot(vWorld - uImpostorSphere.xyz, uImpostorDir) / uImpostorSphere.w;
                fragColour = vec4(clamp(depth * 0.5 + 0.5, 0.0, 1.0), clamp(gloss * uSpecular, 0.0, 1.0), 0.0, 1.0);
                return;
            }
            vec3 l = normalize(uLightDir);
            """);
        return f;
    }

    static string Replace(string source, string pattern, string replacement)
    {
        var regex = new Regex(pattern);
        if (!regex.IsMatch(source)) throw new InvalidOperationException($"ImpostorShaders: '{pattern}' not found in the shared mesh shader; update the patch.");
        return regex.Replace(source, _ => replacement, 1);
    }
}
