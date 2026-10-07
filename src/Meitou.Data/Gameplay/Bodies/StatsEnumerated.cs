namespace Meitou.Data.Gameplay.Bodies;

/// <summary>
/// The game's stat numbers (<c>StatsEnumerated</c>, docs/game/character-stats.md "The stat list", <b>Verified</b>): what RACE
/// <c>heal stat</c>, <c>stats good&lt;N&gt;</c> / <c>stats bad&lt;N&gt;</c> and all XP code pass around. Numbers 14 and 15 share the
/// storage of <see cref="Medic"/>; 20 and 33 have no storage at all.
/// </summary>
public enum StatsEnumerated
{
    None = 0,
    Strength = 1,
    MeleeAttack = 2,
    Labouring = 3,
    Science = 4,
    Engineering = 5,
    Robotics = 6,
    SmithingWeapon = 7,
    SmithingArmour = 8,
    Medic = 9,
    Thieving = 10,
    Turrets = 11,
    Farming = 12,
    Cooking = 13,
    HiveMedic = 14,
    Vet = 15,
    Stealth = 16,
    Athletics = 17,
    Dexterity = 18,
    MeleeDefence = 19,
    Weapons = 20,
    Toughness = 21,
    Assassination = 22,
    Swimming = 23,
    Perception = 24,
    Katanas = 25,
    Sabres = 26,
    Hackers = 27,
    HeavyWeapons = 28,
    Blunt = 29,
    MartialArts = 30,
    MassCombat = 31,
    Dodge = 32,
    Survival = 33,
    Polearms = 34,
    Crossbows = 35,
    FriendlyFire = 36,
    Lockpicking = 37,
    SmithingBow = 38,
    End = 39,
}

/// <summary>Names and storage of the stats: the STATS record field (which is also the save key) of each number.</summary>
public static class StatsInfo
{
    /// <summary>Number of stat numbers, 0 (<see cref="StatsEnumerated.None"/>) to 38.</summary>
    public const int Count = (int)StatsEnumerated.End;

    // Index = stat number. Null: no field of that name (aliases and the two numbers without storage).
    static readonly string?[] fieldNames =
    [
        null, "strength", "attack", "labouring", "science", "engineer", "robotics", "weapon smith", "armour smith", "medic",
        "thievery", "turrets", "farming", "cooking", null, null, "stealth", "athletics", "dexterity", "defence",
        null, "toughness2", "assassin", "swimming", "perception", "katana", "sabres", "hackers", "heavy weapons", "blunt",
        "unarmed", "mass combat", "dodge", null, "poles", "bow", "ff", "lockpicking", "bow smith",
    ];

    /// <summary>The STATS field / saved key of a stat (<c>strength</c>, <c>attack</c>, <c>toughness2</c>...); null for numbers that share or lack storage.</summary>
    public static string? FieldName(StatsEnumerated stat) => (uint)stat < Count ? fieldNames[(int)stat] : null;

    /// <summary>The number whose storage a stat uses: 14 and 15 are <see cref="StatsEnumerated.Medic"/>; <see cref="StatsEnumerated.None"/> for 0, 20, 33 and out of range.</summary>
    public static StatsEnumerated StorageOf(StatsEnumerated stat) => stat switch
    {
        StatsEnumerated.HiveMedic or StatsEnumerated.Vet => StatsEnumerated.Medic,
        StatsEnumerated.None or StatsEnumerated.Weapons or StatsEnumerated.Survival => StatsEnumerated.None,
        _ => (uint)stat < Count ? stat : StatsEnumerated.None,
    };

    /// <summary>The stat with a given STATS field name, else <see cref="StatsEnumerated.None"/>.</summary>
    public static StatsEnumerated FromFieldName(string name)
    {
        for (int i = 1; i < Count; i++)
            if (fieldNames[i] == name) return (StatsEnumerated)i;
        return StatsEnumerated.None;
    }

    /// <summary>The 33 stats the FCS STATS record has fields for (<b>Verified</b> against fcs_fields.tsv): every number with a field name except mass combat, which is saved only.</summary>
    public static IReadOnlyList<StatsEnumerated> RecordStats { get; } =
        Enumerable.Range(1, Count - 1).Select(i => (StatsEnumerated)i).Where(s => FieldName(s) is not null && s != StatsEnumerated.MassCombat).ToArray();
}
