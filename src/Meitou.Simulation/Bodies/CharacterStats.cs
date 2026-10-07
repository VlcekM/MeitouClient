using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// A character's stats: one float per stat, no level and no XP counter (docs/game/character-stats.md "Stats"). Training raises a stat through
/// <see cref="Gain"/>, whose curve saturates near 101. The instance belongs to one character; nothing here is shared, so the world can run characters in
/// parallel. The XP sources that call <see cref="Gain"/> are in <see cref="XpService"/>.
/// </summary>
public sealed class CharacterStats
{
    /// <summary>The cap the XP curve uses: every wrapper in the original passes 101.</summary>
    public const float XpCap = 101;

    // Indexed by stat number; numbers 14, 15 (shared with Medic), 20 and 33 (no storage) stay unused.
    readonly float[] values = new float[StatsInfo.Count];

    /// <summary>Stats that read as 1 for a character built without data (the editor's default of a STATS field).</summary>
    public CharacterStats()
    {
        foreach (var s in StatsInfo.RecordStats) values[(int)s] = 1;
        values[(int)StatsEnumerated.MassCombat] = 0;
    }

    /// <summary>
    /// The value of a stat (0 for a number without storage); <see cref="StatsEnumerated.HiveMedic"/> and <see cref="StatsEnumerated.Vet"/> read
    /// <see cref="StatsEnumerated.Medic"/>. Setting a number without storage does nothing.
    /// </summary>
    public float this[StatsEnumerated stat]
    {
        get => StatsInfo.StorageOf(stat) is var s && s != StatsEnumerated.None ? values[(int)s] : 0;
        set { if (StatsInfo.StorageOf(stat) is var s && s != StatsEnumerated.None) values[(int)s] = value; }
    }

    public float Strength => values[(int)StatsEnumerated.Strength];
    public float Dexterity => values[(int)StatsEnumerated.Dexterity];
    public float Toughness => values[(int)StatsEnumerated.Toughness];
    public float Athletics => values[(int)StatsEnumerated.Athletics];

    /// <summary>A copy of the values by stat number.</summary>
    public float[] ToArray() => (float[])values.Clone();

    /// <summary>
    /// The core gain (<c>FUN_1408c5df0</c>, <b>Observed</b>): f = ((cap - stat) / cap)^2; <c>amount * f</c> is added only when 0 &lt; amount &lt;= 20 and
    /// 0 &lt; f &lt;= 20; a NaN result is not kept (an old NaN becomes 20). A stat at 1 grows at full rate, at 50 about a quarter; at the cap it
    /// stops. The square makes a stat <i>above</i> the cap grow again, until f passes 20 (above about 553).
    /// </summary>
    public static float CoreGain(float stat, float amount, float cap = XpCap)
    {
        if (float.IsNaN(stat)) return 20;
        float f = (cap - stat) / cap;
        f *= f;
        if (!(amount > 0 && amount <= 20 && f > 0 && f <= 20)) return stat;
        float result = stat + amount * f;
        return float.IsNaN(result) ? stat : result;
    }

    /// <summary>Trains a stat by <paramref name="amount"/> through the core curve; returns the new value. A stat without storage is left alone.</summary>
    public float Gain(StatsEnumerated stat, float amount, float cap = XpCap)
    {
        var s = StatsInfo.StorageOf(stat);
        if (s == StatsEnumerated.None) return 0;
        return values[(int)s] = CoreGain(values[(int)s], amount, cap);
    }

    /// <summary>
    /// A new character's stats (docs/game/character-stats.md "Building a character's stats"): the base values (a STATS record through
    /// <see cref="StatsData.Read"/>, or <see cref="FromGroups"/>), then the CHARACTER's <c>stats randomise</c> n adding a uniform offset in [-n, n] to each of the
    /// 33 record stats (the original's "about 30"; the exact set is <b>Unknown</b>; floored at 0 here), then every stat number 1 to 38 times the race
    /// multiplier of that number. Medic's storage is shared by numbers 9, 14 and 15, so it takes the product of the three entries, as a literal
    /// reading of the loop gives (<b>Observed</b>). The offsets come from the world's seeded rolls, so the result depends only on the arguments.
    /// </summary>
    public static CharacterStats Create(ReadOnlySpan<float> baseValues, RaceData race, float randomise, ulong worldSeed, ulong characterKey)
    {
        var stats = new CharacterStats();
        for (int i = 1; i < StatsInfo.Count && i < baseValues.Length; i++)
            if (StatsInfo.StorageOf((StatsEnumerated)i) == (StatsEnumerated)i) stats.values[i] = baseValues[i];
        if (randomise > 0)
            foreach (var s in StatsInfo.RecordStats)
            {
                float u = BodyRolls.Float(worldSeed, characterKey, BodyRolls.StatsDomain, (ulong)s);
                stats.values[(int)s] = MathF.Max(0, stats.values[(int)s] + (u * 2 - 1) * randomise);
            }
        for (int i = 1; i < StatsInfo.Count; i++)
        {
            var storage = StatsInfo.StorageOf((StatsEnumerated)i);
            if (storage != StatsEnumerated.None) stats.values[(int)storage] *= race.StatMultipliers[i];
        }
        return stats;
    }

    /// <summary>Stats exactly as saved (the save holds the current trained values, so no randomising and no race multiplier).</summary>
    public static CharacterStats FromValues(ReadOnlySpan<float> savedValues)
    {
        var stats = new CharacterStats();
        for (int i = 1; i < StatsInfo.Count && i < savedValues.Length; i++)
            if (StatsInfo.StorageOf((StatsEnumerated)i) == (StatsEnumerated)i) stats.values[i] = savedValues[i];
        return stats;
    }

    /// <summary>
    /// The base values of a CHARACTER that names no STATS record: its integers <c>combat stats</c>, <c>unarmed stats</c>, <c>stealth stats</c>,
    /// <c>ranged stats</c> and <c>strength</c> each fan out to a group. <b>Unknown</b> in the original ("exact groups not decoded"); the groups here are an
    /// engine choice: combat gives melee attack, defence, dodge and the weapon skills, unarmed gives martial arts, stealth gives stealth, assassination,
    /// thieving and lockpicking, ranged gives crossbows and turrets. Everything else stays 1; a group of 0 leaves its stats at 1.
    /// </summary>
    public static float[] FromGroups(int combat, int unarmed, int stealth, int ranged, int strength)
    {
        var v = new float[StatsInfo.Count];
        foreach (var s in StatsInfo.RecordStats) v[(int)s] = 1;
        void Set(int value, params StatsEnumerated[] stats) { if (value > 0) foreach (var s in stats) v[(int)s] = value; }
        Set(combat, StatsEnumerated.MeleeAttack, StatsEnumerated.MeleeDefence, StatsEnumerated.Dodge, StatsEnumerated.Katanas, StatsEnumerated.Sabres,
            StatsEnumerated.HeavyWeapons, StatsEnumerated.Blunt, StatsEnumerated.Polearms);
        Set(unarmed, StatsEnumerated.MartialArts);
        Set(stealth, StatsEnumerated.Stealth, StatsEnumerated.Assassination, StatsEnumerated.Thieving, StatsEnumerated.Lockpicking);
        Set(ranged, StatsEnumerated.Crossbows, StatsEnumerated.Turrets);
        Set(strength, StatsEnumerated.Strength);
        return v;
    }

    /// <summary>
    /// Writes the saved STATS floats (docs/formats/save.md): the stat's field name (<c>strength</c>, <c>attack</c>, <c>toughness2</c>, <c>mass combat</c>...) to its value.
    /// The legacy keys (<c>endurance</c>, <c>warrior spirit</c>, <c>xp</c>, <c>free attribute points</c>...) are not kept.
    /// </summary>
    public void WriteSave(IDictionary<string, float> floats)
    {
        for (int i = 1; i < StatsInfo.Count; i++)
            if (StatsInfo.FieldName((StatsEnumerated)i) is { } name) floats[name] = values[i];
    }

    /// <summary>Reads the saved STATS floats written by <see cref="WriteSave"/> (a missing key keeps the default).</summary>
    public static CharacterStats ReadSave(IReadOnlyDictionary<string, float> floats)
    {
        var stats = new CharacterStats();
        for (int i = 1; i < StatsInfo.Count; i++)
            if (StatsInfo.FieldName((StatsEnumerated)i) is { } name && floats.TryGetValue(name, out float v)) stats.values[i] = v;
        return stats;
    }
}
