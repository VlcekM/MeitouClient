using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>The foliage layout cache (docs/formats/foliage.md, "Layout cache"), on a small catalog made here: no game files needed.</summary>
public sealed class FoliageLayoutCacheTests : IDisposable
{
    readonly string temp = Path.Combine(Path.GetTempPath(), "meitou-foliage-cache-test-" + Guid.NewGuid().ToString("N"));
    readonly GameInstall install;

    public FoliageLayoutCacheTests()
    {
        Directory.CreateDirectory(Path.Combine(temp, "kenshi", "data"));
        File.WriteAllBytes(Path.Combine(temp, "kenshi", "data", "gamedata.base"), []);
        install = GameInstall.Open(Path.Combine(temp, "kenshi"));
    }

    public void Dispose()
    {
        try { Directory.Delete(temp, true); } catch (IOException) { }
    }

    static FcsRecord Rec(string id, FcsRecordType type) => new() { StringId = id, Flags = 0x10, Name = id, RecordType = type };

    static FoliageCatalog Catalog(float treeHeight = 1.5f)
    {
        var tree = Rec("1-t.mod", FcsRecordType.FOLIAGE_MESH);
        tree.Floats["max height"] = treeHeight;
        tree.References["meshes"] = [new FcsReference("2-t.mod", 3, 0, 0)];
        var leaf = Rec("2-t.mod", FcsRecordType.FOLIAGE_MESH);
        var grass = Rec("3-t.mod", FcsRecordType.GRASS);
        var grass2 = Rec("4-t.mod", FcsRecordType.GRASS);
        var trees = Rec("10-t.mod", FcsRecordType.FOLIAGE_LAYER);
        trees.References["meshes"] = [new FcsReference("1-t.mod", 5, 0, 0)];
        var lawn = Rec("11-t.mod", FcsRecordType.FOLIAGE_LAYER);
        lawn.References["grass"] = [new FcsReference("3-t.mod", 0, 0, 0), new FcsReference("4-t.mod", 1, 0, 0)];
        var biome = Rec("20-t.mod", FcsRecordType.BIOMES);
        biome.Ints["index"] = 0x112233;
        biome.References["foliage"] = [new FcsReference("10-t.mod", 0, 0, 0), new FcsReference("11-t.mod", 0, 0, 0)];
        var file = new FcsFile();
        file.Records.AddRange([tree, leaf, grass, grass2, trees, lawn, biome]);
        var db = new GameDatabase();
        db.Apply(file, "t.mod");
        return FoliageCatalog.Load(db);
    }

    static readonly (Vector2, float)[] Towns = [(new Vector2(100, 200), 2000f)];

    FoliageLayoutCache Open(FoliageCatalog catalog, string name, IEnumerable<(Vector2, float)>? towns = null) =>
        new(install, catalog, towns ?? Towns, Path.Combine(temp, name), 0);

    static (FoliageZone, FoliageGround) Zone(FoliageCatalog catalog, ZoneCoordinate coordinate, bool complete = true)
    {
        var rng = new Random(coordinate.X * 100 + coordinate.Y);
        var tree = catalog.Meshes["1-t.mod"];
        var leaf = catalog.Meshes["2-t.mod"];
        var trees = catalog.Layers["10-t.mod"];
        var lawn = catalog.Layers["11-t.mod"];
        var zone = new FoliageZone { Zone = coordinate, Resources = 7, Complete = complete };
        for (int i = 0; i < 500; i++)
            zone.Instances.Add(new FoliageInstance(i % 3 == 0 ? leaf : tree, trees, new Vector3(rng.NextSingle() * 1000, rng.NextSingle() * 50, rng.NextSingle() * 1000),
                rng.NextSingle() * 3, rng.NextSingle() * 360, Quaternion.Normalize(new Quaternion(rng.NextSingle(), rng.NextSingle(), rng.NextSingle(), rng.NextSingle() + 0.1f))));
        if (complete)
        {
            var a = new byte[FoliageGround.Size * FoliageGround.Size];
            rng.NextBytes(a);
            zone.Grass.Add(new FoliageGrassPatch(lawn, lawn.Grass[0].Grass, 0, a, 1, 2, 3, 4));
            zone.Grass.Add(new FoliageGrassPatch(lawn, lawn.Grass[1].Grass, 1, (byte[])a.Clone(), 5, 6, 7, 8));   // same coverage: stored once
            zone.Grass.Add(new FoliageGrassPatch(lawn, lawn.Grass[1].Grass, 0, new byte[a.Length], 9, 10, 11, 12));
        }
        var heights = new float[FoliageGround.Size * FoliageGround.Size];
        for (int i = 0; i < heights.Length; i++) heights[i] = rng.NextSingle() * 300 - 20;
        return (zone, new FoliageGround(-1234.5f, 4321.25f, heights));
    }

    static void AssertSame(FoliageZone a, FoliageGround ga, FoliageZone b, FoliageGround gb)
    {
        Assert.Equal(a.Zone, b.Zone);
        Assert.Equal(a.Resources, b.Resources);
        Assert.Equal(a.Complete, b.Complete);
        Assert.Equal(a.Instances.Count, b.Instances.Count);
        for (int i = 0; i < a.Instances.Count; i++)
        {
            var (x, y) = (a.Instances[i], b.Instances[i]);
            Assert.Equal(x.Mesh.StringId, y.Mesh.StringId);
            Assert.Equal(x.Layer.StringId, y.Layer.StringId);
            Assert.Equal((x.Position, x.Scale, x.YawDegrees, x.Orientation), (y.Position, y.Scale, y.YawDegrees, y.Orientation));   // bit for bit
        }
        Assert.Equal(a.Grass.Count, b.Grass.Count);
        for (int i = 0; i < a.Grass.Count; i++)
        {
            var (x, y) = (a.Grass[i], b.Grass[i]);
            Assert.Equal(x.Layer.StringId, y.Layer.StringId);
            Assert.Equal(x.Grass.StringId, y.Grass.StringId);
            Assert.Equal(x.Channel, y.Channel);
            Assert.Equal(x.Density, y.Density);
            Assert.Equal((x.X0, x.Z0, x.X1, x.Z1), (y.X0, y.Z0, y.X1, y.Z1));
        }
        Assert.Equal((ga.X0, ga.Z0), (gb.X0, gb.Z0));
        for (int j = 0; j < FoliageGround.Size; j++)
            for (int i = 0; i < FoliageGround.Size; i++) Assert.Equal(ga.At(i, j), gb.At(i, j));
    }

    [Fact]
    [Slow]
    public void Real_zones_read_back_equal_to_the_ones_just_placed()
    {
        var real = GameInstall.Locate();
        Assert.SkipWhen(real is null, "Kenshi install not found");
        var db = GameDatabase.Load(LoadOrder.BaseGame(real!));
        var levels = WorldLevelData.Load(real!);
        var catalog = FoliageCatalog.Load(db);
        var towns = FoliageWorld.ReadTowns(db, levels);
        var root = Path.Combine(temp, "real");
        var zones = new ZoneCoordinate[] { new(14, 30), new(21, 29), new(44, 23) };
        var placed = new List<(FoliageZone, FoliageGround?)>();
        var writeCache = new FoliageLayoutCache(real!, catalog, towns, root, 0);
        using (var world = new FoliageWorld(real!, db, levels, catalog, cache: writeCache))
            foreach (var z in zones) { placed.Add(world.Load(z)); placed.Add(world.Load(z, farOnly: true)); }
        Assert.Equal(zones.Length * 2, writeCache.Stats.Written);
        var readCache = new FoliageLayoutCache(real!, FoliageCatalog.Load(db), towns, root, 0);
        using (var world = new FoliageWorld(real!, db, levels, readCache.Catalog, cache: readCache))
            for (int i = 0; i < zones.Length * 2; i++)
            {
                var (zone, ground) = world.Load(zones[i / 2], farOnly: i % 2 == 1);
                AssertSame(placed[i].Item1, placed[i].Item2!, zone, ground!);
            }
        Assert.Equal(zones.Length * 2, readCache.Stats.Hits);
        Assert.Equal(0, readCache.Stats.Misses);
    }

    [Fact]
    public void A_saved_zone_reads_back_equal_in_a_new_cache()
    {
        var catalog = Catalog();
        var coordinate = new ZoneCoordinate(21, 29);
        var (zone, ground) = Zone(catalog, coordinate);
        var writer = Open(catalog, "c");
        Assert.Null(writer.TryLoad(coordinate, false));
        writer.Save(coordinate, false, zone, ground, 12.5);
        Assert.True(File.Exists(writer.PathFor(coordinate, false)));

        var fresh = Catalog();   // another process's view: a new catalog of the same data
        var reader = Open(fresh, "c");
        var hit = reader.TryLoad(coordinate, false);
        Assert.NotNull(hit);
        Assert.Same(fresh.Meshes["1-t.mod"], hit.Value.Zone.Instances[1].Mesh);   // resolved against the reader's catalog
        Assert.Same(fresh.Layers["11-t.mod"].Grass[1].Grass, hit.Value.Zone.Grass[1].Grass);
        AssertSame(zone, ground, hit.Value.Zone, hit.Value.Ground);
        Assert.Equal(1, reader.Stats.Hits);
        Assert.True(reader.Stats.SavedMs > 0);
        // The grass coverage shared by two patches is stored once: the file is smaller than three maps.
        int map = FoliageGround.Size * FoliageGround.Size;
        Assert.True(new FileInfo(writer.PathFor(coordinate, false)).Length < 3 * map + 500 * 40 + map * 4 + 4000);
        // The far-only kind is a separate entry.
        Assert.Null(reader.TryLoad(coordinate, true));
        var (farZone, farGround) = Zone(catalog, coordinate, complete: false);
        writer.Save(coordinate, true, farZone, farGround, 1);
        var far = reader.TryLoad(coordinate, true)!.Value;
        Assert.False(far.Zone.Complete);
        AssertSame(farZone, farGround, far.Zone, far.Ground);
        Assert.Empty(Directory.GetFiles(writer.Directory, "*.tmp"));
    }

    [Fact]
    public void The_key_follows_the_data_that_the_layout_reads()
    {
        var catalog = Catalog();
        var key = FoliageLayoutCache.ComputeKey(install, catalog, Towns);
        Assert.Equal(key, FoliageLayoutCache.ComputeKey(install, Catalog(), Towns));   // same data, same key

        Assert.NotEqual(key, FoliageLayoutCache.ComputeKey(install, Catalog(treeHeight: 2f), Towns));   // a field of a mesh record
        Assert.NotEqual(key, FoliageLayoutCache.ComputeKey(install, catalog, [(new Vector2(100, 200), 1500f)]));   // a town's range
        Assert.NotEqual(key, FoliageLayoutCache.ComputeKey(install, catalog, [.. Towns, (new Vector2(5, 6), 2000f)]));   // another town
        var fewer = Catalog();
        fewer.ByBiome[0x112233].RemoveAt(1);
        Assert.NotEqual(key, FoliageLayoutCache.ComputeKey(install, fewer, Towns));   // a layer dropped from the biome

        // A file the layout reads changes size: the key changes.
        var land = Path.Combine(install.DataDirectory, "newland", "land");
        Directory.CreateDirectory(land);
        var heightmap = Path.Combine(install.DataDirectory, TerrainHeightmap.RelativePath);
        File.WriteAllBytes(heightmap, new byte[10]);
        var withMap = FoliageLayoutCache.ComputeKey(install, catalog, Towns);
        Assert.NotEqual(key, withMap);
        File.WriteAllBytes(heightmap, new byte[11]);
        var sized = FoliageLayoutCache.ComputeKey(install, catalog, Towns);
        Assert.NotEqual(withMap, sized);
        // ... or just its write time.
        File.SetLastWriteTimeUtc(heightmap, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var dated = FoliageLayoutCache.ComputeKey(install, catalog, Towns);
        Assert.NotEqual(sized, dated);
        // An overlay tile is part of it too.
        var overlays = Path.Combine(land, "overlaymaps");
        Directory.CreateDirectory(overlays);
        File.WriteAllBytes(Path.Combine(overlays, "new_overlay.3.4.png"), new byte[5]);
        Assert.NotEqual(dated, FoliageLayoutCache.ComputeKey(install, catalog, Towns));
    }

    [Fact]
    public void An_entry_of_another_key_zone_or_kind_is_not_used()
    {
        var catalog = Catalog();
        var coordinate = new ZoneCoordinate(3, 4);
        var (zone, ground) = Zone(catalog, coordinate);
        var a = Open(catalog, "k");
        a.Save(coordinate, false, zone, ground, 3);
        var b = Open(catalog, "k", [(new Vector2(1, 1), 10f)]);
        Assert.NotEqual(a.Directory, b.Directory);
        Directory.CreateDirectory(b.Directory);
        File.Copy(a.PathFor(coordinate, false), b.PathFor(coordinate, false));   // as if it had been left there under the wrong key
        Assert.Null(b.TryLoad(coordinate, false));
        Assert.Equal(1, b.Stats.Rejected);
        // Right key, wrong zone: the entry for (3, 4) copied over (5, 6).
        File.Copy(a.PathFor(coordinate, false), a.PathFor(new ZoneCoordinate(5, 6), false));
        Assert.Null(a.TryLoad(new ZoneCoordinate(5, 6), false));
        Assert.NotNull(a.TryLoad(coordinate, false));
        // Right everything but the kind.
        File.Copy(a.PathFor(coordinate, false), a.PathFor(coordinate, true));
        Assert.Null(a.TryLoad(coordinate, true));
    }

    [Fact]
    public void Truncated_or_damaged_entries_are_rejected()
    {
        var catalog = Catalog();
        var coordinate = new ZoneCoordinate(21, 29);
        var (zone, ground) = Zone(catalog, coordinate);
        var cache = Open(catalog, "t");
        var good = cache.Encode(zone, ground, false, 4)!;
        Assert.NotNull(cache.Decode(good, coordinate, false));
        var path = cache.PathFor(coordinate, false);
        Directory.CreateDirectory(cache.Directory);

        var lengths = new[] { 0, 1, 7, 50, 100, good.Length / 2, good.Length - 9, good.Length - 8, good.Length - 1 };
        foreach (int length in lengths)
        {
            Assert.Null(cache.Decode(good[..length], coordinate, false));
            File.WriteAllBytes(path, good[..length]);
            Assert.Null(cache.TryLoad(coordinate, false));   // a miss, never an exception
        }
        Assert.Equal(lengths.Length, cache.Stats.Rejected);

        foreach (int at in new[] { 0, 5, 40, 200, good.Length / 2, good.Length - 20 })
        {
            var bad = (byte[])good.Clone();
            bad[at] ^= 0x40;
            Assert.Null(cache.Decode(bad, coordinate, false));
        }
        // Garbage that is not an entry at all.
        File.WriteAllBytes(path, Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray());
        Assert.Null(cache.TryLoad(coordinate, false));
        // An entry naming a mesh the catalog does not have (the data changed under an unchanged key) is a miss too.
        var other = Catalog();
        other.Meshes.Remove("2-t.mod");
        var strange = new FoliageLayoutCache(install, other, Towns, Path.Combine(temp, "t"), 0);
        Assert.Equal(cache.Directory, strange.Directory);   // the same key (the key is computed from the records, which are the same)
        File.WriteAllBytes(path, good);
        Assert.Null(strange.TryLoad(coordinate, false));
        Assert.Equal(1, strange.Stats.Rejected);
        Assert.NotNull(cache.TryLoad(coordinate, false));   // and the good one still loads
    }

    [Fact]
    public void A_zone_that_the_catalog_cannot_name_is_not_written()
    {
        var catalog = Catalog();
        var foreign = Catalog();   // the same records, other objects: not what a reader's catalog would resolve to
        var (zone, ground) = Zone(foreign, new ZoneCoordinate(1, 1));
        Assert.Null(Open(catalog, "f").Encode(zone, ground, false, 1));
    }

    [Fact]
    public void No_load_cache_reads_and_writes_nothing()
    {
        var catalog = Catalog();
        var coordinate = new ZoneCoordinate(8, 9);
        var (zone, ground) = Zone(catalog, coordinate);
        var cache = Open(catalog, "n");
        cache.Save(coordinate, false, zone, ground, 1);
        Assert.NotNull(cache.TryLoad(coordinate, false));
        bool was = LoadCaches.Disabled;
        try
        {
            LoadCaches.Disabled = true;
            Assert.Null(cache.TryLoad(coordinate, false));
            var (z2, g2) = Zone(catalog, new ZoneCoordinate(8, 10));
            cache.Save(new ZoneCoordinate(8, 10), false, z2, g2, 1);
            Assert.False(File.Exists(cache.PathFor(new ZoneCoordinate(8, 10), false)));
        }
        finally { LoadCaches.Disabled = was; }
    }

    [Fact]
    public void Maintenance_deletes_abandoned_temporaries_old_keys_and_the_least_recent_over_the_cap()
    {
        var catalog = Catalog();
        var root = Path.Combine(temp, "m");
        var (zone, ground) = Zone(catalog, new ZoneCoordinate(1, 1));
        var mine = new FoliageLayoutCache(install, catalog, Towns, root, 0);
        mine.Save(new ZoneCoordinate(1, 1), false, zone, ground, 1);
        var oldKey = Path.Combine(root, "0000000000000001");
        Directory.CreateDirectory(oldKey);
        File.WriteAllBytes(Path.Combine(oldKey, "z1.1.w.mfl"), new byte[1000]);
        Directory.SetLastWriteTimeUtc(oldKey, DateTime.UtcNow.AddDays(-40));
        var recentKey = Path.Combine(root, "0000000000000002");
        Directory.CreateDirectory(recentKey);
        File.WriteAllBytes(Path.Combine(recentKey, "z1.1.w.mfl"), new byte[1000]);
        var leftover = Path.Combine(mine.Directory, "z9.9.w.mfl.123.1.tmp");
        File.WriteAllBytes(leftover, new byte[10]);
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-3));
        var fresh = Path.Combine(mine.Directory, "z8.8.w.mfl.124.1.tmp");   // another process's save in progress
        File.WriteAllBytes(fresh, new byte[10]);

        mine.Maintain();
        Assert.False(Directory.Exists(oldKey));
        Assert.True(Directory.Exists(recentKey));
        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(mine.PathFor(new ZoneCoordinate(1, 1), false)));

        long mineBytes = new FileInfo(mine.PathFor(new ZoneCoordinate(1, 1), false)).Length;
        File.WriteAllBytes(Path.Combine(recentKey, "z1.1.w.mfl"), new byte[mineBytes * 3]);
        var capped = new FoliageLayoutCache(install, catalog, Towns, root, mineBytes * 2);
        capped.Maintain();   // the other key takes it over the cap and goes first; this key.s file stays
        Assert.False(Directory.Exists(recentKey));
        Assert.True(File.Exists(mine.PathFor(new ZoneCoordinate(1, 1), false)));
    }
}
