using System.Globalization;
using Meitou.Content;
using Meitou.Game.Host;

namespace Meitou.Modding;

/// <summary>
/// Mods whose feature Meitou has built in (docs/formats/rekenshi.md). When one is enabled, Meitou takes its settings into its own once
/// (<see cref="UserConfig.AdoptedMods"/>) and says so; a code mod among them is not run, so the feature never applies twice, and a save made while it
/// ran does not report it gone.
/// </summary>
static class BuiltInMods
{
    /// <summary>
    /// A mod Meitou covers: its <c>.mod</c> name or its code mod's id, what the notice says became of its settings, and how they are taken in (from
    /// the mod and the mods on disk by name).
    /// </summary>
    sealed record Covered(string? ModName, string? CodeId, string Taken, Action<UserConfig, ModInfo, IReadOnlyDictionary<string, ModInfo>> Adopt);

    /// <summary>
    /// The <c>.mod</c> name of xXDarkrusoXx's RE_Kenshi plugin "Player-Only XP x10" (Workshop 3679678086).
    /// </summary>
    const string RekenshiXpMod = "Player XP Only x10 RE";

    /// <summary>
    /// The XP multiplier both XP mods start at.
    /// </summary>
    const float DefaultXpMod = 10;

    /// <summary>
    /// What the XP mods' notice says became of their settings.
    /// </summary>
    const string XpTaken = "its multiplier is now Squad XP in the Advanced settings (per-skill multipliers are not kept)";

    /// <summary>
    /// The mods Meitou covers.
    /// </summary>
    static readonly Covered[] All =
    [
        new(RekenshiXpMod, null, XpTaken, (config, mod, _) => config.SquadXp = Clamped(RekenshiXp(mod.Folder) ?? DefaultXpMod)),
        // Its code mod port "[MeitouClient] Player-Only XP x10" (Workshop 3838916871), which uses the plugin's file over its own saved setting when the
        // plugin is installed.
        new(null, "player-only-xp", XpTaken, (config, _, onDisk) => config.SquadXp = Clamped(PortXp(config, onDisk))),
    ];

    /// <summary>
    /// Takes in the settings of each enabled covered mod not taken in before and returns the notices saying so; the config is saved when one was.
    /// A mod folder that cannot be read takes nothing in.
    /// </summary>
    public static List<string> Adopt(UserConfig config, GameInstall install)
    {
        List<string> notices = [];
        var onDisk = ModsOnDisk(install);
        foreach (var mod in ModList.ReadEnabled(install).Select(onDisk.GetValueOrDefault).OfType<ModInfo>())
        {
            if (CoveredOf(mod) is not { } covered || config.AdoptedMods.Contains(mod.Name)) continue;

            covered.Adopt(config, mod, onDisk);
            config.AdoptedMods.Add(mod.Name);
            Console.WriteLine($"mods      {mod.Name} is built into Meitou: Squad XP {config.SquadXp}");
            notices.Add($"{mod.Name} is built into Meitou now: {covered.Taken}.");
        }

        if (notices.Count > 0) config.Save();

        return notices;
    }

    /// <summary>
    /// Moves the covered code mods of <paramref name="enabled"/> to <paramref name="disabled"/>, each with a log line, so they are not run and the
    /// dependency checks count them as switched off.
    /// </summary>
    public static void SkipCovered(List<ModInfo> enabled, List<ModInfo> disabled)
    {
        foreach (var mod in enabled.Where(m => CoveredOf(m) is { CodeId: not null }).ToList())
        {
            Console.WriteLine($"mods      {mod.Name} not run: built into Meitou");
            enabled.Remove(mod);
            disabled.Add(mod);
        }
    }

    /// <summary>
    /// <paramref name="saved"/> without the covered code mods, so a save made while one ran does not report it gone.
    /// </summary>
    public static CodeModEnvironment WithoutCovered(CodeModEnvironment saved) =>
        saved with { Mods = [.. saved.Mods.Where(m => !All.Any(c => c.CodeId == m.Id))] };

    /// <summary>
    /// The mods on disk by name; none when the folders cannot be read.
    /// </summary>
    static Dictionary<string, ModInfo> ModsOnDisk(GameInstall install)
    {
        try
        {
            return ModList.Discover(install).ToDictionary(m => m.Name, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"mods      the mod folders could not be read, no built-in mods taken in: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// The entry covering <paramref name="mod"/>, by its <c>.mod</c> name or its code mod's id; null when Meitou does not cover it.
    /// </summary>
    static Covered? CoveredOf(ModInfo mod)
    {
        string? id = mod.HasCode ? CodeModManifest.TryReadId(mod.CodeManifestPath) : null;
        return All.FirstOrDefault(c => c.ModName == mod.Name || id is not null && c.CodeId == id);
    }

    /// <summary>
    /// The plugin's <c>xp_multiplier</c> in <c>plugin/player_xp_boost.ini</c> of <paramref name="folder"/> (a trailing <c>;</c> or <c>#</c> comment
    /// ignored); null when the file or the line is missing (the original game writes the file on its first run).
    /// </summary>
    static float? RekenshiXp(string folder)
    {
        string path = Path.Combine(folder, "plugin", "player_xp_boost.ini");
        try
        {
            if (!File.Exists(path)) return null;
            foreach (string line in File.ReadLines(path))
                if (IniValue(line, "xp_multiplier") is { } value) return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"mods      {path} could not be read: {e.Message}");
        }
        return null;
    }

    /// <summary>
    /// The number of a <c>key=value</c> line for <paramref name="key"/>; null for another line or a value that is not a number.
    /// </summary>
    static float? IniValue(string line, string key)
    {
        int equals = line.IndexOf('=');
        if (equals <= 0 || !line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return null;

        string value = line[(equals + 1)..];
        int comment = value.IndexOfAny([';', '#']);
        if (comment >= 0) value = value[..comment];
        return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float number) ? number : null;
    }

    /// <summary>
    /// The multiplier the port ran with: the plugin's file when the plugin is installed and the file names one, else the port's saved
    /// <c>Multiplier</c> setting, else its default.
    /// </summary>
    static float PortXp(UserConfig config, IReadOnlyDictionary<string, ModInfo> onDisk)
    {
        if (onDisk.GetValueOrDefault(RekenshiXpMod) is { } plugin && RekenshiXp(plugin.Folder) is { } shared) return shared;

        string? saved = config.ModSettings.GetValueOrDefault("player-only-xp")?.GetValueOrDefault("Multiplier");
        return float.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : DefaultXpMod;
    }

    /// <summary>
    /// A mod's multiplier within the slider's range; the default for one that is not a finite number.
    /// </summary>
    static float Clamped(float value) => float.IsFinite(value) ? Math.Clamp(value, UserConfig.MinXpRate, UserConfig.MaxXpRate) : DefaultXpMod;
}
