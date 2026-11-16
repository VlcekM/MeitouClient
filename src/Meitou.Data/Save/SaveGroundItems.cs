using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Meitou.Data.Fcs;

namespace Meitou.Data.Save;

/// <summary>
/// A loose item of a zone file's <c>1-itemlist</c>: the list entry (position, rotation) and its ITEM record read as an item, a container with its
/// contents (docs/formats/save.md "Zone files").
/// </summary>
public sealed record SaveGroundItem(FcsInstance Instance, FcsRecord Record, SaveItem Item);

/// <summary>
/// Reads and writes the <c>1-itemlist</c> of a zone file (docs/formats/save.md "Zone files", loose items).
/// </summary>
public static partial class SaveGroundItems
{
    /// <summary>
    /// The string id of the list (<b>Observed</b>: every sample zone file with loose items).
    /// </summary>
    public const string ListId = "1-itemlist";

    /// <summary>
    /// The zone's loose items in list order, a container's contents read through the INVENTORY_STATE its ITEM's one instance names (as a carried
    /// container's are); entries whose target is missing or no ITEM are skipped.
    /// </summary>
    public static List<SaveGroundItem> Read(SaveZone zone)
    {
        var found = new List<SaveGroundItem>();
        if (zone.ItemList is not { } list) return found;
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in zone.File.Records) byId.TryAdd(r.StringId, r);
        foreach (var i in list.Instances)
        {
            if (!byId.TryGetValue(i.Target, out var r) || r.RecordType != FcsRecordType.Item) continue;

            List<SaveItem>? contents = null;
            if (r.Instances.Count > 0 && byId.TryGetValue(r.Instances[0].Target, out var inner) && inner.RecordType == FcsRecordType.InventoryState)
                contents = SaveInventory.Read(zone.File, inner);
            found.Add(new SaveGroundItem(i, r, SaveInventory.ItemOf(r, contents)));
        }
        return found;
    }

    /// <summary>
    /// Replaces the zone's list and the ITEM records it named (with the records of their contents) with <paramref name="items"/> (each a position, a
    /// rotation and the item; one with a <see cref="SaveItem.Source"/> keeps its record id and its unmodelled keys) and <paramref name="kept"/>
    /// (entries written back as read, contents and all). A new item's record gets id <c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c> with <c>n</c>
    /// above every such id of the file and of the read items, and the list's entries are numbered from 1. A container's contents get an
    /// INVENTORY_STATE named by the item's one instance and an INVENTORY_ITEM_STATE per item, with ids of the same form (<b>Observed</b>, a backpack
    /// the original saved on the ground); an empty container gets none. An empty list is not written (the original deletes it).
    /// </summary>
    public static void Write(SaveZone zone, IReadOnlyList<(Vector3 Position, Quaternion Rotation, SaveItem Item)> items,
        IReadOnlyList<SaveGroundItem> kept)
    {
        var file = zone.File;
        if (zone.ItemList is { } old) RemoveOld(file, old, kept);
        if (items.Count + kept.Count == 0) return;
        var list = new FcsRecord { Type = (int)FcsRecordType.InstanceCollection, Id = NextNumber(file), Name = "0", StringId = ListId };
        string suffix = string.Create(CultureInfo.InvariantCulture, $"-zone{zone.Coordinate.X}.{zone.Coordinate.Y}-INGAME");
        int n = LastNumber(file, items, kept, suffix);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        int entry = 0;
        foreach (var k in kept)
        {
            if (!taken.Add(k.Record.StringId)) continue;
            file.Records.Add(k.Record);
            list.Instances.Add(new FcsInstance
            {
                Id = Entry(++entry), Target = k.Record.StringId, Position = k.Instance.Position, Rotation = k.Instance.Rotation
            });
        }
        foreach (var (position, rotation, item) in items)
        {
            var record = GroundRecord(item);
            record.Id = NextNumber(file);
            string? id = item.Source?.StringId;
            if (id is null || !taken.Add(id))
            {
                id = (++n).ToString(CultureInfo.InvariantCulture) + suffix;
                taken.Add(id);
            }
            record.StringId = id;
            file.Records.Add(record);
            if (item.Contents is { Count: > 0 } contents)
                AddContents(file, record, contents, () => (++n).ToString(CultureInfo.InvariantCulture) + suffix);
            list.Instances.Add(new FcsInstance { Id = Entry(++entry), Target = id, Position = position, Rotation = rotation });
        }
        file.Records.Add(list);
        file.Data.NextId = Math.Max(file.Data.NextId, file.Records.Max(r => r.Id));
    }

    /// <summary>
    /// Takes the old list out of <paramref name="file"/> with the ITEM records it named and the records their contents reach, but for the
    /// <paramref name="kept"/> entries' contents (written back as read).
    /// </summary>
    static void RemoveOld(SaveFile file, FcsRecord old, IReadOnlyList<SaveGroundItem> kept)
    {
        var targets = old.Instances.Select(i => i.Target).ToHashSet(StringComparer.Ordinal);
        var keptIds = kept.Select(k => k.Record.StringId).ToHashSet(StringComparer.Ordinal);
        var keep = kept.SelectMany(k => SaveInventory.Reachable(file, k.Record)).ToHashSet(StringComparer.Ordinal);
        var contents = file.Records
            .Where(r => r.RecordType == FcsRecordType.Item && targets.Contains(r.StringId) && !keptIds.Contains(r.StringId))
            .SelectMany(r => SaveInventory.Reachable(file, r))
            .Where(id => !keep.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
        file.Records.RemoveAll(r => ReferenceEquals(r, old) || (r.RecordType == FcsRecordType.Item && targets.Contains(r.StringId))
            || contents.Contains(r.StringId));
    }

    /// <summary>
    /// Writes a loose container's <paramref name="contents"/>: an INVENTORY_STATE named by the item's one instance, filled as a carried container's
    /// (<see cref="SaveInventory.Replace"/>), its records named by <paramref name="nextId"/>.
    /// </summary>
    static void AddContents(SaveFile file, FcsRecord item, IReadOnlyList<SaveItem> contents, Func<string> nextId)
    {
        var state = new FcsRecord { Type = (int)FcsRecordType.InventoryState, Name = "0", Id = NextNumber(file), StringId = nextId() };
        file.Records.Add(state);
        file.Data.NextId = Math.Max(file.Data.NextId, state.Id);
        item.Instances.Add(SaveInventory.Link(1, state.StringId));
        SaveInventory.Replace(file, state, contents, _ => nextId());
    }

    /// <summary>
    /// The ITEM record of a loose item: the INVENTORY_ITEM_STATE key set (<see cref="SaveInventory.ItemRecord"/>, a read record's own keys kept) with
    /// <c>in inventory</c> false and an empty <c>section</c>; a new one has no <c>uniform</c> and no <c>persist</c> handle (<b>Observed</b>: the one
    /// sample limb).
    /// </summary>
    public static FcsRecord GroundRecord(SaveItem item)
    {
        var r = SaveInventory.ItemRecord(item);
        r.Type = (int)FcsRecordType.Item;
        if (item.Source is null) r.Strings.Remove("uniform");
        r.Strings["section"] = "";
        r.Bools["in inventory"] = false;
        return r;
    }

    /// <summary>
    /// The highest <c>n</c> of the <paramref name="suffix"/> ids already in use: the file's records and instances, the kept entries and the read
    /// items being written back (whose records were just removed, so a new item numbered from the file alone would take a read item's id and load as
    /// that item).
    /// </summary>
    static int LastNumber(SaveFile file, IReadOnlyList<(Vector3 Position, Quaternion Rotation, SaveItem Item)> items,
        IReadOnlyList<SaveGroundItem> kept, string suffix)
    {
        var ids = file.Records.Select(r => r.StringId)
            .Concat(file.Records.SelectMany(r => r.Instances).Select(i => i.Id))
            .Concat(kept.Select(k => k.Record.StringId))
            .Concat(items.Select(i => i.Item.Source?.StringId ?? ""));
        return ids.Select(id => Number(id, suffix)).DefaultIfEmpty(0).Max();
    }

    /// <summary>
    /// The instance id of list entry <paramref name="k"/>.
    /// </summary>
    static string Entry(int k) => k.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The number before <paramref name="suffix"/> in <paramref name="id"/>; 0 when the id does not end with it or the rest is not a number.
    /// </summary>
    static int Number(string id, string suffix)
    {
        if (!id.EndsWith(suffix, StringComparison.Ordinal)) return 0;

        return int.TryParse(id.AsSpan(0, id.Length - suffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    /// <summary>
    /// The next free record id of <paramref name="file"/>: one past the larger of its header's next id and its records' highest id.
    /// </summary>
    static int NextNumber(SaveFile file) =>
        file.Records.Count == 0 ? Math.Max(file.Data.NextId, 0) + 1 : Math.Max(file.Data.NextId, file.Records.Max(r => r.Id)) + 1;
}