using Meitou.Content;

namespace Meitou.Data.Ogre;

/// <summary>One resource location: <c>FileSystem=./data/materials</c> under a <c>[Group]</c> of <c>resources.cfg</c>, or a mod folder.</summary>
public sealed record OgreResourceLocation(string Group, string Type, string Path);

/// <summary>
/// Ogre's resource locations as Kenshi registers them (docs/formats/ogre-material.md, "Resource locations"), with a
/// by-name index per resource group. Ogre looks resources up by bare file name (case-insensitive on Windows'
/// FileSystem archive); locations are not recursive; within a group the last-added location wins a name.
/// </summary>
public sealed class OgreScriptResources
{
    /// <summary>Script patterns in the order Ogre's ScriptCompilerManager registers them (all files of one, then the next).</summary>
    public static readonly string[] ScriptPatterns = ["*.program", "*.material", "*.particle", "*.compositor", "*.os"];

    /// <summary>Ogre's own groups, which exist besides the configured ones (normally empty).</summary>
    static readonly string[] BuiltInGroups = ["Autodetect", "Internal"];

    /// <summary>Mod sub-folders Kenshi does not register (relative to the mod folder).</summary>
    static readonly string[] SkippedModFolders = ["leveldata", "newland/leveldata", "locale", "LC_MESSAGES"];

    // Group -> bare name -> path. Groups in ordinal order, as Ogre's std::map keeps them.
    readonly SortedDictionary<string, Dictionary<string, string>> groups = new(StringComparer.Ordinal);

    // resources.cfg folder relative to data/ -> group (later sections overwrite earlier ones); used to place mod folders.
    readonly Dictionary<string, string> folderGroups = new(StringComparer.OrdinalIgnoreCase);
    bool recursive;

    /// <summary>Folder that script names in diagnostics are relative to (the install, or the indexed folder).</summary>
    public string Root { get; private init; } = "";

    /// <summary>Indexed locations in registration order.</summary>
    public List<OgreResourceLocation> Locations { get; } = [];

    /// <summary>
    /// Locations Kenshi registers that are not indexed here: folders missing on disk (empty for Ogre too) and
    /// non-FileSystem locations (<c>Zip=./data/meshes/OgreCore.zip</c>, Ogre's own overlay media).
    /// </summary>
    public List<OgreResourceLocation> Skipped { get; } = [];

    /// <summary>Resource groups in ordinal order (Ogre's search order for a name not in the requested group).</summary>
    public IEnumerable<string> Groups => groups.Keys;

    /// <summary>
    /// Reads <c>resources.cfg</c> the way Kenshi registers it: sections in ordinal name order, lines within a section
    /// by type then file order, and the first <c>/land</c> of a path rewritten to <c>/newland/land</c>. Relative
    /// paths are resolved against <paramref name="root"/> (the install folder).
    /// </summary>
    public static OgreScriptResources ReadConfig(string configFile, string root)
    {
        var result = new OgreScriptResources { Root = Path.GetFullPath(root) };
        foreach (var group in BuiltInGroups) result.groups[group] = new(StringComparer.OrdinalIgnoreCase);

        var sections = new SortedDictionary<string, List<(string Type, string Path)>>(StringComparer.Ordinal);
        string section = "";
        foreach (var raw in File.ReadAllLines(configFile))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']') { section = line[1..^1]; continue; }
            int sep = line.IndexOfAny(['\t', ':', '=']);
            if (sep <= 0) continue;
            if (!sections.TryGetValue(section, out var lines)) sections[section] = lines = [];
            lines.Add((line[..sep].Trim(), line[(sep + 1)..].Trim()));
        }

        var dataDirectory = Path.GetFullPath(Path.Combine(root, "data"));
        foreach (var (group, lines) in sections)
            foreach (var (type, rawPath) in lines.OrderBy(l => l.Type, StringComparer.Ordinal)) // stable: file order within a type
            {
                var path = Path.GetFullPath(Path.Combine(root, RewriteLand(rawPath)));
                var location = new OgreResourceLocation(group, type, path);
                if (type == "FileSystem")
                    result.folderGroups[Normalize(Path.GetRelativePath(dataDirectory, path))] = group;
                result.Add(location);
            }
        return result;
    }

    /// <summary>
    /// <c>resources.cfg</c> plus the folders of the mods in <paramref name="loadOrder"/> (core files excluded), as
    /// <see cref="AddModFolder"/> registers them.
    /// </summary>
    public static OgreScriptResources ForInstall(GameInstall install, LoadOrder loadOrder)
    {
        var result = ReadConfig(Path.Combine(install.Root, "resources.cfg"), install.Root);
        foreach (var entry in loadOrder.Entries.Where(e => !LoadOrder.BaseFiles.Contains(e.Name)))
            result.AddModFolder(Path.GetDirectoryName(entry.Path)!);
        return result;
    }

    /// <summary>
    /// Registers a mod folder like Kenshi: every sub-folder (any depth, sorted) that directly holds a file becomes a
    /// location in the group of the matching <c>resources.cfg</c> folder (or its nearest listed parent; else
    /// <c>General</c>), skipping <c>leveldata</c>, <c>newland/leveldata</c>, <c>locale</c> and <c>LC_MESSAGES</c>; the
    /// mod folder itself joins <c>General</c> if it directly holds a file other than <c>_*</c>, <c>*.mod</c> and
    /// <c>*.translation</c>.
    /// </summary>
    public void AddModFolder(string modRoot)
    {
        modRoot = Path.GetFullPath(modRoot);
        if (!Directory.Exists(modRoot)) return;
        bool rootHasResources = Directory.EnumerateFiles(modRoot).Select(Path.GetFileName).Any(f =>
            !f!.StartsWith('_') && !f.EndsWith(".mod", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".translation", StringComparison.OrdinalIgnoreCase));
        if (rootHasResources)
            Add(new OgreResourceLocation("General", "FileSystem", modRoot));

        foreach (var folder in Directory.EnumerateDirectories(modRoot, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Normalize(Path.GetRelativePath(modRoot, folder));
            if (SkippedModFolders.Any(s => relative.Equals(s, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!Directory.EnumerateFiles(folder).Any()) continue;
            Add(new OgreResourceLocation(GroupOf(relative), "FileSystem", folder));
        }
    }

    /// <summary>An index over every file below <paramref name="directory"/> in one group (first path wins for a repeated name).</summary>
    public static OgreScriptResources FromDirectory(string directory)
    {
        var result = new OgreScriptResources { recursive = true, Root = Path.GetFullPath(directory) };
        var index = result.groups["General"] = new(StringComparer.OrdinalIgnoreCase);
        result.Locations.Add(new OgreResourceLocation("General", "FileSystem", Path.GetFullPath(directory)));
        foreach (var file in Files(directory, "*", SearchOption.AllDirectories))
            index.TryAdd(Path.GetFileName(file), file);
        return result;
    }

    /// <summary>
    /// The path of a resource by bare file name, or null: from <paramref name="group"/> if it has it, else from the
    /// first group (ordinal order) that does, as Ogre's <c>openResource</c> searches.
    /// </summary>
    public string? Find(string name, string? group = null)
    {
        if (group is not null && groups.TryGetValue(group, out var own) && own.TryGetValue(name, out var path))
            return path;
        foreach (var index in groups.Values)
            if (index.TryGetValue(name, out path))
                return path;
        return null;
    }

    public bool Contains(string name) => Find(name) is not null;

    /// <summary>
    /// The resource group a file of the index belongs to (the first, in ordinal order, whose index maps its bare name to this path), or
    /// <c>General</c> for a file outside every location.
    /// </summary>
    public string GroupOfFile(string path)
    {
        string name = Path.GetFileName(path);
        string full = Path.GetFullPath(path);
        foreach (var (group, index) in groups)
            if (index.TryGetValue(name, out var found) && string.Equals(Path.GetFullPath(found), full, StringComparison.OrdinalIgnoreCase))
                return group;
        return "General";
    }

    /// <summary>
    /// Script files in Kenshi's parse order: group <c>GUI</c> first, then the other groups in ordinal order; within a
    /// group pattern by pattern, then location by location, then by file name. A file in two locations is returned
    /// for each. For <see cref="FromDirectory"/> indexes the folder is searched recursively.
    /// </summary>
    public IEnumerable<string> ScriptFiles(params string[] patterns)
    {
        var order = groups.Keys.Where(g => g == "GUI").Concat(groups.Keys.Where(g => g != "GUI"));
        foreach (var group in order)
            foreach (var pattern in patterns)
                foreach (var location in Locations.Where(l => l.Group == group))
                    foreach (var file in Files(location.Path, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                        yield return file;
    }

    /// <summary>Text of a script by bare name, for <c>import</c> (searched across groups; Ogre would try the importing script's group first).</summary>
    public string? ReadScript(string name) => Find(name) is { } path ? OgreScriptLexer.ReadText(path) : null;

    void Add(OgreResourceLocation location)
    {
        if (!groups.TryGetValue(location.Group, out var index))
            groups[location.Group] = index = new(StringComparer.OrdinalIgnoreCase);
        if (location.Type != "FileSystem" || !Directory.Exists(location.Path))
        {
            Skipped.Add(location);
            return;
        }
        Locations.Add(location);
        foreach (var file in Files(location.Path, "*", SearchOption.TopDirectoryOnly))
            index[Path.GetFileName(file)] = file;
    }

    string GroupOf(string relativeFolder)
    {
        for (var folder = relativeFolder; folder.Length > 0; folder = folder.Contains('/') ? folder[..folder.LastIndexOf('/')] : "")
            if (folderGroups.TryGetValue(folder, out var group))
                return group;
        return "General";
    }

    /// <summary>Kenshi's rewrite of <c>resources.cfg</c> paths: <c>newland/</c> inserted after the first <c>/land</c>'s slash.</summary>
    static string RewriteLand(string path)
    {
        int at = path.Length > 4 ? path.IndexOf("/land", StringComparison.Ordinal) : -1;
        return at < 0 ? path : path.Insert(at + 1, "newland/");
    }

    static string Normalize(string relative)
    {
        var r = relative.Replace('\\', '/').TrimEnd('/');
        return r == "." ? "" : r;
    }

    static IEnumerable<string> Files(string directory, string pattern, SearchOption option) =>
        Directory.EnumerateFiles(directory, pattern, option)
            // EnumerateFiles' "*.material" also matches longer extensions on Windows (8.3 rule); keep exact ones.
            .Where(f => pattern == "*" || f.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
}
