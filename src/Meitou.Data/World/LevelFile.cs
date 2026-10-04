using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// A world-state file: <c>zone.X.Y.zone</c> or <c>*.level</c> (docs/formats/zones.md). These are FCS
/// game-data files (records of the GAMESTATE_* types, instance collections, roads), followed in most files by
/// a list of int32 values whose meaning is unknown. Formats 15 and 16 are read by <see cref="FcsReader"/> and
/// round-trip byte for byte; the legacy formats 10 and 13 (only under <c>data/leveldata/</c>) are read by an
/// own reader and can't be written back.
/// </summary>
public sealed class LevelFile
{
    /// <summary>FCS file type number from the first int32: 10, 13, 15 or 16 in the base game (17 is accepted too).</summary>
    public int Format { get; init; }

    /// <summary>
    /// Header and records. For legacy formats <see cref="FcsFile.FileType"/> is meaningless (kept at its
    /// default), records have <see cref="FcsRecord.Flags"/> 0, and int ids are stored as decimal strings.
    /// </summary>
    public required FcsFile Data { get; init; }

    /// <summary>
    /// The trailing list after the records, flattened, or null if the file ends after them. Stored as int32
    /// <c>count</c>, then <c>count</c> entries of <see cref="TrailerEntryInts"/> int32 values each.
    /// </summary>
    public int[]? Trailer { get; set; }

    /// <summary>Int32 values per trailer entry: 1 in formats 15 and 16, 2 in the legacy format 13 files.</summary>
    public int TrailerEntryInts { get; set; } = 1;

    /// <summary>
    /// Format 11–14 only: each record's list of (key, included) tags that the editor uses to pick which fields
    /// a mod changes. Indexed like <see cref="FcsFile.Records"/>; null for other formats.
    /// </summary>
    public List<List<KeyValuePair<string, bool>>>? LegacyFieldTags { get; init; }

    public bool IsLegacy => Format < 15;

    public static LevelFile ReadFile(string path) => Read(File.ReadAllBytes(path));

    public static LevelFile Read(byte[] bytes)
    {
        if (bytes.Length < 4)
            throw new FcsFormatException("File is too short.", 0);
        int format = BitConverter.ToInt32(bytes, 0);
        using var stream = new MemoryStream(bytes, writable: false);
        LevelFile file;
        if (format is 15 or 16 or 17)
            file = new LevelFile { Format = format, Data = FcsReader.Read(stream) };
        else if (format is >= 8 and <= 14)
            file = LegacyReader.Read(stream, format);
        else
            throw new FcsFormatException($"Unsupported level file format {format}.", 0);
        (file.Trailer, file.TrailerEntryInts) = ReadTrailer(bytes, stream.Position);
        return file;
    }

    static (int[]? Values, int EntryInts) ReadTrailer(byte[] bytes, long position)
    {
        long remaining = bytes.Length - position;
        if (remaining == 0) return (null, 1);
        if (remaining < 4)
            throw new FcsFormatException($"{remaining} stray bytes after the records.", position);
        int count = BitConverter.ToInt32(bytes, (int)position);
        // The entry size follows from the byte count: 4 bytes (formats 15, 16) or 8 (format 13).
        int entryInts = count > 0 && remaining == 4 + 8L * count ? 2 : 1;
        if (count < 0 || remaining != 4 + 4L * entryInts * count)
            throw new FcsFormatException($"Trailer declares {count} entries but {remaining - 4} bytes follow.", position);
        var values = new int[count * entryInts];
        for (int i = 0; i < values.Length; i++)
            values[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)position + 4 + 4 * i));
        return (values, entryInts);
    }

    /// <summary>Writes formats 15 and 16 in the same layout they were read from.</summary>
    public void Write(Stream stream)
    {
        if (IsLegacy)
            throw new NotSupportedException($"Writing legacy level file format {Format} is not supported.");
        if ((int)Data.FileType != Format)
            throw new InvalidOperationException($"Format {Format} differs from the data's file type {(int)Data.FileType}.");
        FcsWriter.Write(Data, stream, keepByteSizes: true);
        if (Trailer is null) return;
        using var w = new BinaryWriter(stream, FcsReader.Encoding, leaveOpen: true);
        if (TrailerEntryInts < 1 || Trailer.Length % TrailerEntryInts != 0)
            throw new InvalidOperationException($"Trailer length {Trailer.Length} is not a multiple of {TrailerEntryInts}.");
        w.Write(Trailer.Length / TrailerEntryInts);
        foreach (var v in Trailer) w.Write(v);
    }

    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        Write(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Reader for FCS formats 8–14, following the editor's handling of old files (FCS <c>GameData.load</c> and
    /// <c>Item.load</c>; facts in docs/formats/zones.md): no file header, no record flags, a field tag list from
    /// format 11, int32 instance and state ids instead of strings.
    /// </summary>
    static class LegacyReader
    {
        public static LevelFile Read(Stream stream, int format)
        {
            using var r = new BinaryReader(stream, FcsReader.Encoding, leaveOpen: true);
            try
            {
                r.ReadInt32();
                var data = new FcsFile { NextId = r.ReadInt32() };
                var tags = format >= 11 ? new List<List<KeyValuePair<string, bool>>>() : null;
                for (int n = Count(r); n > 0; n--)
                    data.Records.Add(ReadRecord(r, format, tags));
                return new LevelFile { Format = format, Data = data, LegacyFieldTags = tags };
            }
            catch (EndOfStreamException e)
            {
                throw new FcsFormatException("Unexpected end of file.", stream.Position, e);
            }
        }

        static FcsRecord ReadRecord(BinaryReader r, int format, List<List<KeyValuePair<string, bool>>>? allTags)
        {
            var record = new FcsRecord { ByteSize = r.ReadUInt32(), Type = r.ReadInt32(), Id = r.ReadInt32(), Name = Str(r) };
            // Format < 7 builds the string id from the numeric id; not needed for any known level file.
            record.StringId = format >= 7 ? Str(r) : record.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (allTags is not null)
            {
                var tags = new List<KeyValuePair<string, bool>>();
                for (int n = Count(r); n > 0; n--)
                    tags.Add(new(Str(r), Bool(r)));
                allTags.Add(tags);
            }

            Map(r, record.Bools, Bool);
            Map(r, record.Floats, r => r.ReadSingle());
            Map(r, record.Ints, r => r.ReadInt32());
            if (format > 8)
            {
                Map(r, record.Vector3s, r => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                Map(r, record.Vector4s, r => new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            }
            Map(r, record.Strings, Str);
            Map(r, record.Filenames, Str);

            for (int c = Count(r); c > 0; c--)
            {
                var category = Str(r);
                var list = new List<FcsReference>();
                for (int n = Count(r); n > 0; n--)
                {
                    string target = Str(r);
                    int v0 = r.ReadInt32();
                    int v1 = format >= 10 ? r.ReadInt32() : 0, v2 = format >= 10 ? r.ReadInt32() : 0;
                    list.Add(new FcsReference(target, v0, v1, v2));
                }
                record.References[category] = list;
            }

            for (int n = Count(r); n > 0; n--)
            {
                var instance = new FcsInstance
                {
                    Id = r.ReadInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Target = Str(r),
                    Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                };
                float w = r.ReadSingle(), x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
                instance.Rotation = new Quaternion(x, y, z, w);
                for (int s = Count(r); s > 0; s--)
                    instance.States.Add(r.ReadInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
                record.Instances.Add(instance);
            }
            return record;
        }

        static void Map<T>(BinaryReader r, OrderedDictionary<string, T> map, Func<BinaryReader, T> read)
        {
            for (int n = Count(r); n > 0; n--)
            {
                long at = r.BaseStream.Position;
                if (!map.TryAdd(Str(r), read(r)))
                    throw new FcsFormatException("Duplicate property.", at);
            }
        }

        static bool Bool(BinaryReader r) => r.ReadByte() switch
        {
            0 => false,
            1 => true,
            var b => throw new FcsFormatException($"Bool value {b} is neither 0 nor 1.", r.BaseStream.Position - 1),
        };

        static int Count(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n is < 0 or > 16 * 1024 * 1024)
                throw new FcsFormatException($"Implausible count or length {n}.", r.BaseStream.Position - 4);
            return n;
        }

        static string Str(BinaryReader r)
        {
            int length = Count(r);
            if (length == 0) return "";
            var bytes = r.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return FcsReader.Encoding.GetString(bytes);
        }
    }
}
