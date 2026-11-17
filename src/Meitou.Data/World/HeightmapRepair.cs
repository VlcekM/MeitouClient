using System.Collections.Concurrent;
using Meitou.Content;

namespace Meitou.Data.World;

/// <summary>
/// The fix for holes and needles in the game's own heightmap (docs/formats/terrain.md "Holes and needles"; Fixes settings, on by default):
/// <see cref="HeightmapHoles"/> are filled from their rim and <see cref="HeightmapNeedles"/> lowered to the ground around them. Found by rule from
/// the file itself, once per file version (kept in <see cref="HeightmapRepairCache"/>). Applies only to the install's own
/// <see cref="TerrainHeightmap.RelativePath"/>, never to a mod's heightmap, and only while switched on (<see cref="Configure"/>).
/// <see cref="TerrainHeightmap"/> applies it to every read.
/// </summary>
public sealed class HeightmapRepair
{
    #region The switch

    /// <summary>
    /// The heightmap the fix applies to (the install's own, full path), or null while it is off.
    /// </summary>
    static volatile string? s_target;

    /// <summary>
    /// Whether the fix was switched on (<see cref="Configure"/>), whether or not a mod's heightmap stands in for the game's.
    /// </summary>
    public static bool IsOn => s_target is not null;

    /// <summary>
    /// Whether an enabled mod's heightmap stands in for the game's (an <c>RE_Kenshi.json</c> rebind), so the fix has nothing to repair. Set by
    /// <see cref="Configure"/>.
    /// </summary>
    public static bool Replaced { get; private set; }

    /// <summary>
    /// The name of the enabled mod whose folder holds that heightmap; null when none does or the game's own is read.
    /// </summary>
    public static string? ReplacedBy { get; private set; }

    /// <summary>
    /// Switches the fix on for <paramref name="install"/>'s own heightmap, or off, and notes whether a mod's heightmap stands in for it.
    /// Heightmaps opened afterwards follow it; the game calls it once, at start.
    /// </summary>
    public static void Configure(GameInstall install, bool enabled)
    {
        string own = OwnHeightmap(install), read = Path.GetFullPath(TerrainHeightmap.PathOf(install));
        Replaced = !string.Equals(read, own, StringComparison.OrdinalIgnoreCase);
        ReplacedBy = Replaced ? ModOwning(install, read) : null;
        s_target = enabled ? own : null;
    }

    /// <summary>
    /// The full path of <paramref name="install"/>'s own heightmap, whatever a mod rebinds it to.
    /// </summary>
    public static string OwnHeightmap(GameInstall install) =>
        Path.GetFullPath(GameFiles.Resolve(Path.Combine(install.DataDirectory, TerrainHeightmap.RelativePath)));

    /// <summary>
    /// Whether the fix is on and applies to the heightmap at <paramref name="path"/>.
    /// </summary>
    public static bool AppliesTo(string path) =>
        s_target is { } target && string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name of the enabled mod whose folder holds <paramref name="file"/>, or null.
    /// </summary>
    static string? ModOwning(GameInstall install, string file)
    {
        foreach (var mod in ReKenshiFile.EnabledMods(install))
        {
            string folder = Path.GetFullPath(mod.Folder) + Path.DirectorySeparatorChar;
            if (file.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return mod.Name;
        }

        return null;
    }

    #endregion

    #region Finding the repairs

    /// <summary>
    /// The repairs per file (full path, length and write time), made on first use.
    /// </summary>
    static readonly ConcurrentDictionary<(string Path, long Length, DateTime Stamp), Lazy<HeightmapRepair?>> Found = new();

    /// <summary>
    /// The repairs of the heightmap at <paramref name="path"/> when the fix applies to it (<see cref="AppliesTo"/>) and the file could be scanned,
    /// else null.
    /// </summary>
    public static HeightmapRepair? For(string path)
    {
        if (!AppliesTo(path)) return null;

        var file = new FileInfo(path);
        return Found.GetOrAdd((file.FullName, file.Length, file.LastWriteTimeUtc), _ => new(() => FindIn(file))).Value;
    }

    /// <summary>
    /// What a cache key adds for the heightmap at <paramref name="path"/>: the repaired samples' count and a hash of them, or nothing when the fix
    /// does not apply, so a cache built with the fix is not used without it and the other way round.
    /// </summary>
    public static string StampFor(string path)
    {
        if (For(path) is not { } repair) return "";

        // FNV-1a over (row, column, value): the same in every process, unlike HashCode.
        ulong hash = 14695981039346656037;
        foreach (var (column, row, value) in repair.Samples())
            foreach (long part in (long[])[row, column, value]) hash = (hash ^ (ulong)part) * 1099511628211;
        return $"repaired {repair.Count} {hash:x16}";
    }

    /// <summary>
    /// Finds the repairs of <paramref name="map"/> (opened from a file): its holes, then its needles.
    /// </summary>
    public static HeightmapRepair Find(TerrainHeightmap map)
    {
        var (holes, holeCount) = HeightmapHoles.Find(map);
        var (needles, needleCount) = HeightmapNeedles.Find(map);
        return new HeightmapRepair(map.Size, holes.Concat(needles), holeCount, needleCount);
    }

    /// <summary>
    /// The repairs of <paramref name="file"/>: kept from an earlier start, else found and kept. Null when the file cannot be scanned, so the
    /// heightmap reads as it is and the cache keys stay as with the fix off.
    /// </summary>
    static HeightmapRepair? FindIn(FileInfo file)
    {
        try
        {
            using var map = TerrainHeightmap.OpenUnrepaired(file.FullName);
            if (HeightmapRepairCache.TryRead(file, map.Size) is { } kept)
                return new(map.Size, kept.Samples, kept.Holes, kept.Needles);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var repair = Find(map);
            HeightmapRepairCache.Write(file, [.. repair.Samples()], repair.Holes, repair.Needles);
            double seconds = watch.Elapsed.TotalSeconds;
            Console.WriteLine($"terrain   heightmap fix: {repair.Holes} holes and {repair.Needles} needles found in {seconds:F1} s");
            return repair;
        }
        catch (Exception e)
        {
            // Any fault of the scan (a read error in its parallel pass comes as an AggregateException) leaves the ground as the file has it.
            Console.Error.WriteLine($"terrain   heightmap fix: {file.FullName}: {e.GetBaseException().Message}; the ground is read as it is");
            return null;
        }
    }

    #endregion

    #region The repairs

    /// <summary>
    /// Row to its repaired samples (column, value), by column.
    /// </summary>
    readonly Dictionary<int, (int Column, ushort Value)[]> _rows;

    /// <summary>
    /// Whether a row holds a repaired sample, by row.
    /// </summary>
    readonly bool[] _rowRepaired;

    /// <summary>
    /// Repairs of <paramref name="samples"/> on a heightmap <paramref name="size"/> samples across.
    /// </summary>
    HeightmapRepair(int size, IEnumerable<(int Column, int Row, ushort Value)> samples, int holes, int needles)
    {
        _rowRepaired = new bool[size];
        _rows = samples
            .GroupBy(s => s.Row)
            .ToDictionary(g => g.Key, g => g.Select(s => (s.Column, s.Value)).OrderBy(s => s.Column).ToArray());
        foreach (var row in _rows.Keys) _rowRepaired[row] = true;

        Count = _rows.Values.Sum(r => r.Length);
        Holes = holes;
        Needles = needles;
    }

    /// <summary>
    /// Repaired samples.
    /// </summary>
    public int Count { get; }
    /// <summary>
    /// Holes filled.
    /// </summary>
    public int Holes { get; }
    /// <summary>
    /// Needles lowered.
    /// </summary>
    public int Needles { get; }

    /// <summary>
    /// The repaired value of (<paramref name="column"/>, <paramref name="row"/>), if it is repaired.
    /// </summary>
    public bool TryGet(int column, int row, out ushort value)
    {
        value = 0;
        if ((uint)row >= (uint)_rowRepaired.Length || !_rowRepaired[row]) return false;

        var samples = _rows[row];
        int at = Array.BinarySearch(samples, (column, (ushort)0), ColumnOrder.Instance);
        if (at < 0) return false;

        value = samples[at].Value;
        return true;
    }

    /// <summary>
    /// Writes the repaired samples of <paramref name="row"/> into <paramref name="samples"/> (the whole row, column 0 first).
    /// </summary>
    public void Apply(int row, Span<ushort> samples)
    {
        if ((uint)row >= (uint)_rowRepaired.Length || !_rowRepaired[row]) return;

        foreach (var (column, value) in _rows[row]) samples[column] = value;
    }

    /// <summary>
    /// Every repaired sample as (column, row, value), row by row.
    /// </summary>
    public IEnumerable<(int Column, int Row, ushort Value)> Samples() =>
        _rows
            .OrderBy(r => r.Key)
            .SelectMany(r => r.Value.Select(s => (s.Column, r.Key, s.Value)));

    /// <summary>
    /// Orders repaired samples by column.
    /// </summary>
    sealed class ColumnOrder : IComparer<(int Column, ushort Value)>
    {
        /// <summary>
        /// The one instance.
        /// </summary>
        public static readonly ColumnOrder Instance = new();

        /// <inheritdoc/>
        public int Compare((int Column, ushort Value) x, (int Column, ushort Value) y) => x.Column.CompareTo(y.Column);
    }

    #endregion
}
