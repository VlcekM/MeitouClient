using System.Numerics;
using Meitou.Rendering;
using Meitou.Rendering.Impostors;
using Meitou.Rendering.Gpu.Core;

namespace Meitou.Tests.Rendering;

/// <summary>Impostor atlases (docs/impostors.md): the layout maths, the file format and cache, and a bake on the GPU.</summary>
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
        }
        Assert.Equal(new Vector2(0.5f, 0.5f), ImpostorLayout.Encode(Vector3.UnitY));
        Assert.Equal(new Vector2(1, 1), ImpostorLayout.Encode(Vector3.UnitX));
        Assert.Equal(new Vector2(1, 0), ImpostorLayout.Encode(Vector3.UnitZ));
        // Below the horizon is clamped to it.
        Assert.Equal(ImpostorLayout.Encode(Vector3.UnitX), ImpostorLayout.Encode(Vector3.Normalize(new Vector3(1, -0.3f, 0))));
    }

    [Fact]
    public void Normal_encoding_round_trips_over_the_whole_sphere()
    {
        var random = new Random(3);
        for (int i = 0; i < 2000; i++)
        {
            var n = Vector3.Normalize(new Vector3(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1));
            var p = ImpostorLayout.EncodeNormal(n);
            Assert.InRange(p.X, -1, 1);
            Assert.InRange(p.Y, -1, 1);
            Assert.True(Vector3.Distance(n, ImpostorLayout.DecodeNormal(p)) < 1e-5f, $"{n}");
        }
    }

    [Fact]
    public void Frame_basis_is_orthonormal_and_right_handed()
    {
        for (int i = 0; i < 12; i++)
            for (int j = 0; j < 12; j++)
            {
                var d = ImpostorLayout.FrameDirection(i, j, 12);
                ImpostorLayout.Basis(d, out var right, out var up);
                Assert.True(MathF.Abs(right.Length() - 1) < 1e-5f && MathF.Abs(up.Length() - 1) < 1e-5f);
                Assert.True(MathF.Abs(Vector3.Dot(right, up)) < 1e-5f && MathF.Abs(Vector3.Dot(right, d)) < 1e-5f && MathF.Abs(Vector3.Dot(up, d)) < 1e-5f);
                Assert.True(Vector3.Distance(Vector3.Cross(right, up), d) < 1e-5f);
                Assert.True(up.Y >= -1e-6f, "up never points down");
            }
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
                weights[a] += w.X; weights[b] += w.Y; weights[c] += w.Z;
                Assert.True(weights.TryGetValue(new Vector2(i, j), out float own) && own > 0.999f, $"frame {i},{j}");
            }
    }

    [Fact]
    public void Transition_distance_puts_one_atlas_texel_on_a_pixel()
    {
        float fov = 50 * MathF.PI / 180;
        float d = ImpostorLayout.TransitionDistance(200, 256, 1080, fov, 0);
        // At d the sphere's diameter covers 256 pixels of a 1080-pixel-high screen.
        float pixels = 2 * 200 / (2 * d * MathF.Tan(fov / 2)) * 1080;
        Assert.True(MathF.Abs(pixels - 256) < 0.01f);
        Assert.Equal(1500, ImpostorLayout.TransitionDistance(10, 256, 1080, fov, 1500));
    }

    [Fact]
    public void Size_classes_follow_the_largest_instance()
    {
        Assert.Null(ImpostorClass.For(20));
        Assert.Equal(ImpostorClass.Medium, ImpostorClass.For(100));
        Assert.Equal(ImpostorClass.Large, ImpostorClass.For(500));
        Assert.Equal(6, ImpostorClass.Medium.Levels);   // 128 .. 4
        Assert.Equal(7, ImpostorClass.Large.Levels);    // 256 .. 4
    }

    [Fact]
    public void Coverage_scale_keeps_the_share_above_the_cut()
    {
        var coverage = Enumerable.Range(0, 64).Select(i => i / 128f).ToArray();   // all below 0.5
        float scale = ImpostorAssembler.CoverageScale(coverage, 0.25f);
        int kept = coverage.Count(c => c * scale >= 0.5f);
        Assert.InRange(kept, 15, 17);
        Assert.Equal(1, ImpostorAssembler.CoverageScale(coverage, 0));
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
                return b;
            }).ToArray(),
        };
        return new ImpostorAtlas
        {
            Grid = grid, FramePixels = frame, Centre = new Vector3(1, 2, 3), Radius = 4.5f, Name = "test",
            Textures = [Make(ImpostorMap.Albedo, ImpostorEncoding.Bc3), Make(ImpostorMap.Normal, encoding), Make(ImpostorMap.Depth, ImpostorEncoding.Rgba8)],
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
        Assert.Equal("test", read.Name);
        for (int m = 0; m < 3; m++)
        {
            Assert.Equal(atlas.Textures[m].Map, read.Textures[m].Map);
            Assert.Equal(atlas.Textures[m].Encoding, read.Textures[m].Encoding);
            for (int l = 0; l < atlas.Levels; l++) Assert.Equal(atlas.Textures[m].Levels[l], read.Textures[m].Levels[l]);
        }
    }

    [Fact]
    public void Damaged_or_foreign_files_read_as_null()
    {
        var atlas = Synthetic();
        using var stream = new MemoryStream();
        atlas.Write(stream);
        var bytes = stream.ToArray();
        Assert.Null(ImpostorAtlas.Read(new MemoryStream(bytes[..(bytes.Length / 2)])));   // truncated
        var wrongMagic = (byte[])bytes.Clone();
        wrongMagic[0] ^= 0xFF;
        Assert.Null(ImpostorAtlas.Read(new MemoryStream(wrongMagic)));
        var wrongBaker = (byte[])bytes.Clone();
        wrongBaker[8] ^= 0x01;   // the baker version
        Assert.Null(ImpostorAtlas.Read(new MemoryStream(wrongBaker)));
        // A checksum that does not match the data. The header: magic, format, baker, grid, frame, levels (6 × 4), centre (12), radius (4),
        // name ("test": 1 + 4), count (4), 3 × (map, encoding, 2 level sizes) (3 × 16), then the 8-byte checksum.
        int checksum = 24 + 12 + 4 + 5 + 4 + 48;
        var tampered = (byte[])bytes.Clone();
        tampered[checksum] ^= 0x01;
        Assert.Null(ImpostorAtlas.Read(new MemoryStream(tampered)));
        Assert.Null(ImpostorAtlas.Read(new MemoryStream([])));
    }

    [Fact]
    public void Block_encoding_decodes_close_to_the_source()
    {
        const int size = 16;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int o = (y * size + x) * 4;
                rgba[o] = (byte)(x * 16); rgba[o + 1] = (byte)(y * 16); rgba[o + 2] = 0; rgba[o + 3] = 255;
            }
        var bc5 = ImpostorEncoder.Decode(ImpostorEncoding.Bc5, ImpostorEncoder.EncodeBc5(rgba, size), size);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            Assert.InRange(Math.Abs(bc5[i] - rgba[i]), 0, 6);
            Assert.InRange(Math.Abs(bc5[i + 1] - rgba[i + 1]), 0, 6);
        }
    }

    [Fact]
    public void Bc3_keeps_both_colours_of_a_two_colour_block_and_its_coverage()
    {
        // Orange leaves and blue-grey twigs in one block, the rest uncovered (a typical frame edge): both hues must survive.
        const int size = 4;
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < 16; i++)
        {
            (byte r, byte g, byte b, byte a) = (i % 3) switch { 0 => ((byte)210, (byte)130, (byte)40, (byte)255), 1 => ((byte)90, (byte)100, (byte)120, (byte)200), _ => ((byte)0, (byte)0, (byte)0, (byte)0) };
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (r, g, b, a);
        }
        var decoded = ImpostorEncoder.Decode(ImpostorEncoding.Bc3, ImpostorEncoder.Encode(ImpostorEncoding.Bc3, rgba, size), size);
        for (int i = 0; i < 16; i++)
        {
            Assert.InRange(Math.Abs(decoded[i * 4 + 3] - rgba[i * 4 + 3]), 0, 18);   // half a step of the 0..255 eight-value ramp
            if (rgba[i * 4 + 3] == 0) continue;
            for (int c = 0; c < 3; c++) Assert.InRange(Math.Abs(decoded[i * 4 + c] - rgba[i * 4 + c]), 0, 10);
        }
    }

    static ImpostorSource Source(string directory, string name = "Tree") => new()
    {
        Name = name,
        MeshPath = Path.Combine(directory, "tree.mesh"),
        Main = new ImpostorMaterial("bark.dds", null, null, null, 0.5f, true, false, Vector2.One, 0.2f),
        MaxScale = 2,
        TexturePaths = [Path.Combine(directory, "bark.dds")],
    };

    [Fact]
    public void Cache_hits_on_the_same_sources_and_misses_after_a_change()
    {
        var directory = Directory.CreateTempSubdirectory("meitou-impostor-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "tree.mesh"), "mesh");
            File.WriteAllText(Path.Combine(directory, "bark.dds"), "bark");
            var cache = new ImpostorCache(Path.Combine(directory, "cache"));
            var source = Source(directory);
            Assert.Null(cache.TryLoad(source));
            cache.Save(source, Synthetic());
            Assert.StartsWith(Path.Combine(directory, "cache"), cache.PathFor(source));
            Assert.NotNull(cache.TryLoad(source));
            Assert.NotNull(cache.TryLoad(Source(directory)));   // a new source object with the same inputs
            Assert.Equal(source.Key, Source(directory).Key);

            // A different material, scale or file gives another key.
            Assert.NotEqual(source.Key, new ImpostorSource { Name = "Tree", MeshPath = source.MeshPath, Main = source.Main with { AlphaThreshold = 0.6f }, MaxScale = 2, TexturePaths = source.TexturePaths }.Key);
            Assert.NotEqual(source.Key, new ImpostorSource { Name = "Tree", MeshPath = source.MeshPath, Main = source.Main, MaxScale = 3, TexturePaths = source.TexturePaths }.Key);
            File.WriteAllText(Path.Combine(directory, "bark.dds"), "bark, changed");
            Assert.Null(cache.TryLoad(Source(directory)));

            // A damaged file is a miss, not an exception.
            var again = Source(directory);
            cache.Save(again, Synthetic());
            File.WriteAllBytes(cache.PathFor(again), [1, 2, 3]);
            Assert.Null(cache.TryLoad(again));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Cache_defaults_to_local_app_data()
    {
        if (Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_CACHE") is not null) return;
        var root = new ImpostorCache().Root;
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), root);
        Assert.EndsWith(Path.Combine("Meitou", "impostors"), root);
    }

    // ---------------------------------------------------------------- bake (GPU)

    static VulkanDevice? TryCreate()
    {
        try { return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true }); }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    /// <summary>A quad in the plane x = <paramref name="x"/> over y in [−1, 1] and z in [<paramref name="z0"/>, <paramref name="z1"/>], with a vertex colour and a normal along +X.</summary>
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
        using var ctx = new Meitou.Rendering.Gpu.GpuContext(device!);
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
            if (redFirst) { model.Parts.Add(Quad(0.5f, 0, 1, red)); model.Parts.Add(Quad(-0.5f, -1, 1, blue)); }
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
            var albedo = atlas[ImpostorMap.Albedo]!.Levels[0];
            var normal = atlas[ImpostorMap.Normal]!.Levels[0];
            var depth = atlas[ImpostorMap.Depth]!.Levels[0];
            int At(float u, float v) => ((96 + (int)(v * 32)) * 128 + 96 + (int)(u * 32)) * 4;
            // The radius is √(0.5² + 1 + 1) × 1.01 ≈ 1.515: z = 0.5 is at u ≈ 0.33, z = −0.5 at u ≈ 0.67.
            int left = At(0.33f, 0.5f), right = At(0.67f, 0.5f), outside = At(0.02f, 0.02f);
            Assert.True(albedo[left] > 120 && albedo[left + 2] < 40, $"red on the left (red drawn {(redFirst ? "first" : "last")}): {albedo[left]},{albedo[left + 1]},{albedo[left + 2]}");
            Assert.True(albedo[right + 2] > 120 && albedo[right] < 40, $"blue on the right: {albedo[right]},{albedo[right + 1]},{albedo[right + 2]}");
            Assert.Equal(255, albedo[left + 3]);
            Assert.Equal(0, albedo[outside + 3]);
            var n = ImpostorLayout.DecodeNormal(new Vector2(normal[left], normal[left + 1]) / 127.5f - Vector2.One);
            Assert.True(n.Z > 0.95f, $"normal towards the viewer: {n}");
            Assert.True(depth[left] > depth[right] + 40, $"red (x = 0.5) in front of blue (x = −0.5): {depth[left]} vs {depth[right]}");
        }
    }
}
