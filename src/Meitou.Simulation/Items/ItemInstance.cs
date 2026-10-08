using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>
/// One item a character carries (an INVENTORY_ITEM_STATE of a save). The fields keep the save's names (docs/formats/save.md): <c>base data sid</c> = <see cref="Record"/>,
/// <c>material sid</c>, <c>company sid</c>, <c>section</c>, <c>inventory x</c> / <c>inventory y</c>, <c>quantity</c>, <c>level</c>, <c>item function</c>, <c>quality</c>, <c>charges</c>.
/// </summary>
public sealed class ItemInstance
{
    public required string Record { get; init; }
    public string Name { get; init; } = "";
    public FcsRecordType Type { get; init; }
    public string MaterialId { get; set; } = "";
    public string CompanyId { get; set; } = "";
    /// <summary>Where it is: <c>main</c>, <c>shirt</c>, <c>head</c>, <c>boots</c>, <c>armour</c>, <c>legs</c>, <c>hip</c>, <c>back</c>, <c>backpack_attach</c> (a worn backpack), <c>backpack_content</c> (inside it).</summary>
    public string Section { get; set; } = "main";
    public int X { get; set; }
    public int Y { get; set; }
    public int Quantity { get; set; } = 1;
    public int Level { get; set; }
    public int ItemFunction { get; set; }
    /// <summary>Quality 0 to 100 (clothing from the character's armour grade).</summary>
    public float Quality { get; set; }
    /// <summary>Charges left; for food the record's charges times CONSTANTS <c>food quality mult</c>, which is what is eaten (100 points are one hunger level).</summary>
    public float Charges { get; set; } = 1;
    public int Width { get; init; } = 1;
    public int Height { get; init; } = 1;
    /// <summary>Weight of one unit in kg as it counts in the inventory total (weapons already scaled by <c>weapon inventory weight mult</c>).</summary>
    public float UnitWeight { get; init; }
    public bool Stackable { get; init; }
    public bool IsFood { get; init; }
    /// <summary>A worn backpack's contents (null for other items).</summary>
    public Inventory? Contents { get; init; }
    /// <summary>The factor of the contents' weight (a bag's <c>encumbrance effect</c>).</summary>
    public float ContentsFactor { get; init; } = 1;

    /// <summary>The weight this item and what is in it add to the load.</summary>
    public float Weight => UnitWeight * Quantity + (Contents?.ContentWeight() ?? 0) * ContentsFactor;

    /// <summary>The hunger levels eating the item gives: <see cref="Charges"/> / 100.</summary>
    public float Nutrition => IsFood ? Charges / 100f : 0;
}
