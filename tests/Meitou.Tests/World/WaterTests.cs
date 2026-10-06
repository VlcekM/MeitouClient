using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class WaterTests
{
    [Fact]
    public void Sun_rises_at_plus_x_and_culminates_at_the_latitude_angle()
    {
        var clock = new SkyClock(54, 5, 23);
        Assert.Equal(0f, clock.Phase(5), 5);
        Assert.Equal(0.5f, clock.Phase(14), 5);
        Assert.Equal(1f, clock.Phase(23), 5);
        Assert.Equal(1.5f, clock.Phase(2), 5);   // the night half-turn: 23 → 5 over 6 hours, 2:00 is half way
        Assert.Equal(1.5f, clock.Phase(26), 5);  // hours wrap
        var rise = clock.SunDirection(5);
        Assert.True(Vector3.Distance(rise, Vector3.UnitX) < 1e-5f);
        var noon = clock.SunDirection(14);
        Assert.Equal(MathF.Cos(54 * MathF.PI / 180), noon.Y, 4);
        Assert.Equal(MathF.Sin(54 * MathF.PI / 180), noon.Z, 4);
        Assert.True(clock.SunDirection(8).Y > 0 && clock.SunDirection(2).Y < 0);
        Assert.Equal(1f, clock.SunDirection(9.3f).Length(), 4);
    }

    [Fact]
    [Slow]
    public void Base_game_water_parameters_and_colour_map()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var clock = SkyClock.FromDatabase(db);
        Assert.Equal(new SkyClock(54, 5, 23), clock);

        var water = BiomeWater.ByIndex(db);
        var swamp = water.Values.Single(w => w.Name.Trim() == "Swamp");
        Assert.Equal(90f / 5000f, swamp.Scale.X, 6);
        Assert.Equal(1f / 4.5f, swamp.InvOpacity, 5);   // visibility 0.45 × 10
        var flood = water.Values.Single(w => w.Name.Trim() == "Floodwastes");
        Assert.Equal(5f, flood.InvStrength, 4);         // strength 0.2
        Assert.Equal(25f, flood.Distortion.X);
        Assert.Equal(0.01f, water[0xFFFF00].InvOpacity, 6); // the fcs.def default visibility 10 gives the material's 0.01

        // watercolourmap.png (128², world bounds) is each biome's "water color" at the pixel centre: the game rebuilds
        // it from the biomes when the file is missing. Exact on most pixels, off near biome borders (blurred).
        var map = TextureLoader.LoadImage(File.ReadAllBytes(BiomeWater.ColourMapPath(install!)));
        var biomes = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install!.DataDirectory, TerrainMaps.BiomeMap)));
        Assert.Equal((128, 128), (map.Width, map.Height));
        int exact = 0, near = 0, total = 0;
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                int o = ((y * 8 + 4) * 1024 + x * 8 + 4) * 4;
                uint index = (uint)(biomes.Pixels[o] << 16 | biomes.Pixels[o + 1] << 8 | biomes.Pixels[o + 2]);
                if (!water.TryGetValue(index, out var w)) continue;
                int p = (y * 128 + x) * 4;
                var c = w.Colour * 255;
                float d = Math.Abs(map.Pixels[p] - c.X) + Math.Abs(map.Pixels[p + 1] - c.Y) + Math.Abs(map.Pixels[p + 2] - c.Z);
                total++;
                if (d < 0.5f) exact++;
                if (d <= 12) near++;
            }
        Assert.True(exact > total / 2 && near > total * 3 / 4, $"{exact} exact, {near} within 12 of {total}");
    }

    [Fact]
    [Slow]
    public void Base_game_low_buildings_are_water_structures()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        using var map = TerrainHeightmap.Open(install!);
        var low = WorldLevelData.Load(install!).Buildings().Where(b => map.HeightAt(b.Position.X, b.Position.Z) < WorldWater.Height).ToList();
        // Observed: only about 5% of the 11,714 placed buildings stand on ground below the water level, and the most
        // common of them are pontoons, swamp walkboards and the like.
        Assert.InRange(low.Count, 300, 900);
        var names = low.Select(b => db.Find(b.BuildingId)?.Name ?? "").ToList();
        int watery = names.Count(n => n.Contains("Pontoon") || n.Contains("Swamp") || n.Contains("Riceweed"));
        Assert.True(watery > low.Count / 4, $"{watery} of {low.Count}");
    }
}
