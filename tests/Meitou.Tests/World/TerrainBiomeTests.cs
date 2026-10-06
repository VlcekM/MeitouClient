using System.Buffers.Binary;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class TerrainBiomeTests
{
    static byte[] BlendInfo(int cells, int resolution, uint[] slots, byte[] trailer)
    {
        var data = new byte[12 + slots.Length * 4 + trailer.Length];
        "KBI1"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), cells);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), resolution);
        for (int i = 0; i < slots.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12 + 4 * i), slots[i]);
        trailer.CopyTo(data, 12 + slots.Length * 4);
        return data;
    }

    [Fact]
    public void Blend_info_reads_slot_colours_per_cell()
    {
        uint[] slots = new uint[4 * 5];
        slots[0] = 0x00FF00;
        slots[5] = 0xB400FF;
        slots[6] = 0x0A1E78;
        // Slot masks at resolution 2 (8 nodes): cells 0, 2, 3 one slot (a single byte); cell 1 a full tree: slot 0
        // in the left leaves, slot 1 in the right ones (leaf index 4 | morton(x, z), x in the even bit).
        byte[] full1 = [0x43, 0x43, 0, 0, 0x21, 0x22, 0x21, 0x22];
        byte[] trailer = [8, 0, 0, 0, 0x21, .. full1, 0x21, 0x22]; // cells 2 and 3: one slot each
        var file = BlendInfoFile.Read(BlendInfo(2, 2, slots, []));
        Assert.Null(file.Nodes);
        file = BlendInfoFile.Read(BlendInfo(2, 2, slots, trailer));
        Assert.Equal((2, 2, 2), (file.CellsX, file.CellsZ, file.Resolution));
        Assert.Equal(0x00FF00u, file.Slot(0, 0, 0));
        Assert.Equal(0x0A1E78u, file.Slot(1, 0, 1));
        Assert.Single(file.Nodes![0]);
        Assert.Equal(8, file.Nodes[1].Length);
        Assert.Equal(0x21, file.SlotMask(1, 0, 0.1, 0.1, 0.2, 0.2));   // one left leaf: slot 0
        Assert.Equal(0x22, file.SlotMask(1, 0, 0.6, 0.6, 0.9, 0.9));   // one right leaf: slot 1
        Assert.Equal(0x43, file.SlotMask(1, 0, 0.1, 0.1, 0.9, 0.2));   // spans two leaves: their common parent, the root
        Assert.Equal(0x21, file.SlotMask(0, 0, 0, 0, 1, 1));          // single-byte cell
        Assert.Equal((0, 0), file.CellOf(-1, -1));
        Assert.Equal((1, 1), file.CellOf(0, 0));
        Assert.Equal((1, 1), file.CellOf(1e9, 1e9)); // clamped
        Assert.Equal(0b1001, BlendInfoFile.Morton(1, 2));
        Assert.Throws<InvalidDataException>(() => BlendInfoFile.Read("KBI2\0\0\0\0\0\0\0\0"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => BlendInfoFile.Read(BlendInfo(4, 4, [1, 2], [])));
        Assert.Throws<InvalidDataException>(() => BlendInfoFile.Read(BlendInfo(2, 2, slots, [8, 0, 0, 0, 0x21])));
    }

    [Fact]
    [Slow]
    public void Base_game_slot_masks_describe_the_blend_map()
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
                    Assert.Equal(BlendInfoFile.MaskSlots(n[i]), BlendInfoFile.MaskSlots((byte)(n[4 * i] | n[4 * i + 1] | n[4 * i + 2] | n[4 * i + 3])));
                // A leaf is one blend-map pixel: it holds the slots weighted there, and none that are not weighted in its 3 x 3 neighbourhood.
                for (int lz = 0; lz < 32; lz++)
                    for (int lx = 0; lx < 32; lx++)
                    {
                        int px = cx * 32 + lx, pz = cz * 32 + lz, own = Used(px, pz), near = 0;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++) near |= Used(px + dx, pz + dz);
                        int leaf = BlendInfoFile.MaskSlots(n[1024 | BlendInfoFile.Morton(lx, lz)]);
                        Assert.True((leaf & own) == own && (leaf & ~near) == 0, $"cell {cx},{cz} leaf {lx},{lz}: mask {leaf:X2}, weights {own:X2}, around {near:X2}");
                    }
            }
    }

    static uint Rgb(byte[] p, int i) => (uint)(p[i] << 16 | p[i + 1] << 8 | p[i + 2]);

    [Fact]
    [Slow]
    public void Base_game_blend_map_channels_weight_the_cell_slots()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var info = BlendInfoFile.Open(install!);
        Assert.Equal(32, info.Cells);
        var blend = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install!.DataDirectory, TerrainMaps.BlendMap)));
        var biome = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BiomeMap)));
        Assert.Equal((1024, 1024), (blend.Width, biome.Width));

        // R, G, B, A weight slots 0..3 and the remainder (255 minus their sum) weights slot 4; the strongest names the slot
        // whose colour biomemap.png shows (row = +Z, like the heightmap). Where the channels are all 0, slot 4 alone applies.
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

    [Fact]
    [Slow]
    public void Base_game_overlay_alpha_marks_roads()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var points = WorldLevelData.Load(install!).Roads().SelectMany(r => r.Points).Where((_, i) => i % 40 == 0).ToList();
        var tiles = new Dictionary<(int, int), RgbaImage>();
        double on = 0, off = 0;
        foreach (var p in points)
        {
            on += Alpha(p.X, p.Z);
            off += Alpha(p.X + 300, p.Z + 300);
        }
        Assert.True(on > 5 * off, $"road alpha {on / points.Count:0.0} vs {off / points.Count:0.0} off the road");

        int Alpha(double x, double z)
        {
            var first = tiles.Count > 0 ? tiles.Values.First() : null;
            int tile = first?.Width ?? TextureLoader.LoadImage(File.ReadAllBytes(TerrainMaps.OverlayTile(install!, "new_overlay", 0, 0))).Width;
            double unit = (double)WorldLayout.WorldSize / (tile * TerrainMaps.OverlayTiles);
            int px = Math.Clamp((int)((x + WorldLayout.HalfWorldSize) / unit), 0, tile * 8 - 1), pz = Math.Clamp((int)((z + WorldLayout.HalfWorldSize) / unit), 0, tile * 8 - 1);
            var key = (px / tile, pz / tile);
            if (!tiles.TryGetValue(key, out var img))
                tiles[key] = img = TextureLoader.LoadImage(File.ReadAllBytes(TerrainMaps.OverlayTile(install!, "new_overlay", key.Item1, key.Item2)));
            return img.Pixels[((pz % tile) * tile + px % tile) * 4 + 3];
        }
    }
}
