using System.Numerics;

namespace Meitou.Data.World;

public sealed partial class FloorSurfaces
{
    /// <summary>
    /// A surface's triangles binned on an X/Z grid over its box (engine choice): a point query tests only the triangles whose box, slightly widened, holds the point's cell,
    /// in their order in the surface, so it finds what testing them all would. Every character inside a building asks its floor and cover whenever it moves, and a large
    /// building's floor part holds thousands of triangles.
    /// </summary>
    sealed class TriangleGrid
    {
        /// <summary>The most cells along a side.</summary>
        const int MaxSide = 128;

        /// <summary>The most entries per triangle on average before the grid is made coarser (large triangles sit in many cells).</summary>
        const int MaxEntriesPerTriangle = 16;

        readonly Vector2 min;
        readonly float perX, perZ;
        readonly int nx, nz;

        // Cell c's triangles (their first corner's index) are items[start[c]..start[c + 1]].
        readonly int[] start, items;

        public TriangleGrid(Vector3[] corners, Vector2 min, Vector2 max)
        {
            this.min = min;
            int count = corners.Length / 3;
            var size = Vector2.Max(max - min, new Vector2(1e-3f));
            float cell = MathF.Sqrt(size.X * size.Y / Math.Max(count, 1));
            (nx, nz) = (Side(size.X, cell), Side(size.Y, cell));
            while (true)
            {
                (perX, perZ) = (nx / size.X, nz / size.Y);
                start = new int[nx * nz + 1];
                int entries = Count(corners);
                if (entries <= MaxEntriesPerTriangle * count + 1024 || nx * nz == 1) break;
                (nx, nz) = (Math.Max(nx / 2, 1), Math.Max(nz / 2, 1));
            }
            items = Fill(corners);
        }

        static int Side(float extent, float cell) => Math.Clamp((int)MathF.Ceiling(extent / cell), 1, MaxSide);

        /// <summary>The first corner's index of every triangle that may hold <paramref name="q"/> (X/Z), in surface order.</summary>
        public ReadOnlySpan<int> At(Vector2 q)
        {
            int c = Cell(q.Y, min.Y, perZ, nz) * nx + Cell(q.X, min.X, perX, nx);
            return items.AsSpan(start[c], start[c + 1] - start[c]);
        }

        static int Cell(float v, float origin, float per, int n) => Math.Clamp((int)((v - origin) * per), 0, n - 1);

        /// <summary>Counts each cell's triangles into <see cref="start"/> (shifted by one) and returns the total.</summary>
        int Count(Vector3[] corners)
        {
            int total = 0;
            for (int i = 0; i + 2 < corners.Length; i += 3)
            {
                var (x0, x1, z0, z1) = Cells(corners, i);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                        start[z * nx + x + 1]++;
                total += (x1 - x0 + 1) * (z1 - z0 + 1);
            }
            return total;
        }

        /// <summary>Turns the counts into offsets and lists each cell's triangles in surface order.</summary>
        int[] Fill(Vector3[] corners)
        {
            for (int c = 0; c < nx * nz; c++) start[c + 1] += start[c];
            var result = new int[start[^1]];
            var next = start[..^1];
            for (int i = 0; i + 2 < corners.Length; i += 3)
            {
                var (x0, x1, z0, z1) = Cells(corners, i);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                        result[next[z * nx + x]++] = i;
            }
            return result;
        }

        /// <summary>
        /// The cells of triangle <paramref name="i"/>'s X/Z box, widened past the slack the point tests allow at the edges (<see cref="HeightAt"/>, <see cref="RayTriangle"/>:
        /// a barycentric 1e-4 of the triangle's size).
        /// </summary>
        (int X0, int X1, int Z0, int Z1) Cells(Vector3[] corners, int i)
        {
            var (a, b, c) = (corners[i], corners[i + 1], corners[i + 2]);
            var lo = new Vector2(MathF.Min(a.X, MathF.Min(b.X, c.X)), MathF.Min(a.Z, MathF.Min(b.Z, c.Z)));
            var hi = new Vector2(MathF.Max(a.X, MathF.Max(b.X, c.X)), MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
            float pad = 1e-3f * MathF.Max(hi.X - lo.X, hi.Y - lo.Y) + 1e-2f;
            return (Cell(lo.X - pad, min.X, perX, nx), Cell(hi.X + pad, min.X, perX, nx), Cell(lo.Y - pad, min.Y, perZ, nz), Cell(hi.Y + pad, min.Y, perZ, nz));
        }
    }
}
