using Meitou.Data.Fcs;

namespace Meitou.Data.Save;

/// <summary>
/// The limbs of a MEDICAL_STATE (docs/formats/save.md "Limbs"): the int <c>limbs</c> holds the state of limb slot k (0 left arm, 1 right arm, 2
/// left leg, 3 right leg) in bits 2k..2k+1 (0 original, 1 stump, 2 replaced, 3 crushed), and the reference list <c>limbs</c> names, for each
/// replaced slot (reference value 0), the INVENTORY_ITEM_STATE of the fitted prosthetic in the same file. The prosthetics are not in the
/// character's INVENTORY_STATE.
/// </summary>
public static class SaveLimbs
{
    /// <summary>
    /// The key of both the packed int and the reference list.
    /// </summary>
    public const string Key = "limbs";

    /// <summary>
    /// The number of limb slots.
    /// </summary>
    public const int Slots = 4;

    /// <summary>
    /// The state of <paramref name="slot"/> in a packed <c>limbs</c> value.
    /// </summary>
    public static int StateOf(int bits, int slot) => (bits >> (2 * slot)) & 3;

    /// <summary>
    /// The prosthetic item of each slot (null where the list names none or its record is missing).
    /// </summary>
    public static SaveItem?[] Read(SaveFile file, FcsRecord medical)
    {
        var items = new SaveItem?[Slots];
        if (!medical.References.TryGetValue(Key, out var refs) || refs.Count == 0) return items;
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in file.Records) byId.TryAdd(r.StringId, r);
        foreach (var x in refs)
            if ((uint)x.Value0 < Slots && byId.TryGetValue(x.TargetStringId, out var r) && r.RecordType == FcsRecordType.InventoryItemState)
                items[x.Value0] = SaveInventory.ItemOf(r);
        return items;
    }

    /// <summary>
    /// Writes <paramref name="bits"/> and, when a slot is replaced, the reference list to <paramref name="items"/> (per slot; null for a slot without
    /// one). A prosthetic read from this file keeps its record (and id); the records of prosthetics no longer fitted leave the file. Without a
    /// replaced slot the list is removed, as the original writes none.
    /// </summary>
    public static void Write(SaveFile file, FcsRecord medical, int bits, IReadOnlyList<SaveItem?> items)
    {
        medical.Ints[Key] = bits;
        var old = medical.References.TryGetValue(Key, out var refs) ? refs.Select(x => x.TargetStringId).ToHashSet(StringComparer.Ordinal) : [];
        var kept = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        var list = new List<FcsReference>();
        int next = SaveBuilder.NextNumber(file);
        for (int slot = 0; slot < Slots && slot < items.Count; slot++)
        {
            if (StateOf(bits, slot) != 2 || items[slot] is not { } item) continue;
            var r = SaveInventory.ItemRecord(item);
            foreach (var k in new[] { "dam", "wear", "stun" }) r.Floats.TryAdd(k, 0);
            if (item.Source is { } was && old.Contains(was.StringId) && !kept.ContainsKey(was.StringId))
            {
                (r.Id, r.StringId, r.Name, r.Flags) = (was.Id, was.StringId, was.Name, was.Flags);
                kept[was.StringId] = r;
            }
            else
            {
                r.Id = next;
                r.StringId = $"{next}--INGAME";
                file.Data.NextId = Math.Max(file.Data.NextId, next++);
                file.Records.Add(r);
            }
            list.Add(new FcsReference(r.StringId, slot, 0, 0));
        }
        for (int i = file.Records.Count - 1; i >= 0; i--)
        {
            string id = file.Records[i].StringId;
            if (!old.Contains(id)) continue;
            if (kept.TryGetValue(id, out var replacement)) file.Records[i] = replacement;
            else file.Records.RemoveAt(i);
        }
        if (list.Count > 0) medical.References[Key] = list;
        else medical.References.Remove(Key);
    }
}