using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// A rectangular window of heightmap samples in memory, taken every <see cref="Step"/>-th sample. Window
/// point (i, j) is heightmap sample (<see cref="Column0"/> + i·step, <see cref="Row0"/> + j·step); samples past
/// the heightmap's edge repeat the edge. Column follows world +X, row world +Z (docs/formats/terrain.md).
/// </summary>
public sealed class HeightWindow
{
    /// <summary>
    /// A window of <paramref name="columns"/> × <paramref name="rows"/> <paramref name="raw"/> samples from (<paramref name="column0"/>,
    /// <paramref name="row0"/>), <paramref name="step"/> apart.
    /// </summary>
    public HeightWindow(int column0, int row0, int step, int columns, int rows, ushort[] raw)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        if (raw.Length != columns * rows) throw new ArgumentException($"{raw.Length} samples for a {columns}x{rows} window.", nameof(raw));
        (Column0, Row0, Step, Columns, Rows, Raw) = (column0, row0, step, columns, rows, raw);
    }

    /// <summary>
    /// Heightmap column and row of window point (0, 0); may lie outside the heightmap.
    /// </summary>
    public int Column0 { get; }
    /// <summary>
    /// See <see cref="Column0"/>.
    /// </summary>
    public int Row0 { get; }
    /// <summary>
    /// Heightmap samples between neighbouring window points.
    /// </summary>
    public int Step { get; }
    /// <summary>
    /// Window points along X.
    /// </summary>
    public int Columns { get; }
    /// <summary>
    /// Window points along Z.
    /// </summary>
    public int Rows { get; }
    /// <summary>
    /// Raw samples, row-major.
    /// </summary>
    public ushort[] Raw { get; }

    /// <summary>
    /// World units between neighbouring window points.
    /// </summary>
    public float Spacing => Step * WorldLayout.SampleSpacing;

    /// <summary>
    /// World height at window point (i, j), clamped to the window.
    /// </summary>
    public float Height(int i, int j) =>
        WorldLayout.RawToHeight(Raw[Math.Clamp(j, 0, Rows - 1) * Columns + Math.Clamp(i, 0, Columns - 1)]);

    /// <summary>
    /// The height at world X/Z interpolated bilinearly between the window's points; false outside the window.
    /// </summary>
    public bool TryHeightAt(double x, double z, out float height)
    {
        var (col, row) = WorldLayout.ToSample(x, z);
        double u = (col - Column0) / Step, v = (row - Row0) / Step;
        height = 0;
        if (u < 0 || v < 0 || u > Columns - 1 || v > Rows - 1) return false;
        int i = Math.Min((int)u, Columns - 2), j = Math.Min((int)v, Rows - 2);
        double fu = u - i, fv = v - j;
        height = (float)(Height(i, j) * (1 - fu) * (1 - fv) + Height(i + 1, j) * fu * (1 - fv) + Height(i, j + 1) * (1 - fu) * fv
            + Height(i + 1, j + 1) * fu * fv);
        return true;
    }

    /// <summary>
    /// World X/Z of window point (i, j).
    /// </summary>
    public (double X, double Z) WorldOf(int i, int j) => WorldLayout.FromSample(Column0 + (double)i * Step, Row0 + (double)j * Step);

    /// <summary>
    /// World position of window point (i, j).
    /// </summary>
    public Vector3 Position(int i, int j)
    {
        var (x, z) = WorldOf(i, j);
        return new Vector3((float)x, Height(i, j), (float)z);
    }

    /// <summary>
    /// Surface normal at window point (i, j) from central differences of the window's own heights (one-sided at
    /// the window edge). For y = h(x, z) the normal is (−∂h/∂x, 1, −∂h/∂z), normalised.
    /// </summary>
    public Vector3 Normal(int i, int j)
    {
        int i0 = Math.Max(i - 1, 0), i1 = Math.Min(i + 1, Columns - 1);
        int j0 = Math.Max(j - 1, 0), j1 = Math.Min(j + 1, Rows - 1);
        float dx = (Height(i1, j) - Height(i0, j)) / Math.Max((i1 - i0) * Spacing, 1e-6f);
        float dz = (Height(i, j1) - Height(i, j0)) / Math.Max((j1 - j0) * Spacing, 1e-6f);
        return Vector3.Normalize(new Vector3(-dx, 1, -dz));
    }
}