using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Meitou.Content;

namespace Meitou.Data.World;

/// <summary>
/// Switches for the derived-data load caches (<c>--no-load-cache</c>): when set nothing is read from or written to them.
/// </summary>
public static class LoadCaches
{
    /// <summary>
    /// Nothing is read from or written to the load caches.
    /// </summary>
    public static bool Disabled { get; set; }
}

/// <summary>
/// What the foliage layout cache did so far (a snapshot of <see cref="FoliageLayoutCache.Stats"/>).
/// </summary>
public readonly record struct FoliageLayoutCacheStats(int Hits, int Misses, int Rejected, int Written, double LoadMs, double ComputeMs, double SavedMs,
    long BytesRead, long BytesWritten)
{
    /// <summary>
    /// A one-line summary for the log.
    /// </summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var odd = Rejected > 0 ? string.Create(inv, $" ({Rejected} unusable entries recomputed)") : "";
        return string.Create(inv, $"layout cache {Hits} hits, {Misses} misses{odd}; hits loaded in {LoadMs:0} ms ({BytesRead / 1048576.0:0.0} MB), ")
            + string.Create(inv, $"misses computed in {ComputeMs:0} ms, {Written} written ({BytesWritten / 1048576.0:0.0} MB); ")
            + string.Create(inv, $"saved about {SavedMs / 1000:0.0} s of worker time");
    }
}

/// <summary>
/// The disk cache of laid-out foliage zones (<see cref="FoliageLayout.Place"/>'s result and the ground it was placed on): the layout is deterministic
/// (docs/formats/foliage.md, "Random numbers"), so a start with the same game data and code reads it back instead of placing a million meshes.
/// <para>
/// <c>%LOCALAPPDATA%\Meitou\foliage\&lt;key&gt;\z&lt;X&gt;.&lt;Y&gt;.&lt;w|f&gt;.mfl</c> (or the constructor's root; never in the repository),
/// one file per zone and layout kind (whole or far only). The key is a hash of everything the layout reads: the foliage records (every field of the
/// layers, meshes and grass types reached from the biomes), the towns, the size and write time of the heightmap, blend file, biome map and overlay
/// tiles, this format's version and <see cref="LayoutVersion"/>, bumped by hand when a code change alters what the layout places, so an edit
/// elsewhere keeps the cache (<see cref="FoliageLayoutCacheKey"/>). Settings (foliage and grass range, grass density) are not inputs of the layout
/// and are not in it.
/// </para>
/// <para>
/// A file is written to a temporary name and moved into place, carries its key, zone, sizes and a checksum, and is read whole: anything that does not
/// verify (truncated, other key, wrong zone, bad checksum, a mesh or layer id the catalog does not have) is a miss and is placed again. The folder is
/// kept in bounds by <see cref="Upkeep"/>. Thread-safe.
/// </para>
/// </summary>
public sealed class FoliageLayoutCache
{
    /// <summary>
    /// The file format's version: bump it when the layout of the bytes changes.
    /// </summary>
    public const int FormatVersion = 1;
    /// <summary>
    /// The layout's version: bump it whenever a change to the layout code (FoliageLayout, its random numbers, the readers it uses) changes what it
    /// places; a stale cache otherwise keeps serving the old placement.
    /// </summary>
    public const int LayoutVersion = 2;
    /// <summary>
    /// The first four bytes of a file.
    /// </summary>
    const uint Magic = 0x434C464D;   // "MFLC"
    /// <summary>
    /// The default of <see cref="MaxBytes"/>: 2 GB.
    /// </summary>
    public const long DefaultMaxBytes = 2048L << 20;

    /// <summary>
    /// The catalog the files' mesh and layer ids are looked up in.
    /// </summary>
    readonly FoliageCatalog _catalog;
    /// <summary>
    /// The folder of this key's files.
    /// </summary>
    readonly string _directory;
    /// <summary>
    /// The 32-byte key every file carries (<see cref="FoliageLayoutCacheKey"/>).
    /// </summary>
    readonly byte[] _key;
    /// <summary>
    /// The counts behind <see cref="Stats"/>.
    /// </summary>
    int _hits, _misses, _rejected, _written;
    /// <summary>
    /// The player-owned towns the key was made with; the cache is bypassed when the world's snapshot has a different number (a load after the cache was
    /// opened).
    /// </summary>
    readonly int _keyedOwned;
    /// <summary>
    /// The times and byte totals behind <see cref="Stats"/>.
    /// </summary>
    long _loadTicks, _computeTicks, _savedMicros, _bytesRead, _bytesWritten;

    /// <summary>
    /// A cache for <paramref name="install"/>'s layouts of <paramref name="catalog"/> with <paramref name="towns"/>, in its own key's folder under
    /// <paramref name="root"/> (default the local application data's <c>Meitou/foliage</c>), kept within <paramref name="maxBytes"/>.
    /// </summary>
    public FoliageLayoutCache(GameInstall install, FoliageCatalog catalog, IEnumerable<FoliageTown> towns, string? root = null, long? maxBytes = null)
    {
        this._catalog = catalog;
        var rootFolder = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "foliage");
        var list = towns as IReadOnlyCollection<FoliageTown> ?? [.. towns];
        _keyedOwned = list.Count(t => t.Owned);
        _key = FoliageLayoutCacheKey.Compute(install, catalog, list);
        var keyName = Convert.ToHexString(_key.AsSpan(0, 8)).ToLowerInvariant();
        _directory = Path.Combine(rootFolder, keyName);
        Upkeep = new FoliageLayoutCacheUpkeep(rootFolder, keyName, _directory, maxBytes ?? DefaultMaxBytes);
    }

    /// <summary>
    /// The catalog the files' mesh and layer ids are looked up in.
    /// </summary>
    public FoliageCatalog Catalog => _catalog;
    /// <summary>
    /// The pruning and eviction of the cache's folder.
    /// </summary>
    public FoliageLayoutCacheUpkeep Upkeep { get; }
    /// <summary>
    /// The folder holding one folder per key.
    /// </summary>
    public string Root => Upkeep.Root;
    /// <summary>
    /// The name of this key's folder: the key's first eight bytes in hex.
    /// </summary>
    public string KeyName => Upkeep.KeyName;
    /// <summary>
    /// The most the files may total in bytes; 0 or less is no cap.
    /// </summary>
    public long MaxBytes
    {
        get => Upkeep.MaxBytes;
        set => Upkeep.MaxBytes = value;
    }

    /// <summary>
    /// The folder of this key's files.
    /// </summary>
    public string Directory => _directory;

    /// <summary>
    /// What the cache did so far.
    /// </summary>
    public FoliageLayoutCacheStats Stats => new(_hits, _misses, _rejected, _written, _loadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
        _computeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, Interlocked.Read(ref _savedMicros) / 1000.0, Interlocked.Read(ref _bytesRead),
        Interlocked.Read(ref _bytesWritten));

    /// <summary>
    /// The file of a zone and layout kind.
    /// </summary>
    public string PathFor(ZoneCoordinate zone, bool farOnly) => Path.Combine(_directory, $"z{zone.X}.{zone.Y}.{(farOnly ? 'f' : 'w')}.mfl");

    /// <summary>
    /// The world's player-owned towns differ from the ones the key was made with: the cache is bypassed.
    /// </summary>
    bool Stale => FoliageWorld.PlayerTowns.Count != _keyedOwned;

    #region Reading

    /// <summary>
    /// The cached zone, or null (a miss: absent, or unusable, which is also counted in <see cref="FoliageLayoutCacheStats.Rejected"/>).
    /// </summary>
    public (FoliageZone Zone, FoliageGround Ground)? TryLoad(ZoneCoordinate zone, bool farOnly) =>
        Fetch(zone, farOnly, grouped: false) is { } f ? (f.Zone, f.Ground) : null;

    /// <summary>
    /// Like <see cref="TryLoad"/>, with the instances already grouped for the renderer (<see cref="GroupedFoliageZone"/>; its zone's instance list is
    /// empty): the records are built straight from the file's, so the zone's 56-byte-per-instance list and the copies grouping it made are
    /// never allocated.
    /// </summary>
    public GroupedFoliageZone? TryLoadGrouped(ZoneCoordinate zone, bool farOnly) => Fetch(zone, farOnly, grouped: true)?.Grouped;

    /// <summary>
    /// Parses and verifies one file's bytes; null when it is not a valid entry for this key, zone and kind.
    /// </summary>
    public (FoliageZone Zone, FoliageGround Ground, float ComputeMs)? Decode(byte[] bytes, ZoneCoordinate zone, bool farOnly) =>
        Decode(bytes, bytes.Length, zone, farOnly, grouped: false) is { } d ? (d.Zone, d.Ground, d.ComputeMs) : null;

    /// <summary>
    /// A decoded file: the zone, its ground, the time its layout took, and the instances grouped when asked for.
    /// </summary>
    readonly record struct Decoded(FoliageZone Zone, FoliageGround Ground, float ComputeMs, GroupedFoliageZone? Grouped);

    /// <summary>
    /// Reads and decodes a zone's file, counting the hit or miss.
    /// </summary>
    Decoded? Fetch(ZoneCoordinate zone, bool farOnly, bool grouped)
    {
        if (LoadCaches.Disabled || Stale) return null;
        var path = PathFor(zone, farOnly);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[]? bytes = null;
        int length;
        try
        {
            if (!File.Exists(path)) return Miss(false);
            (bytes, length) = ReadFile(path);
        }
        catch (IOException)
        {
            return Miss(true);
        }
        catch (UnauthorizedAccessException)
        {
            return Miss(true);
        }

        Decoded? result;
        try
        {
            result = Decode(bytes, length, zone, farOnly, grouped);
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidDataException or KeyNotFoundException
            or OverflowException or EndOfStreamException)
        {
            result = null;
        }
        if (result is null) return Miss(true);

        CountHit(length, result.Value.ComputeMs, t0);
        return result;
    }

    /// <summary>
    /// Counts a miss (<paramref name="bad"/>: an unusable file, also counted as rejected); returns null.
    /// </summary>
    Decoded? Miss(bool bad)
    {
        Interlocked.Increment(ref _misses);
        if (bad) Interlocked.Increment(ref _rejected);
        return null;
    }

    /// <summary>
    /// Counts a hit of <paramref name="length"/> bytes started at <paramref name="t0"/>: the layout time it saved, less the time loading it took.
    /// </summary>
    void CountHit(int length, float computeMs, long t0)
    {
        Interlocked.Increment(ref _hits);
        Upkeep.TouchOnce();
        Interlocked.Add(ref _bytesRead, length);
        Interlocked.Add(ref _savedMicros, (long)(computeMs * 1000));
        long took = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        Interlocked.Add(ref _loadTicks, took);
        Interlocked.Add(ref _savedMicros, -(long)(took * 1e6 / System.Diagnostics.Stopwatch.Frequency));
    }

    /// <summary>
    /// The whole file in a fresh buffer. A file is 300 KB on average; the shared ArrayPool keeps such arrays for the
    /// life of the worker threads, which adds up to gigabytes over a tour of the world's zones. The decode copies
    /// everything it keeps, so the file bytes can go back to the GC immediately.
    /// </summary>
    static (byte[] Buffer, int Length) ReadFile(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        long size = RandomAccess.GetLength(file);
        if (size > int.MaxValue) throw new IOException("cache file too large");
        var buffer = GC.AllocateUninitializedArray<byte>((int)size);
        int read = 0;
        while (read < size)
        {
            int n = RandomAccess.Read(file, buffer.AsSpan(read, (int)size - read), read);
            if (n == 0) break;
            read += n;
        }
        return (buffer, read);   // a file that shrank meanwhile fails its checksum
    }

    /// <summary>
    /// Parses and verifies the first <paramref name="fileLength"/> bytes; null when they are not a valid entry for this key, zone and kind. A short
    /// read throws (<see cref="InvalidDataException"/>), as does an id the catalog lacks.
    /// </summary>
    Decoded? Decode(byte[] bytes, int fileLength, ZoneCoordinate zone, bool farOnly, bool grouped)
    {
        const int header = 4 + 4 + 32 + 4 + 4 + 1 + 1 + 4 + 4;
        if (fileLength < header + 8) return null;
        int end = fileLength - 8;
        var data = bytes.AsSpan(0, end);
        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(fileLength - 8)) != Checksum(data)) return null;

        var file = new FoliageLayoutFileCursor(bytes, end);
        if (!ReadHeader(ref file, zone, farOnly)) return null;
        bool complete = file.ReadU8() != 0;
        int resources = file.ReadI32();
        float computeMs = BitConverter.Int32BitsToSingle(file.ReadI32());
        var (meshes, layers) = ReadCatalogEntries(ref file);
        var ground = ReadGround(ref file);

        // The grass patches and the density maps they share.
        int densityCount = file.ReadCount(), patchCount = file.ReadCount();
        var densities = ReadDensities(ref file, densityCount);
        var result = new FoliageZone { Zone = zone, Resources = resources, Complete = complete };
        ReadPatches(ref file, result, layers, densities, patchCount);

        var disk = ReadInstances(ref file);
        if (file.At != end) return null;
        if (grouped) return new Decoded(result, ground, computeMs, GroupDisk(result, ground, disk, meshes, layers));
        AddInstances(result, disk, meshes, layers);
        return new Decoded(result, ground, computeMs, null);
    }

    /// <summary>
    /// The magic, format version, key, zone and layout kind; false when any differs from this cache's.
    /// </summary>
    bool ReadHeader(ref FoliageLayoutFileCursor file, ZoneCoordinate zone, bool farOnly)
    {
        if (file.ReadU32() != Magic || file.ReadI32() != FormatVersion) return false;
        if (!file.Take(32).SequenceEqual(_key)) return false;
        if (file.ReadI32() != zone.X || file.ReadI32() != zone.Y) return false;
        return (file.ReadU8() != 0) == farOnly;
    }

    /// <summary>
    /// The file's mesh and layer id lists, looked up in the catalog (an unknown id throws <see cref="KeyNotFoundException"/>).
    /// </summary>
    (FoliageMesh[] Meshes, FoliageLayer[] Layers) ReadCatalogEntries(ref FoliageLayoutFileCursor file)
    {
        var meshIds = file.ReadStrings();
        var layerIds = file.ReadStrings();
        var meshes = new FoliageMesh[meshIds.Length];
        for (int i = 0; i < meshes.Length; i++) meshes[i] = _catalog.Meshes[meshIds[i]];
        var layers = new FoliageLayer[layerIds.Length];
        for (int i = 0; i < layers.Length; i++) layers[i] = _catalog.Layers[layerIds[i]];
        return (meshes, layers);
    }

    /// <summary>
    /// The ground: its origin and the height grid, copied out in bulk.
    /// </summary>
    static FoliageGround ReadGround(ref FoliageLayoutFileCursor file)
    {
        float gx = file.ReadF32(), gz = file.ReadF32();
        var heights = ZoneArrays.Uninitialized<float>(FoliageGround.Size * FoliageGround.Size);
        MemoryMarshal.Cast<byte, float>(file.Take(heights.Length * 4)).CopyTo(heights);
        return new FoliageGround(gx, gz, heights);
    }

    /// <summary>
    /// The density maps the grass patches share, each copied out of the file.
    /// </summary>
    static byte[][] ReadDensities(ref FoliageLayoutFileCursor file, int densityCount)
    {
        var densities = new byte[densityCount][];
        for (int i = 0; i < densityCount; i++)
        {
            int length = file.ReadCount();
            var map = file.Take(length);
            densities[i] = ZoneArrays.Uninitialized<byte>(length);
            map.CopyTo(densities[i]);
        }
        return densities;
    }

    /// <summary>
    /// The grass patches into the zone, each naming its layer, the layer's grass type and its shared density map by index.
    /// </summary>
    static void ReadPatches(ref FoliageLayoutFileCursor file, FoliageZone result, FoliageLayer[] layers, byte[][] densities, int patchCount)
    {
        for (int i = 0; i < patchCount; i++)
        {
            var layer = layers[file.ReadU16()];
            int grassIndex = file.ReadU16(), channel = file.ReadI32(), density = file.ReadI32();
            float x0 = file.ReadF32(), z0 = file.ReadF32(), x1 = file.ReadF32(), z1 = file.ReadF32();
            result.Grass.Add(new FoliageGrassPatch(layer, layer.Grass[grassIndex].Grass, channel, densities[density], x0, z0, x1, z1));
        }
    }

    /// <summary>
    /// The instances: fixed-size records, read in bulk (a view of the file's bytes, nothing copied).
    /// </summary>
    static ReadOnlySpan<InstanceOnDisk> ReadInstances(ref FoliageLayoutFileCursor file)
    {
        int count = file.ReadCount();
        int size = Unsafe_SizeOf();
        return MemoryMarshal.Cast<byte, InstanceOnDisk>(file.Take(checked(count * size)));
    }

    /// <summary>
    /// The file's instance records into the zone's instance list.
    /// </summary>
    static void AddInstances(FoliageZone result, ReadOnlySpan<InstanceOnDisk> disk, FoliageMesh[] meshes, FoliageLayer[] layers)
    {
        var list = result.Instances;
        list.Capacity = disk.Length;
        foreach (ref readonly var d in disk)
        {
            list.Add(new FoliageInstance(meshes[d.Mesh], layers[d.Layer], new Vector3(d.X, d.Y, d.Z), d.Scale, d.Yaw,
                new Quaternion(d.Qx, d.Qy, d.Qz, d.Qw)));
        }
    }

    /// <summary>
    /// The file's records grouped as <see cref="FoliageGrouping.Group"/> groups the instances read from them (same groups in the same order,
    /// bit-identical records), in two passes over the file's span: count the instances of each (mesh, layer), then fill exact-size arrays.
    /// </summary>
    static GroupedFoliageZone GroupDisk(FoliageZone zone, FoliageGround ground, ReadOnlySpan<InstanceOnDisk> disk, FoliageMesh[] meshes,
        FoliageLayer[] layers)
    {
        var slotOf = new Dictionary<int, int>();
        var keys = new List<int>();
        var counts = new List<int>();
        CountGroups(disk, meshes, layers, slotOf, keys, counts);

        var records = new FoliageInstanceRecord[keys.Count][];
        var maxScale = new float[keys.Count];
        var filled = new int[keys.Count];
        for (int g = 0; g < records.Length; g++) records[g] = ZoneArrays.Uninitialized<FoliageInstanceRecord>(counts[g]);
        var (minY, maxY) = FillGroups(disk, slotOf, records, maxScale, filled);

        var groups = new List<FoliageInstanceGroup>(keys.Count);
        for (int g = 0; g < keys.Count; g++)
            groups.Add(new FoliageInstanceGroup(meshes[(int)((uint)keys[g] >> 16)], layers[keys[g] & 0xFFFF], records[g], maxScale[g]));
        return new GroupedFoliageZone
        {
            Zone = zone,
            Ground = ground,
            Groups = groups,
            InstanceCount = disk.Length,
            MinY = disk.Length == 0 ? 0 : minY,
            MaxY = disk.Length == 0 ? 0 : maxY,
        };
    }

    /// <summary>
    /// The first pass: each (mesh, layer) key in order of first use with its slot, and the instances of each slot.
    /// </summary>
    static void CountGroups(ReadOnlySpan<InstanceOnDisk> disk, FoliageMesh[] meshes, FoliageLayer[] layers, Dictionary<int, int> slotOf, List<int> keys,
        List<int> counts)
    {
        int lastKey = -1, lastSlot = 0;
        foreach (ref readonly var d in disk)
        {
            int key = d.Mesh << 16 | d.Layer;
            if (key == lastKey)
            {
                counts[lastSlot]++;
                continue;
            }
            if (!slotOf.TryGetValue(key, out lastSlot))
            {
                _ = (meshes[d.Mesh], layers[d.Layer]);   // an index the file's own list lacks throws, as decoding the instances does
                lastSlot = keys.Count;
                slotOf[key] = lastSlot;
                keys.Add(key);
                counts.Add(0);
            }
            lastKey = key;
            counts[lastSlot]++;
        }
    }

    /// <summary>
    /// The second pass: each instance's record into its slot's array, the slots' largest scales; returns the instances' lowest and highest Y.
    /// </summary>
    static (float MinY, float MaxY) FillGroups(ReadOnlySpan<InstanceOnDisk> disk, Dictionary<int, int> slotOf, FoliageInstanceRecord[][] records,
        float[] maxScale, int[] filled)
    {
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        int lastKey = -1, lastSlot = 0;
        foreach (ref readonly var d in disk)
        {
            int key = d.Mesh << 16 | d.Layer;
            if (key != lastKey)
            {
                lastSlot = slotOf[key];
                lastKey = key;
            }
            int at = filled[lastSlot]++;
            records[lastSlot][at] = new FoliageInstanceRecord
            {
                Transform = FoliageInstance.TransformOf(d.Scale, new Quaternion(d.Qx, d.Qy, d.Qz, d.Qw), new Vector3(d.X, d.Y, d.Z)),
                Ground = new Vector4(d.X, d.Z, d.Scale, lastSlot),
            };
            maxScale[lastSlot] = at == 0 ? d.Scale : Math.Max(maxScale[lastSlot], d.Scale);
            minY = Math.Min(minY, d.Y);
            maxY = Math.Max(maxY, d.Y);
        }
        return (minY, maxY);
    }

    #endregion

    #region Writing

    /// <summary>
    /// Records the time a miss took to place (for the summary) and writes the zone; failures are ignored, the cache is only a cache.
    /// </summary>
    public void Save(ZoneCoordinate zone, bool farOnly, FoliageZone laid, FoliageGround ground, double computeMs)
    {
        if (LoadCaches.Disabled || Stale) return;
        Interlocked.Add(ref _computeTicks, (long)(computeMs * System.Diagnostics.Stopwatch.Frequency / 1000));
        try
        {
            var bytes = Encode(laid, ground, farOnly, (float)computeMs);
            if (bytes is null) return;
            System.IO.Directory.CreateDirectory(_directory);
            var path = PathFor(zone, farOnly);
            var temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "."
                + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            Interlocked.Increment(ref _written);
            Interlocked.Add(ref _bytesWritten, bytes.Length);
            Upkeep.TouchOnce();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The file for a zone; null when the zone cannot be written back faithfully (an unknown reference).
    /// </summary>
    public byte[]? Encode(FoliageZone laid, FoliageGround ground, bool farOnly, float computeMs)
    {
        var meshIndex = new Dictionary<FoliageMesh, int>();
        var layerIndex = new Dictionary<FoliageLayer, int>();
        if (!IndexReferences(laid, meshIndex, layerIndex)) return null;
        var (densities, patchDensity) = ShareDensities(laid);

        var heights = ground.Heights;
        int size = Unsafe_SizeOf();
        using var ms = new MemoryStream(1024 + heights.Length * 4 + densities.Sum(d => d.Length) + laid.Instances.Count * size + laid.Grass.Count * 40);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            WriteHeader(w, laid, farOnly, computeMs);
            WriteStrings(w, meshIndex.OrderBy(p => p.Value).Select(p => p.Key.StringId));
            WriteStrings(w, layerIndex.OrderBy(p => p.Value).Select(p => p.Key.StringId));
            w.Write(ground.X0);
            w.Write(ground.Z0);
            w.Write(MemoryMarshal.AsBytes(heights));
            if (!WriteGrass(w, laid, layerIndex, densities, patchDensity)) return null;
            WriteInstances(w, laid, meshIndex, layerIndex);
        }
        return WithChecksum(ms);
    }

    /// <summary>
    /// Numbers the meshes and layers the zone uses in order of first use; false when one is not the catalog's own object (what is read back is looked
    /// up by id in the catalog) or there are more than a 16-bit index holds.
    /// </summary>
    bool IndexReferences(FoliageZone laid, Dictionary<FoliageMesh, int> meshIndex, Dictionary<FoliageLayer, int> layerIndex)
    {
        foreach (var i in laid.Instances)
        {
            if (!meshIndex.ContainsKey(i.Mesh)) meshIndex[i.Mesh] = meshIndex.Count;
            if (!layerIndex.ContainsKey(i.Layer)) layerIndex[i.Layer] = layerIndex.Count;
        }
        foreach (var p in laid.Grass) if (!layerIndex.ContainsKey(p.Layer)) layerIndex[p.Layer] = layerIndex.Count;

        // What is read back is looked up by id in the catalog: it must be the same object.
        foreach (var m in meshIndex.Keys) if (!_catalog.Meshes.TryGetValue(m.StringId, out var c) || !ReferenceEquals(c, m)) return false;
        foreach (var l in layerIndex.Keys) if (!_catalog.Layers.TryGetValue(l.StringId, out var c) || !ReferenceEquals(c, l)) return false;
        return meshIndex.Count <= ushort.MaxValue && layerIndex.Count <= ushort.MaxValue;
    }

    /// <summary>
    /// The distinct density maps of the zone's grass patches (equal contents written once) and each patch's index among them.
    /// </summary>
    static (List<byte[]> Densities, int[] PatchDensity) ShareDensities(FoliageZone laid)
    {
        var densities = new List<byte[]>();
        var patchDensity = new int[laid.Grass.Count];
        for (int i = 0; i < patchDensity.Length; i++)
        {
            var d = laid.Grass[i].Density;
            int found = densities.FindIndex(x => x.AsSpan().SequenceEqual(d));
            if (found < 0)
            {
                found = densities.Count;
                densities.Add(d);
            }
            patchDensity[i] = found;
        }
        return (densities, patchDensity);
    }

    /// <summary>
    /// The magic, format version, key, zone, layout kind, completeness, resources and layout time.
    /// </summary>
    void WriteHeader(BinaryWriter w, FoliageZone laid, bool farOnly, float computeMs)
    {
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(_key);
        w.Write(laid.Zone.X);
        w.Write(laid.Zone.Y);
        w.Write((byte)(farOnly ? 1 : 0));
        w.Write((byte)(laid.Complete ? 1 : 0));
        w.Write(laid.Resources);
        w.Write(BitConverter.SingleToInt32Bits(computeMs));
    }

    /// <summary>
    /// A string list: a 16-bit count, then each string's 16-bit byte length and UTF-8 bytes.
    /// </summary>
    static void WriteStrings(BinaryWriter w, IEnumerable<string> ids)
    {
        var list = ids.ToList();
        w.Write((ushort)list.Count);
        foreach (var s in list)
        {
            var b = Encoding.UTF8.GetBytes(s);
            w.Write((ushort)b.Length);
            w.Write(b);
        }
    }

    /// <summary>
    /// The density maps, then the grass patches; false when a patch's grass is not one of its layer's.
    /// </summary>
    static bool WriteGrass(BinaryWriter w, FoliageZone laid, Dictionary<FoliageLayer, int> layerIndex, List<byte[]> densities, int[] patchDensity)
    {
        w.Write(densities.Count);
        w.Write(laid.Grass.Count);
        foreach (var d in densities)
        {
            w.Write(d.Length);
            w.Write(d);
        }
        for (int i = 0; i < laid.Grass.Count; i++)
        {
            var p = laid.Grass[i];
            int g = -1;
            for (int k = 0; k < p.Layer.Grass.Count; k++)
            {
                if (!ReferenceEquals(p.Layer.Grass[k].Grass, p.Grass)) continue;
                g = k;
                break;
            }
            if (g < 0) return false;
            w.Write((ushort)layerIndex[p.Layer]);
            w.Write((ushort)g);
            w.Write(p.Channel);
            w.Write(patchDensity[i]);
            w.Write(p.X0);
            w.Write(p.Z0);
            w.Write(p.X1);
            w.Write(p.Z1);
        }
        return true;
    }

    /// <summary>
    /// The instance count, then the instances as fixed-size records in one block.
    /// </summary>
    static void WriteInstances(BinaryWriter w, FoliageZone laid, Dictionary<FoliageMesh, int> meshIndex, Dictionary<FoliageLayer, int> layerIndex)
    {
        w.Write(laid.Instances.Count);
        var records = new InstanceOnDisk[laid.Instances.Count];
        for (int i = 0; i < records.Length; i++)
        {
            var n = laid.Instances[i];
            records[i] = new InstanceOnDisk
            {
                Mesh = (ushort)meshIndex[n.Mesh],
                Layer = (ushort)layerIndex[n.Layer],
                X = n.Position.X,
                Y = n.Position.Y,
                Z = n.Position.Z,
                Scale = n.Scale,
                Yaw = n.YawDegrees,
                Qx = n.Orientation.X,
                Qy = n.Orientation.Y,
                Qz = n.Orientation.Z,
                Qw = n.Orientation.W,
            };
        }
        w.Write(MemoryMarshal.AsBytes(records.AsSpan()));
    }

    /// <summary>
    /// The written bytes with their checksum appended.
    /// </summary>
    static byte[] WithChecksum(MemoryStream ms)
    {
        ms.TryGetBuffer(out var buffer);
        var total = new byte[buffer.Count + 8];
        buffer.AsSpan().CopyTo(total);
        BinaryPrimitives.WriteUInt64LittleEndian(total.AsSpan(buffer.Count), Checksum(total.AsSpan(0, buffer.Count)));
        return total;
    }

    #endregion

    #region File format

    /// <summary>
    /// One instance as the file stores it: mesh and layer index, position, scale, yaw in degrees and orientation.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct InstanceOnDisk
    {
        /// <summary>
        /// The indices into the file's mesh and layer lists.
        /// </summary>
        public ushort Mesh, Layer;
        /// <summary>
        /// Position, scale, yaw (degrees) and orientation quaternion.
        /// </summary>
        public float X, Y, Z, Scale, Yaw, Qx, Qy, Qz, Qw;
    }

    /// <summary>
    /// The size of an <see cref="InstanceOnDisk"/> record in bytes.
    /// </summary>
    static int Unsafe_SizeOf() => System.Runtime.CompilerServices.Unsafe.SizeOf<InstanceOnDisk>();

    /// <summary>
    /// A fast 64-bit checksum (eight bytes at a time, multiply-rotate); it only has to notice damage, not resist an attacker.
    /// </summary>
    internal static ulong Checksum(ReadOnlySpan<byte> data)
    {
        const ulong p1 = 0x9E3779B185EBCA87UL, p2 = 0xC2B2AE3D27D4EB4FUL;
        ulong h = 0x165667B19E3779F9UL ^ (ulong)data.Length;
        var words = MemoryMarshal.Cast<byte, ulong>(data);
        foreach (var w in words) h = BitOperations.RotateLeft((h ^ (w * p2)) * p1, 31) + p1;
        for (int i = words.Length * 8; i < data.Length; i++) h = BitOperations.RotateLeft((h ^ data[i]) * p1, 11) * p2;
        h ^= h >> 33;
        h *= p2;
        h ^= h >> 29;
        return h;
    }

    #endregion
}
