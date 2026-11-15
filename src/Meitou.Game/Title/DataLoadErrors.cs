using Meitou.Content;
using Meitou.Data;
using Meitou.Localisation;

namespace Meitou.Game.Title;

/// <summary>
/// The data files of the load order that failed to load (<see cref="GameDatabase.NotLoaded"/>), told as the game tells them (docs/formats/fcs-mod.md,
/// "Files that fail to load"): a log line each, an error box per core file, then one box listing the mods.
/// </summary>
internal static class DataLoadErrors
{
    /// <summary>
    /// The core file that has no <c>.mod</c> extension to add.
    /// </summary>
    const string BaseFile = "gamedata.base";

    /// <summary>
    /// Writes the game's log line for each file that failed: <c>[Mods] Failed to load gamedata.base.</c>, <c>[Mods] Failed to load core mod: X</c>,
    /// <c>[Mods] Failed to load mod: X</c> with <c> (*)</c> after a Workshop mod.
    /// </summary>
    public static void Log(GameDatabase db, GameInstall install)
    {
        foreach (var entry in db.NotLoaded)
            Console.WriteLine(LogLine(entry, install));
    }

    /// <summary>
    /// Opens the error boxes for the files that failed, one after another: "Error!" per core file, then "Mod error(s) (Ignored files)" with the
    /// mods' names. Nothing when every file loaded.
    /// </summary>
    public static void Show(PlayerInterface player, GameDatabase db)
    {
        var boxes = Boxes(db);
        ShowFrom(player, boxes, 0);
    }

    /// <summary>
    /// The log line of one file that failed.
    /// </summary>
    static string LogLine(LoadOrderEntry entry, GameInstall install)
    {
        if (entry.Name == BaseFile) return "[Mods] Failed to load gamedata.base.";
        if (LoadOrder.BaseFiles.Contains(entry.Name)) return $"[Mods] Failed to load core mod: {ModName(entry)}";

        return $"[Mods] Failed to load mod: {ModName(entry)}{(IsWorkshop(entry, install) ? " (*)" : "")}";
    }

    /// <summary>
    /// The boxes to show, in the game's order: each core file's, then the mods' list.
    /// </summary>
    static List<(string Title, string Text)> Boxes(GameDatabase db)
    {
        var boxes = new List<(string Title, string Text)>();
        var mods = new List<string>();
        foreach (var entry in db.NotLoaded)
        {
            if (LoadOrder.BaseFiles.Contains(entry.Name))
                boxes.Add(("Error!", Loc.Tr("Failed to load the core '{1}' file.").Replace("{1}", entry.Name)));
            else
                mods.Add(ModName(entry));
        }

        if (mods.Count == 0) return boxes;

        string list = string.Concat(mods.Select(m => "\n" + m));
        boxes.Add(("Mod error(s) (Ignored files)", Loc.Tr("Failed to load the following mod(s):") + list));
        return boxes;
    }

    /// <summary>
    /// Shows box <paramref name="index"/>; its Ok shows the next.
    /// </summary>
    static void ShowFrom(PlayerInterface player, List<(string Title, string Text)> boxes, int index)
    {
        if (index >= boxes.Count) return;

        var (title, text) = boxes[index];
        player.MessageBox(title, text, () => ShowFrom(player, boxes, index + 1));
    }

    /// <summary>
    /// The mod's name: its file name without <c>.mod</c>.
    /// </summary>
    static string ModName(LoadOrderEntry entry) => Path.GetFileNameWithoutExtension(entry.Name);

    /// <summary>
    /// Whether the mod comes from the Workshop (outside the install's <c>mods</c> folder).
    /// </summary>
    static bool IsWorkshop(LoadOrderEntry entry, GameInstall install) =>
        !Path.GetFullPath(entry.Path).StartsWith(Path.GetFullPath(install.ModsDirectory), StringComparison.OrdinalIgnoreCase);
}
