using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// An immutable copy of the terrain's height sources (the whole-world coarse grid and the streamed fine window with its fade band), taken on the
/// render thread by <see cref="TerrainRenderer.Snapshot"/>. <see cref="HeightAt"/> gives exactly what <see cref="TerrainRenderer.HeightAt"/> gave at
/// that moment and may be called from any thread (the arrays and the window are never written after they are published).
/// </summary>
internal readonly struct HeightSnapshot
{
    readonly ushort[] coarse;
    readonly int coarseSize;
    readonly HeightWindow fine;
    readonly float fineBand, fx0, fz0, fx1, fz1;

    public HeightSnapshot(ushort[] coarse, int coarseSize, HeightWindow fine, float fineBand)
    {
        this.coarse = coarse;
        this.coarseSize = coarseSize;
        this.fine = fine;
        this.fineBand = fineBand;
        var (x0, z0) = fine.WorldOf(0, 0);
        fx0 = (float)x0;
        fz0 = (float)z0;
        fx1 = fx0 + (fine.Columns - 1) * fine.Spacing;
        fz1 = fz0 + (fine.Rows - 1) * fine.Spacing;
    }

    /// <summary>Height at a world point (bilinear in the coarse grid, blended into the fine window across its edge band).</summary>
    public float HeightAt(float x, float z)
    {
        float h = WorldLayout.HalfWorldSize;
        float c = Bilinear(coarse, coarseSize, coarseSize, x + h, z + h, WorldLayout.WorldSize / (float)(coarseSize - 1));
        float w = Math.Clamp(Math.Min(Math.Min(x - fx0, fx1 - x), Math.Min(z - fz0, fz1 - z)) / fineBand, 0, 1);
        if (w <= 0) return c;
        return c + (Bilinear(fine.Raw, fine.Columns, fine.Rows, x - fx0, z - fz0, fine.Spacing) - c) * w;
    }

    static float Bilinear(ushort[] raw, int cols, int rows, float x, float z, float spacing)
    {
        float fx = Math.Clamp(x / spacing, 0, cols - 1), fz = Math.Clamp(z / spacing, 0, rows - 1);
        int i = Math.Clamp((int)fx, 0, Math.Max(cols - 2, 0)), j = Math.Clamp((int)fz, 0, Math.Max(rows - 2, 0));
        int i1 = Math.Min(i + 1, cols - 1), j1 = Math.Min(j + 1, rows - 1);
        float tx = fx - i, tz = fz - j;
        float v = raw[j * cols + i] * (1 - tx) * (1 - tz) + raw[j * cols + i1] * tx * (1 - tz) + raw[j1 * cols + i] * (1 - tx) * tz + raw[j1 * cols + i1] * tx * tz;
        return v * (WorldLayout.MaxHeight / ushort.MaxValue);
    }
}
