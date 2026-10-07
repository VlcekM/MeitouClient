using System.Numerics;

namespace Meitou.Data.Physics;

/// <summary>
/// Turns a collision shape into triangles the way the original's navmesh generator does (docs/formats/collision.md, "Shape to triangles"):
/// a box is its 8 corners and 12 triangles, a capsule a 16-sided prism around its local Y axis (radius r, half length h/2 + r, no caps
/// rounding), a convex mesh its hull, a triangle mesh its triangles; planes and spheres give nothing. The result is in the exporter's
/// space (Z up) with the shape's pose applied; <see cref="ToWorldAxes"/> maps it to Kenshi's Y-up space.
/// </summary>
public static class CollisionTriangulator
{
    /// <summary>Sides of the capsule prism.</summary>
    public const int CapsuleSides = 16;

    /// <summary>Exporter space to Ogre/Kenshi space: (x, y, z) → (x, z, −y), a −90° rotation about X.</summary>
    public static Vector3 ToWorldAxes(Vector3 p) => new(p.X, p.Z, -p.Y);

    /// <summary>Appends the shape's triangles (outward-facing under <c>cross(v1 - v0, v2 - v0)</c>) to the lists. Returns the number of triangles added.</summary>
    public static int Triangulate(CollisionShape shape, List<Vector3> vertices, List<int> indices)
    {
        int before = indices.Count;
        int vBase = vertices.Count;
        var pose = shape.Pose;
        // A reflecting pose turns the faces inside out.
        bool flip = Vector3.Dot(Vector3.Cross(pose.Row0, pose.Row1), pose.Row2) < 0;

        switch (shape.Kind)
        {
            case CollisionShapeKind.Box:
                AddBox(shape.HalfExtents, pose, flip, vertices, indices);
                break;
            case CollisionShapeKind.Capsule:
                AddCapsule(shape.Radius, shape.Height, pose, flip, vertices, indices);
                break;
            case CollisionShapeKind.Convex when shape.Mesh is { } hull:
                {
                    var tris = ConvexHull.Triangles(hull.Vertices);
                    if (tris.Length == 0) break;
                    foreach (var v in hull.Vertices) vertices.Add(pose.Apply(v));
                    for (int i = 0; i < tris.Length; i += 3)
                        Add(indices, vBase + tris[i], vBase + tris[i + 1], vBase + tris[i + 2], flip);
                    break;
                }
            case CollisionShapeKind.TriangleMesh when shape.Mesh is { } mesh:
                {
                    foreach (var v in mesh.Vertices) vertices.Add(pose.Apply(v));
                    for (int i = 0; i < mesh.Indices.Length; i += 3)
                        Add(indices, vBase + mesh.Indices[i], vBase + mesh.Indices[i + 1], vBase + mesh.Indices[i + 2], flip);
                    break;
                }
        }
        return (indices.Count - before) / 3;
    }

    static void Add(List<int> indices, int a, int b, int c, bool flip)
    {
        indices.Add(a);
        if (flip) { indices.Add(c); indices.Add(b); }
        else { indices.Add(b); indices.Add(c); }
    }

    static void AddBox(Vector3 h, Pose pose, bool flip, List<Vector3> vertices, List<int> indices)
    {
        int b = vertices.Count;
        for (int i = 0; i < 8; i++)
            vertices.Add(pose.Apply(new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z)));
        // Corner index bits: 1 = +x, 2 = +y, 4 = +z. Quads wound counter-clockwise seen from outside (cross(v1 - v0, v3 - v0) outward).
        ReadOnlySpan<int> quads =
        [
            0, 4, 6, 2, // -x
            1, 3, 7, 5, // +x
            0, 1, 5, 4, // -y
            2, 6, 7, 3, // +y
            0, 2, 3, 1, // -z
            4, 5, 7, 6, // +z
        ];
        for (int q = 0; q < quads.Length; q += 4)
        {
            Add(indices, b + quads[q], b + quads[q + 1], b + quads[q + 2], flip);
            Add(indices, b + quads[q], b + quads[q + 2], b + quads[q + 3], flip);
        }
    }

    static void AddCapsule(float r, float height, Pose pose, bool flip, List<Vector3> vertices, List<int> indices)
    {
        int b = vertices.Count;
        float half = height * 0.5f + r;
        for (int ring = 0; ring < 2; ring++)
            for (int i = 0; i < CapsuleSides; i++)
            {
                float a = MathF.Tau * i / CapsuleSides;
                // Axis is local Y; rings at -half and +half.
                vertices.Add(pose.Apply(new Vector3(MathF.Cos(a) * r, ring == 0 ? -half : half, MathF.Sin(a) * r)));
            }
        for (int i = 0; i < CapsuleSides; i++)
        {
            int j = (i + 1) % CapsuleSides;
            // Angle runs from +x towards +z; this winding points the normals outward.
            Add(indices, b + i, b + CapsuleSides + j, b + j, flip);
            Add(indices, b + i, b + CapsuleSides + i, b + CapsuleSides + j, flip);
        }
        // Caps: fans around ring vertex 0.
        for (int i = 1; i < CapsuleSides - 1; i++)
        {
            Add(indices, b, b + i, b + i + 1, flip);                                            // bottom, facing -y
            Add(indices, b + CapsuleSides, b + CapsuleSides + i + 1, b + CapsuleSides + i, flip); // top, facing +y
        }
    }
}
