using System.Buffers;
using DotRecast.Core.Numerics;
using DotRecast.Recast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// A tile's compact heightfield (Recast's rcBuildCompactHeightfield: the open space over each walkable span, linked to the neighbouring
/// columns' it connects to), for DotRecast's partitioning. Adapted from DotRecast's <c>RcCompacts.BuildCompactHeightfield</c> (zlib licence,
/// THIRD_PARTY_NOTICES.md), which allocates a builder object per span; here one builder serves every span and the field's arrays are rented
/// from the shared pool, so they are longer than the field's counts and belong to it until it is disposed (docs/simulation.md "Navmesh").
/// </summary>
internal sealed class NavCompactField : IDisposable
{
    /// <summary>
    /// The highest layer of a column a span can connect to (Recast: RC_NOT_CONNECTED - 1).
    /// </summary>
    const int MaxLayers = RcRecast.RC_NOT_CONNECTED - 1;

    /// <summary>
    /// The one builder every span is made through (DotRecast's compact span has no other constructor).
    /// </summary>
    readonly RcCompactSpanBuilder _builder = RcCompactSpanBuilder.NewBuilder();

    /// <summary>
    /// The highest layer a connection would have needed; over <see cref="MaxLayers"/> the field is refused.
    /// </summary>
    int _maxLayer;

    /// <summary>
    /// The compact heightfield of <paramref name="spans"/>: two spans of neighbouring columns connect when the free space they share is
    /// <paramref name="walkableHeight"/> and their floors are within <paramref name="walkableClimb"/>.
    /// </summary>
    public NavCompactField(NavTileSpans spans, int walkableHeight, int walkableClimb)
    {
        var grid = spans.Grid;
        Field = new RcCompactHeightfield
        {
            width = grid.Width,
            height = grid.Height,
            borderSize = grid.Border,
            walkableHeight = walkableHeight,
            walkableClimb = walkableClimb,
            maxRegions = 0,
            bmin = new RcVec3f(grid.Min.X, grid.Min.Y, grid.Min.Z),
            bmax = new RcVec3f(grid.Max.X, grid.Max.Y + walkableHeight * grid.CellHeight, grid.Max.Z),
            cs = grid.CellSize,
            ch = grid.CellHeight,
            cells = ArrayPool<RcCompactCell>.Shared.Rent(grid.Width * grid.Height),
        };
        AddSpans(spans);
        Connect();
        if (_maxLayer > MaxLayers)
            throw new InvalidOperationException($"Compact heightfield: too many layers {_maxLayer} (max: {MaxLayers})");
    }

    /// <summary>
    /// The field. Its <c>cells</c>, <c>spans</c> and <c>areas</c> are rented: valid up to the field's own counts, and only until
    /// <see cref="Dispose"/>.
    /// </summary>
    public RcCompactHeightfield Field { get; }

    /// <summary>
    /// Sets the connection of span <paramref name="span"/> of <paramref name="field"/> in direction <paramref name="direction"/> to
    /// <paramref name="connection"/> through <paramref name="builder"/> (a compact span is rebuilt to change it).
    /// </summary>
    public static void SetConnection(RcCompactHeightfield field, RcCompactSpanBuilder builder, int span, int direction, int connection)
    {
        ref var s = ref field.spans[span];
        builder.y = s.y;
        builder.reg = s.reg;
        builder.con = s.con;
        builder.h = s.h;
        RcRecast.SetCon(builder, direction, connection);
        s = builder.Build();
    }

    /// <summary>
    /// Returns the field's arrays to the pool; the field is not used after.
    /// </summary>
    public void Dispose()
    {
        ArrayPool<RcCompactCell>.Shared.Return(Field.cells);
        ArrayPool<RcCompactSpan>.Shared.Return(Field.spans);
        ArrayPool<int>.Shared.Return(Field.areas);
        Field.cells = [];
        Field.spans = [];
        Field.areas = [];
    }

    /// <summary>
    /// Makes the field's cells, spans and areas from the walkable spans: each stands on its span's top and is open up to the next span's bottom.
    /// </summary>
    void AddSpans(NavTileSpans solid)
    {
        int walkable = 0;
        int columns = Field.width * Field.height;
        for (int c = 0; c < columns; c++)
        {
            foreach (ref readonly var span in solid.Column(c))
                if (span.Area != RcRecast.RC_NULL_AREA) walkable++;
        }

        Field.spanCount = walkable;
        Field.spans = ArrayPool<RcCompactSpan>.Shared.Rent(walkable);
        Field.areas = ArrayPool<int>.Shared.Rent(walkable);
        int count = 0;
        for (int c = 0; c < columns; c++)
        {
            var spans = solid.Column(c);
            int first = count;
            for (int i = 0; i < spans.Length; i++)
            {
                if (spans[i].Area == RcRecast.RC_NULL_AREA) continue;

                int top = i + 1 < spans.Length ? spans[i + 1].Min : RcRecast.RC_SPAN_MAX_HEIGHT;
                _builder.y = Math.Clamp(spans[i].Max, 0, RcRecast.RC_SPAN_MAX_HEIGHT);
                _builder.h = Math.Clamp(top - spans[i].Max, 0, RcRecast.RC_SPAN_MAX_HEIGHT);
                Field.spans[count] = _builder.Build();
                Field.areas[count++] = spans[i].Area;
            }

            // A column without spans has the cell DotRecast leaves it: index 0.
            Field.cells[c] = new RcCompactCell(spans.Length == 0 ? 0 : first, count - first);
        }
    }

    /// <summary>
    /// Links every span to the span it connects to in each of the four neighbouring columns.
    /// </summary>
    void Connect()
    {
        for (int z = 0; z < Field.height; z++)
        {
            for (int x = 0; x < Field.width; x++)
            {
                var cell = Field.cells[x + z * Field.width];
                for (int i = cell.index; i < cell.index + cell.count; i++)
                    ConnectSpan(x, z, i);
            }
        }
    }

    /// <summary>
    /// Sets the four connections of span <paramref name="i"/> of the column (<paramref name="x"/>, <paramref name="z"/>).
    /// </summary>
    void ConnectSpan(int x, int z, int i)
    {
        var span = Field.spans[i];
        _builder.y = span.y;
        _builder.h = span.h;
        _builder.con = 0;
        for (int direction = 0; direction < 4; direction++)
        {
            int nx = x + RcRecast.GetDirOffsetX(direction);
            int nz = z + RcRecast.GetDirOffsetY(direction);
            bool inside = nx >= 0 && nz >= 0 && nx < Field.width && nz < Field.height;
            int layer = inside ? Layer(span, Field.cells[nx + nz * Field.width]) : RcRecast.RC_NOT_CONNECTED;
            RcRecast.SetCon(_builder, direction, layer);
        }

        Field.spans[i] = _builder.Build();
    }

    /// <summary>
    /// The layer of the lowest span of <paramref name="neighbour"/> that <paramref name="span"/> connects to, <c>RC_NOT_CONNECTED</c> when it
    /// connects to none; a span over <see cref="MaxLayers"/> is passed over and recorded in <see cref="_maxLayer"/>.
    /// </summary>
    int Layer(RcCompactSpan span, RcCompactCell neighbour)
    {
        for (int k = neighbour.index; k < neighbour.index + neighbour.count; k++)
        {
            var other = Field.spans[k];
            int bottom = Math.Max(span.y, other.y);
            int top = Math.Min(span.y + span.h, other.y + other.h);
            if (top - bottom < Field.walkableHeight || Math.Abs(other.y - span.y) > Field.walkableClimb) continue;

            int layer = k - neighbour.index;
            if (layer <= MaxLayers) return layer;

            _maxLayer = Math.Max(_maxLayer, layer);
        }

        return RcRecast.RC_NOT_CONNECTED;
    }
}
