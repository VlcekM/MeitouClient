using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// Minimum and maximum terrain height over square areas of the world: a pyramid of cells, level 0 the cells of a
/// coarse heightmap grid covering the whole world (refined with the samples of a finer window where one is given),
/// each further level merging 2 × 2 cells, up to one cell for the world. Bounds are conservative for bilinear
/// interpolation of either grid.
/// </summary>
public sealed class TerrainHeightBounds
{
    readonly float[][] min, max;

    /// <param name="coarse">(<paramref name="size"/>)² raw samples covering the world, size = 2^n + 1 (row = +Z).</param>
    public TerrainHeightBounds(ushort[] coarse, int size, HeightWindow? fine = null)
    {
        int cells = size - 1;
        if (cells < 1 || (cells & (cells - 1)) != 0 || coarse.Length != size * size)
            throw new ArgumentException($"Coarse grid must be (2^n + 1)², got {coarse.Length} samples for size {size}.");
        Cells = cells;
        CellSize = (double)WorldLayout.WorldSize / cells;
        int levels = (int)Math.Log2(cells) + 1;
        min = new float[levels][];
        max = new float[levels][];
        var mn = new float[cells * cells];
        var mx = new float[cells * cells];
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                ushort a = coarse[z * size + x], b = coarse[z * size + x + 1], c = coarse[(z + 1) * size + x], d = coarse[(z + 1) * size + x + 1];
                mn[z * cells + x] = WorldLayout.RawToHeight(Math.Min(Math.Min(a, b), Math.Min(c, d)));
                mx[z * cells + x] = WorldLayout.RawToHeight(Math.Max(Math.Max(a, b), Math.Max(c, d)));
            }
        min[0] = mn;
        max[0] = mx;
        for (int l = 1; l < levels; l++)
        {
            min[l] = new float[(cells >> l) * (cells >> l)];
            max[l] = new float[(cells >> l) * (cells >> l)];
        }
        if (fine is not null) Apply(Measure(fine), rebuildAll: false);
        Rebuild(0, 0, cells - 1, cells - 1);
    }

    /// <summary>Recomputes the levels above 0 over the level-0 cell rectangle (inclusive) and what depends on it.</summary>
    void Rebuild(int cx0, int cz0, int cx1, int cz1)
    {
        for (int l = 1; l < min.Length; l++)
        {
            cx0 >>= 1; cz0 >>= 1; cx1 >>= 1; cz1 >>= 1;
            int n = Cells >> l, p = n * 2;
            for (int z = cz0; z <= cz1; z++)
                for (int x = cx0; x <= cx1; x++)
                {
                    int a = 2 * z * p + 2 * x, b = a + 1, c = a + p, d = c + 1;
                    min[l][z * n + x] = Math.Min(Math.Min(min[l - 1][a], min[l - 1][b]), Math.Min(min[l - 1][c], min[l - 1][d]));
                    max[l][z * n + x] = Math.Max(Math.Max(max[l - 1][a], max[l - 1][b]), Math.Max(max[l - 1][c], max[l - 1][d]));
                }
        }
    }

    /// <summary>The height range of a fine window's samples per level-0 cell, ready for <see cref="Apply"/>.</summary>
    /// <param name="Cx0">First level-0 cell column of the patch.</param>
    public sealed record Patch(int Cx0, int Cz0, int Width, int Height, float[] Min, float[] Max);

    /// <summary>
    /// Measures a window against the cell grid without touching the pyramid, so it can run on another thread while
    /// the pyramid is in use; <see cref="Apply"/> then merges the result.
    /// </summary>
    public Patch Measure(HeightWindow fine)
    {
        var (xa, za) = fine.WorldOf(0, 0);
        var (xb, zb) = fine.WorldOf(fine.Columns - 1, fine.Rows - 1);
        int cx0 = Math.Clamp((int)Math.Ceiling((xa + WorldLayout.HalfWorldSize) / CellSize) - 1, 0, Cells - 1);
        int cz0 = Math.Clamp((int)Math.Ceiling((za + WorldLayout.HalfWorldSize) / CellSize) - 1, 0, Cells - 1);
        int cx1 = Math.Clamp((int)Math.Floor((xb + WorldLayout.HalfWorldSize) / CellSize), cx0, Cells - 1);
        int cz1 = Math.Clamp((int)Math.Floor((zb + WorldLayout.HalfWorldSize) / CellSize), cz0, Cells - 1);
        int w = cx1 - cx0 + 1, h = cz1 - cz0 + 1;
        var mn = new float[w * h];
        var mx = new float[w * h];
        Array.Fill(mn, float.MaxValue);
        Array.Fill(mx, float.MinValue);
        var fxs = new double[fine.Columns];
        for (int i = 0; i < fxs.Length; i++) fxs[i] = (fine.WorldOf(i, 0).X + WorldLayout.HalfWorldSize) / CellSize;
        for (int j = 0; j < fine.Rows; j++)
        {
            double fz = (fine.WorldOf(0, j).Z + WorldLayout.HalfWorldSize) / CellSize;
            int zlo = Math.Max((int)Math.Ceiling(fz) - 1, cz0), zhi = Math.Min((int)Math.Floor(fz), cz1);
            for (int i = 0; i < fine.Columns; i++)
            {
                float v = WorldLayout.RawToHeight(fine.Raw[j * fine.Columns + i]);
                double fx = fxs[i];
                // A sample on a cell edge bounds the cells on both sides.
                int xlo = Math.Max((int)Math.Ceiling(fx) - 1, cx0), xhi = Math.Min((int)Math.Floor(fx), cx1);
                for (int cz = zlo; cz <= zhi; cz++)
                    for (int cx = xlo; cx <= xhi; cx++)
                    {
                        int k = (cz - cz0) * w + cx - cx0;
                        if (v < mn[k]) mn[k] = v;
                        if (v > mx[k]) mx[k] = v;
                    }
            }
        }
        return new Patch(cx0, cz0, w, h, mn, mx);
    }

    /// <summary>
    /// Widens the cells of a <see cref="Measure"/>d patch (never narrows them, so bounds stay valid for windows
    /// applied earlier) and refreshes the levels above.
    /// </summary>
    public void Apply(Patch patch) => Apply(patch, rebuildAll: true);

    void Apply(Patch patch, bool rebuildAll)
    {
        for (int z = 0; z < patch.Height; z++)
            for (int x = 0; x < patch.Width; x++)
            {
                int k = (patch.Cz0 + z) * Cells + patch.Cx0 + x, p = z * patch.Width + x;
                if (patch.Min[p] == float.MaxValue) continue;
                min[0][k] = Math.Min(min[0][k], patch.Min[p]);
                max[0][k] = Math.Max(max[0][k], patch.Max[p]);
            }
        if (rebuildAll) Rebuild(patch.Cx0, patch.Cz0, patch.Cx0 + patch.Width - 1, patch.Cz0 + patch.Height - 1);
    }

    /// <summary>Level-0 cells per side and their edge length in world units.</summary>
    public int Cells { get; }
    public double CellSize { get; }

    /// <summary>Height range over the square from (<paramref name="x0"/>, <paramref name="z0"/>) with edge <paramref name="size"/>.</summary>
    public (float Min, float Max) Range(double x0, double z0, double size)
    {
        int level = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(size / CellSize, 1))), 0, min.Length - 1);
        int n = Cells >> level;
        double cell = CellSize * (1 << level);
        int cx0 = Math.Clamp((int)Math.Floor((x0 + WorldLayout.HalfWorldSize) / cell + 1e-9), 0, n - 1);
        int cz0 = Math.Clamp((int)Math.Floor((z0 + WorldLayout.HalfWorldSize) / cell + 1e-9), 0, n - 1);
        int cx1 = Math.Clamp((int)Math.Ceiling((x0 + size + WorldLayout.HalfWorldSize) / cell - 1e-9) - 1, cx0, n - 1);
        int cz1 = Math.Clamp((int)Math.Ceiling((z0 + size + WorldLayout.HalfWorldSize) / cell - 1e-9) - 1, cz0, n - 1);
        float lo = float.MaxValue, hi = float.MinValue;
        for (int z = cz0; z <= cz1; z++)
            for (int x = cx0; x <= cx1; x++)
            {
                lo = Math.Min(lo, min[level][z * n + x]);
                hi = Math.Max(hi, max[level][z * n + x]);
            }
        return (lo, hi);
    }
}

/// <summary>A node chosen for drawing: a square of the quadtree drawn with one LOD level's grid.</summary>
/// <param name="Quadrant">-1: the whole node; 0..3: only that quarter (bit 0 = +X half, bit 1 = +Z half).</param>
public readonly record struct TerrainNode(int Level, double X0, double Z0, double Size, int Quadrant);

/// <summary>
/// Continuous distance-dependent LOD (CDLOD) over the whole world, the scheme whose morphing Kenshi's own terrain
/// shaders show (docs/formats/terrain.md, "Terrain LOD"). The world square is a quadtree; every node is drawn with the
/// same <see cref="GridCells"/>² grid, level 0 being the finest. A node of level l is drawn where the eye is within
/// <see cref="Ranges"/>[l] of it and refined where it is within Ranges[l − 1]. Between <see cref="MorphStart"/> and
/// <see cref="MorphEnd"/> of its level a vertex slides onto the grid of the next coarser level, reaching it by the end
/// of the range, so neighbouring levels meet without cracks or pops as long as the height depends on position only.
/// The ranges come from a screen-space error rule (<see cref="TerrainLod"/>, <see cref="SetRanges(TerrainLod)"/>) or, for tests,
/// a fixed multiple of the node size.
/// </summary>
public sealed class TerrainQuadtree
{
    /// <param name="finestSpacing">Vertex spacing wanted at level 0 in world units (the finest heightmap spacing drawn).</param>
    /// <param name="lodDistance">Range of a level in multiples of its node size (at least 2 keeps neighbours within one level).</param>
    public TerrainQuadtree(float finestSpacing, int gridCells = 32, float lodDistance = 2.5f, float morphFraction = 0.7f)
    {
        if (gridCells < 2 || (gridCells & (gridCells - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(gridCells), "must be a power of two");
        ArgumentOutOfRangeException.ThrowIfLessThan(lodDistance, 2f);
        GridCells = gridCells;
        // The leaf is the smallest power-of-two fraction of the world whose grid is not finer than asked.
        int levels = 1;
        while (levels < 20 && WorldLayout.WorldSize / (double)(1 << levels) / gridCells >= finestSpacing - 1e-6) levels++;
        LevelCount = levels;
        LeafSize = WorldLayout.WorldSize / (double)(1 << (levels - 1));
        this.morphFraction = morphFraction;
        Ranges = new float[levels];
        MorphStart = new float[levels];
        MorphEnd = new float[levels];
        var ranges = new float[levels];
        for (int l = 0; l < levels; l++)
            ranges[l] = (float)(LeafSize * (1 << l) * lodDistance);
        SetRanges(ranges);
    }

    /// <summary>A tree whose level ranges come from a screen-space error rule (<see cref="TerrainLod.Ranges"/>).</summary>
    public TerrainQuadtree(float finestSpacing, int gridCells, TerrainLod lod) : this(finestSpacing, gridCells) => SetRanges(lod);

    readonly float morphFraction;

    /// <summary>Takes the level ranges of <paramref name="lod"/>; false when they are the ones in use already.</summary>
    public bool SetRanges(TerrainLod lod)
    {
        if (lod == lastLod) return false;   // the frame's passes ask every time; the rule rarely changes
        bool changed = SetRanges(lod.Ranges(LevelCount, LeafSize, GridCells));
        lastLod = lod;
        return changed;
    }

    TerrainLod? lastLod;

    /// <summary>
    /// Sets the level ranges (increasing; the last one is ignored: the root is drawn wherever nothing finer is) and the morph
    /// band of each level, the last <c>1 − morphFraction</c> of the distances between the finer level's range and its own.
    /// False when nothing changed.
    /// </summary>
    public bool SetRanges(ReadOnlySpan<float> ranges)
    {
        if (ranges.Length != LevelCount) throw new ArgumentException($"{LevelCount} ranges needed, got {ranges.Length}", nameof(ranges));
        lastLod = null;
        bool same = true;
        for (int l = 0; l + 1 < LevelCount; l++) same &= Ranges[l] == ranges[l];
        if (same && Ranges[^1] == float.MaxValue) return false;
        for (int l = 0; l + 1 < LevelCount; l++)
        {
            if (!(ranges[l] > (l == 0 ? 0 : ranges[l - 1]))) throw new ArgumentException($"range {l} ({ranges[l]}) does not grow", nameof(ranges));
            Ranges[l] = ranges[l];
        }
        for (int l = 0; l < LevelCount; l++)
        {
            float lo = l == 0 ? 0 : Ranges[l - 1];
            MorphEnd[l] = Ranges[l];
            MorphStart[l] = lo + (Ranges[l] - lo) * morphFraction;
        }
        // The root is drawn wherever nothing finer is: it covers the whole world from any eye position.
        Ranges[LevelCount - 1] = float.MaxValue;
        MorphStart[LevelCount - 1] = MorphEnd[LevelCount - 1] = float.MaxValue;
        return true;
    }

    public int GridCells { get; }
    public int LevelCount { get; }
    public double LeafSize { get; }
    public double NodeSize(int level) => LeafSize * (1 << level);
    public double Spacing(int level) => NodeSize(level) / GridCells;
    public float[] Ranges { get; }
    public float[] MorphStart { get; }
    public float[] MorphEnd { get; }

    /// <summary>The nodes to draw for an eye position, skipping those <paramref name="visible"/> rejects (box min, max).</summary>
    public void Select(Vector3 eye, TerrainHeightBounds bounds, Func<Vector3, Vector3, bool>? visible, List<TerrainNode> result)
    {
        result.Clear();
        Select(LevelCount - 1, -WorldLayout.HalfWorldSize, -WorldLayout.HalfWorldSize, eye, bounds, visible, result);
    }

    bool Select(int level, double x0, double z0, Vector3 eye, TerrainHeightBounds bounds, Func<Vector3, Vector3, bool>? visible, List<TerrainNode> result)
    {
        double size = NodeSize(level);
        var (lo, hi) = bounds.Range(x0, z0, size);
        var bmin = new Vector3((float)x0, lo, (float)z0);
        var bmax = new Vector3((float)(x0 + size), hi, (float)(z0 + size));
        if (!SphereTouchesBox(eye, Ranges[level], bmin, bmax)) return false;
        if (visible is not null && !visible(bmin, bmax)) return true; // in range but culled: nothing to draw
        if (level == 0 || !SphereTouchesBox(eye, Ranges[level - 1], bmin, bmax))
        {
            result.Add(new TerrainNode(level, x0, z0, size, -1));
            return true;
        }
        double half = size / 2;
        for (int q = 0; q < 4; q++)
        {
            double cx = x0 + (q & 1) * half, cz = z0 + (q >> 1) * half;
            if (!Select(level - 1, cx, cz, eye, bounds, visible, result))
                result.Add(new TerrainNode(level, x0, z0, size, q));
        }
        return true;
    }

    public static bool SphereTouchesBox(Vector3 centre, float radius, Vector3 min, Vector3 max)
    {
        if (radius == float.MaxValue) return true;
        var d = Vector3.Clamp(centre, min, max) - centre;
        return d.LengthSquared() <= radius * radius;
    }

    /// <summary>How far a vertex of <paramref name="level"/> at <paramref name="distance"/> from the eye has slid onto the coarser grid (0..1).</summary>
    public float Morph(int level, float distance) =>
        MorphStart[level] == float.MaxValue ? 0 : Math.Clamp((distance - MorphStart[level]) / Math.Max(MorphEnd[level] - MorphStart[level], 1e-3f), 0, 1);

    /// <summary>
    /// A grid coordinate (0..<see cref="GridCells"/>) after morphing by <paramref name="k"/>: odd coordinates move
    /// towards the even one below, so at k = 1 the vertex lies on the next level's grid.
    /// </summary>
    public static Vector2 MorphGrid(Vector2 grid, float k)
    {
        var odd = new Vector2(MathF.Floor(grid.X) % 2, MathF.Floor(grid.Y) % 2);
        return grid - odd * k;
    }

    /// <summary>
    /// Triangle-list indices of a (cells + 1)² vertex grid (vertex (i, j) at j·(cells + 1) + i, X along i, Z along j),
    /// counter-clockwise seen from above (+Y), for the whole grid (<paramref name="quadrant"/> −1) or one quarter.
    /// </summary>
    public static uint[] GridIndices(int cells, int quadrant = -1)
    {
        int n = cells + 1, i0 = 0, j0 = 0, count = cells;
        if (quadrant >= 0) { count = cells / 2; i0 = (quadrant & 1) * count; j0 = (quadrant >> 1) * count; }
        var list = new List<uint>(count * count * 6);
        uint V(int i, int j) => (uint)(j * n + i);
        for (int j = j0; j < j0 + count; j++)
            for (int i = i0; i < i0 + count; i++)
            {
                // (i, j) -> (i, j+1) -> (i+1, j): the face normal (cross product) points up, +Y.
                list.AddRange([V(i, j), V(i, j + 1), V(i + 1, j)]);
                list.AddRange([V(i + 1, j), V(i, j + 1), V(i + 1, j + 1)]);
            }
        return [.. list];
    }
}
