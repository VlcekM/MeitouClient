using System.Numerics;
using System.Text;
using Meitou.Content;

namespace Meitou.Data.World;

/// <summary>The shape of a placed fog volume (docs/formats/fogfeatures.md): the type byte of <c>fogfeatures.dat</c>.</summary>
public enum FogFeatureType : byte
{
    Sphere = 2,
    Beam = 3,
    /// <summary>"Block" in the level editor (class <c>FogPlaneVolume</c>): the space inside seven planes.</summary>
    Block = 4,
}

/// <summary>
/// One placed fog volume from <c>data/newland/land/fogfeatures.dat</c> (docs/formats/fogfeatures.md). <see cref="Colour"/> is the stored RGB
/// (the stored alpha is read but the game keeps 1). <see cref="Distance"/> is the density distance (the shader's density is its inverse),
/// <see cref="Edge"/> the edge softening distance of blocks and beams. A block's <see cref="Planes"/> hold (normal, w) with the inside where
/// <c>dot(normal, p) &lt; w</c> for all seven; a sphere uses <see cref="Position"/> and <see cref="Radius"/>; a beam runs from
/// <see cref="Position"/> to <see cref="End"/> with <see cref="Radius"/>.
/// </summary>
public sealed record FogFeature(string Name, FogFeatureType Type, Vector3 Colour, float Alpha, float Distance, float Edge,
    Vector3 Position, Vector3 End, float Radius, Vector4[] Planes)
{
    /// <summary>The shader's <c>density</c>: 1 / distance, 1 when the distance is not positive (the game's setter).</summary>
    public float Density => Distance > 0 ? 1f / Distance : 1f;
    /// <summary>The shader's <c>edgeBlur</c>: 1 / edge.</summary>
    public float EdgeBlur => 1f / Edge;

    /// <summary>Whether a point is inside a block's seven planes.</summary>
    public bool Contains(Vector3 p)
    {
        if (Type != FogFeatureType.Block) return false;
        foreach (var plane in Planes)
            if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z >= plane.W) return false;
        return true;
    }

    /// <summary>
    /// A block's corners: the points where three of its planes meet that lie on or inside the other four (within 0.5 units). The game builds the
    /// volume's mesh from these (FUN_14010b6a0). The planes in the base game face mostly upwards, so a block is open below and its corners include
    /// points far underground.
    /// </summary>
    public IReadOnlyList<Vector3> Corners()
    {
        var corners = new List<Vector3>();
        if (Type != FogFeatureType.Block) return corners;
        for (int a = 0; a < Planes.Length; a++)
            for (int b = a + 1; b < Planes.Length; b++)
                for (int c = b + 1; c < Planes.Length; c++)
                {
                    if (Intersect(Planes[a], Planes[b], Planes[c]) is not { } p) continue;
                    bool inside = true;
                    foreach (var plane in Planes)
                        if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z > plane.W + 0.5f) { inside = false; break; }
                    if (inside) corners.Add(p);
                }
        return corners;
    }

    /// <summary>
    /// The x, z extent of a block's cross-section at <paramref name="height"/> (the polygon where all seven planes hold), or null when it is
    /// empty there. The blocks widen downwards, so the section at the lowest visible height bounds everything of the block that can be seen.
    /// </summary>
    public (Vector2 Min, Vector2 Max)? SectionBounds(float height)
    {
        if (Type != FogFeatureType.Block) return null;
        // In x, z each plane is a half-plane a . xz < c.
        var lines = new List<(Vector2 A, float C)>();
        foreach (var p in Planes)
        {
            var a = new Vector2(p.X, p.Z);
            float c = p.W - p.Y * height;
            if (a.LengthSquared() < 1e-12f) { if (c <= 0) return null; continue; }
            lines.Add((a, c));
        }
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        bool any = false;
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
            {
                var (a, c) = lines[i]; var (b, d) = lines[j];
                float det = a.X * b.Y - a.Y * b.X;
                if (MathF.Abs(det) < 1e-9f) continue;
                var q = new Vector2((c * b.Y - a.Y * d) / det, (a.X * d - c * b.X) / det);
                bool inside = true;
                foreach (var (e, f) in lines)
                    if (Vector2.Dot(e, q) > f + 0.5f) { inside = false; break; }
                if (!inside) continue;
                min = Vector2.Min(min, q); max = Vector2.Max(max, q);
                any = true;
            }
        return any ? (min, max) : null;
    }

    static Vector3? Intersect(Vector4 a, Vector4 b, Vector4 c)
    {
        var na = new Vector3(a.X, a.Y, a.Z); var nb = new Vector3(b.X, b.Y, b.Z); var nc = new Vector3(c.X, c.Y, c.Z);
        var bc = Vector3.Cross(nb, nc);
        float det = Vector3.Dot(na, bc);
        if (MathF.Abs(det) < 1e-6f) return null;
        return (a.W * bc + b.W * Vector3.Cross(nc, na) + c.W * Vector3.Cross(na, nb)) / det;
    }
}

/// <summary>
/// The placed fog volumes of the world (docs/formats/fogfeatures.md): <c>data/newland/land/fogfeatures.dat</c>, which the game loads from its
/// "Landscape" resource group (FUN_14010f060) and its level editor writes ("Terrain Fog", FUN_140777ce0).
/// </summary>
public static class FogFeatures
{
    public const string RelativePath = "newland/land/fogfeatures.dat";

    /// <summary>The install's file, or an empty list when it has none.</summary>
    public static IReadOnlyList<FogFeature> Load(GameInstall install)
    {
        string path = Path.Combine(install.DataDirectory, RelativePath);
        return File.Exists(path) ? Read(File.ReadAllBytes(path)) : [];
    }

    /// <summary>
    /// Parses the file: magic <c>FF01</c> or <c>FF02</c>, an int32 count, then per volume (FF02 only) a byte-length name, a type byte, RGBA
    /// colour floats, the density distance, and the type's own fields. Little-endian throughout. Throws on an unknown type (the game would
    /// lose its place in the file too).
    /// </summary>
    public static IReadOnlyList<FogFeature> Read(ReadOnlySpan<byte> data)
    {
        int at = 0;
        int version = Encoding.ASCII.GetString(Take(data, ref at, 4)) switch
        {
            "FF01" => 1,
            "FF02" => 2,
            var m => throw new FormatException($"fogfeatures: magic '{m}' is neither FF01 nor FF02"),
        };
        int count = BitConverter.ToInt32(Take(data, ref at, 4));
        var list = new List<FogFeature>(Math.Max(count, 0));
        for (int i = 0; i < count; i++)
        {
            string name = "";
            if (version >= 2)
            {
                int length = Take(data, ref at, 1)[0];
                name = Encoding.Latin1.GetString(Take(data, ref at, length));
            }
            var type = (FogFeatureType)Take(data, ref at, 1)[0];
            var colour = new Vector3(F(data, ref at), F(data, ref at), F(data, ref at));
            float alpha = F(data, ref at);
            float distance = F(data, ref at);
            switch (type)
            {
                case FogFeatureType.Sphere:
                {
                    var p = V3(data, ref at);
                    float radius = F(data, ref at);
                    list.Add(new FogFeature(name, type, colour, alpha, distance, 0, p, p, radius, []));
                    break;
                }
                case FogFeatureType.Beam:
                {
                    var p = V3(data, ref at);
                    var end = V3(data, ref at);
                    float radius = F(data, ref at), edge = F(data, ref at);
                    list.Add(new FogFeature(name, type, colour, alpha, distance, edge, p, end, radius, []));
                    break;
                }
                case FogFeatureType.Block:
                {
                    float edge = F(data, ref at);
                    var planes = new Vector4[7];
                    // Stored as (normal, d) and made Ogre::Plane(normal, -d), whose constant is d; the shader's w is minus the constant
                    // (FUN_14010b6a0), so inside is dot(normal, p) < -d.
                    for (int k = 0; k < 7; k++)
                    {
                        var n = V3(data, ref at);
                        planes[k] = new Vector4(n, -F(data, ref at));
                    }
                    list.Add(new FogFeature(name, type, colour, alpha, distance, edge, Vector3.Zero, Vector3.Zero, 0, planes));
                    break;
                }
                default:
                    throw new FormatException($"fogfeatures: volume {i} ('{name}') has unknown type {(byte)type}");
            }
        }
        return list;
    }

    static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int at, int n)
    {
        if (at + n > data.Length) throw new FormatException("fogfeatures: the file ends early");
        var s = data.Slice(at, n);
        at += n;
        return s;
    }

    static float F(ReadOnlySpan<byte> data, ref int at) => BitConverter.ToSingle(Take(data, ref at, 4));
    static Vector3 V3(ReadOnlySpan<byte> data, ref int at) => new(F(data, ref at), F(data, ref at), F(data, ref at));
}
