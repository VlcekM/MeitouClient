namespace Meitou.Data.World;

/// <summary>
/// Holes in the game's own heightmap (docs/formats/terrain.md "Holes and needles"): small patches at zero height inside raised ground, filled
/// smoothly from their rim. Part of <see cref="HeightmapRepair"/>.
/// </summary>
public static class HeightmapHoles
{
    /// <summary>
    /// The highest raw sample (about 10 units) that counts as the floor of a hole: the game's holes are zero, with a few samples of 1 or 2 at
    /// their edge.
    /// </summary>
    public const int Floor = 64;

    /// <summary>
    /// The largest patch filled, in samples: the game's own holes hold up to about 1,600; the sea floor and flat land at zero are far larger.
    /// </summary>
    public const int MaxSamples = 2000;

    /// <summary>
    /// The raw height (about 30 units) that more than half of a patch's rim (its neighbours above <see cref="Floor"/>) stands above, so the
    /// patch is a hole in raised ground and not the edge of the sea floor.
    /// </summary>
    public const int RimHeight = 200;

    /// <summary>
    /// The filled samples of every hole in <paramref name="map"/> as (column, row, value), and how many holes there were.
    /// </summary>
    public static (List<(int Column, int Row, ushort Value)> Samples, int Count) Find(TerrainHeightmap map)
    {
        var holes = Patches.Scan(map).Holes(map.Size);
        var samples = new List<(int Column, int Row, ushort Value)>();
        foreach (var hole in holes) samples.AddRange(Fill(map, hole));
        return (samples, holes.Count);
    }

    /// <summary>
    /// <paramref name="hole"/>'s samples raised to a smooth surface spanning its rim: the samples around it held fixed, the hole relaxed until
    /// each sample is the mean of its four neighbours.
    /// </summary>
    static List<(int Column, int Row, ushort Value)> Fill(TerrainHeightmap map, List<(int Column, int Row)> hole)
    {
        int c0 = hole.Min(s => s.Column) - 1, r0 = hole.Min(s => s.Row) - 1;
        int columns = hole.Max(s => s.Column) - c0 + 2, rows = hole.Max(s => s.Row) - r0 + 2;
        var raw = map.ReadWindow(c0, r0, columns, rows).Raw;
        var heights = raw.Select(h => (double)h).ToArray();
        var cells = hole.Select(s => (s.Row - r0) * columns + s.Column - c0).ToArray();
        double start = RimMean(raw, cells);
        foreach (int i in cells) heights[i] = start;

        Relax(heights, cells, columns);
        return [.. hole.Select((s, k) => (s.Column, s.Row, (ushort)Math.Clamp(Math.Round(heights[cells[k]]), 0, ushort.MaxValue)))];
    }

    /// <summary>
    /// The mean of the window's samples above <see cref="Floor"/> outside the hole: the fill's starting height.
    /// </summary>
    static double RimMean(ushort[] raw, int[] cells)
    {
        var inHole = cells.ToHashSet();
        var rim = raw.Where((h, i) => h > Floor && !inHole.Contains(i)).Select(h => (double)h).ToList();
        return rim.Count == 0 ? 0 : rim.Average();
    }

    /// <summary>
    /// Over-relaxed Gauss-Seidel on the <paramref name="cells"/> of <paramref name="heights"/> (a window <paramref name="columns"/> wide whose
    /// border never moves) until no sample moves by a quarter of a raw unit.
    /// </summary>
    static void Relax(double[] heights, int[] cells, int columns)
    {
        for (int pass = 0; pass < 5000; pass++)
        {
            double change = 0;
            foreach (int i in cells)
            {
                double mean = (heights[i - 1] + heights[i + 1] + heights[i - columns] + heights[i + columns]) / 4;
                double next = heights[i] + 1.9 * (mean - heights[i]);
                change = Math.Max(change, Math.Abs(next - heights[i]));
                heights[i] = next;
            }

            if (change < 0.25) return;
        }
    }

    /// <summary>
    /// The low patches of a heightmap: runs of samples at or below <see cref="Floor"/> per row, joined to the runs they touch in the row above
    /// (4-connected), with each patch's size and rim counted as it grows.
    /// </summary>
    sealed class Patches
    {
        /// <summary>
        /// Every run: its row, first column, the column after it, and its label.
        /// </summary>
        readonly List<(int Row, int Start, int End, int Label)> _runs = [];

        /// <summary>
        /// A label's parent label; a patch's root is its own parent.
        /// </summary>
        readonly List<int> _parent = [];

        /// <summary>
        /// Per root label: samples, rim samples (4-neighbours above the floor) and rim samples above <see cref="RimHeight"/>.
        /// </summary>
        readonly List<(long Samples, long Rim, long HighRim)> _stats = [];

        /// <summary>
        /// Labels every low sample of <paramref name="map"/>, streaming its rows once.
        /// </summary>
        public static Patches Scan(TerrainHeightmap map)
        {
            int size = map.Size;
            var patches = new Patches();
            var rows = new[] { new ushort[size], new ushort[size], new ushort[size] };
            var labels = new[] { new int[size], new int[size] };
            map.ReadRow(0, rows[1]);
            for (int r = 0; r < size; r++)
            {
                if (r + 1 < size) map.ReadRow(r + 1, rows[2]);

                Array.Fill(labels[1], -1);
                patches.ScanRow(r, size, rows, labels);
                (rows[0], rows[1], rows[2]) = (rows[1], rows[2], rows[0]);
                (labels[0], labels[1]) = (labels[1], labels[0]);
            }

            return patches;
        }

        /// <summary>
        /// The patches that are holes, each its samples as (column, row): no larger than <see cref="MaxSamples"/>, more than half the rim above
        /// <see cref="RimHeight"/>, and clear of the map's edge (where half the rim lies outside the map).
        /// </summary>
        public List<List<(int Column, int Row)>> Holes(int size)
        {
            var holes = new Dictionary<int, List<(int Column, int Row)>>();
            foreach (var (row, start, end, label) in _runs)
            {
                int root = Root(label);
                var (samples, rim, high) = _stats[root];
                if (samples > MaxSamples || 2 * high <= rim) continue;

                if (!holes.TryGetValue(root, out var hole)) holes[root] = hole = [];
                for (int c = start; c < end; c++) hole.Add((c, row));
            }

            return [.. holes.Values.Where(h => !h.Any(s => s.Column == 0 || s.Row == 0 || s.Column == size - 1 || s.Row == size - 1))];
        }

        /// <summary>
        /// Labels the low runs of row <paramref name="r"/> (<paramref name="rows"/>: the rows above, at and below it; <paramref name="labels"/>:
        /// the row above's labels and this row's, filled here) and joins each to the runs it touches above.
        /// </summary>
        void ScanRow(int r, int size, ushort[][] rows, int[][] labels)
        {
            var row = rows[1];
            for (int c = 0; c < size;)
            {
                if (row[c] > Floor)
                {
                    c++;
                    continue;
                }

                int start = c;
                while (c < size && row[c] <= Floor) c++;
                int label = AddRun(r, start, c, size, rows);
                Array.Fill(labels[1], label, start, c - start);
                if (r == 0) continue;

                for (int i = start; i < c; i++)
                    if (labels[0][i] >= 0) Join(labels[0][i], label);
            }
        }

        /// <summary>
        /// A new label for the run of row <paramref name="r"/> from <paramref name="start"/> to before <paramref name="end"/>, with its size and
        /// rim (the samples above, below and either side of it).
        /// </summary>
        int AddRun(int r, int start, int end, int size, ushort[][] rows)
        {
            long rim = 0, high = 0;
            void Count(ushort neighbour)
            {
                if (neighbour <= Floor) return;

                rim++;
                if (neighbour > RimHeight) high++;
            }

            for (int i = start; i < end; i++)
            {
                if (r > 0) Count(rows[0][i]);
                if (r + 1 < size) Count(rows[2][i]);
            }
            if (start > 0) Count(rows[1][start - 1]);
            if (end < size) Count(rows[1][end]);

            int label = _parent.Count;
            _parent.Add(label);
            _stats.Add((end - start, rim, high));
            _runs.Add((r, start, end, label));
            return label;
        }

        /// <summary>
        /// Joins the patches of labels <paramref name="a"/> and <paramref name="b"/>, adding their counts.
        /// </summary>
        void Join(int a, int b)
        {
            a = Root(a);
            b = Root(b);
            if (a == b) return;

            _parent[b] = a;
            _stats[a] = (_stats[a].Samples + _stats[b].Samples, _stats[a].Rim + _stats[b].Rim, _stats[a].HighRim + _stats[b].HighRim);
        }

        /// <summary>
        /// The root label of <paramref name="label"/>'s patch (halving the path on the way).
        /// </summary>
        int Root(int label)
        {
            while (_parent[label] != label) label = _parent[label] = _parent[_parent[label]];
            return label;
        }
    }
}
