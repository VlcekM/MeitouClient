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
    /// <summary>
    /// FCS file type number from the first int32: 10, 13, 15 or 16 in the base game (17 is accepted too).
    /// </summary>
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

    /// <summary>
    /// Int32 values per trailer entry: 1 in formats 15 and 16, 2 in the legacy format 13 files.
    /// </summary>
    public int TrailerEntryInts { get; set; } = 1;

    /// <summary>
    /// Format 11–14 only: each record's list of (key, included) tags that the editor uses to pick which fields
    /// a mod changes. Indexed like <see cref="FcsFile.Records"/>; null for other formats.
    /// </summary>
    public List<List<KeyValuePair<string, bool>>>? LegacyFieldTags { get; init; }

    /// <summary>
    /// Whether the file is in a format before 15 (<see cref="FcsLegacyReader"/>).
    /// </summary>
    public bool IsLegacy => Format < 15;

    /// <summary>
    /// Reads the level file at <paramref name="path"/> (<see cref="Content.GameFiles.Resolve"/>d ignoring case).
    /// </summary>
    public static LevelFile ReadFile(string path) => Read(File.ReadAllBytes(Content.GameFiles.Resolve(path)));

    /// <summary>
    /// Parses a level file: the records in the current layout (formats 15 to 17) or the legacy one (8 to 14), then the trailer; throws for
    /// another format.
    /// </summary>
    public static LevelFile Read(byte[] bytes)
    {
        if (bytes.Length < 4)
            throw new FcsFormatException("File is too short.", 0);
        int format = BitConverter.ToInt32(bytes, 0);
        using var stream = new MemoryStream(bytes, writable: false);
        LevelFile file;
        if (format is 15 or 16 or 17)
            file = new LevelFile { Format = format, Data = FcsReader.Read(stream) };
        else if (FcsLegacyReader.IsLegacy(format))
        {
            var (data, tags) = FcsLegacyReader.Read(stream, format);
            file = new LevelFile { Format = format, Data = data, LegacyFieldTags = tags };
        }
        else
            throw new FcsFormatException($"Unsupported level file format {format}.", 0);
        (file.Trailer, file.TrailerEntryInts) = ReadTrailer(bytes, stream.Position);
        return file;
    }

    /// <summary>
    /// The trailer ints after the records at <paramref name="position"/>, and the ints per entry; (null, 1) when the file ends there.
    /// </summary>
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

    /// <summary>
    /// Writes formats 15 and 16 in the same layout they were read from.
    /// </summary>
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

    /// <summary>
    /// The file as bytes (<see cref="Write"/>).
    /// </summary>
    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        Write(ms);
        return ms.ToArray();
    }
}
