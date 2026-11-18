using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Task 6.2 (PA §2.19, CAP 15, spike S11): the handler walk to the cage and the release (PrisonerUtil.cpp: the constants :114-116 and :122-133,
// the helpers IssueMove / IssueRun / ClearHandlerOrderQueue / HandlerTransitTarget :443-507 and ReleasePrisonerFromCage :746-766, the
// functions BeginHandlerUnlocks .. SendPairToArena :1110-1257). Every GetTickCount is the host's NowMs (PgClock, sim ms). The engine half is
// src/Game/PrisonerUtil.Release.cs. IssueLiftOrder (:768-784, only called by the return) is PrisonerUtilLogic.Return.cs (task 6.3).

public sealed partial class PrisonerUtilLogic<TId>
{
    /// <summary>C++ <c>kUnlockArriveRadius</c> (PrisonerUtil.cpp:114).</summary>
    public const float UnlockArriveRadius = 120.0f;
    /// <summary>C++ <c>kUnlockHoldSec</c> (PrisonerUtil.cpp:115).</summary>
    public const float UnlockHoldSec = 0.45f;
    /// <summary>C++ <c>kUnlockTimeoutSec</c> (PrisonerUtil.cpp:116).</summary>
    public const float UnlockTimeoutSec = 45.0f;
    /// <summary>C++ <c>kHandlerSideOffset</c> (PrisonerUtil.cpp:122).</summary>
    public const float HandlerSideOffset = 28.0f;
    /// <summary>C++ <c>kHandlerLeadOffset</c> (PrisonerUtil.cpp:123).</summary>
    public const float HandlerLeadOffset = 8.0f;

    /// <summary>C++ <c>kPrisonerFightLines</c> (PrisonerUtil.cpp:124-133), byte for byte.</summary>
    public static readonly string[] PrisonerFightLines =
    [
        "Finally. Let's make this count.",
        "Thought you'd never ask.",
        "Who am I fighting?",
        "Open the way. I'm ready.",
        "Try not to blink.",
        "Freedom first. Questions later.",
        "Let's get this over with.",
        "This should be interesting.",
    ];

    // C++: PrisonerUtil.cpp:443-457
    static void IssueMove(IPrisonerHost<TId> h, TId c, Vector3 loc)
    {
        if (h.IsNull(c) || !h.Valid(c))
            return;

        h.SetMoveStance(c);   // setStandingOrder(HOLD, false); setStandingOrder(PASSIVE, false)

        h.MoveTo(c, loc);     // movement->setDestination(loc, HIGH_PRIORITY, true); addOrder(MOVE_CUS_ORDERED); setDestination(loc, false)
    }

    // C++: PrisonerUtil.cpp:459-470
    static void IssueRun(IPrisonerHost<TId> h, TId c, Vector3 loc)
    {
        IssueMove(h, c, loc);
        if (h.IsNull(c) || !h.Valid(c))
            return;
        h.SetRunSpeed(c);     // movement->setDesiredSpeedOrders(RUN); movement->setDesiredSpeed(RUN)
    }

    // C++: PrisonerUtil.cpp:472-479
    static void ClearHandlerOrderQueue(IPrisonerHost<TId> h, TId handler)
    {
        if (h.IsNull(handler) || !h.Valid(handler))
            return;
        h.ClearOrderQueue(handler);   // addOrder(NULL, MOVE_CUS_ORDERED, NULL, false, true, pos); removeJob(MOVE_CUS_ORDERED)
    }

    /// <summary>The cage of an entry as the C++ dereferences it (<c>static_cast&lt;Building*&gt;(entry.cageUseable)</c>): its key, or null. A
    /// key that names nothing any more reads as null here (the C++ would read a dangling pointer; the port never reads a stale key's position).</summary>
    static string? CageBuildingOf(IPrisonerHost<TId> h, MatchEntry<TId> entry) =>
        entry.CageUseable is { } cage && h.BuildingValid(cage) ? cage : null;

    // C++: PrisonerUtil.cpp:481-507
    static Vector3 HandlerTransitTarget(IPrisonerHost<TId> h, MatchEntry<TId> entry, Vector3 destination, int entryIndex)
    {
        Vector3 origin = destination;
        if (CageBuildingOf(h, entry) is { } cage)
            origin = h.BuildingPosition(cage);
        else if (!h.IsNull(entry.Prisoner) && h.Valid(entry.Prisoner))
            origin = h.Position(entry.Prisoner);
        Vector3 direction = destination - origin;
        direction.Y = 0.0f;
        float lenSq = direction.X * direction.X + direction.Z * direction.Z;
        var side = new Vector3(1.0f, 0.0f, 0.0f);
        if (lenSq > 1.0f)
        {
            float invLen = 1.0f / MathF.Sqrt(lenSq);
            direction.X *= invLen;
            direction.Z *= invLen;
            side.X = -direction.Z;
            side.Z = direction.X;
        }
        if ((entryIndex % 2) != 0)
            side = -side;
        return destination - (direction * HandlerLeadOffset) +
            (side * HandlerSideOffset);
    }

    /// <summary>Port only (task 9.3d): PrepareMatch's refusal when the engine cannot let a prisoner out of a cage (no C++ counterpart: Kenshi's
    /// setPrisonMode always exists).</summary>
    public const string ReleaseUnavailableStatus = "Prisoner matches unavailable: this game version cannot release prisoners from cages";

    // C++: PrisonerUtil.cpp:746-766
    void ReleasePrisonerFromCage(IPrisonerHost<TId> h, MatchEntry<TId> entry)
    {
        TId prisoner = entry.Prisoner;
        if (h.IsNull(prisoner) || !h.Valid(prisoner) || entry.Released)
            return;

        entry.WasChained = h.IsChainedMode(prisoner);
        ClearHandlerOrderQueue(h, entry.Handler);
        h.LeavePrison(prisoner);
        // Port only (task 9.3d): Kenshi's setPrisonMode(false) always frees; the port's release can refuse (the cage release part off, a throw read as
        // refused). A prisoner still held stays unreleased and pending: the next tick tries again, and the pair is not sent to the arena caged.
        if (h.IsImprisoned(prisoner))
            return;
        if (entry.WasChained)
            h.ClearChainedMode(prisoner);

        entry.Released = true;
        entry.UnlockPending = false;
        entry.NewlyReleased = true;
        State.ProtectionImmediate = true;

        int lineCount = PrisonerFightLines.Length;
        h.Say(prisoner, PrisonerFightLines[h.Rand() % lineCount]);
    }

    // C++: PrisonerUtil.cpp:1110-1144
    public void BeginHandlerUnlocks(IPrisonerHost<TId> h)
    {
        if (State.MatchEntries.Count == 0)
        {
            State.Unlocking = false;
            return;
        }

        State.Unlocking = true;
        State.UnlockLastTick = h.NowMs;

        if (!State.HandlerStatesRemembered && State.MatchHandlers.Count != 0)
        {
            h.RememberMatchFighters(State.MatchHandlers);
            State.HandlerStatesRemembered = true;
        }
        for (int i = 0; i < State.MatchEntries.Count; ++i)
        {
            MatchEntry<TId> entry = State.MatchEntries[i];
            entry.Released = false;
            entry.UnlockPending = true;
            entry.UnlockArrived = false;
            entry.UnlockElapsed = 0.0f;
            entry.UnlockHoldElapsed = 0.0f;
            entry.NewlyReleased = false;

            TId handler = entry.Handler;
            string? cageBuilding = CageBuildingOf(h, entry);
            if (!h.IsNull(handler) && h.Valid(handler) && !h.IsDead(handler) && cageBuilding is not null)
                IssueMove(h, handler, h.BuildingPosition(cageBuilding));
        }
    }

    // C++: PrisonerUtil.cpp:1146-1221
    public void TickHandlerUnlocks(IPrisonerHost<TId> h)
    {
        if (!State.Unlocking)
            return;

        uint now = h.NowMs;
        float dt = 0.0f;
        if (State.UnlockLastTick != 0)
            dt = (float)unchecked(now - State.UnlockLastTick) / 1000.0f;   // DWORD subtraction, then float
        State.UnlockLastTick = now;
        if (dt < 0.0f)
            dt = 0.0f;
        if (dt > 1.0f)
            dt = 1.0f;

        const float arriveSq = UnlockArriveRadius * UnlockArriveRadius;
        bool anyPending = false;

        for (int i = 0; i < State.MatchEntries.Count; ++i)
        {
            MatchEntry<TId> entry = State.MatchEntries[i];
            if (entry.Released || !entry.UnlockPending)
                continue;

            anyPending = true;
            entry.UnlockElapsed += dt;

            TId handler = entry.Handler;
            string? cageBuilding = CageBuildingOf(h, entry);

            bool handlerNear = false;
            if (!h.IsNull(handler) && h.Valid(handler) && !h.IsDead(handler) && cageBuilding is not null)
            {
                handlerNear = HorizontalDistSq(
                    h.Position(handler),
                    h.BuildingPosition(cageBuilding)) <= arriveSq;
            }

            bool timedOut = entry.UnlockElapsed >= UnlockTimeoutSec;
            if (handlerNear)
            {
                if (!entry.UnlockArrived)
                {
                    entry.UnlockArrived = true;
                    entry.UnlockHoldElapsed = 0.0f;
                    // Fake unlock moment at the cage.
                    if (!h.IsNull(handler) && cageBuilding is not null)
                        h.IssueReleaseOrder(handler, cageBuilding, entry.Prisoner);
                }
                entry.UnlockHoldElapsed += dt;
                if (entry.UnlockHoldElapsed >= UnlockHoldSec || timedOut)
                    ReleasePrisonerFromCage(h, entry);
            }
            else if (timedOut)
            {
                ReleasePrisonerFromCage(h, entry);
            }
            else if (!h.IsNull(handler) && h.Valid(handler) && !h.IsDead(handler) && cageBuilding is not null)
            {
                IssueMove(h, handler, h.BuildingPosition(cageBuilding));
            }
        }

        if (!anyPending)
            State.Unlocking = false;
    }

    // C++: PrisonerUtil.cpp:1223-1237
    public bool ConsumeNewlyReleased(IPrisonerHost<TId> h, TId prisoner)
    {
        if (h.IsNull(prisoner))
            return false;
        for (int i = 0; i < State.MatchEntries.Count; ++i)
        {
            if (!Eq.Equals(State.MatchEntries[i].Prisoner, prisoner))
                continue;
            if (!State.MatchEntries[i].NewlyReleased)
                return false;
            State.MatchEntries[i].NewlyReleased = false;
            return true;
        }
        return false;
    }

    // C++: PrisonerUtil.cpp:1239-1257
    public void SendPairToArena(IPrisonerHost<TId> h, TId prisoner, Vector3 prisonerTarget)
    {
        if (h.IsNull(prisoner) || !h.Valid(prisoner))
            return;

        for (int i = 0; i < State.MatchEntries.Count; ++i)
        {
            MatchEntry<TId> entry = State.MatchEntries[i];
            if (!Eq.Equals(entry.Prisoner, prisoner) || !entry.Released)
                continue;

            IssueRun(h, prisoner, prisonerTarget);
            TId handler = entry.Handler;
            if (!h.IsNull(handler) && h.Valid(handler) && !h.IsDead(handler))
                IssueRun(h, handler, HandlerTransitTarget(h, entry, prisonerTarget, i));
            return;
        }
    }
}
