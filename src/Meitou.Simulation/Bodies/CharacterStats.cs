using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// A character's stats: one float per stat, no level and no XP counter (docs/game/character-stats.md "Stats"). Training raises a stat through
/// <see cref="Gain"/>, whose curve saturates near 101. The instance belongs to one character; nothing here is shared, so the world can run characters
/// in parallel. The XP sources that call <see cref="Gain"/> are in <see cref="XpService"/>.
/// </summary>
public sealed class CharacterStats
{
    /// <summary>
    /// The cap the XP curve uses: every wrapper in the original passes 101.
    /// </summary>
    public const float XpCap = 101;

    /// <summary>
    /// The stored values by stat number. Indexed by stat number; numbers 14, 15 (shared with Medic), 20 and 33 (no storage) stay unused.
    /// </summary>
    readonly float[] _values = new float[StatsInfo.Count];

    /// <summary>
    /// Stats that read as 1 for a character built without data (the editor's default of a STATS field).
    /// </summary>
    public CharacterStats()
    {
        foreach (var s in StatsInfo.RecordStats) _values[(int)s] = 1;
        _values[(int)StatsEnumerated.MassCombat] = 0;
    }

    /// <summary>
    /// The value of a stat (0 for a number without storage); <see cref="StatsEnumerated.HiveMedic"/> and <see cref="StatsEnumerated.Vet"/> read
    /// <see cref="StatsEnumerated.Medic"/>. Setting a number without storage does nothing.
    /// </summary>
    public float this[StatsEnumerated stat]
    {
        get => StatsInfo.StorageOf(stat) is var s && s != StatsEnumerated.None ? _values[(int)s] : 0;
        set { if (StatsInfo.StorageOf(stat) is var s && s != StatsEnumerated.None) _values[(int)s] = value; }
    }

    /// <summary>
    /// The read-time stat multiplier (<c>CharStats</c> +0x34): 1 for people here; an animal's is its age (<c>CharStatsAnimal</c> sets it from the
    /// character's slot 0x398, which returns the age, <b>Observed</b>, docs/game/animals.md). The combat formulas multiply the stats they read by it.
    /// An animal's grows over time (<see cref="BodySystem"/>) and is saved as GAMESTATE_CHARACTER <c>age</c>.
    /// </summary>
    public float Scale { get; set; } = 1;

    /// <summary>
    /// The stored strength.
    /// </summary>
    public float Strength => _values[(int)StatsEnumerated.Strength];
    /// <summary>
    /// The stored dexterity.
    /// </summary>
    public float Dexterity => _values[(int)StatsEnumerated.Dexterity];
    /// <summary>
    /// The stored toughness (unscaled; the formulas read <see cref="ScaledToughness"/>).
    /// </summary>
    public float Toughness => _values[(int)StatsEnumerated.Toughness];

    /// <summary>
    /// The toughness every read of the original uses: stat x the stats multiplier (<c>+0x90</c> x <c>+0x34</c>), see <see cref="Scale"/>.
    /// </summary>
    public float ScaledToughness => Toughness * Scale;
    /// <summary>
    /// The stored athletics.
    /// </summary>
    public float Athletics => _values[(int)StatsEnumerated.Athletics];

    /// <summary>
    /// The values a character had before the debug god mode raised them (null while not boosted); the save writes these, so boosted stats never reach
    /// a save.
    /// </summary>
    float[]? _godOriginal;

    /// <summary>
    /// Whether the debug god mode (<see cref="Boost"/>) holds this character's stats raised.
    /// </summary>
    public bool Boosted => _godOriginal is not null;

    /// <summary>
    /// The values <see cref="Unboost"/> restores, or null while not boosted (state hash only).
    /// </summary>
    internal float[]? GodOriginal => _godOriginal;

    /// <summary>
    /// Debug god mode (Meitou only): keeps the current values for <see cref="Unboost"/> and sets every stat that has storage to
    /// <paramref name="value"/>. Does nothing while already boosted, so the first values stay the ones that come back. Training while boosted changes
    /// nothing visible (the XP curve gives a stat this high no gain) and anything it adds is dropped by <see cref="Unboost"/>.
    /// </summary>
    public void Boost(float value)
    {
        if (_godOriginal is not null) return;
        _godOriginal = (float[])_values.Clone();
        for (int i = 1; i < StatsInfo.Count; i++)
            if (StatsInfo.StorageOf((StatsEnumerated)i) == (StatsEnumerated)i) _values[i] = value;
    }

    /// <summary>
    /// Puts back exactly the values <see cref="Boost"/> kept; does nothing when not boosted.
    /// </summary>
    public void Unboost()
    {
        if (_godOriginal is null) return;
        Array.Copy(_godOriginal, _values, _values.Length);
        _godOriginal = null;
    }

    /// <summary>
    /// A copy of the values by stat number.
    /// </summary>
    public float[] ToArray() => (float[])_values.Clone();

    /// <summary>
    /// The core gain (<c>FUN_1408c5df0</c>, <b>Observed</b>): f = ((cap - stat) / cap)^2; <c>amount * f</c> is added only when 0 &lt; amount &lt;= 20
    /// and 0 &lt; f &lt;= 20; a NaN result is not kept (an old NaN becomes 20). A stat at 1 grows at full rate, at 50 about a quarter; at the cap it
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

    /// <summary>
    /// Trains a stat by <paramref name="amount"/> through the core curve; returns the new value. A stat without storage is left alone. A NaN stat
    /// stays NaN: in game the XP entries (<c>xpStat_eventBased</c>, <c>xpGeneral</c>) never reach the core's reset to 20 for one (<b>Verified</b> in
    /// game, issue #3).
    /// </summary>
    public float Gain(StatsEnumerated stat, float amount, float cap = XpCap)
    {
        var s = StatsInfo.StorageOf(stat);
        if (s == StatsEnumerated.None) return 0;
        if (float.IsNaN(_values[(int)s])) return _values[(int)s];
        return _values[(int)s] = CoreGain(_values[(int)s], amount, cap);
    }

    /// <summary>
    /// A new character's stats (docs/game/character-stats.md "Building a character's stats"): the base values (a STATS record through
    /// <see cref="StatsData.Read"/>, or <see cref="FromGroups"/>), then the CHARACTER's <c>stats randomise</c> n adding a uniform offset in [-n, n]
    /// to each of the 33 record stats (the original's "about 30"; the exact set is <b>Unknown</b>; floored at 0 here), then every stat number 1 to 38
    /// times the race multiplier of that number. Medic's storage is shared by numbers 9, 14 and 15, so it takes the product of the three entries, as
    /// a literal reading of the loop gives (<b>Observed</b>). The offsets come from the world's seeded rolls, so the result depends only on the
    /// arguments.
    /// </summary>
    public static CharacterStats Create(ReadOnlySpan<float> baseValues, RaceData race, float randomise, ulong worldSeed, ulong characterKey)
    {
        var stats = new CharacterStats();
        for (int i = 1; i < StatsInfo.Count && i < baseValues.Length; i++)
            if (StatsInfo.StorageOf((StatsEnumerated)i) == (StatsEnumerated)i) stats._values[i] = baseValues[i];
        if (randomise > 0)
            foreach (var s in StatsInfo.RecordStats)
            {
                float u = BodyRolls.Float(worldSeed, characterKey, BodyRolls.StatsDomain, (ulong)s);
                stats._values[(int)s] = MathF.Max(0, stats._values[(int)s] + (u * 2 - 1) * randomise);
            }
        for (int i = 1; i < StatsInfo.Count; i++)
        {
            var storage = StatsInfo.StorageOf((StatsEnumerated)i);
            if (storage != StatsEnumerated.None) stats._values[(int)storage] *= race.StatMultipliers[i];
        }
        return stats;
    }

    /// <summary>
    /// Stats exactly as saved (the save holds the current trained values, so no randomising and no race multiplier).
    /// </summary>
    public static CharacterStats FromValues(ReadOnlySpan<float> savedValues)
    {
        var stats = new CharacterStats();
        for (int i = 1; i < StatsInfo.Count && i < savedValues.Length; i++)
            if (StatsInfo.StorageOf((StatsEnumerated)i) == (StatsEnumerated)i) stats._values[i] = savedValues[i];
        return stats;
    }

    /// <summary>
    /// The base values of a CHARACTER (or ANIMAL_CHARACTER) that names no STATS record: its integers <c>combat stats</c> c, <c>unarmed stats</c> u,
    /// <c>stealth stats</c> s, <c>ranged stats</c> r and <c>strength</c> each fan out to a group, each only when above 0 and in this order
    /// (<b>Observed</b>, the CharStats constructor <c>FUN_14064cb30</c>, docs/game/character-stats.md "Building a character's stats"): c sets
    /// strength, dexterity, toughness, melee attack and defence, athletics, swimming, medic, turrets, friendly fire and the six weapon skills to c,
    /// assassination to c/2, martial arts and dodge to c/10; u sets martial arts, dodge and dexterity to u; s sets stealth, thieving, lockpicking,
    /// assassination and medic to s and dexterity to max(u, s); <c>strength</c> sets strength; r sets crossbows, perception, friendly fire and
    /// turrets. The rest stays 1 (the defaults record's values are not read here). The legacy <c>endurance</c> slot c also sets has no storage here.
    /// </summary>
    public static float[] FromGroups(int combat, int unarmed, int stealth, int ranged, int strength)
    {
        var v = new float[StatsInfo.Count];
        foreach (var s in StatsInfo.RecordStats) v[(int)s] = 1;
        void Set(float value, params StatsEnumerated[] stats) { foreach (var s in stats) v[(int)s] = value; }
        if (combat > 0)
        {
            Set(combat, StatsEnumerated.Strength, StatsEnumerated.Dexterity, StatsEnumerated.Toughness, StatsEnumerated.MeleeAttack,
                StatsEnumerated.MeleeDefence, StatsEnumerated.Katanas, StatsEnumerated.Sabres, StatsEnumerated.Blunt, StatsEnumerated.Polearms,
                StatsEnumerated.HeavyWeapons, StatsEnumerated.Hackers,
                StatsEnumerated.Turrets, StatsEnumerated.Athletics, StatsEnumerated.Swimming, StatsEnumerated.Medic, StatsEnumerated.FriendlyFire);
            Set(combat * 0.5f, StatsEnumerated.Assassination);
            Set(combat * 0.1f, StatsEnumerated.MartialArts, StatsEnumerated.Dodge);
        }
        if (unarmed > 0) Set(unarmed, StatsEnumerated.MartialArts, StatsEnumerated.Dodge, StatsEnumerated.Dexterity);
        if (stealth > 0)
        {
            Set(Math.Max(unarmed, stealth), StatsEnumerated.Dexterity);
            Set(stealth, StatsEnumerated.Stealth, StatsEnumerated.Thieving, StatsEnumerated.Assassination, StatsEnumerated.Lockpicking,
                StatsEnumerated.Medic);
        }
        if (strength > 0) Set(strength, StatsEnumerated.Strength);
        if (ranged > 0) Set(ranged, StatsEnumerated.Crossbows, StatsEnumerated.Perception, StatsEnumerated.FriendlyFire, StatsEnumerated.Turrets);
        return v;
    }

    /// <summary>
    /// Writes the saved STATS floats (docs/formats/save.md): the stat's field name (<c>strength</c>, <c>attack</c>, <c>toughness2</c>, <c>mass
    /// combat</c>...) to its value. The legacy keys (<c>endurance</c>, <c>warrior spirit</c>, <c>xp</c>, <c>free attribute points</c>...) are not
    /// kept; the save writer gives a new character the STATS template's <c>warrior spirit</c> and a loaded one keeps its saved value.
    /// </summary>
    public void WriteSave(IDictionary<string, float> floats)
    {
        var written = _godOriginal ?? _values;   // a boosted character is saved with the values it had before
        for (int i = 1; i < StatsInfo.Count; i++)
            if (StatsInfo.FieldName((StatsEnumerated)i) is { } name) floats[name] = written[i];
    }

    /// <summary>
    /// Reads the saved STATS floats written by <see cref="WriteSave"/> (a missing key keeps the default), except that a save without
    /// <c>lockpicking</c> gets half the character's thievery and one without <c>toughness2</c> its defence as toughness (<c>FUN_14064b6a0</c>,
    /// docs/game/character-stats.md).
    /// </summary>
    public static CharacterStats ReadSave(IReadOnlyDictionary<string, float> floats)
    {
        var stats = new CharacterStats();
        for (int i = 1; i < StatsInfo.Count; i++)
            if (StatsInfo.FieldName((StatsEnumerated)i) is { } name && floats.TryGetValue(name, out float v)) stats._values[i] = v;
        if (!floats.ContainsKey("lockpicking")) stats._values[(int)StatsEnumerated.Lockpicking] = stats._values[(int)StatsEnumerated.Thieving] * 0.5f;
        // FUN_14064b6a0: toughness = defence
        if (!floats.ContainsKey("toughness2")) stats._values[(int)StatsEnumerated.Toughness] = stats._values[(int)StatsEnumerated.MeleeDefence];
        return stats;
    }
}