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
        int nextId = Math.Max(info?.NextId ?? 0, 0);
        var list = new FcsRecord { Type = (int)FcsRecordType.InstanceCollection, Name = "0", StringId = WorldLevelData.BuildingListId };

        // The handles of the zone's level buildings: an `internalbuildings-<k>` handle is kept only when it names one of them (most name generated
        // furniture that is not written).
        var handles = new HashSet<(int I, int S)>();
        foreach (var s in levelZone.OfType(FcsRecordType.GamestateBuilding))
            if (s.Ints.ContainsKey("handleI")) handles.Add((s.GetInt("handleI"), s.GetInt("handleS")));

        var slots = new SortedSet<int>(info?.Slots ?? []);
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, instance) in levelList.Instances)
        {
            if (instance.IsCleared)
            {
                list.Instances.Add(new FcsInstance { Id = id, Target = "" });
                continue;
            }
            if (gameData.Find(instance.Target) is not { Type: FcsRecordType.Building } building) continue;
            var entry = new FcsInstance { Id = id, Target = instance.Target, Position = instance.Position, Rotation = instance.Rotation };
            string? stateId = null;
            foreach (var sid in instance.States.Distinct(StringComparer.Ordinal))
            {
                var state = levelZone.Find(sid);
                if (state?.Type == FcsRecordType.InventoryState && state.Instances.Count > 0) entry.States.Add(sid);
                else if (state?.Type == FcsRecordType.GamestateBuilding && stateId is null) stateId = sid;
            }
            if (stateId is not null && written.Add(stateId))
            {
                var record = StateFromLevel(levelZone.Find(stateId)!, building, handles, ++nextId);
                file.Records.Add(record);
                if (Hand.Read(record, "handle") is { IsNull: false } h) slots.Add(h.I);
            }
            if (stateId is not null) entry.States.Add(stateId);
            list.Instances.Add(entry);
        }
        if (!list.Instances.Any(i => !i.IsRemoved)) return null;
        list.Id = ++nextId;
        file.Records.Add(list);
        file.Data.NextId = nextId;
        file.SlotLists.Add([.. slots]);
        return new SaveZone(zone, file);
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