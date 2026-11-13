using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// One face of a walkable surface for a snap query (<see cref="IWalkability.SurfaceFaces"/>): its corners in world coordinates (counter-clockwise seen
/// from above; edge <c>k</c> runs from corner <c>k</c> to <c>k + 1</c>), which of its edges end the surface (nothing behind them, not a seam to a
/// neighbouring tile) and whether the closest-point filter skips it (door and water faces).
/// </summary>
public sealed record SurfaceFace(Vector3[] Corners, bool[] OpenEdges, bool Filtered);
