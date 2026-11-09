using System.Numerics;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Tests.Rendering;

/// <summary>
/// Card trimming (docs/render-foliage.md, "Card trimming"): the alpha mask, the hull and its simplification, the clip, and the trimmed geometry of a
/// quad.
/// </summary>
public class FoliageCardTrimTests
{
    static readonly FoliageCardTrimOptions Plain = new() { MaxVertices = 6, TriangleCost = 0.05f, MinSaving = 0.1f, Margin = 0.25f, MinPixels = 16 };

    /// <summary>
    /// A mask of <paramref name="size"/> x <paramref name="size"/> cells with one level in which the cells <paramref name="set"/> says are set.
    /// </summary>
    static FoliageCardMask Mask(int size, Func<int, int, bool> set, int levels = 1)
    {
        var cells = new bool[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) cells[y * size + x] = set(x, y);
        return new FoliageCardMask(size, size, [.. Enumerable.Repeat(cells, levels)]);
    }

    static bool Inside(Vector2[] convex, Vector2 p, float tolerance = 1e-3f)
    {
        float sign = FoliageCardTrimmer.SignedArea(convex) >= 0 ? 1 : -1;
        for (int i = 0; i < convex.Length; i++)
        {
            var a = convex[i];
            var b = convex[(i + 1) % convex.Length];
            if (sign * ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) < -tolerance * (b - a).Length())
                return false;
        }
        return true;
    }

    [Fact]
    public void The_convex_hull_contains_every_point_and_drops_inner_ones()
    {
        var random = new Random(3);
        var points = Enumerable.Range(0, 200).Select(_ => new Vector2(random.NextSingle() * 10, random.NextSingle() * 6)).ToList();
        var hull = FoliageCardTrimmer.ConvexHull(points);

        Assert.InRange(hull.Length, 3, 40);

        foreach (var p in points) Assert.True(Inside(hull, p), $"{p} outside the hull");

        Assert.All(hull, h => Assert.Contains(h, points));
    }

    [Fact]
    public void A_clip_stays_inside_the_card_whichever_way_either_winds()
    {
        Vector2[] card = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        Vector2[] big = [new(-5, 5), new(5, -5), new(15, 5), new(5, 15)];
        foreach (var (polygon, c) in new[] { (big, card), (big.Reverse().ToArray(), card), (big, card.Reverse().ToArray()) })
        {
            var clipped = FoliageCardTrimmer.Clip(polygon, c);

            Assert.True(clipped.Length >= 3);
            Assert.All(clipped, p => Assert.True(Inside(card, p), $"{p} outside the card"));
            Assert.True(Math.Abs(FoliageCardTrimmer.SignedArea(clipped)) <= 100f + 1e-3f);
        }

        Assert.Empty(FoliageCardTrimmer.Clip([new(20, 20), new(30, 20), new(25, 30)], card));
    }

    /// <summary>
    /// A leaf quad in the x-y plane, 10 x 10 units, facing +z, with the texture over it (the quad's two triangles share the diagonal).
    /// </summary>
    static ModelPart Quad(Vector3? offset = null, bool duplicateCorners = false)
    {
        var o = offset ?? Vector3.Zero;
        Vertex V(float x, float y) => new()
        {
            Position = o + new Vector3(x * 10, y * 10, 0), Normal = Vector3.UnitZ, Uv = new Vector2(x, 1 - y), Tangent = new Vector4(1, 0, 0, 1),
                    Colour = new Vector4(1, 1, 1, 0.5f + 0.5f * y),
        };
        Vertex[] v = [V(0, 0), V(1, 0), V(1, 1), V(0, 1)];
        if (duplicateCorners) v = [.. v, V(0, 0), V(1, 1)];
        uint[] i = duplicateCorners ? [0, 1, 2, 4, 5, 3] : [0, 1, 2, 0, 2, 3];
        return new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = v, Indices = i, HasUv = true, HasColours = true, HasTangents = true };
    }

    [Fact]
    public void A_quad_with_a_small_leaf_becomes_a_smaller_polygon_with_interpolated_corners()
    {
        // A leaf: the middle third of the texture, taller than wide.
        var mask = Mask(96, (x, y) => x is >= 32 and < 64 && y is >= 8 and < 88);
        var part = Quad(new Vector3(100, 0, 0));
        foreach (bool duplicated in new[] { false, true })
        {
            part = Quad(new Vector3(100, 0, 0), duplicated);
            var result = FoliageCardTrimmer.Trim(part, mask, Plain);

            Assert.NotNull(result);
            Assert.Equal(1, result.Stats.Trimmed);
            Assert.Equal(1, result.Stats.Units);   // one card, though two triangles (found by position and coordinate, not number)
            Assert.True(result.Stats.WorldAreaAfter < 0.5 * result.Stats.WorldAreaBefore, $"{result.Stats}");
            Assert.True(result.Vertices.Length > part.Vertices.Length);
            Assert.True(result.Indices.Length % 3 == 0 && result.Indices.Length >= 3);
            Assert.All(result.Indices, i => Assert.True(i < result.Vertices.Length));

            // The original vertices are kept as they were; every new one is the card's own interpolation: on the plane, inside the card, colour by
            // height, a unit normal.
            for (int k = 0; k < part.Vertices.Length; k++) Assert.Equal(part.Vertices[k], result.Vertices[k]);
            foreach (var v in result.Vertices.Skip(part.Vertices.Length))
            {
                Assert.Equal(0, v.Position.Z, 1e-3);
                Assert.InRange(v.Position.X, 100 - 1e-3, 110 + 1e-3);
                Assert.InRange(v.Position.Y, -1e-3, 10 + 1e-3);
                Assert.Equal(v.Position.X - 100, v.Uv.X * 10, 1e-3);
                Assert.Equal(10 - v.Uv.Y * 10, v.Position.Y, 1e-3);
                Assert.Equal(0.5f + 0.05f * v.Position.Y, v.Colour.W, 1e-3);
                Assert.Equal(1, v.Normal.Length(), 1e-4);
            }

            // The same winding as the card's first triangle (facing +z), every triangle of it.
            for (int t = 0; t < result.Indices.Length; t += 3)
            {
                var a = result.Vertices[result.Indices[t]].Position;
                var b = result.Vertices[result.Indices[t + 1]].Position;
                var c = result.Vertices[result.Indices[t + 2]].Position;

                Assert.True(Vector3.Cross(b - a, c - a).Z > 0, "winding flipped");
            }

            // Nothing outside what the mask lets through (cells 32..64 of 96 across, 8..88 down: u 0.333..0.667, v 0.083..0.917) is drawn, less the
            // margin.
            var areaAfter = 0f;
            for (int t = 0; t < result.Indices.Length; t += 3)
            {
                var a = result.Vertices[result.Indices[t]].Position;
                var b = result.Vertices[result.Indices[t + 1]].Position;
                var c = result.Vertices[result.Indices[t + 2]].Position;
                areaAfter += Vector3.Cross(b - a, c - a).Length() / 2;
            }

            Assert.Equal(result.Stats.WorldAreaAfter, areaAfter, 0.5);
        }
    }

    [Fact]
    public void A_quad_all_opaque_is_left_alone_and_a_fully_transparent_one_is_dropped()
    {
        var part = Quad();
        var opaque = FoliageCardTrimmer.Trim(part, Mask(32, (_, _) => true), Plain);

        Assert.NotNull(opaque);
        Assert.Equal(0, opaque.Stats.Trimmed);
        Assert.Same(part.Vertices, opaque.Vertices);
        Assert.Same(part.Indices, opaque.Indices);

        var clear = FoliageCardTrimmer.Trim(part, Mask(32, (_, _) => false), Plain);

        Assert.NotNull(clear);
        Assert.Equal(1, clear.Stats.Removed);
        Assert.Empty(clear.Indices);
    }
}
