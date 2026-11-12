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
    /// <summary>
    /// The heightmap window sampled inside it.
    /// </summary>
    readonly HeightWindow _window;
    /// <summary>
    /// The whole-world grid sampled outside the window; null when there is none.
    /// </summary>
    readonly ushort[]? _coarse;
    /// <summary>
    /// Samples per side of the coarse grid.
    /// </summary>
    readonly int _coarseSize;
    /// <summary>
    /// Heightmap samples between neighbouring coarse samples.
    /// </summary>
    readonly int _coarseStep;

    /// <summary>
    /// Heights from <paramref name="window"/>, and outside it from <paramref name="coarse"/> when given.
    /// </summary>
    public GroundHeights(HeightWindow window, ushort[]? coarse = null, int coarseSize = 0, int coarseStep = 8)
    {
        this._window = window ?? throw new ArgumentNullException(nameof(window));
        this._coarse = coarse;
        this._coarseSize = coarseSize;
        this._coarseStep = coarseStep;
    }

    /// <summary>
    /// World height at X/Z, in world units.
    /// </summary>
    public float HeightAt(float x, float z)
    {
        double step = _window.Step;
        var (col, row) = WorldLayout.ToSample(x, z);
        double u = (col - _window.Column0) / step, v = (row - _window.Row0) / step;
        if (u >= 0 && v >= 0 && u <= _window.Columns - 1 && v <= _window.Rows - 1)
        {
            int i = Math.Min((int)u, _window.Columns - 2), j = Math.Min((int)v, _window.Rows - 2);
            if (i < 0 || j < 0) return _window.Height(Math.Max(i, 0), Math.Max(j, 0));
            double fu = u - i, fv = v - j;
            return (float)(_window.Height(i, j) * (1 - fu) * (1 - fv) + _window.Height(i + 1, j) * fu * (1 - fv) +
                           _window.Height(i, j + 1) * (1 - fu) * fv + _window.Height(i + 1, j + 1) * fu * fv);
        }
        if (_coarse is null)
        {
            int ci = Math.Clamp((int)Math.Round(u), 0, _window.Columns - 1), cj = Math.Clamp((int)Math.Round(v), 0, _window.Rows - 1);
            return _window.Height(ci, cj);
        }
        double cu = Math.Clamp(col / _coarseStep, 0, _coarseSize - 1), cv = Math.Clamp(row / _coarseStep, 0, _coarseSize - 1);
        int c = Math.Min((int)cu, _coarseSize - 2), r = Math.Min((int)cv, _coarseSize - 2);
        double a = cu - c, b = cv - r;
        double h = _coarse[r * _coarseSize + c] * (1 - a) * (1 - b) + _coarse[r * _coarseSize + c + 1] * a * (1 - b) +
                   _coarse[(r + 1) * _coarseSize + c] * (1 - a) * b + _coarse[(r + 1) * _coarseSize + c + 1] * a * b;
        return (float)(h * (WorldLayout.MaxHeight / ushort.MaxValue));
    }

    /// <summary>
    /// Whether X/Z lies inside the full-resolution window.
    /// </summary>
    public bool InsideWindow(float x, float z)
    {
        var (col, row) = WorldLayout.ToSample(x, z);
        double u = (col - _window.Column0) / _window.Step, v = (row - _window.Row0) / _window.Step;
        return u >= 0 && v >= 0 && u <= _window.Columns - 1 && v <= _window.Rows - 1;
    }
}