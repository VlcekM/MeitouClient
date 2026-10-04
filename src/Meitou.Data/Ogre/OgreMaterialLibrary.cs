using Meitou.Content;

namespace Meitou.Data.Ogre;

/// <summary>
/// Materials and GPU programs from a set of Ogre scripts, found by name the way Ogre's MaterialManager and
/// GpuProgramManager find them (globally, case-sensitive). Each file is compiled on its own: inheritance only sees
/// the same file and what it imports. See docs/formats/ogre-material.md.
/// </summary>
public sealed class OgreMaterialLibrary
{
    /// <summary>Object types that define GPU programs.</summary>
    public static readonly string[] ProgramClasses =
        ["vertex_program", "fragment_program", "geometry_program", "tessellation_hull_program", "tessellation_domain_program", "compute_program"];

    public Dictionary<string, OgreMaterial> Materials { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OgreGpuProgram> Programs { get; } = new(StringComparer.Ordinal);

    /// <summary>Later definitions of a name already in <see cref="Materials"/> (which one Kenshi keeps is Unknown; the first is kept here).</summary>
    public List<OgreMaterial> DuplicateMaterials { get; } = [];

    public List<OgreScriptFile> Files { get; } = [];

    /// <summary>Every diagnostic of every file, plus <see cref="OgreScriptError.Fatal"/> for files that failed to parse.</summary>
    public List<OgreScriptDiagnostic> Diagnostics { get; } = [];

    /// <summary>Top-level object types seen (material, vertex_program, compositor_node...) and how many of each.</summary>
    public Dictionary<string, int> TopLevelClasses { get; } = [];

    public OgreMaterial? Find(string name) => Materials.GetValueOrDefault(name);

    /// <summary>Compiles one script and adds what it defines. A parse failure is recorded, not thrown.</summary>
    public OgreScriptFile? AddScript(string text, string name, Func<string, string?>? importResolver = null)
    {
        OgreScriptFile file;
        try
        {
            file = OgreScriptCompiler.Compile(text, name, importResolver);
        }
        catch (OgreScriptException e)
        {
            Diagnostics.Add(new(OgreScriptError.Fatal, e.File, e.Line, e.Message));
            return null;
        }
        Add(file);
        return file;
    }

    /// <summary>Adds the objects of a compiled file in order, as Ogre's translators would create them.</summary>
    public void Add(OgreScriptFile file)
    {
        Files.Add(file);
        Diagnostics.AddRange(file.Diagnostics);
        foreach (var obj in file.Objects)
        {
            TopLevelClasses[obj.Class] = TopLevelClasses.GetValueOrDefault(obj.Class) + 1;
            if (obj.Class == "material")
            {
                if (obj.Name.Length == 0)
                {
                    Diagnostics.Add(new(OgreScriptError.ObjectNameExpected, obj.File, obj.Line));
                    continue;
                }
                var material = new OgreMaterial(obj);
                if (!Materials.TryAdd(obj.Name, material))
                {
                    DuplicateMaterials.Add(material);
                    Diagnostics.Add(new(OgreScriptError.DuplicateDefinition, obj.File, obj.Line, $"material {obj.Name}, first in {Materials[obj.Name].File}"));
                }
            }
            else if (ProgramClasses.Contains(obj.Class) && obj.Name.Length > 0)
            {
                if (!Programs.TryAdd(obj.Name, new OgreGpuProgram(obj)))
                    Diagnostics.Add(new(OgreScriptError.DuplicateDefinition, obj.File, obj.Line, $"{obj.Class} {obj.Name}, first in {Programs[obj.Name].Source.File}"));
            }
        }
    }

    /// <summary>Compiles script files in the given order; imports are resolved through <paramref name="resources"/>.</summary>
    public static OgreMaterialLibrary Load(IEnumerable<string> paths, OgreScriptResources resources)
    {
        var library = new OgreMaterialLibrary();
        foreach (var path in paths)
            library.AddScript(OgreScriptLexer.ReadText(path), Path.GetRelativePath(resources.Root, path), resources.ReadScript);
        return library;
    }

    /// <summary>
    /// What Ogre loads at start-up: the <c>.program</c> and <c>.material</c> files in the <c>resources.cfg</c> folders
    /// (not recursive), in Ogre's order. <c>.particle</c>, <c>.compositor</c> and <c>.os</c> scripts are not compiled.
    /// </summary>
    public static OgreMaterialLibrary LoadConfigured(GameInstall install, out OgreScriptResources resources)
    {
        resources = OgreScriptResources.ReadConfig(Path.Combine(install.Root, "resources.cfg"), install.Root);
        return Load(resources.ScriptFiles("*.program", "*.material"), resources);
    }

    /// <summary>Every <c>.program</c>, then every <c>.material</c> anywhere under data/, imports resolved over all of data/.</summary>
    public static OgreMaterialLibrary LoadAll(GameInstall install, out OgreScriptResources resources)
    {
        resources = OgreScriptResources.FromDirectory(install.DataDirectory);
        return Load(resources.ScriptFiles("*.program", "*.material"), resources);
    }
}
