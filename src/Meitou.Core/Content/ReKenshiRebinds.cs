using System.Collections.Concurrent;

namespace Meitou.Content;

/// <summary>
/// The file rebinds of the enabled mods' <c>RE_Kenshi.json</c> files (docs/formats/rekenshi.md "Data entries"): a file the original opens by
/// one path is read from another, or hidden. Made once per install and <c>mods.cfg</c> version and shared. The map readers ask
/// <see cref="DataFile"/> for every file they open by a fixed path and stamp into a cache key, so a rebound map is read, and cached, as one.
/// </summary>
public sealed class ReKenshiRebinds
{
    /// <summary>
    /// The rebinds per install root, <c>mods.cfg</c> file and its write time, made on first use.
    /// </summary>
    static readonly ConcurrentDictionary<(string Root, string ModList, DateTime Stamp), Lazy<ReKenshiRebinds>> Cache = new();

    /// <summary>
    /// The target that hides a file (RE_Kenshi's <c>HIDE</c>).
    /// </summary>
    const string Hide = "HIDE";

    /// <summary>
    /// The folder under the install root that no file is in: a hidden file reads as missing there.
    /// </summary>
    const string HiddenFolder = "RE_Kenshi HIDE";

    /// <summary>
    /// Matched path (<see cref="Key"/>) to the file read instead.
    /// </summary>
    readonly Dictionary<string, string> _targets = new(StringComparer.Ordinal);

    /// <summary>
    /// The install the rebinds belong to.
    /// </summary>
    readonly GameInstall _install;

    /// <summary>
    /// The rebinds of <paramref name="install"/>'s enabled mods.
    /// </summary>
    ReKenshiRebinds(GameInstall install) => _install = install;

    /// <summary>
    /// The rebinds of <paramref name="install"/> with its current <c>mods.cfg</c>, shared by every caller.
    /// </summary>
    public static ReKenshiRebinds For(GameInstall install)
    {
        string modList = ModList.ModsCfgPath(install);
        var key = (install.Root, modList, File.Exists(modList) ? File.GetLastWriteTimeUtc(modList) : default);
        return Cache.GetOrAdd(key, _ => new(() => Load(install))).Value;
    }

    /// <summary>
    /// The file to read for <paramref name="relativePath"/> under the install's data folder: an enabled mod's rebind of it, else the install's
    /// own (spelt as on disk, <see cref="GameFiles.Resolve"/>).
    /// </summary>
    public static string DataFile(GameInstall install, string relativePath) =>
        For(install).Redirect(GameFiles.Resolve(Path.Combine(install.DataDirectory, relativePath)));

    /// <summary>
    /// Whether no enabled mod rebinds any file: <see cref="Redirect"/> then returns every path as it is.
    /// </summary>
    public bool IsEmpty => _targets.Count == 0;

    /// <summary>
    /// <paramref name="path"/> (a file the game would open) or the file an enabled mod rebinds it to; a hidden file becomes a path where nothing is.
    /// </summary>
    public string Redirect(string path)
    {
        if (_targets.Count == 0) return path;

        return _targets.TryGetValue(KeyOf(path), out var target) ? target : path;
    }

    /// <summary>
    /// Reads the enabled mods' files in load order; a later mod's rebind of the same path replaces an earlier one (RE_Kenshi's map).
    /// </summary>
    static ReKenshiRebinds Load(GameInstall install)
    {
        var rebinds = new ReKenshiRebinds(install);
        var enabled = ReKenshiFile.EnabledMods(install);
        foreach (var mod in enabled)
        {
            if (ReKenshiFile.TryRead(mod.Folder) is not { } file) continue;

            foreach (var (from, to) in file.FileRebinds)
            {
                // A target no path can be (a NUL in it) costs RE_Kenshi that one file, and here that one rebind.
                if (rebinds.Target(to, enabled) is { } target) rebinds._targets[Key(from)] = target;
                else Console.Error.WriteLine($"mods      {mod.Name}: RE_Kenshi rebind of {from} to {to} names no file; skipped");
            }
        }

        return rebinds;
    }

    /// <summary>
    /// The file a rebind's target names: <c>HIDE</c> a path where nothing is; else <see cref="ReKenshiFile.ExpandModRoot"/>, a relative path
    /// taken from the install root, the original's working folder, <c>\</c> read as a separator on every system; null when it is no path.
    /// </summary>
    string? Target(string to, IReadOnlyList<ModInfo> enabled)
    {
        if (to == Hide) return Path.Combine(_install.Root, HiddenFolder, "file");

        string path = ReKenshiFile.ExpandModRoot(to, enabled).Replace('\\', '/');
        try
        {
            return GameFiles.Resolve(Path.GetFullPath(path, _install.Root));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The key a path the game opens is matched by: relative to the install root when it lies under it (<see cref="Key"/>).
    /// </summary>
    string KeyOf(string path)
    {
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(_install.Root, full);
        return Key(relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative);
    }

    /// <summary>
    /// A rebind path as a key: the original's mixed separators, a leading <c>./</c> and doubled slashes do not matter, nor does letter case
    /// (<c>data\newland/land\fullmap.tif</c>, <c>./data/newland//land/blendinfo.dat</c> and <c>data/newland/land/blendMap.png</c> each name one
    /// data file).
    /// </summary>
    static string Key(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".");
        return string.Join('/', parts).ToLowerInvariant();
    }
}
