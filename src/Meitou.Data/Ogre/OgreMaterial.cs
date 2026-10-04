using System.Globalization;
using System.Numerics;

namespace Meitou.Data.Ogre;

/// <summary>
/// A material from an Ogre <c>.material</c> script, after inheritance. Typed accessors follow Ogre's
/// MaterialTranslator and friends: properties are applied in order, so the last one wins, and a property Ogre
/// would reject is ignored (the default stays). <see cref="Source"/> keeps every raw property. Syntax and
/// defaults: docs/formats/ogre-material.md.
/// </summary>
public sealed class OgreMaterial
{
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

    public OgreScriptObject Source { get; }
    public string Name => Source.Name;
    public string File => Source.File;
    public int Line => Source.Line;
    public List<OgreTechnique> Techniques { get; }

    /// <summary>
    /// <c>set_texture_alias alias texture</c>: replaces the texture of every unit whose alias matches. The first one
    /// of an alias wins, so (base properties coming first) a derived material cannot change a base's alias.
    /// </summary>
    public Dictionary<string, string> TextureAliases { get; } = [];

    public bool ReceiveShadows => OgreScriptValues.Bool(Source, "receive_shadows", true);
    public bool TransparencyCastsShadows => OgreScriptValues.Bool(Source, "transparency_casts_shadows", false);

    public override string ToString() => $"material {Name} ({File}:{Line})";
}

public sealed class OgreTechnique
{
    internal OgreTechnique(OgreScriptObject source)
    {
        Source = source;
        Passes = [.. source.Objects("pass").Select(p => new OgrePass(p))];
    }

    public OgreScriptObject Source { get; }
    public string Name => Source.Name;
    public List<OgrePass> Passes { get; }

    /// <summary>Material scheme; Ogre's default is <c>Default</c>.</summary>
    public string Scheme => OgreScriptValues.String(Source, "scheme") ?? "Default";

    public int LodIndex => OgreScriptValues.Int(Source, "lod_index") ?? 0;
    public string? ShadowCasterMaterial => OgreScriptValues.String(Source, "shadow_caster_material");
    public string? ShadowReceiverMaterial => OgreScriptValues.String(Source, "shadow_receiver_material");
}

[Flags]
public enum OgreTrackVertexColour { None = 0, Ambient = 1, Diffuse = 2, Specular = 4, Emissive = 8 }

public enum OgreSceneBlendFactor
{
    One, Zero, DestColour, SourceColour, OneMinusDestColour, OneMinusSourceColour,
    DestAlpha, SourceAlpha, OneMinusDestAlpha, OneMinusSourceAlpha,
}

public enum OgreCompareFunction { AlwaysFail, AlwaysPass, Less, LessEqual, Equal, NotEqual, GreaterEqual, Greater }
public enum OgreCullHardware { None, Clockwise, Anticlockwise }
public enum OgreCullSoftware { None, Back, Front }
public enum OgrePolygonMode { Points, Wireframe, Solid }

public readonly record struct OgreSceneBlend(OgreSceneBlendFactor Source, OgreSceneBlendFactor Dest)
{
    public static readonly OgreSceneBlend Replace = new(OgreSceneBlendFactor.One, OgreSceneBlendFactor.Zero);
    public bool IsOpaque => this == Replace;
}

public readonly record struct OgreAlphaRejection(OgreCompareFunction Function, byte Value);

public sealed class OgrePass
{
    internal OgrePass(OgreScriptObject source)
    {
        Source = source;
        TextureUnits = [.. source.Objects("texture_unit").Select(u => new OgreTextureUnit(u))];
    }

    public OgreScriptObject Source { get; }
    public string Name => Source.Name;
    public List<OgreTextureUnit> TextureUnits { get; }

    public Vector4 Ambient => Colour("ambient", Vector4.One);
    public Vector4 Diffuse => Colour("diffuse", Vector4.One);
    public Vector4 Emissive => Colour("emissive", new Vector4(0, 0, 0, 1));

    /// <summary><c>specular r g b [a] shininess</c>; alpha defaults to 1.</summary>
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

    /// <summary>Which colours come from the vertex colour (<c>diffuse vertexcolour</c> etc.).</summary>
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

    /// <summary><c>scene_blend</c>: a shorthand (add, modulate, colour_blend, alpha_blend) or two factors. Default one zero.</summary>
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

    public bool DepthCheck => OgreScriptValues.Bool(Source, "depth_check", true);
    public bool DepthWrite => OgreScriptValues.Bool(Source, "depth_write", true);
    public OgreCompareFunction DepthFunction => Last("depth_func", a => a.Count == 1 ? CompareFunction(a[0]) : null) ?? OgreCompareFunction.LessEqual;
    public bool Lighting => OgreScriptValues.Bool(Source, "lighting", true);
    public bool ColourWrite => OgreScriptValues.Bool(Source, "colour_write", true);

    public OgreCullHardware CullHardware => Last("cull_hardware", a => a.Count != 1 ? null : a[0] switch
    {
        "none" => OgreCullHardware.None,
        "clockwise" => OgreCullHardware.Clockwise,
        "anticlockwise" => (OgreCullHardware?)OgreCullHardware.Anticlockwise,
        _ => null,
    }) ?? OgreCullHardware.Clockwise;

    public OgreCullSoftware CullSoftware => Last("cull_software", a => a.Count != 1 ? null : a[0] switch
    {
        "none" => OgreCullSoftware.None,
        "back" => OgreCullSoftware.Back,
        "front" => (OgreCullSoftware?)OgreCullSoftware.Front,
        _ => null,
    }) ?? OgreCullSoftware.Back;

    public OgrePolygonMode PolygonMode => Last("polygon_mode", a => a.Count != 1 ? null : a[0] switch
    {
        "points" => OgrePolygonMode.Points,
        "wireframe" => OgrePolygonMode.Wireframe,
        "solid" => (OgrePolygonMode?)OgrePolygonMode.Solid,
        _ => null,
    }) ?? OgrePolygonMode.Solid;

    /// <summary><c>alpha_rejection func [value]</c>; default always_pass 0. A missing value keeps the previous one.</summary>
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

    public OgreProgramRef? VertexProgram => ProgramRef("vertex_program_ref");
    public OgreProgramRef? FragmentProgram => ProgramRef("fragment_program_ref");
    public OgreProgramRef? GeometryProgram => ProgramRef("geometry_program_ref");
    public OgreProgramRef? ShadowCasterVertexProgram => ProgramRef("shadow_caster_vertex_program_ref");
    public OgreProgramRef? ShadowCasterFragmentProgram => ProgramRef("shadow_caster_fragment_program_ref");

    /// <summary>The last <paramref name="kind"/> object in the pass (each one replaces the previous program).</summary>
    public OgreProgramRef? ProgramRef(string kind) =>
        Source.Objects(kind).Select(o => new OgreProgramRef(o)).LastOrDefault();

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

    T? Last<T>(string name, Func<IReadOnlyList<string>, T?> parse) where T : struct
    {
        T? result = null;
        foreach (var p in Source.Properties.Where(p => p.Name == name))
            if (parse(p.Args) is { } v) result = v;
        return result;
    }

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

/// <summary>A <c>vertex_program_ref Name { param_named ... }</c> (or fragment, geometry...) inside a pass.</summary>
public sealed class OgreProgramRef(OgreScriptObject source)
{
    public OgreScriptObject Source { get; } = source;

    /// <summary><c>vertex_program_ref</c>, <c>fragment_program_ref</c>...</summary>
    public string Kind => Source.Class;

    public string Name => Source.Name;

    /// <summary><c>param_named</c>, <c>param_named_auto</c>, <c>param_indexed</c>, <c>shared_params_ref</c>... in order.</summary>
    public IEnumerable<OgreScriptProperty> Parameters => Source.Properties;

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>Texture type given after the name in <c>texture name [type]</c>.</summary>
public enum OgreTextureType { Texture1D, Texture2D, Texture3D, CubeMap, Texture2DArray }

public sealed class OgreTextureUnit
{
    internal OgreTextureUnit(OgreScriptObject source) => Source = source;

    public OgreScriptObject Source { get; }
    public string Name => Source.Name;

    /// <summary>
    /// The unit's alias for <c>set_texture_alias</c>: its <c>texture_alias</c>, else its name (Ogre's
    /// TextureUnitState::setName fills an empty alias with the name).
    /// </summary>
    public string Alias => OgreScriptValues.String(Source, "texture_alias") ?? Name;

    /// <summary>Set when the material's <c>set_texture_alias</c> replaced the texture.</summary>
    public string? AliasTexture { get; internal set; }

    /// <summary>The <c>texture</c> property's arguments (name, then type / mipmaps / alpha / format / gamma).</summary>
    public IReadOnlyList<string>? TextureArgs =>
        Source.Properties.LastOrDefault(p => p.Name == "texture" && p.Values.Count is >= 1 and <= 5)?.Args;

    /// <summary>The texture file name to bind, after <c>set_texture_alias</c>; null when the unit sets none.</summary>
    public string? Texture => AliasTexture ?? TextureArgs?[0];

    public OgreTextureType TextureType => TextureArgs?.Skip(1).Select(a => a switch
    {
        "1d" => OgreTextureType.Texture1D, // Ogre falls back to 2D where 1D is unsupported
        "2d" => OgreTextureType.Texture2D,
        "3d" => OgreTextureType.Texture3D,
        "cubic" => OgreTextureType.CubeMap,
        "2darray" => (OgreTextureType?)OgreTextureType.Texture2DArray,
        _ => null,
    }).LastOrDefault(t => t is not null) ?? OgreTextureType.Texture2D;

    /// <summary><c>texture name ... gamma</c>: read as sRGB.</summary>
    public bool Gamma => TextureArgs?.Skip(1).Contains("gamma") ?? false;

    /// <summary><c>cubic_texture</c> arguments: one name + combinedUVW/separateUV, or six names + separateUV.</summary>
    public IReadOnlyList<string>? CubicTexture => Source.Property("cubic_texture")?.Args;

    /// <summary><c>anim_texture</c> arguments: base name, frame count, duration; or frame names then duration.</summary>
    public IReadOnlyList<string>? AnimTexture => Source.Property("anim_texture")?.Args;

    /// <summary><c>content_type</c> arguments, e.g. <c>compositor rt_interiormask</c> or <c>shadow</c>; null means named.</summary>
    public IReadOnlyList<string>? ContentType => Source.Property("content_type")?.Args;

    public IReadOnlyList<string>? AddressMode => Source.Property("tex_address_mode")?.Args;
    public IReadOnlyList<string>? Filtering => Source.Property("filtering")?.Args;
    public int TexCoordSet => OgreScriptValues.Int(Source, "tex_coord_set") ?? 0;

    /// <summary>
    /// Every texture file the unit names: the texture (or its alias replacement), the cube map faces and the
    /// animation frames, expanded the way Ogre derives file names. Units filled by a compositor or shadow name none.
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
                for (int i = 0; i < frames; i++) yield return InsertSuffix(anim[0], "_" + i.ToString(CultureInfo.InvariantCulture));
            else foreach (var f in anim.Take(anim.Count - 1)) yield return f;
        }
    }

    static string InsertSuffix(string name, string suffix)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name + suffix : name[..dot] + suffix + name[dot..];
    }
}

/// <summary>A GPU program declared in a script: <c>vertex_program Name hlsl { source x.hlsl ... }</c>.</summary>
public sealed class OgreGpuProgram(OgreScriptObject source)
{
    public OgreScriptObject Source { get; } = source;

    /// <summary><c>vertex_program</c>, <c>fragment_program</c>, <c>geometry_program</c>...</summary>
    public string Kind => Source.Class;

    public string Name => Source.Name;

    /// <summary><c>hlsl</c>, <c>glsl</c>, <c>cg</c>, <c>asm</c>, <c>unified</c>...</summary>
    public string? Language => Source.Args.FirstOrDefault();

    /// <summary>The shader source file (<c>source</c>), for non-unified programs.</summary>
    public string? SourceFile => OgreScriptValues.String(Source, "source");

    public string? EntryPoint => OgreScriptValues.String(Source, "entry_point");

    /// <summary>For <c>unified</c> programs: the programs it picks from, in order.</summary>
    public IReadOnlyList<string> Delegates => [.. Source.Properties.Where(p => p.Name == "delegate" && p.Values.Count == 1).Select(p => p.Args[0])];

    public OgreScriptObject? DefaultParams => Source.Objects("default_params").LastOrDefault();

    public override string ToString() => $"{Kind} {Name} {Language}";
}

/// <summary>
/// Value parsing as Ogre's ScriptTranslator does (getBoolean, getString, getInt, getFloat). Lookups by property
/// name return the last valid occurrence: Ogre applies each in order and ignores (logs) invalid ones.
/// </summary>
public static class OgreScriptValues
{
    /// <summary><c>true/false/yes/no/on/off</c> (exact case) of a one-value property.</summary>
    public static bool? Bool(OgreScriptProperty p) => p.Args is [var v] ? v switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => null,
    } : null;

    public static string? String(OgreScriptProperty p) => p.Args is [var v] ? v : null;

    public static int? Int(OgreScriptProperty p) =>
        p.Args is [var v] && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null;

    public static bool Bool(OgreScriptObject o, string name, bool defaultValue) => Last(o, name, Bool) ?? defaultValue;
    public static string? String(OgreScriptObject o, string name) => Last(o, name, p => String(p));
    public static int? Int(OgreScriptObject o, string name) => Last(o, name, Int);

    public static float? ParseFloat(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : null;

    static T? Last<T>(OgreScriptObject o, string name, Func<OgreScriptProperty, T?> parse) where T : struct
    {
        T? result = null;
        foreach (var p in o.Properties.Where(p => p.Name == name))
            if (parse(p) is { } v) result = v;
        return result;
    }

    static string? Last(OgreScriptObject o, string name, Func<OgreScriptProperty, string?> parse)
    {
        string? result = null;
        foreach (var p in o.Properties.Where(p => p.Name == name))
            if (parse(p) is { } v) result = v;
        return result;
    }
}
