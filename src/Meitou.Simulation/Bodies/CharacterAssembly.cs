using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The spawn-time assembly of a character: from its CHARACTER record and race it gets <c>Race</c>, <c>Stats</c>, <c>Medical</c>, <c>Inventory</c>,
/// <c>Fighter</c>, <c>WaterFactor</c> and the top speed of the speed chain (docs/simulation.md "Bodies", "Combat"). The population
/// system uses the instance; the race lookups are static so the save code reads the same record the same way.
/// </summary>
sealed class CharacterAssembly(PopulationData data)
{
    /// <summary>
    /// The id of the race a CHARACTER record names: its first <c>race</c> reference with a positive count; null when it has none. An
    /// ANIMAL_CHARACTER: <see cref="CharacterAppearance.AnimalRaceId"/>.
    /// </summary>
    public static string? RaceId(GameRecord record)
    {
        if (record.Type == FcsRecordType.AnimalCharacter) return CharacterAppearance.AnimalRaceId(record);
        foreach (var r in record.GetReferences("race"))
            if (r.Values.Value0 > 0) return r.TargetStringId;
        return null;
    }

    /// <summary>
    /// The RACE record of a character: the one its generated look was made with, else the one its record names.
    /// </summary>
    public static GameRecord? RaceRecord(GameDatabase db, string recordId, CharacterAppearance? look)
    {
        var race = look?.Race;
        if (race is null && db.Find(recordId) is { } rec && RaceId(rec) is { } id) race = db.Find(id);
        return race;
    }

    /// <summary>
    /// The <see cref="RaceData"/> of a RACE record id, read once per id into <paramref name="cache"/> (null for an id that is not a race).
    /// </summary>
    public static RaceData? RaceDataOf(GameDatabase db, string raceId, Dictionary<string, RaceData?> cache)
    {
        if (cache.TryGetValue(raceId, out var cached)) return cached;
        var race = db.Find(raceId) is { Type: FcsRecordType.Race } rec ? RaceData.From(rec, db) : null;
        cache[raceId] = race;
        return race;
    }

    /// <summary>
    /// The race record of a spawned character (see <see cref="RaceRecord"/>).
    /// </summary>
    public GameRecord? RaceOf(string recordId, CharacterAppearance? look) => RaceRecord(data.Db, recordId, look);

    /// <summary>
    /// The top speed a character starts with: the 45 of a creature without a race. A character with a race gets its real top speed from
    /// <see cref="Attach"/> (docs/game/pathfinding.md "Speed"), so the lerp over the race's speed skills that stood in for it before stats existed is
    /// gone.
    /// </summary>
    public const float StandInMaxSpeed = 45;

    /// <summary>
    /// The walk speed of a character: the race's <c>walk speed</c>, 15 without a race.
    /// </summary>
    public static float WalkSpeed(GameRecord? race) => race?.GetFloat("walk speed", 15) ?? 15;

    /// <summary>
    /// The weapons (the first of the hip and of the back section; quality the level x 0.01; which is in hand goes by the arms,
    /// <see cref="Fighter.Weapon"/>) and the worn armour (quality / 100), docs/simulation.md "Combat". The fists' length is the race's <c>fists
    /// lv1</c> WEAPON's, else <c>FISTS</c>' (docs/game/combat.md "Attack technique choice").
    /// </summary>
    Fighter MakeFighter(Inventory inventory, MedicalState? medical, string? raceId)
    {
        var combat = data.Combat;
        WeaponInstance? First(string section)
        {
            foreach (var item in inventory.Items.Where(i => i.Section == section && i.Type == FcsRecordType.Weapon))
                if (combat.Weapon(item.Record) is { } w)
                    return WeaponInstance.Create(item.Level * 0.01f, w, combat.Manufacturer(item.CompanyId),
                        combat.WeaponMaterial(item.MaterialId), combat.Constants);
            return null;
        }
        var armour = inventory.Items
            .Where(i => i.Type == FcsRecordType.Armour && InventoryCommands.IsEquipmentSection(i.Section) && combat.Armour(i.Record) is not null)
            // Its stats follow the level (the grade's quality), not `quality`, which is 100 on saved armour; level 0 is full strength without the
            // slot rules (docs/game/combat.md "Armour and medical hit").
            .Select(i => (Data: combat.Armour(i.Record)!, i.Level))
            .Select(x => ArmourPiece.Create(x.Data, ArmourPiece.QualityOf(x.Data, x.Level), ArmourPiece.Graded(x.Data, x.Level)));

        // The crossbow: the first CROSSBOW in the weapon sections (back, then hip), its quality the level x 0.01 like a weapon's.
        CrossbowInstance? crossbow = null;
        foreach (var section in new[] { "back", "hip" })
            foreach (var item in inventory.Items.Where(i => i.Section == section && i.Type == FcsRecordType.Crossbow))
                if (crossbow is null && combat.Crossbow(item.Record) is { } x)
                    crossbow = CrossbowInstance.Create(item.Level * 0.01f, x, combat.Constants, data.BodyOptions.GlobalDamageMultiplier,
                        item.UnitWeight, section == "back");
        var packRecord = inventory.Backpack is { } pack ? data.Db.Find(pack.Record) : null;
        float? backpackStealth = inventory.Backpack is not null ? packRecord?.GetFloat("stealth mult", 1) ?? 1 : null;

        // CONTAINER +0x238 (FUN_140763810): the raw athletics mult, the seed of the gear's smallest run factor.
        float backpackAthletics = packRecord?.GetFloat("athletics mult", 1) ?? 1;
        string fistsId = (raceId is null ? null : data.Db.Find(raceId))?.GetReferences("fists lv1")
            .Select(r => r.TargetStringId).FirstOrDefault() ?? "FISTS";

        // A fist blow reads only the item's record (multipliers, bleed, penetration, length), so its quality does not matter.
        var fists = (combat.Weapon(fistsId) ?? combat.Weapon("FISTS")) is { } fistsData
            ? WeaponInstance.Create(1, fistsData, null, null, combat.Constants)
            : null;
        var fighter = new Fighter(First("hip"), armour, backpackStealth, backpackAthletics,
            packRecord?.GetInt("combat skill bonus", 0) ?? 0, packRecord?.GetFloat("combat speed mult", 1) ?? 1)
        {
            BackWeapon = First("back"),
            Crossbow = crossbow,
            Fists = fists,
        };
        if (medical is not null) fighter.SetArms(medical.ArmWorks(left: false), medical.ArmWorks(left: true));
        return fighter;
    }

    /// <summary>
    /// An animal's fighter (docs/game/animals.md): its natural weapon, the ANIMAL_CHARACTER's <c>weapon</c> (a WEAPON of a creature category) made by
    /// its <c>weapon level</c> manufacturer; no armour. The weapon's level is the manufacturer's first model's level, with that model (an <b>engine
    /// choice</b>: the original makes the item without a level argument, and its default was not traced, <b>Unknown</b>). No weapon (or one not in
    /// the data) gives a fighter without an attack.
    /// </summary>
    Fighter MakeAnimalFighter(AnimalData animal)
    {
        var combat = data.Combat;
        WeaponInstance? weapon = null;
        if (animal.WeaponId is { } wid && combat.Weapon(wid) is { } w)
        {
            var maker = animal.WeaponLevelId is { } mid ? combat.Manufacturer(mid) : null;
            var model = maker is { Models.Count: > 0 } ? maker.Models[0] : (WeaponModelEntry?)null;
            weapon = WeaponInstance.Create((model?.Level ?? 0) * 0.01f, w, maker,
                model is { } m ? combat.WeaponMaterial(m.ModelStringId) : null, combat.Constants);
        }
        return new Fighter(weapon) { IsAnimal = true };
    }

    Fighter FighterOf(GameRecord? record, Inventory inventory, MedicalState? medical, string? raceId) =>
        AnimalData.From(record) is { } animal ? MakeAnimalFighter(animal) : MakeFighter(inventory, medical, raceId);

    /// <summary>
    /// The FACTION record of the character's faction (null: none or not in the data).
    /// </summary>
    GameRecord? FactionRecord(CharacterCold cold) =>
        (uint)cold.Faction < (uint)data.Factions.Count ? data.Db.Find(data.Factions[cold.Faction].Id) : null;

    /// <summary>
    /// Gives what the character has on (any item outside the main grid and the bag) without a colour the wearer's scheme, which it keeps
    /// (<c>AppearanceBase::createAttachedObject</c>, docs/characters.md "Clothing colours"): the CHARACTER's <c>color</c>, else its current
    /// faction's.
    /// </summary>
    void Dye(CharacterCold cold, Inventory inventory)
    {
        if (cold.Animal is not null || ItemColours.Scheme(data.Db, data.Db.Find(cold.RecordId), FactionRecord(cold)) is not { } scheme) return;
        foreach (var item in inventory.Items)
            if (item.ColourId.Length == 0 && item.Section != "main") item.ColourId = scheme.StringId;
    }

    /// <summary>
    /// <c>Character::setChainedMode</c> (<c>FUN_14032e590</c>, <b>Verified</b> by decompile): a character wearing no locked shackles gets a new pair,
    /// <c>SHACKLES</c> (Prisoner Shackles, boots) while its boots section is enabled, else <c>SHACKLES-BODY</c> (Hive Prisoner Shackles, body; Meitou
    /// reads a disabled boots section as the race's <c>no shoes</c>), worn and locked (the ARMOUR's lock). What it wore in that slot goes into its
    /// inventory (engine choice, as <see cref="Locks.LockSystem"/>'s putting on).
    /// </summary>
    public void Chain(CharacterCold c)
    {
        if (c.Inventory is not { } inv || Locks.LockSystem.WornShackles(c) is not null) return;
        if (data.Items.Create(c.Race?.NoShoes == true ? "SHACKLES-BODY" : Kidnap.CaptiveSystem.ShacklesRecord, 1) is not { } shackles) return;
        if (inv.Items.FirstOrDefault(i => i.Section == shackles.EquipmentSection) is { } worn)
        {
            var (section, x, y) = (worn.Section, worn.X, worn.Y);
            inv.Items.Remove(worn);
            worn.Section = "main";
            if (!inv.Add(worn))
            {
                (worn.Section, worn.X, worn.Y) = (section, x, y);
                inv.Items.Add(worn);
            }
        }
        if (!InventoryCommands.TryEquip(inv, shackles)) return;
        RefreshEquipment(c);
    }

    public void RefreshEquipment(CharacterCold cold)
    {
        if (cold.Inventory is not { } inventory) return;
        var fighter = FighterOf(data.Db.Find(cold.RecordId), inventory, cold.Medical, cold.Race?.StringId);
        fighter.Guarding = cold.Fighter?.Guarding ?? false;
        fighter.EncumbranceFactor = cold.Fighter?.EncumbranceFactor ?? 1;
        cold.Fighter = fighter;

        // A weapon already drawn follows the new kit (a weapon picked up or put away in a fight, a limb fitted): a swing with the old hold would play
        // with empty hands.
        if (cold.DrawnWeapon != WeaponKinds.None && cold.DrawnWeapon != HandHold.OfCategory((int)WeaponCategory.Bow).Weapon)
            cold.DrawnWeapon = CombatSystem.MeleeDrawn(fighter);
        Dye(cold, inventory);
        if (cold.Appearance is not { } look || look.Animal is not null) return;
        var body = look.Loadout?.Appearance.Record ?? look.Body?.Record;
        if (Saving.SavedKit.LoadoutOf(body, Saving.SavedKit.FromInventory(inventory), data.Db, look.Loadout?.Seed ?? 0, FactionRecord(cold))
            is { } kit)
        {
            Saving.SavedKit.SetLimbs(kit, cold.Medical, data.Db);
            if (data.Appearances?.Create(cold.RecordId, kit) is { } updated) cold.Appearance = updated;
        }
    }

    /// <summary>
    /// <see cref="Attach"/> for a character loaded from a save: the race, stats and medical state are the saved ones (not rolled), the inventory and
    /// the fighter are the saved <paramref name="inventory"/> when there is one, else built from the character's record and look like a new
    /// character's; the fighter from the inventory. The top speed is the loader's. An animal takes its saved GAMESTATE_CHARACTER <c>age</c>
    /// (<paramref name="age"/>; 1 when the save has none) as its stats multiplier and size (docs/game/animals.md "Ageing").
    /// </summary>
    public void AttachSaved(CharacterCold cold, RaceData race, CharacterStats stats, MedicalState medical, GameRecord? record,
        Inventory? inventory = null, float age = 1)
    {
        cold.NpcClass = NpcClassFor(cold, record);
        cold.FactionImportance = FactionImportanceOf(record);
        cold.Race = race;
        cold.Stats = stats;
        cold.Medical = medical;
        if (AnimalData.From(record) is { } animal)
        {
            (cold.Animal, cold.ShownAge, stats.Scale) = (animal, age, age);
            medical.SetAnimalBody(animal.Scale(age), animal.HpMult);
        }
        cold.Inventory = inventory ?? data.Items.Build(record, cold.Appearance?.Loadout);
        Dye(cold, cold.Inventory);

        // Fitted prosthetics show in the limbs window: their items sit in the inventory's limb sections (saved apart, SavedLimbs).
        for (int k = 0; k < 4 && cold.Inventory is { } carried; k++)
            if (medical.LimbPart(k)?.ReplacementItem is { } fitted && !carried.Items.Contains(fitted))
            {
                (fitted.Section, fitted.X, fitted.Y) = (LimbReplacement.SlotOf(k), 0, 0);
                carried.Items.Add(fitted);
            }
        cold.Fighter = FighterOf(record, cold.Inventory, cold.Medical, cold.Race?.StringId);
        cold.WaterFactor = race.WaterAvoidance * (cold.Faction == data.PlayerFaction && data.PlayerFaction >= 0 ? 0.5f : 1);
        cold.RoadPreference = data.RoadPreferenceOf(cold.Faction);
    }

    /// <summary>
    /// CHARACTER <c>NPC class</c> (<b>Verified</b>, carry-loot.md 4.3: read by <c>FUN_14062b210</c>, the editor's CharacterTypeEnum, <c>fcs.def</c>
    /// default MILITARY); None without a CHARACTER record (an animal's ANIMAL_CHARACTER, or nothing).
    /// </summary>
    /// <summary>
    /// The record's <c>NPC class</c>, except that a player-faction character is an Adventurer (9), as the original sets it when the character is made
    /// (<c>FUN_14062b210</c>).
    /// </summary>
    FundamentalType NpcClassFor(CharacterCold cold, GameRecord? record) =>
        cold.IsPlayer || (data.PlayerFaction >= 0 && cold.Faction == data.PlayerFaction) ? FundamentalType.Adventurer : NpcClassOf(record);

    /// <summary>
    /// CHARACTER <c>faction importance</c> (<b>Verified</b>: every one of the 639 merged base CHARACTER records has it, 0 to 100, most 1); 0 for any
    /// other record (the game's float map lookup of a missing key), so an animal's faction does not mind.
    /// </summary>
    public static float FactionImportanceOf(GameRecord? record) =>
        record is { Type: FcsRecordType.Character } ? record.GetFloat("faction importance", 0) : 0;

    public static FundamentalType NpcClassOf(GameRecord? record) =>
        record is { Type: FcsRecordType.Character }
            ? (FundamentalType)record.GetInt("NPC class", (int)FundamentalType.Military)
            : FundamentalType.None;

    /// <summary>
    /// Gives a character with a race its stats and medical state, its inventory and fighter, and the top speed of the speed chain (stage 7). An
    /// animal (ANIMAL_CHARACTER, whose record names its race) gets its body at <paramref name="age"/> and its natural weapon (docs/game/animals.md).
    /// Anything without a race is left as it is.
    /// </summary>
    public void Attach(CharacterId id, CharacterCold cold, ref CharacterHot hot, GameRecord? record, GameRecord? race, ulong seed, float age = 1)
    {
        cold.NpcClass = NpcClassFor(cold, record);
        cold.FactionImportance = FactionImportanceOf(record);
        if (race is null) return;
        var (raceData, stats, medical) = data.Bodies.Create(race, record, seed, Rng.Key(id), age);
        cold.Race = raceData;
        cold.Stats = stats;
        cold.Medical = medical;
        if (AnimalData.From(record) is { } animal) (cold.Animal, cold.ShownAge) = (animal, age);

        // One made for the player has no death items (engine choice, docs/game/animals.md "Death items": a start's dog had its meat and skins, #341).
        bool npc = !cold.IsPlayer && (data.PlayerFaction < 0 || cold.Faction != data.PlayerFaction);
        cold.Inventory = data.Items.Build(record, cold.Appearance?.Loadout, seed, Rng.Key(id), deathItems: npc);
        if (npc) data.Items.AddSpawnMoney(cold.Inventory, record, seed, Rng.Key(id));

        // CHARACTER `wears uniform` (CharacterHuman::setupInventorySections, Verified read; Observed use): the armour it is made wearing is its
        // faction's uniform.
        if (record is { Type: FcsRecordType.Character } && record.GetBool("wears uniform") && (uint)cold.Faction < (uint)data.Factions.Count)
            foreach (var piece in cold.Inventory.Items)
                if (piece.Type == FcsRecordType.Armour && piece.Section != "main") piece.Uniform = data.Factions[cold.Faction].Id;

        // CHARACTER `shaved`: a character made shaven-headed (Character vslot 124 isHeadShaven); a loaded one takes its saved state instead
        // (Saving.SlaveSave).
        if (record is { Type: FcsRecordType.Character }) cold.Shaved = record.GetBool("shaved");
        FitRolledLimbs(cold.Appearance?.Loadout, cold.Inventory, medical);
        Dye(cold, cold.Inventory);
        cold.Fighter = FighterOf(record, cold.Inventory, cold.Medical, cold.Race?.StringId);
        cold.WaterFactor = raceData.WaterAvoidance * (cold.Faction == data.PlayerFaction && data.PlayerFaction >= 0 ? 0.5f : 1);
        cold.RoadPreference = data.RoadPreferenceOf(cold.Faction);
        hot.MaxSpeed = Speed.Run(raceData, stats, medical, 1, statsFactor: cold.Fighter?.AthleticsFactor ?? 1);

        // A start without a limb (CHARACTER `starting health`) shows its stump, one made with a prosthetic (`robot limbs`) its prosthetic.
        if (Enumerable.Range(0, 4).Any(k => medical.LimbPart(k) is { Limb: not LimbState.Original })) RefreshEquipment(cold);
    }

    /// <summary>
    /// Fits the prosthetics a new character's loadout rolled from its CHARACTER <c>robot limbs</c> (<c>CharacterHuman::setupInventorySections</c>,
    /// <b>Verified</b>): each is made at its rolled level and put on its limb whatever the limb was, so it also covers a <c>starting health</c>
    /// stump. The item sits in the limb's inventory section, as a fitted one does.
    /// </summary>
    void FitRolledLimbs(Loadout? loadout, Inventory inventory, MedicalState medical)
    {
        if (loadout is null) return;
        for (int k = 0; k < loadout.Limbs.Length; k++)
        {
            if (loadout.Limbs[k] is not { State: (int)LimbState.Replaced, Prosthetic: { } record } rolled) continue;
            if (data.Items.Limb(record.StringId) is not { } limb || limb.LimbSlot != k
                || data.Items.Create(record.StringId) is not { } item)
                continue;
            item.Level = rolled.Level;
            if (!medical.FitReplacement(limb, item)) continue;
            (item.Section, item.X, item.Y) = (LimbReplacement.SlotOf(k), 0, 0);
            inventory.Items.Add(item);
        }
    }
}