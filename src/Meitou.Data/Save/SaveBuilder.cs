using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>
/// Makes the records of a new save from scratch (docs/formats/save.md "Implementation outline"): the key sets are the ones that every record of the type has in
/// the three real saves, ids follow the game's patterns (<c>&lt;n&gt;-quick.save-INGAME</c> in <c>quick.save</c>, <c>&lt;n&gt;--INGAME</c> in a platoon file,
/// <c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c> in a zone file, a state's id with <c>-S&lt;hex type&gt;</c>), and the random serials of handles come from a seeded
/// hash so the same input gives the same save. Call <see cref="SaveGame.UpdateDerived"/> when done.
/// </summary>
public sealed class SaveBuilder
{
    readonly ulong _seed;
    int _nextContainer = 1;
    int _nextTownContainer = 1;
    int _nextSerial;

    public SaveBuilder(ulong seed = 0)
    {
        this._seed = seed;
        var quick = new SaveFile();
        Game = new SaveGame(quick.WithRecord(NewCamera()));
        FactionCollectionOthers = NewCollection(Game.Quick);
        FactionCollectionPlayer = NewCollection(Game.Quick);
        Game.Reindex();
        Game.Camera.Version = SaveGame.GameVersion;
        Game.Quick.SlotLists.AddRange(
            [[], [], Enumerable.Range(1, SaveGame.ZoneContainerCount).ToArray(), Enumerable.Range(1, SaveGame.ZoneContainerCount).ToArray()]);
        Game.Portraits = SavePortraits.Blank();
    }

    /// <summary>
    /// Adds to an existing save (a loaded one): containers and numbers continue after the highest in use.
    /// </summary>
    public SaveBuilder(SaveGame game, ulong seed = 0)
    {
        this._seed = seed;
        Game = game;
        var player = game.PlayerFaction;
        var collections = game.FactionCollections;
        FactionCollectionPlayer = collections.FirstOrDefault(c => player is not null && c.Instances.Any(i => i.Target == player.Record.StringId))
            ?? Quick(FcsRecordType.InstanceCollection);
        FactionCollectionOthers = collections.FirstOrDefault(c => c != FactionCollectionPlayer) ?? Quick(FcsRecordType.InstanceCollection);
        _nextContainer = game.Platoons.Select(p => p.Handle.C).DefaultIfEmpty(0).Max() + 1;
        _nextTownContainer = game.Towns.Select(t => t.Handle.C).DefaultIfEmpty(0).Max() + 1;
        _nextSerial = game.Platoons.Count;
    }

    public SaveGame Game { get; }
    public FcsRecord FactionCollectionOthers { get; }
    public FcsRecord FactionCollectionPlayer { get; }

    #region Ids

    /// <summary>
    /// The next number of a file: one more than the highest numeric id in it.
    /// </summary>
    public static int NextNumber(SaveFile file) =>
        file.Records.Count == 0
            ? Math.Max(file.Data.NextId, 0) + 1
            : Math.Max(file.Data.NextId, file.Records.Max(r => Math.Max(r.Id, SaveGame.NumberOf(r)))) + 1;

    static string QuickId(int n) => $"{n}-{SaveFolder.QuickSaveName}-INGAME";

    FcsRecord Quick(FcsRecordType type, string name = "0")
    {
        int n = NextNumber(Game.Quick);
        var r = new FcsRecord { Type = (int)type, Id = n, Name = name, StringId = QuickId(n) };
        Game.Quick.Records.Add(r);
        Game.Quick.Data.NextId = n;
        return r;
    }

    /// <summary>
    /// A random 32-bit serial from the seed and a counter, the way the game's allocator makes one (any value is legal; negative ones occur).
    /// </summary>
    public int Serial()
    {
        ulong x = _seed + 0x9E3779B97F4A7C15UL * (ulong)(++_nextSerial);
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return (int)(x ^ (x >> 31));
    }

    #endregion

    #region Quick save

    static FcsRecord NewCamera()
    {
        var r = new FcsRecord { Type = (int)FcsRecordType.Camera, Name = "0" };
        foreach (var k in new[] { "eject", "share", "sit", "sleep", "feed", "ditch", "bl" }) r.Bools[k] = true;
        foreach (var k in new[] { "help", "shootfirst", "stay", "rescue", "heal", "ep", "sky updating" }) r.Bools[k] = false;
        foreach (var k in new[] { "nnm", "bs", "rs", "gdm", "ht", "ps", "cod" }) r.Floats[k] = 1;
        r.Floats["alt"] = 0;
        r.Floats["zoom"] = -800;
        r.Floats["biome_text_timer"] = 0;
        r.Floats["sky clouds density"] = 0;
        r.Floats["sky clouds density speed"] = 0;
        r.Floats["sky update current time"] = 0;
        r.Floats["sky update total time"] = 0;
        r.Ints["time day"] = 1;
        r.Ints["time hour"] = 13;
        r.Ints["time minute"] = 0;
        r.Ints["player money"] = 0;
        r.Ints["squads"] = 0;
        r.Ints["members"] = 0;
        r.Ints["floor num"] = 0;
        r.Ints["formation"] = 0;
        r.Ints["num usedUniques"] = 0;
        Hand.Null.Write(r, "selected_character");
        Hand.Null.Write(r, "selected_gui_object");
        Hand.Null.Write(r, "tracking");
        r.Vector3S["pos"] = Vector3.Zero;
        r.Vector3S["sky ambient color mult"] = Vector3.One;
        r.Vector3S["sky ambient color mult speed"] = Vector3.Zero;
        // The game's own start view: yaw 0, 30 degrees down (w, x, y, z; docs/formats/camera.md "Saved camera"); the host writes the real view over it.
        r.Vector4S["rot"] = KenshiCamera.ToSaved(0, KenshiCamera.InitialPitchDegrees * MathF.PI / 180, 800).Rot;
        r.Strings["version"] = SaveGame.GameVersion;
        r.Strings["pfaction name"] = "Nameless";
        r.Strings["area"] = "";
        r.Strings["biome_text_groud"] = "";
        r.References["zones"] = [];
        r.References["mods"] = [];
        return r;
    }

    FcsRecord NewCollection(SaveFile file)
    {
        var r = Quick(FcsRecordType.InstanceCollection);
        return r;
    }

    /// <summary>
    /// Adds a faction: the GAMESTATE_FACTION, its WAR_SAVESTATE, and an instance in the player collection or the other one (the collections are what the game's
    /// faction writers walk). <paramref name="relations"/> is the faction's table (not for the player's faction, which has none).
    /// </summary>
    public SaveFaction AddFaction(string factionId, string name, bool isPlayer, float prosperity, IEnumerable<SaveRelation>? relations = null,
        int platoonCounter = 0, int rank = 0)
    {
        var war = Quick(FcsRecordType.WarSavestate);
        war.Floats["updatetime"] = 40;
        foreach (var k in new[] { "num plats", "num poss", "num requests", "num actives", "fwc id" }) war.Ints[k] = 0;
        war.Strings["faction"] = factionId;

        var f = Quick(FcsRecordType.GamestateFaction, name);
        f.Strings["gamedata stringID"] = factionId;
        f.Floats["prosperity"] = prosperity;
        f.Ints["platoonIDs"] = platoonCounter;
        var faction = new SaveFaction(f, war);
        if (isPlayer)
        {
            f.Floats["global trust"] = 0;
            f.References["known"] = [];
        }
        else
        {
            f.Ints["rank"] = rank;
            faction.SetRelations(relations ?? []);
        }

        var collection = isPlayer ? FactionCollectionPlayer : FactionCollectionOthers;
        string id = (collection.Instances.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        collection.Instances.Add(new FcsInstance { Id = id, Target = f.StringId });
        Game.Factions.Add(faction);
        return faction;
    }

    /// <summary>
    /// Adds the state of a town or nest. <paramref name="handleType"/> is 13 (TOWN) or 85 (NEST); each gets a container of its own, or keeps
    /// <paramref name="hand"/> (a level town state's).
    /// </summary>
    public SaveTown AddTown(string name, string instance, string factionId, bool isNest, ZoneCoordinate? zone, int handleType = (int)FcsRecordType.Town,
        Hand? hand = null)
    {
        var r = Quick(FcsRecordType.GamestateTown, name);
        foreach (var k in new[] { "started", "explored", "recently discovered", "discovered", "ded" }) r.Bools[k] = false;
        r.Bools["is nest"] = isNest;
        r.Bools["public"] = !isNest;
        foreach (var k in new[] { "pop", "pop2", "popd", "popd2", "tod" }) r.Floats[k] = 0;
        r.Ints["artifacts count"] = 0;
        r.Ints["plevel"] = 0;
        if (hand is { } h)
        {
            h.Write(r, "hand");
            _nextTownContainer = Math.Max(_nextTownContainer, h.C + 1);
        }
        else new Hand(handleType, _nextTownContainer++, Serial(), 0, 0).Write(r, "hand");
        r.Strings["faction"] = factionId;
        r.Strings["instance"] = instance;
        r.Strings["building material"] = "";
        r.Strings["replacementTown"] = "";
        if (zone is { } z)
        {
            r.Ints["zzX0"] = z.X;
            r.Ints["zzY0"] = z.Y;
        }
        var town = new SaveTown(r);
        Game.Towns.Add(town);
        return town;
    }

    /// <summary>
    /// Adds the world records the game always writes after the towns: BIOMES, RESEARCH and TERRAIN_DECALS, empty. (Weather is not modelled yet; the
    /// research is written by <c>Meitou.Simulation.Saving.ResearchSave</c>.)
    /// </summary>
    public void AddEmptyWorldState()
    {
        Quick(FcsRecordType.Biomes);
        var research = Quick(FcsRecordType.Research);
        research.Floats["num finished"] = 0;
        research.Floats["num currents"] = 0;
        var decals = Quick(FcsRecordType.TerrainDecals);
        decals.Ints["total"] = 0;
        Game.Reindex();
    }

    #endregion

    #region Platoons and characters

    /// <summary>
    /// Adds a platoon: its PLATOON record and an empty platoon file (named <c>&lt;name&gt;.platoon</c>, the record's <c>content file</c>) with the characters'
    /// INSTANCE_COLLECTION. Characters are added with <see cref="AddCharacter"/>.
    /// </summary>
    public SavePlatoon AddPlatoon(string name, string factionName, string factionId, string squadTemplate, Vector3 position, int money = 0,
        int squadIndex = 0)
    {
        var r = new FcsRecord { Type = (int)FcsRecordType.Platoon, Name = name, StringId = name, Id = NextNumber(Game.Quick) };
        Game.Quick.Records.Add(r);
        Game.Quick.Data.NextId = Math.Max(Game.Quick.Data.NextId, r.Id);
        // `persistent` with `sqt` 1 (every sqt-1 platoon of the samples is persistent): a caller writing another pool sets both.
        foreach (var k in new[] { "intact", "is resident", "canref", "persistent" }) r.Bools[k] = k is "intact" or "canref" or "persistent";
        foreach (var k in new[] { "special", "dead", "never been activated", "imprisoned", "runningAwayMode", "homelok", "campaign" })
            r.Bools[k] = false;
        r.Floats["towntime"] = 0;
        r.Floats["jobhr"] = 0;
        r.Ints["squad index"] = squadIndex;
        r.Ints["sqt"] = 1;
        r.Ints["char count"] = 0;
        r.Ints["money"] = money;
        r.Ints["owned stuff count"] = 0;
        r.Ints["slave count"] = 0;
        int cs = Serial();
        new Hand((int)FcsRecordType.Platoon, _nextContainer++, cs, 0, 0).Write(r, "handle");
        foreach (var prefix in new[] { "currenttown", "hometown", "homebuilding", "occupied", "separated", "target town", "mission employer",
            "mission target", "mission town" })
        {
            Hand.Null.Write(r, prefix);
        }
        r.Vector3S["position"] = position;
        foreach (var k in new[] { "replacementAI", "contractjob", "mission data", "map area name", "map area sid", "currentPackage", "basetown" })
            r.Strings[k] = "";
        r.Strings["platoon stringID"] = name;
        r.Strings["faction name"] = factionName;
        r.Strings["faction stringID"] = factionId;
        r.Strings["squad template"] = squadTemplate;

        var platoon = new SavePlatoon(r, null);
        GiveFile(platoon);
        Game.Platoons.Add(platoon);
        return platoon;
    }

    /// <summary>
    /// Gives a platoon with no file an empty one (named <c>&lt;string id&gt;.platoon</c>, the record's <c>content file</c>) with the characters' INSTANCE_COLLECTION; a platoon
    /// with a file keeps it.
    /// </summary>
    public void GiveFile(SavePlatoon platoon)
    {
        if (platoon.File is not null) return;
        platoon.ContentFile = SaveFolder.PlatoonName(platoon.Record.StringId);
        var file = new SaveFile();
        var collection = new FcsRecord { Type = (int)FcsRecordType.InstanceCollection, Name = "0" };
        file.Records.Add(collection);
        collection.Ints["char count"] = 0;
        AssignId(file, collection, "");
        platoon.File = file;
    }

    /// <summary>
    /// Gives a record the next number of the file: id, string id <c>&lt;n&gt;-&lt;suffix&gt;-INGAME</c>.
    /// </summary>
    static void AssignId(SaveFile file, FcsRecord r, string suffix, string stateSuffix = "")
    {
        int n = NextNumber(file);
        r.Id = n;
        r.StringId = $"{n}-{suffix}-INGAME{stateSuffix}";
        file.Data.NextId = n;
    }

    /// <summary>
    /// Adds a character to a platoon: an instance in the file's collection and the six state records in the order the game writes them, with the
    /// handle (type 1, the platoon's container and serial, the next free slot from 1). The records start with the defaults of a character that has done nothing;
    /// fill STATS, MEDICAL_STATE and CHARACTER_APPEARANCE afterwards.
    /// </summary>
    public SaveCharacter AddCharacter(SavePlatoon platoon, string recordId, string name, string ownerFactionId, Vector3 position, Quaternion rotation,
        bool isLeader = false, int squadMemberType = 0, float age = 0.5f)
    {
        var file = platoon.File ?? throw new InvalidOperationException("The platoon has no file.");
        var instance = new FcsInstance { Target = recordId, Position = position, Rotation = rotation };
        var c = new SaveCharacter(platoon, instance);

        // The file's records come in the order: the character's items are made first by callers, so the six states are appended here in a fixed order.
        c.GameState = AddState(file, instance, FcsRecordType.GamestateCharacter);
        c.Ai = AddState(file, instance, FcsRecordType.GamestateAi, "ai");
        c.Inventory = AddState(file, instance, FcsRecordType.InventoryState);
        c.Medical = AddState(file, instance, FcsRecordType.MedicalState);
        c.Stats = AddState(file, instance, FcsRecordType.Stats, name);
        c.Appearance = AddState(file, instance, FcsRecordType.CharacterAppearance);

        var g = c.GameState;
        FillGameState(g, name, ownerFactionId, isLeader, squadMemberType, age);
        c.Ai!.Bools["jobs"] = true;
        JoinPlatoon(platoon, file, c, g);
        return c;
    }

    /// <summary>
    /// A character's state record appended to the platoon file with the file's next number, named in the character's instance.
    /// </summary>
    static FcsRecord AddState(SaveFile file, FcsInstance instance, FcsRecordType type, string recordName = "0")
    {
        var r = new FcsRecord { Type = (int)type, Name = recordName };
        AssignId(file, r, "");
        file.Records.Add(r);
        instance.States.Add(r.StringId);
        return r;
    }

    /// <summary>
    /// The GAMESTATE_CHARACTER defaults of a character that has done nothing (its portrait serial is the next <see cref="Serial"/>).
    /// </summary>
    void FillGameState(FcsRecord g, string name, string ownerFactionId, bool isLeader, int squadMemberType, float age)
    {
        foreach (var k in new[] { "tn", "escap", "kidn", "carrying", "stealth" }) g.Bools[k] = false;
        g.Bools["is leader"] = isLeader;
        g.Floats["ssct"] = 0;
        g.Floats["disguiseblown"] = 0;
        g.Floats["age"] = age;
        g.Floats["decay"] = 0;
        g.Ints["TI day"] = 0;
        g.Ints["slavestate"] = 0;
        g.Ints["in something"] = 0;
        g.Ints["portrait_serial"] = Serial();
        g.Ints["squad mem type"] = squadMemberType;
        g.Ints["personality"] = 0;
        foreach (var prefix in new[] { "TI town", "isindoors", "carrying", "in what", "slaver" }) Hand.Null.Write(g, prefix);
        g.Strings["name"] = name;
        g.Strings["owner faction ID"] = ownerFactionId;
    }

    /// <summary>
    /// Gives the character its handle (the platoon's container and serial, the next free slot from 1) and its instance a number, and adds it to the
    /// platoon file's collection and the platoon.
    /// </summary>
    void JoinPlatoon(SavePlatoon platoon, SaveFile file, SaveCharacter c, FcsRecord g)
    {
        var instance = c.Instance;
        var collection = platoon.Collection ?? throw new InvalidOperationException("The platoon file has no collection.");
        int slot = platoon.Characters.Count == 0 ? 1 : platoon.Characters.Max(x => x.Handle.I) + 1;
        var ph = platoon.Handle;
        new Hand((int)FcsRecordType.Character, ph.C, ph.Cs, slot, Serial()).Write(g, "handle");

        int n = NextNumber(file);
        instance.Id = $"{n}--INGAME";
        file.Data.NextId = n;
        collection.Instances.Add(instance);
        collection.Ints["char count"] = collection.Instances.Count;
        platoon.CharCount = collection.Instances.Count;
        platoon.Characters.Add(c);
    }

    /// <summary>
    /// Removes a platoon: its PLATOON record and its file (the original deletes the record of a platoon it destroys,
    /// docs/game/factions-squads-towns.md "Persistent platoons").
    /// </summary>
    public void RemovePlatoon(SavePlatoon platoon)
    {
        Game.Quick.Records.Remove(platoon.Record);
        Game.Platoons.Remove(platoon);
    }

    /// <summary>
    /// Removes a character from its platoon: its instance, the six state records and everything its inventory holds (items and the inventories of containers
    /// inside it). The other characters keep their slots.
    /// </summary>
    public void RemoveCharacter(SavePlatoon platoon, SaveCharacter character)
    {
        var file = platoon.File ?? throw new InvalidOperationException("The platoon has no file.");
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in file.Records) byId[r.StringId] = r;
        var doomed = new HashSet<string>(StringComparer.Ordinal);
        void Take(string id)
        {
            if (!doomed.Add(id) || !byId.TryGetValue(id, out var r)) return;
            foreach (var i in r.Instances) { Take(i.Target); foreach (var s in i.States) Take(s); }
        }
        foreach (var id in character.Instance.States) Take(id);
        file.Records.RemoveAll(r => doomed.Contains(r.StringId));
        if (platoon.Collection is { } collection)
        {
            collection.Instances.Remove(character.Instance);
            collection.Ints["char count"] = collection.Instances.Count;
        }
        platoon.Characters.Remove(character);
        platoon.CharCount = platoon.Characters.Count;
    }

    /// <summary>
    /// Replaces what a character's INVENTORY_STATE holds with <paramref name="items"/>: the old item records (and the contents of containers among them) leave the file, and
    /// each item gets an INVENTORY_ITEM_STATE (<see cref="SaveInventory"/>) linked by an instance numbered from 1; a container's contents get an INVENTORY_STATE of their
    /// own, linked by the item's one instance, as the game writes them. An item read from this inventory keeps its record's id and place in the file (and a container its
    /// contents' state), so an inventory that did not change is written back the same.
    /// </summary>
    public void SetInventory(SavePlatoon platoon, SaveCharacter character, IReadOnlyList<SaveItem> items)
    {
        var file = platoon.File ?? throw new InvalidOperationException("The platoon has no file.");
        var inventory = character.Inventory ?? throw new InvalidOperationException("The character has no INVENTORY_STATE.");
        SaveInventory.Replace(file, inventory, items, n => $"{n}--INGAME");
    }

    #endregion
}

static class SaveFileExtensions
{
    public static SaveFile WithRecord(this SaveFile file, FcsRecord record)
    {
        record.Id = 1;
        record.StringId = $"1-{SaveFolder.QuickSaveName}-INGAME";
        file.Data.NextId = 1;
        file.Records.Add(record);
        return file;
    }
}