using System.Diagnostics.CodeAnalysis;
using Meitou.Data.Fcs;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Player;

/// <summary>
/// A right click on an item of a player character's own window (<c>FUN_140714a20</c>, <b>Observed</b>), one unit (all with
/// <see cref="QuickMoveItem.All"/>, repeated until a move fails): money becomes cats; a worn item goes to the main grid (else the open bag); a
/// grid item to the slot that takes it, else a prosthetic onto its stump, else from the main grid into the open bag, and a bag item to the main
/// grid. A blueprint of the player's faction is read (<see cref="Production.ProductionSystem.Learn"/>) and used up when it taught something; map
/// items are <see cref="ReadMapItem"/>.
/// </summary>
sealed class QuickMoves(PopulationData? data)
{
    /// <summary>
    /// Re-dresses a character whose worn items changed (null without game data).
    /// </summary>
    readonly CharacterAssembly? _assembly = data is null ? null : new(data);

    /// <summary>
    /// Applies a right click (<see cref="QuickMoveItem"/>).
    /// </summary>
    internal void QuickMove(World world, QuickMoveItem quick)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(quick.Character, out int s) || table.Cold(s) is not { IsPlayer: true, Inventory: { } root } cold)
            return;
        var item = root.All().ElementAtOrDefault(quick.Item.Index);
        if (!Clickable(root, item, quick.Item, out var holder)) return;
        if (item!.LockedOn)
        {
            world.Post(HeldItems.LockedText);
            return;
        }
        if (quick.Into != CharacterId.None)
        {
            QuickMoveInto(world, quick, s, cold, holder, item);
            return;
        }
        if (item.ItemFunction == PlayerSystem.MoneyFunction && cold.Faction == world.Player.Faction)
        {
            PlayerSystem.Cash(world, data, item, item.Quantity);
            holder.Items.Remove(item);
            return;
        }
        if (item.Type == FcsRecordType.Blueprint && cold.Faction == world.Player.Faction)
        {
            // One read whatever Shift says: a second unit would only say "Research already known".
            if (world.SystemOf<Production.ProductionSystem>()?.Learn(world, item) != true) return;

            root.RemoveOne(item);
            ItemUseObservers.Report(world, ItemUseKind.Blueprint, item.Record, table.IdOf(s), null, 1, true);
            return;
        }

        var bag = quick.BagOpen ? root.Backpack?.Contents : null;
        string from = item.Section;
        if (InventoryCommands.LimbSlotOf(from) is >= 0 and var fitted)
        {
            TakeOffLimb(cold, root, bag, holder, item, fitted);
            return;
        }
        MoveUnits(quick, cold, root, bag, holder, item, from);
    }

    /// <summary>
    /// Whether a right click may act on <paramref name="item"/> (a quick move, or a mod's item use, <see cref="ItemUses"/>): the item the token
    /// names, with the mod tag the window showed (a stale click never acts on a differently tagged item in its cell), in a movable or limb
    /// section, held by <paramref name="root"/> or its bag (<paramref name="holder"/>). The caller checks <see cref="ItemInstance.LockedOn"/>.
    /// </summary>
    internal static bool Clickable(Inventory root, ItemInstance? item, InventoryItemToken token, [NotNullWhen(true)] out Inventory? holder)
    {
        holder = null;
        if (!HeldItems.Matches(item, token) || item!.ModTag != token.ModTag) return false;
        if (!(InventoryCommands.IsMovableSection(item!.Section) || InventoryCommands.IsLimbSection(item.Section))) return false;

        holder = HeldItems.HolderOf(root, item);
        return holder is not null;
    }

    /// <summary>
    /// A fitted prosthetic comes off into the grid (else the open bag) when it fits there.
    /// </summary>
    void TakeOffLimb(CharacterCold cold, Inventory root, Inventory? bag, Inventory holder, ItemInstance item, int fitted)
    {
        if (cold.Medical?.LimbPart(fitted) is not { Limb: Bodies.LimbState.Replaced } part || part.ReplacementItem != item)
            return;
        holder.Items.Remove(item);
        if (!root.AddTo("main", item) && !(bag?.AddTo("backpack_content", item) ?? false))
        {
            holder.Items.Add(item);
            return;
        }
        cold.Medical.RemoveReplacement(fitted);
        _assembly?.RefreshEquipment(cold);
    }

    /// <summary>
    /// Moves the clicked units one at a time (<see cref="Step"/>) until all asked for moved or a move fails; the character is re-dressed when any
    /// moved.
    /// </summary>
    void MoveUnits(QuickMoveItem quick, CharacterCold cold, Inventory root, Inventory? bag, Inventory holder, ItemInstance item, string from)
    {
        bool worn = InventoryCommands.IsEquipmentSection(from);
        int want = quick.All ? item.Quantity : 1, moved = 0;
        while (moved < want)
        {
            bool last = item.Quantity <= 1;
            int at = holder.Items.IndexOf(item);
            if (at < 0) break;
            var unit = last ? item : item.WithQuantity(1);
            if (last) holder.Items.RemoveAt(at);
            if (!Step(unit, worn, cold, root, bag, from))
            {
                if (last) holder.Items.Insert(at, item);
                break;
            }
            if (!last) item.Quantity--;
            moved++;
        }
        if (moved > 0) _assembly?.RefreshEquipment(cold);
    }

    /// <summary>
    /// One unit's move out of <paramref name="from"/>: a worn one to the main grid (else the open bag, unless it is that bag); a grid one to the
    /// slot that takes it, a bag onto the back, a prosthetic onto its stump, a bag item to the main grid, a main grid item into the open bag (not
    /// a container holding items); a live animal's shut grid takes nothing. False when nothing took it.
    /// </summary>
    bool Step(ItemInstance unit, bool worn, CharacterCold cold, Inventory root, Inventory? bag, string from)
    {
        // A live animal's shut grid takes nothing (InventoryCommands.MainClosed): its window does not show it.
        bool shut = InventoryCommands.MainClosed(cold);

        // The worn bag never goes into its own open window (it would hold itself and be lost with its contents, #703): with no room in the
        // grid it stays on, as in the original.
        if (worn)
        {
            return !shut && root.AddTo("main", unit)
                || unit.Contents != bag && InventoryCommands.FitsInBag(unit) && (bag?.AddTo("backpack_content", unit) ?? false);
        }
        if (InventoryCommands.TryEquip(root, unit, cold)) return true;
        if (from == "main" && InventoryCommands.TryWearBag(root, unit, cold)) return true;
        if (unit.Type == FcsRecordType.LimbReplacement && data?.Items.Limb(unit.Record) is { LimbSlot: >= 0 and <= 3 } limb
            && cold.Medical?.LimbPart(limb.LimbSlot) is { Limb: Bodies.LimbState.Stump })
        {
            string section = limb.LimbSlot switch { 0 => "left_arm_replace", 1 => "right_arm_replace", 2 => "left_leg_replace",
                    _ => "right_leg_replace" };
            if (root.Items.Exists(i => i.Section == section)) return false;
            (unit.Section, unit.X, unit.Y) = (section, 0, 0);
            root.Items.Add(unit);
            cold.Medical.FitReplacement(limb, unit, unit.LimbDamage, unit.LimbWear, unit.LimbStun);
            (unit.LimbDamage, unit.LimbWear, unit.LimbStun) = (0, 0, 0);
            return true;
        }
        if (from == "backpack_content") return !shut && root.AddTo("main", unit);   // out of the open bag into the main grid

        return from == "main" && bag is not null && InventoryCommands.FitsInBag(unit) && bag.AddTo("backpack_content", unit);
    }

    /// <summary>
    /// A right click into another player character's open window (<c>FUN_140713d20</c>): units into its bag window first when open, then its slots,
    /// then its grid (<see cref="InventoryCommands.MoveInto"/>), money as one stack; only within the item move's reach of the clicked character
    /// (<see cref="ItemMoveReach.CharactersNear"/>, "Out of trading range."), as a drag between the two windows. A worn bag with contents stays.
    /// </summary>
    void QuickMoveInto(World world, QuickMoveItem quick, int source, CharacterCold from, Inventory holder, ItemInstance item)
    {
        var table = world.Characters;
        if (quick.Into == quick.Character || !table.TryResolveNext(quick.Into, out int t)
                || table.Cold(t) is not { IsPlayer: true, Inventory: { } into } taker)
            return;
        if (!InventoryCommands.IsMovableSection(item.Section) || item.Contents is not null && item.Section == "backpack_attach")
            return;
        if (!ItemMoveReach.CharactersNear(data, world, source, t)) return;
        bool worn = InventoryCommands.IsEquipmentSection(item.Section);
        if (InventoryCommands.MoveInto(holder, item, InventoryCommands.ClickCount(item, quick.All ? 0 : 1), into, quick.IntoBag, out bool wear,
                taker) == 0)
        {
            world.Post($"{world.ShownName(taker, ShownPlace.Notice)} has no room for {item.ShownName()}.");
            return;
        }
        if (worn) _assembly?.RefreshEquipment(from);
        if (wear) _assembly?.RefreshEquipment(taker);
    }
}
