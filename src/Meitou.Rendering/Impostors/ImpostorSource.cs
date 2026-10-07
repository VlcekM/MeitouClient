using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Meitou.Data.World;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// The material settings of one mesh's parts, as <see cref="FoliageRenderer"/> builds them from the FOLIAGE_MESH record (its <c>AssetFor</c>;
/// docs/formats/foliage.md "Materials"): the texture names and the uniforms the mesh shader gets.
/// </summary>
public sealed record ImpostorMaterial(string? Diffuse, string? Normal, string? Diffuse2, string? Normal2, float AlphaThreshold, bool DoubleSided,
    bool Triplanar, Vector2 Tile, float Specular)
{
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Diffuse}|{Normal}|{Diffuse2}|{Normal2}|{AlphaThreshold:R}|{DoubleSided}|{Triplanar}|{Tile.X:R},{Tile.Y:R}|{Specular:R}");
}

/// <summary>
/// One thing to bake: a FOLIAGE_MESH's mesh (and leaves mesh) with its materials, the record's largest scale (which picks the size class) and the
/// files they resolve to. <see cref="Key"/> identifies the atlas in the cache.
/// </summary>
public sealed class ImpostorSource
{
    public required string Name { get; init; }
    public required string MeshPath { get; init; }
    public string? LeavesPath { get; init; }
    public required ImpostorMaterial Main { get; init; }
    public ImpostorMaterial? Leaves { get; init; }
    /// <summary>The largest scale the placer gives an instance (<see cref="FoliageMesh.MaxScale"/>).</summary>
    public float MaxScale { get; init; } = 1;
    /// <summary>The mean of the record's two scale limits: the instance the bake's texture detail is matched to (<see cref="ImpostorClass.LodBias"/>).</summary>
    public float MeanScale { get; init; } = 1;
    /// <summary>Resolved paths of every texture the materials name (null when not found), for the key.</summary>
    public IReadOnlyList<string?> TexturePaths { get; init; } = [];

    /// <summary>
    /// For a TERRAIN-mode rock (docs/impostors.md section 12): the terrain's parameter row of the biome whose material it is baked with (-1: not a rock; the
    /// source is an ordinary mesh) and the text that identifies that material (<c>TerrainTextures.BiomeKey</c>), part of the cache key. The rock is baked with the
    /// terrain's mesh material, so the textures its record names (none are used in TERRAIN mode) are not part of it.
    /// </summary>
    public int RockBiome { get; init; } = -1;
    public string? RockMaterial { get; init; }
    public bool IsRock => RockBiome >= 0;
    /// <summary>Changes with the rock bake (its shader, frame rule or placement); in the cache key of rock atlases only, so the tree atlases stay valid.</summary>
    public const int RockBakerVersion = 1;

    /// <summary>
    /// Why a FOLIAGE_MESH gets no impostor, or null when it can have one: TERRAIN-mode meshes are textured by the biome under each instance
    /// (one atlas per mesh cannot be right) and EMISSIVE ones glow (not baked).
    /// </summary>
    public static string? Ineligible(FoliageMesh mesh) => mesh.MaterialType switch
    {
        2 => "TERRAIN mode (textured by the biome at each instance)",
        6 => "EMISSIVE mode (not baked)",
        _ => null,
    };

    /// <summary>The source for a FOLIAGE_MESH (null when the mesh file is missing), with the material rules of <see cref="FoliageRenderer"/>.</summary>
    public static ImpostorSource? From(FoliageMesh mesh, AssetLocator assets) => From(mesh, assets, -1, null);

    /// <summary>
    /// The source of a TERRAIN-mode rock for the biome of terrain row <paramref name="rockBiome"/> (<paramref name="rockMaterial"/>: its identity): the mesh and its
    /// scales, no textures of its own (the material is the biome's). <see cref="Ineligible"/> still says no for the tools, which have no terrain to bake with.
    /// </summary>
    public static ImpostorSource? ForRock(FoliageMesh mesh, AssetLocator assets, int rockBiome, string rockMaterial) => From(mesh, assets, rockBiome, rockMaterial);

    static ImpostorSource? From(FoliageMesh mesh, AssetLocator assets, int rockBiome, string? rockMaterial)
    {
        string? Find(string? name) => name is null ? null : assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        string? Texture(string? name) => name is null ? null : assets.Find(Path.GetFileName(name.Replace('\\', '/'))) ?? assets.Find(name);
        var meshPath = Find(mesh.MeshPath);
        if (meshPath is null) return null;
        int mode = mesh.MaterialType;
        bool dual = mode is 3 or 5;
        var main = new ImpostorMaterial(mesh.Texture, mesh.Normal, dual ? mesh.Texture2 : null, dual ? mesh.Normal2 : null,
            mode == 4 ? mesh.AlphaThreshold / 255f : 0, mode == 4, mode is 1 or 5, new Vector2(mesh.TileX, mesh.TileY), mesh.SpecularMult);
        ImpostorMaterial? leaves = mesh.LeavesMesh is null ? null
            : new ImpostorMaterial(mesh.LeavesTexture, mesh.LeavesNormal, null, null, mesh.LeavesAlphaThreshold / 255f, true, false, Vector2.One, 0);
        if (rockBiome >= 0)
        {
            // A TERRAIN-mode rock: the biome's material, none of the record's own.
            main = new ImpostorMaterial(null, null, null, null, 0, false, false, Vector2.One, mesh.SpecularMult);
            leaves = null;
        }
        var textures = new[] { main.Diffuse, main.Normal, main.Diffuse2, main.Normal2, leaves?.Diffuse, leaves?.Normal }.Select(Texture).ToList();
        return new ImpostorSource
        {
            RockBiome = rockBiome,
            RockMaterial = rockMaterial,
            Name = mesh.Name,
            MeshPath = meshPath,
            LeavesPath = Find(mesh.LeavesMesh),
            Main = main,
            Leaves = leaves,
            MaxScale = mesh.MaxScale,
            MeanScale = (Math.Abs(mesh.MinScale) + Math.Abs(mesh.MaxScale)) / 2,
            TexturePaths = textures,
        };
    }

    /// <summary>
    /// The cache key: SHA-256 (hex) of the baker and format versions, every source file's path, length and last write time, the materials
    /// and the largest scale. A changed file, a mod overriding one, a new baker or a different record gives a different key.
    /// </summary>
    public string Key
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"baker {ImpostorAtlas.BakerVersion} format {ImpostorAtlas.FormatVersion}\n");
            void File(string? path)
            {
                if (path is null) { sb.Append("-\n"); return; }
                var info = new FileInfo(path);
                sb.Append(CultureInfo.InvariantCulture, $"{Path.GetFullPath(path).ToLowerInvariant()}|{(info.Exists ? info.Length : -1)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}\n");
            }
            File(MeshPath);
            File(LeavesPath);
            foreach (var t in TexturePaths) File(t);
            sb.Append(Main.Describe()).Append('\n').Append(Leaves?.Describe() ?? "-").Append('\n');
            sb.Append(CultureInfo.InvariantCulture, $"{MaxScale:R}|{MeanScale:R}|grid {ImpostorClass.DefaultGrid} magnify {ImpostorClass.Magnification:R} bias {ImpostorClass.BiasScale:R}");
            // Appended only for rocks: the tree atlases' keys (and so their cache files) stay what they were.
            if (IsRock) sb.Append(CultureInfo.InvariantCulture, $"\nrock {RockBakerVersion} {RockMaterial}\nrock frame {ImpostorClass.RockMaxFrame}");
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
    }
}

