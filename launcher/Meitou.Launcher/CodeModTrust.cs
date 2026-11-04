using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Meitou.Launcher;

// Code mods (docs/modding.md "Trust"): an enabled mod with a meitou.json runs C# code of its own in the game. Play asks about the enabled ones not
// decided yet; the Code mods button asks about every code mod in the list, so a choice can be changed. Allowed names go to the game's
// meitou.user.json (trustedCodeMods), the game loading only their code; data-only names go to the launcher's config, so Play does not ask again.
public sealed partial class MainWindow
{
    const string TrustedCodeModsKey = "trustedCodeMods";

    enum CodeModChoice { Later, Ticked, TrustAll, DataOnlyAll }

    /// <summary>Before Play: the enabled code mods neither allowed nor chosen as data only.</summary>
    async Task AskForCodeMods()
    {
        game = GameSettings.Load();
        var trusted = game.GetStrings(TrustedCodeModsKey);
        var undecided = mods.Where(m => m.Enabled && m.Mod.HasCode).Select(m => m.Name)
            .Where(n => !trusted.Contains(n, StringComparer.Ordinal) && !config.DataOnlyCodeMods.Contains(n, StringComparer.Ordinal)).ToList();
        if (undecided.Count > 0) await DecideCodeMods(undecided);
    }

    async void CodeMods_Click(object? sender, RoutedEventArgs e)
    {
        var names = mods.Where(m => m.Mod.HasCode).Select(m => m.Name).ToList();
        if (names.Count == 0) { Status("No code mods in the list."); return; }
        game = GameSettings.Load();
        await DecideCodeMods(names);
    }

    /// <summary>
    /// One question for the mods, a box per mod ticked when it is allowed now: Trust all, Data only all, or Apply the ticks. Escape decides nothing.
    /// </summary>
    async Task DecideCodeMods(List<string> names)
    {
        var trusted = game.GetStrings(TrustedCodeModsKey).ToHashSet(StringComparer.Ordinal);
        var boxes = names.Select(n => new CheckBox { Content = n, IsChecked = trusted.Contains(n), Margin = new(0, 4, 0, 0) }).ToList();
        string text = Tr("These mods run C# code of their own in the game, with your user's rights, like any program you install. "
            + "Meitou cannot sandbox them. Ticked mods may run their code; the others load as data only.");
        CodeModChoice[] values = [CodeModChoice.DataOnlyAll, CodeModChoice.TrustAll, CodeModChoice.Ticked];
        string[] labels = [Tr("Data only all"), Tr("Trust all"), Tr("Apply")];
        var choice = await ChooseAsync<CodeModChoice>(Tr("Code mods"), [Wrapped(text), .. boxes], [.. labels.Zip(values)], CodeModChoice.Later);
        if (choice == CodeModChoice.Later) return;
        bool Allowed(int i) => choice == CodeModChoice.TrustAll || choice == CodeModChoice.Ticked && boxes[i].IsChecked == true;
        var allow = names.Where((_, i) => Allowed(i)).ToHashSet();
        SaveCodeModChoices(names, allow, trusted);
    }

    void SaveCodeModChoices(List<string> names, HashSet<string> allow, HashSet<string> trusted)
    {
        trusted.ExceptWith(names);
        trusted.UnionWith(allow);
        game.Set(TrustedCodeModsKey, trusted.Order(StringComparer.Ordinal));
        config.DataOnlyCodeMods = [.. config.DataOnlyCodeMods.Except(names).Concat(names.Except(allow)).Order(StringComparer.Ordinal)];
        config.Save();
        try
        {
            game.Save(Math.Max(schemaVersion, 1));
            Status(Tr("Code mods: {1} allowed, {2} data only.", allow.Count, names.Count - allow.Count));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status(Tr("Could not write {1}: {2}", GameSettings.FileName, e.Message));
        }
    }
}
