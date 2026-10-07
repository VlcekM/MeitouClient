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
    public static ImpostorSource? From(FoliageMesh mesh, AssetLocator assets)
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
        var textures = new[] { main.Diffuse, main.Normal, main.Diffuse2, main.Normal2, leaves?.Diffuse, leaves?.Normal }.Select(Texture).ToList();
        return new ImpostorSource
        {
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
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
    }
}

/// <summary>
/// The disk cache of baked atlases: <c>%LOCALAPPDATA%\Meitou\impostors\&lt;name&gt;_&lt;key&gt;.mimp</c> (overridden by <c>MEITOU_IMPOSTOR_CACHE</c> or the
/// constructor; never inside the repository). Files are written to a temporary name and moved into place, so a crash leaves no half file.
/// </summary>
public sealed class ImpostorCache(string? root = null)
{
    public string Root { get; } = root ?? Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_CACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "impostors");

    public string PathFor(ImpostorSource source)
    {
        var name = new string(source.Name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (name.Length > 40) name = name[..40];
        return Path.Combine(Root, $"{name}_{source.Key[..24]}.mimp");
    }

    /// <summary>The cached atlas, or null when there is none or it is unreadable (it is then baked and written again).</summary>
    public ImpostorAtlas? TryLoad(ImpostorSource source)
    {
        var path = PathFor(source);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return ImpostorAtlas.Read(stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Save(ImpostorSource source, ImpostorAtlas atlas)
    {
        var path = PathFor(source);
        Directory.CreateDirectory(Root);
        var temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            atlas.Write(stream);
        File.Move(temporary, path, overwrite: true);
    }
}
