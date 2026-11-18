using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/PrisonerUtil.h/.cpp (Proving Grounds by Shutori, GPLv3), task 6.1 (PA §2.19, CAP 15): the C++
// decisions of the namespace over IPrisonerHost (the character, building and cage reads). The glue (src/Game/PrisonerUtil.cs, partial with
// PrisonerUtil.Discovery.cs) binds the host to a world and to CharacterIds; Core never sees an engine type. A building or cage is a string key
// (the glue's: a cage fixture id, a player site id or a town building key; null = the C++ NULL pointer), a character a TId.
// Split by the plan's groups (partial class): this file = the file-level state (PrisonerUtil.cpp:46-108), the small helpers both groups use,
// HasRuntimeActivity / AbandonWorldState / ClearMatchState and the lock/return accessors they read; PrisonerUtilLogic.Discovery.cs = the cage
// and prisoner discovery (:150-440), the roster, FindCageForOccupant, CountHandlerCandidates, PrepareMatch and the match reads (:855-1108).
// PrisonerUtilLogic.Release.cs (task 6.2) = the handler walk and the release (constants :114-116, :122-133; :443-507, :746-766,
// :1110-1257); PrisonerUtilLogic.Protection.cs (task 6.2) = parking, protection and the upkeep (constants :120-121; :135-142, :509-537,
// :786-800, :1259-1309, :1370-1405); PrisonerUtilLogic.Return.cs (task 6.3) = ringside aid, the return and the lock check (constants :111-113,
// :117-119; :539-744 without ReleasePrisonerFromCage, :768-784, :1311-1368, :1407-1725, :1742-1774).

/// <summary>Part of <see cref="IPrisonerHost{TId}"/>: the world reads and the other subsystems (task 6.1 discovery and state; task 6.2 the
/// clock, FightStarter and SparSession reads).</summary>
public interface IPrisonerWorldHost<TId>
{
    /// <summary><c>!c</c>: no character at all.</summary>
    bool IsNull(TId c);
    /// <summary><c>c-&gt;isValid()</c>.</summary>
    bool Valid(TId c);
    /// <summary><c>c-&gt;isDead()</c>.</summary>
    bool IsDead(TId c);
    /// <summary><c>c-&gt;getPosition()</c>.</summary>
    Vector3 Position(TId c);
    /// <summary><c>c-&gt;inSomething == IN_PRISON</c>.</summary>
    bool IsImprisoned(TId c);
    /// <summary><c>c-&gt;inWhat.isValid() ? c-&gt;inWhat.getBuilding() : NULL</c>: the cage the character is held in (null: unknown).</summary>
    string? HeldIn(TId c);
    /// <summary><c>ou-&gt;getCharactersWithinSphere(results, origin, radius, 0, radius, max, max, NULL)</c>; null when there is no world
    /// (<c>!ou</c>).</summary>
    IReadOnlyList<TId>? CharactersInSphere(Vector3 origin, float radius, int max);
    /// <summary>The registry is still there (<c>registry-&gt;isValid()</c>).</summary>
    bool RegistryValid(ArenaBuilding registry);
    /// <summary><c>building-&gt;isValid()</c>.</summary>
    bool BuildingValid(string building);
    /// <summary><c>building-&gt;getPosition()</c>.</summary>
    Vector3 BuildingPosition(string building);
    /// <summary><c>building-&gt;isThePlayer()</c>.</summary>
    bool IsThePlayer(string building);
    /// <summary><c>building-&gt;getSpecialFunction() == BF_CAGE</c>.</summary>
    bool IsCageFunction(string building);
    /// <summary><c>building-&gt;getUseableStuff()</c> (null: none).</summary>
    string? UseableStuffOf(string building);
    /// <summary><c>building-&gt;findAllFurnitureWithFunction(furniture, BF_CAGE)</c>: the cage furniture inside it, appended.</summary>
    void CageFurniture(string building, List<string> into);
    /// <summary><c>useable-&gt;getOccupant()</c>: the occupant's character, none when the handle is not valid.</summary>
    TId Occupant(string useable);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(results, origin, radius, BUILDING, max, NULL)</c> filtered to the C++'s BUILDING /
    /// BUILDING_FUNCTIONALITY / FOLIAGE_BUILDING types; null when there is no world (<c>!ou</c>).</summary>
    IReadOnlyList<string>? BuildingsInSphere(Vector3 origin, float radius, int max);
    /// <summary><c>PrisonerUtilInternal::CollectPlayerOwnedCageBuildings(out)</c> (PrisonerUtilPlatoon.cpp:73-100), appended.</summary>
    void CollectPlayerOwnedCageBuildings(List<string> into);
    /// <summary><c>PrisonerUtilInternal::IsBuildingOwnedByPlayer(building)</c> (PrisonerUtilPlatoon.cpp:102-118).</summary>
    bool IsBuildingOwnedByPlayer(string building);
    /// <summary><c>ArenaIngress::GetBoundRegistry()</c> (null: none).</summary>
    ArenaBuilding? BoundRegistry();
    /// <summary><c>FightStarter::DisengageMatch(&amp;handlers[0], count)</c>.</summary>
    void DisengageMatch(IReadOnlyList<TId> handlers);
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);
    /// <summary><c>GetTickCount()</c>: the sim clock's milliseconds (PgClock.NowMs, frozen while paused).</summary>
    uint NowMs { get; }
    /// <summary><c>FightStarter::RememberMatchFighters(&amp;handlers[0], count)</c> (BeginHandlerUnlocks :1123-1124).</summary>
    void RememberMatchFighters(IReadOnlyList<TId> handlers);
    /// <summary><c>SparSession::IsActive()</c>.</summary>
    bool SparIsActive();
    /// <summary><c>c-&gt;isUnconcious()</c> (IsConsciousPrisoner :539-542, task 6.3).</summary>
    bool IsUnconscious(TId c);
    /// <summary><c>c-&gt;getName()</c> (ReportCageReturnFailure :563-574, task 6.3).</summary>
    string Name(TId c);
    /// <summary><c>SparSession::IsEliminated(c)</c> (TickRingsideAid :1331, task 6.3).</summary>
    bool IsEliminated(TId c);
    /// <summary><c>ArenaMedical::ShouldStabilizePrisoner(ArenaMedical::GetProtocol())</c> (task 6.3).</summary>
    bool ShouldStabilizePrisoner();
    /// <summary><c>ArenaMedical::NeedsStabilization(c)</c> (ArenaMedical.cpp:245-249, task 6.3).</summary>
    bool NeedsStabilization(TId c);
    /// <summary><c>if (ou) ou-&gt;showPlayerAMessage(text, true)</c> (ReportCageReturnFailure :573, task 6.3).</summary>
    void ShowPlayerMessage(string text);
}

/// <summary>Part of <see cref="IPrisonerHost{TId}"/>: the prison writes and the speech of the release (task 6.2), the return's imprison and the
/// cage and lock reads of the lock check (task 6.3). A cage here is the key the logic holds (null never passed).</summary>
public interface IPrisonerCageHost<TId>
{
    /// <summary><c>handler-&gt;addOrder(cage, RELEASE_PRISONER, prisoner, false, true, cage-&gt;getPosition())</c> (TickHandlerUnlocks :1196-1202,
    /// "Fake unlock moment at the cage").</summary>
    void IssueReleaseOrder(TId handler, string cage, TId prisoner);
    /// <summary><c>c-&gt;isChainedMode()</c>.</summary>
    bool IsChainedMode(TId c);
    /// <summary><c>prisoner-&gt;setPrisonMode(false, NULL)</c> (ReleasePrisonerFromCage :754). The logic reads <see cref="IsImprisoned"/> after
    /// it (task 9.3d): a prisoner still held is not released.</summary>
    void LeavePrison(TId prisoner);
    /// <summary>Port only (task 9.3d): the engine can let a prisoner out of a cage (the port's Cages.ReleaseAvailable: Meitou's
    /// FixtureRegistry.Release resolved). Kenshi's setPrisonMode always exists; without the release a prisoner match would start with the
    /// prisoner still caged, so <see cref="PrisonerUtilLogic{TId}.PrepareMatch"/> refuses it (<see cref="PrisonerUtilLogic{TId}.ReleaseUnavailableStatus"/>).</summary>
    bool CageReleaseAvailable() => true;
    /// <summary><c>prisoner-&gt;setChainedMode(false, hand(prisoner))</c> (:755-756).</summary>
    void ClearChainedMode(TId prisoner);
    /// <summary><c>c-&gt;say(text)</c>.</summary>
    void Say(TId c, string text);
    /// <summary><c>rand()</c> of the C runtime (0..32767).</summary>
    int Rand();
    /// <summary><c>prisoner-&gt;setPrisonMode(true, cage)</c> (FinishCageEntry :690).</summary>
    void Imprison(TId prisoner, string cage);
    /// <summary><c>prisoner-&gt;setChainedMode(true, hand(prisoner))</c> (FinishCageEntry :691).</summary>
    void SetChainedMode(TId prisoner);
    /// <summary><c>building-&gt;destroyed</c> (ResolveReturnReferences :601, SecureReturnedPrisoner :615).</summary>
    bool IsDestroyed(string cage);
    /// <summary><c>cage-&gt;isBroken()</c>.</summary>
    bool IsBroken(string cage);
    /// <summary><c>cage-&gt;getDoorLock() != NULL</c>.</summary>
    bool HasLock(string cage);
    /// <summary><c>GetNativeLockVerifier() != NULL</c> (:546-561: the KenshiLib export <c>AI::isDoorLocked</c>).</summary>
    bool LockVerifierAvailable();
    /// <summary><c>c-&gt;getAI() != NULL</c> (SecureReturnedPrisoner :620-623).</summary>
    bool HasAI(TId c);
    /// <summary><c>verify(ai, entry-&gt;returnCageHand, cage-&gt;getPosition())</c>: the native "is this door locked" predicate (lock strength and a
    /// broken lock included).</summary>
    bool IsDoorLocked(string cage);
    /// <summary><c>handler-&gt;checkPlayerOrderForProblems(LOCK_DOOR, cage)</c>: true when the order is rejected.</summary>
    bool LockOrderRejected(TId handler, string cage);
    /// <summary><c>handler-&gt;addOrder(cage, LOCK_DOOR, cage, false, true, cage-&gt;getPosition())</c> (SecureReturnedPrisoner :639).</summary>
    void IssueLockOrder(TId handler, string cage);
}

/// <summary>Part of <see cref="IPrisonerHost{TId}"/>: stances, moves, runs, order queues and the handlers' park (task 6.2), the AI reset, the
/// carry and the treat orders (task 6.3).</summary>
public interface IPrisonerMotionHost<TId>
{
    /// <summary><c>setStandingOrder(HOLD, false)</c> + <c>(PASSIVE, false)</c> (IssueMove :448-449, ClearParkOrders :513-514).</summary>
    void SetMoveStance(TId c);
    /// <summary><c>movement-&gt;setDestination(loc, HIGH_PRIORITY, true)</c> + <c>addOrder(NULL, MOVE_CUS_ORDERED, NULL, false, true, loc)</c> +
    /// <c>setDestination(loc, false)</c> (IssueMove :451-456): one move order to <paramref name="location"/>.</summary>
    void MoveTo(TId c, Vector3 location);
    /// <summary><c>movement-&gt;setDesiredSpeedOrders(RUN)</c> + <c>setDesiredSpeed(RUN)</c> (IssueRun :464-469).</summary>
    void SetRunSpeed(TId c);
    /// <summary><c>addOrder(NULL, MOVE_CUS_ORDERED, NULL, false, true, pos)</c> + <c>removeJob(MOVE_CUS_ORDERED)</c> (ClearHandlerOrderQueue
    /// :476-478): the character's order queue emptied.</summary>
    void ClearOrderQueue(TId c);
    /// <summary><c>c-&gt;clearAllAIGoals()</c> + <c>setStandingOrder(PASSIVE, true)</c> + <c>(HOLD, true)</c> + <c>(AGG, false)</c> +
    /// <c>reThinkCurrentAIAction()</c> (ApplyParkOrders :522-526): the character stands still where it is.</summary>
    void ParkInPlace(TId c);
    /// <summary><c>setStandingOrder(PASSIVE, true)</c> + <c>(HOLD, true)</c> + <c>(AGG, false)</c> (ReinforceParkOrders :534-536): the hold
    /// re-asserted without clearing the AI.</summary>
    void ReassertPark(TId c);
    /// <summary><c>c-&gt;clearAllAIGoals()</c> + <c>c-&gt;reThinkCurrentAIAction()</c> (BeginReturnMovement :707-715, TickRingsideAid :1350-1351).</summary>
    void ClearAIGoals(TId c);
    /// <summary><c>c-&gt;isCarryingSomething</c>.</summary>
    bool IsCarryingSomething(TId c);
    /// <summary><c>c-&gt;isBeingCarried()</c>.</summary>
    bool IsBeingCarried(TId c);
    /// <summary><c>c-&gt;dropCarriedObject(false, false)</c> (FinishCageEntry :678-679).</summary>
    void DropCarriedObject(TId c);
    /// <summary><c>handler-&gt;addOrder(NULL, LIFT_PERSON_PLAYER_ORDER, prisoner, false, true, prisoner-&gt;getPosition())</c> (IssueLiftOrder
    /// :775-781): the handler walks to the prisoner and picks it up.</summary>
    void LiftPerson(TId handler, TId prisoner);
    /// <summary><c>ArenaMedical::IssueStabilizeOrder(medic, target)</c> (ArenaMedical.cpp:251-267: first aid, or robot repair when the robot need is
    /// higher).</summary>
    void IssueStabilizeOrder(TId medic, TId target);
}

/// <summary>Part of <see cref="IPrisonerHost{TId}"/>: the fight reads and writes of the protection (task 6.2).</summary>
public interface IPrisonerCombatHost<TId>
{
    /// <summary><c>SparSession::IsSparringOpponent(a, b)</c>.</summary>
    bool IsSparringOpponent(TId a, TId b);
    /// <summary><c>prisoner-&gt;getAllAttackers(attackers)</c>: every character attacking <paramref name="c"/>, appended (a stale entry is the
    /// C++'s <c>hand::getCharacter()</c> NULL: none).</summary>
    void AllAttackers(TId c, List<TId> into);
    /// <summary><c>c-&gt;getAttackTarget().getCharacter()</c> (none when the hand is not valid).</summary>
    TId AttackTarget(TId c);
    /// <summary><c>a-&gt;clearTempEnemyStatus(b)</c>.</summary>
    void ClearTempEnemyStatus(TId a, TId b);
    /// <summary><c>c-&gt;removeJob(FOCUSED_MELEE_ATTACK)</c> + <c>removeJob(UNPROVOKED_FOCUSED_MELEE_ATTACK)</c>.</summary>
    void RemoveMeleeAttackJobs(TId c);
    /// <summary><c>c-&gt;endCombatMode()</c>.</summary>
    void EndCombatMode(TId c);
    /// <summary><c>a-&gt;rememberCharacter(b, ST_TEMPORARY_ALLY)</c>.</summary>
    void RememberTemporaryAlly(TId a, TId b);
}

/// <summary>What PrisonerUtil.cpp / PrisonerUtilPlatoon.cpp read of the world (the glue implements it over PgEngine, src/Engine/Cages.cs,
/// ArenaIngress, FightStarter and SparSession). A building / cage is a string key (null: none); "Valid" is the C++
/// <c>x &amp;&amp; x-&gt;isValid()</c>. Split by what the members do (pure grouping); the logic, the glue and the test fake use the whole.</summary>
public interface IPrisonerHost<TId> : IPrisonerWorldHost<TId>, IPrisonerCageHost<TId>, IPrisonerMotionHost<TId>, IPrisonerCombatHost<TId>
{
}

/// <summary>C++ <c>NearbyCage</c> (PrisonerUtil.cpp:48-52): a cage building and its UseableStuff.</summary>
public readonly record struct NearbyCage(string Building, string Useable);

/// <summary>C++ <c>MatchEntry</c> (PrisonerUtil.cpp:54-84): one prisoner of the match, its cage, its handler and the release / return / lock
/// state the 6.2 / 6.3 steps advance. A class: the C++ mutates the entries in place (<c>MatchEntry&amp; entry = g_matchEntries[e]</c>). The
/// <c>hand</c> fields keep the key the C++ handle names (a character id is a handle already; a cage key is resolved again by the glue).</summary>
public sealed class MatchEntry<TId>
{
    public TId Prisoner = default!;
    public string? CageHand;
    public string? CageUseable;
    public TId Handler = default!;
    public int OriginalCageIndex;
    public bool WasChained;
    public bool Released;
    public bool UnlockPending;
    public bool UnlockArrived;
    public float UnlockElapsed;
    public float UnlockHoldElapsed;
    public bool NewlyReleased;
    public string? ReturnCage;
    public string? ReturnCageBuilding;
    public string? ReturnCageHand;
    public TId PrisonerHand = default!;
    public TId HandlerHand = default!;
    public bool LockPending;
    public bool LockOrderIssued;
    public uint LockStartedAt;
    public bool ReturnPending;
    public bool ReturnEscorting;
    public bool ReturnCarrying;
    public bool ReturnPickedUp;
    public bool ReturnTreating;
    public float ReturnElapsed;
    public bool RingsideTreating;
    public float RingsideTreatElapsed;
}

/// <summary>The file-level state of PrisonerUtil.cpp (:86-104), ONE object per world (owned through src/Game/PrisonerUtil.cs by
/// ProvingGroundsSystem, which is built again for each world). AbandonWorldState and ClearMatchState reset exactly what the C++ resets.</summary>
public sealed class PrisonerUtilState<TId>
{
    public readonly List<TId> RosterPrisoners = [];               // g_rosterPrisoners
    public readonly List<NearbyCage> NearbyCages = [];            // g_nearbyCages
    public readonly List<MatchEntry<TId>> MatchEntries = [];      // g_matchEntries
    public readonly List<TId> MatchHandlers = [];                 // g_matchHandlers
    public readonly List<TId> MatchPrisonersScratch = [];         // g_matchPrisonersScratch
    public bool MatchHasPrisoner;                                 // g_matchHasPrisoner
    public bool Returning;                                        // g_returning
    public bool Unlocking;                                        // g_unlocking
    public bool CageReturnFailed;                                 // g_cageReturnFailed
    public string CageReturnStatus = "";                          // g_cageReturnStatus
    public bool HandlerStatesRemembered;                          // g_handlerStatesRemembered
    public uint ReturnLastTick;                                   // g_returnLastTick (GetTickCount -> PgClock.NowMs)
    public uint UnlockLastTick;                                   // g_unlockLastTick
    public uint RingsideAidLastTick;                              // g_ringsideAidLastTick
    public uint MatchUpkeepLastTick;                              // g_matchUpkeepLastTick
    public float ProtectionElapsedSec;                            // g_protectionElapsedSec
    public float HandlerElapsedSec;                               // g_handlerElapsedSec
    public bool ProtectionImmediate;                              // g_protectionImmediate
}

/// <summary>PrisonerUtil.cpp's decisions over a host. One object per world (its <see cref="State"/>).</summary>
public sealed partial class PrisonerUtilLogic<TId>
{
    /// <summary>C++ <c>kCageNearRegistry</c> (PrisonerUtil.h:13).</summary>
    public const float CageNearRegistry = 2500.0f;
    /// <summary>C++ <c>kSphereSearchMax</c> (PrisonerUtil.cpp:106).</summary>
    public const int SphereSearchMax = 512;

    /// <summary>The C++ file-level state.</summary>
    public PrisonerUtilState<TId> State { get; } = new();

    static readonly EqualityComparer<TId> Eq = EqualityComparer<TId>.Default;

    // C++: PrisonerUtil.cpp:144-150
    void ResetMatchUpkeep()
    {
        State.MatchUpkeepLastTick = 0;
        FrameCadencePolicy.Reset(ref State.ProtectionElapsedSec);
        FrameCadencePolicy.Reset(ref State.HandlerElapsedSec);
        State.ProtectionImmediate = false;
    }

    // C++: PrisonerUtil.cpp:152-157
    static float HorizontalDistSq(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }

    // C++: PrisonerUtil.cpp:167-175
    static bool ContainsCharacter(List<TId> list, TId c)
    {
        for (int i = 0; i < list.Count; ++i)
        {
            if (Eq.Equals(list[i], c))
                return true;
        }
        return false;
    }

    // C++: PrisonerUtil.cpp:417-428
    bool HasMatchPrisonerEntry(IPrisonerHost<TId> h, TId c)
    {
        if (h.IsNull(c))
            return false;

        for (int i = 0; i < State.MatchEntries.Count; ++i)
        {
            if (Eq.Equals(State.MatchEntries[i].Prisoner, c))
                return true;
        }
        return false;
    }

    // C++: PrisonerUtil.cpp:802-829
    static void MakeEmptyEntry(MatchEntry<TId> entry)
    {
        entry.Prisoner = default!;
        entry.CageUseable = null;
        entry.Handler = default!;
        entry.OriginalCageIndex = -1;
        entry.WasChained = false;
        entry.Released = false;
        entry.UnlockPending = false;
        entry.UnlockArrived = false;
        entry.UnlockElapsed = 0.0f;
        entry.UnlockHoldElapsed = 0.0f;
        entry.NewlyReleased = false;
        entry.ReturnCage = null;
        entry.ReturnCageBuilding = null;
        entry.LockPending = false;
        entry.LockOrderIssued = false;
        entry.LockStartedAt = 0;
        entry.ReturnPending = false;
        entry.ReturnEscorting = false;
        entry.ReturnCarrying = false;
        entry.ReturnPickedUp = false;
        entry.ReturnTreating = false;
        entry.ReturnElapsed = 0.0f;
        entry.RingsideTreating = false;
        entry.RingsideTreatElapsed = 0.0f;
    }

    // C++: PrisonerUtil.cpp:831-834
    public bool HasRuntimeActivity() => State.Unlocking || State.Returning || State.MatchEntries.Count != 0;

    // C++: PrisonerUtil.cpp:836-853
    public void AbandonWorldState()
    {
        State.CageReturnFailed = false;
        State.CageReturnStatus = "";
        State.RosterPrisoners.Clear();
        State.NearbyCages.Clear();
        State.MatchEntries.Clear();
        State.MatchHandlers.Clear();
        State.MatchPrisonersScratch.Clear();
        State.MatchHasPrisoner = false;
        State.Returning = false;
        State.Unlocking = false;
        State.HandlerStatesRemembered = false;
        State.ReturnLastTick = 0;
        State.UnlockLastTick = 0;
        State.RingsideAidLastTick = 0;
        ResetMatchUpkeep();
    }

    // ---------- accessors of the return / lock state (the steps that set it: PrisonerUtilLogic.Return.cs, task 6.3) ----------

    // C++: PrisonerUtil.cpp:1727-1730
    public bool IsReturning() => State.Returning;

    // C++: PrisonerUtil.cpp:1732-1737
    public bool HasPendingCageLocks()
    {
        for (int e = 0; e < State.MatchEntries.Count; ++e)
            if (State.MatchEntries[e].LockPending) return true;
        return false;
    }

    // C++: PrisonerUtil.cpp:1739
    public bool HasCageReturnFailure() => State.CageReturnFailed;

    // C++: PrisonerUtil.cpp:1740
    public string GetCageReturnStatus() => State.CageReturnStatus;

    // C++: PrisonerUtil.cpp:1776-1804
    public void ClearMatchState(IPrisonerHost<TId> h)
    {
        // UI cancellation must not cancel the handler's queued native lock or
        // erase the handles required by the GUI/lifecycle tick.
        if (HasPendingCageLocks()) return;
        if (State.HandlerStatesRemembered && State.MatchHandlers.Count != 0)
        {
            var survivingHandlers = new List<TId>();
            for (int i = 0; i < State.MatchEntries.Count; ++i)
            {
                TId handler = State.MatchEntries[i].HandlerHand;
                if (!h.IsNull(handler) && h.Valid(handler)) survivingHandlers.Add(handler);
            }
            if (survivingHandlers.Count != 0)
                h.DisengageMatch(survivingHandlers);
        }

        State.MatchEntries.Clear();
        State.MatchHandlers.Clear();
        State.HandlerStatesRemembered = false;
        State.MatchHasPrisoner = false;
        State.Returning = false;
        State.Unlocking = false;
        State.ReturnLastTick = 0;
        State.UnlockLastTick = 0;
        State.RingsideAidLastTick = 0;
        ResetMatchUpkeep();
    }
}
