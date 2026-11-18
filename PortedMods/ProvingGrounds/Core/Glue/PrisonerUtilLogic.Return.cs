using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Task 6.3 (PA §2.19, §2.11; CAP 12, 15; spike S11): ringside aid, the return to the cages and the lock check (PrisonerUtil.cpp: the constants
// :111-113 and :117-119, IsConsciousPrisoner / GetNativeLockVerifier :539-561, ReportCageReturnFailure .. BeginReturnMovement :563-735,
// IssueLiftOrder :768-784, TickRingsideAid :1311-1368, BeginReturnToCages / TickReturn :1407-1725, TickPendingCageLocks / ReturnAllToCages
// :1742-1774). Every GetTickCount is the host's NowMs (PgClock, sim ms). The engine half is src/Game/PrisonerUtil.Return.cs.
//
// The lock check: the C++ placed the prisoner (setPrisonMode) and then ordered the handler to lock the cage (LOCK_DOOR), verifying with the
// native AI::isDoorLocked for up to 30 s. Meitou's Fixtures.Imprison locks the cage itself (spike S11: locked and effectively locked right
// after), so the first AdvanceCageLock after the placement finds it Secured. The state machine below is the C++'s, unchanged; what Meitou makes
// a pass-through is the host's (src/Game/PrisonerUtil.Return.cs): no lock order exists (IssueLockOrder gives none and LockOrderRejected is
// never true), so the IssueOrder state is reached only when the placed cage's lock does not count as locked (a broken or level-0 lock) and
// then waits the 30 s for the "did not engage" failure, as the C++'s lock order on such a lock would (docs/divergences.md "PrisonerUtil (task
// 6.3").

public sealed partial class PrisonerUtilLogic<TId>
{
    /// <summary>C++ <c>kReturnArriveRadius</c> (PrisonerUtil.cpp:111).</summary>
    public const float ReturnArriveRadius = 36.0f;
    /// <summary>C++ <c>kReturnTimeoutSec</c> (PrisonerUtil.cpp:112).</summary>
    public const float ReturnTimeoutSec = 60.0f;
    /// <summary>C++ <c>kLockTimeoutMs</c> (PrisonerUtil.cpp:113).</summary>
    public const uint LockTimeoutMs = 30000;
    /// <summary>C++ <c>kCarryPickupRetrySec</c> (PrisonerUtil.cpp:117).</summary>
    public const float CarryPickupRetrySec = 2.5f;
    /// <summary>C++ <c>kTreatmentTimeoutSec</c> (PrisonerUtil.cpp:118).</summary>
    public const float TreatmentTimeoutSec = 20.0f;
    /// <summary>C++ <c>kTreatmentRetrySec</c> (PrisonerUtil.cpp:119).</summary>
    public const float TreatmentRetrySec = 3.0f;

    // C++: PrisonerUtil.cpp:539-542
    static bool IsConsciousPrisoner(IPrisonerHost<TId> h, TId c) =>
        !h.IsNull(c) && h.Valid(c) && !h.IsDead(c) && !h.IsUnconscious(c);

    // C++: PrisonerUtil.cpp:563-574
    void ReportCageReturnFailure(IPrisonerHost<TId> h, TId prisoner, string reason)
    {
        string message = "Could not secure ";
        message += !h.IsNull(prisoner) && h.Valid(prisoner) ? h.Name(prisoner) : "prisoner";
        message += ": ";
        message += reason;
        State.CageReturnFailed = true;
        if (State.CageReturnStatus.Length != 0) State.CageReturnStatus += "\n";
        State.CageReturnStatus += message;
        h.Debug("Proving Grounds: " + message);
        h.ShowPlayerMessage(message);
    }

    // C++: PrisonerUtil.cpp:576-591
    /// <summary>The C++ failure texts of each lock outcome, byte for byte.</summary>
    public static string CageLockFailureReason(PrisonerMatchLogic.CageLockAction action) => action switch
    {
        PrisonerMatchLogic.CageLockAction.MissingDestination => "destination cage or pole is missing or destroyed",
        PrisonerMatchLogic.CageLockAction.WrongOccupant => "destination does not contain the expected prisoner",
        PrisonerMatchLogic.CageLockAction.MissingLock => "destination has no operable lock",
        PrisonerMatchLogic.CageLockAction.Broken => "destination cage or pole is broken",
        PrisonerMatchLogic.CageLockAction.NoVerifier => "native cage lock verification is unavailable",
        PrisonerMatchLogic.CageLockAction.NoHandler => "no conscious handler can lock the destination",
        PrisonerMatchLogic.CageLockAction.OrderFailed => "native cage lock order was rejected",
        PrisonerMatchLogic.CageLockAction.TimedOut => "native lock did not engage before timeout (blocked or broken lock)",
        _ => "native cage lock failed",
    };

    // C++: PrisonerUtil.cpp:593-604
    // Resolves each handle afresh so destroyed/unloaded furniture or actors cannot leave a pending operation dereferencing its old raw pointer.
    // (A character id is its own handle: a stale one stays and reads as not Valid, the C++'s NULL from hand::getCharacter.)
    partial void ResolveReturnReferences(IPrisonerHost<TId> h, MatchEntry<TId> entry)
    {
        entry.Prisoner = entry.PrisonerHand;
        entry.Handler = entry.HandlerHand;
        entry.ReturnCageBuilding = entry.ReturnCageHand;
        string? building = entry.ReturnCageBuilding;
        if (building is not null && (!h.BuildingValid(building) || h.IsDestroyed(building))) building = null;
        entry.ReturnCageBuilding = building;
        entry.ReturnCage = building is not null ? AsUseableCage(h, building) : null;
    }

    // C++: PrisonerUtil.cpp:606-650
    bool SecureReturnedPrisoner(IPrisonerHost<TId> h, MatchEntry<TId> returning, out string statusOut)
    {
        statusOut = "";
        MatchEntry<TId> entry = returning;
        TId prisoner = entry.Prisoner;
        string? cage = entry.ReturnCage;
        TId handler = entry.Handler;
        var observation = new PrisonerMatchLogic.CageLockObservation();
        observation.DestinationValid = cage is not null && h.BuildingValid(cage) && !h.IsDestroyed(cage);
        observation.CorrectOccupant = observation.DestinationValid && !h.IsNull(prisoner) &&
            h.Valid(prisoner) && !h.IsDead(prisoner) &&
            Eq.Equals(h.Occupant(cage!), prisoner);
        observation.HasLock = observation.DestinationValid && h.HasLock(cage!);
        observation.Broken = observation.DestinationValid && h.IsBroken(cage!);
        observation.HandlerAvailable = IsConsciousPrisoner(h, handler) && h.HasAI(handler);
        bool verify = h.LockVerifierAvailable();   // NativeLockPredicate verify = GetNativeLockVerifier() (C++: PrisonerUtil.cpp:544-561)
        bool ai = observation.HandlerAvailable ||   // AI* ai = handlerAvailable ? handler->getAI() : (prisoner valid ? prisoner->getAI() : NULL)
            (!h.IsNull(prisoner) && h.Valid(prisoner) && h.HasAI(prisoner));
        observation.VerifierAvailable = verify && ai;
        observation.Locked = observation.DestinationValid && observation.HasLock &&
            observation.VerifierAvailable &&
            h.IsDoorLocked(cage!);
        observation.OrderIssued = entry.LockOrderIssued;
        observation.TimedOut = unchecked(h.NowMs - entry.LockStartedAt) >= LockTimeoutMs;   // DWORD subtraction
        PrisonerMatchLogic.CageLockAction action = PrisonerMatchLogic.ChooseCageLockAction(observation);
        if (action == PrisonerMatchLogic.CageLockAction.IssueOrder)
        {
            // addOrder copies SUBJECT into the queued hand (native 0x5D1799). Native factory 0x32EB90 maps LOCK_DOOR (0x4D) to Task_LockDoor;
            // its update 0x337630 resolves that building and calls its virtual getDoorLock. HERE predicates use the actor's current building.
            // Never send a null/broad cage target.
            ClearParkOrders(h, handler);
            observation.OrderRejected = h.LockOrderRejected(handler, cage!);
            if (observation.OrderRejected)
                action = PrisonerMatchLogic.ChooseCageLockAction(observation);
            else
            {
                h.IssueLockOrder(handler, cage!);
                entry.LockOrderIssued = true;
                action = PrisonerMatchLogic.CageLockAction.Wait;
            }
        }
        if (action == PrisonerMatchLogic.CageLockAction.Secured)
            return true;
        if (action != PrisonerMatchLogic.CageLockAction.Wait)
            statusOut = CageLockFailureReason(action);
        return false;
    }

    // C++: PrisonerUtil.cpp:652-664
    void AdvanceCageLock(IPrisonerHost<TId> h, MatchEntry<TId> entry)
    {
        ResolveReturnReferences(h, entry);
        bool secured = SecureReturnedPrisoner(h, entry, out string failure);
        if (secured || failure.Length != 0)
        {
            entry.LockPending = false;
            entry.ReturnPending = false;
            if (entry.LockOrderIssued) ClearHandlerOrderQueue(h, entry.Handler);
            if (failure.Length != 0) ReportCageReturnFailure(h, entry.Prisoner, failure);
        }
    }

    // C++: PrisonerUtil.cpp:666-698
    void FinishCageEntry(IPrisonerHost<TId> h, MatchEntry<TId> entry)
    {
        if (entry.LockPending) return;
        ResolveReturnReferences(h, entry);
        TId prisoner = entry.Prisoner;
        TId handler = entry.Handler;
        string? cage = entry.ReturnCage;

        if (!h.IsNull(handler) && h.Valid(handler))
        {
            if (h.IsCarryingSomething(handler))
                h.DropCarriedObject(handler);
            ClearHandlerOrderQueue(h, handler);
        }

        // Never replace a different live occupant during emergency placement.
        bool canPlace = cage is not null && !h.IsBroken(cage) &&
            (IsCageFree(h, cage) || Eq.Equals(h.Occupant(cage), prisoner));
        if (!h.IsNull(prisoner) && h.Valid(prisoner) && !h.IsDead(prisoner) && canPlace)
        {
            h.Imprison(prisoner, cage!);
            h.SetChainedMode(prisoner);
        }
        entry.ReturnEscorting = false;
        entry.ReturnCarrying = false;
        entry.ReturnPickedUp = false;
        entry.ReturnTreating = false;
        entry.ReturnPending = true;
        entry.LockPending = true;
        entry.LockOrderIssued = false;
        entry.LockStartedAt = h.NowMs;
        AdvanceCageLock(h, entry);
    }

    // C++: PrisonerUtil.cpp:700-735
    void BeginReturnMovement(IPrisonerHost<TId> h, MatchEntry<TId> entry)
    {
        TId prisoner = entry.Prisoner;
        TId handler = entry.Handler;
        entry.ReturnTreating = false;
        entry.ReturnElapsed = 0.0f;

        if (!h.IsNull(prisoner) && h.Valid(prisoner))
            h.ClearAIGoals(prisoner);   // clearAllAIGoals(); reThinkCurrentAIAction()
        if (!h.IsNull(handler) && h.Valid(handler) && !h.IsDead(handler))
            h.ClearAIGoals(handler);

        if (entry.ReturnEscorting)
        {
            if (entry.ReturnCageBuilding is { } cageBuilding)
            {
                Vector3 cagePos = h.BuildingPosition(cageBuilding);
                IssueRun(h, prisoner, cagePos);
                IssueRun(h, handler, HandlerTransitTarget(h, entry, cagePos, 0));
            }
            return;
        }

        if (entry.ReturnCarrying)
        {
            if (!h.IsNull(handler) && h.Valid(handler) && !h.IsDead(handler))
                IssueLiftOrder(h, handler, prisoner);
            else
                FinishCageEntry(h, entry);
            return;
        }

        FinishCageEntry(h, entry);
    }

    // C++: PrisonerUtil.cpp:768-784
    static void IssueLiftOrder(IPrisonerHost<TId> h, TId handler, TId prisoner)
    {
        if (h.IsNull(handler) || !h.Valid(handler) || h.IsNull(prisoner) || !h.Valid(prisoner))
            return;

        ClearParkOrders(h, handler);
        h.LiftPerson(handler, prisoner);   // addOrder(NULL, LIFT_PERSON_PLAYER_ORDER, prisoner, false, true, prisoner->getPosition())
        // Let LIFT_PERSON_PLAYER_ORDER walk to and pick up the prisoner.
        // Calling pickupObject here relocates a KO'd prisoner immediately.
    }

    // C++: PrisonerUtil.cpp:1311-1368
    public void TickRingsideAid(IPrisonerHost<TId> h)
    {
        if (!h.SparIsActive() || !State.MatchHasPrisoner ||
            !h.ShouldStabilizePrisoner())
        {
            State.RingsideAidLastTick = 0;
            return;
        }

        uint now = h.NowMs;
        float dt = State.RingsideAidLastTick == 0 ? 0.0f :
            (float)unchecked(now - State.RingsideAidLastTick) / 1000.0f;   // DWORD subtraction, then float
        State.RingsideAidLastTick = now;
        if (dt < 0.0f) dt = 0.0f;
        if (dt > 1.0f) dt = 1.0f;

        for (int i = 0; i < State.MatchEntries.Count; ++i)
        {
            MatchEntry<TId> entry = State.MatchEntries[i];
            TId prisoner = entry.Prisoner;
            TId handler = entry.Handler;
            bool eligible = !h.IsNull(prisoner) && h.Valid(prisoner) &&
                !h.IsDead(prisoner) && h.IsEliminated(prisoner) &&
                h.NeedsStabilization(prisoner);
            if (!eligible)
            {
                if (entry.RingsideTreating)
                {
                    entry.RingsideTreating = false;
                    entry.RingsideTreatElapsed = 0.0f;
                    ApplyParkOrders(h, handler);
                }
                continue;
            }
            if (h.IsNull(handler) || !h.Valid(handler) || h.IsDead(handler))
                continue;

            float previous = entry.RingsideTreatElapsed;
            entry.RingsideTreatElapsed += dt;
            if (!entry.RingsideTreating)
            {
                entry.RingsideTreating = true;
                entry.RingsideTreatElapsed = 0.0f;
                h.ClearAIGoals(handler);   // clearAllAIGoals(); reThinkCurrentAIAction()
                ClearParkOrders(h, handler);
                h.IssueStabilizeOrder(handler, prisoner);
                continue;
            }
            int retry = (int)(entry.RingsideTreatElapsed / TreatmentRetrySec);
            int previousRetry = (int)(previous / TreatmentRetrySec);
            if (retry != previousRetry)
            {
                ClearParkOrders(h, handler);
                h.IssueStabilizeOrder(handler, prisoner);
            }
        }
    }

    // C++: PrisonerUtil.cpp:1407-1575
    public void BeginReturnToCages(IPrisonerHost<TId> h, out string statusOut)
    {
        statusOut = State.CageReturnStatus;
        // Cancellation/pre-save may revisit a return. Never replace its pending destination, handler, native order, or timeout with a fresh
        // attempt.
        if (State.Returning || HasPendingCageLocks()) return;
        State.Returning = false;
        State.ReturnLastTick = 0;
        State.Unlocking = false;
        for (int i = 0; i < State.MatchEntries.Count; ++i)
            State.MatchEntries[i].UnlockPending = false;

        if (State.MatchEntries.Count == 0)
        {
            ClearMatchState(h);
            return;
        }

        ArenaBuilding? registry = h.BoundRegistry();
        var cages = new List<NearbyCage>();
        CollectCagesNearRegistry(h, registry, cages);

        int count = cages.Count;
        if (count <= 0)
        {
            for (int i = 0; i < State.MatchEntries.Count; ++i)
            {
                TId prisoner = State.MatchEntries[i].PrisonerHand;
                ReportCageReturnFailure(h, prisoner, "no destination cage or pole is available");
            }
            statusOut = State.CageReturnStatus;
            ClearMatchState(h);
            return;
        }

        var free = new bool[count];
        var distToPrisonerSq = new float[count];
        var inRegistryRange = new bool[count];
        var alreadyReserved = new bool[count];

        for (int i = 0; i < count; ++i)
        {
            inRegistryRange[i] = registry is not null
                ? IsWithinRegistryRange(h, registry, cages[i].Building)
                : false;
            free[i] = IsCageFree(h, cages[i].Useable);
            alreadyReserved[i] = false;
        }

        bool anyPending = false;

        for (int e = 0; e < State.MatchEntries.Count; ++e)
        {
            MatchEntry<TId> entry = State.MatchEntries[e];
            entry.ReturnPending = false;
            entry.ReturnEscorting = false;
            entry.ReturnCarrying = false;
            entry.ReturnPickedUp = false;
            entry.ReturnTreating = false;
            entry.ReturnElapsed = 0.0f;
            entry.ReturnCage = null;
            entry.ReturnCageBuilding = null;

            entry.Prisoner = entry.PrisonerHand;
            entry.Handler = entry.HandlerHand;
            TId prisoner = entry.Prisoner;
            if (h.IsNull(prisoner) || !h.Valid(prisoner) || h.IsDead(prisoner))
            {
                ReportCageReturnFailure(h, prisoner, "prisoner is missing or dead");
                continue;
            }

            // An aborted unlock may leave an occupied yet unlocked cage.
            // Verify it through the same placement/lock path as ordinary return.
            if (!entry.Released)
            {
                entry.ReturnCageHand = entry.CageHand;
                FinishCageEntry(h, entry);
                if (entry.ReturnPending) anyPending = true;
                continue;
            }

            // The fresh cage scan may have a different order after destruction.
            // cageHand.getBuilding(): the C++ NULLs only a missing building; here a no-longer-valid one is dropped too. The result is the same:
            // FindCageIndex looks it up in this scan's cages, which hold valid cages only, so an invalid cage could never be found or chosen.
            string? originalBuilding = entry.CageHand is { } hand && h.BuildingValid(hand) ? hand : null;
            entry.OriginalCageIndex = FindCageIndex(AsUseableCage(h, originalBuilding), cages);
            Vector3 prisonerPos = h.Position(prisoner);
            for (int i = 0; i < count; ++i)
            {
                distToPrisonerSq[i] = HorizontalDistSq(
                    prisonerPos,
                    h.BuildingPosition(cages[i].Building));
            }

            // Original cage is free again once the prisoner left it.
            if (entry.OriginalCageIndex >= 0 &&
                entry.OriginalCageIndex < count)
            {
                free[entry.OriginalCageIndex] = IsCageFree(h,
                    cages[entry.OriginalCageIndex].Useable);
            }

            PrisonerMatchLogic.CagePick pick = PrisonerMatchLogic.ChooseReturnCage(
                entry.OriginalCageIndex,
                free,
                distToPrisonerSq,
                inRegistryRange,
                alreadyReserved,
                count);

            if (pick.Index < 0)
            {
                ReportCageReturnFailure(h, prisoner, "no free destination cage or pole is available");
                continue;
            }

            alreadyReserved[pick.Index] = true;
            string cage = cages[pick.Index].Useable;
            entry.ReturnCage = cage;
            entry.ReturnCageBuilding = cage;
            entry.ReturnCageHand = cage;

            PrisonerMatchLogic.ReturnAction action =
                PrisonerMatchLogic.ChooseReturnAction(
                    IsConsciousPrisoner(h, prisoner),
                    h.IsDead(prisoner));

            TId handler = entry.Handler;
            if (!h.IsNull(handler) && h.Valid(handler))
                ClearParkOrders(h, handler);

            entry.ReturnEscorting = action == PrisonerMatchLogic.ReturnAction.EscortWalk;
            entry.ReturnCarrying = action == PrisonerMatchLogic.ReturnAction.Carry;
            entry.ReturnPending = true;

            bool canTreat = !h.IsNull(handler) && h.Valid(handler) && !h.IsDead(handler);
            if (h.ShouldStabilizePrisoner() &&
                canTreat && h.NeedsStabilization(prisoner))
            {
                entry.ReturnTreating = true;
                entry.ReturnElapsed = 0.0f;
                anyPending = true;
                h.IssueStabilizeOrder(handler, prisoner);
            }
            else
            {
                BeginReturnMovement(h, entry);
                if (entry.ReturnPending)
                    anyPending = true;
            }
        }

        if (State.CageReturnFailed) statusOut = State.CageReturnStatus;
        if (anyPending)
        {
            State.Returning = true;
            State.ReturnLastTick = h.NowMs;
            if (statusOut.Length == 0)
                statusOut = "Returning prisoners to cages...";
        }
        else
        {
            ClearMatchState(h);
        }
    }

    // C++: PrisonerUtil.cpp:1577-1725
    public void TickReturn(IPrisonerHost<TId> h)
    {
        TickPendingCageLocks(h);
        if (!State.Returning)
            return;

        uint now = h.NowMs;
        float dt = 0.0f;
        if (State.ReturnLastTick != 0)
            dt = (float)unchecked(now - State.ReturnLastTick) / 1000.0f;   // DWORD subtraction, then float
        State.ReturnLastTick = now;
        if (dt < 0.0f)
            dt = 0.0f;
        if (dt > 1.0f)
            dt = 1.0f;

        const float arriveSq = ReturnArriveRadius * ReturnArriveRadius;
        bool anyPending = false;

        for (int e = 0; e < State.MatchEntries.Count; ++e)
        {
            MatchEntry<TId> entry = State.MatchEntries[e];
            if (!entry.ReturnPending || entry.LockPending)
                continue;

            ResolveReturnReferences(h, entry);
            TId prisoner = entry.Prisoner;
            TId handler = entry.Handler;
            entry.ReturnElapsed += dt;

            if (h.IsNull(prisoner) || !h.Valid(prisoner) || h.IsDead(prisoner))
            {
                entry.ReturnPending = false;
                ReportCageReturnFailure(h, prisoner, "prisoner is missing or dead");
                continue;
            }
            if (entry.ReturnCage is null || h.IsBroken(entry.ReturnCage))
            {
                FinishCageEntry(h, entry);
                continue;
            }

            bool timedOut = entry.ReturnElapsed >= ReturnTimeoutSec;

            if (entry.ReturnTreating)
            {
                bool treatmentTimedOut = entry.ReturnElapsed >= TreatmentTimeoutSec;
                bool medicUnavailable = h.IsNull(handler) || !h.Valid(handler) || h.IsDead(handler);
                if (!h.NeedsStabilization(prisoner) ||
                    treatmentTimedOut || medicUnavailable)
                {
                    BeginReturnMovement(h, entry);
                    if (entry.ReturnPending)
                        anyPending = true;
                    continue;
                }

                int slot = (int)(entry.ReturnElapsed / TreatmentRetrySec);
                int prevSlot = (int)((entry.ReturnElapsed - dt) / TreatmentRetrySec);
                if (slot != prevSlot)
                    h.IssueStabilizeOrder(handler, prisoner);
                anyPending = true;
                continue;
            }

            if (entry.ReturnCarrying)
            {
                bool carrying =
                    (!h.IsNull(handler) && h.Valid(handler) &&
                        (h.IsCarryingSomething(handler) || h.IsBeingCarried(prisoner)));

                if (!entry.ReturnPickedUp)
                {
                    if (carrying)
                    {
                        entry.ReturnPickedUp = true;
                        if (entry.ReturnCageBuilding is { } cb && !h.IsNull(handler))
                            IssueMove(h, handler, h.BuildingPosition(cb));
                    }
                    else if (timedOut)
                    {
                        FinishCageEntry(h, entry);
                        continue;
                    }
                    else if (!h.IsNull(handler) && h.Valid(handler) && !h.IsDead(handler))
                    {
                        int slot = (int)(
                            entry.ReturnElapsed / CarryPickupRetrySec);
                        int prevSlot = (int)(
                            (entry.ReturnElapsed - dt) / CarryPickupRetrySec);
                        if (slot != prevSlot)
                            IssueLiftOrder(h, handler, prisoner);
                    }
                    anyPending = true;
                    continue;
                }

                bool carriedArrived = false;
                if (!h.IsNull(handler) && h.Valid(handler) && entry.ReturnCageBuilding is { } cb2)
                {
                    carriedArrived = HorizontalDistSq(
                        h.Position(handler),
                        h.BuildingPosition(cb2)) <= arriveSq;
                }

                if (carriedArrived || timedOut)
                {
                    FinishCageEntry(h, entry);
                    continue;
                }

                if (!h.IsNull(handler) && h.Valid(handler) && entry.ReturnCageBuilding is { } cb3)
                    IssueMove(h, handler, h.BuildingPosition(cb3));
                anyPending = true;
                continue;
            }

            // Walking escort path.
            bool arrived = false;
            if (entry.ReturnCageBuilding is { } cb4)
            {
                arrived = HorizontalDistSq(
                    h.Position(prisoner),
                    h.BuildingPosition(cb4)) <= arriveSq;
            }

            if (arrived || timedOut)
            {
                FinishCageEntry(h, entry);
                continue;
            }

            if (entry.ReturnCageBuilding is { } cb5)
            {
                Vector3 cagePos = h.BuildingPosition(cb5);
                IssueRun(h, prisoner, cagePos);
                IssueRun(h, handler, HandlerTransitTarget(h, entry, cagePos, e));
            }
            anyPending = true;
        }

        for (int e = 0; e < State.MatchEntries.Count; ++e)
            if (State.MatchEntries[e].ReturnPending) anyPending = true;
        if (!anyPending)
        {
            State.Returning = false;
            ClearMatchState(h);
        }
    }

    // C++: PrisonerUtil.cpp:1742-1756
    public void TickPendingCageLocks(IPrisonerHost<TId> h)
    {
        bool anyPending = false;
        for (int e = 0; e < State.MatchEntries.Count; ++e)
        {
            MatchEntry<TId> entry = State.MatchEntries[e];
            if (entry.LockPending) AdvanceCageLock(h, entry);
            if (entry.ReturnPending) anyPending = true;
        }
        if (State.Returning && !anyPending)
        {
            State.Returning = false;
            ClearMatchState(h);
        }
    }

    // C++: PrisonerUtil.cpp:1758-1774
    public void ReturnAllToCages(IPrisonerHost<TId> h, out string statusOut)
    {
        // Force placement, then retain native lock work until verified/failed.
        BeginReturnToCages(h, out statusOut);
        bool anyPending = false;
        for (int e = 0; e < State.MatchEntries.Count; ++e)
        {
            MatchEntry<TId> entry = State.MatchEntries[e];
            if (entry.ReturnPending && !entry.LockPending) FinishCageEntry(h, entry);
            if (entry.ReturnPending) anyPending = true;
        }
        State.Returning = anyPending;
        if (!anyPending) ClearMatchState(h);
        if (State.CageReturnFailed) statusOut = State.CageReturnStatus;
        else if (anyPending) statusOut = "Waiting for native cage locks before return completes...";
        else statusOut = "";
    }
}
