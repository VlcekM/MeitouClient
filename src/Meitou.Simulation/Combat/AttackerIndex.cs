namespace Meitou.Simulation.Combat;

/// <summary>
/// Who is after whom, from the combat state of the last tick (<see cref="CombatSystem"/>'s read buffer): per slot the characters whose target it is,
/// and those yielding to it. It stands in for the grid searches round a target that only looked for its own attackers: a check walks the target's
/// list and keeps the ones whose grid cell the search would have covered (<see cref="SpatialGrid.Range"/>), so it sees the same characters, and in
/// the same order (the grid's: cell column, row, then slot). Only characters the grid holds (alive in <see cref="CharacterTable.Previous"/>) are
/// listed. Built in a serial phase, read from any thread.
/// </summary>
sealed class AttackerIndex
{
    /// <summary>
    /// Each attacker (its target, grid cell and slot), sorted by target, then cell column, row and slot.
    /// </summary>
    (int Target, int X, int Z, int Slot)[] _attackers = [];
    /// <summary>
    /// Each yielder (whom it yields to, its slot), sorted by that slot.
    /// </summary>
    (int Target, int Slot)[] _yielders = [];
    /// <summary>
    /// How many entries of <see cref="_attackers"/> and <see cref="_yielders"/> are used, and the slots indexed (the table's high water at the
    /// last <see cref="Build"/>).
    /// </summary>
    int _attackerCount, _yielderCount, _size;

    /// <summary>
    /// Per slot: where its attackers and its yielders start in the sorted arrays (the next slot's start ends them).
    /// </summary>
    int[] _attackersAt = [], _yieldersAt = [];
    /// <summary>
    /// Each slot's grid cell at the last <see cref="Build"/>.
    /// </summary>
    (int X, int Z)[] _cells = [];

    /// <summary>
    /// Indexes the last tick's targets and yields (<paramref name="read"/>) of the characters alive in <paramref name="table"/>'s previous state,
    /// with their cells in <paramref name="grid"/> (serial).
    /// </summary>
    public void Build(CombatSlot[] read, CharacterTable table, SpatialGrid grid)
    {
        int n = table.HighWater;
        var prev = table.Previous;
        if (_cells.Length < n) _cells = new (int, int)[Math.Max(n, _cells.Length * 2)];
        if (_attackers.Length < n)
        {
            _attackers = new (int, int, int, int)[_cells.Length];
            _gathered = new (int, int, int, int)[_cells.Length];
            _yielders = new (int, int)[_cells.Length];
        }
        if (_attackersAt.Length < n + 1)
        {
            _attackersAt = new int[_cells.Length + 1];
            _yieldersAt = new int[_cells.Length + 1];
        }
        _attackerCount = _yielderCount = 0;
        _size = n;
        Array.Clear(_attackersAt, 0, n + 1);
        for (int o = 0; o < n && o < read.Length; o++)
        {
            if (!prev[o].Alive) continue;
            var cell = _cells[o] = grid.Cell(prev[o].Position);
            ref readonly var s = ref read[o];
            if ((uint)s.TargetSlot < (uint)n)
            {
                _gathered[_attackerCount++] = (s.TargetSlot, cell.X, cell.Z, o);
                _attackersAt[s.TargetSlot + 1]++;
            }
            if ((uint)s.YieldTo < (uint)n) _yielders[_yielderCount++] = (s.YieldTo, o);
        }

        // Sorted as whole tuples (unique, the slot is in each, so the order is total), done as a bucket per target and each bucket sorted by cell and
        // slot: a crowd round few targets made the tuple sort a large part of the tick.
        for (int t = 0; t < n; t++) _attackersAt[t + 1] += _attackersAt[t];
        if (_fill.Length < n) _fill = new int[_attackersAt.Length];
        Array.Copy(_attackersAt, _fill, n);
        for (int k = 0; k < _attackerCount; k++) _attackers[_fill[_gathered[k].Target]++] = _gathered[k];
        for (int t = 0; t < n; t++)
            if (_attackersAt[t + 1] - _attackersAt[t] > 1)
                _attackers.AsSpan(_attackersAt[t], _attackersAt[t + 1] - _attackersAt[t]).Sort(static (a, b) =>
                    a.X != b.X ? a.X.CompareTo(b.X) : a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.Slot.CompareTo(b.Slot));
        Array.Sort(_yielders, 0, _yielderCount);
        int y = 0;
        for (int t = 0; t <= n; t++)
        {
            while (y < _yielderCount && _yielders[y].Target < t) y++;
            _yieldersAt[t] = y;
        }
    }

    /// <summary>
    /// The attackers in slot order before the bucket sort (scratch of <see cref="Build"/>).
    /// </summary>
    (int Target, int X, int Z, int Slot)[] _gathered = [];
    /// <summary>
    /// Each target's next free entry in <see cref="_attackers"/> while the buckets fill (scratch of <see cref="Build"/>).
    /// </summary>
    int[] _fill = [];

    /// <summary>
    /// The characters whose target is <paramref name="t"/>, in the grid's order, with their cells.
    /// </summary>
    public ReadOnlySpan<(int Target, int X, int Z, int Slot)> AttackersOf(int t) =>
        (uint)t < (uint)_size ? _attackers.AsSpan(_attackersAt[t], _attackersAt[t + 1] - _attackersAt[t]) : [];

    /// <summary>
    /// The characters yielding to <paramref name="t"/>, by slot.
    /// </summary>
    public ReadOnlySpan<(int Target, int Slot)> YieldingTo(int t) =>
        (uint)t < (uint)_size ? _yielders.AsSpan(_yieldersAt[t], _yieldersAt[t + 1] - _yieldersAt[t]) : [];

    /// <summary>
    /// The grid cell of a listed character as of the build.
    /// </summary>
    public (int X, int Z) CellOf(int slot) => _cells[slot];
}