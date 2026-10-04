using Meitou.Content;

namespace Meitou.Data;

/// <summary>
/// The game-data files to load, in order, as the game picks them (docs/formats/overview.md, "Load order"):
/// the core files, then the mods listed in <c>data/mods.cfg</c>.
/// </summary>
public sealed class LoadOrder
{
    /// <summary>Core files, always first and in this order (a fixed list in the game; missing ones are skipped).</summary>
    public static readonly IReadOnlyList<string> BaseFiles = ["gamedata.base", "Newwworld.mod", "Dialogue.mod", "rebirth.mod"];

    // mods.cfg entries the game ignores because they name core files.
    static readonly string[] CoreModNames = ["Newwworld", "Dialogue", "rebirth"];

    public List<LoadOrderEntry> Entries { get; } = [];

    public List<GameDataIssue> Issues { get; } = [];

    public static LoadOrder FromInstall(GameInstall install)
    {
        var order = new LoadOrder();
        foreach (var name in BaseFiles)
        {
            var path = Path.Combine(install.DataDirectory, name);
            if (File.Exists(path))
                order.Entries.Add(new LoadOrderEntry(name, path));
        }

        var modsCfg = Path.Combine(install.DataDirectory, "mods.cfg");
        if (!File.Exists(modsCfg))
            return order;
        var available = FindMods(install);
        foreach (var name in ParseModsCfg(File.ReadAllLines(modsCfg)))
        {
            if (order.Entries.Any(e => e.Name == name + ".mod"))
                continue;
            if (available.TryGetValue(name, out var path))
                order.Entries.Add(new LoadOrderEntry(name + ".mod", path));
            else
                order.Issues.Add(new GameDataIssue(GameDataIssueKind.MissingFile, "mods.cfg", null, $"mod '{name}' not found"));
        }
        return order;
    }

    /// <summary>
    /// Mod names (without <c>.mod</c>) from <c>mods.cfg</c>, with the game's rules: lines are taken as they are
    /// (not trimmed); empty lines and lines starting with <c>#</c>, <c>/</c> or <c>;</c> are skipped; the text
    /// after the last dot must be exactly <c>mod</c>; core files are skipped. Duplicates are kept here.
    /// </summary>
    public static IEnumerable<string> ParseModsCfg(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] is '#' or '/' or ';') continue;
            int dot = line.LastIndexOf('.');
            if (dot < 0 || line[(dot + 1)..] != "mod") continue;
            var name = line[..dot];
            if (!CoreModNames.Contains(name)) yield return name;
        }
    }

    /// <summary>
    /// Mods on disk by name (case-sensitive, as in the game): workshop items first, each the first <c>*.mod</c>
    /// file of its folder in sorted order; then <c>mods/&lt;name&gt;/&lt;name&gt;.mod</c>, which replaces a workshop
    /// mod of the same name. The game asks Steam for the subscribed items; we take every downloaded item.
    /// </summary>
    public static Dictionary<string, string> FindMods(GameInstall install)
    {
        var mods = new Dictionary<string, string>(StringComparer.Ordinal);
        if (install.WorkshopDirectory is { } workshop)
            foreach (var item in Directory.EnumerateDirectories(workshop).Order(StringComparer.Ordinal))
            {
                var file = Directory.EnumerateFiles(item)
                    .Where(f => Path.GetExtension(f) == ".mod")
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault();
                if (file is not null)
                    mods[Path.GetFileNameWithoutExtension(file)] = file;
            }
        if (Directory.Exists(install.ModsDirectory))
            foreach (var folder in Directory.EnumerateDirectories(install.ModsDirectory))
            {
                var name = Path.GetFileName(folder);
                var file = Path.Combine(folder, name + ".mod");
                if (File.Exists(file))
                    mods[name] = file;
            }
        return mods;
    }
}

public sealed record LoadOrderEntry(string Name, string Path);
