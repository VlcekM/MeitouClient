using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>Makes item instances and the inventories of new characters from the game data.</summary>
public sealed class ItemFactory(GameDatabase db, GameConstants constants)
{
    readonly ConcurrentDictionary<string, ItemInfo?> infos = new();

    public GameDatabase Db { get; } = db;
    public GameConstants Constants { get; } = constants;

    public ItemInfo? Info(string recordId) => infos.GetOrAdd(recordId, id => Db.Find(id) is { } r ? ItemInfo.From(r) : null);

    /// <summary>The cheapest food in the data (lowest <c>value</c> per nutrition point, ties by id): the ration an NPC without food is given.</summary>
    public ItemInstance? CheapestFood() => Create(CheapestFoodId() ?? "");

    readonly Lazy<string?> cheapestFoodId = new(() => db.OfType(FcsRecordType.ITEM).Select(ItemInfo.From).Where(i => i.IsFood && i.Charges > 0)
        .OrderBy(i => i.Value / i.Charges).ThenBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault()?.Id);   // the factory is shared with the build thread, hence Lazy

    string? CheapestFoodId() => cheapestFoodId.Value;

    /// <summary>A new instance of an item record (null for an unknown record). Food gets its charges scaled by <c>food quality mult</c>; weapons weigh <c>weapon inventory weight mult</c> of their weight.</summary>
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
