namespace Meitou.Data.Gameplay.Bodies;

/// <summary>
/// The game's stat numbers (<c>StatsEnumerated</c>, docs/game/character-stats.md "The stat list", <b>Verified</b>): what RACE <c>heal stat</c>,
/// <c>stats good&lt;N&gt;</c> / <c>stats bad&lt;N&gt;</c> and all XP code pass around. Numbers 14 and 15 share the storage of <see cref="Medic"/>; 20
/// and 33 have no storage at all.
/// </summary>
public enum StatsEnumerated
{
    /// <summary>
    /// No stat (the enum's 0; no storage).
    /// </summary>
    None = 0,
    /// <summary>
    /// <c>STAT_STRENGTH</c>: STATS field and save key <c>strength</c>, CharStats <c>+0x80</c>.
    /// </summary>
    Strength = 1,
    /// <summary>
    /// <c>STAT_MELEE_ATTACK</c>: STATS field and save key <c>attack</c>, CharStats <c>+0x120</c>.
    /// </summary>
    MeleeAttack = 2,
    /// <summary>
    /// <c>STAT_LABOURING</c>: STATS field and save key <c>labouring</c>, CharStats <c>+0xe4</c>.
    /// </summary>
    Labouring = 3,
    /// <summary>
    /// <c>STAT_SCIENCE</c>: STATS field and save key <c>science</c>, CharStats <c>+0xe0</c>.
    /// </summary>
    Science = 4,
    /// <summary>
    /// <c>STAT_ENGINEERING</c>: STATS field and save key <c>engineer</c>, CharStats <c>+0xcc</c>.
    /// </summary>
    Engineering = 5,
    /// <summary>
    /// <c>STAT_ROBOTICS</c>: STATS field and save key <c>robotics</c>, CharStats <c>+0xdc</c>.
    /// </summary>
    Robotics = 6,
    /// <summary>
    /// <c>STAT_SMITHING_WEAPON</c>: STATS field and save key <c>weapon smith</c>, CharStats <c>+0xd0</c>.
    /// </summary>
    SmithingWeapon = 7,
    /// <summary>
    /// <c>STAT_SMITHING_ARMOUR</c>: STATS field and save key <c>armour smith</c>, CharStats <c>+0xd4</c>.
    /// </summary>
    SmithingArmour = 8,
    /// <summary>
    /// <c>STAT_MEDIC</c>: STATS field and save key <c>medic</c>, CharStats <c>+0x98</c>.
    /// </summary>
    Medic = 9,
    /// <summary>
    /// <c>STAT_THIEVING</c>: STATS field and save key <c>thievery</c>, CharStats <c>+0xac</c>.
    /// </summary>
    Thieving = 10,
    /// <summary>
    /// <c>STAT_TURRETS</c>: STATS field and save key <c>turrets</c>, CharStats <c>+0x114</c>.
    /// </summary>
    Turrets = 11,
    /// <summary>
    /// <c>STAT_FARMING</c>: STATS field and save key <c>farming</c>, CharStats <c>+0xe8</c>.
    /// </summary>
    Farming = 12,
    /// <summary>
    /// <c>STAT_COOKING</c>: STATS field and save key <c>cooking</c>, CharStats <c>+0xec</c>.
    /// </summary>
    Cooking = 13,
    /// <summary>
    /// <c>STAT_HIVEMEDIC</c>: shares <c>medic</c> (CharStats <c>+0x98</c>).
    /// </summary>
    HiveMedic = 14,
    /// <summary>
    /// <c>STAT_VET</c>: shares <c>medic</c> (CharStats <c>+0x98</c>).
    /// </summary>
    Vet = 15,
    /// <summary>
    /// <c>STAT_STEALTH</c>: STATS field and save key <c>stealth</c>, CharStats <c>+0xa4</c>.
    /// </summary>
    Stealth = 16,
    /// <summary>
    /// <c>STAT_ATHLETICS</c>: STATS field and save key <c>athletics</c>, CharStats <c>+0x94</c>.
    /// </summary>
    Athletics = 17,
    /// <summary>
    /// <c>STAT_DEXTERITY</c>: STATS field and save key <c>dexterity</c>, CharStats <c>+0x88</c>.
    /// </summary>
    Dexterity = 18,
    /// <summary>
    /// <c>STAT_MELEE_DEFENCE</c>: STATS field and save key <c>defence</c>, CharStats <c>+0x124</c>.
    /// </summary>
    MeleeDefence = 19,
    /// <summary>
    /// <c>STAT_WEAPONS</c>: no storage (the accessor returns a dummy slot).
    /// </summary>
    Weapons = 20,
    /// <summary>
    /// <c>STAT_TOUGHNESS</c>: STATS field and save key <c>toughness2</c>, CharStats <c>+0x90</c>.
    /// </summary>
    Toughness = 21,
    /// <summary>
    /// <c>STAT_ASSASSINATION</c>: STATS field and save key <c>assassin</c>, CharStats <c>+0xb8</c>.
    /// </summary>
    Assassination = 22,
    /// <summary>
    /// <c>STAT_SWIMMING</c>: STATS field and save key <c>swimming</c>, CharStats <c>+0xa8</c>.
    /// </summary>
    Swimming = 23,
    /// <summary>
    /// <c>STAT_PERCEPTION</c>: STATS field and save key <c>perception</c>, CharStats <c>+0x8c</c>.
    /// </summary>
    Perception = 24,
    /// <summary>
    /// <c>STAT_KATANAS</c>: STATS field and save key <c>katana</c>, CharStats <c>+0xf8</c>.
    /// </summary>
    Katanas = 25,
    /// <summary>
    /// <c>STAT_SABRES</c>: STATS field and save key <c>sabres</c>, CharStats <c>+0xfc</c>.
    /// </summary>
    Sabres = 26,
    /// <summary>
    /// <c>STAT_HACKERS</c>: STATS field and save key <c>hackers</c>, CharStats <c>+0x100</c>.
    /// </summary>
    Hackers = 27,
    /// <summary>
    /// <c>STAT_HEAVYWEAPONS</c>: STATS field and save key <c>heavy weapons</c>, CharStats <c>+0x108</c>.
    /// </summary>
    HeavyWeapons = 28,
    /// <summary>
    /// <c>STAT_BLUNT</c>: STATS field and save key <c>blunt</c>, CharStats <c>+0x104</c>.
    /// </summary>
    Blunt = 29,
    /// <summary>
    /// <c>STAT_MARTIALARTS</c>: STATS field and save key <c>unarmed</c>, CharStats <c>+0x10c</c>.
    /// </summary>
    MartialArts = 30,
    /// <summary>
    /// <c>STAT_MASSCOMBAT</c>: save key <c>mass combat</c> only (no STATS field), CharStats <c>+0x9c</c>.
    /// </summary>
    MassCombat = 31,
    /// <summary>
    /// <c>STAT_DODGE</c>: STATS field and save key <c>dodge</c>, CharStats <c>+0xf0</c>.
    /// </summary>
    Dodge = 32,
    /// <summary>
    /// <c>STAT_SURVIVAL</c>: no storage (dummy).
    /// </summary>
    Survival = 33,
    /// <summary>
    /// <c>STAT_POLEARMS</c>: STATS field and save key <c>poles</c>, CharStats <c>+0x118</c>.
    /// </summary>
    Polearms = 34,
    /// <summary>
    /// <c>STAT_CROSSBOWS</c>: STATS field and save key <c>bow</c>, CharStats <c>+0x110</c>.
    /// </summary>
    Crossbows = 35,
    /// <summary>
    /// <c>STAT_FRIENDLY_FIRE</c>: STATS field and save key <c>ff</c>, CharStats <c>+0xf4</c>.
    /// </summary>
    FriendlyFire = 36,
    /// <summary>
    /// <c>STAT_LOCKPICKING</c>: STATS field and save key <c>lockpicking</c>, CharStats <c>+0xb0</c>.
    /// </summary>
    Lockpicking = 37,
    /// <summary>
    /// <c>STAT_SMITHING_BOW</c>: STATS field and save key <c>bow smith</c>, CharStats <c>+0xd8</c>.
    /// </summary>
    SmithingBow = 38,
    /// <summary>
    /// One past the last stat number: the count, not a stat.
    /// </summary>
    End = 39,
}

/// <summary>
/// Names and storage of the stats: the STATS record field (which is also the save key) of each number.
/// </summary>
public static class StatsInfo
{
    /// <summary>
    /// Number of stat numbers, 0 (<see cref="StatsEnumerated.None"/>) to 38.
    /// </summary>
    public const int Count = (int)StatsEnumerated.End;

    /// <summary>
    /// The STATS field (and save key) of each stat number, by number; null where there is no field of that name (14 and 15 share medic's, 20 and 33
    /// have no storage).
    /// </summary>
    static readonly string?[] FieldNames =
    [
        null, "strength", "attack", "labouring", "science", "engineer", "robotics", "weapon smith", "armour smith", "medic",
        "thievery", "turrets", "farming", "cooking", null, null, "stealth", "athletics", "dexterity", "defence",
        null, "toughness2", "assassin", "swimming", "perception", "katana", "sabres", "hackers", "heavy weapons", "blunt",
        "unarmed", "mass combat", "dodge", null, "poles", "bow", "ff", "lockpicking", "bow smith",
    ];

    /// <summary>
    /// The STATS field / saved key of a stat (<c>strength</c>, <c>attack</c>, <c>toughness2</c>...); null for numbers that share or lack storage.
    /// </summary>
    public static string? FieldName(StatsEnumerated stat) => (uint)stat < Count ? FieldNames[(int)stat] : null;

    /// <summary>
    /// The number whose storage a stat uses: 14 and 15 are <see cref="StatsEnumerated.Medic"/>; <see cref="StatsEnumerated.None"/> for 0, 20, 33 and
    /// out of range.
    /// </summary>
    public static StatsEnumerated StorageOf(StatsEnumerated stat) => stat switch
    {
        StatsEnumerated.HiveMedic or StatsEnumerated.Vet => StatsEnumerated.Medic,
        StatsEnumerated.None or StatsEnumerated.Weapons or StatsEnumerated.Survival => StatsEnumerated.None,
        _ => (uint)stat < Count ? stat : StatsEnumerated.None,
    };

    /// <summary>
    /// The stat with a given STATS field name, else <see cref="StatsEnumerated.None"/>.
    /// </summary>
    public static StatsEnumerated FromFieldName(string name)
    {
        for (int i = 1; i < Count; i++)
            if (FieldNames[i] == name) return (StatsEnumerated)i;

        return StatsEnumerated.None;
    }

    /// <summary>
    /// The 33 stats the FCS STATS record has fields for (<b>Verified</b> against fcs_fields.tsv): every number with a field name except mass combat,
    /// which is saved only.
    /// </summary>
    public static IReadOnlyList<StatsEnumerated> RecordStats { get; } =
        Enumerable.Range(1, Count - 1)
            .Select(i => (StatsEnumerated)i)
            .Where(s => FieldName(s) is not null && s != StatsEnumerated.MassCombat)
            .ToArray();
}
