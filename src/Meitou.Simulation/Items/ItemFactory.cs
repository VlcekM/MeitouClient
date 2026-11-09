using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>
/// Makes item instances and the inventories of new characters from the game data.
/// </summary>
public sealed class ItemFactory(GameDatabase db, GameConstants constants, Func<Meitou.Data.Gameplay.Combat.CombatDatabase>? combat = null)
{
    readonly ConcurrentDictionary<string, ItemInfo?> _infos = new();

    public GameDatabase Db { get; } = db;
    public GameConstants Constants { get; } = constants;

    public ItemInfo? Info(string recordId) => _infos.GetOrAdd(recordId, id => Db.Find(id) is { } r ? ItemInfo.From(r) : null);

    readonly ConcurrentDictionary<string, Meitou.Data.Gameplay.Bodies.LimbReplacement?> _limbs = new(StringComparer.Ordinal);
    /// <summary>
    /// The prosthetic data of a LIMB_REPLACEMENT record (null for another record).
    /// </summary>
    public Meitou.Data.Gameplay.Bodies.LimbReplacement? Limb(string recordId) =>
        _limbs.GetOrAdd(recordId, id => Db.Find(id) is { Type: FcsRecordType.LimbReplacement } r ? Data.Gameplay.Bodies.LimbReplacement.From(r)
                : null);

    /// <summary>
    /// The cheapest food in the data (lowest <c>value</c> per nutrition point, ties by id): the ration an NPC without food is given.
    /// </summary>
    public ItemInstance? CheapestFood() => Create(CheapestFoodId() ?? "");

    readonly Lazy<string?> _cheapestFoodId = new(() => db.OfType(FcsRecordType.Item).Select(ItemInfo.From).Where(i => i.IsFood && i.Charges > 0)
        // the factory is shared with the build thread, hence Lazy
        .OrderBy(i => i.Value / i.Charges).ThenBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault()?.Id);

    string? CheapestFoodId() => _cheapestFoodId.Value;

    readonly Lazy<string?> _moneyId = new(() => db.OfType(FcsRecordType.Item).Where(r => r.Name == Trading.Market.MoneyItemName)
        .Select(r => r.StringId).OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault());

    /// <summary>
    /// CHARACTER <c>money item prob</c> / <c>min</c> / <c>max</c> for a newly made character not of the player's faction
    /// (<c>RootObjectFactory::process</c>, <b>Verified</b>): when a roll in [0, 1) is at most the prob (so the data's 80-100 always pass), a stack of
    /// the money item worth U[min, max] goes into the inventory; one that does not fit is lost.
    /// </summary>
    public void AddSpawnMoney(Inventory inventory, GameRecord? character, ulong seed, ulong key)
    {
        if (character is not { Type: FcsRecordType.Character } || _moneyId.Value is not { } money) return;
        float prob = character.GetFloat("money item prob", 0);
        int min = character.GetInt("money item min", 0), max = character.GetInt("money item max", 0);
        if (max <= 0 || max < min) return;
        ulong h = Rng.Hash(seed, key, RngPurpose.Spawn, 0x4D6F_6E65_7949_746DUL);
        if (Rng.Float(h) > prob) return;
        int amount = min + (int)(Rng.Float(h ^ 0x9E37_79B9_7F4A_7C15UL) * (max - min + 1));
        if (amount > 0 && Create(money, amount) is { } cats) inventory.Add(cats);
    }

    readonly ConcurrentDictionary<string, Meitou.Data.Gameplay.Combat.ArmourData?> _armourData = new();

    /// <summary>
    /// The weight function of an armour record that lists part coverage (null: it keeps its record weight).
    /// </summary>
    Func<ItemInstance, float>? ArmourWeight(string recordId)
    {
        var data = _armourData.GetOrAdd(recordId, id => Db.Find(id) is { Type: FcsRecordType.Armour } r ? Data.Gameplay.Combat.ArmourData.From(r)
                : null);
        return data is { Coverage.Count: > 0 } ? item => data.WeightAt(item.Level) ?? item.RecordWeight : null;
    }

    /// <summary>
    /// The weight function of a melee weapon (<b>Observed</b>, docs/game/ui-screens.md "Total Weight"): its level, manufacturer and material give the
    /// larger of <c>weight kg</c> x the manufacturer's weight mod and 40 x blunt multiplier x <c>weight mult</c> x <c>weapon inventory weight
    /// mult</c> (<see cref="Combat.WeaponStats"/>); without combat data the record's weight x the multiplier.
    /// </summary>
    Func<ItemInstance, float>? WeaponWeight(string recordId) => combat is null ? null : item =>
    {
        var data = combat();
        return data.Weapon(recordId) is { } weapon
            ? Combat.WeaponStats.Compute(item.Level * 0.01f, weapon, data.Manufacturer(item.CompanyId)
                    ?? Data.Gameplay.Combat.WeaponManufacturer.Neutral, data.WeaponMaterial(item.MaterialId)
                            ?? Data.Gameplay.Combat.WeaponMaterial.None, data.Constants).Weight
            : item.RecordWeight;
    };

    /// <summary>
    /// A new instance of an item record (null for an unknown record). Food and raw meat get their charges scaled by <c>food quality mult</c>
    /// (<see cref="ItemInfo.ScalesCharges"/>); weapons weigh <c>weapon inventory weight mult</c> of their weight.
    /// </summary>
    public ItemInstance? Create(string? recordId, int quantity = 1, float quality = 0)
    {
        // A reference to a record the data lacks (a mod's) has no id.
        if (string.IsNullOrEmpty(recordId)) return null;
        if (Db.Find(recordId) is { Type: FcsRecordType.Research } research)
            return Blueprint(research, quantity, quality);
        return Info(recordId) is { } info ? Make(info, info.Type, quantity, quality) : null;
    }

    /// <summary>
    /// A blueprint of a RESEARCH record (the original's item factory, <c>FUN_140580750</c>, <b>Verified</b>; docs/game/buildings-production.md
    /// "Blueprints"): it looks like the research's <c>blueprint item</c> (an ITEM), else <c>BLUEPRINT_ITEM</c>, and carries the research as its
    /// company and material, as the saves hold it. Null without a look record.
    /// </summary>
    public ItemInstance? Blueprint(GameRecord research, int quantity = 1, float quality = 0)
    {
        string look = research.GetReferences("blueprint item").Select(r => r.TargetStringId).FirstOrDefault(id
                => Db.Find(id) is { Type: FcsRecordType.Item }) ?? "BLUEPRINT_ITEM";
        if (Info(look) is not { } info || Make(info, FcsRecordType.Blueprint, quantity, quality) is not { } item)
            return null;
        item.CompanyId = item.MaterialId = research.StringId;
        return item;
    }

    /// <summary>
    /// The research a blueprint teaches (null for any other item).
    /// </summary>
    public GameRecord? ResearchOf(ItemInstance item) => item.Type == FcsRecordType.Blueprint
            && Db.Find(item.CompanyId) is { Type: FcsRecordType.Research } r ? r : null;

    ItemInstance Make(ItemInfo info, FcsRecordType type, int quantity, float quality)
    {
        bool weapon = info.Type is FcsRecordType.Weapon or FcsRecordType.Crossbow;
        float maxCharges = info.ScalesCharges ? info.Charges * Constants.FoodQualityMult : info.Charges;
        return new ItemInstance
        {
            Record = info.Id,
            Name = info.Name,
            // A new item takes its record's colour (Item::Item, docs/characters.md "Clothing colours").
            ColourId = ItemColours.Of(Db, Db.Find(info.Id))?.StringId ?? "",
            Type = type,
            EquipmentSection = info.Type is FcsRecordType.Armour or FcsRecordType.Item && Db.Find(info.Id) is { } worn ? SectionOf(worn) : "",
            WornOnlyBy = info.WornOnlyBy,
            WornNeverBy = info.WornNeverBy,
            IsLocked = info.Type == FcsRecordType.Armour && Db.Find(info.Id) is { } locked && locked.GetBool("is locked"),
            Lock = info.Type == FcsRecordType.Armour && Db.Find(info.Id) is { } shackles && shackles.GetBool("is locked")
                ? new Locks.LockState { Level = Math.Clamp(shackles.Ints.GetValueOrDefault("lock level"), 0, 100),
                        Hardness = shackles.Floats.GetValueOrDefault("hardness"), Locked = true }
                : null,
            Quantity = Math.Max(quantity, 1),
            // An ITEM keeps its record's quality (Item::Item, FUN_14075faf0); the other kinds take the maker's grade.
            Quality = type == FcsRecordType.Item ? info.Quality : quality,
            // An armour piece's stats follow its level, which a made piece takes from its grade quality; so do a crossbow's and a limb's
            // (FUN_140580750 hands the grade to those constructors).
            Level = info.Type is FcsRecordType.Armour or FcsRecordType.Crossbow or FcsRecordType.LimbReplacement ? (int)quality : 0,
            ItemFunction = info.Function,
            Charges = info.Type == FcsRecordType.Item ? maxCharges : 1,   // only an ITEM starts full
            MaxCharges = maxCharges,
            Width = info.Width,
            Height = info.Height,
            UnitWeight = weapon ? info.Weight * Constants.WeaponInventoryWeightMult : info.Weight,
            DynamicWeight = info.Type == FcsRecordType.Armour ? ArmourWeight(info.Id) : info.Type == FcsRecordType.Weapon ? WeaponWeight(info.Id)
                    : null,
            StackLimit = info.StackLimit,
            IsFood = info.IsFood,
            Contents = info.Type == FcsRecordType.Container
                ? new Inventory(Math.Max(info.StorageWidth, 1), Math.Max(info.StorageHeight, 1)) { StackBonusMinimum = info.StackBonusMinimum,
                        StackBonusMult = info.StackBonusMult }
                : null,
            ContentsFactor = info.EncumbranceEffect,
        };
    }

    /// <summary>
    /// The main grid of a character of <paramref name="record"/>: an ANIMAL_CHARACTER's <c>inventory w</c> x <c>inventory h</c> (Skimmer and Garru 4
    /// x 8, Beak Thing 8 x 8; <b>Verified</b> by probe), each below 1 on its own becoming 4 and 8 (<c>FUN_14062a130</c>, <b>Verified</b>); a person's
    /// <see cref="Inventory.MainWidth"/> x <see cref="Inventory.MainHeight"/>.
    /// </summary>
    public static (int Width, int Height) MainSize(GameRecord? record) =>
        record is { Type: FcsRecordType.AnimalCharacter } animal
            ? (animal.GetInt("inventory w") is var w and >= 1 ? w : 4, animal.GetInt("inventory h") is var h and >= 1 ? h : 8)
            : (Inventory.MainWidth, Inventory.MainHeight);

    /// <summary>
    /// The inventory of a new character: what the generator rolled to wear and carry (clothing in its slot's section, a backpack, the crossbow and
    /// weapons at hip/back), then a blueprint per CHARACTER <c>blueprints</c> entry, then the CHARACTER record's <c>inventory</c> list (an ITEM
    /// reference whose first value is the quantity; entries with 0 never spawn, <b>Observed</b>), and last its <c>death items</c> the same way,
    /// marked <see cref="ItemInstance.Death"/> (docs/game/animals.md "Death items"). With a <paramref name="seed"/> (a new character's, keyed by
    /// <paramref name="key"/>) a single unit of the <c>inventory</c> list that holds more than one charge starts with a random part of them; without
    /// one the items start full.
    /// </summary>
    public Inventory Build(GameRecord? character, Data.Characters.Loadout? loadout, ulong? seed = null, ulong key = 0, bool deathItems = true)
    {
        var (width, height) = MainSize(character);
        var inventory = new Inventory(width, height);
        if (loadout is not null)
        {
            foreach (var c in loadout.Clothing)
                if (Create(c.Record.StringId, 1, c.Quality) is { } cloth)
                {
                    cloth.Section = SectionOf(c.Record);
                    cloth.MaterialId = c.Material?.StringId ?? "";
                    if (c.Colour is { } colour) cloth.ColourId = colour.StringId;
                    inventory.Items.Add(cloth);
                }
            if (loadout.Backpack is { } b && Create(b.Record.StringId, 1, b.Quality) is { } pack)
            {
                pack.Section = "backpack_attach";
                if (b.Colour is { } colour) pack.ColourId = colour.StringId;
                inventory.Items.Add(pack);
            }
            if (loadout.Crossbow is { } x) AddWeapon(inventory, x);
            foreach (var w in loadout.Weapons) AddWeapon(inventory, w);
        }
        if (character is { Type: FcsRecordType.AnimalCharacter }) AddAnimalBackpacks(inventory, character);

        // The CHARACTER's blueprints come after the weapons and before its inventory list (CharacterHuman::setupInventorySections, Verified): one of
        // each RESEARCH whose v0 is
        // above 0, whatever the number.
        if (character is not null)
            foreach (var r in character.GetReferences("blueprints"))
                if (r.Values.Value0 > 0 && Db.Find(r.TargetStringId) is { Type: FcsRecordType.Research } research
                        && Blueprint(research) is { } blueprint)
                    inventory.Add(blueprint);
        if (character is not null)
        {
            int index = 0;
            foreach (var r in character.GetReferences("inventory"))
            {
                index++;
                if (r.Values.Value0 <= 0) continue;
                if (Create(r.TargetStringId, r.Values.Value0) is not { } item) continue;

                // A person's single starting unit that holds more than one charge starts part used (CharacterHuman::setupInventorySections,
                // Verified): a whole number in [1, trunc(max)]. An animal's list is not rolled.
                if (seed is { } s && character.Type != FcsRecordType.AnimalCharacter && r.Values.Value0 == 1 && item.Charges > 1)
                    item.Charges = 1
                            + Rng.Int(Rng.Hash(s, key, RngPurpose.Spawn, 0x496E_764C_6F61_6400UL + (ulong)index), Math.Max((int)item.MaxCharges, 1));
                inventory.Add(item);
            }
        }
        if (loadout?.Crossbow is { } bow) AddAmmo(inventory, bow.Weapon);
        if (character is not null && deathItems)
            foreach (var r in character.GetReferences("death items"))
            {
                // One stack of the whole quantity in the first free cell, whatever the item's stack limit (as the saves hold them).
                if (r.Values.Value0 < 1 || Create(r.TargetStringId, r.Values.Value0) is not { } item
                        || !inventory.FindCell("main", item.Width, item.Height, out int x, out int y))
                    continue;
                (item.Death, item.X, item.Y) = (true, x, y);
                inventory.Items.Add(item);
            }
        return inventory;
    }

    /// <summary>
    /// A new animal's luggage (docs/game/animals.md "Pack animals", <c>FUN_14062a130</c>, <b>Observed</b>): every entry of the ANIMAL_CHARACTER's
    /// <c>backpack</c> list is made and added, whatever its values (the Pack Bull's entry is (0, 0, 0)); the first goes on the <c>backpack_attach</c>
    /// slot, so a pack beast carries its bag's grid from the start. More entries go where they fit (none in the data has two).
    /// </summary>
    void AddAnimalBackpacks(Inventory inventory, GameRecord animal)
    {
        foreach (var r in animal.GetReferences("backpack"))
        {
            if (Create(r.TargetStringId) is not { Type: FcsRecordType.Container } pack) continue;
            if (inventory.Backpack is null)
            {
                pack.Section = "backpack_attach";
                inventory.Items.Add(pack);
            }
            else inventory.Add(pack);
        }
    }

    /// <summary>
    /// An <b>engine choice</b>: a character whose loadout rolled a crossbow gets one item of the crossbow's <c>ammo</c> (one stack of bolts: 10 to 40
    /// shots by the record's <c>charges</c>) unless its record's list already gave it some. Only 3 of the 40 base-game CHARACTER records with
    /// crossbows list bolts; how the original equips NPC crossbowmen with ammunition is <b>Unknown</b> (docs/simulation.md "Combat", ranged).
    /// </summary>
    void AddAmmo(Inventory inventory, GameRecord crossbow)
    {
        var refs = crossbow.GetReferences("ammo");
        if (refs.Count == 0) return;
        string ammo = refs[0].TargetStringId;
        foreach (var i in inventory.All())
            if (i.Record == ammo) return;
        if (Create(ammo) is { } bolts) inventory.Add(bolts);
    }

    /// <summary>
    /// A loadout weapon in its section and row; when another weapon already holds those cells (a two-row crossbow fills the back), in the hip when
    /// that is free, else in the pockets: two weapons never share cells.
    /// </summary>
    void AddWeapon(Inventory inventory, Data.Characters.LoadoutWeapon w)
    {
        if (Create(w.Weapon.StringId, 1) is not { } item) return;
        item.Level = w.Level;
        item.MaterialId = w.Model?.StringId ?? "";
        item.CompanyId = w.Manufacturer?.StringId ?? "";
        if (Place(inventory, item, w.Section, w.Row) || Place(inventory, item, "hip", 0)) inventory.Items.Add(item);
        else
        {
            item.Section = "main";
            inventory.Add(item);
        }
    }

    /// <summary>
    /// Puts <paramref name="item"/> at the start of <paramref name="row"/> of an equipment <paramref name="section"/> when it fits there and the
    /// cells are free.
    /// </summary>
    static bool Place(Inventory inventory, ItemInstance item, string section, int row)
    {
        var size = InventoryCommands.SectionSize(section);
        if (item.Width > size.Width || row + item.Height > size.Height || !inventory.IsFree(section, 0, row, item.Width, item.Height))
            return false;
        (item.Section, item.X, item.Y) = (section, 0, row);
        return true;
    }

    /// <summary>
    /// The section a worn ARMOUR or ITEM goes to by its <c>slot</c> (the save's section names; <c>FUN_14075faf0</c> stores the slot of every item
    /// type and a section takes an item whose slot is its type, <c>FUN_14074bc30</c>, <b>Verified</b>). An ARMOUR with another slot goes to "main";
    /// an ITEM with slot 7 (none) or another slot has no section (in the base data only the two lanterns, slot 14, have one).
    /// </summary>
    static string SectionOf(GameRecord worn) => (Data.Characters.AttachSlot)worn.GetInt("slot") switch
    {
        Data.Characters.AttachSlot.Shirt => "shirt",
        Data.Characters.AttachSlot.Hat => "head",
        Data.Characters.AttachSlot.Boots => "boots",
        Data.Characters.AttachSlot.Body => "armour",
        Data.Characters.AttachSlot.Legs => "legs",
        Data.Characters.AttachSlot.Belt => "belt",
        _ => worn.Type == FcsRecordType.Item ? "" : "main",
    };
}