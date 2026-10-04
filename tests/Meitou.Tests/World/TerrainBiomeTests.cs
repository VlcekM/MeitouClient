using System.Buffers.Binary;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

namespace Meitou.Tests.World;

public class TerrainBiomeTests
{
    static byte[] BlendInfo(int cx, int cz, uint[] slots, byte[] trailer)
    {
        var data = new byte[12 + slots.Length * 4 + trailer.Length];
        "KBI1"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), cx);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), cz);
        for (int i = 0; i < slots.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12 + 4 * i), slots[i]);
        trailer.CopyTo(data, 12 + slots.Length * 4);
        return data;
    }

    [Fact]
    public void Blend_info_reads_slot_colours_per_cell()
    {
        uint[] slots = [0x00FF00, 0, 0, 0, 0, 0xB400FF, 0x0A1E78, 0, 0, 0];
        var file = BlendInfoFile.Read(BlendInfo(2, 1, slots, [1, 2, 3]));
        Assert.Equal((2, 1), (file.CellsX, file.CellsZ));
        Assert.Equal(0x00FF00u, file.Slot(0, 0, 0));
        Assert.Equal(0x0A1E78u, file.Slot(1, 0, 1));
        Assert.Equal([1, 2, 3], file.Trailer);
        Assert.Equal((0, 0), file.CellOf(-1, 0));
        Assert.Equal((1, 0), file.CellOf(0, 0));
        Assert.Equal((1, 0), file.CellOf(1e9, 1e9)); // clamped
        Assert.Throws<InvalidDataException>(() => BlendInfoFile.Read("KBI2\0\0\0\0\0\0\0\0"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => BlendInfoFile.Read(BlendInfo(4, 4, [1, 2], [])));
    }

    static uint Rgb(byte[] p, int i) => (uint)(p[i] << 16 | p[i + 1] << 8 | p[i + 2]);

    [Fact]
    public void Base_game_blend_map_channels_weight_the_cell_slots()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var info = BlendInfoFile.Open(install!);
        Assert.Equal((32, 32), (info.CellsX, info.CellsZ));
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
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
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
