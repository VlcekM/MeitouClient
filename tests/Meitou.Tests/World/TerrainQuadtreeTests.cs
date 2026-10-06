using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class TerrainQuadtreeTests
{
    static HeightWindow Window(int columns, int rows, Func<int, int, ushort> raw, int column0 = 0, int row0 = 0, int step = 1)
    {
        var samples = new ushort[columns * rows];
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < columns; i++)
                samples[j * columns + i] = raw(i, j);
        return new HeightWindow(column0, row0, step, columns, rows, samples);
    }

    [Fact]
    public void Window_points_land_on_world_coordinates()
    {
        var w = Window(3, 3, (_, _) => 0, column0: 8192, row0: 100, step: 4);
        Assert.Equal((0.0, 100 * 18 - 147456.0), w.WorldOf(0, 0));
        Assert.Equal((2 * 4 * 18.0, (100 + 4) * 18 - 147456.0), w.WorldOf(2, 1));
        Assert.Equal(72f, w.Spacing);
        Assert.Equal(new Vector3(0, 0, 100 * 18 - 147456), w.Position(0, 0));
    }

    [Fact]
    public void Window_normals_point_up_on_flat_ground_and_lean_downhill_on_slopes()
    {
        var flat = Window(5, 5, (_, _) => 1000);
        Assert.Equal(Vector3.UnitY, flat.Normal(2, 2));
        // Height rises along +X by 18 units per sample (raw value per unit = 65535 / 9800): a 45 degree slope.
        float rawPerUnit = ushort.MaxValue / WorldLayout.MaxHeight;
        var slope = Window(5, 5, (i, j) => (ushort)Math.Round(i * 18 * rawPerUnit));
        var n = slope.Normal(2, 2);
        Assert.True(n.X < -0.6 && n.Y > 0.6, $"normal {n} should lean towards -X");
    }

    [Fact]
    public void Grid_triangles_face_up_and_quadrants_split_the_grid()
    {
        const int cells = 8;
        var full = TerrainQuadtree.GridIndices(cells);
        Assert.Equal(cells * cells * 6, full.Length);
        Vector3 P(uint v) => new(v % (cells + 1), 0, v / (cells + 1));
        for (int t = 0; t < full.Length; t += 3)
            Assert.True(Vector3.Cross(P(full[t + 1]) - P(full[t]), P(full[t + 2]) - P(full[t])).Y > 0, $"triangle {t / 3} faces down");
        var quarters = Enumerable.Range(0, 4).SelectMany(q => TerrainQuadtree.GridIndices(cells, q).Chunk(3).Select(t => string.Join(",", t))).Order().ToList();
        Assert.Equal(full.Chunk(3).Select(t => string.Join(",", t)).Order(), quarters);
        Assert.Equal(new Vector2(2, 4), TerrainQuadtree.MorphGrid(new Vector2(3, 4), 1));
        Assert.Equal(new Vector2(2.5f, 4), TerrainQuadtree.MorphGrid(new Vector2(3, 4), 0.5f));
    }

    static TerrainHeightBounds FlatBounds(ushort raw = 0) => new(Enumerable.Repeat(raw, 65 * 65).ToArray(), 65);

    [Fact]
    public void Height_bounds_cover_the_coarse_grid_and_the_fine_window()
    {
        // 4 x 4 cells over the world; a peak sample at grid point (1, 1).
        var coarse = new ushort[5 * 5];
        coarse[1 * 5 + 1] = 65535;
        var b = new TerrainHeightBounds(coarse, 5);
        double cell = WorldLayout.WorldSize / 4.0, x0 = -WorldLayout.HalfWorldSize;
        Assert.Equal((0f, 9800f), b.Range(x0, x0, cell));                  // the cell touching the peak
        Assert.Equal((0f, 0f), b.Range(x0 + 2 * cell, x0 + 2 * cell, cell)); // far from it
        Assert.Equal((0f, 9800f), b.Range(x0, x0, 4 * cell));              // the whole world
        // A fine window sample inside cell (3, 3) raises its maximum.
        var fine = new HeightWindow(16000, 16000, 1, 1, 1, [32768]);
        var refined = new TerrainHeightBounds(coarse, 5, fine);
        Assert.Equal(WorldLayout.RawToHeight(32768), refined.Range(x0 + 3 * cell, x0 + 3 * cell, cell).Max);
    }

    [Fact]
    public void Height_bounds_take_a_window_later_the_same_as_at_construction()
    {
        var coarse = new ushort[5 * 5];
        coarse[1 * 5 + 1] = 65535;
        var fine = new HeightWindow(16000, 16000, 1, 3, 3, [100, 100, 100, 100, 32768, 100, 100, 100, 100]);
        var early = new TerrainHeightBounds(coarse, 5, fine);
        var late = new TerrainHeightBounds(coarse, 5);
        late.Apply(late.Measure(fine));
        double cell = WorldLayout.WorldSize / 4.0, x0 = -WorldLayout.HalfWorldSize;
        foreach (double size in new[] { cell, 2 * cell, 4 * cell })
            for (int cz = 0; cz < 4; cz++)
                for (int cx = 0; cx < 4; cx++)
                    Assert.Equal(early.Range(x0 + cx * cell, x0 + cz * cell, size), late.Range(x0 + cx * cell, x0 + cz * cell, size));
        // The window only widens: a second window elsewhere keeps the first one's samples in the bounds.
        late.Apply(late.Measure(new HeightWindow(0, 0, 1, 1, 1, [65535])));
        Assert.Equal(WorldLayout.RawToHeight(32768), late.Range(x0 + 3 * cell, x0 + 3 * cell, cell).Max);
    }

    [Fact]
    public void Quadtree_levels_follow_the_finest_spacing()
    {
        var q = new TerrainQuadtree(18, gridCells: 32);
        Assert.Equal(10, q.LevelCount);
        Assert.Equal(576, q.LeafSize);
        Assert.Equal(18, q.Spacing(0));
        Assert.Equal(WorldLayout.WorldSize, q.NodeSize(q.LevelCount - 1));
        Assert.Equal(7, new TerrainQuadtree(144).LevelCount);
        for (int l = 0; l + 1 < q.LevelCount; l++)
        {
            Assert.True(q.MorphStart[l] < q.MorphEnd[l] && q.MorphEnd[l] == q.Ranges[l]);
            Assert.True(q.Ranges[l] >= 2 * q.NodeSize(l));
            Assert.True(q.MorphStart[l + 1] >= q.Ranges[l], "a level starts morphing only beyond the finer level's range");
        }
    }

    [Theory]
    [InlineData(0, 300, 0)]
    [InlineData(12345, 2000, -70000)]
    [InlineData(146000, 50, 146000)]
    [InlineData(0, 40000, 0)]
    public void Selected_nodes_tile_the_world_and_meet_without_cracks(float x, float y, float z)
    {
        var q = new TerrainQuadtree(18, gridCells: 32);
        var eye = new Vector3(x, y, z);
        var nodes = new List<TerrainNode>();
        q.Select(eye, FlatBounds(), null, nodes);
        // Squares drawn (whole nodes or quarters) cover the world exactly once.
        var squares = nodes.Select(n => n.Quadrant < 0 ? (n.Level, X: n.X0, Z: n.Z0, S: n.Size) : (n.Level, X: n.X0 + (n.Quadrant & 1) * n.Size / 2, Z: n.Z0 + (n.Quadrant >> 1) * n.Size / 2, S: n.Size / 2)).ToList();
        Assert.Equal((double)WorldLayout.WorldSize * WorldLayout.WorldSize, squares.Sum(s => s.S * s.S), 1);
        // Below the eye, the finest level whose range reaches the ground is drawn (level 0 when the eye is low).
        int finest = Enumerable.Range(0, q.LevelCount).First(l => q.Ranges[l] > y);
        Assert.True(squares.Min(s => s.Level) <= finest, $"finest drawn level {squares.Min(s => s.Level)}, expected {finest}");
        foreach (var a in squares)
            foreach (var b in squares)
            {
                if (a.Level >= b.Level) continue;
                // Touching squares (sharing part of an edge) differ by one level at most, and on the shared edge the
                // finer one's odd vertices have fully slid onto the coarser grid.
                bool touchX = (Math.Abs(a.X + a.S - b.X) < 1e-6 || Math.Abs(b.X + b.S - a.X) < 1e-6) && a.Z < b.Z + b.S && b.Z < a.Z + a.S;
                bool touchZ = (Math.Abs(a.Z + a.S - b.Z) < 1e-6 || Math.Abs(b.Z + b.S - a.Z) < 1e-6) && a.X < b.X + b.S && b.X < a.X + a.S;
                if (!touchX && !touchZ) continue;
                Assert.True(b.Level - a.Level == 1, $"levels {a.Level} and {b.Level} touch");
                double spacing = q.Spacing(a.Level);
                for (int k = 1; k < 2 * q.GridCells * a.S / q.NodeSize(a.Level); k += 2)
                {
                    double px, pz;
                    if (touchX) { px = Math.Abs(a.X + a.S - b.X) < 1e-6 ? a.X + a.S : a.X; pz = a.Z + k * spacing; if (pz <= b.Z || pz >= b.Z + b.S) continue; }
                    else { pz = Math.Abs(a.Z + a.S - b.Z) < 1e-6 ? a.Z + a.S : a.Z; px = a.X + k * spacing; if (px <= b.X || px >= b.X + b.S) continue; }
                    float d = Vector3.Distance(eye, new Vector3((float)px, 0, (float)pz));
                    Assert.Equal(1f, q.Morph(a.Level, d));
                }
                // ... while the coarser one's vertices there have not started sliding to the next level yet.
                double coarse = q.Spacing(b.Level);
                for (int k = 1; k < 2 * q.GridCells; k += 2)
                {
                    double px, pz;
                    if (touchX) { px = Math.Abs(a.X + a.S - b.X) < 1e-6 ? b.X : b.X + b.S; pz = b.Z + k * coarse; if (pz <= a.Z || pz >= a.Z + a.S) continue; }
                    else { pz = Math.Abs(a.Z + a.S - b.Z) < 1e-6 ? b.Z : b.Z + b.S; px = b.X + k * coarse; if (px <= a.X || px >= a.X + a.S) continue; }
                    float d = Vector3.Distance(eye, new Vector3((float)px, 0, (float)pz));
                    Assert.Equal(0f, q.Morph(b.Level, d));
                }
            }
    }

    [Fact]
    public void Culled_nodes_are_left_out()
    {
        var q = new TerrainQuadtree(18);
        var all = new List<TerrainNode>();
        var half = new List<TerrainNode>();
        q.Select(new Vector3(0, 500, 0), FlatBounds(), null, all);
        q.Select(new Vector3(0, 500, 0), FlatBounds(), (min, max) => max.X > 0, half);
        Assert.True(half.Count > 0 && half.Count < all.Count);
        Assert.All(half, n => Assert.True(n.X0 + n.Size > 0));
    }
    [Fact]
    public void Heightmap_window_reads_strided_samples_and_clamps_at_the_edge()
    {
        static ushort Pattern(int c, int r) => (ushort)(r * 100 + c);
        using var map = TerrainHeightmap.Open(new MemoryStream(TerrainTests.Tiff(9, 9, Pattern)));
        var w = map.ReadWindow(1, 2, 3, 2, step: 3);
        Assert.Equal([Pattern(1, 2), Pattern(4, 2), Pattern(7, 2), Pattern(1, 5), Pattern(4, 5), Pattern(7, 5)], w.Raw);
        var edge = map.ReadWindow(-2, 7, 2, 2, step: 2);
        Assert.Equal([Pattern(0, 7), Pattern(0, 7), Pattern(0, 8), Pattern(0, 8)], edge.Raw);
    }

    [Fact]
    [Slow]
    public void Base_game_window_matches_point_samples()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        using var map = TerrainHeightmap.Open(install!);
        var w = map.ReadWindow(8000, 9000, 5, 4, step: 7);
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 5; i++)
                Assert.Equal(map.Sample(8000 + i * 7, 9000 + j * 7), w.Raw[j * 5 + i]);
        var (x, z) = w.WorldOf(2, 3);
        Assert.Equal(map.HeightAt(x, z), w.Height(2, 3), 2);
    }
}
