using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>What an item record says about carrying and eating it (docs/game/economy.md "item instances", character-stats.md "Encumbrance and carrying").</summary>
public sealed record ItemInfo(string Id, string Name, FcsRecordType Type)
{
    /// <summary>The record's name in the data files (<see cref="GameRecord.BaseName"/>): for finding an item by its English name, whatever the language.</summary>
    public string BaseName { get => _baseName ?? Name; init => _baseName = value; }
    string? _baseName;
    /// <summary><c>weight kg</c>.</summary>
    public float Weight { get; init; }
    /// <summary><c>inventory footprint width</c> / <c>height</c>: cells in a grid.</summary>
    public int Width { get; init; } = 1;
    public int Height { get; init; } = 1;
    /// <summary>
    /// How many units one inventory cell holds: ITEM <c>stackable</c>, an int ("number that can be stacked in inventory", default 1 in <c>fcs.def</c>; 1 means it does not stack). Weapons,
    /// armour, containers, map items, crossbows and limbs are always 1 (the exe's stack limit returns 1 for those types before it reads the field). Below 1 counts as 1 (engine; the data has no 0).
    /// </summary>
    public int StackLimit { get; init; } = 1;
    /// <summary>A CONTAINER's <c>stackable bonus minimum</c> and <c>stackable bonus mult</c>: inside it an item's stack limit is trunc(max(limit, minimum) x mult) (1 and 1 for other items).</summary>
    public int StackBonusMinimum { get; init; } = 1;
    public float StackBonusMult { get; init; } = 1;
    /// <summary>
    /// The item function a made item holds (<see cref="FunctionOf"/>): an ITEM's <c>item function</c> (3 food, 15 raw meat, 8 drugs, 1 first aid kit, ...; 7 reads as 0, and 3 with a
    /// single charge as 0), 5 for a weapon, 6 for armour, 4 for a container, else 0.
    /// </summary>
    public int Function { get; init; }
    /// <summary>The record's <c>charges</c> before food scaling, at least 1 (the constructor lifts anything below 1 to 1).</summary>
    public float Charges { get; init; } = 1;
    public int Value { get; init; }
    /// <summary>
    /// <c>trade item</c>: sells for its full price unless stolen (docs/game/economy.md section 5). Cleared for the food that <see cref="ScalesCharges"/> (the constructor drops it with the
    /// scaling).
    /// </summary>
    public bool TradeItem { get; init; }
    /// <summary>
    /// The record's <c>quality</c> (1 to 100). For a first aid kit this is the quality that caps the medic's skill in the bandaging rate (character-stats.md "First aid, medkits and robot
    /// repair"). It is read from the record when needed and is not copied to <see cref="ItemInstance.Quality"/>, which is the clothing grade.
    /// </summary>
    public float Quality { get; init; }
    /// <summary>A CONTAINER's <c>storage size width</c> / <c>height</c> (cells of its contents grid; 0 for other items).</summary>
    public int StorageWidth { get; init; }
    public int StorageHeight { get; init; }
    /// <summary>A CONTAINER's <c>encumbrance effect</c>: the factor on the weight of its contents (1 for other items).</summary>
    public float EncumbranceEffect { get; init; } = 1;
    /// <summary>
    /// Who may wear it (the original's <c>RaceLimiter</c>, <c>FUN_140763150</c>, <b>Verified</b> by decompile): a non-empty <c>races</c> list names the only races, and then <c>races exclude</c>
    /// is not read; otherwise <c>races exclude</c> names the races that may not. RACE string ids; empty: anyone but an animal, which needs its race in <c>races</c>
    /// (<c>RaceLimiter::canEquip</c> <c>FUN_140593f00</c> and <c>Limiter::canEquip</c> <c>FUN_1405936b0</c>, <b>Verified</b> by decompile; checked by sections other than type 7, <c>FUN_14074bc30</c>).
    /// </summary>
    public string[] WornOnlyBy { get; init; } = [];
    public string[] WornNeverBy { get; init; } = [];

    /// <summary>
    /// A food item: an ITEM with <c>item function</c> 3 (rice, bread, fish, dried meat...). Raw meat (15) is not eaten by people by this rule (race <c>special food</c> would say,
    /// <b>Unknown</b>).
    /// </summary>
    public bool IsFood => Type == FcsRecordType.Item && Function == 3;

    /// <summary>An ordinary first aid kit (<c>item function</c> 1): bandages organic body parts (<b>Observed</b>, character-stats.md).</summary>
    public const int FirstAidKitFunction = 1;
    /// <summary>A splint kit (<c>item function</c> 2, <b>Verified</b>: <c>hasSplintKit</c> and the Splint Kit records): splints limbs (character-stats.md "Splints").</summary>
    public const int SplintKitFunction = 2;
    /// <summary>A robot repair kit (<c>item function</c> 12): repairs robotic parts.</summary>
    public const int RobotKitFunction = 12;

    /// <summary>
    /// The item constructor's charges and function (<c>FUN_14075faf0</c>, then <c>FUN_140763810</c> for a container, <b>Verified</b>): charges below 1 become 1; a weapon holds function 5, armour 6, a
    /// container 4 (set after the base constructor), an ITEM its record's <c>item function</c>; function 7 reads as 0 (not the container's 4); food (3) with a single charge is no food (0), a
    /// raw meat (15) of one charge stays.
    /// </summary>
    public static (int Function, float Charges) FunctionOf(GameRecord r)
    {
        float charges = Math.Max(r.GetFloat("charges", 1), 1);
        if (r.Type == FcsRecordType.Container) return (4, charges);
        int function = r.Type switch { FcsRecordType.Weapon => 5, FcsRecordType.Armour => 6, FcsRecordType.Item => r.GetInt("item function"), _ => 0 };
        if (function == 7) function = 0;
        if (function == 3 && charges <= 1) function = 0;
        return (function, charges);
    }

    /// <summary>
    /// Whether the item's charges are scaled by <c>food quality mult</c> at construction (<c>FUN_14075faf0</c>, <b>Verified</b>): an ITEM of function 3 or 15 with more than one charge. Its
    /// <c>trade item</c> flag is cleared and its current charges start at the scaled maximum.
    /// </summary>
    public bool ScalesCharges => Scales(Type, Function, Charges);

    static bool Scales(FcsRecordType type, int function, float charges) => type == FcsRecordType.Item && function is 3 or 15 && charges > 1;

    public static ItemInfo From(GameRecord r)
    {
        var (function, charges) = FunctionOf(r);
        return new(r.StringId, r.Name, r.Type)
        {
            BaseName = r.BaseName,
            Weight = r.GetFloat("weight kg"),
            Width = Math.Max(r.GetInt("inventory footprint width", 1), 1),
            Height = Math.Max(r.GetInt("inventory footprint height", 1), 1),
            StackLimit = StackLimitOf(r),
            Function = function,
            Charges = charges,
            Value = r.GetInt("value"),
            TradeItem = r.GetBool("trade item") && !Scales(r.Type, function, charges),
            Quality = r.GetFloat("quality"),
            StorageWidth = r.GetInt("storage size width"),
            StorageHeight = r.GetInt("storage size height"),
            EncumbranceEffect = r.Type == FcsRecordType.Container ? Math.Max(r.GetFloat("encumbrance effect", 1), 0) : 1,
            StackBonusMinimum = r.Type == FcsRecordType.Container ? r.GetInt("stackable bonus minimum", 1) : 1,
            StackBonusMult = r.Type == FcsRecordType.Container ? r.GetFloat("stackable bonus mult", 1) : 1,
            WornOnlyBy = RaceLimiter.Of(r).Only,
            WornNeverBy = RaceLimiter.Of(r).Never,
        };
    }

    /// <summary>The stack limit of an item record (<see cref="StackLimit"/>): 1 for the types that never stack, else the record's <c>stackable</c> (at least 1).</summary>
    public static int StackLimitOf(GameRecord r) => r.Type is FcsRecordType.Weapon or FcsRecordType.Armour or FcsRecordType.Container or FcsRecordType.MapItem
        or FcsRecordType.Crossbow or FcsRecordType.LimbReplacement ? 1 : Math.Max(r.GetInt("stackable", 1), 1);

    /// <summary>
    /// The stack limit of an item inside a container with the given bonus (the exe's <c>FUN_1407466b0</c>, <b>Observed</b>): trunc(max(limit, minimum) x mult), at least 1. The types that
    /// never stack keep 1 (the caller checks them first, as the exe does). The function's third input, a flag of the inventory section that forces the base to 1, is not read here
    /// (<b>Unknown</b> which sections set it).
    /// </summary>
    public static int StackLimitIn(int limit, int bonusMinimum, float bonusMult) => Math.Max((int)(Math.Max(limit, bonusMinimum) * bonusMult), 1);
}