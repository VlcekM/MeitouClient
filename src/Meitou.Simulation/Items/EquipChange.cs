namespace Meitou.Simulation.Items;

/// <summary>
/// An item equipped into or unequipped from a character's equipment section (<see cref="IEquipObserver"/>): the tick, the character, the section
/// name (e.g. <c>head</c>, <c>body</c>, <c>primary</c>, <c>backpack_attach</c> or a mod slot), the item's record string id, and whether it was
/// equipped (true) or unequipped (false).
/// </summary>
public readonly record struct EquipChange(long Tick, CharacterId Character, string Section, string ItemRecord, bool Equipped);

/// <summary>
/// A system told whenever a character equips or unequips an item in any non-main inventory section (docs/modding.md "Character observers"). Called
/// serially in slot order once per tick after the slow world, or as the character leaves the world. It may read the world and update its own hashed
/// state, and must not mutate characters or other world state. One that throws is disabled for that world, and the fault enters the state hash.
/// Engine API, not in the original.
/// </summary>
public interface IEquipObserver : ITickSystem
{
    /// <summary>
    /// A character equipped or unequipped an item.
    /// </summary>
    void EquipChanged(World world, in EquipChange change);
}
