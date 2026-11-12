using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The zone files of a save (docs/formats/save.md "Zone files"). The original writes a file for every zone it has loaded (on unload and at save time)
/// that holds any record, and a zone file marks the zone as visited: the original then makes no town residents for it at load (they come from the
/// save's platoons). A Meitou save does the same for the zones its population has had active (<see cref="World.VisitedZones"/>): a zone the loaded
/// save has a file of keeps it (the building locks and doors and the player's buildings are written into it by <see cref="SaveCapture"/>), one it has
/// none of gets one made from the level data (<see cref="SaveZoneBuilder.FromLevel"/>), whose buildings name their town by the save's town handle.
/// </summary>
public static class ZoneSave
{
    /// <summary>
    /// Adds a zone file for each visited zone the save has none of, in grid order; returns those zones.
    /// </summary>
    public static List<ZoneCoordinate> WriteVisited(SaveGame game, World world, PopulationData data, WorldLevelData? levels)
    {
        var made = new List<ZoneCoordinate>();
        if (levels is null) return made;
        foreach (var z in world.VisitedZones.OrderBy(z => z.X).ThenBy(z => z.Y))
        {
            if (game.Zones.ContainsKey(z)) continue;
            levels.Zones.TryGetValue(z, out var db);
            levels.ZoneInfo.TryGetValue(z, out var info);
            if (SaveZoneBuilder.FromLevel(z, db, info, data.Db) is not { } zone) continue;
            game.Zones[z] = zone;
            made.Add(z);
        }

        if (made.Count > 0) BindTowns(game, data, made);
        return made;
    }

    /// <summary>
    /// The <c>town</c> handle of each building of a placed town in the new zone files: the <c>hand</c> of the town's state in the save
    /// (the original writes the town the building belongs to, <b>Observed</b>: 3726 of 6743 saved base-building states name another
    /// town container than their level state). A building inside another keeps its level value.
    /// </summary>
    static void BindTowns(SaveGame game, PopulationData data, List<ZoneCoordinate> made)
    {
        var hands = new Dictionary<string, Hand>(StringComparer.Ordinal);
        foreach (var t in game.Towns)
            if (t.Instance.Length > 0 && !t.Handle.IsNull) hands.TryAdd(t.Instance, t.Handle);
        var fresh = made.ToHashSet();
        var states = new Dictionary<ZoneCoordinate, Dictionary<string, FcsRecord>>();
        foreach (var site in data.Sites)
        {
            if (!hands.TryGetValue(site.InstanceId, out var hand)) continue;
            foreach (var b in site.Buildings)
            {
                if (!fresh.Contains(b.Zone)) continue;
                if (!states.TryGetValue(b.Zone, out var map)) states[b.Zone] = map = StatesByInstance(game.Zones[b.Zone]);
                if (!map.TryGetValue(b.InstanceId, out var state) || state.Bools.GetValueOrDefault("is inside building")) continue;
                hand.Write(state, "town");
            }
        }
    }

    /// <summary>
    /// The zone's building states by the instance id of the building that lists them.
    /// </summary>
    static Dictionary<string, FcsRecord> StatesByInstance(SaveZone zone)
    {
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in zone.BuildingStates) byId[r.StringId] = r;
        var map = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        if (zone.BuildingList is { } list)
        {
            foreach (var i in list.Instances)
            {
                foreach (var s in i.States)
                {
                    if (byId.TryGetValue(s, out var state))
                    {
                        map[i.Id] = state;
                        break;
                    }
                }
            }
        }

        return map;
    }
}