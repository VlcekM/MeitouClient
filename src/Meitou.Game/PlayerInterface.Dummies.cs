using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;

namespace Meitou.Game;

/// <summary>
/// A town's training dummies (docs/game/ui-input.md "Context menu verbs": UseableStuff function 7 gives 97 USE_TRAINING_DUMMY whoever owns it): with characters selected the pointer over one
/// is <c>use</c>; a quick right click has them train there (<see cref="OperateOrder"/>), with Shift it becomes the Training job; held, the menu offers "Training". A player's own dummy is a
/// build site and goes through the site verbs instead.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>
    /// The dummy under the pixel with the ray distance, picked as a bed is (<see cref="PickBedAt"/>): on its record's collision, else its middle projected within <see cref="BedPickPixels"/>.
    /// </summary>
    (TrainingSpot Dummy, float T)? PickDummyAt(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray) return null;
        (TrainingSpot, float)? hit = null, near = null;
        float bestT = float.MaxValue, bestPixels = BedPickPixels;
        foreach (var d in session.CurrentSnapshot.Dummies)
        {
            if (buildHost?.Placement.Footprint(d.Record) is { } footprint)
            {
                var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, d.Yaw);
                if (BuildingRayT(ray, footprint.Place(d.Position, yaw), footprint.YMin, footprint.YMax, d.Record, d.Position, d.Position.Y, yaw) is { } t && t < bestT && AboveViewedFloor?.Invoke(d.Position) != true && !Hidden(ray, t))
                    (hit, bestT) = ((d, t), t);
                continue;
            }
            var middle = d.Position + new Vector3(0, 1, 0);
            if (Project(middle) is not { } at) continue;
            float gap = Vector2.Distance(at, pixel), distance = Vector3.Distance(ray.Origin, middle);
            if (gap < bestPixels && AboveViewedFloor?.Invoke(d.Position) != true && !Hidden(ray, distance)) (near, bestPixels) = ((d, distance), gap);
        }
        return hit ?? near;
    }

    (TrainingSpot Dummy, Vector2 At, long Since)? dummyPending;

    /// <summary>A right press on a dummy with characters selected: released before the hold time it trains, held longer it opens the menu (true: taken).</summary>
    bool DummyPress(Vector2 p)
    {
        if (SelectedIds().Count == 0 || PickDummyAt(p) is not { } dummy) return false;
        dummyPending = (dummy.Dummy, p, System.Diagnostics.Stopwatch.GetTimestamp());
        return true;
    }

    void Train(TrainingSpot dummy) => Say(SlowModifier
        ? new AddJob(SelectedIds(), TaskType.USE_TRAINING_DUMMY, dummy.Id) { Tick = session.World.Tick }
        : new OperateOrder(SelectedIds(), dummy.Id) { Tick = session.World.Tick });

    /// <summary>The right button let go: a press on a dummy released before the menu opened.</summary>
    void DummyRelease()
    {
        if (dummyPending is not { } pending) return;
        dummyPending = null;
        if (SelectedIds().Count > 0) Train(pending.Dummy);
    }

    /// <summary>Once a frame: a dummy press held long enough opens its menu.</summary>
    void UpdateDummy()
    {
        if (dummyPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < ContextHoldSeconds) return;
        dummyPending = null;
        if (SelectedIds().Count == 0) return;
        contextMenu = new(buildHost?.ItemName(pending.Dummy.Record) ?? pending.Dummy.Record, pending.At, [("Training", () => Train(pending.Dummy))]);
    }
}
