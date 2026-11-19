using System.Buffers;
using System.Numerics;
using DotRecast.Recast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The cell grid of a tile's heightfield (Recast's rcHeightfield without its spans): <see cref="Width"/> by <see cref="Height"/> columns of
/// <see cref="CellSize"/> from <see cref="Min"/>, cells <see cref="CellHeight"/> high up to <see cref="Max"/>, the outer <see cref="Border"/>
/// columns the neighbouring tiles'.
/// </summary>
internal readonly record struct NavTileGrid(int Width, int Height, Vector3 Min, Vector3 Max, float CellSize, float CellHeight, int Border);

/// <summary>
/// The solid spans of a tile's heightfield, column after column and upward within a column, with Recast's two span filters the tile builder
/// uses (adapted from DotRecast's <c>RcFilters</c>, zlib licence, THIRD_PARTY_NOTICES.md). Filled a column at a time; the arrays are rented from
/// the shared pool (docs/simulation.md "Navmesh").
/// </summary>
internal sealed class NavTileSpans : IDisposable
{
    /// <summary>
    /// Where each column's spans start in <see cref="_spans"/>, and after the last column where they end (rented: longer than that).
    /// </summary>
    readonly int[] _starts;

    /// <summary>
    /// The spans (rented: longer than <see cref="Count"/>).
    /// </summary>
    readonly NavSpan[] _spans;

    /// <summary>
    /// The columns ended so far.
    /// </summary>
    int _columns;

    /// <summary>
    /// No spans yet, with room for <paramref name="capacity"/> in the columns of <paramref name="grid"/>.
    /// </summary>
    public NavTileSpans(in NavTileGrid grid, int capacity)
    {
        Grid = grid;
        _starts = ArrayPool<int>.Shared.Rent(grid.Width * grid.Height + 1);
        _spans = ArrayPool<NavSpan>.Shared.Rent(capacity);
        _starts[0] = 0;
    }

    /// <summary>
    /// The heightfield's cell grid.
    /// </summary>
    public NavTileGrid Grid { get; }

    /// <summary>
    /// The spans added.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// The spans of <paramref name="column"/> (x + z * width), lowest first.
    /// </summary>
    public Span<NavSpan> Column(int column) => _spans.AsSpan(_starts[column], _starts[column + 1] - _starts[column]);

    /// <summary>
    /// Adds a span over the ones already in the column being filled.
    /// </summary>
    public void Add(NavSpan span) => _spans[Count++] = span;

    /// <summary>
    /// Ends the column being filled; the next span starts the column after it.
    /// </summary>
    public void EndColumn() => _starts[++_columns] = Count;

    /// <summary>
    /// Gives a cutting span the area of the walkable span under it when its top is within <paramref name="climb"/> of that one's (Recast's
    /// rcFilterLowHangingWalkableObstacles).
    /// </summary>
    public void LiftLowObstacles(int climb)
    {
        for (int column = 0; column < _columns; column++)
        {
            var spans = Column(column);
            bool belowWalkable = false;
            int belowArea = RcRecast.RC_NULL_AREA;
            for (int i = 0; i < spans.Length; i++)
            {
                bool walkable = spans[i].Area != RcRecast.RC_NULL_AREA;
                if (!walkable && belowWalkable && spans[i].Max - spans[i - 1].Max <= climb) spans[i].Area = belowArea;

                belowWalkable = walkable;
                belowArea = spans[i].Area;
            }
        }
    }

    /// <summary>
    /// Takes the area from every span with less than <paramref name="height"/> free over it (Recast's rcFilterWalkableLowHeightSpans).
    /// </summary>
    public void DropLowHeadroom(int height)
    {
        for (int column = 0; column < _columns; column++)
        {
            var spans = Column(column);
            for (int i = 0; i < spans.Length; i++)
            {
                int ceiling = i + 1 < spans.Length ? spans[i + 1].Min : RcRecast.RC_SPAN_MAX_HEIGHT;
                if (ceiling - spans[i].Max < height) spans[i].Area = RcRecast.RC_NULL_AREA;
            }
        }
    }

    /// <summary>
    /// Returns the arrays to the pool; the spans are not used after.
    /// </summary>
    public void Dispose()
    {
        ArrayPool<int>.Shared.Return(_starts);
        ArrayPool<NavSpan>.Shared.Return(_spans);
    }
}
