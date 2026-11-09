using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// The characters of the last tick sorted into square cells on the ground plane, for neighbour queries (separation, targets in
/// reach). Built once per tick in the serial Schedule step from <see cref="CharacterTable.Previous"/>, then only read, so phases
/// can query it from any thread. Within a cell the slots are ascending; <see cref="Query"/> visits cells in a fixed order, so the
/// order neighbours are seen in depends on the state alone, never on the thread count (float sums over neighbours rely on that).
/// </summary>
public sealed class SpatialGrid
{
    /// <summary>
    /// The edge of a cell.
    /// </summary>
    readonly float _cellSize;
    /// <summary>
    /// The (cell, slot) pairs of the living characters, sorted.
    /// </summary>
    (long Cell, int Slot)[] _entries = [];
    /// <summary>
    /// The number of entries in use.
    /// </summary>
    int _count;

    /// <summary>
    /// A grid of cells <paramref name="cellSize"/> on a side.
    /// </summary>
    public SpatialGrid(float cellSize)
    {
        if (!(cellSize > 0)) throw new ArgumentOutOfRangeException(nameof(cellSize));

        this._cellSize = cellSize;
    }

    /// <summary>
    /// The edge of a cell.
    /// </summary>
    public float CellSize => _cellSize;

    /// <summary>
    /// Sorts the living characters of <paramref name="state"/> into their cells.
    /// </summary>
    internal void Build(ReadOnlySpan<CharacterHot> state)
    {
        if (_entries.Length < state.Length) _entries = new (long, int)[Math.Max(state.Length, _entries.Length * 2)];

        _count = 0;

        for (int i = 0; i < state.Length; i++)
        {
            if (!state[i].Alive) continue;

            _entries[_count++] = (CellOf(state[i].Position.X, state[i].Position.Z), i);
        }

        // (cell, slot) pairs are unique, so the sort is total and its result does not depend on the algorithm.
        Array.Sort(_entries, 0, _count);
    }

    /// <summary>
    /// The packed cell of a point.
    /// </summary>
    long CellOf(float x, float z) => Pack((int)MathF.Floor(x / _cellSize), (int)MathF.Floor(z / _cellSize));

    /// <summary>
    /// The cell column and row of a point (the cell <see cref="Build"/> files it in).
    /// </summary>
    internal (int X, int Z) Cell(Vector3 at) => ((int)MathF.Floor(at.X / _cellSize), (int)MathF.Floor(at.Z / _cellSize));

    /// <summary>
    /// The cells <see cref="Query"/> visits for a circle: a character is visited exactly when its <see cref="Cell"/> lies in this range.
    /// </summary>
    internal CellRange Range(float x, float z, float radius) =>
        new((int)MathF.Floor((x - radius) / _cellSize), (int)MathF.Floor((x + radius) / _cellSize), (int)MathF.Floor((z - radius) / _cellSize),
            (int)MathF.Floor((z + radius) / _cellSize));

    /// <summary>
    /// A rectangle of cells, the ends included.
    /// </summary>
    internal readonly record struct CellRange(int X0, int X1, int Z0, int Z1)
    {
        /// <summary>
        /// Whether <paramref name="cell"/> lies in the range.
        /// </summary>
        public bool Holds((int X, int Z) cell) => cell.X >= X0 && cell.X <= X1 && cell.Z >= Z0 && cell.Z <= Z1;
    }

    /// <summary>
    /// Packs a cell's column and row into one sort key (the row unsigned).
    /// </summary>
    static long Pack(int cx, int cz) => ((long)cx << 32) | (uint)cz;

    /// <summary>
    /// Calls <paramref name="visit"/> with the slot of every character whose cell is within the cells overlapping the circle of
    /// <paramref name="radius"/> round (<paramref name="x"/>, <paramref name="z"/>); the caller tests the exact distance. The
    /// order is by cell (row by row), then slot.
    /// </summary>
    public void Query<TState>(float x, float z, float radius, ref TState state, QueryVisitor<TState> visit)
    {
        int x0 = (int)MathF.Floor((x - radius) / _cellSize), x1 = (int)MathF.Floor((x + radius) / _cellSize);
        int z0 = (int)MathF.Floor((z - radius) / _cellSize), z1 = (int)MathF.Floor((z + radius) / _cellSize);

        // The cells of one column (cx) with cz ascending are adjacent in the sorted entries, so one search per run finds the first and a scan to
        // the last visits them in the same order as a search per cell would. The packed cz is unsigned, so negative rows sort after the others:
        // a column crossing cz = 0 is two runs, the negative one first.
        for (int cx = x0; cx <= x1; cx++)
        {
            if (z0 < 0 && z1 >= 0)
            {
                Run(Pack(cx, z0), Pack(cx, -1), ref state, visit);
                Run(Pack(cx, 0), Pack(cx, z1), ref state, visit);
            }
            else Run(Pack(cx, z0), Pack(cx, z1), ref state, visit);
        }
    }

    /// <summary>
    /// Visits the entries from the cell <paramref name="first"/> to <paramref name="last"/> in order.
    /// </summary>
    void Run<TState>(long first, long last, ref TState state, QueryVisitor<TState> visit)
    {
        for (int i = FirstOf(first); i < _count && _entries[i].Cell <= last; i++) visit(_entries[i].Slot, ref state);
    }

    /// <summary>
    /// Visits one found slot with the caller's state.
    /// </summary>
    public delegate void QueryVisitor<TState>(int slot, ref TState state);

    /// <summary>
    /// The index of the first entry of cell <paramref name="key"/> or after it.
    /// </summary>
    int FirstOf(long key)
    {
        int lo = 0, hi = _count;

        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_entries[mid].Cell < key) lo = mid + 1; else hi = mid;
        }

        return lo;
    }
}