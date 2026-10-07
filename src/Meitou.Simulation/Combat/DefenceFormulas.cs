using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>Block, dodge and skill-against-skill chances (docs/game/combat.md "Technique choice, block and dodge", <b>Observed</b>).</summary>
public static class DefenceFormulas
{
    /// <summary>The bonus to melee defence while guarding (the "Block" stance's +20; that this is the state of the HUD's Block button is <b>Unknown</b>).</summary>
    public const float GuardBonus = 20;

    /// <summary>
    /// The block chance in per cent (<c>FUN_1408862d0</c>) from the defender's defence D and the attacker's skill: d = D - skill; p = base + increase x d when d &gt; 0, else base + reduction x d
    /// (the increase and reduction are the stored per-level 1.2 and 1.5); above 90, p = 90 + 0.2 (p - 90); clamped to [5, 95]. <paramref name="min"/> and <paramref name="max"/> are the
    /// clamp: the variant used by unarmed fighters (<c>FUN_140885d80</c>) clamps to [0, 95].
    /// </summary>
    public static float BlockChance(float defence, float attackerSkill, CombatConstants c, float min = 5, float max = 95)
    {
        float d = defence - attackerSkill;
        float p = c.BaseBlockChance + (d > 0 ? c.BlockChanceIncreasePerLevel : c.BlockChanceReductionPerLevel) * d;
        if (p > 90) p = 90 + 0.2f * (p - 90);
        return Math.Clamp(p, min, max);
    }

    /// <summary>
    /// The defender's defence D (<c>FUN_1408862d0</c>): the melee defence stat (effective) plus the weapon's defence mod (dropped when the weapon cannot block; an unarmed fighter has none)
    /// plus the worn gear's equipment bonus, plus <see cref="GuardBonus"/> in a guarding state.
    /// </summary>
    public static float Defence(float meleeDefence, int weaponDefenceMod, bool weaponCanBlock, float equipmentBonus, bool guarding) =>
        meleeDefence + (weaponCanBlock ? weaponDefenceMod : 0) + equipmentBonus + (guarding ? GuardBonus : 0);

    /// <summary>
    /// The "effective defence" of the unarmed variant (<c>FUN_140885190</c>): the stat minus (1 - e) x stat minus (1 - l) x stat plus the multiplier term, with e the encumbrance factor clamped
    /// to [0.4, 1] and l the leg factor (the worse leg's health fraction x 1.8, clamped to [0, 1] by <c>FUN_140884970</c>, then to [0.5, 1] here). The multiplier term is not decoded:
    /// <paramref name="bonus"/> is added as given. Plus <see cref="GuardBonus"/> when guarding.
    /// </summary>
    public static float EffectiveDefence(float stat, float encumbrance, float legFactor, float bonus, bool guarding)
    {
        float e = Math.Clamp(encumbrance, 0.4f, 1f);
        float l = Math.Clamp(Math.Clamp(legFactor, 0f, 1f), 0.5f, 1f);
        return stat - (1 - e) * stat - (1 - l) * stat + bonus + (guarding ? GuardBonus : 0);
    }

    /// <summary>The probability that <see cref="SkillRoll"/> is true (derived by hand from the comparison): with k = B / A, 1 - 1 / (2k) for k &gt;= 1, else k / 2. 0.5 at equal skill.</summary>
    public static float SkillRollChance(float skill1, float skill2, CombatConstants c)
    {
        float f = c.AttackChanceFactor;
        float a = 1 + f * MathF.Max(0, skill2 - skill1);
        float b = 1 + f * MathF.Max(0, skill1 - skill2);
        float k = b / a;
        return k >= 1 ? 1 - 1 / (2 * k) : k / 2;
    }

    /// <summary>The skill-against-skill roll (<c>FUN_140665370</c>) for two uniform randoms: A = 1 + F max(0, s2 - s1), B = 1 + F max(0, s1 - s2); true when r1 x A &lt; r2 x B.</summary>
    public static bool SkillRoll(float skill1, float skill2, float r1, float r2, CombatConstants c)
    {
        float f = c.AttackChanceFactor;
        float a = 1 + f * MathF.Max(0, skill2 - skill1);
        float b = 1 + f * MathF.Max(0, skill1 - skill2);
        return r1 * a < r2 * b;
    }

    /// <summary>The dodge skill (<c>FUN_140885ce0</c>): 0.01 x (0.6 x the gear's dodge value + 0.4 x dexterity x scale).</summary>
    public static float DodgeSkill(float equipmentDodge, float dexterity) => 0.01f * (0.6f * equipmentDodge + 0.4f * dexterity);
}
