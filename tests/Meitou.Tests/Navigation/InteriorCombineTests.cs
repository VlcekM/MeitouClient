using System.Numerics;
using Meitou.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary><see cref="NavInteriors.Combine"/> (docs/game/pathfinding.md, "Unseeded interiors").</summary>
public class InteriorCombineTests
{
    [Fact]
    public void The_interior_whose_own_pruning_kept_nothing_is_kept_and_linked_to_the_exterior_door()
    {
        // Exterior: a kept door quad on x 0..10. Interior: a door quad on x 10..20 of the same door with a floor quad beyond it, all pruned (no seed).
        // Quads wind counter-clockwise (X right, Z up); edge 1 is the +X side, edge 3 the -X side.
        var exterior = new ZoneNavMesh
        {
            ZoneX = 0, ZoneZ = 0, BoundsMin = new(-100), BoundsMax = new(100),
            Vertices = [new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(0, 0, 10)],
            Polygons = [[0, 1, 2, 3]], Neighbours = [[-1, -1, -1, -1]], Areas = [NavArea.Door], Kept = [true],
            DoorIds = ["door"], DoorOf = [0],
        };
        var interior = new ZoneNavMesh
        {
            ZoneX = 0, ZoneZ = 0, BoundsMin = new(-100), BoundsMax = new(100),
            Vertices = [new(10, 0, 0), new(20, 0, 0), new(20, 0, 10), new(10, 0, 10), new(30, 0, 0), new(30, 0, 10)],
            Polygons = [[0, 1, 2, 3], [1, 4, 5, 2]], Neighbours = [[-1, 1, -1, -1], [-1, -1, -1, 0]],
            Areas = [NavArea.Door, NavArea.Ground], Kept = [false, false],
            DoorIds = ["door"], DoorOf = [0, -1],
        };

        var combined = NavInteriors.Combine(exterior, [new InteriorMesh(interior, "house")]);

        Assert.Equal(3, combined.PolygonCount);
        Assert.All(combined.Kept, Assert.True);
        // The interior's door polygon (1) links its -X edge to the exterior's door polygon (0), and back along the exterior's +X edge.
        Assert.Equal([3, 0], combined.LinksOf(1).ToArray());
        Assert.Equal([1, 1], combined.LinksOf(0).ToArray());
        Assert.Equal([0, 0, -1], combined.DoorOf!);
    }
}
