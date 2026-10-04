using System.Buffers.Binary;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// <c>data/newland/land/blendinfo.dat</c> (magic <c>KBI1</c>): which biomes the terrain blends in each cell of a
/// square grid. Cell (x, z) lists up to <see cref="SlotsPerCell"/> biome colours (the BIOMES <c>index</c>
/// colours of <c>biomemap.png</c>); <c>blendmap.png</c>'s R, G, B, A are the weights of slots 0..3 at each pixel.
/// After the table each cell has a quadtree of slot masks saying which slots are in use where, which the game
/// queries to find the biomes a terrain page or water patch needs. Layout and evidence in docs/formats/terrain.md.
/// </summary>
public sealed class BlendInfoFile
{
    public const string RelativePath = "newland/land/blendinfo.dat";
    public const int SlotsPerCell = 5;
    static readonly byte[] Magic = "KBI1"u8.ToArray();

    BlendInfoFile(int cells, int resolution, uint[] slots, byte[][]? nodes) =>
        (Cells, Resolution, Slots, Nodes) = (cells, resolution, slots, nodes);

    /// <summary>Cells per side (32 in the base game: 2 × 2 zones per cell).</summary>
    public int Cells { get; }
    /// <summary>Same as <see cref="Cells"/>; the grid is square (kept for callers that think in X and Z).</summary>
    public int CellsX => Cells;
    public int CellsZ => Cells;
    /// <summary>Leaves per cell side of the slot-mask quadtree (32 in the base game: one blend-map pixel per leaf).</summary>
    public int Resolution { get; }

    /// <summary>Stored slot values, <c>(z × Cells + x) × SlotsPerCell + k</c>: RGB in the low 24 bits.</summary>
    public uint[] Slots { get; }

    /// <summary>
    /// Per cell (<c>z × Cells + x</c>) its slot-mask nodes: one byte when the cell uses a single slot, else
    /// 2 × <see cref="Resolution"/>² bytes indexed like a heap (node 1 the whole cell, node i's children 4i..4i+3,
    /// leaves from Resolution²; byte 0 repeats the root). Null when the file ends after the slot table.
    /// </summary>
    public byte[][]? Nodes { get; }

    public double CellSize => (double)WorldLayout.WorldSize / Cells;

    /// <summary>RGB colour of a slot (the high byte of the stored value is dropped), 0 = unused.</summary>
    public uint Slot(int cellX, int cellZ, int slot) => Slots[(cellZ * Cells + cellX) * SlotsPerCell + slot] & 0xFFFFFF;

    /// <summary>The cell containing world point (x, z), clamped to the grid.</summary>
    public (int X, int Z) CellOf(double x, double z) =>
        (Math.Clamp((int)Math.Floor((x + WorldLayout.HalfWorldSize) / CellSize), 0, Cells - 1),
         Math.Clamp((int)Math.Floor((z + WorldLayout.HalfWorldSize) / CellSize), 0, Cells - 1));

    /// <summary>Slot bits (bit k = slot k) of a node byte; the top three bits hold how many are set.</summary>
    public static int MaskSlots(byte node) => node & 0x1F;
    public static int MaskCount(byte node) => node >> 5;

    /// <summary>Morton code of a leaf: x in the even bits, z in the odd bits.</summary>
    public static int Morton(int x, int z)
    {
        int m = 0;
        for (int b = 0; b < 15; b++) m |= ((x >> b) & 1) << (2 * b) | ((z >> b) & 1) << (2 * b + 1);
        return m;
    }

    /// <summary>
    /// The mask of the smallest quadtree node of a cell that contains the box from (<paramref name="u0"/>, <paramref name="v0"/>)
    /// to (<paramref name="u1"/>, <paramref name="v1"/>), given as fractions 0..1 of the cell along X and Z: which of
    /// the cell's slots have weight anywhere in the box (the game's rule, docs/formats/terrain.md).
    /// </summary>
    public byte SlotMask(int cellX, int cellZ, double u0, double v0, double u1, double v1)
    {
        if (Nodes is null) throw new InvalidOperationException("The file has no slot masks.");
        var nodes = Nodes[cellZ * Cells + cellX];
        if (nodes.Length == 1) return nodes[0];
        int Leaf(double f) => Math.Clamp((int)Math.Floor(f * Resolution), 0, Resolution - 1);
        int a = Resolution * Resolution | Morton(Leaf(u0), Leaf(v0));
        int b = Resolution * Resolution | Morton(Leaf(u1), Leaf(v1));
        while (a != b) { a >>= 2; b >>= 2; }
        return nodes[a];
    }

    public static BlendInfoFile Open(GameInstall install) => Read(File.ReadAllBytes(Path.Combine(install.DataDirectory, RelativePath)));

    public static BlendInfoFile Read(byte[] data)
    {
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Not a blend info file (magic KBI1 missing).");
        int cells = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        int resolution = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        long table = (long)cells * cells * SlotsPerCell * 4;
        if (cells <= 0 || cells > 4096 || resolution <= 0 || resolution > 4096 || (resolution & (resolution - 1)) != 0 || 12 + table > data.Length)
            throw new InvalidDataException($"Implausible grid {cells}² / resolution {resolution} for {data.Length} bytes.");
        var slots = new uint[cells * cells * SlotsPerCell];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12 + 4 * i));
        int p = (int)(12 + table);
        if (p == data.Length) return new BlendInfoFile(cells, resolution, slots, null);

        // Slot-mask quadtrees: int32 node count, then per cell its root byte and, unless the root says one slot, the rest.
        if (p + 4 > data.Length) throw new InvalidDataException("Truncated slot-mask header.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
        p += 4;
        if (count < 2 * resolution * resolution || count > 1 << 24)
            throw new InvalidDataException($"Slot-mask node count {count} does not fit resolution {resolution}.");
        var nodes = new byte[cells * cells][];
        for (int c = 0; c < nodes.Length; c++)
        {
            if (p >= data.Length) throw new InvalidDataException($"Truncated slot masks at cell {c}.");
            byte root = data[p++];
            if (MaskCount(root) == 1) { nodes[c] = [root]; continue; }
            if (p + count - 1 > data.Length) throw new InvalidDataException($"Truncated slot masks at cell {c}.");
            var n = new byte[count];
            n[0] = root;
            data.AsSpan(p, count - 1).CopyTo(n.AsSpan(1));
            p += count - 1;
            nodes[c] = n;
        }
        if (p != data.Length) throw new InvalidDataException($"{data.Length - p} bytes after the slot masks.");
        return new BlendInfoFile(cells, resolution, slots, nodes);
    }
}

/// <summary>Terrain layers of a biome, in the order of Kenshi's terrain texture arrays (terrain shader, docs/formats/terrain.md).</summary>
public enum TerrainLayer { Base, Slope, Cliff, Grass, Dirt, Road }

/// <summary>
/// How a BIOMES record textures the terrain: six layers (diffuse + normal map, tiling), slope ranges, colour
/// overlay strengths and the distance fade. Slope values are converted the way the game hands them to the
/// shader (× 0.01, compared with <c>1 − normal.y</c>).
/// </summary>
public sealed record BiomeTerrain
{
    public required string StringId { get; init; }
    public required string Name { get; init; }
    /// <summary>Colour of the biome in <c>biomemap.png</c> / <c>blendinfo.dat</c> (RGB).</summary>
    public required uint Index { get; init; }
    /// <summary>Per <see cref="TerrainLayer"/>: diffuse and normal texture paths as stored (null when empty).</summary>
    public required string?[] Diffuse { get; init; }
    public required string?[] Normal { get; init; }
    /// <summary>Per layer: repeats per 5000 world units on X and Y.</summary>
    public required Vector2[] Tiling { get; init; }
    /// <summary>Shader slope ranges: X slope layer, Y cliff layer, Z "slope … 3" (unused by the shader), W grass.</summary>
    public required Vector4 SlopeMin { get; init; }
    public required Vector4 SlopeMax { get; init; }
    public required Vector4 SlopeBlend { get; init; }
    /// <summary>Colour-map strength for cliff, grass, dirt, road (base and slope always take the full colour).</summary>
    public required Vector4 OverlayMult { get; init; }
    /// <summary>Ground colour (RGB 0..1): what the textures fade to with distance.</summary>
    public required Vector3 GroundColour { get; init; }
    public required float FadeDistance { get; init; }
    public required float BrightnessFix { get; init; }
    public required float DistortWavelength { get; init; }
    public required float DistortAmplitude { get; init; }

    /// <summary>Factor the game applies to the FCS slope values before handing them to the shader (Verified, kenshi_x64.exe).</summary>
    public const float SlopeScale = 0.01f;

    static readonly string[] TextureFields = ["texture base", "texture slope", "texture vertical", "texture grass", "texture dirt", "texture road"];
    static readonly string[] TilingSuffix = ["0", "1", "2", "grass", "dirt", "road"];

    public static BiomeTerrain FromRecord(GameRecord r)
    {
        static string? Tex(GameRecord r, string field) => r.GetPath(field) is { Length: > 0 } p ? p : null;
        // Defaults are the fcs.def ones.
        Vector2[] defaultTiling = [new(50, 50), new(70, 70), new(40, 10), new(40, 40), new(40, 40), new(40, 40)];
        Vector4 Slope(string what, float d1, float d2, float dGrass) => new Vector4(
            r.GetFloat($"slope {what} 1", d1), r.GetFloat($"slope {what} 2", d2), r.GetFloat($"slope {what} 3", 0), r.GetFloat($"slope {what} grass", dGrass)) * SlopeScale;
        uint ground = (uint)r.GetInt("ground colour", 0xC0A040) & 0xFFFFFF;
        float brightness = r.GetFloat("brightness fix", 1);
        return new BiomeTerrain
        {
            StringId = r.StringId,
            Name = r.Name,
            Index = (uint)r.GetInt("index") & 0xFFFFFF,
            Diffuse = [.. TextureFields.Select(f => Tex(r, f))],
            Normal = [.. TextureFields.Select(f => Tex(r, f + " normal"))],
            Tiling = [.. TilingSuffix.Select((s, i) => new Vector2(r.GetFloat($"tiling X {s}", defaultTiling[i].X), r.GetFloat($"tiling Y {s}", defaultTiling[i].Y)))],
            SlopeMin = Slope("min", 0, 0, 0),
            SlopeMax = Slope("max", 90, 90, 20),
            SlopeBlend = Slope("fade", 5, 5, 5),
            OverlayMult = new Vector4(r.GetFloat("overlay mult vertical", 1), r.GetFloat("overlay mult grass", 1), r.GetFloat("overlay mult dirt", 1), r.GetFloat("overlay mult road", 1)),
            GroundColour = new Vector3((ground >> 16) & 0xFF, (ground >> 8) & 0xFF, ground & 0xFF) / 255f,
            FadeDistance = r.GetFloat("fade distance", 4000),
            BrightnessFix = brightness == 0 ? 1 : brightness, // the game replaces 0 with 1
            DistortWavelength = r.GetFloat("distort wavelength", 1000),
            DistortAmplitude = r.GetFloat("distort amplitude", 0),
        };
    }

    /// <summary>All BIOMES records by <see cref="Index"/> colour (the first record wins if two share a colour).</summary>
    public static Dictionary<uint, BiomeTerrain> ByIndex(GameDatabase db)
    {
        var result = new Dictionary<uint, BiomeTerrain>();
        foreach (var r in db.OfType(FcsRecordType.BIOMES).OrderBy(r => r.StringId, StringComparer.Ordinal))
            result.TryAdd((uint)r.GetInt("index") & 0xFFFFFF, FromRecord(r));
        return result;
    }
}

/// <summary>
/// Paths of the land maps under <c>data/newland/land/</c> used to texture the terrain (docs/formats/terrain.md):
/// the biome blend map, and the 8 × 8 tiled overlay (<c>new_overlay.X.Y.png</c>) and colour (<c>colour.X.Y.png</c>)
/// maps, tile X along world +X and Y along +Z.
/// </summary>
public static class TerrainMaps
{
    public const string LandDirectory = "newland/land";
    public const string BlendMap = "newland/land/blendmap.png";
    public const string BiomeMap = "newland/land/biomemap.png";
    public const int OverlayTiles = 8;

    /// <summary><c>new_overlay</c>: R and G grass (the shader takes the larger), B dirt, A road. <c>colour</c>: RGB tint, A multiplies gloss.</summary>
    public static string OverlayTile(GameInstall install, string kind, int x, int z) =>
        Path.Combine(install.DataDirectory, LandDirectory, "overlaymaps", $"{kind}.{x}.{z}.png");
}

/// <summary>
/// Per-pixel blends of per-biome values over a blend-map-sized grid of the whole world: each pixel weights the
/// values of its cell's slots by the blend map (slot 4 takes the remainder), the way the terrain shader blends biomes.
/// Used for the far terrain's ground colour and the water parameters.
/// </summary>
public static class BiomeField
{
    /// <param name="blend">The blend map (RGBA, row = +Z), a whole number of pixels per blend-info cell.</param>
    /// <param name="value">A biome's value by its index colour; null leaves the biome out of the blend.</param>
    /// <param name="fallback">Value of pixels without any known biome.</param>
    public static Vector4[] Bake(BlendInfoFile info, int width, int height, byte[] blend, Func<uint, Vector4?> value, Vector4 fallback)
    {
        if (blend.Length != width * height * 4 || width % info.Cells != 0 || height % info.Cells != 0)
            throw new ArgumentException($"A {width}x{height} blend map does not fit {info.Cells}² cells.");
        int ppx = width / info.Cells, ppz = height / info.Cells;
        var cache = new Dictionary<uint, Vector4?>();
        Vector4? Get(uint c)
        {
            if (c == 0) return null;
            lock (cache)
            {
                if (!cache.TryGetValue(c, out var v)) cache[c] = v = value(c);
                return v;
            }
        }
        var result = new Vector4[width * height];
        Parallel.For(0, height, y =>
        {
            Span<float> w = stackalloc float[BlendInfoFile.SlotsPerCell];
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4, cx = x / ppx, cz = y / ppz;
                int sum = blend[o] + blend[o + 1] + blend[o + 2] + blend[o + 3];
                for (int k = 0; k < 4; k++) w[k] = blend[o + k];
                w[4] = Math.Max(0, 255 - sum);
                var acc = Vector4.Zero;
                float total = 0;
                for (int k = 0; k < BlendInfoFile.SlotsPerCell; k++)
                {
                    if (w[k] <= 0 || Get(info.Slot(cx, cz, k)) is not { } v) continue;
                    acc += v * w[k];
                    total += w[k];
                }
                result[y * width + x] = total > 0 ? acc / total : fallback;
            }
        });
        return result;
    }
}
