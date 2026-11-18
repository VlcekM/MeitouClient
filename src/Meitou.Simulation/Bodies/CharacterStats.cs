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
    /// The largest amount the core gain adds (<see cref="CoreGain"/>).
    /// </summary>
    const float MaxCoreGain = 20;

    /// <summary>
    /// The stored values by stat number. Indexed by stat number; numbers 14, 15 (shared with Medic), 20 and 33 (no storage) stay unused.
    /// </summary>
    readonly float[] _values = new float[StatsInfo.Count];

    /// <summary>
    /// The code mods' modifiers, made on first use (<see cref="Modifiers"/>).
    /// </summary>
    StatModifiers? _modifiers;

    /// <summary>
    /// The levels in code mods' skills, made on first use (<see cref="ModSkills"/>).
    /// </summary>
    ModSkillLevels? _modSkills;

    /// <summary>
    /// The code mods' status effects on this character, made on first use (<see cref="Effects"/>).
    /// </summary>
    ActiveStatusEffects? _effects;

    /// <summary>
    /// The world whose experience filters this character's gains pass (<see cref="BindExperience"/>): set while the character is in a world that
    /// has a filter, null otherwise.
    /// </summary>
    World? _xpWorld;

    /// <summary>
    /// The character these stats belong to in <see cref="_xpWorld"/>.
    /// </summary>
    CharacterId _xpOwner = CharacterId.None;

    /// <summary>
    /// The code mods' carrying capacity modifiers, made on first use (<see cref="CarryModifiers"/>).
    /// </summary>
    CarryModifiers? _carryModifiers;

    /// <summary>
    /// What this character's XP gains are multiplied by (Advanced settings "Squad XP" and "Others' XP", <see cref="BodyOptions.SquadXp"/>; Meitou
    /// only, the game has 1). <see cref="BodySystem"/> sets it on every body step from the character's side; not saved.
    /// </summary>
    public float XpRate { get; set; } = 1;

    /// <summary>
    /// Stats that read as 1 for a character built without data (the editor's default of a STATS field).
    /// </summary>
    public CharacterStats()
    {
        foreach (var s in StatsInfo.RecordStats) _values[(int)s] = 1;
        _values[(int)StatsEnumerated.MassCombat] = 0;
    }

    /// <summary>
    /// The value of a stat: the stored value plus the <see cref="Modifiers"/> (0 for a number without storage); <see cref="StatsEnumerated.HiveMedic"/>
    /// and <see cref="StatsEnumerated.Vet"/> read <see cref="StatsEnumerated.Medic"/>. Setting writes the stored value; a number without storage does
    /// nothing. Code that trains or keeps a stat reads <see cref="Stored"/>, so a modifier is never baked in. A code mod's skill's number
    /// (<see cref="ModSkill.Stat"/>) reads and writes its level (no modifiers), so a weapon that names the skill uses it as any weapon skill.
    /// </summary>
    public float this[StatsEnumerated stat]
    {
        get => StatsInfo.StorageOf(stat) is var s && s != StatsEnumerated.None ? Value(s) : ModLevel(stat);
        set
        {
            if (StatsInfo.StorageOf(stat) is var s && s != StatsEnumerated.None) _values[(int)s] = value;
            else Bodies.ModSkills.Of(stat)?.Set(this, value);
        }
    }

    /// <summary>
    /// The stored (trained) value of a stat, without the <see cref="Modifiers"/> (0 for a number without storage; a mod skill's level for its
    /// number).
    /// </summary>
    public float Stored(StatsEnumerated stat) => StatsInfo.StorageOf(stat) is var s && s != StatsEnumerated.None ? _values[(int)s] : ModLevel(stat);

    /// <summary>
    /// The level of the code mod's skill numbered <paramref name="stat"/>; 0 for any number that is not one (every number without mods).
    /// </summary>
    float ModLevel(StatsEnumerated stat) => Bodies.ModSkills.Of(stat) is { } skill ? skill.Level(this) : 0;

    /// <summary>
    /// The code mods' temporary amounts on this character's stats (docs/modding.md "Stat modifiers"), made on first use.
    /// </summary>
    public StatModifiers Modifiers => _modifiers ??= new();

    /// <summary>
    /// The modifiers if any were ever made (state hash only).
    /// </summary>
    internal StatModifiers? ModifiersOrNull => _modifiers;

    /// <summary>
    /// The levels in code mods' skills (docs/modding.md "Skills"), made on first use; read and train them through <see cref="ModSkill"/>.
    /// </summary>
    public ModSkillLevels ModSkills => _modSkills ??= new();

    /// <summary>
    /// The mod skill levels if any were ever made.
    /// </summary>
    internal ModSkillLevels? ModSkillsOrNull => _modSkills;

    /// <summary>
    /// The code mods' status effects on this character (docs/modding.md "Status effects"), null if none was ever put on; put them on and take them
    /// off through <see cref="StatusEffect"/>.
    /// </summary>
    public ActiveStatusEffects? EffectsOrNull => _effects;

    /// <summary>
    /// The status effects, made on first use.
    /// </summary>
    internal ActiveStatusEffects Effects => _effects ??= new();

    /// <summary>
    /// The code mods' amounts on this character's carrying capacity (docs/modding.md "Carry capacity"), made on first use.
    /// </summary>
    public CarryModifiers CarryModifiers => _carryModifiers ??= new();

    /// <summary>
    /// The carrying capacity modifiers if any were ever made (the capacity's readers and the state hash).
    /// </summary>
    internal CarryModifiers? CarryModifiersOrNull => _carryModifiers;

    /// <summary>
    /// A storage's value with its modifiers.
    /// </summary>
    float Value(StatsEnumerated storage) => _values[(int)storage] + (_modifiers?[storage] ?? 0);

    /// <summary>
    /// The read-time stat multiplier (<c>CharStats</c> +0x34): 1 for people here; an animal's is its age (<c>CharStatsAnimal</c> sets it from the
    /// character's slot 0x398, which returns the age, <b>Observed</b>, docs/game/animals.md). The combat formulas multiply the stats they read by it.
    /// An animal's grows over time (<see cref="BodySystem"/>) and is saved as GAMESTATE_CHARACTER <c>age</c>.
    /// </summary>
    public float Scale { get; set; } = 1;

    /// <summary>
    /// The strength, with its modifiers.
    /// </summary>
    public float Strength => Value(StatsEnumerated.Strength);
    /// <summary>
    /// The dexterity, with its modifiers.
    /// </summary>
    public float Dexterity => Value(StatsEnumerated.Dexterity);
    /// <summary>
    /// The toughness, with its modifiers (unscaled; the formulas read <see cref="ScaledToughness"/>).
    /// </summary>
    public float Toughness => Value(StatsEnumerated.Toughness);

    /// <summary>
    /// The toughness every read of the original uses: stat x the stats multiplier (<c>+0x90</c> x <c>+0x34</c>), see <see cref="Scale"/>.
    /// </summary>
    public float ScaledToughness => Toughness * Scale;
    /// <summary>
    /// The athletics, with its modifiers.
    /// </summary>
    public float Athletics => Value(StatsEnumerated.Athletics);

    /// <summary>
    /// The values a character had before the debug "Set stats to 1000" raised them (null while not boosted); the save writes these, so boosted stats
    /// never reach a save.
    /// </summary>
    float[]? _boostOriginal;

    /// <summary>
    /// Whether the debug stat boost (<see cref="Boost"/>) holds this character's stats raised.
    /// </summary>
    public bool Boosted => _boostOriginal is not null;

    /// <summary>
    /// The values <see cref="Unboost"/> restores, or null while not boosted (state hash only).
    /// </summary>
    internal float[]? BoostOriginal => _boostOriginal;

    /// <summary>
    /// The debug stat boost (Meitou only): keeps the current values for <see cref="Unboost"/> and sets every stat that has storage to
    /// <paramref name="value"/>. Does nothing while already boosted, so the first values stay the ones that come back. Training while boosted changes
    /// nothing visible (the XP curve gives a stat this high no gain) and anything it adds is dropped by <see cref="Unboost"/>.
    /// </summary>
    public void Boost(float value)
    {
        if (_boostOriginal is not null) return;
        _boostOriginal = (float[])_values.Clone();
        for (int i = 1; i < StatsInfo.Count; i++)
            if (StatsInfo.StorageOf((StatsEnumerated)i) == (StatsEnumerated)i) _values[i] = value;
    }

    /// <summary>
    /// Puts back exactly the values <see cref="Boost"/> kept; does nothing when not boosted.
    /// </summary>
    public void Unboost()
    {
        if (_boostOriginal is null) return;
        Array.Copy(_boostOriginal, _values, _values.Length);
        _boostOriginal = null;
    }

    /// <summary>
    /// A copy of the stored values by stat number (no modifiers).
    /// </summary>
    public float[] ToArray() => (float[])_values.Clone();

    /// <summary>
    /// A copy of the values with their modifiers by stat number (what the stats window shows).
    /// </summary>
    public float[] ToEffectiveArray()
    {
        var values = ToArray();
        if (_modifiers is null) return values;
        for (int i = 1; i < values.Length; i++)
            if (StatsInfo.StorageOf((StatsEnumerated)i) == (StatsEnumerated)i) values[i] += _modifiers[(StatsEnumerated)i];
        return values;
    }

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
        if (!(amount > 0 && amount <= MaxCoreGain && f > 0 && f <= 20)) return stat;
        float result = stat + amount * f;
        return float.IsNaN(result) ? stat : result;
    }

    /// <summary>
    /// Trains a stat by <paramref name="amount"/> through the core curve; returns the new value. A stat without storage is left alone. A NaN stat
    /// stays NaN: in game the XP entries (<c>xpStat_eventBased</c>, <c>xpGeneral</c>) never reach the core's reset to 20 for one (<b>Verified</b> in
    /// game, issue #3). A code mod's skill's number trains that skill on the same curve (<see cref="ModSkill.Gain(CharacterStats, float)"/>).
    /// </summary>
    public float Gain(StatsEnumerated stat, float amount, float cap = XpCap) => Gain(stat, amount, cap, ExperienceSource.Other);

    /// <summary>
    /// <see cref="Gain(StatsEnumerated, float, float)"/> for one of the game's own gains, which the code mods' experience filters see as
    /// <paramref name="source"/>.
    /// </summary>
    internal float Gain(StatsEnumerated stat, float amount, float cap, ExperienceSource source)
    {
        var s = StatsInfo.StorageOf(stat);
        if (s == StatsEnumerated.None) return Bodies.ModSkills.Of(stat)?.Gain(this, amount, cap) ?? 0;
        float value = _values[(int)s];
        if (float.IsNaN(value)) return value;
        return _values[(int)s] = CoreGain(value, Filtered(stat, value, Rated(value, amount, cap), cap, source), cap);
    }

    /// <summary>
    /// Binds these stats to <paramref name="world"/>'s experience filters as <paramref name="owner"/>'s, as the character enters the world
    /// (<see cref="CharacterWatch"/>).
    /// </summary>
    internal void BindExperience(World world, CharacterId owner) => (_xpWorld, _xpOwner) = (world, owner);

    /// <summary>
    /// Clears the binding, as the character leaves the world or the world is discarded.
    /// </summary>
    internal void UnbindExperience() => (_xpWorld, _xpOwner) = (null, CharacterId.None);

    /// <summary>
    /// Takes the binding of <paramref name="old"/>, the stats these replace on a character, and clears it there.
    /// </summary>
    internal void TakeExperienceBinding(CharacterStats? old)
    {
        if (old?._xpWorld is null) return;

        (_xpWorld, _xpOwner) = (old._xpWorld, old._xpOwner);
        old.UnbindExperience();
    }

    /// <summary>
    /// A core gain's amount after the code mods' experience filters (<see cref="IExperienceFilter"/>), held as <see cref="Rated"/> holds the
    /// rate: to the core's limit, and with no raise for a stat at or past the gain's cap. Only an amount in the core's range (above 0, at most its
    /// limit) is asked: the core adds nothing for any other, and a filter cannot bring one back. The amount itself without a filter.
    /// </summary>
    float Filtered(StatsEnumerated stat, float value, float amount, float cap, ExperienceSource source)
    {
        if (_xpWorld is null || !(amount > 0 && amount <= MaxCoreGain)) return amount;

        float filtered = Ask(stat, amount, source);
        return value >= cap ? MathF.Min(filtered, amount) : MathF.Min(filtered, MaxCoreGain);
    }

    /// <summary>
    /// An amount the game adds to a stat straight, past the core curve (first aid, mass combat), after the code mods' experience filters, held as
    /// the game holds the XP rate setting there: no raise once the stat is at or past <paramref name="raiseBelow"/> (first aid's 100; mass combat
    /// has no such limit). The amount itself without a filter or for one not above 0.
    /// </summary>
    internal float FilterStraight(StatsEnumerated stat, float amount, ExperienceSource source, float raiseBelow)
    {
        if (_xpWorld is null || !(amount > 0)) return amount;

        float filtered = Ask(stat, amount, source);
        return Stored(stat) >= raiseBelow ? MathF.Min(filtered, amount) : filtered;
    }

    /// <summary>
    /// The filters' amount for a gain of these bound stats; the amount itself for stats no longer the bound character's (a safety: a stats object
    /// a mod moved by hand is never charged to whoever now holds the slot).
    /// </summary>
    float Ask(StatsEnumerated stat, float amount, ExperienceSource source)
    {
        var world = _xpWorld!;
        if (world.Characters.Cold(_xpOwner.Slot)?.Stats != this) return amount;

        return world.ExperienceFilters.Pass(world, _xpOwner, stat, amount, source);
    }

    /// <summary>
    /// A gain times <see cref="XpRate"/>, held to the core's limit of 20 so a multiplied gain is never dropped. An amount the core ignores stays as it
    /// is, and a stat at or past the gain's cap (a training dummy's level) takes no raise, so a dummy's limit stands.
    /// </summary>
    internal float Rated(float stat, float amount, float cap)
    {
        float rate = XpRate;
        if (rate == 1 || !(amount > 0 && amount <= MaxCoreGain)) return amount;
        if (rate > 1 && stat >= cap) return amount;

        return MathF.Min(amount * rate, MaxCoreGain);
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
    /// kept; the save writer gives a new character the STATS template's <c>warrior spirit</c> and a loaded one keeps its saved value. The code mods'
    /// skill levels follow under <see cref="ModSkillLevels.SaveKeyPrefix"/>, none without them, and their status effects under
    /// <see cref="ActiveStatusEffects.SaveKeyPrefix"/> (the record's earlier effect keys removed first) once this character had one.
    /// </summary>
    public void WriteSave(IDictionary<string, float> floats)
    {
        var written = _boostOriginal ?? _values;   // a boosted character is saved with the values it had before
        for (int i = 1; i < StatsInfo.Count; i++)
            if (StatsInfo.FieldName((StatsEnumerated)i) is { } name) floats[name] = written[i];
        _modSkills?.WriteSave(floats);
        _effects?.WriteSave(floats);
    }

    /// <summary>
    /// Reads the saved STATS floats written by <see cref="WriteSave"/> (a missing key keeps the default), except that a save without
    /// <c>lockpicking</c> gets half the character's thievery and one without <c>toughness2</c> its defence as toughness (<c>FUN_14064b6a0</c>,
    /// docs/game/character-stats.md). Every mod skill level and status effect is kept, registered or not; a registered effect's stat amounts are set
    /// again.
    /// </summary>
    public static CharacterStats ReadSave(IReadOnlyDictionary<string, float> floats)
    {
        var stats = new CharacterStats();
        for (int i = 1; i < StatsInfo.Count; i++)
            if (StatsInfo.FieldName((StatsEnumerated)i) is { } name && floats.TryGetValue(name, out float v)) stats._values[i] = v;
        if (!floats.ContainsKey("lockpicking")) stats._values[(int)StatsEnumerated.Lockpicking] = stats._values[(int)StatsEnumerated.Thieving] * 0.5f;
        // FUN_14064b6a0: toughness = defence
        if (!floats.ContainsKey("toughness2")) stats._values[(int)StatsEnumerated.Toughness] = stats._values[(int)StatsEnumerated.MeleeDefence];
        stats._modSkills = ModSkillLevels.ReadSave(floats);
        stats._effects = ActiveStatusEffects.ReadSave(floats, stats);
        return stats;
    }
}