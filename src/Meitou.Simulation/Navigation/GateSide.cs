using System.Numerics;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The two side points of a gate as the gate-code pass takes them (docs/game/pathfinding.md "Gate codes", <c>FUN_1402eab70</c>): a GatewayBuilding
/// (record <c>is wall gate</c>) with a first door, its door's inner and outer markers pushed apart by half their distance.
/// </summary>
/// <param name="Building">The gate's placement key (what its door fixtures and door polygons are named by).</param>
/// <param name="First">The first side point (from the door's inner marker, <c>+0x3c8</c>).</param>
/// <param name="Second">The second side point (from the door's outer marker, <c>+0x3bc</c>).</param>
public readonly record struct GateSide(string Building, Vector3 First, Vector3 Second);
