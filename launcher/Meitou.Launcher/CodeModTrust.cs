using Avalonia.Interactivity;

namespace Meitou.Launcher;

// Code mods (docs/modding.md "Trust"): an enabled mod with a meitou.json runs C# code of its own in the game. One launcher setting, Trust code
// mods (on by default), decides for all of them: on, every code mod in the list goes to the game's meitou.user.json (trustedCodeMods), the
// game loading only their code; off, the list is emptied and they load as data only. Written when the box changes and before each Play.
public sealed partial class MainWindow
{
    const string TrustedCodeModsKey = "trustedCodeMods";

    void TrustCodeMods_Click(object? sender, RoutedEventArgs e)
    {
        config.TrustCodeMods = TrustCodeModsBox.IsChecked == true;
        config.Save();
        WriteCodeModTrust();
        ShowDependencyProblems();
    }

    /// <summary>The game's trustedCodeMods for the setting: every code mod in the list, or none.</summary>
    void WriteCodeModTrust()
    {
        game = GameSettings.Load();
        var trusted = game.GetStrings(TrustedCodeModsKey).ToHashSet(StringComparer.Ordinal);
        if (config.TrustCodeMods) trusted.UnionWith(mods.Where(m => m.Mod.HasCode).Select(m => m.Name));
        else trusted.Clear();

        game.Set(TrustedCodeModsKey, trusted.Order(StringComparer.Ordinal));
        try
        {
            game.Save(Math.Max(schemaVersion, 1));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status(Tr("Could not write {1}: {2}", GameSettings.FileName, e.Message));
        }
    }
}
