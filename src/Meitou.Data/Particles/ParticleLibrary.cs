using System.Numerics;
using Meitou.Content;
using Meitou.Data.Ogre;

namespace Meitou.Data.Particles;

/// <summary>How a particle material blends (<c>scene_blend</c>).</summary>
public enum ParticleBlend { Add, Alpha, Modulate, ColourBlend, Opaque }

/// <summary>
/// What a particle needs of an Ogre <c>.material</c> from <c>data/particles/materials</c> (docs/formats/particle-universe.md): the texture,
/// the blend, the depth flags and the shading the vertex and fragment programs give. The shipped particle shaders (basic.hlsl) output
/// <c>texture · colour · vertexColour</c>; the "Ambient" vertex programs scale the particle colour by <c>clamp(5 sunY + 0.2, 0.1, 1)</c>; there is
/// no fog term (Verified in the shader source). The interior-mask clip is not reproduced.
/// </summary>
public sealed record ParticleMaterial(string Name, string? Texture, ParticleBlend Blend, bool DepthCheck, bool DepthWrite, bool Ambient, Vector4 ColourScale, bool Clamp)
{
    public static ParticleMaterial From(OgreMaterial m)
    {
        var pass = m.Techniques.FirstOrDefault()?.Passes.FirstOrDefault();
        if (pass is null) return new ParticleMaterial(m.Name, null, ParticleBlend.Alpha, true, false, false, Vector4.One, true);
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
                if (float.TryParse(p.Args[2], System.Globalization.CultureInfo.InvariantCulture, out float r) && float.TryParse(p.Args[3], System.Globalization.CultureInfo.InvariantCulture, out float g)
                    && float.TryParse(p.Args[4], System.Globalization.CultureInfo.InvariantCulture, out float b) && float.TryParse(p.Args[5], System.Globalization.CultureInfo.InvariantCulture, out float a))
                    colour = new Vector4(r, g, b, a);
        bool clamp = unit?.AddressMode is not { Count: > 0 } mode || mode[0] != "wrap";
        return new ParticleMaterial(m.Name, unit?.Texture, kind, pass.DepthCheck, pass.DepthWrite,
            pass.VertexProgram?.Name.Contains("Ambient", StringComparison.Ordinal) == true, colour, clamp);
    }
}

/// <summary>
/// The ParticleUniverse scripts (<c>data/particles/scripts/*.pu</c>) by system name, and the particle materials by name. A system is
/// found by its own name, not its file's (<c>Ash-Flakes_Light</c> is in <c>Ashland_MultiFlakes.pu</c>, <c>Volk-Cloud</c> in <c>Volc-Pumper.pu</c>).
/// </summary>
public sealed class ParticleLibrary
{
    public Dictionary<string, PuSystemDef> Systems { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every system read, in file order, including the ones whose name an earlier file took (<see cref="Duplicates"/>).</summary>
    public List<PuSystemDef> AllSystems { get; } = [];
    public Dictionary<string, ParticleMaterial> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files that did not read, with the reason (empty for the base game).</summary>
    public List<string> Errors { get; } = [];
    /// <summary>Systems whose name an earlier file already took (the first is kept).</summary>
    public List<string> Duplicates { get; } = [];
    public int ScriptFiles { get; private set; }
    public string? Directory { get; private set; }

    public PuSystemDef? FindSystem(string name) => Systems.GetValueOrDefault(name);
    public ParticleMaterial? FindMaterial(string name) => Materials.GetValueOrDefault(name);

    /// <summary>Reads every <c>*.pu</c> of <c>data/particles/scripts</c> and every <c>*.material</c> of <c>data/particles/materials</c>.</summary>
    public static ParticleLibrary Load(GameInstall install) => Load(Path.Combine(install.DataDirectory, "particles"));

    /// <summary>Reads <c>scripts/*.pu</c> and <c>materials/*.material</c> under <paramref name="particlesDirectory"/>.</summary>
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
            foreach (var d in ogre.Diagnostics.Where(d => d.Code == OgreScriptError.Fatal)) library.Errors.Add(d.ToString());
        }
        return library;
    }

    /// <summary>Adds the systems of one script's text (errors are collected, not thrown).</summary>
    public void AddScript(string text, string file)
    {
        ScriptFiles++;
        try
        {
            foreach (var node in PuScriptReader.Parse(text).Where(n => n.Kind == "system"))
            {
                var system = PuSystemDef.From(node, file);
                AllSystems.Add(system);
                if (!Systems.TryAdd(system.Name, system)) Duplicates.Add($"{system.Name} ({file})");
            }
        }
        catch (PuScriptException e)
        {
            Errors.Add($"{file}: {e.Message}");
        }
    }
}
