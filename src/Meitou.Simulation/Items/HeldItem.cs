namespace Meitou.Simulation.Items;

/// <summary>Whose items an item window shows: a character's inventory (the player's, or the open loot's body), the open trade's stock, or a building's machine or container.</summary>
public enum ItemHolder : byte { Character, Stock, Site }

/// <summary>
/// An item window as the held-item commands address it (docs/game/ui-screens.md section 6 "Holding an item"): <see cref="Character"/> for <see cref="ItemHolder.Character"/>,
/// <see cref="Site"/> (a machine's site or a container's fixture id) for <see cref="ItemHolder.Site"/>.
/// </summary>
public readonly record struct ItemOwner(ItemHolder Holder, CharacterId Character, string Site)
{
    public static ItemOwner Of(CharacterId character) => new(ItemHolder.Character, character, "");
    public static ItemOwner Stock => new(ItemHolder.Stock, CharacterId.None, "");
    public static ItemOwner AtSite(string site) => new(ItemHolder.Site, CharacterId.None, site);
}

/// <summary>
/// The item on the mouse (the original's single <c>MouseInventory</c> item, <c>FUN_14070e640</c>): taken out of its window, with where it came from so it can go back
/// (<see cref="PlayerSystem.ReturnHeldItem"/>, <c>FUN_14070d860</c>). <see cref="Grid"/> is the root inventory it was in (a character's, the stock, a storage or container grid; null for a
/// machine's input or output slot, addressed by <see cref="From"/>'s site); <see cref="Section"/>, <see cref="X"/>, <see cref="Y"/> its place there. <see cref="Actor"/> is the character
/// whose inventory window holds it (the buyer, the looter, the one in reach).
/// </summary>
public sealed class HeldItem
{
    public required ItemInstance Item { get; init; }
    public required ItemOwner From { get; init; }
    public Inventory? Grid { get; init; }
    public required string Section { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public CharacterId Actor { get; init; } = CharacterId.None;
}

/// <summary>
/// The held item as the interface draws it (published with the snapshot): its icon's record and material, its footprint and count, its charge bar, its type and armour slot (which
/// sections take it), the window it came from and, for a bag, how many items are in it (-1: not a bag).
/// </summary>
public sealed record HeldItemView(string Record, string Name, string Material, Meitou.Data.Fcs.FcsRecordType Type, string EquipmentSection, int Quantity, int Width, int Height,
    float? Charge, ItemOwner From, int BagItems)
{
    public static HeldItemView Of(HeldItem held) => new(held.Item.Record, held.Item.Name, held.Item.MaterialId, held.Item.Type, held.Item.EquipmentSection, held.Item.Quantity,
        held.Item.Width, held.Item.Height, held.Item.ChargeFraction, held.From, held.Item.Contents?.Items.Count ?? -1) { WornOnlyBy = held.Item.WornOnlyBy, WornNeverBy = held.Item.WornNeverBy, Colour = held.Item.ColourId };

    /// <summary>The COLOR_DATA it is dyed with (<see cref="ItemInstance.ColourId"/>).</summary>
    public string Colour { get; init; } = "";
    /// <summary>The held item's race limits (<see cref="ItemInstance.WornOnlyBy"/>, <see cref="ItemInstance.WornNeverBy"/>).</summary>
    public string[] WornOnlyBy { get; init; } = [];
    public string[] WornNeverBy { get; init; } = [];
    /// <summary>Whether a character of the RACE <paramref name="race"/> may wear it (null: no wearer).</summary>
    public bool WearableBy(string? race, bool animal = false) => ItemInstance.Wearable(WornOnlyBy, WornNeverBy, race, animal);
}

/// <summary>
/// A command on the held item. <see cref="Serial"/> is the interface's own number for it: the world publishes the last one it applied (<see cref="WorldSnapshot.HeldSerial"/>), so the
/// interface knows when its picture of the held item is the world's again.
/// </summary>
public abstract record HeldCommand : SimCommand
{
    public long Serial { get; init; }
}

/// <summary>
/// Lifts <see cref="Item"/> out of <see cref="From"/> onto the mouse (<c>FUN_1407139a0</c>): the whole stack with <see cref="Whole"/> (Shift or Ctrl) or when it is a single unit, else one unit.
/// For a machine's slots the token's section is <c>in1</c> / <c>in2</c> (an input) or <c>out</c> (the output, or the crafted item at the token's index); a storage or container grid is
/// <c>main</c>. Nothing while an item is held already.
/// </summary>
public sealed record LiftItem(CharacterId Actor, ItemOwner From, InventoryItemToken Item, bool Whole) : HeldCommand;

/// <summary>
/// Places the held item into <see cref="Into"/>'s <see cref="Section"/> with its top-left at (<see cref="X"/>, <see cref="Y"/>) (<c>FUN_140715560</c>): moved inside the section, merged with
/// the one stack under it or swapped with the one item under it; refused, it goes back (<see cref="ReturnHeld"/>). Between the stock and the customer it is a purchase or a sale.
/// </summary>
public sealed record PlaceHeld(ItemOwner Into, string Section, int X, int Y) : HeldCommand;

/// <summary>Gives the held item to <see cref="Target"/>, a character in the world (<c>FUN_1407136a0</c>): added where it fits in its inventory.</summary>
public sealed record GiveHeld(CharacterId Target) : HeldCommand;

/// <summary>Drops the held item on the ground (<c>FUN_1407136a0</c>), when its window allows it (the player's own); otherwise it goes back.</summary>
public sealed record DropHeld : HeldCommand;

/// <summary>Puts the held item back where it came from (<c>FUN_14070d860</c>).</summary>
public sealed record ReturnHeld : HeldCommand;

/// <summary>
/// A right click on an item of a player character's own window with no other owner's window open (<c>FUN_140714a20</c>): a money item (function 14) is turned into cats; a grid item goes
/// to the slot that takes it (equipped), a worn one to the grid, else a prosthetic to its limb, else between the grid and the bag (<see cref="BagOpen"/>). One unit, all with <see cref="All"/>.
/// With <see cref="Into"/> (another player character's window open, a pinned one) it moves there instead, as into a loot (<see cref="InventoryCommands.MoveInto"/>).
/// </summary>
public sealed record QuickMoveItem(CharacterId Character, InventoryItemToken Item, bool All, bool BagOpen) : SimCommand
{
    /// <summary>The player character whose window takes the item; <see cref="CharacterId.None"/> for a move within the character's own windows.</summary>
    public CharacterId Into { get; init; } = CharacterId.None;
    /// <summary>The <see cref="Into"/> character's bag window is open: the item goes there first.</summary>
    public bool IntoBag { get; init; }
}
