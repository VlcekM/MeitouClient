namespace ProvingGrounds.Core.Glue;

// The engine half of upstream/ProvingGrounds/plugin/src/ArenaRingGuard.cpp (Proving Grounds by Shutori, GPLv3), task 3.4 (PA §2.10, CAP 5),
// every function in its order, over a small host so the decisions run without the game (E3, Tests/Glue/RingGuardLogicTests.cs). The header's
// pure arithmetic (ClassifyRing, RingReturnPoint, RingCorrectionDue, ArenaRingGuard.h:21-56) is Core/ArenaRingGuard.cs (M1). src/Game/
// ArenaRingGuard.cs binds the world (PgRingHost there).
//
// Distance: 2D, x and z only (ArenaRingGuard.cpp:104-106 dx/dz, sqrt(dx*dx + dz*dz)), as the C++. The C++ keeps the fighter's height (:114-115)
// and relies on Kenshi's physics; the port puts it on the ground under the spot at that floor (Meitou has no physics to resolve it).

/// <summary>What the ring guard reads and writes in the game (one world). All calls on the sim thread, serial step.</summary>
public interface IRingGuardHost<TId>
{
    /// <summary><c>SparSession::IsActive()</c>.</summary>
    bool SparActive { get; }
    /// <summary><c>g_site.isValid()</c>, <c>g_site.getBuilding()</c>, <c>site-&gt;isValid()</c> and <c>site-&gt;getPosition()</c>
    /// (ArenaRingGuard.cpp:69-71, :82): false when the registered site is gone.</summary>
    bool TryGetSiteCentre(out float x, out float y, out float z);
    /// <summary><c>SparSession::GetParticipantCount()</c>.</summary>
    int ParticipantCount { get; }
    /// <summary><c>SparSession::GetParticipant(i)</c>; <see cref="IsNone"/> for a null character.</summary>
    TId Participant(int i);
    bool IsNone(TId c);
    /// <summary><c>c-&gt;isValid()</c>.</summary>
    bool Exists(TId c);
    /// <summary><c>SparSession::IsEliminated(c)</c>.</summary>
    bool IsEliminated(TId c);
    /// <summary><c>c-&gt;getMovement()</c> non-null, then <c>c-&gt;getPosition()</c> (:100-103): false when either is missing.</summary>
    bool TryGetPosition(TId c, out float x, out float y, out float z);
    /// <summary>Port only: carried, or unconscious or dead (KOWatcher's down test). Meitou's ragdoll owns such a body (CAP 5;
    /// RagdollSystem.Wanted: incapacitated or carried, RagdollSystem.cs:449-451): never moved.</summary>
    bool IsDowned(TId c);
    /// <summary>The correction: <c>combatMover.hasForcedWP = false</c> + <c>_setPositionSimple(target)</c> (:123-124) for a fighter beyond
    /// out, and in place of <c>combatMover.setForcedWP(target)</c> (:134) for one in the near band (Meitou has no forced combat waypoint:
    /// a small step, divergences.md). Puts the character at (x, ground height there at the floor near <paramref name="nearY"/>, z), keeping its
    /// facing and its current task so the fight goes on; false (nothing moved) when that spot is not walkable at that floor.</summary>
    bool TryPlaceKeepingTask(TId c, float x, float nearY, float z);
    /// <summary><c>GetTickCount()</c> (the port's PgClock ms).</summary>
    uint NowMs { get; }
    /// <summary><c>PGLog::Debug(line)</c>.</summary>
    void Debug(string line);
}

/// <summary>What the registered site is, for <see cref="RingGuardLogic{TId}.RingScaleForSite"/> (C++ ArenaIdentity::IsBanner / IsSmallArena of
/// the site building, ArenaRingGuard.cpp:43-44; any other site is a full-size arena, :45-47).</summary>
public enum RingSiteKind { Arena, SmallArena, Banner }

/// <summary>
/// ArenaRingGuard.cpp: keeps the fighters of the running spar inside the arena they fight in. One object per world (the C++ namespace globals
/// g_site's scale, g_nearAt, g_outAt, :34-38). The site itself (the C++ <c>hand g_site</c>) is the host's: it answers whether it still exists
/// and where its centre is.
/// </summary>
public sealed class RingGuardLogic<TId>
{
    /// <summary>C++: ArenaRingGuard.cpp:25 <c>kSmallArenaRingScale</c>. An arena-size factor, deliberately not ArenaIngress's staging scale 0.5.</summary>
    public const float SmallArenaRingScale = 0.6f;
    /// <summary>C++: ArenaRingGuard.cpp:36 <c>kCorrectionIntervalMs</c>: one correction per second per kind, throttled per PASS.</summary>
    public const uint CorrectionIntervalMs = 1000u;
    /// <summary>Port only: the near-band step inward at full arena size, scaled by the site's scale like the radii (the original steered with
    /// a forced combat waypoint, which Meitou has none of; at full size the band between near and out is 2.5 units, and so is the gap between
    /// near and return).</summary>
    public const float NearStepUnits = 2.5f;

    bool hasSite;                // C++ g_site != null (the host answers its validity)
    float ringScale;             // C++ :30 g_ringScale
    uint nearAt;                 // C++ :31 g_nearAt
    uint outAt;                  // C++ :32 g_outAt

    // Port only: per participant slot, when it was last counted as a downed / unwalkable skip (0 = not yet), so each counts once per fighter
    // per 1,000 ms window. Grown only when a larger match is seen; cleared by SetArena.
    uint[] downedCountedAt = [];
    uint[] unwalkableCountedAt = [];

    /// <summary>Port only (test output, never logged per correction): corrections made since the last SetArena, by kind; downed bodies left
    /// alone and fighters left because their spot was not walkable, each counted once per fighter per 1,000 ms it stays so.</summary>
    public int NearCorrections { get; private set; }
    public int OutCorrections { get; private set; }
    public int DownedSkips { get; private set; }
    public int UnwalkableSkips { get; private set; }

    /// <summary>The scale in force (0 = no ring).</summary>
    public float RingScale => ringScale;
    public bool HasSite => hasSite;
    /// <summary>The PgClock ms of the last correction of each kind (0 = none since SetArena): test output.</summary>
    public uint NearAt => nearAt;
    public uint OutAt => outAt;

    // The arena's size, from the site itself. 0 means "no ring here", which makes
    // the guard inert rather than guessing a radius for a site it does not know.
    // C++: ArenaRingGuard.cpp:40-48
    public static float RingScaleForSite(bool present, RingSiteKind kind)
    {
        if (!present) return 0.0f;
        if (kind == RingSiteKind.Banner) return 0.0f;
        if (kind == RingSiteKind.SmallArena) return SmallArenaRingScale;
        // The town Crucible and the player-run arena are the same size, so the
        // tuned radii apply to both unchanged.
        return 1.0f;
    }

    // C++: ArenaRingGuard.cpp:53-59
    public void SetArena(bool present, RingSiteKind kind)
    {
        hasSite = present;
        ringScale = RingScaleForSite(present, kind);
        nearAt = 0;
        outAt = 0;
        // port only: the counts and their windows start again with the new site
        NearCorrections = OutCorrections = DownedSkips = UnwalkableSkips = 0;
        Array.Clear(downedCountedAt);
        Array.Clear(unwalkableCountedAt);
    }

    /// <summary>Whether participant slot <paramref name="i"/> counts again (once per fighter per 1,000 ms window); stamps it.</summary>
    static bool CountOnce(ref uint[] stamps, int i, int count, uint now)
    {
        if (stamps.Length < count) Array.Resize(ref stamps, Math.Max(count, stamps.Length * 2));
        if (!ArenaRingGuard.RingCorrectionDue(now, stamps[i], CorrectionIntervalMs)) return false;
        stamps[i] = now == 0 ? 1u : now;
        return true;
    }

    /// <summary>TEST ONLY (pg ring ... scale): replaces the site's scale, so a live check can draw a small ring. No C++ counterpart.</summary>
    public void OverrideScaleForTest(float scale) => ringScale = hasSite ? scale : 0.0f;

    // C++: ArenaRingGuard.cpp:61-143
    public void Tick(IRingGuardHost<TId> host)
    {
        // A bout is live in either arena at the moment combat starts -- the town bout
        // from its own OnMatchStarted, the player-run arena from FinishIngressAndStart
        // once the walk-in completes -- so this one predicate covers both, and excludes
        // staging, when fighters approach from outside the ring by design.
        if (!host.SparActive) return;
        if (ringScale <= 0.0f) return;
        if (!hasSite) return;
        if (!host.TryGetSiteCentre(out float centreX, out _, out float centreZ)) return;

        BalanceTuning.Values tuning = BalanceTuning.Get();
        float nearRadius = (float)tuning.RingNearRadius * ringScale;
        float outRadius = (float)tuning.RingOutRadius * ringScale;
        float returnRadius = (float)tuning.RingReturnRadius * ringScale;
        // Equal near/out thresholds mean the guard is off. The other condition keeps
        // the corrections ordered: the return must land inside the near line or the
        // fighter would be steered again the moment they arrive.
        if (outRadius <= nearRadius || returnRadius >= nearRadius) return;

        uint now = host.NowMs;
        bool nearDue = ArenaRingGuard.RingCorrectionDue(now, nearAt, CorrectionIntervalMs);
        bool outDue = ArenaRingGuard.RingCorrectionDue(now, outAt, CorrectionIntervalMs);
        if (!nearDue && !outDue) return;

        int count = host.ParticipantCount;
        bool didNear = false, didOut = false;
        for (int i = 0; i < count; ++i)
        {
            // Participants, not an arena's own fighter list: this is what both arenas
            // share, and it keeps medics and spectators out of scope by construction.
            TId c = host.Participant(i);
            if (host.IsNone(c) || !host.Exists(c)) continue;
            // An eliminated fighter is aftercare's patient; moving them would fight
            // the medics for the body.
            if (host.IsEliminated(c)) continue;

            // getMovement() and getPosition() (:100-103)
            if (!host.TryGetPosition(c, out float px, out float py, out float pz)) continue;
            float dx = px - centreX;
            float dz = pz - centreZ;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            ArenaRingGuard.RingZone zone = ArenaRingGuard.ClassifyRing(distance, nearRadius, outRadius);
            if (zone == ArenaRingGuard.RingZone.Inside) continue;
            bool outside = zone == ArenaRingGuard.RingZone.Outside;
            if (outside ? !outDue : !nearDue) continue;
            // Port only: a downed body is the ragdoll's (CAP 5); the C++ moved it like any other.
            if (host.IsDowned(c)) { if (CountOnce(ref downedCountedAt, i, count, now)) DownedSkips++; continue; }

            if (outside)
            {
                ArenaRingGuard.RingReturnPoint(dx, dz, returnRadius, out float rx, out float rz);
                // Already through the wall: a move order cannot be trusted to path back
                // out of geometry, so set the position directly. (The C++ clears its forced
                // waypoint first; Meitou has none.) Their own floor (the ground under the spot
                // near their height), so a correction never changes floor level; a spot that is
                // not walkable there is left (port only: not stamped, counted).
                if (!host.TryPlaceKeepingTask(c, centreX + rx, py, centreZ + rz))
                {
                    if (CountOnce(ref unwalkableCountedAt, i, count, now)) UnwalkableSkips++;
                    continue;
                }
                didOut = true;
                OutCorrections++;
                host.Debug("Proving Grounds: ring guard returned fighter to the ring (dist=" + CMath.FormatF(distance, 0)
                    + " out=" + CMath.FormatF(outRadius, 0) + " -> " + CMath.FormatF(returnRadius, 0)
                    + " scale=" + CMath.FormatF(ringScale, 2) + ")");
            }
            else
            {
                // Still inside: the C++ steers with a forced combat waypoint at the return point, so they keep fighting. Meitou has no
                // such waypoint for a fighter: the port steps them NearStepUnits x the ring's scale inward on their own bearing, never
                // past that return point (divergences.md); an unwalkable spot is left as above.
                float stepRadius = MathF.Max(distance - NearStepUnits * ringScale, returnRadius);
                ArenaRingGuard.RingReturnPoint(dx, dz, stepRadius, out float sx, out float sz);
                if (!host.TryPlaceKeepingTask(c, centreX + sx, py, centreZ + sz))
                {
                    if (CountOnce(ref unwalkableCountedAt, i, count, now)) UnwalkableSkips++;
                    continue;
                }
                didNear = true;
                NearCorrections++;
                host.Debug("Proving Grounds: ring guard steering fighter inward (dist=" + CMath.FormatF(distance, 0)
                    + " near=" + CMath.FormatF(nearRadius, 0) + " -> " + CMath.FormatF(returnRadius, 0)
                    + " scale=" + CMath.FormatF(ringScale, 2) + ")");
            }
        }
        if (didNear) nearAt = now;
        if (didOut) outAt = now;
    }
}
