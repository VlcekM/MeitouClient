using System.Globalization;
using System.Numerics;

namespace Meitou.Data.Ogre;

/// <summary>
/// A material from an Ogre <c>.material</c> script, after inheritance. Typed accessors follow Ogre's MaterialTranslator and friends: properties are
/// applied in order, so the last one wins, and a property Ogre would reject is ignored (the default stays). <see cref="Source"/> keeps every raw
/// property. Syntax and defaults: docs/formats/ogre-material.md.
/// </summary>
public sealed class OgreMaterial
{
    /// <summary>
    /// Reads the material's techniques from <paramref name="source"/> (inheritance already applied) and applies its texture aliases to their units.
    /// </summary>
    internal OgreMaterial(OgreScriptObject source)
    {
        Source = source;
        Techniques = [.. source.Objects("technique").Select(t => new OgreTechnique(t))];
        foreach (var p in source.Properties.Where(p => p.Name == "set_texture_alias" && p.Values.Count is >= 2 and <= 3))
            TextureAliases.TryAdd(p.Args[0], p.Args[1]); // std::map::insert: the first one of an alias wins
        // Ogre applies set_texture_alias after the whole material is built (Material::applyTextureAliases).
        foreach (var unit in Techniques.SelectMany(t => t.Passes).SelectMany(p => p.TextureUnits))
            if (unit.Alias.Length > 0 && TextureAliases.TryGetValue(unit.Alias, out var texture))
                unit.AliasTexture = texture;
    }

    /// <summary>
    /// The material's script object after inheritance, with every raw property.
    /// </summary>
    public OgreScriptObject Source { get; }
    /// <summary>
    /// The material's name.
    /// </summary>
    public string Name => Source.Name;
    /// <summary>
    /// The script file that defines the material.
    /// </summary>
    public string File => Source.File;
    /// <summary>
    /// The line of the material's definition in <see cref="File"/>.
    /// </summary>
    public int Line => Source.Line;
    /// <summary>
    /// The material's techniques in script order.
    /// </summary>
    public List<OgreTechnique> Techniques { get; }

    /// <summary>
    /// <c>set_texture_alias alias texture</c>: replaces the texture of every unit whose alias matches. The first one of an alias wins, so (base
    /// properties coming first) a derived material cannot change a base's alias.
    /// </summary>
    public Dictionary<string, string> TextureAliases { get; } = [];

    /// <summary>
    /// <c>receive_shadows</c>; default true.
    /// </summary>
    public bool ReceiveShadows => OgreScriptValues.Bool(Source, "receive_shadows", true);
    /// <summary>
    /// <c>transparency_casts_shadows</c>; default false.
    /// </summary>
    public bool TransparencyCastsShadows => OgreScriptValues.Bool(Source, "transparency_casts_shadows", false);

    public override string ToString() => $"material {Name} ({File}:{Line})";
}

/// <summary>
/// A <c>technique</c> of a material: its passes and the scheme, LOD and shadow materials it is chosen by.
/// </summary>
public sealed class OgreTechnique
{
    /// <summary>
    /// Reads the technique's passes from <paramref name="source"/>.
    /// </summary>
    internal OgreTechnique(OgreScriptObject source)
    {
        Source = source;
        Passes = [.. source.Objects("pass").Select(p => new OgrePass(p))];
    }

    /// <summary>
    /// The technique's script object.
    /// </summary>
    public OgreScriptObject Source { get; }
    /// <summary>
    /// The technique's name; empty when it has none.
    /// </summary>
    public string Name => Source.Name;
    /// <summary>
    /// The technique's passes in script order.
    /// </summary>
    public List<OgrePass> Passes { get; }

    /// <summary>
    /// Material scheme; Ogre's default is <c>Default</c>.
    /// </summary>
    public string Scheme => OgreScriptValues.String(Source, "scheme") ?? "Default";

    /// <summary>
    /// <c>lod_index</c>; default 0.
    /// </summary>
    public int LodIndex => OgreScriptValues.Int(Source, "lod_index") ?? 0;
    /// <summary>
    /// <c>shadow_caster_material</c>: the material shadow casters use instead; null when not set.
    /// </summary>
    public string? ShadowCasterMaterial => OgreScriptValues.String(Source, "shadow_caster_material");
    /// <summary>
    /// <c>shadow_receiver_material</c>: the material shadow receivers use instead; null when not set.
    /// </summary>
    public string? ShadowReceiverMaterial => OgreScriptValues.String(Source, "shadow_receiver_material");
}

/// <summary>
/// The colours a pass takes from the vertex colour (<c>ambient vertexcolour</c> and the like).
/// </summary>
[Flags]
public enum OgreTrackVertexColour
{
    /// <summary>
    /// No colour comes from the vertex colour.
    /// </summary>
    None = 0,

    /// <summary>
    /// <c>ambient vertexcolour</c>.
    /// </summary>
    Ambient = 1,

    /// <summary>
    /// <c>diffuse vertexcolour</c>.
    /// </summary>
    Diffuse = 2,

    /// <summary>
    /// <c>specular vertexcolour</c>.
    /// </summary>
    Specular = 4,

    /// <summary>
    /// <c>emissive vertexcolour</c>.
    /// </summary>
    Emissive = 8
}

/// <summary>
/// A <c>scene_blend</c> factor (<c>one</c>, <c>zero</c>, <c>dest_colour</c>, ...).
/// </summary>
public enum OgreSceneBlendFactor
{
    /// <summary>
    /// <c>one</c>: 1.
    /// </summary>
    One,

    /// <summary>
    /// <c>zero</c>: 0.
    /// </summary>
    Zero,

    /// <summary>
    /// <c>dest_colour</c>: the destination colour.
    /// </summary>
    DestColour,

    /// <summary>
    /// <c>src_colour</c>: the source colour.
    /// </summary>
    SourceColour,

    /// <summary>
    /// <c>one_minus_dest_colour</c>: 1 - the destination colour.
    /// </summary>
    OneMinusDestColour,

    /// <summary>
    /// <c>one_minus_src_colour</c>: 1 - the source colour.
    /// </summary>
    OneMinusSourceColour,

    /// <summary>
    /// <c>dest_alpha</c>: the destination alpha.
    /// </summary>
    DestAlpha,

    /// <summary>
    /// <c>src_alpha</c>: the source alpha.
    /// </summary>
    SourceAlpha,

    /// <summary>
    /// <c>one_minus_dest_alpha</c>: 1 - the destination alpha.
    /// </summary>
    OneMinusDestAlpha,

    /// <summary>
    /// <c>one_minus_src_alpha</c>: 1 - the source alpha.
    /// </summary>
    OneMinusSourceAlpha,
}

/// <summary>
/// A depth or alpha test function (<c>depth_func</c>, <c>alpha_rejection</c>).
/// </summary>
public enum OgreCompareFunction
{
    /// <summary>
    /// <c>always_fail</c>: the test never passes.
    /// </summary>
    AlwaysFail,

    /// <summary>
    /// <c>always_pass</c>: the test always passes.
    /// </summary>
    AlwaysPass,

    /// <summary>
    /// <c>less</c>.
    /// </summary>
    Less,

    /// <summary>
    /// <c>less_equal</c>.
    /// </summary>
    LessEqual,

    /// <summary>
    /// <c>equal</c>.
    /// </summary>
    Equal,

    /// <summary>
    /// <c>not_equal</c>.
    /// </summary>
    NotEqual,

    /// <summary>
    /// <c>greater_equal</c>.
    /// </summary>
    GreaterEqual,

    /// <summary>
    /// <c>greater</c>.
    /// </summary>
    Greater
}

/// <summary>
/// <c>cull_hardware</c>: which winding the GPU culls.
/// </summary>
public enum OgreCullHardware
{
    /// <summary>
    /// <c>none</c>: nothing is culled.
    /// </summary>
    None,

    /// <summary>
    /// <c>clockwise</c>: triangles wound clockwise on screen are culled.
    /// </summary>
    Clockwise,

    /// <summary>
    /// <c>anticlockwise</c>: triangles wound anticlockwise on screen are culled.
    /// </summary>
    Anticlockwise
}

/// <summary>
/// <c>cull_software</c>: which faces Ogre culls on the CPU.
/// </summary>
public enum OgreCullSoftware
{
    /// <summary>
    /// <c>none</c>: nothing is culled.
    /// </summary>
    None,

    /// <summary>
    /// <c>back</c>: back faces are culled.
    /// </summary>
    Back,

    /// <summary>
    /// <c>front</c>: front faces are culled.
    /// </summary>
    Front
}

/// <summary>
/// <c>polygon_mode</c>: how a pass rasterises its triangles.
/// </summary>
public enum OgrePolygonMode
{
    /// <summary>
    /// <c>points</c>: vertices only.
    /// </summary>
    Points,

    /// <summary>
    /// <c>wireframe</c>: edges only.
    /// </summary>
    Wireframe,

    /// <summary>
    /// <c>solid</c>: filled triangles.
    /// </summary>
    Solid
}

/// <summary>
/// A pass's <c>scene_blend</c>: the source and destination factors.
/// </summary>
public readonly record struct OgreSceneBlend(OgreSceneBlendFactor Source, OgreSceneBlendFactor Dest)
{
    /// <summary>
    /// <c>one zero</c>: the source replaces the destination, Ogre's default.
    /// </summary>
    public static readonly OgreSceneBlend Replace = new(OgreSceneBlendFactor.One, OgreSceneBlendFactor.Zero);
    /// <summary>
    /// Whether the blend is <see cref="Replace"/>.
    /// </summary>
    public bool IsOpaque => this == Replace;
}

/// <summary>
/// A pass's <c>alpha_rejection</c>: the test function and the 0..255 value the alpha is compared with.
/// </summary>
public readonly record struct OgreAlphaRejection(OgreCompareFunction Function, byte Value);

/// <summary>
/// A <c>pass</c> of a technique: its typed render state, programs and texture units.
/// </summary>
public sealed class OgrePass
{
    /// <summary>
    /// Reads the pass's texture units from <paramref name="source"/>.
    /// </summary>
    internal OgrePass(OgreScriptObject source)
    {
        Source = source;
        TextureUnits = [.. source.Objects("texture_unit").Select(u => new OgreTextureUnit(u))];
    }

    /// <summary>
    /// The pass's script object.
    /// </summary>
    public OgreScriptObject Source { get; }
    /// <summary>
    /// The pass's name; empty when it has none.
    /// </summary>
    public string Name => Source.Name;
    /// <summary>
    /// The pass's texture units in script order.
    /// </summary>
    public List<OgreTextureUnit> TextureUnits { get; }

    /// <summary>
    /// <c>ambient r g b [a]</c>; default white.
    /// </summary>
    public Vector4 Ambient => Colour("ambient", Vector4.One);
    /// <summary>
    /// <c>diffuse r g b [a]</c>; default white.
    /// </summary>
    public Vector4 Diffuse => Colour("diffuse", Vector4.One);
    /// <summary>
    /// <c>emissive r g b [a]</c>; default black, alpha 1.
    /// </summary>
    public Vector4 Emissive => Colour("emissive", new Vector4(0, 0, 0, 1));

    /// <summary>
    /// <c>specular r g b [a] shininess</c>; alpha defaults to 1.
    /// </summary>
    public Vector4 Specular
    {
        get
        {
            var result = new Vector4(0, 0, 0, 1);
            foreach (var p in Source.Properties.Where(p => p.Name == "specular"))
            {
                var a = p.Args;
                if (a.Count is < 4 or > 5 || a[0] == "vertexcolour") continue;
                var f = a.Select(OgreScriptValues.ParseFloat).ToArray();
                if (f[0] is null || f[1] is null || f[2] is null) continue;
                if (a.Count == 4) result = new Vector4(f[0]!.Value, f[1]!.Value, f[2]!.Value, 1);
                else if (f[3] is { } alpha) result = new Vector4(f[0]!.Value, f[1]!.Value, f[2]!.Value, alpha);
            }
            return result;
        }
    }

    /// <summary>
    /// The shininess of <c>specular</c> (its last value); default 0.
    /// </summary>
    public float Shininess
    {
        get
        {
            float result = 0;
            foreach (var p in Source.Properties.Where(p => p.Name == "specular"))
            {
                var a = p.Args;
                bool vc = a.Count >= 2 && a[0] == "vertexcolour";
                if ((vc || a.Count is 4 or 5) && OgreScriptValues.ParseFloat(a[^1]) is { } s) result = s;
            }
            return result;
        }
    }

    /// <summary>
    /// Which colours come from the vertex colour (<c>diffuse vertexcolour</c> etc.).
    /// </summary>
    public OgreTrackVertexColour VertexColourTracking
    {
        get
        {
            var result = OgreTrackVertexColour.None;
            foreach (var (name, flag) in new[] { ("ambient", OgreTrackVertexColour.Ambient), ("diffuse", OgreTrackVertexColour.Diffuse),
                         ("specular", OgreTrackVertexColour.Specular), ("emissive", OgreTrackVertexColour.Emissive) })
                if (Source.Properties.Any(p => p.Name == name && p.Args is ["vertexcolour", ..]))
                    result |= flag;
            return result;
        }
    }

    /// <summary>
    /// <c>scene_blend</c>: a shorthand (add, modulate, colour_blend, alpha_blend) or two factors. Default one zero.
    /// </summary>
    public OgreSceneBlend SceneBlend
    {
        get
        {
            var result = OgreSceneBlend.Replace;
            foreach (var p in Source.Properties.Where(p => p.Name == "scene_blend"))
            {
                var a = p.Args;
                if (a.Count == 1)
                {
                    OgreSceneBlend? shorthand = a[0] switch
                    {
                        "add" => new(OgreSceneBlendFactor.One, OgreSceneBlendFactor.One),
                        "modulate" => new(OgreSceneBlendFactor.DestColour, OgreSceneBlendFactor.Zero),
                        "colour_blend" => new(OgreSceneBlendFactor.SourceColour, OgreSceneBlendFactor.OneMinusSourceColour),
                        "alpha_blend" => new(OgreSceneBlendFactor.SourceAlpha, OgreSceneBlendFactor.OneMinusSourceAlpha),
                        _ => null, // "replace" is not accepted by the v2-0 pass translator
                    };
                    if (shorthand is { } s) result = s;
                }
                else if (a.Count == 2 && BlendFactor(a[0]) is { } src && BlendFactor(a[1]) is { } dst)
                    result = new(src, dst);
            }
            return result;
        }
    }

    /// <summary>
    /// <c>depth_check</c>; default true.
    /// </summary>
    public bool DepthCheck => OgreScriptValues.Bool(Source, "depth_check", true);
    /// <summary>
    /// <c>depth_write</c>; default true.
    /// </summary>
    public bool DepthWrite => OgreScriptValues.Bool(Source, "depth_write", true);
    /// <summary>
    /// <c>depth_func</c>; default less_equal.
    /// </summary>
    public OgreCompareFunction DepthFunction => Last("depth_func", a => a.Count == 1 ? CompareFunction(a[0]) : null) ?? OgreCompareFunction.LessEqual;
    /// <summary>
    /// <c>lighting</c>; default true.
    /// </summary>
    public bool Lighting => OgreScriptValues.Bool(Source, "lighting", true);
    /// <summary>
    /// <c>colour_write</c>; default true.
    /// </summary>
    public bool ColourWrite => OgreScriptValues.Bool(Source, "colour_write", true);

    /// <summary>
    /// <c>cull_hardware</c>; default clockwise.
    /// </summary>
    public OgreCullHardware CullHardware => Last("cull_hardware", a => a.Count != 1 ? null : a[0] switch
    {
        "none" => OgreCullHardware.None,
        "clockwise" => OgreCullHardware.Clockwise,
        "anticlockwise" => (OgreCullHardware?)OgreCullHardware.Anticlockwise,
        _ => null,
    }) ?? OgreCullHardware.Clockwise;

    /// <summary>
    /// <c>cull_software</c>; default back.
    /// </summary>
    public OgreCullSoftware CullSoftware => Last("cull_software", a => a.Count != 1 ? null : a[0] switch
    {
        "none" => OgreCullSoftware.None,
        "back" => OgreCullSoftware.Back,
        "front" => (OgreCullSoftware?)OgreCullSoftware.Front,
        _ => null,
    }) ?? OgreCullSoftware.Back;

    /// <summary>
    /// <c>polygon_mode</c>; default solid.
    /// </summary>
    public OgrePolygonMode PolygonMode => Last("polygon_mode", a => a.Count != 1 ? null : a[0] switch
    {
        "points" => OgrePolygonMode.Points,
        "wireframe" => OgrePolygonMode.Wireframe,
        "solid" => (OgrePolygonMode?)OgrePolygonMode.Solid,
        _ => null,
    }) ?? OgrePolygonMode.Solid;

    /// <summary>
    /// <c>alpha_rejection func [value]</c>; default always_pass 0. A missing value keeps the previous one.
    /// </summary>
    public OgreAlphaRejection AlphaRejection
    {
        get
        {
            var result = new OgreAlphaRejection(OgreCompareFunction.AlwaysPass, 0);
            foreach (var p in Source.Properties.Where(p => p.Name == "alpha_rejection"))
            {
                var a = p.Args;
                if (a.Count is < 1 or > 2 || CompareFunction(a[0]) is not { } func) continue;
                if (a.Count == 1) result = result with { Function = func };
                else if (uint.TryParse(a[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint v))
                    result = new(func, unchecked((byte)v));
            }
            return result;
        }
    }

    /// <summary>
    /// The last <c>vertex_program_ref</c>; null when the pass has none.
    /// </summary>
    public OgreProgramRef? VertexProgram => ProgramRef("vertex_program_ref");
    /// <summary>
    /// The last <c>fragment_program_ref</c>; null when the pass has none.
    /// </summary>
    public OgreProgramRef? FragmentProgram => ProgramRef("fragment_program_ref");
    /// <summary>
    /// The last <c>geometry_program_ref</c>; null when the pass has none.
    /// </summary>
    public OgreProgramRef? GeometryProgram => ProgramRef("geometry_program_ref");
    /// <summary>
    /// The last <c>shadow_caster_vertex_program_ref</c>; null when the pass has none.
    /// </summary>
    public OgreProgramRef? ShadowCasterVertexProgram => ProgramRef("shadow_caster_vertex_program_ref");
    /// <summary>
    /// The last <c>shadow_caster_fragment_program_ref</c>; null when the pass has none.
    /// </summary>
    public OgreProgramRef? ShadowCasterFragmentProgram => ProgramRef("shadow_caster_fragment_program_ref");

    /// <summary>
    /// The last <paramref name="kind"/> object in the pass (each one replaces the previous program).
    /// </summary>
    public OgreProgramRef? ProgramRef(string kind) =>
        Source.Objects(kind).Select(o => new OgreProgramRef(o)).LastOrDefault();

    /// <summary>
    /// The colour property <paramref name="name"/>: the last with three or four numbers (alpha 1 when not given), else <paramref name="initial"/>;
    /// <c>vertexcolour</c> is skipped.
    /// </summary>
    Vector4 Colour(string name, Vector4 initial)
    {
        var result = initial;
        foreach (var p in Source.Properties.Where(p => p.Name == name))
        {
            var a = p.Args;
            if (a.Count is 0 or > 4 || a[0] == "vertexcolour") continue;
            // Ogre's getColour starts from white (alpha 1) and needs at least r g b.
            var c = Vector4.One;
            bool ok = a.Count >= 3;
            for (int i = 0; i < a.Count && ok; i++)
            {
                if (OgreScriptValues.ParseFloat(a[i]) is { } f) c[i] = f;
                else ok = false;
            }
            if (ok) result = c;
        }
        return result;
    }

    /// <summary>
    /// The last occurrence of property <paramref name="name"/> that <paramref name="parse"/> accepts; null when none does.
    /// </summary>
    T? Last<T>(string name, Func<IReadOnlyList<string>, T?> parse) where T : struct
    {
        T? result = null;
        foreach (var p in Source.Properties.Where(p => p.Name == name))
            if (parse(p.Args) is { } v) result = v;
        return result;
    }

    /// <summary>
    /// A <c>scene_blend</c> factor word; null for an unknown one.
    /// </summary>
    internal static OgreSceneBlendFactor? BlendFactor(string s) => s switch
    {
        "one" => OgreSceneBlendFactor.One,
        "zero" => OgreSceneBlendFactor.Zero,
        "dest_colour" => OgreSceneBlendFactor.DestColour,
        "src_colour" => OgreSceneBlendFactor.SourceColour,
        "one_minus_dest_colour" => OgreSceneBlendFactor.OneMinusDestColour,
        "one_minus_src_colour" => OgreSceneBlendFactor.OneMinusSourceColour,
        "dest_alpha" => OgreSceneBlendFactor.DestAlpha,
        "src_alpha" => OgreSceneBlendFactor.SourceAlpha,
        "one_minus_dest_alpha" => OgreSceneBlendFactor.OneMinusDestAlpha,
        "one_minus_src_alpha" => OgreSceneBlendFactor.OneMinusSourceAlpha,
        _ => null,
    };

    /// <summary>
    /// A compare function word (<c>always_fail</c> ... <c>greater</c>); null for an unknown one.
    /// </summary>
    internal static OgreCompareFunction? CompareFunction(string s) => s switch
    {
        "always_fail" => OgreCompareFunction.AlwaysFail,
        "always_pass" => OgreCompareFunction.AlwaysPass,
        "less" => OgreCompareFunction.Less,
        "less_equal" => OgreCompareFunction.LessEqual,
        "equal" => OgreCompareFunction.Equal,
        "not_equal" => OgreCompareFunction.NotEqual,
        "greater_equal" => OgreCompareFunction.GreaterEqual,
        "greater" => OgreCompareFunction.Greater,
        _ => null,
    };
}

/// <summary>
/// A <c>vertex_program_ref Name { param_named ... }</c> (or fragment, geometry...) inside a pass.
/// </summary>
public sealed class OgreProgramRef(OgreScriptObject source)
{
    /// <summary>
    /// The reference's script object.
    /// </summary>
    public OgreScriptObject Source { get; } = source;

    /// <summary>
    /// <c>vertex_program_ref</c>, <c>fragment_program_ref</c>...
    /// </summary>
    public string Kind => Source.Class;

    /// <summary>
    /// The name of the program referred to.
    /// </summary>
    public string Name => Source.Name;

    /// <summary>
    /// <c>param_named</c>, <c>param_named_auto</c>, <c>param_indexed</c>, <c>shared_params_ref</c>... in order.
    /// </summary>
    public IEnumerable<OgreScriptProperty> Parameters => Source.Properties;

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>
/// Texture type given after the name in <c>texture name [type]</c>.
/// </summary>
public enum OgreTextureType
{
    /// <summary>
    /// <c>1d</c> (Ogre falls back to 2D where 1D is unsupported).
    /// </summary>
    Texture1D,

    /// <summary>
    /// <c>2d</c>, the default.
    /// </summary>
    Texture2D,

    /// <summary>
    /// <c>3d</c>: a volume texture.
    /// </summary>
    Texture3D,

    /// <summary>
    /// <c>cubic</c>: a cube map.
    /// </summary>
    CubeMap,

    /// <summary>
    /// <c>2darray</c>: an array of 2D textures.
    /// </summary>
    Texture2DArray
}

/// <summary>
/// A <c>texture_unit</c> of a pass: its texture, its alias and the sampling properties.
/// </summary>
public sealed class OgreTextureUnit
{
    /// <summary>
    /// Wraps the unit's script object.
    /// </summary>
    internal OgreTextureUnit(OgreScriptObject source) => Source = source;

    /// <summary>
    /// The unit's script object.
    /// </summary>
    public OgreScriptObject Source { get; }
    /// <summary>
    /// The unit's name; empty when it has none.
    /// </summary>
    public string Name => Source.Name;

    /// <summary>
    /// The unit's alias for <c>set_texture_alias</c>: its <c>texture_alias</c>, else its name (Ogre's TextureUnitState::setName fills an empty alias
    /// with the name).
    /// </summary>
    public string Alias => OgreScriptValues.String(Source, "texture_alias") ?? Name;

    /// <summary>
    /// Set when the material's <c>set_texture_alias</c> replaced the texture.
    /// </summary>
    public string? AliasTexture { get; internal set; }

    /// <summary>
    /// The <c>texture</c> property's arguments (name, then type / mipmaps / alpha / format / gamma).
    /// </summary>
    public IReadOnlyList<string>? TextureArgs =>
        Source.Properties.LastOrDefault(p => p.Name == "texture" && p.Values.Count is >= 1 and <= 5)?.Args;

    /// <summary>
    /// The texture file name to bind, after <c>set_texture_alias</c>; null when the unit sets none.
    /// </summary>
    public string? Texture => AliasTexture ?? TextureArgs?[0];

    /// <summary>
    /// The type after the name in <c>texture</c> (the last one given); default 2D.
    /// </summary>
    public OgreTextureType TextureType => TextureArgs?.Skip(1).Select(a => a switch
    {
        "1d" => OgreTextureType.Texture1D, // Ogre falls back to 2D where 1D is unsupported
        "2d" => OgreTextureType.Texture2D,
        "3d" => OgreTextureType.Texture3D,
        "cubic" => OgreTextureType.CubeMap,
        "2darray" => (OgreTextureType?)OgreTextureType.Texture2DArray,
        _ => null,
    }).LastOrDefault(t => t is not null) ?? OgreTextureType.Texture2D;

    /// <summary>
    /// <c>texture name ... gamma</c>: read as sRGB.
    /// </summary>
    public bool Gamma => TextureArgs?.Skip(1).Contains("gamma") ?? false;

    /// <summary>
    /// <c>cubic_texture</c> arguments: one name + combinedUVW/separateUV, or six names + separateUV.
    /// </summary>
    public IReadOnlyList<string>? CubicTexture => Source.Property("cubic_texture")?.Args;

    /// <summary>
    /// <c>anim_texture</c> arguments: base name, frame count, duration; or frame names then duration.
    /// </summary>
    public IReadOnlyList<string>? AnimTexture => Source.Property("anim_texture")?.Args;

    /// <summary>
    /// <c>content_type</c> arguments, e.g. <c>compositor rt_interiormask</c> or <c>shadow</c>; null means named.
    /// </summary>
    public IReadOnlyList<string>? ContentType => Source.Property("content_type")?.Args;

    /// <summary>
    /// <c>tex_address_mode</c> arguments; null when not set.
    /// </summary>
    public IReadOnlyList<string>? AddressMode => Source.Property("tex_address_mode")?.Args;
    /// <summary>
    /// <c>filtering</c> arguments; null when not set.
    /// </summary>
    public IReadOnlyList<string>? Filtering => Source.Property("filtering")?.Args;
    /// <summary>
    /// <c>tex_coord_set</c>; default 0.
    /// </summary>
    public int TexCoordSet => OgreScriptValues.Int(Source, "tex_coord_set") ?? 0;

    /// <summary>
    /// Every texture file the unit names: the texture (or its alias replacement), the cube map faces and the animation frames, expanded the way Ogre
    /// derives file names. Units filled by a compositor or shadow name none.
    /// </summary>
    public IEnumerable<string> TextureFiles()
    {
        if (ContentType is [not "named", ..]) yield break;
        if (Texture is { } t) yield return t;
        if (CubicTexture is { Count: > 0 } cube)
        {
            if (cube.Count >= 6) foreach (var f in cube.Take(6)) yield return f;
            else if (cube.Count == 2 && cube[1] == "separateUV")
                foreach (var suffix in new[] { "_fr", "_bk", "_lf", "_rt", "_up", "_dn" })
                    yield return InsertSuffix(cube[0], suffix);
            else yield return cube[0];
        }
        if (AnimTexture is { Count: >= 2 } anim)
        {
            if (anim.Count == 3 && int.TryParse(anim[1], CultureInfo.InvariantCulture, out int frames))
                for (int i = 0; i < frames; i++)
                    yield return InsertSuffix(anim[0], "_" + i.ToString(CultureInfo.InvariantCulture));
            else foreach (var f in anim.Take(anim.Count - 1)) yield return f;
        }
    }

    /// <summary>
    /// <paramref name="name"/> with <paramref name="suffix"/> before its extension (at the end without one).
    /// </summary>
    static string InsertSuffix(string name, string suffix)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name + suffix : name[..dot] + suffix + name[dot..];
    }
}

/// <summary>
/// A GPU program declared in a script: <c>vertex_program Name hlsl { source x.hlsl ... }</c>.
/// </summary>
public sealed class OgreGpuProgram(OgreScriptObject source)
{
    /// <summary>
    /// The program's script object.
    /// </summary>
    public OgreScriptObject Source { get; } = source;

    /// <summary>
    /// <c>vertex_program</c>, <c>fragment_program</c>, <c>geometry_program</c>...
    /// </summary>
    public string Kind => Source.Class;

    /// <summary>
    /// The program's name.
    /// </summary>
    public string Name => Source.Name;

    /// <summary>
    /// <c>hlsl</c>, <c>glsl</c>, <c>cg</c>, <c>asm</c>, <c>unified</c>...
    /// </summary>
    public string? Language => Source.Args.FirstOrDefault();

    /// <summary>
    /// The shader source file (<c>source</c>), for non-unified programs.
    /// </summary>
    public string? SourceFile => OgreScriptValues.String(Source, "source");

    /// <summary>
    /// <c>entry_point</c>: the shader function; null when not set.
    /// </summary>
    public string? EntryPoint => OgreScriptValues.String(Source, "entry_point");

    /// <summary>
    /// For <c>unified</c> programs: the programs it picks from, in order.
    /// </summary>
    public IReadOnlyList<string> Delegates => [.. Source.Properties.Where(p => p.Name == "delegate" && p.Values.Count == 1).Select(p => p.Args[0])];

    /// <summary>
    /// The last <c>default_params</c> block; null when the program has none.
    /// </summary>
    public OgreScriptObject? DefaultParams => Source.Objects("default_params").LastOrDefault();

    public override string ToString() => $"{Kind} {Name} {Language}";
}

/// <summary>
/// Value parsing as Ogre's ScriptTranslator does (getBoolean, getString, getInt, getFloat). Lookups by property name return the last valid
/// occurrence: Ogre applies each in order and ignores (logs) invalid ones.
/// </summary>
public static class OgreScriptValues
{
    /// <summary>
    /// <c>true/false/yes/no/on/off</c> (exact case) of a one-value property.
    /// </summary>
    public static bool? Bool(OgreScriptProperty p) => p.Args is [var v] ? v switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => null,
    } : null;

    /// <summary>
    /// The single value of a one-value property; null otherwise.
    /// </summary>
    public static string? String(OgreScriptProperty p) => p.Args is [var v] ? v : null;

    /// <summary>
    /// The single value of a one-value property as an int; null when it is not one.
    /// </summary>
    public static int? Int(OgreScriptProperty p) =>
        p.Args is [var v] && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null;

    /// <summary>
    /// The last valid boolean <paramref name="name"/> of <paramref name="o"/>, else <paramref name="defaultValue"/>.
    /// </summary>
    public static bool Bool(OgreScriptObject o, string name, bool defaultValue) => Last(o, name, Bool) ?? defaultValue;
    /// <summary>
    /// The last single-value <paramref name="name"/> of <paramref name="o"/>; null when none.
    /// </summary>
    public static string? String(OgreScriptObject o, string name) => Last(o, name, p => String(p));
    /// <summary>
    /// The last valid int <paramref name="name"/> of <paramref name="o"/>; null when none.
    /// </summary>
    public static int? Int(OgreScriptObject o, string name) => Last(o, name, Int);

    /// <summary>
    /// <paramref name="s"/> as an invariant float; null when it is not one.
    /// </summary>
    public static float? ParseFloat(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : null;

    /// <summary>
    /// The last occurrence of <paramref name="name"/> that <paramref name="parse"/> accepts; null when none does.
    /// </summary>
    static T? Last<T>(OgreScriptObject o, string name, Func<OgreScriptProperty, T?> parse) where T : struct
    {
        T? result = null;
        foreach (var p in o.Properties.Where(p => p.Name == name))
            if (parse(p) is { } v) result = v;
        return result;
    }

    /// <summary>
    /// The last occurrence of <paramref name="name"/> that <paramref name="parse"/> accepts; null when none does.
    /// </summary>
    static string? Last(OgreScriptObject o, string name, Func<OgreScriptProperty, string?> parse)
    {
        string? result = null;
        foreach (var p in o.Properties.Where(p => p.Name == name))
            if (parse(p) is { } v) result = v;
        return result;
    }
}