using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.Physics;

namespace Meitou.Navigation;

/// <summary>One shape of a collision file, triangulated once in Kenshi's Y-up axes (the exporter-to-Ogre map applied, the shape's pose applied).</summary>
internal sealed class PreparedShape(CollisionShapeKind kind, Vector3[] vertices, int[] indices, bool isConvex)
{
    public CollisionShapeKind Kind { get; } = kind;
    public Vector3[] Vertices { get; } = vertices;
    /// <summary>Three indices per triangle, outward-facing.</summary>
    public int[] Indices { get; } = indices;
    /// <summary>True for boxes, capsule prisms and convex hulls (a closed convex solid: the navmesh can cut a prism around it).</summary>
    public bool IsConvex { get; } = isConvex;
}

/// <summary>A collision file ready for placement: the triangles of every shape that gives any.</summary>
internal sealed class PreparedCollision(PreparedShape[] shapes)
{
    public PreparedShape[] Shapes { get; } = shapes;
}

/// <summary>Loads and triangulates collision files by record path, once each; safe from several threads.</summary>
internal sealed class CollisionCache(GameInstall install)
{
    readonly ConcurrentDictionary<string, PreparedCollision?> files = new(StringComparer.OrdinalIgnoreCase);
    int missing;

    /// <summary>Number of lookups that found no file (or an unreadable one).</summary>
    public int Missing => missing;

    /// <summary>The prepared file, or null when the record's path is empty, the file is not in the install, or it cannot be read.</summary>
    public PreparedCollision? Get(string recordPath)
    {
        if (string.IsNullOrWhiteSpace(recordPath)) return null;
        return files.GetOrAdd(recordPath, Load);
    }

    PreparedCollision? Load(string recordPath)
    {
        var path = CollisionPaths.Resolve(install, recordPath);
        if (path is null) { Interlocked.Increment(ref missing); return null; }
        CollisionFile file;
        try { file = CollisionFile.ReadFile(path); }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException or FormatException)
        {
            Interlocked.Increment(ref missing);
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
        return new PreparedCollision([.. shapes]);
    }

    /// <summary>What <see cref="FixClosedWinding"/> found.</summary>
    internal enum Winding { Open, Outward, Flipped }

    /// <summary>
    /// A closed triangle mesh (every edge has its reverse) with negative signed volume is inside out: flip it so that its faces point outward. An open mesh
    /// is kept as stored. Our own rule (docs/formats/collision.md, "Findings").
    /// </summary>
    internal static Winding FixClosedWinding(Vector3[] v, int[] idx)
    {
        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t < idx.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var e = (idx[t + k], idx[t + (k + 1) % 3]);
                edges[e] = edges.GetValueOrDefault(e) + 1;
            }
        foreach (var (a, b) in edges.Keys)
            if (!edges.ContainsKey((b, a))) return Winding.Open; // open mesh: keep as stored
        float volume = 0;
        for (int t = 0; t < idx.Length; t += 3)
            volume += Vector3.Dot(v[idx[t]], Vector3.Cross(v[idx[t + 1]], v[idx[t + 2]]));
        if (volume >= 0) return Winding.Outward;
        for (int t = 0; t < idx.Length; t += 3) (idx[t + 1], idx[t + 2]) = (idx[t + 2], idx[t + 1]);
        return Winding.Flipped;
    }
}
