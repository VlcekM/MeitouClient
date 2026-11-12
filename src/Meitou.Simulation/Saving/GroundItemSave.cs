using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// Loose items through the zone files' <c>1-itemlist</c> (docs/formats/save.md "Zone files"; docs/simulation.md "Ground items"): every listed item
/// whose record the game data has lies in <see cref="World.GroundItems"/> after a load, with its saved position and rotation; a save writes each
/// zone's list from them again. Entries whose item the data lacks (a missing mod) are written back as read.
/// </summary>
public static class GroundItemSave
{
    /// <summary>
    /// Puts the save's loose items on the ground, in zone and list order; returns how many the data could not make. <paramref name="factionOf"/>
    /// gives a faction id's index for <see cref="GroundItem.OwnedBy"/>, -1 for one that does not count (unknown, or <c>not real</c>).
    /// </summary>
    public static int Load(SaveGame save, World world, ItemFactory factory, Func<string, int>? factionOf = null)
    {
        int missing = 0;
        var handles = factionOf is null ? null : save.BuildHandles();
        foreach (var zone in save.Zones.Values)
        {
            foreach (var g in SaveGroundItems.Read(zone))
            {
                if (SavedKit.ItemOf(g.Item, factory) is { } item)
                {
                    int owner = handles is null || g.Item.Source is not { } r ? -1 : OwnerOf(r, handles, factionOf!);
                    int stolen = MarkStolen(item, g.Item, factionOf);
                    if (owner < 0) owner = stolen;
                    world.GroundItems.Load(item, g.Instance.Position, g.Instance.Rotation, owner);
                }
                else
                {
                    missing++;
                }
            }
        }

        return missing;
    }

    /// <summary>
    /// Marks a loose item stolen from the faction Meitou's <see cref="SaveItem.StolenFrom"/> names (a dropped stolen item) and returns that
    /// faction, its owner when its <c>ownedby</c> names none; -1 when it names none.
    /// </summary>
    static int MarkStolen(ItemInstance item, SaveItem saved, Func<string, int>? factionOf)
    {
        int faction = saved.StolenFrom is { Length: > 0 } id && factionOf is not null ? factionOf(id) : -1;
        if (faction >= 0) item.StolenFrom = faction;

        return faction;
    }

    /// <summary>
    /// The faction an item's <c>ownedby</c> handle names (docs/game/ui-input.md "Ground item labels"): the owning character's platoon's; a set handle
    /// whose character the save no longer holds falls back to the <c>insideBuilding</c> building's <c>owner faction ID</c> (the label's fallback,
    /// used for the pick-up too: engine choice). -1: none.
    /// </summary>
    static int OwnerOf(FcsRecord item, SaveHandles handles, Func<string, int> factionOf)
    {
        var owner = Hand.Read(item, "ownedby");
        if (owner.IsNull) return -1;

        if (handles.Character(owner) is { } c) return factionOf(c.Platoon.FactionId);

        return handles.Building(Hand.Read(item, "insideBuilding"))?.Strings.GetValueOrDefault("owner faction ID") is { Length: > 0 } f
            ? factionOf(f)
            : -1;
    }

    /// <summary>
    /// Writes each zone's list from the items lying in it (by position). A zone whose list would name the same records it does and gains no item is
    /// left as it is (so an unchanged save keeps its zone files); a zone with items and no file gets one; a file left with no record goes (as the
    /// original deletes it). Returns the new items a camp laid (<see cref="GroundItem.Camp"/>), for <see cref="WritePersist"/> once the nests have
    /// their handles.
    /// </summary>
    public static List<(SaveItem Item, int Camp)> Write(SaveGame game, World world, ItemFactory factory)
    {
        var camps = new List<(SaveItem Item, int Camp)>();
        var byZone = GroupByZone(world);
        foreach (var z in game.Zones.Keys.Where(z => game.Zones[z].ItemList is not null).Concat(byZone.Keys).Distinct().ToList())
        {
            var items = byZone.GetValueOrDefault(z) ?? [];
            game.Zones.TryGetValue(z, out var zone);
            var old = zone is null ? [] : SaveGroundItems.Read(zone);
            var kept = old.Where(o => factory.Create(o.Item.BaseId) is null).ToList();
            var named = old.Select(o => o.Record.StringId).ToHashSet(StringComparer.Ordinal);
            bool unchanged = zone is not null && items.All(i => i.Item.Saved is { } s && named.Contains(s.StringId))
                && items.Count + kept.Count == old.Count;
            if (unchanged) continue;

            if (zone is null) game.Zones[z] = zone = new SaveZone(z, new SaveFile());
            var saved = items.Select(i => (i.Position, i.Rotation, Item: SavedKit.FromItem(i.Item))).ToList();
            for (int k = 0; k < items.Count; k++)
                if (items[k].Camp is { } camp && items[k].Item.Saved is null) camps.Add((saved[k].Item, camp));

            SaveGroundItems.Write(zone, saved, kept);
            if (zone.File.Records.Count == 0) game.Zones.Remove(z);
        }

        return camps;
    }

    /// <summary>
    /// The ground items of the world by the zone they lie in.
    /// </summary>
    static Dictionary<ZoneCoordinate, List<GroundItem>> GroupByZone(World world)
    {
        var byZone = new Dictionary<ZoneCoordinate, List<GroundItem>>();
        foreach (var g in world.GroundItems.All)
        {
            var z = WorldLayout.ZoneOf(g.Position.X, g.Position.Z);
            if (!byZone.TryGetValue(z, out var list)) byZone[z] = list = [];
            list.Add(g);
        }

        return byZone;
    }

    /// <summary>
    /// Puts the original's <c>persist</c> handle on a camp's new items (docs/formats/save.md "Zone files"): the nest's (type 85,
    /// <paramref name="nests"/> by wild site) or the town's (type 13, a <c>spawn in town centre</c> town's state). A camp the save holds no handle of
    /// leaves its items without one. A read item keeps its own.
    /// </summary>
    public static void WritePersist(SaveGame game, PopulationData data, List<(SaveItem Item, int Camp)> camps, IReadOnlyDictionary<int, Hand> nests)
    {
        if (camps.Count == 0) return;

        var towns = new Dictionary<string, Hand>(StringComparer.Ordinal);
        foreach (var t in game.Towns)
            if (!t.IsNest && t.Instance.Length > 0 && !t.Handle.IsNull) towns.TryAdd(t.Instance, t.Handle);

        foreach (var (item, camp) in camps)
        {
            int townSite = -1 - camp;
            var hand = camp >= 0
                ? nests.GetValueOrDefault(camp, Hand.Null)
                : townSite < data.Sites.Count
                    ? towns.GetValueOrDefault(data.Sites[townSite].InstanceId, Hand.Null)
                    : Hand.Null;
            if (!hand.IsNull && item.Written is { } record) hand.Write(record, "persist");
        }
    }
}
