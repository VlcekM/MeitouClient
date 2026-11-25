using DotRecast.Recast;
using static DotRecast.Recast.RcRecast;
using static Meitou.Simulation.Navigation.NavTileRegions;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Grows watershed regions from high points in the distance field. Adapted from DotRecast's RcRegions to reuse
/// the level buckets, flood stack and expansion updates (zlib licence, THIRD_PARTY_NOTICES.md).
/// </summary>
internal sealed class NavWatershed
{
    /// <summary>
    /// The number of distance levels collected in each scan, with two units of distance per level.
    /// </summary>
    const int LevelCount = 8;

    /// <summary>
    /// The expansion limit per level above zero, in rounds of simultaneous assignments.
    /// </summary>
    const int ExpansionRounds = 8;

    /// <summary>
    /// The scratch lists owned by the current tile-building thread.
    /// </summary>
    [ThreadStatic]
    static NavWatershed? s_scratch;

    /// <summary>
    /// Unassigned spans grouped by the level at which they become eligible for expansion.
    /// </summary>
    readonly List<RcLevelStackEntry>[] _levels = CreateLevels();

    /// <summary>
    /// The stack for flooding a new region, also used for the last expansion over unassigned spans.
    /// </summary>
    readonly List<RcLevelStackEntry> _stack = new(256);

    /// <summary>
    /// Assignments to apply after evaluating every span in one expansion round.
    /// </summary>
    readonly List<RcDirtyEntry> _updates = new();

    /// <summary>
    /// The bucket position and span of each entry of <see cref="_updates"/>, to put back one the round cannot assign.
    /// </summary>
    readonly List<(int Position, RcLevelStackEntry Span)> _updated = new();

    /// <summary>
    /// Fills the interior of a tile with watershed regions and returns the next unused region ID.
    /// </summary>
    public static int Partition(RcCompactHeightfield field, Span<int> regions, ReadOnlySpan<int> distance,
        Span<int> regionDistance, int maxDistance, int nextId)
    {
        regionDistance.Clear();
        var scratch = s_scratch ??= new NavWatershed();
        return scratch.FillLevels(field, regions, distance, regionDistance, (maxDistance + 1) & ~1, nextId);
    }

    /// <summary>
    /// Allocates the level buckets once for a worker thread.
    /// </summary>
    static List<RcLevelStackEntry>[] CreateLevels()
    {
        var levels = new List<RcLevelStackEntry>[LevelCount];
        for (int i = 0; i < levels.Length; i++)
            levels[i] = new List<RcLevelStackEntry>(256);

        return levels;
    }

    /// <summary>
    /// Expands and seeds regions at each descending level, then expands into the remaining unassigned spans.
    /// </summary>
    int FillLevels(RcCompactHeightfield field, Span<int> regions, ReadOnlySpan<int> distance, Span<int> regionDistance,
        int level, int nextId)
    {
        int bucket = -1;
        while (level > 0)
        {
            level -= 2;
            bucket = (bucket + 1) & (LevelCount - 1);

            if (bucket == 0) SortByLevel(field, regions, distance, level);
            else AppendUnassigned(_levels[bucket - 1], _levels[bucket], regions);

            var spans = _levels[bucket];
            MarkAssigned(spans, regions);
            Expand(field, regions, regionDistance, spans, level);

            foreach (var span in spans)
            {
                if (span.index < 0 || regions[span.index] != 0) continue;

                if (Flood(field, regions, distance, regionDistance, span, level, nextId)) nextId++;
            }
        }

        FindUnassigned(field, regions, _stack);
        Expand(field, regions, regionDistance, _stack, 0);
        return nextId;
    }

    /// <summary>
    /// Collects unassigned walkable spans by distance, in column and span order.
    /// </summary>
    void SortByLevel(RcCompactHeightfield field, ReadOnlySpan<int> regions, ReadOnlySpan<int> distance, int level)
    {
        int firstLevel = level >> 1;
        foreach (var bucket in _levels)
            bucket.Clear();

        for (int y = 0; y < field.height; y++)
        {
            for (int x = 0; x < field.width; x++)
            {
                var cell = field.cells[x + y * field.width];
                for (int i = cell.index; i < cell.index + cell.count; i++)
                {
                    if (field.areas[i] == RC_NULL_AREA || regions[i] != 0) continue;

                    int bucket = firstLevel - (distance[i] >> 1);
                    if (bucket < LevelCount) _levels[Math.Max(bucket, 0)].Add(new RcLevelStackEntry(x, y, i));
                }
            }
        }
    }

    /// <summary>
    /// Carries the preceding level's unassigned spans after the spans already in the next bucket.
    /// </summary>
    static void AppendUnassigned(List<RcLevelStackEntry> previous, List<RcLevelStackEntry> next, ReadOnlySpan<int> regions)
    {
        foreach (var span in previous)
            if (span.index >= 0 && regions[span.index] == 0) next.Add(span);
    }

    /// <summary>
    /// Skips spans already reached by flooding at an earlier level while keeping the bucket in order.
    /// </summary>
    static void MarkAssigned(List<RcLevelStackEntry> spans, ReadOnlySpan<int> regions)
    {
        for (int j = 0; j < spans.Count; j++)
        {
            var span = spans[j];
            if (regions[span.index] != 0) spans[j] = new RcLevelStackEntry(span.x, span.y, -1);
        }
    }

    /// <summary>
    /// Collects all walkable spans that still have no region for the final expansion at level zero.
    /// </summary>
    static void FindUnassigned(RcCompactHeightfield field, ReadOnlySpan<int> regions, List<RcLevelStackEntry> spans)
    {
        spans.Clear();
        for (int y = 0; y < field.height; y++)
        {
            for (int x = 0; x < field.width; x++)
            {
                var cell = field.cells[x + y * field.width];
                for (int i = cell.index; i < cell.index + cell.count; i++)
                    if (regions[i] == 0 && field.areas[i] != RC_NULL_AREA) spans.Add(new RcLevelStackEntry(x, y, i));
            }
        }
    }

    /// <summary>
    /// Expands existing regions in discrete rounds. At level zero it runs until no more spans can be assigned.
    /// </summary>
    void Expand(RcCompactHeightfield field, Span<int> regions, Span<int> regionDistance, List<RcLevelStackEntry> spans, int level)
    {
        if (spans.Count == 0) return;

        int rounds = level > 0 ? ExpansionRounds : int.MaxValue;
        for (int round = 0; round < rounds; round++)
        {
            _updates.Clear();
            _updated.Clear();
            for (int j = 0; j < spans.Count; j++)
            {
                var span = spans[j];
                if (span.index < 0) continue;

                var entry = FindExpansion(field, regions, regionDistance, span);
                if (entry.region == 0) continue;

                spans[j] = new RcLevelStackEntry(span.x, span.y, -1);
                _updates.Add(entry);
                _updated.Add((j, span));
            }

            if (ApplyUpdates(field, regions, regionDistance, spans) == 0) return;
        }
    }

    /// <summary>
    /// Assigns the round's expansions in order, and returns how many it made. One that would now lie beside its region at a step
    /// (<see cref="BesideOwn"/>: a span beside it took the region earlier in the round) is put back in <paramref name="spans"/> for a later round.
    /// </summary>
    int ApplyUpdates(RcCompactHeightfield field, Span<int> regions, Span<int> regionDistance, List<RcLevelStackEntry> spans)
    {
        int applied = 0;
        for (int k = 0; k < _updates.Count; k++)
        {
            var entry = _updates[k];
            var (j, span) = _updated[k];
            if (BesideOwn(field, regions, span, entry.region))
            {
                spans[j] = span;
                continue;
            }

            regions[entry.index] = entry.region;
            regionDistance[entry.index] = entry.distance2;
            applied++;
        }

        return applied;
    }

    /// <summary>
    /// Chooses the nearest same-area region, keeping the first direction at an equal distance.
    /// </summary>
    static RcDirtyEntry FindExpansion(RcCompactHeightfield field, ReadOnlySpan<int> regions, ReadOnlySpan<int> regionDistance,
        RcLevelStackEntry span)
    {
        int area = field.areas[span.index];
        int region = regions[span.index];
        int best = 0xffff;
        for (int dir = 0; dir < 4; dir++)
        {
            int next = Neighbour(field, span.x, span.y, span.index, dir);
            if (next < 0 || field.areas[next] != area) continue;

            int candidate = regions[next];
            if (candidate <= 0 || (candidate & RC_BORDER_REG) != 0 || regionDistance[next] + 2 >= best) continue;
            if (BesideOwn(field, regions, span, candidate)) continue;

            region = candidate;
            best = regionDistance[next] + 2;
        }

        return new RcDirtyEntry(span.index, region, best);
    }

    /// <summary>
    /// Floods an unassigned span and its reachable neighbours without letting the new region touch another interior region.
    /// </summary>
    bool Flood(RcCompactHeightfield field, Span<int> regions, ReadOnlySpan<int> distance, Span<int> regionDistance, RcLevelStackEntry seed,
        int level, int id)
    {
        int area = field.areas[seed.index];
        int threshold = Math.Max(0, level - 2);
        bool found = false;
        _stack.Clear();
        PushFlood(regions, regionDistance, seed, id);
        while (_stack.Count > 0)
        {
            var span = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            if (TouchesOtherRegion(field, regions, span, area, id) || BesideOwn(field, regions, span, id))
            {
                regions[span.index] = 0;
                continue;
            }

            found = true;
            for (int dir = 0; dir < 4; dir++)
            {
                int next = Neighbour(field, span.x, span.y, span.index, dir);
                if (next < 0 || field.areas[next] != area || distance[next] < threshold || regions[next] != 0) continue;

                var neighbour = new RcLevelStackEntry(span.x + GetDirOffsetX(dir), span.y + GetDirOffsetY(dir), next);
                PushFlood(regions, regionDistance, neighbour, id);
            }
        }

        return found;
    }

    /// <summary>
    /// Assigns a span to the flood before pushing it so other spans cannot enqueue it again.
    /// </summary>
    void PushFlood(Span<int> regions, Span<int> regionDistance, RcLevelStackEntry span, int id)
    {
        regions[span.index] = id;
        regionDistance[span.index] = 0;
        _stack.Add(span);
    }

    /// <summary>
    /// Checks cardinal neighbours and diagonals for another region. A cardinal neighbour carrying a tile border ends
    /// the check in that direction before the diagonal is read.
    /// </summary>
    static bool TouchesOtherRegion(RcCompactHeightfield field, ReadOnlySpan<int> regions, RcLevelStackEntry span, int area, int id)
    {
        for (int dir = 0; dir < 4; dir++)
        {
            int next = Neighbour(field, span.x, span.y, span.index, dir);
            if (next < 0 || field.areas[next] != area) continue;

            int region = regions[next];
            if ((region & RC_BORDER_REG) != 0) continue;

            if (region != 0 && region != id) return true;

            int diagonalDir = (dir + 1) & 3;
            int diagonal = Neighbour(field, span.x + GetDirOffsetX(dir), span.y + GetDirOffsetY(dir), next, diagonalDir);
            if (diagonal < 0 || field.areas[diagonal] != area) continue;

            int diagonalRegion = regions[diagonal];
            if (diagonalRegion != 0 && diagonalRegion != id) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a span lies beside a span of <paramref name="region"/> across one of its unconnected edges: a step, such as the side of a stair ramp
    /// over the ground along it. Grown into the region, the region would hold both and meet itself at the step; its contour then has a slit there,
    /// which its polygons span (docs/game/pathfinding.md, "Own builder", steps inside a region).
    /// </summary>
    internal static bool BesideOwn(RcCompactHeightfield field, ReadOnlySpan<int> regions, RcLevelStackEntry span, int region)
    {
        for (int dir = 0; dir < 4; dir++)
        {
            if (GetCon(field.spans[span.index], dir) != RC_NOT_CONNECTED) continue;

            int x = span.x + GetDirOffsetX(dir);
            int y = span.y + GetDirOffsetY(dir);
            if (x < 0 || y < 0 || x >= field.width || y >= field.height) continue;

            var cell = field.cells[x + y * field.width];
            for (int i = cell.index, end = cell.index + cell.count; i < end; i++)
                if (regions[i] == region) return true;
        }

        return false;
    }
}
