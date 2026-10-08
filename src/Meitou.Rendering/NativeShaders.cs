using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The native model's shader text (docs/renderer-native.md 3.3, step O): the prelude every native program starts with, and the native
/// variants of the shared GLSL. A native variant is made from the legacy text by <see cref="Port"/>, which rewrites only declarations: the
/// <c>#version 330 core</c> line becomes <c>#version 450</c> plus the prelude, every loose <c>uniform</c> declaration becomes <c>#define</c>s
/// over the new members (a sampler over a bindless lookup), and the shadow blocks get their set and binding. Every function body stays the
/// legacy text, character for character, so the maths is the same source; the values reaching it are the same C# floats, moved from a
/// default block into <see cref="FrameConstants"/>, <see cref="ViewConstants"/> or push constants. Compiled with Vulkan's strict rules (relaxed
/// rules off), so a uniform the maps do not cover is an error rather than a silent default block: <see cref="Port"/> throws naming it.
/// </summary>
/// <remarks>
/// The layout (steward, wave 3b): set 0 one push-descriptor set per native segment (<see cref="NativeFrame"/>) with <see cref="FrameConstants"/>
/// (binding 0), the three shadow blocks as the GL code binds them (1 receiver, 2 caster, 3 Meitou: they are GL buffers VkGl renames, and
/// the caster block changes between cascades, so they are taken per segment, not per frame), <see cref="ViewConstants"/> (4) and the skinning matrices (5); push constants of up to <see cref="PushBytes"/>
/// bytes, the same range in every native program so all native pipeline layouts are compatible and the sets are bound once per segment;
/// set 1 the bindless table (<see cref="BindlessTable.Declarations"/>).
/// <para>
/// The pushed set is set 0, as the legacy programs' is, and the table is set 1, not the other way round: with the table at 0 and set 1
/// pushed, a legacy program's push of its set 0 after a native segment crashed the validation layer (1.4.363) inside
/// vkCmdPushDescriptorSetKHR with no error reported (observed: SeamTests' native-then-legacy test, and the forest view after the foliage's
/// step O segments, with MEITOU_VK_VALIDATION=1 and =sync; a minidump puts the fault in VkLayer_khronos_validation.dll). With the push
/// always at set 0 both run clean. Without the layer both orders render the same.
/// </para>
/// </remarks>
static partial class NativeShaders
{
    public const int BindlessSet = 1, FrameSet = 0;
    public const uint FrameBinding = 0, ReceiverBinding = 1, CasterBinding = 2, MeitouBinding = 3, ViewBinding = 4, BonesBinding = 5;
    /// <summary>The push-constant range of every native program (the guaranteed minimum).</summary>
    public const uint PushBytes = 128;

    /// <summary>Set 0, binding 0: the frame's atmosphere (the values <c>SkyRenderer.Apply</c> sets) and the bindless indices of the frame's
    /// shared textures. Layout std140; <see cref="FrameConstants"/> is the C# side.</summary>
    public const string FrameBlock = """
        layout(std140, set = 0, binding = 0) uniform FrameConstants
        {
            vec4 atmoSun;
            vec4 atmoLight;
            vec3 atmoSunLight;
            vec4 atmoParams;
            vec4 atmoTau;
            vec3 atmoTint;
            vec4 atmoFog;
            vec3 atmoFogColour;
            vec4 atmoSimple;
            vec4 atmoHaze;
            vec4 atmoHazeCloud;
            vec4 atmoAltitude;
            vec4 atmoMaps;
            uint atmoIrradiance;
            uint atmoSpecular;
            uint atmoAmbientMap;
            uint shadowMap;
            uint shadowNoise;
            uint shadowTerrain;
            uint shadowBlocker;
            uint shadowLandmark;
        } frame;

        """;

    /// <summary>Set 0, binding 4: what one view (a native segment's camera) shares. Layout std140; <see cref="ViewConstants"/> is the C# side.</summary>
    public const string ViewBlock = """
        layout(std140, set = 0, binding = 4) uniform ViewConstants
        {
            mat4 viewProjection;
            mat4 previousViewProjection;
            vec3 eye;
            float time;
            vec3 lightDir;
            float previousTime;
            vec3 fogColour;
            float fogDistance;
            vec2 nearPlanes;
            vec2 jitterNdc;
        } view;

        """;

    /// <summary>Set 0, binding 5: the shared mesh shader's skinning matrices (<c>uBones</c>).</summary>
    public const string BonesBlock = """
        layout(std140, set = 0, binding = 5) uniform MeshBones
        {
            mat4 bones[128];
        } meshBones;

        """;

    /// <summary>The members of the shared mesh shaders' push constants (<see cref="MeshPush"/> is the C# side; std430, 128 bytes).</summary>
    public const string MeshPushMembers = """
            vec3 tint;
            float triplanarScale;
            vec3 flatColour;
            float alphaThreshold;
            vec2 tile;
            float specular;
            int alphaSource;
            int alphaChannel;
            int greyChannel;
            uint diffuse;
            uint normal;
            uint diffuse2;
            uint normal2;
            uint headDiffuse;
            uint headNormal;
            bool hasHead;
            bool normalSwizzled;
            bool hasDiffuse;
            bool hasNormal;
            bool hasDual;
            bool triplanar;
            bool emissive;
            bool useVertexColour;
            bool wireframe;
            bool skinned;
            bool coverage;
            uint spare;
        """;

    /// <summary>The push-constant block (instance <c>pc</c>) with <paramref name="members"/>.</summary>
    public static string PushBlock(string members) => $"layout(push_constant) uniform Push\n{{\n{members}\n}} pc;\n";

    /// <summary>
    /// What follows <c>#version 450</c> in every native program: the bindless arrays (set 1), the frame, view and bones blocks (set 0) and the
    /// push-constant block. <c>gl_VertexID</c> is GL's name; with relaxed rules off it is <c>gl_VertexIndex</c> (the same value: the native
    /// draws start at vertex 0).
    /// </summary>
    public static string Prelude(string pushMembers) =>
        BindlessTable.Declarations(BindlessSet) + FrameBlock + ViewBlock + BonesBlock + PushBlock(pushMembers) + "#define gl_VertexID gl_VertexIndex\n";

    /// <summary>The atmosphere's and the shadows' uniforms (<see cref="AtmosphereShaders"/>, <see cref="ShadowShaders"/>, <see cref="MeitouShadowShaders"/>):
    /// <see cref="FrameConstants"/> members and the bindless arrays they index.</summary>
    public static readonly IReadOnlyDictionary<string, string> FrameMap = new Dictionary<string, string>
    {
        ["uAtmoSun"] = "frame.atmoSun", ["uAtmoLight"] = "frame.atmoLight", ["uAtmoSunLight"] = "frame.atmoSunLight", ["uAtmoParams"] = "frame.atmoParams",
        ["uAtmoTau"] = "frame.atmoTau", ["uAtmoTint"] = "frame.atmoTint", ["uAtmoFog"] = "frame.atmoFog", ["uAtmoFogColour"] = "frame.atmoFogColour",
        ["uAtmoSimple"] = "frame.atmoSimple", ["uAtmoHaze"] = "frame.atmoHaze", ["uAtmoHazeCloud"] = "frame.atmoHazeCloud",
        ["uAtmoAltitude"] = "frame.atmoAltitude", ["uAtmoMaps"] = "frame.atmoMaps",
        ["uAtmoIrradiance"] = "texturesCube[frame.atmoIrradiance]", ["uAtmoSpecular"] = "texturesCube[frame.atmoSpecular]",
        ["uAtmoAmbientMap"] = "textures2D[frame.atmoAmbientMap]",
        ["uShadowMap"] = "shadowTextures[frame.shadowMap]", ["uShadowNoise"] = "textures2D[frame.shadowNoise]",
        ["uShadowTerrain"] = "textures2D[frame.shadowTerrain]", ["uShadowBlocker"] = "textures2D[frame.shadowBlocker]",
        ["uShadowLandmark"] = "shadowTextures[frame.shadowLandmark]",
    };

    /// <summary>The per-view uniforms the world shaders share: <see cref="ViewConstants"/> members.</summary>
    public static readonly IReadOnlyDictionary<string, string> ViewMap = new Dictionary<string, string>
    {
        ["uViewProjection"] = "view.viewProjection", ["uPreviousViewProjection"] = "view.previousViewProjection", ["uEye"] = "view.eye",
        ["uTime"] = "view.time", ["uLightDir"] = "view.lightDir", ["uPreviousTime"] = "view.previousTime", ["uFogColour"] = "view.fogColour",
        ["uFogDistance"] = "view.fogDistance", ["uNearPlanes"] = "view.nearPlanes", ["uJitterNdc"] = "view.jitterNdc",
    };

    /// <summary>The shared mesh shaders' own uniforms (<see cref="Shaders.MeshVertex"/>, <see cref="Shaders.MeshFragment"/>,
    /// <see cref="ShadowShaders.MeshDepthFragment"/>; foliage's <c>uCoverage</c>): <see cref="MeshPush"/> members, the textures by their
    /// bindless index. <c>uModel</c> is not here: each consumer supplies its own (foliage: per-instance rows).</summary>
    public static readonly IReadOnlyDictionary<string, string> MeshMap = new Dictionary<string, string>
    {
        ["uSkinned"] = "pc.skinned", ["uBones"] = "meshBones.bones",
        ["uDiffuse"] = "textures2D[pc.diffuse]", ["uNormal"] = "textures2D[pc.normal]", ["uDiffuse2"] = "textures2D[pc.diffuse2]",
        ["uNormal2"] = "textures2D[pc.normal2]", ["uHeadDiffuse"] = "textures2D[pc.headDiffuse]", ["uHeadNormal"] = "textures2D[pc.headNormal]",
        ["uHasHead"] = "pc.hasHead", ["uNormalSwizzled"] = "pc.normalSwizzled", ["uHasDiffuse"] = "pc.hasDiffuse", ["uHasNormal"] = "pc.hasNormal",
        ["uHasDual"] = "pc.hasDual", ["uTriplanar"] = "pc.triplanar", ["uTriplanarScale"] = "pc.triplanarScale", ["uTile"] = "pc.tile",
        ["uAlphaSource"] = "pc.alphaSource", ["uAlphaChannel"] = "pc.alphaChannel", ["uGreyChannel"] = "pc.greyChannel", ["uTint"] = "pc.tint",
        ["uAlphaThreshold"] = "pc.alphaThreshold", ["uEmissive"] = "pc.emissive", ["uUseVertexColour"] = "pc.useVertexColour",
        ["uSpecular"] = "pc.specular", ["uWireframe"] = "pc.wireframe", ["uFlatColour"] = "pc.flatColour", ["uCoverage"] = "pc.coverage",
    };

    /// <summary>The shadow blocks' bindings in set 0.</summary>
    public static readonly IReadOnlyDictionary<string, uint> BlockBindings = new Dictionary<string, uint>
    {
        [ShadowShaders.ReceiverBlock] = ReceiverBinding, [ShadowShaders.CasterBlock] = CasterBinding, [MeitouShadowShaders.Block] = MeitouBinding,
    };

    /// <summary>The standard maps merged: frame, view and mesh (later ones win, then <paramref name="own"/>).</summary>
    public static Dictionary<string, string> Map(IReadOnlyDictionary<string, string>? own = null)
    {
        var map = new Dictionary<string, string>(FrameMap);
        foreach (var (k, v) in ViewMap) map[k] = v;
        foreach (var (k, v) in MeshMap) map[k] = v;
        if (own is not null) foreach (var (k, v) in own) map[k] = v;
        return map;
    }

    /// <summary>
    /// The native variant of a legacy GLSL text: <c>#version 330 core</c> becomes <c>#version 450</c> and the prelude (with the push block of
    /// <paramref name="pushMembers"/>; a text without a version line, such as <see cref="AtmosphereShaders.Functions"/>, gets none), each
    /// loose <c>uniform T a, b;</c> becomes <c>#define a ...</c> lines from <paramref name="map"/> (default: <see cref="Map"/>), and each
    /// <c>layout(std140) uniform Block</c> gets set <see cref="FrameSet"/> and its binding from <see cref="BlockBindings"/>. Nothing else is touched. Throws when a
    /// uniform or a block is not mapped.
    /// </summary>
    public static string Port(string legacy, IReadOnlyDictionary<string, string>? map = null, string pushMembers = MeshPushMembers)
    {
        map ??= Map();
        string text = VersionRegex().Replace(legacy, _ => "#version 450\n" + Prelude(pushMembers), 1);
        text = LooseUniformRegex().Replace(text, m =>
        {
            var sb = new StringBuilder();
            foreach (var raw in m.Groups["names"].Value.Split(','))
            {
                string name = raw.Trim();
                int bracket = name.IndexOf('[');
                if (bracket >= 0) name = name[..bracket].Trim();
                if (!map.TryGetValue(name, out var expression))
                    throw new InvalidOperationException($"NativeShaders.Port: uniform '{name}' ({m.Groups["type"].Value}) has no native mapping");
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(m.Groups["indent"].Value).Append("#define ").Append(name).Append(' ').Append(expression);
            }
            return sb.ToString();
        });
        text = BlockRegex().Replace(text, m =>
        {
            string name = m.Groups["name"].Value;
            if (!BlockBindings.TryGetValue(name, out uint binding))
                throw new InvalidOperationException($"NativeShaders.Port: uniform block '{name}' has no native binding");
            return $"{m.Groups["indent"].Value}layout(std140, set = {FrameSet}, binding = {binding}) uniform {name}";
        });
        return text;
    }

    [GeneratedRegex(@"#version\s+330\s+core[^\n]*\n?")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"(?m)^(?<indent>[ \t]*)uniform[ \t]+(?<type>\w+)[ \t]+(?<names>[^;{()]+);[^\n\r]*")]
    private static partial Regex LooseUniformRegex();

    [GeneratedRegex(@"(?m)^(?<indent>[ \t]*)layout\(std140\)[ \t]+uniform[ \t]+(?<name>\w+)")]
    private static partial Regex BlockRegex();

    // ---- the native variants of the shared text (docs/renderer-native.md 3.3) ----

    /// <summary><see cref="Shaders.MeshVertex"/> with <paramref name="own"/> mapping <c>uModel</c> (and anything else the consumer adds).</summary>
    public static string MeshVertex(IReadOnlyDictionary<string, string> own) => Port(Shaders.MeshVertex, Map(own));
    /// <summary><see cref="Shaders.MeshFragment"/> (embeds <see cref="AtmosphereShaders.Functions"/> and the shadow receivers).</summary>
    public static string MeshFragment(IReadOnlyDictionary<string, string>? own = null) => Port(Shaders.MeshFragment, Map(own));
    /// <summary><see cref="AtmosphereShaders.Functions"/> (embeds <see cref="ShadowShaders.Functions"/> and <see cref="MeitouShadowShaders.Functions"/>);
    /// for a native text that already has the prelude.</summary>
    public static string AtmosphereFunctions => Port(AtmosphereShaders.Functions, Map());
    /// <summary><see cref="ShadowShaders.Functions"/> alone (the shadow receivers).</summary>
    public static string ShadowFunctions => Port(ShadowShaders.Functions, Map());
    /// <summary><see cref="ShadowShaders.DepthFragment"/> (the caster block at set 0, binding 2).</summary>
    public static string DepthFragment => Port(ShadowShaders.DepthFragment, Map());
    /// <summary><see cref="ShadowShaders.MeshDepthFragment"/>: the mesh shaders' cut-out on <see cref="MeshPush"/>.</summary>
    public static string MeshDepthFragment(IReadOnlyDictionary<string, string>? own = null) => Port(ShadowShaders.MeshDepthFragment, Map(own));
    /// <summary><see cref="PostProcessShaders.Vertex"/> (the full-screen triangle).</summary>
    public static string PostProcessVertex => Port(PostProcessShaders.Vertex, Map());
}

/// <summary>The C# side of <see cref="NativeShaders.FrameBlock"/> (std140; offsets checked against the reflection by a test).</summary>
[StructLayout(LayoutKind.Explicit, Size = 240)]
struct FrameConstants
{
    [FieldOffset(0)] public Vector4 AtmoSun;
    [FieldOffset(16)] public Vector4 AtmoLight;
    [FieldOffset(32)] public Vector3 AtmoSunLight;
    [FieldOffset(48)] public Vector4 AtmoParams;
    [FieldOffset(64)] public Vector4 AtmoTau;
    [FieldOffset(80)] public Vector3 AtmoTint;
    [FieldOffset(96)] public Vector4 AtmoFog;
    [FieldOffset(112)] public Vector3 AtmoFogColour;
    [FieldOffset(128)] public Vector4 AtmoSimple;
    [FieldOffset(144)] public Vector4 AtmoHaze;
    [FieldOffset(160)] public Vector4 AtmoHazeCloud;
    [FieldOffset(176)] public Vector4 AtmoAltitude;
    [FieldOffset(192)] public Vector4 AtmoMaps;
    [FieldOffset(208)] public uint AtmoIrradiance;
    [FieldOffset(212)] public uint AtmoSpecular;
    [FieldOffset(216)] public uint AtmoAmbientMap;
    [FieldOffset(220)] public uint ShadowMap;
    [FieldOffset(224)] public uint ShadowNoise;
    [FieldOffset(228)] public uint ShadowTerrain;
    [FieldOffset(232)] public uint ShadowBlocker;
    [FieldOffset(236)] public uint ShadowLandmark;

    /// <summary>The frame-global uniform each member holds (<see cref="FrameGlobals"/> names, as <c>SkyRenderer</c> publishes them): offset and size.</summary>
    public static readonly (string Name, int Offset, int Size)[] Uniforms =
    [
        ("uAtmoSun", 0, 16), ("uAtmoLight", 16, 16), ("uAtmoSunLight", 32, 12), ("uAtmoParams", 48, 16), ("uAtmoTau", 64, 16),
        ("uAtmoTint", 80, 12), ("uAtmoFog", 96, 16), ("uAtmoFogColour", 112, 12), ("uAtmoSimple", 128, 16), ("uAtmoHaze", 144, 16),
        ("uAtmoHazeCloud", 160, 16), ("uAtmoAltitude", 176, 16), ("uAtmoMaps", 192, 16),
    ];

    /// <summary>The frame-global textures, the array each is registered in and the member that holds its index.</summary>
    public static readonly (string Name, BindlessKind Kind, int Offset)[] Textures =
    [
        ("uAtmoIrradiance", BindlessKind.Cube, 208), ("uAtmoSpecular", BindlessKind.Cube, 212), ("uAtmoAmbientMap", BindlessKind.Texture2D, 216),
        ("uShadowMap", BindlessKind.Shadow2D, 220), ("uShadowNoise", BindlessKind.Texture2D, 224), ("uShadowTerrain", BindlessKind.Texture2D, 228),
        ("uShadowBlocker", BindlessKind.Texture2D, 232), ("uShadowLandmark", BindlessKind.Shadow2D, 236),
    ];
}

/// <summary>The C# side of <see cref="NativeShaders.ViewBlock"/> (std140). Matrices as <c>WorldGl.Matrix</c> passes them (System.Numerics memory).</summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
struct ViewConstants
{
    [FieldOffset(0)] public Matrix4x4 ViewProjection;
    [FieldOffset(64)] public Matrix4x4 PreviousViewProjection;
    [FieldOffset(128)] public Vector3 Eye;
    [FieldOffset(140)] public float Time;
    [FieldOffset(144)] public Vector3 LightDir;
    [FieldOffset(156)] public float PreviousTime;
    [FieldOffset(160)] public Vector3 FogColour;
    [FieldOffset(172)] public float FogDistance;
    [FieldOffset(176)] public Vector2 NearPlanes;
    [FieldOffset(184)] public Vector2 JitterNdc;
}

/// <summary>The C# side of <see cref="NativeShaders.MeshPushMembers"/> (std430 push constants, 128 bytes). GLSL bools are 32-bit (0 / 1).</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
struct MeshPush
{
    [FieldOffset(0)] public Vector3 Tint;
    [FieldOffset(12)] public float TriplanarScale;
    [FieldOffset(16)] public Vector3 FlatColour;
    [FieldOffset(28)] public float AlphaThreshold;
    [FieldOffset(32)] public Vector2 Tile;
    [FieldOffset(40)] public float Specular;
    [FieldOffset(44)] public int AlphaSource;
    [FieldOffset(48)] public int AlphaChannel;
    [FieldOffset(52)] public int GreyChannel;
    [FieldOffset(56)] public uint Diffuse;
    [FieldOffset(60)] public uint Normal;
    [FieldOffset(64)] public uint Diffuse2;
    [FieldOffset(68)] public uint Normal2;
    [FieldOffset(72)] public uint HeadDiffuse;
    [FieldOffset(76)] public uint HeadNormal;
    [FieldOffset(80)] public uint HasHead;
    [FieldOffset(84)] public uint NormalSwizzled;
    [FieldOffset(88)] public uint HasDiffuse;
    [FieldOffset(92)] public uint HasNormal;
    [FieldOffset(96)] public uint HasDual;
    [FieldOffset(100)] public uint Triplanar;
    [FieldOffset(104)] public uint Emissive;
    [FieldOffset(108)] public uint UseVertexColour;
    [FieldOffset(112)] public uint Wireframe;
    [FieldOffset(116)] public uint Skinned;
    [FieldOffset(120)] public uint Coverage;
    [FieldOffset(124)] public uint Spare;
}
