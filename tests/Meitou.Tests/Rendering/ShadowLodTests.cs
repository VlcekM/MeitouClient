using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Game.Rendering.Shadows;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The per-cascade level choice of the coarser far shadow casters (<see cref="ShadowLod"/>; pure arithmetic, no GPU, no game). The tolerances are
/// passed in, never the static settings.
/// </summary>
public class ShadowLodTests
{
    // The swamp view's cascades (1920x1080, range 10000, 1024 px tiles): texel in world units.
    static readonly float[] SwampTexels = [1.71f, 4.05f, 8.82f, 20.06f];
    const float PixelScale = 1158f;   // 1080 px, 50 degrees

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
}

/// <summary>
/// The quadtree's floor level (the shadow cascades' coarser terrain): nodes are no finer than it, and the area drawn is the same.
/// </summary>
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
}
