using System.Globalization;
using System.Text;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

// The reward-item engine adapter (task 5.2a): the ENGINE calls of upstream/ProvingGrounds/plugin/src/RewardDelivery.cpp, one method each; its
// rules (offers, grade recognition, the purchase transaction) are Core (Core/ArenaRewards.cs, Core/RewardTransaction.cs, Core/Glue/RewardGrades.cs)
// and task 5.2b's src/Game/RewardDelivery.cs. Meitou 0.17.3 (tag v0.17.3, fa34a1b); paths relative to its repo root; CAP 10, PA §2.17-§2.18.
// Sim thread, serial steps only (Inputs / SlowWorld), like PgEngine.
//
// E1: every engine call of RewardDelivery.cpp and what replaces it here.
//   ou->gamedata.getData(sid) (records: reward items, models, manufacturers, research, the cloaks, BLUEPRINT_ITEM) .. PopulationData.Db.Find
//       (GameDatabase.cs:52) inside the methods below; a missing record answers null as getData does
//   ou->gamedata.getDataByName("Warlord Forge", WEAPON_MANUFACTURER) (:117) ......... ResolveWeaponManufacturer: Db.OfType(WeaponManufacturer)
//       by name (GameDatabase.cs:57); reached only for a weapon reward without a manufacturer (every catalogue row names one)
//   GameData::getReferenceListIfExists("material") (:155-166) ........................ ResolveItemMaterial: GameRecord.GetReferences (GameRecord.cs:157)
//   GameData::getReferenceListIfExists("weapon models") + values[0] (:133-153) ........ FindWeaponModel (only for a row without a model id)
//   GameData::findInList / addToList("weapon types") (EnsureWeaponListed, :120-131) .. DROPPED (divergences.md "Reward items"): Meitou reads
//       a weapon instance's maker and model by id only (combat: CharacterAssembly.cs:81 Manufacturer(CompanyId) / WeaponMaterial(MaterialId);
//       weight: ItemFactory.cs:103-111; icon: ItemIcons.MaterialOf(item, materialId)); the "weapon types" list is read only for a RECORD's
//       generic look (MaterialResolver.cs:375-385), shop stock and crafting, never for an item that has its own model. 181 / 182 are not in
//       Arena Forge's list in the data mod (only in Null Forged's), so the C++ added them at runtime; Meitou does not need it.
//   RootObjectFactory::createItem(data, hand(), company, material, quality, NULL) (TryCreate :522-549) .. ItemFactory.Create(record, 1, quality)
//       (ItemFactory.cs:117; PopulationData.Items, PopulationData.cs:682) + MaterialId = the first "material" reference (as a loadout's
//       clothing, ItemFactory.cs:222); the retry without material (:640-644) is not needed (Create does not take a material)
//   new Sword(data, manufacturer, model, hand(), quality) (TryCreateSword :588-611) + the createItem permutations (:672-706) .. Create, then
//       CompanyId = manufacturer, MaterialId = model, Level = quality (ItemInstance.cs:66,70,116; as AddWeapon, ItemFactory.cs:313-318);
//       Create never fails for a WEAPON record, so the permutations collapse into the first
//   Gear::level_0_100 = q, Gear::level = q / 100 (NormalizeGearQuality :419-431) .. Level = q and Quality = q (DebugCommands.cs:752-759 SetQuality);
//       Create already gives armour Level = (int)q (ItemFactory.cs:175) and every non-ITEM Quality = q (:172)
//   Item::getLevel, Gear::level_0_100, getLevel01 (logs) ....... RewardItem.Level, Level / 100 (CMath.FormatF)
//   createItem(BLUEPRINT_ITEM, hand(), research, research, -1) (CreateBlueprintReward :551-586) .. ItemFactory.Create(researchId) (a RESEARCH id
//       gives its blueprint, ItemFactory.cs:121-139: CompanyId = MaterialId = the research). The look record is the research's own "blueprint
//       item" (280 / 281 for research 50 / 52) where the C++ named BLUEPRINT_ITEM (divergences.md "Reward items")
//   Item::chargesLeft = charges (PurchaseGeneral :1084-1085) .......... SetCharges: ItemInstance.Charges (public, ItemInstance.cs:155)
//   Item::itemFunction (log) ................................... RewardItem.Function (ItemInstance.ItemFunction, :146)
//   Inventory::getItem, getAllItems, getEquippedArmour / getEquippedWeapons, getAllSections + getItems (FindExactGrade, HasAnyGrade,
//       FindPreviousGrade, FindInInventorySections :198-357) .. Copies(world, id, record): every copy of the record in Inventory.All() (the
//       items and the worn backpack's contents, Inventory.cs:237-246), unworn ones first, then worn; the level tests are Core RewardGrades
//   Inventory::hasRoomForItem(data) (:851, :1070) ............... HasRoomForRecord / HasRoomForBlueprint (task 5.2b: asked before the item is
//       made, as the C++): Inventory.HasRoomFor (Inventory.cs:160: main grid, else the worn backpack; one unit) of a probe item of the record
//       (the blueprint of the research), never given; HasRoomFor(item) asks it of a made item (the test verb pg reward-item)
//   Inventory::removeItemDontDestroy_returnsItem(item, 1, false) (ItemDelivery :476-488) .. TakeOut: Inventory.Remove (the whole item, :287)
//       for a single unit, RemoveOne + a one-unit copy for a stack (:271-285); the item is kept alive by the returned handle
//   Character::giveItem(item, dropOnFail false, destroyOnFail true|false) (:481, :492) .. Give: Inventory.Add bound by reflection (Members.cs
//       InventoryAdd: 0.20 added a mainShut parameter; Inventory.cs:146 at 0.17.3, Meitou's port of
//       the original's auto-add: stacks, then the main grid, then the worn backpack); never worn, as the C++ (giveItem has no equip
//       parameter, KenshiLib Character.h giveItem(Item*, bool dropOnFail, bool destroyOnFail)). Not dropped on the ground (dropOnFail false)
//   ou->destroy(item, false, reason) (DestroyUndelivered :433-437) .. Destroy: the handle is marked gone. An ItemInstance that is in no
//       inventory, container or ground list is not in the world (nothing saves or draws it), so there is nothing to delete
//   (the engine's own fighter rebuild after a kit change) .. Refresh: CharacterAssembly.RefreshEquipment by reflection (Members.cs, CAP 10 trap):
//       Meitou rebuilds CharacterCold.Fighter only there, so every change here refreshes
//   the colour-band migration (LevelsMatch :169-196) .. a rule, Core RewardGrades.LevelsMatch (no engine call)
//   ou->activeMods (HasExtraInventorySections :65-85) .. not needed: the cloak is always record 89 (spike S13; Core RewardGrades.ItemRecord)
//   Character::isValid, getInventory, getName, CollectPlayerSquad (:773-787) .. PgEngine / SquadUtil (task 5.2b)
//   actor.getCharacter() == character, item->isGear(), item->getGameData() == itemData (identity / type tests inside the C++ helpers)
//       .. task 5.2b's binding (FighterIdentity) / ItemInstance.GearLevelOf(type) / ItemInstance.Record == record (in Copies)
//   InventoryCommands.TryEquip .. Wear (TEST ONLY, pg reward-item ... wear): the C++ never wears a reward; the live check wears one to see the
//       refresh reach Fighter.Weapon

/// <summary>A reward item the adapter made or found: an opaque handle the glue holds across the two transaction phases (task 5.2b keeps
/// the previous grade alive in one until delivery and accounting both succeed). It wraps Meitou's <c>ItemInstance</c>; nothing outside
/// src/Engine sees the engine type.</summary>
sealed class RewardItem
{
    internal RewardItem(ItemInstance item, bool loose) { Item = item; Loose = loose; }

    /// <summary>The item is in no inventory: true for an item just made (CreateRewardItem, CreateBlueprintReward) or taken out
    /// (<see cref="Items.TakeOut"/>), false for one a character holds (<see cref="Items.Copies"/>, after a successful <see cref="Items.Give"/>).
    /// Give takes only a loose item; TakeOut only a held one.</summary>
    public bool Loose { get; internal set; }

    /// <summary>The unit itself.</summary>
    internal ItemInstance Item { get; set; }

    /// <summary>After <see cref="Items.Give"/>: the stack that took the unit when it merged into one (Inventory.Add tops up stacks first,
    /// Inventory.cs:146-153), else null (the unit is its own item).</summary>
    internal ItemInstance? Holder { get; set; }


    /// <summary>C++ <c>ou-&gt;destroy</c> was called (or giveItem destroyed it on a failure).</summary>
    public bool Destroyed { get; internal set; }

    public string Record => Item.Record;
    /// <summary>Units in it (the stack's quantity when it is held merged into one).</summary>
    public int Quantity => (Holder ?? Item).Quantity;
    public string Type => Item.Type.ToString();
    public int Level => Item.Level;
    public float Quality => Item.Quality;
    public string CompanyId => Item.CompanyId;
    public string MaterialId => Item.MaterialId;
    public float Charges => Item.Charges;
    public int Function => Item.ItemFunction;
    /// <summary>Where it is: its section ("main", "belt", "armour", "back", ...; "backpack_content" inside the worn bag). A loose item keeps the
    /// section it was in when last held (a take-out does not clear it), so a report can say where the previous grade came from.</summary>
    public string Section => (Holder ?? Item).Section;
    /// <summary>Held, in an equipment section (InventoryCommands.IsEquipmentSection, InventoryCommands.cs:27-28).</summary>
    public bool Equipped => !Loose && InventoryCommands.IsEquipmentSection(Section);
}

/// <summary>The reward-item engine adapter (header above). Every method is sim-thread, serial-step only; a stale character id never throws
/// (reads answer empty / null, writes false).</summary>
static class Items
{
    /// <summary>Why a new purchase may not start (empty: <see cref="CanStart"/>): the members that did not resolve, and a refresh that threw.
    /// The glue refuses to START a purchase while this is not empty, with one log line (task 5.2b; the test verbs refuse here); a purchase
    /// already under way still finishes or rolls back (<see cref="Bound"/>).</summary>
    internal static IEnumerable<string> Missing() =>
        refreshFailed is { } failure ? Members.Missing(Part.Rewards).Append($"CharacterAssembly.RefreshEquipment (threw {failure})")
        : Members.Missing(Part.Rewards);

    /// <summary>A new purchase may start (Core <see cref="RewardItemsGate.MayStart"/>): every member resolved and the refresh never threw in
    /// this process. The glue asks this (or <see cref="Missing"/>) before it reserves or takes anything out.</summary>
    internal static bool CanStart => RewardItemsGate.MayStart(Members.Missing(Part.Rewards).Any(), refreshFailed is not null);

    /// <summary>The GATE of every item move here (Give, TakeOut, Destroy's take-out, Wear, Refresh): Core <see cref="RewardItemsGate.MayMove"/>,
    /// members only. Deliberately NOT <see cref="CanStart"/>: after a refresh threw, a purchase already under way must still finish or roll back
    /// (Give(new), then on a failure Give(previous, destroyOnFail: false)); refusing those would lose a previous grade already taken out.</summary>
    static bool Bound => RewardItemsGate.MayMove(Members.Missing(Part.Rewards).Any());

    static CharacterCold? Cold(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int slot) ? world.Characters.Cold(slot) : null;

    // ---------- making ----------

    /// <summary>C++: RewardDelivery.cpp:108-118 (<c>ResolveWeaponManufacturer</c>): the named manufacturer; with none, Warlord Forge
    /// (<c>71-Proving Grounds.mod</c>, else the WEAPON_MANUFACTURER named "Warlord Forge").</summary>
    static GameRecord? ResolveWeaponManufacturer(GameDatabase db, string? manufacturerStringId)
    {
        if (manufacturerStringId is not null)
            return db.Find(manufacturerStringId);
        if (db.Find(PgRecords.NullForged) is { } manufacturer)
            return manufacturer;
        return db.OfType(FcsRecordType.WeaponManufacturer).FirstOrDefault(r => r.Name == "Warlord Forge");
    }

    /// <summary>C++: RewardDelivery.cpp:133-153 (<c>FindWeaponModel</c>): the manufacturer's "weapon models" entry whose first value is the
    /// quality; null when none.</summary>
    static GameRecord? FindWeaponModel(GameDatabase db, GameRecord? manufacturer, int modelValue)
    {
        if (manufacturer is null)
            return null;
        foreach (var r in manufacturer.GetReferences("weapon models"))
            if (r.Values.Value0 == modelValue)
                return db.Find(r.TargetStringId);
        return null;
    }

    /// <summary>C++: RewardDelivery.cpp:155-166 (<c>ResolveItemMaterial</c>): the record's first "material" reference; null when none.</summary>
    static GameRecord? ResolveItemMaterial(GameDatabase db, GameRecord? itemData)
    {
        if (itemData is null)
            return null;
        var materials = itemData.GetReferences("material");
        if (materials.Count == 0)
            return null;
        return db.Find(materials[0].TargetStringId);
    }

    /// <summary>C++: RewardDelivery.cpp:396-417 (<c>LogGearQuality</c>), the gear quality line.</summary>
    static void LogGearQuality(ItemInstance item, string label, Action<string>? diag)
    {
        if (diag is null) return;
        string level = item.Level.ToString(CultureInfo.InvariantCulture);
        if (ItemInstance.GearLevelOf(item.Type, 1) == 0)
        {
            diag($"Proving Grounds: {label} getLevel={level} (not gear)");
            return;
        }
        diag($"Proving Grounds: {label} getLevel={level} level_0_100={level} level01={CMath.FormatF(item.Level / 100.0f, 3)}");
    }

    /// <summary>Task 5.3b: the record and material (the inventory icon's look: ItemIcons.Choose(record, material)) of the item
    /// <see cref="CreateRewardItem"/> would make for these arguments, read from the game data alone (no item made; the Rewards window's preview,
    /// RewardsUI.cpp:375-391 / :410-425, which the C++ makes, reads the icon of and destroys). Null where CreateRewardItem answers null (unknown record,
    /// a weapon without a manufacturer). Mirrors CreateRewardItem and ItemFactory.Create / Blueprint (ItemFactory.cs:117-139): a RESEARCH id looks like
    /// its "blueprint item" (an ITEM), else BLUEPRINT_ITEM, made of the research; armour carries its first "material"; a weapon its model (the row's,
    /// else the manufacturer's model of that quality). Pure over the read-only <paramref name="db"/>: callable from the window thread.</summary>
    internal static (string Record, string Material)? PreviewLook(GameDatabase db, string recordId, int qualityLevel, string? weaponModelStringId = null,
        string? weaponManufacturerStringId = null)
    {
        var itemData = db.Find(recordId);
        if (itemData is null)
            return null;
        var material = ResolveItemMaterial(db, itemData);
        if (itemData.Type != FcsRecordType.Weapon)
        {
            string record = itemData.StringId, materialId = "";
            if (itemData.Type == FcsRecordType.Research)
            {
                string? look = itemData.GetReferences("blueprint item").Select(r => r.TargetStringId)
                    .FirstOrDefault(id => db.Find(id) is { Type: FcsRecordType.Item });
                record = look ?? "BLUEPRINT_ITEM";
                if (db.Find(record) is null)
                    return null;
                materialId = itemData.StringId;
            }
            if (material is not null)
                materialId = material.StringId;
            return (record, materialId);
        }
        var manufacturer = ResolveWeaponManufacturer(db, weaponManufacturerStringId);
        if (manufacturer is null)
            return null;
        var model = weaponModelStringId is not null ? db.Find(weaponModelStringId) : FindWeaponModel(db, manufacturer, qualityLevel);
        return (itemData.StringId, model?.StringId ?? "");
    }

    /// <summary>C++: RewardDelivery.cpp:613-709 (<c>CreateRewardItem</c>): a new reward item of the record at the grade, not in any
    /// inventory yet. Armour (any non-WEAPON record): <c>Create(record, 1, q)</c> with its first material, Level = Quality = q. A WEAPON: Create,
    /// CompanyId = the manufacturer (the row's, else Warlord Forge), MaterialId = the model (the row's, else the manufacturer's model of that
    /// quality), Level = Quality = q. A quality below 0 (the general items, :1081) keeps the record's own (Create's default). Null for an
    /// unknown record or a missing manufacturer. <paramref name="diag"/> gets the C++ RewardDiag lines (task 5.2b sends them to the debug log).</summary>
    internal static RewardItem? CreateRewardItem(PopulationData data, string recordId, int qualityLevel, string? weaponModelStringId = null,
        string? weaponManufacturerStringId = null, Action<string>? diag = null)
    {
        var db = data.Db;
        var itemData = db.Find(recordId);
        if (itemData is null)
            return null;
        string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
        diag?.Invoke($"Proving Grounds: CreateRewardItem sid={itemData.StringId} type={Num((int)itemData.Type)} level={Num(qualityLevel)}");
        var material = ResolveItemMaterial(db, itemData);
        diag?.Invoke($"Proving Grounds: material={material?.StringId ?? "(none)"}");

        if (itemData.Type != FcsRecordType.Weapon)
        {
            var armour = Make(data, recordId, qualityLevel);
            diag?.Invoke($"Proving Grounds: armour createItem -> {(armour is null ? "null" : "ok")} (level now {Num(armour?.Level ?? -1)})");
            if (armour is null)
                return null;
            if (material is not null)
                armour.MaterialId = material.StringId;
            LogGearQuality(armour, "armour createItem", diag);
            return new RewardItem(armour, loose: true);
        }

        var manufacturer = ResolveWeaponManufacturer(db, weaponManufacturerStringId);
        if (manufacturer is null)
        {
            diag?.Invoke("Proving Grounds: reward weapon manufacturer missing");
            return null;
        }
        // EnsureWeaponListed (:120-131, :654) dropped: see the header.
        var model = weaponModelStringId is not null ? db.Find(weaponModelStringId) : FindWeaponModel(db, manufacturer, qualityLevel);
        if (weaponModelStringId is not null && model is null)
            diag?.Invoke($"Proving Grounds: weapon model record missing: {weaponModelStringId}");
        diag?.Invoke($"Proving Grounds: weapon manufacturer={manufacturer.StringId} model={model?.StringId ?? "(none)"}");

        var weapon = Make(data, recordId, qualityLevel);
        diag?.Invoke($"Proving Grounds: Sword(mfr,model) -> {(weapon is null ? "null" : "ok")} (level now {Num(weapon?.Level ?? -1)})");
        if (weapon is null)
        {
            diag?.Invoke("Proving Grounds: all weapon spawn attempts failed");
            return null;
        }
        weapon.CompanyId = manufacturer.StringId;
        weapon.MaterialId = model?.StringId ?? "";
        LogGearQuality(weapon, "Sword(mfr,model)", diag);
        return new RewardItem(weapon, loose: true);
    }

    /// <summary>Create + the grade (C++ createItem / Sword with the quality, then NormalizeGearQuality, RewardDelivery.cpp:419-431): for a
    /// quality of 0 or more, Level and Quality = q on gear (an ITEM keeps its record quality, ItemFactory.cs:172).</summary>
    static ItemInstance? Make(PopulationData data, string recordId, int qualityLevel)
    {
        var item = data.Items.Create(recordId, 1, Math.Max(qualityLevel, 0));
        if (item is null || qualityLevel < 0)
            return item;
        if (ItemInstance.GearLevelOf(item.Type, 1) != 0)
        {
            item.Level = qualityLevel;
            item.Quality = qualityLevel;
        }
        return item;
    }

    /// <summary>C++: RewardDelivery.cpp:551-586 (<c>CreateBlueprintReward</c>): the blueprint of a RESEARCH record, carrying the research as
    /// its company and material (ItemFactory.Blueprint, ItemFactory.cs:131-139). Null for a record that is not a RESEARCH.</summary>
    internal static RewardItem? CreateBlueprintReward(PopulationData data, string researchId, Action<string>? diag = null)
    {
        var research = data.Db.Find(researchId);
        if (research is null || research.Type != FcsRecordType.Research)
            return null;
        var blueprint = data.Items.Blueprint(research);
        diag?.Invoke($"Proving Grounds: blueprint create research={research.StringId} -> {(blueprint is null ? "null" : "ok")} function="
            + (blueprint?.ItemFunction ?? -1).ToString(CultureInfo.InvariantCulture));
        return blueprint is null ? null : new RewardItem(blueprint, loose: true);
    }

    /// <summary>C++: RewardDelivery.cpp:1084-1085 (<c>newItem-&gt;chargesLeft = charges</c>): ItemInstance.Charges (ItemInstance.cs:155).
    /// MaxCharges stays the record's (the shipped 291 / 293 / 295 records hold 1200 / 125 / 600 already).</summary>
    internal static void SetCharges(RewardItem item, int charges) => item.Item.Charges = charges;

    /// <summary>Whether the record is a WEAPON (the C++ <c>itemData-&gt;type == WEAPON</c> branches); false for an unknown record.</summary>
    internal static bool IsWeapon(PopulationData data, string recordId) => data.Db.Find(recordId)?.Type == FcsRecordType.Weapon;

    /// <summary>C++ <c>character-&gt;getInventory()</c> is non-null (RewardDelivery.cpp:784-789, :1051); false for a stale id.</summary>
    internal static bool HasInventory(World world, CharacterId id) => Cold(world, id)?.Inventory is not null;

    /// <summary>Whether the record exists (C++ <c>getData</c> non-null).</summary>
    internal static bool RecordExists(PopulationData data, string recordId) => data.Db.Find(recordId) is not null;

    // ---------- finding ----------

    /// <summary>C++ <c>getItem</c> / <c>getAllItems</c> / <c>getEquippedArmour</c>, <c>getEquippedWeapons</c> / every section's items, as
    /// FindExactGrade, HasAnyGrade and FindPreviousGrade walk them (RewardDelivery.cpp:198-357): every copy of the record the character holds,
    /// in <c>Inventory.All()</c> order (the items, then the worn backpack's contents, Inventory.cs:237-246), the unworn ones first, then the worn
    /// ones (prosthetic limb sections are not inventory, CAP 13). Empty for a stale id. The level tests are Core
    /// (<see cref="RewardGrades.FindExact"/>, <see cref="RewardGrades.FindPrevious"/>) over <see cref="Levels"/>.</summary>
    internal static List<RewardItem> Copies(World world, CharacterId id, string recordId)
    {
        var list = new List<RewardItem>();
        if (Cold(world, id)?.Inventory is not { } inventory)
            return list;
        var worn = new List<RewardItem>();
        foreach (var item in inventory.All())
        {
            if (item.Record != recordId || InventoryCommands.IsLimbSection(item.Section))
                continue;
            (InventoryCommands.IsEquipmentSection(item.Section) ? worn : list).Add(new RewardItem(item, loose: false));
        }
        list.AddRange(worn);
        return list;
    }

    /// <summary>The levels of <paramref name="copies"/> (C++ <c>Item::getLevel</c>), for Core's finders.</summary>
    internal static int[] Levels(IReadOnlyList<RewardItem> copies) => [.. copies.Select(c => c.Level)];

    /// <summary>C++ <c>Inventory::hasRoomForItem(data)</c> (RewardDelivery.cpp:851, :1070): one unit of the item finds a place in the main
    /// grid or the worn backpack (<c>Inventory.HasRoomFor</c>, Inventory.cs:160). False for a stale id.</summary>
    internal static bool HasRoomFor(World world, CharacterId id, RewardItem item) =>
        Cold(world, id)?.Inventory is { } inventory && inventory.HasRoomFor(item.Item);

    /// <summary>C++ <c>inventory-&gt;hasRoomForItem(itemData)</c> (RewardDelivery.cpp:851, :1070) asked of the RECORD, before the reward is made
    /// (task 5.2b keeps the C++ order): <c>Inventory.HasRoomFor</c> of a probe item of the record at quality 0 (<c>ItemFactory.Create</c>, which
    /// touches no world, ItemFactory.cs:117-125; the probe is never given and simply dropped). The probe has the record's own size and stack
    /// limit, as the C++ asks of the record. False for a stale id or an unknown record.</summary>
    internal static bool HasRoomForRecord(World world, PopulationData data, CharacterId id, string recordId) =>
        Cold(world, id)?.Inventory is { } inventory && data.Items.Create(recordId, 1, 0) is { } probe && inventory.HasRoomFor(probe);

    /// <summary>C++ <c>inventory-&gt;hasRoomForItem(deliveryData)</c> (RewardDelivery.cpp:1056-1058, :1070): the blueprint of a research, asked
    /// before it is made. The C++ asks of BLUEPRINT_ITEM, the record it wraps the research in; Meitou's blueprint takes the research's own
    /// "blueprint item" look (ItemFactory.Blueprint, ItemFactory.cs:131-139), so the probe is that blueprint. False for a stale id or a record
    /// that is not a RESEARCH.</summary>
    internal static bool HasRoomForBlueprint(World world, PopulationData data, CharacterId id, string researchId) =>
        Cold(world, id)?.Inventory is { } inventory && BlueprintProbe(data, researchId) is { } probe && inventory.HasRoomFor(probe);

    /// <summary>C++ <c>researchData &amp;&amp; deliveryData</c> (RewardDelivery.cpp:1053-1060): the research record exists and a blueprint of it
    /// can be made (its look record exists).</summary>
    internal static bool BlueprintRecordsExist(PopulationData data, string researchId) => BlueprintProbe(data, researchId) is not null;

    static ItemInstance? BlueprintProbe(PopulationData data, string researchId) =>
        data.Db.Find(researchId) is { Type: FcsRecordType.Research } research ? data.Items.Blueprint(research) : null;

    // ---------- moving ----------

    /// <summary>C++ <c>inventory-&gt;removeItemDontDestroy_returnsItem(item, 1, false)</c> (ItemDelivery::RemovePrevious / RemoveNew,
    /// RewardDelivery.cpp:474-488): takes one unit of a HELD item out of the character's inventory and returns it, loose (kept alive by the
    /// handle, in no inventory; its Section still says where it was); a stack loses one unit and the unit is a copy (Inventory.RemoveOne,
    /// Inventory.cs:271-285). Null when the part is off, the handle is loose, or the character or the item is not there. The fighter is
    /// refreshed; a refresh that throws does not undo the take-out (the handle is returned all the same).</summary>
    internal static RewardItem? TakeOut(World world, PopulationData data, CharacterId id, RewardItem held)
    {
        if (!Bound || held.Loose || Cold(world, id) is not { Inventory: { } inventory } cold)
            return null;
        var item = held.Holder ?? held.Item;
        if (!inventory.All().Contains(item))
            return null;
        RewardItem taken;
        if (item.Quantity > 1)
        {
            if (!inventory.RemoveOne(item)) return null;
            taken = new RewardItem(item.WithQuantity(1), loose: true);
        }
        else
        {
            if (!inventory.Remove(item)) return null;
            taken = new RewardItem(item, loose: true);
        }
        SafeRefresh(data, cold);
        return taken;
    }

    /// <summary>C++ <c>character-&gt;giveItem(item, false, destroyOnFail)</c> (ItemDelivery::DeliverNew / RestorePrevious,
    /// RewardDelivery.cpp:481, :492): a LOOSE item into the character's inventory by <c>Inventory.Add</c> (stacks, the main grid, then the worn
    /// backpack, Inventory.cs:146-153; bound by reflection, Members.InventoryAdd), never worn and never dropped. On a failure nothing is placed,
    /// and with <paramref name="destroyOnFail"/> the item is destroyed (marked gone, as the C++ giveItem's destroyOnFail). Refused (false,
    /// nothing changes) when the part is off, the handle is destroyed or not loose, or the inventory already holds the item. The fighter is
    /// refreshed; a refresh that throws does not undo the delivery (true all the same).</summary>
    internal static bool Give(World world, PopulationData data, CharacterId id, RewardItem item, bool destroyOnFail)
    {
        if (!Bound || item.Destroyed || !item.Loose || Cold(world, id) is not { Inventory: { } inventory } cold)
            return false;
        var unit = item.Item;
        if (inventory.All().Contains(unit))
            return false;   // backstop: a loose handle whose item is somehow held already
        // which stack took the unit: the item whose quantity rose (Inventory.Add tops up stacks first, Inventory.cs:197-215)
        var before = new Dictionary<ItemInstance, int>(ReferenceEqualityComparer.Instance);
        foreach (var held in inventory.All()) before[held] = held.Quantity;
        if (unit.Quantity != 1 || !Add(inventory, unit))
        {
            if (destroyOnFail) item.Destroyed = true;
            return false;
        }
        if (inventory.All().Contains(unit))
            item.Holder = null;
        else
        {
            item.Holder = inventory.All().FirstOrDefault(i => before.TryGetValue(i, out int q) && i.Quantity > q);
            unit.Quantity = 1;   // the top-up left the handle's unit at 0 outside the list
        }
        item.Loose = false;
        SafeRefresh(data, cold);
        return true;
    }

    /// <summary>Inventory.Add through <see cref="Members.InventoryAdd"/> (0.17.3: <c>Add(ItemInstance)</c>; 0.20: <c>Add(ItemInstance, bool
    /// mainShut = false)</c>, called with false). Bound once into a delegate.</summary>
    static bool Add(Inventory inventory, ItemInstance item)
    {
        if (add is null && Members.InventoryAdd is { } m)
        {
            if (m.GetParameters().Length == 1)
                add = m.CreateDelegate<Func<Inventory, ItemInstance, bool>>();
            else
            {
                var two = m.CreateDelegate<Func<Inventory, ItemInstance, bool, bool>>();
                add = (inv, i) => two(inv, i, false);
            }
        }
        return add is not null && add(inventory, item);
    }

    static Func<Inventory, ItemInstance, bool>? add;

    /// <summary>C++ <c>ou-&gt;destroy(item, false, reason)</c> (DestroyUndelivered, RewardDelivery.cpp:433-437): the handle's item is gone.
    /// <paramref name="owner"/> is the character whose inventory may still hold it (the C++ destroys an item wherever it is; the transaction calls
    /// it on items it took out of this fighter, or never gave). A loose item is marked destroyed: Meitou keeps no world list of items, so an
    /// ItemInstance in no inventory, container or ground list is gone. A held one is taken out of <paramref name="owner"/> first; true only
    /// when it is gone (false, not marked, when the take-out failed: the item is still in the inventory).</summary>
    internal static bool Destroy(World world, PopulationData data, CharacterId owner, RewardItem item)
    {
        if (item.Destroyed) return true;
        if (!item.Loose)
        {
            if (TakeOut(world, data, owner, item) is not { } taken) return false;
            item.Item = taken.Item;
            item.Holder = null;
            item.Loose = true;
        }
        item.Destroyed = true;
        return true;
    }

    /// <summary>TEST ONLY (pg reward-item ... wear): wears a held item that sits in a grid (not merged into a stack), through
    /// <c>InventoryCommands.TryEquip</c> (InventoryCommands.cs:144-175: weapons into back then hip, armour into its free section), then
    /// refreshes the fighter. On a refusal the item goes back where it was (same list place, section and cell). The C++ never wears a reward
    /// (giveItem); the live check uses this to see the refresh reach <c>Fighter.Weapon</c>.</summary>
    internal static bool Wear(World world, PopulationData data, CharacterId id, RewardItem item)
    {
        if (!Bound || item.Loose || item.Holder is not null || Cold(world, id) is not { Inventory: { } inventory } cold)
            return false;
        int at = inventory.Items.IndexOf(item.Item);
        if (at < 0) return false;
        var (section, x, y) = (item.Item.Section, item.Item.X, item.Item.Y);
        inventory.Items.RemoveAt(at);
        if (!InventoryCommands.TryEquip(inventory, item.Item, cold))
        {
            (item.Item.Section, item.Item.X, item.Item.Y) = (section, x, y);
            inventory.Items.Insert(at, item.Item);
            return false;
        }
        SafeRefresh(data, cold);
        return true;
    }

    // ---------- the fighter refresh (CAP 10 trap) ----------

    static Action<CharacterCold>? refresh;
    static PopulationData? refreshData;
    static string? refreshFailed;

    /// <summary>Forgets the world's assembly (called when a new world's system takes over, ProvingGroundsSystem.TakeOver), so no old
    /// PopulationData is kept alive. A refresh that threw keeps the part off for the process.</summary>
    internal static void ForgetWorld()
    {
        refresh = null;
        refreshData = null;
    }

    /// <summary>CharacterAssembly.RefreshEquipment(cold) (Members.cs; CharacterAssembly.cs:207-222) through a delegate bound once per
    /// PopulationData to its own assembly (as each system makes its own). Never throws: a refresh that throws is logged once and turns the
    /// rewards part off (<see cref="Missing"/> names it); the inventory change it followed stands. False when it did not run.</summary>
    static bool SafeRefresh(PopulationData data, CharacterCold cold)
    {
        if (refreshFailed is not null || Members.CharacterAssemblyNew is not { } ctor || Members.RefreshEquipment is not { } method)
            return false;
        try
        {
            if (refresh is null || !ReferenceEquals(refreshData, data))
            {
                refresh = method.CreateDelegate<Action<CharacterCold>>(ctor.Invoke([data]));
                refreshData = data;
            }
            refresh(cold);
            return true;
        }
        catch (Exception e)
        {
            refreshFailed = $"{e.GetType().Name}: {e.Message}";
            ProvingGroundsMod.Ctx?.Log($"rewards off: CharacterAssembly.RefreshEquipment threw ({refreshFailed}); the inventory change stands");
            return false;
        }
    }

    /// <summary>Refreshes a character's fighter (false: stale id, part off, or the refresh failed).</summary>
    internal static bool Refresh(World world, PopulationData data, CharacterId id) => Bound && Cold(world, id) is { } cold && SafeRefresh(data, cold);

    // ---------- test output ----------

    /// <summary>TEST ONLY (pg items): one JSON object with every item of the character (record, name, type, section, equipped, level, quality,
    /// companyId, materialId, charges, maxCharges, quantity) and what its fighter holds (the weapon in hand, the back and hip weapons, each
    /// record and quality; the number of armour pieces). Floats through CMath.FormatG. "exists":false for a stale id.</summary>
    internal static string ItemsJson(World world, CharacterId id)
    {
        var j = new StringBuilder();
        j.Append("{\"id\":").Append(PGLog.Quote(CharacterIdText.Format(id))).Append(",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture));
        if (Cold(world, id) is not { } cold) return j.Append(",\"exists\":false}").ToString();
        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        string G(float f) => TestJson.G(f);   // task 8.8: NaN -> null
        j.Append(",\"exists\":true,\"items\":[");
        bool first = true;
        foreach (var i in cold.Inventory?.All() ?? [])
        {
            if (!first) j.Append(',');
            first = false;
            j.Append("{\"record\":").Append(PGLog.Quote(i.Record)).Append(",\"name\":").Append(PGLog.Quote(i.Name))
                .Append(",\"type\":").Append(PGLog.Quote(i.Type.ToString())).Append(",\"section\":").Append(PGLog.Quote(i.Section))
                .Append(",\"equipped\":").Append(InventoryCommands.IsEquipmentSection(i.Section) ? "true" : "false")
                .Append(",\"level\":").Append(N(i.Level)).Append(",\"quality\":").Append(G(i.Quality))
                .Append(",\"companyId\":").Append(PGLog.Quote(i.CompanyId)).Append(",\"materialId\":").Append(PGLog.Quote(i.MaterialId))
                .Append(",\"charges\":").Append(G(i.Charges)).Append(",\"maxCharges\":").Append(G(i.MaxCharges))
                .Append(",\"quantity\":").Append(N(i.Quantity)).Append('}');
        }
        j.Append(']');
        string W(Meitou.Simulation.Combat.WeaponInstance? w) => w is null ? "null"
            : "{\"record\":" + PGLog.Quote(w.Data.StringId) + ",\"quality\":" + G(w.Stats.Quality) + "}";
        var f = cold.Fighter;
        j.Append(",\"fighter\":").Append(f is null ? "null"
            : "{\"weapon\":" + W(f.Weapon) + ",\"back\":" + W(f.BackWeapon) + ",\"hip\":" + W(f.HipWeapon) + ",\"armour\":" + N(f.Armour.Length) + "}");
        return j.Append('}').ToString();
    }
}
