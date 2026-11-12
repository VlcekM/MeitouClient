using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>
/// One entry of a faction's relation table: the faction it is about, the relation (-100 to 100), the two trust values and the war flag (Meitou's own
/// key <c>meitou war&lt;k&gt;</c>; the original saves none).
/// </summary>
public readonly record struct SaveRelation(string FactionId, float Relation, float Trust, float TrustNeg, bool War = false);

/// <summary>
/// A GAMESTATE_FACTION (type 37) with its WAR_SAVESTATE (type 9) (docs/formats/save.md "Factions").
/// </summary>
public sealed class SaveFaction(FcsRecord record, FcsRecord? war)
{
    /// <summary>
    /// Meitou-only key prefix of a relation entry's war flag (<c>meitou war&lt;k&gt;</c>, bool, written only while set).
    /// </summary>
    public const string WarKey = "meitou war";

    /// <summary>
    /// The GAMESTATE_FACTION record the properties read and write.
    /// </summary>
    public FcsRecord Record { get; } = record;

    /// <summary>
    /// The faction's war state; the two are paired by the faction id in the war record's <c>faction</c> string (one each, 103 of 103 in the
    /// three saves).
    /// </summary>
    public FcsRecord? War { get; set; } = war;

    /// <summary>
    /// The FACTION record id (<c>gamedata stringID</c>).
    /// </summary>
    public string Id { get => Record.Strings.GetValueOrDefault("gamedata stringID", ""); set => Record.Strings["gamedata stringID"] = value; }

    /// <summary>
    /// The record's name: the faction's name.
    /// </summary>
    public string Name { get => Record.Name; set => Record.Name = value; }

    /// <summary>
    /// <c>prosperity</c>.
    /// </summary>
    public float Prosperity { get => Record.Floats.GetValueOrDefault("prosperity"); set => Record.Floats["prosperity"] = value; }

    /// <summary>
    /// The faction's platoon name counter: the next <c>&lt;n&gt;</c> of <c>&lt;faction&gt;_&lt;n&gt;</c>.
    /// </summary>
    public int PlatoonCounter { get => Record.Ints.GetValueOrDefault("platoonIDs"); set => Record.Ints["platoonIDs"] = value; }

    /// <summary>
    /// The player's rank in this faction (<c>rank</c>, every faction but the player's; docs/game/factions-squads-towns.md "Faction rank").
    /// </summary>
    public int Rank { get => Record.Ints.GetValueOrDefault("rank"); set => Record.Ints["rank"] = value; }

    /// <summary>
    /// The faction's global trust (FactionRelations +0x18): Meitou-only <c>meitou global trust</c> (float, written only while not 0; the original's
    /// player-table <c>global trust</c> is another field, <see cref="PlayerGlobalTrust"/>).
    /// </summary>
    public float GlobalTrust
    {
        get => Record.Floats.GetValueOrDefault("meitou global trust");
        set
        {
            if (value != 0) Record.Floats["meitou global trust"] = value;
            else Record.Floats.Remove("meitou global trust");
        }
    }

    /// <summary>
    /// The original's <c>global trust</c> of the player's faction (the PlayerFactionRelations float at +0x14; its link to the +0x18 DC_REPUTATION
    /// reads is <b>Unknown</b>).
    /// </summary>
    public float PlayerGlobalTrust => Record.Floats.GetValueOrDefault("global trust");

    /// <summary>
    /// The player's faction has a <c>known</c> list and no relation table.
    /// </summary>
    public bool IsPlayer => Record.References.ContainsKey("known") || !Record.Strings.ContainsKey("relationSID0");

    /// <summary>
    /// The player faction's <c>known</c> list: the FACTION ids of the factions the player has met (docs/game/factions-squads-towns.md "Factions
    /// met"); empty when there is none.
    /// </summary>
    public IReadOnlyList<string> Known
    {
        get => [.. (Record.References.GetValueOrDefault("known") ?? []).Select(r => r.TargetStringId)];
        set => Record.References["known"] = [.. value.Select(id => new FcsReference(id, 0, 0, 0))];
    }

    /// <summary>
    /// The relation table, in the order of its counter <c>k</c> (<c>relation&lt;k&gt;</c>, <c>trust&lt;k&gt;</c>, <c>trustNeg&lt;k&gt;</c>,
    /// <c>relationSID&lt;k&gt;</c>).
    /// </summary>
    public IReadOnlyList<SaveRelation> Relations
    {
        get
        {
            var list = new List<SaveRelation>();
            for (int k = 0; Record.Strings.TryGetValue("relationSID" + k, out var sid); k++)
            {
                list.Add(new SaveRelation(sid, Record.Floats.GetValueOrDefault("relation" + k), Record.Floats.GetValueOrDefault("trust" + k),
                    Record.Floats.GetValueOrDefault("trustNeg" + k), Record.Bools.GetValueOrDefault(WarKey + k)));
            }
            return list;
        }
    }

    /// <summary>
    /// Replaces the relation table.
    /// </summary>
    public void SetRelations(IEnumerable<SaveRelation> relations)
    {
        for (int k = 0; Record.Strings.ContainsKey("relationSID" + k); k++)
        {
            Record.Strings.Remove("relationSID" + k);
            Record.Floats.Remove("relation" + k);
            Record.Floats.Remove("trust" + k);
            Record.Floats.Remove("trustNeg" + k);
            Record.Bools.Remove(WarKey + k);
        }
        int n = 0;
        foreach (var r in relations)
        {
            Record.Floats["relation" + n] = r.Relation;
            Record.Floats["trust" + n] = r.Trust;
            Record.Floats["trustNeg" + n] = r.TrustNeg;
            if (r.War) Record.Bools[WarKey + n] = true;
            Record.Strings["relationSID" + n] = r.FactionId;
            n++;
        }
    }

    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>
/// A GAMESTATE_TOWN (type 94): the state of a town or a nest. The placement itself is not saved (docs/formats/save.md "Towns").
/// </summary>
public sealed class SaveTown(FcsRecord record)
{
    /// <summary>
    /// The GAMESTATE_TOWN record the properties read and write.
    /// </summary>
    public FcsRecord Record { get; } = record;

    /// <summary>
    /// The instance id of the placement in the level data (<c>9-rebirth-INGAME</c>); empty for a nest.
    /// </summary>
    public string Instance { get => Record.Strings.GetValueOrDefault("instance", ""); set => Record.Strings["instance"] = value; }

    /// <summary>
    /// <c>faction</c>: the FACTION id of the town's owners.
    /// </summary>
    public string FactionId { get => Record.Strings.GetValueOrDefault("faction", ""); set => Record.Strings["faction"] = value; }

    /// <summary>
    /// <c>is nest</c>: the record is a nest's state.
    /// </summary>
    public bool IsNest => Record.Bools.GetValueOrDefault("is nest");

    /// <summary>
    /// <c>discovered</c>: the player has found the town.
    /// </summary>
    public bool Discovered => Record.Bools.GetValueOrDefault("discovered");

    /// <summary>
    /// The town's handle (<c>hand</c>).
    /// </summary>
    public Hand Handle { get => Hand.Read(Record, "hand"); set => value.Write(Record, "hand"); }

    /// <summary>
    /// The zone the town is in (<c>zzX0</c>, <c>zzY0</c>), when the record has it.
    /// </summary>
    public ZoneCoordinate? Zone => Record.Ints.TryGetValue("zzX0", out int x) ? new ZoneCoordinate(x, Record.Ints.GetValueOrDefault("zzY0")) : null;
    public override string ToString() => Record.Name;
}

/// <summary>
/// One character of a platoon file: its instance in the file's collection and the six state records it points at, in the order the game writes them
/// (docs/formats/save.md ".platoon files").
/// </summary>
public sealed class SaveCharacter(SavePlatoon platoon, FcsInstance instance)
{
    /// <summary>
    /// The platoon the character belongs to.
    /// </summary>
    public SavePlatoon Platoon { get; } = platoon;

    /// <summary>
    /// The character's instance in the platoon file's collection.
    /// </summary>
    public FcsInstance Instance { get; } = instance;

    /// <summary>
    /// The GAMESTATE_CHARACTER: name, faction, handle and squad role.
    /// </summary>
    public FcsRecord? GameState { get; set; }

    /// <summary>
    /// The GAMESTATE_AI record.
    /// </summary>
    public FcsRecord? Ai { get; set; }

    /// <summary>
    /// The INVENTORY_STATE record.
    /// </summary>
    public FcsRecord? Inventory { get; set; }

    /// <summary>
    /// The MEDICAL_STATE record.
    /// </summary>
    public FcsRecord? Medical { get; set; }

    /// <summary>
    /// The STATS record.
    /// </summary>
    public FcsRecord? Stats { get; set; }

    /// <summary>
    /// The CHARACTER_APPEARANCE record.
    /// </summary>
    public FcsRecord? Appearance { get; set; }

    /// <summary>
    /// The CHARACTER (or ANIMAL_CHARACTER) record the character was made from.
    /// </summary>
    public string RecordId { get => Instance.Target; set => Instance.Target = value; }

    /// <summary>
    /// The instance's position.
    /// </summary>
    public Vector3 Position { get => Instance.Position; set => Instance.Position = value; }

    /// <summary>
    /// The instance's rotation.
    /// </summary>
    public Quaternion Rotation { get => Instance.Rotation; set => Instance.Rotation = value; }

    /// <summary>
    /// The state's <c>name</c>; empty without a state.
    /// </summary>
    public string Name => GameState?.Strings.GetValueOrDefault("name", "") ?? "";

    /// <summary>
    /// The state's <c>owner faction ID</c>; empty without a state.
    /// </summary>
    public string OwnerFactionId => GameState?.Strings.GetValueOrDefault("owner faction ID", "") ?? "";

    /// <summary>
    /// The state's <c>handle</c>; the null handle without a state (setting one needs the state).
    /// </summary>
    public Hand Handle { get => GameState is null ? Hand.Null : Hand.Read(GameState, "handle"); set => value.Write(GameState!, "handle"); }

    /// <summary>
    /// The state's <c>is leader</c>: the character leads its squad.
    /// </summary>
    public bool IsLeader => GameState?.Bools.GetValueOrDefault("is leader") ?? false;

    /// <summary>
    /// The state's <c>squad mem type</c>; 0 without a state.
    /// </summary>
    public int SquadMemberType => GameState?.Ints.GetValueOrDefault("squad mem type") ?? 0;

    /// <summary>
    /// The state records the character has, in the order the game writes them.
    /// </summary>
    public IEnumerable<FcsRecord> StateRecords => new[] { GameState, Ai, Inventory, Medical, Stats, Appearance }.OfType<FcsRecord>();
    public override string ToString() => $"{Name} ({RecordId})";
}

/// <summary>
/// A PLATOON record (type 34) of <c>quick.save</c> and, when the platoon is loaded, its <c>.platoon</c> file with the characters.
/// </summary>
public sealed class SavePlatoon(FcsRecord record, SaveFile? file)
{
    /// <summary>
    /// The PLATOON record the properties read and write.
    /// </summary>
    public FcsRecord Record { get; } = record;

    /// <summary>
    /// The platoon file; null for a platoon that is not loaded (its <c>content file</c> is just <c>platoon/</c>).
    /// </summary>
    public SaveFile? File { get; set; } = file;

    /// <summary>
    /// The characters of the platoon file.
    /// </summary>
    public List<SaveCharacter> Characters { get; } = [];

    /// <summary>
    /// <c>&lt;faction name&gt;_&lt;n&gt;</c>, also the record's string id.
    /// </summary>
    public string Name
    {
        get => Record.Strings.GetValueOrDefault("platoon stringID", Record.StringId);
        set => Record.Strings["platoon stringID"] = value;
    }

    /// <summary>
    /// <c>faction name</c>.
    /// </summary>
    public string FactionName => Record.Strings.GetValueOrDefault("faction name", "");

    /// <summary>
    /// <c>faction stringID</c>: the FACTION id.
    /// </summary>
    public string FactionId { get => Record.Strings.GetValueOrDefault("faction stringID", ""); set => Record.Strings["faction stringID"] = value; }

    /// <summary>
    /// <c>squad template</c>: the SQUAD_TEMPLATE id; empty when none.
    /// </summary>
    public string SquadTemplate => Record.Strings.GetValueOrDefault("squad template", "");

    /// <summary>
    /// <c>content file</c>: <c>platoon/</c> and the file name when the platoon has a file.
    /// </summary>
    public string ContentFile { get => Record.Filenames.GetValueOrDefault("content file", ""); set => Record.Filenames["content file"] = value; }

    /// <summary>
    /// The name inside <c>platoon/</c>, or null when the platoon has no file.
    /// </summary>
    public string? FileName => ContentFile.Length > "platoon/".Length ? ContentFile : null;

    /// <summary>
    /// Whether the platoon's file is loaded.
    /// </summary>
    public bool IsLoaded => File is not null;

    /// <summary>
    /// The platoon's <c>handle</c>.
    /// </summary>
    public Hand Handle { get => Hand.Read(Record, "handle"); set => value.Write(Record, "handle"); }

    /// <summary>
    /// <c>position</c>.
    /// </summary>
    public Vector3 Position { get => Record.Vector3S.GetValueOrDefault("position"); set => Record.Vector3S["position"] = value; }

    /// <summary>
    /// <c>money</c>.
    /// </summary>
    public int Money { get => Record.Ints.GetValueOrDefault("money"); set => Record.Ints["money"] = value; }

    /// <summary>
    /// <c>char count</c>: the number of characters.
    /// </summary>
    public int CharCount { get => Record.Ints.GetValueOrDefault("char count"); set => Record.Ints["char count"] = value; }

    /// <summary>
    /// <c>dead</c>: the platoon is dead.
    /// </summary>
    public bool IsDead => Record.Bools.GetValueOrDefault("dead");

    /// <summary>
    /// The collection of the file that lists the characters.
    /// </summary>
    public FcsRecord? Collection =>
        File?.Records.FirstOrDefault(r => r.RecordType == FcsRecordType.InstanceCollection && r.Ints.ContainsKey("char count"));

    public override string ToString() => Name;
}

/// <summary>
/// A building of a zone as the saved list names it: the placement, the state record, and where it came from.
/// </summary>
public sealed record SavedBuilding(string InstanceId, string Target, Vector3 Position, Quaternion Rotation, FcsRecord? State, bool InBase,
    bool Overrides);

/// <summary>
/// A <c>zone.X.Y.zone</c> file (docs/formats/save.md ".zone files").
/// </summary>
public sealed class SaveZone(ZoneCoordinate coordinate, SaveFile file)
{
    /// <summary>
    /// The zone's grid coordinate.
    /// </summary>
    public ZoneCoordinate Coordinate { get; } = coordinate;

    /// <summary>
    /// The zone file.
    /// </summary>
    public SaveFile File { get; } = file;

    /// <summary>
    /// The container id every handle of an object in this zone has as <c>C</c> (<c>Y * 64 + X + 1</c>).
    /// </summary>
    public int ContainerId => ContainerIdOf(Coordinate);

    /// <summary>
    /// The container id of the zone at <paramref name="z"/> (see <see cref="ContainerId"/>).
    /// </summary>
    public static int ContainerIdOf(ZoneCoordinate z) => z.Y * WorldLayout.ZoneCount + z.X + 1;

    /// <summary>
    /// <c>0-buildinglist</c>: the zone's whole placed-building list; null when the zone has no building.
    /// </summary>
    public FcsRecord? BuildingList => File.Records.FirstOrDefault(IsBuildingList);

    /// <summary>
    /// <c>1-itemlist</c>: loose items lying in the zone.
    /// </summary>
    public FcsRecord? ItemList => File.Records.FirstOrDefault(IsItemList);

    /// <summary>
    /// The zone's INSTANCE_COLLECTION of buildings (<c>*-buildinglist</c>).
    /// </summary>
    static bool IsBuildingList(FcsRecord r) =>
        r.StringId.EndsWith("-buildinglist", StringComparison.Ordinal) && r.RecordType == FcsRecordType.InstanceCollection;

    /// <summary>
    /// The zone's INSTANCE_COLLECTION of ground items (<c>*-itemlist</c>).
    /// </summary>
    static bool IsItemList(FcsRecord r) =>
        r.StringId.EndsWith("-itemlist", StringComparison.Ordinal) && r.RecordType == FcsRecordType.InstanceCollection;

    /// <summary>
    /// The GAMESTATE_BUILDING records of the zone file.
    /// </summary>
    public IEnumerable<FcsRecord> BuildingStates => File.Records.Where(r => r.RecordType == FcsRecordType.GamestateBuilding);

    /// <summary>
    /// The saved building list laid over the base game's placements of the zone, by instance id (the order the game loads them in: base layers first,
    /// then the save's zone file on top, so an id in both is overwritten). <paramref name="baseZone"/> is <c>WorldLevelData.Zones[coordinate]</c> or
    /// null. A base placement the save lacks stays as it is; an entry the save clears (empty target) clears the base one.
    /// </summary>
    public List<SavedBuilding> Overlay(GameDatabase? baseZone)
    {
        var states = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in File.Records) states[r.StringId] = r;
        var merged = new Dictionary<string, SavedBuilding>(StringComparer.Ordinal);
        var order = new List<string>();
        if (baseZone?.Find(WorldLevelData.BuildingListId) is { } baseList)
            foreach (var (id, i) in baseList.Instances)
            {
                merged[id] = new SavedBuilding(id, i.Target, i.Position, i.Rotation, null, InBase: true, Overrides: false);
                order.Add(id);
            }
        if (BuildingList is { } list)
            foreach (var i in list.Instances)
            {
                // The building state; an inventory state can come first.
                FcsRecord? state = i.States
                    .Select(s => states.GetValueOrDefault(s))
                    .FirstOrDefault(r => r?.RecordType == FcsRecordType.GamestateBuilding)
                    ?? (i.States.Count > 0 ? states.GetValueOrDefault(i.States[0]) : null);
                bool inBase = merged.ContainsKey(i.Id);
                if (!inBase) order.Add(i.Id);
                merged[i.Id] = new SavedBuilding(i.Id, i.Target, i.Position, i.Rotation, state, inBase, inBase);
            }
        return [.. order.Select(id => merged[id])];
    }

    public override string ToString() => $"zone.{Coordinate.X}.{Coordinate.Y}";
}