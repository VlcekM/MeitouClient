using Meitou.Settings;

namespace Meitou.Launcher;

/// <summary>
/// A look the Graphics page's look row offers: the schema's (Meitou first, then an older game's examples), one of the player's (its file) or
/// one an enabled mod ships (its file, the mod's name as the source).
/// </summary>
sealed record LookChoice(SettingsLook Look, string? File, string Source);

/// <summary>
/// The looks the look row offers and the mods' settings a look holds (docs/launcher.md "Graphics", docs/modding.md "Look presets"), apart
/// from the window so they run without one.
/// </summary>
static class LookChoices
{
    /// <summary>
    /// The schema's looks (Meitou built in, then an older game's examples), the player's files in the look folder under
    /// <paramref name="dataDirectory"/> (beside the game's settings file), then those the enabled <paramref name="mods"/> ship in the same
    /// folder of their own, in load order and marked with the mod's name, as the game lists them; a name a look already has is left out.
    /// </summary>
    public static List<LookChoice> All(SettingsLooks l, string dataDirectory, IEnumerable<(string Name, string Folder)> mods)
    {
        var all = l.Presets.Select((look, i) => new LookChoice(look, null, i == 0 ? "built in" : "example")).ToList();
        if (l.Folder.Length == 0) return all;

        AddFolder(all, Path.Combine(dataDirectory, l.Folder), "mine");
        foreach (var (name, folder) in mods) AddFolder(all, Path.Combine(folder, l.Folder), name);
        return all;
    }

    /// <summary>
    /// Adds a folder's preset files under <paramref name="source"/>, but those whose name a look already has.
    /// </summary>
    static void AddFolder(List<LookChoice> all, string folder, string source)
    {
        foreach (var (file, read) in LookPresetFile.ReadFolder(folder))
        {
            if (all.Any(x => x.Look.Name.Equals(read.Name, StringComparison.OrdinalIgnoreCase))) continue;

            all.Add(new(new SettingsLook { Name = read.Name, Values = read.Values }, file, source));
        }
    }

    /// <summary>
    /// The code mods' settings a look's file holds (<c>mods</c>: mod id, setting key, text); none for a look without a file.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> ModsOf(LookChoice? look) =>
        look?.File is { } file && File.Exists(file) && LookPresetFile.Read(File.ReadAllText(file)) is { } read ? read.Mods : [];

    /// <summary>
    /// Writes the mods' settings <paramref name="look"/> holds into the game's saved ones and removes those <paramref name="before"/> held and
    /// it does not (back to the mod's default). The launcher knows no other setting of a mod: one changed in the game stays, and the game counts
    /// it as a change.
    /// </summary>
    public static void ApplyMods(GameSettings game, LookChoice before, LookChoice look)
    {
        var held = ModsOf(look);
        foreach (var (mod, values) in ModsOf(before))
        {
            foreach (string key in values.Keys.Where(k => !(held.TryGetValue(mod, out var kept) && kept.ContainsKey(k))))
                game.SetModSetting(mod, key, null);
        }
        foreach (var (mod, values) in held)
        {
            foreach (var (key, text) in values) game.SetModSetting(mod, key, text);
        }
    }
}
