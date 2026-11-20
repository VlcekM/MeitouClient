using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Game.Settings;

/// <summary>
/// The skills' own Squad XP multipliers (Advanced settings, under "Squad XP"; <see cref="Host.UserConfig.SquadSkillXp"/>): which skills have one,
/// how the config keeps them and the list the simulation reads.
/// </summary>
static class SkillXp
{
    /// <summary>
    /// The skills with a slider, in the skills window's order: the label, the stat, the setting the Player-Only XP port saved it under and the
    /// plugin's key in its settings file (<c>stat_&lt;key&gt;</c>).
    /// </summary>
    internal static readonly (string Label, StatsEnumerated Stat, string PortSetting, string PluginKey)[] Skills =
    [
        ("Strength", StatsEnumerated.Strength, "Strength", "STRENGTH"),
        ("Dexterity", StatsEnumerated.Dexterity, "Dexterity", "DEXTERITY"),
        ("Toughness", StatsEnumerated.Toughness, "Toughness", "TOUGHNESS"),
        ("Perception", StatsEnumerated.Perception, "Perception", "PERCEPTION"),
        ("Melee attack", StatsEnumerated.MeleeAttack, "MeleeAttack", "MELEE_ATTACK"),
        ("Melee defence", StatsEnumerated.MeleeDefence, "MeleeDefence", "MELEE_DEFENCE"),
        ("Dodge", StatsEnumerated.Dodge, "Dodge", "DODGE"),
        ("Martial arts", StatsEnumerated.MartialArts, "MartialArts", "MARTIALARTS"),
        ("Katanas", StatsEnumerated.Katanas, "Katanas", "KATANAS"),
        ("Sabres", StatsEnumerated.Sabres, "Sabres", "SABRES"),
        ("Hackers", StatsEnumerated.Hackers, "Hackers", "HACKERS"),
        ("Heavy weapons", StatsEnumerated.HeavyWeapons, "HeavyWeapons", "HEAVYWEAPONS"),
        ("Blunt", StatsEnumerated.Blunt, "Blunt", "BLUNT"),
        ("Polearms", StatsEnumerated.Polearms, "Polearms", "POLEARMS"),
        ("Crossbows", StatsEnumerated.Crossbows, "Crossbows", "CROSSBOWS"),
        ("Turrets", StatsEnumerated.Turrets, "Turrets", "TURRETS"),
        ("Precision shooting", StatsEnumerated.FriendlyFire, "PrecisionShooting", "FRIENDLY_FIRE"),
        ("Stealth", StatsEnumerated.Stealth, "Stealth", "STEALTH"),
        ("Lockpicking", StatsEnumerated.Lockpicking, "Lockpicking", "LOCKPICKING"),
        ("Thievery", StatsEnumerated.Thieving, "Thievery", "THIEVING"),
        ("Assassination", StatsEnumerated.Assassination, "Assassination", "ASSASSINATION"),
        ("Athletics", StatsEnumerated.Athletics, "Athletics", "ATHLETICS"),
        ("Swimming", StatsEnumerated.Swimming, "Swimming", "SWIMMING"),
        ("Field medic", StatsEnumerated.Medic, "FieldMedic", "MEDIC"),
        ("Robotics", StatsEnumerated.Robotics, "Robotics", "ROBOTICS"),
        ("Engineering", StatsEnumerated.Engineering, "Engineering", "ENGINEERING"),
        ("Science", StatsEnumerated.Science, "Science", "SCIENCE"),
        ("Weapon smith", StatsEnumerated.SmithingWeapon, "WeaponSmith", "SMITHING_WEAPON"),
        ("Armour smith", StatsEnumerated.SmithingArmour, "ArmourSmith", "SMITHING_ARMOUR"),
        ("Bow smith", StatsEnumerated.SmithingBow, "BowSmith", "SMITHING_BOW"),
        ("Labouring", StatsEnumerated.Labouring, "Labouring", "LABOURING"),
        ("Farming", StatsEnumerated.Farming, "Farming", "FARMING"),
        ("Cooking", StatsEnumerated.Cooking, "Cooking", "COOKING"),
    ];

    /// <summary>
    /// The lowest slider value, one arrow-key step below 0: below 0 a skill follows Squad XP.
    /// </summary>
    internal const float Follows = -0.25f;

    /// <summary>
    /// The config's key of a skill: its STATS field name (<c>strength</c>, <c>attack</c>, <c>ff</c>...).
    /// </summary>
    internal static string Key(StatsEnumerated stat) => StatsInfo.FieldName(stat)!;

    /// <summary>
    /// A skill's row id in the settings menu, a member of the Squad XP group (<see cref="FeatureGrouping.FeatureGroups"/>).
    /// </summary>
    internal static string Raw(StatsEnumerated stat) => "xp:" + Key(stat);

    /// <summary>
    /// The rows of the skills' sliders, the Squad XP group's members.
    /// </summary>
    internal static string[] Rows => [.. Skills.Select(s => Raw(s.Stat))];

    /// <summary>
    /// <paramref name="saved"/> without what no skill reads: unknown keys, numbers that are not finite and skills that follow Squad XP (below 0);
    /// the rest held to the XP sliders' range.
    /// </summary>
    internal static Dictionary<string, float> Kept(Dictionary<string, float>? saved)
    {
        var kept = new Dictionary<string, float>();
        if (saved is null) return kept;

        foreach (var (_, stat, _, _) in Skills)
        {
            string key = Key(stat);
            if (saved.TryGetValue(key, out float value) && float.IsFinite(value) && value >= 0)
                kept[key] = value == 0 ? 0 : Math.Min(value, Host.UserConfig.MaxXpRate);   // -0 kept as 0
        }

        return kept;
    }

    /// <summary>
    /// The Squad XP label during play ("XP x10", <see cref="PlayerInterface.XpLabel"/>); null while Show Squad XP is off.
    /// </summary>
    internal static string? Label(Host.UserConfig config) =>
        config.ShowSquadXp ? "XP x" + config.SquadXp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// The skills' multipliers as the simulation reads them (<see cref="Simulation.Bodies.BodyOptions.SquadSkillXp"/>): by stat number, each
    /// skill's own or <see cref="Follows"/>.
    /// </summary>
    internal static float[] Rates(IReadOnlyDictionary<string, float> saved)
    {
        var rates = new float[StatsInfo.Count];
        Array.Fill(rates, Follows);
        foreach (var (_, stat, _, _) in Skills)
            if (saved.TryGetValue(Key(stat), out float value) && value >= 0) rates[(int)stat] = value;

        return rates;
    }
}
