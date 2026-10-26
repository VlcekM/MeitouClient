using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Simulation.Items;

namespace Meitou.Simulation;

/// <summary>
/// The item on the mouse (docs/game/ui-screens.md section 6 "Holding an item"; the original's <c>MouseInventory</c>, <b>Observed</b> by decompile): one item at a time is lifted out of a window
/// (<see cref="LiftItem"/>), then placed in a window's section (<see cref="PlaceHeld"/>: moved, merged onto the one stack under it, or swapped with the one item under it, which takes its place
/// on the mouse), given to a character in the world (<see cref="GiveHeld"/>), dropped on the ground (<see cref="DropHeld"/>) or put back (<see cref="ReturnHeld"/>). Lifting from the
/// trader's stock buys nothing: the purchase happens when it is placed in the customer's window, priced then; a sale when the customer's item is placed on the stock. The item is world
/// state (<see cref="PlayerState.Held"/>); it goes back when its window closes or its holder goes down (<see cref="KeepHeld"/>) and before a save (<see cref="World.ReturnHeldItem"/>).
/// </summary>
public sealed partial class PlayerSystem
{
    /// <summary>
    /// How far a held item reaches another owner (<c>FUN_14070d290</c>, <b>Observed</b>): within 1000 units normally, 60 when it is being stolen (a non-player window's).
    /// </summary>
    public const float GiveRange = 1000, StealRange = 60;

    /// <summary>The ITEM <c>item function</c> of money (the "Cats" item): a right click or a move into the player's window turns it into cats (<c>FUN_140714a20</c>).</summary>
    public const int MoneyFunction = 14;

    const string LockedText = "This item is locked in place and cannot be removed. You can try to pick the lock, find a saw, or use brute strength.";

    /// <summary>Where a held item is placed: the window's root inventory, the inventory holding the section (the worn bag's for <c>backpack_content</c>), the section's size in cells
    /// (0: one item whatever its footprint), and the window's character, container or machine.</summary>
    readonly record struct Target(Inventory Root, Inventory Grid, string Section, int Columns, int Rows, CharacterCold? Owner, Locks.Fixture? Container, Production.Machine? Machine);

    void HeldInput(World world, HeldCommand command)
    {
        world.Player.HeldSerial = command.Serial;
        switch (command)
        {
            case LiftItem lift: Lift(world, lift); break;
            case PlaceHeld place: Place(world, place); break;
            case GiveHeld give: Give(world, give); break;
            case DropHeld: DropHeldItem(world); break;
            case ReturnHeld: ReturnHeldItem(world); break;
        }
    }

    /// <summary>
    /// The inventory of a character window a held item may come out of or go into: one of the player's characters, or the open loot's body when <paramref name="actor"/> is its looter.
    /// </summary>
    static Inventory? CharacterWindow(World world, CharacterId who, CharacterId actor, out CharacterCold? cold)
    {
        cold = null;
        if (!world.Characters.TryResolveNext(who, out int slot) || world.Characters.Cold(slot) is not { Inventory: { } inventory } c) return null;
        if (!c.IsPlayer && !(world.SystemOf<Carrying.CarrySystem>()?.OpenLoot(world) is { } open && open.Body == who && open.Looter == actor)) return null;
        cold = c;
        return inventory;
    }

    /// <summary>The inventory directly holding <paramref name="item"/>: the root, or the worn bag's contents.</summary>
    static Inventory? HolderOf(Inventory root, ItemInstance item) =>
        root.Items.Contains(item) ? root : root.Backpack?.Contents is { } bag && bag.Items.Contains(item) ? bag : null;

    static bool Matches(ItemInstance? item, InventoryItemToken t, bool section = true) => item is not null && item.Record == t.Record && (!section || item.Section == t.Section)
        && item.X == t.X && item.Y == t.Y && item.Quantity == t.Quantity && item.Quality == t.Quality;

    /// <summary><paramref name="units"/> of <paramref name="item"/>: the item itself when that is all of it, else a split-off copy of full units (the stack keeps its partly used one).</summary>
    static ItemInstance Part(ItemInstance item, int units)
    {
        if (units >= item.Quantity) return item;
        var part = item.WithQuantity(units);   // the first units, with their receipt's first coins
        item.Quantity -= units;
        if (part.MaxCharges > 0) part.Charges = part.MaxCharges;
        return part;
    }

    /// <summary>What a lift takes out of <paramref name="holder"/> (<c>FUN_1407139a0</c>): the whole stack when <paramref name="whole"/> or a single unit, else one unit.</summary>
    static ItemInstance Split(Inventory holder, ItemInstance item, bool whole)
    {
        if (whole || item.Quantity <= 1)
        {
            holder.Items.Remove(item);
            return item;
        }
        return Part(item, 1);
    }

    static void Hold(World world, LiftItem lift, ItemInstance unit, Inventory? grid, string section, int x, int y) =>
        world.Player.Held = new HeldItem { Item = unit, From = lift.From, Grid = grid, Section = section, X = x, Y = y, Actor = lift.Actor };

    void Lift(World world, LiftItem lift)
    {
        if (world.Player.Held is not null) return;
        var t = lift.Item;
        switch (lift.From.Holder)
        {
            case ItemHolder.Character:
            {
                if (CharacterWindow(world, lift.From.Character, lift.Actor, out var cold) is not { } root) return;
                var item = root.All().ElementAtOrDefault(t.Index);
                if (!Matches(item, t) || !(InventoryCommands.IsMovableSection(item!.Section) || InventoryCommands.IsLimbSection(item.Section)) || HolderOf(root, item) is not { } holder) return;
                if (item.LockedOn) { world.Post(LockedText); return; }
                if (InventoryCommands.LimbSlotOf(item.Section) is >= 0 and var limb)
                {
                    // A fitted prosthetic comes off: the limb is a stump while it is held.
                    if (cold!.Medical?.LimbPart(limb) is not { Limb: Bodies.LimbState.Replaced } part || part.ReplacementItem != item) return;
                    cold.Medical.RemoveReplacement(limb);
                }
                var (section, x, y) = (item.Section, item.X, item.Y);
                Hold(world, lift, Split(holder, item, lift.Whole), root, section, x, y);
                if (InventoryCommands.IsEquipmentSection(section) || InventoryCommands.IsLimbSection(section)) assembly?.RefreshEquipment(cold!);
                return;
            }
            case ItemHolder.Stock:
            {
                if (world.SystemOf<Trading.TradeSystem>()?.OpenStock(world, out var buyer) is not { } stock || buyer != lift.Actor) return;
                var item = stock.All().ElementAtOrDefault(t.Index);
                if (!Matches(item, t) || HolderOf(stock, item!) is not { } holder) return;
                Hold(world, lift, Split(holder, item!, lift.Whole), stock, item!.Section, item.X, item.Y);
                return;
            }
            case ItemHolder.Site:
                LiftFromSite(world, lift);
                return;
        }
    }

    /// <summary>A lift out of an open container, a storage or bench grid (<c>main</c>), a machine's input (<c>in1</c>, <c>in2</c>) or its output or crafted items (<c>out</c>).</summary>
    void LiftFromSite(World world, LiftItem lift)
    {
        var t = lift.Item;
        string site = lift.From.Site;
        if (ContainerFor(world, site, lift.Actor, out var opener) is { } container)
        {
            if (opener is null || world.Player.OpenContainer != site) return;
            var grid = PlayerSystem.GridOf(world, container, out var region);
            var item = grid.Items.ElementAtOrDefault(t.Index);
            if (!Matches(item, t with { X = t.X + region.X, Y = t.Y + region.Y }, section: false) || !region.Holds(item!)) return;   // the window shows cells from the container's corner
            Hold(world, lift, Split(grid, item!, lift.Whole), grid, "main", item!.X, item.Y);
            world.Machines.Touch();
            return;
        }
        if (world.SystemOf<Production.ProductionSystem>()?.Reach(world, lift.Actor, site, out _) is not { } m) return;
        if (t.Section == "main")
        {
            if (m.Storage is not { } storage || m.Closed || storage.Items.ElementAtOrDefault(t.Index) is not { } item || !Matches(item, t, section: false)) return;
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
            (string Item, int Count) slot = t.Section == "out" ? (m.Output?.Recipe.Item ?? "", m.Output?.Count ?? 0) : k >= 0 && k < m.Inputs.Length ? (m.Inputs[k].Item, m.Inputs[k].Count) : ("", 0);
            if (slot.Count <= 0 || slot.Item != t.Record) return;
            int n = lift.Whole ? slot.Count : 1;
            if (data?.Items.Create(slot.Item, n) is not { } made) return;
            if (t.Section == "out") m.Output!.Count -= n;
            else m.Inputs[k].Count -= n;
            Hold(world, lift, made, null, t.Section, 0, 0);
        }
        world.Machines.Touch();
    }

    /// <summary>A machine window's input slot section (<c>in1</c>, <c>in2</c>, the layout's names) as an input index; -1 for any other.</summary>
    static int InputIndex(string section) => section is ['i', 'n', var d] && d is >= '1' and <= '9' ? d - '1' : -1;

    Target? Resolve(World world, HeldItem held, PlaceHeld place)
    {
        var into = place.Into;
        switch (into.Holder)
        {
            case ItemHolder.Character:
            {
                if (CharacterWindow(world, into.Character, held.Actor, out var cold) is not { } root) return null;
                if (place.Section == "backpack_content") return root.Backpack?.Contents is { } bag ? new(root, bag, place.Section, bag.Width, bag.Height, cold, null, null) : null;
                if (place.Section == "main") return new(root, root, place.Section, root.Width, root.Height, cold, null, null);
                if (!InventoryCommands.IsEquipmentSection(place.Section) && !InventoryCommands.IsLimbSection(place.Section)) return null;
                var (w, h) = InventoryCommands.IsSingleSlot(place.Section) ? (0, 0) : InventoryCommands.SectionSize(place.Section);
                return new(root, root, place.Section, w, h, cold, null, null);
            }
            case ItemHolder.Stock:
                return place.Section == "main" && world.SystemOf<Trading.TradeSystem>()?.OpenStock(world, out var buyer) is { } stock && buyer == held.Actor
                    ? new(stock, stock, "main", stock.Width, stock.Height, null, null, null) : null;
            default:
                if (place.Section != "main") return null;
                if (ContainerFor(world, into.Site, held.Actor, out var opener) is { } container)
                    return opener is not null && world.Player.OpenContainer == into.Site && PlayerSystem.ShopOf(world, container) is null ? new(PlayerSystem.GridOf(world, container), PlayerSystem.GridOf(world, container), "main", PlayerSystem.GridOf(world, container).Width, PlayerSystem.GridOf(world, container).Height, null, container, null) : null;
                return world.SystemOf<Production.ProductionSystem>()?.Reach(world, held.Actor, into.Site, out _) is { Storage: { } storage, Closed: false } m
                    ? new(storage, storage, "main", storage.Width, storage.Height, null, null, m) : null;
        }
    }

    /// <summary>Whether the held item came out of one of the player's own windows (theirs or a player-faction character's), so leaving it is not a theft and it may be dropped.</summary>
    static bool FromPlayerWindow(World world, HeldItem held) => held.From.Holder switch
    {
        ItemHolder.Character => world.Characters.TryResolveNext(held.From.Character, out int s) && world.Characters.Cold(s) is { } c && (c.IsPlayer || c.Faction == world.Player.Faction),
        ItemHolder.Site => world.Fixtures.Find(held.From.Site) is not { Contents: not null } box || box.Owner == world.Player.Faction,
        _ => false,
    };

    /// <summary>
    /// The stolen mark a held item gets when it goes to <paramref name="taker"/>'s faction: from a loot that is a theft (<see cref="Crime.CrimeSystem.IsLootTheft"/>) the body's faction, from
    /// another faction's container that faction (engine choice, as the container's take), else unchanged.
    /// </summary>
    static int MarkFor(World world, HeldItem held, CharacterCold? taker)
    {
        var item = held.Item;
        var table = world.Characters;
        if (taker is null) return item.StolenFrom;
        if (held.From.Holder == ItemHolder.Character && table.TryResolveNext(held.From.Character, out int v) && table.Cold(v) is { IsPlayer: false } victim
            && table.TryResolveNext(held.Actor, out int a) && Crime.CrimeSystem.IsLootTheft(table.Cold(a)!, victim)) return victim.Faction;
        if (held.From.Holder == ItemHolder.Site && world.Fixtures.Find(held.From.Site) is { Owner: >= 0 } box && box.Owner != taker.Faction) return box.Owner;
        return item.StolenFrom;
    }

    /// <summary>
    /// The held item leaves another faction's container or character for another owner: the pending crime of the move (a container: Looting for a drag, <c>FUN_140715560</c>, Theft
    /// for a give, <c>FUN_140712490</c>; a character: <see cref="Crime.CrimeSystem.TakeCode"/>) and the steal roll (<see cref="Crime.CrimeSystem.Steal"/>,
    /// <see cref="Crime.CrimeSystem.Pickpocket"/>). True when the thief was caught and the item goes back.
    /// </summary>
    static bool Caught(World world, HeldItem held, Crime.CrimeSystem.TakeKind kind)
    {
        var table = world.Characters;
        if (world.SystemOf<Crime.CrimeSystem>() is not { } crimes || !table.TryResolveNext(held.Actor, out int thief)) return false;
        if (held.From.Holder == ItemHolder.Site)
            return world.Fixtures.Find(held.From.Site) is { Contents: not null } box && box.Owner >= 0 && box.Owner != world.Player.Faction && crimes.Steal(world, thief, box, kind == Crime.CrimeSystem.TakeKind.Drag ? Crime.CrimeCode.Looting : Crime.CrimeCode.Theft);
        return held.From.Holder == ItemHolder.Character && table.TryResolveNext(held.From.Character, out int owner) && table.Cold(owner) is { IsPlayer: false }
            && crimes.Pickpocket(world, thief, owner, held.Item, kind);
    }

    /// <summary>The held item went to another owner out of the open loot: the body may be butchered (a death item) and the looting seen.</summary>
    static void LeftLoot(World world, HeldItem held, bool death)
    {
        if (held.From.Holder == ItemHolder.Character && world.SystemOf<Carrying.CarrySystem>() is { } carry && carry.OpenLoot(world) is { } open && open.Body == held.From.Character)
            carry.TookHeld(world, death);
    }

    /// <summary>Money (<see cref="MoneyFunction"/>) into the player's purse: "{N} cats added." (<c>FUN_140714a20</c>).</summary>
    void Cash(World world, ItemInstance item, int units) => Cash(world, data, item, units);

    /// <summary><paramref name="units"/> of the money item <paramref name="item"/> into the player's purse, "{N} cats added.", priced at the record's <c>value</c>.</summary>
    internal static void Cash(World world, PopulationData? data, ItemInstance item, int units)
    {
        long cats = (long)(data?.Items.Info(item.Record)?.Value ?? 0) * units;
        world.Player.Money += cats;
        world.Post($"{cats} cats added.");
    }

    /// <summary>
    /// Places the held item (<c>FUN_140715560</c>, <b>Observed</b>): the section must take it (<see cref="InventoryCommands.SlotRefusal(string, FcsRecordType, string, Inventory?, bool)"/>, a worn slot only what the owner's race may wear and none its race flags blocked, a limited storage its items, a prosthetic slot a
    /// stump of its side); the cell is the drop's, moved inside the section (a footprint larger than the section is refused); two or more items under it refuse it; one locked on refuses it
    /// ("Item is locked in place."); one of the same kind takes what its stack has room for (the rest stays on the mouse; a full stack takes nothing); any other is swapped. Between the stock
    /// and the customer the units are capped to what the payer can afford and paid; a stolen item put on a stock that is no fence may get the seller caught. A refusal puts the item back.
    /// </summary>
    void Place(World world, PlaceHeld place)
    {
        var player = world.Player;
        if (player.Held is not { } held) return;
        if (place.Into.Holder == ItemHolder.Site && place.Section != "main") { PlaceInSlot(world, held, place); return; }
        var into = place.Into;
        var item = held.Item;
        bool buy = held.From.Holder == ItemHolder.Stock && into.Holder != ItemHolder.Stock, sell = held.From.Holder != ItemHolder.Stock && into.Holder == ItemHolder.Stock;
        var trade = world.SystemOf<Trading.TradeSystem>();
        CharacterId customer = CharacterId.None;
        if ((buy || sell) && trade?.OpenStock(world, out customer) is null) { ReturnHeldItem(world); return; }
        // The stock trades with its customer only: stock goes into the customer's windows, the customer's own items onto the stock.
        if (buy && into != ItemOwner.Of(customer) || sell && held.From != ItemOwner.Of(customer) || Resolve(world, held, place) is not { } target)
        {
            ReturnHeldItem(world);
            return;
        }
        string? refusal = InventoryCommands.SlotRefusal(target.Section, item.Type, item.EquipmentSection, item.Contents, item.WearableBy(target.Owner?.Race?.StringId, target.Owner is { } wearer && SquadRegistry.IsAnimal(wearer)) && target.Owner?.Race?.BlocksSection(target.Section) != true);
        if (refusal is null && target.Machine is { Data.IsStorage: true } storage && !storage.Data.Accepts(item.Record, item.Type)) refusal = "Invalid container type.";
        if (refusal is null && InventoryCommands.LimbSlotOf(target.Section) is >= 0 and var limbSlot
            && (data?.Items.Limb(item.Record)?.LimbSlot != limbSlot || target.Owner?.Medical?.LimbPart(limbSlot) is not { Limb: Bodies.LimbState.Stump or Bodies.LimbState.Replaced }))
            refusal = "Can't wear that item.";   // a prosthetic of the slot's side, on a stump (or swapped with the one fitted there)
        if (refusal is not null)
        {
            if (refusal.Length > 0) world.Post(refusal);
            ReturnHeldItem(world);
            return;
        }
        // Money moved out of another's window into the player's own becomes cats.
        if (item.ItemFunction == MoneyFunction && target.Owner is { IsPlayer: true } && !FromPlayerWindow(world, held) && !buy)
        {
            Cash(world, item, item.Quantity);
            player.Held = null;
            LeftLoot(world, held, item.Death);
            return;
        }
        int x = 0, y = 0;
        List<ItemInstance> under;
        if (target.Columns == 0) under = target.Grid.Items.Where(i => i.Section == target.Section).ToList();
        else
        {
            if (item.Width > target.Columns || item.Height > target.Rows) { ReturnHeldItem(world); return; }
            x = Math.Clamp(place.X, 0, target.Columns - item.Width);
            y = Math.Clamp(place.Y, 0, target.Rows - item.Height);
            if (!target.Grid.InRegion(x, y, item.Width, item.Height))
            {
                // On a shop's greyed cells, or across two of its containers (engine message).
                world.Post("No room for that item.");
                ReturnHeldItem(world);
                return;
            }
            under = target.Grid.Items.Where(i => i.Section == target.Section && x < i.X + i.Width && i.X < x + item.Width && y < i.Y + i.Height && i.Y < y + item.Height).ToList();
        }
        if (under.Count >= 2) { ReturnHeldItem(world); return; }
        if (under is [{ LockedOn: true }])
        {
            world.Post("Item is locked in place.");
            ReturnHeldItem(world);
            return;
        }
        if (into != held.From && Caught(world, held, Crime.CrimeSystem.TakeKind.Drag)) { ReturnHeldItem(world); return; }
        int limit = target.Columns == 0 ? 1 : target.Grid.LimitOf(item);
        // Leaving its window for another owner's: no longer a death item, maybe stolen. Put back on a refusal; the units that stay on the mouse keep the old marks.
        bool changes = into != held.From, death = item.Death;
        int mark = item.StolenFrom;
        if (changes) (item.Death, item.StolenFrom) = (false, MarkFor(world, held, target.Owner));
        void Undo(ItemInstance i) => (i.Death, i.StolenFrom) = (death, mark);
        // The units the trade lets through (all of them outside a trade); 0: refused, put back.
        int Exchange(int units, bool all)
        {
            if (!buy && !sell) return units;
            if (item.Contents is { Items.Count: > 0 }) { world.Post(Trading.TradeSystem.BagNotEmpty); return 0; }
            if (sell && trade!.CaughtSelling(world, item)) return 0;
            int k = trade!.AffordHeld(world, item, units, sell);
            if (all && k is > 0 && k < units) { world.Post(sell ? "The shopkeeper can't afford that." : "You can't afford that."); return 0; }
            return k;
        }
        void Refuse() { Undo(item); ReturnHeldItem(world); }

        if (under is [var other])
        {
            if (target.Columns > 0 && limit > 1 && InventoryCommands.Stackable(item, other))
            {
                int room = Math.Min(limit - other.Quantity, item.Quantity);
                if (room <= 0) { Undo(item); return; }   // a full stack: the item stays on the mouse
                int n = Exchange(room, all: false);
                if (n <= 0) { Refuse(); return; }
                // Units of the same trade and direction keep their coins in the joined stack.
                var (kept, coming) = (other.Buyback, buy || sell ? trade!.PayHeld(world, item, n, sell) : item.Buyback?.Take(n));
                InventoryCommands.Merge(other, item, n);
                if (kept is not null && coming is not null && kept.Joins(coming)) other.Buyback = kept.Append(coming);
                // The merged stack lies at the drop's cell.
                (other.X, other.Y) = (x, y);
                if (item.Quantity <= 0) player.Held = null;
                else Undo(item);
                if (changes) LeftLoot(world, held, death);
                Touched(world, held, target);
                return;
            }
            // A swap: the held item takes the other's place, the other goes onto the mouse.
            if (item.Quantity > limit || Exchange(item.Quantity, all: true) <= 0) { Refuse(); return; }
            if (buy || sell) item.Buyback = trade!.PayHeld(world, item, item.Quantity, sell);
            target.Grid.Items.Remove(other);
            if (InventoryCommands.LimbSlotOf(target.Section) is >= 0 and var fitted) target.Owner?.Medical?.RemoveReplacement(fitted);
            Put(target, item, x, y);
            player.Held = new HeldItem { Item = other, From = into, Grid = target.Root, Section = other.Section, X = other.X, Y = other.Y, Actor = held.Actor };
            if (changes) LeftLoot(world, held, death);
            Touched(world, held, target);
            return;
        }
        {
            int n = Exchange(Math.Min(limit, item.Quantity), all: false);
            if (n <= 0) { Refuse(); return; }
            var receipt = buy || sell ? trade!.PayHeld(world, item, n, sell) : null;
            var unit = Part(item, n);
            if (buy || sell) unit.Buyback = receipt;
            Put(target, unit, x, y);
            if (ReferenceEquals(unit, item)) player.Held = null;
            else Undo(item);
            if (changes) LeftLoot(world, held, death);
            Touched(world, held, target);
        }
    }

    /// <summary>Puts an item into the target's section at (<paramref name="x"/>, <paramref name="y"/>); a prosthetic is fitted on its stump.</summary>
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

    /// <summary>After a placement: a worn section re-dresses its character; a machine or container's window is redrawn.</summary>
    void Touched(World world, HeldItem held, Target target)
    {
        if (target.Owner is { } owner && (InventoryCommands.IsEquipmentSection(target.Section) || InventoryCommands.IsLimbSection(target.Section))) assembly?.RefreshEquipment(owner);
        if (target.Container is not null || target.Machine is not null || held.From.Holder == ItemHolder.Site) world.Machines.Touch();
    }

    /// <summary>
    /// The held item onto a machine's input slot (<c>in1</c>, <c>in2</c>): the input of its record takes what it has room for, the rest stays on the mouse; the output (<c>out</c>) takes
    /// nothing (the original's section named "out" refuses every item). A refusal puts it back.
    /// </summary>
    void PlaceInSlot(World world, HeldItem held, PlaceHeld place)
    {
        var item = held.Item;
        int k = InputIndex(place.Section);
        if (held.From.Holder == ItemHolder.Stock || world.SystemOf<Production.ProductionSystem>()?.Reach(world, held.Actor, place.Into.Site, out _) is not { } m || k < 0 || k >= m.Inputs.Length)
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
        if (n <= 0) { ReturnHeldItem(world); return; }
        bool death = item.Death;
        m.Inputs[k].Count += n;
        item.Quantity -= n;
        if (item.Quantity <= 0) world.Player.Held = null;
        if (place.Into != held.From) LeftLoot(world, held, death);
        world.Machines.Touch();
    }

    /// <summary>
    /// Gives the held item to a character in the world (<c>FUN_1407136a0</c>): someone other than its window's owner, within <see cref="GiveRange"/> (<see cref="StealRange"/> for a non-player
    /// window's item; "Out of trading range."), added where it fits in its inventory (<see cref="Inventory.Add"/>). Nothing fits: "No room for that item." and it goes back; what does not fit
    /// stays on the mouse. Stock is not given away.
    /// </summary>
    void Give(World world, GiveHeld give)
    {
        var player = world.Player;
        if (player.Held is not { } held) return;
        var table = world.Characters;
        if (held.From.Holder == ItemHolder.Stock || held.From == ItemOwner.Of(give.Target) || !table.TryResolveNext(give.Target, out int s) || table.Cold(s) is not { Inventory: { } into } taker
            || DropSpot(world, held) is not var (from, _))
        {
            ReturnHeldItem(world);
            return;
        }
        float reach = FromPlayerWindow(world, held) ? GiveRange : StealRange;
        if (Vector2.Distance(new(from.X, from.Z), new(table.Next[s].Position.X, table.Next[s].Position.Z)) > reach)
        {
            world.Post("Out of trading range.");
            ReturnHeldItem(world);
            return;
        }
        if (Caught(world, held, Crime.CrimeSystem.TakeKind.Give)) { ReturnHeldItem(world); return; }
        var item = held.Item;
        bool death = item.Death;
        int mark = item.StolenFrom, before = item.Quantity;
        (item.Death, item.StolenFrom) = (false, MarkFor(world, held, taker));
        bool all = into.Add(item);
        if (item.Quantity == before)
        {
            (item.Death, item.StolenFrom) = (death, mark);
            world.Post("No room for that item.");
            ReturnHeldItem(world);
            return;
        }
        if (all) player.Held = null;
        else (item.Death, item.StolenFrom) = (death, mark);
        LeftLoot(world, held, death);
        if (held.From.Holder == ItemHolder.Site) world.Machines.Touch();
    }

    /// <summary>
    /// Drops the held item on the ground (<c>FUN_1407136a0</c>): only out of the player's own windows (a character of the player's faction, a machine, a container of the player's
    /// faction); otherwise it goes back. It lands at the feet of its window's character (else of its holder), a little ahead (engine choice), turned at random.
    /// </summary>
    void DropHeldItem(World world)
    {
        if (world.Player.Held is not { } held) return;
        if (!FromPlayerWindow(world, held) || DropSpot(world, held) is not var (at, slot)) { ReturnHeldItem(world); return; }
        world.Player.Held = null;
        Ground(world, held.Item, at, slot);
    }

    /// <summary>Where a held item lands on the ground: ahead of its window's character, else of its holder (with that character's slot); null when neither is about.</summary>
    static (Vector3 At, int Slot)? DropSpot(World world, HeldItem held)
    {
        var table = world.Characters;
        int s = held.From.Holder == ItemHolder.Character && table.TryResolveNext(held.From.Character, out int o) ? o : table.TryResolveNext(held.Actor, out int a) ? a : -1;
        if (s < 0) return null;
        ref var hot = ref table.Next[s];
        var at = hot.Position + new Vector3(MathF.Sin(hot.Yaw), 0, MathF.Cos(hot.Yaw)) * 3;
        at.Y = world.Walkability.GroundHeight(at.X, at.Z, hot.Position.Y);
        return (at, s);
    }

    /// <summary>An item on the ground, turned at random (the original's physics tumbles it) by a roll of the character and the tick.</summary>
    static void Ground(World world, ItemInstance item, Vector3 at, int slot)
    {
        item.Section = "main";
        world.GroundItems.Drop(item, at, Rng.Float(Rng.Hash(world.Seed, (ulong)slot, RngPurpose.Loot, (ulong)world.Tick)) * MathF.Tau);
    }

    /// <summary>
    /// Puts the held item back (<c>FUN_14070d860</c>, <b>Observed</b>): into its old place when that is still free (a prosthetic refitted on its stump), else wherever its window's inventory
    /// takes it (<see cref="Inventory.Add"/>), else after arranging that inventory (<see cref="InventoryCommands.Arrange"/>), else onto the ground. A machine slot's units go back into the slot.
    /// </summary>
    internal void ReturnHeldItem(World world)
    {
        var player = world.Player;
        if (player.Held is not { } held) return;
        player.Held = null;
        var item = held.Item;
        if (held.Grid is not { } root)
        {
            if (world.Machines.Find(held.From.Site) is { } m)
            {
                int k = InputIndex(held.Section);
                bool back = true;
                if (held.Section == "out" && m.Craft is { } craft) craft.Done.Insert(Math.Clamp(held.X, 0, craft.Done.Count), item);
                else if (held.Section == "out" && m.Output is { } output && output.Recipe.Item == item.Record) output.Count += item.Quantity;
                else if (k >= 0 && k < m.Inputs.Length && m.Inputs[k].Item == item.Record) m.Inputs[k].Count += item.Quantity;
                else back = false;
                world.Machines.Touch();
                if (back) return;
            }
            if (DropSpot(world, held) is var (spot, slot)) Ground(world, item, spot, slot);
            return;
        }
        var table = world.Characters;
        var owner = held.From.Holder == ItemHolder.Character && table.TryResolveNext(held.From.Character, out int s) ? table.Cold(s) : null;
        if (held.From.Holder == ItemHolder.Site) world.Machines.Touch();
        var grid = held.Section == "backpack_content" ? root.Backpack?.Contents : root;
        if (grid is not null && BackInPlace(grid, held, item, owner))
        {
            if (owner is not null && (InventoryCommands.IsEquipmentSection(held.Section) || InventoryCommands.IsLimbSection(held.Section))) assembly?.RefreshEquipment(owner);
            return;
        }
        if (root.Add(item) || InventoryCommands.Arrange(root) && root.Add(item)) return;
        if (DropSpot(world, held) is var (at, from)) Ground(world, item, at, from);
    }

    /// <summary>The held item into its old place: a free slot (a prosthetic on its stump), or its old cell when it is free and its stack fits there.</summary>
    bool BackInPlace(Inventory grid, HeldItem held, ItemInstance item, CharacterCold? owner)
    {
        string section = held.Section;
        if (InventoryCommands.LimbSlotOf(section) is >= 0 and var limbSlot)
        {
            if (owner?.Medical is not { } medical || data?.Items.Limb(item.Record) is not { } limb || medical.LimbPart(limbSlot) is not { Limb: Bodies.LimbState.Stump }
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

    /// <summary>Each tick: the held item goes back when its window is gone (the loot, trade or container closed, its character gone) or its holder is down or gone.</summary>
    void KeepHeld(World world)
    {
        if (world.Player.Held is not { } held) return;
        var table = world.Characters;
        bool keep = table.TryResolveNext(held.Actor, out int a) && table.Cold(a) is { } actor && actor.Medical is not { Incapacitated: true } && held.From.Holder switch
        {
            ItemHolder.Character => CharacterWindow(world, held.From.Character, held.Actor, out _) is not null,
            ItemHolder.Stock => world.SystemOf<Trading.TradeSystem>()?.OpenStock(world, out _) is not null,
            _ => world.Player.OpenContainer == held.From.Site || world.Machines.Find(held.From.Site) is not null,
        };
        if (!keep) ReturnHeldItem(world);
    }

    /// <summary>
    /// A right click on an item of a player character's own window (<c>FUN_140714a20</c>, <b>Observed</b>), one unit (all with <see cref="QuickMoveItem.All"/>, repeated until a move fails):
    /// money becomes cats; a worn item goes to the main grid (else the open bag); a grid item to the slot that takes it, else a prosthetic onto its stump, else from the main grid into the open
    /// bag. A blueprint of the player's faction is read (<see cref="Production.ProductionSystem.Learn"/>) and used up when it taught something; map items are <see cref="ReadMapItem"/>.
    /// </summary>
    void QuickMove(World world, QuickMoveItem quick)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(quick.Character, out int s) || table.Cold(s) is not { IsPlayer: true, Inventory: { } root } cold) return;
        var item = root.All().ElementAtOrDefault(quick.Item.Index);
        if (!Matches(item, quick.Item) || !(InventoryCommands.IsMovableSection(item!.Section) || InventoryCommands.IsLimbSection(item.Section)) || HolderOf(root, item) is not { } holder) return;
        if (item.LockedOn) { world.Post(LockedText); return; }
        if (item.ItemFunction == MoneyFunction && cold.Faction == world.Player.Faction)
        {
            Cash(world, item, item.Quantity);
            holder.Items.Remove(item);
            return;
        }
        if (item.Type == FcsRecordType.BLUEPRINT && cold.Faction == world.Player.Faction)
        {
            // One read whatever Shift says: a second unit would only say "Research already known".
            if (world.SystemOf<Production.ProductionSystem>()?.Learn(world, item) == true) root.RemoveOne(item);
            return;
        }
        var bag = quick.BagOpen ? root.Backpack?.Contents : null;
        string from = item.Section;
        if (InventoryCommands.LimbSlotOf(from) is >= 0 and var fitted)
        {
            // A fitted prosthetic comes off into the grid (else the open bag) when it fits there.
            if (cold.Medical?.LimbPart(fitted) is not { Limb: Bodies.LimbState.Replaced } part || part.ReplacementItem != item) return;
            holder.Items.Remove(item);
            if (!root.AddTo("main", item) && !(bag?.AddTo("backpack_content", item) ?? false)) { holder.Items.Add(item); return; }
            cold.Medical.RemoveReplacement(fitted);
            assembly?.RefreshEquipment(cold);
            return;
        }
        bool worn = InventoryCommands.IsEquipmentSection(from);
        bool Step(ItemInstance unit)
        {
            if (worn) return root.AddTo("main", unit) || (bag?.AddTo("backpack_content", unit) ?? false);
            if (InventoryCommands.TryEquip(root, unit, cold.Race)) return true;
            if (unit.Type == FcsRecordType.CONTAINER && unit.Contents is not null && from == "main" && !root.Items.Exists(i => i.Section == "backpack_attach"))
            {
                (unit.Section, unit.X, unit.Y) = ("backpack_attach", 0, 0);
                root.Items.Add(unit);
                return true;
            }
            if (unit.Type == FcsRecordType.LIMB_REPLACEMENT && data?.Items.Limb(unit.Record) is { LimbSlot: >= 0 and <= 3 } limb
                && cold.Medical?.LimbPart(limb.LimbSlot) is { Limb: Bodies.LimbState.Stump })
            {
                string section = limb.LimbSlot switch { 0 => "left_arm_replace", 1 => "right_arm_replace", 2 => "left_leg_replace", _ => "right_leg_replace" };
                if (root.Items.Exists(i => i.Section == section)) return false;
                (unit.Section, unit.X, unit.Y) = (section, 0, 0);
                root.Items.Add(unit);
                cold.Medical.FitReplacement(limb, unit, unit.LimbDamage, unit.LimbWear, unit.LimbStun);
                (unit.LimbDamage, unit.LimbWear, unit.LimbStun) = (0, 0, 0);
                return true;
            }
            return from == "main" && bag is not null && bag.AddTo("backpack_content", unit);
        }
        int want = quick.All ? item.Quantity : 1, moved = 0;
        while (moved < want)
        {
            bool last = item.Quantity <= 1;
            int at = holder.Items.IndexOf(item);
            if (at < 0) break;
            var unit = last ? item : item.WithQuantity(1);
            if (last) holder.Items.RemoveAt(at);
            if (!Step(unit))
            {
                if (last) holder.Items.Insert(at, item);
                break;
            }
            if (!last) item.Quantity--;
            moved++;
        }
        if (moved > 0) assembly?.RefreshEquipment(cold);
    }
}
