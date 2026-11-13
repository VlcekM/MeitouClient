using Meitou.Data.Fcs;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

/// <summary>
/// Snapshot identity prevents a drag started before eating/looting from moving a different item.
/// </summary>
public sealed record InventoryItemToken(int Index, string Record, string Section, int X, int Y, int Quantity, float Quality);
/// <summary>
/// The inventory window's ARRANGE (<c>FUN_14070f740</c>): <see cref="InventoryCommands.Arrange(Inventory, bool)"/> on the grid of the window
/// pressed: the character's main grid, or the worn bag's when <paramref name="Bag"/>.
/// </summary>
public sealed record ArrangeInventory(CharacterId Character, bool Bag = false) : SimCommand;

/// <summary>
/// Moving items between inventory sections and grids, and the sections' rules.
/// </summary>
public static class InventoryCommands
{
    /// <summary>
    /// Whether <paramref name="section"/> is a worn item's (equipment) section, a mod's (<see cref="ModSlots"/>) included.
    /// </summary>
    public static bool IsEquipmentSection(string section) => section is "back" or "hip" or "shirt" or "head" or "legs" or "boots" or "armour"
            or "belt" or "backpack_attach" || ModSlots.Of(section) is not null;

    /// <summary>
    /// An equipment section disabled for <paramref name="wearer"/> (<c>CharacterHuman::validateInventorySections</c> <c>FUN_1406218f0</c>,
    /// <b>Observed</b>; drawn red, it takes nothing): <c>head</c> for a race with <c>no hats</c>, <c>shirt</c> with <c>no shirts</c>, <c>boots</c>
    /// with <c>no shoes</c> or while neither leg is the original (both lost or replaced).
    /// </summary>
    public static bool SectionClosed(CharacterCold? wearer, string section) => wearer?.Race is { } race && section switch
    {
        "head" => race.NoHats,
        "shirt" => race.NoShirts,
        "boots" => race.NoShoes || wearer.Medical is { } m && m.LimbPart(2) is { Limb: not LimbState.Original }
                && m.LimbPart(3) is { Limb: not LimbState.Original },
        _ => false,
    };

    /// <summary>
    /// An animal's main grid is shut (<c>CharacterAnimal</c> vtable slot 48, <c>FUN_1405cc630</c>, <b>Observed</b>: it enables <c>main</c> only when
    /// the flag at +0x5bc, taken to be dead, is set, or the animal is not the player's and is down; the animal window then leaves the grid out,
    /// <c>FUN_14014f570</c>). Shut, it takes nothing put in by hand; what is in it stays and can still be taken out (Meitou: earlier saves may hold
    /// items there). Not a <see cref="SectionClosed"/> section, which would empty it.
    /// </summary>
    public static bool MainClosed(CharacterCold? owner) =>
        owner is not null && SquadRegistry.IsAnimal(owner) && owner.Medical is not { Dead: true } && (owner.IsPlayer || !CharacterFacts.Down(owner));

    /// <summary>
    /// Whether <paramref name="wearer"/> may wear <paramref name="item"/> (null: no check): its race limits, an animal only what lists its race
    /// (<see cref="ItemInstance.WearableBy"/>).
    /// </summary>
    static bool WearableBy(ItemInstance item, CharacterCold? wearer)
            => item.WearableBy(wearer?.Race?.StringId, wearer is not null && SquadRegistry.IsAnimal(wearer));

    /// <summary>
    /// Wears a bag in a free <c>backpack_attach</c> (Meitou: a picked-up bag that no grid takes, as worn gear that does not fit is;
    /// <see cref="TryEquip"/> leaves bags to the trade): a CONTAINER the wearer may wear, while it wears none. False: nothing changed.
    /// </summary>
    public static bool TryWearBag(Inventory root, ItemInstance item, CharacterCold? wearer)
    {
        if (item.Type != FcsRecordType.Container || item.Contents is null || item.Quantity != 1 || root.Backpack is not null
                || !WearableBy(item, wearer))
            return false;
        item.Section = "backpack_attach";
        item.X = 0;
        item.Y = 0;
        root.Items.Add(item);
        return true;
    }

    /// <summary>
    /// <c>validateInventorySections</c> (<c>FUN_1406218f0</c>, character vtable slot 130; <b>Verified</b> body): a section
    /// <see cref="SectionClosed"/> for the character empties itself. The first worn boots of a character whose race may wear shoes are dropped in
    /// front of it (slot 53, <c>FUN_1405ca740</c>); every other item of a closed section is destroyed (<c>FUN_140749c20</c> with its delete flag).
    /// The original runs it when it builds a human's inventory, when a leg is severed (<c>FUN_14064edc0</c>, parts 2 and 3) and for every character
    /// when the new game's editor closes (<c>FUN_1405f5d10</c>); Meitou calls it for the last two (<see cref="PopulationSystem"/>'s limb queue,
    /// <see cref="Editing.CharacterEditing.EndCreation"/>: a race switched in the editor keeps the old race's clothes until then), as a built
    /// inventory never has a worn item in a closed section.
    /// </summary>
    public static void ValidateSections(World world, int slot, CharacterCold cold)
    {
        if (cold.Inventory is not { } inventory) return;
        foreach (string section in ClosableSections)
        {
            if (!SectionClosed(cold, section)) continue;
            if (section == "boots" && cold.Race is { NoShoes: false } && inventory.Items.Find(i => i.Section == section) is { } boots)
            {
                inventory.Items.Remove(boots);
                HeldItems.DropAhead(world, slot, boots);
            }
            inventory.Items.RemoveAll(i => i.Section == section);
        }
    }

    /// <summary>
    /// The sections a race or a missing limb can close (<see cref="ValidateSections"/>).
    /// </summary>
    static readonly string[] ClosableSections = ["shirt", "head", "boots"];

    /// <summary>
    /// Whether items may be moved into <paramref name="section"/>: the main grid, a backpack's contents or an equipment section.
    /// </summary>
    public static bool IsMovableSection(string section) => section is "main" or "backpack_content" || IsEquipmentSection(section);
    /// <summary>
    /// A fitted prosthetic's section (<see cref="Meitou.Data.Gameplay.Bodies.LimbReplacement.SlotOf"/>): the limbs window's slots; not traded, not
    /// saved with the inventory.
    /// </summary>
    public static bool IsLimbSection(string section) => LimbSlotOf(section) >= 0;
    /// <summary>
    /// The limb slot (0 left arm, 1 right arm, 2 left leg, 3 right leg) of a prosthetic section; -1 for other sections.
    /// </summary>
    public static int LimbSlotOf(string section)
            => section switch { "left_arm_replace" => 0, "right_arm_replace" => 1, "left_leg_replace" => 2, "right_leg_replace" => 3, _ => -1 };

    /// <summary>
    /// Engine slot capacities, in inventory cells, a mod slot's its own (<see cref="ModSlots"/>). Clothing and backpack slots hold one item.
    /// </summary>
    public static (int Width, int Height) SectionSize(string section) => section switch
    {
        "back" => (10, 2),
        "hip" => (7, 1),
        "backpack_attach" => (5, 4),
        "shirt" or "boots" => (4, 2),
        "head" => (4, 3),
        "legs" => (4, 5),
        "armour" => (4, 6),
        "belt" => (2, 2),
        _ => ModSlots.Of(section) is { } mod ? (mod.Width, mod.Height) : (0, 0),
    };

    /// <summary>
    /// Puts one unit into the equipment slot that takes it when that is free (the trade window's right click, <c>FUN_140713d20</c>: sections that
    /// accept the item's slot type are tried before the general grids): weapons the first free cell of <c>back</c>, then <c>hip</c>; armour its
    /// <see cref="ItemInstance.EquipmentSection"/> when nothing is worn there. The order of two matching sections is <b>Unknown</b>. Bags are not
    /// equipped here: the trade moves them whole, into a free <c>backpack_attach</c> first (engine choice, docs/simulation.md). False: no slot, the
    /// caller places it in a grid. Nothing is worn that the wearer (<paramref name="wearer"/>; null: no check) may not wear (its race:
    /// <see cref="ItemInstance.WearableBy"/>), nor in a section closed for it (<see cref="SectionClosed"/>).
    /// </summary>
    public static bool TryEquip(Inventory root, ItemInstance item, CharacterCold? wearer = null)
    {
        if (!WearableBy(item, wearer) || item.Quantity != 1 || item.Width <= 0 || item.Height <= 0) return false;
        string[] sections = item.Type is FcsRecordType.Weapon or FcsRecordType.Crossbow ? ["back", "hip"]
            : item.Type is FcsRecordType.Armour or FcsRecordType.Item && IsEquipmentSection(item.EquipmentSection)
                    && item.EquipmentSection is not ("back" or "hip" or "backpack_attach") ? [item.EquipmentSection] : [];
        foreach (string section in sections)
        {
            if (SectionClosed(wearer, section)) continue;
            int x = 0, y = 0;
            if (section is "back" or "hip")
            {
                var size = SectionSize(section);
                var cell = Enumerable.Range(0, Math.Max(0, size.Height - item.Height + 1))
                    .SelectMany(cy => Enumerable.Range(0, Math.Max(0, size.Width - item.Width + 1)).Select(cx => (X: cx, Y: cy)))
                    .Cast<(int X, int Y)?>().FirstOrDefault(c => root.IsFree(section, c!.Value.X, c.Value.Y, item.Width, item.Height));
                if (cell is not { } free) continue;
                (x, y) = free;
            }
            else if (root.Items.Any(i => i.Section == section)) continue;
            item.Section = section;
            item.X = x;
            item.Y = y;
            root.Items.Add(item);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Moves the item of <paramref name="token"/> within <paramref name="root"/> to <paramref name="section"/> at (<paramref name="x"/>,
    /// <paramref name="y"/>); prosthetic sections allowed; false when it cannot go there.
    /// </summary>
    public static bool Move(Inventory root, InventoryItemToken token, string section, int x, int y, CharacterCold? wearer = null)
            => Transfer(root, token, root, section, x, y, limbs: true, wearer);

    /// <summary>
    /// A right click's unit count for <paramref name="item"/>: money is never split (<c>FUN_140713d20</c> skips the one-unit split for item function
    /// 14), so 0, the whole stack, whatever Shift says.
    /// </summary>
    public static int ClickCount(ItemInstance item, int count) => item.ItemFunction == PlayerSystem.MoneyFunction ? 0 : count;

    /// <summary>
    /// Moves at most <paramref name="count"/> units (0: all) of <paramref name="item"/>, held directly by <paramref name="owner"/>, into
    /// <paramref name="into"/> where they first fit (<see cref="Inventory.Add"/>: stacks of it, then free cells); what does not fit stays where it
    /// was. Returns the units moved. A right click into a storage or container moves one unit this way, with Shift the stack (docs/game/ui-input.md,
    /// the input tables); into a character it is <see cref="MoveInto"/>.
    /// </summary>
    public static int MoveUnits(Inventory owner, ItemInstance item, int count, Inventory into)
    {
        if (count <= 0 || count >= item.Quantity)
        {
            int before = item.Quantity, at = owner.Items.IndexOf(item);
            if (at < 0) return 0;
            owner.Items.RemoveAt(at);
            bool death = item.Death;
            item.Death = false;   // leaving its owner, it is no longer a death item
            if (into.Add(item)) return before;
            item.Death = death;
            if (item.Quantity > 0) owner.Items.Insert(at, item);
            return before - item.Quantity;
        }
        var unit = item.WithQuantity(count);
        unit.Death = false;
        int moved = into.Add(unit) ? count : count - unit.Quantity;
        item.Quantity -= moved;
        return moved;
    }

    /// <summary>
    /// Moves the item <paramref name="token"/> names in <paramref name="from"/> to <paramref name="section"/> at (<paramref name="x"/>,
    /// <paramref name="y"/>) of <paramref name="into"/>: the same inventory (<see cref="Move"/>) or another character's (the loot window), by the
    /// same rules. Prosthetic sections only with <paramref name="limbs"/>. False, nothing moved: the token is stale or the place does not take the
    /// item (an equipment or prosthetic section also one whose race <paramref name="wearer"/> may not wear, or a section closed for it; null: no
    /// check).
    /// </summary>
    public static bool Transfer(Inventory from, InventoryItemToken token, Inventory into, string section, int x, int y, bool limbs = false,
            CharacterCold? wearer = null)
    {
        var item = from.All().ElementAtOrDefault(token.Index);
        bool Movable(string s) => IsMovableSection(s) || limbs && IsLimbSection(s);
        if (item is null || item.Record != token.Record || item.Section != token.Section || item.X != token.X || item.Y != token.Y
                || item.Quantity != token.Quantity || item.Quality != token.Quality ||
            !Movable(item.Section) || !Movable(section)) return false;
        if (item.LockedOn && (section != item.Section || into != from)) return false;   // worn locked shackles stay on
        var owner = from.Items.Contains(item) ? from : from.Backpack?.Contents;
        var target = section == "backpack_content" ? into.Backpack?.Contents : into;

        // A bag cannot contain itself or another container; moving it preserves all its contents.
        if (section == "backpack_content" && item.Contents is not null) return false;
        var size = section is "back" or "hip" ? SectionSize(section) : IsEquipmentSection(section) || IsLimbSection(section)
                ? (item.Width, item.Height) : (target?.Width ?? 0, target?.Height ?? 0);
        if (owner is null || target is null || !owner.Items.Contains(item) || x < 0 || y < 0 || item.Width <= 0 || item.Height <= 0 ||
            x > size.Item1 - item.Width || y > size.Item2 - item.Height || item.Quantity > target.LimitOf(item)) return false;
        if ((IsEquipmentSection(section) || IsLimbSection(section)) && !WearableBy(item, wearer)) return false;
        if (SectionClosed(wearer, section) || section == "main" && MainClosed(wearer)) return false;
        if (IsEquipmentSection(section))
        {
            bool allowed = section switch
            {
                "back" or "hip" => item.Type is FcsRecordType.Weapon or FcsRecordType.Crossbow,
                "backpack_attach" => item.Type == FcsRecordType.Container && item.Contents is not null,
                _ => item.Type is FcsRecordType.Armour or FcsRecordType.Item && item.EquipmentSection == section,
            };
            if (!allowed || item.Quantity != 1) return false;
            if (section is not ("back" or "hip") && (x != 0 || y != 0 || target.Items.Any(i => i != item && i.Section == section)))
                return false;
        }

        // A prosthetic slot holds one LIMB_REPLACEMENT; whether it fits the limb is the caller's check (PlayerState).
        if (IsLimbSection(section)
                && (item.Type != FcsRecordType.LimbReplacement || item.Quantity != 1 || x != 0 || y != 0
                        || target.Items.Any(i => i != item && i.Section == section)))
            return false;
        foreach (var other in target.Items)
            if (other != item && other.Section == section && x < other.X + other.Width && other.X < x + item.Width && y < other.Y + other.Height
                    && other.Y < y + item.Height)
                return false;
        if (owner != target)
        {
            owner.Items.Remove(item);
            target.Items.Add(item);
        }
        if (into != from) item.Death = false;
        item.Section = section;
        item.X = x;
        item.Y = y;
        return true;
    }
    /// <summary>
    /// ARRANGE (<c>FUN_14070f740</c>, <b>Observed</b>): every grid section (the main grid and the worn bag's) is emptied and its items added again
    /// through the auto-add (<see cref="Inventory.AddTo"/>: partial stacks merge) in a sorted order: first by the longer side of the footprint, then
    /// by its area, then by its width, largest first (a stable sort); the first order in which everything fits is kept, otherwise every item goes
    /// back where it was (false).
    /// </summary>
    public static bool Arrange(Inventory root) => Arrange([(root, "main"), (root.Backpack?.Contents, "backpack_content")]);

    /// <summary>
    /// The ARRANGE button of one window (<c>FUN_14070f740</c>, <b>Observed</b>: it arranges only the inventory its window shows, its vtable
    /// <c>+0x70</c>): the main grid, or the worn bag's when <paramref name="bag"/>; as <see cref="Arrange(Inventory)"/> otherwise.
    /// </summary>
    public static bool Arrange(Inventory root, bool bag) =>
        Arrange(bag ? [(root.Backpack?.Contents, "backpack_content")] : [(root, "main")]);

    /// <summary>
    /// Arranges <paramref name="sections"/> (a grid and the section of it arranged; a missing grid is skipped) as <see cref="Arrange(Inventory)"/>.
    /// </summary>
    static bool Arrange((Inventory? Grid, string Section)[] sections)
    {
        var grids = new List<(Inventory Grid, string Section, ItemInstance[] Items)>();
        foreach (var (grid, section) in sections)
            if (grid is not null) grids.Add((grid, section, [.. grid.Items]));
        var saved = grids.SelectMany(g => g.Items).Select(i => (Item: i, i.X, i.Y, i.Quantity, i.Charges)).ToArray();
        void Restore()
        {
            foreach (var g in grids)
            {
                g.Grid.Items.Clear();
                g.Grid.Items.AddRange(g.Items);
            }
            foreach (var old in saved)
                (old.Item.X, old.Item.Y, old.Item.Quantity, old.Item.Charges) = (old.X, old.Y, old.Quantity, old.Charges);
        }
        Func<ItemInstance, long>[] orders = [i => Math.Max(i.Width, i.Height), i => i.Width * (long)i.Height, i => i.Width];
        foreach (var order in orders)
        {
            bool fits = true;
            foreach (var (grid, section, all) in grids)
            {
                var items = all.Where(i => i.Section == section).OrderByDescending(order).ToArray();
                grid.Items.RemoveAll(i => i.Section == section);
                foreach (var item in items)
                    if (item.Width <= 0 || item.Height <= 0 || !grid.AddTo(section, item))
                    {
                        fits = false;
                        break;
                    }
                if (!fits) break;
            }
            if (fits) return true;
            Restore();
        }
        return false;
    }

    /// <summary>
    /// The types that never stack (<c>FUN_14075e090</c>): WEAPON, ARMOUR, CONTAINER, MAP_ITEM, CROSSBOW, LIMB_REPLACEMENT; the others stack up to
    /// their record's <c>stackable</c>.
    /// </summary>
    public static bool NeverStacks(FcsRecordType type) =>
        type is FcsRecordType.Weapon or FcsRecordType.Armour or FcsRecordType.Container or FcsRecordType.MapItem or FcsRecordType.Crossbow
                or FcsRecordType.LimbReplacement;

    /// <summary>
    /// Whether a held item joins the stack it is dropped on (<c>FUN_140715560</c>): same record (so type), material and quality, of a type that
    /// stacks; Meitou also keeps stolen, death and unique items apart, as <see cref="Inventory.Add"/> does.
    /// </summary>
    public static bool Stackable(ItemInstance a, ItemInstance b) => !NeverStacks(a.Type) && a.Record == b.Record && a.MaterialId == b.MaterialId
            && a.Quality == b.Quality
        && a.Mark == b.Mark && a.Death == b.Death && a.Unique == b.Unique && a.Contents is null && b.Contents is null;

    /// <summary>
    /// Moves <paramref name="units"/> of <paramref name="from"/> onto the stack <paramref name="into"/>. Partial charges combine: a stack is full
    /// units and one partly used one (its <see cref="ItemInstance.Charges"/>), so when the last of <paramref name="from"/> moves, its part joins
    /// <paramref name="into"/>'s; two parts that fit in one unit make one.
    /// </summary>
    public static void Merge(ItemInstance into, ItemInstance from, int units)
    {
        if (units <= 0) return;
        bool last = units >= from.Quantity;
        into.Quantity += units;
        from.Quantity -= units;
        if (last) JoinPart(into, from.Charges);
    }

    /// <summary>
    /// Joins the partly used unit of a stack whose last units just moved onto <paramref name="into"/> (its <paramref name="charges"/>) with
    /// <paramref name="into"/>'s own: two parts that fit in one unit make one, else one unit is full and the rest is the part. An item of a
    /// single charge has no part.
    /// </summary>
    public static void JoinPart(ItemInstance into, float charges)
    {
        if (into.MaxCharges <= 1) return;

        float combined = into.Charges + charges;
        if (combined <= into.MaxCharges) into.Quantity--;
        into.Charges = combined <= into.MaxCharges ? combined : combined - into.MaxCharges;
    }

    /// <summary>
    /// Whether a section takes an item, the section half of <c>FUN_14074bc30</c>: the grids (<c>main</c>, <c>backpack_content</c>) anything, but a
    /// bag only empty into a bag; a weapon slot (<c>back</c>, <c>hip</c>) a WEAPON or CROSSBOW; <c>backpack_attach</c> a CONTAINER; a prosthetic slot
    /// a LIMB_REPLACEMENT; a clothing slot the ARMOUR or belt ITEM of that slot. Before the slot type, a section other than the grids checks the
    /// wearer's race (<paramref name="wearable"/> false: "Can't wear that item."; <c>RaceLimiter</c>). Null: taken; otherwise the refusal (""
    /// silent).
    /// </summary>
    public static string? SlotRefusal(string section, FcsRecordType type, string equipmentSection, Inventory? contents, bool wearable = true) =>
        SlotRefusal(section, type, equipmentSection, contents?.Items.Count ?? -1, wearable);

    /// <summary>
    /// As <see cref="SlotRefusal(string, FcsRecordType, string, Inventory?, bool)"/> with the number of items in a bag (-1: the item is not a bag).
    /// </summary>
    public static string? SlotRefusal(string section, FcsRecordType type, string equipmentSection, int bagItems, bool wearable = true)
    {
        if (section == "backpack_content")
            return bagItems > 0 ? "Container cannot be placed here unless it is empty." : null;
        if (section == "main") return null;
        if (!wearable) return "Can't wear that item.";
        bool takes = section switch
        {
            "back" or "hip" => type is FcsRecordType.Weapon or FcsRecordType.Crossbow,
            "backpack_attach" => type == FcsRecordType.Container && bagItems >= 0,
            _ when IsLimbSection(section) => type == FcsRecordType.LimbReplacement,
            _ when IsEquipmentSection(section) => type is FcsRecordType.Armour or FcsRecordType.Item && equipmentSection == section,
            _ => false,
        };
        return takes ? null : "Invalid container type.";
    }

    /// <summary>
    /// A section holding one item whatever its footprint (clothing, the bag slot, the prosthetic slots); the others are grids.
    /// </summary>
    public static bool IsSingleSlot(string section) => IsLimbSection(section) || IsEquipmentSection(section) && section is not ("back" or "hip");

    /// <summary>
    /// The right click's move into another character (<c>FUN_140713d20</c>, its target <c>FUN_14070dcd0</c>): one unit at a time, at most
    /// <paramref name="count"/> (0: until one fails), each into the target's open bag window first (<paramref name="bagFirst"/>), then its character
    /// window: the slots of the item's type (<see cref="TryEquip"/>), then the main grid, stacks of it first (<see cref="Inventory.AddTo"/>). Leaving
    /// its owner a unit is no longer a death item. Returns the units moved; <paramref name="equipped"/>: one was worn. <paramref name="bagOnly"/>:
    /// only into the target's worn bag, nowhere else.
    /// </summary>
    public static int MoveInto(Inventory owner, ItemInstance item, int count, Inventory into, bool bagFirst, out bool equipped,
            CharacterCold? wearer = null, bool bagOnly = false)
    {
        equipped = false;
        int want = count <= 0 ? item.Quantity : Math.Min(count, item.Quantity), moved = 0;
        while (moved < want)
        {
            bool last = item.Quantity <= 1;
            int at = owner.Items.IndexOf(item);
            if (at < 0) break;
            var unit = last ? item : item.WithQuantity(1);
            bool death = unit.Death;
            if (last) owner.Items.RemoveAt(at);
            unit.Death = false;
            bool worn = false;
            bool placed = bagOnly ? into.Backpack?.Contents is { } only && only.AddTo("backpack_content", unit)
                : bagFirst && into.Backpack?.Contents is { } bag && bag.AddTo("backpack_content", unit) || (worn = TryEquip(into, unit, wearer))
                        || !MainClosed(wearer) && into.AddTo("main", unit);
            if (!placed)
            {
                unit.Death = death;
                if (last) owner.Items.Insert(at, item);
                break;
            }
            equipped |= worn;
            if (!last) item.Quantity--;
            moved++;
        }
        return moved;
    }
}