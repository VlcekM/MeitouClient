using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Combat;

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
}
