using System.Numerics;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Towns;

/// <summary>
/// The town node map the original's bar squads are put at (<c>FUN_1409fcd20</c> via <c>FUN_14038b000</c>): per door of a building two "Node
/// Guardpost" nodes (<c>FUN_14038b650</c>, called from the door's point update <c>FUN_14029cc90</c>, <b>Verified</b>). The door's outer point is
/// pushed out by 1.1 times the distance between its two points (<see cref="DoorPoints"/>), the node's Y lies 10 under the point's before it is
/// dropped to the ground (a ray from 100 above), and it is shifted 19 along the door's side axis (the way through x up) and, as a second node, 19 to
/// the other side. Nothing is made when a node of the list lies within 1 of the first position. The order of the original's list is the order its
/// doors were first updated, which is not reproducible; here buildings in the town's order, doors by index (<b>engine choice</b>).
/// </summary>
public static class TownGuardNodes
{
    /// <summary>
    /// The side shift of a node, how far under the door's point its Y lies, and the distance within which a node counts as there already.
    /// </summary>
    const float Shift = 19, DropY = 10, Same = 1;

    /// <summary>
    /// The nodes of the town's doors, in order. Empty when the gate knows no door.
    /// </summary>
    public static List<Vector3> Of(GameDatabase db, IEnumerable<TownBuilding> buildings, IWalkability walk)
    {
        var nodes = new List<Vector3>();
        if (walk is not IDoorGate gate) return nodes;

        foreach (var b in buildings)
        {
            if (b.Destroyed || db.Find(b.BuildingId) is not { } record) continue;

            int doors = record.GetReferences("doors").Count;
            for (int i = 0; i < doors; i++)
            {
                if (gate.DoorSpotOf(record.StringId, i) is { } local) Add(nodes, Place(local, b), b.Position, walk);
            }
        }

        return nodes;
    }

    /// <summary>
    /// The door's spot in the world: the building's yaw turns it (as <see cref="FixtureRegistry.DoorSpotOf"/>).
    /// </summary>
    static DoorSpot Place(DoorSpot local, TownBuilding b)
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.Yaw);
        return local with
        {
            Centre = b.Position + Vector3.Transform(local.Centre, turn), Normal = Vector3.Transform(local.Normal, turn),
        };
    }

    /// <summary>
    /// The nodes of one door (its <paramref name="spot"/> in the world, <paramref name="origin"/> the building's position)
    /// added to <paramref name="nodes"/>, unless one lies within 1 of the first.
    /// </summary>
    public static void Add(List<Vector3> nodes, DoorSpot spot, Vector3 origin, IWalkability walk)
    {
        var outer = DoorPoints.Outer(spot, origin);
        var way = DoorPoints.Away(spot, origin);

        // cross(way, up), the way through taken on the ground plane.
        var side = new Vector2(-way.Y, way.X) * Shift;

        // The point's own ray down (FUN_14029cc90) finds the ground; the node keeps the point's Y less 10.
        float y = walk.GroundHeight(outer.X, outer.Y, spot.Centre.Y) - DropY;
        var at = new Vector3(outer.X + side.X, y, outer.Y + side.Y);
        if (nodes.Exists(n => Vector3.Distance(n, at) < Same)) return;
        nodes.Add(at);
        nodes.Add(at - new Vector3(2 * side.X, 0, 2 * side.Y));
    }
}