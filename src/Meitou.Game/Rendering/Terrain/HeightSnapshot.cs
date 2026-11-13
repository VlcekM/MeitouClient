using Meitou.Data.World;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// An immutable copy of the terrain's height sources (the whole-world coarse grid and the streamed fine window with its fade band), taken on the
/// render thread by <see cref="TerrainRenderer.Snapshot"/>. <see cref="HeightAt"/> gives exactly what <see cref="TerrainRenderer.HeightAt"/> gave at
/// that moment and may be called from any thread (the arrays and the window are never written after they are published).
/// </summary>
internal readonly struct HeightSnapshot
{
    /// <summary>
    /// The whole-world coarse heights (raw 16-bit, row-major).
    /// </summary>
    readonly ushort[] _coarse;

    /// <summary>
    /// The coarse grid's side in samples.
    /// </summary>
    readonly int _coarseSize;

    /// <summary>
    /// The streamed fine window.
    /// </summary>
    readonly HeightWindow _fine;

    /// <summary>
    /// The fine window's fade band (world units) and its world rectangle (x0, z0, x1, z1, sample centre to sample centre).
    /// </summary>
    readonly float _fineBand, _fx0, _fz0, _fx1, _fz1;

    /// <summary>
    /// The fine window it was taken with: the same one means the same heights (they are never written after they are published).
    /// </summary>
    public HeightWindow Fine => _fine;

    /// <summary>
    /// A snapshot of the given arrays (not copied).
    /// </summary>
    public HeightSnapshot(ushort[] coarse, int coarseSize, HeightWindow fine, float fineBand)
    {
        this._coarse = coarse;
        this._coarseSize = coarseSize;
        this._fine = fine;
        this._fineBand = fineBand;
        var (x0, z0) = fine.WorldOf(0, 0);
        _fx0 = (float)x0;
        _fz0 = (float)z0;
        _fx1 = _fx0 + (fine.Columns - 1) * fine.Spacing;
        _fz1 = _fz0 + (fine.Rows - 1) * fine.Spacing;
    }

    /// <summary>
    /// Height at a world point (bilinear in the coarse grid, blended into the fine window across its edge band).
    /// </summary>
    public float HeightAt(float x, float z)
    {
        float h = WorldLayout.HalfWorldSize;
        float c = Bilinear(_coarse, _coarseSize, _coarseSize, x + h, z + h, WorldLayout.WorldSize / (float)(_coarseSize - 1));
        float w = Math.Clamp(Math.Min(Math.Min(x - _fx0, _fx1 - x), Math.Min(z - _fz0, _fz1 - z)) / _fineBand, 0, 1);
        if (w <= 0) return c;
        return c + (Bilinear(_fine.Raw, _fine.Columns, _fine.Rows, x - _fx0, z - _fz0, _fine.Spacing) - c) * w;
    }

    /// <summary>
    /// Height at a world point through a smooth surface: Catmull-Rom over the fine window's samples (the coarse grid's bilinear outside it,
    /// blended as <see cref="HeightAt"/> blends). The drawn ground curves its triangles' edges between the samples (the outline refinement); this
    /// follows such a curve far closer than the bilinear facets do.
    /// </summary>
    public float SmoothHeightAt(float x, float z)
    {
        float h = WorldLayout.HalfWorldSize;
        float c = Bilinear(_coarse, _coarseSize, _coarseSize, x + h, z + h, WorldLayout.WorldSize / (float)(_coarseSize - 1));
        float w = Math.Clamp(Math.Min(Math.Min(x - _fx0, _fx1 - x), Math.Min(z - _fz0, _fz1 - z)) / _fineBand, 0, 1);
        if (w <= 0) return c;
        return c + (Bicubic(_fine.Raw, _fine.Columns, _fine.Rows, x - _fx0, z - _fz0, _fine.Spacing) - c) * w;
    }

    /// <summary>
    /// Catmull-Rom height of a raw grid at (<paramref name="x"/>, <paramref name="z"/>) from its corner, in world units (clamped to the grid).
    /// </summary>
    static float Bicubic(ushort[] raw, int cols, int rows, float x, float z, float spacing)
    {
        float fx = Math.Clamp(x / spacing, 0, cols - 1), fz = Math.Clamp(z / spacing, 0, rows - 1);
        int i = Math.Clamp((int)fx, 0, Math.Max(cols - 2, 0)), j = Math.Clamp((int)fz, 0, Math.Max(rows - 2, 0));
        float tx = fx - i, tz = fz - j;
        float sum = 0;
        for (int b = -1; b <= 2; b++)
        {
            int row = Math.Clamp(j + b, 0, rows - 1) * cols;
            float line = 0;
            for (int a = -1; a <= 2; a++) line += raw[row + Math.Clamp(i + a, 0, cols - 1)] * CatmullRom(a, tx);
            sum += line * CatmullRom(b, tz);
        }

        return sum * (WorldLayout.MaxHeight / ushort.MaxValue);
    }

    /// <summary>
    /// The Catmull-Rom weight of sample <paramref name="k"/> (-1 to 2) at <paramref name="t"/> between samples 0 and 1.
    /// </summary>
    static float CatmullRom(int k, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return k switch
        {
            -1 => 0.5f * (-t3 + 2 * t2 - t),
            0 => 0.5f * (3 * t3 - 5 * t2 + 2),
            1 => 0.5f * (-3 * t3 + 4 * t2 + t),
            _ => 0.5f * (t3 - t2),
        };
    }

    /// <summary>
    /// Bilinear height of a raw grid at (<paramref name="x"/>, <paramref name="z"/>) from its corner, in world units (clamped to the grid).
    /// </summary>
    static float Bilinear(ushort[] raw, int cols, int rows, float x, float z, float spacing)
    {
        float fx = Math.Clamp(x / spacing, 0, cols - 1), fz = Math.Clamp(z / spacing, 0, rows - 1);
        int i = Math.Clamp((int)fx, 0, Math.Max(cols - 2, 0)), j = Math.Clamp((int)fz, 0, Math.Max(rows - 2, 0));
        int i1 = Math.Min(i + 1, cols - 1), j1 = Math.Min(j + 1, rows - 1);
        float tx = fx - i, tz = fz - j;
        float v = raw[j * cols + i] * (1 - tx) * (1 - tz) + raw[j * cols + i1] * tx * (1 - tz) + raw[j1 * cols + i] * (1 - tx) * tz +
            raw[j1 * cols + i1] * tx * tz;
        return v * (WorldLayout.MaxHeight / ushort.MaxValue);
    }
}

/// <summary>
/// A terrain height source for a worker thread: <see cref="Refresh"/> (render thread, while no worker reads it) takes a <see cref="HeightSnapshot"/>,
/// <see cref="HeightAt"/> reads it.
/// </summary>
public sealed class TerrainHeights
{
    /// <summary>
    /// The snapshot <see cref="HeightAt"/> reads.
    /// </summary>
    HeightSnapshot _snapshot;

    /// <summary>
    /// Takes a new snapshot of <paramref name="terrain"/>.
    /// </summary>
    public void Refresh(TerrainRenderer terrain) => _snapshot = terrain.Snapshot();

    /// <summary>
    /// The height at a world point in the last snapshot.
    /// </summary>
    public float HeightAt(float x, float z) => _snapshot.HeightAt(x, z);
}