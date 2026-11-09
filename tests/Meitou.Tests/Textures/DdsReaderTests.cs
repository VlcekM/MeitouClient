using System.Text;
using Meitou.Content;
using Meitou.Data.Textures;

namespace Meitou.Tests.Textures;

public class DdsReaderTests
{
    const uint FlagsBase = 0x1 | 0x2 | 0x4 | 0x1000; // CAPS | HEIGHT | WIDTH | PIXELFORMAT

    /// <summary>
    /// A DDS file: legacy header (and DX10 header if given), then <paramref name="body"/>.
    /// </summary>
    static byte[] Dds(int width, int height, int mips, string? fourCc, byte[] body,
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
        w.Write(fourCc is null ? new byte[4] : Encoding.ASCII.GetBytes(fourCc));
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

    /// <summary>
    /// A BC1 colour block: endpoints and one 2-bit index per pixel (row-major).
    /// </summary>
    static byte[] ColourBlock(ushort c0, ushort c1, params int[] indices)
    {
        uint bits = 0;
        for (int i = 0; i < indices.Length; i++) bits |= (uint)indices[i] << (2 * i);
        return [.. BitConverter.GetBytes(c0), .. BitConverter.GetBytes(c1), .. BitConverter.GetBytes(bits)];
    }

    /// <summary>
    /// A BC3 alpha / BC4 block: endpoints and one 3-bit index per pixel.
    /// </summary>
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
    public void Bc3_alpha_has_eight_value_and_six_value_modes()
    {
        var eight = DdsDecoder.Decode(DdsReader.Read(Dds(4, 4, 1, "DXT5", [.. ChannelBlock(255, 0, 0, 1, 2, 7), .. ColourBlock(0xFFFF, 0xFFFF)])));
        Assert.Equal(new byte[] { 255, 0, 219, 36 }, Enumerable.Range(0, 4).Select(x => eight[x, 0].A));
        AssertPixel(eight, 0, 0, 255, 255, 255, 255);

        var six = DdsDecoder.Decode(DdsReader.Read(Dds(4, 4, 1, "DXT5", [.. ChannelBlock(0, 100, 2, 5, 6, 7), .. ColourBlock(0, 0)])));
        Assert.Equal(new byte[] { 20, 80, 0, 255 }, Enumerable.Range(0, 4).Select(x => six[x, 0].A));
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

    /// <summary>
    /// Every base-game <c>.dds</c>: headers parse, the computed surfaces fill the file exactly, and a sample of each
    /// format decodes (largest and smallest level). Also pins the format survey in docs/formats/dds.md.
    /// </summary>
    [Fact]
    [Slow]
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
            try
            {
                dds = DdsReader.ReadFile(path);
            }
            catch (DdsFormatException e)
            {
                failures.Add($"{Path.GetRelativePath(install.DataDirectory, path)}: {e.Message}");
                continue;
            }

            string kind = (dds.FourCc ?? $"RGB{dds.Masks!.BitCount}") + (dds.IsCubemap ? " cube" : "");
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
