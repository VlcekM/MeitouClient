using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Meitou.Rendering.Impostors;

/// <summary>The C# side of <see cref="ImpostorShaders.PushMembers"/> (std430 push constants, 80 bytes). GLSL bools are 32-bit (0 / 1).</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct ImpostorPush
{
    /// <summary>The baked bounding sphere (object space): centre, radius.</summary>
    [FieldOffset(0)] public Vector4 Sphere;
    /// <summary>The camera's up axis (world): the billboards' roll.</summary>
    [FieldOffset(16)] public Vector3 CameraUp;
    [FieldOffset(28)] public float Grid;
    /// <summary>The atlas maps' bindless indices (2D float array).</summary>
    [FieldOffset(32)] public uint Albedo;
    [FieldOffset(36)] public uint Normal;
    /// <summary>The atlas's gloss x specular (<see cref="ImpostorAtlas.Gloss"/>), what the mesh passes to the lighting.</summary>
    [FieldOffset(40)] public float Gloss;
    [FieldOffset(44)] public uint Spare2;
    [FieldOffset(48)] public uint Blend;
    [FieldOffset(52)] public int Debug;
    [FieldOffset(56)] public uint Coverage;
    [FieldOffset(60)] public uint Spare;
    /// <summary>A directional view (w 1, a shadow cascade): the direction towards its viewer; w 0 uses the view's eye.</summary>
    [FieldOffset(64)] public Vector4 View;
}

/// <summary>The C# side of <see cref="ImpostorShaders.RockPushMembers"/> (std430 push constants, 128 bytes): <see cref="ImpostorPush"/> and the terrain's far-fade inputs.</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct ImpostorRockPush
{
    [FieldOffset(0)] public Vector4 Sphere;
    [FieldOffset(16)] public Vector3 CameraUp;
    [FieldOffset(28)] public float Grid;
    [FieldOffset(32)] public uint Albedo;
    [FieldOffset(36)] public uint Normal;
    [FieldOffset(40)] public float Gloss;
    [FieldOffset(44)] public uint Spare2;
    [FieldOffset(48)] public uint Blend;
    [FieldOffset(52)] public int Debug;
    [FieldOffset(56)] public uint Coverage;
    [FieldOffset(60)] public uint Spare;
    [FieldOffset(64)] public Vector4 View;
    /// <summary>The colour-map window (x0, z0, 1/width, 1/depth), the bindless indices of the ground, whole-world colour and colour maps.</summary>
    [FieldOffset(80)] public Vector4 Region;
    [FieldOffset(96)] public uint Ground;
    [FieldOffset(100)] public uint WorldColour;
    [FieldOffset(104)] public uint Colour;
    [FieldOffset(108)] public float HalfWorld;
    [FieldOffset(112)] public float FarStart;
    [FieldOffset(116)] public float FarEnd;
    /// <summary>Bit 0 ground map, 1 whole-world colour map, 2 colour map, 3 apply the colour map's tint.</summary>
    [FieldOffset(120)] public uint Flags;
}

/// <summary>
/// GLSL of the impostors (docs/impostors.md):
/// <list type="bullet">
/// <item><see cref="Functions"/>: the sampling API for any impostor shader (the hemi-octahedral map, frame selection, the three-frame blend
/// with per-pixel frame-plane projection) returning the surface the lighting needs.</item>
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
        """;

    /// <summary>
    /// The fragment-stage sampling (<see cref="Functions"/> first): <c>impostorSample</c> returns the surface the lighting needs, either from
    /// one of the three frames chosen per pixel by the weights (<c>pick</c> in [0, 1), a dither value: crisp cut-outs, the frames interleave and
    /// temporal anti-aliasing averages them) or the three blended by their weights (<c>pick</c> &lt; 0: smooth, but leaves and branches that do
    /// not line up between the frames thin out).
    /// </summary>
    public const string FragmentFunctions = """

        // What the lighting needs. normal and position are in object space; coverage is the cut-out (cut at 0.5); the albedo is the colour
        // of the covered texels (the BC1 atlas is premultiplied by the sampler's filtering: transparent texels decode to black, alpha 0).
        struct ImpostorSurface { vec3 albedo; float coverage; vec3 normal; vec3 position; };
        // grid: frames per atlas side; centre, radius: the baked bounding sphere (object space); origin: the eye in object space; ray: from the
        // eye to this pixel's point on the billboard (object space, any length); cells and weights from impostorSelect (constant per instance);
        // pick: a dither value in [0, 1) to use one frame, or < 0 to blend the three. The position is on the frame's plane through the sphere's
        // centre (the atlas has no depth).
        ImpostorSurface impostorSample(sampler2D albedoMap, sampler2D normalMap, float grid, vec3 centre, float radius,
                                       vec3 origin, vec3 ray, vec2 cellA, vec2 cellB, vec2 cellC, vec3 weights, float pick)
        {
            ImpostorSurface s = ImpostorSurface(vec3(0.0), 0.0, vec3(0.0), vec3(0.0));
            vec2 cells[3] = vec2[3](cellA, cellB, cellC);
            // One set of texture gradients for every fetch, from frame A's projection (the frames are a grid step apart, their scales
            // agree): the fetches below are in non-uniform control flow when frames are picked per pixel.
            vec3 dirA = impostorDecode(cellA / (grid - 1.0)), rightA, upA, pointA;
            impostorBasis(dirA, rightA, upA);
            vec2 localA = impostorFrameUv(dirA, rightA, upA, centre, radius, origin, ray, 0.0, pointA) / grid;
            vec2 gx = dFdx(localA), gy = dFdy(localA);
            // pick >= 2 (the shadow casters) is the plain pick (pick - 2) of one frame per texel, without the vote below.
            bool plain = pick >= 2.0;
            if (plain) pick -= 2.0;
            if (pick >= 0.0 && !plain)
            {
                // Silhouette from the vote, colour from one frame. The cut-out is the three frames' coverages blended by their weights
                // (constant over the instance, smooth in space: a clean edge, and no holes where the frame a dither value picked has none
                // at this point, the dots at the edge of trunks and bulbs whose parts lie far in front of or behind the crown's centre,
                // where the frames' parallax shifts their edges by pixels). The colour comes from one frame, picked by the dither
                // value among the frames that cover the point (weights x coverage), so a texel is never shaded black (crisp leaves).
                vec4 av[3];
                vec3 dv[3], rv[3], uv3[3], pv[3];
                vec2 tv[3];
                float cw[3];
                float vote = 0.0;
                for (int k = 0; k < 3; k++)
                {
                    cw[k] = 0.0;
                    float w = weights[k];
                    dv[k] = impostorDecode(cells[k] / (grid - 1.0));
                    impostorBasis(dv[k], rv[k], uv3[k]);
                    vec2 local = impostorFrameUv(dv[k], rv[k], uv3[k], centre, radius, origin, ray, 0.0, pv[k]);
                    tv[k] = (cells[k] + clamp(local, 0.0, 1.0)) / grid;
                    av[k] = vec4(0.0);
                    if (w <= 0.0) continue;
                    bool inside = all(greaterThanEqual(local, vec2(0.0))) && all(lessThanEqual(local, vec2(1.0)));
                    if (!inside) continue;
                    av[k] = textureGrad(albedoMap, tv[k], gx, gy);
                    cw[k] = w * av[k].a;
                    vote += cw[k];
                }
                s.coverage = vote;
                if (vote > 1e-5)
                {
                    float t = pick * vote;
                    int chosen = 2;
                    float acc = 0.0;
                    for (int k = 0; k < 3; k++)
                    {
                        acc += cw[k];
                        if (cw[k] > 0.0 && t < acc) { chosen = k; break; }
                        if (cw[k] > 0.0) chosen = k;
                    }
                    // The normal and the position are the covering frames' blend: a curved surface seen by frames a few degrees apart
                    // has different normals at the points they put under this pixel, and picking among them dithered the shading.
                    vec3 normal = vec3(0.0), position = vec3(0.0);
                    for (int k = 0; k < 3; k++)
                    {
                        if (cw[k] <= 0.0) continue;
                        vec3 nf = impostorDecodeNormal(textureGrad(normalMap, tv[k], gx, gy).rg * 2.0 - 1.0);
                        normal += (rv[k] * nf.x + uv3[k] * nf.y + dv[k] * nf.z) * cw[k];
                        position += pv[k] * cw[k];
                    }
                    s.albedo = av[chosen].rgb / max(av[chosen].a, 1e-4);
                    s.normal = normalize(normal);
                    s.position = position / vote;
                }
                else
                {
                    s.normal = -normalize(ray);
                    s.position = (pv[0] * weights.x + pv[1] * weights.y + pv[2] * weights.z) / max(weights.x + weights.y + weights.z, 1e-5);
                }
                return s;
            }
            if (pick >= 0.0)
            {
                // The frame whose cumulative weight passes the dither value.
                weights = pick < weights.x ? vec3(1.0, 0.0, 0.0) : pick < weights.x + weights.y ? vec3(0.0, 1.0, 0.0) : vec3(0.0, 0.0, 1.0);
            }
            vec3 colour = vec3(0.0);
            float total = 0.0;
            vec3 fallback = vec3(0.0);
            for (int k = 0; k < 3; k++)
            {
                float w = weights[k];
                if (w <= 0.0) continue;
                vec3 dir = impostorDecode(cells[k] / (grid - 1.0)), right, up, point;
                impostorBasis(dir, right, up);
                vec2 local = impostorFrameUv(dir, right, up, centre, radius, origin, ray, 0.0, point);
                vec2 uv = (cells[k] + clamp(local, 0.0, 1.0)) / grid;
                vec4 a = textureGrad(albedoMap, uv, gx, gy);
                vec3 nf = impostorDecodeNormal(textureGrad(normalMap, uv, gx, gy).rg * 2.0 - 1.0);
                bool inside = all(greaterThanEqual(local, vec2(0.0))) && all(lessThanEqual(local, vec2(1.0)));
                float cw = w * (inside ? a.a : 0.0);
                s.coverage += cw;
                if (inside) colour += a.rgb * w;
                s.normal += (right * nf.x + up * nf.y + dir * nf.z) * cw;
                s.position += point * cw;
                fallback += point * w;
                total += w;
            }
            if (s.coverage > 1e-5)
            {
                s.albedo = colour / s.coverage;
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
        uniform vec4 uImpostorView;      // w 1: a directional view (a shadow cascade), xyz towards its viewer (world); w 0: uEye
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
            // A directional view looks along one direction: its eye stands far out along it (the rays are nearly parallel).
            vec3 eye = uImpostorView.w > 0.5 ? centre + normalize(uImpostorView.xyz) * (1000.0 * radius) : uEye;
            vec3 toEye = eye - centre;
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
            vObjectEye = inverse * (eye - translation);
            vModel0 = model[0]; vModel1 = model[1]; vModel2 = model[2]; vModel3 = model[3];
            gl_Position = uViewProjection * vec4(p, 1.0);
        }
        """;


    /// <summary>
    /// The fragment shader (early depth testing stays on: the quad's own depth is written, the atlas has no depth). The mesh shader's lighting
    /// on the sampled surface, the same fade dither as the meshes (complementary in the crossfade band), and one frame of the three per pixel by a
    /// noise independent of the fade's (or the three blended with <c>uImpostorBlend</c>).
    /// </summary>
    public static readonly string Fragment = "#version 330 core\n" + AtmosphereShaders.Functions + Functions + FragmentFunctions + """
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
        uniform vec4 uImpostor;
        uniform float uImpostorGrid;
        uniform float uImpostorGloss;
        uniform bool uImpostorBlend;     // blend the three frames instead of picking one per pixel
        uniform int uImpostorDebug;      // 1: albedo, 2: normal (object space), 3: coverage, unlit
        uniform bool uCoverage;          // alpha to coverage (multisampled target), as the foliage meshes
        uniform vec3 uLightDir;
        uniform vec3 uEye;
        uniform vec3 uFogColour;
        uniform float uFogDistance;
        out vec4 fragColour;
        float foliageDither() { return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))); }
        float framePick() { return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.00583715, 0.06711056)))); }
        void main()
        {
            // The fade: as the meshes (a dither threshold, 2 = whole); negative = the complement of the mesh's dither (the crossfade).
            float dither = foliageDither();
            if (vFade < 0.0 ? dither < -vFade : (vFade < 1.0 && dither >= vFade)) discard;
            float pick = uImpostorBlend ? -1.0 : framePick();
            ImpostorSurface s = impostorSample(uImpostorAlbedo, uImpostorNormal, uImpostorGrid, uImpostor.xyz, uImpostor.w,
                vObjectEye, vObjectPoint - vObjectEye, vCellA, vCellB, vCellC, vWeights, pick);
            if (uImpostorDebug == 3) { fragColour = vec4(vec3(s.coverage), 1.0); return; }
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
            float gloss = uImpostorGloss;
            if (uImpostorDebug == 1) { fragColour = vec4(s.albedo, 1.0); return; }
            if (uImpostorDebug == 2) { fragColour = vec4(n * 0.5 + 0.5, 1.0); return; }
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
        }
        """;

    /// <summary>
    /// The impostor as a shadow caster (a cascade's depth-only pass, <see cref="Vertex"/> with a directional <c>uImpostorView</c>): one frame per
    /// texel (the pick noise, no fade: casters ignore the distance fade as the meshes' depth pass does), cut at coverage 0.5, the depth of the
    /// frame's plane through the crown's centre with the game's caster bias (<see cref="ShadowShaders"/>' <c>shadowWriteDepth</c>, on that depth).
    /// </summary>
    public static readonly string DepthFragment = "#version 330 core\n" + Functions + FragmentFunctions + $$"""
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
        uniform vec4 uImpostor;
        uniform float uImpostorGrid;
        uniform mat4 uViewProjection;
        layout(std140) uniform {{ShadowShaders.CasterBlock}}
        {
            vec4 uShadowBias;   // x fixed, y slope, z max slope (depth units)
        };
        void main()
        {
            float pick = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.00583715, 0.06711056))));
            ImpostorSurface s = impostorSample(uImpostorAlbedo, uImpostorNormal, uImpostorGrid, uImpostor.xyz, uImpostor.w,
                vObjectEye, vObjectPoint - vObjectEye, vCellA, vCellB, vCellC, vWeights, pick + 2.0);   // + 2: the plain pick, see impostorSample
            if (s.coverage < 0.5) discard;
            mat4 model = mat4(vModel0, vModel1, vModel2, vModel3);
            vec4 clip = uViewProjection * vec4((model * vec4(s.position, 1.0)).xyz, 1.0);
            float z = clip.z / clip.w;
            float g = length(vec2(dFdx(z), dFdy(z)));
            gl_FragDepth = clamp(z + min(uShadowBias.z, uShadowBias.y * g) + uShadowBias.x, 0.0, 1.0);
        }
        """;

    /// <summary><see cref="DepthFragment"/> in the native model.</summary>
    public static string DepthFragmentNative() => NativeShaders.Port(DepthFragment, NativeShaders.Map(NativeMap), PushMembers);

    /// <summary>
    /// The bake (native model, docs/renderer-native.md 3.3): <see cref="Shaders.MeshFragment"/> with an output switch inserted after the normal
    /// mapping and before the lighting, so the albedo, normal and gloss are exactly what the mesh shader would light. Pass 0 writes the albedo,
    /// 1 the normal in the frame's basis (× 0.5 + 0.5), 2 the depth along the frame direction and the gloss × specular; alpha 1 wherever the
    /// cut-out keeps the pixel. The frame's values come from a storage block at set 0, binding 6 (<see cref="BakeBlock"/>, the baker's
    /// <c>NativeFrame</c> extra binding), since the mesh shader's push block is full.
    /// </summary>
    public static string BakeFragmentNative()
    {
        string f = Shaders.MeshFragment;
        f = Replace(f, @"#version\s+330\s+core", """
            #version 330 core
            layout(std430, set = 0, binding = 6) readonly buffer ImpostorBake { vec4 sphere; vec4 dir; vec4 right; vec4 up; ivec4 pass; } impostorBake;
            #define uImpostorPass impostorBake.pass.x
            #define uImpostorSphere impostorBake.sphere
            #define uImpostorDir impostorBake.dir.xyz
            #define uImpostorRight impostorBake.right.xyz
            #define uImpostorUp impostorBake.up.xyz
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
        return NativeShaders.Port(f);
    }

    /// <summary>The bake's vertex shader: <see cref="Shaders.MeshVertex"/> in the native model with the identity model matrix (object space is
    /// world space while baking).</summary>
    public static string BakeVertexNative() => NativeShaders.MeshVertex(new Dictionary<string, string> { ["uModel"] = "mat4(1.0)" });

    /// <summary>The C# side of the bake's storage block (<see cref="BakeFragmentNative"/>, std430, 80 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BakeBlock
    {
        public Vector4 Sphere, Dir, Right, Up;
        public int Pass, Pad1, Pad2, Pad3;
    }

    // ---- the impostor program in the native model (docs/renderer-native.md 3.3): Vertex and Fragment through NativeShaders.Port ----

    /// <summary>The impostor program's push constants (<see cref="ImpostorPush"/> is the C# side; std430, 80 bytes).</summary>
    public const string PushMembers = """
            vec4 sphere;
            vec3 cameraUp;
            float grid;
            uint albedo;
            uint normal;
            float gloss;
            uint spare2;
            bool blend;
            int debug;
            bool coverage;
            uint spare;
            vec4 view;
        """;

    /// <summary>The impostor program's own uniforms: <see cref="ImpostorPush"/> members, the atlas maps by their bindless index.</summary>
    static readonly Dictionary<string, string> NativeMap = new()
    {
        ["uImpostor"] = "pc.sphere", ["uCameraUp"] = "pc.cameraUp", ["uImpostorGrid"] = "pc.grid",
        ["uImpostorAlbedo"] = "textures2D[pc.albedo]", ["uImpostorNormal"] = "textures2D[pc.normal]", ["uImpostorGloss"] = "pc.gloss",
        ["uImpostorBlend"] = "pc.blend", ["uImpostorDebug"] = "pc.debug", ["uCoverage"] = "pc.coverage",
        ["uImpostorView"] = "pc.view",
    };

    /// <summary><see cref="Vertex"/> in the native model.</summary>
    public static string VertexNative() => NativeShaders.Port(Vertex, NativeShaders.Map(NativeMap), PushMembers);

    /// <summary><see cref="Fragment"/> in the native model.</summary>
    public static string FragmentNative() => NativeShaders.Port(Fragment, NativeShaders.Map(NativeMap), PushMembers);

    // ---- TERRAIN-mode rocks (docs/impostors.md section 12) ----

    /// <summary><see cref="PushMembers"/> and what a rock's impostor needs of the terrain (<see cref="ImpostorRockPush"/> is the C# side; offsets 80 to 123): the
    /// colour-map window, the bindless indices of the ground, whole-world colour and colour maps, the world's half size, the distances the material fades to the
    /// ground colour between, and which maps exist (bit 0 ground, 1 whole-world colour, 2 colour map).</summary>
    public const string RockPushMembers = PushMembers + """

            vec4 rockRegion;
            uint rockGround;
            uint rockWorldColour;
            uint rockColour;
            float rockHalfWorld;
            float rockFarStart;
            float rockFarEnd;
            uint rockFlags;
        """;

    static readonly Dictionary<string, string> RockNativeMap = new(NativeMap)
    {
        ["uRockGround"] = "textures2D[pc.rockGround]", ["uRockWorldColour"] = "textures2D[pc.rockWorldColour]", ["uRockColour"] = "textures2D[pc.rockColour]",
        ["uRockRegion"] = "pc.rockRegion", ["uRockHalfWorld"] = "pc.rockHalfWorld", ["uRockFarStart"] = "pc.rockFarStart", ["uRockFarEnd"] = "pc.rockFarEnd",
        ["uRockFlags"] = "pc.rockFlags",
    };

    /// <summary>
    /// <see cref="Fragment"/> for a TERRAIN-mode rock: what the terrain's mesh shader does with distance that the atlas (baked with the material fully applied, at the
    /// transition distance) does not hold. The material fades to the biomes' ground colour between the material distance's 80% and 100% (the ground and
    /// whole-world colour maps, as <c>TerrainShaders.Fragment</c> does it), per pixel from the eye's distance, before the lighting.
    /// </summary>
    public static string RockFragmentNative()
    {
        string f = Fragment;
        f = Replace(f, @"uniform\s+vec3\s+uLightDir\s*;", """
            uniform vec3 uLightDir;
            uniform sampler2D uRockGround;
            uniform sampler2D uRockWorldColour;
            uniform sampler2D uRockColour;
            uniform vec4 uRockRegion;
            uniform float uRockHalfWorld;
            uniform float uRockFarStart;
            uniform float uRockFarEnd;
            uniform uint uRockFlags;
            """);
        f = Replace(f, @"float\s+gloss\s*=\s*uImpostorGloss\s*;", """
            float gloss = uImpostorGloss;
            {
                vec2 world01 = (world.xz + uRockHalfWorld) / (2.0 * uRockHalfWorld);
                vec2 gx = dFdx(world01), gy = dFdy(world01);
                float nw = 1.0 - smoothstep(uRockFarStart, uRockFarEnd, length(world - uEye));
                if ((uRockFlags & 4u) != 0u && (uRockFlags & 8u) != 0u && nw > 0.0)
                {
                    vec2 cuv = fract((world.xz + uRockHalfWorld) * uRockRegion.zw);
                    s.albedo *= textureGrad(uRockColour, cuv, gx * uRockRegion.zw * 2.0 * uRockHalfWorld, gy * uRockRegion.zw * 2.0 * uRockHalfWorld).rgb * 1.2;
                }
                if (nw < 1.0 && (uRockFlags & 1u) != 0u)
                {
                    vec3 far = textureGrad(uRockGround, world01, gx, gy).rgb;
                    if ((uRockFlags & 2u) != 0u) far *= textureGrad(uRockWorldColour, world01, gx, gy).rgb * 1.2;
                    s.albedo = mix(far, s.albedo, nw);
                    gloss = mix(0.2, gloss, nw);
                }
            }
            """);
        return NativeShaders.Port(f, NativeShaders.Map(RockNativeMap), RockPushMembers);
    }

    /// <summary>The rock program's vertex stage: the impostor quad (only <see cref="PushMembers"/> are read; the fragment stage's block is the larger one).</summary>
    public static string RockVertexNative() => VertexNative();

    static string Replace(string source, string pattern, string replacement)
    {
        var regex = new Regex(pattern);
        if (!regex.IsMatch(source)) throw new InvalidOperationException($"ImpostorShaders: '{pattern}' not found in the shared mesh shader; update the patch.");
        return regex.Replace(source, _ => replacement, 1);
    }
}
