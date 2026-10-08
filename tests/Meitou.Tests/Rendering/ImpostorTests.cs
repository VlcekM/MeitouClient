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
    public void Rock_class_caps_the_frame_and_moves_the_transition_out_for_huge_rocks()
    {
        var ordinary = ImpostorClass.ForRock(300)!.Value;
        Assert.Equal(ImpostorClass.For(300)!.Value, ordinary);
        Assert.Equal(ImpostorClass.ReferenceDistance, ordinary.Transition(300, ImpostorClass.ReferenceDistance));
        var huge = ImpostorClass.ForRock(2400)!.Value;
        Assert.True(huge.FramePixels <= ImpostorClass.RockMaxFrame);
        Assert.True(huge.Knee > 0 && huge.Knee < 2400);
        Assert.True(huge.Transition(2400, 4000) > 4000);
        Assert.Equal(4000f * 2400 / huge.Knee, huge.Transition(2400, 4000), 1);
        // Trees (For) keep their transition whatever the radius.
        Assert.Equal(4000f, ImpostorClass.For(2400)!.Value.Transition(2400, 4000));
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
        // Under radius 48 only the small class: from radius 2, with enough triangles to beat a quad (a caller without the count gets it by size).
        Assert.Null(ImpostorClass.For(20, 10));
        Assert.Null(ImpostorClass.For(1.5f, 5000));
        var small = ImpostorClass.For(20, 500)!.Value;
        Assert.Equal(("small", ImpostorClass.SmallFrame, ImpostorClass.SmallGrid), (small.Name, small.FramePixels, small.Grid));
        Assert.True(small.IsSmall && ImpostorClass.For(20)!.Value.IsSmall && !ImpostorClass.For(60)!.Value.IsSmall);
        // The frame is the smallest power of two that holds the largest instance's size on a 1080-line screen at the reference distance
        // (within the allowed magnification), from 64 to 256 pixels.
        Assert.Equal(64, ImpostorClass.For(60)!.Value.FramePixels);
        Assert.Equal(256, ImpostorClass.For(2000)!.Value.FramePixels);
        int last = 0;
        for (float radius = 48; radius < 3000; radius *= 1.2f)
        {
            var c = ImpostorClass.For(radius)!.Value;
            Assert.True(c.FramePixels >= last && BitOperations.IsPow2(c.FramePixels) && c.FramePixels is >= 64 and <= 256, $"{radius}: {c}");
            Assert.Equal(ImpostorClass.DefaultGrid, c.Grid);
            if (c.FramePixels < ImpostorClass.MaxFrame) Assert.True(ImpostorClass.ScreenDiameter(radius) / ImpostorClass.Magnification <= c.FramePixels, $"{radius}: {c}");
            last = c.FramePixels;
        }
        Assert.Equal(5, new ImpostorClass("a", 64, 8).Levels);    // 64 .. 4
        Assert.Equal(6, ImpostorClass.Medium.Levels);   // 128 .. 4
        Assert.Equal(7, ImpostorClass.Large.Levels);    // 256 .. 4
    }

    [Fact]
    public void Bake_lod_bias_matches_the_texel_density_of_the_screen_at_the_reference_distance()
    {
        if (ImpostorClass.BiasScale == 0) return;
        var c = new ImpostorClass("a", 256, 8);
        // A sphere that is exactly 2 x 256 pixels across at the reference distance (the bake draws 2 x the frame): no bias; half of that: one level.
        float radius = 2 * 256 * ImpostorClass.ReferenceDistance * MathF.Tan(25 * MathF.PI / 180) / ImpostorClass.ReferenceHeight;
        Assert.InRange(c.LodBias(radius) / ImpostorClass.BiasScale, -1e-3f, 1e-3f);
        Assert.InRange(c.LodBias(radius / 2) / ImpostorClass.BiasScale, 1 - 1e-3f, 1 + 1e-3f);
        Assert.InRange(c.LodBias(radius / 1e6f), 0, 4 * ImpostorClass.BiasScale + 1e-3f);   // clamped
    }

    [Fact]
    public void The_small_class_has_its_own_transition_that_keeps_the_screen_size_constant()
    {
        var medium = ImpostorClass.For(60)!.Value;
        Assert.Equal(4000, medium.Transition(60, 4000));
        Assert.Equal(2000, medium.Transition(1000, 2000));   // medium and large: the user's distance whatever the size
        float previous = 0;
        foreach (float radius in new[] { 2f, 5, 10, 20, 40, 47.9f })
        {
            var c = ImpostorClass.For(radius, 1000)!.Value;
            float t = c.Transition(radius, 4000);
            Assert.True(t > previous && t < 4000, $"{radius}: {t}");
            previous = t;
            // The largest instance is the same number of pixels across at its transition, whatever the radius: 27.8 at 1080 lines (the smallest medium mesh's at 4000).
            Assert.InRange(ImpostorClass.ScreenDiameter(radius, t), 27.6f, 28.0f);
            Assert.Equal(t, c.BakeDistance, 1e-3f);
            Assert.Equal(t / 2, c.Transition(radius, 2000), 1e-3f);   // the slider scales it
        }
        // The bake's texture bias is matched to the distance the class is shown at (not the reference 4000): a 32 pixel frame, baked at 64, against a 27.8 pixel sphere.
        var s = ImpostorClass.For(10, 1000)!.Value;
        float expected = MathF.Log2(2 * s.FramePixels / ImpostorClass.ScreenDiameter(10, s.BakeDistance));
        Assert.Equal(Math.Clamp(expected, 0, 4) * ImpostorClass.BiasScale, s.LodBias(10), 1e-3f);
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
        // A checksum that does not match the data. The header: magic, format, baker, grid, frame, levels (6 × 4), centre (12), radius (4), gloss (4),
        // name ("test": 1 + 4), count (4), 2 × (map, encoding, 2 level sizes) (2 × 16), then the 8-byte checksum.
        int checksum = 24 + 12 + 4 + 4 + 5 + 4 + 32;
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
                (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = x % 2 == 0 ? ((byte)200, (byte)120, (byte)40, (byte)255) : ((byte)60, (byte)90, (byte)140, (byte)255);
            }
        var data = ImpostorEncoder.EncodeBc1(rgba, size);
        Assert.Equal(ImpostorTexture.LevelBytes(ImpostorEncoding.Bc1, size), data.Length);
        var decoded = ImpostorEncoder.Decode(ImpostorEncoding.Bc1, data, size);
        for (int i = 0; i < size * size; i++)
        {
            Assert.Equal(rgba[i * 4 + 3], decoded[i * 4 + 3]);   // the cut-out is exact
            if (rgba[i * 4 + 3] == 0)
            {
                Assert.Equal(0, decoded[i * 4] + decoded[i * 4 + 1] + decoded[i * 4 + 2]);   // transparent decodes to black (the sampler premultiplies)
                continue;
            }
            for (int c = 0; c < 3; c++) Assert.InRange(Math.Abs(decoded[i * 4 + c] - rgba[i * 4 + c]), 0, 12);
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

    /// <summary>Writes a file with an atlas header of the given versions (the cache only reads the first 12 bytes of it to judge its age) and body of <paramref name="size"/> bytes.</summary>
    static string FakeAtlas(string folder, string name, int format, int baker, int size, DateTime lastUse)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        var bytes = new byte[size];
        BitConverter.GetBytes(ImpostorAtlas.Magic).CopyTo(bytes, 0);
        BitConverter.GetBytes(format).CopyTo(bytes, 4);
        BitConverter.GetBytes(baker).CopyTo(bytes, 8);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, lastUse);
        return path;
    }

    [Fact]
    public void Cache_evicts_the_least_recently_used_files_above_the_cap()
    {
        var folder = Path.Combine(Directory.CreateTempSubdirectory("meitou-impostor-test").FullName, "cache");
        try
        {
            var now = DateTime.UtcNow;
            // Ten files of 100 KB, the number in the name the age rank (0 is the oldest); the cap is 750 KB: eviction goes to 90% of it (675 KB).
            var paths = Enumerable.Range(0, 10).Select(i => FakeAtlas(folder, $"tree{i}_0000.mimp", ImpostorAtlas.FormatVersion, ImpostorAtlas.BakerVersion, 100_000, now.AddHours(i - 20))).ToArray();
            var cache = new ImpostorCache(folder, 750_000);
            // The oldest file is used again (a load sets the last write time), so it is the last to go.
            File.SetLastWriteTimeUtc(paths[0], now);
            var result = cache.Maintain(now);
            Assert.Equal(10, result.Files);
            Assert.Equal(1_000_000, result.Bytes);
            Assert.Equal(4, result.Evicted);   // 1,000,000 down to at most 675,000: four 100 KB files
            Assert.Equal(600_000, result.BytesAfter);
            Assert.True(File.Exists(paths[0]), "the file used last survives");
            Assert.False(File.Exists(paths[1]));
            Assert.False(File.Exists(paths[4]));
            Assert.True(File.Exists(paths[5]));
            Assert.True(File.Exists(paths[9]));
            Assert.Equal(0, cache.Maintain(now).Evicted);   // under the cap: nothing more
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void Cache_without_a_cap_keeps_everything_and_missing_folders_are_fine()
    {
        var folder = Path.Combine(Directory.CreateTempSubdirectory("meitou-impostor-test").FullName, "cache");
        try
        {
            Assert.Equal(default, new ImpostorCache(folder, 1000).Maintain());   // no folder yet
            for (int i = 0; i < 4; i++) FakeAtlas(folder, $"tree{i}_0000.mimp", ImpostorAtlas.FormatVersion, ImpostorAtlas.BakerVersion, 100_000, DateTime.UtcNow.AddDays(-i));
            var result = new ImpostorCache(folder, 0).Maintain();
            Assert.Equal((4, 0, 0), (result.Files, result.Evicted, result.Stale));
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void Cache_removes_files_of_older_formats_and_abandoned_temporaries_only()
    {
        var folder = Path.Combine(Directory.CreateTempSubdirectory("meitou-impostor-test").FullName, "cache");
        try
        {
            var now = DateTime.UtcNow;
            string current = FakeAtlas(folder, "current_0000.mimp", ImpostorAtlas.FormatVersion, ImpostorAtlas.BakerVersion, 5000, now);
            string olderBaker = FakeAtlas(folder, "baker_0000.mimp", ImpostorAtlas.FormatVersion, ImpostorAtlas.BakerVersion - 1, 5000, now);
            string olderFormat = FakeAtlas(folder, "format_0000.mimp", ImpostorAtlas.FormatVersion - 1, ImpostorAtlas.BakerVersion + 5, 5000, now);
            string newer = FakeAtlas(folder, "newer_0000.mimp", ImpostorAtlas.FormatVersion, ImpostorAtlas.BakerVersion + 1, 5000, now);
            string newerFormat = FakeAtlas(folder, "newerformat_0000.mimp", ImpostorAtlas.FormatVersion + 1, 1, 5000, now);
            string junk = Path.Combine(folder, "junk_0000.mimp");
            File.WriteAllBytes(junk, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14]);
            string shortFile = Path.Combine(folder, "short_0000.mimp");
            File.WriteAllBytes(shortFile, [1, 2, 3]);
            string oldTemporary = Path.Combine(folder, "tree_0000.mimp.4242.tmp");
            string freshTemporary = Path.Combine(folder, "tree_1111.mimp.4343.tmp");
            File.WriteAllBytes(oldTemporary, [0]);
            File.WriteAllBytes(freshTemporary, [0]);
            File.SetLastWriteTimeUtc(oldTemporary, now.AddHours(-3));
            string other = Path.Combine(folder, "notes.txt");
            File.WriteAllText(other, "not ours");

            var result = new ImpostorCache(folder, 0).Maintain(now);
            Assert.Equal(5, result.Stale);   // older baker, older format, junk, short, old temporary
            Assert.Equal(3, result.Files);   // current, newer baker, newer format
            Assert.True(File.Exists(current));
            Assert.True(File.Exists(newer));
            Assert.True(File.Exists(newerFormat));
            Assert.True(File.Exists(freshTemporary));
            Assert.True(File.Exists(other));
            foreach (var gone in new[] { olderBaker, olderFormat, junk, shortFile, oldTemporary }) Assert.False(File.Exists(gone), gone);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void Cache_cap_comes_from_the_environment_default_or_the_constructor()
    {
        Assert.Equal(7L << 20, new ImpostorCache("x", 7L << 20).MaxBytes);
        if (Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_CACHE_MB") is null) Assert.Equal(ImpostorCache.DefaultMaxBytes, new ImpostorCache("x").MaxBytes);
        Assert.Equal(512L << 20, ImpostorCache.DefaultMaxBytes);
    }

    [Fact]
    public void Loading_and_saving_an_atlas_count_as_uses()
    {
        var directory = Directory.CreateTempSubdirectory("meitou-impostor-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "tree.mesh"), "mesh");
            File.WriteAllText(Path.Combine(directory, "bark.dds"), "bark");
            var cache = new ImpostorCache(Path.Combine(directory, "cache"), 0);
            var source = Source(directory);
            cache.Save(source, Synthetic());
            var path = cache.PathFor(source);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
            Assert.NotNull(cache.TryLoad(source));
            Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(5), "a load sets the last use");
            // A load of a damaged file is not a use.
            File.WriteAllBytes(path, [1, 2, 3]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
            Assert.Null(cache.TryLoad(source));
            Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(29));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>Three pictures of one row (grid 2, frames of 8): premultiplied albedo, normal and gloss, from a seed.</summary>
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
        Assert.NotEqual(first.Textures[0].Levels[0], other.Textures[0].Levels[0]);
        for (int m = 0; m < 2; m++)
            for (int l = 0; l < first.Levels; l++)
                Assert.Equal(first.Textures[m].Levels[l], again.Textures[m].Levels[l]);
        Assert.Equal(first.Gloss, again.Gloss);
        ImpostorAssembler.ReleasePool();
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
            int At(float u, float v) => ((96 + (int)(v * 32)) * 128 + 96 + (int)(u * 32)) * 4;
            // The radius is √(0.5² + 1 + 1) × 1.01 ≈ 1.515: z = 0.5 is at u ≈ 0.33, z = −0.5 at u ≈ 0.67.
            int left = At(0.33f, 0.5f), right = At(0.67f, 0.5f), outside = At(0.02f, 0.02f);
            Assert.True(albedo[left] > 120 && albedo[left + 2] < 40, $"red on the left (red drawn {(redFirst ? "first" : "last")}): {albedo[left]},{albedo[left + 1]},{albedo[left + 2]}");
            Assert.True(albedo[right + 2] > 120 && albedo[right] < 40, $"blue on the right: {albedo[right]},{albedo[right + 1]},{albedo[right + 2]}");
            Assert.Equal(255, albedo[left + 3]);
            Assert.Equal(0, albedo[outside + 3]);
            var n = ImpostorLayout.DecodeNormal(new Vector2(normal[left], normal[left + 1]) / 127.5f - Vector2.One);
            Assert.True(n.Z > 0.95f, $"normal towards the viewer: {n}");

        }
    }
}
