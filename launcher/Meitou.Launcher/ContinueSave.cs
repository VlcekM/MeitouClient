using Avalonia.Interactivity;
using Meitou.Content;

namespace Meitou.Launcher;

// The Continue the last save setting (docs/launcher.md "Continue the last save"): Play loads the newest save when it was made with the
// enabled mods.
public sealed partial class MainWindow
{
    // Options that pick the game's start themselves; the game refuses --load beside the others.
    static readonly string[] StartOptions = ["--load", "--new-game", "--no-population", "--duel", "--sandbox", "--battle", "--terrain-sandbox"];

    void Continue_Click(object? sender, RoutedEventArgs e)
    {
        config.ContinueLastSave = ContinueBox.IsChecked == true;
        config.Save();
    }

    /// <summary>
    /// The save folder Play loads, or null to start at the menu; the note (empty when the setting is off) is added to Play's status.
    /// </summary>
    async Task<(string? Folder, string Note)> ContinueTarget(GameInstall gameInstall)
    {
        if (!config.ContinueLastSave) return (null, "");
        if (SplitArguments(config.LaunchArguments).FirstOrDefault(StartOptions.Contains) is { } option)
            return (null, Tr(" Not continuing the last save: Launch arguments have {1}.", option));

        var enabled = mods.Where(m => m.Enabled).Select(m => m.Name).ToList();
        try
        {
            return await Task.Run(() => NewestSave(gameInstall, enabled));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, Tr(" Could not read the last save: {1}", ex.Message));
        }
    }

    static (string? Folder, string Note) NewestSave(GameInstall gameInstall, List<string> enabled)
    {
        var slots = SaveModList.Scan(SaveModList.Roots(gameInstall));
        if (slots.Count == 0) return (null, Tr(" No save to continue."));

        var slot = slots[0];
        if (SaveModList.Read(slot.Folder) is not { } saved) return (null, Tr(" The last save '{1}' has no mod list.", slot.Name));

        var differences = ModDifferences(saved, enabled);
        return differences is null
            ? (slot.Folder, Tr(" Continuing '{1}'.", slot.Name))
            : (null, Tr(" Not continuing '{1}', made with other mods ({2}).", slot.Name, differences));
    }

    // Null when the save's mods are the enabled ones (in any order), else which are missing and which are extra.
    static string? ModDifferences(List<string> saved, List<string> enabled)
    {
        var missing = saved.Except(enabled, StringComparer.OrdinalIgnoreCase).ToList();
        var extra = enabled.Except(saved, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0 && extra.Count == 0) return null;

        var parts = new List<string>();
        if (missing.Count > 0) parts.Add(Tr("not enabled: {1}", string.Join(", ", missing)));
        if (extra.Count > 0) parts.Add(Tr("not in the save: {1}", string.Join(", ", extra)));
        return string.Join("; ", parts);
    }
}
