using Meitou.Content;

namespace Meitou.Data.Ogre;

/// <summary>
/// Finds art files the way the viewer needs them: an existing absolute path; else the bare file name through the <c>resources.cfg</c> and enabled mod
/// folders (Ogre's lookup, not recursive; the game cuts FCS's <c>.\data\...</c> paths to it), then the path relative to the install, then the bare
/// name anywhere under data/ (for files outside those folders).
/// </summary>
public sealed class AssetLocator
{
    /// <summary>
    /// The install the files are found in.
    /// </summary>
    readonly GameInstall _install;
    /// <summary>
    /// Every file under data/ by bare name, indexed on first use (the last rule).
    /// </summary>
    readonly Lazy<OgreScriptResources> _everything;
    /// <summary>
    /// The material library of <see cref="Configured"/>, parsed on first use.
    /// </summary>
    readonly Lazy<OgreMaterialLibrary> _materials;

    /// <summary>
    /// The names found so far: a bare name's lookup tries two paths on disk before the resource folders, and a town's characters asked for the same
    /// animation and skeleton files thousands of times while loading. A name not found is looked for again next time.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Path, string How)> _found = new();

    /// <summary>
    /// A locator for <paramref name="install"/>; the indexes are made on first use.
    /// </summary>
    public AssetLocator(GameInstall install)
    {
        this._install = install;
        Configured = GameResources.For(install);
        _everything = new(() => OgreScriptResources.FromDirectory(install.DataDirectory));
        _materials = new(() => OgreMaterialLibrary.Load(Configured));
    }

    /// <summary>
    /// The resource locations the game registers for the install (resources.cfg, the language and the enabled mods).
    /// </summary>
    public OgreScriptResources Configured { get; }

    /// <summary>
    /// The material scripts the game compiles (<see cref="OgreMaterialLibrary.Load(OgreScriptResources)"/>), parsed once on first use and shared;
    /// read only.
    /// </summary>
    public OgreMaterialLibrary Materials => _materials.Value;

    /// <summary>
    /// Full path of <paramref name="name"/>, or null. <paramref name="how"/> says which rule found it.
    /// </summary>
    public string? Find(string name, out string how)
    {
        if (_found.TryGetValue(name, out var known))
        {
            how = known.How;
            return known.Path;
        }

        var path = Search(name, out how);
        if (path is not null) _found.TryAdd(name, (path, how));
        return path;
    }

    /// <summary>
    /// Finds <paramref name="name"/> by the rules in order (an absolute path, the configured locations, the path in the install, anywhere under
    /// data/); <paramref name="how"/> names the rule.
    /// </summary>
    string? Search(string name, out string how)
    {
        how = "";
        if (string.IsNullOrWhiteSpace(name)) return null;

        name = name.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(name) && GameFiles.Exists(name))
        {
            how = "path";
            return Path.GetFullPath(GameFiles.Resolve(name));
        }

        var bare = Path.GetFileName(name);

        // A record's .\data\... path is cut to its bare name before Ogre's index is asked (docs/formats/overview.md), so a mod's file of the same
        // name wins.
        if (!Path.IsPathRooted(name) && Configured.Find(bare) is { } configured)
        {
            how = "resources.cfg";
            return configured;
        }

        if (!Path.IsPathRooted(name))
        {
            var underInstall = GameFiles.Resolve(Path.GetFullPath(Path.Combine(_install.Root, name)));
            if (File.Exists(underInstall))
            {
                how = "install path";
                return underInstall;
            }

            if (File.Exists(name))
            {
                how = "path";
                return Path.GetFullPath(name);
            }
        }

        if (Configured.Find(bare) is { } named)
        {
            how = "resources.cfg";
            return named;
        }

        if (_everything.Value.Find(bare) is { } anywhere)
        {
            how = "data/ (outside resources.cfg)";
            return anywhere;
        }

        return null;
    }

    /// <summary>
    /// Full path of <paramref name="name"/>, or null.
    /// </summary>
    public string? Find(string name) => Find(name, out _);

    /// <summary>
    /// The file a record's path names, by its bare name in resource group <paramref name="group"/> alone (Ogre's <c>resourceExists(group, name)</c>),
    /// or null. A bare name may be in several groups: an item's <c>Water.png</c> is in Icons, and <c>data/materials/water.png</c> in General.
    /// </summary>
    public string? FindInGroup(string name, string group)
    {
        string bare = Path.GetFileName(name.Trim().Replace('\\', '/'));
        return Configured.Contains(bare, group) ? Configured.Find(bare, group) : null;
    }

    /// <summary>
    /// <paramref name="path"/> relative to the install's folder.
    /// </summary>
    public string Relative(string path) => Path.GetRelativePath(_install.Root, path);

    /// <summary>
    /// A character mesh the way Kenshi finds it: the bare file name through resources.cfg first, else <see cref="Find(string)"/> on the FCS path.
    /// </summary>
    public string? FindMesh(string fcsPath) => Configured.Find(Path.GetFileName(fcsPath.Replace('\\', '/'))) ?? Find(fcsPath);
}
