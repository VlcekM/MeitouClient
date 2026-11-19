using System.Text.RegularExpressions;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>
/// Makes the zone file of a zone the save has none of from the level data (docs/formats/save.md "Zone files"), the way the original's zone save
/// writes a zone whose buildings stand as the level files place them: a <c>0-buildinglist</c> with every placed building of the merged level zone
/// (id, target, position and rotation as the level list has them) and an empty-target entry for each id a layer removes, and a GAMESTATE_BUILDING per
/// building made from its level state with the key set the original writes. What the original would write from its own runtime state and Meitou does
/// not model is left for the original to make again: <c>serialised interior</c> is not written, so the original generates each interior as for a
/// fresh zone (no furniture records, no inventories, no loose items are written); a building's level inventory state is named only when it holds
/// items (the level files provide it on load).
/// </summary>
public static partial class SaveZoneBuilder
{
    /// <summary>
    /// The <c>version</c> of every saved building state (<b>Observed</b>: 270 in all 36561 states of the owner's saves).
    /// </summary>
    public const int StateVersion = 270;

    /// <summary>
    /// The zone file, or null when the level zone places no building (the original deletes a zone file with no records). <paramref name="gameData"/>
    /// resolves the targets: an entry whose target is no BUILDING (undefined, or a record of the zone file) is left out, as the original does not
    /// make it.
    /// </summary>
    public static SaveZone? FromLevel(ZoneCoordinate zone, GameDatabase? levelZone, ZoneLevelInfo? info, GameDatabase gameData)
    {
        if (levelZone?.Find(WorldLevelData.BuildingListId) is not { } levelList) return null;

        var file = new SaveFile();
        var list = NewList();
        var entries = new LevelEntries(file, list, Math.Max(info?.NextId ?? 0, 0));
        entries.Slots.UnionWith(info?.Slots ?? []);
        entries.Add(levelZone, levelList, gameData);
        if (!list.Instances.Any(i => !i.IsRemoved)) return null;

        list.Id = entries.TakeId();
        file.Records.Add(list);
        file.Data.NextId = entries.LastId;
        file.SlotLists.Add([.. entries.Slots]);
        return new SaveZone(zone, file);
    }

    /// <summary>
    /// Adds to a zone file the save already has the level's buildings its building list does not name, each as <see cref="FromLevel"/> writes it,
    /// when it has a level state and stands inside no building. Such a file comes from an import (it keeps a zone's removals and the player's
    /// buildings only, docs/formats/save.md "Importing a game"), from a zone first written for the player's buildings or loose items alone, or
    /// from a save made before a mod placed the building. An entry the list names, a removal too, stays as it is. A building inside another is
    /// not added: a save takes those out of a ruin, a bought house and a home furnished anew, and their absence is all that records it. A level
    /// state whose handle slot a state of the file holds is written without its handle (the building is then named by its level handle, as one
    /// with no saved state is). Returns the instance ids of the buildings added.
    /// </summary>
    public static IReadOnlyList<string> Complete(SaveZone zone, GameDatabase? levelZone, GameDatabase gameData)
    {
        if (levelZone?.Find(WorldLevelData.BuildingListId) is not { } levelList) return [];

        var file = zone.File;
        var list = zone.BuildingList ?? NewList();
        int lastId = file.Records.Count == 0 ? Math.Max(file.Data.NextId, 0) : Math.Max(file.Data.NextId, file.Records.Max(r => r.Id));
        var entries = new LevelEntries(file, list, lastId);
        entries.KeepFile(zone.ContainerId);
        entries.Add(levelZone, levelList, gameData);
        if (entries.Added.Count == 0) return [];

        if (zone.BuildingList is null)
        {
            list.Id = entries.TakeId();
            file.Records.Add(list);
        }

        file.Data.NextId = entries.LastId;

        // A file without a slot list gets one from its states when the save is written (SaveGame.FillZoneSlotLists).
        if (file.SlotLists.Count > 0) file.SlotLists[0] = [.. file.SlotLists[0].Union(entries.Slots).Order()];
        return entries.Added;
    }

    /// <summary>
    /// An empty <c>0-buildinglist</c>.
    /// </summary>
    static FcsRecord NewList() =>
        new() { Type = (int)FcsRecordType.InstanceCollection, Name = "0", StringId = WorldLevelData.BuildingListId };

    /// <summary>
    /// The level's entries going into one zone file's building list, with the states they add to the file.
    /// </summary>
    sealed class LevelEntries(SaveFile file, FcsRecord list, int lastId)
    {
        /// <summary>
        /// The instance ids the list named before <see cref="Add"/>: their entries stay as they are.
        /// </summary>
        readonly HashSet<string> _listed = new(StringComparer.Ordinal);

        /// <summary>
        /// The string ids of the states the file holds: one is listed again, not written twice.
        /// </summary>
        readonly HashSet<string> _written = new(StringComparer.Ordinal);

        /// <summary>
        /// The handle slots (container, slot) the file's own building states held before <see cref="Add"/>.
        /// </summary>
        readonly HashSet<(int C, int I)> _taken = [];

        /// <summary>
        /// Whether the entries go into a file the save had (<see cref="KeepFile"/>): then only buildings with a level state that stand inside no
        /// building are added, and no removal.
        /// </summary>
        bool _completing;

        /// <summary>
        /// The last record id given in the file.
        /// </summary>
        public int LastId { get; private set; } = lastId;

        /// <summary>
        /// The handle slots of the states added, and any the caller put in first.
        /// </summary>
        public SortedSet<int> Slots { get; } = [];

        /// <summary>
        /// The instance ids of the buildings added.
        /// </summary>
        public List<string> Added { get; } = [];

        /// <summary>
        /// The next record id of the file.
        /// </summary>
        public int TakeId() => ++LastId;

        /// <summary>
        /// Notes what the file holds already: the list's entries, the records' ids and the slots of the building states in the zone's container.
        /// </summary>
        public void KeepFile(int container)
        {
            _completing = true;
            foreach (var i in list.Instances) _listed.Add(i.Id);

            foreach (var r in file.Records)
            {
                _written.Add(r.StringId);
                if (r.RecordType != FcsRecordType.GamestateBuilding) continue;
                if (Hand.Read(r, "handle") is { IsNull: false } h && h.C == container) _taken.Add((h.C, h.I));
            }
        }

        /// <summary>
        /// Adds every level entry the list does not name: a removal as an empty-target entry, a building with its state; into a file the save
        /// had, only the buildings <see cref="StandsAlone"/> passes.
        /// </summary>
        public void Add(GameDatabase levelZone, GameRecord levelList, GameDatabase gameData)
        {
            // The handles of the zone's level buildings: an `internalbuildings-<k>` handle is kept only when it names one of them (most name
            // generated furniture that is not written).
            var handles = new HashSet<(int I, int S)>();
            foreach (var s in levelZone.OfType(FcsRecordType.GamestateBuilding))
                if (s.Ints.ContainsKey("handleI")) handles.Add((s.GetInt("handleI"), s.GetInt("handleS")));

            foreach (var (id, instance) in levelList.Instances)
            {
                if (_listed.Contains(id)) continue;
                if (_completing && !StandsAlone(levelZone, instance)) continue;

                if (instance.IsCleared)
                {
                    list.Instances.Add(new FcsInstance { Id = id, Target = "" });
                    continue;
                }

                if (gameData.Find(instance.Target) is not { Type: FcsRecordType.Building } building) continue;

                list.Instances.Add(Entry(levelZone, id, instance, building, handles));
                Added.Add(id);
            }
        }

        /// <summary>
        /// Whether a level entry places a building that has a level state and stands inside no building.
        /// </summary>
        static bool StandsAlone(GameDatabase levelZone, GameInstance instance)
        {
            if (instance.IsCleared) return false;

            var state = instance.States
                .Select(levelZone.Find)
                .FirstOrDefault(s => s?.Type == FcsRecordType.GamestateBuilding);
            return state is not null && !state.GetBool("is inside building");
        }

        /// <summary>
        /// A building's list entry: its level inventory state when that holds items, and its building state, written into the file when it is not
        /// there yet.
        /// </summary>
        FcsInstance Entry(GameDatabase levelZone, string id, GameInstance instance, GameRecord building, HashSet<(int I, int S)> handles)
        {
            var entry = new FcsInstance { Id = id, Target = instance.Target, Position = instance.Position, Rotation = instance.Rotation };
            string? stateId = null;
            foreach (var sid in instance.States.Distinct(StringComparer.Ordinal))
            {
                var state = levelZone.Find(sid);
                if (state?.Type == FcsRecordType.InventoryState && state.Instances.Count > 0) entry.States.Add(sid);
                else if (state?.Type == FcsRecordType.GamestateBuilding && stateId is null) stateId = sid;
            }

            if (stateId is not null && _written.Add(stateId)) file.Records.Add(State(levelZone.Find(stateId)!, building, handles));
            if (stateId is not null) entry.States.Add(stateId);
            return entry;
        }

        /// <summary>
        /// A building's state from its level state, its handle's slot noted; without the handle when a state of the file holds that slot.
        /// </summary>
        FcsRecord State(GameRecord level, GameRecord building, HashSet<(int I, int S)> handles)
        {
            var record = StateFromLevel(level, building, handles, TakeId());
            if (Hand.Read(record, "handle") is not { IsNull: false } h) return record;

            if (_taken.Contains((h.C, h.I))) DropHandle(record);
            else Slots.Add(h.I);

            return record;
        }

        /// <summary>
        /// Takes the five parts of a state's own handle off it.
        /// </summary>
        static void DropHandle(FcsRecord record)
        {
            foreach (string part in (string[])["TYPE", "C", "CS", "I", "S"]) record.Ints.Remove("handle" + part);
        }
    }

    /// <summary>
    /// A building's saved state from its level state (<b>Observed</b>, census of 6743 base-building states in the owner's saves against the level
    /// files): every level key, without <c>is public night</c> (never written) and the old <c>is outside</c> (folded into <c>flags</c> bit 0,
    /// <c>foliage</c> bit 1, when the state has no <c>flags</c>), with the keys the original always writes added when missing (<c>pause</c>,
    /// <c>destroyed</c> false, <c>name</c> the record's name, <c>residents template</c> and <c>squad template</c> empty, <c>version</c>; a lock's and
    /// each door's <c>hardness</c>, a door's <c>pause&lt;i&gt;</c>), and only the <c>internalbuildings-&lt;k&gt;</c> handles that name a building of
    /// the zone, renumbered from 0.
    /// </summary>
    public static FcsRecord StateFromLevel(GameRecord level, GameRecord building, IReadOnlySet<(int I, int S)> zoneHandles, int id)
    {
        var r = new FcsRecord { Type = (int)FcsRecordType.GamestateBuilding, Id = id, Name = "0", StringId = level.StringId };
        foreach (var (k, v) in level.Bools)
            if (k is not ("is public night" or "is outside")) r.Bools[k] = v;
        foreach (var (k, v) in level.Floats) r.Floats[k] = v;
        int kept = 0;
        foreach (var (k, v) in level.Ints)
        {
            if (InternalBuilding().IsMatch(k)) continue;
            r.Ints[k] = v;
        }
        for (int k = 0; level.Ints.ContainsKey($"internalbuildings-{k}I"); k++)
        {
            string p = $"internalbuildings-{k}";
            if (!zoneHandles.Contains((level.GetInt(p + "I"), level.GetInt(p + "S")))) continue;
            var hand = new Hand(level.GetInt(p + "TYPE"), level.GetInt(p + "C"), level.GetInt(p + "CS"), level.GetInt(p + "I"), level.GetInt(p + "S"));
            hand.Write(r, $"internalbuildings-{kept++}");
        }
        foreach (var (k, v) in level.Vector3S) r.Vector3S[k] = v;
        foreach (var (k, v) in level.Vector4S) r.Vector4S[k] = v;
        foreach (var (k, v) in level.Strings) r.Strings[k] = v;

        if (!r.Ints.ContainsKey("flags"))
            r.Ints["flags"] = (level.GetBool("is outside") ? 1 : 0) | (level.GetBool("foliage") ? 2 : 0);
        r.Bools.TryAdd("pause", false);
        r.Bools.TryAdd("destroyed", false);
        r.Bools.TryAdd("foliage", false);
        r.Bools.TryAdd("is inside building", false);
        r.Ints.TryAdd("version", StateVersion);
        r.Strings.TryAdd("name", building.Name);
        r.Strings.TryAdd("residents template", "");
        r.Strings.TryAdd("squad template", "");
        // Locks: a building's own lock and each door i carry the building record's `hardness`, a door its `pause<i>` (Observed: 3093 of 3093 own
        // locks, 614 of 614 doors).
        float hardness = building.GetFloat("hardness");
        if (r.Ints.ContainsKey("lock lvl")) r.Floats.TryAdd("hardness", hardness);
        for (int i = 0; r.Ints.ContainsKey($"state{i}"); i++)
        {
            string suffix = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            r.Bools.TryAdd("pause" + suffix, false);
            r.Floats.TryAdd("hardness" + suffix, hardness);
        }
        return r;
    }

    /// <summary>
    /// Matches the five int keys of an <c>internalbuildings-&lt;k&gt;</c> handle.
    /// </summary>
    [GeneratedRegex(@"^internalbuildings-\d+(TYPE|C|CS|I|S)$")]
    private static partial Regex InternalBuilding();
}