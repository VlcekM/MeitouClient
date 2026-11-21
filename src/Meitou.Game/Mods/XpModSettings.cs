using System.Globalization;
using Meitou.Content;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Game.Host;
using Meitou.Game.Settings;

namespace Meitou.Modding;

/// <summary>
/// The settings of the two Player-Only XP mods as Meitou's Squad XP takes them in (<see cref="BuiltInMods"/>, docs/formats/rekenshi.md): the main
/// multiplier, each skill's own (<see cref="UserConfig.SquadSkillXp"/>) and the "XP x10" label (<see cref="UserConfig.ShowSquadXp"/>).
/// </summary>
static class XpModSettings
{
    /// <summary>
    /// The XP multiplier both XP mods start at.
    /// </summary>
    const float DefaultXpMod = 10;

    /// <summary>
    /// Takes in the RE_Kenshi plugin's settings from its file in <paramref name="folder"/>, its defaults where the file (which the original game
    /// writes on its first run) has none. Without <paramref name="main"/> the main multiplier is left as it is (<see cref="BuiltInMods"/>' second
    /// takeover, for a player whose first one copied only that).
    /// </summary>
    internal static void AdoptPlugin(UserConfig config, string folder, bool main = true)
    {
        Defaults(config, main);
        if (PluginFile(folder) is { } file) AdoptFile(config, file, main);
    }

    /// <summary>
    /// Takes in the port's settings as it ran with them: its saved settings, over them the plugin's file when the plugin is installed (the port
    /// read the shared file at start), its defaults where neither has a value. <paramref name="main"/> as in <see cref="AdoptPlugin"/>.
    /// </summary>
    internal static void AdoptPort(UserConfig config, IReadOnlyDictionary<string, ModInfo> onDisk, bool main = true)
    {
        Defaults(config, main);
        AdoptSaved(config, config.ModSettings.GetValueOrDefault(CoveredMods.XpPort.CodeId!) ?? [], main);
        if (onDisk.GetValueOrDefault(CoveredMods.RekenshiXp.ModName!) is { } plugin && PluginFile(plugin.Folder) is { } shared)
            AdoptFile(config, shared, main);
    }

    /// <summary>
    /// The mods' defaults: the main multiplier 10 (when <paramref name="main"/>), every skill following it, the label on.
    /// </summary>
    static void Defaults(UserConfig config, bool main)
    {
        if (main) config.SquadXp = DefaultXpMod;
        config.SquadSkillXp.Clear();
        config.ShowSquadXp = true;
    }

    /// <summary>
    /// Takes in the port's saved settings where they have a value: <c>Multiplier</c>, each skill's own (0 or more; below follows) and
    /// <c>ShowIndicator</c>.
    /// </summary>
    static void AdoptSaved(UserConfig config, Dictionary<string, string> saved, bool main)
    {
        if (main && Number(saved.GetValueOrDefault("Multiplier")) is { } multiplier) config.SquadXp = Clamped(multiplier);
        foreach (var (_, stat, portSetting, _) in SkillXp.Skills)
            if (Number(saved.GetValueOrDefault(portSetting)) is { } own) SetSkill(config, stat, own);

        if (bool.TryParse(saved.GetValueOrDefault("ShowIndicator"), out bool show)) config.ShowSquadXp = show;
    }

    /// <summary>
    /// Takes in the plugin's file where it has a value: <c>xp_multiplier</c>, each skill's <c>stat_&lt;SKILL&gt;</c> (0 or more, as the port wrote
    /// them; below follows) and <c>show_hud</c> (0 is off).
    /// </summary>
    static void AdoptFile(UserConfig config, Dictionary<string, float> file, bool main)
    {
        if (main && file.TryGetValue("xp_multiplier", out float multiplier)) config.SquadXp = Clamped(multiplier);
        foreach (var (_, stat, _, pluginKey) in SkillXp.Skills)
            if (file.TryGetValue("stat_" + pluginKey, out float own)) SetSkill(config, stat, own);

        if (file.TryGetValue("show_hud", out float show)) config.ShowSquadXp = show != 0;
    }

    /// <summary>
    /// Gives <paramref name="stat"/> its own multiplier <paramref name="value"/> when it is a number of 0 or more, else lets it follow Squad XP.
    /// </summary>
    static void SetSkill(UserConfig config, StatsEnumerated stat, float value)
    {
        string key = SkillXp.Key(stat);
        if (float.IsFinite(value) && value >= 0) config.SquadSkillXp[key] = value == 0 ? 0 : Math.Min(value, UserConfig.MaxXpRate);
        else config.SquadSkillXp.Remove(key);
    }

    /// <summary>
    /// The numbers of the plugin's <c>plugin/player_xp_boost.ini</c> in <paramref name="folder"/> by key, any case (<c>key=value</c> lines, a
    /// trailing <c>;</c> or <c>#</c> comment ignored); null when the file is missing or cannot be read.
    /// </summary>
    static Dictionary<string, float>? PluginFile(string folder)
    {
        string path = Path.Combine(folder, "plugin", "player_xp_boost.ini");
        try
        {
            if (!File.Exists(path)) return null;

            var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(path))
                if (IniLine(line) is var (key, value)) values[key] = value;

            return values;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"mods      {path} could not be read: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The key and number of a <c>key=value</c> line; null for another line or a value that is not a number.
    /// </summary>
    static (string Key, float Value)? IniLine(string line)
    {
        int equals = line.IndexOf('=');
        if (equals <= 0) return null;

        string value = line[(equals + 1)..];
        int comment = value.IndexOfAny([';', '#']);
        if (comment >= 0) value = value[..comment];
        return Number(value) is { } number ? (line[..equals].Trim(), number) : null;
    }

    /// <summary>
    /// <paramref name="text"/> as a number (invariant culture); null for none.
    /// </summary>
    static float? Number(string? text) =>
        float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float number) ? number : null;

    /// <summary>
    /// A mod's multiplier within the slider's range; the default for one that is not a finite number.
    /// </summary>
    static float Clamped(float value) => float.IsFinite(value) ? Math.Clamp(value, UserConfig.MinXpRate, UserConfig.MaxXpRate) : DefaultXpMod;
}
