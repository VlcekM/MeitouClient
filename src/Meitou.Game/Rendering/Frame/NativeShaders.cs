using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Frame;

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
/// the caster block changes between cascades, so they are taken per segment, not per frame), <see cref="ViewConstants"/> (4) and the skinning
/// matrices (5); push constants of up to <see cref="PushBytes"/>
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
    /// <summary>
    /// The descriptor set numbers: the bindless table is set 1, the pushed set of the frame's blocks set 0.
    /// </summary>
    public const int BindlessSet = 1, FrameSet = 0;

    /// <summary>
    /// Set 0's uniform buffer bindings: the frame block, the shadow receiver, caster and Meitou shadow blocks, the view block and the bone block.
    /// </summary>
    public const uint FrameBinding = 0, ReceiverBinding = 1, CasterBinding = 2, MeitouBinding = 3, ViewBinding = 4, BonesBinding = 5;

    /// <summary>
    /// The push-constant range of every native program (the guaranteed minimum).
    /// </summary>
    public const uint PushBytes = 128;

    /// <summary>
    /// Set 0, binding 0: the frame's atmosphere (the values <c>SkyRenderer.Apply</c> sets) and the bindless indices of the frame's shared textures.
    /// Layout std140; <see cref="FrameConstants"/> is the C# side.
    /// </summary>
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
            vec4 weatherWet;
            vec4 weatherDust;
            uint weatherGround;
            uint weatherDustNoise;
            vec4 fogVolumeEye;
            vec4 fogVolumeInfo;
            vec4 fogVolumeData[512];
            vec4 giParams;
            vec4 giGrid[4];
            uint giIrradiance;
            uint giDistance;
            uint giBase;
            vec4 lightGrid;
            uint lightCells;
            uint lightIndex;
            uint lightData;
            uint lampTiles;
            vec4 atmoNight;
            vec4 atmoCloud;
            vec4 atmoCloudWind;
            uint atmoClouds;
            uint atmoCloudsTile;
            float grassRayReach;
            float wetClock;
            float wetWorld;
            vec4 wetSplash[8];
        } frame;

        """;

    /// <summary>
    /// Set 0, binding 4: what one view (a native segment's camera) shares. Layout std140; <see cref="ViewConstants"/> is the C# side.
    /// </summary>
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
            uint interiorMask;
            uint interiorCut;
            uint cutVolumes;        // the shadow cut (WorldObjectRenderer.ShadowCut): how many of cutRanges apply
            uint cutPad;
            vec4 cutRanges[4];      // per volume: first plane, plane count, lowest and highest Y
            vec4 cutPlanes[32];     // footprint edges: inward X/Z normal in xy, offset in z (inside when dot(n, p.xz) >= z)
        } view;

        """;

    /// <summary>
    /// Set 0, binding 5: the shared mesh shader's skinning matrices (<c>uBones</c>).
    /// </summary>
    public const string BonesBlock = """
        layout(std140, set = 0, binding = 5) uniform MeshBones
        {
            mat4 bones[128];
        } meshBones;

        """;

    /// <summary>
    /// The members of the shared mesh shaders' push constants (<see cref="MeshPush"/> is the C# side; std430, 128 bytes).
    /// </summary>
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

    /// <summary>
    /// The push-constant block (instance <c>pc</c>) with <paramref name="members"/>.
    /// </summary>
    public static string PushBlock(string members) => $"layout(push_constant) uniform Push\n{{\n{members}\n}} pc;\n";

    /// <summary>
    /// What follows <c>#version 450</c> in every native program: the bindless arrays (set 1), the frame, view and bones blocks (set 0) and the
    /// push-constant block. <c>gl_VertexID</c> is GL's name; with relaxed rules off it is <c>gl_VertexIndex</c> (the same value: the native
    /// draws start at vertex 0).
    /// </summary>
    public static string Prelude(string pushMembers) =>
        BindlessTable.Declarations(BindlessSet) + FrameBlock + ViewBlock + BonesBlock + PushBlock(pushMembers)
        + "#define gl_VertexID gl_VertexIndex\n";

    /// <summary>
    /// The atmosphere's and the shadows' uniforms (<see cref="AtmosphereShaders"/>, <see cref="ShadowShaders"/>, see
    /// cref="MeitouShadowShaders"/>):
    /// <see cref="FrameConstants"/> members and the bindless arrays they index.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FrameMap = new Dictionary<string, string>
    {
        ["uAtmoSun"] = "frame.atmoSun",
        ["uAtmoLight"] = "frame.atmoLight",
        ["uAtmoSunLight"] = "frame.atmoSunLight",
        ["uAtmoParams"] = "frame.atmoParams",
        ["uAtmoTau"] = "frame.atmoTau",
        ["uAtmoTint"] = "frame.atmoTint",
        ["uAtmoFog"] = "frame.atmoFog",
        ["uAtmoFogColour"] = "frame.atmoFogColour",
        ["uAtmoSimple"] = "frame.atmoSimple",
        ["uAtmoHaze"] = "frame.atmoHaze",
        ["uAtmoHazeCloud"] = "frame.atmoHazeCloud",
        ["uAtmoAltitude"] = "frame.atmoAltitude",
        ["uAtmoMaps"] = "frame.atmoMaps",
        ["uAtmoNight"] = "frame.atmoNight",
        ["uAtmoCloud"] = "frame.atmoCloud",
        ["uAtmoCloudWind"] = "frame.atmoCloudWind",
        ["uAtmoClouds"] = "textures2D[frame.atmoClouds]",
        ["uAtmoCloudsTile"] = "textures2D[frame.atmoCloudsTile]",
        ["uAtmoIrradiance"] = "texturesCube[frame.atmoIrradiance]",
        ["uAtmoSpecular"] = "texturesCube[frame.atmoSpecular]",
        ["uAtmoAmbientMap"] = "textures2D[frame.atmoAmbientMap]",
        ["uShadowMap"] = "shadowArrays[frame.shadowMap]",
        ["uShadowNoise"] = "textures2D[frame.shadowNoise]",
        ["uShadowTerrain"] = "textures2D[frame.shadowTerrain]",
        ["uShadowBlocker"] = "textures2DArray[frame.shadowBlocker]",
        ["uShadowLandmark"] = "shadowTextures[frame.shadowLandmark]",
        ["uWeatherWet"] = "frame.weatherWet", ["uWeatherDust"] = "frame.weatherDust",
        ["uWeatherGround"] = "textures2D[frame.weatherGround]", ["uWeatherDustNoise"] = "textures2D[frame.weatherDustNoise]",
        ["uFogVolumeEye"] = "frame.fogVolumeEye", ["uFogVolumeInfo"] = "frame.fogVolumeInfo", ["uFogVolumeData"] = "frame.fogVolumeData",
        ["uGrassRayReach"] = "frame.grassRayReach", ["uWetClock"] = "frame.wetClock", ["uWetWorld"] = "frame.wetWorld",
        ["uWetSplash"] = "frame.wetSplash",
        ["uGiParams"] = "frame.giParams", ["uGiGrid"] = "frame.giGrid", ["uGiIrradiance"] = "textures2D[frame.giIrradiance]",
        ["uGiDistance"] = "textures2D[frame.giDistance]", ["uGiBase"] = "textures2D[frame.giBase]",
        ["uLightGrid"] = "frame.lightGrid", ["uLightCells"] = "textures2D[frame.lightCells]", ["uLightIndex"] = "textures2D[frame.lightIndex]",
        ["uLightData"] = "textures2D[frame.lightData]", ["uLampTiles"] = "textures2D[frame.lampTiles]",
    };

    /// <summary>
    /// The per-view uniforms the world shaders share: <see cref="ViewConstants"/> members.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ViewMap = new Dictionary<string, string>
    {
        ["uViewProjection"] = "view.viewProjection",
        ["uPreviousViewProjection"] = "view.previousViewProjection",
        ["uEye"] = "view.eye",
        ["uTime"] = "view.time",
        ["uLightDir"] = "view.lightDir",
        ["uPreviousTime"] = "view.previousTime",
        ["uFogColour"] = "view.fogColour",
        ["uFogDistance"] = "view.fogDistance",
        ["uNearPlanes"] = "view.nearPlanes",
        ["uJitterNdc"] = "view.jitterNdc",
        // The interior cut (InteriorMaskRenderer.WithCut; set by InteriorMaskRenderer.Apply for the main view only).
        ["uInteriorMask"] = "textures2D[view.interiorMask]",
        ["uInteriorCut"] = "(view.interiorCut != 0u)",
    };

    /// <summary>
    /// The shared mesh shaders' own uniforms (<see cref="Shaders.MeshVertex"/>, <see cref="Shaders.MeshFragment"/>,
    /// <see cref="ShadowShaders.MeshDepthFragment"/>; foliage's <c>uCoverage</c>): <see cref="MeshPush"/> members, the textures by their
    /// bindless index. <c>uModel</c> is not here: each consumer supplies its own (foliage: per-instance rows).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> MeshMap = new Dictionary<string, string>
    {
        ["uSkinned"] = "pc.skinned",
        ["uBones"] = "meshBones.bones",
        ["uDiffuse"] = "textures2D[pc.diffuse]",
        ["uNormal"] = "textures2D[pc.normal]",
        ["uDiffuse2"] = "textures2D[pc.diffuse2]",
        ["uNormal2"] = "textures2D[pc.normal2]",
        ["uHeadDiffuse"] = "textures2D[pc.headDiffuse]",
        ["uHeadNormal"] = "textures2D[pc.headNormal]",
        ["uHasHead"] = "pc.hasHead",
        ["uNormalSwizzled"] = "pc.normalSwizzled",
        ["uHasDiffuse"] = "pc.hasDiffuse",
        ["uHasNormal"] = "pc.hasNormal",
        ["uHasDual"] = "pc.hasDual",
        ["uTriplanar"] = "pc.triplanar",
        ["uTriplanarScale"] = "pc.triplanarScale",
        ["uTile"] = "pc.tile",
        ["uAlphaSource"] = "pc.alphaSource",
        ["uAlphaChannel"] = "pc.alphaChannel",
        ["uGreyChannel"] = "pc.greyChannel",
        ["uTint"] = "pc.tint",
        ["uAlphaThreshold"] = "pc.alphaThreshold",
        ["uEmissive"] = "pc.emissive",
        ["uUseVertexColour"] = "pc.useVertexColour",
        ["uSurface"] = "pc.spare",
        ["uSpecular"] = "pc.specular",
        ["uWireframe"] = "pc.wireframe",
        ["uFlatColour"] = "pc.flatColour",
        ["uCoverage"] = "pc.coverage",
    };

    /// <summary>
    /// The shadow blocks' bindings in set 0.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, uint> BlockBindings = new Dictionary<string, uint>
    {
        [ShadowShaders.ReceiverBlock] = ReceiverBinding,
        [ShadowShaders.CasterBlock] = CasterBinding,
        [MeitouShadowShaders.Block] = MeitouBinding,
    };

    /// <summary>
    /// The standard maps merged: frame, view and mesh (later ones win, then <paramref name="own"/>).
    /// </summary>
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
    /// <c>layout(std140) uniform Block</c> gets set <see cref="FrameSet"/> and its binding from <see cref="BlockBindings"/>. Nothing else is touched.
    /// Throws when a uniform or a block is not mapped.
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

    /// <summary>
    /// The GLSL 3.30 version line the port replaces.
    /// </summary>
    [GeneratedRegex(@"#version\s+330\s+core[^\n]*\n?")]
    private static partial Regex VersionRegex();

    /// <summary>
    /// A loose (default-block) uniform declaration: its indent, type and names.
    /// </summary>
    [GeneratedRegex(@"(?m)^(?<indent>[ \t]*)uniform[ \t]+(?<type>\w+)[ \t]+(?<names>[^;{()]+);[^\n\r]*")]
    private static partial Regex LooseUniformRegex();

    /// <summary>
    /// A std140 uniform block's declaration, given its native set and binding by the port.
    /// </summary>
    [GeneratedRegex(@"(?m)^(?<indent>[ \t]*)layout\(std140\)[ \t]+uniform[ \t]+(?<name>\w+)")]
    private static partial Regex BlockRegex();

    // ---- the native variants of the shared text (docs/renderer-native.md 3.3) ----

    /// <summary>
    /// <see cref="Shaders.MeshVertex"/> with <paramref name="own"/> mapping <c>uModel</c> (and anything else the consumer adds).
    /// </summary>
    public static string MeshVertex(IReadOnlyDictionary<string, string> own) => Port(Shaders.MeshVertex, Map(own));

    /// <summary>
    /// <see cref="Shaders.MeshFragment"/> (embeds <see cref="AtmosphereShaders.Functions"/> and the shadow receivers).
    /// </summary>
    public static string MeshFragment(IReadOnlyDictionary<string, string>? own = null) => Port(Shaders.MeshFragment, Map(own));

    /// <summary>
    /// <see cref="AtmosphereShaders.Functions"/> (embeds <see cref="ShadowShaders.Functions"/> and <see cref="MeitouShadowShaders.Functions"/>); for
    /// a native text that already has the prelude.
    /// </summary>
    public static string AtmosphereFunctions => Port(AtmosphereShaders.Functions, Map());

    /// <summary>
    /// <see cref="ShadowShaders.Functions"/> alone (the shadow receivers).
    /// </summary>
    public static string ShadowFunctions => Port(ShadowShaders.Functions, Map());

    /// <summary>
    /// <see cref="ShadowShaders.DepthFragment"/> (the caster block at set 0, binding 2).
    /// </summary>
    public static string DepthFragment => Port(ShadowShaders.DepthFragment, Map());

    /// <summary>
    /// <see cref="ShadowShaders.MeshDepthFragment"/>: the mesh shaders' cut-out on <see cref="MeshPush"/>.
    /// </summary>
    public static string MeshDepthFragment(IReadOnlyDictionary<string, string>? own = null) => Port(ShadowShaders.MeshDepthFragment, Map(own));

    /// <summary>
    /// <see cref="PostProcessShaders.Vertex"/> (the full-screen triangle).
    /// </summary>
    public static string PostProcessVertex => Port(PostProcessShaders.Vertex, Map());
}

/// <summary>
/// The C# side of <see cref="NativeShaders.FrameBlock"/> (std140; offsets checked against the reflection by a test).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 8848)]
unsafe struct FrameConstants
{
    /// <summary>
    /// <c>uAtmoSun</c>: xyz the direction to the sun (its real height).
    /// </summary>
    [FieldOffset(0)] public Vector4 AtmoSun;

    /// <summary>
    /// <c>uAtmoLight</c>: xyz the lighting direction (the sun's with y clamped to 0), w the environment light's factor.
    /// </summary>
    [FieldOffset(16)] public Vector4 AtmoLight;

    /// <summary>
    /// <c>uAtmoSunLight</c>: the sun colour's rgb times its w (docs/formats/lighting.md).
    /// </summary>
    [FieldOffset(32)] public Vector3 AtmoSunLight;

    /// <summary>
    /// <c>uAtmoParams</c>: x 1 game sky / 0 simple, y the distance where the physical haze is complete, z the physical haze's scale height in world
    /// units.
    /// </summary>
    [FieldOffset(48)] public Vector4 AtmoParams;

    /// <summary>
    /// <c>uAtmoTau</c>: the physical haze's optical depth straight up, rgb Rayleigh's, a Mie's.
    /// </summary>
    [FieldOffset(64)] public Vector4 AtmoTau;

    /// <summary>
    /// <c>uAtmoTint</c>: the weather's sky colour multiplier.
    /// </summary>
    [FieldOffset(80)] public Vector3 AtmoTint;

    /// <summary>
    /// <c>uAtmoFog</c>: the weather's fog, x start, y end, z enabled.
    /// </summary>
    [FieldOffset(96)] public Vector4 AtmoFog;

    /// <summary>
    /// <c>uAtmoFogColour</c>: the weather fog's colour.
    /// </summary>
    [FieldOffset(112)] public Vector3 AtmoFogColour;

    /// <summary>
    /// <c>uAtmoSimple</c>: the simple sky's fog colour and the distance of complete fog.
    /// </summary>
    [FieldOffset(128)] public Vector4 AtmoSimple;

    /// <summary>
    /// <c>uAtmoHaze</c>: Kenshi haze, x 1 when on, y start and z end of the atmosphere fog, w the weather fog's density (1 / distance).
    /// </summary>
    [FieldOffset(144)] public Vector4 AtmoHaze;

    /// <summary>
    /// <c>uAtmoHazeCloud</c>: Kenshi haze's horizon clouds, rgb the cloud colour, a how far the haze is pulled to it.
    /// </summary>
    [FieldOffset(160)] public Vector4 AtmoHazeCloud;

    /// <summary>
    /// <c>uAtmoAltitude</c>: x the physical haze's share, y the haze strength, z how far the eye is above the game's camera heights.
    /// </summary>
    [FieldOffset(176)] public Vector4 AtmoAltitude;

    /// <summary>
    /// <c>uAtmoMaps</c>: x irradiance cube, y specular cube, z ambient map present (0 none, else 1 + the night ambient floor), w half the world's
    /// width.
    /// </summary>
    [FieldOffset(192)] public Vector4 AtmoMaps;

    /// <summary>
    /// The bindless index of <c>uAtmoIrradiance</c>, the sky's irradiance cube.
    /// </summary>
    [FieldOffset(208)] public uint AtmoIrradiance;

    /// <summary>
    /// The bindless index of <c>uAtmoSpecular</c>, the sky's specular cube.
    /// </summary>
    [FieldOffset(212)] public uint AtmoSpecular;

    /// <summary>
    /// The bindless index of <c>uAtmoAmbientMap</c>, the ambient map.
    /// </summary>
    [FieldOffset(216)] public uint AtmoAmbientMap;

    /// <summary>
    /// The bindless index of <c>uShadowMap</c>, the shadow cascades (cascade i in layer i).
    /// </summary>
    [FieldOffset(220)] public uint ShadowMap;

    /// <summary>
    /// The bindless index of <c>uShadowNoise</c>, the shadows' noise texture.
    /// </summary>
    [FieldOffset(224)] public uint ShadowNoise;

    /// <summary>
    /// The bindless index of <c>uShadowTerrain</c>, the Meitou terrain shadow per world grid sample.
    /// </summary>
    [FieldOffset(228)] public uint ShadowTerrain;

    /// <summary>
    /// The bindless index of <c>uShadowBlocker</c>, the cascades' half-resolution depth for the blocker search, a layer per cascade.
    /// </summary>
    [FieldOffset(232)] public uint ShadowBlocker;

    /// <summary>
    /// The bindless index of <c>uShadowLandmark</c>, the landmarks' depth along the sun.
    /// </summary>
    [FieldOffset(236)] public uint ShadowLandmark;

    /// <summary>
    /// <c>uWeatherWet</c>: the weather's surface values, x wetness, y rain amount (rain / 50, saturated), z game time in hours.
    /// </summary>
    [FieldOffset(240)] public Vector4 WeatherWet;

    /// <summary>
    /// <c>uWeatherDust</c>: xyz the dust amounts (current, inside, slope; docs/formats/weather.md "Dust").
    /// </summary>
    [FieldOffset(256)] public Vector4 WeatherDust;

    /// <summary>
    /// The bindless index of <c>uWeatherGround</c>, the terrain's whole-world ground colour map (the dust colour).
    /// </summary>
    [FieldOffset(272)] public uint WeatherGround;

    /// <summary>
    /// The bindless index of <c>uWeatherDustNoise</c>, the dust noise texture.
    /// </summary>
    [FieldOffset(276)] public uint WeatherDustNoise;

    /// <summary>
    /// <c>uFogVolumeEye</c>: xyz the eye, w the fog blocks' light.
    /// </summary>
    [FieldOffset(288)] public Vector4 FogVolumeEye;

    /// <summary>
    /// <c>uFogVolumeInfo</c>: x the vec4s of <see cref="FogVolumeData"/> in use, y the sun colour's w.
    /// </summary>
    [FieldOffset(304)] public Vector4 FogVolumeInfo;
    /// <summary>
    /// The placed fog volumes in view, packed (<see cref="FogVolumeShaders"/>): <see cref="FogVolumeShaders.MaxData"/> vec4s.
    /// </summary>
    [FieldOffset(320)] public fixed float FogVolumeData[FogVolumeShaders.MaxData * 4];
    /// <summary>
    /// The probe GI (docs/render-gi.md): <c>uGiParams</c>, <c>uGiGrid[4]</c> and its three textures.
    /// </summary>
    [FieldOffset(8512)] public Vector4 GiParams;

    /// <summary>
    /// <c>uGiGrid[4]</c>: per cascade, the probe grid's place and spacing.
    /// </summary>
    [FieldOffset(8528)] public fixed float GiGrid[16];

    /// <summary>
    /// The bindless index of <c>uGiIrradiance</c>, the probes' irradiance.
    /// </summary>
    [FieldOffset(8592)] public uint GiIrradiance;

    /// <summary>
    /// The bindless index of <c>uGiDistance</c>, the probes' distances.
    /// </summary>
    [FieldOffset(8596)] public uint GiDistance;

    /// <summary>
    /// The bindless index of <c>uGiBase</c>, the probe columns' base heights.
    /// </summary>
    [FieldOffset(8600)] public uint GiBase;
    /// <summary>
    /// The lamps (docs/render-lights.md): <c>uLightGrid</c> and its three textures.
    /// </summary>
    [FieldOffset(8608)] public Vector4 LightGrid;

    /// <summary>
    /// The bindless index of <c>uLightCells</c>, the lamp grid's cells.
    /// </summary>
    [FieldOffset(8624)] public uint LightCells;

    /// <summary>
    /// The bindless index of <c>uLightIndex</c>, the lamp grid's index list.
    /// </summary>
    [FieldOffset(8628)] public uint LightIndex;

    /// <summary>
    /// The bindless index of <c>uLightData</c>, the lamps' data.
    /// </summary>
    [FieldOffset(8632)] public uint LightData;

    /// <summary>
    /// The bindless index of <c>uLampTiles</c>, the lamps' visibility cache (<c>Gi.LampTiles</c>).
    /// </summary>
    [FieldOffset(8636)] public uint LampTiles;

    /// <summary>
    /// Meitou night air (<c>uAtmoNight</c>, docs/formats/sky.md "Night air"): rgb the colour at full night, w its weight.
    /// </summary>
    [FieldOffset(8640)] public Vector4 AtmoNight;
    /// <summary>
    /// Meitou cloud shadows (<c>uAtmoCloud</c>, <c>uAtmoCloudWind</c>, docs/formats/clouds.md "Meitou cloud shadows"): the layer's numbers, the wind
    /// shift, and its two textures.
    /// </summary>
    [FieldOffset(8656)] public Vector4 AtmoCloud;

    /// <summary>
    /// <c>uAtmoCloudWind</c>: legacy clouds, xy the wind shift; volume clouds, xy the map's origin and z its span.
    /// </summary>
    [FieldOffset(8672)] public Vector4 AtmoCloudWind;

    /// <summary>
    /// The bindless index of <c>uAtmoClouds</c>, the cloud shadows' map.
    /// </summary>
    [FieldOffset(8688)] public uint AtmoClouds;

    /// <summary>
    /// The bindless index of <c>uAtmoCloudsTile</c>, the cloud shadows' tiling texture.
    /// </summary>
    [FieldOffset(8692)] public uint AtmoCloudsTile;

    /// <summary>
    /// How far from the eye the traced sun's rays see grass (<c>uGrassRayReach</c>, <c>GiScene.GrassRayParams</c>): 0 while they see none.
    /// </summary>
    [FieldOffset(8696)] public float GrassRayReach;

    /// <summary>
    /// Game seconds wrapping at 4096 (<c>uWetClock</c>, <c>WeatherSurfaces.Seconds</c>): what the characters' water beads swell and run off by.
    /// </summary>
    [FieldOffset(8700)] public float WetClock;

    /// <summary>
    /// 1 while the world shows its wetness: drops, runs and puddles (<c>uWetWorld</c>, <c>WeatherSurfaces.WetWorld</c>).
    /// </summary>
    [FieldOffset(8704)] public float WetWorld;

    /// <summary>
    /// The last feet in the puddles (<c>uWetSplash[8]</c>, <c>WeatherSurfaces.AddSplash</c>): x, z, the wet clock at the step, the strength (0:
    /// none).
    /// </summary>
    [FieldOffset(8720)] public fixed float WetSplash[32];

    /// <summary>
    /// The frame-global uniform each member holds (<see cref="FrameGlobals"/> names, as <c>SkyRenderer</c> publishes them): offset and size.
    /// </summary>
    public static readonly (string Name, int Offset, int Size)[] Uniforms =
    [
        ("uAtmoSun", 0, 16), ("uAtmoLight", 16, 16), ("uAtmoSunLight", 32, 12), ("uAtmoParams", 48, 16), ("uAtmoTau", 64, 16),
        ("uAtmoTint", 80, 12), ("uAtmoFog", 96, 16), ("uAtmoFogColour", 112, 12), ("uAtmoSimple", 128, 16), ("uAtmoHaze", 144, 16),
        ("uAtmoHazeCloud", 160, 16), ("uAtmoAltitude", 176, 16), ("uAtmoMaps", 192, 16), ("uWeatherWet", 240, 16), ("uWeatherDust", 256, 16),
        ("uFogVolumeEye", 288, 16), ("uFogVolumeInfo", 304, 16), ("uFogVolumeData", 320, FogVolumeShaders.MaxData * 16),
        ("uGiParams", 8512, 16), ("uGiGrid", 8528, 64),
        ("uLightGrid", 8608, 16), ("uAtmoNight", 8640, 16), ("uAtmoCloud", 8656, 16), ("uAtmoCloudWind", 8672, 16),
        ("uGrassRayReach", 8696, 4), ("uWetClock", 8700, 4), ("uWetWorld", 8704, 4), ("uWetSplash", 8720, 128),
    ];

    /// <summary>
    /// The frame-global textures, the array each is registered in and the member that holds its index.
    /// </summary>
    public static readonly (string Name, BindlessKind Kind, int Offset)[] Textures =
    [
        ("uAtmoIrradiance", BindlessKind.Cube, 208), ("uAtmoSpecular", BindlessKind.Cube, 212), ("uAtmoAmbientMap", BindlessKind.Texture2D, 216),
        ("uShadowMap", BindlessKind.ShadowArray, 220), ("uShadowNoise", BindlessKind.Texture2D, 224), ("uShadowTerrain", BindlessKind.Texture2D, 228),
        ("uShadowBlocker", BindlessKind.Texture2DArray, 232), ("uShadowLandmark", BindlessKind.Shadow2D, 236),
        ("uWeatherGround", BindlessKind.Texture2D, 272), ("uWeatherDustNoise", BindlessKind.Texture2D, 276),
        ("uGiIrradiance", BindlessKind.Texture2D, 8592), ("uGiDistance", BindlessKind.Texture2D, 8596), ("uGiBase", BindlessKind.Texture2D, 8600),
        ("uLightCells", BindlessKind.Texture2D, 8624), ("uLightIndex", BindlessKind.Texture2D, 8628), ("uLightData", BindlessKind.Texture2D, 8632),
        ("uLampTiles", BindlessKind.Texture2D, 8636),
        ("uAtmoClouds", BindlessKind.Texture2D, 8688), ("uAtmoCloudsTile", BindlessKind.Texture2D, 8692),
    ];
}

/// <summary>
/// The C# side of <see cref="NativeShaders.ViewBlock"/> (std140). Matrices as <c>WorldGl.Matrix</c> passes them (System.Numerics memory).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 784)]
struct ViewConstants
{
    /// <summary>
    /// The view's view-projection matrix.
    /// </summary>
    [FieldOffset(0)] public Matrix4x4 ViewProjection;

    /// <summary>
    /// The previous frame's view-projection matrix (for motion vectors).
    /// </summary>
    [FieldOffset(64)] public Matrix4x4 PreviousViewProjection;

    /// <summary>
    /// The eye in world units.
    /// </summary>
    [FieldOffset(128)] public Vector3 Eye;

    /// <summary>
    /// <c>uTime</c>: the shaders' animation time (the foliage's sway phase).
    /// </summary>
    [FieldOffset(140)] public float Time;

    /// <summary>
    /// <c>uLightDir</c>: the direction of the light.
    /// </summary>
    [FieldOffset(144)] public Vector3 LightDir;

    /// <summary>
    /// <c>uPreviousTime</c>: <see cref="Time"/> of the previous frame (for motion vectors).
    /// </summary>
    [FieldOffset(156)] public float PreviousTime;

    /// <summary>
    /// <c>uFogColour</c>: the distance haze's colour.
    /// </summary>
    [FieldOffset(160)] public Vector3 FogColour;

    /// <summary>
    /// <c>uFogDistance</c>: the distance haze's distance (0: off; the character renderer's icon and editor views pass negative marks).
    /// </summary>
    [FieldOffset(172)] public float FogDistance;

    /// <summary>
    /// <c>uNearPlanes</c>: the depth's planes, for turning a stored depth back into view distance.
    /// </summary>
    [FieldOffset(176)] public Vector2 NearPlanes;

    /// <summary>
    /// The projection's jitter in NDC (the upscalers' subpixel offset).
    /// </summary>
    [FieldOffset(184)] public Vector2 JitterNdc;

    /// <summary>
    /// The interior cut's mask texture, a bindless index (<c>InteriorMaskRenderer</c>; the main view only).
    /// </summary>
    [FieldOffset(192)] public uint InteriorMask;

    /// <summary>
    /// Whether the interior cut applies (non-zero), set by <c>InteriorMaskRenderer.Apply</c> for the main view only.
    /// </summary>
    [FieldOffset(196)] public uint InteriorCut;

    /// <summary>
    /// The shadow cut: how many of <see cref="CutRanges"/> apply.
    /// </summary>
    [FieldOffset(200)] public uint CutVolumes;

    /// <summary>
    /// Padding to the next vec4.
    /// </summary>
    [FieldOffset(204)] public uint CutPad;

    /// <summary>
    /// The shadow cut's volumes: per volume the first plane, the plane count, the lowest and highest Y.
    /// </summary>
    [FieldOffset(208)] public CutVolumeArray CutRanges;

    /// <summary>
    /// The shadow cut volumes' footprint edges: the inward X/Z normal in xy, the offset in z (inside when dot(n, p.xz) &gt;= z).
    /// </summary>
    [FieldOffset(272)] public CutPlaneArray CutPlanes;
}

/// <summary>
/// Four vec4s inline: <see cref="ViewConstants.CutRanges"/>.
/// </summary>
[System.Runtime.CompilerServices.InlineArray(4)]
struct CutVolumeArray
{
    /// <summary>
    /// The first element: the runtime lays the others out after it.
    /// </summary>
    Vector4 _first;
}

/// <summary>
/// Thirty-two vec4s inline: <see cref="ViewConstants.CutPlanes"/>.
/// </summary>
[System.Runtime.CompilerServices.InlineArray(32)]
struct CutPlaneArray
{
    /// <summary>
    /// The first element: the runtime lays the others out after it.
    /// </summary>
    Vector4 _first;
}

/// <summary>
/// The C# side of <see cref="NativeShaders.MeshPushMembers"/> (std430 push constants, 128 bytes). GLSL bools are 32-bit (0 / 1).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
struct MeshPush
{
    /// <summary>
    /// The colour the diffuse is multiplied by.
    /// </summary>
    [FieldOffset(0)] public Vector3 Tint;

    /// <summary>
    /// The triplanar mapping's scale, texture units per world unit.
    /// </summary>
    [FieldOffset(12)] public float TriplanarScale;

    /// <summary>
    /// The colour a wireframe draw is filled with.
    /// </summary>
    [FieldOffset(16)] public Vector3 FlatColour;

    /// <summary>
    /// The cut-out's alpha threshold (0: no cut-out).
    /// </summary>
    [FieldOffset(28)] public float AlphaThreshold;

    /// <summary>
    /// The texture coordinates' tiling.
    /// </summary>
    [FieldOffset(32)] public Vector2 Tile;

    /// <summary>
    /// The material's specular.
    /// </summary>
    [FieldOffset(40)] public float Specular;

    /// <summary>
    /// Where the cut-out's alpha comes from: 0 none, 1 the diffuse alpha, 2 the normal map's alpha, 3 a diffuse channel (<see cref="AlphaChannel"/>).
    /// </summary>
    [FieldOffset(44)] public int AlphaSource;

    /// <summary>
    /// The diffuse channel the alpha is read from when <see cref="AlphaSource"/> is 3.
    /// </summary>
    [FieldOffset(48)] public int AlphaChannel;

    /// <summary>
    /// At 0 or more, the diffuse is that one channel of the texture, tinted; -1 off.
    /// </summary>
    [FieldOffset(52)] public int GreyChannel;

    /// <summary>
    /// The bindless index of the diffuse texture.
    /// </summary>
    [FieldOffset(56)] public uint Diffuse;

    /// <summary>
    /// The bindless index of the normal map.
    /// </summary>
    [FieldOffset(60)] public uint Normal;

    /// <summary>
    /// The bindless index of the second set's diffuse (dual maps).
    /// </summary>
    [FieldOffset(64)] public uint Diffuse2;

    /// <summary>
    /// The bindless index of the second set's normal map.
    /// </summary>
    [FieldOffset(68)] public uint Normal2;

    /// <summary>
    /// The bindless index of the head diffuse added to the diffuse.
    /// </summary>
    [FieldOffset(72)] public uint HeadDiffuse;

    /// <summary>
    /// The bindless index of the head normal map added to the normal.
    /// </summary>
    [FieldOffset(76)] public uint HeadNormal;

    /// <summary>
    /// Whether the head textures are added.
    /// </summary>
    [FieldOffset(80)] public uint HasHead;

    /// <summary>
    /// Whether the normal map is swizzled: X in alpha, Y in green, Z rebuilt.
    /// </summary>
    [FieldOffset(84)] public uint NormalSwizzled;

    /// <summary>
    /// Whether there is a diffuse texture.
    /// </summary>
    [FieldOffset(88)] public uint HasDiffuse;

    /// <summary>
    /// Whether there is a normal map.
    /// </summary>
    [FieldOffset(92)] public uint HasNormal;

    /// <summary>
    /// Whether the second texture set is mixed in by the vertex alpha.
    /// </summary>
    [FieldOffset(96)] public uint HasDual;

    /// <summary>
    /// Whether the textures are mapped triplanar in world space.
    /// </summary>
    [FieldOffset(100)] public uint Triplanar;

    /// <summary>
    /// Whether the material is emissive.
    /// </summary>
    [FieldOffset(104)] public uint Emissive;

    /// <summary>
    /// Whether the vertex colour is used.
    /// </summary>
    [FieldOffset(108)] public uint UseVertexColour;

    /// <summary>
    /// Whether the draw is a wireframe in <see cref="FlatColour"/>.
    /// </summary>
    [FieldOffset(112)] public uint Wireframe;

    /// <summary>
    /// Whether the mesh is skinned.
    /// </summary>
    [FieldOffset(116)] public uint Skinned;

    /// <summary>
    /// Whether the draw uses alpha to coverage.
    /// </summary>
    [FieldOffset(120)] public uint Coverage;

    /// <summary>
    /// A free word the shared shader does not read; the foliage puts its material's surface value there.
    /// </summary>
    [FieldOffset(124)] public uint Spare;
}