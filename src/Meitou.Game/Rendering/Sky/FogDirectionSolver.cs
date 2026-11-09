using System.Numerics;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The fog cull's bound by ray direction (docs/formats/fogfeatures.md "Fog cull by direction"): solved for a block round an eye, in the background
/// unless inline, and kept while the eye stays within <see cref="FogVolumes.DirectionSolveMargin"/> of where it was solved.
/// </summary>
sealed class FogDirectionSolver
{
    /// <summary>
    /// A finished solve: the block, the eye it was solved round, the far clip, and the radius found (null: none).
    /// </summary>
    readonly record struct DirectionSolve(FogVolumes.Volume Block, Vector3 Centre, float Far, float? Radius);

    /// <summary>
    /// The last solve that landed.
    /// </summary>
    DirectionSolve? _solved;

    /// <summary>
    /// The solve running in the background, or null.
    /// </summary>
    Task<DirectionSolve>? _solving;

    /// <summary>
    /// A solve is running in the background.
    /// </summary>
    public bool Solving => _solving is not null;

    /// <summary>
    /// <see cref="HideDistanceByDirection"/> for an eye within <see cref="FogVolumes.DirectionSolveMargin"/> of where it was last solved for this
    /// block and far clip; else a new solve round this eye starts (in the background unless <paramref name="inline"/>) and null is returned until it
    /// lands.
    /// </summary>
    public float? Radius(FogVolumes.Volume block, Vector3 eye, float far, bool inline)
    {
        if (_solving is { IsCompleted: true } done)
        {
            _solved = done.Result;
            _solving = null;
        }
        bool Fits(DirectionSolve s) => s.Block == block && s.Far == far && Vector3.Distance(s.Centre, eye) <= FogVolumes.DirectionSolveMargin;
        if (_solved is { } s && Fits(s)) return s.Radius;
        if (_solving is not null) return null;
        var planes = block.Data.AsSpan(3, 7).ToArray();
        float edgeBlur = block.Data[1].W, density = block.Data[2].W;
        DirectionSolve Solve() => new(block, eye, far,
            HideDistanceByDirection(planes, eye, FogVolumes.DirectionSolveMargin, edgeBlur, density, far, FogVolumes.DirectionSolveBudget));
        if (inline)
        {
            _solved = Solve();
            return Fits(_solved.Value) ? _solved.Value.Radius : null;
        }
        _solving = Task.Run(Solve);
        return null;
    }

    /// <summary>
    /// A tighter hide distance than <see cref="FogVolumes.HideDistance"/>, by the ray direction (docs/formats/fogfeatures.md "Fog cull by
    /// direction"): the least R found such that every ray from an eye within <paramref name="margin"/> of <paramref name="eye"/> whose path inside
    /// the block is between R and the far clip <paramref name="far"/> gets alpha 0.9998 by the shader's own formula; null when the search runs past
    /// <paramref name="budget"/> cells.
    /// <para>Along the unit ray d the shader's path is L = min(fragment, exit T(d), far), and plane i's distance at the path's middle is inside_i - L
    /// dn_i / 2 (dn_i = n_i . d): at least inside_i / 2 when dn_i &gt; 0 (L dn_i &lt;= inside_i, the path ends inside the plane) and at least
    /// inside_i otherwise. The directions are cells of a cube map and the paths intervals [L0, L1]. Over a cell with centre c and chord radius delta
    /// (|d - c| &lt;= delta, largest at a corner since the cell is the cone of its corners), dn_i is within |n_i| delta of n_i . c and |d.y| is at
    /// least |c.y| - delta, so the curve's input is at least L0 density (1 + 0.9 max(|c.y| - delta, 0)) prod clamp(blur(L1) lower_i) (blur does not
    /// grow with L). A cell is dropped when that reaches <see cref="FogVolumes.CullAlpha"/> (proven) or when no path of it ends inside the block (L0
    /// dn_i &gt; inside_i across the cell for a plane with dn_i &gt; 0). The rest are split, the path or the directions (whichever moves the middle
    /// point more), longest paths first, so the first cell too small to split that is not proven bounds R by its L1. The margin lowers each inside_i
    /// for the fog and raises it for the paths, so one result holds while the eye stays within it.</para>
    /// </summary>
    public static float? HideDistanceByDirection(ReadOnlySpan<Vector4> planes, Vector3 eye, float margin, float edgeBlur, float density,
        float far, int budget = 40000)
    {
        if (!(density > 0) || !(edgeBlur > 0) || far <= 0 || planes.Length == 0) return null;
        int n = planes.Length;
        Span<double> nx = stackalloc double[n], ny = stackalloc double[n], nz = stackalloc double[n], length = stackalloc double[n];
        Span<double> insideLo = stackalloc double[n], insideHi = stackalloc double[n];
        if (!MeasurePlanes(planes, eye, margin, nx, ny, nz, length, insideLo, insideHi)) return null;
        // A little above CullAlpha: the shader works in float.
        const double threshold = FogVolumes.CullAlpha + 5e-4;
        var queue = SeedDirectionCells(far);
        int cells = 0;
        while (queue.TryDequeue(out var cell, out _))
        {
            if (++cells > budget) return null;
            var (cx, cy, cz) = CubeDirection(cell.Face, (cell.U0 + cell.U1) * 0.5, (cell.V0 + cell.V1) * 0.5);
            double delta = 0;
            for (int k = 0; k < 4; k++)
            {
                var (x, y, z) = CubeDirection(cell.Face, (k & 1) == 0 ? cell.U0 : cell.U1, (k & 2) == 0 ? cell.V0 : cell.V1);
                delta = Math.Max(delta, Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz)));
            }
            delta = delta * (1 + 1e-9) + 1e-12;
            double blur = edgeBlur * Math.Min(1, 1 / (cell.L1 * 0.00006));
            double edge = 1;
            bool feasible = true;
            for (int i = 0; i < n; i++)
            {
                double dn = nx[i] * cx + ny[i] * cy + nz[i] * cz, lo = dn - length[i] * delta, hi = dn + length[i] * delta;
                if (lo > 0 && cell.L0 * lo > insideHi[i])
                {
                    feasible = false;
                    break;
                }
                double lower = hi <= 0 ? insideLo[i] : Math.Max(insideLo[i] - cell.L1 * hi * 0.5, insideLo[i] * 0.5);
                edge *= Math.Clamp(blur * lower, 0, 1);
            }
            if (!feasible) continue;
            edge *= 1 + 0.9 * Math.Max(Math.Abs(cy) - delta, 0);
            if (cell.L0 * density * edge >= threshold) continue;
            double spreadPath = cell.L1 - cell.L0, spreadDirection = delta * cell.L1;
            double tolerance = Math.Max(16, cell.L1 * 0.02);
            // Paths up to L1 may fail (and at L1 itself, in a cell of the same priority not yet split): R is past it, and none when that is the far
            // clip.
            if (spreadPath <= tolerance && spreadDirection <= tolerance)
                return cell.L1 + 1 < far ? (float)(cell.L1 + 1) : null;
            if (spreadPath >= spreadDirection) SplitPath(queue, cell);
            else SplitDirections(queue, cell);
        }
        return 0;
    }

    /// <summary>
    /// A cell of the search: cube face <c>Face</c>'s directions over [U0, U1] × [V0, V1], paths inside the block over [L0, L1].
    /// </summary>
    readonly record struct DirectionCell(int Face, double U0, double U1, double V0, double V1, double L0, double L1);

    /// <summary>
    /// Measures each plane's normal length and the eye's inside distance with the margin (into the spans); false when the eye is not inside one of
    /// the planes.
    /// </summary>
    static bool MeasurePlanes(ReadOnlySpan<Vector4> planes, Vector3 eye, float margin, Span<double> nx, Span<double> ny, Span<double> nz,
        Span<double> length, Span<double> insideLo, Span<double> insideHi)
    {
        for (int i = 0; i < planes.Length; i++)
        {
            var p = planes[i];
            (nx[i], ny[i], nz[i]) = (p.X, p.Y, p.Z);
            length[i] = Math.Sqrt(nx[i] * nx[i] + ny[i] * ny[i] + nz[i] * nz[i]);
            double inside = p.W - (nx[i] * eye.X + ny[i] * eye.Y + nz[i] * eye.Z);
            (insideLo[i], insideHi[i]) = (inside - margin * length[i], inside + margin * length[i]);
            if (insideLo[i] <= 1) return false;
        }
        return true;
    }

    /// <summary>
    /// The six cube faces, each in a 4 × 4 grid of cells over the whole path range (0 to <paramref name="far"/>).
    /// </summary>
    static PriorityQueue<DirectionCell, double> SeedDirectionCells(float far)
    {
        var queue = new PriorityQueue<DirectionCell, double>();
        for (int face = 0; face < 6; face++)
            for (int a = 0; a < 4; a++)
                for (int b = 0; b < 4; b++)
                    queue.Enqueue(new DirectionCell(face, -1 + a * 0.5, -0.5 + a * 0.5, -1 + b * 0.5, -0.5 + b * 0.5, 0, far), -far);
        return queue;
    }

    /// <summary>
    /// Splits a cell at the middle of its path range.
    /// </summary>
    static void SplitPath(PriorityQueue<DirectionCell, double> queue, DirectionCell cell)
    {
        double mid = (cell.L0 + cell.L1) * 0.5;
        queue.Enqueue(cell with { L0 = mid }, -cell.L1);
        queue.Enqueue(cell with { L1 = mid }, -mid);
    }

    /// <summary>
    /// Splits a cell into four at the middle of its directions.
    /// </summary>
    static void SplitDirections(PriorityQueue<DirectionCell, double> queue, DirectionCell cell)
    {
        double um = (cell.U0 + cell.U1) * 0.5, vm = (cell.V0 + cell.V1) * 0.5;
        queue.Enqueue(cell with { U1 = um, V1 = vm }, -cell.L1);
        queue.Enqueue(cell with { U0 = um, V1 = vm }, -cell.L1);
        queue.Enqueue(cell with { U1 = um, V0 = vm }, -cell.L1);
        queue.Enqueue(cell with { U0 = um, V0 = vm }, -cell.L1);
    }

    /// <summary>
    /// The unit direction through (u, v) of cube face <paramref name="face"/> (0..5: +x, -x, +y, -y, +z, -z).
    /// </summary>
    static (double X, double Y, double Z) CubeDirection(int face, double u, double v)
    {
        var (x, y, z) = face switch { 0 => (1.0, u, v), 1 => (-1.0, u, v), 2 => (u, 1.0, v), 3 => (u, -1.0, v), 4 => (u, v, 1.0), _ => (u, v, -1.0) };
        double l = Math.Sqrt(x * x + y * y + z * z);
        return (x / l, y / l, z / l);
    }
}
