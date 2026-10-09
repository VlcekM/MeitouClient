using System.Numerics;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>Card trimming (docs/render-foliage.md, "Card trimming"): the alpha mask, the hull and its simplification, the clip, and the trimmed geometry of a quad.</summary>
public class FoliageCardTrimTests
{
    /// <summary>A mask of <paramref name="size"/> x <paramref name="size"/> cells with one level in which the cells <paramref name="set"/> says are set.</summary>
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
            var a = convex[i]; var b = convex[(i + 1) % convex.Length];
            if (sign * ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) < -tolerance * (b - a).Length()) return false;
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
    public void A_simplified_hull_still_contains_the_hull_with_at_most_the_cap_vertices()
    {
        // A circle of 40 points.
        var circle = FoliageCardTrimmer.ConvexHull([.. Enumerable.Range(0, 40).Select(i => new Vector2(MathF.Cos(i * MathF.Tau / 40), MathF.Sin(i * MathF.Tau / 40)) * 20 + new Vector2(30, 30))]);
        foreach (int cap in new[] { 3, 4, 6, 8 })
        {
            var simple = FoliageCardTrimmer.Simplify(circle, cap);
            Assert.True(simple.Length <= cap, $"cap {cap} gave {simple.Length}");
            foreach (var p in circle) Assert.True(Inside(simple, p, 1e-2f), $"cap {cap}: {p} outside");
            Assert.True(FoliageCardTrimmer.SignedArea(simple) >= FoliageCardTrimmer.SignedArea(circle) - 1e-3f);
        }
        // More vertices never mean more area.
        Assert.True(FoliageCardTrimmer.SignedArea(FoliageCardTrimmer.Simplify(circle, 8)) <= FoliageCardTrimmer.SignedArea(FoliageCardTrimmer.Simplify(circle, 4)));
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

    [Fact]
    public void The_hull_of_a_mask_holds_the_set_cells_under_the_card_and_grows_by_the_margin()
    {
        var mask = Mask(64, (x, y) => x is >= 20 and < 40 && y is >= 10 and < 50);   // a bar
        Vector2[] card = [new(0, 0), new(64, 0), new(64, 64), new(0, 64)];
        var hull = FoliageCardTrimmer.Hull(card, mask, 0, 0);
        Assert.Equal(20 * 40, Math.Abs(FoliageCardTrimmer.SignedArea(hull)), 1e-2);
        var grown = FoliageCardTrimmer.Hull(card, mask, 0, 1);
        Assert.Equal(22 * 42, Math.Abs(FoliageCardTrimmer.SignedArea(grown)), 1e-2);
        // A card over a bit of the texture with nothing set has no hull at all.
        Assert.Empty(FoliageCardTrimmer.Hull([new(0, 0), new(10, 0), new(10, 5), new(0, 5)], mask, 0, 0));
    }

    [Fact]
    public void A_card_over_a_part_of_the_texture_only_sees_the_cells_it_overlaps()
    {
        // Two bars; the card covers the left half: the right bar is not in its hull.
        var mask = Mask(64, (x, y) => (x is >= 5 and < 10 || x is >= 50 and < 60) && y is >= 10 and < 30);
        Vector2[] card = [new(0, 0), new(32, 0), new(32, 64), new(0, 64)];
        var hull = FoliageCardTrimmer.Hull(card, mask, 0, 0);
        Assert.Equal(5 * 20, Math.Abs(FoliageCardTrimmer.SignedArea(hull)), 1e-2);
    }

    [Fact]
    public void Coarser_mask_levels_contain_the_finer_and_a_single_opaque_texel_spreads_with_the_mips()
    {
        var alpha = new byte[64 * 64];
        for (int y = 30; y < 34; y++) for (int x = 30; x < 34; x++) alpha[y * 64 + x] = 255;   // a 4 x 4 blob
        var mask = FoliageCardMask.FromAlpha(alpha, 64, 64, 128, coarsest: 4);
        Assert.True(mask.LevelCount >= 4);
        for (int l = 1; l < mask.LevelCount; l++)
            for (int i = 0; i < mask.Levels[l].Length; i++) Assert.True(!mask.Levels[l - 1][i] || mask.Levels[l][i]);
        Assert.True(mask.Coverage(0) < mask.Coverage(2));
        // Level 0: the blob and one texel around it (6 x 6); its centre is set, a texel two away is not.
        Assert.True(mask.Levels[0][32 * 64 + 32]);
        Assert.True(mask.Levels[0][29 * 64 + 29]);
        Assert.False(mask.Levels[0][27 * 64 + 27]);
        // Each mip is the average of 2 x 2: the blob keeps 255 at mip 1 (2 x 2 inside), and level 1 holds it with its texel around.
        Assert.True(mask.Levels[1][27 * 64 + 27 + 1] || mask.Levels[1][28 * 64 + 28]);
    }

    [Fact]
    public void A_mask_survives_the_disk_cache()
    {
        var mask = Mask(48, (x, y) => (x * 7 + y * 3) % 5 == 0, levels: 3);
        var path = Path.Combine(Path.GetTempPath(), $"meitou-trim-{Guid.NewGuid():N}.mtrm");
        try
        {
            Assert.True(FoliageCardMask.Write(path, mask) > 0);
            var back = FoliageCardMask.TryRead(path);
            Assert.NotNull(back);
            Assert.Equal((mask.Width, mask.Height, mask.LevelCount), (back.Width, back.Height, back.LevelCount));
            for (int l = 0; l < mask.LevelCount; l++) Assert.Equal(mask.Levels[l], back.Levels[l]);
        }
        finally { File.Delete(path); }
        Assert.Null(FoliageCardMask.TryRead(path));
    }

    /// <summary>A leaf quad in the x-y plane, 10 x 10 units, facing +z, with the texture over it (the quad's two triangles share the diagonal).</summary>
    static ModelPart Quad(Vector3? offset = null, bool duplicateCorners = false)
    {
        var o = offset ?? Vector3.Zero;
        Vertex V(float x, float y) => new()
        {
            Position = o + new Vector3(x * 10, y * 10, 0), Normal = Vector3.UnitZ, Uv = new Vector2(x, 1 - y), Tangent = new Vector4(1, 0, 0, 1), Colour = new Vector4(1, 1, 1, 0.5f + 0.5f * y),
        };
        Vertex[] v = [V(0, 0), V(1, 0), V(1, 1), V(0, 1)];
        if (duplicateCorners) v = [.. v, V(0, 0), V(1, 1)];
        uint[] i = duplicateCorners ? [0, 1, 2, 4, 5, 3] : [0, 1, 2, 0, 2, 3];
        return new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = v, Indices = i, HasUv = true, HasColours = true, HasTangents = true };
    }

    static readonly FoliageCardTrimOptions Plain = new() { MaxVertices = 6, TriangleCost = 0.05f, MinSaving = 0.1f, Margin = 0.25f, MinPixels = 16 };

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
            // The original vertices are kept as they were; every new one is the card's own interpolation: on the plane, inside the card, colour by height, a unit normal.
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
                var a = result.Vertices[result.Indices[t]].Position; var b = result.Vertices[result.Indices[t + 1]].Position; var c = result.Vertices[result.Indices[t + 2]].Position;
                Assert.True(Vector3.Cross(b - a, c - a).Z > 0, "winding flipped");
            }
            // Nothing outside what the mask lets through (cells 32..64 of 96 across, 8..88 down: u 0.333..0.667, v 0.083..0.917) is drawn, less the margin.
            var areaAfter = 0f;
            for (int t = 0; t < result.Indices.Length; t += 3)
            {
                var a = result.Vertices[result.Indices[t]].Position; var b = result.Vertices[result.Indices[t + 1]].Position; var c = result.Vertices[result.Indices[t + 2]].Position;
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

    [Fact]
    public void A_triangle_pair_that_is_not_one_affine_card_is_trimmed_triangle_by_triangle()
    {
        // A kite: the second triangle's far corner is not where the first one's map puts it (its position is lifted), so they are two cards.
        var part = Quad();
        var vertices = (Vertex[])part.Vertices.Clone();
        vertices[3].Position += new Vector3(0, 0, 5);
        var kite = new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = vertices, Indices = part.Indices, HasUv = true, HasColours = true, HasTangents = true };
        var result = FoliageCardTrimmer.Trim(kite, Mask(64, (x, y) => x is >= 8 and < 20 && y is >= 40 and < 56), Plain);
        Assert.NotNull(result);
        Assert.Equal(2, result.Stats.Units);
        Assert.All(result.Indices, i => Assert.True(i < result.Vertices.Length));
    }

    [Fact]
    public void A_card_in_another_repeat_of_the_texture_is_trimmed_and_keeps_its_coordinates()
    {
        var mask = Mask(64, (x, y) => x is >= 16 and < 48 && y is >= 16 and < 48);
        var part = Quad();
        var vertices = part.Vertices.Select(v => { var w = v; w.Uv += new Vector2(-2, 3); return w; }).ToArray();
        var shifted = new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = vertices, Indices = part.Indices, HasUv = true, HasColours = true, HasTangents = true };
        var result = FoliageCardTrimmer.Trim(shifted, mask, Plain);
        Assert.NotNull(result);
        Assert.Equal(1, result.Stats.Trimmed);
        foreach (var v in result.Vertices.Skip(4))
        {
            Assert.InRange(v.Uv.X, -2 + 0.2, -2 + 0.8);
            Assert.InRange(v.Uv.Y, 3 + 0.2, 3 + 0.8);
        }
    }

    [Fact]
    public void Bigger_cards_may_use_a_coarser_mask_level_than_small_ones()
    {
        // Level 0 holds a bar; level 1 holds it fatter. A card 512 cells across picks a coarser level than one 16 across.
        var fine = new bool[512 * 512];
        var coarse = new bool[512 * 512];
        for (int y = 100; y < 400; y++)
            for (int x = 200; x < 300; x++) { fine[y * 512 + x] = true; for (int d = -20; d <= 20; d++) coarse[y * 512 + Math.Clamp(x + d, 0, 511)] = true; }
        var mask = new FoliageCardMask(512, 512, [fine, coarse, coarse, coarse, coarse, coarse]);
        var big = FoliageCardTrimmer.Outline([new(0, 0), new(512, 0), new(512, 512), new(0, 512)], mask, level: 3, Plain, 2);
        var near = FoliageCardTrimmer.Outline([new(0, 0), new(512, 0), new(512, 512), new(0, 512)], mask, level: 0, Plain, 2);
        Assert.NotNull(big);
        Assert.NotNull(near);
        Assert.True(Math.Abs(FoliageCardTrimmer.SignedArea(near)) < Math.Abs(FoliageCardTrimmer.SignedArea(big)));
    }
}
