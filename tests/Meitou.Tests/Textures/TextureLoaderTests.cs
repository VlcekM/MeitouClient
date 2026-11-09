using Meitou.Content;
using Meitou.Data.Textures;

namespace Meitou.Tests.Textures;

public class TextureLoaderTests
{
    /// <summary>
    /// Every base-game <c>.tga</c> and <c>.jpg</c>, and a sample of the <c>.png</c> files, decode to RGBA8.
    /// </summary>
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
