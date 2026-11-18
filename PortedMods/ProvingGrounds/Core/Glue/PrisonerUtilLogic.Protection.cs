namespace ProvingGrounds.Core.Glue;

// Task 6.2 (PA §2.19, §2.8; CAP 1, 2, 19): parking the handlers, protecting the released prisoners and the cadenced upkeep (PrisonerUtil.cpp:
// the constants :120-121, AdvanceMatchUpkeepClock :135-142, ClearParkOrders / ApplyParkOrders / ReinforceParkOrders :509-537,
// DisengageOutsiderFromPrisoner :786-800, ProtectMatchPrisoners :1259-1309, ParkMatchHandlers .. TickMatchUpkeep :1370-1405). Every
// GetTickCount is the host's NowMs (PgClock, sim ms). The engine half is src/Game/PrisonerUtil.Protection.cs. TickRingsideAid (:1311-1368) is
// PrisonerUtilLogic.Return.cs (task 6.3).

public sealed partial class PrisonerUtilLogic<TId>
{
    /// <summary>C++ <c>kProtectionIntervalSec</c> (PrisonerUtil.cpp:120).</summary>
    public const float ProtectionIntervalSec = 0.10f;
    /// <summary>C++ <c>kHandlerReinforceIntervalSec</c> (PrisonerUtil.cpp:121).</summary>
    public const float HandlerReinforceIntervalSec = 0.25f;

    readonly List<TId> attackersScratch = [];   // the C++ local lektor<hand> attackers (ProtectMatchPrisoners), reused

    // C++: PrisonerUtil.cpp:135-142
    float AdvanceMatchUpkeepClock(IPrisonerHost<TId> h)
    {
        uint now = h.NowMs;
        float dt = State.MatchUpkeepLastTick == 0 ? 0.0f :
            (float)unchecked(now - State.MatchUpkeepLastTick) / 1000.0f;   // DWORD subtraction, then float
        State.MatchUpkeepLastTick = now;
        return dt > 1.0f ? 1.0f : dt;
    }

    // C++: PrisonerUtil.cpp:509-515 (callers: IssueLiftOrder, TickRingsideAid, SecureReturnedPrisoner and BeginReturnToCages, task 6.3)
    static void ClearParkOrders(IPrisonerHost<TId> h, TId c)
    {
        if (h.IsNull(c) || !h.Valid(c))
            return;
        h.SetMoveStance(c);   // setStandingOrder(HOLD, false); setStandingOrder(PASSIVE, false)
    }

    // C++: PrisonerUtil.cpp:517-527
    static void ApplyParkOrders(IPrisonerHost<TId> h, TId c)
    {
        if (h.IsNull(c) || !h.Valid(c) || h.IsDead(c))
            return;
        // Drop lingering sit-around / job AI before freezing in place.
        h.ParkInPlace(c);   // clearAllAIGoals; PASSIVE on; HOLD on; AGG off; reThinkCurrentAIAction
    }

    // C++: PrisonerUtil.cpp:529-537
    static void ReinforceParkOrders(IPrisonerHost<TId> h, TId c)
    {
        if (h.IsNull(c) || !h.Valid(c) || h.IsDead(c))
            return;
        // Re-assert hold without wiping medical / escort AI mid-match.
        h.ReassertPark(c);  // PASSIVE on; HOLD on; AGG off
    }

    // C++: PrisonerUtil.cpp:786-800
    static void DisengageOutsiderFromPrisoner(IPrisonerHost<TId> h, TId outsider, TId prisoner)
    {
        if (h.IsNull(outsider) || !h.Valid(outsider) || h.IsNull(prisoner) || !h.Valid(prisoner))
            return;
        if (h.IsSparringOpponent(outsider, prisoner))
            return;

        h.ClearTempEnemyStatus(outsider, prisoner);
        h.ClearTempEnemyStatus(prisoner, outsider);
        h.RemoveMeleeAttackJobs(outsider);   // removeJob(FOCUSED_MELEE_ATTACK); removeJob(UNPROVOKED_FOCUSED_MELEE_ATTACK)
        TId target = h.AttackTarget(outsider);
        if (!h.IsNull(target) && Eq.Equals(target, prisoner))
            h.EndCombatMode(outsider);
    }

    /// <summary>C++ <c>ResolveReturnReferences(entry)</c> (PrisonerUtil.cpp:595-604), read by ProtectMatchPrisoners while a return runs; its
    /// body is in PrisonerUtilLogic.Return.cs (task 6.3).</summary>
    partial void ResolveReturnReferences(IPrisonerHost<TId> h, MatchEntry<TId> entry);

    // C++: PrisonerUtil.cpp:1259-1309
    public void ProtectMatchPrisoners(IPrisonerHost<TId> h)
    {
        if (!State.MatchHasPrisoner)
            return;

        for (int i = 0; i < State.MatchEntries.Count; ++i)
        {
            MatchEntry<TId> entry = State.MatchEntries[i];
            if (State.Returning)
            {
                ResolveReturnReferences(h, entry);
                if (entry.LockPending || !entry.ReturnPending) continue;
            }
            TId prisoner = entry.Prisoner;
            if (h.IsNull(prisoner) || !h.Valid(prisoner) || h.IsDead(prisoner))
                continue;
            if (!entry.Released)
                continue;

            var attackers = attackersScratch;
            attackers.Clear();
            h.AllAttackers(prisoner, attackers);
            for (int a = 0; a < attackers.Count; ++a)
            {
                TId outsider = attackers[a];
                DisengageOutsiderFromPrisoner(h, outsider, prisoner);
            }

            // If the prisoner is chasing a non-match target, break it off.
            TId target = h.AttackTarget(prisoner);
            if (!h.IsNull(target))
            {
                if (!h.IsSparringOpponent(prisoner, target))
                {
                    h.ClearTempEnemyStatus(prisoner, target);
                    h.RemoveMeleeAttackJobs(prisoner);
                    h.EndCombatMode(prisoner);
                }
            }

            // Mark nearby squad handlers as temp-allies of the prisoner so AI
            // is less eager to pull them into world fights.
            TId handler = entry.Handler;
            if (!h.IsNull(handler) && h.Valid(handler))
            {
                h.RememberTemporaryAlly(handler, prisoner);
                h.RememberTemporaryAlly(prisoner, handler);
            }
        }
    }

    // C++: PrisonerUtil.cpp:1370-1374
    public void ParkMatchHandlers(IPrisonerHost<TId> h)
    {
        for (int i = 0; i < State.MatchHandlers.Count; ++i)
            ApplyParkOrders(h, State.MatchHandlers[i]);
    }

    // C++: PrisonerUtil.cpp:1376-1380
    public void ReinforceParkedHandlers(IPrisonerHost<TId> h)
    {
        for (int i = 0; i < State.MatchHandlers.Count; ++i)
            ReinforceParkOrders(h, State.MatchHandlers[i]);
    }

    // C++: PrisonerUtil.cpp:1382-1405
    public void TickMatchUpkeep(IPrisonerHost<TId> h)
    {
        if (!State.MatchHasPrisoner)
        {
            ResetMatchUpkeep();
            return;
        }

        float dt = AdvanceMatchUpkeepClock(h);
        if (State.ProtectionImmediate ||
            FrameCadencePolicy.Advance(
                ref State.ProtectionElapsedSec, dt, ProtectionIntervalSec))
        {
            ProtectMatchPrisoners(h);
            State.ProtectionImmediate = false;
        }

        if (h.SparIsActive() &&
            FrameCadencePolicy.Advance(
                ref State.HandlerElapsedSec, dt, HandlerReinforceIntervalSec))
        {
            ReinforceParkedHandlers(h);
        }
    }
}
