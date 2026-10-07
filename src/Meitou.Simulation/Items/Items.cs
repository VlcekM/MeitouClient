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

/// <summary>
/// A character's inventory: its items in sections, with their grid cells. The main section is a grid of <see cref="MainWidth"/> x <see cref="MainHeight"/> cells (an engine
/// choice: the size of a character's own pockets is <b>Unknown</b>); a backpack's contents are an inventory of the CONTAINER's storage size. Only serial phases change it.
/// </summary>
public sealed class Inventory(int width, int height)
{
    public const int MainWidth = 8, MainHeight = 6;

    public Inventory() : this(MainWidth, MainHeight) { }

    public int Width { get; } = width;
    public int Height { get; } = height;
    public List<ItemInstance> Items { get; } = [];

    /// <summary>The worn backpack, if any.</summary>
    public ItemInstance? Backpack => Items.Find(i => i.Section == "backpack_attach");

    /// <summary>The weight of everything in this inventory (not including a carried person).</summary>
    public float ContentWeight()
    {
        float w = 0;
        foreach (var i in Items) w += i.Weight;
        return w;
    }

    /// <summary>First cell where a w x h footprint fits among the items of <paramref name="section"/>, or false.</summary>
    public bool FindCell(string section, int w, int h, out int x, out int y)
    {
        for (y = 0; y + h <= Height; y++)
            for (x = 0; x + w <= Width; x++)
                if (Free(section, x, y, w, h)) return true;
        x = y = 0;
        return false;
    }

    bool Free(string section, int x, int y, int w, int h)
    {
        foreach (var i in Items)
        {
            if (i.Section != section) continue;
            if (x < i.X + i.Width && i.X < x + w && y < i.Y + i.Height && i.Y < y + h) return false;
        }
        return true;
    }

    /// <summary>
    /// Puts an item in: a stackable item joins a stack of the same record and quality; otherwise it takes the first free cell of <c>main</c>, then of the backpack. False when
    /// there is no room (the caller drops the item).
    /// </summary>
    public bool Add(ItemInstance item)
    {
        if (item.Stackable)
            foreach (var other in Items)
                if (other.Record == item.Record && other.Stackable && other.Quality == item.Quality && other.Section is "main" or "backpack_content")
                {
                    other.Quantity += item.Quantity;
                    return true;
                }
        if (FindCell("main", item.Width, item.Height, out int x, out int y))
        {
            item.Section = "main";
            item.X = x;
            item.Y = y;
            Items.Add(item);
            return true;
        }
        if (Backpack?.Contents is { } bag && bag.FindCell("backpack_content", item.Width, item.Height, out x, out y))
        {
            item.Section = "backpack_content";
            item.X = x;
            item.Y = y;
            bag.Items.Add(item);
            return true;
        }
        return false;
    }

    /// <summary>Every item of this inventory and of the backpack, in order.</summary>
    public IEnumerable<ItemInstance> All()
    {
        foreach (var i in Items)
        {
            yield return i;
            if (i.Contents is { } bag) foreach (var inner in bag.All()) yield return inner;
        }
    }

    /// <summary>Removes one unit of an item (the whole item when it is the last), wherever it is.</summary>
    public bool RemoveOne(ItemInstance item)
    {
        if (Items.Contains(item))
        {
            if (item.Quantity > 1) item.Quantity--;
            else Items.Remove(item);
            return true;
        }
        foreach (var i in Items)
            if (i.Contents is { } bag && bag.RemoveOne(item)) return true;
        return false;
    }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Items.Count);
        foreach (var i in Items)
        {
            h.Add(Rng.StableHash(i.Record));
            h.Add(Rng.StableHash(i.Section));
            h.Add(i.X);
            h.Add(i.Y);
            h.Add(i.Quantity);
            h.Add(i.Level);
            h.Add(i.Quality);
            h.Add(i.Charges);
            i.Contents?.Hash(ref h);
        }
    }
}

/// <summary>Makes item instances and the inventories of new characters from the game data.</summary>
public sealed class ItemFactory(GameDatabase db, GameConstants constants)
{
    readonly ConcurrentDictionary<string, ItemInfo?> infos = new();

    public GameDatabase Db { get; } = db;
    public GameConstants Constants { get; } = constants;

    public ItemInfo? Info(string recordId) => infos.GetOrAdd(recordId, id => Db.Find(id) is { } r ? ItemInfo.From(r) : null);

    /// <summary>A new instance of an item record (null for an unknown record). Food gets its charges scaled by <c>food quality mult</c>; weapons weigh <c>weapon inventory weight mult</c> of their weight.</summary>
    ItemInfo? cheapest;
    bool cheapestDone;

    /// <summary>The cheapest food in the data (lowest <c>value</c> per nutrition point, ties by id): the ration an NPC without food is given.</summary>
    public ItemInstance? CheapestFood() => Create(CheapestFoodId() ?? "");

    string? CheapestFoodId()
    {
        if (!cheapestDone)
        {
            cheapest = Db.OfType(FcsRecordType.ITEM).Select(ItemInfo.From).Where(i => i.IsFood && i.Charges > 0)
                .OrderBy(i => i.Value / i.Charges).ThenBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault();
            cheapestDone = true;
        }
        return cheapest?.Id;
    }

    public ItemInstance? Create(string recordId, int quantity = 1, float quality = 0)
    {
        if (Info(recordId) is not { } info) return null;
        bool weapon = info.Type is FcsRecordType.WEAPON or FcsRecordType.CROSSBOW;
        return new ItemInstance
        {
            Record = info.Id,
            Name = info.Name,
            Type = info.Type,
            Quantity = Math.Max(quantity, 1),
            Quality = quality,
            ItemFunction = info.Function,
            Charges = info.IsFood ? info.Charges * Constants.FoodQualityMult : info.Charges,
            Width = info.Width,
            Height = info.Height,
            UnitWeight = weapon ? info.Weight * Constants.WeaponInventoryWeightMult : info.Weight,
            Stackable = info.Stackable,
            IsFood = info.IsFood,
            Contents = info.Type == FcsRecordType.CONTAINER ? new Inventory(Math.Max(info.StorageWidth, 1), Math.Max(info.StorageHeight, 1)) : null,
            ContentsFactor = info.EncumbranceEffect,
        };
    }

    /// <summary>
    /// The inventory of a new character: what the generator rolled to wear and carry (clothing in its slot's section, a backpack, the crossbow and weapons at hip/back), then the CHARACTER
    /// record's <c>inventory</c> list (an ITEM reference whose first value is the quantity; entries with 0 never spawn, <b>Observed</b>).
    /// </summary>
    public Inventory Build(GameRecord? character, Meitou.Data.Characters.Loadout? loadout)
    {
        var inventory = new Inventory();
        if (loadout is not null)
        {
            foreach (var c in loadout.Clothing)
                if (Create(c.Record.StringId, 1, c.Quality) is { } cloth)
                {
                    cloth.Section = SectionOf(c.Record);
                    cloth.MaterialId = c.Material?.StringId ?? "";
                    inventory.Items.Add(cloth);
                }
            if (loadout.Backpack is { } b && Create(b.Record.StringId, 1, b.Quality) is { } pack)
            {
                pack.Section = "backpack_attach";
                inventory.Items.Add(pack);
            }
            if (loadout.Crossbow is { } x) AddWeapon(inventory, x);
            foreach (var w in loadout.Weapons) AddWeapon(inventory, w);
        }
        if (character is not null)
            foreach (var r in character.GetReferences("inventory"))
            {
                if (r.Values.Value0 <= 0) continue;
                if (Create(r.TargetStringId, r.Values.Value0) is { } item) inventory.Add(item);
            }
        return inventory;
    }

    void AddWeapon(Inventory inventory, Meitou.Data.Characters.LoadoutWeapon w)
    {
        if (Create(w.Weapon.StringId, 1) is not { } item) return;
        item.Section = w.Section;
        item.Y = w.Row;
        item.Level = w.Level;
        item.MaterialId = w.Model?.StringId ?? "";
        item.CompanyId = w.Manufacturer?.StringId ?? "";
        inventory.Items.Add(item);
    }

    /// <summary>The section a worn ARMOUR goes to by its <c>slot</c> (the save's section names; belt is <b>Unknown</b> and uses <c>main</c>).</summary>
    static string SectionOf(GameRecord armour) => (Meitou.Data.Characters.AttachSlot)armour.GetInt("slot") switch
    {
        Meitou.Data.Characters.AttachSlot.Shirt => "shirt",
        Meitou.Data.Characters.AttachSlot.Hat => "head",
        Meitou.Data.Characters.AttachSlot.Boots => "boots",
        Meitou.Data.Characters.AttachSlot.Body => "armour",
        Meitou.Data.Characters.AttachSlot.Legs => "legs",
        _ => "main",
    };
}

/// <summary>A text listing of an inventory for the debug HUD.</summary>
public static class InventoryText
{
    public static IReadOnlyList<string> Lines(Inventory inventory)
    {
        var lines = new List<string> { $"Carries {inventory.ContentWeight():0.0} kg" };
        foreach (var i in inventory.All())
            lines.Add($"{(i.Quantity > 1 ? i.Quantity + " x " : "")}{i.Name} ({i.Section} {i.X},{i.Y}){(i.IsFood ? $" food {i.Nutrition:0.00}" : "")}");
        return lines;
    }
}
