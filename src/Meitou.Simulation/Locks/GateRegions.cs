using System.Numerics;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The gate codes (docs/game/pathfinding.md "Gate codes"): the walkable ground split into regions by the gates, 0 the open world and 1.. the regions
/// the walls close in. Made from the navmesh off the simulation thread; immutable, safe from any thread.
/// </summary>
public interface IGateRegions
{
    /// <summary>
    /// <c>NavMesh::getGateCode</c> at <paramref name="at"/>: 0 on unlabelled exterior ground (the open world), 1.. a labelled region, -1 off the
    /// navmesh, on an unlabelled door face or in an unlabelled interior (the original's "unknown").
    /// </summary>
    int CodeAt(Vector3 at);
    /// <summary>
    /// The gates of the pass, each with its first and second code.
    /// </summary>
    IReadOnlyList<GateLink> Gates { get; }
}

/// <summary>
/// A gate building (its placement key, as its door fixtures name it) and its codes: the first (its first door's <c>+0x378</c>) and the second (the
/// building's own); equal when both sides are one region.
/// </summary>
public readonly record struct GateLink(string Building, int[] Codes);

/// <summary>
/// New gate codes (the navmesh around the gates changed). Applied by the lock system; derived from the navmesh like a path, so not hashed or saved.
/// </summary>
public sealed record GateRegionsCommand(IGateRegions Regions) : SimCommand;