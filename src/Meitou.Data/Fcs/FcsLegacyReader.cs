using System.Numerics;

namespace Meitou.Data.Fcs;

/// <summary>
/// Reader for FCS formats 8–14, following the editor's handling of old files (FCS <c>GameData.load</c> and
/// <c>Item.load</c>; facts in docs/formats/zones.md): no file header, no record flags, a field tag list from
/// format 11, int32 instance and state ids instead of strings. The game's loader reads these formats too (old level files, old body files).
/// </summary>
public static class FcsLegacyReader
{
    /// <summary>
    /// Whether <paramref name="format"/> (a file's first int) is one of the legacy formats this reads.
    /// </summary>
    public static bool IsLegacy(int format) => format is >= 8 and <= 14;

    /// <summary>
    /// Reads a legacy file from the start of <paramref name="stream"/>: the format int (skipped), the next id and the records, with each record's
    /// field tags (from format 11 on; else null). Records have <see cref="FcsRecord.Flags"/> 0 and int ids are stored as decimal strings.
    /// </summary>
    public static (FcsFile Data, List<List<KeyValuePair<string, bool>>>? Tags) Read(Stream stream, int format)
    {
        using var r = new BinaryReader(stream, FcsReader.Encoding, leaveOpen: true);
        try
        {
            r.ReadInt32();
            var data = new FcsFile { NextId = r.ReadInt32() };
            var tags = format >= 11 ? new List<List<KeyValuePair<string, bool>>>() : null;
            for (int n = Count(r); n > 0; n--)
                data.Records.Add(ReadRecord(r, format, tags));
            return (data, tags);
        }
        catch (EndOfStreamException e)
        {
            throw new FcsFormatException("Unexpected end of file.", stream.Position, e);
        }
    }

    /// <summary>
    /// Reads one legacy record; <paramref name="allTags"/>, when given, collects each record's field tags.
    /// </summary>
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
            Map(r, record.Vector3S, r => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            Map(r, record.Vector4S, r => new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
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

    /// <summary>
    /// Reads a count, then that many name and value pairs into <paramref name="map"/>.
    /// </summary>
    static void Map<T>(BinaryReader r, OrderedDictionary<string, T> map, Func<BinaryReader, T> read)
    {
        for (int n = Count(r); n > 0; n--)
        {
            long at = r.BaseStream.Position;
            if (!map.TryAdd(Str(r), read(r)))
                throw new FcsFormatException("Duplicate property.", at);
        }
    }

    /// <summary>
    /// Reads a bool byte; throws unless it is 0 or 1.
    /// </summary>
    static bool Bool(BinaryReader r) => r.ReadByte() switch
    {
        0 => false,
        1 => true,
        var b => throw new FcsFormatException($"Bool value {b} is neither 0 nor 1.", r.BaseStream.Position - 1),
    };

    /// <summary>
    /// Reads a count; throws when it is negative or implausibly large.
    /// </summary>
    static int Count(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n is < 0 or > 16 * 1024 * 1024)
            throw new FcsFormatException($"Implausible count or length {n}.", r.BaseStream.Position - 4);
        return n;
    }

    /// <summary>
    /// Reads a length-prefixed string of any bytes (<see cref="LosslessUtf8"/>, as <see cref="FcsReader"/>).
    /// </summary>
    static string Str(BinaryReader r)
    {
        int length = Count(r);
        if (length == 0) return "";
        var bytes = r.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return LosslessUtf8.GetString(bytes);
    }
}
