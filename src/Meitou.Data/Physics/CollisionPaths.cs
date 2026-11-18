using Meitou.Content;
using Meitou.Data.Ogre;

namespace Meitou.Data.Physics;

/// <summary>
/// Finds the collision (and other asset) files that records name by install-relative paths.
/// </summary>
public static class CollisionPaths
{
    /// <summary>
    /// Ogre's default resource group, which the game's collision opener searches first before the others.
    /// </summary>
    const string DefaultGroup = "General";

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
    /// else the file of that bare name in the game's resource folders (an enabled mod's own folders included), or null when the field is empty or
    /// no such file exists.
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
        if (File.Exists(full)) return full;

        // Many mods name their file as if it were the base game's (.\data\buildings\...) but ship it elsewhere in their own folder; the game asks
        // Ogre for the bare name (docs/formats/overview.md "Resource lookup"), which finds it there.
        return Index(install)?.Find(Path.GetFileName(rel), DefaultGroup) is { } byName && File.Exists(byName) ? byName : null;
    }

    /// <summary>
    /// The install's resource index (<see cref="GameResources"/>), or null for an install without <c>resources.cfg</c> (a test install).
    /// </summary>
    static OgreScriptResources? Index(GameInstall install) =>
        File.Exists(Path.Combine(install.Root, "resources.cfg")) ? GameResources.For(install) : null;

    /// <summary>
    /// The collision file a record's path names, as docs/formats/collision.md "File lookup" describes the game opening a building part's
    /// (<c>xml collision</c>, <c>destroyed collision</c>): by its bare name, the XML beside a cooked <c>.bin</c> of that name first, else the XML
    /// of that name (the last enabled mod holding it wins, or an enabled mod's rebind of it); a file outside the resource folders by
    /// <see cref="Resolve"/>. Every file the collision cache reads goes this way. Null when none is found.
    /// </summary>
    public static string? ResolveCollision(GameInstall install, string recordPath)
    {
        if (string.IsNullOrWhiteSpace(recordPath)) return null;
        if (Index(install) is not { } resources) return Resolve(install, recordPath);

        string name = Path.GetFileName(recordPath.Replace('\\', '/'));
        if (CookedSource(resources, name) is { } cooked) return cooked;
        if (resources.Find(name, DefaultGroup) is { } byName && File.Exists(byName)) return Rebound(install, resources, byName);

        return Resolve(install, recordPath);
    }

    /// <summary>
    /// The enabled mods' rebinds and each XML's rebound file, by the resource index they were read with (<see cref="GameResources.For"/> makes a
    /// new one when <c>mods.cfg</c> changes), so the collision lookups read the rebinds once and work out each file's rebind once.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OgreScriptResources, ReboundFiles> RebindsByIndex = new();

    /// <summary>
    /// One resource index's rebinds and the files worked out so far (null: hidden).
    /// </summary>
    sealed class ReboundFiles(ReKenshiRebinds rebinds)
    {
        /// <summary>
        /// The enabled mods' rebinds.
        /// </summary>
        public ReKenshiRebinds Rebinds { get; } = rebinds;

        /// <summary>
        /// Each XML found and the file read for it; null (not made) when no mod rebinds a file.
        /// </summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, string?>? Files { get; } =
            rebinds.IsEmpty ? null : new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The XML the game opens through Ogre when no cooked file of its name exists, or the file an enabled mod rebinds it to
    /// (<see cref="ReKenshiRebinds"/>); null for a rebind to no file (<c>HIDE</c>). Without any rebind, the path itself.
    /// </summary>
    static string? Rebound(GameInstall install, OgreScriptResources resources, string xml)
    {
        if (!RebindsByIndex.TryGetValue(resources, out var rebound))
        {
            rebound = new ReboundFiles(ReKenshiRebinds.For(install));
            RebindsByIndex.AddOrUpdate(resources, rebound);
        }
        if (rebound.Files is not { } files) return xml;
        if (files.TryGetValue(xml, out var known)) return known;

        string target = rebound.Rebinds.Redirect(xml);
        string? result = ReferenceEquals(target, xml) || File.Exists(target) ? target : null;
        files.TryAdd(xml, result);
        return result;
    }

    /// <summary>
    /// The XML that the cooked file the game prefers for <paramref name="name"/> (its stem + <c>.bin</c>, found by bare name) was made from: the
    /// file of that name beside it; null when there is no cooked file or no XML beside it. Meitou reads the XML, not the cooked file.
    /// </summary>
    static string? CookedSource(OgreScriptResources resources, string name)
    {
        if (resources.Find(Path.ChangeExtension(name, ".bin"), DefaultGroup) is not { } bin) return null;

        var beside = GameFiles.Resolve(Path.Combine(Path.GetDirectoryName(bin)!, name));
        return File.Exists(beside) ? beside : null;
    }
}