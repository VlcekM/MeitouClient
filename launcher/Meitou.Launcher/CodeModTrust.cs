namespace Meitou.Launcher;

// Code mods (docs/modding.md "Trust"): an enabled mod with a meitou.json runs C# code of its own in the game. Play asks once for each one not yet allowed and
// writes the allowed names to the game's meitou.user.json (trustedCodeMods); the game loads only those mods' code, the others as data only.
public sealed partial class MainWindow
{
    const string TrustedCodeModsKey = "trustedCodeMods";

    async Task AskForCodeMods()
    {
        game = GameSettings.Load();
        var allowed = game.GetStrings(TrustedCodeModsKey);
        var asking = mods.Where(m => m.Enabled && m.Mod.HasCode && !allowed.Contains(m.Name, StringComparer.Ordinal)).Select(m => m.Name).ToList();
        if (asking.Count == 0) return;
        string text = Tr("{1} run C# code of their own in the game, with your user's rights, like any program you install. Meitou cannot sandbox them. "
            + "Allow them? Not allowed, they load as data only.", string.Join(", ", asking));
        if (!await AskAsync(Tr("Code mods"), text, Tr("Allow"), Tr("Data only"))) return;
        game.Set(TrustedCodeModsKey, allowed.Concat(asking));
        try { game.Save(Math.Max(schemaVersion, 1)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status(Tr("Could not write {1}: {2}", GameSettings.FileName, e.Message)); }
    }
}
