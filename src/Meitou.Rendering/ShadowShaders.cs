using System.Globalization;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// GLSL of the sun shadows (docs/formats/shadows.md): the receiver term <c>kenshiShadow</c> that the lighting multiplies the sun by, the
/// depth-only fragment stages the renderers' caster draws use, and the debug views. The cascade data come from <see cref="ShadowPass"/>
/// through two uniform blocks (the receiver's and the caster's), bound once per program by <see cref="Bind"/>.
/// </summary>
static class ShadowShaders
{
    /// <summary>Uniform buffer binding points of the receiver block and the caster block.</summary>
    public const uint ReceiverBinding = 6, CasterBinding = 7;
    public const string ReceiverBlock = "KenshiShadowReceiver", CasterBlock = "KenshiShadowCaster";
    /// <summary>The shadow map's texture unit: below the atmosphere's three (<see cref="SkyRenderer.AssignSamplerUnits"/>), out of every scene shader's way.</summary>
    public static int MapUnit { get; private set; } = -1;

    /// <summary>The noise texture's unit (the game's <c>white-noise.png</c>), next below <see cref="MapUnit"/>.</summary>
    public static int NoiseUnit { get; private set; } = -1;

    static string F(float v) => v.ToString("0.#########", CultureInfo.InvariantCulture) + (v == MathF.Floor(v) ? ".0" : "");

    static string Taps() => string.Join(", ", KenshiShadows.PcfOffsets.Select(o => $"vec2({F(o.X)}, {F(o.Y)})"));

    /// <summary>
    /// The receiver: <c>float kenshiShadow(vec3 world, vec3 n)</c> is 1 where the sun reaches the point and 0 where a caster hides it; the
    /// lighting calls it, and it calls <c>kenshiShadowFaithful</c>, the game's CSM (shadowFunctions.hlsl <c>computeShadowMultiplier</c>):
    /// the cascade by the game's clip-z test, nothing beyond the last split, the twelve HEX12 taps shifted and turned by the game's noise
    /// texture, projected onto the surface's plane, each a point-sampled compare. Include it before use.
    /// </summary>
    public static readonly string Functions = $$"""

        layout(std140) uniform {{ReceiverBlock}}
        {
            mat4 uShadowTile[4];       // (world - origin) -> (u, v, depth) in the cascade's tile, each 0..1
            vec4 uShadowRect[4];       // the tile in the atlas: x0, y0, width, height
            vec4 uShadowCascade[4];    // x: the view depth up to which it is used (the game's clip-z test), y: PCF radius (tile UV), zw: extent x, y (world)
            vec4 uShadowExtent[4];     // x: extent z (world), y: 1 / tile texels
            mat4 uShadowLight;         // world -> light axes (rotation)
            vec4 uShadowOrigin;        // xyz: the origin of uShadowTile, w: 1 when shadows are on
            vec4 uShadowForward;       // xyz: the camera's view direction (cascades go by depth along it), w: cascades
            vec4 uShadowAtlas;         // x: atlas side in texels, y: debug cascade tint (0/1), z: 1 when uShadowNoise holds the game's noise, w: 1 for the Meitou receiver
        };
        uniform sampler2DShadow uShadowMap;
        uniform sampler2D uShadowNoise;

        // The cascade a point falls in: 0..3, or -1 beyond the shadow range (and when shadows are off). The game compares its pixel's
        // clip-space z with split - split[0]; uShadowCascade[i].x is that test turned into a view depth.
        int shadowCascade(vec3 world)
        {
            if (uShadowOrigin.w < 0.5) return -1;
            float depth = dot(world - uShadowOrigin.xyz, uShadowForward.xyz);
            int count = int(uShadowForward.w);
            for (int i = 0; i < 4; i++)
                if (i < count && depth <= uShadowCascade[i].x) return i;
            return -1;
        }

        float shadowHash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

        // The game's jitter: the red channel of white-noise.png, point sampled and wrapped, at the Direct3D atlas UV × 1024 (fixed to the
        // ground, not the screen). Without the file (uShadowAtlas.z = 0) a hash of the same coordinate stands in.
        float shadowNoise(vec2 uvD3d)
        {
            if (uShadowAtlas.z < 0.5) return shadowHash(floor(uvD3d * {{F(KenshiShadows.NoiseScale * 64)}}));
            vec2 size = vec2(textureSize(uShadowNoise, 0));
            vec2 texel = mod(floor(uvD3d * {{F(KenshiShadows.NoiseScale)}} * size), size);
            return texelFetch(uShadowNoise, ivec2(texel), 0).r;
        }

        // One point-sampled compare: the texel holding uv (the game's `filtering none`) read at its centre, where the linear compare's
        // weights are exactly 1, 0, 0, 0; outside the atlas the border is 1 (white, the far end): lit unless the point is beyond it.
        float shadowPointCompare(vec2 uv, float z)
        {
            float size = uShadowAtlas.x;
            vec2 texel = floor(uv * size);
            if (any(lessThan(texel, vec2(0.0))) || any(greaterThanEqual(texel, vec2(size)))) return z <= 1.0 ? 1.0 : 0.0;
            return textureLod(uShadowMap, vec3((texel + 0.5) / size, z), 0.0);
        }

        const vec2 kenshiShadowTaps[{{KenshiShadows.PcfTaps}}] = vec2[]({{Taps()}});

        // The game's CSM receiver (shadowFunctions.hlsl computeShadowMultiplier with PCF, HEX12 and jitter; docs/formats/shadows.md).
        // It works in the game's frame, the Direct3D atlas UV (v down) and 0..1 depth, and turns only the final position into GL's (v up).
        float kenshiShadowFaithful(vec3 world, vec3 n)
        {
            int c = shadowCascade(world);
            if (c < 0) return 1.0;
            vec3 tile = (uShadowTile[c] * vec4(world - uShadowOrigin.xyz, 1.0)).xyz;
            vec4 rect = uShadowRect[c];
            vec2 atlas = rect.xy + tile.xy * rect.zw;
            float noise = shadowNoise(vec2(atlas.x, 1.0 - atlas.y));
            float angle = noise * {{F(KenshiShadows.PcfJitterAngle)}};
            float cs = cos(angle), sn = sin(angle);
            // The radius in the atlas's UV (the game's csmParams.y), times its 0.6 · 0.5.
            float radius = uShadowCascade[c].y * rect.z * {{F(KenshiShadows.PcfOffsetScale)}};
            // The light-space normal (y up) used as is against the v-down offsets, as the game does.
            vec3 nl = normalize(mat3(uShadowLight) * n);
            float lit = 0.0;
            for (int k = 0; k < {{KenshiShadows.PcfTaps}}; k++)
            {
                vec2 o = kenshiShadowTaps[k] + vec2(noise * {{F(KenshiShadows.PcfJitterShift)}}, 0.0);
                vec2 r = vec2(cs * o.x + sn * o.y, cs * o.y - sn * o.x) * radius;
                // The offset (r, 0) with its component along the normal removed: u, v (Direct3D) and depth (in UV units, as the game adds it).
                vec3 d = vec3(r, 0.0) - nl * dot(nl.xy, r);
                lit += shadowPointCompare(atlas + vec2(d.x, -d.y), tile.z + d.z);
            }
            return lit / {{F(KenshiShadows.PcfTaps)}};
        }
        {{MeitouShadowShaders.Functions}}
        // The receiver the lighting calls: the game's CSM, or the Meitou shadows (uShadowAtlas.w = 1; the shadows enhancement).
        float kenshiShadow(vec3 world, vec3 n)
        {
            return uShadowAtlas.w > 0.5 ? kenshiShadowMeitou(world, n) : kenshiShadowFaithful(world, n);
        }

        // The debug views' cascade colours (the game's own debug colours: red, orange, yellow, green).
        vec3 shadowCascadeColour(int c)
        {
            return c == 0 ? vec3(1.0, 0.0, 0.0) : c == 1 ? vec3(1.0, 0.5, 0.0) : c == 2 ? vec3(1.0, 1.0, 0.0) : c == 3 ? vec3(0.0, 1.0, 0.0) : vec3(1.0);
        }

        """;

    /// <summary>
    /// What every caster's fragment stage ends with: the game's caster bias on the 0..1 depth the cascade matrices produce (the GL clip z
    /// of −1..1 holds it unchanged, so it is window depth · 2 − 1), flattened onto 0 when the caster lies between the box and the sun.
    /// </summary>
    const string CasterDepth = $$"""

        layout(std140) uniform {{CasterBlock}}
        {
            vec4 uShadowBias;   // x fixed, y slope, z max slope (depth units)
        };
        void shadowWriteDepth()
        {
            float z = gl_FragCoord.z * 2.0 - 1.0;
            float g = length(vec2(dFdx(z), dFdy(z)));
            gl_FragDepth = clamp(z + min(uShadowBias.z, uShadowBias.y * g) + uShadowBias.x, 0.0, 1.0);
        }

        """;

    /// <summary>Depth-only fragment stage for geometry without a cut-out (the terrain).</summary>
    public static readonly string DepthFragment = "#version 330 core\n" + CasterDepth + """
        void main() { shadowWriteDepth(); }
        """;

    /// <summary>
    /// Depth-only fragment stage for the mesh shaders' vertex stage (<see cref="Shaders.MeshVertex"/> and its instanced forms): the same
    /// cut-out uniforms as the mesh fragment shader, so the renderers' material setters drive it unchanged.
    /// </summary>
    public static readonly string MeshDepthFragment = "#version 330 core\n" + CasterDepth + """
        in vec2 vUv;
        uniform sampler2D uDiffuse;
        uniform sampler2D uNormal;
        uniform bool uHasDiffuse;
        uniform bool uHasNormal;
        uniform vec2 uTile;
        uniform int uAlphaSource;      // 0 none, 1 diffuse alpha, 2 normal-map alpha, 3 diffuse channel
        uniform int uAlphaChannel;
        uniform float uAlphaThreshold;
        void main()
        {
            if (uAlphaThreshold > 0.0 && uAlphaSource != 0)
            {
                vec2 uv = vUv * uTile;
                float alpha = 1.0;
                if (uAlphaSource == 1 && uHasDiffuse) alpha = texture(uDiffuse, uv).a;
                else if (uAlphaSource == 2 && uHasNormal) alpha = texture(uNormal, uv).a;
                else if (uAlphaSource == 3 && uHasDiffuse) alpha = texture(uDiffuse, uv)[uAlphaChannel];
                if (alpha < uAlphaThreshold) discard;
            }
            shadowWriteDepth();
        }
        """;

    public const string FullscreenVertex = """
        #version 330 core
        out vec2 vUv;
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Debug view of the receiver: the scene's depth (near slice) back to world positions, then the shadow term. Mode 2: the term in white
    /// and black, tinted by the cascade; mode 3: written as a multiplier over the finished picture (a preview of the lighting change).
    /// </summary>
    public static readonly string DebugFragment = "#version 330 core\n" + Functions + """
        in vec2 vUv;
        uniform sampler2D uSceneDepth;
        uniform mat4 uInverse;       // NDC -> position relative to the eye (rotation-only view · near slice projection, inverted)
        uniform vec3 uEye;
        uniform int uMode;
        out vec4 fragColour;
        void main()
        {
            float d = texture(uSceneDepth, vUv).r;
            if (d >= 1.0) { fragColour = uMode == 3 ? vec4(1.0) : vec4(0.2, 0.3, 0.5, 1.0); return; }
            vec4 p = uInverse * vec4(vUv * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
            vec3 world = uEye + p.xyz / p.w;
            // Without the G-buffer's normal the debug view takes the depth's own slope.
            vec3 n = normalize(cross(dFdx(world), dFdy(world)));
            if (dot(n, uEye - world) < 0.0) n = -n;
            float s = kenshiShadow(world, n);
            int c = shadowCascade(world);
            if (uMode == 3) { fragColour = vec4(vec3(0.35 + 0.65 * s), 1.0); return; }
            vec3 tint = c < 0 ? vec3(0.5) : mix(vec3(1.0), shadowCascadeColour(c), 0.35);
            fragColour = vec4(tint * (0.12 + 0.88 * s), 1.0);
        }
        """;

    /// <summary>Debug view of the cascade maps: the atlas's depth in grey (near the sun dark), drawn into a corner of the picture.</summary>
    public const string AtlasFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uAtlas;
        out vec4 fragColour;
        void main()
        {
            float d = texture(uAtlas, vUv).r;
            vec2 edge = abs(fract(vUv * 2.0) - 0.5);
            float line = max(edge.x, edge.y) > 0.497 ? 1.0 : 0.0;
            fragColour = vec4(mix(vec3(pow(d, 4.0)), vec3(1.0, 0.2, 0.2), line), 1.0);
        }
        """;

    /// <summary>
    /// Points a freshly linked program's shadow blocks and sampler at their binding points and unit (nothing when it has none). Called by
    /// <see cref="WorldGl.Program"/> for every world program.
    /// </summary>
    public static void Bind(IGl gl, uint program)
    {
        if (MapUnit < 0)
        {
            gl.GetInteger(GetPName.MaxCombinedTextureImageUnits, out int combined);
            MapUnit = combined - 4;
            NoiseUnit = combined - 5;
            // Zero-filled blocks (shadows off) at both binding points, so a program reading them is defined without a ShadowPass
            // (--no-shadows, the character viewer); a live pass binds its own buffers over them.
            foreach (var (binding, bytes) in new[] { (ReceiverBinding, ShadowPass.ReceiverBytes), (CasterBinding, 16), (MeitouShadowShaders.Binding, MeitouShadowShaders.BlockBytes) })
            {
                uint buffer = gl.GenBuffer();
                gl.BindBuffer(BufferTargetARB.UniformBuffer, buffer);
                var zeros = new byte[bytes];
                gl.BufferData<byte>(BufferTargetARB.UniformBuffer, zeros, BufferUsageARB.StaticDraw);
                gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
                gl.BindBufferBase(BufferTargetARB.UniformBuffer, binding, buffer);
            }
        }
        uint receiver = gl.GetUniformBlockIndex(program, ReceiverBlock);
        if (receiver != uint.MaxValue) gl.UniformBlockBinding(program, receiver, ReceiverBinding);
        uint caster = gl.GetUniformBlockIndex(program, CasterBlock);
        if (caster != uint.MaxValue) gl.UniformBlockBinding(program, caster, CasterBinding);
        int map = gl.GetUniformLocation(program, "uShadowMap"), noise = gl.GetUniformLocation(program, "uShadowNoise");
        if (map >= 0 || noise >= 0)
        {
            gl.UseProgram(program);
            if (map >= 0) gl.Uniform1(map, MapUnit);
            if (noise >= 0) gl.Uniform1(noise, NoiseUnit);
            gl.UseProgram(0);
        }
        MeitouShadowShaders.Bind(gl, program);
    }
}
