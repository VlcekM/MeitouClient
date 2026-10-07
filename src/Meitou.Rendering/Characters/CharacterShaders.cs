using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Meitou.Rendering.Characters;

/// <summary>
/// The character programs, native GLSL 450 on the native model's sets (docs/renderer-native.md 3.3; docs/character-renderer.md): set 0's
/// two extra storage buffers hold the frame's bone palettes (binding 6) and the materials of the parts in view (binding 7), and the
/// instances carry the model matrix, the palette's first bone and the material's index as per-instance vertex inputs, so one instanced draw
/// covers every character that shows the same mesh part whatever its textures, skin or hair colour.
/// </summary>
static class CharacterShaders
{
    /// <summary>The first per-instance input location (the mesh's own inputs, <c>Vertex</c>, are at 0 to 6): four matrix rows, then <see cref="DataLocation"/>.</summary>
    public const int InstanceLocation = 7, DataLocation = 11;
    public const uint BonesBinding = 6, MaterialsBinding = 7;

    // Material flags (CharacterMaterialRecord.Flags).
    public const uint HasDiffuse = 1 << 0, HasNormal = 1 << 1, NormalSwizzled = 1 << 2, HasHead = 1 << 3, HasHeadNormal = 1 << 4, HasHeadMask = 1 << 5,
        HasBodyMask = 1 << 6, HasHair = 1 << 7, HasBeard = 1 << 8, ClipNormalAlpha = 1 << 9, Dyed = 1 << 10, Vest0Coloured = 1 << 11;
    // Shading modes.
    public const uint ShadeItem = 0, ShadeBody = 1, ShadeHair = 2;
    // Texture slots (CharacterMaterialRecord.Tex).
    public const int SlotDiffuse = 0, SlotNormal = 1, SlotColourMap = 2, SlotHeadDiffuse = 3, SlotHeadNormal = 4, SlotHeadMask = 5, SlotBodyMask = 6,
        SlotHairOverlay = 7, SlotBeardOverlay = 8, SlotVest = 9, Slots = 18;

    const string PushMembers = """
            vec3 flatColour;
            uint wireframe;
        """;

    const string Structs = """
        struct CharMaterial
        {
            uint shading;        // 0 item, 1 body, 2 hair
            uint flags;
            uint vestCount;
            float alphaThreshold;
            uint tex[20];
            vec4 shirtColour;
            vec4 skinTone;
            vec4 hairColour;
            vec4 hairAlpha;
            vec4 hairMult;
            vec4 beardAlpha;
            vec4 diffuseChannel;
            vec4 alphaChannel;
            vec4 colour1;
            vec4 colour2;
        };
        layout(std430, set = 0, binding = 7) readonly buffer Materials { CharMaterial items[]; } materials;
        """;

    static string Head(string stage) => "#version 450\n" + NativeShaders.Prelude(PushMembers) + Structs + stage;

    public static string Vertex() => "#version 450\n" + NativeShaders.Prelude(PushMembers) + """
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec4 aTangent;
        layout(location = 4) in vec4 aColour;           // x: the vertex's morph slot as raw bits (0: none)
        layout(location = 5) in uvec4 aBones;
        layout(location = 6) in vec4 aWeights;
        layout(location = 7) in vec4 aInstance0;
        layout(location = 8) in vec4 aInstance1;
        layout(location = 9) in vec4 aInstance2;
        layout(location = 10) in vec4 aInstance3;
        layout(location = 11) in uvec4 aInstanceData;   // x first bone of the palette, y material, z flags
        layout(std430, set = 0, binding = 6) readonly buffer Skin { mat4 bones[]; } skin;
        layout(std430, set = 0, binding = 8) readonly buffer Morphs { vec4 deltas[]; } morphs;

        out vec3 vWorld;
        out vec3 vNormal;
        out vec4 vTangent;
        out vec2 vUv;
        flat out uint vMaterial;

        void main()
        {
            mat4 model = mat4(vec4(aInstance0.xyz, 0.0), vec4(aInstance1.xyz, 0.0), vec4(aInstance2.xyz, 0.0), vec4(aInstance3.xyz, 1.0));
            mat4 sk = mat4(1.0);
            if (dot(aWeights, vec4(1.0)) > 0.0)
            {
                uint b = aInstanceData.x;
                sk = skin.bones[b + aBones.x] * aWeights.x + skin.bones[b + aBones.y] * aWeights.y
                   + skin.bones[b + aBones.z] * aWeights.z + skin.bones[b + aBones.w] * aWeights.w;
            }
            mat4 world = model * sk;
            vec3 local = aPosition;
            uint slot = floatBitsToUint(aColour.x);
            if (aInstanceData.w != 0u && slot != 0u && slot < 0x1000000u) local += morphs.deltas[aInstanceData.w + slot].xyz;
            vec4 p = world * vec4(local, 1.0);
            vWorld = p.xyz;
            mat3 n = mat3(world);
            vNormal = n * aNormal;
            vTangent = vec4(n * aTangent.xyz, aTangent.w);
            vUv = aUv;
            vMaterial = aInstanceData.y;
            gl_Position = view.viewProjection * p;
        }
        """;

    // The texture lookups are non-uniform across a draw (the instances differ in material) but constant over a primitive, so the
    // derivatives of the quad are fine. The cut-outs discard after every lookup (no derivatives after an OpKill).
    const string Lookup = """
        vec4 tex(uint i, vec2 uv) { return texture(textures2D[nonuniformEXT(i)], uv); }
        """;

    public static string Fragment() => "#version 450\n" + NativeShaders.Prelude(PushMembers) + NativeShaders.AtmosphereFunctions + Structs + Lookup + """
        in vec3 vWorld;
        in vec3 vNormal;
        in vec4 vTangent;
        in vec2 vUv;
        flat in uint vMaterial;
        layout(location = 0) out vec4 fragColour;

        bool flag(uint f, uint bit) { return (f & bit) != 0u; }

        // A clothing "vest" layer drawn on the body (docs/characters.md): the coverage is the vest normal map's alpha.
        void vest(uint d, uint nm, uint c, bool coloured, vec3 shirt, vec2 uv, inout vec3 albedo, inout vec4 normalMap, inout float gloss)
        {
            vec4 v = tex(d, uv);
            vec4 vn = tex(nm, uv);
            if (coloured) v.rgb = mix(v.rgb, shirt * dot(v.rgb, vec3(1.0 / 3.0)), tex(c, uv).r);
            albedo = mix(albedo, v.rgb, vn.a);
            normalMap.wy = mix(normalMap.wy, vn.xy, vn.a);
            gloss = mix(gloss, v.a, vn.a);
        }

        void main()
        {
            if (pc.wireframe != 0u) { fragColour = vec4(pc.flatColour, 1.0); return; }
            CharMaterial m = materials.items[vMaterial];
            uint f = m.flags;
            vec3 n = normalize(vNormal);
            if (!gl_FrontFacing) n = -n;
            vec2 uv = vUv;
            vec3 albedo = vec3(0.72);
            float gloss = 0.2;
            vec4 nm = vec4(0.5, 0.5, 1.0, 0.5);
            bool swizzled = flag(f, 4u);
            bool cut = false;

            if (m.shading == 2u)
            {
                vec4 t = flag(f, 1u) ? tex(m.tex[0], uv) : vec4(1.0);
                cut = flag(f, 1u) && dot(t, m.alphaChannel) < m.alphaThreshold;
                float brightness = dot(t, m.diffuseChannel);
                albedo = brightness * m.hairColour.rgb;
                gloss = brightness * 0.3;
            }
            else if (m.shading == 1u)
            {
                vec4 body = flag(f, 1u) ? tex(m.tex[0], uv) : vec4(0.7, 0.6, 0.5, 0.2);
                if (flag(f, 2u)) nm = tex(m.tex[1], uv);
                float tone = flag(f, 64u) ? tex(m.tex[6], uv).r : 0.0;
                vec2 huv = uv + vec2(0.0, 1.0);          // head UVs sit in v -1..0
                if (flag(f, 8u)) body += tex(m.tex[3], huv);
                if (flag(f, 16u) && flag(f, 2u)) nm += tex(m.tex[4], huv);
                if (flag(f, 32u)) tone += tex(m.tex[5], huv).r;
                body.rgb *= 1.0 - m.skinTone.rgb * tone;
                if (flag(f, 128u))
                {
                    vec4 h = tex(m.tex[7], huv);
                    body.rgb = mix(body.rgb, m.hairColour.rgb * clamp(dot(h, m.hairMult), 0.0, 1.0), clamp(dot(h, m.hairAlpha), 0.0, 1.0));
                }
                if (flag(f, 256u))
                {
                    float b = clamp(dot(tex(m.tex[8], huv), m.beardAlpha), 0.0, 1.0);
                    body.rgb = mix(body.rgb, m.hairColour.rgb * b, b);
                }
                albedo = body.rgb;
                gloss = body.a;
                if (m.vestCount > 0u) vest(m.tex[9], m.tex[10], m.tex[11], flag(f, 2048u), m.shirtColour.rgb, uv, albedo, nm, gloss);
                if (m.vestCount > 1u) vest(m.tex[12], m.tex[13], m.tex[14], flag(f, 4096u), m.shirtColour.rgb, uv, albedo, nm, gloss);
                if (m.vestCount > 2u) vest(m.tex[15], m.tex[16], m.tex[17], flag(f, 8192u), m.shirtColour.rgb, uv, albedo, nm, gloss);
                swizzled = true;                          // character normals: X in alpha, Y in green
            }
            else
            {
                vec4 d = flag(f, 1u) ? tex(m.tex[0], uv) : vec4(0.72, 0.72, 0.72, 0.2);
                bool clip = flag(f, 512u);
                if (flag(f, 2u) || clip) nm = tex(m.tex[1], uv);
                vec3 cm = flag(f, 1024u) ? tex(m.tex[2], uv).rgb : vec3(0.0);
                cut = clip && nm.a < 0.6;
                if (flag(f, 1024u))
                {
                    float intensity = dot(d.rgb, vec3(1.0 / 3.0));
                    d.rgb = mix(d.rgb, m.colour1.rgb * mix(intensity, 1.0, m.colour1.a), cm.r);
                    d.rgb = mix(d.rgb, m.colour2.rgb * mix(intensity, 1.0, m.colour2.a), cm.g);
                }
                albedo = d.rgb;
                gloss = d.a;
            }
            if (cut) discard;

            if (flag(f, 2u) && m.shading != 2u && dot(vTangent.xyz, vTangent.xyz) > 1e-8)
            {
                vec3 t = normalize(vTangent.xyz - n * dot(n, vTangent.xyz));
                vec3 b = cross(n, t) * vTangent.w;
                vec3 tn;
                if (swizzled) { vec2 xy = vec2(nm.a, nm.g) * 2.0 - 1.0; tn = vec3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy)))); }
                else tn = nm.xyz * 2.0 - 1.0;
                n = normalize(t * tn.x + b * tn.y + n * tn.z);
            }

            // Lit as the world's other meshes are (Shaders.MeshFragment): the game's deferred lighting with the game's sky, else the simple model.
            vec3 v = normalize(view.eye - vWorld);
            gloss = clamp(gloss, 0.0, 1.0);
            vec3 colour;
            if (view.fogDistance > 0.0 && uAtmoParams.x > 0.5) colour = kenshiLight(albedo, n, v, gloss, vWorld);
            else
            {
                vec3 l = normalize(view.lightDir);
                float diff = max(dot(n, l), 0.0);
                vec3 ambient = mix(vec3(0.22, 0.20, 0.18), vec3(0.42, 0.45, 0.50), 0.5 + 0.5 * n.y);
                vec3 h = normalize(l + v);
                float spec = pow(max(dot(n, h), 0.0), 8.0 + 56.0 * gloss) * gloss * 0.5;
                vec3 sunLight = vec3(1.0, 0.97, 0.92);
                colour = albedo * (ambient + diff * sunLight) + spec * diff * sunLight;
            }
            if (view.fogDistance > 0.0) colour = atmoApply(colour, view.eye, vWorld);
            fragColour = vec4(colour, 1.0);
        }
        """;

    /// <summary>The sun's shadow caster: the cut-outs of <see cref="Fragment"/> (hair cards, worn cloth) and the game's caster bias.</summary>
    public static string DepthFragment()
    {
        string stock = NativeShaders.DepthFragment;
        const string stockMain = "void main() { shadowWriteDepth(); }";
        if (!stock.Contains(stockMain)) throw new InvalidOperationException("CharacterShaders: the stock depth fragment changed");
        return stock.Replace(stockMain, "") + Structs + Lookup + """
        in vec2 vUv;
        flat in uint vMaterial;
        void main()
        {
            CharMaterial m = materials.items[vMaterial];
            uint f = m.flags;
            bool cut = false;
            if (m.shading == 2u && (f & 1u) != 0u)
                cut = dot(tex(m.tex[0], vUv), m.alphaChannel) < m.alphaThreshold;
            else if (m.shading == 0u && (f & 512u) != 0u)
                cut = tex(m.tex[1], vUv).a < 0.6;
            if (cut) discard;
            shadowWriteDepth();
        }
        """;
    }

    public static string DepthVertex() => Vertex();
}

/// <summary>The C# side of <c>CharMaterial</c> (std430, 256 bytes): the part's textures by bindless index and its colours.</summary>
[StructLayout(LayoutKind.Sequential)]
struct CharacterMaterialRecord
{
    public uint Shading, Flags, VestCount;
    public float AlphaThreshold;
    public TextureSlots Tex;
    public Vector4 ShirtColour, SkinTone, HairColour, HairAlpha, HairMult, BeardAlpha, DiffuseChannel, AlphaChannel, Colour1, Colour2;

    public const int Size = 256;
}

[InlineArray(20)]
struct TextureSlots { uint first; }

/// <summary>The C# side of the push block (<c>flatColour</c>, <c>wireframe</c>): 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
struct CharacterPush
{
    public Vector3 FlatColour;
    public uint Wireframe;
}

/// <summary>One instance (80 bytes): the model matrix whose rows the vertex program reads, the palette's first bone, the material's index.</summary>
[StructLayout(LayoutKind.Sequential)]
struct CharacterInstance80
{
    public Matrix4x4 Model;
    public uint BoneBase, Material, Flags, MorphBase;

    public const int Size = 80;
}
