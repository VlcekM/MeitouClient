using System.Globalization;
using Meitou.Data.World;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

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

    static string F(float v) => v.ToString("0.#########", CultureInfo.InvariantCulture) + (v == MathF.Floor(v) ? ".0" : "");

    /// <summary>
    /// The receiver: <c>float kenshiShadow(vec3 world, vec3 n)</c> is 1 where the sun reaches the point and 0 where a caster hides it. As
    /// the game's CSM (shadowFunctions.hlsl <c>computeShadowMultiplier</c>): the cascade by view depth, nothing beyond the last split, 12
    /// taps on a hexagonal ring turned by a random angle per shadow texel, the taps kept on the surface's plane. Include it before use.
    /// </summary>
    public static readonly string Functions = $$"""

        layout(std140) uniform {{ReceiverBlock}}
        {
            mat4 uShadowTile[4];       // (world - origin) -> (u, v, depth) in the cascade's tile, each 0..1
            vec4 uShadowRect[4];       // the tile in the atlas: x0, y0, width, height
            vec4 uShadowCascade[4];    // x: the far view depth it serves, y: PCF radius (tile UV), zw: extent x, y (world)
            vec4 uShadowExtent[4];     // x: extent z (world), y: 1 / tile texels
            mat4 uShadowLight;         // world -> light axes (rotation)
            vec4 uShadowOrigin;        // xyz: the origin of uShadowTile, w: 1 when shadows are on
            vec4 uShadowForward;       // xyz: the camera's view direction (cascades go by depth along it), w: cascades
            vec4 uShadowAtlas;         // x: atlas side in texels, y: debug cascade tint (0/1)
        };
        uniform sampler2DShadow uShadowMap;

        // The cascade a point falls in: 0..3, or -1 beyond the shadow range (and when shadows are off).
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

        float kenshiShadow(vec3 world, vec3 n)
        {
            int c = shadowCascade(world);
            if (c < 0) return 1.0;
            vec3 tile = (uShadowTile[c] * vec4(world - uShadowOrigin.xyz, 1.0)).xyz;
            if (any(lessThan(tile, vec3(0.0))) || any(greaterThan(tile, vec3(1.0)))) return 1.0;   // the map's border is lit
            vec4 rect = uShadowRect[c];
            // The surface's plane in the tile's units: a tap moved by (du, dv) moves this much in depth.
            vec3 nl = mat3(uShadowLight) * n;
            vec3 ext = vec3(uShadowCascade[c].zw, uShadowExtent[c].x);
            float nz = abs(nl.z) < 0.2 ? (nl.z < 0.0 ? -0.2 : 0.2) : nl.z;
            vec2 slope = -vec2(nl.x * ext.x, nl.y * ext.y) / (nz * ext.z);
            // A random turn per shadow texel (the game reads a noise texture at the atlas UV × 1024).
            vec2 atlasUv = rect.xy + tile.xy * rect.zw;
            float noise = shadowHash(floor(atlasUv * 1024.0));
            float angle = noise * 6.2831853;
            mat2 turn = mat2(cos(angle), sin(angle), -sin(angle), cos(angle)) * (uShadowCascade[c].y * {{F(KenshiShadows.PcfOffsetScale)}});
            vec2 lo = vec2(uShadowExtent[c].y), hi = vec2(1.0 - uShadowExtent[c].y);
            float lit = 0.0;
            for (int k = 0; k < {{KenshiShadows.PcfTaps}}; k++)
            {
                // Six taps at radius 1 and six at 2.4 between them: a hexagonal ring like the game's (which reaches 2.65).
                float a = float(k % 6) * 1.0471976 + (k < 6 ? 0.0 : 0.5235988);
                vec2 o = vec2(cos(a), sin(a)) * (k < 6 ? 1.0 : 2.4) + vec2(noise * 0.25, 0.0);
                vec2 d = turn * o;
                vec2 uv = clamp(tile.xy + d, lo, hi);
                float z = tile.z + dot(uv - tile.xy, slope);
                lit += textureLod(uShadowMap, vec3(rect.xy + uv * rect.zw, z), 0.0);
            }
            return lit / {{F(KenshiShadows.PcfTaps)}};
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
    public static void Bind(GL gl, uint program)
    {
        if (MapUnit < 0)
        {
            gl.GetInteger(GetPName.MaxCombinedTextureImageUnits, out int combined);
            MapUnit = combined - 4;
            // Zero-filled blocks (shadows off) at both binding points, so a program reading them is defined without a ShadowPass
            // (--no-shadows, the character viewer); a live pass binds its own buffers over them.
            foreach (var (binding, bytes) in new[] { (ReceiverBinding, ShadowPass.ReceiverBytes), (CasterBinding, 16) })
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
        int map = gl.GetUniformLocation(program, "uShadowMap");
        if (map >= 0)
        {
            gl.UseProgram(program);
            gl.Uniform1(map, MapUnit);
            gl.UseProgram(0);
        }
    }
}
