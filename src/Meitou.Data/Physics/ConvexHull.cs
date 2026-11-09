using System.Numerics;

namespace Meitou.Data.Physics;

/// <summary>
/// Incremental 3D convex hull for the small point sets of cooked convex meshes (tens of vertices). Triangles come out with
/// outward normals under <c>cross(v1 - v0, v2 - v0)</c>.
/// </summary>
public static class ConvexHull
{
    /// <summary>
    /// A hull triangle and its outward plane.
    /// </summary>
    sealed class Face
    {
        /// <summary>
        /// The corners, as indices into the points.
        /// </summary>
        public int A, B, C;

        /// <summary>
        /// The outward unit normal.
        /// </summary>
        public Vector3 Normal;

        /// <summary>
        /// The plane's offset: <c>dot(Normal, x) = D</c> on the face.
        /// </summary>
        public float D;

        /// <summary>
        /// Seen by the point being added: removed once the horizon is found.
        /// </summary>
        public bool Dead;
    }

    /// <summary>
    /// The hull's triangles as indices into <paramref name="points"/>; empty when the points are (nearly) flat, collinear or fewer than four.
    /// </summary>
    public static int[] Triangles(Vector3[] points, float epsilon = 1e-3f)
    {
        int n = points.Length;
        if (n < 4) return [];
        if (!InitialTetrahedron(points, epsilon, out int i0, out int i1, out int i2, out int i3)) return [];

        var faces = new List<Face>();
        var centre = (points[i0] + points[i1] + points[i2] + points[i3]) * 0.25f;
        AddFace(points, faces, i0, i1, i2, centre);
        AddFace(points, faces, i0, i1, i3, centre);
        AddFace(points, faces, i0, i2, i3, centre);
        AddFace(points, faces, i1, i2, i3, centre);

        var used = new HashSet<int> { i0, i1, i2, i3 };
        var horizon = new Dictionary<(int, int), int>();
        for (int p = 0; p < n; p++)
        {
            if (used.Contains(p)) continue;
            if (!AddPoint(points, p, faces, horizon, centre, epsilon)) continue;
            used.Add(p);
        }
        return Indices(faces);
    }

    /// <summary>
    /// The initial tetrahedron: the two farthest points on an axis extreme, then the farthest from their line, then from their plane. False when
    /// the points are (nearly) collinear or flat.
    /// </summary>
    static bool InitialTetrahedron(Vector3[] points, float epsilon, out int i0, out int i1, out int i2, out int i3)
    {
        int n = points.Length;
        i0 = 0;
        i1 = 0;
        i3 = -1;
        for (int i = 1; i < n; i++)
        {
            if (points[i].X < points[i0].X) i0 = i;
            if (points[i].X > points[i1].X) i1 = i;
        }
        if (i0 == i1) i1 = (i0 + 1) % n;

        float best = -1;
        i2 = -1;
        for (int i = 0; i < n; i++)
        {
            float d = Vector3.Cross(points[i1] - points[i0], points[i] - points[i0]).LengthSquared();
            if (d > best)
            {
                best = d;
                i2 = i;
            }
        }
        if (best < epsilon * epsilon) return false;

        var baseNormal = Vector3.Normalize(Vector3.Cross(points[i1] - points[i0], points[i2] - points[i0]));
        best = -1;
        for (int i = 0; i < n; i++)
        {
            float d = MathF.Abs(Vector3.Dot(points[i] - points[i0], baseNormal));
            if (d > best)
            {
                best = d;
                i3 = i;
            }
        }
        if (best < epsilon) return false;

        return true;
    }

    /// <summary>
    /// Adds the face (a, b, c), wound so that its normal points away from <paramref name="inside"/>; none when it is degenerate.
    /// </summary>
    static void AddFace(Vector3[] points, List<Face> faces, int a, int b, int c, Vector3 inside)
    {
        var normal = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
        float len = normal.Length();
        if (len < 1e-9f) return;

        normal /= len;
        float d = Vector3.Dot(normal, points[a]);
        if (Vector3.Dot(normal, inside) - d > 0)
        {
            (b, c) = (c, b);
            normal = -normal;
            d = -d;
        }
        faces.Add(new Face { A = a, B = b, C = c, Normal = normal, D = d });
    }

    /// <summary>
    /// Grows the hull by point <paramref name="p"/>: the faces it sees die and their horizon is fanned to it. False when it sees none (inside).
    /// </summary>
    static bool AddPoint(Vector3[] points, int p, List<Face> faces, Dictionary<(int, int), int> horizon, Vector3 centre, float epsilon)
    {
        var pt = points[p];
        bool any = false;
        foreach (var f in faces)
        {
            if (!f.Dead && Vector3.Dot(f.Normal, pt) - f.D > epsilon)
            {
                f.Dead = true;
                any = true;
            }
        }
        if (!any) return false;

        // Horizon: directed edges of dead faces whose reverse is not an edge of another dead face.
        horizon.Clear();
        foreach (var f in faces)
        {
            if (!f.Dead) continue;
            Edge(horizon, f.A, f.B);
            Edge(horizon, f.B, f.C);
            Edge(horizon, f.C, f.A);
        }
        faces.RemoveAll(f => f.Dead);
        foreach (var ((a, b), _) in horizon)
        {
            // New face (a, b, p), wound like the face it replaces: outward is away from the hull's interior.
            var normal = Vector3.Cross(points[b] - points[a], pt - points[a]);
            float len = normal.Length();
            if (len < 1e-9f) continue;
            normal /= len;
            float d = Vector3.Dot(normal, pt);
            // Interior reference: the tetrahedron's centre stays inside the growing hull.
            if (Vector3.Dot(normal, centre) - d > 0) faces.Add(new Face { A = b, B = a, C = p, Normal = -normal, D = -d });
            else faces.Add(new Face { A = a, B = b, C = p, Normal = normal, D = d });
        }
        return true;
    }

    /// <summary>
    /// Records the directed edge (a, b) of a dead face, or cancels it against its reverse.
    /// </summary>
    static void Edge(Dictionary<(int, int), int> horizon, int a, int b)
    {
        if (!horizon.Remove((b, a))) horizon[(a, b)] = 0;
    }

    /// <summary>
    /// The faces' corners, three per face.
    /// </summary>
    static int[] Indices(List<Face> faces)
    {
        var result = new int[faces.Count * 3];
        for (int i = 0; i < faces.Count; i++)
        {
            result[i * 3] = faces[i].A;
            result[i * 3 + 1] = faces[i].B;
            result[i * 3 + 2] = faces[i].C;
        }
        return result;
    }
}