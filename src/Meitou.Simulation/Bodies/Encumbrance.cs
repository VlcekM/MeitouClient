using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>Carrying capacity and the encumbrance factor (docs/game/character-stats.md "Encumbrance and carrying", <b>Observed</b>, <c>FUN_140884780</c>).</summary>
public static class Encumbrance
{
    /// <summary>
    /// Carried weight W: the inventory's total weight (weapons already scaled by <c>weapon inventory weight mult</c>, see <see cref="WeaponWeight"/>) plus
    /// <c>carry person weight</c> when carrying a person.
    /// </summary>
    public static float CarriedWeight(GameConstants c, float inventoryWeight, bool carryingPerson) =>
        inventoryWeight + (carryingPerson ? c.CarryPersonWeight : 0);

    /// <summary>A weapon's weight in the inventory total: <c>weapon inventory weight mult</c> (0.5) times its weight.</summary>
    public static float WeaponWeight(GameConstants c, float weaponWeight) => weaponWeight * c.WeaponInventoryWeightMult;

    /// <summary>
    /// Capacity C = <c>carry weight mult</c> x strength x strengthInjuryMultiplier + <c>encumbrance base</c>. <paramref name="strengthInjuryMultiplier"/> is the
    /// stat multiplier of strength (<see cref="MedicalState.StatMultiplier"/> with strength, 1 when unhurt).
    /// </summary>
    public static float Capacity(GameConstants c, float strength, float strengthInjuryMultiplier = 1) =>
        c.CarryWeightMult * strength * strengthInjuryMultiplier + c.EncumbranceBase;

    /// <summary>
    /// The encumbrance factor e = clamp((C - 0.05 W) / (0.95 W), 0, 1): 1 while W &lt;= C, about 0.47 at W = 2C, 0 at W = 20C. It multiplies run speed, raises the hunger rate
    /// (1 + (1 - e) x <c>encumbrance hunger rate</c>) and feeds strength and athletics XP.
    /// </summary>
    public static float Factor(float carriedWeight, float capacity)
    {
        if (carriedWeight <= 0) return 1;
        return Math.Clamp((capacity - 0.05f * carriedWeight) / (0.95f * carriedWeight), 0, 1);
    }

    /// <summary>The factor of a character: weight and capacity from its strength and injuries.</summary>
    public static float Factor(GameConstants c, float inventoryWeight, bool carryingPerson, float strength, float strengthInjuryMultiplier = 1) =>
        Factor(CarriedWeight(c, inventoryWeight, carryingPerson), Capacity(c, strength, strengthInjuryMultiplier));
}
