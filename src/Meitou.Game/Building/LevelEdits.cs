using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Game.Building;

/// <summary>
/// What the level editor changed in the level data (docs/game/level-editor.md): buildings it placed and level-data buildings it cleared, by key.
/// <see cref="Save"/> writes them as the zone files of a mod's <c>leveldata/</c> folder; nothing of the original's files is touched.
/// </summary>
public sealed class LevelEdits
{
    /// <summary>
    /// The mod the editor saves into, a folder of <c>mods/</c> made by Meitou (Meitou's choice; the original saves into a mod picked from the active
    /// ones).
    /// </summary>
    public const string ModName = "Meitou Level Editor";

    /// <summary>
    /// A file in the mod folder that marks it as Meitou's: a folder of that name without it is someone else's and is never written.
    /// </summary>
    public const string MarkerFile = "meitou-level-editor.txt";

    /// <summary>
    /// The buildings the editor placed this session, by placement key.
    /// </summary>
    readonly Dictionary<string, BuildingPlacement> _added = new(StringComparer.Ordinal);
    /// <summary>
    /// The level-data buildings the editor cleared, by placement key.
    /// </summary>
    readonly Dictionary<string, BuildingPlacement> _cleared = new(StringComparer.Ordinal);
    /// <summary>
    /// Per zone, the number the next placed building's instance id takes (above the zone's highest <c>nextId</c>).
    /// </summary>
    readonly Dictionary<ZoneCoordinate, int> _nextId = [];

    /// <summary>
    /// The buildings the editor placed.
    /// </summary>
    public IReadOnlyCollection<BuildingPlacement> Added => _added.Values;
    /// <summary>
    /// The level-data buildings the editor cleared.
    /// </summary>
    public IReadOnlyCollection<BuildingPlacement> Cleared => _cleared.Values;
    /// <summary>
    /// Whether the level-data building with this key was cleared.
    /// </summary>
    public bool IsCleared(string key) => _cleared.ContainsKey(key);
    /// <summary>
    /// Whether nothing was placed or cleared.
    /// </summary>
    public bool IsEmpty => _added.Count == 0 && _cleared.Count == 0;

    /// <summary>
    /// A new building at <paramref name="position"/> (absolute height): its instance id is <c>&lt;n&gt;-zone&lt;x&gt;.&lt;z&gt;-INGAME</c>, n
    /// counting on from the zone's highest <c>nextId</c> (the original's form for an id made in game, docs/formats/zones.md "Placement keys"), its
    /// state id the instance id with <c>-S23</c>.
    /// </summary>
    public BuildingPlacement Add(string record, Vector3 position, Quaternion rotation, float ground, WorldLevelData levels,
        string? interiorLayout = null, string? exteriorLayout = null)
    {
        var zone = WorldLayout.ZoneOf(position.X, position.Z);
        int n = _nextId.TryGetValue(zone, out int next) ? next : levels.ZoneInfo.TryGetValue(zone, out var info) ? info.NextId : 1;
        string id;
        do id = $"{n++}-zone{zone.X}.{zone.Y}-INGAME";
        while (levels.Zones.TryGetValue(zone, out var db) && db.Records.Values.Any(r => r.Instances.ContainsKey(id)));
        _nextId[zone] = n;
        var at = new Vector3(position.X, position.Y - ground, position.Z);
        var placement = new BuildingPlacement(zone, id, record, at, rotation, StateIdOf(id), position.Y)
        {
            InteriorLayout = interiorLayout,
            ExteriorLayout = exteriorLayout,
        };
        _added[placement.Key] = placement;
        return placement;
    }

    /// <summary>
    /// Clears a building: one placed here is forgotten (true), a level-data one is noted for the save (false). Null when it was cleared already.
    /// </summary>
    public bool? Clear(BuildingPlacement placement)
    {
        if (_added.Remove(placement.Key)) return true;
        return _cleared.TryAdd(placement.Key, placement) ? false : null;
    }

    /// <summary>
    /// Drops every edit (a new world or level data).
    /// </summary>
    public void Forget()
    {
        _added.Clear();
        _cleared.Clear();
        _nextId.Clear();
    }

    /// <summary>
    /// The zone file that adds <paramref name="placement"/> to the level data, as the saved mod holds it (<see cref="Save"/>): its state and its
    /// building-list entry. Merged into the running world's level data with <see cref="WorldLevelData.ApplyEdit"/>, the building is then a level
    /// building like one read at load.
    /// </summary>
    public static FcsFile PlacedFile(BuildingPlacement placement, string recordName) => EditFile(placement, recordName, cleared: false);

    /// <summary>
    /// The zone file that clears <paramref name="placement"/>: an entry of its id with no target, which empties the instance of the layers before.
    /// </summary>
    public static FcsFile ClearedFile(BuildingPlacement placement) => EditFile(placement, "", cleared: true);

    /// <summary>
    /// The zone file that puts a level building cleared earlier back (its state record was never taken out of the zone).
    /// </summary>
    public static FcsFile RestoredFile(BuildingPlacement placement) => EditFile(placement, "", cleared: false, withState: false);

    /// <summary>
    /// A zone file with the building list entry of <paramref name="p"/>: no target when <paramref name="cleared"/>, else its building with its state
    /// id (and, with <paramref name="withState"/>, its state record).
    /// </summary>
    static FcsFile EditFile(BuildingPlacement p, string recordName, bool cleared, bool withState = true)
    {
        var data = new FcsFile { FileType = FcsFileType.V15 };
        var list = new FcsRecord { Type = (int)FcsRecordType.InstanceCollection, Name = "0", StringId = WorldLevelData.BuildingListId };
        data.Records.Add(list);
        var instance = new FcsInstance { Id = p.InstanceId, Target = cleared ? "" : p.BuildingId, Position = p.Position, Rotation = p.Rotation };
        if (!cleared)
        {
            if (p.StateId is { } stateId) instance.States.Add(stateId);
            if (withState) data.Records.Insert(0, State(p, recordName));
        }
        list.Instances.Add(instance);
        return data;
    }

    /// <summary>
    /// The GAMESTATE_BUILDING state id of an instance: the instance id with <c>-S</c> and the record type in hex.
    /// </summary>
    static string StateIdOf(string instance)
    {
        string type = ((int)FcsRecordType.GamestateBuilding).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        return instance + "-S" + type;
    }

    /// <summary>
    /// Writes one <c>zone.X.Y.zone</c> per zone with an edit into <paramref name="folder"/> (the mod's <c>leveldata/</c>), merged over what an
    /// earlier save left there: a placed building is a <c>0-buildinglist</c> entry with its GAMESTATE_BUILDING state, a cleared one an entry of the
    /// same id with no target (the merge rule that removes an instance, docs/formats/zones.md). The zone's slot list
    /// (<see cref="ZoneLevelInfo.Slots"/>) is written as the trailer, because a layer without one empties it. Returns the zones written.
    /// </summary>
    public IReadOnlyList<ZoneCoordinate> Save(string folder, WorldLevelData levels, Func<string, string> recordName)
    {
        Directory.CreateDirectory(folder);
        var zones = EditedZones();
        foreach (var zone in zones)
        {
            string path = Path.Combine(folder, zone.FileName);
            var data = File.Exists(path) ? LevelFile.ReadFile(path).Data : new FcsFile { FileType = FcsFileType.V15 };
            File.WriteAllBytes(path, ZoneBytes(zone, data, levels, recordName));
        }
        return zones;
    }

    /// <summary>
    /// The session's edits as one zone file per edited zone, in the saved mod's form but without an earlier file to merge over: what a game save
    /// stores (docs/game/level-editor.md "In the game save").
    /// </summary>
    public SortedDictionary<ZoneCoordinate, byte[]> Snapshot(WorldLevelData levels, Func<string, string> recordName)
    {
        var order = Comparer<ZoneCoordinate>.Create((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var files = new SortedDictionary<ZoneCoordinate, byte[]>(order);
        foreach (var zone in EditedZones()) files[zone] = ZoneBytes(zone, new FcsFile { FileType = FcsFileType.V15 }, levels, recordName);
        return files;
    }

    /// <summary>
    /// The zones with an edit, by row, then column.
    /// </summary>
    List<ZoneCoordinate> EditedZones() =>
        [.. _added.Values.Concat(_cleared.Values).Select(p => p.Zone).Distinct().OrderBy(z => z.Y).ThenBy(z => z.X)];

    /// <summary>
    /// A zone's file: <paramref name="data"/> with the zone's cleared entries and placed buildings (with their states) put in, the building list
    /// last, the next id raised past the edits', and the zone's slot list as the trailer.
    /// </summary>
    byte[] ZoneBytes(ZoneCoordinate zone, FcsFile data, WorldLevelData levels, Func<string, string> recordName)
    {
        var list = data.Records.FirstOrDefault(r => r.StringId == WorldLevelData.BuildingListId);
        if (list is null)
        {
            list = new FcsRecord { Type = (int)FcsRecordType.InstanceCollection, Name = "0", StringId = WorldLevelData.BuildingListId };
            data.Records.Add(list);
        }
        foreach (var p in _cleared.Values.Where(p => p.Zone == zone))
        {
            Put(list, new FcsInstance { Id = p.InstanceId, Target = "", Position = p.Position, Rotation = p.Rotation });
            if (p.StateId is { } state) data.Records.RemoveAll(r => r.StringId == state);
        }
        foreach (var p in _added.Values.Where(p => p.Zone == zone))
        {
            var instance = new FcsInstance { Id = p.InstanceId, Target = p.BuildingId, Position = p.Position, Rotation = p.Rotation };
            instance.States.Add(p.StateId!);
            Put(list, instance);
            data.Records.RemoveAll(r => r.StringId == p.StateId);
            data.Records.Insert(data.Records.IndexOf(list), State(p, recordName(p.BuildingId)));
        }
        // The building list stays the last record, as in the game's files.
        data.Records.Remove(list);
        data.Records.Add(list);
        foreach (var r in data.Records) if (r.ByteSize == 0) r.ByteSize = 1;   // the writer then puts the real size
        int highest = _nextId.GetValueOrDefault(zone);
        data.NextId = Math.Max(data.NextId, Math.Max(highest, levels.ZoneInfo.TryGetValue(zone, out var info) ? info.NextId : 0));
        using var stream = new MemoryStream();
        FcsWriter.Write(data, stream);
        using var w = new BinaryWriter(stream, FcsReader.Encoding, leaveOpen: true);
        var slots = levels.ZoneInfo.TryGetValue(zone, out var zoneInfo) ? zoneInfo.Slots : [];
        w.Write(slots.Count);
        foreach (int slot in slots) w.Write(slot);
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Puts the instance into the building list, replacing one of the same id.
    /// </summary>
    static void Put(FcsRecord list, FcsInstance instance)
    {
        list.Instances.RemoveAll(i => i.Id == instance.Id);
        list.Instances.Add(instance);
    }

    /// <summary>
    /// Reads a zone file made by <see cref="Snapshot"/>: the buildings it places (with the height from their state) and the keys of the level
    /// buildings it clears. A file that does not read gives nothing.
    /// </summary>
    public static (List<BuildingPlacement> Placed, List<string> ClearedKeys) ReadSnapshot(ZoneCoordinate zone, byte[] bytes)
    {
        var placed = new List<BuildingPlacement>();
        var clearedKeys = new List<string>();
        FcsFile data;
        try
        {
            data = LevelFile.Read(bytes).Data;
        }
        catch (Exception e) when (e is FcsFormatException or EndOfStreamException or InvalidDataException)
        {
            return (placed, clearedKeys);
        }
        var states = data.Records.ToDictionary(r => r.StringId, StringComparer.Ordinal);
        foreach (var list in data.Records.Where(r => r.StringId == WorldLevelData.BuildingListId))
            foreach (var i in list.Instances)
            {
                if (i.Target.Length == 0)
                {
                    clearedKeys.Add(BuildingKey.Of(zone, i.Id));
                    continue;
                }
                string? stateId = i.States.FirstOrDefault();
                float? worldY = stateId is not null && states.TryGetValue(stateId, out var state)
                    && state.Floats.TryGetValue("world Y pos", out float y) ? y : null;
                string? interior = stateId is not null && states.TryGetValue(stateId, out var layoutState)
                    && layoutState.Strings.TryGetValue("interior layout name", out string? il) && il.Length > 0 ? il : null;
                string? exterior = stateId is not null && states.TryGetValue(stateId, out var layoutState2)
                    && layoutState2.Strings.TryGetValue("exterior layout name", out string? el) && el.Length > 0 ? el : null;
                placed.Add(new BuildingPlacement(zone, i.Id, i.Target, i.Position, i.Rotation, stateId, worldY)
                {
                    InteriorLayout = interior,
                    ExteriorLayout = exterior,
                });
            }
        return (placed, clearedKeys);
    }

    /// <summary>
    /// Takes a building read from a game save back among the session's edits (the level data and the world are told by the host), so the next save
    /// and Save Mod hold it again.
    /// </summary>
    public void Restore(BuildingPlacement placement)
    {
        _added[placement.Key] = placement;
        int dash = placement.InstanceId.IndexOf('-');
        if (dash > 0 && int.TryParse(placement.InstanceId.AsSpan(0, dash), out int n))
            _nextId[placement.Zone] = Math.Max(_nextId.GetValueOrDefault(placement.Zone), n + 1);
    }

    /// <summary>
    /// The placed building's state: complete, not destroyed, its absolute height (the keys a level building's state carries; the full set a save
    /// writes is in docs/formats/zones.md).
    /// </summary>
    static FcsRecord State(BuildingPlacement p, string name)
    {
        var state = new FcsRecord { Type = (int)FcsRecordType.GamestateBuilding, Name = "0", StringId = p.StateId! };
        state.Bools["is complete"] = true;
        state.Bools["destroyed"] = false;
        state.Floats["world Y pos"] = p.WorldY ?? p.Position.Y;
        state.Ints["version"] = 270;
        state.Ints["floornum"] = 0;
        state.Ints["flags"] = 1;   // bit 0: outside
        state.Strings["name"] = name;
        // The layouts the player (or the agent-playtest place-building command) chose: the keys a level building's state
        // carries (docs/formats/zones.md).
        if (p.InteriorLayout is { Length: > 0 } interior) state.Strings["interior layout name"] = interior;
        if (p.ExteriorLayout is { Length: > 0 } exterior) state.Strings["exterior layout name"] = exterior;
        return state;
    }
}