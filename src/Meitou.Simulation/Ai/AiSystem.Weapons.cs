using Meitou.Data.Fcs;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The AI's weapon handling: <c>Task_EquipBestWeapon</c>'s search of a character's inventory.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// The character assembly used for the weapon steps.
    /// </summary>
    readonly CharacterAssembly _assembly = new(data);

    /// <summary>
    /// The third section of <c>Task_EquipBestWeapon</c> (<c>FUN_140346c30</c> :91-118, <b>Verified</b>; docs/game/ai-tasks.md): when neither the back
    /// nor the hip gave a weapon of the task's kind and the character is not of a player faction, the first item of its inventory (in inventory
    /// order, the worn backpack's contents after the items) of the kind goes into its hand. The task flag is the weapon's ranged flag: 6 EQUIP_WEAPON
    /// takes a melee WEAPON, 264 EQUIP_CROSSBOW a CROSSBOW. The AI's usability test (vfunc <c>0x68</c>) is not decoded and counts as true. Meitou has
    /// no held-item pointer (the drawn weapon follows the back and hip sections), so the item goes into the first weapon section it fits
    /// (<see cref="InventoryCommands.TryEquip"/>) and the fighter is rebuilt; with both sections full nothing is replaced and the hand stays empty.
    /// True when a weapon came out.
    /// </summary>
    internal bool DrawFromInventory(World world, CharacterCold cold, bool ranged)
    {
        if (cold.Inventory is not { } inventory || CarrySystem.PlayerSide(world, cold)) return false;
        var kind = ranged ? FcsRecordType.Crossbow : FcsRecordType.Weapon;
        if (FirstOfKind(inventory, inventory, kind) is not var (owner, item)) return false;
        owner.Items.Remove(item);
        if (!InventoryCommands.TryEquip(inventory, item, cold))
        {
            owner.Items.Add(item);
            return false;
        }

        _assembly.RefreshEquipment(cold);
        return true;
    }

    /// <summary>
    /// The first item of <paramref name="kind"/> that is not on the back or hip and is a single one, in <paramref name="scan"/> or else in a bag
    /// inside it (depth first), with the inventory holding it; null when there is none.
    /// </summary>
    static (Inventory Owner, ItemInstance Item)? FirstOfKind(Inventory root, Inventory scan, FcsRecordType kind)
    {
        foreach (var item in scan.Items)
            if (item.Type == kind && item.Section is not ("back" or "hip") && item.Quantity == 1) return (scan, item);
        foreach (var item in scan.Items)
            if (item.Contents is { } bag && FirstOfKind(root, bag, kind) is { } found) return found;
        return null;
    }
}