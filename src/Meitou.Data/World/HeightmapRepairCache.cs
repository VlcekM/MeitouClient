namespace Meitou.Data.World;

/// <summary>
/// The repairs found in a heightmap file (<see cref="HeightmapRepair"/>), kept in the load caches (<see cref="LoadCaches.Root"/>'s <c>terrain</c>)
/// so a start does not scan the file again: one file per heightmap path, used while the heightmap's size and write time and the rules' version
/// match. Nothing of the heightmap but the repaired samples is kept.
/// </summary>
public static class HeightmapRepairCache
{
    /// <summary>
    /// The rules' version: a change to <see cref="HeightmapHoles"/> or <see cref="HeightmapNeedles"/> raises it, so old results are found again.
    /// </summary>
    public const int RulesVersion = 2;

    /// <summary>
    /// The cache file's first bytes.
    /// </summary>
    static ReadOnlySpan<byte> Magic => "MHRP"u8;

    /// <summary>
    /// The repairs kept for <paramref name="heightmap"/> (<paramref name="size"/> samples across: samples, hole and needle counts); null when there
    /// are none, they are stale, unreadable or damaged (a count or sample that cannot be), or the load caches are off. A damaged file is found
    /// again and rewritten.
    /// </summary>
    public static (List<(int Column, int Row, ushort Value)> Samples, int Holes, int Needles)? TryRead(FileInfo heightmap, int size)
    {
        string file = FileFor(heightmap);
        if (LoadCaches.Disabled || !File.Exists(file)) return null;

        try
        {
            using var reader = new BinaryReader(File.OpenRead(file));
            if (!Matches(reader, heightmap)) return null;

            int holes = reader.ReadInt32(), needles = reader.ReadInt32(), count = reader.ReadInt32();
            long left = reader.BaseStream.Length - reader.BaseStream.Position;
            if (holes < 0 || needles < 0 || count < 0 || left != count * 10L) return null;

            var samples = new List<(int, int, ushort)>(count);
            for (int i = 0; i < count; i++)
            {
                var (column, row, value) = (reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt16());
                if ((uint)column >= (uint)size || (uint)row >= (uint)size) return null;

                samples.Add((column, row, value));
            }

            return (samples, holes, needles);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps the repairs found for <paramref name="heightmap"/>, written to a temporary file and moved into place, so a write cut short never
    /// leaves a partial file; a cache that cannot be written is skipped.
    /// </summary>
    public static void Write(FileInfo heightmap, IReadOnlyCollection<(int Column, int Row, ushort Value)> samples, int holes, int needles)
    {
        if (LoadCaches.Disabled) return;

        string file = FileFor(heightmap);
        string temporary = $"{file}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            WriteFile(temporary, heightmap, samples, holes, needles);
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Left for the next write to replace.
            }
        }
    }

    /// <summary>
    /// Writes the cache file <paramref name="path"/>: the header (<see cref="Matches"/>), the counts and the samples, 10 bytes each.
    /// </summary>
    static void WriteFile(string path, FileInfo heightmap, IReadOnlyCollection<(int Column, int Row, ushort Value)> samples, int holes,
        int needles)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Magic);
        writer.Write(RulesVersion);
        writer.Write(heightmap.Length);
        writer.Write(heightmap.LastWriteTimeUtc.Ticks);
        writer.Write(holes);
        writer.Write(needles);
        writer.Write(samples.Count);
        foreach (var (column, row, value) in samples)
        {
            writer.Write(column);
            writer.Write(row);
            writer.Write(value);
        }
    }

    /// <summary>
    /// Whether the header <paramref name="reader"/> starts with was written for this version of <paramref name="heightmap"/> and these rules.
    /// </summary>
    static bool Matches(BinaryReader reader, FileInfo heightmap) =>
        reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) && reader.ReadInt32() == RulesVersion
        && reader.ReadInt64() == heightmap.Length && reader.ReadInt64() == heightmap.LastWriteTimeUtc.Ticks;

    /// <summary>
    /// The cache file for <paramref name="heightmap"/>: named by an FNV-1a hash of its full path, in lower case.
    /// </summary>
    static string FileFor(FileInfo heightmap)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in heightmap.FullName.ToLowerInvariant()) hash = (hash ^ c) * 1099511628211;
        return LoadCaches.Folder("terrain", $"heightmap-repair-{hash:x16}.bin");
    }
}
