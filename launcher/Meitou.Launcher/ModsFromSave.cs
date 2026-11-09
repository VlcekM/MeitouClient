using Avalonia.Controls;
using Avalonia.Interactivity;
using Meitou.Content;

namespace Meitou.Launcher;

/// <summary>
/// The From save button (docs/launcher.md "Mod list"): lists the saves the game lists and applies the chosen one's mods, in its load order, as a
/// preset is applied (not saved yet).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// From save: shows the saves the game lists, and applies the chosen one's mods in its load order (names those not on disk).
    /// </summary>
    async void ModsFromSave_Click(object? sender, RoutedEventArgs e)
    {
        if (_install is null)
        {
            Status("Set the Kenshi folder first (Settings).");
            return;
        }
        if (!ModsReady()) return;

        var roots = SaveModList.Roots(_install);
        var slots = await Task.Run(() => SaveModList.Scan(roots));
        if (slots.Count == 0)
        {
            Status("No saves found.");
            return;
        }
        if (await PickSave(slots) is not { } slot) return;

        List<string>? saved;
        try
        {
            saved = await Task.Run(() => SaveModList.Read(slot.Folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status(Tr("Could not read the save '{1}': {2}", slot.Name, ex.Message));
            return;
        }
        if (saved is null)
        {
            Status(Tr("The save '{1}' has no mod list.", slot.Name));
            return;
        }

        var missing = ApplyOrder(saved.Select(name => (name, true)));
        Status(Tr("Mods of the save '{1}' applied.", slot.Name) + NotOnDisk(missing));
    }

    /// <summary>
    /// Asks which save to take the mods from, newest first; null for Cancel or Escape.
    /// </summary>
    async Task<SaveModSlot?> PickSave(IReadOnlyList<SaveModSlot> slots)
    {
        var list = new ListBox
        {
            ItemsSource = slots.Select(s => $"{s.Name}  ·  {s.ModifiedUtc.ToLocalTime():g}  ·  {Tr(s.Source)}").ToList(),
            SelectedIndex = 0,
            MaxHeight = 320,
            Margin = new(0, 8, 0, 0),
        };
        var body = new List<Control> { Wrapped(Tr("Take the mods and their load order from this save:")), list };
        bool apply = await ChooseAsync(Tr("Mods from a save"), body, [(Tr("Cancel"), false), (Tr("Apply"), true)], cancel: false);
        return apply && list.SelectedIndex >= 0 ? slots[list.SelectedIndex] : null;
    }
}
