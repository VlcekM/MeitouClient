using Meitou.Content;
using Meitou.Data.Gameplay;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The item icons the character renderer draws (docs/game/ui-screens.md "Item icons") against the pictures the game saved of the same items.
/// </summary>
public class ItemIconRenderTests
{
    static VulkanDevice? TryCreate()
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = false });
        }
        catch (Exception e)
            when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    /// <summary>
    /// Draws the icons of <paramref name="keys"/> in a headless context; the RGBA texels (rows bottom first, as the targets are) by key and the
    /// texture sizes.
    /// </summary>
    static Dictionary<IconKey, (byte[] Pixels, int Width, int Height)> Draw(GameInstall install, IReadOnlyList<IconKey> keys)
    {
        using var device = TryCreate();

        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");

        using var ctx = new GpuContext(device!);
        using var characters = new CharacterRenderer(ctx, install, InstallData.FullLoadOrder!, new AssetLocator(install)) { LoadBudget = 0 };
        var result = new Dictionary<IconKey, (byte[], int, int)>();

        // The meshes are read on workers (CharacterContent.IconMeshReady): frames go on until every icon is final, for at most a minute.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int frame = 0; watch.Elapsed < TimeSpan.FromMinutes(1) && result.Count < keys.Count; frame++)
        {
            if (frame > 0 && characters.PendingIcons > 0) Thread.Sleep(5);
            ctx.BeginFrame();
            foreach (var key in keys) characters.TryGetIcon(key, out _);
            characters.Update(default);
            characters.DrawIcons();
            ctx.Finish();
            if (characters.PendingIcons == 0)
                foreach (var key in keys)
                    if (characters.TryGetIcon(key, out var icon))
                        result[key] = (ctx.ReadBack(icon.Texture, 4), icon.Texture.Desc.Width, icon.Texture.Desc.Height);
        }
        return result;
    }

    /// <summary>
    /// Share of the pixels of <paramref name="a"/> and <paramref name="b"/> (same size) that are drawn in at least one that are drawn in both;
    /// <paramref name="flipB"/> reads b upside down.
    /// </summary>
    static double Overlap(byte[] a, byte[] b, int w, int h, bool flipA, bool flipB)
    {
        int both = 0, either = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool ina = a[((flipA ? h - 1 - y : y) * w + x) * 4 + 3] > 127, inb = b[((flipB ? h - 1 - y : y) * w + x) * 4 + 3] > 127;
                if (ina && inb) both++;
                if (ina || inb) either++;
            }
        return either == 0 ? 0 : (double)both / either;
    }

    /// <summary>
    /// The largest relative difference of a channel's mean between the pixels both pictures draw (<paramref name="ours"/> upside down, as the targets
    /// are).
    /// </summary>
    static double MeanColourDifference(byte[] ours, byte[] theirs, int w, int h)
    {
        var (a, b, n) = (new double[3], new double[3], 0);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4, j = ((h - 1 - y) * w + x) * 4;
                if (theirs[i + 3] <= 127 || ours[j + 3] <= 127) continue;
                for (int c = 0; c < 3; c++) { a[c] += ours[j + c]; b[c] += theirs[i + c]; }
                n++;
            }
        return n == 0 ? 1 : Enumerable.Range(0, 3).Max(c => Math.Abs(a[c] - b[c]) / Math.Max(b[c], 1));
    }

    [Fact]
    [Slow]
    public void Icons_drawn_from_the_mesh_cover_the_same_part_of_the_picture_as_the_games_saved_ones()
    {
        var install = InstallData.Install;

        Assert.SkipWhen(install is null || InstallData.FullLoadOrder is null, "no Kenshi install");

        var cache = Path.Combine(install!.DataDirectory, "icons");

        // Saved pictures of vanilla meshes (a modded install's pictures of weapons are often of a mod's mesh): a bag, a hat, three armours, the
        // starter loincloth with its torn cut-outs, a basket and bolts, each skipped when the install has not saved it.
        var wanted = new List<(IconKey Key, string File)>();
        foreach (var (item, material) in new[]
        {
            ("576-gamedata.base", "686-gamedata.base"), ("2149-gamedata.base", "2147-gamedata.base"), ("2156-gamedata.base", "2155-gamedata.base"),
            ("2167-gamedata.base", "2162-gamedata.base"), ("2194-gamedata.base", "2191-gamedata.base"), ("45554-gamedata.base", "5263-gamedata.base"),
            ("2308-clothes_v1.mod", "2312-clothes_v1.mod"), ("96014-rebirth.mod", "96015-rebirth.mod"),
        })
        {
            string file = Path.Combine(cache, $"{item}.{material}.png");
            if (File.Exists(file)) wanted.Add((new IconKey(item, material), file));
        }

        Assert.NotEmpty(wanted);

        var drawn = Draw(install, [.. wanted.Select(w => w.Key)]);
        string dump = Path.Combine(Path.GetTempPath(), "meitou-icon-test");
        Directory.CreateDirectory(dump);
        foreach (var (key, file) in wanted)
        {
            Assert.True(drawn.TryGetValue(key, out var ours), $"no icon drawn for {key}");

            var theirs = TextureLoader.LoadImage(File.ReadAllBytes(file));

            Assert.Equal((theirs.Width, theirs.Height), (ours.Width, ours.Height));

            double overlap = Overlap(ours.Pixels, theirs.Pixels, ours.Width, ours.Height, flipA: false, flipB: true);
            var flipped = new byte[ours.Pixels.Length];
            for (int y = 0; y < ours.Height; y++) ours.Pixels.AsSpan((ours.Height - 1 - y) * ours.Width * 4,
                    ours.Width * 4).CopyTo(flipped.AsSpan(y * ours.Width * 4));
            string name = Path.GetFileNameWithoutExtension(file);
            PngWriter.Write(Path.Combine(dump, name + ".ours.png"), ours.Width, ours.Height, flipped);
            File.Copy(file, Path.Combine(dump, name + ".game.png"), overwrite: true);
            double requiredOverlap = key.Record == "2308-clothes_v1.mod" ? 0.99 : 0.9;

            Assert.True(overlap >= requiredOverlap,
                    $"{name}: the drawn icon covers {overlap:0.000} of the saved one's picture (ours {ours.Width}x{ours.Height})");

            double colour = MeanColourDifference(ours.Pixels, theirs.Pixels, ours.Width, ours.Height);

            Assert.True(colour <= 0.06, $"{name}: the drawn icon's mean colour is {colour:P0} off the saved one's");
        }
    }
}
