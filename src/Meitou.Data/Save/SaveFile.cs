using System.Buffers.Binary;
using Meitou.Data.Fcs;

namespace Meitou.Data.Save;

/// <summary>
/// One file of a save folder (<c>quick.save</c>, <c>*.platoon</c>, <c>*.zone</c>): an FCS file of type 15 followed by the slot lists
/// (docs/formats/save.md "Object handles and slot lists"). A list is an int32 count and that many ascending int32; <c>quick.save</c> has four,
/// a zone file one, a platoon file none. Strings are read losslessly (<see cref="Fcs.LosslessUtf8"/>). The game writes every file as type 15, except that a V17 header-carrying platoon file was seen in a quicksave folder (docs/formats/save.md), so any FCS file type is accepted. Reading and writing again gives the same bytes.
/// </summary>
public sealed class SaveFile
{
    public FcsFile Data { get; init; } = new() { FileType = FcsFileType.V15 };

    /// <summary>The slot lists after the records, in file order.</summary>
    public List<int[]> SlotLists { get; } = [];

    public List<FcsRecord> Records => Data.Records;

    public static SaveFile ReadFile(string path) => Read(File.ReadAllBytes(path));

    public static SaveFile Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var data = FcsReader.Read(stream, lossless: true);
        var file = new SaveFile { Data = data };
        long at = stream.Position;
        while (at < bytes.Length)
        {
            if (bytes.Length - at < 4) throw new FcsFormatException($"{bytes.Length - at} stray bytes after the records.", at);
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)at));
            if (count < 0 || (long)count * 4 > bytes.Length - at - 4)
                throw new FcsFormatException($"Slot list declares {count} entries but {bytes.Length - at - 4} bytes follow.", at);
            var list = new int[count];
            for (int i = 0; i < count; i++)
                list[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)at + 4 + 4 * i));
            file.SlotLists.Add(list);
            at += 4 + 4L * count;
        }
        return file;
    }

    public void Write(Stream stream)
    {
        // Record sizes are written as held: the game's own files hold the child count there (docs/formats/save.md "byteSize").
        FcsWriter.Write(Data, stream, keepByteSizes: true, lossless: true);
        using var w = new BinaryWriter(stream, FcsReader.Encoding, leaveOpen: true);
        foreach (var list in SlotLists)
        {
            w.Write(list.Length);
            foreach (int v in list) w.Write(v);
        }
    }

    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        Write(ms);
        return ms.ToArray();
    }

    public void WriteFile(string path) => File.WriteAllBytes(path, ToBytes());
}
