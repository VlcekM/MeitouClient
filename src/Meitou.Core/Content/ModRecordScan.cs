using System.Buffers.Binary;
using System.Text;

namespace Meitou.Content;

/// <summary>
/// What one record of a data file does to the file paths of its string id (the key records merge by, docs/formats/fcs-mod.md "Merging").
/// </summary>
/// <param name="StringId">The record's string id.</param>
/// <param name="IsNew">Flag bit 0 is clear: the file states the record whole, not as a change to an earlier one.</param>
/// <param name="Removed">Its bool <c>REMOVED</c> is true: the merge drops the record and its fields.</param>
/// <param name="Filled">The filename keys it gives a path.</param>
/// <param name="Blank">The filename keys it gives an empty value.</param>
public sealed record ScannedRecord(string StringId, bool IsNew, bool Removed, IReadOnlyList<string> Filled, IReadOnlyList<string> Blank);

/// <summary>
/// The records of a <c>.mod</c> / <c>.base</c> file that set, empty or remove file paths, for <see cref="ModBlanking"/>. Read by a slim reader of
/// its own (file types 15 to 17, docs/formats/fcs-mod.md "Record"): the launcher has no <c>Meitou.Data</c>, and a mod list is hundreds of
/// megabytes of which this keeps the string ids, the flags and the filename keys.
/// </summary>
public sealed class ModRecordScan
{
    /// <summary>
    /// The bool key that removes a record.
    /// </summary>
    static ReadOnlySpan<byte> RemovedKey => "REMOVED"u8;

    /// <summary>
    /// A scan holding <paramref name="records"/>.
    /// </summary>
    public ModRecordScan(IReadOnlyList<ScannedRecord> records) => Records = records;

    /// <summary>
    /// The records that touch a file path or remove their record, in file order; the others are left out.
    /// </summary>
    public IReadOnlyList<ScannedRecord> Records { get; }

    /// <summary>
    /// Scans the file at <paramref name="path"/>; null when it cannot be read or is not a type 15 to 17 file that reads to its end.
    /// </summary>
    public static ModRecordScan? TryRead(string path)
    {
        try
        {
            return Read(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Scans a whole file's bytes; throws <see cref="InvalidDataException"/> when they are not a type 15 to 17 file.
    /// </summary>
    public static ModRecordScan Read(ReadOnlySpan<byte> data)
    {
        var cursor = new Cursor(data);
        SkipHeader(ref cursor);
        cursor.Skip(4);   // next id

        var records = new List<ScannedRecord>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int n = cursor.Count(); n > 0; n--)
            if (ReadRecord(ref cursor, keys) is { } record) records.Add(record);

        return new ModRecordScan(records);
    }

    /// <summary>
    /// Skips the file type and the header: none in type 15, version and four strings in 16, a declared length in 17.
    /// </summary>
    static void SkipHeader(ref Cursor cursor)
    {
        int type = cursor.Int32();
        if (type == 15) return;
        if (type == 17)
        {
            cursor.Skip(cursor.Count());
            return;
        }
        if (type != 16) throw new InvalidDataException($"Unknown file type {type}.");

        cursor.Skip(4);   // version
        for (int n = 0; n < 4; n++) cursor.SkipString();   // author, description, dependencies, references
    }

    /// <summary>
    /// Reads one record, decoding its string id, flags, <c>REMOVED</c> and filename keys and skipping the rest; null when it neither touches a
    /// file path nor removes its record. <paramref name="keys"/> holds the key strings read so far, so each is made once per file.
    /// </summary>
    static ScannedRecord? ReadRecord(ref Cursor cursor, HashSet<string> keys)
    {
        cursor.Skip(12);   // byte size, type, numeric id
        cursor.SkipString();   // name
        string stringId = cursor.String();
        bool isNew = (cursor.Int32() & 1) == 0;

        bool removed = ReadRemoved(ref cursor);
        SkipValues(ref cursor, 4);   // floats
        SkipValues(ref cursor, 4);   // ints
        SkipValues(ref cursor, 12);   // vec3
        SkipValues(ref cursor, 16);   // vec4
        for (int n = cursor.Count(); n > 0; n--)
        {
            cursor.SkipString();
            cursor.SkipString();
        }
        var (filled, blank) = ReadFilenames(ref cursor, keys);
        SkipReferences(ref cursor);
        SkipInstances(ref cursor);

        if (!removed && filled is null && blank is null) return null;
        return new ScannedRecord(stringId, isNew, removed, filled ?? [], blank ?? []);
    }

    /// <summary>
    /// Reads the bool list; whether it has <c>REMOVED</c> set.
    /// </summary>
    static bool ReadRemoved(ref Cursor cursor)
    {
        bool removed = false;
        for (int n = cursor.Count(); n > 0; n--)
        {
            var key = cursor.Bytes();
            bool value = cursor.Byte() != 0;
            if (value && key.SequenceEqual(RemovedKey)) removed = true;
        }
        return removed;
    }

    /// <summary>
    /// Skips a list of keys with fixed-size values of <paramref name="size"/> bytes.
    /// </summary>
    static void SkipValues(ref Cursor cursor, int size)
    {
        for (int n = cursor.Count(); n > 0; n--)
        {
            cursor.SkipString();
            cursor.Skip(size);
        }
    }

    /// <summary>
    /// Reads the filename list: the keys with a path and the keys with an empty value (null for none).
    /// </summary>
    static (List<string>? Filled, List<string>? Blank) ReadFilenames(ref Cursor cursor, HashSet<string> keys)
    {
        List<string>? filled = null, blank = null;
        for (int n = cursor.Count(); n > 0; n--)
        {
            string key = Pooled(cursor.Bytes(), keys);
            if (cursor.Bytes().IsEmpty) (blank ??= []).Add(key);
            else
                (filled ??= []).Add(key);
        }
        return (filled, blank);
    }

    /// <summary>
    /// Skips the reference lists: a name, then targets with three ints each.
    /// </summary>
    static void SkipReferences(ref Cursor cursor)
    {
        for (int lists = cursor.Count(); lists > 0; lists--)
        {
            cursor.SkipString();
            for (int n = cursor.Count(); n > 0; n--)
            {
                cursor.SkipString();
                cursor.Skip(12);
            }
        }
    }

    /// <summary>
    /// Skips the instances: id, target, position, rotation and the state ids.
    /// </summary>
    static void SkipInstances(ref Cursor cursor)
    {
        for (int n = cursor.Count(); n > 0; n--)
        {
            cursor.SkipString();
            cursor.SkipString();
            cursor.Skip(12 + 16);
            for (int states = cursor.Count(); states > 0; states--) cursor.SkipString();
        }
    }

    /// <summary>
    /// The key <paramref name="utf8"/> spells, taken from <paramref name="keys"/> when it was read before.
    /// </summary>
    static string Pooled(ReadOnlySpan<byte> utf8, HashSet<string> keys)
    {
        Span<char> buffer = stackalloc char[128];
        if (utf8.Length > buffer.Length) return Encoding.UTF8.GetString(utf8);

        var chars = buffer[..Encoding.UTF8.GetChars(utf8, buffer)];
        var lookup = keys.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(chars, out var known)) return known;

        string key = new(chars);
        keys.Add(key);
        return key;
    }

    /// <summary>
    /// A read position in a file's bytes; reading past the end throws <see cref="InvalidDataException"/>.
    /// </summary>
    ref struct Cursor(ReadOnlySpan<byte> data)
    {
        /// <summary>
        /// The bytes not read yet.
        /// </summary>
        ReadOnlySpan<byte> _rest = data;

        /// <summary>
        /// Reads a little-endian int32.
        /// </summary>
        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        /// <summary>
        /// Reads one byte.
        /// </summary>
        public byte Byte() => Take(1)[0];

        /// <summary>
        /// Reads a count or length: an int32 that is not negative.
        /// </summary>
        public int Count()
        {
            int count = Int32();
            if (count < 0) throw new InvalidDataException("Negative count.");
            return count;
        }

        /// <summary>
        /// Reads a length-prefixed run of bytes (a string's UTF-8).
        /// </summary>
        public ReadOnlySpan<byte> Bytes() => Take(Count());

        /// <summary>
        /// Reads a length-prefixed string; bytes that are not UTF-8 decode to U+FFFD.
        /// </summary>
        public string String() => Encoding.UTF8.GetString(Bytes());

        /// <summary>
        /// Skips a length-prefixed string.
        /// </summary>
        public void SkipString() => Take(Count());

        /// <summary>
        /// Skips <paramref name="bytes"/> bytes.
        /// </summary>
        public void Skip(int bytes) => Take(bytes);

        /// <summary>
        /// The next <paramref name="bytes"/> bytes, stepping past them.
        /// </summary>
        ReadOnlySpan<byte> Take(int bytes)
        {
            if (bytes > _rest.Length) throw new InvalidDataException("Unexpected end of file.");

            var taken = _rest[..bytes];
            _rest = _rest[bytes..];
            return taken;
        }
    }
}
