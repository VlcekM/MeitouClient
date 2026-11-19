using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The work data of the buildings and home pieces the AI's workers use, read from their <c>functionality</c> records and cached by string id: the
/// reach, the work clip, the functionality, the hunger rate and the operator slots. Derived from the data, not hashed.
/// </summary>
sealed class WorkFunctionality(PopulationData data)
{
    /// <summary>
    /// How near a worker must come to each building, by building id (<see cref="RangeOf"/>).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> _ranges = new(StringComparer.Ordinal);

    /// <summary>
    /// The work clip of each building and home piece, by string id (<see cref="ClipOf"/>, <see cref="PieceClipOf"/>).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _clips = new(StringComparer.Ordinal);

    /// <summary>
    /// The hunger rate at each building, by building id (<see cref="HungerRateOf"/>).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> _hungerRates = new(StringComparer.Ordinal);

    /// <summary>
    /// The operator slots of each workplace, by building id (<see cref="MaxOperatorsOf"/>).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _maxOperators = new(StringComparer.Ordinal);

    /// <summary>
    /// The BuildingFunction of each building record, by its string id (<see cref="FunctionOf"/>).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _functions = new(StringComparer.Ordinal);

    /// <summary>
    /// The use nodes of each building record, in the building's own frame (<see cref="MarkerOf"/>).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Vector3[]> _useNodes = new(StringComparer.Ordinal);

    /// <summary>
    /// How near a worker must come to its building's position: the functionality's <c>use range</c> (the original's <c>UseableStuff</c> reach,
    /// <c>maxUseRange</c>, docs/game/buildings-production.md "Work distance"), <see cref="AiSystem.WorkReach"/> for a building without one.
    /// </summary>
    public float RangeOf(TownBuilding b) => _ranges.GetOrAdd(b.BuildingId, id =>
        data.Db.Find(id)?.GetReferences("functionality") is [var link, ..] && data.Db.Find(link.TargetStringId) is { } function
            ? function.GetFloat("use range", AiSystem.WorkReach)
            : AiSystem.WorkReach);

    /// <summary>
    /// <c>Building::getPositionMarker(from)</c> of a workplace (<c>FUN_14054b850</c>, <b>Verified</b>; docs/game/buildings-production.md "Work
    /// distance"): where a worker walks to and what its reach is measured from. With two or more use nodes the one nearest <paramref name="from"/>
    /// (squared 3D distance), with one that node, with none the building's position. A node that cannot be stood on is passed over and with none
    /// left the building's position is taken (engine choice, for the original's bad-node map: it skips a node a walk to which proved impossible).
    /// </summary>
    public Vector3 MarkerOf(TownBuilding b, Vector3 from, IWalkability walk)
    {
        var nodes = _useNodes.GetOrAdd(b.BuildingId, LocalNodes);
        if (nodes.Length == 0) return b.Position;

        var turn = Matrix4x4.CreateRotationY(b.Yaw);
        if (nodes.Length == 1) return b.Position + Vector3.Transform(nodes[0], turn);

        Span<bool> refused = stackalloc bool[nodes.Length];
        while (Nearest(b, nodes, turn, from, refused) is var (k, at))
        {
            if (walk.IsWalkable(at.X, at.Z)) return at;
            refused[k] = true;
        }

        return b.Position;
    }

    /// <summary>
    /// The use nodes of building record <paramref name="id"/> in the building's own frame, none for a record without them.
    /// </summary>
    Vector3[] LocalNodes(string id) =>
        data.Db.Find(id) is { } record && Locks.FixtureRegistry.UseNodes(data.Db, record) is { } nodes ? [.. nodes.Select(n => n.At)] : [];

    /// <summary>
    /// The node of <paramref name="nodes"/> nearest <paramref name="from"/> among those not <paramref name="refused"/>, with its place in the
    /// world; null when every one is refused.
    /// </summary>
    static (int Index, Vector3 At)? Nearest(TownBuilding b, Vector3[] nodes, Matrix4x4 turn, Vector3 from, ReadOnlySpan<bool> refused)
    {
        (int Index, Vector3 At)? best = null;
        float bestD = float.MaxValue;

        for (int k = 0; k < nodes.Length; k++)
        {
            if (refused[k]) continue;

            var at = b.Position + Vector3.Transform(nodes[k], turn);
            float d = Vector3.DistanceSquared(at, from);
            if (d < bestD) (best, bestD) = ((k, at), d);
        }

        return best;
    }

    /// <summary>
    /// The clip a worker plays at a building: its functionality's <c>animation</c>, else <c>stonemine</c> (<c>Task_OperateMachine</c>,
    /// docs/game/buildings-production.md).
    /// </summary>
    public string ClipOf(TownBuilding b) => _clips.GetOrAdd(b.BuildingId, id =>
        data.Db.Find(id)?.GetReferences("functionality") is [var link, ..]
            && data.Db.Find(link.TargetStringId)?.GetReferences("animation") is [var anim, ..]
            && data.Db.Find(anim.TargetStringId) is { } clip ? clip.Name : "stonemine");

    /// <summary>
    /// The clip of a home piece pretend-operated (a workstation): its record's functionality's <c>animation</c>, else <c>stonemine</c>.
    /// </summary>
    public string PieceClipOf(HomeSpot piece)
    {
        var record = data.Db.Find(piece.Record ?? "");
        return _clips.GetOrAdd(piece.Record ?? "", _ =>
            record?.GetReferences("functionality") is [var link, ..]
                && data.Db.Find(link.TargetStringId)?.GetReferences("animation") is [var anim, ..]
                && data.Db.Find(anim.TargetStringId) is { } found ? found.Name : "stonemine");
    }

    /// <summary>
    /// The BUILDING_FUNCTIONALITY (string id) of a home piece pretend-operated (a workstation or training dummy), null without one: its
    /// <c>animation events</c> go on the work clip as for a worker's building (<see cref="FunctionalityOf"/>).
    /// </summary>
    public string? PieceFunctionalityOf(HomeSpot piece) =>
        data.Db.Find(piece.Record ?? "")?.GetReferences("functionality") is [var link, ..] ? link.TargetStringId : null;

    /// <summary>
    /// The BUILDING_FUNCTIONALITY (string id) of a worker's building, null without one: <c>Task_OperateMachine</c>, which the original runs for 189 /
    /// 190's act-outs (87, 221), adds its <c>animation events</c> to the work clip each update (docs/audio.md "Machine work").
    /// </summary>
    public string? FunctionalityOf(TownBuilding b) =>
        data.Db.Find(b.BuildingId)?.GetReferences("functionality") is [var link, ..] ? link.TargetStringId : null;

    /// <summary>
    /// The ITEM (string id) a worker holds in the <c>hands</c> point at functionality <paramref name="functionality"/>: its <c>special tool</c>
    /// (a mine's Pickaxe, a farm's Hoe), which <c>Task_OperateMachine</c> attaches for 189 / 190's act-outs too; null for none.
    /// </summary>
    public string? ToolOf(string? functionality) =>
        data.Db.Find(functionality ?? "")?.GetReferences("special tool") is [var tool, ..] ? tool.TargetStringId : null;

    /// <summary>
    /// The hunger rate of a worker at a building: its functionality's <c>hunger rate</c> (1 without one), which <c>Task_OperateMachine</c>'s set-up
    /// (<c>FUN_14035b430</c>, <b>Verified</b>) writes to the operator's CharStats <c>+0x178</c> for the real (87) and the pretend (221) act-out
    /// alike.
    /// </summary>
    public float HungerRateOf(TownBuilding b) => _hungerRates.GetOrAdd(b.BuildingId, id =>
        data.Db.Find(id)?.GetReferences("functionality") is [var link, ..]
            && data.Db.Find(link.TargetStringId) is { } function ? function.GetFloat("hunger rate", 1) : 1);

    /// <summary>
    /// A workplace's functionality <c>max operators</c> (1 when the key is missing); no limit for a building without a functionality record.
    /// </summary>
    public int MaxOperatorsOf(TownBuilding building) => _maxOperators.GetOrAdd(building.BuildingId, id =>
        data.Db.Find(id)?.GetReferences("functionality") is [var link, ..] && data.Db.Find(link.TargetStringId) is { } function
            ? function.GetInt("max operators", 1) : int.MaxValue);

    /// <summary>
    /// The BuildingFunction of building record <paramref name="record"/>: its functionality's <c>function</c>, as a level building's
    /// (<see cref="TownBuilding.Function"/>); -1 without one.
    /// </summary>
    public int FunctionOf(string record) => _functions.GetOrAdd(record, id =>
        data.Db.Find(id)?.GetReferences("functionality") is [var link, ..] && data.Db.Find(link.TargetStringId) is { } function
            ? function.GetInt("function", -1) : -1);
}
