using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The per-cascade level choice of the coarser far shadow casters (<see cref="ShadowLod"/>; pure arithmetic, no GPU, no game). The tolerances are passed in, never the static settings.</summary>
public class ShadowLodTests
{
    // The swamp view's cascades (1920x1080, range 10000, 1024 px tiles): texel in world units.
    static readonly float[] SwampTexels = [1.71f, 4.05f, 8.82f, 20.06f];
    const float PixelScale = 1158f;   // 1080 px, 50 degrees

    [Fact]
    public void Only_the_far_cascades_are_coarsened()
    {
        bool was = ShadowLod.Enabled;
        try
        {
            ShadowLod.Enabled = true;
            Assert.False(ShadowLod.Applies(0));
            Assert.True(ShadowLod.Applies(1));
            Assert.True(ShadowLod.Applies(3));
            ShadowLod.Enabled = false;
            Assert.False(ShadowLod.Applies(2));
        }
        finally { ShadowLod.Enabled = was; }
    }

    [Fact]
    public void An_object_takes_the_level_of_the_distance_where_a_pixel_is_as_wide_as_the_cascade_texels()
    {
        Assert.Equal(0, ShadowLod.ObjectLodFloor(0, PixelScale, 1));   // not coarsened
        Assert.Equal(0, ShadowLod.ObjectLodFloor(8.82f, PixelScale, 0));   // switched off
        Assert.Equal(8.82f * PixelScale, ShadowLod.ObjectLodFloor(8.82f, PixelScale, 1), 0.01);
        // A mesh with game levels at 4000 and 8000 units: cascade 1 (4 units a texel) reaches only the first, cascades 2 and 3 the coarsest.
        float[] distances = [0, 4000, 8000];
        int Level(float texel, float distance) => MeshLod.Select(distances, Math.Max(distance, ShadowLod.ObjectLodFloor(texel, PixelScale, 1)));
        Assert.Equal(1, Level(SwampTexels[1], 100));
        Assert.Equal(2, Level(SwampTexels[2], 100));
        Assert.Equal(2, Level(SwampTexels[3], 100));
        Assert.Equal(0, Level(0, 100));   // cascade 0 keeps the distance rule
        // An instance already farther than the floor keeps its own (coarser) level, never a finer one.
        Assert.Equal(2, Level(SwampTexels[1], 9000));
    }

    [Fact]
    public void A_terrain_level_is_allowed_while_its_grid_error_stays_within_the_texels()
    {
        const double Spacing = 18, Roughness = TerrainLod.Roughness;   // level l: error 2 x 18 x 2^l = 36, 72, 144, 288 ...
        Assert.Equal(0, ShadowLod.TerrainFloorLevel(0, Spacing, Roughness, 9, 4));   // not coarsened
        // At one texel no swamp cascade can drop a level: even level 1 deviates by 72 units, more than the 20 of cascade 3.
        foreach (float texel in SwampTexels) Assert.Equal(0, ShadowLod.TerrainFloorLevel(texel, Spacing, Roughness, 9, 1));
        // At four texels cascade 3 (80 units) takes level 1 (72), cascade 2 (35) stays at 0.
        Assert.Equal(1, ShadowLod.TerrainFloorLevel(SwampTexels[3], Spacing, Roughness, 9, 4));
        Assert.Equal(0, ShadowLod.TerrainFloorLevel(SwampTexels[2], Spacing, Roughness, 9, 4));
        // The allowance grows level by level, and the root is never the floor.
        Assert.Equal(2, ShadowLod.TerrainFloorLevel(20, Spacing, Roughness, 9, 8));   // 160: level 2 (144) fits, level 3 (288) does not
        Assert.Equal(7, ShadowLod.TerrainFloorLevel(1e9f, Spacing, Roughness, 9, 8));   // capped at levels - 2
    }

    [Fact]
    public void A_small_mesh_casts_its_impostor_only_where_the_texels_are_large_enough()
    {
        Assert.Equal(ShadowLod.ImpostorFrom, ShadowLod.ImpostorTransition(150, SwampTexels[3], 8));   // 150 <= 8 x 20
        Assert.Equal(float.PositiveInfinity, ShadowLod.ImpostorTransition(200, SwampTexels[3], 8));   // 200 > 160
        Assert.Equal(float.PositiveInfinity, ShadowLod.ImpostorTransition(150, SwampTexels[2], 8));   // 150 > 71
        Assert.Equal(ShadowLod.ImpostorFrom, ShadowLod.ImpostorTransition(60, SwampTexels[2], 8));
        Assert.Equal(float.PositiveInfinity, ShadowLod.ImpostorTransition(1, 0, 8));   // cascade not coarsened
        Assert.Equal(float.PositiveInfinity, ShadowLod.ImpostorTransition(1, SwampTexels[3], 0));   // switched off
    }

    [Fact]
    public void Generated_levels_allow_more_texels_in_a_coarsened_cascade_never_fewer()
    {
        Assert.Equal(4, ShadowLod.RockTolerance(2, true, 4));
        Assert.Equal(2, ShadowLod.RockTolerance(2, false, 4));
        Assert.Equal(2, ShadowLod.RockTolerance(2, true, 0));
        Assert.Equal(6, ShadowLod.RockTolerance(6, true, 4));
    }
}

/// <summary>The quadtree's floor level (the shadow cascades' coarser terrain): nodes are no finer than it, and the area drawn is the same.</summary>
public class TerrainFloorLevelTests
{
    const int Samples = 1025;

    static TerrainHeightBounds FlatBounds() => new(new ushort[Samples * Samples], Samples);

    static List<TerrainNode> Select(int floor, Vector3 eye)
    {
        var tree = new TerrainQuadtree(18, 64, new TerrainLod(1158, 16, 16, 7500)) { FloorLevel = floor };
        var nodes = new List<TerrainNode>();
        tree.Select(eye, FlatBounds(), null, nodes);
        return nodes;
    }

    static double Area(IEnumerable<TerrainNode> nodes) => nodes.Sum(n => n.Quadrant < 0 ? n.Size * n.Size : n.Size * n.Size / 4);

    [Fact]
    public void No_node_is_finer_than_the_floor_and_the_same_ground_is_covered()
    {
        var eye = new Vector3(1000, 500, -2000);
        var plain = Select(0, eye);
        Assert.Contains(plain, n => n.Level == 0);   // the distance rule alone draws the finest level near the eye
        for (int floor = 1; floor <= 3; floor++)
        {
            var floored = Select(floor, eye);
            Assert.All(floored, n => Assert.True(n.Level >= floor, $"level {n.Level} below the floor {floor}"));
            Assert.Equal(Area(plain), Area(floored), 1.0);
            Assert.True(floored.Count < plain.Count);
        }
    }

    [Fact]
    public void A_negative_floor_is_the_distance_rule()
    {
        var eye = new Vector3(0, 100, 0);
        Assert.Equal(Select(0, eye), Select(-3, eye));
    }
}
