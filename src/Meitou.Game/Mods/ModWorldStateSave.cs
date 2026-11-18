using System.Text;

namespace Meitou.Modding;

/// <summary>
/// A mod's world state values in its save data (docs/formats/save.md "Mod data"): the entry <see cref="EntryName"/> of the mod's sidecar, holding the
/// format (int32 1), the count (int32), then per value the WORLD_EVENT_STATE's string id (as <see cref="BinaryWriter"/> writes a string) and the value
/// (a byte, 0 or 1), in string id order.
/// </summary>
static class ModWorldStateSave
{
    /// <summary>
    /// The entry's name in the sidecar; no system of a mod may have it.
    /// </summary>
    public const string EntryName = "meitou world states";
    /// <summary>
    /// The entry's format this game writes and reads.
    /// </summary>
    const int FormatVersion = 1;

    /// <summary>
    /// The entry holding <paramref name="values"/>.
    /// </summary>
    public static byte[] Write(IEnumerable<KeyValuePair<string, bool>> values)
    {
        var list = values.ToList();
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(FormatVersion);
            w.Write(list.Count);
            foreach (var (record, value) in list)
            {
                w.Write(record);
                w.Write(value);
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Sets the entry's values in <paramref name="world"/> through the mod's claimed handles <paramref name="owned"/> (by record id; a state the mod
    /// no longer claims is dropped); throws <see cref="InvalidDataException"/> for an entry that is not one.
    /// </summary>
    public static void Read(byte[] entry, Simulation.World world, IReadOnlyDictionary<string, Simulation.Factions.ModWorldState> owned)
    {
        using var r = new BinaryReader(new MemoryStream(entry, writable: false), Encoding.UTF8);
        try
        {
            int version = r.ReadInt32();
            if (version != FormatVersion) throw new InvalidDataException($"world states format {version}, this game reads {FormatVersion}");
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string record = r.ReadString();
                bool value = r.ReadBoolean();
                if (owned.TryGetValue(record, out var state)) state.Set(world, value);
            }
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("world states entry cut short", e);
        }
    }

    /// <summary>
    /// The values <paramref name="world"/> holds for the records of <paramref name="owned"/>, in string id order.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, bool>> ValuesOf(Simulation.World world,
        IReadOnlyDictionary<string, Simulation.Factions.ModWorldState> owned) =>
        world.ModStatesOrNull?.All.Where(v => owned.ContainsKey(v.Key)) ?? [];
}
