// Port of the pure part of upstream/ProvingGrounds/plugin/src/ArenaRingGuard.h (Proving Grounds by Shutori, GPLv3): the ring
// arithmetic. The engine surface the header also declares (SetArena(Building*), Tick(), ArenaRingGuard.h:58-71) and its body
// ArenaRingGuard.cpp are glue (src/Game/ArenaRingGuard.cs, M3).
namespace ProvingGrounds.Core;

/// <summary>Keeps fighters inside the arena they are fighting in: the zone, return point and throttle arithmetic.
/// C++: ArenaRingGuard.h:21-56 (PA §2.10). The shipped radii are BalanceTuning's (near 145, out 147.5, return 142.5).</summary>
public static class ArenaRingGuard
{
    // ---- pure arithmetic: no engine calls, so a reader can check the geometry ----

    /// <summary>C++: ArenaRingGuard.h:23</summary>
    public enum RingZone { Inside, NearEdge, Outside }

    // C++: ArenaRingGuard.h:25-30
    public static RingZone ClassifyRing(float distance, float nearRadius, float outRadius)
    {
        if (distance >= outRadius) return RingZone.Outside;
        if (distance >= nearRadius) return RingZone.NearEdge;
        return RingZone.Inside;
    }

    // Pull a fighter straight in along their own radius. Aiming at the centre
    // itself would stack the whole bout on one point and reset the fight's
    // geometry; keeping the angle preserves where they were standing in the ring.
    // C++: ArenaRingGuard.h:35-49
    public static void RingReturnPoint(float dx, float dz, float targetRadius, out float x, out float z)
    {
        float length = MathF.Sqrt(dx * dx + dz * dz);
        if (length > 1.0f)
        {
            x = dx * targetRadius / length;
            z = dz * targetRadius / length;
        }
        else
        {
            // Exactly on the centre: any direction is equally inward.
            x = targetRadius;
            z = 0.0f;
        }
    }

    // Both corrections are throttled, so a fighter held against the wall is not
    // re-ordered every frame. Wrap-safe: the unsigned difference handles rollover.
    // C++: ArenaRingGuard.h:53-56
    public static bool RingCorrectionDue(uint now, uint last, uint intervalMs)
    {
        return last == 0 || unchecked(now - last) >= intervalMs;
    }
}
