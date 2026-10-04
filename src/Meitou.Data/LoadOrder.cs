using Meitou.Content;

namespace Meitou.Data;

/// <summary>
/// The game-data files to load, in order (docs/formats/overview.md, "Load order"): the base files that
/// exist, then the mods listed in <c>data/mods.cfg</c>.
/// </summary>
public sealed class LoadOrder
{
    /// <summary>Base files, always first and in this order; files that don't exist are skipped.</summary>
    public static readonly IReadOnlyList<string> BaseFiles =
        ["gamedata.base", "Newwworld.mod", "Dialogue.mod", "coltontown.mod", "Nizu.mod", "Mohamad.mod", "rebirth.mod"];

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
        if (File.Exists(modsCfg))
            foreach (var mod in ParseModsCfg(File.ReadAllLines(modsCfg)))
                order.AddMod(install, mod);
        return order;
    }

    /// <summary>Mod file names from <c>mods.cfg</c>: one per line; blank lines and base files are ignored.</summary>
    public static IEnumerable<string> ParseModsCfg(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !BaseFiles.Contains(l, StringComparer.OrdinalIgnoreCase));

    void AddMod(GameInstall install, string name)
    {
        if (Entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            return;
        string[] candidates =
        [
            Path.Combine(install.DataDirectory, name),
            Path.Combine(install.ModsDirectory, Path.GetFileNameWithoutExtension(name), name),
        ];
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
            Issues.Add(new GameDataIssue(GameDataIssueKind.MissingFile, "mods.cfg", null, $"mod '{name}' not found in data/ or mods/"));
        else
            Entries.Add(new LoadOrderEntry(name, path));
    }
}

public sealed record LoadOrderEntry(string Name, string Path);
