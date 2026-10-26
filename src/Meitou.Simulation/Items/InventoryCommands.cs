using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Items;

/// <summary>Snapshot identity prevents a drag started before eating/looting from moving a different item.</summary>
public sealed record InventoryItemToken(int Index, string Record, string Section, int X, int Y, int Quantity, float Quality);
/// <summary>The inventory window's ARRANGE (<c>FUN_14070f740</c>): <see cref="InventoryCommands.Arrange"/> on the character's grids.</summary>
public sealed record ArrangeInventory(CharacterId Character) : SimCommand;

public static class InventoryCommands
{
    public static bool IsEquipmentSection(string section) => section is "back" or "hip" or "shirt" or "head" or "legs" or "boots" or "armour" or "belt" or "backpack_attach";
    public static bool IsMovableSection(string section) => section is "main" or "backpack_content" || IsEquipmentSection(section);
    /// <summary>A fitted prosthetic's section (<see cref="Meitou.Data.Gameplay.Bodies.LimbReplacement.SlotOf"/>): the limbs window's slots; not traded, not saved with the inventory.</summary>
    public static bool IsLimbSection(string section) => LimbSlotOf(section) >= 0;
    /// <summary>The limb slot (0 left arm, 1 right arm, 2 left leg, 3 right leg) of a prosthetic section; -1 for other sections.</summary>
    public static int LimbSlotOf(string section) => section switch { "left_arm_replace" => 0, "right_arm_replace" => 1, "left_leg_replace" => 2, "right_leg_replace" => 3, _ => -1 };

    // Engine slot capacities, in inventory cells. Clothing and backpack slots hold one item.
    public static (int Width, int Height) SectionSize(string section) => section switch
    {
        "back" => (10, 2), "hip" => (7, 1), "backpack_attach" => (5, 4),
        "shirt" or "boots" => (4, 2), "head" => (4, 3), "legs" => (4, 5),
        "armour" => (4, 6), "belt" => (2, 2), _ => (0, 0),
    };

    /// <summary>
    /// Puts one unit into the equipment slot that takes it when that is free (the trade window's right click, <c>FUN_140713d20</c>: sections that accept the item's slot type are tried before
    /// the general grids): weapons the first free cell of <c>back</c>, then <c>hip</c>; armour its <see cref="ItemInstance.EquipmentSection"/> when nothing is worn there. The order of two
    /// matching sections is <b>Unknown</b>. Bags are not equipped here: the trade moves them whole, into a free <c>backpack_attach</c> first (engine choice, docs/simulation.md). False: no slot, the caller places it in a grid. Nothing is worn
    /// that the wearer's race (<paramref name="wearer"/>; null: no check) may not wear (<see cref="ItemInstance.WearableBy"/>) and no section it has flagged (<c>no hats</c>, <c>no shirts</c>, <c>no shoes</c>: <see cref="RaceData.BlocksSection(string)"/>).
    /// </summary>
    public static bool TryEquip(Inventory root, ItemInstance item, RaceData? wearer = null)
    {
        if (!item.WearableBy(wearer?.StringId) || item.Quantity != 1 || item.Width <= 0 || item.Height <= 0) return false;
        string[] sections = item.Type is Meitou.Data.Fcs.FcsRecordType.WEAPON or Meitou.Data.Fcs.FcsRecordType.CROSSBOW ? ["back", "hip"]
            : item.Type is Meitou.Data.Fcs.FcsRecordType.ARMOUR or Meitou.Data.Fcs.FcsRecordType.ITEM && IsEquipmentSection(item.EquipmentSection) && item.EquipmentSection is not ("back" or "hip" or "backpack_attach") ? [item.EquipmentSection] : [];
        foreach (string section in sections)
        {
            if (wearer?.BlocksSection(section) == true) continue;
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

    public static bool Move(Inventory root, InventoryItemToken token, string section, int x, int y, RaceData? wearer = null) => Transfer(root, token, root, section, x, y, limbs: true, wearer);

    /// <summary>
    /// Moves at most <paramref name="count"/> units (0: all) of <paramref name="item"/>, held directly by <paramref name="owner"/>, into <paramref name="into"/> where they first fit
    /// (<see cref="Inventory.Add"/>: stacks of it, then free cells); what does not fit stays where it was. Returns the units moved. A right click into a storage or container moves one unit this
    /// way, with Shift the stack (docs/game/ui-input.md, the input tables); into a character it is <see cref="MoveInto"/>.
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
    /// Moves the item <paramref name="token"/> names in <paramref name="from"/> to <paramref name="section"/> at (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="into"/>: the same
    /// inventory (<see cref="Move"/>) or another character's (the loot window), by the same rules. Prosthetic sections only with <paramref name="limbs"/>. False, nothing moved: the token is
    /// stale or the place does not take the item (an equipment or prosthetic section also one the race <paramref name="wearer"/> may not wear, or one it has flagged blocked; null: no check).
    /// </summary>
    public static bool Transfer(Inventory from, InventoryItemToken token, Inventory into, string section, int x, int y, bool limbs = false, RaceData? wearer = null)
    {
        var item = from.All().ElementAtOrDefault(token.Index);
        bool Movable(string s) => IsMovableSection(s) || limbs && IsLimbSection(s);
        if (item is null || item.Record != token.Record || item.Section != token.Section || item.X != token.X || item.Y != token.Y || item.Quantity != token.Quantity || item.Quality != token.Quality ||
            !Movable(item.Section) || !Movable(section)) return false;
        if (item.LockedOn && (section != item.Section || into != from)) return false;   // worn locked shackles stay on
        var owner = from.Items.Contains(item) ? from : from.Backpack?.Contents;
        var target = section == "backpack_content" ? into.Backpack?.Contents : into;
        // A bag cannot contain itself or another container; moving it preserves all its contents.
        if (section == "backpack_content" && item.Contents is not null) return false;
        var size = section is "back" or "hip" ? SectionSize(section) : IsEquipmentSection(section) || IsLimbSection(section) ? (item.Width, item.Height) : (target?.Width ?? 0, target?.Height ?? 0);
        if (owner is null || target is null || !owner.Items.Contains(item) || x < 0 || y < 0 || item.Width <= 0 || item.Height <= 0 ||
            x > size.Item1 - item.Width || y > size.Item2 - item.Height || item.Quantity > target.LimitOf(item)) return false;
        if ((IsEquipmentSection(section) || IsLimbSection(section)) && !item.WearableBy(wearer?.StringId)) return false;
        if (wearer?.BlocksSection(section) == true) return false;
        if (IsEquipmentSection(section))
        {
            bool allowed = section switch
            {
                "back" or "hip" => item.Type is Meitou.Data.Fcs.FcsRecordType.WEAPON or Meitou.Data.Fcs.FcsRecordType.CROSSBOW,
                "backpack_attach" => item.Type == Meitou.Data.Fcs.FcsRecordType.CONTAINER && item.Contents is not null,
                _ => item.Type is Meitou.Data.Fcs.FcsRecordType.ARMOUR or Meitou.Data.Fcs.FcsRecordType.ITEM && item.EquipmentSection == section,
            };
            if (!allowed || item.Quantity != 1) return false;
            if (section is not ("back" or "hip") && (x != 0 || y != 0 || target.Items.Any(i => i != item && i.Section == section))) return false;
        }
        // A prosthetic slot holds one LIMB_REPLACEMENT; whether it fits the limb is the caller's check (PlayerState).
        if (IsLimbSection(section) && (item.Type != Meitou.Data.Fcs.FcsRecordType.LIMB_REPLACEMENT || item.Quantity != 1 || x != 0 || y != 0 || target.Items.Any(i => i != item && i.Section == section))) return false;
        foreach (var other in target.Items)
            if (other != item && other.Section == section && x < other.X + other.Width && other.X < x + item.Width && y < other.Y + other.Height && other.Y < y + item.Height) return false;
        if (owner != target) { owner.Items.Remove(item); target.Items.Add(item); }
        if (into != from) item.Death = false;
        item.Section = section; item.X = x; item.Y = y; return true;
    }
    /// <summary>
    /// ARRANGE (<c>FUN_14070f740</c>, <b>Observed</b>): every grid section (the main grid and the worn bag's) is emptied and its items added again through the auto-add
    /// (<see cref="Inventory.AddTo"/>: partial stacks merge) in a sorted order: first by the longer side of the footprint, then by its area, then by its width, largest first (a stable sort);
    /// the first order in which everything fits is kept, otherwise every item goes back where it was (false).
    /// </summary>
    public static bool Arrange(Inventory root)
    {
        var grids = new List<(Inventory Grid, string Section, ItemInstance[] Items)>();
        foreach (var (grid, section) in new[] { (root, "main"), (root.Backpack?.Contents, "backpack_content") })
            if (grid is not null) grids.Add((grid, section, [.. grid.Items]));
        var saved = grids.SelectMany(g => g.Items).Select(i => (Item: i, i.X, i.Y, i.Quantity, i.Charges)).ToArray();
        void Restore()
        {
            foreach (var g in grids) { g.Grid.Items.Clear(); g.Grid.Items.AddRange(g.Items); }
            foreach (var old in saved) (old.Item.X, old.Item.Y, old.Item.Quantity, old.Item.Charges) = (old.X, old.Y, old.Quantity, old.Charges);
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
                    if (item.Width <= 0 || item.Height <= 0 || !grid.AddTo(section, item)) { fits = false; break; }
                if (!fits) break;
            }
            if (fits) return true;
            Restore();
        }
        return false;
    }

    /// <summary>
    /// The types that never stack (<c>FUN_14075e090</c>): WEAPON, ARMOUR, CONTAINER, MAP_ITEM, CROSSBOW, LIMB_REPLACEMENT; the others stack up to their record's <c>stackable</c>.
    /// </summary>
    public static bool NeverStacks(FcsRecordType type) =>
        type is FcsRecordType.WEAPON or FcsRecordType.ARMOUR or FcsRecordType.CONTAINER or FcsRecordType.MAP_ITEM or FcsRecordType.CROSSBOW or FcsRecordType.LIMB_REPLACEMENT;

    /// <summary>
    /// Whether a held item joins the stack it is dropped on (<c>FUN_140715560</c>): same record (so type), material and quality, of a type that stacks; Meitou also keeps stolen and death
    /// items apart, as <see cref="Inventory.Add"/> does.
    /// </summary>
    public static bool Stackable(ItemInstance a, ItemInstance b) => !NeverStacks(a.Type) && a.Record == b.Record && a.MaterialId == b.MaterialId && a.Quality == b.Quality
        && a.StolenFrom == b.StolenFrom && a.Death == b.Death && a.Contents is null && b.Contents is null;

    /// <summary>
    /// Moves <paramref name="units"/> of <paramref name="from"/> onto the stack <paramref name="into"/>. Partial charges combine: a stack is full units and one partly used one (its
    /// <see cref="ItemInstance.Charges"/>), so when the last of <paramref name="from"/> moves, its part joins <paramref name="into"/>'s; two parts that fit in one unit make one.
    /// </summary>
    public static void Merge(ItemInstance into, ItemInstance from, int units)
    {
        if (units <= 0) return;
        bool last = units >= from.Quantity;
        into.Quantity += units;
        from.Quantity -= units;
        if (!last || into.MaxCharges <= 0) return;
        float combined = into.Charges + from.Charges;
        if (combined <= into.MaxCharges) { into.Quantity--; into.Charges = combined; }
        else into.Charges = combined - into.MaxCharges;
    }

    /// <summary>
    /// Whether a section takes an item, the section half of <c>FUN_14074bc30</c>: the grids (<c>main</c>, <c>backpack_content</c>) anything, but a bag only empty into a bag; a weapon slot
    /// (<c>back</c>, <c>hip</c>) a WEAPON or CROSSBOW; <c>backpack_attach</c> a CONTAINER; a prosthetic slot a LIMB_REPLACEMENT; a clothing slot the ARMOUR or belt ITEM of that slot.
    /// Before the slot type, a section other than the grids checks the wearer's race (<paramref name="wearable"/> false: "Can't wear that item."; <c>RaceLimiter</c>). Null: taken; otherwise the refusal ("" silent).
    /// </summary>
    public static string? SlotRefusal(string section, FcsRecordType type, string equipmentSection, Inventory? contents, bool wearable = true) =>
        SlotRefusal(section, type, equipmentSection, contents?.Items.Count ?? -1, wearable);

    /// <summary>As <see cref="SlotRefusal(string, FcsRecordType, string, Inventory?, bool)"/> with the number of items in a bag (-1: the item is not a bag).</summary>
    public static string? SlotRefusal(string section, FcsRecordType type, string equipmentSection, int bagItems, bool wearable = true)
    {
        if (section == "backpack_content") return bagItems > 0 ? "Container cannot be placed here unless it is empty." : null;
        if (section == "main") return null;
        if (!wearable) return "Can't wear that item.";
        bool takes = section switch
        {
            "back" or "hip" => type is FcsRecordType.WEAPON or FcsRecordType.CROSSBOW,
            "backpack_attach" => type == FcsRecordType.CONTAINER && bagItems >= 0,
            _ when IsLimbSection(section) => type == FcsRecordType.LIMB_REPLACEMENT,
            _ when IsEquipmentSection(section) => type is FcsRecordType.ARMOUR or FcsRecordType.ITEM && equipmentSection == section,
            _ => false,
        };
        return takes ? null : "Invalid container type.";
    }

    /// <summary>A section holding one item whatever its footprint (clothing, the bag slot, the prosthetic slots); the others are grids.</summary>
    public static bool IsSingleSlot(string section) => IsLimbSection(section) || IsEquipmentSection(section) && section is not ("back" or "hip");

    /// <summary>
    /// The right click's move into another character (<c>FUN_140713d20</c>, its target <c>FUN_14070dcd0</c>): one unit at a time, at most <paramref name="count"/> (0: until one fails), each
    /// into the target's open bag window first (<paramref name="bagFirst"/>), then its character window: the slots of the item's type (<see cref="TryEquip"/>), then the main grid, stacks of it
    /// first (<see cref="Inventory.AddTo"/>). Leaving its owner a unit is no longer a death item. Returns the units moved; <paramref name="equipped"/>: one was worn.
    /// </summary>
    public static int MoveInto(Inventory owner, ItemInstance item, int count, Inventory into, bool bagFirst, out bool equipped, RaceData? wearer = null)
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
            bool placed = bagFirst && into.Backpack?.Contents is { } bag && bag.AddTo("backpack_content", unit) || (worn = TryEquip(into, unit, wearer)) || into.AddTo("main", unit);
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
