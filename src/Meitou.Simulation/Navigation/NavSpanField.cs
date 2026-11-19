using System.Buffers;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// A solid span of a heightfield column, in height cells over the field's base.
/// </summary>
internal struct NavSpan
{
    /// <summary>
    /// The span's bottom.
    /// </summary>
    public int Min;

    /// <summary>
    /// The span's top.
    /// </summary>
    public int Max;

    /// <summary>
    /// The area of the span's top (<see cref="NavArea"/>).
    /// </summary>
    public int Area;
}

/// <summary>
/// The solid spans of a tile's columns while triangles are rasterized into them: each column a list sorted upward, a new span merged with the ones
/// it overlaps or touches. The merge is Recast's rcAddSpan, adapted from DotRecast's <c>RcRasterizations.AddSpan</c> (zlib licence,
/// THIRD_PARTY_NOTICES.md), which allocates an object per rasterized span; here the spans live in arrays rented from the shared pool and a merged
/// span's slot is taken again (docs/simulation.md "Navmesh").
/// </summary>
internal sealed class NavSpanField : IDisposable
{
    /// <summary>
    /// The index that ends a list.
    /// </summary>
    public const int None = -1;

    /// <summary>
    /// The first span of each column, <see cref="None"/> for an empty one (rented: longer than the columns).
    /// </summary>
    readonly int[] _heads;

    /// <summary>
    /// The spans, in the order they were taken (rented).
    /// </summary>
    NavSpan[] _spans;

    /// <summary>
    /// The span after each span in its column, or after a free slot in the free list (rented).
    /// </summary>
    int[] _next;

    /// <summary>
    /// The slots of <see cref="_spans"/> taken so far, free ones among them.
    /// </summary>
    int _used;

    /// <summary>
    /// The first free slot, <see cref="None"/> when there is none.
    /// </summary>
    int _free = None;

    /// <summary>
    /// An empty field of <paramref name="columns"/> columns.
    /// </summary>
    public NavSpanField(int columns)
    {
        _heads = ArrayPool<int>.Shared.Rent(columns);
        _spans = ArrayPool<NavSpan>.Shared.Rent(columns);
        _next = ArrayPool<int>.Shared.Rent(_spans.Length);
        _heads.AsSpan(0, columns).Fill(None);
    }

    /// <summary>
    /// The spans in the columns now.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// The lowest span of <paramref name="column"/>, <see cref="None"/> when it is empty.
    /// </summary>
    public int First(int column) => _heads[column];

    /// <summary>
    /// The span over <paramref name="span"/> in its column, <see cref="None"/> over the highest.
    /// </summary>
    public int Next(int span) => _next[span];

    /// <summary>
    /// The span at an index <see cref="First"/> or <see cref="Next"/> gave.
    /// </summary>
    public ref readonly NavSpan this[int span] => ref _spans[span];

    /// <summary>
    /// Adds the span <paramref name="min"/>..<paramref name="max"/> with <paramref name="area"/> to <paramref name="column"/>, merged with every
    /// span of the column it overlaps or touches. A merged-in span whose top is within <paramref name="mergeThreshold"/> of the merged top (the
    /// new span is raised to it first) gives the higher of the two areas.
    /// </summary>
    public void Add(int column, int min, int max, int area, int mergeThreshold)
    {
        int previous = None;
        int current = _heads[column];
        while (current != None && _spans[current].Min <= max)
        {
            int next = _next[current];
            if (_spans[current].Max < min)
            {
                previous = current;
                current = next;
                continue;
            }

            // The span overlaps or touches the new one: it is taken out of the column and into the new one.
            var merged = _spans[current];
            min = Math.Min(min, merged.Min);
            max = Math.Max(max, merged.Max);
            if (Math.Abs(max - merged.Max) <= mergeThreshold) area = Math.Max(area, merged.Area);

            Unlink(column, previous, next);
            Release(current);
            current = next;
        }

        Insert(column, previous, new NavSpan { Min = min, Max = max, Area = area });
    }

    /// <summary>
    /// Returns the arrays to the pool; the field is not used after.
    /// </summary>
    public void Dispose()
    {
        ArrayPool<int>.Shared.Return(_heads);
        ArrayPool<NavSpan>.Shared.Return(_spans);
        ArrayPool<int>.Shared.Return(_next);
    }

    /// <summary>
    /// Makes <paramref name="next"/> follow <paramref name="previous"/> in <paramref name="column"/> (the column's first span when
    /// <paramref name="previous"/> is <see cref="None"/>).
    /// </summary>
    void Unlink(int column, int previous, int next)
    {
        if (previous != None)
            _next[previous] = next;
        else
            _heads[column] = next;
    }

    /// <summary>
    /// Puts <paramref name="span"/> into <paramref name="column"/> after <paramref name="previous"/> (first when that is <see cref="None"/>).
    /// </summary>
    void Insert(int column, int previous, NavSpan span)
    {
        int slot = Take();
        _spans[slot] = span;
        if (previous != None)
        {
            _next[slot] = _next[previous];
            _next[previous] = slot;
        }
        else
        {
            _next[slot] = _heads[column];
            _heads[column] = slot;
        }

        Count++;
    }

    /// <summary>
    /// A slot for a span: a free one, else the next unused, the arrays grown when they are full.
    /// </summary>
    int Take()
    {
        if (_free != None)
        {
            int slot = _free;
            _free = _next[slot];
            return slot;
        }

        if (_used == Math.Min(_spans.Length, _next.Length)) Grow();

        return _used++;
    }

    /// <summary>
    /// Puts the slot of a span taken out of its column on the free list.
    /// </summary>
    void Release(int slot)
    {
        _next[slot] = _free;
        _free = slot;
        Count--;
    }

    /// <summary>
    /// Moves the spans and their links into arrays twice as long.
    /// </summary>
    void Grow()
    {
        var spans = ArrayPool<NavSpan>.Shared.Rent(_used * 2);
        var next = ArrayPool<int>.Shared.Rent(_used * 2);
        _spans.AsSpan(0, _used).CopyTo(spans);
        _next.AsSpan(0, _used).CopyTo(next);
        ArrayPool<NavSpan>.Shared.Return(_spans);
        ArrayPool<int>.Shared.Return(_next);
        _spans = spans;
        _next = next;
    }
}
