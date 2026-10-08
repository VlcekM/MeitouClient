using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>What an item record says about carrying and eating it (docs/game/economy.md "item instances", character-stats.md "Encumbrance and carrying").</summary>
public sealed record ItemInfo(string Id, string Name, FcsRecordType Type)
{
    /// <summary><c>weight kg</c>.</summary>
    public float Weight { get; init; }
    /// <summary><c>inventory footprint width</c> / <c>height</c>: cells in a grid.</summary>
    public int Width { get; init; } = 1;
    public int Height { get; init; } = 1;
    public bool Stackable { get; init; }
    /// <summary><c>item function</c> (3 food, 15 raw meat, 8 drugs, 1 first aid kit, ...).</summary>
    public int Function { get; init; }
    /// <summary>The record's <c>charges</c> (default 1).</summary>
    public float Charges { get; init; } = 1;
    public int Value { get; init; }
    /// <summary>A CONTAINER's <c>storage size width</c> / <c>height</c> (cells of its contents grid; 0 for other items).</summary>
    public int StorageWidth { get; init; }
    public int StorageHeight { get; init; }
    /// <summary>A CONTAINER's <c>encumbrance effect</c>: the factor on the weight of its contents (1 for other items).</summary>
    public float EncumbranceEffect { get; init; } = 1;

    /// <summary>
    /// A food item: an ITEM with <c>item function</c> 3 (rice, bread, fish, dried meat...). Raw meat (15) is not eaten by people by this rule (race <c>special food</c> would say,
    /// <b>Unknown</b>).
    /// </summary>
    public bool IsFood => Type == FcsRecordType.ITEM && Function == 3;

    public static ItemInfo From(GameRecord r) => new(r.StringId, r.Name, r.Type)
    {
        Weight = r.GetFloat("weight kg"),
        Width = Math.Max(r.GetInt("inventory footprint width", 1), 1),
        Height = Math.Max(r.GetInt("inventory footprint height", 1), 1),
        Stackable = r.GetBool("stackable"),
        Function = r.GetInt("item function"),
        Charges = r.GetFloat("charges", 1),
        Value = r.GetInt("value"),
        StorageWidth = r.GetInt("storage size width"),
        StorageHeight = r.GetInt("storage size height"),
        EncumbranceEffect = r.Type == FcsRecordType.CONTAINER ? r.GetFloat("encumbrance effect", 1) : 1,
    };
}
