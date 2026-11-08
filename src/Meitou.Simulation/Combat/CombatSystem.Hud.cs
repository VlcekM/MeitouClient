using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The combat numbers the HUD's stats tooltips show (<c>FUN_140892120</c>, docs/game/ui-screens.md section 4): the attack and block play rates (CharStats <c>+0x188</c> / <c>+0x18c</c>),
/// the weapon's cut and blunt damage at the fighter's skills (<c>FUN_140883ba0</c>, <c>FUN_140883c60</c>; fists' for an unarmed fighter) and the stats window's "Martial arts damage",
/// the four terms of the blow in hand (<c>FUN_1408868b0</c>, <see cref="CombatSystem.HudDamage"/>: the weapon's for an armed fighter).
/// </summary>
public readonly record struct HudCombat(float AttackRate, float BlockRate, float Cut, float Blunt, float UnarmedDamage, float Resistance)
{
    /// <summary>The damage it takes to stagger the fighter (<see cref="DamageFormulas.StumbleThreshold"/>, <c>FUN_140884020</c>).</summary>
    public float Stagger { get; init; }
}

public sealed partial class CombatSystem
{
    /// <summary>The <see cref="HudCombat"/> numbers of a fighter with stats and a body; null without them.</summary>
    internal HudCombat? HudNumbers(World world, int slot, CharacterCold cold)
    {
        if (cold is not { Fighter: { } fighter, Stats: { } stats, Medical: not null }) return null;
        float str = Eff(cold, StatsEnumerated.Strength), dex = Eff(cold, StatsEnumerated.Dexterity) * fighter.DexterityMult, tough = stats.ScaledToughness;
        var fists = DamageFormulas.Unarmed(UnarmedRating(world, slot, cold, fighter), tough, str, _combat);
        float cut = fists.Cut, blunt = fists.Blunt;
        if (!fighter.Unarmed)
        {
            float skill = Eff(cold, fighter.WeaponSkill);
            cut = DamageFormulas.Cut(skill, dex, fighter.Weapon!.Stats, _combat);
            blunt = DamageFormulas.Blunt(skill, str, fighter.Weapon.Stats, _combat);
        }
        return new HudCombat(Rate(world, slot, cold, true), Rate(world, slot, cold, false), cut, blunt, HudDamage(world, slot, cold), HitResolver.ToughnessResistance(tough, _combat))
        { Stagger = DamageFormulas.StumbleThreshold(tough, _combat) };
    }

    /// <summary>
    /// <c>Character::isInCombatMode(melee, ranged)</c> (<c>FUN_140436610</c>, docs/game/ui-screens.md section 4 "Combat mode"): with <paramref name="melee"/> the melee fight's flag
    /// (<see cref="MeleeCombatMode"/>), with <paramref name="ranged"/> the ranged controller's <c>combatMode</c> (<see cref="RangedCombatMode"/>) while its current target is up
    /// (not down: <c>Character</c> slot 6 <c>FUN_1405c9e10</c>, dead or unconscious, ragdolled or carried (<see cref="Ragdolls.RagdollSystem.IsRagdoll"/>), playing dead or KO).
    /// </summary>
    internal bool IsInCombatMode(CharacterTable table, CharacterId id, CharacterCold cold, bool melee, bool ranged)
    {
        if (melee && MeleeCombatMode(id, cold)) return true;
        if (!ranged || !RangedCombatMode(cold)) return false;
        int target = -1, generation = 0;
        if (cold.Gunning is { } key) { if (_turrets.TryGetValue(key, out var t)) (target, generation) = (t.Target, t.TargetGeneration); }
        else if (StateOf(id) is { TargetSlot: >= 0 } s) (target, generation) = (s.TargetSlot, s.TargetGeneration);
        return target >= 0 && table.TryResolveNext(new CharacterId(target, generation), out int slot) && table.Cold(slot) is { } tc
            && !CharacterFacts.Down(tc);
    }

    /// <summary>
    /// CombatClass <c>+0x130</c>: on from a melee fight task's start (<c>initCombatMode</c> mode 0) to its end (mode 2). Meitou's melee fight runs from the engagement or the switch from
    /// the crossbow to the fight's end or the switch to the crossbow: in a fight that is not a shooting one.
    /// </summary>
    internal bool MeleeCombatMode(CharacterId id, CharacterCold cold) => cold.InCombat && !StateOf(id).Ranged;

    /// <summary>
    /// The ranged controller's <c>combatMode</c> (<c>+0x2f0 → +0x36</c>): set by every update of the shooting task (<c>Task_RangedAttack</c>) and of turret manning (<c>Task_UseTurret</c>,
    /// which ends on a broken turret), cleared when either task ends: <see cref="CharacterCold.RangedCombatMode"/> (set by each shooting tick) or manning a turret this tick.
    /// </summary>
    internal static bool RangedCombatMode(CharacterCold cold) => cold.Gunning is not null || cold.RangedCombatMode;

    /// <summary>
    /// <c>CharStats::getGUIStatsDisplayMode</c> (<c>FUN_1408840d0</c>): the weapon in hand, else the back section's, else the hip's; none is 1 (Martial Arts); a crossbow in ranged mode
    /// while not moving (movement <c>+0x24</c>) is 2 (Crossbows, Precision, Perception); fists in hand (CharStats <c>+0x1f0</c> 5) in a melee fight is 1; else 0 (Attack, Defense).
    /// </summary>
    internal int StatsDisplayMode(CharacterId id, CharacterCold cold, bool moving)
    {
        if (cold.Fighter is not { } f) return 1;
        bool? crossbow = cold.DrawnWeapon != WeaponKinds.None ? cold.DrawnWeapon == BowKind
            : f.BackWeapon is not null ? false : f.Crossbow is { AtBack: true } ? true : f.HipWeapon is not null ? false : f.Crossbow is not null ? true : null;
        if (crossbow is null) return 1;
        if (crossbow.Value && Orders.RangedMode(cold) && !moving) return 2;
        return MeleeCombatMode(id, cold) && HandCategory(cold) == (int)WeaponCategory.Unarmed ? 1 : 0;
    }

    static readonly WeaponKinds BowKind = HandHold.OfCategory((int)WeaponCategory.Bow).Weapon;

    /// <summary>
    /// CharStats <c>+0x1f0 currentWeaponType</c>: the <c>skill category</c> of the weapon in hand (<c>CharStats::setWeapon</c> <c>FUN_140897f30</c> at a draw), 6 for a crossbow, 5 when
    /// nothing is drawn (fists).
    /// </summary>
    internal static int HandCategory(CharacterCold cold) =>
        cold.DrawnWeapon == WeaponKinds.None ? (int)WeaponCategory.Unarmed : cold.DrawnWeapon == BowKind ? (int)WeaponCategory.Bow
        : cold.Fighter?.Weapon?.Data.SkillCategory ?? (int)WeaponCategory.Unarmed;

    /// <summary>
    /// The weight <c>calculateWeaponWeightXPMult</c> (<c>FUN_1408838e0</c>) is given: the drawn weapon's item weight (<c>CharStats::setWeapon</c>, Item slot 74, <c>+0x120</c>), for a
    /// crossbow its <c>weight kg</c> x <c>weapon inventory weight mult</c>.
    /// </summary>
    internal static float HandWeight(CharacterCold cold) =>
        cold.DrawnWeapon == BowKind && cold.Fighter?.Crossbow is { } x ? x.Weight : cold.Fighter?.Weapon?.Stats.Weight ?? 0;
}