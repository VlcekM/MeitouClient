using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class VolumetricCloudsTests
{
    [Fact]
    public void The_slab_is_above_the_world_and_handles_views_above_inside_and_below()
    {
        Assert.True(VolumetricClouds.Bottom > WorldLayout.MaxHeight);
        Assert.True(VolumetricClouds.Intersect(0, 1, out var below));
        Assert.Equal(new Vector2(VolumetricClouds.Bottom, VolumetricClouds.Top), below);
        Assert.False(VolumetricClouds.Intersect(0, -1, out _));
        Assert.True(VolumetricClouds.Intersect(20000, -1, out var above));
        Assert.Equal(new Vector2(1000, 8000), above);
        Assert.True(VolumetricClouds.Intersect(15000, 1, out var inside));
        Assert.Equal(new Vector2(0, 4000), inside);
        Assert.False(VolumetricClouds.Intersect(0, 0, out _));
        Assert.True(VolumetricClouds.Intersect(15000, 0, out var horizontal));
        Assert.Equal(new Vector2(0, VolumetricClouds.MaxDistance), horizontal);
        Assert.False(VolumetricClouds.Intersect(0, 0.00001f, out _));
    }

    [Fact]
    public void Noise_is_periodic_including_negative_world_coordinates()
    {
        foreach (int period in new[] {4, 8, 16, 32})
        {
            var p = new Vector3(-2.25f, 1.5f, 3.125f);
            float n = VolumetricClouds.Noise(p, period);
            Assert.InRange(n, 0, 1);
            foreach (var axis in new[] {Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ})
                Assert.Equal(n, VolumetricClouds.Noise(p + axis * period, period), 5);
        }
    }

    [Fact]
    public void Wrapping_the_weather_wind_keeps_every_density_band_continuous()
    {
        var p = new Vector3(-50731.5f, 15000, 3778.25f);
        foreach (float scale in new[] {VolumetricClouds.PatternPeriod, VolumetricClouds.ShapePeriod, VolumetricClouds.DetailPeriod})
        {
            Assert.Equal(0, VolumetricClouds.PatternPeriod % scale);
            float n = VolumetricClouds.Noise(p / scale * 4, 4);
            Assert.Equal(n, VolumetricClouds.Noise((p + Vector3.UnitX * VolumetricClouds.PatternPeriod) / scale * 4, 4), 4);
            Assert.Equal(n, VolumetricClouds.Noise((p + Vector3.UnitZ * VolumetricClouds.PatternPeriod) / scale * 4, 4), 4);
        }
    }
    [Fact]
    public void Atlas_padding_wraps_each_slice_without_leaking_into_another_slice()
    {
        var atlas = VolumetricClouds.CreateAtlas();
        Assert.Equal(VolumetricClouds.AtlasSize * VolumetricClouds.AtlasSize * 4, atlas.Length);
        const int tile = VolumetricClouds.TileSize, side = VolumetricClouds.AtlasSize;
        for (int z = 0; z < 64; z++)
        {
            int ox = z % 8 * tile, oy = z / 8 * tile;
            for (int i = 1; i <= 64; i++) for (int c = 0; c < 4; c++)
            {
                byte At(int x, int y) => atlas[((oy+y) * side + ox+x) * 4+c];
                Assert.Equal(At(64, i), At(0, i));
                Assert.Equal(At(1, i), At(65, i));
                Assert.Equal(At(i, 64), At(i, 0));
                Assert.Equal(At(i, 1), At(i, 65));
            }
        }
        Assert.True(atlas.Where((_, i) => i % 4 == 0).Max() - atlas.Where((_, i) => i % 4 == 0).Min() > 80);
    }
}