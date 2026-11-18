using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/TownArena.h/.cpp (Proving Grounds by Shutori, GPLv3), M7 (TW §2.1-2.5, CAP 2, 5, 6, 11, 18-20): the C++
// decisions of the namespace over ITownHost (the character, building, clock and other-subsystem reads). The glue (src/Town/TownArena.cs) binds the
// host to a world and to CharacterIds; Core never sees an engine type. A building is an ArenaBuilding (held across ticks as its ArenaBuildingRef,
// null = the C++ NULL / an invalid hand), a character a TId, a town a long key (TownKey: the glue's encoding of Meitou's CampaignTown).
// Split by the plan's task groups (partial class):
//   TownArenaLogic.cs ............ task 7.1: the file-level state (TownArena.cpp:56-118), Status, Finish (:397-428), the state accessors (:1318-1336,
//                                   :1405-1414, :1457-1462), CancelBooking (:1337-1351), Cancel / CancelForSave / AbandonWorldState (:1781-1858)
//   TownArenaLogic.Scheduling.cs . task 7.1: DiscoverTownRegistry / HasPlayerInRegistryTown (:368-395), BindRegistry / BindRegistryNear (:1296-1317),
//                                   SetScheduleEnabled (:1352-1359), Tick (:1860-2066: the stop rules and the 5 s schedule clock; its bout parts are 7.3)
//   TownArenaLogic.Bouts.cs ...... task 7.3: CancelPlannedNpcBout (:620-632, done in 7.1: the stop rule and Cancel need it),
//                                   PlannedNpcReady, PlanNpcBout, the restored-bout wait, the next event and BeginEvent, the event phases of Tick
//   TownArenaLogic.Cards.cs ...... task 7.5: the card (Challenges, the previews, quotes, captions, booking, the paid refresh, settlement, progress)
//   TownArenaLogic.Matchmaking.cs  task 7.2: IngressQuarantined (:120-125), PlayerLineupKey / EligibleOpponent (:157-173), Ready (:297-346),
//                                   RefreshMatchmakingRoster / SnapshotFor (:574-590), the throttle and frozen roster of UpdateMatchmakingPreview
//                                   (:746-793; the rest of that function is task 7.5's)
//   TownArenaLogic.Rules.cs ...... task 7.4: IsIncidentPair / IsActiveCollateralIncidentPair (:1415-1438), the state the combat hooks publish for
//                                   them, TownIncidentPairs (their loops, shared with the hooks' view) and the after-bout TownBoutForgiveness

/// <summary>A town key (the glue's encoding of a Meitou <c>CampaignTown</c>: a placed site's index, a player town's number negated) and the C++
/// "same town" test (<c>getTown() == getCurrentTownLocation()</c> with neither NULL, TW §2.1, as <see cref="TownRule.SameTown"/>).</summary>
public static class TownKey
{
    /// <summary>No town (the C++ NULL <c>Town*</c>).</summary>
    public const long None = long.MinValue;

    /// <summary>Both name the same town and neither is "no town".</summary>
    public static bool Same(long a, long b) => a != None && a == b;
}

/// <summary>A building of a sphere query or a held building resolved now: the building as ArenaIdentity reads it, and its town (the C++
/// <c>Building::getTown()</c>, <see cref="TownKey.None"/> for none).</summary>
public readonly record struct PlacedBuilding(ArenaBuilding Building, long Town);

/// <summary>C++ <c>enum Phase</c> (TownArena.cpp:58).</summary>
public enum TownPhase { Idle, Preparing, Fighting, Aftercare }

/// <summary>What TownArena.cpp reads of the world and calls in the other subsystems, for task 7.1 (later tasks add their members). The glue
/// implements it over PgEngine, src/Engine/Towns.cs, ArenaIdentity, ArenaIngress, SparSession, ArenaMedical, WorldLifecycle and the M8
/// placeholders (src/Town/TownBookie.cs, TownAnnouncer.cs, TownLimbShop.cs); the test fake in Tests/Glue/TownArenaLogicTests.cs.</summary>
public interface ITownHost<TId> : ITownRosterHost<TId>
{
    // ---------- the world ----------
    /// <summary><c>ou-&gt;isPaused()</c>. The port's step never runs while paused (a paused world runs no SlowWorld): always false there.</summary>
    bool IsPaused { get; }
    /// <summary><c>GetTickCount()</c>: the sim clock's milliseconds (PgClock.NowMs, frozen while paused).</summary>
    uint NowMs { get; }
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);

    // ---------- characters ----------
    /// <summary><c>c-&gt;getPosition()</c> (read only after <see cref="Valid"/>).</summary>
    Vector3 Position(TId c);
    /// <summary><c>ou-&gt;player-&gt;getAllPlayerCharacters()</c>; null when there is no player (<c>!ou-&gt;player</c>).</summary>
    IReadOnlyList<TId>? PlayerCharacters();

    // ---------- buildings ----------
    /// <summary><c>ou-&gt;getObjectsWithinSphere(nearby, origin, radius, BUILDING, max, NULL)</c>: each with its town; null when there is no world.</summary>
    IReadOnlyList<PlacedBuilding>? BuildingsInSphere(Vector3 origin, float radius, int max);
    /// <summary>The held building as it is now (<c>hand::getBuilding()</c> of a valid hand) with its town; null when it is gone or names another record.</summary>
    PlacedBuilding? Resolve(ArenaBuildingRef held);

    // ---------- other subsystems ----------
    /// <summary><c>WorldLifecycle::IsArenaOperationBusy()</c> (WorldLifecycle.cpp:235-249).</summary>
    bool ArenaOperationBusy();
    /// <summary>Port only (WorldLifecycle.cs, done in task 7.1): a save the sim prepared is queued and not captured yet, or a manual save waits for cage
    /// locks; nothing may start town activity then (the C++ prepares and saves in one call, so it has no such gap).</summary>
    bool SaveInFlight { get; }
    /// <summary><c>ArenaIngress::IsPending()</c>.</summary>
    bool IngressPending();
    /// <summary><c>ArenaIngress::Cancel()</c>.</summary>
    void IngressCancel();
    /// <summary><c>SparSession::IsActive()</c>.</summary>
    bool SparIsActive();
    /// <summary><c>SparSession::GetParticipantCount()</c>.</summary>
    int SparParticipantCount();
    /// <summary><c>SparSession::GetParticipant(i)</c> (none for an empty slot).</summary>
    TId SparParticipant(int i);
    /// <summary><c>SparSession::AbortForSave()</c>.</summary>
    void SparAbortForSave();
    /// <summary>Port only (task 7.4, its end moved to Finish by task 8.4b): the town event ends (Finish, after its aftercare): the town rules'
    /// after-bout clearing of TEMP_ENEMY runs now (nothing when no owned match start was recorded). Finish runs in every path that ends an event
    /// (the aftercare's end, Cancel, CancelForSave and the stop rule), so the clearing still comes before any save capture.</summary>
    void TownRulesEventEnded();
    /// <summary><c>ArenaMedical::GetProtocol()</c>.</summary>
    ArenaMedicalPolicy.Protocol GetProtocol();
    /// <summary><c>ArenaMedical::SetProtocol(p)</c>.</summary>
    void SetProtocol(ArenaMedicalPolicy.Protocol p);

    // ---------- M8 subsystems (tasks 8.1a-8.7) ----------
    /// <summary><c>TownAnnouncer::Release()</c>.</summary>
    void AnnouncerRelease();
    /// <summary><c>TownAnnouncer::TickReleases()</c>.</summary>
    void AnnouncerTickReleases();
    /// <summary><c>TownAnnouncer::AbandonWorldState()</c>.</summary>
    void AnnouncerAbandon();
    /// <summary><c>TownSpectators::Release()</c>.</summary>
    void SpectatorsRelease();
    /// <summary><c>TownSpectators::Tick()</c>.</summary>
    void SpectatorsTick();
    /// <summary><c>TownSpectators::AbandonWorldState()</c>.</summary>
    void SpectatorsAbandon();
    /// <summary><c>TownCorpseCleanup::Release()</c>.</summary>
    void CorpseCleanupRelease();
    /// <summary><c>TownCorpseCleanup::Tick()</c>.</summary>
    void CorpseCleanupTick();
    /// <summary><c>TownCorpseCleanup::AbandonWorldState()</c>.</summary>
    void CorpseCleanupAbandon();
    /// <summary><c>TownAftercare::Release()</c>.</summary>
    void AftercareRelease();
    /// <summary><c>TownAftercare::TickBedRecovery()</c>.</summary>
    void AftercareTickBedRecovery();
    /// <summary><c>TownAftercare::AbandonWorldState()</c>.</summary>
    void AftercareAbandon();
    /// <summary><c>TownLimbShop::IsActive()</c>.</summary>
    bool LimbShopIsActive();
    /// <summary><c>TownLimbShop::Tick()</c>.</summary>
    void LimbShopTick();
    /// <summary><c>TownLimbShop::AbandonWorldState()</c>.</summary>
    void LimbShopAbandon();
    /// <summary><c>TownBookie::Settle(outcome)</c>.</summary>
    void BookieSettle(int outcome);
    /// <summary><c>TownBookie::RefundUnrestoredWager(saved)</c>.</summary>
    void BookieRefundUnrestoredWager(ArenaPersistence.PlannedNpcBout saved);
    /// <summary><c>TownBookie::CancelNpcBout(reason)</c>.</summary>
    void BookieCancelNpcBout(string reason);
    /// <summary><c>TownBookie::AbandonWorldState()</c>.</summary>
    void BookieAbandon();
    /// <summary><c>TownDiagnosticRun::CancelPending()</c> (the controlled diagnostic run, task 8.7a: src/Town/TownDiagnosticRun.cs).</summary>
    void DiagnosticCancelPending();
    /// <summary><c>TownMatchmakingSearch::Shutdown()</c> (the background search, task 7.2).</summary>
    void MatchmakingShutdown();

    // ---------- task 7.2: the background search (TownMatchmakingSearch.h) ----------
    /// <summary><c>TownMatchmakingSearch::BeginGeneration(generation)</c>.</summary>
    void MatchmakingBeginGeneration(uint generation);
    /// <summary><c>TownMatchmakingSearch::Enqueue(request)</c> (the worker copies it).</summary>
    bool MatchmakingEnqueue(TownMatchmakingSearch.Request request);
    /// <summary><c>TownMatchmakingSearch::Poll(result)</c>.</summary>
    bool MatchmakingPoll(out TownMatchmakingSearch.Result? result);

    // ---------- task 7.2: readiness (TownArena.cpp:297-346) ----------
    /// <summary><c>c-&gt;isDead()</c>.</summary>
    bool IsDead(TId c);
    /// <summary><c>c-&gt;isUnconcious()</c>.</summary>
    bool IsUnconscious(TId c);
    /// <summary><c>c-&gt;isInCombatMode(true, true)</c>: in a melee or a ranged fight.</summary>
    bool InCombatMode(TId c);
    /// <summary><c>ArenaMedical::NeedsStabilization(c)</c>.</summary>
    bool NeedsStabilization(TId c);
    /// <summary><c>RosterStatus::Read(c, health, reason)</c> (Core <see cref="RosterStatus.Read"/> over the character's medical reads).</summary>
    bool ReadHealth(TId c, out RosterStatus.Snapshot health, out string error);
    /// <summary><c>!med-&gt;isInBloodlossTrauma() &amp;&amp; !med-&gt;isProbablyDying() &amp;&amp; !med-&gt;isCrippled() &amp;&amp; med-&gt;hasAnArmToFightWith()</c>
    /// (TownArena.cpp:325-326; read for medics only).</summary>
    bool MedicallyAble(TId c);
    /// <summary><c>LeaderboardStore::HasActiveSave()</c>.</summary>
    bool HasActiveSave { get; }

    // ---------- task 7.3: NPC-vs-NPC bouts (TownArena.cpp:635-697, 1086-1097, 1464-1520, 1569-1751, 1890-2066) ----------
    // The C++ NULL Character* is ITownRosterHost.None (task 7.3).
    /// <summary><c>ou-&gt;getTimeStamp_inGameHours().getTotalHours()</c> (read only with a world).</summary>
    double TotalHours { get; }
    /// <summary><c>LeaderboardStore::GetTownChallenges()</c>: the held card (a reference: <c>RecordHistory</c> and the ambient stream write it).</summary>
    TownChallengePolicy.Card TownChallenges();
    /// <summary><c>registry-&gt;getInstanceID()-&gt;uid</c> of a held building ("" when it has none); the saved bout's registry key.</summary>
    string BuildingUid(ArenaBuildingRef building);
    /// <summary><c>c-&gt;getInstanceID()-&gt;uid</c> (<c>LegacyOpponentId</c>, TownArena.cpp:136-140; "" when none).</summary>
    string CharacterUid(TId c);
    /// <summary><c>ou-&gt;showPlayerAMessage(text, true)</c>.</summary>
    void ShowPlayerMessage(string text);
    /// <summary><c>ArenaIngress::GetLocationMode()</c>.</summary>
    ArenaLocationMode IngressGetLocationMode();
    /// <summary><c>ArenaIngress::SetLocationMode(mode)</c>.</summary>
    void IngressSetLocationMode(ArenaLocationMode mode);
    /// <summary><c>ArenaIngress::Begin(mode, pair, teams, count, NULL, 0, arena)</c> (the town arena as the site override).</summary>
    bool IngressBegin(MatchRules.MatchMode mode, TId[] fighters, MatchRules.MatchTeam[] teams, int count, ArenaBuildingRef arena);
    /// <summary><c>ArenaIngress::GetStatus()</c>.</summary>
    string IngressGetStatus();
    /// <summary><c>SparSession::GetMode()</c>.</summary>
    MatchRules.MatchMode SparMode();
    /// <summary><c>SparSession::IsEliminated(c)</c>.</summary>
    bool SparIsEliminated(TId c);
    /// <summary><c>ArenaRingGuard::SetArena(arena)</c> (TownArena.cpp:1715).</summary>
    void RingGuardSetArena(ArenaBuildingRef arena);
    /// <summary>Port only (spike S10, overview §9): the town fighters this bout borrowed (CharacterCold.Engaged, the Meitou counterpart of the
    /// C++ <c>clearAllAIGoals</c> + STAND_STILL goal of ArenaIngress.cpp:437, :608-614) are given back to their town AI (<c>Engaged = false;
    /// Ai.Replan = true</c>) when the town event ends (<see cref="TownArenaLogic{TId}"/>'s Finish).</summary>
    void ReleaseTownHolds();
    /// <summary>Port only (task 7.4): the town's match has started (the town rules record the bout's TEMP_ENEMY state, from the fighters held
    /// in the state).</summary>
    void TownRulesBoutStarted();

    // ---- M8 placeholders the bout calls (their answers are a world with no market, announcer, spectators, aftercare or diagnostic run) ----
    /// <summary><c>TownBookie::BeginNpcBout(fighters, teams, count, scoreA, scoreB)</c>.</summary>
    void BookieBeginNpcBout(TId[] fighters, MatchRules.MatchTeam[] teams, int count, double scoreA, double scoreB);
    /// <summary><c>TownBookie::PreparationValid()</c>.</summary>
    bool BookiePreparationValid();
    /// <summary><c>TownBookie::HasPendingWager()</c>.</summary>
    bool BookieHasPendingWager();
    /// <summary><c>TownBookie::NpcAssembling()</c>.</summary>
    void BookieNpcAssembling();
    /// <summary><c>TownBookie::CombatStarted()</c>.</summary>
    void BookieCombatStarted();
    /// <summary><c>TownBookie::CapturePlannedNpcBout(out)</c>.</summary>
    void BookieCapturePlannedNpcBout(ArenaPersistence.PlannedNpcBout output);
    /// <summary><c>TownBookie::RestorePlannedNpcBout(saved)</c>.</summary>
    bool BookieRestorePlannedNpcBout(ArenaPersistence.PlannedNpcBout saved);
    /// <summary><c>TownAnnouncer::Begin(arena, playerMatch, challenge, roles, roleCount, fighters, fighterCount)</c>.</summary>
    void AnnouncerBegin(ArenaBuildingRef arena, bool playerMatch, int challenge, string[] roles, int roleCount, TId[] fighters, int fighterCount);
    /// <summary><c>TownSpectators::Begin(arena)</c>.</summary>
    void SpectatorsBegin(ArenaBuildingRef arena);
    /// <summary><c>TownSpectators::ReactToResult(result)</c>.</summary>
    void SpectatorsReactToResult(SparPodium.Snapshot result);
    /// <summary><c>TownCorpseCleanup::Begin(arena, fighters)</c>.</summary>
    void CorpseCleanupBegin(ArenaBuildingRef arena, TId[] fighters, int count);
    /// <summary><c>TownCorpseCleanup::Seal()</c>.</summary>
    void CorpseCleanupSeal();
    /// <summary><c>TownCorpseCleanup::AdmitArenaDeaths()</c>.</summary>
    void CorpseCleanupAdmitArenaDeaths();
    /// <summary><c>TownCorpseCleanup::HasPending()</c>.</summary>
    bool CorpseCleanupHasPending();
    /// <summary><c>TownCorpseCleanup::GetStatus()</c>.</summary>
    string CorpseCleanupGetStatus();
    /// <summary><c>TownAftercare::ControlAvailable()</c>.</summary>
    bool AftercareControlAvailable();
    /// <summary><c>TownAftercare::Begin(doctors, fighters, arena, standby)</c>.</summary>
    void AftercareBegin(List<TId> doctors, TId[] fighters, int count, ArenaBuildingRef arena, Vector3 standby);
    /// <summary><c>TownAftercare::Standby()</c>.</summary>
    void AftercareStandby();
    /// <summary><c>TownAftercare::MarkWinner(c)</c>.</summary>
    void AftercareMarkWinner(TId c);
    /// <summary><c>TownAftercare::Treat(elapsed, eliminatedOnly)</c>: true while care goes on.</summary>
    bool AftercareTreat(float elapsed, bool eliminatedOnly);
    /// <summary><c>TownAftercare::GetStatus()</c>.</summary>
    string AftercareGetStatus();
    /// <summary><c>TownLimbShop::Begin(registry, participants, count)</c> (null = the C++ NULL registry).</summary>
    void LimbShopBegin(ArenaBuildingRef? registry, TId[] participants, int count);
    /// <summary><c>TownDiagnosticRun::NextRequest()</c>.</summary>
    TownMatchmakingPolicy.DiagnosticRequest DiagnosticNextRequest();
    /// <summary><c>TownDiagnosticRun::Assign(match, hours)</c>.</summary>
    void DiagnosticAssign(TownMatchmakingPolicy.Match match, double hours);
    /// <summary><c>TownDiagnosticRun::CompletePending(result, hours)</c>.</summary>
    void DiagnosticCompletePending(SparPodium.Snapshot result, double hours);
    /// <summary><c>TownDiagnosticRun::AbortPending()</c>.</summary>
    void DiagnosticAbortPending();
    /// <summary><c>TownDiagnosticRun::RetryPending()</c>.</summary>
    void DiagnosticRetryPending();

    // ---------- task 7.5: the challenge card (TownArena.cpp:177-293, 347-366, 467-564, 740-1072, 1099-1295, 1383-1404, 1525-1733) ----------
    /// <summary>The key a save's first card is seeded from (C++ <c>LeaderboardStore::GetActiveSaveKey()</c>, TownArena.cpp:244-248). Port: the
    /// snapshot's <c>generation</c> (Meitou's save key is the fixed "meitou", docs/divergences.md "TownArena challenge cards").</summary>
    string CardSeedKey { get; }
    /// <summary><c>LeaderboardStore::GetChallengeAccount()</c>: the held credit and escrow (a reference).</summary>
    ref TownChallengeBuyInPolicy.Account ChallengeAccount();
    /// <summary><c>TownChallengeWallet::Balance()</c>: the player's money (-1 with no player).</summary>
    int WalletBalance();
    /// <summary><c>TownChallengeWallet::Take(amount)</c>.</summary>
    bool WalletTake(int amount);
    /// <summary><c>TownChallengeWallet::Credit(amount)</c>.</summary>
    bool WalletCredit(int amount);
    /// <summary><c>CombatBalanceLog::ChallengeSettled(outcome, credit)</c>.</summary>
    void LogChallengeSettled(int outcome, int credit);
    /// <summary><c>CombatBalanceLog::ChallengeAccepted(card)</c>.</summary>
    void LogChallengeAccepted(CombatBalanceLog.ChallengeCard card);
}

/// <summary>The C++ file-level state of TownArena.cpp (:56-118) that task 7.1 needs; later tasks add theirs (the previews, searches and quotes of
/// 7.2 / 7.5, the quarantine of 7.3) and reset them in <see cref="TownArenaLogic{TId}.AbandonWorldState"/>. Held buildings are refs (the C++
/// <c>hand</c>), resolved again each time they are read. One per world, owned by the system.</summary>
public sealed class TownArenaState<TId>
{
    public const int MaxFighters = 16;                                       // g_fighters[16]

    public TownPhase Phase = TownPhase.Idle;                                 // g_phase
    // ---- task 7.5: the card's display globals (TownArena.cpp:60-61, 68, 98-105) live in one Core DisplayState, which the caption functions read;
    // g_queuedChallenge, g_activeChallenge and g_bookingPending are its fields, read and written here through these properties (one copy). ----
    /// <summary>g_previews, g_directPreview, g_challengeReservation, g_unavailableReasons, g_buyInTicket, g_buyInQuotes and the three globals
    /// below (Core <see cref="TownArena.DisplayState"/>).</summary>
    public readonly TownArena.DisplayState Display = new();
    public int QueuedChallenge { get => Display.QueuedChallenge; set => Display.QueuedChallenge = value; }   // g_queuedChallenge
    public int ActiveChallenge { get => Display.ActiveChallenge; set => Display.ActiveChallenge = value; }   // g_activeChallenge
    public TownChallengePolicy.Offer DirectBooking = new();                  // g_directBooking
    public readonly bool[] BestAvailable = new bool[5];                      // g_bestAvailable[5]
    public int PaidRefreshCountBeforeBooking;                                // g_paidRefreshCountBeforeBooking
    public bool PaidRefreshResetPending;                                     // g_paidRefreshResetPending
    public bool ResultRecorded;                                              // g_resultRecorded
    public uint LastCardDiscovery;                                           // g_lastCardDiscovery
    public bool SchedulePaused;                                              // g_schedulePaused
    public bool PlayerMatch;                                                 // g_playerMatch
    public float ActiveMarksMultiplier = 1.0f;                               // g_activeMarksMultiplier
    public int ActiveMarksBonus;                                             // g_activeMarksBonus
    public bool BookingPending { get => Display.BookingPending; set => Display.BookingPending = value; }     // g_bookingPending
    public bool QuietDiscovery;                                              // g_quietDiscovery
    public TId BookedPlayer;                                                 // g_bookedPlayer (NULL at start: the constructor's none)
    public ArenaBuildingRef? BookedRegistry;                                 // g_bookedRegistry
    public ArenaBuildingRef? TownRegistry;                                   // g_townRegistry
    public ArenaBuildingRef? ActiveRegistry;                                 // g_activeRegistry
    public double NextNpcHours;                                              // g_nextNpcHours
    public TownMatchmakingPolicy.Match PlannedNpc = new();                   // g_plannedNpc
    public ArenaBuildingRef? PlannedRegistry;                                // g_plannedRegistry
    public readonly List<TId> PlannedFighters = [];                          // g_plannedFighters
    public ArenaPersistence.PlannedNpcBout RestoredNpc = new();              // g_restoredNpc
    public bool RestoredNpcPending;                                          // g_restoredNpcPending
    public double RestoreObservedHours;                                      // g_restoreObservedHours
    public uint LastScheduleCheck;                                           // g_lastScheduleCheck (GetTickCount -> PgClock.NowMs; 0 = "check now")
    public readonly TId[] Fighters = new TId[MaxFighters];                   // g_fighters (every slot NULL at start: the constructor's none)
    public readonly MatchRules.MatchTeam[] FighterTeams = new MatchRules.MatchTeam[MaxFighters];   // g_fighterTeams
    public int FighterCount;                                                 // g_fighterCount
    public int PlayerCount;                                                  // g_playerCount
    public readonly List<TId> BookedTeam = [];                               // g_bookedTeam
    public ArenaMedicalPolicy.Protocol PreviousProtocol = ArenaMedicalPolicy.Protocol.RingsideAid;   // g_previousProtocol
    public bool SequentialCare;                                              // g_sequentialCare
    public float AftercareElapsed;                                           // g_aftercareElapsed
    public uint AftercareTick;                                               // g_aftercareTick
    public uint LastAid;                                                     // g_lastAid
    public string Status = TownArena.Text.Idle;                              // g_status
    public uint PreviewTick;                                                 // g_previewTick
    // ---- task 7.2: the matchmaking snapshot and the background search (TownArena.cpp:104-117) ----
    public readonly bool[] Searching = new bool[6];                          // g_searching[6] (task 7.5 sets it)
    public readonly bool[] SearchQueued = new bool[5];                       // g_searchQueued[5] (task 7.5 sets it; the frozen roster reads it)
    public readonly bool[] SearchCompleted = new bool[5];                    // g_searchCompleted[5] (task 7.5 sets it)
    public uint SearchGeneration;                                            // g_searchGeneration
    public TownRosterSnapshot<TId> PreviewSnapshot = new();                  // g_previewSnapshot
    public bool PreviewSnapshotValid;                                        // g_previewSnapshotValid
    public uint PreviewSnapshotTick;                                         // g_previewSnapshotTick
    public string PreviewKey = "";                                           // g_previewKey
    public string RetryContext = "";                                         // g_retryContext
    public uint MatchmakingDiagnosticTick;                                   // g_matchmakingDiagnosticTick
    public readonly List<TId> IngressQuarantine = [];                        // g_ingressQuarantine (task 7.3 fills it)
    /// <summary>Port only (census: the town fills ~10 s after arrival): PgClock ms of the first settle-clock check (<c>ObserveRoster</c>, each second in
    /// Tick) that found a player in the registry's town since it was last found empty (0 = not observed); see <see cref="TownArenaLogic{TId}.RosterSettled"/>.</summary>
    public uint RosterObservedSince;
    /// <summary>Port only: PgClock ms of the last settle-clock check (once per second, <c>ObserveRoster</c>).</summary>
    public uint RosterObserveCheck;

    /// <summary>The state at start: every character slot the C++ NULL (<paramref name="none"/>, the host's <c>None</c>: a TId's default may name
    /// a real character, CharacterId's being slot 0).</summary>
    public TownArenaState(TId none)
    {
        BookedPlayer = none;
        Array.Fill(Fighters, none);
    }
    /// <summary>Port only: schedule checks made in this world (the 5 s clock of Tick; the test-only <c>pg town</c> shows it).</summary>
    public long ScheduleChecks;
}

/// <summary>TownArena.cpp's decisions over a host. One object per world (its <see cref="State"/>). Sim thread only.</summary>
public sealed partial class TownArenaLogic<TId>
{
    /// <summary>The C++ file-level state.</summary>
    public TownArenaState<TId> State { get; }

    /// <param name="none">The C++ NULL character (the host's <c>None</c>), which the state starts with.</param>
    public TownArenaLogic(TId none) { State = new TownArenaState<TId>(none); }
    TownArenaState<TId> s => State;

    static readonly EqualityComparer<TId> Eq = EqualityComparer<TId>.Default;

    // C++: TownArena.cpp:127-134 (the pop-up is dead code, kShowTownStatusPopups false :118; Core.TownArena.UpdateStatus)
    void Status(ITownHost<TId> h, string message)
    {
        string? line = TownArena.UpdateStatus(ref s.Status, message);
        if (line != null) h.Debug(line);
    }

    // C++: TownArena.cpp:397-428
    void Finish(ITownHost<TId> h, string message)
    {
        h.AnnouncerRelease();
        h.SpectatorsRelease();
        h.CorpseCleanupRelease();
        // The next market can already be open while this bout's medics finish.
        if (!s.PlannedNpc.Valid) h.BookieSettle(-1);
        // An ambient bout may finish while a paid player booking is queued.
        // Only the active player event owns settlement here.
        if (s.PlayerMatch && s.QueuedChallenge < 0) SettleChallenge(h, -1);
        if (s.ActiveChallenge >= 0)
        {
            if (s.ActiveChallenge != s.QueuedChallenge)
                CancelCardBooking(h, s.ActiveChallenge);
            s.ActiveChallenge = -1;
        }
        h.AftercareRelease();
        h.TownRulesEventEnded();   // port only (task 7.4; task 8.4b: at the event's end, as the C++'s IsIncidentPair holds through Aftercare :1424-1426)
        h.ReleaseTownHolds();   // port only (spike S10): the borrowed town fighters go back to their town AI
        h.SetProtocol(s.PreviousProtocol);
        s.Phase = TownPhase.Idle;
        s.SequentialCare = false;
        s.PlayerMatch = false;
        s.ActiveMarksMultiplier = 1.0f;
        s.ActiveMarksBonus = 0;
        s.ActiveRegistry = null;
        if (!s.PlannedNpc.Valid) s.NextNpcHours = 0.0;
        for (int i = 0; i < TownArenaState<TId>.MaxFighters; ++i) s.Fighters[i] = h.None;
        s.FighterCount = 0;
        s.PlayerCount = 0;
        s.PreviewTick = 0;
        if (s.QueuedChallenge < 0) ResetChallengeReservation();
        Status(h, message);
    }

    // ---------- the state accessors ----------

    // C++: TownArena.cpp:1318
    public bool IsScheduleEnabled() => !s.SchedulePaused;
    // C++: TownArena.cpp:1319
    public bool HasBooking() => s.BookingPending;
    // C++: TownArena.cpp:1320
    public bool IsPlayerMatch() => s.PlayerMatch;
    // C++: TownArena.cpp:1321
    public bool IsLineupLocked() => s.BookingPending || (s.PlayerMatch && s.Phase != TownPhase.Aftercare);
    // C++: TownArena.cpp:1331-1333
    public float GetActiveMarksMultiplier(ITownHost<TId> h) => s.PlayerMatch && OwnsMatch(h) ? s.ActiveMarksMultiplier : 1.0f;
    // C++: TownArena.cpp:1334-1336
    public int GetActiveMarksBonus(ITownHost<TId> h) => s.PlayerMatch && OwnsMatch(h) ? s.ActiveMarksBonus : 0;
    // C++: TownArena.cpp:1405
    public bool IsBusy() => s.Phase != TownPhase.Idle;
    // C++: TownArena.cpp:1406
    public bool IsAftercare() => s.Phase == TownPhase.Aftercare;
    // C++: TownArena.cpp:1407
    public string GetStatus() => s.Status;

    // C++: TownArena.cpp:1409-1414
    public bool IsFighter(ITownHost<TId> h, TId c)
    {
        if (h.IsNull(c) || (s.Phase != TownPhase.Preparing && s.Phase != TownPhase.Fighting)) return false;
        for (int i = 0; i < s.FighterCount; ++i) if (Eq.Equals(s.Fighters[i], c)) return true;
        return false;
    }

    /// <summary>The town bout's fighters while <see cref="IsFighter"/> can answer true (the combat hooks' published view, task 3.2b), appended.</summary>
    public void CopyFighters(List<TId> into)
    {
        if (s.Phase != TownPhase.Preparing && s.Phase != TownPhase.Fighting) return;
        for (int i = 0; i < s.FighterCount; ++i) into.Add(s.Fighters[i]);
    }

    /// <summary>Port only (task 10.1, the interop API's InBout): the town event's fighters from the walk to the ring (Preparing) through the
    /// fight to the end of the aftercare (Finish sets the phase back to Idle), appended.</summary>
    public void CopyEventFighters(List<TId> into)
    {
        if (s.Phase == TownPhase.Idle) return;
        for (int i = 0; i < s.FighterCount; ++i) into.Add(s.Fighters[i]);
    }

    // C++: TownArena.cpp:1457-1462
    public bool OwnsMatch(ITownHost<TId> h)
    {
        if (!h.SparIsActive() || s.FighterCount < 2 || h.SparParticipantCount() != s.FighterCount) return false;
        for (int i = 0; i < s.FighterCount; ++i) if (!IsFighter(h, h.SparParticipant(i))) return false;
        return true;
    }

    // ---------- cancelling ----------

    // C++: TownArena.cpp:1337-1351
    public void CancelBooking(ITownHost<TId> h)
    {
        if (s.QueuedChallenge >= 0) SettleChallenge(h, -1);
        if (s.QueuedChallenge >= 0)
            CancelCardBooking(h, s.QueuedChallenge);
        s.QueuedChallenge = -1;
        s.BookingPending = false;
        s.BookedPlayer = h.None;
        s.BookedTeam.Clear();
        s.BookedRegistry = null;
        Status(h, TownArena.Text.BookingCancelled);
        s.DirectBooking = new TownChallengePolicy.Offer();
        if (s.ActiveChallenge < 0) ResetChallengeReservation();
        s.PreviewTick = 0;
    }

    // C++: TownArena.cpp:1781-1785
    public void Cancel(ITownHost<TId> h)
    {
        CancelPlannedNpcBout(h, TownArena.Text.ScheduledCancelled);
        CancelForSave(h);
    }

    // C++: TownArena.cpp:1786-1795
    public void CancelForSave(ITownHost<TId> h)
    {
        // A distinct next market may already be announced during aftercare.
        // Abort the active operation without refunding that future wager.
        if (s.BookingPending) CancelBooking(h);
        if (!IsBusy()) return;
        if (s.Phase == TownPhase.Preparing && h.IngressPending()) h.IngressCancel();
        if (OwnsMatch(h)) AbortOwnedMatch(h);
        Finish(h, TownArena.Text.TrialCancelled);
    }

    /// <summary>C++: TownArena.cpp:1797-1858. No actor access (world teardown may already have destroyed them): only the other subsystems'
    /// own resets and the protocol, and (task 7.2) the matchmaking previews, searches and the quarantine list, (task 7.5) the offers, quotes and ticket.</summary>
    public void AbandonWorldState(ITownHost<TId> h)
    {
        h.MatchmakingShutdown();
        h.AnnouncerAbandon();
        h.CorpseCleanupAbandon();
        h.LimbShopAbandon();
        h.AftercareAbandon();
        h.SpectatorsAbandon();
        h.BookieAbandon();
        h.DiagnosticCancelPending();
        // No actor access: world teardown may already have destroyed them.
        s.QueuedChallenge = -1;
        s.ActiveChallenge = -1;
        s.LastCardDiscovery = 0;
        s.TownRegistry = null;
        s.ActiveRegistry = null;
        s.ResultRecorded = false;
        if (IsBusy()) h.SetProtocol(s.PreviousProtocol);
        s.Phase = TownPhase.Idle;
        s.SequentialCare = false;
        s.SchedulePaused = false;
        s.PlayerMatch = false;
        s.ActiveMarksMultiplier = 1.0f;
        s.ActiveMarksBonus = 0;
        s.BookingPending = false;
        s.BookedPlayer = h.None;
        s.BookedTeam.Clear();
        s.BookedRegistry = null;
        s.LastScheduleCheck = 0;
        s.NextNpcHours = 0.0;
        s.PlannedNpc = new TownMatchmakingPolicy.Match();
        s.PlannedRegistry = null; s.PlannedFighters.Clear();
        s.RestoredNpc = new ArenaPersistence.PlannedNpcBout();
        s.RestoredNpcPending = false;
        s.RestoreObservedHours = 0.0;
        for (int i = 0; i < TownArenaState<TId>.MaxFighters; ++i) s.Fighters[i] = h.None;
        s.FighterCount = 0;
        s.PlayerCount = 0;
        s.LastAid = 0;
        s.AftercareElapsed = 0.0f;
        s.AftercareTick = 0;
        s.QuietDiscovery = false;
        s.Status = TownArena.Text.Idle;
        s.PreviewKey = ""; s.RetryContext = ""; s.PreviewTick = 0;
        s.PreviewSnapshot = new TownRosterSnapshot<TId>();
        s.PreviewSnapshotValid = false;
        s.PreviewSnapshotTick = 0;
        s.SearchGeneration = 0;
        s.MatchmakingDiagnosticTick = 0;
        s.IngressQuarantine.Clear();
        for (int i = 0; i < 5; ++i) s.Display.Previews[i] = new TownChallengePolicy.Offer();
        s.Display.DirectPreview = new TownChallengePolicy.Offer();
        s.DirectBooking = new TownChallengePolicy.Offer();
        s.Display.ChallengeReservation = new TownChallengePolicy.Offer();
        s.Display.BuyInTicket = new TownChallengeBuyInPolicy.Ticket();
        for (int i = 0; i < 6; ++i) s.Display.BuyInQuotes[i] = new TownChallengeBuyInPolicy.Quote();
        for (int i = 0; i < 6; ++i) { s.Display.UnavailableReasons[i] = ""; s.Searching[i] = false; }
        for (int i = 0; i < 5; ++i)
        {
            s.SearchQueued[i] = false;
            s.SearchCompleted[i] = false;
            s.BestAvailable[i] = false;
        }
        s.ScheduleChecks = 0;   // port only
        s.RosterObservedSince = 0; s.RosterObserveCheck = 0;   // port only
    }

    // ---------- port only ----------

    /// <summary>Port only (the test-only <c>pg town</c> and the F10 row): "Scratch is active" as TW §2.1 states it: a finished Town Registry is bound
    /// (<c>g_townRegistry</c>, found by <see cref="BindRegistryNear"/>) and some player character's current town is the registry's town
    /// (<see cref="HasPlayerInRegistryTown"/>).</summary>
    public bool IsActive(ITownHost<TId> h) =>
        s.TownRegistry is { } r && h.Resolve(r) is { } b && ArenaIdentityLogic.IsRegistryFinished(b.Building) && HasPlayerInTown(h, b);
}
