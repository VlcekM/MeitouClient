using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;

namespace Meitou.ModelViewer;

/// <summary>
/// Finds art files the way the viewer needs them: an existing path, a path relative to the install (FCS stores
/// <c>.\data\...</c>), then the bare file name through the <c>resources.cfg</c> and enabled mod folders (Ogre's lookup, not
/// recursive), then the bare name anywhere under data/ (for files outside those folders).
/// </summary>
public sealed class AssetLocator
{
    readonly GameInstall install;
    readonly Lazy<OgreScriptResources> everything;

    public AssetLocator(GameInstall install)
    {
        this.install = install;
        Configured = OgreScriptResources.ForInstall(install, LoadOrder.FromInstall(install));
        everything = new(() => OgreScriptResources.FromDirectory(install.DataDirectory));
    }

    public OgreScriptResources Configured { get; }

    /// <summary>Full path of <paramref name="name"/>, or null. <paramref name="how"/> says which rule found it.</summary>
    public string? Find(string name, out string how)
    {
        how = "";
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(name) && File.Exists(name)) { how = "path"; return Path.GetFullPath(name); }
        if (!Path.IsPathRooted(name))
        {
            var underInstall = Path.GetFullPath(Path.Combine(install.Root, name));
            if (File.Exists(underInstall)) { how = "install path"; return underInstall; }
            if (File.Exists(name)) { how = "path"; return Path.GetFullPath(name); }
        }
        var bare = Path.GetFileName(name);
        if (Configured.Find(bare) is { } configured) { how = "resources.cfg"; return configured; }
        if (everything.Value.Find(bare) is { } anywhere) { how = "data/ (outside resources.cfg)"; return anywhere; }
        return null;
    }

    public string? Find(string name) => Find(name, out _);

    public string Relative(string path) => Path.GetRelativePath(install.Root, path);
}
