using System.Text;
using Meitou.Content;
using Meitou.Data.Textures;

namespace Meitou.Tests.Textures;

public class DdsReaderTests
{
    const uint FlagsBase = 0x1 | 0x2 | 0x4 | 0x1000; // CAPS | HEIGHT | WIDTH | PIXELFORMAT

    /// <summary>A DDS file: legacy header (and DX10 header if given), then <paramref name="body"/>.</summary>
    static byte[] Dds(int width, int height, int mips, string? fourCC, byte[] body,
        uint pfFlags = 0x4, int bitCount = 0, uint r = 0, uint g = 0, uint b = 0, uint a = 0, uint caps2 = 0,
        (int Dxgi, int Dimension, uint Misc, int ArraySize)? dx10 = null)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("DDS "));
        w.Write(124);
        w.Write(FlagsBase | (mips > 0 ? DdsReader.FlagMipMapCount : 0));
        w.Write(height);
        w.Write(width);
        w.Write(0); // pitch / linear size (ignored by the reader)
        w.Write(0); // depth
        w.Write(mips);
        for (int i = 0; i < 11; i++) w.Write(0);
        w.Write(32);
        w.Write(pfFlags);
        w.Write(fourCC is null ? new byte[4] : Encoding.ASCII.GetBytes(fourCC));
        w.Write(bitCount);
        w.Write(r);
        w.Write(g);
        w.Write(b);
        w.Write(a);
        w.Write(0x1000u); // caps: TEXTURE
        w.Write(caps2);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        if (dx10 is { } d)
        {
            w.Write(d.Dxgi);
            w.Write(d.Dimension);
            w.Write(d.Misc);
            w.Write(d.ArraySize);
            w.Write(0);
        }
        w.Write(body);
        return ms.ToArray();
    }

    /// <summary>A BC1 colour block: endpoints and one 2-bit index per pixel (row-major).</summary>
    static byte[] ColourBlock(ushort c0, ushort c1, params int[] indices)
    {
        uint bits = 0;
        for (int i = 0; i < indices.Length; i++) bits |= (uint)indices[i] << (2 * i);
        return [.. BitConverter.GetBytes(c0), .. BitConverter.GetBytes(c1), .. BitConverter.GetBytes(bits)];
    }

    /// <summary>A BC3 alpha / BC4 block: endpoints and one 3-bit index per pixel.</summary>
    static byte[] ChannelBlock(byte a0, byte a1, params int[] indices)
    {
        ulong bits = 0;
        for (int i = 0; i < indices.Length; i++) bits |= (ulong)indices[i] << (3 * i);
        return [a0, a1, .. BitConverter.GetBytes(bits).AsSpan(0, 6).ToArray()];
    }

    static void AssertPixel(RgbaImage image, int x, int y, int r, int g, int b, int a)
    {
        var p = image[x, y];
        Assert.Equal((r, g, b, a), ((int)p.R, (int)p.G, (int)p.B, (int)p.A));
    }

    [Fact]
    public void Bc1_four_colour_block_interpolates_thirds()
    {
        var file = DdsReader.Read(Dds(4, 4, 1, "DXT1", ColourBlock(0xF800, 0x001F, 0, 1, 2, 3)));
        Assert.Equal(DdsFormat.Bc1, file.Format);
        Assert.Equal(0, file.TrailingBytes);
        var image = DdsDecoder.Decode(file);
        AssertPixel(image, 0, 0, 255, 0, 0, 255);
        AssertPixel(image, 1, 0, 0, 0, 255, 255);
        AssertPixel(image, 2, 0, 170, 0, 85, 255);
        AssertPixel(image, 3, 0, 85, 0, 170, 255);
        AssertPixel(image, 0, 1, 255, 0, 0, 255); // index 0 elsewhere
    }

    [Fact]
    public void Bc1_with_color0_not_greater_has_midpoint_and_transparent_black()
    {
        var image = DdsDecoder.Decode(DdsReader.Read(Dds(4, 4, 1, "DXT1", ColourBlock(0x001F, 0xF800, 0, 1, 2, 3))));
        AssertPixel(image, 0, 0, 0, 0, 255, 255);
        AssertPixel(image, 1, 0, 255, 0, 0, 255);
        AssertPixel(image, 2, 0, 128, 0, 128, 255);
        AssertPixel(image, 3, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void Bc2_uses_explicit_alpha_and_always_four_colours()
    {
        // Alpha nibbles for pixels 0..3: 0, 5, 10, 15; colour endpoints in "three colour" order must still give four colours.
        byte[] alpha = [0x50, 0xFA, 0, 0, 0, 0, 0, 0];
        var image = DdsDecoder.Decode(DdsReader.Read(Dds(4, 4, 1, "DXT3", [.. alpha, .. ColourBlock(0x001F, 0xF800, 0, 1, 2, 3)])));
        Assert.Equal(new byte[] { 0, 85, 170, 255 }, Enumerable.Range(0, 4).Select(x => image[x, 0].A));
        AssertPixel(image, 3, 0, 170, 0, 85, 255); // (c0 + 2 c1) / 3, not transparent black
    }

    [Fact]
    public void Bc3_alpha_has_eight_value_and_six_value_modes()
    {
        var eight = DdsDecoder.Decode(DdsReader.Read(Dds(4, 4, 1, "DXT5", [.. ChannelBlock(255, 0, 0, 1, 2, 7), .. ColourBlock(0xFFFF, 0xFFFF)])));
        Assert.Equal(new byte[] { 255, 0, 219, 36 }, Enumerable.Range(0, 4).Select(x => eight[x, 0].A));
        AssertPixel(eight, 0, 0, 255, 255, 255, 255);

        var six = DdsDecoder.Decode(DdsReader.Read(Dds(4, 4, 1, "DXT5", [.. ChannelBlock(0, 100, 2, 5, 6, 7), .. ColourBlock(0, 0)])));
        Assert.Equal(new byte[] { 20, 80, 0, 255 }, Enumerable.Range(0, 4).Select(x => six[x, 0].A));
    }

    [Fact]
    public void Bc4_and_bc5_fill_red_and_green()
    {
        var bc4 = DdsDecoder.Decode(DdsReader.Read(Dds(4, 4, 1, "ATI1", ChannelBlock(200, 100, 0, 1))));
        AssertPixel(bc4, 0, 0, 200, 200, 200, 255);
        AssertPixel(bc4, 1, 0, 100, 100, 100, 255);
        var bc5 = DdsDecoder.Decode(DdsReader.Read(Dds(4, 4, 1, "ATI2", [.. ChannelBlock(200, 100, 0), .. ChannelBlock(10, 20, 1)])));
        AssertPixel(bc5, 0, 0, 200, 20, 0, 255);
    }

    [Fact]
    public void Odd_sized_mip_chain_rounds_up_to_whole_blocks()
    {
        // 5x3 BC1: level 0 is 2x1 blocks (16 bytes), level 1 2x1 pixels and level 2 1x1 are one block each.
        var body = new byte[16 + 8 + 8];
        var file = DdsReader.Read(Dds(5, 3, 3, "DXT1", body));
        Assert.Equal(3, file.MipCount);
        Assert.Equal([(5, 3, 16), (2, 1, 8), (1, 1, 8)], file.Surfaces.Select(s => (s.Width, s.Height, s.Length)));
        Assert.Equal(0, file.TrailingBytes);
        Assert.Equal(1, DdsDecoder.Decode(file, 0, 2).Width);

        // The edge block is clipped: pixel (4, 0) comes from the second block.
        ColourBlock(0xF800, 0xF800).CopyTo(body, 8);
        var image = DdsDecoder.Decode(DdsReader.Read(Dds(5, 3, 3, "DXT1", body)));
        AssertPixel(image, 3, 0, 0, 0, 0, 255);
        AssertPixel(image, 4, 0, 255, 0, 0, 255);
        AssertPixel(image, 4, 2, 255, 0, 0, 255);
    }

    [Fact]
    public void Missing_mip_count_flag_means_one_level_and_extra_bytes_are_trailing()
    {
        var data = Dds(4, 4, 0, "DXT1", new byte[8 + 8]);
        var file = DdsReader.Read(data);
        Assert.Equal(1, file.MipCount);
        Assert.Equal(8, file.TrailingBytes);
    }

    [Fact]
    public void Cubemap_has_six_images_each_with_its_mip_chain()
    {
        // 8x8 BC3 with 4 levels: 4 blocks + 1 + 1 + 1 per face.
        int face = (4 + 1 + 1 + 1) * 16;
        var body = new byte[6 * face];
        body[2 * face] = 0xAB; // first byte of +Y
        var file = DdsReader.Read(Dds(8, 8, 4, "DXT5", body, caps2: 0x200 | 0xFC00));
        Assert.True(file.IsCubemap);
        Assert.Equal(6, file.ImageCount);
        Assert.Equal(24, file.Surfaces.Count);
        Assert.Equal(128 + 2 * face, file.Surface(2, 0).Offset);
        Assert.Equal(0xAB, file.SurfaceData(file.Surface(2, 0))[0]);
        Assert.Equal(0, file.TrailingBytes);
    }

    [Fact]
    public void Dx10_header_gives_format_and_array_size()
    {
        var file = DdsReader.Read(Dds(4, 4, 1, "DX10", new byte[2 * 16], dx10: (77, 3, 0, 2)));
        Assert.Equal(DdsFormat.Bc3, file.Format);
        Assert.Equal(77, file.DxgiFormat);
        Assert.Equal(148, file.HeaderSize);
        Assert.Equal(2, file.ImageCount);
        Assert.Equal(0, file.TrailingBytes);

        var rgba = DdsReader.Read(Dds(1, 1, 1, "DX10", [10, 20, 30, 40], dx10: (28, 3, 0, 1)));
        AssertPixel(DdsDecoder.Decode(rgba), 0, 0, 10, 20, 30, 40);
        var bgra = DdsReader.Read(Dds(1, 1, 1, "DX10", [10, 20, 30, 40], dx10: (87, 3, 0, 1)));
        AssertPixel(DdsDecoder.Decode(bgra), 0, 0, 30, 20, 10, 40);
        Assert.Throws<DdsFormatException>(() => DdsReader.Read(Dds(4, 4, 1, "DX10", new byte[16], dx10: (95, 3, 0, 1)))); // BC6H
    }

    [Fact]
    public void Uncompressed_formats_scale_masked_channels_to_eight_bits()
    {
        // X1R5G5B5 (as meshes/sky0026.dds): no alpha mask, so alpha is 255.
        ushort x1r5g5b5 = (31 << 10) | (16 << 5) | 0;
        var file = DdsReader.Read(Dds(1, 1, 0, null, BitConverter.GetBytes(x1r5g5b5), pfFlags: 0x40, bitCount: 16, r: 0x7C00, g: 0x3E0, b: 0x1F));
        Assert.Equal(DdsFormat.Uncompressed, file.Format);
        AssertPixel(DdsDecoder.Decode(file), 0, 0, 255, 132, 0, 255);

        // A8R8G8B8 is stored B, G, R, A.
        var argb = DdsReader.Read(Dds(2, 1, 0, null, [1, 2, 3, 4, 5, 6, 7, 8], pfFlags: 0x41, bitCount: 32, r: 0xFF0000, g: 0xFF00, b: 0xFF, a: 0xFF000000));
        var image = DdsDecoder.Decode(argb);
        AssertPixel(image, 0, 0, 3, 2, 1, 4);
        AssertPixel(image, 1, 0, 7, 6, 5, 8);

        // R8G8B8 (24 bit) and L8 luminance.
        AssertPixel(DdsDecoder.Decode(DdsReader.Read(Dds(1, 1, 0, null, [1, 2, 3], pfFlags: 0x40, bitCount: 24, r: 0xFF0000, g: 0xFF00, b: 0xFF))), 0, 0, 3, 2, 1, 255);
        AssertPixel(DdsDecoder.Decode(DdsReader.Read(Dds(1, 1, 0, null, [77], pfFlags: 0x20000, bitCount: 8, r: 0xFF))), 0, 0, 77, 77, 77, 255);
    }

    [Fact]
    public void Bad_files_are_rejected()
    {
        var good = Dds(4, 4, 1, "DXT1", new byte[8]);
        Assert.Throws<DdsFormatException>(() => DdsReader.Read(good[..100]));
        Assert.Throws<DdsFormatException>(() => DdsReader.Read(good[..^1])); // surface past the end
        var badMagic = (byte[])good.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<DdsFormatException>(() => DdsReader.Read(badMagic));
        Assert.Throws<DdsFormatException>(() => DdsReader.Read(Dds(4, 4, 1, "BC7U", new byte[16])));
    }

    /// <summary>
    /// Every base-game <c>.dds</c>: headers parse, the computed surfaces fill the file exactly, and a sample of each
    /// format decodes (largest and smallest level). Also pins the format survey in docs/formats/dds.md.
    /// </summary>
    [Fact]
    public void Reads_every_base_game_dds()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not configured (KENSHI_PATH or meitou.local.json).");

        var files = Directory.EnumerateFiles(install!.DataDirectory, "*.dds", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var counts = new Dictionary<string, int>();
        var decodedPerKind = new Dictionary<string, int>();
        var failures = new List<string>();
        int noMips = 0, npot = 0;
        var trailing = new List<string>();
        foreach (var path in files)
        {
            DdsFile dds;
            try { dds = DdsReader.ReadFile(path); }
            catch (DdsFormatException e) { failures.Add($"{Path.GetRelativePath(install.DataDirectory, path)}: {e.Message}"); continue; }

            string kind = (dds.FourCC ?? $"RGB{dds.Masks!.BitCount}") + (dds.IsCubemap ? " cube" : "");
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            if (dds.TrailingBytes != 0) trailing.Add($"{Path.GetFileName(path)} {dds.TrailingBytes}");
            if (dds.MipCount == 1) noMips++;
            if (!IsPowerOfTwo(dds.Width) || !IsPowerOfTwo(dds.Height)) npot++;

            if (decodedPerKind.GetValueOrDefault(kind) < 25)
            {
                decodedPerKind[kind] = decodedPerKind.GetValueOrDefault(kind) + 1;
                for (int image = 0; image < dds.ImageCount; image++)
                {
                    var top = DdsDecoder.Decode(dds, image, 0);
                    Assert.Equal(dds.Width * dds.Height * 4, top.Pixels.Length);
                    DdsDecoder.Decode(dds, image, dds.MipCount - 1);
                }
            }
        }

        Assert.Empty(failures);
        // Two 512x512 DXT1 files hold 16 bytes after a complete 10-level chain (two extra 1x1 blocks); readers ignore them.
        Assert.Equal(["randomredrock.dds 16", "randomredrock_N.dds 16"], trailing);
        Assert.Equal(1907, files.Count);
        Assert.Equal(new Dictionary<string, int>
        {
            ["DXT5"] = 1157, ["DXT1"] = 698, ["DXT3"] = 47, ["DXT5 cube"] = 3, ["DXT1 cube"] = 1, ["RGB16"] = 1,
        }, counts);
        Assert.Equal(64, noMips);
        Assert.Equal(64, npot);
    }

    static bool IsPowerOfTwo(int v) => (v & (v - 1)) == 0;
}
