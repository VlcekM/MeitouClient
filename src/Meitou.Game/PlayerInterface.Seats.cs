using System.Numerics;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    /// <summary>How near the pointer the projected middle of a seat without a footprint must be, in pixels (an engine choice).</summary>
    const float SeatPickPixels = 30;

    SeatSpot? PickSeat(Vector2 pixel) => PickSeatAt(pixel)?.Seat;

    /// <summary>
    /// The seat under the pixel with the ray distance, when not hidden behind a wall nor above the viewed floor of the building the player is in (<see cref="AboveViewedFloor"/>): the
    /// nearest the ray meets on its record's collision (hull prism, then triangles), as a building is picked; else the seat without a footprint whose middle (half a unit above its spot)
    /// projects nearest the pixel within <see cref="SeatPickPixels"/>. Until its collision is read (on workers) the magnet stands in.
    /// </summary>
    (SeatSpot Seat, float T)? PickSeatAt(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray) return null;
        (SeatSpot, float)? hit = null, near = null;
        float bestT = float.MaxValue, bestPixels = SeatPickPixels;
        foreach (var b in session.CurrentSnapshot.Seats)
        {
            if (buildHost?.Placement.Footprint(b.Record) is { } footprint)
            {
                var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.PlacementYaw);
                if (BuildingRayT(ray, footprint.Place(b.Position, yaw), footprint.YMin, footprint.YMax, b.Record, b.Position, b.Position.Y, yaw) is { } t && t < bestT && AboveViewedFloor?.Invoke(b.Position) != true && !Hidden(ray, t))
                    (hit, bestT) = ((b, t), t);
                continue;
            }
            // An invisible sitting node has no player-selectable shape; only visible seats get the pending-collision magnet.
            if (buildHost?.Parts(b.Record).Count == 0) continue;
            var middle = b.Position + new Vector3(0, 0.5f, 0);
            if (Project(middle) is not { } at) continue;
            float d = Vector2.Distance(at, pixel), distance = Vector3.Distance(ray.Origin, middle);
            if (d < bestPixels && AboveViewedFloor?.Invoke(b.Position) != true && !Hidden(ray, distance)) (near, bestPixels) = ((b, distance), d);
        }
        return hit ?? near;
    }

    (SeatSpot Seat, Vector2 At, long Since)? seatPending;

    /// <summary>A right press on a seat with characters selected: released before the hold time it gives the quick order, held longer it opens the menu (true: taken).</summary>
    bool SeatPress(Vector2 p)
    {
        if (SelectedIds().Count == 0 || PickSeat(p) is not { } seat) return false;
        seatPending = (seat, p, System.Diagnostics.Stopwatch.GetTimestamp());
        return true;
    }

    void SitOn(SeatSpot seat) => Say(SlowModifier
        ? (Meitou.Simulation.SimCommand)new AddJob(SelectedIds(), Meitou.Data.Gameplay.Ai.TaskType.OPERATE_MACHINERY, seat.Id) { Tick = session.World.Tick }
        : new OperateOrder(SelectedIds(), seat.Id) { Tick = session.World.Tick });
    void SeatRelease()
    {
        if (seatPending is not { } pending) return;
        seatPending = null;
        SitOn(pending.Seat);
    }
    void UpdateSeat()
    {
        if (seatPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < ContextHoldSeconds) return;
        seatPending = null;
        if (SelectedIds().Count == 0) return;
        var seat = pending.Seat;
        contextMenu = new(buildHost?.ItemName(seat.Record) ?? seat.Record, pending.At, [("Use", () => SitOn(seat))]);
    }
}
