using System.Numerics;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The probe layers the placed fog hides per column (<see cref="GiFogHide"/>), worked out again only when the eye, the grids or the fog's cull state
/// moved past what the cached answer allows. Shared by the Vulkan and Metal probes.
/// </summary>
internal sealed class ProbeFogCache
{
    /// <summary>
    /// How far the eye may move before the hidden layers are worked out again (the reach boxes are widened by twice that).
    /// </summary>
    const float HiddenSlack = 96;

    /// <summary>
    /// Per tile (cascade, toroidal row, toroidal column): the layers the fog hides.
    /// </summary>
    readonly byte[] _hiddenLayers = new byte[GiProbes.Cascades * GiProbes.Columns * GiProbes.Columns];

    /// <summary>
    /// The fog's cull state the hidden layers were worked out for: its block, hide radius and weather.
    /// </summary>
    (object? Block, float Radius, float Weather) _hiddenState;

    /// <summary>
    /// The eye the hidden layers were worked out at.
    /// </summary>
    Vector3 _hiddenEye;

    /// <summary>
    /// Whether <see cref="_hiddenLayers"/> holds an answer (the fog culls).
    /// </summary>
    bool _hiddenValid;

    /// <summary>
    /// Whether the cache holds an answer.
    /// </summary>
    public bool Valid => _hiddenValid;

    /// <summary>
    /// The layers hidden per tile.
    /// </summary>
    public ReadOnlySpan<byte> HiddenLayers => _hiddenLayers;

    /// <summary>
    /// The probes the fog hides per cascade (all of them, not only the share of this frame) at the last solve.
    /// </summary>
    public int[] Hidden { get; } = new int[GiProbes.Cascades];

    /// <summary>
    /// How many times the hidden layers were worked out, and the ms spent on it in total.
    /// </summary>
    public int Solves { get; private set; }

    /// <summary>
    /// See <see cref="Solves"/>.
    /// </summary>
    public double SolveMs { get; private set; }

    /// <summary>
    /// Forgets the answer (the fog does not cull, or hiding is off).
    /// </summary>
    public void Invalidate() => _hiddenValid = false;

    /// <summary>
    /// The hidden layers for the grids <paramref name="grid"/> (<c>uGiGrid</c>) with their bases and spacings, worked out again unless the cached
    /// answer holds: the eye has moved less than the slack, the grids did not scroll, and the hide distances have grown by no more than the margin
    /// left of the boxes' widening (twice the slack, less the move).
    /// </summary>
    public void Update(FogVolumes fog, Vector3 eye, bool scrolled, ReadOnlySpan<Vector4> grid, ReadOnlySpan<float> baseHeights,
        Func<int, (float Spacing, float SpacingY)> layout)
    {
        var now = fog.CullState;
        float moved = Vector3.Distance(eye, _hiddenEye), margin = 2 * HiddenSlack - moved;
        bool same = _hiddenValid && !scrolled && now.Block == _hiddenState.Block && moved <= HiddenSlack
            && Holds(now.Radius, _hiddenState.Radius, margin) && Holds(now.Weather, _hiddenState.Weather, margin);
        if (same) return;

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Array.Clear(Hidden);
        float nearest = MathF.Min(now.Radius > 0 ? now.Radius : float.MaxValue, now.Weather > 0 ? now.Weather : float.MaxValue);
        for (int c = 0; c < GiProbes.Cascades; c++) Solve(fog, eye, c, grid, baseHeights, layout(c), nearest);

        Solves++;
        SolveMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _hiddenState = now;
        _hiddenEye = eye;
        _hiddenValid = true;
    }

    /// <summary>
    /// Whether a cached hide distance still holds for the current one.
    /// </summary>
    static bool Holds(float now, float cached, float margin) => now == 0 ? cached == 0 : cached > 0 && now <= cached + margin && now >= cached - 100;

    /// <summary>
    /// Cascade <paramref name="c"/>'s hidden layers; a column nearer than the nearest hide distance (in x, z alone, a lower bound of its box's
    /// distance) has none.
    /// </summary>
    void Solve(FogVolumes fog, Vector3 eye, int c, ReadOnlySpan<Vector4> grid, ReadOnlySpan<float> baseHeights, (float S, float Sy) spacing,
        float nearest)
    {
        const int n = GiProbes.Columns;
        var (s, sy) = spacing;
        float across = s + GiFogHide.LookupOffset * MathF.Min(s, sy) + 2 * HiddenSlack;
        float x0 = grid[c * 2].X, z0 = grid[c * 2].Y;
        int offX = (int)grid[c * 2 + 1].X, offZ = (int)grid[c * 2 + 1].Y;

        for (int k = 0; k < n; k++)
        {
            for (int i = 0; i < n; i++)
            {
                int column = (c * n + (offZ + k) % n) * n + (offX + i) % n;
                float dx = MathF.Max(MathF.Abs(x0 + i * s - eye.X) - across, 0), dz = MathF.Max(MathF.Abs(z0 + k * s - eye.Z) - across, 0);
                if (dx * dx + dz * dz < nearest * nearest)
                {
                    _hiddenLayers[column] = 0;
                    continue;
                }

                int layers = GiFogHide.HiddenLayers(fog.Covers, x0 + i * s, z0 + k * s, baseHeights[column], s, sy, GiProbes.Layers, fog.CullFloor,
                    2 * HiddenSlack);
                _hiddenLayers[column] = (byte)layers;
                Hidden[c] += layers;
            }
        }
    }
}
