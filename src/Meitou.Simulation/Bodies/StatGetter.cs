using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The general stat getter <c>CharStats::getStat(stat, unmodified)</c> (<c>FUN_140884ac0</c>, docs/game/character-stats.md "The stat getter"), which
/// BFrizz's Extra Extensions' stat conditions read.
/// </summary>
public static class StatGetter
{
    /// <summary>
    /// The highest stat number the getter knows; above it, as for 0, 20 and 33, it returns 0.
    /// </summary>
    const int LastStat = 38;

    /// <summary>
    /// The multiplier's terms the strength and dexterity readers ask for: no light and no dust.
    /// </summary>
    const StatUse BodyUse = StatUse.Hunger | StatUse.Limbs | StatUse.DamageState | StatUse.LimbItems;

    /// <summary>
    /// The stat <paramref name="stat"/> of <paramref name="c"/>: raw (<paramref name="unmodified"/>) the stored value, strength and dexterity times the
    /// stats' scale; otherwise as its injuries, hunger, light and gear leave it. 0 for a number the getter does not know or a character without stats.
    /// </summary>
    public static float Get(CharacterCold c, StatsEnumerated stat, bool unmodified)
    {
        if (c.Stats is not { } stats || (int)stat is <= 0 or > LastStat) return 0;

        float stored = stats[stat];
        if (stat is StatsEnumerated.Strength or StatsEnumerated.Dexterity) stored *= stats.Scale;
        if (unmodified) return stored;

        return Modified(c, stat, stored);
    }

    /// <summary>
    /// The modified getter's branches: stealth its own term x 100, perception with the worn gear's bonus, strength and dexterity without the light and
    /// dust terms, every other stat with all of them.
    /// </summary>
    static float Modified(CharacterCold c, StatsEnumerated stat, float stored)
    {
        if (stat == StatsEnumerated.Stealth) return Orders.Stealth(c) * 100;
        if (c.Medical is not { } medical) return stored;

        var factors = (c.Fighter?.Factors ?? StatFactors.None) with { Stun = medical.DustStatFactor };
        if (stat is StatsEnumerated.Strength or StatsEnumerated.Dexterity) return stored * medical.StatMultiplier(stat, BodyUse, factors);

        if (stat == StatsEnumerated.Perception) stored += c.Fighter?.PerceptionBonus ?? 0;
        return stored * medical.StatMultiplier(stat, StatUse.All, factors);
    }
}
