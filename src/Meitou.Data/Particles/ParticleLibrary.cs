using System.Numerics;
using Meitou.Content;
using Meitou.Data.Ogre;

namespace Meitou.Data.Particles;

/// <summary>
/// How a particle material blends (<c>scene_blend</c>).
/// </summary>
public enum ParticleBlend
{
    /// <summary>
    /// <c>scene_blend one one</c>: added to the colour behind.
    /// </summary>
    Add,

    /// <summary>
    /// <c>scene_blend src_alpha one_minus_src_alpha</c>: mixed by the particle's alpha; also any blend not named here.
    /// </summary>
    Alpha,

    /// <summary>
    /// <c>scene_blend dest_colour zero</c>: multiplies the colour behind.
    /// </summary>
    Modulate,

    /// <summary>
    /// <c>scene_blend src_colour one_minus_src_colour</c>: mixed by the particle's own colour.
    /// </summary>
    ColourBlend,

    /// <summary>
    /// <c>scene_blend one zero</c> (replace): drawn opaque.
    /// </summary>
    Opaque
}

/// <summary>
/// What a particle needs of an Ogre <c>.material</c> from <c>data/particles/materials</c> (docs/formats/particle-universe.md): the texture, the
/// blend, the depth flags and the shading the vertex and fragment programs give. The shipped particle shaders (basic.hlsl) output <c>texture · colour
/// · vertexColour</c>; the "Ambient" vertex programs scale the particle colour by <c>clamp(5 sunY + 0.2, 0.1, 1)</c>; there is no fog term (Verified
/// in the shader source). The interior-mask clip is not reproduced.
/// </summary>
public sealed record ParticleMaterial(
    string Name, string? Texture, ParticleBlend Blend, bool DepthCheck, bool DepthWrite, bool Ambient, Vector4 ColourScale, bool Clamp)
{
    /// <summary>
    /// The particle view of <paramref name="m"/>'s first pass: the blend its <c>scene_blend</c> factors name, the first texture unit with a texture
    /// and no <c>content_type</c>, the fragment program's <c>colour</c> parameter (else 1), clamped unless the address mode is <c>wrap</c>, and
    /// ambient when the vertex program's name holds "Ambient". A material with no pass is alpha-blended, untextured, depth-checked and clamped.
    /// </summary>
    public static ParticleMaterial From(OgreMaterial m)
    {
        var pass = m.Techniques.FirstOrDefault()?.Passes.FirstOrDefault();
        if (pass is null)
            return new ParticleMaterial(m.Name, null, ParticleBlend.Alpha, true, false, false, Vector4.One, true);
        var blend = pass.SceneBlend;
        var kind = blend == new OgreSceneBlend(OgreSceneBlendFactor.One, OgreSceneBlendFactor.One) ? ParticleBlend.Add
            : blend == new OgreSceneBlend(OgreSceneBlendFactor.SourceAlpha, OgreSceneBlendFactor.OneMinusSourceAlpha) ? ParticleBlend.Alpha
            : blend == new OgreSceneBlend(OgreSceneBlendFactor.DestColour, OgreSceneBlendFactor.Zero) ? ParticleBlend.Modulate
            : blend == new OgreSceneBlend(OgreSceneBlendFactor.SourceColour, OgreSceneBlendFactor.OneMinusSourceColour) ? ParticleBlend.ColourBlend
            : blend.IsOpaque ? ParticleBlend.Opaque : ParticleBlend.Alpha;
        var unit = pass.TextureUnits.FirstOrDefault(u => u.ContentType is null && u.Texture is not null);
        var colour = Vector4.One;
        if (pass.FragmentProgram is { } fp)
            foreach (var p in fp.Parameters.Where(p => p.Name == "param_named" && p.Args is ["colour", "float4", ..] && p.Args.Count >= 6))
                if (float.TryParse(p.Args[2], System.Globalization.CultureInfo.InvariantCulture, out float r)
                    && float.TryParse(p.Args[3], System.Globalization.CultureInfo.InvariantCulture, out float g)
                    && float.TryParse(p.Args[4], System.Globalization.CultureInfo.InvariantCulture, out float b)
                    && float.TryParse(p.Args[5], System.Globalization.CultureInfo.InvariantCulture, out float a))
                    colour = new Vector4(r, g, b, a);
        bool clamp = unit?.AddressMode is not { Count: > 0 } mode || mode[0] != "wrap";
        return new ParticleMaterial(m.Name, unit?.Texture, kind, pass.DepthCheck, pass.DepthWrite,
            pass.VertexProgram?.Name.Contains("Ambient", StringComparison.Ordinal) == true, colour, clamp);
    }
}

/// <summary>
/// The ParticleUniverse scripts (<c>*.pu</c>) by system name, and the particle materials by name. A system is found by its own name, not its file's
/// (<c>Ash-Flakes_Light</c> is in <c>Ashland_MultiFlakes.pu</c>, <c>Volk-Cloud</c> in <c>Volc-Pumper.pu</c>); a later definition of a name replaces
/// the earlier one, as the plugin's <c>createParticleSystemTemplate</c> does (docs/formats/particle-universe.md).
/// </summary>
public sealed class ParticleLibrary
{
    /// <summary>
    /// The systems by name, case-insensitive (the last definition of a name).
    /// </summary>
    public Dictionary<string, PuSystemDef> Systems { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Every system read, in file order, including the ones a later definition replaced (<see cref="Duplicates"/>).
    /// </summary>
    public List<PuSystemDef> AllSystems { get; } = [];
    /// <summary>
    /// The particle materials by name, case-insensitive.
    /// </summary>
    public Dictionary<string, ParticleMaterial> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Files that did not read, with the reason (empty for the base game).
    /// </summary>
    public List<string> Errors { get; } = [];
    /// <summary>
    /// Systems that replaced an earlier definition of their name (the last is kept).
    /// </summary>
    public List<string> Duplicates { get; } = [];
    /// <summary>
    /// How many scripts were added.
    /// </summary>
    public int ScriptFiles { get; private set; }
    /// <summary>
    /// The folder the library was loaded from (the resources' root, or the particles folder).
    /// </summary>
    public string? Directory { get; private set; }

    /// <summary>
    /// The system named <paramref name="name"/>; null when there is none.
    /// </summary>
    public PuSystemDef? FindSystem(string name) => Systems.GetValueOrDefault(name);
    /// <summary>
    /// The material named <paramref name="name"/>; null when there is none.
    /// </summary>
    public ParticleMaterial? FindMaterial(string name) => Materials.GetValueOrDefault(name);

    /// <summary>
    /// Reads every <c>*.pu</c> of <c>data/particles/scripts</c> and every <c>*.material</c> of <c>data/particles/materials</c> (the base game only).
    /// </summary>
    public static ParticleLibrary Load(GameInstall install) => Load(Path.Combine(install.DataDirectory, "particles"));

    /// <summary>
    /// What the game compiles: every <c>*.pu</c> of <paramref name="resources"/>' locations (<c>resources.cfg</c> and the enabled mods' folders) in
    /// Ogre's script order, and the particle materials from <paramref name="materials"/> (Ogre's material names are global; a particle may name any).
    /// </summary>
    public static ParticleLibrary Load(OgreScriptResources resources, OgreMaterialLibrary materials)
    {
        var library = new ParticleLibrary { Directory = resources.Root };
        foreach (var file in resources.ScriptFiles("*.pu"))
            library.AddScript(File.ReadAllText(file, System.Text.Encoding.Latin1), Path.GetRelativePath(resources.Root, file));
        foreach (var (name, material) in materials.Materials) library.Materials[name] = ParticleMaterial.From(material);
        foreach (var d in materials.Diagnostics.Where(d => d.Code == OgreScriptError.Fatal))
            library.Errors.Add(d.ToString());
        return library;
    }

    /// <summary>
    /// Reads <c>scripts/*.pu</c> and <c>materials/*.material</c> under <paramref name="particlesDirectory"/>.
    /// </summary>
    public static ParticleLibrary Load(string particlesDirectory)
    {
        var library = new ParticleLibrary { Directory = particlesDirectory };
        string scripts = Path.Combine(particlesDirectory, "scripts");
        if (System.IO.Directory.Exists(scripts))
            foreach (var file in System.IO.Directory.EnumerateFiles(scripts, "*.pu").OrderBy(f => f, StringComparer.Ordinal))
                library.AddScript(File.ReadAllText(file, System.Text.Encoding.Latin1), Path.GetFileName(file));
        string materials = Path.Combine(particlesDirectory, "materials");
        if (System.IO.Directory.Exists(materials))
        {
            var ogre = new OgreMaterialLibrary();
            foreach (var file in System.IO.Directory.EnumerateFiles(materials, "*.material").OrderBy(f => f, StringComparer.Ordinal))
                ogre.AddScript(File.ReadAllText(file, System.Text.Encoding.Latin1), Path.GetFileName(file));
            foreach (var (name, material) in ogre.Materials) library.Materials[name] = ParticleMaterial.From(material);
            foreach (var d in ogre.Diagnostics.Where(d => d.Code == OgreScriptError.Fatal))
                library.Errors.Add(d.ToString());
        }
        return library;
    }

    /// <summary>
    /// Adds the systems of one script's text (errors are collected, not thrown).
    /// </summary>
    public void AddScript(string text, string file)
    {
        ScriptFiles++;
        try
        {
            foreach (var node in PuScriptReader.Parse(text).Where(n => n.Kind == "system"))
            {
                var system = PuSystemDef.From(node, file);
                AllSystems.Add(system);
                if (Systems.ContainsKey(system.Name)) Duplicates.Add($"{system.Name} ({file})");
                Systems[system.Name] = system;
            }
        }
        catch (PuScriptException e)
        {
            Errors.Add($"{file}: {e.Message}");
        }
    }
}