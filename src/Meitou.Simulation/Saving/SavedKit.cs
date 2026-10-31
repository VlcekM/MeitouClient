using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A saved character's kit both ways (docs/simulation.md "Saves"): the INVENTORY_ITEM_STATEs of its inventory into an <see cref="Inventory"/> and back, and the worn
/// items with its CHARACTER_APPEARANCE into the <see cref="Loadout"/> its look is drawn from.
/// </summary>
public static class SavedKit
{
    /// <summary>The sections a worn ARMOUR sits in (the save's names; docs/formats/save.md). <c>belt</c> takes the ARMOUR with <c>slot</c> 14 (mods put masks, goggles and robes there).</summary>
    static readonly HashSet<string> Worn = new(StringComparer.Ordinal) { "head", "shirt", "armour", "legs", "boots", "belt" };

    /// <summary>
    /// The items as an inventory, at their saved sections and cells. Items whose record the game data lacks are left out and named in <paramref name="notes"/>. The main
    /// grid is the <paramref name="owner"/>'s (<see cref="ItemFactory.MainSize"/>: 8 x 10 for a person, the vanilla screenshot's pocket grid, <b>Observed</b>, docs/game/ui-screens.md),
    /// as tall as the saved items need.
    /// </summary>
    public static Inventory ToInventory(IReadOnlyList<SaveItem> items, ItemFactory factory, List<string>? notes = null, GameRecord? owner = null)
    {
        var (width, height) = ItemFactory.MainSize(owner);
        foreach (var s in items)
            if (s.Section == "main" && factory.Info(s.BaseId) is { } info) height = Math.Max(height, s.Y + Math.Max(info.Height, 1));
        var inventory = new Inventory(width, height);
        Fill(inventory, items, factory, notes, "main");
        return inventory;
    }

    /// <summary>
    /// Restores the saved items of one inventory as the original's load does (<c>FUN_14074c0e0</c>, <b>Verified</b> by decompile, docs/game/ui-screens.md "Loading a saved inventory"): each item goes to its
    /// saved section and cell; one whose cell lies outside the section, overlaps an item already placed (<c>FUN_1407466f0</c>) or whose section is occupied is auto-added instead
    /// (<see cref="Inventory.Add"/> / <see cref="Inventory.AddTo"/>), and one that finds no room is lost. The rejected items are auto-added after the rest are placed (the bag is then known), an engine choice.
    /// </summary>
    static void Fill(Inventory inventory, IReadOnlyList<SaveItem> items, ItemFactory factory, List<string>? notes, string gridSection)
    {
        List<ItemInstance>? rejected = null;
        foreach (var s in items)
        {
            if (ItemOf(s, factory) is not { } item)
            {
                notes?.Add($"item {s.BaseId} is not in the game data; left out");
                continue;
            }
            if (s.Contents is { } contents && item.Contents is { } bag) Fill(bag, contents, factory, notes, "backpack_content");
            if (Fits(inventory, item)) inventory.Items.Add(item);
            else (rejected ??= []).Add(item);
        }
        if (rejected is null) return;
        foreach (var item in rejected)
        {
            string from = $"{item.Section} {item.X},{item.Y}";
            bool added = gridSection == "main" ? inventory.Add(item) : inventory.AddTo(gridSection, item);
            notes?.Add(added ? $"item {item.Record} did not fit at {from}; auto-added at {item.Section} {item.X},{item.Y}" : $"item {item.Record} did not fit at {from} and found no room; left out");
        }
    }

    /// <summary>Whether a loaded item may take its saved place: inside its grid or slot and over no item placed there.</summary>
    static bool Fits(Inventory inventory, ItemInstance item)
    {
        var (w, h) = item.Section is "main" or "backpack_content" ? (inventory.Width, inventory.Height) : InventoryCommands.SectionSize(item.Section);
        if (item.Section is "main" or "backpack_content" or "back" or "hip")
            return item.X >= 0 && item.Y >= 0 && item.X + item.Width <= w && item.Y + item.Height <= h && inventory.IsFree(item.Section, item.X, item.Y, item.Width, item.Height);
        if (InventoryCommands.IsEquipmentSection(item.Section) || InventoryCommands.IsLimbSection(item.Section))
            return !inventory.Items.Any(i => i.Section == item.Section);
        return true;
    }

    /// <summary>A saved item as an instance (without a container's contents); null when the game data lacks its record.</summary>
    internal static ItemInstance? ItemOf(SaveItem s, ItemFactory factory)
    {
        // A blueprint is saved as its look record with the RESEARCH as company and material (Verified, the owner's saves); the factory makes it from the research.
        var research = factory.Db.Find(s.CompanyId) is { Type: FcsRecordType.RESEARCH } c ? c : factory.Db.Find(s.MaterialId) is { Type: FcsRecordType.RESEARCH } m ? m : null;
        if ((research is not null ? factory.Blueprint(research, s.Quantity, s.Quality) : factory.Create(s.BaseId, s.Quantity, s.Quality)) is not { } item) return null;
        item.Section = s.Section;
        item.X = s.X;
        item.Y = s.Y;
        // Meitou saves from before armour took its level from its quality have level 0 and a grade quality below 100: that quality. The original saves some armour
        // at level 0 and quality 100 (Observed, docs/game/ui-screens.md "Item tooltip"), which stays level 0.
        item.Level = s.Level == 0 && item.Type == FcsRecordType.ARMOUR && s.Quality is > 0 and < 100 ? (int)s.Quality : s.Level;
        item.MaterialId = s.MaterialId;
        item.CompanyId = s.CompanyId;
        item.Crafter = s.Crafter;
        item.Uniform = s.Uniform;
        // "" keeps the record's colour the factory gave: saves from before Meitou kept colours wrote "" for every item.
        if (s.ColourId.Length > 0) item.ColourId = s.ColourId;
        item.Charges = s.Charges;
        item.Death = s.Death;
        item.Unique = s.Unique;
        (item.LimbDamage, item.LimbWear, item.LimbStun) = (s.Damage, s.Wear, s.Stun);
        if (s.Lock is { } l && item.Lock is { } lockState)
        {
            lockState.Hardness = l.Hardness;
            lockState.Broken = l.Broken;
            // As the building reader: level and the flag are taken only from a level above 0.
            if (l.Level > 0) (lockState.Level, lockState.Locked) = (Math.Clamp(l.Level, 0, 100), l.Locked);
        }
        item.Saved = s.Source;
        return item;
    }

    /// <summary>The inventory as saved items, a container with its contents; each keeps the record it was read from (<see cref="ItemInstance.Saved"/>).</summary>
    public static List<SaveItem> FromInventory(Inventory inventory) => [.. inventory.Items.Where(i => !InventoryCommands.IsLimbSection(i.Section)).Select(FromItem)];

    internal static SaveItem FromItem(ItemInstance i) => FromItem(i, i.Section);

    /// <summary>
    /// The saved items whose <c>ownedby</c> names a character (<see cref="ItemInstance.Owner"/>) while a save is captured: the capture writes the owner's handle on each once every character
    /// has one (null: not capturing).
    /// </summary>
    [ThreadStatic] internal static List<(SaveItem Item, CharacterId Owner)>? OwnerNotes;

    /// <summary>
    /// A saved item written under <paramref name="section"/> (a machine grid whose original section name is not Meitou's <c>main</c>). Its <c>ownedby</c> is the robbed character's handle, the null
    /// handle when the owner read from the save was cleared since, else the record's own.
    /// </summary>
    internal static SaveItem FromItem(ItemInstance i, string section)
    {
        var saved = new SaveItem
        {
            BaseId = i.Record, MaterialId = i.MaterialId, CompanyId = i.CompanyId, Crafter = i.Crafter, Uniform = i.Uniform, ColourId = i.ColourId, Section = section, X = i.X, Y = i.Y, Quantity = i.Quantity, Level = i.Level,
            ItemFunction = i.ItemFunction, Quality = i.Quality, Charges = i.Charges, Source = i.Saved,
            Damage = i.LimbDamage, Wear = i.LimbWear, Stun = i.LimbStun, Death = i.Death, Unique = i.Unique,
            Lock = i.Lock is { } l ? new SaveItemLock(l.Level, l.EffectivelyLocked, l.Broken, l.Hardness) : null,
            Contents = i.Contents is { } bag ? FromInventory(bag) : null,
            OwnedBy = !i.Owner.IsNone || i.OwnerFromSave ? Hand.Null : null,
        };
        if (!i.Owner.IsNone) OwnerNotes?.Add((saved, i.Owner));
        return saved;
    }

    /// <summary>Puts the non-natural limbs of <paramref name="medical"/> (stumps, prosthetics, crushed) and a cut-off head into <paramref name="loadout"/>.</summary>
    public static void SetLimbs(Loadout loadout, Bodies.MedicalState? medical, GameDatabase db)
    {
        for (int k = 0; k < loadout.Limbs.Length; k++)
            loadout.Limbs[k] = medical?.LimbPart(k) is { Limb: not Bodies.LimbState.Original } p
                ? new LoadoutLimb((int)p.Limb, p.Limb == Bodies.LimbState.Replaced && p.Replacement is { } r ? db.Find(r.StringId) : null)
                : null;
        loadout.Decapitated = medical?.Decapitated == true;
    }

    /// <summary>A copy of <paramref name="loadout"/> (to give it limbs without touching a look another character may share).</summary>
    public static Loadout Copy(Loadout loadout)
    {
        var copy = new Loadout
        {
            Seed = loadout.Seed, Character = loadout.Character, Faction = loadout.Faction, Race = loadout.Race, Female = loadout.Female, Appearance = loadout.Appearance, Generated = loadout.Generated,
            MorphIndex = loadout.MorphIndex, Backpack = loadout.Backpack, Crossbow = loadout.Crossbow,
        };
        copy.Clothing.AddRange(loadout.Clothing);
        copy.Weapons.AddRange(loadout.Weapons);
        loadout.Limbs.CopyTo(copy.Limbs, 0);
        copy.Decapitated = loadout.Decapitated;
        return copy;
    }

    /// <summary>
    /// Whether a CHARACTER_APPEARANCE holds a look (a head or any slider), not just the race and flags that a character without a rolled look is saved with.
    /// </summary>
    public static bool HasLook(FcsRecord? appearance) =>
        appearance is not null && (appearance.Floats.Count > 0 || appearance.Strings.TryGetValue("head", out var head) && head.Length > 0);

    /// <summary>
    /// <paramref name="rolled"/>'s clothes and weapons on the body of <paramref name="appearance"/> (its race, sex, head, hair, colours, sliders). Null when the record holds no
    /// look (<see cref="HasLook"/>) or its race is not in the data.
    /// </summary>
    public static Loadout? Wear(Loadout rolled, FcsRecord? appearance, GameDatabase db)
    {
        if (!HasLook(appearance)) return null;
        var file = new AppearanceFile { Record = appearance! };
        if (file.RaceId is not { } raceId || db.Find(raceId) is not { } race) return null;
        var loadout = new Loadout
        {
            Seed = rolled.Seed, Character = rolled.Character, Faction = rolled.Faction, Race = race, Female = file.Female, Appearance = file, Generated = true,
            MorphIndex = appearance!.Ints.GetValueOrDefault("morph index", -1), Backpack = rolled.Backpack, Crossbow = rolled.Crossbow,
        };
        loadout.Clothing.AddRange(rolled.Clothing);
        loadout.Weapons.AddRange(rolled.Weapons);
        rolled.Limbs.CopyTo(loadout.Limbs, 0);
        return loadout;
    }

    /// <summary>
    /// The loadout a saved character is drawn from: its CHARACTER_APPEARANCE (race, sex, head, hair, beard, skin, sliders: the body-file keys) and what it wears: ARMOUR in the
    /// clothing sections, the backpack, the weapons and crossbow at <c>hip</c> and <c>back</c> (company = manufacturer, material = model). Null without an appearance record
    /// or a race. <paramref name="seed"/> only names it (nothing is rolled); <paramref name="faction"/> is the wearer's (its uniform scheme).
    /// </summary>
    public static Loadout? LoadoutOf(FcsRecord? appearance, IReadOnlyList<SaveItem> items, GameDatabase db, int seed, GameRecord? faction = null)
    {
        if (appearance is null) return null;
        var file = new AppearanceFile { Record = appearance };
        if (file.RaceId is not { } raceId || db.Find(raceId) is not { } race) return null;
        var loadout = new Loadout
        {
            Seed = seed, Faction = faction, Race = race, Female = file.Female, Appearance = file, Generated = true,
            MorphIndex = appearance.Ints.GetValueOrDefault("morph index", -1),
        };
        foreach (var s in items)
        {
            if (db.Find(s.BaseId) is not { } record) continue;
            GameRecord? Find(string id) => id.Length > 0 ? db.Find(id) : null;
            if (record.Type == FcsRecordType.ARMOUR && Worn.Contains(s.Section))
                loadout.Clothing.Add(new LoadoutItem(record) { Quality = (int)s.Quality, Material = Find(s.MaterialId), Colour = Find(s.ColourId) });
            else if (s.Section == "backpack_attach")
                loadout.Backpack = new LoadoutItem(record) { Quality = (int)s.Quality, Colour = Find(s.ColourId) };
            else if (s.Section == "belt" && record.Type == FcsRecordType.ITEM)
                loadout.Carried.Add(new LoadoutItem(record) { Colour = Find(s.ColourId) });
            else if (s.Section is "hip" or "back" && record.Type is FcsRecordType.WEAPON or FcsRecordType.CROSSBOW)
            {
                var weapon = new LoadoutWeapon(record) { Section = s.Section, Row = s.Y, Level = s.Level, Manufacturer = Find(s.CompanyId), Model = Find(s.MaterialId) };
                if (record.Type == FcsRecordType.CROSSBOW) loadout.Crossbow ??= weapon;
                else loadout.Weapons.Add(weapon);
            }
        }
        // Equip order: hip first, then back (the first weapon is the one a fight draws).
        loadout.Weapons.Sort((a, b) => string.CompareOrdinal(a.Section == "hip" ? "0" : "1", b.Section == "hip" ? "0" : "1"));
        return loadout;
    }
}
