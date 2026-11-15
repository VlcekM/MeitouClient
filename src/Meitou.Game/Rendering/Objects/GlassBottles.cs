using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The <c>glass</c> switch (Meitou, on): the game's glass bottles on the ground (Sake; Cactus Rum and Empty Rum Bottle) are drawn see-through, as
/// a mod's opacity map draws an item (docs/render-objects.md "See-through items"), with a map Meitou makes from the bottle's own mesh: the body
/// glass, the neck or cork solid. The game draws them solid.
/// </summary>
static class GlassBottles
{
    #region Bottles

    /// <summary>
    /// One bottle: its mesh and diffuse (file names) and the mesh's vertex count, which a mod's own model or texture changes; how opaque its glass
    /// is; and the share of its height from which it turns solid, reached at <see cref="SolidAt"/>.
    /// </summary>
    readonly record struct Bottle(string Mesh, string Diffuse, int Vertices, float Glass, float SolidFrom, float SolidAt);

    /// <summary>
    /// The bottles (both stand on Y): Sake's round body is glass, its neck (from 55 % of the height) and cross stopper solid; the rum bottle's
    /// body and shoulder are glass, its neck and cork (from 88 %) solid.
    /// </summary>
    static readonly Bottle[] Known =
    [
        new("item_alcohol1.mesh", "props_atlas_regular_diffuse.dds", 558, 0.45f, 0.55f, 0.63f),
        new("rumjug.mesh", "rumjug.dds", 828, 0.6f, 0.88f, 0.94f),
    ];

    /// <summary>
    /// Whether the switch is on: read as each frame's draws are made, so turning it shows at once.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// <paramref name="m"/>, the material of mesh <paramref name="meshField"/> (a record's path), with a made opacity map when the mesh is one of
    /// the bottles as the game ships it (its mesh, diffuse and vertex count) and the material has no alpha of its own; else
    /// <paramref name="m"/> itself (a mod's own opacity map wins).
    /// </summary>
    internal static SurfaceMaterial? Apply(SurfaceMaterial? m, string meshField, AssetLocator assets)
    {
        if (m is null || m.Alpha != AlphaSource.None || Match(meshField, m.Diffuse) is not { } bottle) return m;

        return MapOf(bottle, assets) is { } map ? m with { Alpha = AlphaSource.Opacity, Opacity = map, BuiltInGlass = true } : m;
    }

    /// <summary>
    /// The bottle whose mesh and diffuse these are (file names, case-insensitive); null for any other.
    /// </summary>
    static Bottle? Match(string meshField, string? diffuse)
    {
        if (diffuse is null) return null;

        string mesh = FileName(meshField);
        string texture = FileName(diffuse);
        foreach (var b in Known)
            if (b.Mesh.Equals(mesh, StringComparison.OrdinalIgnoreCase) && b.Diffuse.Equals(texture, StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }

    /// <summary>
    /// The bare file name of a record's path.
    /// </summary>
    static string FileName(string path) => Path.GetFileName(path.Replace('\\', '/'));

    #endregion

    #region Maps

    /// <summary>
    /// The side of a made map, over the mesh's whole UV square (wrapped): the opacity changes with height alone, so this is fine enough.
    /// </summary>
    const int MapSize = 512;

    /// <summary>
    /// Goes up when the maps are made differently: a file of another version is not read.
    /// </summary>
    const int MapVersion = 1;

    /// <summary>
    /// Each bottle's map file by mesh (null: it could not be made), made once a run.
    /// </summary>
    static readonly Dictionary<string, string?> Maps = [];

    /// <summary>
    /// The file of <paramref name="b"/>'s map, made on first use.
    /// </summary>
    static string? MapOf(Bottle b, AssetLocator assets)
    {
        lock (Maps)
        {
            if (!Maps.TryGetValue(b.Mesh, out var map)) Maps[b.Mesh] = map = Make(b, assets);
            return map;
        }
    }

    /// <summary>
    /// Makes <paramref name="b"/>'s map under the derived-data caches (<c>glass</c>) from its mesh; null when the mesh is missing, unreadable or
    /// not the game's (another vertex count), or the file cannot be written.
    /// </summary>
    static string? Make(Bottle b, AssetLocator assets)
    {
        if (assets.FindMesh(b.Mesh) is not { } path) return null;

        OgreMesh mesh;
        try
        {
            mesh = OgreMeshReader.ReadFile(path);
        }
        catch (Exception e) when (e is OgreFormatException or EndOfStreamException or IOException or InvalidDataException)
        {
            return null;
        }
        if (VertexCount(mesh) != b.Vertices) return null;

        string file = LoadCaches.Folder("glass", $"{Path.GetFileNameWithoutExtension(b.Mesh)}-v{MapVersion}.dds");
        if (File.Exists(file) && !LoadCaches.Disabled) return file;

        return Write(file, Draw(b, mesh)) ? file : null;
    }

    /// <summary>
    /// The mesh's vertices, over its submeshes (the shared set once).
    /// </summary>
    static int VertexCount(OgreMesh mesh)
    {
        int shared = mesh.SharedVertexData?.ReadPositions()?.Length ?? 0;
        return shared + mesh.SubMeshes.Where(s => !s.UseSharedVertices).Sum(s => s.VertexData?.ReadPositions()?.Length ?? 0);
    }

    /// <summary>
    /// The map: every texel the mesh covers takes the opacity of the height it is at (the lowest where faces overlap), the rest solid.
    /// </summary>
    static byte[] Draw(Bottle b, OgreMesh mesh)
    {
        var map = new byte[MapSize * MapSize];
        Array.Fill(map, (byte)255);

        var heights = mesh.SubMeshes.SelectMany(s => Data(mesh, s).ReadPositions() ?? []).Select(p => p.Y).ToArray();
        float bottom = heights.Min();
        float tall = Math.Max(heights.Max() - bottom, 1e-6f);
        foreach (var sub in mesh.SubMeshes) DrawSubMesh(map, b, Data(mesh, sub), sub.Indices.Indices, bottom, tall);
        return map;
    }

    /// <summary>
    /// The vertices a submesh uses.
    /// </summary>
    static OgreVertexData Data(OgreMesh mesh, OgreSubMesh sub) => sub.UseSharedVertices ? mesh.SharedVertexData! : sub.VertexData!;

    /// <summary>
    /// Draws one submesh's triangles into <paramref name="map"/> at their UVs, each corner at its height's share of <paramref name="tall"/>.
    /// </summary>
    static void DrawSubMesh(byte[] map, Bottle b, OgreVertexData data, IReadOnlyList<uint> indices, float bottom, float tall)
    {
        var positions = data.ReadPositions();
        if (positions is null || data.Find(OgreVertexSemantic.TextureCoordinates) is not { } uvElement) return;

        float[] uv = data.ReadFloats(uvElement);
        int per = uv.Length / positions.Length;
        Vector2 Uv(uint i) => new Vector2(uv[i * per], uv[i * per + 1]) * MapSize;
        float Height(uint i) => (positions[i].Y - bottom) / tall;
        for (int t = 0; t + 2 < indices.Count; t += 3)
        {
            var (i0, i1, i2) = (indices[t], indices[t + 1], indices[t + 2]);
            DrawTriangle(map, b, (Uv(i0), Uv(i1), Uv(i2)), new Vector3(Height(i0), Height(i1), Height(i2)));
        }
    }

    /// <summary>
    /// Draws one triangle (texel corners, wrapped round the map) with its corners' <paramref name="heights"/> interpolated, a little past its edges
    /// so filtering at a seam finds the same opacity.
    /// </summary>
    static void DrawTriangle(byte[] map, Bottle b, (Vector2 A, Vector2 B, Vector2 C) uv, Vector3 heights)
    {
        var (a, p, c) = uv;
        float area = Cross(p - a, c - a);
        if (MathF.Abs(area) < 1e-6f) return;

        float slack = 2.5f / MathF.Sqrt(MathF.Abs(area));
        var min = Vector2.Min(a, Vector2.Min(p, c));
        var max = Vector2.Max(a, Vector2.Max(p, c));
        for (int y = (int)MathF.Floor(min.Y) - 2; y <= (int)MathF.Ceiling(max.Y) + 2; y++)
        {
            for (int x = (int)MathF.Floor(min.X) - 2; x <= (int)MathF.Ceiling(max.X) + 2; x++)
            {
                var at = new Vector2(x + 0.5f, y + 0.5f);
                var w = new Vector3(Cross(p - at, c - at), Cross(c - at, a - at), 0) / area;
                w.Z = 1 - w.X - w.Y;
                if (w.X < -slack || w.Y < -slack || w.Z < -slack) continue;

                int texel = Wrap(y) * MapSize + Wrap(x);
                map[texel] = Math.Min(map[texel], Opacity(b, Math.Clamp(Vector3.Dot(w, heights), 0, 1)));
            }
        }
    }

    /// <summary>
    /// The 2D cross product.
    /// </summary>
    static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;

    /// <summary>
    /// A texel coordinate wrapped onto the map (UVs past 1 repeat, as the item's textures do).
    /// </summary>
    static int Wrap(int i) => (i % MapSize + MapSize) % MapSize;

    /// <summary>
    /// The opacity at <paramref name="height"/> (a share of the bottle's): the glass's, rising to solid between <see cref="Bottle.SolidFrom"/> and
    /// <see cref="Bottle.SolidAt"/>.
    /// </summary>
    static byte Opacity(Bottle b, float height)
    {
        float solid = Math.Clamp((height - b.SolidFrom) / (b.SolidAt - b.SolidFrom), 0, 1);
        return (byte)MathF.Round(255 * (b.Glass + (1 - b.Glass) * solid));
    }

    /// <summary>
    /// Writes <paramref name="opacity"/> (one byte a texel) as an uncompressed BGRA DDS, grey with the opacity in every colour channel, through a
    /// temporary file; false when it cannot be written.
    /// </summary>
    static bool Write(string file, byte[] opacity)
    {
        string temporary = $"{file}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (var w = new BinaryWriter(File.Create(temporary))) WriteDds(w, opacity);
            File.Move(temporary, file, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return File.Exists(file);
        }
    }

    /// <summary>
    /// The DDS header (32-bit RGB with alpha, no mips: the texture cache makes them) and the texels.
    /// </summary>
    static void WriteDds(BinaryWriter w, byte[] opacity)
    {
        // DDS_HEADER: size, flags (caps, height, width, pitch, pixel format), height, width, pitch, depth, mips, 11 reserved.
        uint[] header = [0x20534444u, 124, 0x100F, MapSize, MapSize, MapSize * 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        // DDS_PIXELFORMAT: size, flags (RGB, alpha), no FourCC, 32 bits, the R, G, B and A masks; then caps (texture), caps 2-4, reserved.
        uint[] format = [32, 0x41, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000, 0x1000, 0, 0, 0, 0];
        foreach (uint v in header.Concat(format)) w.Write(v);

        var texels = new byte[opacity.Length * 4];
        for (int i = 0; i < opacity.Length; i++)
        {
            texels.AsSpan(i * 4, 3).Fill(opacity[i]);
            texels[i * 4 + 3] = 255;
        }
        w.Write(texels);
    }

    #endregion
}
