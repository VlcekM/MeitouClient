using System.Numerics;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Where a building's own gate code is read (docs/game/pathfinding.md "Gate codes", <c>FUN_140555fd0</c>): its first door's outer marker, or for a
/// building without a door its position. What stands inside the building takes that code (Character slot 35, <c>FUN_140792200</c>).
/// </summary>
/// <param name="Building">The building's placement key (what its interior polygons are named by, <see cref="ZoneNavMesh.BuildingIds"/>).</param>
/// <param name="At">The point its code is read at.</param>
public readonly record struct BuildingCodePoint(string Building, Vector3 At);
