using System.Globalization;
using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// The decisions of upstream/ProvingGrounds/plugin/src/ArenaIngress.cpp (Proving Grounds by Shutori, GPLv3), task 4.3b (PA §2.5; TW §2.2 step 9;
// CAP 5, 16), over one host interface so they run without the game (E3, Tests/Glue/ArenaIngressLogicTests.cs). src/Game/ArenaIngress.cs is the
// public surface (ArenaIngress.h) and binds the host through PgEngine and the other subsystems (src/Game/PgIngressHost.cs; the E1 table is in
// src/Game/ArenaIngress.cs's header). Every function below is the C++ function of the same name, in the C++'s order, with its line range. The
// formation maths (the :46-110 constants, the target lists, the walk-in target, the escort points, the facing pick) is Core/ArenaFormation.cs
// (task 4.3a): every point comes from there, nothing is recomputed here. The decisions of ArenaIngressPolicy.h are Core/ArenaIngressPolicy.cs.
//
// The Registry / Banner / Leaderboard approach for the UI (task 4.2: BeginApproachForUI, NotifyExternalOrder, GetUiOpener, the PendingOpenUI step
// of Tick and their helpers IssueApproachMoves, RegistryApproachPoint, GetBoundLeaderboard, HasValidLeaderboard, HasValidApproachBuilding,
// BindLeaderboardBuilding, FinishApproachAndOpenUI, OpenInteractUiImmediately) are here too, each with its C++ line range. Their callers are
// src/Game/ContextMenuHooks.cs (the right press on a finished building, the "use" orders, the order and menu cancels).

/// <summary>What ArenaIngress reads of and does to the game and the other modules (one member per C++ call; the E1 table in
/// src/Game/ArenaIngress.cs says what replaces each in Meitou). <typeparamref name="TId"/>: a character handle (the C++ <c>Character*</c>).</summary>
public interface IIngressHost<TId>
{
    // ---------- engine reads ----------
    /// <summary>The "no character" id (a C++ null pointer).</summary>
    TId None { get; }
    bool IsNone(TId c);
    /// <summary><c>c->isValid()</c>.</summary>
    bool Exists(TId c);
    /// <summary><c>c->isDead()</c>.</summary>
    bool IsDead(TId c);
    /// <summary><c>c->getName()</c> of a valid character ("" otherwise).</summary>
    string Name(TId c);
    /// <summary><c>c->getPosition()</c>; false for a stale id.</summary>
    bool TryGetPosition(TId c, out Vec3 position);
    /// <summary><c>c->getMovementSpeed()</c> (the log lines and the approach's settle test, 4.2).</summary>
    float MovementSpeed(TId c);
    /// <summary><c>movement &amp;&amp; movement->isCurrentlyMoving()</c> (log lines only).</summary>
    bool IsMoving(TId c);
    /// <summary><c>movement->destination</c> (log line only; the origin when none).</summary>
    Vec3 Destination(TId c);
    /// <summary><c>GetTickCount()</c> (the port's PgClock ms; uint, wraps).</summary>
    uint NowMs { get; }
    /// <summary><c>ou->isPaused()</c>. The port's step never runs while paused (a paused world runs no SlowWorld): always false there.</summary>
    bool Paused { get; }
    /// <summary>The first character of <c>SquadUtil::CollectPlayerSquad</c> (HasLocationSite :1105-1112), or the none id.</summary>
    TId FirstSquadMember();
    /// <summary><c>SquadUtil::CollectSelectedCharacters</c> (BeginApproachForUI :1197-1198, task 4.2): the selected characters that exist and are
    /// alive, in selection order.</summary>
    TId[] CollectSelectedCharacters();

    // ---------- engine writes (CAP 5, 16) ----------
    /// <summary>The engine part of <c>IssueMove(c, loc)</c> (:428-453): for a town bout fighter <c>clearAllAIGoals()</c>; Hold and Passive off;
    /// the move order; RUN for a town bout fighter (<paramref name="townFighter"/>). The validity check is the logic's.</summary>
    void IssueMove(TId c, Vec3 location, bool townFighter);
    /// <summary><c>c->removeJob(MOVE_CUS_ORDERED)</c> of a valid character.</summary>
    void RemoveMoveJob(TId c);
    /// <summary>The engine part of <c>ParkCharacterInPlace(c)</c> (:599-616) for a valid, living character: the move job removed, AI goals
    /// cleared, Passive and Hold on, AGG off, a <c>STAND_STILL</c> goal when <paramref name="standStillGoal"/> (a match prisoner or a town bout
    /// fighter), and a rethink.</summary>
    void Park(TId c, bool standStillGoal);
    /// <summary>Port only: let go of every character <see cref="Park"/> holds by a means that outlives the call (an NPC held with
    /// <c>Engaged</c>); the C++'s <c>STAND_STILL</c> goal is cleared by the next order or goal. Called where the staging ends
    /// (ClearPendingState, AbandonWorldState).</summary>
    void ReleaseParked();
    /// <summary><c>fighter->lookatPosition(face, true)</c>.</summary>
    void Face(TId c, Vec3 point);
    /// <summary><c>fighter->say(line)</c> (a speech bubble, CAP 16).</summary>
    void Say(TId c, string text);

    // ---------- buildings (task 4.1's ArenaIdentity) ----------
    /// <summary><c>FindSiteForMode(mode, origin)</c> (:226-233 over the search :175-211; it writes the C++ "site search" line).</summary>
    ArenaBuilding? FindSiteForMode(ArenaLocationMode mode, Vector3 origin);
    /// <summary>A held building as it is now (the C++ <c>hand::isValid()</c> + <c>getBuilding()</c>), or null when gone.</summary>
    ArenaBuilding? Resolve(ArenaBuildingRef held);
    /// <summary><c>ArenaRingGuard::SetArena(site)</c> (:1343): the site's centre, a still-exists check and its kind.</summary>
    void SetRingSite(ArenaBuildingRef site);
    /// <summary><c>building->getPositionMarker(from)</c> (RegistryApproachPoint :490, task 4.2), or null when the building exposes none (the C++
    /// then answers the building's origin, which RegistryApproachPoint treats the same way: "stuck at the building origin").</summary>
    Vec3? PositionMarker(ArenaBuilding building, Vec3 from);

    // ---------- ArenaUI / LeaderboardUI (task 4.2) ----------
    /// <summary><c>ArenaUI::ShowFromRegistry(registry, bindRegistry)</c> (:963, :990).</summary>
    void ArenaShowFromRegistry(ArenaBuilding registry, bool bindRegistry);
    /// <summary><c>LeaderboardUI::Show(kind, board)</c> (:973, :1000).</summary>
    void LeaderboardShow(LeaderboardData.Kind kind, ArenaBuilding board);

    // ---------- FightStarter ----------
    void RememberMatchFighters(TId[] fighters, int count);
    void DisengageMatch(TId[] fighters, int count);
    /// <summary>Port only (task 3.5's post-disengage sweep): a walk-in fighter is taken off the stale-target sweep so its move is not stopped.</summary>
    void LeaveStaleSweep(TId c);

    // ---------- SparSession ----------
    bool SparIsActive { get; }
    bool SparStartMatch(MatchRules.MatchMode mode, TId[] fighters, MatchRules.MatchTeam[] teams, int count);
    string SparGetStatus();
    /// <summary>Port only (task 2.5): a save prepared by the save safety is not captured yet (nothing may restart arena activity).</summary>
    bool SaveInFlight { get; }

    // ---------- ResultsUI ----------
    void ResultsCancel();
    void ResultsClose();

    // ---------- TownArena (M7) ----------
    bool TownIsAftercare();
    bool TownIsBusy();
    bool TownIsPlayerMatch();
    bool TownIsFighter(TId c);
    void TownCancelPlannedNpcBout(string reason);
    bool TownValidatePreparation();
    void TownQuarantineIngressFighter(TId c);

    // ---------- TownAnnouncer (M8) ----------
    void AnnouncerRelease();
    void AnnouncerAbandonWorldState();
    void AnnouncerTickApproach();
    bool AnnouncerWaitingForPosition();
    bool AnnouncerHasAnnouncement();
    void AnnouncerBeginAnnouncement();
    void AnnouncerTickAnnouncement(float elapsed);
    float AnnouncerAnnouncementDuration();
    string AnnouncerGetStatus();

    // ---------- TownBookie (M8) ----------
    bool BookieHoldForBets();
    bool BookieHasPendingWager();

    // ---------- PrisonerUtil (M6) ----------
    bool IsMatchPrisoner(TId c);
    bool IsMatchPrisonerReleased(TId c);
    bool AllMatchPrisonersReleased();
    bool MatchIncludesPrisoner();
    void SendPairToArena(TId prisoner, Vec3 target);
    void BeginHandlerUnlocks();
    void TickHandlerUnlocks();
    bool ConsumeNewlyReleased(TId prisoner);
    void ParkMatchHandlers();
    void ReinforceParkedHandlers();
    void BeginReturnToCages(out string status);
}

/// <summary>
/// ArenaIngress.cpp's state and decisions: binds the Registry, stages a bout at its site (formation, the 4 s hold, the countdown with the
/// 3 / 2 / 1 / FIGHT! barks), hands the fighters to SparSession, and walks a Teams 1v1 replacement to its pit marker. One object per world.
/// The engine is reached only through the <see cref="IIngressHost{TId}"/> given to each call.
/// </summary>
public sealed class ArenaIngressLogic<TId> where TId : IEquatable<TId>
{
    /// <summary>C++: ArenaIngress.cpp:115-122.</summary>
    public enum PendingKind
    {
        None = 0,
        PrisonerRelease = 1,
        Formation = 2,
        OpenUI = 3,
        Countdown = 4,
    }

    // C++: ArenaIngress.cpp:124-159 (the namespace globals)
    ArenaBuildingRef? boundRegistry;
    ArenaBuildingRef? boundLeaderboard;
    ArenaBuildingRef? boundSite;
    ArenaLocationMode locationMode = ArenaLocationMode.Arena;
    bool pending;
    PendingKind pendingKind = PendingKind.None;
    float nextMoveIssueSec;
    float elapsedSec;
    float settleHoldSec;
    int countdownStage = -1;
    uint lastTick;
    bool escortsParkIssued;
    MatchRules.MatchMode mode = MatchRules.MatchMode.TeamAvB;
    bool walkInPending;
    TId walkInFighter;
    Vec3 walkInTarget = new(0.0f, 0.0f, 0.0f);
    float walkInNextMoveSec;
    float walkInElapsedSec;
    uint walkInLastTick;
    readonly List<TId> fighters = new();
    readonly List<MatchRules.MatchTeam> teams = new();
    readonly List<TId> escorts = new();
    Vec3[] targets = [];
    readonly struct NpcFormationProgress(float distance, float elapsed)
    {
        public readonly float CheckpointDistance = distance;
        public readonly float LastProgressSec = elapsed;
    }
    readonly List<NpcFormationProgress> npcFormationProgress = new();
    float nextNpcProgressLogSec;
    bool npcGraceLogged;
    TId uiOpener;
    string status = "Idle";
    bool issuingManagedMove;
    readonly TId none;
    readonly List<(TId Id, Vec3 Target, uint Ms, int Gen)> ownApproachMoves = new();   // port only: see ConsumeOwnApproachMove
    int approachGen;   // port only: bumped by each approach start and each ClearPendingState; a move of an older one is a leftover
    const uint OwnApproachMoveLifeMs = 2000;
    readonly Action<string>? ownLog;
    readonly HashSet<string> failuresLogged = new(StringComparer.Ordinal);
    int failureCount;

    /// <param name="none">The "no character" id.</param>
    /// <param name="ownLog">Where the port's own lines go (a staging step that threw: once per step name per world).</param>
    public ArenaIngressLogic(TId none, Action<string>? ownLog = null)
    {
        this.none = none;
        this.ownLog = ownLog;
        walkInFighter = none;
        uiOpener = none;
    }

    /// <summary>Staging steps that threw and were skipped in this world (the F10 row; port only).</summary>
    public int FailureCount => failureCount;

    /// <summary>Port only (SparSessionLogic's pattern): one step of a clean-up under its own try/catch, so a throw never keeps the others from
    /// running or the state from being cleared. The first failure of each step name in this world is logged.</summary>
    void Guarded(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { LogFailure(what, e); }
    }

    void LogFailure(string what, Exception e)
    {
        failureCount++;
        if (failuresLogged.Add(what))
            ownLog?.Invoke($"ArenaIngress {what} failed (logged once per step per world): {e.GetType().Name}: {e.Message}");
    }

    /// <summary>Port-only status of a Begin that threw part-way (the fighters are restored).</summary>
    public const string BeginFailedText = "Ingress failed";
    /// <summary>Port-only status of a Begin whose fighter or team list is shorter than its count (the C++ reads past the end).</summary>
    public const string ShortListText = "Invalid fighter selection";

    // ---------- read-outs for the port's status and tests (no C++ counterpart) ----------

    /// <summary>The phase now.</summary>
    public PendingKind Kind => pendingKind;
    /// <summary>The fighters staged now (copy).</summary>
    public IReadOnlyList<TId> Fighters => fighters;
    /// <summary>C++ <c>g_escorts</c>: the staged escorts (the prisoners' handlers, ArenaUI.cpp:2384-2386); the test-only PG-INGRESS state line (task 6.1).</summary>
    public IReadOnlyList<TId> Escorts => escorts;
    /// <summary>Their formation points (world).</summary>
    public IReadOnlyList<Vec3> Targets => targets;
    /// <summary>The countdown's time so far (and the phase's elapsed time otherwise).</summary>
    public float ElapsedSec => elapsedSec;
    /// <summary>The gather hold so far.</summary>
    public float SettleHoldSec => settleHoldSec;
    /// <summary>The countdown stage last barked (3, 2, 1, 0 = FIGHT!; -1 = none).</summary>
    public int CountdownStage => countdownStage;
    /// <summary>The bound fight site.</summary>
    public ArenaBuildingRef? BoundSite => boundSite;
    /// <summary>The walk-in target.</summary>
    public Vec3 WalkInTarget => walkInTarget;
    /// <summary>The walk-in time so far.</summary>
    public float WalkInElapsedSec => walkInElapsedSec;
    /// <summary>Whether the escorts were parked (the C++ <c>g_escortsParkIssued</c>: reset with the staging, never set in 1.3.4).</summary>
    public bool EscortsParkIssued => escortsParkIssued;
    /// <summary>The bound Leaderboard's ref (BeginApproachForUI binds it), null when none.</summary>
    public ArenaBuildingRef? BoundLeaderboardRef => boundLeaderboard;
    /// <summary>While the logic is inside a managed move (the C++ <c>g_issuingManagedMove</c>; NotifyExternalOrder reads it).</summary>
    public bool IssuingManagedMove => issuingManagedMove;

    // ---------- the C++ anonymous namespace ----------

    static Vector3 N(Vec3 v) => new(v.X, v.Y, v.Z);
    static Vec3 V(Vector3 v) => new(v.X, v.Y, v.Z);
    static Quat Ori(ArenaBuilding b) => Quat.FromYaw(b.Yaw);
    static ArenaFormation.LocationMode Formation(ArenaLocationMode m) => (ArenaFormation.LocationMode)(int)m;

    bool Valid(IIngressHost<TId> h, TId c) => !h.IsNone(c) && h.Exists(c);

    Vec3? PositionOf(IIngressHost<TId> h, TId c) => Valid(h, c) && h.TryGetPosition(c, out Vec3 p) ? p : null;

    // C++: ArenaIngress.cpp:213-224
    Vector3 ResolveSearchOrigin(IIngressHost<TId> h, TId[] fs, int count)
    {
        if (boundRegistry is { } held && h.Resolve(held) is { } reg)
            return reg.Position;
        if (fs != null && count > 0 && PositionOf(h, fs[0]) is { } p)
            return N(p);
        return Vector3.Zero;
    }

    // C++: ArenaIngress.cpp:428-453
    void IssueMove(IIngressHost<TId> h, TId c, Vec3 loc)
    {
        if (!Valid(h, c))
            return;
        // Town package goals can otherwise immediately reclaim a trial fighter; Hold/Passive ignore move tasks (the host clears them).
        issuingManagedMove = true;
        try { h.IssueMove(c, loc, h.TownIsFighter(c)); }
        finally { issuingManagedMove = false; }
    }

    // C++: ArenaIngress.cpp:472-481
    void IssueApproachMoves(IIngressHost<TId> h)
    {
        PruneOwnApproachMoves(h.NowMs);
        int count = fighters.Count;
        for (int i = 0; i < count; ++i)
        {
            if (Valid(h, fighters[i]))
                ownApproachMoves.Add((fighters[i], targets[i], h.NowMs, approachGen));   // port only (ConsumeOwnApproachMove)
            IssueMove(h, fighters[i], targets[i]);
        }

        PGLog.Debug("Proving Grounds: approach move (re)issued");
    }

    // C++: ArenaIngress.cpp:483-511. The marker is read only for a valid walker (:488-497), else the local use node (-3, 10, 3) pushed 30 units
    // outward (ArenaFormation.RegistryApproachPoint, task 4.3a: the C++ maths, golden-tested).
    Vec3 RegistryApproachPoint(IIngressHost<TId> h, ArenaBuilding building, TId from)
    {
        Vec3? marker = null;
        if (PositionOf(h, from) is { } p)
            marker = h.PositionMarker(building, p) ?? V(building.Position);   // no marker: the C++ answers the origin, rejected below
        return ArenaFormation.RegistryApproachPoint(V(building.Position), Ori(building), marker);
    }

    /// <summary>Port only (the PG-INTERACT diagnostic line): the walk-up point RegistryApproachPoint gives <paramref name="from"/>.</summary>
    public Vec3 ApproachPointFor(IIngressHost<TId> h, ArenaBuilding building, TId from) => RegistryApproachPoint(h, building, from);

    /// <summary>Port only (the window's busy flag): C++ ShouldOpenInteractUiImmediately's ingress inputs (:1170-1173): a staged fight
    /// (prisoner release, formation, countdown) or a walk-in is pending.</summary>
    public bool FightIngressOrWalkInPending =>
        walkInPending || (pending &&
            (pendingKind == PendingKind.PrisonerRelease || pendingKind == PendingKind.Formation || pendingKind == PendingKind.Countdown));

    // C++: ArenaIngress.cpp:513-524
    ArenaBuilding? GetBoundLeaderboard(IIngressHost<TId> h)
    {
        if (boundLeaderboard is not { } held)
            return null;
        if (h.Resolve(held) is not { } b)
            return null;
        return ArenaIdentityLogic.IsLeaderboard(b) ? b : null;
    }

    // C++: ArenaIngress.cpp:526-530
    bool HasValidLeaderboard(IIngressHost<TId> h)
    {
        ArenaBuilding? board = GetBoundLeaderboard(h);
        return board != null && ArenaIdentityLogic.IsLeaderboard(board);
    }

    // C++: ArenaIngress.cpp:532-538
    bool HasValidApproachBuilding(IIngressHost<TId> h) =>
        (HasValidRegistry(h) &&
         ArenaIdentityLogic.IsMatchUiOpenerFinished(GetBoundRegistry(h)))
        || (HasValidLeaderboard(h) &&
            ArenaIdentityLogic.IsLeaderboardFinished(GetBoundLeaderboard(h)));

    // C++: ArenaIngress.cpp:540-547. The building as it is now (the caller resolved it); held as its ref.
    void BindLeaderboardBuilding(ArenaBuilding? board)
    {
        if (!ArenaIdentityLogic.IsLeaderboard(board))
            return;
        // Same assignment style as BindRegistry (RootObject* -> hand).
        boundLeaderboard = board!.Value.Ref;
        boundRegistry = null;
    }

    // C++: ArenaIngress.cpp:549-574 EscortGatherBase -> ArenaFormation.EscortGatherBase over the site and the bound Registry
    Vec3 EscortGatherBase(IIngressHost<TId> h, ArenaBuilding? site)
    {
        Vec3? registry = GetBoundRegistry(h) is { } r ? V(r.Position) : null;
        return ArenaFormation.EscortGatherBase(Formation(locationMode), site is { } s ? V(s.Position) : null,
            site is { } s2 ? Ori(s2) : Quat.FromYaw(0f), registry);
    }

    // C++: ArenaIngress.cpp:576-597
    void IssueEscortMoves(IIngressHost<TId> h, ArenaBuilding? site, List<TId> es)
    {
        if (es.Count <= 0)
            return;

        Vec3 @base = EscortGatherBase(h, site);
        for (int i = 0; i < es.Count; ++i)
        {
            TId escort = es[i];
            if (!Valid(h, escort))
                continue;
            IssueMove(h, escort, ArenaFormation.EscortMoveTarget(@base, i));
        }
    }

    // C++: ArenaIngress.cpp:599-616
    void ParkCharacterInPlace(IIngressHost<TId> h, TId c)
    {
        if (!Valid(h, c) || h.IsDead(c))
            return;
        // HOLD is only a player standing order; released NPC packages can immediately rebuild Patrolling after the rethink below. Give
        // prisoner fighters an explicit active goal for staging instead.
        h.Park(c, h.IsMatchPrisoner(c) || h.TownIsFighter(c));
    }

    // C++: ArenaIngress.cpp:618-627
    bool FighterAtMark(IIngressHost<TId> h, int index, float radius)
    {
        if (index >= fighters.Count || index >= targets.Length)
            return false;
        if (PositionOf(h, fighters[index]) is not { } p)
            return false;
        return ArenaFormation.AtMark(p, targets[index], radius);
    }

    // C++: ArenaIngress.cpp:629-658
    void FaceOpposingFormation(IIngressHost<TId> h, int index)
    {
        if (index >= fighters.Count || teams.Count != targets.Length)
            return;
        TId fighter = fighters[index];
        if (!Valid(h, fighter) || h.IsDead(fighter))
            return;
        if (PositionOf(h, fighter) is not { } p)
            return;
        // Use the opponent's assigned mark even before they arrive; the marks already account for the arena's orientation in world space.
        if (ArenaFormation.OpposingFacePoint(mode, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(teams), targets, index, p) is { } face)
            h.Face(fighter, face);
    }

    // C++: ArenaIngress.cpp:660-678
    void ParkArrivedFighters(IIngressHost<TId> h)
    {
        for (int i = 0; i < fighters.Count; ++i)
        {
            TId fighter = fighters[i];
            if (!Valid(h, fighter))
                continue;
            if (h.IsMatchPrisoner(fighter) &&
                !h.IsMatchPrisonerReleased(fighter))
            {
                continue;
            }
            if (!FighterAtMark(h, i, ArenaFormation.FormationArrivalRadius))
                continue;

            ParkCharacterInPlace(h, fighter);
            FaceOpposingFormation(h, i);
        }
    }

    // C++: ArenaIngress.cpp:680-693
    int CountFightersOutsideFormation(IIngressHost<TId> h)
    {
        int outside = 0;
        for (int i = 0; i < fighters.Count; ++i)
        {
            TId fighter = fighters[i];
            if (!Valid(h, fighter) ||
                !FighterAtMark(h, i, ArenaFormation.FormationArrivalRadius))
            {
                ++outside;
            }
        }
        return outside;
    }

    static string F(float v, int decimals) => CMath.FormatF(v, decimals);
    static string U(int v) => v.ToString(CultureInfo.InvariantCulture);

    // C++: ArenaIngress.cpp:695-716
    void LogNpcFormationProgress(IIngressHost<TId> h, string phase)
    {
        for (int i = 0; i < fighters.Count && i < targets.Length; ++i)
        {
            TId fighter = fighters[i];
            if (PositionOf(h, fighter) is not { } position) continue;
            float horizontal = MathF.Sqrt(ArenaFormation.HorizontalDistSq(position, targets[i]));
            float progressAge = i < npcFormationProgress.Count
                ? elapsedSec - npcFormationProgress[i].LastProgressSec : elapsedSec;
            PGLog.Debug("Proving Grounds: NPC formation " + phase + " elapsed=" + F(elapsedSec, 1) + " slot=" + U(i + 1)
                + " name=" + h.Name(fighter) + " horizontal=" + F(horizontal, 1) + " vertical=" + F(position.Y - targets[i].Y, 1)
                + " atMark=" + (FighterAtMark(h, i, ArenaFormation.FormationArrivalRadius) ? "1" : "0") + " progressAge=" + F(progressAge, 1)
                + " moving=" + (h.IsMoving(fighter) ? "1" : "0") + " speed=" + F(h.MovementSpeed(fighter), 2));
        }
    }

    // C++: ArenaIngress.cpp:718-734
    void BeginNpcFormationProgress(IIngressHost<TId> h)
    {
        npcFormationProgress.Clear();
        if (!h.TownIsBusy() || h.TownIsPlayerMatch() ||
            pendingKind != PendingKind.Formation) return;
        for (int i = 0; i < fighters.Count; ++i)
        {
            float distance = PositionOf(h, fighters[i]) is { } p && i < targets.Length
                ? MathF.Sqrt(ArenaFormation.HorizontalDistSq(p, targets[i])) : 0.0f;
            npcFormationProgress.Add(new NpcFormationProgress(distance, elapsedSec));
        }
        nextNpcProgressLogSec = elapsedSec + ArenaFormation.NpcFormationProgressLogSec;
        npcGraceLogged = false;
        LogNpcFormationProgress(h, "start");
    }

    // C++: ArenaIngress.cpp:736-758
    void UpdateNpcFormationProgress(IIngressHost<TId> h)
    {
        if (!h.TownIsBusy() || h.TownIsPlayerMatch() ||
            pendingKind != PendingKind.Formation) return;
        if (npcFormationProgress.Count != fighters.Count)
            BeginNpcFormationProgress(h);
        for (int i = 0; i < npcFormationProgress.Count; ++i)
        {
            if (PositionOf(h, fighters[i]) is not { } p) continue;
            float distance = MathF.Sqrt(ArenaFormation.HorizontalDistSq(p, targets[i]));
            NpcFormationProgress progress = npcFormationProgress[i];
            if (distance <= progress.CheckpointDistance - ArenaFormation.NpcFormationProgressDistance)
                npcFormationProgress[i] = new NpcFormationProgress(distance, elapsedSec);
        }
        if (elapsedSec >= nextNpcProgressLogSec)
        {
            LogNpcFormationProgress(h, "sample");
            nextNpcProgressLogSec = elapsedSec + ArenaFormation.NpcFormationProgressLogSec;
        }
    }

    // C++: ArenaIngress.cpp:760-782
    void IssueFormationMoves(IIngressHost<TId> h)
    {
        for (int i = 0; i < fighters.Count; ++i)
        {
            TId fighter = fighters[i];
            if (!Valid(h, fighter) || h.IsDead(fighter))
                continue;

            bool prisoner = h.IsMatchPrisoner(fighter);
            if (prisoner && !h.IsMatchPrisonerReleased(fighter))
                continue;
            if (FighterAtMark(h, i, ArenaFormation.FormationArrivalRadius))
                continue;

            if (prisoner)
            {
                h.SendPairToArena(fighter, targets[i]);
            }
            else
            {
                IssueMove(h, fighter, targets[i]);
            }
        }
    }

    // C++: ArenaIngress.cpp:784-822. Port only: the side effects run on local copies, each under Guarded (a throw is logged once and the rest
    // still runs), and the fields are ALWAYS reset (SparSessionLogic.AbortForSave's pattern), so a failing engine call never leaves a staging
    // that IsPending no longer reports.
    void ClearPendingState(IIngressHost<TId> h, bool restoreFighterOrders = true)
    {
        bool restoreFightState =
            restoreFighterOrders &&
            (pendingKind == PendingKind.PrisonerRelease ||
             pendingKind == PendingKind.Formation ||
             pendingKind == PendingKind.Countdown);
        TId[] fs = [.. fighters];
        TId[] es = [.. escorts];

        pending = false;
        pendingKind = PendingKind.None;
        approachGen++;   // port only: a move of the approach cleared here, still in flight, is a leftover (ConsumeOwnApproachMove)
        nextMoveIssueSec = 0.0f;
        elapsedSec = 0.0f;
        settleHoldSec = 0.0f;
        countdownStage = -1;
        lastTick = 0;
        escortsParkIssued = false;
        boundSite = null;
        fighters.Clear();
        teams.Clear();
        escorts.Clear();
        targets = [];

        for (int i = 0; i < fs.Length; ++i)
        {
            TId c = fs[i];
            Guarded("remove move job", () => { if (Valid(h, c)) h.RemoveMoveJob(c); });
        }
        for (int i = 0; i < es.Length; ++i)
        {
            TId c = es[i];
            Guarded("remove move job", () => { if (Valid(h, c)) h.RemoveMoveJob(c); });
        }
        Guarded("release parked", h.ReleaseParked);   // port only: the NPCs Park held (Engaged) are let go where the staging ends
        // Kept as the C++ (:803): only the fighters are disengaged; escorts given Jobs off by Begin's RememberMatchFighters(escorts) keep their
        // remembered state until FightStarter forgets it (divergences.md "ArenaIngress", M6).
        if (restoreFightState && fs.Length > 0)
        {
            Guarded("disengage", () => h.DisengageMatch(fs, fs.Length));
        }
        Guarded("announcer release", h.AnnouncerRelease);
    }

    // C++: ArenaIngress.cpp:824-837
    void ShowFighterCountdown(IIngressHost<TId> h, int stage, string? text)
    {
        if (stage == countdownStage || text == null)
            return;

        countdownStage = stage;
        string line = text;
        for (int i = 0; i < fighters.Count; ++i)
        {
            TId fighter = fighters[i];
            if (Valid(h, fighter))
                h.Say(fighter, line);
        }
    }

    // C++: ArenaIngress.cpp:839-862
    void BeginFightCountdown(IIngressHost<TId> h)
    {
        pendingKind = PendingKind.Countdown;
        elapsedSec = 0.0f;
        countdownStage = -1;
        lastTick = h.NowMs;
        status = h.AnnouncerHasAnnouncement()
            ? "The announcer takes the rail"
            : "Fight starts in 3...";
        ParkArrivedFighters(h);
        for (int i = 0; i < fighters.Count; ++i)
        {
            ParkCharacterInPlace(h, fighters[i]);
            FaceOpposingFormation(h, i);
        }
        if (pendingKind == PendingKind.PrisonerRelease)   // never true here (the kind was set to Countdown above): the C++'s own line
            h.ParkMatchHandlers();
        if (h.AnnouncerHasAnnouncement())
            h.AnnouncerBeginAnnouncement();
        else
            ShowFighterCountdown(h, 3, "3");
        PGLog.Debug("Proving Grounds: pre-fight countdown started");
    }

    // C++: ArenaIngress.cpp:864-905
    ArenaIngressPolicy.IngressObservation BuildIngressObservation(IIngressHost<TId> h)
    {
        var observation = new ArenaIngressPolicy.IngressObservation();
        observation.AllPrisonersReleased = h.AllMatchPrisonersReleased();
        observation.AllFightersValid = true;
        observation.AllAtArrivalRadius = observation.AllPrisonersReleased;
        observation.AllAtSettleRadius = observation.AllPrisonersReleased;
        observation.AllAtFormationRadius = observation.AllPrisonersReleased;
        observation.AllSettled = observation.AllPrisonersReleased;

        const float arrivalRadiusSq = ArenaFormation.ArrivalRadius * ArenaFormation.ArrivalRadius;
        const float settleRadiusSq = ArenaFormation.SettleRadius * ArenaFormation.SettleRadius;
        const float formationRadiusSq =
            ArenaFormation.FormationArrivalRadius * ArenaFormation.FormationArrivalRadius;
        for (int i = 0; i < fighters.Count; ++i)
        {
            TId c = fighters[i];
            if (PositionOf(h, c) is not { } p)
            {
                observation.AllFightersValid = false;
                observation.AllAtArrivalRadius = false;
                observation.AllAtSettleRadius = false;
                observation.AllAtFormationRadius = false;
                observation.AllSettled = false;
                continue;
            }

            float distanceSq = i < targets.Length ? ArenaFormation.HorizontalDistSq(p, targets[i]) : float.MaxValue;
            if (distanceSq > arrivalRadiusSq)
                observation.AllAtArrivalRadius = false;
            if (distanceSq > settleRadiusSq)
                observation.AllAtSettleRadius = false;
            if (distanceSq > formationRadiusSq)
                observation.AllAtFormationRadius = false;
            if (h.MovementSpeed(c) > ArenaFormation.SettleSpeed)
                observation.AllSettled = false;
        }
        return observation;
    }

    // C++: ArenaIngress.cpp:907-917
    bool AnyFighterInvalid(IIngressHost<TId> h)
    {
        int count = fighters.Count;
        for (int i = 0; i < count; ++i)
        {
            if (!Valid(h, fighters[i]))
                return true;
        }
        return false;
    }

    // C++: ArenaIngress.cpp:919-925
    bool HasValidSite(IIngressHost<TId> h) => boundSite is { } site && h.Resolve(site) is not null;

    // C++: ArenaIngress.cpp:927-949
    void FinishIngressAndStart(IIngressHost<TId> h)
    {
        // Ingress ticks before TownArena. Revalidate the locked NPCs before clearing staging ownership or allowing StartMatch to begin combat.
        if (!h.TownValidatePreparation()) return;
        int count = fighters.Count;
        MatchRules.MatchMode m = mode;
        TId[] fs = [.. fighters];
        MatchRules.MatchTeam[] ts = [.. teams];

        ClearPendingState(h, false);

        if (count < 2 || fs.Length == 0 || ts.Length == 0)
        {
            status = "Ingress failed: no fighters";
            return;
        }

        bool ok;
        try
        {
            ok = h.SparStartMatch(m, fs, ts, count);
            status = h.SparGetStatus();
        }
        catch (Exception e)   // port only: a throw is a refused start, so the fighters are released below
        {
            LogFailure("start match", e);
            ok = false;
            status = BeginFailedText;
        }
        if (!ok)
        {
            // ClearPendingState(false) transferred ownership to StartMatch. A rejected start must still release the saved staging orders.
            Guarded("disengage", () => h.DisengageMatch(fs, count));
            PGLog.Debug("Proving Grounds: ingress StartMatch failed: " + status);
        }
        else if (h.MatchIncludesPrisoner())
            h.ParkMatchHandlers();
    }

    // C++: ArenaIngress.cpp:955-984
    void FinishApproachAndOpenUI(IIngressHost<TId> h)
    {
        ArenaBuilding? registry = GetBoundRegistry(h);
        ArenaBuilding? board = GetBoundLeaderboard(h);
        ClearPendingState(h);

        if (registry is { } reg && ArenaIdentityLogic.IsMatchUiOpenerFinished(reg))
        {
            h.ArenaShowFromRegistry(reg, true);
            status = GetStatus();   // the C++ :964 (g_status = ArenaIngress::GetStatus(): a self-assignment)
            PGLog.Debug("Proving Grounds: approach complete -> Arena UI");
            return;
        }

        var leaderboard = LeaderboardData.Kind.Player;
        if (board is { } b && ArenaIdentityLogic.GetFinishedLeaderboardKind(
                b, ref leaderboard))
        {
            h.LeaderboardShow(leaderboard, b);
            status = leaderboard == LeaderboardData.Kind.Town
                ? "Town leaderboard open"
                : "Player leaderboard open";
            PGLog.Debug(leaderboard == LeaderboardData.Kind.Town
                ? "Proving Grounds: approach complete -> Town Leaderboard UI"
                : "Proving Grounds: approach complete -> Player Leaderboard UI");
            return;
        }

        status = "Interact building lost during approach";
    }

    // C++: ArenaIngress.cpp:986-1009
    bool OpenInteractUiImmediately(IIngressHost<TId> h, ArenaBuilding? building)
    {
        if (building is { } b0 && ArenaIdentityLogic.IsMatchUiOpenerFinished(b0))
        {
            h.ArenaShowFromRegistry(
                b0,
                ArenaIngressPolicy.ShouldBindInteractSource(true));
            PGLog.Debug("Proving Grounds: busy match Registry/Banner UI opened immediately");
            return true;
        }

        var leaderboard = LeaderboardData.Kind.Player;
        if (building is { } b && ArenaIdentityLogic.GetFinishedLeaderboardKind(b, ref leaderboard))
        {
            h.LeaderboardShow(leaderboard, b);
            PGLog.Debug(leaderboard == LeaderboardData.Kind.Town
                ? "Proving Grounds: busy match Town Leaderboard UI opened immediately"
                : "Proving Grounds: busy match Player Leaderboard UI opened immediately");
            return true;
        }
        return false;
    }

    // ---------- namespace ArenaIngress (ArenaIngress.h) ----------

    // C++: ArenaIngress.cpp:1012-1041
    public void AbandonWorldState(IIngressHost<TId> h)
    {
        h.AnnouncerAbandonWorldState();
        h.ReleaseParked();   // port only (see IIngressHost.ReleaseParked): no NPC stays Engaged after the staging is dropped
        boundRegistry = null;
        boundLeaderboard = null;
        boundSite = null;
        pending = false;
        pendingKind = PendingKind.None;
        nextMoveIssueSec = 0.0f;
        elapsedSec = 0.0f;
        settleHoldSec = 0.0f;
        countdownStage = -1;
        lastTick = 0;
        escortsParkIssued = false;
        walkInPending = false;
        walkInFighter = none;
        walkInTarget = new Vec3(0.0f, 0.0f, 0.0f);
        walkInNextMoveSec = 0.0f;
        walkInElapsedSec = 0.0f;
        walkInLastTick = 0;
        fighters.Clear();
        teams.Clear();
        escorts.Clear();
        targets = [];
        uiOpener = none;
        issuingManagedMove = false;
        ownApproachMoves.Clear();   // port only
        status = "Idle";
    }

    // C++: ArenaIngress.cpp:1043-1059. The building as it is now (the caller resolves its ref); a held building is kept as its ref.
    public void BindRegistry(ArenaBuilding? registry)
    {
        if (!ArenaIdentityLogic.IsMatchUiOpener(registry))
            return;

        boundRegistry = registry!.Value.Ref;
        boundLeaderboard = null;
        if (ArenaIdentityLogic.IsBanner(registry))
        {
            locationMode = ArenaLocationMode.Banner;
            status = "Bound to banner";
        }
        else
        {
            status = "Bound to registry";
        }
    }

    // C++: ArenaIngress.cpp:1061-1067
    public void ClearBind()
    {
        boundRegistry = null;
        boundLeaderboard = null;
        boundSite = null;
        status = "Unbound";
    }

    // C++: ArenaIngress.cpp:1069-1077
    public bool HasValidRegistry(IIngressHost<TId> h)
    {
        if (boundRegistry is not { } held)
            return false;
        if (h.Resolve(held) is not { } building)
            return false;
        return ArenaIdentityLogic.IsMatchUiOpener(building);
    }

    // C++: ArenaIngress.cpp:1079-1084
    public ArenaBuilding? GetBoundRegistry(IIngressHost<TId> h)
    {
        if (!HasValidRegistry(h))
            return null;
        return h.Resolve(boundRegistry!.Value);
    }

    /// <summary>The bound Registry's ref (port: the identity the C++ <c>hand</c> holds), null when none.</summary>
    public ArenaBuildingRef? BoundRegistryRef => boundRegistry;

    // C++: ArenaIngress.cpp:1084-1089 (g_uiOpener is set by BeginApproachForUI)
    public TId GetUiOpener(IIngressHost<TId> h)
    {
        if (Valid(h, uiOpener))
            return uiOpener;
        return none;
    }

    // C++: ArenaIngress.cpp:1093-1096
    public void SetLocationMode(ArenaLocationMode m) => locationMode = m;

    // C++: ArenaIngress.cpp:1098-1101
    public ArenaLocationMode GetLocationMode() => locationMode;

    // C++: ArenaIngress.cpp:1103-1119
    public bool HasLocationSite(IIngressHost<TId> h)
    {
        Vector3 origin = Vector3.Zero;
        if (HasValidRegistry(h))
        {
            origin = GetBoundRegistry(h)!.Value.Position;
        }
        else
        {
            TId first = h.FirstSquadMember();
            if (PositionOf(h, first) is not { } p)
                return false;
            origin = N(p);
        }
        return h.FindSiteForMode(locationMode, origin) != null;
    }

    // C++: ArenaIngress.cpp:1121-1128
    public string GetMissingLocationMessage()
    {
        if (locationMode == ArenaLocationMode.Banner)
            return "No banner placed near registry";
        if (locationMode == ArenaLocationMode.SmallArena)
            return "No small arena placed near registry";
        return "No arena placed near registry";
    }

    // C++: ArenaIngress.cpp:1130-1133
    public bool IsPending() => pending;

    // C++: ArenaIngress.cpp:1135-1138
    public string GetStatus() => status;

    // C++: ArenaIngress.cpp:1138-1161. <paramref name="fighter"/> = the none id: any (the C++ NULL, "another right-click menu opened").
    public void NotifyExternalOrder(IIngressHost<TId> h, TId fighter)
    {
        if (issuingManagedMove || !pending || pendingKind != PendingKind.OpenUI)
            return;

        if (!h.IsNone(fighter))
        {
            bool isApproaching = false;
            for (int i = 0; i < fighters.Count; ++i)
            {
                if (fighters[i].Equals(fighter))
                {
                    isApproaching = true;
                    break;
                }
            }
            if (!isApproaching)
                return;
        }

        ClearPendingState(h);
        status = "Approach cancelled by new order";
        PGLog.Debug("Proving Grounds: UI approach cancelled by player order");
    }

    /// <summary>Port only (the C++ <c>g_issuingManagedMove</c> for a deferred move): whether a move order arriving now for
    /// <paramref name="fighter"/> to <paramref name="point"/> is one this logic's approach gave (IssueApproachMoves, within the last 2 s, the same
    /// walker and target within 1 unit on the ground); the match is used up. Meitou applies a move order a tick after it is given (CAP 5:
    /// <c>MoveOrder</c> through <c>world.Defer</c>), so the flag the C++ holds around its synchronous <c>addOrder</c> cannot recognise the order
    /// when it arrives: the order hook asks this instead (src/Game/ContextMenuHooks.cs). <paramref name="stale"/>: the approach that gave it is
    /// over (finished or cancelled in the tick between), or another approach replaced it (a right press on a second building before the
    /// first's move arrived: its generation is older): the hook drops such a leftover, which the C++ never has (its move is given and done at
    /// once), so it can never undo the order that cancelled the approach nor walk the walker back to the building it left.</summary>
    public bool ConsumeOwnApproachMove(TId fighter, Vec3 point, uint nowMs, out bool stale)
    {
        PruneOwnApproachMoves(nowMs);
        bool running = pending && pendingKind == PendingKind.OpenUI;
        for (int i = 0; i < ownApproachMoves.Count; ++i)
        {
            var m = ownApproachMoves[i];
            if (m.Id.Equals(fighter) && ArenaFormation.HorizontalDistSq(point, m.Target) <= 1.0f)
            {
                ownApproachMoves.RemoveAt(i);
                stale = !running || m.Gen != approachGen;
                return true;
            }
        }
        stale = false;
        return false;
    }

    /// <summary>The approach moves given and not yet arrived (port only; tests).</summary>
    public int OwnApproachMovesInFlight => ownApproachMoves.Count;

    void PruneOwnApproachMoves(uint nowMs) =>
        ownApproachMoves.RemoveAll(m => unchecked(nowMs - m.Ms) > OwnApproachMoveLifeMs);

    // C++: ArenaIngress.cpp:1163-1240. <paramref name="building"/> = the building as it is now (the caller resolved the clicked one).
    public bool BeginApproachForUI(IIngressHost<TId> h, ArenaBuilding? building)
    {
        bool isMatchUiOpener = ArenaIdentityLogic.IsMatchUiOpenerFinished(building);
        bool isBanner = isMatchUiOpener && ArenaIdentityLogic.IsBanner(building);
        bool isLeaderboard = ArenaIdentityLogic.IsLeaderboardFinished(building);
        if (!isMatchUiOpener && !isLeaderboard)
        {
            status = "Interact building not finished";
            return false;
        }
        bool fightIngressPending = pending &&
            (pendingKind == PendingKind.PrisonerRelease ||
             pendingKind == PendingKind.Formation ||
             pendingKind == PendingKind.Countdown);
        if (ArenaIngressPolicy.ShouldOpenInteractUiImmediately(
                h.SparIsActive, fightIngressPending, walkInPending))
        {
            return OpenInteractUiImmediately(h, building);
        }
        ArenaBuilding b = building!.Value;
        // Multi-select use fires addOrder once per character — do not restart.
        if (pending && pendingKind == PendingKind.OpenUI)
        {
            ArenaBuilding? boundReg = GetBoundRegistry(h);
            ArenaBuilding? boundBoard = GetBoundLeaderboard(h);
            if ((boundReg is { } r && r.Ref == b.Ref) || (boundBoard is { } bb && bb.Ref == b.Ref))
            {
                status = isLeaderboard
                    ? "Walking to leaderboard..."
                    : (isBanner ? "Walking to banner..." : "Walking to registry...");
                return true;
            }
            ClearPendingState(h);
        }

        TId[] selected = h.CollectSelectedCharacters() ?? [];
        if (selected.Length == 0)
        {
            status = "No characters selected";
            return false;
        }

        if (isMatchUiOpener)
            BindRegistry(b);
        else
            BindLeaderboardBuilding(b);

        fighters.Clear();
        fighters.AddRange(selected);
        uiOpener = selected[0];
        teams.Clear();
        targets = new Vec3[selected.Length];
        for (int i = 0; i < selected.Length; ++i)
            targets[i] = RegistryApproachPoint(h, b, selected[i]);

        pending = true;
        pendingKind = PendingKind.OpenUI;
        approachGen++;   // port only (ConsumeOwnApproachMove)
        // Don't move on the RMB frame — Kenshi cancels that order on mouse-up (~0.1s).
        elapsedSec = 0.0f;
        nextMoveIssueSec = ArenaFormation.ApproachFirstMoveDelaySec;
        settleHoldSec = 0.0f;
        lastTick = h.NowMs;
        status = isLeaderboard
            ? "Walking to leaderboard..."
            : (isBanner ? "Walking to banner..." : "Walking to registry...");
        PGLog.Debug(isLeaderboard
            ? "Proving Grounds: approach Leaderboard for UI started"
            : (isBanner
                ? "Proving Grounds: approach Banner for UI started"
                : "Proving Grounds: approach Registry for UI started"));

        if (ArenaIngressPolicy.CanOpenApproachUi(BuildIngressObservation(h)))
        {
            FinishApproachAndOpenUI(h);
            return true;
        }

        return true;
    }

    // C++: ArenaIngress.cpp:1242-1245
    public bool Begin(IIngressHost<TId> h, MatchRules.MatchMode m, TId[] fs, MatchRules.MatchTeam[] ts, int count) =>
        Begin(h, m, fs, ts, count, null, 0);

    // C++: ArenaIngress.cpp:1247-1393. siteOverride: a held building ref (M7's town arena), resolved here.
    public bool Begin(
        IIngressHost<TId> h,
        MatchRules.MatchMode m,
        TId[]? fs,
        MatchRules.MatchTeam[]? ts,
        int count,
        TId[]? es,
        int escortCount,
        ArenaBuildingRef? siteOverride = null)
    {
        if (h.TownIsAftercare())
        {
            status = "Town medics are clearing the arena";
            return false;
        }
        if (fs == null || ts == null || count < 2)
        {
            status = "Need at least two fighters";
            return false;
        }

        if (fs.Length < count || ts.Length < count || (es != null && es.Length < escortCount))   // port only: the C++ would read past the arrays
        {
            status = ShortListText;
            return false;
        }

        for (int i = 0; i < count; ++i)
        {
            if (h.IsNone(fs[i]))
            {
                status = "Invalid fighter selection";
                return false;
            }
        }

        if (h.SparIsActive)
        {
            status = "Already sparring";
            return false;
        }

        // Port only (task 2.5): nothing may restart arena activity between the save prepare and the capture (as SparSession.StartMatch).
        if (h.SaveInFlight)
        {
            status = SparSessionLogic<TId>.SaveInFlightText;
            return false;
        }

        if (pending && pendingKind == PendingKind.OpenUI)
            Cancel(h);

        if (pending)
        {
            status = "Already walking to fight site";
            return false;
        }

        Vector3 origin = ResolveSearchOrigin(h, fs, count);
        ArenaBuilding? overrideNow = siteOverride is { } so ? h.Resolve(so) : null;
        ArenaBuilding? site = siteOverride != null ? overrideNow : h.FindSiteForMode(locationMode, origin);
        if (siteOverride != null && (overrideNow is null || !ArenaIdentityLogic.IsArena(overrideNow)))
        {
            status = "Invalid town arena";
            return false;
        }
        if (site is not { } s)
        {
            status = GetMissingLocationMessage();
            return false;
        }

        mode = m;
        fighters.Clear();
        for (int i = 0; i < count; ++i) fighters.Add(fs[i]);
        teams.Clear();
        for (int i = 0; i < count; ++i) teams.Add(ts[i]);
        escorts.Clear();
        if (es != null && escortCount > 0)
        {
            for (int i = 0; i < escortCount; ++i)
            {
                TId escort = es[i];
                if (Valid(h, escort))
                    escorts.Add(escort);
            }
        }
        boundSite = s.Ref;
        // Port only: the staging owns the fighters from here (pending), so a throw below is cleaned up by ClearPendingState (Jobs back on) and
        // never leaves fighters with Jobs off and no owner. The C++ sets these at the end (:1381-1384); nothing between reads them.
        pending = true;
        pendingKind = PendingKind.Formation;
        try
        {
            BeginStaging(h, m, fs, ts, count, es, escortCount, s);
        }
        catch (Exception e)
        {
            LogFailure("begin", e);
            // No prisoner branch here (task 6.3, as the C++ behaves): the C++ Begin has no failure after BeginHandlerUnlocks, and any false Begin is
            // followed by the caller's own return, ArenaUI's StartMatch (ArenaUI.cpp:2434-2441: MatchIncludesPrisoner -> ReturnAllToCages; port
            // ArenaUILogic's start), which forces every prisoner back to its cage, a released one included. Cancel's BeginReturnToCages (:1395-1411)
            // is not added (docs/divergences.md "ArenaIngress").
            ClearPendingState(h);
            status = BeginFailedText;
            PGLog.Debug("Proving Grounds: arena ingress start failed");
            return false;
        }
        return true;
    }

    // C++: ArenaIngress.cpp:1316-1392 (the rest of Begin, after the site is stored)
    void BeginStaging(IIngressHost<TId> h, MatchRules.MatchMode m, TId[] fs, MatchRules.MatchTeam[] ts, int count, TId[]? es, int escortCount,
        ArenaBuilding s)
    {
        // Jobs off as soon as match start begins walking people in.
        h.RememberMatchFighters(fs, count);
        if (es != null && escortCount > 0)
            h.RememberMatchFighters(es, escortCount);

        h.ResultsCancel();
        h.ResultsClose();

        targets = ArenaFormation.AssignSiteTargets(Formation(locationMode), h.TownIsBusy(), m, ts, count, V(s.Position), Ori(s));

        // Register the ring the fighters will be contained in. This is the one place that knows both the site and whether the bout is a
        // banner, so the guard never has to re-derive either. A banner is boundless: SetArena finds no ring there and the guard goes inert.
        // Note the staging scale above is the LAYOUT's, not the arena's size -- the player-run arena stages at 0.70 while being the same size
        // as the town Crucible.
        h.SetRingSite(s.Ref);

        for (int i = 0; i < count; ++i)
        {
            TId c = fighters[i];
            // Keep caged prisoners put until their handler unlocks them.
            if (h.IsMatchPrisoner(c) &&
                !h.IsMatchPrisonerReleased(c))
            {
                continue;
            }
            // One move order — jobs are already off so they won't wander to sit.
            IssueMove(h, fighters[i], targets[i]);
        }

        if (h.MatchIncludesPrisoner())
        {
            h.BeginHandlerUnlocks();
            status = "Handlers releasing prisoners...";
        }
        else if (escorts.Count > 0)
        {
            IssueEscortMoves(h, s, escorts);
            status = (locationMode == ArenaLocationMode.Banner)
                ? "Walking to banner..."
                : "Walking into arena...";
        }
        else
        {
            status = (locationMode == ArenaLocationMode.Banner)
                ? "Walking to banner..."
                : "Walking into arena...";
        }

        pending = true;
        pendingKind = h.MatchIncludesPrisoner()
            ? PendingKind.PrisonerRelease
            : PendingKind.Formation;
        elapsedSec = 0.0f;
        settleHoldSec = 0.0f;
        escortsParkIssued = false;
        lastTick = h.NowMs;
        PGLog.Debug("Proving Grounds: location ingress started");
        BeginNpcFormationProgress(h);
        if (!h.TownIsBusy())
            h.TownCancelPlannedNpcBout("Player match took priority");
    }

    // C++: ArenaIngress.cpp:1395-1411
    public void Cancel(IIngressHost<TId> h)
    {
        if (!pending)
            return;

        ClearPendingState(h);

        if (h.MatchIncludesPrisoner())
        {
            h.BeginReturnToCages(out string prisonerStatus);
            status = string.IsNullOrEmpty(prisonerStatus) ? "Ingress cancelled" : prisonerStatus;
        }
        else
        {
            status = "Ingress cancelled";
        }

        PGLog.Debug("Proving Grounds: arena ingress cancelled");
    }

    float StepDt(IIngressHost<TId> h, ref uint last)
    {
        uint now = h.NowMs;
        float dt = (float)unchecked(now - last) / 1000.0f;
        last = now;
        return dt;
    }

    // C++: ArenaIngress.cpp:1413-1715
    public void Tick(IIngressHost<TId> h)
    {
        if (!pending)
            return;
        if (h.TownIsBusy() && h.Paused)
        {
            lastTick = h.NowMs;
            h.AnnouncerTickApproach();
            return;
        }

        if (pendingKind == PendingKind.Countdown)
        {
            if (!HasValidSite(h) || AnyFighterInvalid(h))
            {
                ClearPendingState(h);
                status = "Countdown cancelled: fighter or site invalid";
                return;
            }

            elapsedSec += StepDt(h, ref lastTick);

            // Keep everyone frozen so sit-around jobs can't pull them away.
            for (int i = 0; i < fighters.Count; ++i)
            {
                ParkCharacterInPlace(h, fighters[i]);
                FaceOpposingFormation(h, i);
            }
            if (h.MatchIncludesPrisoner())
                h.ReinforceParkedHandlers();

            if (h.AnnouncerHasAnnouncement())
            {
                h.AnnouncerTickAnnouncement(elapsedSec);
                if (h.AnnouncerHasAnnouncement())
                {
                    status = h.AnnouncerGetStatus();
                    if (elapsedSec >= h.AnnouncerAnnouncementDuration())
                        FinishIngressAndStart(h);
                    return;
                }
                // A lost speaker switches to the legacy countdown so the staged fight cannot remain blocked.
                elapsedSec = TownAnnouncerPolicy.CountdownElapsedAfterFallback(
                    true, elapsedSec);
                countdownStage = -1;
            }

            if (elapsedSec < 0.60f)
            {
                status = "Fight starts in 3...";
                ShowFighterCountdown(h, 3, "3");
            }
            else if (elapsedSec < 1.20f)
            {
                status = "Fight starts in 2...";
                ShowFighterCountdown(h, 2, "2");
            }
            else if (elapsedSec < 1.80f)
            {
                status = "Fight starts in 1...";
                ShowFighterCountdown(h, 1, "1");
            }
            else
            {
                status = "Fight!";
                ShowFighterCountdown(h, 0, "FIGHT!");
            }

            if (elapsedSec >= ArenaFormation.FightCountdownSec)
                FinishIngressAndStart(h);
            return;
        }

        // C++: ArenaIngress.cpp:1490-1554 (the approach step)
        if (pendingKind == PendingKind.OpenUI)
        {
            if (!HasValidApproachBuilding(h))
            {
                ClearPendingState(h);
                status = "Interact building invalid during approach";
                return;
            }
            if (AnyFighterInvalid(h))
            {
                ClearPendingState(h);
                status = "Selection invalid during approach";
                return;
            }

            float stepDt = StepDt(h, ref lastTick);
            elapsedSec += stepDt;

            // Keep re-applying the move so RMB / use leftovers can't leave them idle.
            if (elapsedSec >= nextMoveIssueSec)
            {
                IssueApproachMoves(h);
                nextMoveIssueSec = elapsedSec + ArenaFormation.ApproachMoveReissueSec;
            }

            ArenaIngressPolicy.IngressObservation approach =
                BuildIngressObservation(h);
            if (!approach.AllFightersValid)
            {
                ClearPendingState(h);
                status = "Selection invalid during approach";
                return;
            }

            if (ArenaIngressPolicy.CanOpenApproachUi(approach))
            {
                PGLog.Debug("Proving Grounds: approach arrival (radius)");
                FinishApproachAndOpenUI(h);
                return;
            }

            if (approach.AllAtSettleRadius && approach.AllSettled)
            {
                settleHoldSec += stepDt;
                if (settleHoldSec >= ArenaFormation.SettleHoldSec)
                {
                    PGLog.Debug("Proving Grounds: approach arrival (settled)");
                    FinishApproachAndOpenUI(h);
                    return;
                }
            }
            else
            {
                settleHoldSec = 0.0f;
            }

            if (elapsedSec >= ArenaFormation.IngressTimeoutSec)
            {
                PGLog.Debug("Proving Grounds: approach timeout — opening UI anyway");
                FinishApproachAndOpenUI(h);
            }
            return;
        }

        if (!HasValidSite(h))
        {
            ClearPendingState(h);
            status = "Fight site destroyed during ingress";
            PGLog.Debug("Proving Grounds: fight site destroyed mid-ingress");
            return;
        }

        ArenaIngressPolicy.IngressObservation observation = BuildIngressObservation(h);
        if (!observation.AllFightersValid)
        {
            Cancel(h);
            status = "Fighter invalid during ingress";
            return;
        }

        float dt = StepDt(h, ref lastTick);
        elapsedSec += dt;
        h.AnnouncerTickApproach();

        if (h.MatchIncludesPrisoner())
        {
            h.TickHandlerUnlocks();
            for (int i = 0; i < fighters.Count; ++i)
            {
                TId fighter = fighters[i];
                if (h.ConsumeNewlyReleased(fighter))
                {
                    h.SendPairToArena(fighter, targets[i]);
                    PGLog.Debug("Proving Grounds: prisoner unlocked — walking to arena");
                }
            }

            if (elapsedSec >= nextMoveIssueSec)
            {
                IssueFormationMoves(h);
                nextMoveIssueSec = elapsedSec + ArenaFormation.ApproachMoveReissueSec;
            }
            ParkArrivedFighters(h);

            bool allPrisonersReleased =
                h.AllMatchPrisonersReleased();
            if (!allPrisonersReleased)
            {
                status = "Handlers releasing prisoners...";
                settleHoldSec = 0.0f;
                return;
            }

            if (ArenaIngressPolicy.ShouldAdvancePrisonerRelease(
                    pendingKind == PendingKind.PrisonerRelease,
                    h.MatchIncludesPrisoner(),
                    allPrisonersReleased))
            {
                pendingKind = PendingKind.Formation;
                elapsedSec = 0.0f;
                settleHoldSec = 0.0f;
                nextMoveIssueSec = 0.0f;
                IssueFormationMoves(h);
                BeginNpcFormationProgress(h);
                PGLog.Debug("Proving Grounds: all prisoners released - formation phase started");
            }
        }

        if (pendingKind != PendingKind.Formation)
            return;

        if (elapsedSec >= nextMoveIssueSec)
        {
            IssueFormationMoves(h);
            nextMoveIssueSec = elapsedSec + ArenaFormation.ApproachMoveReissueSec;
        }
        ParkArrivedFighters(h);
        UpdateNpcFormationProgress(h);
        observation = BuildIngressObservation(h);
        if (ArenaIngressPolicy.CanBeginCountdown(observation))
        {
            if (h.AnnouncerWaitingForPosition())
            {
                settleHoldSec = 0.0f;
                status = h.AnnouncerGetStatus();
                return;
            }
            if (h.BookieHoldForBets())
            {
                settleHoldSec = 0.0f;
                status = "NPC fighters ready - bookie accepting bets";
                return;
            }
            settleHoldSec += dt;
            if (settleHoldSec < ArenaFormation.FightGatherHoldSec)
            {
                status = "Fighters ready - crowd settling before the match";
                return;
            }
            PGLog.Debug("Proving Grounds: ingress arrival (radius)");
            BeginFightCountdown(h);
            return;
        }

        settleHoldSec = 0.0f;
        int outside = CountFightersOutsideFormation(h);
        status = "Waiting for " + U(outside) + " fighter" + (outside == 1 ? "" : "s") + " to reach formation...";

        // Give fighters making real progress time to reach their marks, but do not let disconnected navigation hold the wager indefinitely.
        if (h.TownIsBusy() && !h.TownIsPlayerMatch() &&
            elapsedSec >= ArenaFormation.NpcFormationTimeoutSec)
        {
            bool stalled = false;
            for (int i = 0; i < fighters.Count; ++i)
            {
                if (!FighterAtMark(h, i, ArenaFormation.FormationArrivalRadius) &&
                    (i >= npcFormationProgress.Count ||
                     elapsedSec - npcFormationProgress[i].LastProgressSec >= ArenaFormation.NpcFormationStallSec))
                    stalled = true;
            }
            bool hardCap = elapsedSec >= ArenaFormation.NpcFormationHardCapSec;
            if (!stalled && !hardCap)
            {
                if (!npcGraceLogged)
                {
                    PGLog.Debug("Proving Grounds: NPC formation extended while missing fighters approach their marks");
                    npcGraceLogged = true;
                }
                return;
            }
            bool wagerPending = h.BookieHasPendingWager();
            for (int i = 0; i < fighters.Count; ++i)
            {
                TId fighter = fighters[i];
                if (PositionOf(h, fighter) is not { } position) continue;
                Vec3 target = targets[i];
                float horizontal = MathF.Sqrt(ArenaFormation.HorizontalDistSq(position, target));
                bool blocked = !FighterAtMark(h, i, ArenaFormation.FormationArrivalRadius);
                float progressAge = i < npcFormationProgress.Count
                    ? elapsedSec - npcFormationProgress[i].LastProgressSec : elapsedSec;
                bool stuck = blocked && progressAge >= ArenaFormation.NpcFormationStallSec;
                Vec3 destination = h.Destination(fighter);
                PGLog.Debug("Proving Grounds: ingress timeout fighter slot=" + U(i + 1) + " name=" + h.Name(fighter)
                    + " blocked=" + (blocked ? "1" : "0") + " stalled=" + (stuck ? "1" : "0") + " progressAge=" + F(progressAge, 1)
                    + " horizontal=" + F(horizontal, 1) + " vertical=" + F(position.Y - target.Y, 1)
                    + " moving=" + (h.IsMoving(fighter) ? "1" : "0") + " speed=" + F(h.MovementSpeed(fighter), 2)
                    + " position=(" + F(position.X, 1) + "," + F(position.Y, 1) + "," + F(position.Z, 1) + ")"
                    + " target=(" + F(target.X, 1) + "," + F(target.Y, 1) + "," + F(target.Z, 1) + ")"
                    + " destination=(" + F(destination.X, 1) + "," + F(destination.Y, 1) + "," + F(destination.Z, 1) + ")");
                if (stuck) h.TownQuarantineIngressFighter(fighter);
            }
            Cancel(h);
            status = hardCap ? "NPC formation hard cap reached; original lineup cancelled" :
                "NPC formation stalled; original lineup cancelled";
            if (wagerPending) status += "; wager refunded";
            PGLog.Debug("Proving Grounds: " + status);
        }
    }

    // C++: ArenaIngress.cpp:1717-1758
    public bool BeginWalkIn(IIngressHost<TId> h, TId fighter, MatchRules.MatchTeam team)
    {
        if (PositionOf(h, fighter) is not { } at)
            return false;
        if (walkInPending)
            return false;
        if (team != MatchRules.MatchTeam.TeamA && team != MatchRules.MatchTeam.TeamB)
            return false;

        if (h.FindSiteForMode(locationMode, N(at)) is not { } site)
            return false;

        walkInTarget = ArenaFormation.WalkInTarget(Formation(locationMode), team, V(site.Position), Ori(site));

        walkInFighter = fighter;
        walkInPending = true;
        walkInElapsedSec = 0.0f;
        walkInNextMoveSec = 0.0f;
        walkInLastTick = h.NowMs;
        h.LeaveStaleSweep(fighter);   // port only (task 3.5's sweep would stop the walk of a fighter it still watches)
        IssueMove(h, fighter, walkInTarget);
        PGLog.Debug("Proving Grounds: Teams 1v1 walk-in started");
        return true;
    }

    // C++: ArenaIngress.cpp:1760-1763
    public TId GetWalkInFighter() => walkInPending ? walkInFighter : none;

    // C++: ArenaIngress.cpp:1765-1768
    public bool IsWalkInPending() => walkInPending;

    // C++: ArenaIngress.cpp:1770-1776
    public bool IsWalkInArrived(IIngressHost<TId> h)
    {
        if (!walkInPending || PositionOf(h, walkInFighter) is not { } p)
            return false;
        return ArenaFormation.HorizontalDistSq(p, walkInTarget)
            <= (ArenaFormation.ArrivalRadius * ArenaFormation.ArrivalRadius);
    }

    // C++: ArenaIngress.cpp:1778-1804
    public void TickWalkIn(IIngressHost<TId> h)
    {
        if (!walkInPending)
            return;

        if (!Valid(h, walkInFighter))
        {
            ClearWalkIn(h);
            return;
        }

        walkInElapsedSec += StepDt(h, ref walkInLastTick);

        if (walkInElapsedSec >= walkInNextMoveSec)
        {
            IssueMove(h, walkInFighter, walkInTarget);
            walkInNextMoveSec = walkInElapsedSec + ArenaFormation.ApproachMoveReissueSec;
        }

        // Timeout: treat as arrived so the bout can continue.
        if (walkInElapsedSec >= ArenaFormation.IngressTimeoutSec && PositionOf(h, walkInFighter) is { } p)
            walkInTarget = p;
    }

    // C++: ArenaIngress.cpp:1806-1816
    public void ClearWalkIn(IIngressHost<TId> h)
    {
        if (Valid(h, walkInFighter))
            h.RemoveMoveJob(walkInFighter);
        walkInPending = false;
        walkInFighter = none;
        walkInTarget = new Vec3(0.0f, 0.0f, 0.0f);
        walkInNextMoveSec = 0.0f;
        walkInElapsedSec = 0.0f;
        walkInLastTick = 0;
    }
}
