using System.Numerics;
using System.Text;

namespace Meitou.Data.Fcs;

/// <summary>Reads <see cref="FcsFile"/>s. Layout: docs/formats/fcs-mod.md.</summary>
public static class FcsReader
{
    /// <summary>
    /// Text encoding of FCS strings: UTF-8 (every non-ASCII string in the base game is valid UTF-8). Strict, so
    /// invalid bytes fail loudly instead of decoding to U+FFFD and silently changing on write.
    /// </summary>
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Sanity limit for counts and string lengths, so corrupt input fails fast instead of allocating gigabytes.
    const int MaxCount = 64 * 1024 * 1024;

    public static FcsFile ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static FcsFile Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding, leaveOpen: true);
        try
        {
            var file = new FcsFile();
            ReadHeader(r, file);
            file.Marker = r.ReadInt32();
            int count = ReadCount(r);
            file.Records.EnsureCapacity(count);
            for (int i = 0; i < count; i++)
                file.Records.Add(ReadRecord(r));
            return file;
        }
        catch (EndOfStreamException e)
        {
            throw new FcsFormatException("Unexpected end of file.", stream.Position, e);
        }
    }

    static void ReadHeader(BinaryReader r, FcsFile file)
    {
        int type = r.ReadInt32();
        if (type is not ((int)FcsFileType.V16 or (int)FcsFileType.V17))
            throw new FcsFormatException($"Unknown file type {type}.", r.BaseStream.Position - 4);
        file.FileType = (FcsFileType)type;

        long headerEnd = -1;
        if (file.FileType == FcsFileType.V17)
            headerEnd = r.BaseStream.Position + ReadCount(r) + 4;

        file.Version = r.ReadInt32();
        file.Author = ReadString(r);
        file.Description = ReadString(r);
        file.Dependencies.AddRange(SplitList(ReadString(r)));
        file.References.AddRange(SplitList(ReadString(r)));

        if (headerEnd >= 0)
        {
            long tail = headerEnd - r.BaseStream.Position;
            if (tail < 0)
                throw new FcsFormatException("Header is longer than its declared length.", r.BaseStream.Position);
            file.HeaderTail = r.ReadBytes((int)tail);
        }
    }

    static FcsRecord ReadRecord(BinaryReader r)
    {
        var record = new FcsRecord
        {
            Unknown = r.ReadInt32(),
            Type = r.ReadInt32(),
            Id = r.ReadInt32(),
            Name = ReadString(r),
            StringId = ReadString(r),
            Flags = r.ReadUInt32(),
        };

        ReadMap(r, record.Bools, r => r.ReadByte() switch
        {
            0 => false,
            1 => true,
            var b => throw new FcsFormatException($"Bool value {b} is neither 0 nor 1.", r.BaseStream.Position - 1),
        });
        ReadMap(r, record.Floats, r => r.ReadSingle());
        ReadMap(r, record.Ints, r => r.ReadInt32());
        ReadMap(r, record.Vector3s, r => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
        ReadMap(r, record.Vector4s, ReadVector4);
        ReadMap(r, record.Strings, ReadString);
        ReadMap(r, record.Filenames, ReadString);

        for (int c = ReadCount(r); c > 0; c--)
        {
            var category = ReadString(r);
            int n = ReadCount(r);
            var list = new List<FcsReference>(n);
            for (; n > 0; n--)
                list.Add(new FcsReference(ReadString(r), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
            if (!record.References.TryAdd(category, list))
                throw new FcsFormatException($"Duplicate reference category '{category}' in {record.StringId}.", r.BaseStream.Position);
        }

        for (int n = ReadCount(r); n > 0; n--)
        {
            var instance = new FcsInstance
            {
                Id = ReadString(r),
                Target = ReadString(r),
                Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                Rotation = ReadVector4(r),
            };
            for (int s = ReadCount(r); s > 0; s--)
                instance.States.Add(ReadString(r));
            record.Instances.Add(instance);
        }

        return record;
    }

    static void ReadMap<T>(BinaryReader r, OrderedDictionary<string, T> map, Func<BinaryReader, T> readValue)
    {
        for (int n = ReadCount(r); n > 0; n--)
        {
            long at = r.BaseStream.Position;
            var key = ReadString(r);
            if (!map.TryAdd(key, readValue(r)))
                throw new FcsFormatException($"Duplicate property '{key}'.", at);
        }
    }

    static Vector4 ReadVector4(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    static string ReadString(BinaryReader r)
    {
        int length = ReadCount(r);
        if (length == 0) return "";
        var bytes = r.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        try { return Encoding.GetString(bytes); }
        catch (DecoderFallbackException e) { throw new FcsFormatException("String is not valid UTF-8.", r.BaseStream.Position - length, e); }
    }

    static int ReadCount(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n is < 0 or > MaxCount)
            throw new FcsFormatException($"Implausible count or length {n}.", r.BaseStream.Position - 4);
        return n;
    }

    static IEnumerable<string> SplitList(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

public sealed class FcsFormatException(string message, long offset, Exception? inner = null)
    : FormatException($"{message} (at byte {offset})", inner)
{
    public long Offset { get; } = offset;
}
