using System.Numerics;
using DotRecast.Recast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Rasterizes a triangle into a tile's spans: clipped to each row of cells and each cell of the row, its height range in the cell added as a span.
/// Recast's rcRasterizeTriangle, adapted from DotRecast's <c>RcRasterizations.RasterizeTri</c> and <c>DividePoly</c> (zlib licence,
/// THIRD_PARTY_NOTICES.md) with the arithmetic kept in its order, so the spans are the same; it writes into a <see cref="NavSpanField"/>.
/// </summary>
internal static class NavTriangleRaster
{
    /// <summary>
    /// The vertices a triangle clipped by a cell's four sides can have.
    /// </summary>
    const int MaxVertices = 7;

    /// <summary>
    /// The floats of one clipped polygon.
    /// </summary>
    const int PolygonFloats = MaxVertices * 3;

    /// <summary>
    /// The polygons a triangle is clipped through, and where each of the four stands in <see cref="Buffer"/>: the part of the triangle not yet
    /// cut into rows, the row being cut into cells, the cell, and a spare the row's rest is cut into.
    /// </summary>
    ref struct Clip
    {
        /// <summary>
        /// Four polygons of <see cref="PolygonFloats"/>.
        /// </summary>
        public Span<float> Buffer;

        /// <summary>
        /// Scratch of <see cref="Divide"/>: each vertex's distance to the cutting line.
        /// </summary>
        public Span<float> Delta;

        /// <summary>
        /// The offset in <see cref="Buffer"/> of the triangle's rest, of the row, of the cell and of the spare.
        /// </summary>
        public int Rest, Row, Cell, Spare;
    }

    /// <summary>
    /// Adds the triangle of the vertices <paramref name="a"/>, <paramref name="b"/> and <paramref name="c"/> of <paramref name="verts"/> to
    /// <paramref name="field"/> with <paramref name="area"/>, its spans merged within <paramref name="mergeThreshold"/>.
    /// </summary>
    public static void Rasterize(NavSpanField field, in NavTileGrid grid, float[] verts, int a, int b, int c, int area, int mergeThreshold)
    {
        var va = new Vector3(verts[a * 3], verts[a * 3 + 1], verts[a * 3 + 2]);
        var vb = new Vector3(verts[b * 3], verts[b * 3 + 1], verts[b * 3 + 2]);
        var vc = new Vector3(verts[c * 3], verts[c * 3 + 1], verts[c * 3 + 2]);
        float minZ = MathF.Min(MathF.Min(va.Z, vb.Z), vc.Z);
        float maxZ = MathF.Max(MathF.Max(va.Z, vb.Z), vc.Z);
        if (!Overlaps(grid, va, vb, vc, minZ, maxZ)) return;

        float inverseCellSize = 1.0f / grid.CellSize;
        int z0 = Math.Clamp((int)((minZ - grid.Min.Z) * inverseCellSize), -1, grid.Height - 1);
        int z1 = Math.Clamp((int)((maxZ - grid.Min.Z) * inverseCellSize), 0, grid.Height - 1);

        var clip = new Clip
        {
            Buffer = stackalloc float[PolygonFloats * 4],
            Delta = stackalloc float[MaxVertices],
            Rest = 0,
            Row = PolygonFloats,
            Cell = PolygonFloats * 2,
            Spare = PolygonFloats * 3,
        };
        va.CopyTo(clip.Buffer);
        vb.CopyTo(clip.Buffer[3..]);
        vc.CopyTo(clip.Buffer[6..]);

        int restCount = 3;
        for (int z = z0; z <= z1; z++)
        {
            // The row's part of the triangle; what lies beyond the row is the rest for the next one.
            float cellZ = grid.Min.Z + z * grid.CellSize;
            Divide(ref clip, clip.Rest, restCount, clip.Row, out int rowCount, clip.Cell, out restCount, cellZ + grid.CellSize, axis: 2);
            (clip.Rest, clip.Cell) = (clip.Cell, clip.Rest);
            if (rowCount < 3 || z < 0) continue;

            RasterizeRow(field, grid, ref clip, rowCount, z, area, mergeThreshold);
        }
    }

    /// <summary>
    /// Whether the box of the triangle <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/> touches the grid's.
    /// </summary>
    static bool Overlaps(in NavTileGrid grid, Vector3 a, Vector3 b, Vector3 c, float minZ, float maxZ)
    {
        float minX = MathF.Min(MathF.Min(a.X, b.X), c.X);
        float maxX = MathF.Max(MathF.Max(a.X, b.X), c.X);
        float minY = MathF.Min(MathF.Min(a.Y, b.Y), c.Y);
        float maxY = MathF.Max(MathF.Max(a.Y, b.Y), c.Y);
        return minX <= grid.Max.X && maxX >= grid.Min.X
            && minY <= grid.Max.Y && maxY >= grid.Min.Y
            && minZ <= grid.Max.Z && maxZ >= grid.Min.Z;
    }

    /// <summary>
    /// Cuts the row polygon of <paramref name="rowCount"/> vertices in row <paramref name="z"/> into its cells and adds each cell's span.
    /// </summary>
    static void RasterizeRow(NavSpanField field, in NavTileGrid grid, ref Clip clip, int rowCount, int z, int area, int mergeThreshold)
    {
        float minX = clip.Buffer[clip.Row];
        float maxX = clip.Buffer[clip.Row];
        for (int i = 1; i < rowCount; i++)
        {
            float v = clip.Buffer[clip.Row + i * 3];
            minX = Math.Min(minX, v);
            maxX = Math.Max(maxX, v);
        }

        float inverseCellSize = 1.0f / grid.CellSize;
        int x0 = (int)((minX - grid.Min.X) * inverseCellSize);
        int x1 = (int)((maxX - grid.Min.X) * inverseCellSize);
        if (x1 < 0 || x0 >= grid.Width) return;

        x0 = Math.Clamp(x0, -1, grid.Width - 1);
        x1 = Math.Clamp(x1, 0, grid.Width - 1);
        int restCount = rowCount;
        for (int x = x0; x <= x1; x++)
        {
            // The cell's part of the row; the row's rest goes to the spare, which is the row for the next cell.
            float cellX = grid.Min.X + x * grid.CellSize;
            Divide(ref clip, clip.Row, restCount, clip.Cell, out int count, clip.Spare, out restCount, cellX + grid.CellSize, axis: 0);
            (clip.Row, clip.Spare) = (clip.Spare, clip.Row);
            if (count < 3 || x < 0) continue;

            AddCell(field, grid, clip.Buffer.Slice(clip.Cell, count * 3), x + z * grid.Width, area, mergeThreshold);
        }
    }

    /// <summary>
    /// Adds the height range of a cell's polygon to <paramref name="column"/> as a span, clamped to the grid's heights.
    /// </summary>
    static void AddCell(NavSpanField field, in NavTileGrid grid, ReadOnlySpan<float> polygon, int column, int area, int mergeThreshold)
    {
        float min = polygon[1];
        float max = polygon[1];
        for (int i = 4; i < polygon.Length; i += 3)
        {
            min = Math.Min(min, polygon[i]);
            max = Math.Max(max, polygon[i]);
        }

        float height = grid.Max.Y - grid.Min.Y;
        min -= grid.Min.Y;
        max -= grid.Min.Y;
        if (max < 0.0f || min > height) return;

        if (min < 0.0f) min = 0;
        if (max > height) max = height;

        float inverseCellHeight = 1.0f / grid.CellHeight;
        int bottom = Math.Clamp((int)MathF.Floor(min * inverseCellHeight), 0, RcRecast.RC_SPAN_MAX_HEIGHT);
        int top = Math.Clamp((int)MathF.Ceiling(max * inverseCellHeight), bottom + 1, RcRecast.RC_SPAN_MAX_HEIGHT);
        field.Add(column, bottom, top, area, mergeThreshold);
    }

    /// <summary>
    /// Cuts the polygon of <paramref name="count"/> vertices at <paramref name="from"/> by the line <paramref name="offset"/> on
    /// <paramref name="axis"/>: the part under the line goes to <paramref name="low"/>, the part over it to <paramref name="high"/> (offsets in
    /// the clip's buffer).
    /// </summary>
    static void Divide(ref Clip clip, int from, int count, int low, out int lowCount, int high, out int highCount, float offset, int axis)
    {
        var buffer = clip.Buffer;
        var delta = clip.Delta;
        for (int i = 0; i < count; i++)
            delta[i] = offset - buffer[from + i * 3 + axis];

        lowCount = 0;
        highCount = 0;
        for (int a = 0, b = count - 1; a < count; b = a, a++)
        {
            var va = buffer.Slice(from + a * 3, 3);
            if ((delta[a] >= 0) != (delta[b] >= 0))
            {
                // The edge crosses the line: the crossing point goes to both parts, then the vertex to its own.
                float s = delta[b] / (delta[b] - delta[a]);
                var vb = buffer.Slice(from + b * 3, 3);
                var crossing = buffer.Slice(low + lowCount * 3, 3);
                crossing[0] = vb[0] + (va[0] - vb[0]) * s;
                crossing[1] = vb[1] + (va[1] - vb[1]) * s;
                crossing[2] = vb[2] + (va[2] - vb[2]) * s;
                crossing.CopyTo(buffer.Slice(high + highCount * 3, 3));
                lowCount++;
                highCount++;

                if (delta[a] > 0)
                    va.CopyTo(buffer.Slice(low + lowCount++ * 3, 3));
                else if (delta[a] < 0)
                    va.CopyTo(buffer.Slice(high + highCount++ * 3, 3));

                continue;
            }

            // The edge stays on one side: the vertex goes to that part, one on the line to both.
            if (delta[a] >= 0)
            {
                va.CopyTo(buffer.Slice(low + lowCount++ * 3, 3));
                if (delta[a] != 0) continue;
            }

            va.CopyTo(buffer.Slice(high + highCount++ * 3, 3));
        }
    }
}
