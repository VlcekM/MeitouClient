using System.Numerics;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Player;

/// <summary>
/// The item on the mouse (docs/game/ui-screens.md section 6 "Holding an item"; the original's <c>MouseInventory</c>, <b>Observed</b> by decompile):
/// one item at a time is lifted out of a window (<see cref="LiftItem"/>), then placed in a window's section (<see cref="PlaceHeld"/>: moved, merged
/// onto the one stack under it, or swapped with the one item under it, which takes its place on the mouse), given to a character in the world
/// (<see cref="GiveHeld"/>), dropped on the ground (<see cref="DropHeld"/>) or put back (<see cref="ReturnHeld"/>). Lifting from the trader's stock
/// buys nothing: the purchase happens when it is placed in the customer's window, priced then; a sale when the customer's item is placed on the
/// stock. The item is world state (<see cref="PlayerState.Held"/>); it goes back when its window closes or its holder goes down
/// (<see cref="KeepHeld"/>) and before a save (<see cref="World.ReturnHeldItem"/>). The <see cref="PlayerSystem"/> owns it; the crime side of a
/// move is <see cref="HeldItemTheft"/>.
/// </summary>
sealed class HeldItems(PopulationData? data)
{
    /// <summary>
    /// How far a held item reaches another owner (<c>FUN_14070d290</c>, <b>Observed</b>): within 1000 units normally, 60 when it is being stolen (a
    /// non-player window's).
    /// </summary>
    public const float GiveRange = 1000, StealRange = 60;

    /// <summary>
    /// The message for an item locked on its wearer, lifted or right-clicked.
    /// </summary>
    internal const string LockedText =
        "This item is locked in place and cannot be removed. You can try to pick the lock, find a saw, or use brute strength.";

    /// <summary>
    /// Re-dresses a character whose worn items changed (null without game data).
    /// </summary>
    readonly CharacterAssembly? _assembly = data is null ? null : new(data);

    /// <summary>
    /// Applies a held-item command (<see cref="HeldCommand"/>), noting its serial.
    /// </summary>
    internal void Input(World world, HeldCommand command)
    {
        world.Player.HeldSerial = command.Serial;
        switch (command)
        {
            case LiftItem lift: Lift(world, lift);
            break;
            case PlaceHeld place: Place(world, place);
            break;
            case GiveHeld give: Give(world, give);
            break;
            case DropHeld: DropHeldItem(world);
            break;
            case ReturnHeld: ReturnHeldItem(world);
            break;
        }
    }

    #region Windows and items

    /// <summary>
    /// The inventory of a character window a held item may come out of or go into: one of the player's characters, or the open loot's body when
    /// <paramref name="actor"/> is its looter.
    /// </summary>
    static Inventory? CharacterWindow(World world, CharacterId who, CharacterId actor, out CharacterCold? cold)
    {
        cold = null;
        if (!world.Characters.TryResolveNext(who, out int slot) || world.Characters.Cold(slot) is not { Inventory: { } inventory } c)
            return null;
        if (!c.IsPlayer && !(world.SystemOf<Carrying.CarrySystem>()?.OpenLoot(world) is { } open && open.Body == who && open.Looter == actor))
            return null;
        cold = c;
        return inventory;
    }

    /// <summary>
    /// The inventory directly holding <paramref name="item"/>: the root, or the worn bag's contents.
    /// </summary>
    internal static Inventory? HolderOf(Inventory root, ItemInstance item) =>
        root.Items.Contains(item) ? root : root.Backpack?.Contents is { } bag && bag.Items.Contains(item) ? bag : null;

    /// <summary>
    /// Whether <paramref name="item"/> is the one the window's token names (its record, cell, quantity and quality, and with
    /// <paramref name="section"/> its section).
    /// </summary>
    internal static bool Matches(ItemInstance? item, InventoryItemToken t, bool section = true) => item is not null && item.Record == t.Record
        && (!section || item.Section == t.Section)
        && item.X == t.X && item.Y == t.Y && item.Quantity == t.Quantity && item.Quality == t.Quality;

    /// <summary>
    /// <paramref name="units"/> of <paramref name="item"/>: the item itself when that is all of it, else a split-off copy of full units (the stack
    /// keeps its partly used one).
    /// </summary>
    static ItemInstance Part(ItemInstance item, int units)
    {
        if (units >= item.Quantity) return item;
        var part = item.WithQuantity(units);   // the first units, with their receipt's first coins
        item.Quantity -= units;
        return part;
    }

    /// <summary>
    /// A machine window's input slot section (<c>in1</c>, <c>in2</c>, the layout's names) as an input index; -1 for any other.
    /// </summary>
    static int InputIndex(string section) => section is ['i', 'n', var d] && d is >= '1' and <= '9' ? d - '1' : -1;

    #endregion

    #region Lift

    /// <summary>
    /// What a lift takes out of <paramref name="holder"/> (<c>FUN_1407139a0</c>): the whole stack when <paramref name="whole"/> or a single unit,
    /// else one unit.
    /// </summary>
    static ItemInstance Split(Inventory holder, ItemInstance item, bool whole)
    {
        if (whole || item.Quantity <= 1)
        {
            holder.Items.Remove(item);
            return item;
        }
        return Part(item, 1);
    }

    /// <summary>
    /// Puts <paramref name="unit"/> on the mouse, remembering where it came from.
    /// </summary>
    static void Hold(World world, LiftItem lift, ItemInstance unit, Inventory? grid, string section, int x, int y) =>
        world.Player.Held = new HeldItem { Item = unit, From = lift.From, Grid = grid, Section = section, X = x, Y = y, Actor = lift.Actor };

    /// <summary>
    /// Lifts an item out of a character window, the trader's stock or a site; nothing while one is held.
    /// </summary>
    void Lift(World world, LiftItem lift)
    {
        if (world.Player.Held is not null) return;
        switch (lift.From.Holder)
        {
            case ItemHolder.Character:
                LiftFromCharacter(world, lift);
                return;
            case ItemHolder.Stock:
                LiftFromStock(world, lift);
                return;
            case ItemHolder.Site:
                LiftFromSite(world, lift);
                return;
        }
    }

    /// <summary>
    /// A lift out of a character window: a movable or limb section's item that is not locked on; a fitted prosthetic leaves a stump.
    /// </summary>
    void LiftFromCharacter(World world, LiftItem lift)
    {
        var t = lift.Item;
        if (CharacterWindow(world, lift.From.Character, lift.Actor, out var cold) is not { } root) return;
        var item = root.All().ElementAtOrDefault(t.Index);
        if (!Matches(item, t) || !(InventoryCommands.IsMovableSection(item!.Section) || InventoryCommands.IsLimbSection(item.Section))
                || HolderOf(root, item) is not { } holder)
            return;
        if (item.LockedOn)
        {
            world.Post(LockedText);
            return;
        }
        if (InventoryCommands.LimbSlotOf(item.Section) is >= 0 and var limb)
        {
            // A fitted prosthetic comes off: the limb is a stump while it is held.
            if (cold!.Medical?.LimbPart(limb) is not { Limb: Bodies.LimbState.Replaced } part || part.ReplacementItem != item)
                return;
            cold.Medical.RemoveReplacement(limb);
        }
        var (section, x, y) = (item.Section, item.X, item.Y);
        Hold(world, lift, Split(holder, item, lift.Whole), root, section, x, y);
        if (InventoryCommands.IsEquipmentSection(section) || InventoryCommands.IsLimbSection(section))
            _assembly?.RefreshEquipment(cold!);
    }

    /// <summary>
    /// A lift out of the open trade's stock, by its customer only.
    /// </summary>
    static void LiftFromStock(World world, LiftItem lift)
    {
        var t = lift.Item;
        if (world.SystemOf<Trading.TradeSystem>()?.OpenStock(world, out var buyer) is not { } stock || buyer != lift.Actor)
            return;
        var item = stock.All().ElementAtOrDefault(t.Index);
        if (!Matches(item, t) || HolderOf(stock, item!) is not { } holder) return;
        Hold(world, lift, Split(holder, item!, lift.Whole), stock, item!.Section, item.X, item.Y);
    }

    /// <summary>
    /// A lift out of an open container, a storage or bench grid (<c>main</c>), a machine's input (<c>in1</c>, <c>in2</c>) or its output or crafted
    /// items (<c>out</c>).
    /// </summary>
    void LiftFromSite(World world, LiftItem lift)
    {
        var t = lift.Item;
        string site = lift.From.Site;
        if (ContainerAccess.ContainerFor(world, site, lift.Actor, out var opener) is { } container)
        {
            if (opener is null || world.Player.OpenContainer != site) return;
            var grid = ContainerAccess.GridOf(world, container, out var region);
            var item = grid.Items.ElementAtOrDefault(t.Index);
            if (!Matches(item, t with { X = t.X + region.X, Y = t.Y + region.Y }, section: false)
                    || !region.Holds(item!)) return;   // the window shows cells from the container's corner
            Hold(world, lift, Split(grid, item!, lift.Whole), grid, "main", item!.X, item.Y);
            world.Machines.Touch();
            return;
        }
        if (world.SystemOf<Production.ProductionSystem>()?.Reach(world, lift.Actor, site, out _) is not { } m) return;
        LiftFromMachine(world, lift, m);
    }

    /// <summary>
    /// A lift out of a machine in reach: its storage grid, its crafted items or its input or output counts (a new item of the slot's record); its
    /// window is redrawn after a lift.
    /// </summary>
    void LiftFromMachine(World world, LiftItem lift, Production.Machine m)
    {
        var t = lift.Item;
        if (t.Section == "main")
        {
            if (m.Storage is not { } storage || m.Closed || storage.Items.ElementAtOrDefault(t.Index) is not { } item
                    || !Matches(item, t, section: false))
                return;
            Hold(world, lift, Split(storage, item, lift.Whole), storage, "main", item.X, item.Y);
        }
        else if (t.Section == "out" && m.Craft is { } craft)
        {
            if (craft.Done.ElementAtOrDefault(t.Index) is not { } item || item.Record != t.Record) return;
            if (lift.Whole || item.Quantity <= 1) craft.Done.Remove(item);
            Hold(world, lift, lift.Whole || item.Quantity <= 1 ? item : Part(item, 1), null, "out", t.Index, 0);
        }
        else
        {
            int k = InputIndex(t.Section);
            (string Item, int Count) slot = t.Section == "out" ? (m.Output?.Recipe.Item ?? "",
                    m.Output?.Count ?? 0) : k >= 0 && k < m.Inputs.Length ? (m.Inputs[k].Item, m.Inputs[k].Count) : ("", 0);
            if (slot.Count <= 0 || slot.Item != t.Record) return;
            int n = lift.Whole ? slot.Count : 1;
            if (data?.Items.Create(slot.Item, n) is not { } made) return;
            if (t.Section == "out") m.Output!.Count -= n;
            else
                m.Inputs[k].Count -= n;
            Hold(world, lift, made, null, t.Section, 0, 0);
        }
        world.Machines.Touch();
    }

    #endregion

    #region Place

    /// <summary>
    /// Where a held item is placed: the window's root inventory, the inventory holding the section (the worn bag's for
    /// <c>backpack_content</c>), the section's size in cells
    /// (0: one item whatever its footprint), and the window's character, container or machine.
    /// </summary>
    readonly record struct Target(Inventory Root, Inventory Grid, string Section, int Columns, int Rows, CharacterCold? Owner,
            Locks.Fixture? Container, Production.Machine? Machine);

    /// <summary>
    /// A placement past its checks: the held item, its target and cell, the units the target's stack takes (<see cref="Inventory.LimitOf"/>), the
    /// trade it is part of (<see cref="Buy"/> / <see cref="Sell"/>, else none), whether it leaves its window and the item's death and stolen marks
    /// from before, which a refusal and the units staying on the mouse keep.
    /// </summary>
    readonly record struct Placing(World World, HeldItem Held, ItemOwner Into, Target Target, int X, int Y, int Limit, bool Buy, bool Sell,
            Trading.TradeSystem? Trade, bool Changes, bool Death, StolenMark Mark);

    /// <summary>
    /// Places the held item (<c>FUN_140715560</c>, <b>Observed</b>): the section must take it
    /// (<see cref="InventoryCommands.SlotRefusal(string, Data.Fcs.FcsRecordType, string, Inventory?, bool)"/>, a worn slot only what the owner's
    /// race may wear, a limited storage its items, a prosthetic slot a stump of its side); the cell is the drop's, moved inside the section (a
    /// footprint larger than the section is refused); two or more items under it refuse it; one locked on refuses it ("Item is locked in place.");
    /// one of the same kind takes what its stack has room for (the rest stays on the mouse; a full stack takes nothing); any other is swapped.
    /// Between the stock and the customer the units are capped to what the payer can afford and paid; a stolen item put on a stock that is no fence
    /// may get the seller caught. A refusal puts the item back.
    /// </summary>
    void Place(World world, PlaceHeld place)
    {
        var player = world.Player;
        if (player.Held is not { } held) return;
        if (place.Into.Holder == ItemHolder.Site && place.Section != "main")
        {
            PlaceInSlot(world, held, place);
            return;
        }
        var into = place.Into;
        var item = held.Item;
        bool buy = held.From.Holder == ItemHolder.Stock && into.Holder != ItemHolder.Stock;
        bool sell = held.From.Holder != ItemHolder.Stock && into.Holder == ItemHolder.Stock;
        var trade = world.SystemOf<Trading.TradeSystem>();
        if (Reached(world, held, place, buy, sell, trade) is not { } target)
        {
            ReturnHeldItem(world);
            return;
        }
        if (Refusal(target, item) is { } refusal)
        {
            if (refusal.Length > 0) world.Post(refusal);
            ReturnHeldItem(world);
            return;
        }

        // Money moved out of another's window into the player's own becomes cats.
        if (item.ItemFunction == PlayerSystem.MoneyFunction && target.Owner is { IsPlayer: true } && !HeldItemTheft.FromPlayerWindow(world, held)
                && !buy)
        {
            PlayerSystem.Cash(world, data, item, item.Quantity);
            player.Held = null;
            HeldItemTheft.LeftLoot(world, held, item.Death);
            return;
        }

        if (Under(world, target, item, place, out int x, out int y) is not { Count: < 2 } under)
        {
            ReturnHeldItem(world);
            return;
        }
        if (under is [{ LockedOn: true }])
        {
            world.Post("Item is locked in place.");
            ReturnHeldItem(world);
            return;
        }
        if (into != held.From && HeldItemTheft.Caught(world, held, Crime.CrimeSystem.TakeKind.Drag))
        {
            ReturnHeldItem(world);
            return;
        }

        int limit = target.Columns == 0 ? 1 : target.Grid.LimitOf(item);
        // Leaving its window for another owner's: no longer a death item, maybe stolen. Put back on a refusal; the units that stay on the mouse keep
        // the old marks.
        bool changes = into != held.From, death = item.Death;
        var mark = item.Mark;
        if (changes) (item.Death, item.Mark) = (false, HeldItemTheft.MarkFor(world, held, target.Owner, overwrite: false));
        var p = new Placing(world, held, into, target, x, y, limit, buy, sell, trade, changes, death, mark);
        if (under is [var other])
        {
            if (target.Columns > 0 && limit > 1 && InventoryCommands.Stackable(item, other)) Merge(in p, other);
            else
                Swap(in p, other);
            return;
        }
        PutUnits(in p);
    }

    /// <summary>
    /// The target of a placement the trade allows; null (the item goes back) when it refuses. The stock trades with its customer only: stock goes
    /// into the customer's windows, the customer's own items onto the stock; a player's other character window takes the trade over in reach.
    /// </summary>
    Target? Reached(World world, HeldItem held, PlaceHeld place, bool buy, bool sell, Trading.TradeSystem? trade)
    {
        var into = place.Into;
        CharacterId customer = CharacterId.None;
        if ((buy || sell) && trade?.OpenStock(world, out customer) is null) return null;
        if ((buy || sell) && !PassTrade(world, trade!, buy ? into : held.From, ref customer)) return null;
        if (buy && into != ItemOwner.Of(customer) || sell && held.From != ItemOwner.Of(customer) || Resolve(world, held, place) is not { } target)
            return null;
        return target;
    }

    /// <summary>
    /// The window section a placement goes into: a character window's grid, bag or slot, the stock, a container's or a storage machine's grid; null
    /// for any other.
    /// </summary>
    static Target? Resolve(World world, HeldItem held, PlaceHeld place)
    {
        var into = place.Into;
        switch (into.Holder)
        {
            case ItemHolder.Character:
                {
                    if (CharacterWindow(world, into.Character, held.Actor, out var cold) is not { } root) return null;
                    if (place.Section == "backpack_content")
                        return root.Backpack?.Contents is { } bag ? new(root, bag, place.Section, bag.Width, bag.Height, cold, null, null) : null;
                    if (place.Section == "main")
                        return new(root, root, place.Section, root.Width, root.Height, cold, null, null);
                    if (!InventoryCommands.IsEquipmentSection(place.Section) && !InventoryCommands.IsLimbSection(place.Section))
                        return null;
                    var (w, h) = InventoryCommands.IsSingleSlot(place.Section) ? (0, 0) : InventoryCommands.SectionSize(place.Section);
                    return new(root, root, place.Section, w, h, cold, null, null);
                }
            case ItemHolder.Stock:
                return place.Section == "main" && world.SystemOf<Trading.TradeSystem>()?.OpenStock(world,
                        out var buyer) is { } stock && buyer == held.Actor
                    ? new(stock, stock, "main", stock.Width, stock.Height, null, null, null) : null;
            default:
                if (place.Section != "main") return null;
                if (ContainerAccess.ContainerFor(world, into.Site, held.Actor, out var opener) is { } container)
                    return opener is not null && world.Player.OpenContainer == into.Site && ContainerAccess.ShopOf(world,
                            container) is null ? new(ContainerAccess.GridOf(world, container), ContainerAccess.GridOf(world, container), "main",
                                    ContainerAccess.GridOf(world, container).Width, ContainerAccess.GridOf(world, container).Height, null, container,
                                    null) : null;
                return world.SystemOf<Production.ProductionSystem>()?.Reach(world, held.Actor, into.Site, out _) is { Storage: { } storage,
                        Closed: false } m
                    ? new(storage, storage, "main", storage.Width, storage.Height, null, null, m) : null;
        }
    }

    /// <summary>
    /// A player's character window other than the customer's takes the trade over when in reach; false (after "Out of trading range.") when it is too
    /// far.
    /// </summary>
    static bool PassTrade(World world, Trading.TradeSystem trade, ItemOwner window, ref CharacterId customer)
    {
        if (window.Holder != ItemHolder.Character || window.Character == customer) return true;
        if (!world.Characters.TryResolveNext(window.Character, out int w) || world.Characters.Cold(w) is not { IsPlayer: true })
            return true;
        if (!trade.TradeWith(world, window.Character)) return false;
        customer = window.Character;
        return true;
    }

    /// <summary>
    /// Why the target's section refuses the item (posted unless empty), or null when it takes it.
    /// </summary>
    string? Refusal(Target target, ItemInstance item)
    {
        string? refusal = InventoryCommands.SlotRefusal(target.Section, item.Type, item.EquipmentSection, item.Contents,
                item.WearableBy(target.Owner?.Race?.StringId, target.Owner is { } wearer && SquadRegistry.IsAnimal(wearer)));
        if (refusal is null && InventoryCommands.SectionClosed(target.Owner, target.Section))
            refusal = "Can't wear that item.";
        if (refusal is null && target.Section == "main"
                && InventoryCommands.MainClosed(target.Owner)) refusal = "";   // a live animal's grid (the original leaves it out of the window)
        if (refusal is null && target.Machine is { Data.IsStorage: true } storage && !storage.Data.TakesByHand(item.Record, item.Type))
            refusal = "Invalid container type.";
        if (refusal is null && InventoryCommands.LimbSlotOf(target.Section) is >= 0 and var limbSlot
            && (data?.Items.Limb(item.Record)?.LimbSlot != limbSlot
                    || target.Owner?.Medical?.LimbPart(limbSlot) is not { Limb: Bodies.LimbState.Stump or Bodies.LimbState.Replaced }))
            refusal = "Can't wear that item.";   // a prosthetic of the slot's side, on a stump (or swapped with the one fitted there)
        return refusal;
    }

    /// <summary>
    /// The items under the held item in the target's section (a slot: whatever is in it) at the cell it lands at; null (the item goes back) when
    /// its footprint is larger than the section or no shop container has room ("No room for that item.").
    /// </summary>
    static List<ItemInstance>? Under(World world, Target target, ItemInstance item, PlaceHeld place, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (target.Columns == 0) return target.Grid.Items.Where(i => i.Section == target.Section).ToList();
        if (item.Width > target.Columns || item.Height > target.Rows) return null;
        x = Math.Clamp(place.X, 0, target.Columns - item.Width);
        y = Math.Clamp(place.Y, 0, target.Rows - item.Height);
        // On a shop's greyed cells, or across two of its containers: the nearest free cell of a container instead (engine choice, #349).
        if (!target.Grid.InRegion(x, y, item.Width, item.Height))
        {
            if (NearestFreeInRegion(target, item, x, y) is not var (fx, fy))
            {
                world.Post("No room for that item.");
                return null;
            }
            (x, y) = (fx, fy);
        }
        int cx = x, cy = y;
        return target.Grid.Items.Where(i => i.Section == target.Section && cx < i.X + i.Width && i.X < cx + item.Width && cy < i.Y + i.Height
                && i.Y < cy + item.Height).ToList();
    }

    /// <summary>
    /// The free cell nearest (<paramref name="x"/>, <paramref name="y"/>) where <paramref name="item"/> lies inside one of the grid's regions; null
    /// when none has room.
    /// </summary>
    static (int X, int Y)? NearestFreeInRegion(Target target, ItemInstance item, int x, int y)
    {
        (int X, int Y)? best = null;
        int bestDistance = int.MaxValue;
        for (int cy = 0; cy + item.Height <= target.Rows; cy++)
        {
            for (int cx = 0; cx + item.Width <= target.Columns; cx++)
            {
                int distance = (cx - x) * (cx - x) + (cy - y) * (cy - y);
                if (distance >= bestDistance || !target.Grid.InRegion(cx, cy, item.Width, item.Height)) continue;

                bool taken = target.Grid.Items.Any(i => i.Section == target.Section && cx < i.X + i.Width && i.X < cx + item.Width
                        && cy < i.Y + i.Height && i.Y < cy + item.Height);
                if (taken) continue;

                (best, bestDistance) = ((cx, cy), distance);
            }
        }
        return best;
    }

    /// <summary>
    /// Puts the placement's old marks back on <paramref name="i"/>.
    /// </summary>
    static void Undo(in Placing p, ItemInstance i) => (i.Death, i.Mark) = (p.Death, p.Mark);

    /// <summary>
    /// A refused placement: the old marks back on the held item, and it goes back.
    /// </summary>
    void Refuse(in Placing p)
    {
        Undo(in p, p.Held.Item);
        ReturnHeldItem(p.World);
    }

    /// <summary>
    /// The units the trade lets through (all of them outside a trade); 0: refused, put back.
    /// </summary>
    static int Exchange(in Placing p, int units, bool all)
    {
        var world = p.World;
        var item = p.Held.Item;
        if (!p.Buy && !p.Sell) return units;
        if (item.Contents is { Items.Count: > 0 })
        {
            world.Post(Trading.TradeSystem.BagNotEmpty);
            return 0;
        }
        if (p.Sell && p.Trade!.CaughtSelling(world, item)) return 0;
        int k = p.Trade!.AffordHeld(world, item, units, p.Sell);
        if (all && k is > 0 && k < units)
        {
            world.Post(p.Sell ? "The shopkeeper can't afford that." : "You can't afford that.");
            return 0;
        }
        return k;
    }

    /// <summary>
    /// The held item onto the one stack of its kind under it: what the stack has room for (a full stack takes nothing and the item stays on the
    /// mouse), paid in a trade; the merged stack lies at the drop's cell.
    /// </summary>
    void Merge(in Placing p, ItemInstance other)
    {
        var (world, held, target) = (p.World, p.Held, p.Target);
        var item = held.Item;
        bool buy = p.Buy, sell = p.Sell;
        int room = Math.Min(p.Limit - other.Quantity, item.Quantity);
        if (room <= 0)
        {
            Undo(in p, item);   // a full stack: the item stays on the mouse
            return;
        }
        int n = Exchange(in p, room, all: false);
        if (n <= 0)
        {
            Refuse(in p);
            return;
        }
        // Units of the same trade and direction keep their coins in the joined stack.
        var (kept, coming) = (other.Buyback, buy || sell ? p.Trade!.PayHeld(world, item, n, sell) : item.Buyback?.Take(n));
        InventoryCommands.Merge(other, item, n);
        if (kept is not null && coming is not null && kept.Joins(coming)) other.Buyback = kept.Append(coming);
        // The merged stack lies at the drop's cell.
        (other.X, other.Y) = (p.X, p.Y);
        if (item.Quantity <= 0) world.Player.Held = null;
        else
            Undo(in p, item);
        if (p.Changes) HeldItemTheft.LeftLoot(world, held, p.Death);
        Touched(world, held, target);
    }

    /// <summary>
    /// A swap: the held item takes the other's place, the other goes onto the mouse.
    /// </summary>
    void Swap(in Placing p, ItemInstance other)
    {
        var (world, held, target) = (p.World, p.Held, p.Target);
        var item = held.Item;
        bool buy = p.Buy, sell = p.Sell;
        if (item.Quantity > p.Limit || Exchange(in p, item.Quantity, all: true) <= 0)
        {
            Refuse(in p);
            return;
        }
        if (buy || sell) item.Buyback = p.Trade!.PayHeld(world, item, item.Quantity, sell);
        target.Grid.Items.Remove(other);
        if (InventoryCommands.LimbSlotOf(target.Section) is >= 0 and var fitted)
            target.Owner?.Medical?.RemoveReplacement(fitted);
        Put(target, item, p.X, p.Y);
        world.Player.Held = new HeldItem { Item = other, From = p.Into, Grid = target.Root, Section = other.Section, X = other.X, Y = other.Y,
                Actor = held.Actor };
        if (p.Changes) HeldItemTheft.LeftLoot(world, held, p.Death);
        Touched(world, held, target);
    }

    /// <summary>
    /// The held item onto free cells: the units the section's stack and the trade let through, the rest staying on the mouse.
    /// </summary>
    void PutUnits(in Placing p)
    {
        var (world, held, target) = (p.World, p.Held, p.Target);
        var item = held.Item;
        bool buy = p.Buy, sell = p.Sell;
        int n = Exchange(in p, Math.Min(p.Limit, item.Quantity), all: false);
        if (n <= 0)
        {
            Refuse(in p);
            return;
        }
        var receipt = buy || sell ? p.Trade!.PayHeld(world, item, n, sell) : null;
        var unit = Part(item, n);
        if (buy || sell) unit.Buyback = receipt;
        Put(target, unit, p.X, p.Y);
        if (ReferenceEquals(unit, item)) world.Player.Held = null;
        else
            Undo(in p, item);
        if (p.Changes) HeldItemTheft.LeftLoot(world, held, p.Death);
        Touched(world, held, target);
    }

    /// <summary>
    /// Puts an item into the target's section at (<paramref name="x"/>, <paramref name="y"/>); a prosthetic is fitted on its stump.
    /// </summary>
    void Put(Target target, ItemInstance item, int x, int y)
    {
        (item.Section, item.X, item.Y) = (target.Section, x, y);
        target.Grid.Items.Add(item);
        if (InventoryCommands.LimbSlotOf(target.Section) >= 0 && target.Owner?.Medical is { } medical && data?.Items.Limb(item.Record) is { } limb)
        {
            medical.FitReplacement(limb, item, item.LimbDamage, item.LimbWear, item.LimbStun);
            (item.LimbDamage, item.LimbWear, item.LimbStun) = (0, 0, 0);
        }
    }

    /// <summary>
    /// After a placement: a worn section re-dresses its character; a machine or container's window is redrawn.
    /// </summary>
    void Touched(World world, HeldItem held, Target target)
    {
        if (target.Owner is { } owner && (InventoryCommands.IsEquipmentSection(target.Section) || InventoryCommands.IsLimbSection(target.Section)))
            _assembly?.RefreshEquipment(owner);
        if (target.Container is not null || target.Machine is not null || held.From.Holder == ItemHolder.Site)
            world.Machines.Touch();
    }

    /// <summary>
    /// The held item onto a machine's input slot (<c>in1</c>, <c>in2</c>): the input of its record takes what it has room for, the rest stays on the
    /// mouse; the output (<c>out</c>) takes nothing (the original's section named "out" refuses every item). A refusal puts it back.
    /// </summary>
    void PlaceInSlot(World world, HeldItem held, PlaceHeld place)
    {
        var item = held.Item;
        int k = InputIndex(place.Section);
        if (held.From.Holder == ItemHolder.Stock
                || world.SystemOf<Production.ProductionSystem>()?.Reach(world, held.Actor, place.Into.Site,
                        out _) is not { } m || k < 0 || k >= m.Inputs.Length)
        {
            ReturnHeldItem(world);
            return;
        }
        if (m.Inputs[k].Item != item.Record)
        {
            world.Post("Invalid container type.");
            ReturnHeldItem(world);
            return;
        }
        int n = Math.Min(m.Inputs[k].Recipe.Capacity - m.Inputs[k].Count, item.Quantity);
        if (n <= 0)
        {
            ReturnHeldItem(world);
            return;
        }
        bool death = item.Death;
        m.Inputs[k].Count += n;
        item.Quantity -= n;
        if (item.Quantity <= 0) world.Player.Held = null;
        if (place.Into != held.From) HeldItemTheft.LeftLoot(world, held, death);
        world.Machines.Touch();
    }

    #endregion

    #region Give and drop

    /// <summary>
    /// Gives the held item to a character in the world (<c>FUN_1407136a0</c>): someone other than its window's owner, within <see cref="GiveRange"/>
    /// (<see cref="StealRange"/> for a non-player window's item; "Out of trading range."), added where it fits in its inventory
    /// (<see cref="Inventory.Add"/>), else in a free slot that takes it. Nothing fits: "No room for that item." and it goes back; what does not fit
    /// stays on the mouse. Stock is not given away.
    /// </summary>
    void Give(World world, GiveHeld give)
    {
        var player = world.Player;
        if (player.Held is not { } held) return;
        var table = world.Characters;
        if (held.From.Holder == ItemHolder.Stock || held.From == ItemOwner.Of(give.Target) || !table.TryResolveNext(give.Target, out int s)
                || table.Cold(s) is not { Inventory: { } into } taker
            || DropSpot(world, held) is not var (from, _))
        {
            ReturnHeldItem(world);
            return;
        }
        float reach = HeldItemTheft.FromPlayerWindow(world, held) ? GiveRange : StealRange;
        if (Vector2.Distance(new(from.X, from.Z), new(table.Next[s].Position.X, table.Next[s].Position.Z)) > reach)
        {
            world.Post("Out of trading range.");
            ReturnHeldItem(world);
            return;
        }
        if (HeldItemTheft.Caught(world, held, Crime.CrimeSystem.TakeKind.Give))
        {
            ReturnHeldItem(world);
            return;
        }
        var item = held.Item;
        bool death = item.Death;
        var mark = item.Mark;
        int before = item.Quantity;
        (item.Death, item.Mark) = (false, HeldItemTheft.MarkFor(world, held, taker, overwrite: true));
        // A whole stack that fits goes in as the item itself, its quantity unchanged: only the result tells it apart from nothing fitting.
        bool all = into.Add(item);

        // Engine choice, as a pick-up: what no grid takes goes into a free slot that takes it (a polearm fits no grid, only the back slot).
        bool worn = !all && item.Quantity == before
                && (InventoryCommands.TryEquip(into, item, taker) || InventoryCommands.TryWearBag(into, item, taker));
        all |= worn;
        if (!all && item.Quantity == before)
        {
            (item.Death, item.Mark) = (death, mark);
            world.Post("No room for that item.");
            ReturnHeldItem(world);
            return;
        }
        if (all) player.Held = null;
        else
            (item.Death, item.Mark) = (death, mark);
        if (worn) _assembly?.RefreshEquipment(taker);
        HeldItemTheft.LeftLoot(world, held, death);
        if (held.From.Holder == ItemHolder.Site) world.Machines.Touch();
    }

    /// <summary>
    /// Drops the held item on the ground (<c>FUN_1407136a0</c>): only out of the player's own windows (a character of the player's faction, a
    /// machine, a container of the player's faction); otherwise it goes back. It lands at the feet of its window's character (else of its holder), a
    /// little ahead (engine choice), turned at random.
    /// </summary>
    void DropHeldItem(World world)
    {
        if (world.Player.Held is not { } held) return;
        if (!HeldItemTheft.FromPlayerWindow(world, held) || DropSpot(world, held) is not var (at, slot))
        {
            ReturnHeldItem(world);
            return;
        }
        world.Player.Held = null;
        Ground(world, held.Item, at, slot);
    }

    /// <summary>
    /// Where a held item lands on the ground: ahead of its window's character, else of its holder (with that character's slot); null when neither is
    /// about.
    /// </summary>
    static (Vector3 At, int Slot)? DropSpot(World world, HeldItem held)
    {
        var table = world.Characters;
        int s = held.From.Holder == ItemHolder.Character && table.TryResolveNext(held.From.Character,
                out int o) ? o : table.TryResolveNext(held.Actor, out int a) ? a : -1;
        return s < 0 ? null : (AheadOf(world, s), s);
    }

    /// <summary>
    /// How far ahead of a character an item it drops lands, and the most its ground may differ in height from the character's there (engine
    /// choices).
    /// </summary>
    const float DropReach = 3, DropStep = 4;

    /// <summary>
    /// The ground ahead of a character where an item it drops lands: <see cref="DropReach"/> units ahead, nearer (to its feet) while the spot is
    /// not one it could stand on at about its own height. Past the edge of a walkway or against a wall the height there is the ground far below or
    /// the wall's top, and the item was out of sight and reach.
    /// </summary>
    static Vector3 AheadOf(World world, int slot)
    {
        ref var hot = ref world.Characters.Next[slot];
        var ahead = new Vector3(MathF.Sin(hot.Yaw), 0, MathF.Cos(hot.Yaw));
        for (float reach = DropReach; reach > 0; reach -= 0.5f)
        {
            var at = hot.Position + ahead * reach;
            if (!world.Walkability.TryStand(at.X, at.Z, hot.Position.Y, out float y) || MathF.Abs(y - hot.Position.Y) > DropStep) continue;

            at.Y = y;
            return at;
        }

        return hot.Position;
    }

    /// <summary>
    /// Drops an item the character wore in front of it (<c>FUN_1405ca740</c>, character vtable slot 53: the item is placed ahead of the character and
    /// leaves its inventory).
    /// </summary>
    internal static void DropAhead(World world, int slot, ItemInstance item) => Ground(world, item, AheadOf(world, slot), slot);

    /// <summary>
    /// An item on the ground, turned at random (the original's physics tumbles it) by a roll of the character and the tick.
    /// </summary>
    static void Ground(World world, ItemInstance item, Vector3 at, int slot)
    {
        item.Section = "main";
        world.GroundItems.Drop(item, at, Rng.Float(Rng.Hash(world.Seed, (ulong)slot, RngPurpose.Loot, (ulong)world.Tick)) * MathF.Tau);
    }

    #endregion

    #region Return

    /// <summary>
    /// Puts the held item back (<c>FUN_14070d860</c>, <b>Observed</b>): into its old place when that is still free (a prosthetic refitted on its
    /// stump), else wherever its window's inventory takes it (<see cref="Inventory.Add"/>), else after arranging that inventory
    /// (<see cref="InventoryCommands.Arrange"/>), else onto the ground. A machine slot's units go back into the slot.
    /// </summary>
    internal void ReturnHeldItem(World world)
    {
        var player = world.Player;
        if (player.Held is not { } held) return;
        player.Held = null;
        var item = held.Item;
        if (held.Grid is not { } root)
        {
            ReturnToMachine(world, held, item);
            return;
        }
        var table = world.Characters;
        var owner = held.From.Holder == ItemHolder.Character && table.TryResolveNext(held.From.Character, out int s) ? table.Cold(s) : null;
        if (held.From.Holder == ItemHolder.Site) world.Machines.Touch();
        var grid = held.Section == "backpack_content" ? root.Backpack?.Contents : root;
        if (grid is not null && BackInPlace(grid, held, item, owner))
        {
            if (owner is not null && (InventoryCommands.IsEquipmentSection(held.Section) || InventoryCommands.IsLimbSection(held.Section)))
                _assembly?.RefreshEquipment(owner);
            return;
        }
        if (root.Add(item) || InventoryCommands.Arrange(root) && root.Add(item)) return;
        if (DropSpot(world, held) is var (at, from)) Ground(world, item, at, from);
    }

    /// <summary>
    /// A held item that came from no inventory back into its machine's slot, crafted list or output; onto the ground when none takes it.
    /// </summary>
    static void ReturnToMachine(World world, HeldItem held, ItemInstance item)
    {
        if (world.Machines.Find(held.From.Site) is { } m)
        {
            int k = InputIndex(held.Section);
            bool back = true;
            if (held.Section == "out" && m.Craft is { } craft)
                craft.Done.Insert(Math.Clamp(held.X, 0, craft.Done.Count), item);
            else if (held.Section == "out" && m.Output is { } output && output.Recipe.Item == item.Record)
                output.Count += item.Quantity;
            else if (k >= 0 && k < m.Inputs.Length && m.Inputs[k].Item == item.Record)
                m.Inputs[k].Count += item.Quantity;
            else
                back = false;
            world.Machines.Touch();
            if (back) return;
        }
        if (DropSpot(world, held) is var (spot, slot)) Ground(world, item, spot, slot);
    }

    /// <summary>
    /// The held item into its old place: a free slot (a prosthetic on its stump), or its old cell when it is free and its stack fits there.
    /// </summary>
    bool BackInPlace(Inventory grid, HeldItem held, ItemInstance item, CharacterCold? owner)
    {
        string section = held.Section;
        if (InventoryCommands.LimbSlotOf(section) is >= 0 and var limbSlot)
        {
            if (owner?.Medical is not { } medical || data?.Items.Limb(item.Record) is not { } limb
                    || medical.LimbPart(limbSlot) is not { Limb: Bodies.LimbState.Stump }
                || grid.Items.Exists(i => i.Section == section)) return false;
            medical.FitReplacement(limb, item, item.LimbDamage, item.LimbWear, item.LimbStun);
            (item.LimbDamage, item.LimbWear, item.LimbStun) = (0, 0, 0);
        }
        else if (InventoryCommands.IsSingleSlot(section))
        {
            if (grid.Items.Exists(i => i.Section == section)) return false;
        }
        else
        {
            var (w, h) = InventoryCommands.IsEquipmentSection(section) ? InventoryCommands.SectionSize(section) : (grid.Width, grid.Height);
            if (held.X < 0 || held.Y < 0 || held.X + item.Width > w || held.Y + item.Height > h || item.Quantity > grid.LimitOf(item)
                || !grid.IsFree(section, held.X, held.Y, item.Width, item.Height)) return false;
        }
        (item.Section, item.X, item.Y) = (section, held.X, held.Y);
        grid.Items.Add(item);
        return true;
    }

    /// <summary>
    /// Each tick: the held item goes back when its window is gone (the loot, trade or container closed, its character gone) or its holder is gone, or
    /// down while working another's window. A downed player character's own items still move (<b>Observed</b> in the original by a player, #329),
    /// and so does stock lifted by a knocked-out (not dead) customer while the trade stays open with them (<c>TradeSystem.OpenStock</c>).
    /// </summary>
    internal void KeepHeld(World world)
    {
        if (world.Player.Held is not { } held) return;
        var table = world.Characters;
        bool own = held.From == ItemOwner.Of(held.Actor);
        bool stock = held.From.Holder == ItemHolder.Stock;
        bool keep = table.TryResolveNext(held.Actor, out int a) && table.Cold(a) is { } actor && (own
                || actor.Medical is not { Incapacitated: true } || (stock && actor.Medical is not { Dead: true })) && held.From.Holder switch
        {
            ItemHolder.Character => CharacterWindow(world, held.From.Character, held.Actor, out _) is not null,
            ItemHolder.Stock => world.SystemOf<Trading.TradeSystem>()?.OpenStock(world, out _) is not null,
            _ => world.Player.OpenContainer == held.From.Site || world.Machines.Find(held.From.Site) is not null,
        };
        if (!keep) ReturnHeldItem(world);
    }

    #endregion
}
