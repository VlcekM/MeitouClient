using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// Ground height for the simulation, read from the CPU heightmap and immutable once built, so any thread may sample it
/// (docs/simulation.md: the simulation does not depend on the renderer). Inside the <see cref="HeightWindow"/> it interpolates
/// the window's samples bilinearly; outside, the coarse whole-world grid (every <c>coarseStep</c>-th sample, as
/// <see cref="TerrainHeightmap.Downsample"/> returns it) so a character that strays past the window still stands on something.
/// </summary>
public sealed class GroundHeights
{
    readonly HeightWindow window;
    readonly ushort[]? coarse;
    readonly int coarseSize;
    readonly int coarseStep;

    public GroundHeights(HeightWindow window, ushort[]? coarse = null, int coarseSize = 0, int coarseStep = 8)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.coarse = coarse;
        this.coarseSize = coarseSize;
        this.coarseStep = coarseStep;
    }

    /// <summary>World height at X/Z, in world units.</summary>
    public float HeightAt(float x, float z)
    {
        double step = window.Step;
        var (col, row) = WorldLayout.ToSample(x, z);
        double u = (col - window.Column0) / step, v = (row - window.Row0) / step;
        if (u >= 0 && v >= 0 && u <= window.Columns - 1 && v <= window.Rows - 1)
        {
            int i = Math.Min((int)u, window.Columns - 2), j = Math.Min((int)v, window.Rows - 2);
            if (i < 0 || j < 0) return window.Height(Math.Max(i, 0), Math.Max(j, 0));
            double fu = u - i, fv = v - j;
            return (float)(window.Height(i, j) * (1 - fu) * (1 - fv) + window.Height(i + 1, j) * fu * (1 - fv) +
                           window.Height(i, j + 1) * (1 - fu) * fv + window.Height(i + 1, j + 1) * fu * fv);
        }
        if (coarse is null)
        {
            int ci = Math.Clamp((int)Math.Round(u), 0, window.Columns - 1), cj = Math.Clamp((int)Math.Round(v), 0, window.Rows - 1);
            return window.Height(ci, cj);
        }
        double cu = Math.Clamp(col / coarseStep, 0, coarseSize - 1), cv = Math.Clamp(row / coarseStep, 0, coarseSize - 1);
        int c = Math.Min((int)cu, coarseSize - 2), r = Math.Min((int)cv, coarseSize - 2);
        double a = cu - c, b = cv - r;
        double h = coarse[r * coarseSize + c] * (1 - a) * (1 - b) + coarse[r * coarseSize + c + 1] * a * (1 - b) +
                   coarse[(r + 1) * coarseSize + c] * (1 - a) * b + coarse[(r + 1) * coarseSize + c + 1] * a * b;
        return (float)(h * (WorldLayout.MaxHeight / ushort.MaxValue));
    }

    /// <summary>Whether X/Z lies inside the full-resolution window.</summary>
    public bool InsideWindow(float x, float z)
    {
        var (col, row) = WorldLayout.ToSample(x, z);
        double u = (col - window.Column0) / window.Step, v = (row - window.Row0) / window.Step;
        return u >= 0 && v >= 0 && u <= window.Columns - 1 && v <= window.Rows - 1;
    }
}
