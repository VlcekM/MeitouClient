using System.Collections.Concurrent;

namespace Meitou.Data.World;

/// <summary>
/// Needles in the game's own heightmap (docs/formats/terrain.md "Holes and needles"): a few samples standing far above smooth ground on every
/// side, lowered to it. Peaks in rough ground (rock, ridges, the corners of cliffs) are real terrain and stay. Part of <see cref="HeightmapRepair"/>.
/// </summary>
public static class HeightmapNeedles
{
    /// <summary>
    /// How far, in raw units (about 600 world units), a needle sample stands above the median of the 9 × 9 samples around it.
    /// </summary>
    public const int Height = 4000;

    /// <summary>
    /// How many samples around a needle must be smooth ground.
    /// </summary>
    public const int SmoothReach = 8;

    /// <summary>
    /// The most, in raw units (about 45 world units), any sample of smooth ground lies off its own 9 × 9 median.
    /// </summary>
    public const int SmoothTolerance = 300;

    /// <summary>
    /// The 9 × 9 neighbourhood's reach either side of a sample.
    /// </summary>
    const int Reach = 4;

    /// <summary>
    /// The neighbourhood's sample count.
    /// </summary>
    const int Window = (2 * Reach + 1) * (2 * Reach + 1);

    /// <summary>
    /// The rank of the neighbourhood's median: a sample is above the median by some height when at least this many lie that far below it.
    /// </summary>
    const int MedianRank = Window / 2 + 1;

    /// <summary>
    /// How close needle samples lie to count as one needle, and how far around a needle its own flanks reach.
    /// </summary>
    const int Spread = 2;

    /// <summary>
    /// The lowered samples of every needle in <paramref name="map"/> (opened from a file) as (column, row, value), and how many needles there
    /// were.
    /// </summary>
    public static (List<(int Column, int Row, ushort Value)> Samples, int Count) Find(TerrainHeightmap map)
    {
        var samples = new List<(int Column, int Row, ushort Value)>();
        var tips = Tips(map);
        var allTips = tips.ToHashSet();
        int count = 0;
        foreach (var group in Group(tips))
        {
            var lowered = Lower(map, group, allTips);
            if (lowered.Count == 0) continue;

            count++;
            samples.AddRange(lowered);
        }

        return (samples, count);
    }

    #region Finding the tips

    /// <summary>
    /// Every sample more than <see cref="Height"/> above the median of its 9 × 9 neighbourhood (edges repeat the nearest sample). Bands of rows
    /// are read in parallel, each from its own handle on the file.
    /// </summary>
    static List<(int Column, int Row)> Tips(TerrainHeightmap map)
    {
        string path = map.FilePath ?? throw new InvalidOperationException("The heightmap was not opened from a file.");
        int size = map.Size, bands = Math.Max(1, Environment.ProcessorCount);
        int perBand = (size + bands - 1) / bands;
        var found = new ConcurrentBag<(int, int)>();
        Parallel.For(0, bands, band =>
        {
            int first = band * perBand, last = Math.Min(size, first + perBand) - 1;
            if (first <= last) TipsInBand(path, size, first, last, found);
        });
        return [.. found];
    }

    /// <summary>
    /// Adds the tips of rows <paramref name="first"/> to <paramref name="last"/> to <paramref name="found"/>, reading each row within reach once
    /// into a rolling window (row k at k mod its length).
    /// </summary>
    static void TipsInBand(string path, int size, int first, int last, ConcurrentBag<(int, int)> found)
    {
        using var map = TerrainHeightmap.OpenUnrepaired(path);
        var window = new ushort[2 * Reach + 1][];
        for (int i = 0; i < window.Length; i++) window[i] = new ushort[size];

        int read = Math.Max(0, first - Reach) - 1;
        for (int r = first; r <= last; r++)
        {
            for (; read < Math.Min(size - 1, r + Reach); read++)
                map.ReadRow(read + 1, window[(read + 1) % window.Length]);

            var row = window[r % window.Length];
            for (int c = 0; c < size; c++)
            {
                if (row[c] > Height && StandsAbove(window, size, c, r, row[c] - Height)) found.Add((c, r));
            }
        }
    }

    /// <summary>
    /// Whether at least <see cref="MedianRank"/> of the 9 × 9 samples around (<paramref name="column"/>, <paramref name="row"/>) lie below
    /// <paramref name="floor"/>, that is, the neighbourhood's median does. <paramref name="window"/> holds row k at k mod its length.
    /// </summary>
    static bool StandsAbove(ushort[][] window, int size, int column, int row, int floor)
    {
        int below = 0, notBelow = 0;
        for (int dr = -Reach; dr <= Reach; dr++)
        {
            var samples = window[Math.Clamp(row + dr, 0, size - 1) % window.Length];
            for (int dc = -Reach; dc <= Reach; dc++)
            {
                if (samples[Math.Clamp(column + dc, 0, size - 1)] < floor)
                {
                    if (++below >= MedianRank) return true;
                }
                else if (++notBelow > Window - MedianRank) return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The tips grouped into needles: tips within <see cref="Spread"/> of each other in both directions are one needle.
    /// </summary>
    static List<List<(int Column, int Row)>> Group(List<(int Column, int Row)> tips)
    {
        var parent = Enumerable.Range(0, tips.Count).ToArray();
        int Root(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }

        var index = tips.Select((t, i) => (t, i)).ToDictionary(p => p.t, p => p.i);
        foreach (var ((c, r), i) in index)
            for (int dr = -Spread; dr <= Spread; dr++)
                for (int dc = -Spread; dc <= Spread; dc++)
                    if (index.TryGetValue((c + dc, r + dr), out var j)) parent[Root(j)] = Root(i);

        return [.. tips.Select((t, i) => (t, i)).GroupBy(p => Root(p.i)).Select(g => g.Select(p => p.t).ToList())];
    }

    #endregion

    #region Lowering a needle

    /// <summary>
    /// The lowered samples of the needle <paramref name="tips"/>, or none when it does not stand alone (<see cref="Area.StandsAlone"/>; the other
    /// needles' tips, <paramref name="allTips"/>, count as needle there, not as ground). Each sample of the needle (more than half
    /// <see cref="Height"/> above its median, within <see cref="Spread"/> of one of its tips) on the map takes the median of its neighbourhood's
    /// samples outside every needle.
    /// </summary>
    static List<(int Column, int Row, ushort Value)> Lower(TerrainHeightmap map, List<(int Column, int Row)> tips,
        HashSet<(int Column, int Row)> allTips)
    {
        var area = new Area(map, tips, allTips);
        if (!area.StandsAlone()) return [];

        var lowered = new List<(int, int, ushort)>();
        foreach (int i in area.Needle)
        {
            int column = area.Column0 + i % area.Columns;
            int row = area.Row0 + i / area.Columns;
            bool onMap = column >= 0 && row >= 0 && column < map.Size && row < map.Size;
            if (onMap) lowered.Add((column, row, area.GroundAt(i)));
        }

        return lowered;
    }

    /// <summary>
    /// The window around one needle: its samples, each sample's distance from its 9 × 9 median, the flanks of every needle's tips in it, and the
    /// needle itself.
    /// </summary>
    sealed class Area
    {
        /// <summary>
        /// The window's first column on the map.
        /// </summary>
        public readonly int Column0;

        /// <summary>
        /// The window's first row on the map.
        /// </summary>
        public readonly int Row0;

        /// <summary>
        /// The window's width: the tips' extent, the smooth ground around them, and the median's reach beyond that.
        /// </summary>
        public readonly int Columns;

        /// <summary>
        /// The window's height, made up as <see cref="Columns"/>.
        /// </summary>
        public readonly int Rows;

        /// <summary>
        /// The window's samples, row by row.
        /// </summary>
        readonly ushort[] _raw;

        /// <summary>
        /// Each sample less its 9 × 9 median (0 within <see cref="Reach"/> of the window's edge, which is never tested).
        /// </summary>
        readonly int[] _off;

        /// <summary>
        /// The samples within <see cref="Spread"/> of any needle's tip in the window: the needles and their flanks.
        /// </summary>
        readonly bool[] _flank;

        /// <summary>
        /// The samples within <see cref="Spread"/> of this needle's own tips.
        /// </summary>
        readonly bool[] _ownFlank;

        /// <summary>
        /// This needle's samples (its own flank samples more than half <see cref="Height"/> above their median), as window indices.
        /// </summary>
        public readonly List<int> Needle = [];

        /// <summary>
        /// Every needle's samples in the window (flank samples more than half <see cref="Height"/> above their median), marked by window index.
        /// </summary>
        readonly bool[] _inNeedle;

        /// <summary>
        /// The lowest tip's raw height.
        /// </summary>
        readonly int _lowestTip;

        /// <summary>
        /// Reads the window around <paramref name="tips"/> from <paramref name="map"/> and marks the needles in it (<paramref name="allTips"/>).
        /// </summary>
        public Area(TerrainHeightmap map, List<(int Column, int Row)> tips, HashSet<(int Column, int Row)> allTips)
        {
            int margin = SmoothReach + Spread + Reach;
            Column0 = tips.Min(t => t.Column) - margin;
            Row0 = tips.Min(t => t.Row) - margin;
            Columns = tips.Max(t => t.Column) - Column0 + margin + 1;
            Rows = tips.Max(t => t.Row) - Row0 + margin + 1;
            _raw = map.ReadWindow(Column0, Row0, Columns, Rows).Raw;
            _off = Offsets();
            _flank = new bool[_raw.Length];
            _ownFlank = new bool[_raw.Length];
            MarkFlanks(tips, allTips);
            _lowestTip = tips.Min(t => _raw[(t.Row - Row0) * Columns + t.Column - Column0]);
            _inNeedle = new bool[_raw.Length];
            MarkNeedles();
        }

        /// <summary>
        /// Whether the needle stands alone, so lowering it leaves smooth ground: within <see cref="SmoothReach"/> of the box around its tips, every
        /// sample not part of a needle lies at least half <see cref="Height"/> below its lowest tip (a cliff's corner has its plateau beside it,
        /// a block of high ground its middle), and every sample outside the needles' flanks is smooth ground, off its median by no more than
        /// <see cref="SmoothTolerance"/> (not rock or a ridge). Other needles nearby count as needle, so a line of needles is lowered whole.
        /// </summary>
        public bool StandsAlone()
        {
            const int edge = Spread + Reach;
            for (int r = edge; r < Rows - edge; r++)
            {
                for (int c = edge; c < Columns - edge; c++)
                {
                    int i = r * Columns + c;
                    if (!_inNeedle[i] && _raw[i] > _lowestTip - Height / 2) return false;
                    if (!_flank[i] && Math.Abs(_off[i]) > SmoothTolerance) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The ground height for needle sample <paramref name="i"/>: the median of its 9 × 9 neighbourhood leaving out every needle.
        /// </summary>
        public ushort GroundAt(int i) => (ushort)Median(i % Columns, i / Columns, _inNeedle);

        /// <summary>
        /// Each sample less its 9 × 9 median, for every sample at least <see cref="Reach"/> inside the window.
        /// </summary>
        int[] Offsets()
        {
            var off = new int[_raw.Length];
            for (int r = Reach; r < Rows - Reach; r++)
            {
                for (int c = Reach; c < Columns - Reach; c++)
                    off[r * Columns + c] = _raw[r * Columns + c] - Median(c, r, null);
            }

            return off;
        }

        /// <summary>
        /// Marks the flanks of every tip in the window (<paramref name="allTips"/>), and of this needle's own <paramref name="tips"/>.
        /// </summary>
        void MarkFlanks(List<(int Column, int Row)> tips, HashSet<(int Column, int Row)> allTips)
        {
            foreach (var (c, r) in allTips)
            {
                bool near = c >= Column0 - Spread && r >= Row0 - Spread && c < Column0 + Columns + Spread && r < Row0 + Rows + Spread;
                if (near) MarkFlank(_flank, c - Column0, r - Row0);
            }

            foreach (var (c, r) in tips) MarkFlank(_ownFlank, c - Column0, r - Row0);
        }

        /// <summary>
        /// Marks the samples within <see cref="Spread"/> of the tip at window (<paramref name="column"/>, <paramref name="row"/>) in
        /// <paramref name="marks"/>; samples outside the window are left out.
        /// </summary>
        void MarkFlank(bool[] marks, int column, int row)
        {
            for (int r = Math.Max(0, row - Spread); r <= Math.Min(Rows - 1, row + Spread); r++)
            {
                for (int c = Math.Max(0, column - Spread); c <= Math.Min(Columns - 1, column + Spread); c++)
                    marks[r * Columns + c] = true;
            }
        }

        /// <summary>
        /// The needles' samples: flank samples more than half <see cref="Height"/> above their median; this needle's are its own flank's.
        /// </summary>
        void MarkNeedles()
        {
            for (int i = 0; i < _raw.Length; i++)
            {
                if (!_flank[i] || _off[i] <= Height / 2) continue;

                _inNeedle[i] = true;
                if (_ownFlank[i]) Needle.Add(i);
            }
        }

        /// <summary>
        /// The median of the 9 × 9 window samples around (<paramref name="column"/>, <paramref name="row"/>), leaving out those marked in
        /// <paramref name="skip"/> (the sample itself when every one is).
        /// </summary>
        int Median(int column, int row, bool[]? skip)
        {
            Span<ushort> values = stackalloc ushort[Window];
            int n = 0;
            for (int dr = -Reach; dr <= Reach; dr++)
                for (int dc = -Reach; dc <= Reach; dc++)
                {
                    int i = (row + dr) * Columns + column + dc;
                    if (skip is null || !skip[i]) values[n++] = _raw[i];
                }

            if (n == 0) return _raw[row * Columns + column];

            values = values[..n];
            values.Sort();
            return values[n / 2];
        }
    }

    #endregion
}
