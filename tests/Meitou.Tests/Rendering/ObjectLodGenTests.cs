using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Rendering;
using Meitou.Rendering.Characters;

namespace Meitou.Tests.Rendering;

/// <summary>Generated object levels (<see cref="ObjectLodGen"/>, docs/render-objects.md "Generated levels"): where they are placed among the file's, and the simplifier's texture guard. No GPU, no game.</summary>
public class ObjectLodGenTests
{
    static FoliageLodSet Set(int[] triangles, float[] errors, float[]? angles = null) => new()
    {
        Indices = [],
        Errors = errors,
        NormalAngles = angles ?? new float[errors.Length],
        Triangles = triangles,
    };

    const float Ppr = 1158f;   // 1080 lines, 50 degrees

    [Fact]
    public void Generated_distances_rise_with_the_level_and_come_sooner_where_triangles_are_tiny()
    {
        // A mesh of radius 100 with 13 000 triangles of edge 0.5 (tiny), levels of half, a quarter and an eighth.
        var set = Set([13000, 6500, 3250, 1625], [0, 0.4f, 1.2f, 3f]);
        var d = ObjectLodGen.Distances(set, 0.5f, 100, Ppr);
        Assert.Equal(0, d[0]);
        for (int k = 1; k < d.Length; k++) Assert.True(d[k] > d[k - 1]);
        // Without the sub-pixel push (triangles said to be large) the first level waits for its own deviation: 0.4 / 100 radii at 1.5 px.
        var large = ObjectLodGen.Distances(set, 50f, 100, Ppr);
        for (int k = 1; k < d.Length; k++) Assert.True(d[k] <= large[k] + 1e-3f);
        float plain = 0.4f / 100 * 100 * Ppr / ObjectLodGen.Tolerance - 100;
        Assert.Equal(plain, large[1], plain * 0.01f);
        Assert.True(d[3] < large[3]);
    }

    [Fact]
    public void A_mesh_without_file_levels_gets_the_generated_ones_and_the_plain_curve_stays_whole()
    {
        var plan = ObjectLodGen.Merge([0], [1000], [0, 300, 900, 2000], [1000, 500, 250, 120], Set([1000, 500, 250, 120], [0, 1, 2, 3]));
        Assert.Equal(4, plan.Levels.Length);
        Assert.All(plan.Levels.Skip(1), l => Assert.True(l.Generated));
        Assert.Equal(new float[] { 0, 300, 900, 2000 }, plan.Curve.All);
        Assert.Equal(0, plan.Curve.LevelFor(100));
        Assert.Equal(1, plan.Curve.Select(301));
        Assert.Equal(3, plan.Curve.Select(1e6f));
    }

    [Fact]
    public void File_levels_stay_in_the_plain_curve_and_a_generated_level_that_undercuts_the_next_file_level_replaces_it()
    {
        // File levels at 4000 (500 triangles) and 8000 (250); generated at 300 (500), 900 (250), 2000 (120).
        var plan = ObjectLodGen.Merge([0, 4000, 8000], [1000, 500, 250], [0, 300, 900, 2000], [1000, 500, 250, 120], Set([1000, 500, 250, 120], [0, 1, 2, 3]));
        // The generated order by distance: g300 (500), g900 (250), g2000 (120); the file's 4000 (500) and 8000 (250) are not finer than the one before, so they drop out of it.
        var generated = Enumerable.Range(0, 20).Select(i => plan.Curve.Select(i * 1000f)).Distinct().Select(l => plan.Levels[l]).ToList();
        Assert.DoesNotContain(generated, l => !l.Generated && l.Source > 0);
        Assert.Equal(2000, generated.Last().Distance);
        // Switched off, only the file's levels remain, by the file's distances.
        var plain = ObjectLodGen.Merge([0, 4000, 8000], [1000, 500, 250], [0], [1000], Set([1000], [0]));
        Assert.Equal(3, plain.Levels.Length);
        Assert.Equal(1, plain.Curve.Select(4001));
        Assert.Equal(2, plain.Curve.Select(8001));
    }

    [Fact]
    public void Generated_levels_switch_at_their_distance_and_file_levels_still_cross_fade()
    {
        var plan = ObjectLodGen.Merge([0, 4000], [1000, 500], [0, 300, 900], [1000, 400, 150], Set([1000, 400, 150], [0, 1, 2]));
        // Just before the generated level at 300 the file's rule would blend: here the pair has a generated level, so it is a plain switch.
        var near = plan.Curve.Blend(299);
        Assert.False(near.IsBlending);
        Assert.Equal(0, near.Lower);
        Assert.Equal(1, plan.Curve.Blend(301).Lower);
        Assert.False(plan.Curve.Blend(301).IsBlending);
        // A mesh of file levels only (no plan) keeps the game's blend around 4000.
        var file = LodCurve.Of([0, 4000, 8000]);
        Assert.True(file.Blend(3990).IsBlending);
    }

    [Fact]
    public void A_generated_level_must_gain_enough_over_the_one_before_it()
    {
        // 1000 -> 900 triangles is not worth a level; 1000 -> 800 is.
        var plan = ObjectLodGen.Merge([0], [1000], [0, 100, 200], [1000, 900, 800], Set([1000, 900, 800], [0, 1, 2]));
        Assert.Equal(2, plan.Levels.Length);
        Assert.Equal(800, plan.Levels[1].Triangles);
    }

    [Fact]
    public void The_file_triangles_of_a_level_without_indices_for_a_submesh_repeat_the_level_before()
    {
        var part = new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = new Vertex[6], Indices = [0, 1, 2, 3, 4, 5] };
        var model = new Model();
        model.Parts.Add(part);
        var levels = new List<MeshLodLevel>
        {
            new(0, [part.Indices]),
            new(4000, [new uint[] { 0, 1, 2 }]),
            new(8000, [null]),
        };
        Assert.Equal(new[] { 2, 1, 1 }, ObjectLodGen.FileTriangles(model, levels));
    }

    // Two flat islands with their own texture coordinates side by side, joined along a seam whose points are duplicated (as an unwrapped mesh does).
    static (Vertex[] Vertices, uint[] Indices) TwoIslands(int n)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        int Add(float x, float y, float uOffset)
        {
            vertices.Add(new Vertex { Position = new Vector3(x, y, 0), Normal = Vector3.UnitZ, Uv = new Vector2(x / n + uOffset, y / n) });
            return vertices.Count - 1;
        }
        int half = n / 2;
        var left = new int[half + 1, n + 1];
        var right = new int[n - half + 1, n + 1];
        for (int y = 0; y <= n; y++)
        {
            for (int x = 0; x <= half; x++) left[x, y] = Add(x, y, 0);
            for (int x = half; x <= n; x++) right[x - half, y] = Add(x, y, 10);
        }
        void Quad(int a, int b, int c, int d) => indices.AddRange([(uint)a, (uint)c, (uint)b, (uint)b, (uint)c, (uint)d]);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < half; x++) Quad(left[x, y], left[x + 1, y], left[x, y + 1], left[x + 1, y + 1]);
            for (int x = 0; x < n - half; x++) Quad(right[x, y], right[x + 1, y], right[x, y + 1], right[x + 1, y + 1]);
        }
        return ([.. vertices], [.. indices]);
    }

    [Fact]
    public void The_uv_guard_keeps_a_triangle_from_taking_corners_of_two_islands()
    {
        var (vertices, indices) = TwoIslands(16);
        float uvPerWorld = MeshSimplifier.UvPerWorld(vertices, indices);
        Assert.Equal(1f / 16, uvPerWorld, 1e-4f);
        var chain = MeshSimplifier.Chain(vertices, indices, [0.5f, 0.25f], 8, uvPerWorld);
        foreach (var level in chain)
        {
            Assert.True(level.Length / 3 < indices.Length / 3);
            for (int t = 0; t < level.Length; t += 3)
            {
                float lo = Math.Min(vertices[level[t]].Uv.X, Math.Min(vertices[level[t + 1]].Uv.X, vertices[level[t + 2]].Uv.X));
                float hi = Math.Max(vertices[level[t]].Uv.X, Math.Max(vertices[level[t + 1]].Uv.X, vertices[level[t + 2]].Uv.X));
                Assert.True(hi - lo < 5, "a triangle spans both islands");
            }
        }
    }
}
