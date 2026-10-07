using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>Distance-based streaming of the object textures' top mips and the meshes' finest LOD levels (docs/renderer-native.md 8.12).</summary>
public class ObjectStreamingTests
{
    // 1600 x 900, 50 degrees: a pixel's footprint at distance d is at least d * cos^2(corner) / focal.
    static MipStreaming View()
    {
        var m = new MipStreaming();
        m.SetView(1600, 900, 50 * MathF.PI / 180, 0);
        return m;
    }

    [Fact]
    public void Nothing_is_dropped_until_the_view_is_known()
    {
        Assert.Equal(0, new MipStreaming().Drop(4096, 4096, 100));
    }

    [Fact]
    public void Near_users_keep_every_level_and_far_ones_lose_the_top_ones()
    {
        var m = View();
        // need = distance / world length of a texture-coordinate unit. A wall 100 units per texture seen from 50: texels are 0.024 units, pixels 0.03.
        Assert.Equal(0, m.Drop(4096, 4096, 0.5f));
        int mid = m.Drop(4096, 4096, 5);
        int far = m.Drop(4096, 4096, 400);
        Assert.InRange(mid, 1, 2);
        Assert.True(far > mid);
        // Never below 512 on the larger side: 4096 loses at most 3 levels.
        Assert.Equal(3, m.Drop(4096, 4096, 1e9f));
        Assert.Equal(1, m.Drop(1024, 1024, 1e9f));
        Assert.Equal(0, m.Drop(512, 512, 1e9f));
    }

    [Fact]
    public void Unknown_or_zero_need_drops_nothing()
    {
        var m = View();
        Assert.Equal(0, m.Drop(4096, 4096, float.PositiveInfinity));
        Assert.Equal(0, m.Drop(4096, 4096, 0));
        Assert.Equal(0, m.Drop(4096, 4096, float.NaN));
    }

    [Fact]
    public void The_bound_follows_the_footprint_of_a_pixel()
    {
        // The level of a surface is log2(pixel footprint / texel size) less the margin: doubling the distance adds one level, doubling the texture size too.
        var m = View();
        float need = 5;
        double level(int n, float nd) => Math.Log2(m.PixelsPerDistance * nd * n) - MipStreaming.Margin + m.Bias;
        Assert.Equal((int)Math.Floor(level(4096, need)), m.Drop(4096, 4096, need));
        Assert.Equal((int)Math.Floor(level(2048, need * 2)), m.Drop(2048, 2048, need * 2));
        Assert.True(m.Bias <= -0.25f);
    }

    [Fact]
    public void An_upscaler_bias_and_the_guard_keep_or_drop_more()
    {
        var plain = View();
        var biased = new MipStreaming();
        biased.SetView(1600, 900, 50 * MathF.PI / 180, -2f);
        Assert.True(biased.Drop(4096, 4096, 5) <= plain.Drop(4096, 4096, 5));
        int before = plain.Drop(4096, 4096, 3);
        plain.Extra = 1;
        Assert.Equal(Math.Min(before + 1, 3), plain.Drop(4096, 4096, 3));
    }

    [Fact]
    public void A_wider_picture_has_a_smaller_footprint()
    {
        var narrow = new MipStreaming();
        narrow.SetView(1600, 900, 30 * MathF.PI / 180, 0);
        var wide = new MipStreaming();
        wide.SetView(1600, 900, 90 * MathF.PI / 180, 0);
        Assert.True(wide.PixelsPerDistance > 0 && narrow.PixelsPerDistance < wide.PixelsPerDistance * 4);
        // A narrower field of view magnifies: more levels are needed at the same distance.
        Assert.True(narrow.Drop(4096, 4096, 3) <= wide.Drop(4096, 4096, 3));
    }

    static ModelPart Quad(float size, float uvSpan, int quads = 1)
    {
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        for (int i = 0; i < quads; i++)
        {
            uint b = (uint)vertices.Count;
            float x0 = i * size * 2;
            vertices.Add(new Vertex { Position = new Vector3(x0, 0, 0), Uv = new Vector2(0, 0) });
            vertices.Add(new Vertex { Position = new Vector3(x0 + size, 0, 0), Uv = new Vector2(uvSpan, 0) });
            vertices.Add(new Vertex { Position = new Vector3(x0 + size, 0, size), Uv = new Vector2(uvSpan, uvSpan) });
            vertices.Add(new Vertex { Position = new Vector3(x0, 0, size), Uv = new Vector2(0, uvSpan) });
            indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        return new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = [.. vertices], Indices = [.. indices], HasUv = true };
    }

    [Fact]
    public void Texel_scale_is_the_world_length_of_a_texture_unit()
    {
        // 10 units of surface over 1 unit of coordinates: 10; over 4 units (tiled): 2.5. Within the histogram's bucket (a quarter octave).
        float one = MeshTexelScale.Of(Quad(10, 1));
        float tiled = MeshTexelScale.Of(Quad(10, 4));
        Assert.InRange(one, 10f, 10f * 1.19f + 0.01f);
        Assert.InRange(tiled, 2.5f, 2.5f * 1.19f + 0.01f);
    }

    [Fact]
    public void Texel_scale_takes_the_stretched_direction()
    {
        // A strip 10 wide and 1000 long with the coordinates running 0..1 on both: the long side is stretched 100 times as much as the short one.
        var v = new[]
        {
            new Vertex { Position = new Vector3(0, 0, 0), Uv = new Vector2(0, 0) }, new Vertex { Position = new Vector3(10, 0, 0), Uv = new Vector2(1, 0) },
            new Vertex { Position = new Vector3(10, 0, 1000), Uv = new Vector2(1, 1) }, new Vertex { Position = new Vector3(0, 0, 1000), Uv = new Vector2(0, 1) },
        };
        var part = new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = v, Indices = [0, 1, 2, 0, 2, 3], HasUv = true };
        Assert.InRange(MeshTexelScale.Of(part), 1000f, 1000f * 1.19f + 1);   // not the 100 of the geometric mean
    }

    [Fact]
    public void Texel_scale_is_unknown_without_coordinates_or_with_them_collapsed()
    {
        var flat = Quad(10, 0);   // every coordinate is 0
        Assert.True(float.IsPositiveInfinity(MeshTexelScale.Of(flat)));
        var noUv = new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = Quad(10, 1).Vertices, Indices = Quad(10, 1).Indices, HasUv = false };
        Assert.True(float.IsPositiveInfinity(MeshTexelScale.Of(noUv)));
    }

    [Fact]
    public void Texel_scale_ignores_a_sliver_but_not_a_real_share()
    {
        // 200 well mapped quads and one collapsed one (a tiny share of the area): the well mapped value stands.
        var good = Quad(10, 1, 200);
        var vertices = good.Vertices.Concat(new[]
        {
            new Vertex { Position = new Vector3(5000, 0, 0) }, new Vertex { Position = new Vector3(5010, 0, 0) }, new Vertex { Position = new Vector3(5010, 0, 10) },
        }).ToArray();
        var indices = good.Indices.Concat(new uint[] { (uint)good.Vertices.Length, (uint)good.Vertices.Length + 1, (uint)good.Vertices.Length + 2 }).ToArray();
        var part = new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = vertices, Indices = indices, HasUv = true };
        Assert.InRange(MeshTexelScale.Of(part), 10f, 20f);
        // Half the area collapsed: nothing is known.
        var half = Quad(10, 1, 1);
        var vs = half.Vertices.Concat(Quad(10, 0, 1).Vertices).ToArray();
        var ix = half.Indices.Concat(Quad(10, 0, 1).Indices.Select(i => i + 4)).ToArray();
        Assert.True(float.IsPositiveInfinity(MeshTexelScale.Of(new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = vs, Indices = ix, HasUv = true })));
    }

    [Fact]
    public void The_level_held_follows_the_lod_value_with_the_blend_band()
    {
        float[] distances = [0, 100, 300, 900];
        Assert.Equal(0, ObjectMeshCache.LevelFor(distances, 50));
        Assert.Equal(0, ObjectMeshCache.LevelFor(distances, 105));   // still fading from level 0 just past 100 (band 6%)
        Assert.Equal(1, ObjectMeshCache.LevelFor(distances, 120));
        Assert.Equal(1, ObjectMeshCache.LevelFor(distances, 320));
        Assert.Equal(3, ObjectMeshCache.LevelFor(distances, 5000));
        // It never says finer than the viewer's own blend can draw, nor coarser.
        for (float v = 0; v < 2000; v += 3.7f)
        {
            int held = ObjectMeshCache.LevelFor(distances, v);
            var blend = MeshLod.Blend(distances, v);
            Assert.True(held <= blend.Lower, $"value {v}: held {held}, finest drawn {blend.Lower}");
        }
    }

    [Fact]
    public void A_mesh_made_for_far_users_holds_only_what_their_levels_use()
    {
        // Two triangle sets over one vertex pool: level 0 uses all of it, level 1 only the first three vertices.
        var vertices = Enumerable.Range(0, 12).Select(i => new Vertex { Position = new Vector3(i, i * 2, 0), Uv = new Vector2(i, 0) }).ToArray();
        var part = new ModelPart { SubMeshIndex = 0, MaterialName = "m", Vertices = vertices, Indices = Enumerable.Range(0, 12).Select(i => (uint)i).ToArray(), HasUv = true };
        var levels = new List<MeshLodLevel> { new(0, [part.Indices]), new(500, [new uint[] { 2, 0, 1 }]) };
        DecodedObjectMesh Make(int minLevel)
        {
            var model = new Model();
            model.Parts.Add(part);
            return new DecodedObjectMesh { Model = model, Levels = levels, Manual = [], Centre = Vector3.Zero, Radius = 10, MinLevel = minLevel };
        }
        var full = ObjectMeshCache.PreparePart(Make(0), part, false);
        Assert.Equal(12, full.Part.Vertices.Length);
        Assert.Equal([0, 12], full.Offset);
        Assert.Equal([12, 3], full.Count);
        var far = ObjectMeshCache.PreparePart(Make(1), part, false);
        Assert.Equal(3, far.Part.Vertices.Length);
        Assert.Equal([0, 0], far.Offset);
        Assert.Equal([0, 3], far.Count);
        // The same triangle, through the compacted vertices: positions in the same order of corners.
        var corners = far.All.Select(i => far.Part.Vertices[i].Position.X).ToArray();
        Assert.Equal([2f, 0f, 1f], corners);
        Assert.Equal(full.UvScale, far.UvScale);   // the textures' scale comes from the whole part
    }

    [Fact]
    public void The_marks_follow_the_budget_and_never_exceed_the_fixed_ones()
    {
        ulong budget = 6000UL << 20;
        var small = new VramGuard(() => (0, budget));
        small.Tick();
        Assert.Equal(budget / 1048576.0 * (2048 / 11453.0), WorldTextureCache.EffectiveMark(2048, 2048 / 11453.0, small), 3);
        var big = new VramGuard(() => (0, 40000UL << 20));
        big.Tick();
        Assert.Equal(2048, WorldTextureCache.EffectiveMark(2048, 2048 / 11453.0, big));
        Assert.Equal(2048, WorldTextureCache.EffectiveMark(2048, 2048 / 11453.0, null));
        Assert.Equal(double.MaxValue, WorldTextureCache.EffectiveMark(double.MaxValue, 0.1, small));
    }
}
