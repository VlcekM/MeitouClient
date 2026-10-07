using System.Numerics;
using Meitou.Rendering;
using Meitou.Rendering.Characters;

namespace Meitou.Tests.Characters;

public class MeshSimplifierTests
{
    static (Vertex[] Vertices, uint[] Indices) Grid(int n)
    {
        var vertices = new Vertex[(n + 1) * (n + 1)];
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                vertices[y * (n + 1) + x] = new Vertex { Position = new Vector3(x, y, (x * 7 + y * 3) % 5 == 0 ? 0.05f : 0), Normal = Vector3.UnitZ, Uv = new Vector2(x, y) / n, Weights = new Vector4(1, 0, 0, 0) };
        var indices = new List<uint>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                uint a = (uint)(y * (n + 1) + x), b = a + 1, c = a + (uint)(n + 1), d = c + 1;
                indices.AddRange([a, c, b, b, c, d]);
            }
        return (vertices, [.. indices]);
    }

    [Fact]
    public void Reduces_to_the_requested_fractions_and_keeps_valid_indices()
    {
        var (vertices, indices) = Grid(24);
        var chain = MeshSimplifier.Chain(vertices, indices, [0.5f, 0.25f, 0.1f], 20);
        int full = indices.Length / 3;
        Assert.InRange(chain[0].Length / 3, full * 0.4, full * 0.55);
        Assert.InRange(chain[1].Length / 3, full * 0.2, full * 0.3);
        Assert.True(chain[2].Length / 3 <= full * 0.15);
        foreach (var level in chain)
        {
            Assert.Equal(0, level.Length % 3);
            Assert.All(level, i => Assert.True(i < vertices.Length));
            for (int t = 0; t < level.Length; t += 3) Assert.True(level[t] != level[t + 1] && level[t + 1] != level[t + 2] && level[t] != level[t + 2]);
        }
    }

    [Fact]
    public void Stops_at_the_floor()
    {
        var (vertices, indices) = Grid(6);
        var chain = MeshSimplifier.Chain(vertices, indices, [0.01f], 30);
        Assert.True(chain[0].Length / 3 >= 20);
    }
}
