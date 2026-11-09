using System.Numerics;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The gate codes (docs/game/pathfinding.md "Gate codes"): the walkable ground split into regions by the gates, 0 the open world and 1.. the regions
/// the walls close in. Made from the navmesh off the simulation thread; immutable, safe from any thread.
/// </summary>
public interface IGateRegions
{
    /// <summary>
    /// The code of the region at <paramref name="at"/>: 0 outside walls, 1.. a walled region, -1 off the navmesh (the original's -1, "unknown").
    /// </summary>
    int CodeAt(Vector3 at);
    /// <summary>
    /// The gates with the codes of the regions beside them (two for a gate in a wall line; one when both sides are the same region).
    /// </summary>
    IReadOnlyList<GateLink> Gates { get; }
}

/// <summary>
/// A gate building (its instance id, as its door fixtures name it) and the codes it joins, ascending.
/// </summary>
public readonly record struct GateLink(string Building, int[] Codes);

/// <summary>
/// New gate codes (the navmesh around the gates changed). Applied by the lock system; derived from the navmesh like a path, so not hashed or saved.
/// </summary>
public sealed record GateRegionsCommand(IGateRegions Regions) : SimCommand;