namespace Meitou.Data.Characters;

/// <summary>
/// The extra clothing rolls code mods' equipment slots ask for (docs/modding.md "Equipment slots", <c>ModSlot.RollFromClothing</c>): after the
/// game's own clothing slots, a generated character rolls one item from its CHARACTER's <c>clothing</c> list for each FCS <c>slot</c> here, in
/// registration order, worn in the slot's section. Filled at startup before the first world is built and never changed while one runs; empty
/// without mods, so generation rolls as the game does.
/// </summary>
public static class ClothingRolls
{
    /// <summary>
    /// The rolls in registration order: the FCS <c>slot</c> and the section the item is worn in.
    /// </summary>
    static readonly List<(int Slot, string Section)> Rolls = [];

    /// <summary>
    /// The rolls in registration order.
    /// </summary>
    public static IReadOnlyList<(int Slot, string Section)> All => Rolls;

    /// <summary>
    /// Adds a roll for FCS <c>slot</c> <paramref name="slot"/>, worn in <paramref name="section"/>.
    /// </summary>
    public static void Add(int slot, string section) => Rolls.Add((slot, section));

    /// <summary>
    /// Removes every roll (tests, and with the equipment slots).
    /// </summary>
    public static void Clear() => Rolls.Clear();
}
