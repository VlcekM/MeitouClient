using System.Text;

namespace Meitou.Modding;

/// <summary>
/// A mod's sidecar file in a save (<c>meitou-mods/&lt;id&gt;.dat</c>, docs/formats/save.md "Mod data"): the magic <c>MTMD</c>, the format version
/// (int32 1), the entry count (int32), then per entry the system name (a 7-bit length-prefixed UTF-8 string, as <see cref="BinaryWriter"/> writes
/// it), the data length (int32) and the data. Little-endian.
/// </summary>
static class ModSaveFile
{
    /// <summary>
    /// The file's magic: "MTMD" read as a little-endian uint32.
    /// </summary>
    const uint Magic = 0x444D544D;   // "MTMD"
    /// <summary>
    /// The format version this game writes and reads.
    /// </summary>
    const int FormatVersion = 1;

    /// <summary>
    /// The file of a mod's systems' entries: name and data each, in order.
    /// </summary>
    public static byte[] Write(IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(entries.Count);
            foreach (var (name, data) in entries)
            {
                w.Write(name);
                w.Write(data.Length);
                w.Write(data);
            }
        }
        return stream.ToArray();
    }

    /// <summary>
    /// The entries by system name; throws <see cref="InvalidDataException"/> for a file that is not one.
    /// </summary>
    public static Dictionary<string, byte[]> Read(byte[] file)
    {
        using var r = new BinaryReader(new MemoryStream(file), Encoding.UTF8);
        try
        {
            if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a mod data file");
            int version = r.ReadInt32();
            if (version != FormatVersion) throw new InvalidDataException($"mod data format {version}, this game reads {FormatVersion}");
            return ReadEntries(r, r.ReadInt32());
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("mod data file cut short", e);
        }
    }

    /// <summary>
    /// Reads <paramref name="count"/> entries (name, length, data); throws for a length past the end of the file.
    /// </summary>
    static Dictionary<string, byte[]> ReadEntries(BinaryReader r, int count)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadString();
            int length = r.ReadInt32();
            if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException($"entry '{name}' has a bad length");
            entries[name] = r.ReadBytes(length);
        }
        return entries;
    }
}