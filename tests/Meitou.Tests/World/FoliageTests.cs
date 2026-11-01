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
    public void Noise_hash_uses_the_exe_multipliers()
    {
        // kenshi_x64.exe FUN_1406cbba0 / FUN_1406d3740 / FUN_1406cc2e0: n (n² 60493 + 19990303) + 0xD208DD0D, masked to 31 bits
        // (docs/formats/foliage.md, "Noise"). The textbook 15731 / 789221 hash would give 1316808037 for (1, 0).
        Assert.Equal(1376312589u, FoliageNoise.Hash(0, 0));
        Assert.Equal(1757122681u, FoliageNoise.Hash(1, 0));
        Assert.Equal(FoliageNoise.Hash(57, 0), FoliageNoise.Hash(0, 1));   // n = x + 57 z
        Assert.Equal(1 - 1376312589.0 / 1073741824, FoliageNoise.Value(0, 0), 12);
        for (int i = -50; i < 50; i++)
        {
            double n = FoliageNoise.Value(i * 7, i * 13);
            Assert.InRange(n, -1.0, 1.0);
        }
    }

    [Fact]
    [Slow]
    public void Far_only_layout_places_exactly_the_far_instances_of_the_whole_layout()
    {
        // docs/formats/foliage.md, "Distances": a layer reseeds its own generator and, without "limit to grass areas", reads nothing the
        // other layers write, so placing the far layers alone (the viewer's far tier) must give the same instances as the whole layout.
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var catalog = FoliageCatalog.Load(db);
        Assert.NotEmpty(catalog.ByBiome);
        Assert.Contains(catalog.Layers.Values, l => l.IsGrass);
        Assert.Contains(catalog.Meshes.Values, m => m.LeavesMesh is not null);
        Assert.Contains(catalog.Meshes.Values, m => m.BuildingType is not null);   // mineable resource rocks
        using var whole = new FoliageWorld(install!, db, WorldLevelData.LoadBaseGame(install!), catalog);
        using var far = new FoliageWorld(install!, db, WorldLevelData.LoadBaseGame(install!), catalog);
        int farZones = 0, farInstances = 0;
        // Desert and canyon zones around (-60564, -45142) with CinderBall01 and canyon blocks, the forest of 14.30, the Hub's south.
        foreach (var zone in new ZoneCoordinate[] { new(17, 21), new(18, 21), new(18, 22), new(19, 22), new(17, 23), new(14, 30), new(20, 29), new(21, 29), new(44, 23), new(31, 45) })
        {
            var (full, ground) = whole.Load(zone);
            var part = far.Load(zone, farOnly: true).Zone;
            Assert.True(full.Complete);
            if (zone == new ZoneCoordinate(14, 30))
            {
                // The forest zone: a ground and grass come with it, the zone cache returns the same instances, and every instance lies in the zone with a legal scale.
                Assert.NotNull(ground);
                Assert.NotEmpty(full.Instances);
                Assert.NotEmpty(full.Grass);
                Assert.Equal(full.Instances.Select(i => i.Position), whole.Zone(zone).Instances.Select(i => i.Position));
                var (x0, z0) = WorldLayout.ZoneOrigin(zone);
                foreach (var i in full.Instances)
                {
                    Assert.InRange(i.Position.X, x0, x0 + WorldLayout.ZoneSize);
                    Assert.InRange(i.Position.Z, z0, z0 + WorldLayout.ZoneSize);
                    Assert.InRange(i.Scale, FoliageLayout.MinScale, FoliageLayout.MaxScale);
                }
            }
            var expected = full.Instances.Where(i => FoliageLayout.IsFarLayer(i.Layer)).ToList();
            if (part.Complete) { Assert.Equal(full.Instances, part.Instances); continue; }   // a far layer that limits to grass areas: laid out whole
            farZones++;
            farInstances += part.Instances.Count;
            Assert.Empty(part.Grass);
            Assert.Equal(expected.Count, part.Instances.Count);
            Assert.Equal(expected, part.Instances);   // mesh, layer, position, scale, yaw and orientation
        }
        Assert.True(farZones >= 5, $"only {farZones} zones laid out far-only");
        Assert.True(farInstances > 0, "no far instances in the sampled zones");
    }
}
