using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Combat;

/// <summary>
/// A character's combat rating (<see cref="Rating"/>).
/// </summary>
public sealed partial class CombatSystem
{
    /// <summary>
    /// A character's combat rating (the status float the original's per-update step <c>FUN_140852a30</c> writes, <b>Observed</b>): (strength + melee
    /// attack rating + melee defence rating) / 3, x 0.1 for a non-animal with no weapon equipped. What dialogue conditions 21 / 22 compare. Strength
    /// is the effective stat (<see cref="Eff"/>), the ratings <see cref="AttackRating"/> and <see cref="DefenceRating"/> without the guard term
    /// (<c>FUN_140886290</c> called with its flag clear, <b>Verified</b>).
    /// </summary>
    public static float Rating(World world, int slot)
    {
        if (world.Characters.Cold(slot) is not { Stats: not null, Medical: not null, Fighter: { } f } c) return 0;

        float strength = Eff(c, StatsEnumerated.Strength);
        float attack = AttackRating(world, slot, c, f);
        float defence = DefenceRating(world, slot, c, f, guard: false);
        float rating = (strength + attack + defence) / 3;
        bool armed = c.Inventory?.Items.Any(x => x.Type == FcsRecordType.Weapon && InventoryCommands.IsEquipmentSection(x.Section)) == true;

        return f.IsAnimal || armed ? rating : rating * 0.1f;
    }

    /// <summary>
    /// The melee attack skill the engine contests every blow with (<see cref="AttackRating"/>, CharStats <c>+0x120</c> path): unarmed the fist
    /// rating, else the trained melee attack plus the gear's attack bonus and the weapon's attack mod (indoors modifier inside), less the dust
    /// penalty; null for no fighter with a body in <paramref name="slot"/>. A pure read of world state the tick has settled, any thread
    /// (docs/modding.md "Combat reads").
    /// </summary>
    public static float? AttackSkill(World world, int slot) =>
        world.Characters.Cold(slot) is { Stats: not null, Medical: not null, Fighter: { } f } c ? AttackRating(world, slot, c, f) : null;

    /// <summary>
    /// The melee defence the block contest uses (<see cref="DefenceRating"/>): unarmed the fist rating, else the trained melee defence plus the
    /// gear's defence bonus, the weapon's defence mod, the indoors modifier and, with <paramref name="guard"/>, 20 while guarding; null as
    /// <see cref="AttackSkill"/>. Any thread, a pure read.
    /// </summary>
    public static float? MeleeDefence(World world, int slot, bool guard = true) =>
        world.Characters.Cold(slot) is { Stats: not null, Medical: not null, Fighter: { } f } c ? DefenceRating(world, slot, c, f, guard) : null;

    /// <summary>
    /// The effective dodge (the original's <c>CharStats::getDodge</c>, <c>FUN_140885190</c>, as the dodge experience reads it): the stored dodge
    /// plus the gear's dodge term (dodge x the gear's dodge multiplier - dodge), through the encumbrance, the legs' factor and, with
    /// <paramref name="guard"/>, the guard bonus for a guarding unarmed fighter; null as <see cref="AttackSkill"/>. Any thread, a pure read.
    /// </summary>
    public static float? EffectiveDodge(World world, int slot, bool guard = true) =>
        world.Characters.Cold(slot) is { Stats: not null, Medical: not null, Fighter: not null } c ? DodgeRating(c, guard) : null;

    /// <summary>
    /// The effective dodge of <paramref name="c"/> (<see cref="EffectiveDodge"/>); the guard term only for an unarmed fighter's guard.
    /// </summary>
    static float DodgeRating(CharacterCold c, bool guard)
    {
        var f = c.Fighter!;
        float dodge = c.Stats!.Stored(StatsEnumerated.Dodge);
        float gear = dodge * f.DodgeMult - dodge;
        bool guarding = guard && Guarding(c) && f.TechniqueCategory == (int)WeaponCategory.Unarmed;

        return DefenceFormulas.EffectiveDefence(dodge, f.EncumbranceFactor, Speed.LegFactor(c.Medical!), gear, guarding);
    }
}
