using System.Numerics;
using Meitou.Data.Physics;

namespace Meitou.Tests.Physics;

public class CollisionShapeTests
{
    static CollisionShape Shape(CollisionShapeKind kind, Pose? pose = null, Vector3 half = default, float radius = 0, float height = 0, CookedMesh? mesh = null) =>
        new() { Kind = kind, Pose = pose ?? Pose.Identity, HalfExtents = half, Radius = radius, Height = height, Mesh = mesh };

    /// <summary>Volume enclosed by the triangles; positive when they face outward under cross(v1 - v0, v2 - v0).</summary>
    static float SignedVolume(List<Vector3> v, List<int> i)
    {
        float vol = 0;
        for (int t = 0; t < i.Count; t += 3) vol += Vector3.Dot(v[i[t]], Vector3.Cross(v[i[t + 1]], v[i[t + 2]])) / 6;
        return vol;
    }

    [Fact]
    public void Box_is_twelve_outward_triangles()
    {
        var v = new List<Vector3>();
        var i = new List<int>();
        Assert.Equal(12, CollisionTriangulator.Triangulate(Shape(CollisionShapeKind.Box, half: new(1, 2, 3)), v, i));
        Assert.Equal(8, v.Count);
        Assert.Equal(2 * 4 * 6, SignedVolume(v, i), 3);
    }

    [Fact]
    public void Capsule_is_a_sixteen_sided_prism_along_local_y()
    {
        var v = new List<Vector3>();
        var i = new List<int>();
        CollisionTriangulator.Triangulate(Shape(CollisionShapeKind.Capsule, radius: 2, height: 6), v, i);
        Assert.Equal(32, v.Count);
        Assert.Equal(-5, v.Min(p => p.Y), 3);
        Assert.Equal(5, v.Max(p => p.Y), 3);
        Assert.All(v, p => Assert.Equal(2, MathF.Sqrt(p.X * p.X + p.Z * p.Z), 3));
        float expected = 0.5f * 16 * MathF.Sin(MathF.Tau / 16) * 4 * 10; // area of a regular 16-gon of circumradius 2 times the length
        Assert.Equal(expected, SignedVolume(v, i), 2);
    }

    [Fact]
    public void Convex_hull_of_cube_corners_is_twelve_triangles_and_ignores_inner_points()
    {
        var pts = new List<Vector3>();
        for (int k = 0; k < 8; k++) pts.Add(new((k & 1) * 2 - 1, ((k >> 1) & 1) * 2 - 1, ((k >> 2) & 1) * 2 - 1));
        pts.Add(Vector3.Zero);
        pts.Add(new(0.5f, 0.2f, -0.1f));
        var mesh = new CookedMesh(pts.ToArray(), []);
        var v = new List<Vector3>();
        var i = new List<int>();
        Assert.Equal(12, CollisionTriangulator.Triangulate(Shape(CollisionShapeKind.Convex, mesh: mesh), v, i));
        Assert.Equal(8, SignedVolume(v, i), 3);
    }

    [Fact]
    public void Convex_hull_of_random_points_is_closed_and_outward()
    {
        var rng = new Random(5);
        var pts = Enumerable.Range(0, 60).Select(_ => new Vector3((float)rng.NextDouble() * 10, (float)rng.NextDouble() * 4, (float)rng.NextDouble() * 6)).ToArray();
        var tris = ConvexHull.Triangles(pts);
        Assert.NotEmpty(tris);
        // Closed: every directed edge has its reverse once.
        var edges = new HashSet<(int, int)>();
        for (int t = 0; t < tris.Length; t += 3)
            foreach (var e in new[] { (tris[t], tris[t + 1]), (tris[t + 1], tris[t + 2]), (tris[t + 2], tris[t]) })
                Assert.True(edges.Add(e));
        foreach (var (a, b) in edges) Assert.Contains((b, a), edges);
        // Every point is on the inner side of every face.
        for (int t = 0; t < tris.Length; t += 3)
        {
            var n = Vector3.Normalize(Vector3.Cross(pts[tris[t + 1]] - pts[tris[t]], pts[tris[t + 2]] - pts[tris[t]]));
            foreach (var p in pts) Assert.True(Vector3.Dot(n, p - pts[tris[t]]) < 1e-2f);
        }
    }

    [Fact]
    public void Flat_or_tiny_point_sets_have_no_hull()
    {
        Assert.Empty(ConvexHull.Triangles([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)]));
        Assert.Empty(ConvexHull.Triangles([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]));
    }

    [Fact]
    public void Pose_rows_are_read_in_order_and_compose_inner_first()
    {
        // Rows (0 1 0), (-1 0 0), (0 0 1): x' = y, y' = -x. Then translate by (1, 2, 3).
        var a = Pose.Parse("0 1 0  -1 0 0  0 0 1  1 2 3");
        Assert.Equal(new Vector3(4 + 1, -5 + 2, 3), a.Apply(new(5, 4, 0)));
        var shift = Pose.Parse("1 0 0  0 1 0  0 0 1  10 0 0");
        Assert.Equal(a.Apply(shift.Apply(new(1, 1, 1))), a.Compose(shift).Apply(new(1, 1, 1)));
        Assert.Equal(shift.Apply(a.Apply(new(1, 1, 1))), shift.Compose(a).Apply(new(1, 1, 1)));
    }

    [Fact]
    public void Axis_map_is_a_rotation_taking_exporter_z_up_to_y_up()
    {
        Assert.Equal(new Vector3(0, 1, 0), CollisionTriangulator.ToWorldAxes(new(0, 0, 1)));
        Assert.Equal(new Vector3(0, 0, -1), CollisionTriangulator.ToWorldAxes(new(0, 1, 0)));
        var x = CollisionTriangulator.ToWorldAxes(Vector3.UnitX);
        var y = CollisionTriangulator.ToWorldAxes(Vector3.UnitY);
        var z = CollisionTriangulator.ToWorldAxes(Vector3.UnitZ);
        Assert.Equal(1, Vector3.Dot(Vector3.Cross(x, y), z), 5);
    }

    [Fact]
    public void Cooked_triangle_mesh_decodes_with_8_bit_indices()
    {
        // Header as in a real file: NXS\x01MESH, version 1, flags 0x0A, threshold, axis 0xFF, extent, 3 vertices, 1 triangle.
        var bytes = new List<byte>();
        bytes.AddRange("NXS\u0001MESH"u8.ToArray());
        void I(int v) => bytes.AddRange(BitConverter.GetBytes(v));
        void F(float v) => bytes.AddRange(BitConverter.GetBytes(v));
        I(1); I(0x0A); F(0.001f); I(0xFF); F(0); I(3); I(1);
        foreach (var f in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) F(f);
        bytes.AddRange([0, 1, 2]);
        var mesh = CookedMeshReader.ReadTriangleMesh(bytes.ToArray());
        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Equal([0, 1, 2], mesh.Indices);
    }

    [Fact]
    public void Broken_cooked_data_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => CookedMeshReader.ReadTriangleMesh("NXS\u0001CVXM"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => CookedMeshReader.ReadConvex("NXS\u0001CVXM\0\0\0\0"u8.ToArray()));
    }
}
