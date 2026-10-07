using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The Meitou draw distances and the landmark class (docs/renderer-native.md 8.19; the <c>reach</c> switch).</summary>
public class LandmarkTests
{
    [Fact]
    public void Meitou_defaults_are_the_new_ones_and_Faithful_keeps_the_old_ones()
    {
        var meitou = WorldOptions.Parse(["--world"])!;
        Assert.True(meitou.MeitouReach);
        Assert.Equal(20000, meitou.ObjectDistanceFor(meitou.MeitouReach));
        Assert.Equal(150000, meitou.LandmarkDistanceFor(meitou.MeitouReach));
        Assert.Equal(16, meitou.TerrainErrorFor(meitou.MeitouReach));

        var faithful = WorldOptions.Parse(["--world", "--faithful", "all"])!;
        Assert.False(faithful.MeitouReach);
        Assert.Equal(12000, faithful.ObjectDistanceFor(faithful.MeitouReach));
        Assert.Equal(0, faithful.LandmarkDistanceFor(faithful.MeitouReach));   // none: landmarks stay with the other objects
        Assert.Equal(WorldRenderOptions.DefaultTerrainPixelError, faithful.TerrainErrorFor(faithful.MeitouReach));
        Assert.Equal(10, faithful.TerrainErrorFor(faithful.MeitouReach));

        // Only reach: the other switches stay Meitou.
        var onlyReach = WorldOptions.Parse(["--world", "--faithful", "reach"])!;
        Assert.False(onlyReach.MeitouReach);
        Assert.True(onlyReach.MeitouRange && onlyReach.Impostors);
    }

    [Fact]
    public void Options_given_on_the_command_line_win_in_both_modes_whatever_the_order()
    {
        foreach (string[] args in new[]
        {
            new[] { "--world", "--object-distance", "7000", "--terrain-error", "4", "--faithful", "all" },
            ["--world", "--faithful", "all", "--object-distance", "7000", "--terrain-error", "4"],
        })
        {
            var o = WorldOptions.Parse(args)!;
            Assert.Equal(7000, o.ObjectDistanceFor(o.MeitouReach));
            Assert.Equal(4, o.TerrainErrorFor(o.MeitouReach));
        }
        var landmarks = WorldOptions.Parse(["--world", "--landmark-distance", "90000"])!;
        Assert.Equal(90000, landmarks.LandmarkDistanceFor(true));
        Assert.Equal(0, landmarks.LandmarkDistanceFor(false));
        Assert.Equal(0, WorldOptions.Parse(["--world", "--landmark-distance", "0"])!.LandmarkDistanceFor(true));   // 0: off
    }

    [Fact]
    public void Meitou_foliage_class_ranges_are_larger_and_the_thresholds_are_unchanged()
    {
        Assert.Equal(50000, FoliageSizes.DefaultLargeRange);
        Assert.Equal(12000, FoliageSizes.DefaultMediumRange);
        Assert.Equal(800, FoliageSizes.DefaultSmallRange);
        Assert.Equal(125, FoliageSizes.LargeFrom);
    }

    [Fact]
    public void A_placement_scales_by_its_largest_axis()
    {
        Assert.Equal(1, LandmarkClass.Scale(Matrix4x4.Identity), 5);
        Assert.Equal(3, LandmarkClass.Scale(Matrix4x4.CreateScale(1, 3, 2) * Matrix4x4.CreateRotationY(0.7f)), 4);
        Assert.Equal(2, LandmarkClass.Scale(Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(100, 200, 300)), 4);
    }

    [Fact]
    public void The_threshold_sits_between_the_largest_building_part_and_the_giants()
    {
        // From the whole-world survey (docs/renderer-native.md 8.19): the largest ordinary building part is 1488, the smallest giant 2000+.
        Assert.True(LandmarkClass.MinRadius > 1488);
        Assert.True(LandmarkClass.MinRadius <= 2007);
    }
}
