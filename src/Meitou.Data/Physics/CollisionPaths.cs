using Meitou.Content;

namespace Meitou.Data.Physics;

/// <summary>
/// Finds the collision (and other asset) files that records name by install-relative paths.
/// </summary>
public static class CollisionPaths
{
    /// <summary>
    /// The mods found per install root, with the newer write time of its <c>mods</c> and Workshop folders when they were scanned.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, Dictionary<string, string> Mods)> ModCache = new();

    /// <summary>
    /// <see cref="ModList.FindMods"/> of an install (local mods over Workshop ones), rescanned when its <c>mods</c> or Workshop folder changes,
    /// since a mod can be subscribed or added while the process runs.
    /// </summary>
    static Dictionary<string, string> Mods(GameInstall install)
    {
        var stamp = Directory.GetLastWriteTimeUtc(install.ModsDirectory);
        if (install.WorkshopDirectory is { } workshop && Directory.GetLastWriteTimeUtc(workshop) is var w && w > stamp) stamp = w;
        if (ModCache.TryGetValue(install.Root, out var cached) && cached.Stamp == stamp) return cached.Mods;
        var found = ModList.FindMods(install);
        ModCache[install.Root] = (stamp, found);
        return found;
    }

    /// <summary>
    /// The folder of the mod a record path names: the exact name as the game matches mods, else the one mod that matches ignoring case
    /// (paths are case-insensitive on Windows); null when none or several do.
    /// </summary>
    static string? ModFolder(GameInstall install, string name)
    {
        var all = Mods(install);
        if (!all.TryGetValue(name, out var file))
        {
            var matches = all.Where(m => string.Equals(m.Key, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (matches.Count != 1) return null;
            file = matches[0].Value;
        }
        return Path.GetDirectoryName(file);
    }

    /// <summary>
    /// The file a record's path field names (<c>.\data\buildings\x.xml</c>, backslashes, optionally without the leading <c>.\</c>) under the install,
    /// or null when the field is empty or the file is not there.
    /// </summary>
    public static string? Resolve(GameInstall install, string recordPath)
    {
        if (string.IsNullOrWhiteSpace(recordPath)) return null;
        var rel = recordPath.Replace('\\', '/').TrimStart('.', '/');
        var full = GameFiles.Resolve(Path.Combine(install.Root, rel));
        if (File.Exists(full)) return full;
        // A mod's own files: <c>.\mods\Mod Name\x</c> is the folder of that mod, which for a Workshop mod is its Workshop folder, not a folder under
        // the install.
        if (rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && rel.IndexOf('/', 5) is var slash and > 5
            && ModFolder(install, rel[5..slash]) is { } folder)
        {
            full = GameFiles.Resolve(Path.Combine(folder, rel[(slash + 1)..]));
            if (File.Exists(full)) return full;
        }
        // Records may name the file relative to data/.
        full = GameFiles.Resolve(Path.Combine(install.DataDirectory, rel.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ? rel[5..] : rel));
        return File.Exists(full) ? full : null;
    }
}