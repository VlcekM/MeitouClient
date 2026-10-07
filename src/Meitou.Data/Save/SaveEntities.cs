using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>One entry of a faction's relation table: the faction it is about, the relation (-100 to 100) and the two trust values.</summary>
public readonly record struct SaveRelation(string FactionId, float Relation, float Trust, float TrustNeg);

/// <summary>A GAMESTATE_FACTION (type 37) with its WAR_SAVESTATE (type 9) (docs/formats/save.md "Factions").</summary>
public sealed class SaveFaction(FcsRecord record, FcsRecord? war)
{
    public FcsRecord Record { get; } = record;

    /// <summary>The faction's war state; the two are paired by the faction id in the war record's <c>faction</c> string (one each, 103 of 103 in the three saves).</summary>
    public FcsRecord? War { get; set; } = war;

    /// <summary>The FACTION record id (<c>gamedata stringID</c>).</summary>
    public string Id { get => Record.Strings.GetValueOrDefault("gamedata stringID", ""); set => Record.Strings["gamedata stringID"] = value; }
    public string Name { get => Record.Name; set => Record.Name = value; }
    public float Prosperity { get => Record.Floats.GetValueOrDefault("prosperity"); set => Record.Floats["prosperity"] = value; }

    /// <summary>The faction's platoon name counter: the next <c>&lt;n&gt;</c> of <c>&lt;faction&gt;_&lt;n&gt;</c>.</summary>
    public int PlatoonCounter { get => Record.Ints.GetValueOrDefault("platoonIDs"); set => Record.Ints["platoonIDs"] = value; }

    /// <summary>The player's faction has a <c>known</c> list and no relation table.</summary>
    public bool IsPlayer => Record.References.ContainsKey("known") || !Record.Strings.ContainsKey("relationSID0");

    /// <summary>The relation table, in the order of its counter <c>k</c> (<c>relation&lt;k&gt;</c>, <c>trust&lt;k&gt;</c>, <c>trustNeg&lt;k&gt;</c>, <c>relationSID&lt;k&gt;</c>).</summary>
    public IReadOnlyList<SaveRelation> Relations
    {
        get
        {
            var list = new List<SaveRelation>();
            for (int k = 0; Record.Strings.TryGetValue("relationSID" + k, out var sid); k++)
                list.Add(new SaveRelation(sid, Record.Floats.GetValueOrDefault("relation" + k), Record.Floats.GetValueOrDefault("trust" + k), Record.Floats.GetValueOrDefault("trustNeg" + k)));
            return list;
        }
    }

    /// <summary>Replaces the relation table.</summary>
    public void SetRelations(IEnumerable<SaveRelation> relations)
    {
        for (int k = 0; Record.Strings.ContainsKey("relationSID" + k); k++)
        {
            Record.Strings.Remove("relationSID" + k);
            Record.Floats.Remove("relation" + k);
            Record.Floats.Remove("trust" + k);
            Record.Floats.Remove("trustNeg" + k);
        }
        int n = 0;
        foreach (var r in relations)
        {
            Record.Floats["relation" + n] = r.Relation;
            Record.Floats["trust" + n] = r.Trust;
            Record.Floats["trustNeg" + n] = r.TrustNeg;
            Record.Strings["relationSID" + n] = r.FactionId;
            n++;
        }
    }

    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>A GAMESTATE_TOWN (type 94): the state of a town or a nest. The placement itself is not saved (docs/formats/save.md "Towns").</summary>
public sealed class SaveTown(FcsRecord record)
{
    public FcsRecord Record { get; } = record;

    /// <summary>The instance id of the placement in the level data (<c>9-rebirth-INGAME</c>); empty for a nest.</summary>
    public string Instance { get => Record.Strings.GetValueOrDefault("instance", ""); set => Record.Strings["instance"] = value; }
    public string FactionId { get => Record.Strings.GetValueOrDefault("faction", ""); set => Record.Strings["faction"] = value; }
    public bool IsNest => Record.Bools.GetValueOrDefault("is nest");
    public bool Discovered => Record.Bools.GetValueOrDefault("discovered");
    public Hand Handle { get => Hand.Read(Record, "hand"); set => value.Write(Record, "hand"); }

    /// <summary>The zone the town is in (<c>zzX0</c>, <c>zzY0</c>), when the record has it.</summary>
    public ZoneCoordinate? Zone => Record.Ints.TryGetValue("zzX0", out int x) ? new ZoneCoordinate(x, Record.Ints.GetValueOrDefault("zzY0")) : null;
    public override string ToString() => Record.Name;
}

/// <summary>
/// One character of a platoon file: its instance in the file's collection and the six state records it points at, in the order the game writes them
/// (docs/formats/save.md ".platoon files").
/// </summary>
public sealed class SaveCharacter(SavePlatoon platoon, FcsInstance instance)
{
    public SavePlatoon Platoon { get; } = platoon;
    public FcsInstance Instance { get; } = instance;
    public FcsRecord? GameState { get; set; }
    public FcsRecord? Ai { get; set; }
    public FcsRecord? Inventory { get; set; }
    public FcsRecord? Medical { get; set; }
    public FcsRecord? Stats { get; set; }
    public FcsRecord? Appearance { get; set; }

    /// <summary>The CHARACTER (or ANIMAL_CHARACTER) record the character was made from.</summary>
    public string RecordId { get => Instance.Target; set => Instance.Target = value; }
    public Vector3 Position { get => Instance.Position; set => Instance.Position = value; }
    public Quaternion Rotation { get => Instance.Rotation; set => Instance.Rotation = value; }
    public string Name => GameState?.Strings.GetValueOrDefault("name", "") ?? "";
    public string OwnerFactionId => GameState?.Strings.GetValueOrDefault("owner faction ID", "") ?? "";
    public Hand Handle { get => GameState is null ? Hand.Null : Hand.Read(GameState, "handle"); set => value.Write(GameState!, "handle"); }
    public bool IsLeader => GameState?.Bools.GetValueOrDefault("is leader") ?? false;
    public int SquadMemberType => GameState?.Ints.GetValueOrDefault("squad mem type") ?? 0;

    public IEnumerable<FcsRecord> StateRecords => new[] { GameState, Ai, Inventory, Medical, Stats, Appearance }.OfType<FcsRecord>();
    public override string ToString() => $"{Name} ({RecordId})";
}

/// <summary>A PLATOON record (type 34) of <c>quick.save</c> and, when the platoon is loaded, its <c>.platoon</c> file with the characters.</summary>
public sealed class SavePlatoon(FcsRecord record, SaveFile? file)
{
    public FcsRecord Record { get; } = record;

    /// <summary>The platoon file; null for a platoon that is not loaded (its <c>content file</c> is just <c>platoon/</c>).</summary>
    public SaveFile? File { get; set; } = file;
    public List<SaveCharacter> Characters { get; } = [];

    /// <summary><c>&lt;faction name&gt;_&lt;n&gt;</c>, also the record's string id.</summary>
    public string Name { get => Record.Strings.GetValueOrDefault("platoon stringID", Record.StringId); set => Record.Strings["platoon stringID"] = value; }
    public string FactionName => Record.Strings.GetValueOrDefault("faction name", "");
    public string FactionId { get => Record.Strings.GetValueOrDefault("faction stringID", ""); set => Record.Strings["faction stringID"] = value; }
    public string SquadTemplate => Record.Strings.GetValueOrDefault("squad template", "");
    public string ContentFile { get => Record.Filenames.GetValueOrDefault("content file", ""); set => Record.Filenames["content file"] = value; }

    /// <summary>The name inside <c>platoon/</c>, or null when the platoon has no file.</summary>
    public string? FileName => ContentFile.Length > "platoon/".Length ? ContentFile : null;
    public bool IsLoaded => File is not null;
    public Hand Handle { get => Hand.Read(Record, "handle"); set => value.Write(Record, "handle"); }
    public Vector3 Position { get => Record.Vector3s.GetValueOrDefault("position"); set => Record.Vector3s["position"] = value; }
    public int Money { get => Record.Ints.GetValueOrDefault("money"); set => Record.Ints["money"] = value; }
    public int CharCount { get => Record.Ints.GetValueOrDefault("char count"); set => Record.Ints["char count"] = value; }
    public bool IsDead => Record.Bools.GetValueOrDefault("dead");

    /// <summary>The collection of the file that lists the characters.</summary>
    public FcsRecord? Collection => File?.Records.FirstOrDefault(r => r.RecordType == FcsRecordType.INSTANCE_COLLECTION && r.Ints.ContainsKey("char count"));

    public override string ToString() => Name;
}

/// <summary>A building of a zone as the saved list names it: the placement, the state record, and where it came from.</summary>
public sealed record SavedBuilding(string InstanceId, string Target, Vector3 Position, Quaternion Rotation, FcsRecord? State, bool InBase, bool Overrides);

/// <summary>A <c>zone.X.Y.zone</c> file (docs/formats/save.md ".zone files").</summary>
public sealed class SaveZone(ZoneCoordinate coordinate, SaveFile file)
{
    public ZoneCoordinate Coordinate { get; } = coordinate;
    public SaveFile File { get; } = file;

    /// <summary>The container id every handle of an object in this zone has as <c>C</c> (<c>Y * 64 + X + 1</c>).</summary>
    public int ContainerId => ContainerIdOf(Coordinate);
    public static int ContainerIdOf(ZoneCoordinate z) => z.Y * WorldLayout.ZoneCount + z.X + 1;

    /// <summary><c>0-buildinglist</c>: the zone's whole placed-building list; null when the zone has no building.</summary>
    public FcsRecord? BuildingList => File.Records.FirstOrDefault(r => r.StringId.EndsWith("-buildinglist", StringComparison.Ordinal) && r.RecordType == FcsRecordType.INSTANCE_COLLECTION);

    /// <summary><c>1-itemlist</c>: loose items lying in the zone.</summary>
    public FcsRecord? ItemList => File.Records.FirstOrDefault(r => r.StringId.EndsWith("-itemlist", StringComparison.Ordinal) && r.RecordType == FcsRecordType.INSTANCE_COLLECTION);

    public IEnumerable<FcsRecord> BuildingStates => File.Records.Where(r => r.RecordType == FcsRecordType.GAMESTATE_BUILDING);

    /// <summary>
    /// The saved building list laid over the base game's placements of the zone, by instance id (the order the game loads them in: base layers first, then the
    /// save's zone file on top, so an id in both is overwritten). <paramref name="baseZone"/> is <c>WorldLevelData.Zones[coordinate]</c> or null.
    /// A base placement the save lacks stays as it is; an entry the save clears (empty target) clears the base one.
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
                FcsRecord? state = i.States.Count > 0 ? states.GetValueOrDefault(i.States[0]) : null;
                bool inBase = merged.ContainsKey(i.Id);
                if (!inBase) order.Add(i.Id);
                merged[i.Id] = new SavedBuilding(i.Id, i.Target, i.Position, i.Rotation, state, inBase, inBase);
            }
        return [.. order.Select(id => merged[id])];
    }

    public override string ToString() => $"zone.{Coordinate.X}.{Coordinate.Y}";
}
