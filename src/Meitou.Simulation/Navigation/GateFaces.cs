using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// How the gate-code pass finds the face at a point and reads a face's code (docs/game/pathfinding.md "Gate codes").
/// </summary>
internal static class GateFaces
{
    /// <summary>
    /// How far up the ray that finds a point's face reaches (<b>Verified</b>, <c>FUN_1403a2750</c>: 500 Havok units).
    /// </summary>
    public const float UpReach = 5000;

    /// <summary>
    /// How far a polygon's surface may lie under the point and still be met by the ray up (engine choice: the agent's climb). The original's faces
    /// float on or over the ground the physics rays find; Meitou's polygons follow it within a few units either way.
    /// </summary>
    public const float BelowTolerance = 5;

    /// <summary>
    /// <c>NavMesh::getGateCode</c>'s fallback looks for the closest face within this much of the point on every axis (<b>Verified</b>,
    /// <c>FUN_1403a30f0</c>: a box of half extent 4 Havok units).
    /// </summary>
    public const float NearReach = 40;

    /// <summary>
    /// <c>FUN_1403a2750</c>: the face a ray from <paramref name="at"/> straight up meets first, within <see cref="UpReach"/>.
    /// </summary>
    public static bool TryFaceAbove(NavWorld world, Vector3 at, out NavRef face)
    {
        face = default;
        float best = float.MaxValue;
        var zone = WorldLayout.ZoneOf(at.X, at.Z);
        var found = new List<int>();
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!world.TryGetSlot(new ZoneCoordinate(zone.X + dx, zone.Y + dz), out int slot)) continue;

                var m = world.Mesh(slot);
                found.Clear();
                m.PolygonsAt(at.X, at.Z, found);
                foreach (int p in found)
                {
                    float y = m.HeightAt(p, at.X, at.Z);
                    if (y < at.Y - BelowTolerance || y > at.Y + UpReach || y >= best) continue;

                    best = y;
                    face = new NavRef(slot, p);
                }
            }
        }

        return best < float.MaxValue;
    }

    /// <summary>
    /// <c>NavMesh::getGateCode</c> (<c>FUN_1403a30f0</c>): the code of the face above <paramref name="at"/>, else of the closest face within
    /// <see cref="NearReach"/> that is not a closed door's (Meitou's labelling knows no door states: no door polygon); -1 when there is none.
    /// </summary>
    public static int CodeAt(NavWorld world, IReadOnlyDictionary<long, int> codes, Vector3 at) =>
        TryFace(world, at, out var face) ? CodeOf(world, codes, face) : -1;

    /// <summary>
    /// The face <c>NavMesh::getGateCode</c> reads at <paramref name="at"/>: the one above (<see cref="TryFaceAbove"/>), else the closest within
    /// <see cref="NearReach"/> that is not a door's.
    /// </summary>
    public static bool TryFace(NavWorld world, Vector3 at, out NavRef face) => TryFaceAbove(world, at, out face) || TryFaceNear(world, at, out face);

    /// <summary>
    /// A face's code as <c>NavMesh::getGateCode</c> reads it: in the exterior an unlabelled face is 0 and an unlabelled door face -1; in an interior
    /// (a section whose id is 0x10000 or more: Meitou's interior polygons) an unlabelled face is -1.
    /// </summary>
    public static int CodeOf(NavWorld world, IReadOnlyDictionary<long, int> codes, NavRef face)
    {
        var m = world.Mesh(face.Zone);
        int code = codes.GetValueOrDefault(face.Key);
        if (code > 0) return code;

        bool unknown = face.Polygon >= m.InteriorStart || IsDoor(m, face.Polygon);
        return unknown ? -1 : 0;
    }

    /// <summary>
    /// Whether a polygon is a door's (face data 4 or 5, the faces the gate filter blocks).
    /// </summary>
    public static bool IsDoor(ZoneNavMesh m, int polygon) => m.Areas[polygon] == NavArea.Door;

    /// <summary>
    /// The closest face within <see cref="NearReach"/> of <paramref name="at"/> on every axis, door polygons left out.
    /// </summary>
    static bool TryFaceNear(NavWorld world, Vector3 at, out NavRef face)
    {
        bool Door(NavRef r) => IsDoor(world.Mesh(r.Zone), r.Polygon);
        return world.TryFindPolygon(at, NearReach, out face, out var snapped, Door) && MathF.Abs(snapped.Y - at.Y) <= NearReach;
    }
}
