namespace ProvingGrounds.Core.Glue;

/// <summary>
/// A character's combat stats as Meitou STORES them (the trained levels the stats screen shows: <c>CharacterCold.Stats[StatsEnumerated.X]</c>,
/// Meitou src/Meitou.Simulation/Bodies/CharacterStats.cs:34-38), copied by the engine adapter (<c>PgEngine.Stats</c>, src/Engine/PgEngine.cs).
/// Plain data, no game types. Holds every stat an original engine read of PA §3.2 / TW §3 maps to; each field names the C++ line that wants it.
/// <para>Not the values the original's weapon- and condition-dependent getters return (<c>getMeleeAttack_melee</c>, <c>getMeleeDefence_melee(false)</c>,
/// <c>getDodge(true)</c>, <c>getMeleeAttack_unarmed(true)</c>, <c>strengthActual</c>, <c>dexterityActual</c>, <c>getEquippedWeaponSkill</c>): those are
/// built later on top of these (PgEngine's header lists them as to-do).</para>
/// <para><see cref="Valid"/> false (<see cref="None"/>, or <c>default</c>) = no value: a stale, removed or stat-less character.</para>
/// </summary>
public readonly struct CombatStats
{
    /// <summary>No value (a stale id, a character without a stats object).</summary>
    public static readonly CombatStats None = default;

    /// <summary>True when the values were read from a live character's stats.</summary>
    public bool Valid { get; init; }

    /// <summary>StatsEnumerated.MeleeAttack (2). Behind <c>getMeleeAttack_melee()</c> / <c>getMeleeAttack_unarmed(true)</c>
    /// (ArenaCombatProfile.cpp:78); S12's mean.</summary>
    public float MeleeAttack { get; init; }
    /// <summary>StatsEnumerated.MeleeDefence (19). Behind <c>getMeleeDefence_melee(false)</c> (ArenaCombatProfile.cpp:79); S12's mean.</summary>
    public float MeleeDefence { get; init; }
    /// <summary>StatsEnumerated.Dodge (32). Behind <c>getDodge(true)</c> (ArenaCombatProfile.cpp:79); S12's mean.</summary>
    public float Dodge { get; init; }
    /// <summary>StatsEnumerated.Strength (1). Behind <c>strengthActual()</c> (ArenaCombatProfile.cpp:80, ArenaUI.cpp:567); S12's mean.</summary>
    public float Strength { get; init; }
    /// <summary>StatsEnumerated.Toughness (21), unscaled. <c>toughness()</c> (ArenaCombatProfile.cpp:81, ArenaUI.cpp:566) reads
    /// <see cref="ScaledToughness"/>; S12's mean uses this stored value.</summary>
    public float Toughness { get; init; }
    /// <summary>StatsEnumerated.Dexterity (18). Behind <c>dexterityActual()</c> (ArenaCombatProfile.cpp:82, ArenaUI.cpp:568); S12's mean.</summary>
    public float Dexterity { get; init; }

    /// <summary>StatsEnumerated.Katanas (25): <c>stats-&gt;katanas</c>, ArenaCombatProfile.cpp:25.</summary>
    public float Katanas { get; init; }
    /// <summary>StatsEnumerated.Sabres (26): <c>stats-&gt;sabres</c>, ArenaCombatProfile.cpp:26.</summary>
    public float Sabres { get; init; }
    /// <summary>StatsEnumerated.Blunt (29): <c>stats-&gt;blunt</c>, ArenaCombatProfile.cpp:27.</summary>
    public float Blunt { get; init; }
    /// <summary>StatsEnumerated.HeavyWeapons (28): <c>stats-&gt;heavyWeapons</c>, ArenaCombatProfile.cpp:28.</summary>
    public float HeavyWeapons { get; init; }
    /// <summary>StatsEnumerated.Hackers (27): <c>stats-&gt;hackers</c>, ArenaCombatProfile.cpp:29.</summary>
    public float Hackers { get; init; }
    /// <summary>StatsEnumerated.MartialArts (30): <c>stats-&gt;unarmed</c>, ArenaCombatProfile.cpp:30.</summary>
    public float MartialArts { get; init; }
    /// <summary>StatsEnumerated.Crossbows (35): <c>stats-&gt;bows</c>, ArenaCombatProfile.cpp:31.</summary>
    public float Crossbows { get; init; }
    /// <summary>StatsEnumerated.Turrets (11): <c>stats-&gt;turrets</c>, ArenaCombatProfile.cpp:32.</summary>
    public float Turrets { get; init; }
    /// <summary>StatsEnumerated.Polearms (34): <c>stats-&gt;polearms</c>, ArenaCombatProfile.cpp:33.</summary>
    public float Polearms { get; init; }

    /// <summary><c>CharacterStats.Scale</c> (CharacterStats.cs:40-45): the read-time multiplier, 1 for people, an animal's age. Meitou's original
    /// toughness read is stat x this (CharacterStats.cs:59-63, <c>ScaledToughness</c>).</summary>
    public float Scale { get; init; }

    /// <summary>The toughness the original's <c>toughness()</c> returns: <see cref="Toughness"/> x <see cref="Scale"/> (Meitou CharacterStats.cs:63).</summary>
    public float ScaledToughness => Toughness * Scale;
}

/// <summary>
/// Spike S12's stand-in for <c>CharStats::getOverallSkillLevel_0_100</c> (docs/spikes.md S12: the 1.0.68 function is unknown). Read by the
/// original at ArenaUI.cpp:565 (card), :1640 (team average), :2264 (balance), LeaderboardStore.cpp:262 (Marks), PrisonerRecruitment.cpp:58 (price).
/// </summary>
public static class OverallSkill
{
    /// <summary>
    /// <c>(MeleeAttack + MeleeDefence + Dodge + Strength + Toughness + Dexterity + Best) / 7</c>, Best = the largest of Katanas, Sabres, Hackers,
    /// HeavyWeapons, Blunt, Polearms, MartialArts, all stored (trained) levels; float sums left to right, clamped to [0, 100], NOT rounded (the
    /// original returns a float and feeds Marks and prices unrounded; only the card prints <c>%.0f</c>). NaN for <see cref="CombatStats.Valid"/> false.
    /// <para>A NaN stat gives NaN (never silently 0): a NaN sum fails both clamp tests and passes through; a NaN weapon skill is never taken as
    /// the best over a number, so it gives NaN only if every weapon skill is NaN. Not expected from Meitou: its stored values
    /// come from the record's STATS fields, the save's floats and <c>Gain</c>'s saturating curve (CharacterStats.cs:119-189), none of which turn
    /// finite numbers into NaN. The original reads real numbers too.</para>
    /// </summary>
    public static float Of(in CombatStats s)
    {
        if (!s.Valid) return float.NaN;
        float best = float.NaN;
        best = Better(best, s.Katanas);
        best = Better(best, s.Sabres);
        best = Better(best, s.Hackers);
        best = Better(best, s.HeavyWeapons);
        best = Better(best, s.Blunt);
        best = Better(best, s.Polearms);
        best = Better(best, s.MartialArts);
        float mean = (s.MeleeAttack + s.MeleeDefence + s.Dodge + s.Strength + s.Toughness + s.Dexterity + best) / 7f;
        return mean < 0f ? 0f : mean > 100f ? 100f : mean;
    }

    // the larger of the two, a NaN never winning over a number
    static float Better(float best, float v) => float.IsNaN(best) || v > best ? v : best;
}
