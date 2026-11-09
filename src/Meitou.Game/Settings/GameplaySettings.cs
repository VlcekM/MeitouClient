using System.Globalization;
using Meitou.Content;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;

namespace Meitou.Game.Settings;

/// <summary>
/// The original's Gameplay options page (docs/formats/settings.md "Gameplay"): the population and difficulty sliders, the two drop-downs and the
/// autosave, plus the harpoon limit, with the game's slider ranges and missing-key defaults. All apply to the running world when changed.
/// </summary>
sealed record GameplaySettings(
    float SquadSize = 1,
    float Population = 1,
    float RaidSize = 1,
    float RaidFrequency = 1,
    int TownAttacks = 2,
    Dismemberment Dismemberment = Dismemberment.Rare,
    bool AutoSave = true,
    float AutoSaveMinutes = 10,
    int HarpoonLimit = RangedTuning.DefaultStuckLimit,
    bool FastZoneHopping = false)
{
    public const float MinSquadSize = 0.5f, MaxSquadSize = 3, MinPopulation = 0.5f, MaxPopulation = 4,
        MinRaidSize = 0.25f, MaxRaidSize = 4, MinRaidFrequency = 0.25f, MaxRaidFrequency = 3, MinAutoSaveMinutes = 1, MaxAutoSaveMinutes = 30;

    public const float DefaultNpcRange = 3250;

    /// <summary>
    /// The drop-downs' items, by the stored number.
    /// </summary>
    public static readonly string[] TownAttackLabels = ["Bombardment", "High", "Normal", "Fewer", "Rare", "Never"];
    public static readonly string[] DismembermentLabels = ["Never", "Rare", "Frequent"];

    /// <summary>
    /// The install's <c>settings.cfg</c>; every missing key takes the original's default.
    /// </summary>
    public static GameplaySettings FromInstall(GameInstall install)
    {
        var values = ReadValues(install);
        float Get(string key, float fallback, float min, float max) => values.TryGetValue(key, out float v) ? Math.Clamp(v, min, max) : fallback;

        return new(
            SquadSize: Get("Squad size multiplier", 1, MinSquadSize, MaxSquadSize),
            Population: Get("Global population multiplier", 1, MinPopulation, MaxPopulation),
            RaidSize: Get("raidSizeMult", 1, MinRaidSize, MaxRaidSize),
            RaidFrequency: Get("raidFrequencyMult", 1, MinRaidFrequency, MaxRaidFrequency),
            TownAttacks: (int)Get("attacks", 2, 0, 5),
            Dismemberment: (Dismemberment)(int)Get("Dismemberment", 1, 0, 2),
            AutoSave: Get("Auto save", 1, 0, 1) != 0,
            AutoSaveMinutes: Get("Auto save time (minutes)", 10, MinAutoSaveMinutes, MaxAutoSaveMinutes),
            HarpoonLimit: (int)Get("harpoonLimit", RangedTuning.DefaultStuckLimit, RangedTuning.MinStuckLimit, RangedTuning.MaxStuckLimit),
            FastZoneHopping: Get("Fast zone hopping", 0, 0, 1) != 0);
    }

    /// <summary>
    /// The NPC View Range (<c>npc range</c> of the install's settings.cfg; <c>FUN_1405c9f60</c>, <b>Verified</b>): 3250 when missing, 3000 when
    /// stored below 1000. Characters farther from the eye than this are not drawn (<c>CharacterRenderer.DrawDistance</c>); the Characters page's
    /// slider sets it from then on.
    /// </summary>
    public static float NpcRange(GameInstall install) =>
        ReadValues(install).TryGetValue("npc range", out float v) ? (v < 1000 ? 3000 : v) : DefaultNpcRange;

    static Dictionary<string, float> ReadValues(GameInstall install)
    {
        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = Path.Combine(install.Root, "settings.cfg");
            if (File.Exists(file))
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (line.IndexOf('=') is > 0 and var eq
                        && float.TryParse(line[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                        && float.IsFinite(v))
                    {
                        values[line[..eq].Trim()] = v;
                    }
                }
            }
        }
        catch (IOException)
        {
        }

        return values;
    }
}
