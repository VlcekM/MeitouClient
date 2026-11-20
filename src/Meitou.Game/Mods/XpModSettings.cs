using System.Globalization;
using Meitou.Content;
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
    /// The port's id, under which its settings are saved (<see cref="UserConfig.ModSettings"/>).
    /// </summary>
    const string PortId = "player-only-xp";

    /// <summary>
    /// Takes in the RE_Kenshi plugin's settings from its file in <paramref name="folder"/>; its defaults when there is none (the original game
    /// writes the file on its first run).
    /// </summary>
    internal static void AdoptPlugin(UserConfig config, string folder) => AdoptFile(config, PluginFile(folder) ?? []);

    /// <summary>
    /// Takes in the port's settings: the plugin's file when the plugin is installed and has one (the port shares it), else the port's saved
    /// settings, else its defaults.
    /// </summary>
    internal static void AdoptPort(UserConfig config, IReadOnlyDictionary<string, ModInfo> onDisk)
    {
        if (onDisk.GetValueOrDefault(CoveredMods.RekenshiXp.ModName!) is { } plugin && PluginFile(plugin.Folder) is { } shared)
        {
            AdoptFile(config, shared);
            return;
        }

        var saved = config.ModSettings.GetValueOrDefault(PortId) ?? [];
        config.SquadXp = Clamped(Number(saved.GetValueOrDefault("Multiplier")) ?? DefaultXpMod);
        foreach (var (_, stat, portSetting, _) in SkillXp.Skills)
            SetSkill(config, stat, Number(saved.GetValueOrDefault(portSetting)), lowest: 0);

        config.ShowSquadXp = !bool.TryParse(saved.GetValueOrDefault("ShowIndicator"), out bool show) || show;
    }

    /// <summary>
    /// Takes in the plugin's file: <c>xp_multiplier</c>, each skill's <c>stat_&lt;SKILL&gt;</c> (a value below 1, outside the plugin's own range,
    /// follows Squad XP) and <c>show_hud</c> (on unless 0); a missing key keeps the mod's default.
    /// </summary>
    static void AdoptFile(UserConfig config, Dictionary<string, float> file)
    {
        config.SquadXp = Clamped(file.TryGetValue("xp_multiplier", out float main) ? main : DefaultXpMod);
        foreach (var (_, stat, _, pluginKey) in SkillXp.Skills)
            SetSkill(config, stat, file.TryGetValue("stat_" + pluginKey, out float own) ? own : null, lowest: 1);

        config.ShowSquadXp = !file.TryGetValue("show_hud", out float show) || show != 0;
    }

    /// <summary>
    /// Gives <paramref name="stat"/> its own multiplier <paramref name="value"/> when it is a number at <paramref name="lowest"/> or above, else
    /// lets it follow Squad XP.
    /// </summary>
    static void SetSkill(UserConfig config, Data.Gameplay.Bodies.StatsEnumerated stat, float? value, float lowest)
    {
        string key = SkillXp.Key(stat);
        if (value is { } own && float.IsFinite(own) && own >= lowest) config.SquadSkillXp[key] = Math.Min(own, UserConfig.MaxXpRate);
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
