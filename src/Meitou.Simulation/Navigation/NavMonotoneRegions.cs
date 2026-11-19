using DotRecast.Recast;
using static DotRecast.Recast.RcRecast;
using static Meitou.Simulation.Navigation.NavTileRegions;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Partitions a tile by sweeping one row at a time. Adapted from DotRecast's RcRegions to reuse
/// the row and predecessor buffers (zlib licence, THIRD_PARTY_NOTICES.md).
/// </summary>
internal sealed class NavMonotoneRegions
{
    /// <summary>
    /// The predecessor sentinel for a sweep with contacts in multiple regions.
    /// </summary>
    const int MultipleNeighbours = 0xffff;

    /// <summary>
    /// The buffers owned by the current tile-building thread.
    /// </summary>
    [ThreadStatic]
    static NavMonotoneRegions? s_scratch;

    /// <summary>
    /// Contacts with each region in the previous row, cleared before sweeping a row.
    /// </summary>
    int[] _previous = new int[1024];

    /// <summary>
    /// The sweeps numbered from one in the current row.
    /// </summary>
    Sweep[] _sweeps = [];

    /// <summary>
    /// A sweep's contacts with the previous row and the region assigned to it.
    /// </summary>
    struct Sweep
    {
        /// <summary>
        /// The only region touched in the previous row, zero before a contact, or the multiple-neighbour sentinel.
        /// </summary>
        public int NeighbourId;

        /// <summary>
        /// The number of contacts recorded with the sweep's predecessor.
        /// </summary>
        public int Contacts;

        /// <summary>
        /// The final region ID for this sweep.
        /// </summary>
        public int RegionId;
    }

    /// <summary>
    /// Fills the interior of a tile with monotone regions and returns the next unused region ID.
    /// </summary>
    public static int Partition(RcCompactHeightfield field, Span<int> regions, int nextId)
    {
        var scratch = s_scratch ??= new NavMonotoneRegions();
        return scratch.FillRows(field, regions, nextId);
    }

    /// <summary>
    /// Collects row-local sweeps, assigns their region IDs, and replaces the temporary IDs before the next row.
    /// </summary>
    int FillRows(RcCompactHeightfield field, Span<int> regions, int nextId)
    {
        int border = field.borderSize;
        int capacity = Math.Max(field.width, field.height);
        if (_sweeps.Length < capacity) Array.Resize(ref _sweeps, capacity);

        var sweeps = _sweeps.AsSpan(0, capacity);
        for (int y = border; y < field.height - border; y++)
        {
            if (_previous.Length < nextId) Array.Resize(ref _previous, nextId * 2);

            _previous.AsSpan(0, nextId).Clear();
            int sweepCount = SweepRow(field, regions, sweeps, y);
            nextId = AssignRegions(sweeps[..sweepCount], nextId);
            RemapRow(field, regions, sweeps[..sweepCount], y);
        }

        return nextId;
    }

    /// <summary>
    /// Scans one row from left to right and joins each span to its same-area left neighbour's sweep, if present.
    /// </summary>
    int SweepRow(RcCompactHeightfield field, Span<int> regions, Span<Sweep> sweeps, int y)
    {
        int nextSweep = 1;
        for (int x = field.borderSize; x < field.width - field.borderSize; x++)
        {
            var cell = field.cells[x + y * field.width];
            for (int i = cell.index; i < cell.index + cell.count; i++)
            {
                if (field.areas[i] == RC_NULL_AREA) continue;

                int sweep = InteriorNeighbour(field, regions, x, y, i, 0);
                if (sweep == 0)
                {
                    sweep = nextSweep++;
                    sweeps[sweep] = default;
                }

                int previous = InteriorNeighbour(field, regions, x, y, i, 3);
                if (previous != 0) AddContact(ref sweeps[sweep], previous);

                regions[i] = sweep;
            }
        }

        return nextSweep;
    }

    /// <summary>
    /// The same-area interior region across an edge, or zero if the edge has none.
    /// </summary>
    static int InteriorNeighbour(RcCompactHeightfield field, ReadOnlySpan<int> regions, int x, int y, int index, int direction)
    {
        int next = Neighbour(field, x, y, index, direction);
        if (next < 0 || field.areas[next] != field.areas[index]) return 0;

        int id = regions[next];
        return (id & RC_BORDER_REG) == 0 ? id : 0;
    }

    /// <summary>
    /// Records a contact while the sweep still has a single predecessor.
    /// </summary>
    void AddContact(ref Sweep sweep, int previous)
    {
        if (sweep.NeighbourId != 0 && sweep.NeighbourId != previous)
        {
            sweep.NeighbourId = MultipleNeighbours;
            return;
        }

        sweep.NeighbourId = previous;
        sweep.Contacts++;
        _previous[previous]++;
    }

    /// <summary>
    /// Extends a region only when all its contacts in this row are with the same sweep.
    /// </summary>
    int AssignRegions(Span<Sweep> sweeps, int nextId)
    {
        for (int i = 1; i < sweeps.Length; i++)
        {
            ref var sweep = ref sweeps[i];
            int previous = sweep.NeighbourId;
            bool extend = previous != MultipleNeighbours && previous != 0 && _previous[previous] == sweep.Contacts;
            sweep.RegionId = extend ? previous : nextId++;
        }

        return nextId;
    }

    /// <summary>
    /// Replaces the sweep numbers in the row with their assigned region IDs.
    /// </summary>
    static void RemapRow(RcCompactHeightfield field, Span<int> regions, ReadOnlySpan<Sweep> sweeps, int y)
    {
        for (int x = field.borderSize; x < field.width - field.borderSize; x++)
        {
            var cell = field.cells[x + y * field.width];
            for (int i = cell.index; i < cell.index + cell.count; i++)
            {
                int sweep = regions[i];
                if (sweep > 0 && sweep < sweeps.Length) regions[i] = sweeps[sweep].RegionId;
            }
        }
    }
}
