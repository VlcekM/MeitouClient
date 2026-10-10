using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Meitou.Content;

namespace Meitou.Data.World;

/// <summary>Switches for the derived-data load caches (<c>--no-load-cache</c>, <c>MEITOU_NO_LOAD_CACHE=1</c>): when set nothing is read from or written to them.</summary>
public static class LoadCaches
{
    public static bool Disabled { get; set; } = Environment.GetEnvironmentVariable("MEITOU_NO_LOAD_CACHE") == "1";
}

/// <summary>What the foliage layout cache did so far (a snapshot of <see cref="FoliageLayoutCache.Stats"/>).</summary>
public readonly record struct FoliageLayoutCacheStats(int Hits, int Misses, int Rejected, int Written, double LoadMs, double ComputeMs, double SavedMs, long BytesRead, long BytesWritten)
{
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var odd = Rejected > 0 ? string.Create(inv, $" ({Rejected} unusable entries recomputed)") : "";
        return string.Create(inv, $"layout cache {Hits} hits, {Misses} misses{odd}; hits loaded in {LoadMs:0} ms ({BytesRead / 1048576.0:0.0} MB), misses computed in {ComputeMs:0} ms, {Written} written ({BytesWritten / 1048576.0:0.0} MB); saved about {SavedMs / 1000:0.0} s of worker time");
    }
}

/// <summary>
/// The disk cache of laid-out foliage zones (<see cref="FoliageLayout.Place"/>'s result and the ground it was placed on): the layout is deterministic
/// (docs/formats/foliage.md, "Random numbers"), so a start with the same game data and code reads it back instead of placing a million meshes.
/// <para>
/// <c>%LOCALAPPDATA%\Meitou\foliage\&lt;key&gt;\z&lt;X&gt;.&lt;Y&gt;.&lt;w|f&gt;.mfl</c> (overridden by <c>MEITOU_FOLIAGE_CACHE</c> or the constructor; never in the repository),
/// one file per zone and layout kind (whole or far only). The key is a hash of everything the layout reads: the foliage records (every field of the
/// layers, meshes and grass types reached from the biomes), the towns, the size and write time of the heightmap, blend file, biome map and overlay
/// tiles, this format's version and a hash of the source of the layout's code (Meitou.Data and Meitou.Core, <see cref="LayoutCodeHash"/>, generated at build time), so any change to
/// the layout code or its inputs makes a new key, and the same sources give the same key in any directory or build. Settings (foliage and grass range, grass density) are not inputs of the layout and are not in it.
/// </para>
/// <para>
/// A file is written to a temporary name and moved into place, carries its key, zone, sizes and a checksum, and is read whole: anything that does not
/// verify (truncated, other key, wrong zone, bad checksum, a mesh or layer id the catalog does not have) is a miss and is placed again.
/// Thread-safe.
/// </para>
/// </summary>
public sealed class FoliageLayoutCache
{
    /// <summary>The file format's version: bump it when the layout of the bytes changes.</summary>
    public const int FormatVersion = 1;
    const uint Magic = 0x434C464D;   // "MFLC"
    public const long DefaultMaxBytes = 2048L << 20;
    static readonly TimeSpan AbandonedTemporary = TimeSpan.FromHours(1), StaleKey = TimeSpan.FromDays(30);

    readonly FoliageCatalog catalog;
    public FoliageCatalog Catalog => catalog;
    readonly string directory;
    readonly byte[] key;
    int hits, misses, rejected, written, maintained;
    long loadTicks, computeTicks, savedMicros, bytesRead, bytesWritten;

    public FoliageLayoutCache(GameInstall install, FoliageCatalog catalog, IEnumerable<(Vector2 At, float Range)> towns, string? root = null, long? maxBytes = null)
    {
        this.catalog = catalog;
        Root = root ?? Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_CACHE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "foliage");
        MaxBytes = maxBytes ?? (double.TryParse(Environment.GetEnvironmentVariable("MEITOU_FOLIAGE_CACHE_MB"), NumberStyles.Float, CultureInfo.InvariantCulture, out double mb) ? (long)(mb * 1048576) : DefaultMaxBytes);
        key = ComputeKey(install, catalog, towns);
        KeyName = Convert.ToHexString(key.AsSpan(0, 8)).ToLowerInvariant();
        directory = Path.Combine(Root, KeyName);
    }

    public string Root { get; }
    public string KeyName { get; }
    /// <summary>The most the files may total in bytes; 0 or less is no cap.</summary>
    public long MaxBytes { get; set; }
    public string Directory => directory;

    public FoliageLayoutCacheStats Stats => new(hits, misses, rejected, written, loadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, computeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
        Interlocked.Read(ref savedMicros) / 1000.0, Interlocked.Read(ref bytesRead), Interlocked.Read(ref bytesWritten));

    public string PathFor(ZoneCoordinate zone, bool farOnly) => Path.Combine(directory, $"z{zone.X}.{zone.Y}.{(farOnly ? 'f' : 'w')}.mfl");

    // ------------------------------------------------------------------ key

    /// <summary>The 32-byte key (see the class summary).</summary>
    public static byte[] ComputeKey(GameInstall install, FoliageCatalog catalog, IEnumerable<(Vector2 At, float Range)> towns)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var text = new StringBuilder();
        void Flush()
        {
            hash.AppendData(Encoding.UTF8.GetBytes(text.ToString()));
            text.Clear();
        }
        var inv = CultureInfo.InvariantCulture;
        text.Append("meitou-foliage-layout-cache|").Append(FormatVersion).Append('\n');
        // The code: a hash of the source of Meitou.Data and Meitou.Core (everything the layout runs), generated at build time (LayoutCodeHash.targets): the same for the same
        // sources in any directory or build, unlike the assemblies' module ids.
        text.Append("code|").Append(LayoutCodeHash.Value).Append('\n');
        // The records: biomes in colour order, their layers, each layer's meshes (with children) and grass types, field by field.
        var done = new HashSet<Meitou.Data.GameRecord>();
        foreach (var (colour, layers) in catalog.ByBiome.OrderBy(b => b.Key))
        {
            text.Append("biome|").Append(colour).Append('|').Append(layers.Count).Append('\n');
            foreach (var layer in layers)
            {
                text.Append("layer|").Append(layer.StringId).Append('\n');
                if (layer.Record is { } lr && done.Add(lr)) AppendRecord(text, lr);
                foreach (var (mesh, count) in layer.Meshes) { text.Append("m|").Append(count).Append('\n'); AppendMesh(text, mesh, done); }
                foreach (var (grass, channel) in layer.Grass)
                {
                    text.Append("g|").Append(channel).Append('\n');
                    if (grass.Record is { } gr) AppendRecord(text, gr); else text.Append(grass.StringId).Append('\n');
                }
            }
            Flush();
        }
        foreach (var (at, range) in towns) text.Append("town|").Append(at.X.ToString("R", inv)).Append('|').Append(at.Y.ToString("R", inv)).Append('|').Append(range.ToString("R", inv)).Append('\n');
        // The files the layout reads, by size and write time.
        void File(string path, string name)
        {
            var info = new FileInfo(path);
            text.Append("file|").Append(name).Append('|').Append(info.Exists ? info.Length : -1).Append('|').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }
        File(Path.Combine(install.DataDirectory, TerrainHeightmap.RelativePath), "heightmap");
        File(Path.Combine(install.DataDirectory, BlendInfoFile.RelativePath), "blend");
        File(Path.Combine(install.DataDirectory, TerrainMaps.BiomeMap), "biomemap");
        var overlays = Path.Combine(install.DataDirectory, TerrainMaps.LandDirectory, "overlaymaps");
        if (System.IO.Directory.Exists(overlays))
            foreach (var f in System.IO.Directory.EnumerateFiles(overlays, "new_overlay.*.png").OrderBy(f => f, StringComparer.Ordinal)) File(f, Path.GetFileName(f));
        Flush();
        return hash.GetHashAndReset();
    }

    static void AppendMesh(StringBuilder text, FoliageMesh mesh, HashSet<Meitou.Data.GameRecord> done)
    {
        if (!done.Add(mesh.Record)) { text.Append("again|").Append(mesh.StringId).Append('\n'); return; }
        AppendRecord(text, mesh.Record);
        foreach (var child in mesh.Children) AppendMesh(text, child, done);
        if (mesh.BuildingType is { } b) text.Append("building|").Append(b.StringId).Append('\n');
    }

    static void AppendRecord(StringBuilder text, Meitou.Data.GameRecord r)
    {
        var inv = CultureInfo.InvariantCulture;
        text.Append("rec|").Append(r.StringId).Append('|').Append((int)r.Type).Append('|').Append(r.Name).Append('\n');
        foreach (var (k, v) in r.Bools.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append('b').Append(k).Append('=').Append(v ? 1 : 0).Append('\n');
        foreach (var (k, v) in r.Floats.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append('f').Append(k).Append('=').Append(BitConverter.SingleToInt32Bits(v).ToString(inv)).Append('\n');
        foreach (var (k, v) in r.Ints.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append('i').Append(k).Append('=').Append(v).Append('\n');
        foreach (var (k, v) in r.Vector3s.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append('v').Append(k).Append('=').Append(v.X.ToString("R", inv)).Append(',').Append(v.Y.ToString("R", inv)).Append(',').Append(v.Z.ToString("R", inv)).Append('\n');
        foreach (var (k, v) in r.Vector4s.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append('w').Append(k).Append('=').Append(v.X.ToString("R", inv)).Append(',').Append(v.Y.ToString("R", inv)).Append(',').Append(v.Z.ToString("R", inv)).Append(',').Append(v.W.ToString("R", inv)).Append('\n');
        foreach (var (k, v) in r.Strings.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append('s').Append(k).Append('=').Append(v).Append('\n');
        foreach (var (k, v) in r.Filenames.OrderBy(p => p.Key, StringComparer.Ordinal)) text.Append('p').Append(k).Append('=').Append(v).Append('\n');
        foreach (var list in r.ReferenceLists.OrderBy(l => l, StringComparer.Ordinal))
        {
            text.Append('r').Append(list).Append('\n');
            foreach (var x in r.GetReferences(list)) text.Append(' ').Append(x.TargetStringId).Append(':').Append(x.Values.Value0).Append(',').Append(x.Values.Value1).Append(',').Append(x.Values.Value2).Append('\n');
        }
    }

    // ------------------------------------------------------------------ read

    /// <summary>The cached zone, or null (a miss: absent, or unusable, which is also counted in <see cref="FoliageLayoutCacheStats.Rejected"/>).</summary>
    public (FoliageZone Zone, FoliageGround Ground)? TryLoad(ZoneCoordinate zone, bool farOnly) =>
        Fetch(zone, farOnly, grouped: false) is { } f ? (f.Zone, f.Ground) : null;

    /// <summary>
    /// Like <see cref="TryLoad"/>, with the instances already grouped for the renderer (<see cref="GroupedFoliageZone"/>; its zone's instance list is empty): the
    /// records are built straight from the file's, so the zone's 56-byte-per-instance list and the copies grouping it made are never allocated.
    /// </summary>
    public GroupedFoliageZone? TryLoadGrouped(ZoneCoordinate zone, bool farOnly) => Fetch(zone, farOnly, grouped: true)?.Grouped;

    readonly record struct Decoded(FoliageZone Zone, FoliageGround Ground, float ComputeMs, GroupedFoliageZone? Grouped);

    Decoded? Fetch(ZoneCoordinate zone, bool farOnly, bool grouped)
    {
        if (LoadCaches.Disabled) return null;
        var path = PathFor(zone, farOnly);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[]? bytes = null;
        try
        {
            int length;
            try
            {
                if (!System.IO.File.Exists(path)) return Miss(false);
                (bytes, length) = ReadPooled(path);
            }
            catch (IOException) { return Miss(true); }
            catch (UnauthorizedAccessException) { return Miss(true); }
            Decoded? result;
            try { result = Decode(bytes, length, zone, farOnly, grouped); }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidDataException or KeyNotFoundException or OverflowException or EndOfStreamException) { result = null; }
            if (result is null) return Miss(true);
            Interlocked.Increment(ref hits);
            TouchOnce();
            Interlocked.Add(ref bytesRead, length);
            Interlocked.Add(ref savedMicros, (long)(result.Value.ComputeMs * 1000));
            long took = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            Interlocked.Add(ref loadTicks, took);
            Interlocked.Add(ref savedMicros, -(long)(took * 1e6 / System.Diagnostics.Stopwatch.Frequency));
            return result;
        }
        finally
        {
            // Nothing decoded refers to the file's bytes (strings, heights, densities and records are copies).
            if (bytes is not null) System.Buffers.ArrayPool<byte>.Shared.Return(bytes);
        }

        Decoded? Miss(bool bad)
        {
            Interlocked.Increment(ref misses);
            if (bad) Interlocked.Increment(ref rejected);
            return null;
        }
    }

    /// <summary>The whole file in a pooled buffer (a file is 300 KB on average, a large object the size of a zone's records; files over 1 MB are not pooled by the shared pool).</summary>
    static (byte[] Buffer, int Length) ReadPooled(string path)
    {
        using var file = System.IO.File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        long size = RandomAccess.GetLength(file);
        if (size > int.MaxValue) throw new IOException("cache file too large");
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)size);
        try
        {
            int read = 0;
            while (read < size)
            {
                int n = RandomAccess.Read(file, buffer.AsSpan(read, (int)size - read), read);
                if (n == 0) break;
                read += n;
            }
            return (buffer, read);   // a file that shrank meanwhile fails its checksum
        }
        catch
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>Parses and verifies one file's bytes; null when it is not a valid entry for this key, zone and kind.</summary>
    public (FoliageZone Zone, FoliageGround Ground, float ComputeMs)? Decode(byte[] bytes, ZoneCoordinate zone, bool farOnly) =>
        Decode(bytes, bytes.Length, zone, farOnly, grouped: false) is { } d ? (d.Zone, d.Ground, d.ComputeMs) : null;

    Decoded? Decode(byte[] bytes, int fileLength, ZoneCoordinate zone, bool farOnly, bool grouped)
    {
        const int Header = 4 + 4 + 32 + 4 + 4 + 1 + 1 + 4 + 4;
        if (fileLength < Header + 8) return null;
        int end = fileLength - 8;
        var data = bytes.AsSpan(0, end);
        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(fileLength - 8)) != Checksum(data)) return null;
        int at = 0;
        if (ReadU32() != Magic || ReadI32() != FormatVersion) return null;
        if (!bytes.AsSpan(at, 32).SequenceEqual(key)) return null;
        at += 32;
        if (ReadI32() != zone.X || ReadI32() != zone.Y) return null;
        if ((ReadU8() != 0) != farOnly) return null;
        bool complete = ReadU8() != 0;
        int resources = ReadI32();
        float computeMs = BitConverter.Int32BitsToSingle(ReadI32());
        var meshIds = ReadStrings();
        var layerIds = ReadStrings();
        var meshes = new FoliageMesh[meshIds.Length];
        for (int i = 0; i < meshes.Length; i++) meshes[i] = catalog.Meshes[meshIds[i]];
        var layers = new FoliageLayer[layerIds.Length];
        for (int i = 0; i < layers.Length; i++) layers[i] = catalog.Layers[layerIds[i]];

        // The ground.
        float gx = ReadF32(), gz = ReadF32();
        var heights = new float[FoliageGround.Size * FoliageGround.Size];
        Need(heights.Length * 4);
        MemoryMarshal.Cast<byte, float>(data.Slice(at, heights.Length * 4)).CopyTo(heights);
        at += heights.Length * 4;
        var ground = new FoliageGround(gx, gz, heights);

        // The grass patches and the density maps they share.
        int densityCount = ReadCount(), patchCount = ReadCount();
        var densities = new byte[densityCount][];
        for (int i = 0; i < densityCount; i++)
        {
            int length = ReadCount();
            Need(length);
            densities[i] = data.Slice(at, length).ToArray();
            at += length;
        }
        var result = new FoliageZone { Zone = zone, Resources = resources, Complete = complete };
        for (int i = 0; i < patchCount; i++)
        {
            var layer = layers[ReadU16()];
            int grassIndex = ReadU16(), channel = ReadI32(), density = ReadI32();
            float x0 = ReadF32(), z0 = ReadF32(), x1 = ReadF32(), z1 = ReadF32();
            result.Grass.Add(new FoliageGrassPatch(layer, layer.Grass[grassIndex].Grass, channel, densities[density], x0, z0, x1, z1));
        }

        // The instances: fixed-size records, read in bulk.
        int count = ReadCount();
        int size = Unsafe_SizeOf();
        Need(checked(count * size));
        var disk = MemoryMarshal.Cast<byte, InstanceOnDisk>(data.Slice(at, count * size));
        at += count * size;
        if (at != end) return null;
        if (grouped) return new Decoded(result, ground, computeMs, GroupDisk(result, ground, disk, meshes, layers));
        var list = result.Instances;
        list.Capacity = count;
        foreach (ref readonly var d in disk)
            list.Add(new FoliageInstance(meshes[d.Mesh], layers[d.Layer], new Vector3(d.X, d.Y, d.Z), d.Scale, d.Yaw, new Quaternion(d.Qx, d.Qy, d.Qz, d.Qw)));
        return new Decoded(result, ground, computeMs, null);

        void Need(int n) { if (n < 0 || at + n > end) throw new InvalidDataException("truncated"); }
        uint ReadU32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4)); at += 4; return v; }
        int ReadI32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at, 4)); at += 4; return v; }
        int ReadU16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2)); at += 2; return v; }
        byte ReadU8() { Need(1); return bytes[at++]; }
        float ReadF32() => BitConverter.Int32BitsToSingle(ReadI32());
        int ReadCount() { int n = ReadI32(); if (n < 0 || n > (1 << 26)) throw new InvalidDataException("count"); return n; }
        string[] ReadStrings()
        {
            int n = ReadU16();
            var s = new string[n];
            for (int i = 0; i < n; i++)
            {
                int length = ReadU16();
                Need(length);
                s[i] = Encoding.UTF8.GetString(bytes, at, length);
                at += length;
            }
            return s;
        }
    }

    /// <summary>
    /// The file's records grouped as <see cref="FoliageGrouping.Group"/> groups the instances read from them (same groups in the same order, bit-identical records),
    /// in two passes over the file's span: count the instances of each (mesh, layer), then fill exact-size arrays.
    /// </summary>
    static GroupedFoliageZone GroupDisk(FoliageZone zone, FoliageGround ground, ReadOnlySpan<InstanceOnDisk> disk, FoliageMesh[] meshes, FoliageLayer[] layers)
    {
        var slotOf = new Dictionary<int, int>();
        var keys = new List<int>();
        var counts = new List<int>();
        int lastKey = -1, lastSlot = 0;
        foreach (ref readonly var d in disk)
        {
            int key = d.Mesh << 16 | d.Layer;
            if (key == lastKey) { counts[lastSlot]++; continue; }
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
        var records = new FoliageInstanceRecord[keys.Count][];
        var maxScale = new float[keys.Count];
        var filled = new int[keys.Count];
        for (int g = 0; g < records.Length; g++) records[g] = new FoliageInstanceRecord[counts[g]];
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        lastKey = -1;
        foreach (ref readonly var d in disk)
        {
            int key = d.Mesh << 16 | d.Layer;
            if (key != lastKey) { lastSlot = slotOf[key]; lastKey = key; }
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
        var groups = new List<FoliageInstanceGroup>(keys.Count);
        for (int g = 0; g < keys.Count; g++) groups.Add(new FoliageInstanceGroup(meshes[(int)((uint)keys[g] >> 16)], layers[keys[g] & 0xFFFF], records[g], maxScale[g]));
        return new GroupedFoliageZone
        {
            Zone = zone, Ground = ground, Groups = groups, InstanceCount = disk.Length,
            MinY = disk.Length == 0 ? 0 : minY, MaxY = disk.Length == 0 ? 0 : maxY,
        };
    }

    // ------------------------------------------------------------------ write

    /// <summary>Records the time a miss took to place (for the summary) and writes the zone; failures are ignored, the cache is only a cache.</summary>
    public void Save(ZoneCoordinate zone, bool farOnly, FoliageZone laid, FoliageGround ground, double computeMs)
    {
        if (LoadCaches.Disabled) return;
        Interlocked.Add(ref computeTicks, (long)(computeMs * System.Diagnostics.Stopwatch.Frequency / 1000));
        try
        {
            var bytes = Encode(laid, ground, farOnly, (float)computeMs);
            if (bytes is null) return;
            System.IO.Directory.CreateDirectory(directory);
            var path = PathFor(zone, farOnly);
            var temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            System.IO.File.WriteAllBytes(temporary, bytes);
            System.IO.File.Move(temporary, path, overwrite: true);
            Interlocked.Increment(ref written);
            Interlocked.Add(ref bytesWritten, bytes.Length);
            TouchOnce();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The file for a zone; null when the zone cannot be written back faithfully (an unknown reference).</summary>
    public byte[]? Encode(FoliageZone laid, FoliageGround ground, bool farOnly, float computeMs)
    {
        var meshIndex = new Dictionary<FoliageMesh, int>();
        var layerIndex = new Dictionary<FoliageLayer, int>();
        foreach (var i in laid.Instances)
        {
            if (!meshIndex.ContainsKey(i.Mesh)) meshIndex[i.Mesh] = meshIndex.Count;
            if (!layerIndex.ContainsKey(i.Layer)) layerIndex[i.Layer] = layerIndex.Count;
        }
        foreach (var p in laid.Grass) if (!layerIndex.ContainsKey(p.Layer)) layerIndex[p.Layer] = layerIndex.Count;
        // What is read back is looked up by id in the catalog: it must be the same object.
        foreach (var m in meshIndex.Keys) if (!catalog.Meshes.TryGetValue(m.StringId, out var c) || !ReferenceEquals(c, m)) return null;
        foreach (var l in layerIndex.Keys) if (!catalog.Layers.TryGetValue(l.StringId, out var c) || !ReferenceEquals(c, l)) return null;
        if (meshIndex.Count > ushort.MaxValue || layerIndex.Count > ushort.MaxValue) return null;

        var densities = new List<byte[]>();
        var patchDensity = new int[laid.Grass.Count];
        for (int i = 0; i < patchDensity.Length; i++)
        {
            var d = laid.Grass[i].Density;
            int found = densities.FindIndex(x => x.AsSpan().SequenceEqual(d));
            if (found < 0) { found = densities.Count; densities.Add(d); }
            patchDensity[i] = found;
        }
        var heights = ground.Heights;
        int size = Unsafe_SizeOf();
        using var ms = new MemoryStream(1024 + heights.Length * 4 + densities.Sum(d => d.Length) + laid.Instances.Count * size + laid.Grass.Count * 40);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(key);
            w.Write(laid.Zone.X);
            w.Write(laid.Zone.Y);
            w.Write((byte)(farOnly ? 1 : 0));
            w.Write((byte)(laid.Complete ? 1 : 0));
            w.Write(laid.Resources);
            w.Write(BitConverter.SingleToInt32Bits(computeMs));
            void Strings(IEnumerable<string> ids)
            {
                var list = ids.ToList();
                w.Write((ushort)list.Count);
                foreach (var s in list) { var b = Encoding.UTF8.GetBytes(s); w.Write((ushort)b.Length); w.Write(b); }
            }
            Strings(meshIndex.OrderBy(p => p.Value).Select(p => p.Key.StringId));
            Strings(layerIndex.OrderBy(p => p.Value).Select(p => p.Key.StringId));
            w.Write(ground.X0);
            w.Write(ground.Z0);
            w.Write(MemoryMarshal.AsBytes(heights));
            w.Write(densities.Count);
            w.Write(laid.Grass.Count);
            foreach (var d in densities) { w.Write(d.Length); w.Write(d); }
            for (int i = 0; i < laid.Grass.Count; i++)
            {
                var p = laid.Grass[i];
                int g = -1;
                for (int k = 0; k < p.Layer.Grass.Count; k++) if (ReferenceEquals(p.Layer.Grass[k].Grass, p.Grass)) { g = k; break; }
                if (g < 0) return null;
                w.Write((ushort)layerIndex[p.Layer]);
                w.Write((ushort)g);
                w.Write(p.Channel);
                w.Write(patchDensity[i]);
                w.Write(p.X0); w.Write(p.Z0); w.Write(p.X1); w.Write(p.Z1);
            }
            w.Write(laid.Instances.Count);
            var records = new InstanceOnDisk[laid.Instances.Count];
            for (int i = 0; i < records.Length; i++)
            {
                var n = laid.Instances[i];
                records[i] = new InstanceOnDisk
                {
                    Mesh = (ushort)meshIndex[n.Mesh], Layer = (ushort)layerIndex[n.Layer],
                    X = n.Position.X, Y = n.Position.Y, Z = n.Position.Z, Scale = n.Scale, Yaw = n.YawDegrees,
                    Qx = n.Orientation.X, Qy = n.Orientation.Y, Qz = n.Orientation.Z, Qw = n.Orientation.W,
                };
            }
            w.Write(MemoryMarshal.AsBytes(records.AsSpan()));
        }
        ms.TryGetBuffer(out var buffer);
        var total = new byte[buffer.Count + 8];
        buffer.AsSpan().CopyTo(total);
        BinaryPrimitives.WriteUInt64LittleEndian(total.AsSpan(buffer.Count), Checksum(total.AsSpan(0, buffer.Count)));
        return total;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct InstanceOnDisk
    {
        public ushort Mesh, Layer;
        public float X, Y, Z, Scale, Yaw, Qx, Qy, Qz, Qw;
    }

    static int Unsafe_SizeOf() => System.Runtime.CompilerServices.Unsafe.SizeOf<InstanceOnDisk>();

    /// <summary>A fast 64-bit checksum (eight bytes at a time, multiply-rotate); it only has to notice damage, not resist an attacker.</summary>
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

    // ------------------------------------------------------------------ upkeep

    void TouchOnce() { if (Interlocked.Increment(ref maintained) == 1) MaintainInBackground(); }

    /// <summary>A <see cref="Maintain"/> pass on a worker; failures are ignored.</summary>
    public void MaintainInBackground() => Task.Run(() =>
    {
        try { Maintain(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    });

    /// <summary>
    /// Keeps the cache in bounds: abandoned temporary files go, keys unused for 30 days go (the folder of a key is touched when a start opens it for writing and
    /// on each pass), and when the files total more than <see cref="MaxBytes"/> the other keys go whole, least recently used first, then this key's oldest files,
    /// down to 90% of the cap. Returns (files, bytes before, files deleted).
    /// </summary>
    public (int Files, long Bytes, int Deleted) Maintain(DateTime? now = null)
    {
        if (!System.IO.Directory.Exists(Root)) return default;
        var time = now ?? DateTime.UtcNow;
        int deleted = 0, files = 0;
        long total = 0;
        try { System.IO.Directory.SetLastWriteTimeUtc(directory, time); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var keys = new List<(DirectoryInfo Dir, long Bytes)>();
        foreach (var dir in new DirectoryInfo(Root).EnumerateDirectories())
        {
            long bytes = 0;
            foreach (var f in dir.EnumerateFiles())
            {
                if (f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) { if (time - f.LastWriteTimeUtc > AbandonedTemporary && TryDelete(f)) deleted++; continue; }
                if (!f.Extension.Equals(".mfl", StringComparison.OrdinalIgnoreCase)) continue;
                bytes += f.Length;
                files++;
            }
            if (dir.Name != KeyName && time - dir.LastWriteTimeUtc > StaleKey) { deleted += DeleteDirectory(dir); continue; }
            total += bytes;
            keys.Add((dir, bytes));
        }
        long before = total;
        if (MaxBytes > 0 && total > MaxBytes)
        {
            long target = (long)(MaxBytes * 0.9);
            foreach (var (dir, bytes) in keys.Where(k => k.Dir.Name != KeyName).OrderBy(k => k.Dir.LastWriteTimeUtc))
            {
                if (total <= target) break;
                deleted += DeleteDirectory(dir);
                total -= bytes;
            }
            if (total > target && keys.FirstOrDefault(k => k.Dir.Name == KeyName).Dir is { } mine)
                foreach (var f in mine.EnumerateFiles("*.mfl").OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= target) break;
                    long length = f.Length;
                    if (TryDelete(f)) { total -= length; deleted++; }
                }
        }
        return (files, before, deleted);
    }

    static bool TryDelete(FileInfo f)
    {
        try { f.Delete(); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static int DeleteDirectory(DirectoryInfo dir)
    {
        int n = 0;
        foreach (var f in dir.EnumerateFiles()) if (TryDelete(f)) n++;
        try { dir.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return n;
    }
}
