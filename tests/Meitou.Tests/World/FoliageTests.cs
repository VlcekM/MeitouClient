using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class FoliageTests
{
    [Fact]
    public void Random_is_the_standard_mersenne_twister()
    {
        // MT19937's reference output for its default seed 5489.
        var rng = new FoliageRandom(5489);
        Assert.Equal(3499211612u, rng.Next());
        Assert.Equal(581869302u, rng.Next());
        Assert.Equal(3890346734u, rng.Next());
        Assert.Equal(3, rng.Count);
        rng.Seed(5489);
        Assert.Equal(0, rng.Count);
        Assert.Equal(3499211612u, rng.Next());
    }

    [Fact]
    public void Float_draw_scales_the_result_by_its_maximum()
    {
        var a = new FoliageRandom(42);
        var b = new FoliageRandom(42);
        for (int i = 0; i < 100; i++)
        {
            uint u = b.Next();
            Assert.Equal((float)(u * (1.0 / uint.MaxValue) * 300.0 + 100), a.Float(100, 400));
        }
        Assert.Equal(7f, new FoliageRandom(1).Float(7, 7));
    }

    [Fact]
    public void Integer_draw_stays_in_range_and_reaches_both_ends()
    {
        var rng = new FoliageRandom(7);
        var seen = new HashSet<uint>();
        for (int i = 0; i < 1000; i++)
        {
            uint v = rng.IntUpTo(5);
            Assert.InRange(v, 0u, 5u);
            seen.Add(v);
        }
        Assert.Equal(6, seen.Count);
        Assert.Equal(0u, rng.IntUpTo(0));
    }

    [Fact]
    public void Layer_seed_is_the_record_number_minus_the_zone_noise()
    {
        Assert.Equal(1234, FoliageRandom.LeadingInteger("1234-gamedata.base"));
        Assert.Equal(-12, FoliageRandom.LeadingInteger(" -12x"));
        Assert.Equal(0, FoliageRandom.LeadingInteger("Newwworld.mod"));
        // Zone corner (0, 0): h = 1376312589, so the subtracted term is (int)(h / 2^30 × 16777215 − 16777215).
        double v = 1376312589.0 / 1073741824 * 16777215.0 - 16777215.0;
        Assert.Equal(unchecked((uint)(1234 - (int)v)), FoliageRandom.LayerSeed("1234-gamedata.base", 0, 0));
        // The corner is truncated to integers before hashing.
        Assert.Equal(FoliageRandom.LayerSeed("5", -4608, 9216), FoliageRandom.LayerSeed("5", -4608.4f, 9216.9f));
    }

    [Fact]
    public void Noise_hash_matches_the_classic_integer_noise()
    {
        Assert.Equal(1376312589u, FoliageNoise.Hash(0, 0));
        Assert.Equal(1316808037u, FoliageNoise.Hash(1, 0));
        Assert.Equal(FoliageNoise.Hash(57, 0), FoliageNoise.Hash(0, 1));   // n = x + 57 z
        Assert.Equal(1 - 1376312589.0 / 1073741824, FoliageNoise.Value(0, 0), 12);
        for (int i = -50; i < 50; i++)
        {
            double n = FoliageNoise.Value(i * 7, i * 13);
            Assert.InRange(n, -1.0, 1.0);
        }
    }

    [Fact]
    public void Coverage_is_zero_below_the_cutoff_and_capped()
    {
        for (int i = 0; i < 200; i++)
        {
            double x = i * 517.3, z = i * 91.7;
            Assert.Equal(0f, FoliageNoise.Coverage(x, z, 72, 1f, 3, 1));
            float c = FoliageNoise.Coverage(x, z, 72, 0.3f, 5, 1.5f);
            Assert.InRange(c, 0f, 1.5f);
        }
    }

    static FoliageGround Plane(Func<int, int, float> h)
    {
        var heights = new float[FoliageGround.Size * FoliageGround.Size];
        for (int j = 0; j < FoliageGround.Size; j++)
            for (int i = 0; i < FoliageGround.Size; i++) heights[j * FoliageGround.Size + i] = h(i, j);
        return new FoliageGround(1000, 2000, heights);
    }

    [Fact]
    public void Ground_height_is_bilinear_and_slope_is_the_larger_step()
    {
        var g = Plane((i, j) => 2 * i + 3 * j);
        Assert.Equal(2 * 1.5f + 3 * 2.25f, g.Height(1000 + 36 * 1.5f, 2000 + 36 * 2.25f), 3);
        Assert.Equal(0f, g.Height(0, 0), 3);   // clamped to the grid
        // max(|h - h(i-1, j)|, |h - h(i, j+1)|) = max(2, 3)
        Assert.Equal(3f, g.Slope(1000 + 36 * 10.5f, 2000 + 36 * 10.5f), 4);
        var steep = Plane((i, j) => 10 * i);
        Assert.Equal(10f, steep.Slope(1000 + 36 * 5, 2000), 4);
        Assert.Equal(0f, steep.Slope(1000, 2000), 4);   // the first column has no left neighbour
        var n = steep.Normal(1000 + 36 * 64, 2000 + 36 * 64);
        Assert.True(n.X < 0 && n.Y > 0 && MathF.Abs(n.Z) < 1e-5f);
    }

    static FoliageGrassPatch Patch(byte density, float maxSlope = 10)
    {
        var grass = new FoliageGrass { StringId = "77-test.mod", Name = "test grass", Sprite = "grass.dds", Density = 1, MaxSlope = maxSlope };
        var layer = new FoliageLayer { StringId = "78-test.mod", Name = "test layer" };
        var map = Enumerable.Repeat(density, FoliageGround.Size * FoliageGround.Size).ToArray();
        return new FoliageGrassPatch(layer, grass, 0, map, 1000, 2000, 1000 + WorldLayout.ZoneSize, 2000 + WorldLayout.ZoneSize);
    }

    [Fact]
    public void Grass_blades_follow_density_slope_and_are_deterministic()
    {
        var flat = Plane((_, _) => 500);
        var full = FoliageGrassField.Blades(Patch(255), flat, 1000, 2000, 576);
        int candidates = (int)(0.005f * 576 * 576);
        Assert.Equal(0, full.Length % FoliageGrassField.Stride);
        Assert.InRange(full.Length / FoliageGrassField.Stride, candidates * 99 / 100, candidates);
        Assert.Equal(full, FoliageGrassField.Blades(Patch(255), flat, 1000, 2000, 576));
        Assert.Empty(FoliageGrassField.Blades(Patch(0), flat, 1000, 2000, 576));
        var half = FoliageGrassField.Blades(Patch(128), flat, 1000, 2000, 576).Length / FoliageGrassField.Stride;
        Assert.InRange(half, candidates * 40 / 100, candidates * 60 / 100);
        // Too steep for the grass: nothing grows.
        Assert.Empty(FoliageGrassField.Blades(Patch(255, maxSlope: 5), Plane((i, _) => 10 * i), 1100, 2000, 576));
        for (int i = 0; i < full.Length; i += FoliageGrassField.Stride)
        {
            Assert.InRange(full[i], 1000f, 1576f);
            Assert.Equal(500f, full[i + 1]);
            Assert.InRange(full[i + 3], 0f, 1f);
        }
    }

    [Fact]
    public void Visibility_ranges_are_ten_times_the_page_table()
    {
        Assert.Equal(500, FoliageLayout.VisibilityRange(FoliageVisibility.Close));
        Assert.Equal(1000, FoliageLayout.VisibilityRange(FoliageVisibility.Medium));
        Assert.Equal(8000, FoliageLayout.VisibilityRange(FoliageVisibility.Far));
        Assert.Equal(40000, FoliageLayout.VisibilityRange(FoliageVisibility.Feature));
        Assert.Equal(2000, FoliageLayout.VisibilityRange(FoliageVisibility.Medium, 2));
    }

    [Fact]
    public void Catalog_and_a_zone_load_from_the_game()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var order = LoadOrder.BaseGame(install!);
        var db = GameDatabase.Load(order);
        var catalog = FoliageCatalog.Load(db);
        Assert.NotEmpty(catalog.ByBiome);
        Assert.Contains(catalog.Layers.Values, l => l.IsGrass);
        Assert.Contains(catalog.Meshes.Values, m => m.LeavesMesh is not null);
        Assert.Contains(catalog.Meshes.Values, m => m.BuildingType is not null);   // mineable resource rocks

        using var world = new FoliageWorld(install!, db, WorldLevelData.Load(install!), catalog);
        var zone = new ZoneCoordinate(14, 30);
        var (a, ground) = world.Load(zone);
        var b = world.Zone(zone);
        Assert.NotNull(ground);
        Assert.NotEmpty(a.Instances);
        Assert.NotEmpty(a.Grass);
        Assert.Equal(a.Instances.Count, b.Instances.Count);
        Assert.Equal(a.Instances.Select(i => i.Position), b.Instances.Select(i => i.Position));
        var (x0, z0) = WorldLayout.ZoneOrigin(zone);
        foreach (var i in a.Instances)
        {
            Assert.InRange(i.Position.X, x0, x0 + WorldLayout.ZoneSize);
            Assert.InRange(i.Position.Z, z0, z0 + WorldLayout.ZoneSize);
            Assert.InRange(i.Scale, FoliageLayout.MinScale, FoliageLayout.MaxScale);
        }
    }

    [Fact]
    public void Generated_grass_coverage_has_the_shipped_overlay_footprint()
    {
        // Observed (docs/formats/foliage.md, "Grass coverage"): the regenerated R channel is non-zero on about the same pixels as
        // the R the game ships in new_overlay, though the values differ.
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var order = LoadOrder.BaseGame(install!);
        var db = GameDatabase.Load(order);
        using var world = new FoliageWorld(install!, db, WorldLevelData.Load(install!));
        var zone = new ZoneCoordinate(38, 38);
        var tile = FoliageOverlay.ReadTile(install!, zone);
        var shipped = new FoliageOverlay(zone, tile);
        int game = 0;
        for (int j = shipped.ZonePixelZ; j < shipped.ZonePixelZ + 128; j++)
            for (int i = shipped.ZonePixelX; i < shipped.ZonePixelX + 128; i++)
                if (shipped.Get(0, i, j) > 0) game++;
        var map = TerrainHeightmap.Open(install!);
        var overlay = new FoliageOverlay(zone, tile);
        FoliageLayout.Place(new FoliageZoneInput
        {
            Zone = zone, Ground = FoliageGround.Read(map, zone), Overlay = overlay, Biomes = world.ZoneBiomes(zone), BiomeAt = FoliageBiomeMap.Open(install!).At,
        }, world.Catalog);
        map.Dispose();
        int ours = 0;
        for (int j = overlay.ZonePixelZ; j < overlay.ZonePixelZ + 128; j++)
            for (int i = overlay.ZonePixelX; i < overlay.ZonePixelX + 128; i++)
                if (overlay.Get(0, i, j) > 0) ours++;
        Assert.InRange(ours, game * 0.9, game * 1.1);
    }
}
