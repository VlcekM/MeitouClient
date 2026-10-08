using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Meitou.Rendering;

/// <summary>
/// The foliage shaders (docs/viewer.md, "Foliage").
/// <list type="bullet">
/// <item>Meshes (trees, bushes, rocks): the viewer's shared mesh shaders (<see cref="Shaders"/>, so lighting and the atmosphere
/// follow whatever is added there) given a per-instance model matrix (attributes 7 to 10, divisor 1) whose row 0 w carries the
/// distance fade (a dither threshold, 2 = fully visible), and an alpha-to-coverage output for the cut-out leaves.</item>
/// <item>Grass: quads built from <c>gl_VertexID</c> around per-blade instances, with Kenshi's grass shader rules (foliage.hlsl):
/// the top edge sways along X by <c>sin(time + x × frequency)</c>, the blade sinks into the ground over the last fifth of its
/// range, the cut-out is diffuse alpha 0.6, the colour is the sprite times the colour map, the normal is straight up.</item>
/// </list>
/// </summary>
static class FoliageShaders
{
    public const int InstanceLocation = 7;

    public static string MeshVertex()
    {
        string v = Shaders.MeshVertex;
        v = Replace(v, @"uniform\s+mat4\s+uModel\s*;", """
            layout(location = 7) in vec4 aInstance0;
            layout(location = 8) in vec4 aInstance1;
            layout(location = 9) in vec4 aInstance2;
            layout(location = 10) in vec4 aInstance3;
            out float vFade;
            #define uModel mat4(vec4(aInstance0.xyz, 0.0), vec4(aInstance1.xyz, 0.0), vec4(aInstance2.xyz, 0.0), vec4(aInstance3.xyz, 1.0))
            """, required: true);
        v = Replace(v, @"void\s+main\s*\(\s*\)\s*\{", """
            void main()
            {
                vFade = aInstance0.w;
            """, required: true);
        return v;
    }

    public static string MeshFragment()
    {
        string f = Shaders.MeshFragment;
        f = Replace(f, @"#version\s+330\s+core", """
            #version 330 core
            in float vFade;
            uniform bool uCoverage;      // alpha to coverage (multisampled target): the cut-out edge becomes the coverage
            float foliageDither() { return fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))); }
            """, required: true);
        f = Replace(f, @"void\s+main\s*\(\s*\)\s*\{", """
            void main()
            {
                if (vFade < 1.0 && foliageDither() >= vFade) discard;
            """, required: true);
        // The cut-out: a hard test, or with alpha to coverage a one-pixel ramp around the threshold.
        f = Replace(f, @"if\s*\(\s*uAlphaThreshold\s*>\s*0\.0\s*&&\s*alpha\s*<\s*uAlphaThreshold\s*\)\s*discard\s*;", """
            float foliageCoverage = 1.0;
            if (uAlphaThreshold > 0.0)
            {
                if (uCoverage)
                {
                    foliageCoverage = clamp((alpha - uAlphaThreshold) / max(fwidth(alpha), 1e-4) + 0.5, 0.0, 1.0);
                    if (foliageCoverage <= 0.0) discard;
                }
                else if (alpha < uAlphaThreshold) discard;
            }
            """, required: true);
        f = Replace(f, @"fragColour\s*=\s*vec4\s*\(\s*colour\s*,\s*1\.0\s*\)\s*;", "fragColour = vec4(colour, foliageCoverage);", required: false);
        return f;
    }

    /// <remarks>The aerial perspective is linear in the colour (<c>atmoApply</c> mixes), so it is evaluated per vertex as a multiply and an add: grass covers much of the screen.</remarks>
    public static readonly string GrassVertex = "#version 330 core\n" + AtmosphereShaders.Functions + """
        layout(location = 0) in vec4 aBlade;     // x, y, z, scale 0..1
        layout(location = 1) in float aYaw;
        uniform mat4 uViewProjection;
        uniform vec4 uSize;                      // min width, max width, min height, max height
        uniform bool uCross;                     // CROSSQUADS: two quads at right angles
        uniform float uTime;                     // sway phase (radians)
        uniform float uFrequency;
        uniform float uSway;                     // sway amplitude along X (units)
        uniform vec3 uEye;
        uniform float uRange;                    // the grass layer's range
        uniform float uFogDistance;
        out vec3 vHazeMul;
        out vec3 vHazeAdd;
        out vec3 vWorld;
        out vec2 vUv;
        out float vFade;
        void main()
        {
            int quad = gl_VertexID / 6, corner = gl_VertexID % 6;
            // two triangles: (0,0) (1,0) (1,1)  (0,0) (1,1) (0,1); u along the quad, v = 1 at the ground, 0 at the top
            int cu[6] = int[6](0, 1, 1, 0, 1, 0);
            int cv[6] = int[6](1, 1, 0, 1, 0, 0);
            float s = aBlade.w;
            float width = mix(uSize.x, uSize.y, s), height = mix(uSize.z, uSize.w, s);
            float yaw = aYaw + (quad == 1 ? 1.5707963 : 0.0);
            vec3 side = vec3(cos(yaw), 0.0, sin(yaw));
            float u = float(cu[corner]), v = float(cv[corner]);
            vec3 p = aBlade.xyz + side * (u - 0.5) * width + vec3(0.0, (1.0 - v) * height, 0.0);
            if (v == 0.0) p.x += uSway * sin(uTime + aBlade.x * uFrequency);
            float dist = distance(uEye.xz, aBlade.xz);
            p.y -= height * clamp(5.0 * dist / uRange - 4.0, 0.0, 1.0);   // grows out of the ground over the last fifth of the range
            vWorld = p;
            vUv = vec2(u, v);
            vFade = 1.0;
            gl_Position = uViewProjection * vec4(p, 1.0);
            vHazeMul = vec3(1.0);
            vHazeAdd = vec3(0.0);
            if (uFogDistance > 0.0)
            {
                vHazeAdd = atmoApply(vec3(0.0), uEye, p);
                vHazeMul = atmoApply(vec3(1.0), uEye, p) - vHazeAdd;
            }
        }
        """;

    public static readonly string GrassFragment = "#version 330 core\n" + AtmosphereShaders.Functions + """
        in vec3 vWorld;
        in vec2 vUv;
        in float vFade;
        in vec3 vHazeMul;
        in vec3 vHazeAdd;
        uniform sampler2D uSprite;
        uniform sampler2D uColourMap;
        uniform bool uHasColourMap;
        uniform vec4 uColourBounds;      // x0, z0, x1, z1: the colour map is stretched over the zone
        uniform bool uCoverage;
        uniform vec3 uLightDir;
        uniform vec3 uEye;
        uniform vec3 uFogColour;
        uniform float uFogDistance;
        uniform bool uWireframe;
        out vec4 fragColour;
        void main()
        {
            if (uWireframe) { fragColour = vec4(0.3, 0.9, 0.3, 1.0); return; }
            vec4 d = texture(uSprite, vUv);
            const float threshold = 0.6;
            float coverage = 1.0;
            if (uCoverage)
            {
                coverage = clamp((d.a - threshold) / max(fwidth(d.a), 1e-4) + 0.5, 0.0, 1.0);
                if (coverage <= 0.0) discard;
            }
            else if (d.a < threshold) discard;
            vec3 albedo = d.rgb;
            if (uHasColourMap)
            {
                vec2 cuv = (vWorld.xz - uColourBounds.xy) / (uColourBounds.zw - uColourBounds.xy);
                albedo *= texture(uColourMap, cuv).rgb;
            }
            // The weather's wetness (foliage.hlsl: gloss 0, then makeWet with the absorbance 0.9; the gloss is written × 0.6).
            float grassGloss = 0.0;
            if (uWeatherWet.x > 0.0)
            {
                vec4 wetSurface = vec4(albedo, 0.0);
                makeWet(wetSurface, uWeatherWet.x, 0.9, -1.0e4, 0.5);
                albedo = wetSurface.rgb;
                grassGloss = wetSurface.a * 0.6;
            }
            // Lit as the shared mesh shader lights a surface facing straight up (foliage.hlsl writes the normal (0, 1, 0)).
            vec3 l = normalize(uLightDir);
            float diff = max(l.y, 0.0);
            vec3 ambient = vec3(0.42, 0.45, 0.50);
            vec3 sunLight = vec3(1.0, 0.97, 0.92);
            vec3 colour = albedo * (ambient + diff * sunLight);
            // World view, game sky: the game's deferred lighting of what foliage.hlsl writes for grass: normal straight up, gloss 0 (grass_fs zeroes the
            // diffuse alpha before writing it × 0.6; only rain would raise it).
            if (uFogDistance > 0.0 && uAtmoParams.x > 0.5) colour = kenshiLight(albedo, vec3(0.0, 1.0, 0.0), normalize(uEye - vWorld), grassGloss, vWorld);
            colour = colour * vHazeMul + vHazeAdd;
            fragColour = vec4(colour, coverage);
        }
        """;

    /// <summary>
    /// The swaying grass's own motion for the upscalers: the blades placed as <see cref="GrassVertex"/> places them, now and with last frame's
    /// sway phase and camera; the clip positions go to <see cref="GrassMotionFragment"/>.
    /// </summary>
    public const string GrassMotionVertex = """
        #version 330 core
        layout(location = 0) in vec4 aBlade;
        layout(location = 1) in float aYaw;
        uniform mat4 uViewProjection;            // this frame's, jittered (as the grass was drawn)
        uniform mat4 uPreviousViewProjection;    // last frame's, unjittered
        uniform vec4 uSize;
        uniform float uTime, uPreviousTime, uFrequency, uSway, uRange;
        uniform vec3 uEye;
        out vec2 vUv;
        out vec4 vNow;
        out vec4 vPrevious;
        void main()
        {
            int quad = gl_VertexID / 6, corner = gl_VertexID % 6;
            int cu[6] = int[6](0, 1, 1, 0, 1, 0);
            int cv[6] = int[6](1, 1, 0, 1, 0, 0);
            float s = aBlade.w;
            float width = mix(uSize.x, uSize.y, s), height = mix(uSize.z, uSize.w, s);
            float yaw = aYaw + (quad == 1 ? 1.5707963 : 0.0);
            vec3 side = vec3(cos(yaw), 0.0, sin(yaw));
            float u = float(cu[corner]), v = float(cv[corner]);
            vec3 p = aBlade.xyz + side * (u - 0.5) * width + vec3(0.0, (1.0 - v) * height, 0.0);
            vec3 q = p;
            if (v == 0.0)
            {
                p.x += uSway * sin(uTime + aBlade.x * uFrequency);
                q.x += uSway * sin(uPreviousTime + aBlade.x * uFrequency);
            }
            float sink = height * clamp(5.0 * distance(uEye.xz, aBlade.xz) / uRange - 4.0, 0.0, 1.0);
            p.y -= sink;
            q.y -= sink;
            vUv = vec2(u, v);
            gl_Position = uViewProjection * vec4(p, 1.0);
            vNow = gl_Position;
            vPrevious = uPreviousViewProjection * vec4(q, 1.0);
        }
        """;

    /// <summary>
    /// Writes the blade's motion (UV, current minus previous, jitter removed, as the velocity pass) where the blade is what the near depth slice
    /// shows: its depth matches the depth buffer's within a small tolerance (the buffer is read, not tested, so the scene's depth stays untouched).
    /// </summary>
    public const string GrassMotionFragment = """
        #version 330 core
        in vec2 vUv;
        in vec4 vNow;
        in vec4 vPrevious;
        uniform sampler2D uSprite;
        uniform sampler2D uNearDepth;
        uniform vec2 uNearPlanes;
        uniform vec2 uJitterNdc;
        out vec4 fragColour;
        float viewZ(float d) { float zd = 2.0 * d - 1.0; return uNearPlanes.x * uNearPlanes.y / (uNearPlanes.y - zd * (uNearPlanes.y - uNearPlanes.x)); }
        void main()
        {
            if (texture(uSprite, vUv).a < 0.6) discard;
            float stored = texelFetch(uNearDepth, ivec2(gl_FragCoord.xy), 0).r;
            if (stored >= 1.0) discard;
            float zs = viewZ(stored), zf = viewZ(gl_FragCoord.z);
            if (abs(zs - zf) > 0.002 * zs + 0.05) discard;
            vec2 now = vNow.xy / vNow.w - uJitterNdc, previous = vPrevious.xy / vPrevious.w;
            fragColour = vec4((now - previous) * 0.5, 0.0, 0.0);
        }
        """;

    // ---- the native model (docs/renderer-native.md 3.3, step O): the texts above through NativeShaders.Port, bodies unchanged ----

    /// <summary><see cref="MeshVertex"/> in the native model (the shared mesh uniforms on <see cref="MeshPush"/> and <see cref="ViewConstants"/>).</summary>
    public static string MeshVertexNative() => NativeShaders.Port(MeshVertex());
    public static string MeshFragmentNative() => NativeShaders.Port(MeshFragment());
    /// <summary><see cref="ShadowShaders.MeshDepthFragment"/> in the native model, for <see cref="MeshVertexNative"/>.</summary>
    public static string MeshDepthNative() => NativeShaders.MeshDepthFragment();

    /// <summary>The blade programs' push constants (<see cref="GrassPush"/> is the C# side; std430).</summary>
    public const string GrassPushMembers = """
            vec4 size;
            vec4 colourBounds;
            float sway;
            float range;
            float frequency;
            bool cross;
            bool hasColourMap;
            bool coverage;
            bool wireframe;
            uint sprite;
            uint colourMap;
            uint nearDepth;
        """;

    /// <summary>The blade programs' own uniforms: <see cref="GrassPush"/> members (the camera, light, fog and sway phases are <see cref="ViewConstants"/>).</summary>
    static readonly Dictionary<string, string> GrassMap = new()
    {
        ["uSize"] = "pc.size", ["uColourBounds"] = "pc.colourBounds", ["uSway"] = "pc.sway", ["uRange"] = "pc.range", ["uFrequency"] = "pc.frequency",
        ["uCross"] = "pc.cross", ["uHasColourMap"] = "pc.hasColourMap", ["uCoverage"] = "pc.coverage", ["uWireframe"] = "pc.wireframe",
        ["uSprite"] = "textures2D[pc.sprite]", ["uColourMap"] = "textures2D[pc.colourMap]", ["uNearDepth"] = "textures2D[pc.nearDepth]",
    };

    public static string GrassVertexNative() => NativeShaders.Port(GrassVertex, NativeShaders.Map(GrassMap), GrassPushMembers);
    public static string GrassFragmentNative() => NativeShaders.Port(GrassFragment, NativeShaders.Map(GrassMap), GrassPushMembers);
    public static string GrassMotionVertexNative() => NativeShaders.Port(GrassMotionVertex, NativeShaders.Map(GrassMap), GrassPushMembers);
    public static string GrassMotionFragmentNative() => NativeShaders.Port(GrassMotionFragment, NativeShaders.Map(GrassMap), GrassPushMembers);

    // ------------------------------------------------------------------ the GPU cull (docs/renderer-native.md 5.3, step A2)
    // Three kernels per view, recorded ahead of the frame (FoliageGpuCull): cull (per chunk of at most 256 instances of one group: the
    // A1 tests in FoliageCull's operation order, a packed fade or -1 per instance, the visible count per chunk), scan (one workgroup:
    // each chunk's output offset in chunk order, and each draw's instance count and first instance), compact (per chunk: the visible
    // instances' matrices, fade in row 0 w, at the chunk's offset plus their rank in it, so the order is the CPU's). Every float operation
    // of a decision is `precise` (no FMA contraction) and written out in the C#'s order: IEEE adds and multiplies are correctly rounded in
    // Vulkan, so the decisions are the CPU's bit for bit; the square root of the fade is corrected to the correctly rounded one (CrSqrt).

    /// <summary>The workgroup size and the largest chunk (instances of one group per workgroup).</summary>
    public const int CullChunk = 256;

    /// <summary>
    /// <c>float CrSqrt(float x)</c>: the correctly rounded square root, as .NET's <c>MathF.Sqrt</c> (sqrtss) gives it. Vulkan does not require
    /// <c>sqrt</c> to be correctly rounded; this takes the driver's result and moves it by an ulp while it is not: <c>r</c> is the correctly
    /// rounded root of <c>x</c> exactly when <c>x</c> lies strictly between the squares of the midpoints next to <c>r</c> (a tie cannot occur).
    /// The squares are compared exactly in 64-bit integers (<c>umulExtended</c>): x = X·2^F and a midpoint m·2^e with integer mantissas.
    /// Zero, denormal, infinite and NaN inputs return the driver's value (a ground distance below 1e-19 is not drawn any differently).
    /// </summary>
    public const string CrSqrt = """
        uvec2 CrSquare(uint a) { uint hi, lo; umulExtended(a, a, hi, lo); return uvec2(lo, hi); }
        uvec2 CrShift(uint x, int k) { return k == 0 ? uvec2(x, 0u) : k < 32 ? uvec2(x << k, x >> (32 - k)) : uvec2(0u, x << (k - 32)); }
        bool CrLess(uvec2 a, uvec2 b) { return a.y < b.y || (a.y == b.y && a.x < b.x); }
        float CrSqrt(float x)
        {
            float r = sqrt(x);
            uint xb = floatBitsToUint(x);
            if (!(x > 0.0) || (xb >> 23) == 0u || (xb >> 23) >= 255u) return r;
            uint X = (xb & 0x7FFFFFu) | 0x800000u;
            int F = int(xb >> 23) - 150;
            for (int step = 0; step < 4; step++)
            {
                uint rb = floatBitsToUint(r);
                uint M = (rb & 0x7FFFFFu) | 0x800000u;
                int E = int(rb >> 23) - 150;
                int k = F - 2 * E + 2;   // x < ((2M + 1) 2^(E-1))^2  <=>  X 2^k < (2M + 1)^2
                if (k < 0 || k > 38) return r;
                if (!CrLess(CrShift(X, k), CrSquare(2u * M + 1u))) { r = uintBitsToFloat(rb + 1u); continue; }
                bool power = M == 0x800000u;   // below a power of two the gap is half as wide
                if (!CrLess(power ? CrSquare(4u * M - 1u) : CrSquare(2u * M - 1u), power ? CrShift(X, k + 2) : CrShift(X, k))) { r = uintBitsToFloat(rb - 1u); continue; }
                return r;
            }
            return r;
        }
        """;

    const string CullCommon = """
        #version 450
        layout(local_size_x = 256) in;
        // 17 floats a record (FoliageInstanceRecord.Pack): rows 1 to 4 xyz at 0..11, the sphere at 12..15, a rock's bits at 16; std430 stride 68.
        struct Instance { float f[17]; };
        struct Chunk { uint first; uint count; float range; float rangeSquared; float inverseBand; uint flags; float transition; float inverseTransitionBand; };
        // A TERRAIN-mode rock chunk (flags 1; with 2 its group's mirroring placements, else the others): ground.w is 1024 when the placement
        // mirrors, plus its biome map row + 1 (0: none). The view's biome rows switch (mode.x) and the resident biomes (a bit per row).
        // A mesh chunk of a group with an impostor (flags 4) keeps the instances before the transition, the group's impostor chunk (flags 8,
        // the same instances) those from the crossfade band on (docs/impostors.md "Drawing"). Not drawn: -2 (visible values are above -1.5).
        // mode.y: the fog cull is on (main colour pass; FogVolumes.WriteCull): fog[0] box min and in w the weather fog's distance squared (0: none), fog[1] box max and in w 1 when the eye's block
        // is there, fog[2] eye + hide distance squared, fog[3..9] the block's planes.
        // mode.z: the occlusion cull is on (main colour pass; HizPyramid.ViewFor): hz and hzOff as OcclusionView.Vectors says.
        struct ViewData { vec4 planes[8]; vec4 lengths[2]; uvec4 resident[2]; uvec4 mode; vec4 fog[10]; vec4 hz[6]; uvec4 hzOff[3]; };
        layout(push_constant) uniform Push { vec2 eye; uint planeCount; uint chunkCount; uint drawCount; float fullThreshold; } pc;
        uint ChunkIndex() { return gl_WorkGroupID.y * gl_NumWorkGroups.x + gl_WorkGroupID.x; }
        // FogVolumes.Covers: the box wholly inside the eye's fog block (its box and seven planes) and its nearest point at least the hide distance away.
        bool FogHidden(ViewData v, vec3 mn, vec3 mx)
        {
            vec3 eye = v.fog[2].xyz;
            precise vec3 q = clamp(eye, mn, mx) - eye;
            precise float d2 = q.x * q.x + q.y * q.y + q.z * q.z;
            if (v.fog[0].w > 0.0 && d2 >= v.fog[0].w) return true;   // the weather fog alone hides it (FogVolumes.Covers)
            if (v.fog[1].w < 0.5 || d2 < v.fog[2].w) return false;
            if (any(lessThan(mn, v.fog[0].xyz)) || any(greaterThan(mx, v.fog[1].xyz))) return false;
            for (int k = 0; k < 7; k++)
            {
                vec4 p = v.fog[3 + k];
                precise float top = (p.x >= 0.0 ? p.x * mx.x : p.x * mn.x) + (p.y >= 0.0 ? p.y * mx.y : p.y * mn.y) + (p.z >= 0.0 ? p.z * mx.z : p.z * mn.z);
                if (top >= p.w - 1.0) return false;
            }
            return true;
        }
        """;

    /// <summary>Kernel 1: per chunk (one workgroup), each instance's packed fade (-1: not drawn) and the chunk's visible count. A rock chunk
    /// (TERRAIN-mode meshes, drawn through the terrain's mesh path) also needs a fade of at least 0.5 (the terrain shader has no dither:
    /// <c>FoliageRenderer.Emit</c>) and the chunk's mirroring.</summary>
    public static readonly string CullCompute = CullCommon + CrSqrt + """
        layout(std430, set = 0, binding = 0) readonly buffer View { ViewData view; };
        layout(std430, set = 0, binding = 1) readonly buffer Instances { Instance instances[]; };
        layout(std430, set = 0, binding = 2) readonly buffer Chunks { Chunk chunks[]; };
        layout(std430, set = 0, binding = 3) writeonly buffer Fades { float fades[]; };
        layout(std430, set = 0, binding = 4) writeonly buffer Counts { uint counts[]; };
        layout(std430, set = 0, binding = 9) readonly buffer Hiz { vec2 hiz[]; };
        // The occlusion cull (HizPyramid, docs/formats/foliage.md "Occlusion culling"): the sphere seen from the previous frame's eye, against that frame's nearest and
        // farthest view distance per block of pixels. view.hz[0] the previous eye and the eye's step, [1..3] its right, up and back axes, [4] tan x, tan y, width, height,
        // [5] pixels per unit of tangent, level count, level 0's width and height; view.hzOff the levels' offsets.
        // The texels covering the pixel rectangle r (x0, y0, x1, y1): the level where it spans at most 2 x 2 texels; false when it leaves the picture.
        bool HizRegion(vec4 r, out float least, out float most)
        {
            least = 3.0e38; most = 0.0;
            if (r.x < 0.0 || r.y < 0.0 || r.z >= view.hz[4].z || r.w >= view.hz[4].w) return false;
            int bw = int(view.hz[5].z), bh = int(view.hz[5].w), levels = int(view.hz[5].y);
            int x0 = int(r.x) >> 2, y0 = int(r.y) >> 2, x1 = min(int(r.z) >> 2, bw - 1), y1 = min(int(r.w) >> 2, bh - 1);
            int l = 0;
            while (l < levels - 1 && (((x1 >> l) - (x0 >> l)) > 1 || ((y1 >> l) - (y0 >> l)) > 1)) l++;
            int w = (bw + (1 << l) - 1) >> l;
            uint off = view.hzOff[l >> 2][l & 3];
            for (int y = y0 >> l; y <= (y1 >> l); y++)
                for (int x = x0 >> l; x <= (x1 >> l); x++)
                {
                    vec2 t = hiz[off + uint(y * w + x)];
                    least = min(least, t.x);
                    most = max(most, t.y);
                }
            return true;
        }
        // True when every ray from the eye to the sphere is stopped by the surfaces of the previous frame: the pixel rectangle the sphere's box covers there, grown by
        // a pixel (the depth was drawn with a jitter) and by the parallax of the eye's step against the nearest surface in it, holds nothing but surfaces nearer than the
        // sphere by a margin. The step's parallax: a ray from the new eye to a point of the sphere crosses the old view's picture within delta * f / z pixels of that
        // point's, at the depth z where it passes the surface it hits; so the rectangle must cover delta * f / (the nearest surface in it).
        bool HizOccluded(vec4 sphere)
        {
            vec3 d = sphere.xyz - view.hz[0].xyz;
            float zc = -dot(view.hz[3].xyz, d), xc = dot(view.hz[1].xyz, d), yc = dot(view.hz[2].xyz, d), r = sphere.w;
            float zn = zc - r, zf = zc + r;
            if (zn < 40.0) return false;   // near the eye (or behind it): drawn
            float tx0 = min(min((xc - r) / zn, (xc - r) / zf), min((xc + r) / zn, (xc + r) / zf));
            float tx1 = max(max((xc - r) / zn, (xc - r) / zf), max((xc + r) / zn, (xc + r) / zf));
            float ty0 = min(min((yc - r) / zn, (yc - r) / zf), min((yc + r) / zn, (yc + r) / zf));
            float ty1 = max(max((yc - r) / zn, (yc - r) / zf), max((yc + r) / zn, (yc + r) / zf));
            vec2 size = view.hz[4].zw, tn = view.hz[4].xy;
            vec4 rect = vec4((0.5 + 0.5 * tx0 / tn.x) * size.x - 1.0, (0.5 + 0.5 * ty0 / tn.y) * size.y - 1.0,
                             (0.5 + 0.5 * tx1 / tn.x) * size.x + 1.0, (0.5 + 0.5 * ty1 / tn.y) * size.y + 1.0);
            float delta = view.hz[0].w, f = view.hz[5].x, pad = 0.0, least, most;
            for (int i = 0; i < 3; i++)
            {
                if (!HizRegion(rect + vec4(-pad, -pad, pad, pad), least, most)) return false;
                float need = delta * f / max(least, 1.0);
                if (need <= pad + 0.5) return most + 0.01 * zn + 3.0 < zn;
                pad = need;
            }
            return false;
        }
        shared uint visibleCount, foggedCount, occludedCount;
        void main()
        {
            uint c = ChunkIndex();
            if (c >= pc.chunkCount) return;
            uint i = gl_LocalInvocationID.x;
            if (i == 0u) { visibleCount = 0u; foggedCount = 0u; occludedCount = 0u; }
            barrier();
            Chunk k = chunks[c];
            float packed = -2.0;
            uint fogged = 0u, occluded = 0u;
            if (i < k.count)
            {
                uint at = k.first + i;
                vec4 ground = vec4(instances[at].f[9], instances[at].f[11], 0.0, instances[at].f[16]);   // x, z of the translation
                vec4 sphere = vec4(instances[at].f[12], instances[at].f[13], instances[at].f[14], instances[at].f[15]);
                precise float dx = ground.x - pc.eye.x;
                precise float dz = ground.y - pc.eye.y;
                precise float d2 = dx * dx + dz * dz;
                if (!(d2 >= k.rangeSquared))
                {
                    precise float d = CrSqrt(d2);
                    precise float w = clamp((k.range - d) * k.inverseBand, 0.0, 1.0);
                    bool visible = true;
                    for (uint p = 0u; p < pc.planeCount; p++)
                    {
                        vec4 q = view.planes[p];
                        precise float side = q.x * sphere.x + q.y * sphere.y + q.z * sphere.z + q.w;
                        precise float limit = -sphere.w * view.lengths[p >> 2][p & 3u];
                        if (side < limit) { visible = false; break; }
                    }
                    if (visible && (k.flags & 1u) != 0u)
                        visible = !(w < 0.5) && ((uint(ground.w) >= 1024u) == ((k.flags & 2u) != 0u));
                    if (visible) packed = w >= pc.fullThreshold ? 2.0 : w;
                    if (visible && (k.flags & 12u) != 0u)
                    {
                        precise float m = clamp((k.transition - d) * k.inverseTransitionBand, 0.0, 1.0);
                        if ((k.flags & 8u) != 0u) packed = m < 1.0 ? (m > 0.0 ? -m : packed) : -2.0;
                        else if ((k.flags & 1u) != 0u) packed = m > 0.0 ? (m < 1.0 ? m : 2.0) : -2.0;   // a rock: no range dither, the fade is the transition band's alone
                        else packed = m > 0.0 ? (m < 1.0 ? m : packed) : -2.0;
                    }
                    // The fog cull last, so it counts only what would have been drawn: the sphere's box (FoliageCull.CullGroup).
                    if (packed > -1.5 && view.mode.y != 0u && FogHidden(view, sphere.xyz - vec3(sphere.w), sphere.xyz + vec3(sphere.w))) { packed = -2.0; fogged = 1u; }
                    if (packed > -1.5 && view.mode.z != 0u && HizOccluded(sphere)) { packed = -2.0; occluded = 1u; }
                }
            }
            fades[c * 256u + i] = packed;
            if (packed > -1.5) atomicAdd(visibleCount, 1u);
            if (fogged != 0u) atomicAdd(foggedCount, 1u);
            if (occluded != 0u) atomicAdd(occludedCount, 1u);
            barrier();
            if (i == 0u) { counts[c] = visibleCount; counts[pc.chunkCount + c] = foggedCount; counts[2u * pc.chunkCount + c] = occludedCount; }
        }
        """;

    /// <summary>Kernel 2: one workgroup. Each chunk's output offset (an exclusive prefix of the counts in chunk order; the chunks of a batch
    /// are consecutive, so a batch's instances are too), the total at the end, and each draw's instance count and first instance.</summary>
    public static readonly string ScanCompute = CullCommon + """
        struct Draw { uint indexCount; uint chunkStart; uint chunkEnd; uint pad; };
        struct Args { uint indexCount; uint instanceCount; uint firstIndex; int vertexOffset; uint firstInstance; };
        layout(std430, set = 0, binding = 4) readonly buffer Counts { uint counts[]; };   // [0, n) the visible, [n, 2n) the fog cull's, [2n, 3n) the occlusion cull's
        layout(std430, set = 0, binding = 5) writeonly buffer Offsets { uint offsets[]; };
        layout(std430, set = 0, binding = 6) readonly buffer Draws { Draw draws[]; };
        layout(std430, set = 0, binding = 7) writeonly buffer ArgsBuffer { Args args[]; };
        shared uint partial[256];
        shared uint foggedTotal, occludedTotal;
        uint per;
        uint OffsetOf(uint c)
        {
            if (c >= pc.chunkCount) return partial[255];
            uint segment = c / per;
            uint o = segment == 0u ? 0u : partial[segment - 1u];
            for (uint j = segment * per; j < c; j++) o += counts[j];
            return o;
        }
        void main()
        {
            uint t = gl_LocalInvocationID.x, n = pc.chunkCount;
            if (t == 0u) { foggedTotal = 0u; occludedTotal = 0u; }
            barrier();
            per = max((n + 255u) / 256u, 1u);
            uint begin = min(t * per, n), end = min(begin + per, n);
            uint sum = 0u;
            uint fogSum = 0u, occSum = 0u;
            for (uint c = begin; c < end; c++) { sum += counts[c]; fogSum += counts[n + c]; occSum += counts[2u * n + c]; }
            atomicAdd(foggedTotal, fogSum);
            atomicAdd(occludedTotal, occSum);
            partial[t] = sum;
            barrier();
            for (uint s = 1u; s < 256u; s <<= 1)
            {
                uint v = t >= s ? partial[t - s] : 0u;
                barrier();
                partial[t] += v;
                barrier();
            }
            uint base = partial[t] - sum;
            for (uint c = begin; c < end; c++) { offsets[c] = base; base += counts[c]; }
            if (t == 255u) offsets[n] = partial[255];
            if (t == 0u) offsets[n + 1u] = foggedTotal;   // the fog cull's total, after the barriers above
            if (t == 1u) offsets[n + 2u] = occludedTotal;
            for (uint d = t; d < pc.drawCount; d += 256u)
            {
                Draw draw = draws[d];
                uint first = OffsetOf(draw.chunkStart);
                args[d] = Args(draw.indexCount, OffsetOf(draw.chunkEnd) - first, 0u, 0, first);
            }
        }
        """;

    /// <summary>Kernel 3: per chunk, the visible instances' matrices (row 0 w = the packed fade) at the chunk's offset plus their rank. A rock's
    /// row 0 w is what <see cref="TerrainRenderer.DrawMeshes"/> writes there: with the view's biome rows its biome row when resident, else -1;
    /// without them 0.</summary>
    public static readonly string CompactCompute = CullCommon + """
        layout(std430, set = 0, binding = 0) readonly buffer View { ViewData view; };
        layout(std430, set = 0, binding = 1) readonly buffer Instances { Instance instances[]; };
        layout(std430, set = 0, binding = 2) readonly buffer Chunks { Chunk chunks[]; };
        layout(std430, set = 0, binding = 3) readonly buffer Fades { float fades[]; };
        layout(std430, set = 0, binding = 5) readonly buffer Offsets { uint offsets[]; };
        layout(std430, set = 0, binding = 8) writeonly buffer Rows { vec4 rows[]; };
        shared uint rank[256];
        void main()
        {
            uint c = ChunkIndex();
            if (c >= pc.chunkCount) return;
            uint i = gl_LocalInvocationID.x;
            float f = fades[c * 256u + i];
            uint visible = f > -1.5 ? 1u : 0u;
            rank[i] = visible;
            barrier();
            for (uint s = 1u; s < 256u; s <<= 1)
            {
                uint v = i >= s ? rank[i - s] : 0u;
                barrier();
                rank[i] += v;
                barrier();
            }
            if (visible != 0u)
            {
                uint o = (offsets[c] + rank[i] - 1u) * 4u;
                uint at = chunks[c].first + i;
                float w = f;
                float lane = 0.0;
                if ((chunks[c].flags & 1u) != 0u)
                {
                    int row = int(uint(instances[at].f[16]) & 1023u) - 1;
                    bool resident = row >= 0 && ((view.resident[row >> 7][(row >> 5) & 3] >> uint(row & 31)) & 1u) != 0u;
                    w = view.mode.x != 0u ? (resident ? float(row) : -1.0) : 0.0;
                    // A rock with an impostor (flag 4) in its transition band: row 1's w, which the terrain's mesh vertex program passes on as the dither threshold.
                    if ((chunks[c].flags & 4u) != 0u && f < 1.0) lane = f;
                }
                // The fourth column is 0, 0, 0, 1 (FoliageInstanceRecord.Pack checks it); row 0's w is the fade.
                rows[o] = vec4(instances[at].f[0], instances[at].f[1], instances[at].f[2], w);
                rows[o + 1u] = vec4(instances[at].f[3], instances[at].f[4], instances[at].f[5], lane);
                rows[o + 2u] = vec4(instances[at].f[6], instances[at].f[7], instances[at].f[8], 0.0);
                rows[o + 3u] = vec4(instances[at].f[9], instances[at].f[10], instances[at].f[11], 1.0);
            }
        }
        """;

    static string Replace(string source, string pattern, string replacement, bool required)
    {
        var regex = new Regex(pattern);
        if (!regex.IsMatch(source))
        {
            if (required) throw new InvalidOperationException($"FoliageShaders: '{pattern}' not found in the shared mesh shader; update the patch.");
            return source;
        }
        return regex.Replace(source, _ => replacement, 1);
    }
}

/// <summary>The C# side of <see cref="FoliageShaders.GrassPushMembers"/> (std430 push constants). GLSL bools are 32-bit (0 / 1).</summary>
[StructLayout(LayoutKind.Explicit, Size = 72)]
struct GrassPush
{
    [FieldOffset(0)] public Vector4 Size;
    [FieldOffset(16)] public Vector4 ColourBounds;
    [FieldOffset(32)] public float Sway;
    [FieldOffset(36)] public float Range;
    [FieldOffset(40)] public float Frequency;
    [FieldOffset(44)] public uint Cross;
    [FieldOffset(48)] public uint HasColourMap;
    [FieldOffset(52)] public uint Coverage;
    [FieldOffset(56)] public uint Wireframe;
    [FieldOffset(60)] public uint Sprite;
    [FieldOffset(64)] public uint ColourMap;
    [FieldOffset(68)] public uint NearDepth;
}
