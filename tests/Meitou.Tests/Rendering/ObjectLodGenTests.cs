using System.Numerics;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Tests.Rendering;

/// <summary>
/// Generated object levels (<see cref="ObjectLodGen"/>, docs/render-objects.md "Generated levels"): where they are placed among the file's, and the
/// simplifier's texture guard. No GPU, no game.
/// </summary>
public class ObjectLodGenTests
{
    static FoliageLodSet Set(int[] triangles, float[] errors, float[]? angles = null) => new()
    {
        Indices = [],
        Errors = errors,
        NormalAngles = angles ?? new float[errors.Length],
        Triangles = triangles,
    };

    [Fact]
    public void File_levels_stay_in_the_plain_curve_and_a_generated_level_that_undercuts_the_next_file_level_replaces_it()
    {
        // File levels at 4000 (500 triangles) and 8000 (250); generated at 300 (500), 900 (250), 2000 (120).
        var plan = ObjectLodGen.Merge([0, 4000, 8000], [1000, 500, 250], [0, 300, 900, 2000], [1000, 500, 250, 120],
                Set([1000, 500, 250, 120], [0, 1, 2, 3]));

        // The generated order by distance: g300 (500), g900 (250), g2000 (120); the file's 4000 (500) and 8000 (250) are not finer than the one
        // before, so they drop out of it.
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
