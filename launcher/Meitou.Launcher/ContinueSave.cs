using Avalonia.Controls;
using Avalonia.Interactivity;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// The Continue part of the Play button (docs/launcher.md "Continue"): starts the game in the newest save when it was made with the enabled mods.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Options that pick the game's start themselves; the game refuses --load beside the others.
    /// </summary>
    static readonly string[] StartOptions = ["--load", "--continue", "--new-game", "--no-population", "--duel", "--sandbox", "--battle",
            "--terrain-sandbox"];

    /// <summary>
    /// Continue: starts the game in the newest save (<see cref="StartGame"/>).
    /// </summary>
    async void Continue_Click(object? sender, RoutedEventArgs e) => await StartGame(continueSave: true);

    /// <summary>
    /// Shows Continue with the newest save's name while there is a save (rescanned when the window is activated).
    /// </summary>
    async Task RefreshContinueAsync()
    {
        var slot = _install is { } gameInstall ? await Task.Run(() => NewestSave(gameInstall)) : null;
        ContinueButton.IsVisible = slot is not null;

        // Its fixed least width (the footer not moving between Play texts) would crowd the footer beside Continue.
        PlayButton.MinWidth = slot is null ? 312 : 0;
        if (_download is null) ShowPlayText();
        if (slot is null) return;

        ContinueSaveText.Text = slot.Name;
        ToolTip.SetTip(ContinueButton, Tr("Load the newest save, {1} ({2}); its mods must be the enabled ones", slot.Name,
                slot.ModifiedUtc.ToLocalTime().ToString("g")));
    }

    /// <summary>
    /// The newest save of the install's save folders, null when there is none or they cannot be read.
    /// </summary>
    static SaveModSlot? NewestSave(GameInstall gameInstall)
    {
        try
        {
            return SaveModList.Scan(SaveModList.Roots(gameInstall)).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest save when its mods are the enabled ones; else null, with the reason in the status.
    /// </summary>
    async Task<SaveModSlot?> ContinueTarget(GameInstall gameInstall)
    {
        if (!ModsReady()) return null;
        if (SplitArguments(_config.LaunchArguments).FirstOrDefault(StartOptions.Contains) is { } option)
        {
            Status(Tr("Launch arguments have {1}, which picks the start itself: remove it to continue a save.", option));
            return null;
        }

        var enabled = _mods.Where(m => m.Enabled).Select(m => m.Name).ToList();
        try
        {
            var (slot, problem) = await Task.Run(() => CheckNewestSave(gameInstall, enabled));
            if (problem is not null) Status(problem);
            return slot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status(Tr("Could not read the last save: {1}", ex.Message));
            return null;
        }
    }

    /// <summary>
    /// The newest save, or null and why it can't be continued.
    /// </summary>
    static (SaveModSlot? Slot, string? Problem) CheckNewestSave(GameInstall gameInstall, List<string> enabled)
    {
        if (SaveModList.Scan(SaveModList.Roots(gameInstall)).FirstOrDefault() is not { } slot)
            return (null, Tr("No save to continue."));
        if (SaveModList.Read(slot.Folder) is not { } saved)
            return (null, Tr("The save '{1}' has no mod list.", slot.Name));

        return ModDifferences(saved, enabled) is { } differences
            ? (null, Tr("The save '{1}' was made with other mods ({2}); Mods > From save applies its list.", slot.Name, differences))
            : (slot, null);
    }

    /// <summary>
    /// Null when the save's mods are the enabled ones (in any order), else which are missing and which are extra.
    /// </summary>
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
