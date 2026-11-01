using System.Collections.Concurrent;
using Meitou.Content;

namespace Meitou.Data.Ogre;

/// <summary>
/// The resource index the game registers for an install (<see cref="OgreScriptResources.ForInstall"/>: <c>resources.cfg</c>, the language folder,
/// the enabled mods' folders), made once per install and <c>mods.cfg</c> version and shared, so the art lookup and the data files that the game opens
/// through Ogre (<c>areasmap.tga</c>, <c>names*.txt</c>, docs/formats/overview.md) see the same mods.
/// </summary>
public static class GameResources
{
    static readonly ConcurrentDictionary<(string Root, DateTime ModList), Lazy<OgreScriptResources>> cache = new();

    public static OgreScriptResources For(GameInstall install)
    {
        string modList = Path.Combine(install.DataDirectory, "mods.cfg");
        var key = (install.Root, File.Exists(modList) ? File.GetLastWriteTimeUtc(modList) : default);
        return cache.GetOrAdd(key, _ => new(() => OgreScriptResources.ForInstall(install, LoadOrder.FromInstall(install)))).Value;
    }

    /// <summary>
    /// The file the game opens for <paramref name="name"/> in <paramref name="group"/> (<c>resourceExists</c> then <c>openResource</c>: the location
    /// registered last wins, so a mod's file replaces the base game's), or null when the group has no such file.
    /// </summary>
    public static string? Find(GameInstall install, string name, string group)
    {
        var resources = For(install);
        return resources.Contains(name, group) ? resources.Find(name, group) : null;
    }
}
