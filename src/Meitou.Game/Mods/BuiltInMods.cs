using System.Globalization;
using Meitou.Content;
using Meitou.Game.Host;

namespace Meitou.Modding;

/// <summary>
/// Mods whose feature Meitou has built in (<see cref="CoveredMods.BuiltIn"/>, docs/formats/rekenshi.md). When one is enabled, Meitou takes its
/// settings into its own once (<see cref="UserConfig.AdoptedMods"/>) and says so; a code mod among them is not run, so the feature never applies
/// twice, and a save made while it ran does not report it gone.
/// </summary>
static class BuiltInMods
{
    /// <summary>
    /// A mod Meitou covers (its entry in <see cref="CoveredMods.BuiltIn"/>), what the notice says became of its settings, and how they are taken in
    /// (from the mod and the mods on disk by name).
    /// </summary>
    sealed record Covered(BuiltInMod Mod, string Taken, Action<UserConfig, ModInfo, IReadOnlyDictionary<string, ModInfo>> Adopt);

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
        new(CoveredMods.RekenshiXp, XpTaken, (config, mod, _) => config.SquadXp = Clamped(RekenshiXp(mod.Folder) ?? DefaultXpMod)),
        // The port uses the plugin's file over its own saved setting when the plugin is installed.
        new(CoveredMods.XpPort, XpTaken, (config, _, onDisk) => config.SquadXp = Clamped(PortXp(config, onDisk))),
        new(CoveredMods.SwampTurtleFix, "Swamp turtle attacks on the Fixes page is on", (config, _, _) => config.FixAnimalMoves = true),
        new(CoveredMods.CharacterHighlight, "Hover outline in the Advanced settings is on", (config, _, _) => config.HoverOutline = true),
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
            Console.WriteLine($"mods      {mod.Name} is built into Meitou: {covered.Taken}");
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
        foreach (var mod in enabled.Where(IsCovered).ToList())
        {
            Console.WriteLine($"mods      {mod.Name} not run: built into Meitou");
            enabled.Remove(mod);
            disabled.Add(mod);
        }
    }

    /// <summary>
    /// Whether <see cref="SkipCovered"/> leaves <paramref name="mod"/> out (a code mod whose feature Meitou has built in); no log line.
    /// </summary>
    public static bool IsCovered(ModInfo mod) => CoveredOf(mod) is { Mod.CodeId: not null };

    /// <summary>
    /// <paramref name="saved"/> without the covered code mods, so a save made while one ran does not report it gone.
    /// </summary>
    public static CodeModEnvironment WithoutCovered(CodeModEnvironment saved) =>
        saved with { Mods = [.. saved.Mods.Where(m => !All.Any(c => c.Mod.CodeId == m.Id))] };

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
    /// The entry covering <paramref name="mod"/> (<see cref="CoveredMods.BuiltInOf"/>); null when Meitou does not cover it.
    /// </summary>
    static Covered? CoveredOf(ModInfo mod) => CoveredMods.BuiltInOf(mod) is { } built ? All.FirstOrDefault(c => c.Mod == built) : null;

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
        if (onDisk.GetValueOrDefault(CoveredMods.RekenshiXp.ModName!) is { } plugin && RekenshiXp(plugin.Folder) is { } shared)
            return shared;

        string? saved = config.ModSettings.GetValueOrDefault("player-only-xp")?.GetValueOrDefault("Multiplier");
        return float.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : DefaultXpMod;
    }

    /// <summary>
    /// A mod's multiplier within the slider's range; the default for one that is not a finite number.
    /// </summary>
    static float Clamped(float value) => float.IsFinite(value) ? Math.Clamp(value, UserConfig.MinXpRate, UserConfig.MaxXpRate) : DefaultXpMod;
}
