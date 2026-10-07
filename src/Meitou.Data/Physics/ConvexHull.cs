using System.Numerics;

namespace Meitou.Data.Physics;

/// <summary>
/// Incremental 3D convex hull for the small point sets of cooked convex meshes (tens of vertices). Triangles come out with
/// outward normals under <c>cross(v1 - v0, v2 - v0)</c>.
/// </summary>
public static class ConvexHull
{
    sealed class Face
    {
        public int A, B, C;
        public Vector3 Normal;
        public float D;
        public bool Dead;
    }

    /// <summary>The hull's triangles as indices into <paramref name="points"/>; empty when the points are (nearly) flat, collinear or fewer than four.</summary>
    public static int[] Triangles(Vector3[] points, float epsilon = 1e-3f)
    {
        int n = points.Length;
        if (n < 4) return [];

        // Initial tetrahedron: the two farthest points on an axis extreme, then the farthest from their line, then from their plane.
        int i0 = 0, i1 = 0;
        for (int i = 1; i < n; i++)
        {
            if (points[i].X < points[i0].X) i0 = i;
            if (points[i].X > points[i1].X) i1 = i;
        }
        if (i0 == i1) i1 = (i0 + 1) % n;
        float best = -1;
        int i2 = -1;
        for (int i = 0; i < n; i++)
        {
            float d = Vector3.Cross(points[i1] - points[i0], points[i] - points[i0]).LengthSquared();
            if (d > best) { best = d; i2 = i; }
        }
        if (best < epsilon * epsilon) return [];
        var baseNormal = Vector3.Normalize(Vector3.Cross(points[i1] - points[i0], points[i2] - points[i0]));
        best = -1;
        int i3 = -1;
        for (int i = 0; i < n; i++)
        {
            float d = MathF.Abs(Vector3.Dot(points[i] - points[i0], baseNormal));
            if (d > best) { best = d; i3 = i; }
        }
        if (best < epsilon) return [];

        var faces = new List<Face>();
        var centre = (points[i0] + points[i1] + points[i2] + points[i3]) * 0.25f;
        void AddFace(int a, int b, int c, Vector3 inside)
        {
            var normal = Vector3.Cross(points[b] - points[a], points[c] - points[a]);
            float len = normal.Length();
            if (len < 1e-9f) return;
            normal /= len;
            float d = Vector3.Dot(normal, points[a]);
            if (Vector3.Dot(normal, inside) - d > 0) { (b, c) = (c, b); normal = -normal; d = -d; }
            faces.Add(new Face { A = a, B = b, C = c, Normal = normal, D = d });
        }
        AddFace(i0, i1, i2, centre);
        AddFace(i0, i1, i3, centre);
        AddFace(i0, i2, i3, centre);
        AddFace(i1, i2, i3, centre);

        var used = new HashSet<int> { i0, i1, i2, i3 };
        var horizon = new Dictionary<(int, int), int>();
        for (int p = 0; p < n; p++)
        {
            if (used.Contains(p)) continue;
            var pt = points[p];
            bool any = false;
            foreach (var f in faces)
                if (!f.Dead && Vector3.Dot(f.Normal, pt) - f.D > epsilon) { f.Dead = true; any = true; }
            if (!any) continue;
            // Horizon: directed edges of dead faces whose reverse is not an edge of another dead face.
            horizon.Clear();
            foreach (var f in faces)
            {
                if (!f.Dead) continue;
                Edge(f.A, f.B); Edge(f.B, f.C); Edge(f.C, f.A);
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
                if (Vector3.Dot(normal, centre) - d > 0) { faces.Add(new Face { A = b, B = a, C = p, Normal = -normal, D = -d }); }
                else faces.Add(new Face { A = a, B = b, C = p, Normal = normal, D = d });
            }
            used.Add(p);

            void Edge(int a, int b)
            {
                if (!horizon.Remove((b, a))) horizon[(a, b)] = 0;
            }
        }
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
