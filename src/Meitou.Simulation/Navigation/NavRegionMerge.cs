using DotRecast.Recast;
using static DotRecast.Recast.RcRecast;
using static Meitou.Simulation.Navigation.NavTileRegions;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Filters and merges tile regions before contour generation. Adapted from DotRecast's RcRegions to reuse
/// the region records and their adjacency lists (zlib licence, THIRD_PARTY_NOTICES.md).
/// </summary>
internal sealed class NavRegionMerge
{
    /// <summary>
    /// The region records and work lists owned by the current tile-building thread.
    /// </summary>
    [ThreadStatic]
    static NavRegionMerge? s_scratch;

    /// <summary>
    /// Records indexed by original region ID. Only the active prefix belongs to the current tile.
    /// </summary>
    readonly List<Region> _regions = new();

    /// <summary>
    /// The DFS stack for finding small connected components.
    /// </summary>
    readonly List<int> _stack = new(32);

    /// <summary>
    /// The regions reached in the current component, in visit order.
    /// </summary>
    readonly List<int> _component = new(32);

    /// <summary>
    /// The target region's contour connections while its list is being rebuilt during a merge.
    /// </summary>
    readonly List<int> _mergeConnections = new();

    /// <summary>
    /// The attributes and neighbours of one region.
    /// </summary>
    sealed class Region
    {
        /// <summary>
        /// The current ID after any merging or renumbering.
        /// </summary>
        public int Id;

        /// <summary>
        /// The number of spans in the region.
        /// </summary>
        public int SpanCount;

        /// <summary>
        /// The walkable area type.
        /// </summary>
        public int Area;

        /// <summary>
        /// Whether a component walk has visited this region.
        /// </summary>
        public bool Visited;

        /// <summary>
        /// Whether this region contains multiple spans in a column.
        /// </summary>
        public bool Overlap;

        /// <summary>
        /// Whether renumbering still needs to assign this region a compact ID.
        /// </summary>
        public bool Remap;

        /// <summary>
        /// The region IDs encountered around the contour, in winding order.
        /// </summary>
        public readonly List<int> Connections = new();

        /// <summary>
        /// The unique region IDs it may not merge with: of spans in the same columns, and of spans beside it across an unconnected edge (a step).
        /// </summary>
        public readonly List<int> Apart = new();

        /// <summary>
        /// Clears the record for a tile while retaining the lists' storage.
        /// </summary>
        public void Reset(int id)
        {
            Id = id;
            SpanCount = 0;
            Area = 0;
            Visited = false;
            Overlap = false;
            Remap = false;
            Connections.Clear();
            Apart.Clear();
        }
    }

    /// <summary>
    /// Finds region connections, removes small components, merges neighbours and renumbers the remaining regions.
    /// </summary>
    public static int Filter(RcCompactHeightfield field, Span<int> ids, int nextId, int minArea, int mergeArea, out int overlaps)
    {
        var scratch = s_scratch ??= new NavRegionMerge();
        int count = nextId + 1;
        scratch.ResetRegions(count);
        scratch.BuildGraph(field, ids, count);
        scratch.RemoveSmallComponents(count, minArea);
        scratch.MergeSmallRegions(count, mergeArea);
        int maxId = scratch.CompactIds(count);
        scratch.RemapSpans(ids);
        overlaps = scratch.CountOverlaps(count);
        return maxId;
    }

    /// <summary>
    /// Ensures that every region ID in this tile has a clean record.
    /// </summary>
    void ResetRegions(int count)
    {
        while (_regions.Count < count)
            _regions.Add(new Region());

        for (int i = 0; i < count; i++)
            _regions[i].Reset(i);
    }

    /// <summary>
    /// Collects each region's span count, the regions it may not merge with and its ordered contour neighbours.
    /// </summary>
    void BuildGraph(RcCompactHeightfield field, ReadOnlySpan<int> ids, int count)
    {
        for (int y = 0; y < field.height; y++)
        {
            for (int x = 0; x < field.width; x++)
            {
                var cell = field.cells[x + y * field.width];
                int end = cell.index + cell.count;
                for (int i = cell.index; i < end; i++)
                {
                    int id = ids[i];
                    if (id == 0 || id >= count) continue;

                    var region = _regions[id];
                    region.SpanCount++;
                    CollectFloors(ids, cell.index, end, i, count, region);
                    CollectBeside(field, ids, x, y, i, count, region);
                    if (region.Connections.Count > 0) continue;

                    region.Area = field.areas[i];
                    FindContour(field, ids, x, y, i, region.Connections);
                }
            }
        }
    }

    /// <summary>
    /// Records regions above and below a span, including its own ID when it overlaps itself.
    /// </summary>
    static void CollectFloors(ReadOnlySpan<int> ids, int start, int end, int index, int count, Region region)
    {
        for (int j = start; j < end; j++)
        {
            int floor = ids[j];
            if (j == index || floor == 0 || floor >= count) continue;

            if (floor == region.Id) region.Overlap = true;

            AddApart(region, floor);
        }
    }

    /// <summary>
    /// Records the other regions of the spans beside a span across its unconnected edges: merged, the two would meet at the step between them
    /// (<see cref="NavWatershed.BesideOwn"/>).
    /// </summary>
    static void CollectBeside(RcCompactHeightfield field, ReadOnlySpan<int> ids, int x, int y, int index, int count, Region region)
    {
        for (int dir = 0; dir < 4; dir++)
        {
            if (GetCon(field.spans[index], dir) != RC_NOT_CONNECTED) continue;

            int nextX = x + GetDirOffsetX(dir);
            int nextY = y + GetDirOffsetY(dir);
            if (nextX < 0 || nextY < 0 || nextX >= field.width || nextY >= field.height) continue;

            var cell = field.cells[nextX + nextY * field.width];
            for (int j = cell.index, end = cell.index + cell.count; j < end; j++)
            {
                int beside = ids[j];
                if (beside != 0 && beside < count && beside != region.Id) AddApart(region, beside);
            }
        }
    }

    /// <summary>
    /// Starts the contour walk at the first edge bordering a different region.
    /// </summary>
    static void FindContour(RcCompactHeightfield field, ReadOnlySpan<int> ids, int x, int y, int index, List<int> connections)
    {
        for (int dir = 0; dir < 4; dir++)
        {
            int neighbour = RegionAcross(field, ids, x, y, index, dir);
            if (neighbour == ids[index]) continue;

            WalkContour(field, ids, x, y, index, dir, neighbour, connections);
            return;
        }
    }

    /// <summary>
    /// Records the region IDs encountered along the contour, rotating clockwise at a boundary and counterclockwise after crossing an inner edge.
    /// </summary>
    static void WalkContour(RcCompactHeightfield field, ReadOnlySpan<int> ids, int x, int y, int index, int dir, int neighbour,
        List<int> connections)
    {
        int startIndex = index;
        int startDir = dir;
        connections.Add(neighbour);

        for (int step = 1; step < 40000; step++)
        {
            int next = Neighbour(field, x, y, index, dir);
            int across = next < 0 ? 0 : ids[next];
            if (across != ids[index])
            {
                if (across != neighbour)
                {
                    connections.Add(across);
                    neighbour = across;
                }

                dir = (dir + 1) & 3;
            }
            else
            {
                if (next < 0) return;

                x += GetDirOffsetX(dir);
                y += GetDirOffsetY(dir);
                index = next;
                dir = (dir + 3) & 3;
            }

            if (index == startIndex && dir == startDir) break;
        }

        RemoveAdjacentDuplicates(connections);
    }

    /// <summary>
    /// The region across a span's edge, with zero for an unconnected edge.
    /// </summary>
    static int RegionAcross(RcCompactHeightfield field, ReadOnlySpan<int> ids, int x, int y, int index, int dir)
    {
        int next = Neighbour(field, x, y, index, dir);
        return next < 0 ? 0 : ids[next];
    }

    /// <summary>
    /// Erases connected components below the minimum area unless they meet the tile border.
    /// </summary>
    void RemoveSmallComponents(int count, int minArea)
    {
        for (int i = 0; i < count; i++)
        {
            var region = _regions[i];
            if (!IsInterior(region.Id) || region.SpanCount == 0 || region.Visited) continue;

            int area = TraceComponent(i, out bool touchesBorder);
            if (area >= minArea || touchesBorder) continue;

            foreach (int index in _component)
            {
                _regions[index].SpanCount = 0;
                _regions[index].Id = 0;
            }
        }
    }

    /// <summary>
    /// Traces a component in stack order, returning its span count and whether it meets a painted border.
    /// </summary>
    int TraceComponent(int start, out bool touchesBorder)
    {
        int area = 0;
        touchesBorder = false;
        _component.Clear();
        _stack.Clear();
        _stack.Add(start);
        _regions[start].Visited = true;

        while (_stack.Count > 0)
        {
            int index = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            _component.Add(index);

            var region = _regions[index];
            area += region.SpanCount;
            foreach (int id in region.Connections)
            {
                if ((id & RC_BORDER_REG) != 0)
                {
                    touchesBorder = true;
                    continue;
                }

                var next = _regions[id];
                if (next.Visited || !IsInterior(next.Id)) continue;

                _stack.Add(next.Id);
                next.Visited = true;
            }
        }

        return area;
    }

    /// <summary>
    /// Merges eligible regions into their smallest compatible neighbours until a pass makes no changes.
    /// </summary>
    void MergeSmallRegions(int count, int mergeArea)
    {
        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < count; i++)
            {
                var region = _regions[i];
                if (!IsInterior(region.Id) || region.Overlap || region.SpanCount == 0) continue;

                // A zero neighbour marks a contour touching unwalkable space.
                if (region.SpanCount > mergeArea && region.Connections.Contains(0)) continue;

                int targetId = FindMergeTarget(region);
                if (targetId == region.Id || !MergeInto(_regions[targetId], region)) continue;

                ReplaceMergedId(count, region.Id, targetId);
                merged = true;
            }
        }
        while (merged);
    }

    /// <summary>
    /// Finds the smallest compatible neighbour, keeping the first in contour order when sizes tie.
    /// </summary>
    int FindMergeTarget(Region region)
    {
        int smallest = 0xfffffff;
        int target = region.Id;
        foreach (int id in region.Connections)
        {
            if ((id & RC_BORDER_REG) != 0) continue;

            var next = _regions[id];
            if (!IsInterior(next.Id) || next.Overlap || next.SpanCount >= smallest) continue;

            if (!CanMerge(region, next) || !CanMerge(next, region)) continue;

            smallest = next.SpanCount;
            target = next.Id;
        }

        return target;
    }

    /// <summary>
    /// Allows a merge across one shared boundary when the area types agree and neither region is kept apart from the other.
    /// </summary>
    static bool CanMerge(Region region, Region other)
    {
        if (region.Area != other.Area) return false;

        int contacts = 0;
        foreach (int id in region.Connections)
            if (id == other.Id && ++contacts > 1) return false;

        return !region.Apart.Contains(other.Id);
    }

    /// <summary>
    /// Splices the two contours at their shared edge and combines their floor lists and span counts.
    /// </summary>
    bool MergeInto(Region target, Region source)
    {
        int targetEdge = target.Connections.IndexOf(source.Id);
        int sourceEdge = source.Connections.IndexOf(target.Id);
        if (targetEdge < 0 || sourceEdge < 0) return false;

        _mergeConnections.Clear();
        _mergeConnections.AddRange(target.Connections);
        target.Connections.Clear();
        AppendContour(target.Connections, _mergeConnections, targetEdge);
        AppendContour(target.Connections, source.Connections, sourceEdge);
        RemoveAdjacentDuplicates(target.Connections);

        foreach (int id in source.Apart)
            AddApart(target, id);

        target.SpanCount += source.SpanCount;
        source.SpanCount = 0;
        source.Connections.Clear();
        return true;
    }

    /// <summary>
    /// Appends a contour in winding order, starting just after the shared edge and skipping that edge.
    /// </summary>
    static void AppendContour(List<int> target, List<int> source, int edge)
    {
        for (int i = 0; i < source.Count - 1; i++)
            target.Add(source[(edge + 1 + i) % source.Count]);
    }

    /// <summary>
    /// Updates regions that already shared the old ID and all their references to that ID.
    /// </summary>
    void ReplaceMergedId(int count, int oldId, int newId)
    {
        for (int i = 0; i < count; i++)
        {
            var region = _regions[i];
            if (!IsInterior(region.Id)) continue;

            if (region.Id == oldId) region.Id = newId;

            bool changed = ReplaceId(region.Connections, oldId, newId);
            ReplaceId(region.Apart, oldId, newId);
            if (changed) RemoveAdjacentDuplicates(region.Connections);
        }
    }

    /// <summary>
    /// Replaces every occurrence without reordering the list.
    /// </summary>
    static bool ReplaceId(List<int> ids, int oldId, int newId)
    {
        bool changed = false;
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] != oldId) continue;

            ids[i] = newId;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Removes adjacent duplicates from a circular contour without changing its winding order.
    /// </summary>
    static void RemoveAdjacentDuplicates(List<int> connections)
    {
        for (int i = 0; i < connections.Count && connections.Count > 1;)
        {
            int next = (i + 1) % connections.Count;
            if (connections[i] == connections[next]) connections.RemoveAt(i);
            else i++;
        }
    }

    /// <summary>
    /// Records a region kept apart once, preserving the order in which they were found.
    /// </summary>
    static void AddApart(Region region, int id)
    {
        if (!region.Apart.Contains(id)) region.Apart.Add(id);
    }

    /// <summary>
    /// Assigns dense IDs in order of the first original record belonging to each merged region.
    /// </summary>
    int CompactIds(int count)
    {
        for (int i = 0; i < count; i++)
            _regions[i].Remap = IsInterior(_regions[i].Id);

        int maxId = 0;
        for (int i = 0; i < count; i++)
        {
            if (!_regions[i].Remap) continue;

            int oldId = _regions[i].Id;
            int newId = ++maxId;
            for (int j = i; j < count; j++)
            {
                var region = _regions[j];
                if (region.Id != oldId) continue;

                region.Id = newId;
                region.Remap = false;
            }
        }

        return maxId;
    }

    /// <summary>
    /// Copies the final IDs to spans, leaving the painted border IDs intact.
    /// </summary>
    void RemapSpans(Span<int> ids)
    {
        for (int i = 0; i < ids.Length; i++)
            if ((ids[i] & RC_BORDER_REG) == 0) ids[i] = _regions[ids[i]].Id;
    }

    /// <summary>
    /// Counts the original region records flagged as overlapping for the watershed diagnostic.
    /// </summary>
    int CountOverlaps(int count)
    {
        int overlaps = 0;
        for (int i = 0; i < count; i++)
            if (_regions[i].Overlap) overlaps++;

        return overlaps;
    }

    /// <summary>
    /// Whether the ID denotes a region distinct from both the null region and the painted tile border.
    /// </summary>
    static bool IsInterior(int id) => id != 0 && (id & RC_BORDER_REG) == 0;
}
