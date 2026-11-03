using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>
/// Beds (docs/game/ui-input.md "Building hover and click", docs/game/locks.md "Beds"): with characters selected the pointer over a bed is <c>use</c>; a quick right click gives the original's
/// order 258 "Sleep" (<see cref="UseBedOrder"/>: every selected character walks there, the first to arrive lies in it until another order). Held, the menu offers the original's one bed verb: "Put in Bed"
/// (<see cref="PutInBedOrder"/>) while a selected character carries someone, else "Sleep", followed by a player bed's site verbs. A quick click while a selected character carries
/// someone lays the body in instead (<b>engine choice</b>: the original's quick click gives 258 regardless, whose plan needs the sleeper not to carry, and how it meets that is
/// <b>Unknown</b>).
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>How near the pointer the projected middle of a bed without a footprint must be, in pixels (an engine choice).</summary>
    const float BedPickPixels = 30;

    BedSpot? PickBed(Vector2 pixel) => PickBedAt(pixel)?.Bed;

    /// <summary>
    /// The bed under the pixel with the ray distance, when not hidden behind a wall nor above the viewed floor of the building the player is in (<see cref="AboveViewedFloor"/>): the
    /// nearest the ray meets on its record's collision (hull prism, then triangles), as a building is picked; else the bed without a footprint whose middle (half a unit above its spot)
    /// projects nearest the pixel within <see cref="BedPickPixels"/>. Until its collision is read (on workers) the magnet stands in.
    /// </summary>
    (BedSpot Bed, float T)? PickBedAt(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray) return null;
        (BedSpot, float)? hit = null, near = null;
        float bestT = float.MaxValue, bestPixels = BedPickPixels;
        foreach (var b in session.CurrentSnapshot.Beds)
        {
            if (buildHost?.Placement.Footprint(b.Record) is { } footprint)
            {
                var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.Yaw);
                if (BuildingRayT(ray, footprint.Place(b.Position, yaw), footprint.YMin, footprint.YMax, b.Record, b.Position, b.Position.Y, yaw) is { } t && t < bestT && AboveViewedFloor?.Invoke(b.Position) != true && !Hidden(ray, t))
                    (hit, bestT) = ((b, t), t);
                continue;
            }
            var middle = b.Position + new Vector3(0, 0.5f, 0);
            if (Project(middle) is not { } at) continue;
            float d = Vector2.Distance(at, pixel), distance = Vector3.Distance(ray.Origin, middle);
            if (d < bestPixels && AboveViewedFloor?.Invoke(b.Position) != true && !Hidden(ray, distance)) (near, bestPixels) = ((b, distance), d);
        }
        return hit ?? near;
    }

    (BedSpot Bed, Vector2 At, long Since)? bedPending;

    /// <summary>A right press on a bed with characters selected: released before the hold time it gives the quick order, held longer it opens the menu (true: taken).</summary>
    bool BedPress(Vector2 p)
    {
        if (SelectedIds().Count == 0 || PickBed(p) is not { } bed) return false;
        bedPending = (bed, p, System.Diagnostics.Stopwatch.GetTimestamp());
        return true;
    }

    /// <summary>The selected characters carrying someone.</summary>
    CharacterId[] Carriers() => [.. Selected().Where(c => !c.Carrying.IsNone).Select(c => c.Id)];

    void SleepIn(BedSpot bed) => Say(new UseBedOrder([], bed.Id) { Tick = session.World.Tick });

    void PutIn(BedSpot bed, CharacterId[] carriers) => Say(new PutInBedOrder(carriers, bed.Id) { Tick = session.World.Tick });

    /// <summary>The right button let go: a press on a bed released before the menu opened.</summary>
    void BedRelease()
    {
        if (bedPending is not { } pending) return;
        bedPending = null;
        if (Carriers() is { Length: > 0 } carriers) PutIn(pending.Bed, carriers);
        else SleepIn(pending.Bed);
    }

    /// <summary>Once a frame: a bed press held long enough opens its menu (the original's <c>FUN_1407a6bc0</c>: 99 when the last selected character carries someone, else the bed's order 258).</summary>
    void UpdateBed()
    {
        if (bedPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < ContextHoldSeconds) return;
        bedPending = null;
        if (SelectedIds().Count == 0) return;
        var bed = pending.Bed;
        var carriers = Carriers();
        var verbs = new List<(string Caption, Action Give)> { Selected().LastOrDefault() is { } lead && !lead.Carrying.IsNone ? ("Put in Bed", () => PutIn(bed, carriers)) : ("Sleep", () => SleepIn(bed)) };
        // A bed the player built: its site's verbs too (dismantle...), which its quick click no longer reaches.
        if (session.CurrentSnapshot.Buildings.FirstOrDefault(s => bed.Id == s.Id + "@bed0") is { } site) verbs.AddRange(SiteVerbs(site));
        contextMenu = new(buildHost?.ItemName(bed.Record) ?? bed.Record, pending.At, verbs);
    }
}
