namespace ProvingGrounds.Core.Glue;

// Port only (no C++ counterpart): the decision of the TEST-ONLY `pg ring push` (src/Game/ArenaRingGuard.cs) about the guard's return spot,
// task 4.8. Tested in Tests/Glue/RingPushRuleTests.cs.

/// <summary>TEST ONLY <c>pg ring push</c>: when a bearing is given, whether the spot the guard would return to on it (RingReturnRadius x the
/// ring's scale) must be walkable before the push is made. Only where the guard acts on the pushed spot (ClassifyRing: the near band or
/// outside, ArenaRingGuard.h:25-30), so a ring check exercises a real return or step; a push inside the near line is never corrected by the
/// guard (an ingress check moving a fighter within the ring) and needs no return spot. Never for an inert ring (scale 0).</summary>
public static class RingPushRule
{
    public static bool NeedsWalkableReturn(bool bearingGiven, float ringScale, float distance, float nearRadius, float outRadius) =>
        bearingGiven && ringScale > 0
        && ArenaRingGuard.ClassifyRing(distance, nearRadius, outRadius) != ArenaRingGuard.RingZone.Inside;

    /// <summary>The floor hints the push tries for its own spot, in order: the site centre's height first, then the character's own height
    /// (the guard's hint for a fighter). The centre first so that a character standing on something else (a roof, a prop, a bench) is put
    /// back on the ring's ground: for a site set around fighters (ring.sh, since task 3.4) the centre's height is the ring's floor. A
    /// building's origin can lie under its floor (Scratch's town Arena 183, 25-48 units under, task 4.8), so where nothing stands at the
    /// centre's height the character's own follows; with that fallback a character on a roof or prop is pushed at its own floor when nothing
    /// stands at the centre's height. One hint when they are the same (-0 and 0 included).</summary>
    public static float[] FloorHints(float centreY, float ownY) => centreY == ownY ? [centreY] : [centreY, ownY];

    /// <summary><c>IWalkability.TryStand</c> at one spot with <paramref name="nearY"/> as the floor hint: true and the ground's height there,
    /// or false.</summary>
    public delegate bool StandAt(float nearY, out float groundY);

    public enum PushRefusal { None, SpotNotWalkable, ReturnSpotNotWalkable }

    /// <summary>The push's floor: the first of <see cref="FloorHints"/> at which the pushed spot stands (<paramref name="atPush"/>), its hint
    /// in <paramref name="floorHint"/> (the guard's write grounds from it); none = <see cref="PushRefusal.SpotNotWalkable"/>. Then, when
    /// <paramref name="needsReturn"/> (<see cref="NeedsWalkableReturn"/>), the guard's return spot on the bearing must stand from the ground
    /// height found at the pushed spot (the guard's hint for the fighter there, <paramref name="atReturn"/>); else
    /// <see cref="PushRefusal.ReturnSpotNotWalkable"/>.</summary>
    public static PushRefusal ChoosePushFloor(float centreY, float ownY, bool needsReturn, StandAt atPush, StandAt atReturn, out float floorHint)
    {
        floorHint = 0;
        float pushedY = 0;
        bool found = false;
        foreach (float h in FloorHints(centreY, ownY))
            if (atPush(h, out float gy)) { floorHint = h; pushedY = gy; found = true; break; }
        if (!found) return PushRefusal.SpotNotWalkable;
        // deliberately no retry with the next hint: the first floor the push spot stands on decides, and a return spot that fails there refuses the push
        if (needsReturn && !atReturn(pushedY, out _)) return PushRefusal.ReturnSpotNotWalkable;
        return PushRefusal.None;
    }
}
