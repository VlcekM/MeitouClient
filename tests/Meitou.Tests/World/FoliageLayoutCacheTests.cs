using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Tests.World;

/// <summary>
/// The foliage layout cache (docs/formats/foliage.md, "Layout cache"), on a small catalog made here: no game files needed.
/// </summary>
public sealed class FoliageLayoutCacheTests : IDisposable
{
    readonly string _temp = Path.Combine(Path.GetTempPath(), "meitou-foliage-cache-test-" + Guid.NewGuid().ToString("N"));
    readonly GameInstall _install;

    public FoliageLayoutCacheTests()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "kenshi", "data"));
        File.WriteAllBytes(Path.Combine(_temp, "kenshi", "data", "gamedata.base"), []);
        _install = GameInstall.Open(Path.Combine(_temp, "kenshi"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, true);
        }
        catch (IOException)
        {
        }
    }

    static FcsRecord Rec(string id, FcsRecordType type) => new() { StringId = id, Flags = 0x10, Name = id, RecordType = type };

    static FoliageCatalog Catalog(float treeHeight = 1.5f)
    {
        var tree = Rec("1-t.mod", FcsRecordType.FoliageMesh);
        tree.Floats["max height"] = treeHeight;
        tree.References["meshes"] = [new FcsReference("2-t.mod", 3, 0, 0)];
        var leaf = Rec("2-t.mod", FcsRecordType.FoliageMesh);
        var grass = Rec("3-t.mod", FcsRecordType.Grass);
        var grass2 = Rec("4-t.mod", FcsRecordType.Grass);
        var trees = Rec("10-t.mod", FcsRecordType.FoliageLayer);
        trees.References["meshes"] = [new FcsReference("1-t.mod", 5, 0, 0)];
        var lawn = Rec("11-t.mod", FcsRecordType.FoliageLayer);
        lawn.References["grass"] = [new FcsReference("3-t.mod", 0, 0, 0), new FcsReference("4-t.mod", 1, 0, 0)];
        var biome = Rec("20-t.mod", FcsRecordType.Biomes);
        biome.Ints["index"] = 0x112233;
        biome.References["foliage"] = [new FcsReference("10-t.mod", 0, 0, 0), new FcsReference("11-t.mod", 0, 0, 0)];
        var file = new FcsFile();
        file.Records.AddRange([tree, leaf, grass, grass2, trees, lawn, biome]);
        var db = new GameDatabase();
        db.Apply(file, "t.mod");
        return FoliageCatalog.Load(db);
    }

    static readonly FoliageTown[] Towns = [new FoliageTown(new Vector2(100, 200), 2000f)];

    FoliageLayoutCache Open(FoliageCatalog catalog, string name, IEnumerable<FoliageTown>? towns = null) =>
        new(_install, catalog, towns ?? Towns, Path.Combine(_temp, name), 0);

    static (FoliageZone, FoliageGround) Zone(FoliageCatalog catalog, ZoneCoordinate coordinate, bool complete = true)
    {
        var rng = new Random(coordinate.X * 100 + coordinate.Y);
        var tree = catalog.Meshes["1-t.mod"];
        var leaf = catalog.Meshes["2-t.mod"];
        var trees = catalog.Layers["10-t.mod"];
        var lawn = catalog.Layers["11-t.mod"];
        var zone = new FoliageZone { Zone = coordinate, Resources = 7, Complete = complete };
        for (int i = 0; i < 500; i++)
            zone.Instances.Add(new FoliageInstance(i % 3 == 0 ? leaf : tree, trees,
                    new Vector3(rng.NextSingle() * 1000, rng.NextSingle() * 50, rng.NextSingle() * 1000),
                rng.NextSingle() * 3, rng.NextSingle() * 360, Quaternion.Normalize(new Quaternion(rng.NextSingle(), rng.NextSingle(),
                        rng.NextSingle(), rng.NextSingle() + 0.1f))));
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
    public void A_saved_zone_reads_back_equal_in_a_new_cache()
    {
        var catalog = Catalog();
        var coordinate = new ZoneCoordinate(21, 29);
        var (zone, ground) = Zone(catalog, coordinate);
        var writer = Open(catalog, "c");
        Assert.Null(writer.TryLoad(coordinate, false));
        writer.Save(coordinate, false, zone, ground, 60000);   // a minute of layout, so the load time under a busy test run never exceeds it
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
}
