using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Impostors;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Tests.Rendering;

/// <summary>
/// Impostor atlases (docs/impostors.md): the layout maths, the file format and cache, and a bake on the GPU.
/// </summary>
public class ImpostorTests
{
    // ---------------------------------------------------------------- layout

    [Fact]
    public void Hemi_octahedral_map_round_trips_and_puts_the_horizon_on_the_border()
    {
        var random = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            var d = Vector3.Normalize(new Vector3(random.NextSingle() * 2 - 1, random.NextSingle(), random.NextSingle() * 2 - 1));
            var uv = ImpostorLayout.Encode(d);

            Assert.InRange(uv.X, 0, 1);
            Assert.InRange(uv.Y, 0, 1);
            Assert.True(Vector3.Distance(d, ImpostorLayout.Decode(uv)) < 1e-5f, $"{d} -> {uv} -> {ImpostorLayout.Decode(uv)}");

            // The normal encoding round-trips over the whole sphere.
            var n = Vector3.Normalize(new Vector3(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1));
            var p = ImpostorLayout.EncodeNormal(n);

            Assert.InRange(p.X, -1, 1);
            Assert.InRange(p.Y, -1, 1);
            Assert.True(Vector3.Distance(n, ImpostorLayout.DecodeNormal(p)) < 1e-5f, $"{n}");
        }

        Assert.Equal(new Vector2(0.5f, 0.5f), ImpostorLayout.Encode(Vector3.UnitY));
        Assert.Equal(new Vector2(1, 1), ImpostorLayout.Encode(Vector3.UnitX));
        Assert.Equal(new Vector2(1, 0), ImpostorLayout.Encode(Vector3.UnitZ));

        // Below the horizon is clamped to it.
        Assert.Equal(ImpostorLayout.Encode(Vector3.UnitX), ImpostorLayout.Encode(Vector3.Normalize(new Vector3(1, -0.3f, 0))));
    }

    [Fact]
    public void Selection_weights_sum_to_one_and_pick_a_frame_alone_on_its_direction()
    {
        const int grid = 12;
        var random = new Random(11);
        for (int k = 0; k < 2000; k++)
        {
            var d = Vector3.Normalize(new Vector3(random.NextSingle() * 2 - 1, random.NextSingle(), random.NextSingle() * 2 - 1));
            var (a, b, c, w) = ImpostorLayout.Select(d, grid);

            Assert.True(MathF.Abs(w.X + w.Y + w.Z - 1) < 1e-5f);
            Assert.True(w.X >= -1e-6f && w.Y >= -1e-6f && w.Z >= -1e-6f);

            foreach (var cell in new[] { a, b, c })
            {
                Assert.InRange(cell.X, 0, grid - 1);
                Assert.InRange(cell.Y, 0, grid - 1);
            }

            // The blended frames are the ones whose directions surround the view: none more than one grid step away.
            foreach (var cell in new[] { a, b, c })

                Assert.True(Vector3.Dot(ImpostorLayout.FrameDirection((int)cell.X, (int)cell.Y, grid), d) > 0.9f);
        }
        for (int i = 0; i < grid; i++)
            for (int j = 0; j < grid; j++)
            {
                var (a, b, c, w) = ImpostorLayout.Select(ImpostorLayout.FrameDirection(i, j, grid), grid);
                var weights = new Dictionary<Vector2, float> { [a] = 0, [b] = 0, [c] = 0 };
                weights[a] += w.X;
                weights[b] += w.Y;
                weights[c] += w.Z;

                Assert.True(weights.TryGetValue(new Vector2(i, j), out float own) && own > 0.999f, $"frame {i},{j}");
            }
    }

    // ---------------------------------------------------------------- format and cache

    static ImpostorAtlas Synthetic(int grid = 2, int frame = 8, ImpostorEncoding encoding = ImpostorEncoding.Bc5)
    {
        int levels = 2;
        var random = new Random(5);
        ImpostorTexture Make(ImpostorMap map, ImpostorEncoding e) => new()
        {
            Map = map,
            Encoding = e,
            Levels = Enumerable.Range(0, levels).Select(l =>
            {
                var b = new byte[ImpostorTexture.LevelBytes(e, grid * (frame >> l))];
                random.NextBytes(b);
                return (ReadOnlyMemory<byte>)b;
            }).ToArray(),
        };
        return new ImpostorAtlas
        {
            Grid = grid, FramePixels = frame, Centre = new Vector3(1, 2, 3), Radius = 4.5f, Name = "test",
            Gloss = 0.4f, Textures = [Make(ImpostorMap.Albedo, ImpostorEncoding.Bc1), Make(ImpostorMap.Normal, encoding)],
        };
    }

    [Fact]
    public void Atlas_file_round_trips()
    {
        var atlas = Synthetic();
        using var stream = new MemoryStream();
        atlas.Write(stream);
        stream.Position = 0;
        var read = ImpostorAtlas.Read(stream);

        Assert.NotNull(read);
        Assert.Equal(atlas.Grid, read.Grid);
        Assert.Equal(atlas.FramePixels, read.FramePixels);
        Assert.Equal(atlas.Centre, read.Centre);
        Assert.Equal(atlas.Radius, read.Radius);
        Assert.Equal(0.4f, read.Gloss);
        Assert.Equal("test", read.Name);

        for (int m = 0; m < 2; m++)
        {
            Assert.Equal(atlas.Textures[m].Map, read.Textures[m].Map);
            Assert.Equal(atlas.Textures[m].Encoding, read.Textures[m].Encoding);

            for (int l = 0; l < atlas.Levels; l++) Assert.Equal(atlas.Textures[m].Levels[l].ToArray(), read.Textures[m].Levels[l].ToArray());
        }
    }

    [Fact]
    public void Bc1_cut_out_keeps_covered_colours_and_transparent_texels()
    {
        // 16 x 16: the left half covered with two hues in blocks, the right half uncovered; the block on the border is half and half.
        const int size = 16;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                bool covered = x < 6 || (x < 10 && y % 2 == 0);
                if (!covered) continue;
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = x % 2 == 0 ? ((byte)200, (byte)120, (byte)40, (byte)255) : ((byte)60, (byte)90,
                        (byte)140, (byte)255);
            }
        var data = ImpostorEncoder.EncodeBc1(rgba, size);

        Assert.Equal(ImpostorTexture.LevelBytes(ImpostorEncoding.Bc1, size), data.Length);

        var decoded = ImpostorEncoder.Decode(ImpostorEncoding.Bc1, data, size);
        for (int i = 0; i < size * size; i++)
        {
            Assert.Equal(rgba[i * 4 + 3], decoded[i * 4 + 3]);   // the cut-out is exact

            if (rgba[i * 4 + 3] == 0)
            {
                // transparent decodes to black (the sampler premultiplies)
                Assert.Equal(0, decoded[i * 4] + decoded[i * 4 + 1] + decoded[i * 4 + 2]);

                continue;
            }
            for (int c = 0; c < 3; c++) Assert.InRange(Math.Abs(decoded[i * 4 + c] - rgba[i * 4 + c]), 0, 12);
        }
    }

    /// <summary>
    /// Three pictures of one row (grid 2, frames of 8): premultiplied albedo, normal and gloss, from a seed.
    /// </summary>
    static (byte[] Albedo, byte[] Normal, byte[] Gloss) Row(int seed)
    {
        var random = new Random(seed);
        var albedo = new byte[16 * 8 * 4];
        var normal = new byte[albedo.Length];
        var gloss = new byte[albedo.Length];
        for (int p = 0; p < 16 * 8; p++)
        {
            byte alpha = random.Next(3) == 0 ? (byte)0 : (byte)random.Next(120, 256);
            albedo[p * 4 + 3] = normal[p * 4 + 3] = gloss[p * 4 + 3] = alpha;
            for (int c = 0; c < 3; c++)
            {
                albedo[p * 4 + c] = (byte)(alpha == 0 ? 0 : random.Next(alpha));
                normal[p * 4 + c] = (byte)(alpha == 0 ? 0 : random.Next(alpha));
                gloss[p * 4 + c] = (byte)(alpha == 0 ? 0 : random.Next(alpha));
            }
        }
        return (albedo, normal, gloss);
    }

    static ImpostorAtlas Assemble(int seed)
    {
        var assembler = new ImpostorAssembler(2, 8, 2);
        for (int row = 0; row < 2; row++)
        {
            var (a, n, g) = Row(seed * 10 + row);
            assembler.AddRow(row, a, n, g);
        }
        return assembler.Finish("t", Vector3.Zero, 1);
    }

    [Fact]
    public void Assembler_output_does_not_depend_on_the_pooled_memory_it_reuses()
    {
        ImpostorAssembler.ReleasePool();
        var first = Assemble(1);
        var other = Assemble(2);   // takes the first's maps and scratch, fills them with other pictures
        var again = Assemble(1);   // takes the other's: must equal the first, not carry anything of the other

        Assert.NotEqual(first.Textures[0].Levels[0].ToArray(), other.Textures[0].Levels[0].ToArray());

        for (int m = 0; m < 2; m++)
            for (int l = 0; l < first.Levels; l++)

                Assert.Equal(first.Textures[m].Levels[l].ToArray(), again.Textures[m].Levels[l].ToArray());
        Assert.Equal(first.Gloss, again.Gloss);

        ImpostorAssembler.ReleasePool();
    }

    // ---------------------------------------------------------------- bake (GPU)

    static VulkanDevice? TryCreate()
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true });
        }
        catch (Exception e)
            when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    /// <summary>
    /// A quad in the plane x = <paramref name="x"/> over y in [−1, 1] and z in [<paramref name="z0"/>, <paramref name="z1"/>], with a vertex colour
    /// and a normal along +X.
    /// </summary>
    static ModelPart Quad(float x, float z0, float z1, Vector4 colour)
    {
        Vertex V(float y, float z) => new() { Position = new(x, y, z), Normal = Vector3.UnitX, Colour = colour, Tangent = new(0, 0, 1, 1) };
        return new ModelPart
        {
            SubMeshIndex = 0, MaterialName = "test", HasColours = true,
            Vertices = [V(-1, z0), V(-1, z1), V(1, z1), V(1, z0)],
            Indices = [0, 2, 1, 0, 3, 2],   // counter-clockwise seen from +X
        };
    }

    /// <summary>
    /// Two quads seen from +X: red in front (x = 0.5) over z ≥ 0 only, blue behind (x = −0.5) over the whole square. The frame looking from +X
    /// must show red where it covers (on its left: the frame's right axis is −Z) and blue elsewhere, a normal along the frame's direction, and
    /// red's depth in front of blue's.
    /// </summary>
    [Fact]
    [Slow]
    public void Bake_renders_the_nearest_surface_in_the_frame_orientation()
    {
        using var device = TryCreate();

        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");

        var red = new Vector4(1, 0, 0, 1);
        var blue = new Vector4(0, 0, 1, 1);
        using var ctx = new Meitou.Game.Rendering.Gpu.GpuContext(device!);
        ctx.BeginFrame();
        using var baker = new ImpostorBaker(ctx, (AssetLocator?)null);
        void NextFrame()
        {
            ctx.EndFrame();
            device!.Frames.WaitAll();
            ctx.BeginFrame();
        }
        foreach (bool redFirst in new[] { true, false })
        {
            var model = new Model();
            if (redFirst)
            {
                model.Parts.Add(Quad(0.5f, 0, 1, red));
                model.Parts.Add(Quad(-0.5f, -1, 1, blue));
            }
            else { model.Parts.Add(Quad(-0.5f, -1, 1, blue)); model.Parts.Add(Quad(0.5f, 0, 1, red)); }
            var meshes = ImpostorMeshes.From(model, null);
            var source = new ImpostorSource
            {
                Name = "quads", MeshPath = "quads.mesh",
                Main = new ImpostorMaterial(null, null, null, null, 0, true, false, Vector2.One, 0),
            };
            baker.Compress = false;
            var atlas = baker.Bake(source, meshes, new ImpostorClass("test", 32, 4), NextFrame);
            NextFrame();

            Assert.True(device!.ValidationErrors == 0, string.Join("\n", device.ValidationLog));

            // Frame (3, 3) looks from +X; its pixels are 96..127 in both directions of the 128-pixel level 0.
            var albedo = atlas[ImpostorMap.Albedo]!.Levels[0].ToArray();
            var normal = atlas[ImpostorMap.Normal]!.Levels[0].ToArray();
            int At(float u, float v) => ((96 + (int)(v * 32)) * 128 + 96 + (int)(u * 32)) * 4;

            // The radius is √(0.5² + 1 + 1) × 1.01 ≈ 1.515: z = 0.5 is at u ≈ 0.33, z = −0.5 at u ≈ 0.67.
            int left = At(0.33f, 0.5f), right = At(0.67f, 0.5f), outside = At(0.02f, 0.02f);

            Assert.True(albedo[left] > 120 && albedo[left + 2] < 40,
                    $"red on the left (red drawn {(redFirst ? "first" : "last")}): {albedo[left]},{albedo[left + 1]},{albedo[left + 2]}");
            Assert.True(albedo[right + 2] > 120 && albedo[right] < 40, $"blue on the right: {albedo[right]},{albedo[right + 1]},{albedo[right + 2]}");
            Assert.Equal(255, albedo[left + 3]);
            Assert.Equal(0, albedo[outside + 3]);

            var n = ImpostorLayout.DecodeNormal(new Vector2(normal[left], normal[left + 1]) / 127.5f - Vector2.One);

            Assert.True(n.Z > 0.95f, $"normal towards the viewer: {n}");
        }
    }
}
