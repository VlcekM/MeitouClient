using Meitou.Simulation.Population;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// What <see cref="CharacterRefresh.Refresh"/> makes again from a character's state.
/// </summary>
public enum CharacterRefreshKind
{
    /// <summary>
    /// The fighter from the inventory (weapons, armour, guard and encumbrance kept), the drawn weapon following the new kit, the worn gear's
    /// colours and the look: what the engine does after every change of worn items.
    /// </summary>
    Equipment,

    /// <summary>
    /// The look alone, from the body, worn kit, limbs and shave (an animal's from the bag it wears).
    /// </summary>
    Look,
}

/// <summary>
/// Makes a character's derived state again after a code mod changed what it is made from (docs/modding.md "Character refresh"): the engine's own
/// paths that move worn items call the same refresh. Serial phases only (a system's <c>Inputs</c>, serial step, <c>IWorldReady</c>, a command
/// handler); it changes the character and reads the merged data.
/// </summary>
public static class CharacterRefresh
{
    /// <summary>
    /// One refresher per population data (the assembly keeps race caches); made on first use.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PopulationData, CharacterAssembly> Assemblies = [];

    /// <summary>
    /// Makes <paramref name="what"/> of <paramref name="cold"/> again from <paramref name="data"/> (a mod's <c>context.Population</c>); a character
    /// without an inventory is left as it is.
    /// </summary>
    public static void Refresh(PopulationData data, CharacterCold cold, CharacterRefreshKind what)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(cold);
        var assembly = Assemblies.GetValue(data, d => new CharacterAssembly(d));
        switch (what)
        {
            case CharacterRefreshKind.Equipment:
                assembly.RefreshEquipment(cold);
                break;
            case CharacterRefreshKind.Look:
                assembly.RefreshLook(cold);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(what), what, "not a refresh kind");
        }
    }
}
