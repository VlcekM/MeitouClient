using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Save;

/// <summary>
/// One item of a saved inventory: an INVENTORY_ITEM_STATE (docs/formats/save.md), with what the engine models. <see cref="Source"/> is the record it
/// was read from, so the keys the engine does not model (the <c>ownedby</c> and <c>insideBuilding</c> handles) survive a round trip.
/// </summary>
public sealed class SaveItem
{
    /// <summary>
    /// The <c>base data sid</c> key: the item's record id.
    /// </summary>
    public required string BaseId { get; init; }

    /// <summary>
    /// The <c>material sid</c> key (a blueprint's: its RESEARCH id; empty: none).
    /// </summary>
    public string MaterialId { get; init; } = "";

    /// <summary>
    /// The <c>company sid</c> key (a blueprint's: its RESEARCH id; empty: none).
    /// </summary>
    public string CompanyId { get; init; } = "";

    /// <summary>
    /// The <c>crafter</c> key: the name of the smith who made the item at a crafting bench (empty: none).
    /// </summary>
    public string Crafter { get; init; } = "";

    /// <summary>
    /// The <c>uniform</c> key: the id of the faction whose uniform the item is (empty: none).
    /// </summary>
    public string Uniform { get; init; } = "";

    /// <summary>
    /// The <c>color sid</c> key: the COLOR_DATA the item is dyed with (empty: none; docs/characters.md "Clothing colours").
    /// </summary>
    public string ColourId { get; init; } = "";

    /// <summary>
    /// The <c>section</c> key: the inventory section the item lies in (default <c>main</c>).
    /// </summary>
    public string Section { get; init; } = "main";

    /// <summary>
    /// The <c>inventory x</c> key: the item's column in its section.
    /// </summary>
    public int X { get; init; }

    /// <summary>
    /// The <c>inventory y</c> key: the item's row in its section.
    /// </summary>
    public int Y { get; init; }

    /// <summary>
    /// The <c>quantity</c> key (default 1).
    /// </summary>
    public int Quantity { get; init; } = 1;

    /// <summary>
    /// The <c>level</c> key: the item level.
    /// </summary>
    public int Level { get; init; }

    /// <summary>
    /// The <c>item function</c> key (11 a blueprint, 17 a severed limb).
    /// </summary>
    public int ItemFunction { get; init; }

    /// <summary>
    /// The <c>quality</c> key.
    /// </summary>
    public float Quality { get; init; }

    /// <summary>
    /// The <c>charges</c> key (default 1).
    /// </summary>
    public float Charges { get; init; } = 1;

    /// <summary>
    /// A removed prosthetic's condition: the <c>dam</c> float, with <see cref="Wear"/> and <see cref="Stun"/> (docs/game/character-stats.md "Limb
    /// replacements and robots"); 0 for other items.
    /// </summary>
    public float Damage { get; init; }

    /// <summary>
    /// A removed prosthetic's <c>wear</c> float (see <see cref="Damage"/>); 0 for other items.
    /// </summary>
    public float Wear { get; init; }

    /// <summary>
    /// A removed prosthetic's <c>stun</c> float (see <see cref="Damage"/>); 0 for other items.
    /// </summary>
    public float Stun { get; init; }

    /// <summary>
    /// The <c>death</c> bool: a death item still on its owner (docs/game/animals.md "Death items").
    /// </summary>
    public bool Death { get; init; }

    /// <summary>
    /// The <c>unique</c> bool, written only when set (<c>FUN_1407609e0</c>): a town's artifact (docs/game/economy.md "Artifacts").
    /// </summary>
    public bool Unique { get; init; }

    /// <summary>
    /// Shackles' lock (LockedArmour, docs/game/locks.md "Save keys"): <c>lock lvl</c>, <c>locked</c>, <c>broken</c>, <c>hardness</c>; null for
    /// other items.
    /// </summary>
    public SaveItemLock? Lock { get; init; }

    /// <summary>
    /// A container's contents (the INVENTORY_STATE its one instance names); null for other items.
    /// </summary>
    public List<SaveItem>? Contents { get; init; }

    /// <summary>
    /// The INVENTORY_ITEM_STATE the item was read from; null for a new item.
    /// </summary>
    public FcsRecord? Source { get; init; }

    /// <summary>
    /// The <c>ownedby</c> handle (the character the item was stolen from) written over the source's; null keeps the source's (the null handle for an
    /// item without one).
    /// </summary>
    public Hand? OwnedBy { get; init; }

    /// <summary>
    /// Meitou's string <see cref="StolenFromKey"/>: the FACTION id the item was stolen from (null: not stolen). It keeps a mark that names no
    /// character (a container's or the ground's), and one whose robbed character is not saved.
    /// </summary>
    public string? StolenFrom { get; init; }

    /// <summary>
    /// The Meitou key of <see cref="StolenFrom"/> (the original ignores it: keys are read by name).
    /// </summary>
    public const string StolenFromKey = "meitou stolen from";

    /// <summary>
    /// The record the item was last written as, so a handle known only once every character has one can be put on it.
    /// </summary>
    public FcsRecord? Written { get; private set; }

    /// <summary>
    /// Records <paramref name="r"/> as the record the item was last written as.
    /// </summary>
    internal void WrittenAs(FcsRecord r) => Written = r;
}

/// <summary>
/// An item's lock as saved; <see cref="Locked"/> is written true only while the lock is effectively locked (as the original's writer,
/// <c>FUN_14029ffd0</c>).
/// </summary>
public readonly record struct SaveItemLock(int Level, bool Locked, bool Broken, float Hardness);

/// <summary>
/// Reads and writes the items of an INVENTORY_STATE inside one save file (a platoon's).
/// </summary>
public static class SaveInventory
{
    /// <summary>
    /// The items an INVENTORY_STATE holds: each instance names an INVENTORY_ITEM_STATE of the same file; a container item's single instance names the
    /// INVENTORY_STATE of its contents (<b>Verified</b> in the samples, save.md). Instances whose target is missing are skipped.
    /// </summary>
    public static List<SaveItem> Read(SaveFile file, FcsRecord inventory)
    {
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in file.Records) byId.TryAdd(r.StringId, r);
        return Read(byId, inventory, 0);
    }

    /// <summary>
    /// The items of <paramref name="inventory"/>, a container's contents read up to four levels deep.
    /// </summary>
    static List<SaveItem> Read(Dictionary<string, FcsRecord> byId, FcsRecord inventory, int depth)
    {
        var items = new List<SaveItem>();
        if (depth > 4) return items;
        foreach (var instance in inventory.Instances)
        {
            if (!byId.TryGetValue(instance.Target, out var r) || r.RecordType != FcsRecordType.InventoryItemState) continue;
            List<SaveItem>? contents = null;
            if (r.Instances.Count > 0 && byId.TryGetValue(r.Instances[0].Target, out var inner) && inner.RecordType == FcsRecordType.InventoryState)
                contents = Read(byId, inner, depth + 1);
            items.Add(ItemOf(r, contents));
        }
        return items;
    }

    /// <summary>
    /// The modelled keys of an INVENTORY_ITEM_STATE, with <paramref name="contents"/> for a container.
    /// </summary>
    internal static SaveItem ItemOf(FcsRecord r, List<SaveItem>? contents = null) => new()
    {
        BaseId = r.Strings.GetValueOrDefault("base data sid", ""),
        MaterialId = r.Strings.GetValueOrDefault("material sid", ""),
        CompanyId = r.Strings.GetValueOrDefault("company sid", ""),
        Crafter = r.Strings.GetValueOrDefault("crafter", ""),
        Uniform = r.Strings.GetValueOrDefault("uniform", ""),
        ColourId = r.Strings.GetValueOrDefault("color sid", ""),
        Section = r.Strings.GetValueOrDefault("section", "main"),
        X = r.Ints.GetValueOrDefault("inventory x"),
        Y = r.Ints.GetValueOrDefault("inventory y"),
        Quantity = r.Ints.GetValueOrDefault("quantity", 1),
        Level = r.Ints.GetValueOrDefault("level"),
        ItemFunction = r.Ints.GetValueOrDefault("item function"),
        Quality = r.Floats.GetValueOrDefault("quality"),
        Charges = r.Floats.GetValueOrDefault("charges", 1),
        Damage = r.Floats.GetValueOrDefault("dam"),
        Wear = r.Floats.GetValueOrDefault("wear"),
        Stun = r.Floats.GetValueOrDefault("stun"),
        Death = r.Bools.GetValueOrDefault("death"),
        Unique = r.Bools.GetValueOrDefault("unique"),
        Lock = r.Ints.TryGetValue("lock lvl", out int lockLevel)
            ? new SaveItemLock(lockLevel, r.Bools.GetValueOrDefault("locked"), r.Bools.GetValueOrDefault("broken"),
                r.Floats.GetValueOrDefault("hardness"))
            : null,
        Contents = contents,
        Source = r,
        StolenFrom = r.Strings.GetValueOrDefault(SaveItem.StolenFromKey),
    };

    /// <summary>
    /// The records an INVENTORY_STATE reaches: its items and, through containers, their contents' states and items (not the state itself).
    /// </summary>
    internal static HashSet<string> Reachable(SaveFile file, FcsRecord inventory)
    {
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in file.Records) byId.TryAdd(r.StringId, r);
        var found = new HashSet<string>(StringComparer.Ordinal);
        void Walk(FcsRecord r)
        {
            foreach (var i in r.Instances)
                if (found.Add(i.Target) && byId.TryGetValue(i.Target, out var next)) Walk(next);
        }
        Walk(inventory);
        return found;
    }

    /// <summary>
    /// The INVENTORY_ITEM_STATE of an item: a copy of its <see cref="SaveItem.Source"/> when it has one (the keys the engine does not model kept),
    /// else the key set every real item record has (null handles, empty colour and uniform), with the modelled keys written over.
    /// </summary>
    internal static FcsRecord ItemRecord(SaveItem item)
    {
        var r = new FcsRecord { Type = (int)FcsRecordType.InventoryItemState, Name = "0" };
        if (item.Source is { } s)
        {
            foreach (var (k, v) in s.Strings) r.Strings[k] = v;
            foreach (var (k, v) in s.Ints) r.Ints[k] = v;
            foreach (var (k, v) in s.Floats) r.Floats[k] = v;
            foreach (var (k, v) in s.Bools) r.Bools[k] = v;
            foreach (var (k, v) in s.Vector3S) r.Vector3S[k] = v;
        }
        else
        {
            r.Strings["uniform"] = "";
            r.Strings["color sid"] = "";
            Hand.Null.Write(r, "insideBuilding");
            Hand.Null.Write(r, "ownedby");
        }
        r.Bools["death"] = item.Death;
        if (item.Unique) r.Bools["unique"] = true;
        else r.Bools.Remove("unique");
        r.Strings["material sid"] = item.MaterialId;
        r.Strings["company sid"] = item.CompanyId;
        r.Strings["uniform"] = item.Uniform;
        r.Strings["color sid"] = item.ColourId;
        if (item.Crafter.Length > 0) r.Strings["crafter"] = item.Crafter;   // written only when not empty, as the original
        r.Strings["section"] = item.Section;
        r.Strings["base data sid"] = item.BaseId;
        r.Ints["item function"] = item.ItemFunction;
        r.Ints["inventory x"] = item.X;
        r.Ints["inventory y"] = item.Y;
        r.Ints["level"] = item.Level;
        r.Ints["quantity"] = item.Quantity;
        r.Floats["charges"] = item.Charges;
        r.Floats["quality"] = item.Quality;
        // A prosthetic's condition: written where the record had it or it is not zero.
        foreach (var (k, v) in new[] { ("dam", item.Damage), ("wear", item.Wear), ("stun", item.Stun) })
            if (v != 0 || r.Floats.ContainsKey(k)) r.Floats[k] = v;
        if (item.Lock is { } l)
        {
            r.Ints["lock lvl"] = l.Level;
            r.Bools["locked"] = l.Locked;
            r.Bools["broken"] = l.Broken;
            r.Floats["hardness"] = l.Hardness;
        }
        item.OwnedBy?.Write(r, "ownedby");
        if (item.StolenFrom is { } stolenFrom) r.Strings[SaveItem.StolenFromKey] = stolenFrom;
        else r.Strings.Remove(SaveItem.StolenFromKey);
        r.Bools["in inventory"] = true;
        item.WrittenAs(r);
        return r;
    }

    /// <summary>
    /// An instance linking to <paramref name="target"/>, numbered <paramref name="index"/>, at the origin.
    /// </summary>
    internal static FcsInstance Link(int index, string target) => new()
    {
        Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture), Target = target, Position = Vector3.Zero, Rotation = Quaternion.Identity
    };

    /// <summary>
    /// Replaces what an INVENTORY_STATE of <paramref name="file"/> holds with <paramref name="items"/>: the old item records (and the contents of
    /// containers among them) leave the file, and each item gets an INVENTORY_ITEM_STATE linked by an instance numbered from 1; a container's
    /// contents get an INVENTORY_STATE of their own, linked by the item's one instance, as the game writes them. An item read from this inventory
    /// keeps its record's id and place in the file, so an inventory that did not change is written back the same. New records take the next numbers
    /// of the file, named by <paramref name="idOf"/>.
    /// </summary>
    public static void Replace(SaveFile file, FcsRecord inventory, IReadOnlyList<SaveItem> items, Func<int, string> idOf)
    {
        var old = Reachable(file, inventory);
        var oldRecords = file.Records.Where(r => old.Contains(r.StringId)).ToDictionary(r => r.StringId, StringComparer.Ordinal);
        var reused = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        var added = new List<FcsRecord>();
        int next = SaveBuilder.NextNumber(file);

        FcsRecord Take(FcsRecord fresh, string? oldId)
        {
            if (oldId is not null && oldRecords.TryGetValue(oldId, out var was) && !reused.ContainsKey(oldId))
            {
                (fresh.Id, fresh.StringId, fresh.Name, fresh.Flags) = (was.Id, was.StringId, was.Name, was.Flags);
                reused[oldId] = fresh;
            }
            else
            {
                fresh.Id = next;
                fresh.StringId = idOf(next);
                file.Data.NextId = Math.Max(file.Data.NextId, next++);
                added.Add(fresh);
            }
            return fresh;
        }

        void Fill(FcsRecord state, IReadOnlyList<SaveItem> list, int depth)
        {
            state.Instances.Clear();
            int index = 0;
            foreach (var item in list)
            {
                var r = Take(ItemRecord(item), item.Source?.StringId);
                state.Instances.Add(Link(++index, r.StringId));
                if (item.Contents is { } contents && depth < 4)
                {
                    string? oldInner = item.Source is { Instances.Count: > 0 } s ? s.Instances[0].Target : null;
                    var inner = Take(new FcsRecord { Type = (int)FcsRecordType.InventoryState, Name = "0" }, oldInner);
                    r.Instances.Add(Link(1, inner.StringId));
                    Fill(inner, contents, depth + 1);
                }
            }
        }

        Fill(inventory, items, 0);
        // Reused records take the place of the ones they replace; the rest of the old ones leave; new ones go at the end.
        for (int i = file.Records.Count - 1; i >= 0; i--)
        {
            string id = file.Records[i].StringId;
            if (!old.Contains(id)) continue;
            if (reused.TryGetValue(id, out var replacement)) file.Records[i] = replacement;
            else file.Records.RemoveAt(i);
        }
        file.Records.AddRange(added);
    }
}