using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using Meitou.Data.World;
using Meitou.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>The stitcher's join of two coinciding open edges Recast left unwelded (ZoneNavMeshBuilder.Stitch, docs/game/pathfinding.md "Own builder").</summary>
public class StitchSeamTests
{
    /// <summary>
    /// Two quads of one tile side by side along X, sharing the edge x = 20 (z 10..13, 3 cells) with separate vertices: the left quad's at height 10 cells
    /// at both ends, the right quad's at 10 + <paramref name="gapAt10"/> and 10 + <paramref name="gapAt13"/>. The climb is 5 cells (MaxClimb 5, cell height 1):
    /// gaps 0 and 6 stay within it along 3 * 5/6 = 2.5 cells, at least SeamClimbCells (2), so they join; 6 and 7 are over it everywhere.
    /// </summary>
    [Theory]
    [InlineData(0, 6, true)]
    [InlineData(6, 0, true)]
    [InlineData(6, 7, false)]
    public void The_seam_joins_coinciding_edges_within_the_climb_along_two_cells(int gapAt10, int gapAt13, bool joined)
    {
        var s = new NavBuildSettings();
        const int nvp = 6, none = RcRecast.RC_MESH_NULL_IDX;
        // Recast winding (clockwise with X right, Z up): left (10,10) (10,13) (20,13) (20,10); right (20,10) (20,13) (30,13) (30,10).
        int[] verts =
        [
            10, 10, 10, 10, 10, 13, 20, 10, 13, 20, 10, 10,
            20, 10 + gapAt10, 10, 20, 10 + gapAt13, 13, 30, 10, 13, 30, 10, 10,
        ];
        int[] polys =
        [
            0, 1, 2, 3, none, none, none, none, none, none, none, none,
            4, 5, 6, 7, none, none, none, none, none, none, none, none,
        ];
        var mesh = new RcPolyMesh
        {
            verts = verts, polys = polys, areas = [NavArea.Ground, NavArea.Ground], nverts = 8, npolys = 2, nvp = nvp,
            bmin = new RcVec3f(0, 0, 0), cs = s.CellSize, ch = s.CellHeight,
        };
        var zone = new ZoneCoordinate(0, 0);
        float size = s.TileCells * s.CellSize;
        var g = new ZoneGeometry { Zone = zone, ZoneMin = Vector2.Zero, ZoneMax = new(size) };

        var stitched = ZoneNavMeshBuilder.Stitch(g, s, [mesh], 1, size);

        Assert.Equal(joined, stitched.Neighbours[0].Contains(1));
        Assert.Equal(joined, stitched.Neighbours[1].Contains(0));
        Assert.True(stitched.LinksOf(0).IsEmpty && stitched.LinksOf(1).IsEmpty);
    }
}
