namespace Meitou.Data.Ogre;

/// <summary>One line of <c>resources.cfg</c>: <c>FileSystem=./data/materials</c> under a <c>[Group]</c>.</summary>
public sealed record OgreResourceLocation(string Group, string Type, string Path);

/// <summary>
/// Ogre's resource folders from <c>resources.cfg</c> and a by-name index over them. Ogre looks resources up by bare
/// file name (case-insensitive on Windows' FileSystem archive); <c>FileSystem</c> locations are not recursive.
/// </summary>
public sealed class OgreScriptResources
{
    /// <summary>Script patterns in the order Ogre's ScriptCompilerManager registers them (all files of one, then the next).</summary>
    public static readonly string[] ScriptPatterns = ["*.program", "*.material", "*.particle", "*.compositor", "*.os"];

    readonly Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase);
    bool recursive;

    /// <summary>Folder that script names in diagnostics are relative to (the install, or the indexed folder).</summary>
    public string Root { get; private init; } = "";

    public List<OgreResourceLocation> Locations { get; } = [];

    /// <summary>Folders listed but missing on disk, and non-FileSystem locations (e.g. <c>Zip=</c>), which are not indexed.</summary>
    public List<OgreResourceLocation> Skipped { get; } = [];

    /// <summary>Reads <c>resources.cfg</c>; relative paths are resolved against <paramref name="root"/> (the install folder).</summary>
    public static OgreScriptResources ReadConfig(string configFile, string root)
    {
        var result = new OgreScriptResources { Root = Path.GetFullPath(root) };
        string group = "General";
        foreach (var raw in File.ReadAllLines(configFile))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']') { group = line[1..^1]; continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var location = new OgreResourceLocation(group, line[..eq].Trim(), Path.GetFullPath(Path.Combine(root, line[(eq + 1)..].Trim())));
            if (location.Type == "FileSystem" && Directory.Exists(location.Path)) result.Locations.Add(location);
            else result.Skipped.Add(location);
        }
        foreach (var location in result.Locations)
            foreach (var file in Files(location.Path, "*", SearchOption.TopDirectoryOnly))
                result.byName.TryAdd(Path.GetFileName(file), file);
        return result;
    }

    /// <summary>An index over every file below <paramref name="directory"/> (first path wins for a repeated name).</summary>
    public static OgreScriptResources FromDirectory(string directory)
    {
        var result = new OgreScriptResources { recursive = true, Root = Path.GetFullPath(directory) };
        result.Locations.Add(new OgreResourceLocation("General", "FileSystem", Path.GetFullPath(directory)));
        foreach (var file in Files(directory, "*", SearchOption.AllDirectories))
            result.byName.TryAdd(Path.GetFileName(file), file);
        return result;
    }

    /// <summary>The path of a resource by bare file name, or null.</summary>
    public string? Find(string name) => byName.GetValueOrDefault(name);

    public bool Contains(string name) => byName.ContainsKey(name);

    /// <summary>
    /// Script files in Ogre's parse order: groups as listed, then pattern by pattern, then location by location,
    /// then by file name. A file in two listed folders is returned for each. For <see cref="FromDirectory"/>
    /// indexes the folder is searched recursively.
    /// </summary>
    public IEnumerable<string> ScriptFiles(params string[] patterns)
    {
        foreach (var group in Locations.Select(l => l.Group).Distinct())
            foreach (var pattern in patterns)
                foreach (var location in Locations.Where(l => l.Group == group))
                    foreach (var file in Files(location.Path, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                        yield return file;
    }

    /// <summary>Text of a script by bare name, for <c>import</c>.</summary>
    public string? ReadScript(string name) => Find(name) is { } path ? OgreScriptLexer.ReadText(path) : null;

    static IEnumerable<string> Files(string directory, string pattern, SearchOption option) =>
        Directory.EnumerateFiles(directory, pattern, option)
            // EnumerateFiles' "*.material" also matches longer extensions on Windows (8.3 rule); keep exact ones.
            .Where(f => pattern == "*" || f.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
}
