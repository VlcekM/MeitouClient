using System.Numerics;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The gate codes of a <see cref="NavWorld"/>: the original's gate-code pass run over Meitou's navmesh (docs/game/pathfinding.md "Gate codes";
/// <c>FUN_1402ef4a0</c>). The gates are the loaded GatewayBuildings with a first door, by their side points (<see cref="GateSide"/>); each gets a
/// first and a second code, and the faces of the regions flooded from them get theirs. Immutable once made; safe from any thread.
/// </summary>
public sealed class NavGateRegions : IGateRegions
{
    /// <summary>
    /// No gates and no navmesh: every point is off the navmesh (-1).
    /// </summary>
    public static readonly NavGateRegions Empty = new(NavWorld.Empty, [], [], [], []);

    /// <summary>
    /// The navmesh the codes label; points are looked up on it.
    /// </summary>
    readonly NavWorld _world;
    /// <summary>
    /// The code written into each labelled face, by polygon key; a face not in it has none.
    /// </summary>
    readonly Dictionary<long, int> _codes;
    /// <summary>
    /// The unlabelled interior polygons' codes, their buildings' (<see cref="GateInteriorCodes"/>).
    /// </summary>
    readonly Dictionary<long, int> _inside;

    /// <summary>
    /// Wraps a pass's result.
    /// </summary>
    NavGateRegions(NavWorld world, Dictionary<long, int> codes, Dictionary<long, int> inside, IReadOnlyList<GateLink> gates,
            IReadOnlyList<string> log)
    {
        _world = world;
        _codes = codes;
        _inside = inside;
        Gates = gates;
        Log = log;
    }

    /// <inheritdoc/>
    public IReadOnlyList<GateLink> Gates { get; }

    /// <summary>
    /// What the pass reported, in the original's words where it logs (a flood without a start face, a flood that failed, several code 0 regions).
    /// </summary>
    public IReadOnlyList<string> Log { get; }

    /// <summary>
    /// The code of an object at <paramref name="at"/>: <c>NavMesh::getGateCode</c> there, except that in an interior it is its building's
    /// (<see cref="GateInteriorCodes"/>; the original keeps each object's code on the object, and what is inside a building takes the building's).
    /// </summary>
    public int CodeAt(Vector3 at)
    {
        if (!GateFaces.TryFace(_world, at, out var face)) return -1;

        return _inside.TryGetValue(face.Key, out int code) ? code : GateFaces.CodeOf(_world, _codes, face);
    }

    /// <summary>
    /// Runs the gate-code pass over <paramref name="world"/>: the gates are the side points its zone meshes hold
    /// (<see cref="ZoneNavMesh.GateSides"/>), by zone (X, then Z) and then as gathered, each building once. The connection tests keep
    /// <paramref name="footprint"/> clear: the default race's <c>pathfind footprint radius</c> (<c>FUN_1402ef4a0</c>).
    /// </summary>
    public static NavGateRegions Label(NavWorld world, float footprint = GateCodeFlood.DefaultFootprint)
    {
        var gates = GatesOf(world);
        if (gates.Count == 0) return new NavGateRegions(world, [], GateInteriorCodes.Of(world, []), [], []);

        var pass = new GateCodePass(world, gates, footprint);
        pass.Run();
        var links = gates.Select((g, i) => new GateLink(g.Building, [pass.First[i], pass.Second[i]])).ToList();
        return new NavGateRegions(world, pass.Codes, GateInteriorCodes.Of(world, pass.Codes), links, pass.Log);
    }

    /// <summary>
    /// The gate list (<c>FUN_1402eab70</c>) of the loaded meshes.
    /// </summary>
    static List<GateSide> GatesOf(NavWorld world)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var gates = new List<GateSide>();
        var slots = Enumerable.Range(0, world.ZoneCount)
            .OrderBy(s => world.Mesh(s).ZoneX)
            .ThenBy(s => world.Mesh(s).ZoneZ);
        foreach (int s in slots)
        {
            foreach (var side in world.Mesh(s).GateSides ?? [])
                if (seen.Add(side.Building)) gates.Add(side);
        }

        return gates;
    }
}
