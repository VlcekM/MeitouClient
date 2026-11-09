using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class TerrainBiomeTests
{
    [Fact]
    [Slow]
    public void Base_game_slot_masks_and_channels_describe_the_blend_map()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var info = BlendInfoFile.Open(install!);
        Assert.Equal((32, 32), (info.Cells, info.Resolution));
        var nodes = info.Nodes!;
        Assert.Equal(656, nodes.Count(n => n.Length == 2048)); // the rest (368) are single bytes
        Assert.Equal(368, nodes.Count(n => n.Length == 1));
        var blend = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install!.DataDirectory, TerrainMaps.BlendMap)));
        int Used(int px, int pz)
        {
            int o = (Math.Clamp(pz, 0, 1023) * 1024 + Math.Clamp(px, 0, 1023)) * 4, m = 0;
            for (int k = 0; k < 4; k++) if (blend.Pixels[o + k] > 0) m |= 1 << k;
            if (255 - blend.Pixels[o] - blend.Pixels[o + 1] - blend.Pixels[o + 2] - blend.Pixels[o + 3] > 0) m |= 16;
            return m;
        }
        for (int cz = 0; cz < 32; cz++)
            for (int cx = 0; cx < 32; cx++)
            {
                var n = nodes[cz * 32 + cx];
                for (int k = 0; k < 5; k++)
                    if ((BlendInfoFile.MaskSlots(n[0]) & (1 << k)) != 0) Assert.NotEqual(0u, info.Slot(cx, cz, k));
                if (n.Length == 1) continue;
                Assert.Equal(n[0], n[1]);
                for (int i = 1; i < n.Length; i++)
                    Assert.Equal(System.Numerics.BitOperations.PopCount((uint)BlendInfoFile.MaskSlots(n[i])), BlendInfoFile.MaskCount(n[i]));
                for (int i = 1; i < 1024 / 2; i++)
                    Assert.Equal(BlendInfoFile.MaskSlots(n[i]),
                            BlendInfoFile.MaskSlots((byte)(n[4 * i] | n[4 * i + 1] | n[4 * i + 2] | n[4 * i + 3])));
                // A leaf is one blend-map pixel: it holds the slots weighted there, and none that are not weighted in its 3 x 3 neighbourhood.
                for (int lz = 0; lz < 32; lz++)
                    for (int lx = 0; lx < 32; lx++)
                    {
                        int px = cx * 32 + lx, pz = cz * 32 + lz, own = Used(px, pz), near = 0;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++) near |= Used(px + dx, pz + dz);
                        int leaf = BlendInfoFile.MaskSlots(n[1024 | BlendInfoFile.Morton(lx, lz)]);
                        Assert.True((leaf & own) == own && (leaf & ~near) == 0,
                                $"cell {cx},{cz} leaf {lx},{lz}: mask {leaf:X2}, weights {own:X2}, around {near:X2}");
                    }
            }

        // R, G, B, A weight slots 0..3 and the remainder (255 minus their sum) weights slot 4; the strongest names the slot
        // whose colour biomemap.png shows (row = +Z, like the heightmap). Where the channels are all 0, slot 4 alone applies.
        var biome = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BiomeMap)));
        Assert.Equal((1024, 1024), (blend.Width, biome.Width));
        int agree = 0, remainderOnly = 0, partial = 0;
        for (int y = 0; y < 1024; y++)
            for (int x = 0; x < 1024; x++)
            {
                int p = (y * 1024 + x) * 4;
                int sum = blend.Pixels[p] + blend.Pixels[p + 1] + blend.Pixels[p + 2] + blend.Pixels[p + 3];
                int[] w = [blend.Pixels[p], blend.Pixels[p + 1], blend.Pixels[p + 2], blend.Pixels[p + 3], 255 - sum];
                int best = Array.IndexOf(w, w.Max());
                if (sum == 0) remainderOnly++;
                else if (sum < 240) partial++;
                if (info.Slot(x / 32, y / 32, best) == Rgb(biome.Pixels, p)) agree++;
            }
        Assert.Equal(1024 * 1024, agree);
        Assert.Equal(0, partial); // Observed: the channels sum to 240..255 or to 0
        Assert.InRange(remainderOnly, 1, 1024 * 1024 / 10);

        // Every colour used resolves to a BIOMES record.
        var db = GameDatabase.Load(LoadOrder.BaseGame(install));
        var biomes = BiomeTerrain.ByIndex(db);
        for (int cz = 0; cz < 32; cz++)
            for (int cx = 0; cx < 32; cx++)
            {
                for (int k = 0; k < BlendInfoFile.SlotsPerCell; k++)
                    if (info.Slot(cx, cz, k) is var c and not 0) Assert.True(biomes.ContainsKey(c), $"cell {cx},{cz} slot {k}: #{c:X6}");
            }
        var sample = biomes.Values.First(b => b.Diffuse[0] is not null);
        Assert.All(sample.Tiling, t => Assert.True(t.X > 0 && t.Y > 0));
        Assert.True(sample.SlopeMax.X <= 1.5f, "slope values are scaled by 0.01");
    }

    static uint Rgb(byte[] p, int i) => (uint)(p[i] << 16 | p[i + 1] << 8 | p[i + 2]);
}
