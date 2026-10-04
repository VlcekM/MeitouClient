using System.Numerics;
using Meitou.Content;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class TerrainMeshTests
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
    public void Flat_terrain_has_up_normals_and_every_triangle_faces_up()
    {
        var mesh = new TerrainMesh(Window(9, 9, (_, _) => 1000), cells: 8);
        var chunk = Assert.Single(mesh.Chunks());
        Assert.Equal(mesh.VerticesPerChunk, chunk.Vertices.Length);
        foreach (var v in chunk.Vertices) Assert.Equal(Vector3.UnitY, v.Normal);
        for (int lod = 0; lod < mesh.LodCount; lod++)
            AssertGridFacesUp(mesh, chunk, lod);
    }

    [Fact]
    public void Sloped_terrain_normals_lean_downhill_and_triangles_still_face_up()
    {
        // Height rises along +X by 18 units per sample (raw value per unit = 65535 / 9800): a 45 degree slope.
        float rawPerUnit = ushort.MaxValue / WorldLayout.MaxHeight;
        var mesh = new TerrainMesh(Window(17, 17, (i, j) => (ushort)Math.Round(i * 18 * rawPerUnit + (j % 3) * 50)), cells: 16);
        var chunk = Assert.Single(mesh.Chunks());
        var n = chunk.Vertices[8 * 17 + 8].Normal;
        Assert.True(n.X < -0.6 && n.Y > 0.6, $"normal {n} should lean towards -X");
        for (int lod = 0; lod < mesh.LodCount; lod++)
            AssertGridFacesUp(mesh, chunk, lod);
    }

    static void AssertGridFacesUp(TerrainMesh mesh, TerrainChunk chunk, int lod)
    {
        var indices = mesh.Indices[lod];
        int grid = TerrainMesh.GridTriangleCount(mesh.Cells, 1 << lod);
        Assert.True(indices.Length > grid * 3);
        for (int t = 0; t < grid; t++)
        {
            var a = chunk.Vertices[indices[3 * t]].Position;
            var b = chunk.Vertices[indices[3 * t + 1]].Position;
            var c = chunk.Vertices[indices[3 * t + 2]].Position;
            Assert.True(Vector3.Cross(b - a, c - a).Y > 0, $"LOD {lod} triangle {t} faces down");
        }
    }

    [Fact]
    public void Chunks_tile_the_window_and_skirts_hang_below_the_edges()
    {
        var mesh = new TerrainMesh(Window(17, 9, (i, j) => (ushort)(i * 100 + j)), cells: 8, skirtDepth: 50);
        Assert.Equal((2, 1), (mesh.ChunksX, mesh.ChunksZ));
        var chunks = mesh.Chunks().ToList();
        Assert.Equal([(0, 0), (8, 0)], chunks.Select(c => (c.I0, c.J0)));
        // The shared edge has the same positions in both chunks.
        for (int j = 0; j <= 8; j++)
            Assert.Equal(chunks[0].Vertices[j * 9 + 8].Position, chunks[1].Vertices[j * 9].Position);
        var skirt = chunks[0].Vertices[81]; // first skirt vertex: below grid vertex (0, 0)
        Assert.Equal(chunks[0].Vertices[0].Position - new Vector3(0, 50, 0), skirt.Position);
        Assert.Equal(4, mesh.LodCount);
        Assert.Equal(8 * 18f, mesh.ChunkSize);
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
