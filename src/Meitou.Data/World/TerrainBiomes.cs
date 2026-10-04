using System.Buffers.Binary;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// <c>data/newland/land/blendinfo.dat</c> (magic <c>KBI1</c>): which biomes the terrain blends in each cell of a
/// coarse grid. Cell (x, z) lists up to <see cref="SlotsPerCell"/> biome colours (the BIOMES <c>index</c>
/// colours of <c>biomemap.png</c>); <c>blendmap.png</c>'s R, G, B, A are the weights of slots 0..3 at each pixel.
/// Layout and evidence in docs/formats/terrain.md.
/// </summary>
public sealed class BlendInfoFile
{
    public const string RelativePath = "newland/land/blendinfo.dat";
    public const int SlotsPerCell = 5;
    static readonly byte[] Magic = "KBI1"u8.ToArray();

    BlendInfoFile(int cellsX, int cellsZ, uint[] slots, byte[] trailer) =>
        (CellsX, CellsZ, Slots, Trailer) = (cellsX, cellsZ, slots, trailer);

    /// <summary>Cells along world X and Z (32 × 32 in the base game: 2 × 2 zones per cell).</summary>
    public int CellsX { get; }
    public int CellsZ { get; }

    /// <summary>Stored slot values, <c>(z × CellsX + x) × SlotsPerCell + k</c>: RGB in the low 24 bits.</summary>
    public uint[] Slots { get; }

    /// <summary>The bytes after the slot table (Unknown meaning; docs/formats/terrain.md).</summary>
    public byte[] Trailer { get; }

    public double CellSize => (double)WorldLayout.WorldSize / CellsX;

    /// <summary>RGB colour of a slot (the high byte of the stored value is dropped), 0 = unused.</summary>
    public uint Slot(int cellX, int cellZ, int slot) => Slots[(cellZ * CellsX + cellX) * SlotsPerCell + slot] & 0xFFFFFF;

    /// <summary>The cell containing world point (x, z), clamped to the grid.</summary>
    public (int X, int Z) CellOf(double x, double z) =>
        (Math.Clamp((int)Math.Floor((x + WorldLayout.HalfWorldSize) / CellSize), 0, CellsX - 1),
         Math.Clamp((int)Math.Floor((z + WorldLayout.HalfWorldSize) / ((double)WorldLayout.WorldSize / CellsZ)), 0, CellsZ - 1));

    public static BlendInfoFile Open(GameInstall install) => Read(File.ReadAllBytes(Path.Combine(install.DataDirectory, RelativePath)));

    public static BlendInfoFile Read(byte[] data)
    {
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("Not a blend info file (magic KBI1 missing).");
        int cx = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        int cz = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        long table = (long)cx * cz * SlotsPerCell * 4;
        if (cx <= 0 || cz <= 0 || 12 + table > data.Length)
            throw new InvalidDataException($"Implausible grid {cx}x{cz} for {data.Length} bytes.");
        var slots = new uint[cx * cz * SlotsPerCell];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12 + 4 * i));
        return new BlendInfoFile(cx, cz, slots, data[(int)(12 + table)..]);
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
