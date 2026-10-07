using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Meitou.Data.Physics;

/// <summary>A rigid transform <c>R · p + T</c> with the 3 × 3 rotation stored by rows, as the NxuStream files write it (docs/formats/collision.md, "File format").</summary>
public readonly record struct Pose(Vector3 Row0, Vector3 Row1, Vector3 Row2, Vector3 Translation)
{
    public static readonly Pose Identity = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);

    public Vector3 Apply(Vector3 p) =>
        new(Vector3.Dot(Row0, p) + Translation.X, Vector3.Dot(Row1, p) + Translation.Y, Vector3.Dot(Row2, p) + Translation.Z);

    /// <summary>Rotation only (a direction).</summary>
    public Vector3 Rotate(Vector3 d) => new(Vector3.Dot(Row0, d), Vector3.Dot(Row1, d), Vector3.Dot(Row2, d));

    /// <summary>The pose that applies <paramref name="inner"/> first, then this one: <c>this(inner(p))</c>.</summary>
    public Pose Compose(Pose inner)
    {
        // Columns of inner's rotation, each rotated by this.
        var c0 = Rotate(new Vector3(inner.Row0.X, inner.Row1.X, inner.Row2.X));
        var c1 = Rotate(new Vector3(inner.Row0.Y, inner.Row1.Y, inner.Row2.Y));
        var c2 = Rotate(new Vector3(inner.Row0.Z, inner.Row1.Z, inner.Row2.Z));
        return new Pose(new Vector3(c0.X, c1.X, c2.X), new Vector3(c0.Y, c1.Y, c2.Y), new Vector3(c0.Z, c1.Z, c2.Z), Apply(inner.Translation));
    }

    /// <summary>Reads the 12 numbers of a <c>globalPose</c> / <c>localPose</c> element: three rotation rows, then the translation.</summary>
    public static Pose Parse(string text)
    {
        Span<float> v = stackalloc float[12];
        int n = 0;
        foreach (var part in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (n == 12) throw new InvalidDataException($"Pose has more than 12 numbers: '{text}'");
            v[n++] = float.Parse(part, CultureInfo.InvariantCulture);
        }
        if (n != 12) throw new InvalidDataException($"Pose needs 12 numbers, got {n}: '{text}'");
        return new Pose(new(v[0], v[1], v[2]), new(v[3], v[4], v[5]), new(v[6], v[7], v[8]), new(v[9], v[10], v[11]));
    }
}

/// <summary>PhysX 2.8 shape types as the game's triangulation knows them (docs/formats/collision.md).</summary>
public enum CollisionShapeKind { Plane, Sphere, Box, Capsule, Convex, TriangleMesh }

/// <summary>A decoded cooked mesh: convex hull vertices (no indices) or triangle mesh vertices and indices.</summary>
public sealed class CookedMesh(Vector3[] vertices, int[] indices)
{
    public Vector3[] Vertices { get; } = vertices;
    /// <summary>Three indices per triangle; empty for a convex mesh (its hull is built from the vertices).</summary>
    public int[] Indices { get; } = indices;
}

/// <summary>One shape of an NxuStream file. <see cref="Pose"/> is <c>globalPose × localPose</c>, in the exporter's space (Z up).</summary>
public sealed class CollisionShape
{
    public required CollisionShapeKind Kind { get; init; }
    public required Pose Pose { get; init; }
    public Vector3 HalfExtents { get; init; }
    public float Radius { get; init; }
    /// <summary>Capsule: distance between the two sphere centres, along the local Y axis.</summary>
    public float Height { get; init; }
    public CookedMesh? Mesh { get; init; }
    public string Name { get; init; } = "";
    /// <summary>The file's own PhysX group of the shape (the game assigns its own per placed object).</summary>
    public int Group { get; init; }
    public bool ActorHasBody { get; init; }
}

/// <summary>
/// A PhysX 2.8 NxuStream2 XML collision file (<c>xml collision</c> of BUILDING_PART, <c>collision</c> of FOLIAGE_MESH): the shapes of
/// all its actors with poses, and the cooked convex / triangle meshes decoded (docs/formats/collision.md).
/// </summary>
public sealed partial class CollisionFile
{
    public List<CollisionShape> Shapes { get; } = [];
    public int ConvexMeshCount { get; private set; }
    public int TriangleMeshCount { get; private set; }

    public static CollisionFile ReadFile(string path)
    {
        using var s = File.OpenRead(path);
        return Read(s);
    }

    public static CollisionFile Read(Stream stream)
    {
        // A few files carry a bare "&" in a file name attribute; escape such ampersands so the XML parses.
        var text = BareAmpersand().Replace(new StreamReader(stream).ReadToEnd(), "&amp;");
        var doc = XDocument.Parse(text);
        var root = doc.Root ?? throw new InvalidDataException("Empty XML");
        if (root.Name.LocalName != "NXUSTREAM2") throw new InvalidDataException($"Not an NxuStream2 file: <{root.Name.LocalName}>");
        var collection = root.Element("NxuPhysicsCollection") ?? throw new InvalidDataException("No NxuPhysicsCollection");
        var file = new CollisionFile();

        var convex = new Dictionary<string, CookedMesh>(StringComparer.Ordinal);
        var triangle = new Dictionary<string, CookedMesh>(StringComparer.Ordinal);
        foreach (var e in collection.Elements("NxConvexMeshDesc"))
            convex[(string?)e.Attribute("id") ?? ""] = CookedMeshReader.ReadConvex(Cooked(e));
        foreach (var e in collection.Elements("NxTriangleMeshDesc"))
            triangle[(string?)e.Attribute("id") ?? ""] = CookedMeshReader.ReadTriangleMesh(Cooked(e));
        file.ConvexMeshCount = convex.Count;
        file.TriangleMeshCount = triangle.Count;

        foreach (var scene in collection.Elements("NxSceneDesc"))
            foreach (var actor in scene.Elements("NxActorDesc"))
            {
                var global = Pose.Parse(actor.Element("globalPose")?.Value ?? "1 0 0 0 1 0 0 0 1 0 0 0");
                bool body = string.Equals((string?)actor.Attribute("hasBody"), "true", StringComparison.OrdinalIgnoreCase);
                foreach (var el in actor.Elements())
                {
                    var kind = el.Name.LocalName switch
                    {
                        "NxPlaneShapeDesc" => CollisionShapeKind.Plane,
                        "NxSphereShapeDesc" => CollisionShapeKind.Sphere,
                        "NxBoxShapeDesc" => CollisionShapeKind.Box,
                        "NxCapsuleShapeDesc" => CollisionShapeKind.Capsule,
                        "NxConvexShapeDesc" => CollisionShapeKind.Convex,
                        "NxTriangleMeshShapeDesc" => CollisionShapeKind.TriangleMesh,
                        _ => (CollisionShapeKind?)null,
                    };
                    if (kind is null) continue;
                    // A few hand-made files (tents) carry bare shapes with no NxShapeDesc: identity local pose, group 0.
                    var desc = el.Element("NxShapeDesc") ?? new XElement("NxShapeDesc");
                    var local = Pose.Parse(desc.Element("localPose")?.Value ?? "1 0 0 0 1 0 0 0 1 0 0 0");
                    CookedMesh? mesh = null;
                    if (kind is CollisionShapeKind.Convex or CollisionShapeKind.TriangleMesh)
                    {
                        string id = (string?)el.Attribute("meshData") ?? "";
                        var table = kind == CollisionShapeKind.Convex ? convex : triangle;
                        mesh = table.GetValueOrDefault(id) ?? throw new InvalidDataException($"Shape refers to unknown mesh '{id}'");
                    }
                    file.Shapes.Add(new CollisionShape
                    {
                        Kind = kind.Value,
                        Pose = global.Compose(local),
                        HalfExtents = kind == CollisionShapeKind.Box ? ParseVector((string?)el.Attribute("dimensions")) : default,
                        Radius = Float(el.Attribute("radius")),
                        Height = Float(el.Attribute("height")),
                        Mesh = mesh,
                        Name = (string?)desc.Attribute("name") ?? "",
                        Group = int.TryParse(desc.Element("group")?.Value, CultureInfo.InvariantCulture, out var g) ? g : 0,
                        ActorHasBody = body,
                    });
                }
            }
        return file;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"&(?!(amp|lt|gt|quot|apos|#[0-9]+|#x[0-9a-fA-F]+);)")]
    private static partial System.Text.RegularExpressions.Regex BareAmpersand();

    static float Float(XAttribute? a) => a is null ? 0 : float.Parse(a.Value, CultureInfo.InvariantCulture);

    static Vector3 ParseVector(string? text)
    {
        var parts = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new InvalidDataException($"Vector needs 3 numbers: '{text}'");
        return new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    /// <summary>The <c>cookedData</c> hex blob of a mesh description (whitespace between the lines ignored), checked against <c>cookedDataSize</c>.</summary>
    static byte[] Cooked(XElement mesh)
    {
        var hex = string.Concat((mesh.Element("cookedData")?.Value ?? "").Where(c => !char.IsWhiteSpace(c)));
        var bytes = Convert.FromHexString(hex);
        if (int.TryParse(mesh.Element("cookedDataSize")?.Value, CultureInfo.InvariantCulture, out var size) && size != bytes.Length)
            throw new InvalidDataException($"cookedDataSize {size} but {bytes.Length} bytes");
        return bytes;
    }
}
