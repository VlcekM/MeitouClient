using System.Numerics;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Game.Building;

/// <summary>
/// Which walls a wall line's pieces pass at its snapped joints (docs/game/buildings-production.md "Walls", <b>Verified</b>, <c>FUN_140427b00</c>):
/// a wall with a link point at the snapped start passes the pieces at the front of the line, one at the snapped end those at its back.
/// </summary>
public sealed class WallJointPass
{
    /// <summary>
    /// A link point nearer than the square root of this to a snapped joint is at it (<b>Verified</b>).
    /// </summary>
    const float ReachSquared = 400;
    /// <summary>
    /// When the line's shortest piece is a short, a link point nearer than its length plus this is at a joint too (<b>Verified</b>).
    /// </summary>
    const float ShortReach = 20;
    /// <summary>
    /// The pieces counted from a joint are those whose lengths first sum to this (<b>Verified</b>).
    /// </summary>
    const float Run = 120;
    /// <summary>
    /// The <c>link type</c> of a short piece.
    /// </summary>
    const int Short = 4;

    /// <summary>
    /// The line's snapped start and snapped end; null where it is not snapped.
    /// </summary>
    readonly Vector3? _start, _end;
    /// <summary>
    /// The square of the distance a link point must be nearer than to be at a joint.
    /// </summary>
    readonly float _reachSquared;
    /// <summary>
    /// Each piece's run index: 0 for a bend piece, else the previous piece's plus one (0 for the first).
    /// </summary>
    readonly int[] _index;
    /// <summary>
    /// Pieces with a run index below this pass the walls at the start.
    /// </summary>
    readonly int _front;
    /// <summary>
    /// Pieces with a run index above this, or of 0, pass the walls at the end.
    /// </summary>
    readonly int _back;

    /// <summary>
    /// The pass for <paramref name="pieces"/> (in the line's order: the start's bend pieces, the end's, then the run's) of a line whose shortest piece
    /// is <paramref name="shortest"/>, snapped at <paramref name="start"/> and <paramref name="end"/> (null: not snapped there).
    /// </summary>
    public WallJointPass(IReadOnlyList<WallPiece> pieces, BuildingBuildData? shortest, Vector3? start, Vector3? end)
    {
        _start = start;
        _end = end;

        float shortReach = shortest is { LinkType: Short } s ? (s.LinkLength + ShortReach) * (s.LinkLength + ShortReach) : 0;
        _reachSquared = MathF.Max(ReachSquared, shortReach);

        _index = RunIndices(pieces);
        _front = FromFront(pieces);
        _back = FromBack(pieces);
    }

    /// <summary>
    /// Whether piece <paramref name="piece"/> passes a wall whose link points are <paramref name="otherStart"/> and <paramref name="otherEnd"/>.
    /// </summary>
    public bool Passes(int piece, Vector3 otherStart, Vector3 otherEnd)
    {
        int index = _index[piece];
        if (At(_start, otherStart, otherEnd) && index < _front) return true;

        return At(_end, otherStart, otherEnd) && (index > _back || index == 0);
    }

    /// <summary>
    /// Whether either link point is at the joint; false when the line is not snapped there.
    /// </summary>
    bool At(Vector3? joint, Vector3 otherStart, Vector3 otherEnd) =>
        joint is { } j && (Vector3.DistanceSquared(j, otherStart) < _reachSquared || Vector3.DistanceSquared(j, otherEnd) < _reachSquared);

    /// <summary>
    /// The run index of each piece.
    /// </summary>
    static int[] RunIndices(IReadOnlyList<WallPiece> pieces)
    {
        var index = new int[pieces.Count];
        for (int i = 1; i < pieces.Count; i++)
            index[i] = pieces[i].Joint ? 0 : index[i - 1] + 1;
        return index;
    }

    /// <summary>
    /// How many pieces from the front it takes for their lengths to reach <see cref="Run"/> (all when they never do).
    /// </summary>
    static int FromFront(IReadOnlyList<WallPiece> pieces)
    {
        int count = 0;
        float sum = 0;
        while (count < pieces.Count && sum < Run)
            sum += pieces[count++].Data.LinkLength;
        return count;
    }

    /// <summary>
    /// The piece count - 1 less the pieces it takes from the back for their lengths to reach <see cref="Run"/>.
    /// </summary>
    static int FromBack(IReadOnlyList<WallPiece> pieces)
    {
        int last = pieces.Count - 1;
        float sum = 0;
        while (last >= 0 && sum < Run)
            sum += pieces[last--].Data.LinkLength;
        return last;
    }
}
