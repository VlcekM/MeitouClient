using System.Numerics;
using System.Text;

namespace Meitou.Data.Fcs;

/// <summary>
/// Reads <see cref="FcsFile"/>s. Layout: docs/formats/fcs-mod.md.
/// </summary>
public static class FcsReader
{
    /// <summary>
    /// Strict UTF-8, for the binary readers and writers of the FCS containers' own numbers. FCS strings themselves are any bytes (the game copies
    /// them as they are, docs/formats/fcs-mod.md "Strings") and go through <see cref="LosslessUtf8"/>.
    /// </summary>
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The sanity limit for counts and string lengths, so corrupt input fails fast instead of allocating gigabytes.
    /// </summary>
    const int MaxCount = 64 * 1024 * 1024;

    /// <summary>
    /// Reads the file at <paramref name="path"/>.
    /// </summary>
    public static FcsFile ReadFile(string path)
    {
        // A large buffer: the reader takes a few bytes at a time, and a mod list is hundreds of megabytes.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Read(stream);
    }

    /// <summary>
    /// Reads a whole file from <paramref name="stream"/>, which is left open. Strings are decoded with <see cref="LosslessUtf8"/>: bytes that are
    /// not UTF-8 are kept and written back unchanged. A file that does not parse throws an <see cref="FcsFormatException"/> whose
    /// <see cref="FcsFormatException.Partial"/> holds what was read before the problem once the file type was known.
    /// </summary>
    public static FcsFile Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding, leaveOpen: true);
        var file = new FcsFile();
        try
        {
            ReadHeader(r, file);
            file.NextId = r.ReadInt32();
            int count = ReadCount(r);
            file.Records.EnsureCapacity(count);
            for (int i = 0; i < count; i++)
                file.Records.Add(ReadRecord(r));
            return file;
        }
        catch (EndOfStreamException e)
        {
            throw new FcsFormatException("Unexpected end of file.", stream.Position, e) { Partial = Typed(file) };
        }
        catch (FcsFormatException e) when (e.Partial is null)
        {
            e.Partial = Typed(file);
            throw;
        }
    }

    /// <summary>
    /// The header of the file at <paramref name="path"/> (type 16 or 17: version, author, description, dependencies, references); null when the file
    /// does not open, has another type or its header does not parse.
    /// </summary>
    public static FcsFile? TryReadHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding, leaveOpen: true);
            var file = new FcsFile();
            ReadHeader(r, file);
            return file.FileType == FcsFileType.V15 ? null : file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FcsFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="file"/> when its type was read and known, else null.
    /// </summary>
    static FcsFile? Typed(FcsFile file) => IsKnownType((int)file.FileType) ? file : null;

    /// <summary>
    /// Whether <paramref name="type"/> is a file type this reader reads (V15, V16, V17).
    /// </summary>
    static bool IsKnownType(int type) => type is (int)FcsFileType.V15 or (int)FcsFileType.V16 or (int)FcsFileType.V17;

    /// <summary>
    /// Reads the file type and, unless V15, the header into <paramref name="file"/>; a V17 header's editor bookkeeping sections are read only as far
    /// as its declared length reaches, the rest is kept as <see cref="FcsFile.HeaderTail"/>.
    /// </summary>
    static void ReadHeader(BinaryReader r, FcsFile file)
    {
        int type = r.ReadInt32();
        if (!IsKnownType(type))
            throw new FcsFormatException($"Unknown file type {type}.", r.BaseStream.Position - 4);
        file.FileType = (FcsFileType)type;
        if (file.FileType == FcsFileType.V15) return;

        long headerEnd = -1;
        if (file.FileType == FcsFileType.V17)
            headerEnd = r.BaseStream.Position + ReadCount(r) + 4;

        file.Version = r.ReadInt32();
        file.Author = ReadString(r);
        file.Description = ReadString(r);
        file.Dependencies.AddRange(SplitList(ReadString(r)));
        file.References.AddRange(SplitList(ReadString(r)));

        if (headerEnd < 0) return;

        // Editor bookkeeping; each section is present only if the declared header length leaves room for it.
        if (r.BaseStream.Position < headerEnd)
        {
            uint saveCounter = r.ReadUInt32(), lastMergeResolve = r.ReadUInt32();
            var merged = new List<FcsMergedMod>();
            for (int n = r.ReadByte(); n > 0; n--)
                merged.Add(new FcsMergedMod(ReadString(r), r.ReadUInt32(), r.ReadUInt32()));
            file.Merge = new FcsMergeInfo(saveCounter, lastMergeResolve, merged);
        }
        if (r.BaseStream.Position < headerEnd)
        {
            file.DeleteRequests = [];
            for (int n = r.ReadByte(); n > 0; n--)
                file.DeleteRequests.Add(new FcsDeleteRequest(ReadString(r), r.ReadUInt32(), [.. ReadString(r).Split(':')]));
        }

        long tail = headerEnd - r.BaseStream.Position;
        if (tail < 0)
            throw new FcsFormatException("Header is longer than its declared length.", r.BaseStream.Position);
        file.HeaderTail = r.ReadBytes((int)tail);
    }

    /// <summary>
    /// Reads one record: its fixed fields, the seven value maps, the reference lists and the instances.
    /// </summary>
    static FcsRecord ReadRecord(BinaryReader r)
    {
        var record = new FcsRecord
        {
            ByteSize = r.ReadUInt32(),
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
        ReadMap(r, record.Vector3S, r => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
        ReadMap(r, record.Vector4S, ReadVector4);
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
                Rotation = ReadRotation(r),
            };
            for (int s = ReadCount(r); s > 0; s--)
                instance.States.Add(ReadString(r));
            record.Instances.Add(instance);
        }

        return record;
    }

    /// <summary>
    /// Reads a count and that many key-value pairs into <paramref name="map"/>; a key that repeats throws.
    /// </summary>
    static void ReadMap<T>(BinaryReader r, OrderedDictionary<string, T> map, Func<BinaryReader, T> readValue)
    {
        int count = ReadCount(r);
        // grown once: a heavy mod list's hundreds of thousands of maps grew by doubling, garbage that held the loading up in collections
        map.EnsureCapacity(count);
        for (int n = count; n > 0; n--)
        {
            long at = r.BaseStream.Position;
            var key = ReadString(r);
            if (!map.TryAdd(key, readValue(r)))
                throw new FcsFormatException($"Duplicate property '{key}'.", at);
        }
    }

    /// <summary>
    /// Reads four floats in stored order (x, y, z, w).
    /// </summary>
    static Vector4 ReadVector4(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    /// <summary>
    /// Reads an instance rotation, stored w, x, y, z.
    /// </summary>
    static Quaternion ReadRotation(BinaryReader r)
    {
        float w = r.ReadSingle(), x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
        return new Quaternion(x, y, z, w);
    }

    /// <summary>
    /// Reads a length-prefixed string of any bytes (<see cref="LosslessUtf8"/>).
    /// </summary>
    static string ReadString(BinaryReader r)
    {
        int length = ReadCount(r);
        if (length == 0) return "";

        // Most are short keys and names: read on the stack rather than into an array each (BinaryReader reads no further than asked, so the stream is
        // where it left off).
        Span<byte> bytes = length <= 1024 ? stackalloc byte[length] : new byte[length];
        r.BaseStream.ReadExactly(bytes);
        return LosslessUtf8.GetString(bytes);
    }

    /// <summary>
    /// Reads an int count or length, throwing on a negative one or one above <see cref="MaxCount"/>.
    /// </summary>
    static int ReadCount(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n is < 0 or > MaxCount)
            throw new FcsFormatException($"Implausible count or length {n}.", r.BaseStream.Position - 4);
        return n;
    }

    /// <summary>
    /// Splits a comma-separated header list (dependencies, references), dropping empty entries.
    /// </summary>
    static IEnumerable<string> SplitList(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// An FCS file that does not parse: the message names the problem and the byte offset it was found at.
/// </summary>
public sealed class FcsFormatException(string message, long offset, Exception? inner = null)
    : FormatException($"{message} (at byte {offset})", inner)
{
    /// <summary>
    /// The byte offset in the stream where the problem was found.
    /// </summary>
    public long Offset { get; } = offset;

    /// <summary>
    /// What was read before the problem when the file's type was a known one (the header as far as it got, the records read whole); null when the
    /// type itself was not, or the problem was not met by <see cref="FcsReader.Read(Stream)"/>.
    /// </summary>
    public FcsFile? Partial { get; internal set; }
}