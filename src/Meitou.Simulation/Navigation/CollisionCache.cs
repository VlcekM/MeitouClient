using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.Physics;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// One shape of a collision file, triangulated once in Kenshi's Y-up axes (the exporter-to-Ogre map applied, the shape's pose applied).
/// </summary>
internal sealed class PreparedShape(CollisionShapeKind kind, Vector3[] vertices, int[] indices, bool isConvex)
{
    /// <summary>
    /// The shape type the triangles come from (box, capsule, convex hull, triangle mesh).
    /// </summary>
    public CollisionShapeKind Kind { get; } = kind;

    /// <summary>
    /// The vertices in Kenshi's Y-up world axes, the shape's pose applied.
    /// </summary>
    public Vector3[] Vertices { get; } = vertices;

    /// <summary>
    /// Three indices per triangle, outward-facing.
    /// </summary>
    public int[] Indices { get; } = indices;

    /// <summary>
    /// True for boxes, capsule prisms and convex hulls (a closed convex solid: the navmesh can cut a prism around it).
    /// </summary>
    public bool IsConvex { get; } = isConvex;
}

/// <summary>
/// A collision file ready for placement: the triangles of every shape that gives any, and its first box's frame (what a door leaf slides along).
/// </summary>
internal sealed class PreparedCollision(PreparedShape[] shapes, Meitou.Data.World.DoorFrame door)
{
    /// <summary>
    /// The prepared shapes that gave triangles, in file order.
    /// </summary>
    public PreparedShape[] Shapes { get; } = shapes;

    /// <summary>
    /// The frame of the file's first box: what a door leaf slides along.
    /// </summary>
    public Meitou.Data.World.DoorFrame Door { get; } = door;
}

/// <summary>
/// Loads and triangulates collision files by record path, once each; safe from several threads.
/// </summary>
internal sealed class CollisionCache(GameInstall install)
{
    /// <summary>
    /// The prepared files by record path (case-insensitive); null for a path with no readable file.
    /// </summary>
    readonly ConcurrentDictionary<string, PreparedCollision?> _files = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Lookups that found no file or an unreadable one; read through <see cref="Missing"/>.
    /// </summary>
    int _missing;

    /// <summary>
    /// Number of lookups that found no file (or an unreadable one).
    /// </summary>
    public int Missing => _missing;

    /// <summary>
    /// The prepared file, or null when the record's path is empty, the file is not in the install, or it cannot be read.
    /// </summary>
    public PreparedCollision? Get(string recordPath)
    {
        if (string.IsNullOrWhiteSpace(recordPath)) return null;

        return _files.GetOrAdd(recordPath, Load);
    }

    /// <summary>
    /// Resolves <paramref name="recordPath"/> in the install, reads it and triangulates every shape (closed triangle meshes turned outward); null,
    /// counted in <see cref="Missing"/>, when it is not found or cannot be read.
    /// </summary>
    PreparedCollision? Load(string recordPath)
    {
        var path = CollisionPaths.Resolve(install, recordPath);
        if (path is null)
        {
            Interlocked.Increment(ref _missing);
            return null;
        }

        CollisionFile file;
        try
        {
            file = CollisionFile.ReadFile(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException or FormatException)
        {
            Interlocked.Increment(ref _missing);
            return null;
        }

        var shapes = new List<PreparedShape>();
        foreach (var s in file.Shapes)
        {
            var v = new List<Vector3>();
            var i = new List<int>();
            CollisionTriangulator.Triangulate(s, v, i);
            if (i.Count == 0) continue;

            var world = v.Select(CollisionTriangulator.ToWorldAxes).ToArray();
            var indices = i.ToArray();
            bool convex = s.Kind is CollisionShapeKind.Box or CollisionShapeKind.Capsule or CollisionShapeKind.Convex;
            if (s.Kind == CollisionShapeKind.TriangleMesh) FixClosedWinding(world, indices);

            shapes.Add(new PreparedShape(s.Kind, world, indices, convex));
        }

        return new PreparedCollision([.. shapes], Data.World.DoorFrame.Of(file));
    }

    /// <summary>
    /// What <see cref="FixClosedWinding"/> found.
    /// </summary>
    internal enum Winding
    {
        /// <summary>
        /// The mesh has an edge without its reverse; kept as stored.
        /// </summary>
        Open,
        /// <summary>
        /// A closed mesh with outward faces (signed volume not negative); kept.
        /// </summary>
        Outward,
        /// <summary>
        /// A closed mesh that was inside out; its triangles were flipped.
        /// </summary>
        Flipped
    }

    /// <summary>
    /// A closed triangle mesh (every edge has its reverse) with negative signed volume is inside out: flip it so that its faces point outward. An
    /// open mesh is kept as stored. Our own rule (docs/formats/collision.md, "Findings").
    /// </summary>
    internal static Winding FixClosedWinding(Vector3[] v, int[] idx)
    {
        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t < idx.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                var e = (idx[t + k], idx[t + (k + 1) % 3]);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        }

        foreach (var (a, b) in edges.Keys)
        {
            if (!edges.ContainsKey((b, a)))
                return Winding.Open; // open mesh: keep as stored
        }

        float volume = 0;
        for (int t = 0; t < idx.Length; t += 3)
            volume += Vector3.Dot(v[idx[t]], Vector3.Cross(v[idx[t + 1]], v[idx[t + 2]]));

        if (volume >= 0) return Winding.Outward;

        for (int t = 0; t < idx.Length; t += 3)
            (idx[t + 1], idx[t + 2]) = (idx[t + 2], idx[t + 1]);

        return Winding.Flipped;
    }
}
