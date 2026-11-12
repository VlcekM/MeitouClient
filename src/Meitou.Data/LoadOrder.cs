using Meitou.Content;
using Meitou.Localisation;

namespace Meitou.Data;

/// <summary>
/// The game-data files to load, in order, as the game picks them (docs/formats/overview.md, "Load order"): the core files, then the mods listed in
/// <c>data/mods.cfg</c>.
/// </summary>
public sealed class LoadOrder
{
    /// <summary>
    /// Core files, always first and in this order (a fixed list in the game; missing ones are skipped).
    /// </summary>
    public static readonly IReadOnlyList<string> BaseFiles = ["gamedata.base", "Newwworld.mod", "Dialogue.mod", "rebirth.mod"];

    /// <summary>
    /// The files to load, in order: the core files present, then the mods <c>mods.cfg</c> lists that were found.
    /// </summary>
    public List<LoadOrderEntry> Entries { get; } = [];

    /// <summary>
    /// Problems found building the order (a mod <c>mods.cfg</c> names that is not on disk).
    /// </summary>
    public List<GameDataIssue> Issues { get; } = [];

    /// <summary>
    /// The language the game runs in and the translation files it adds to the load order (core translation after the core files, a mod's translation
    /// after all mods; docs/formats/localisation.md). English, with no files, for <see cref="BaseGame"/>.
    /// </summary>
    public GameLanguage Language { get; private set; } = GameLanguage.English;

    /// <summary>
    /// Only the core files, without the mods in <c>mods.cfg</c>: the unmodded base game (English: no translation is applied).
    /// </summary>
    public static LoadOrder BaseGame(GameInstall install)
    {
        var order = FromInstall(install);
        order.Entries.RemoveAll(e => !BaseFiles.Contains(e.Name));
        order.Issues.Clear();
        order.Language = GameLanguage.English;
        return order;
    }

    /// <summary>
    /// The core files and the mods of <c>mods.cfg</c> in <paramref name="install"/>, with the language the game would run in.
    /// </summary>
    public static LoadOrder FromInstall(GameInstall install)
    {
        var order = FromInstallFiles(install);
        order.Language = GameLanguage.Detect(install,
            order.Entries.Where(e => !BaseFiles.Contains(e.Name)).Select(e => Path.GetDirectoryName(e.Path)!));
        return order;
    }

    /// <summary>
    /// The core files present, then each mod <c>mods.cfg</c> names in its order (a name already in the list is skipped, one not on disk is an issue).
    /// </summary>
    static LoadOrder FromInstallFiles(GameInstall install)
    {
        var order = new LoadOrder();
        foreach (var name in BaseFiles)
        {
            var path = Path.Combine(install.DataDirectory, name);
            if (File.Exists(path))
                order.Entries.Add(new LoadOrderEntry(name, path));
        }

        var modsCfg = ModList.ModsCfgPath(install);
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
                order.Issues.Add(new GameDataIssue(
                    GameDataIssueKind.MissingFile, name + ".mod", null, "mod file not found (mods.cfg names it)"));
        }

        return order;
    }

    /// <summary>
    /// Mod names from <c>mods.cfg</c> with the game's rules: <see cref="ModList.ParseModsCfg"/>.
    /// </summary>
    public static IEnumerable<string> ParseModsCfg(IEnumerable<string> lines) => ModList.ParseModsCfg(lines);

    /// <summary>
    /// Mods on disk by name: <see cref="ModList.FindMods"/>.
    /// </summary>
    public static Dictionary<string, string> FindMods(GameInstall install) => ModList.FindMods(install);
}

/// <summary>
/// One file of a load order: its file name (e.g. <c>Newwworld.mod</c>) and full path.
/// </summary>
public sealed record LoadOrderEntry(string Name, string Path);
