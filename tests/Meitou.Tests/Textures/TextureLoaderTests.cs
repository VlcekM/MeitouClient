using Meitou.Content;
using Meitou.Data.Textures;

namespace Meitou.Tests.Textures;

public class TextureLoaderTests
{
    [Fact]
    public void Loads_dds_through_the_loader_with_all_mips()
    {
        // 8x8 DXT1, 4 levels, all blocks pure red.
        var block = new byte[] { 0x00, 0xF8, 0x00, 0xF8, 0, 0, 0, 0 };
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("DDS "u8.ToArray());
        w.Write(124); w.Write(0x1007u | DdsReader.FlagMipMapCount); w.Write(8); w.Write(8); w.Write(0); w.Write(0); w.Write(4);
        for (int i = 0; i < 11; i++) w.Write(0);
        w.Write(32); w.Write(4u); w.Write("DXT1"u8.ToArray());
        for (int i = 0; i < 5; i++) w.Write(0);
        w.Write(0x1000u); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int i = 0; i < 4 + 1 + 1 + 1; i++) w.Write(block);
        var path = Path.Combine(Path.GetTempPath(), $"meitou-test-{Guid.NewGuid():N}.dds");
        File.WriteAllBytes(path, ms.ToArray());
        try
        {
            var tex = TextureLoader.LoadFile(path);
            Assert.Equal([8, 4, 2, 1], tex.Levels.Select(l => l.Width));
            Assert.Equal((255, 0, 0, 255), ((int)tex.Levels[3][0, 0].R, (int)tex.Levels[3][0, 0].G, (int)tex.Levels[3][0, 0].B, (int)tex.Levels[3][0, 0].A));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Every base-game <c>.tga</c> and <c>.jpg</c>, and a sample of the <c>.png</c> files, decode to RGBA8.</summary>
    [Fact]
    [Slow]
    public void Loads_base_game_png_tga_and_jpg()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not configured (KENSHI_PATH or meitou.local.json).");

        var all = Directory.EnumerateFiles(install!.DataDirectory, "*", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Order().ToList());
        var files = all.GetValueOrDefault(".tga", []).Concat(all.GetValueOrDefault(".jpg", []))
            .Concat(all.GetValueOrDefault(".png", []).Where((_, i) => i % 20 == 0)).ToList();
        Assert.Equal(11, all.GetValueOrDefault(".tga", []).Count);
        Assert.Equal(1967, all.GetValueOrDefault(".png", []).Count);
        foreach (var file in files)
        {
            var tex = TextureLoader.LoadFile(file, allMips: false);
            Assert.True(tex.Width > 0 && tex.Height > 0, file);
            Assert.Equal(tex.Width * tex.Height * 4, tex.Levels[0].Pixels.Length);
        }
    }
}
