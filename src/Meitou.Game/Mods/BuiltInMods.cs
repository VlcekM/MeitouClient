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
    /// What the XP mods' notice says became of their settings.
    /// </summary>
    const string XpTaken = "its multipliers are now Squad XP in the Advanced settings (open it for each skill's own) and Show Squad XP";

    /// <summary>
    /// The mods Meitou covers.
    /// </summary>
    static readonly Covered[] All =
    [
        new(CoveredMods.RekenshiXp, XpTaken, (config, mod, _) => XpModSettings.AdoptPlugin(config, mod.Folder)),
        // The port uses the plugin's file over its own saved settings when the plugin is installed.
        new(CoveredMods.XpPort, XpTaken, (config, _, onDisk) => XpModSettings.AdoptPort(config, onDisk)),
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
}
