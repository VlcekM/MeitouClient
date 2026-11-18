using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/LeaderboardStore.h/.cpp (Proving Grounds by Shutori, GPLv3), task 5.1 (PA §2.13-§2.16, CAP 9).
// Every function, in its order with its line range, is Core/Glue/LeaderboardStoreLogic.cs (fake-tested, Tests/Glue/LeaderboardStoreLogicTests.cs);
// this file binds it to a world and to CharacterIds. The ledger it reads and writes is the world's ArenaWorldState held by ArenaNativePersistence
// (task 2.4; C++ g_ledger, LeaderboardStore.cpp:29-34): no second copy. Meitou 0.17.3 (tag v0.17.3, fa34a1b). Sim thread, serial steps only.
//
// Engine calls of the C++ and what replaces them (E1; ILeaderboardStoreHost member -> adapter method):
//   character (non-null), c->isValid() .................... IsNull = CharacterId.IsNone; Valid = PgEngine.Exists (TryResolveNext)
//   FighterIdentity::GetOrCreate / Find (CharacterKey :38-45)  src/Game/FighterIdentity.cs GetOrCreate / Find (task 2.3)
//   c->getName() ........................................... PgEngine.Name (CharacterCold.Name)
//   c->getStats()->getOverallSkillLevel_0_100() (:261-262) .. PgEngine.Stats + OverallSkill.Of (spike S12; CombatStats.None = getStats() null)
//   c->isPlayerCharacter() (:119) .......................... PgEngine.IsPlayer
//   c->isChainedMode() (:120) .............................. PgEngine.IsChained (LockSystem.Chained, task 6.1, CAP 15)
//   c->isDead() (:662) ..................................... PgEngine.IsDead
//   PrisonerUtil::IsRosterPrisoner / IsMatchPrisoner / GetRosterPrisoners / GetMatchPrisoners ... src/Game/PrisonerUtil.cs (task 6.1)
//   SparSession::GetParticipant(snap.fighters[i].id) (:241, :352) ... SparSession.Participant (the result row's participant slot)
//   SquadUtil::CollectPlayerSquad (:623-624) ............... PgEngine.PlayerSquads (every member, in squad order)
//   ou->getObjectsWithinSphere(source->getPosition(), 2500, CHARACTER, 512) (:650-655) ... PgEngine.CharactersNear(world, building position,
//       2500, 512); source->isValid() ... the caller's ArenaBuilding (its position as found when the window opened)
//   PGLog::Debug / PGLog::Error ............................ PGLog.Debug / ModLog.Error (the C++ texts byte for byte)
//   GetTickCount() (CompleteWorldTransition :198) .......... ArenaNativePersistence.Resolve (task 2.4) passes Environment.TickCount
//
// Callers (C++ -> port): SparSession.cpp:713-715 ApplyFromSnapshot (PgSparHost, from StopWithStatus); ArenaUI.cpp:2262 GetRating (ArenaUISim);
// MarksHUD.cpp / DebugMenu.cpp GetMarks / GetRating / SetRating / SetMarks (MarksHUDSim, DebugMenuSim, the test-only pg give-rating / give-marks);
// LeaderboardUI.cpp GetStandings / FindRatedCharacter / HasActiveSave / GetActiveSaveKey (LeaderboardUISim); CombatBalanceLog.cpp:96, :148, :153
// GetRating / GetStandings (ProvingGroundsSystem.CombatFighter, PgSparHost.CombatLogStart); the purchases: RewardDelivery / RewardsUI /
// PrisonerRecruitment (tasks 5.2, 5.3, M6); the town accessors: TownArena / TownBookie / TownDiagnostic (M7, M8).

/// <summary>The LeaderboardStore of one world (task 5.1): one object, owned by ProvingGroundsSystem, over the world's arena state (task 2.4). Each
/// call binds the world and runs the C++ function in <see cref="LeaderboardStoreLogic{TId}"/>.</summary>
sealed class LeaderboardStore
{
    readonly ArenaNativePersistence arena;
    readonly FighterIdentity identity;
    readonly PrisonerUtil prisoners;
    readonly Func<SparSession?> session;
    readonly Host host;
    readonly LeaderboardStoreLogic<CharacterId> logic;

    /// <param name="session">The world's SparSession (built after the store; read lazily, as the C++ calls SparSession::GetParticipant).</param>
    internal LeaderboardStore(ArenaNativePersistence arena, FighterIdentity identity, PrisonerUtil prisoners, Func<SparSession?> session)
    {
        this.arena = arena;
        this.identity = identity;
        this.prisoners = prisoners;
        this.session = session;
        host = new Host(this);
        logic = new LeaderboardStoreLogic<CharacterId>(arena.State, host);
    }

    LeaderboardStoreLogic<CharacterId> L(World world) { host.World = world; return logic; }

    /// <summary>ILeaderboardStoreHost bound to the world of the current call: every store call binds it first (L) and finishes inside that
    /// serial step; the one lazy read that outlives a call, the rated-character index, binds its own captured world before each walk.</summary>
    sealed class Host(LeaderboardStore store) : ILeaderboardStoreHost<CharacterId>
    {
        internal World? World;
        World W => World ?? throw new InvalidOperationException("LeaderboardStore used before a world was bound");

        public bool IsNull(CharacterId c) => c.IsNone;
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool TryIdentity(CharacterId c, bool create, out string token) =>
            create ? store.identity.GetOrCreate(W, c, out token) : store.identity.Find(W, c, out token);
        public string Name(CharacterId c) => c.IsNone ? "" : PgEngine.Name(W, c) ?? "";
        public bool TryOverallSkill(CharacterId c, out float skill)
        {
            var stats = PgEngine.Stats(W, c);   // C++ c->getStats(): null = CombatStats.None (Valid false)
            skill = stats.Valid ? OverallSkill.Of(stats) : 0f;
            return stats.Valid;
        }
        public bool IsPlayerCharacter(CharacterId c) => PgEngine.IsPlayer(W, c);
        public bool IsChainedMode(CharacterId c) => PgEngine.IsChained(W, c);   // task 6.1 (CAP 15)
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public bool IsRosterPrisoner(CharacterId c) => !c.IsNone && store.prisoners.IsRosterPrisoner(W, c);
        public bool IsMatchPrisoner(CharacterId c) => !c.IsNone && store.prisoners.IsMatchPrisoner(W, c);
        public CharacterId Participant(int participantId) => store.session()?.Participant(W, participantId) ?? CharacterId.None;
        public IEnumerable<CharacterId> PlayerSquad()
        {
            foreach (var squad in PgEngine.PlayerSquads(W))
                foreach (var c in squad.Members)
                    yield return c;
        }
        // copies: the rated-character index walks these lazily (LeaderboardStoreLogic.PrimaryCandidates), and the C++ vectors behind them change
        // with the next discovery (roster) or the next GetMatchPrisoners call (its scratch); a copy is never modified under the walk
        public IEnumerable<CharacterId> RosterPrisoners() => [.. store.prisoners.GetRosterPrisoners(W)];
        public IEnumerable<CharacterId> MatchPrisoners() => [.. store.prisoners.GetMatchPrisoners(W)];
        public void Debug(string line) => PGLog.Debug(line);
        public void Error(string line) => ModLog.Error(line);
    }

    /// <summary>The C++ town board search (LeaderboardStore.cpp:650-655) around <paramref name="source"/>, or null for no building.</summary>
    static Func<IEnumerable<CharacterId>>? TownSearch(World world, ArenaBuilding? source) => source is { } at
        ? () => PgEngine.CharactersNear(world, at.Position, LeaderboardStoreLogic<CharacterId>.TownSearchRadius, LeaderboardStoreLogic<CharacterId>.TownSearchMax)
        : null;

    // ---------- the tables and the world state (no world needed) ----------

    /// <summary>C++ <c>g_data</c>: the standings and progression tables (the probe, the status row).</summary>
    internal LeaderboardData.Collections Data => logic.Data;

    // C++: LeaderboardStore.cpp:675-679
    internal bool HasActiveSave() => logic.HasActiveSave();
    // C++: LeaderboardStore.cpp:669-673
    internal string GetActiveSaveKey() => logic.GetActiveSaveKey();
    internal string GetActiveSaveGeneration() => logic.GetActiveSaveGeneration();   // task 7.5: the town card's first seed
    // C++: LeaderboardStore.cpp:172
    internal string GetPersistenceError() => logic.GetPersistenceError();
    // C++: LeaderboardStore.cpp:164-170 / :173 / :175-182 / :184-192 / :202-208 (ArenaWorldState's, task 2.4; Meitou's lifecycle drives them
    // through ArenaNativePersistence: Read / Resolve / Write)
    internal void BlockPersistence(string reason) => logic.BlockPersistence(reason);
    internal void Unload() => logic.Unload();
    internal void ActivateSnapshot(ArenaPersistence.Snapshot snapshot) => logic.ActivateSnapshot(snapshot);
    internal bool CaptureSnapshot(ArenaPersistence.Snapshot output) => logic.CaptureSnapshot(output);
    internal bool CommitSaveSnapshot(string sourceSaveName, string targetSaveName) => logic.CommitSaveSnapshot(sourceSaveName, targetSaveName);
    // C++: LeaderboardStore.cpp:596-599
    internal bool CompleteProgressionTransaction() => logic.CompleteProgressionTransaction();

    // ---------- town-only accessors (LeaderboardStore.cpp:91-156; read by TownArena (M7), TownDiagnosticRun (8.7) and TownBookie (8.1a: the credit)) ----------
    internal ref TownDiagnosticData.State GetTownDiagnostic() => ref logic.GetTownDiagnostic();
    internal string GetDiagnosticError() => logic.GetDiagnosticError();
    internal void ClearDiagnosticError() => logic.ClearDiagnosticError();
    internal bool ResetTownStandings() => logic.ResetTownStandings();
    internal TownChallengePolicy.Card GetTownChallenges() => logic.GetTownChallenges();
    internal int BookieCredit { get => logic.BookieCredit; set => logic.BookieCredit = value; }
    internal ref TownChallengeBuyInPolicy.Account GetChallengeAccount() => ref logic.GetChallengeAccount();
    internal bool RemoveTownNpcProgression(World world, CharacterId c) => L(world).RemoveTownNpcProgression(c);

    // ---------- identity ----------

    /// <summary>C++ <c>CharacterKey(character, create)</c> (LeaderboardStore.cpp:38-45): "" when not Ready, not valid or no identity.</summary>
    internal string CharacterKey(World world, CharacterId c, bool create) => L(world).CharacterKey(c, create);

    /// <summary>C++ <c>LogMissingPersistentIdentity</c> (LeaderboardStore.cpp:47-53), once per character until the ledger is next activated.</summary>
    internal void LogMissingIdentity(World world, CharacterId c, string operation) => L(world).LogMissingPersistentIdentity(c, operation);

    // ---------- the match ----------

    /// <summary>C++: LeaderboardStore.cpp:210-367 (SparSession's StopWithStatus, SparSession.cpp:713-715).</summary>
    internal bool ApplyFromSnapshot(World world, SparPodium.Snapshot snapshot, LeaderboardData.Kind kind, float marksMultiplier = 1.0f, int marksTeamBonus = 0) =>
        L(world).ApplyFromSnapshot(snapshot, kind, marksMultiplier, marksTeamBonus);

    // ---------- reads ----------

    // C++: LeaderboardStore.cpp:397-423
    internal List<LeaderboardData.Standing> GetStandings(World world, LeaderboardData.Kind kind) => L(world).GetStandings(kind);
    // C++: LeaderboardStore.cpp:425-439
    internal float GetRating(World world, CharacterId c, LeaderboardData.Kind kind = LeaderboardData.Kind.Player) => L(world).GetRating(c, kind);
    /// <summary>GetRating on the player board (the delegate shape the Arena window and the debug menu take).</summary>
    internal float GetPlayerRating(World world, CharacterId c) => GetRating(world, c);
    // C++: LeaderboardStore.cpp:441-455
    internal int GetMatchCount(World world, CharacterId c, LeaderboardData.Kind kind = LeaderboardData.Kind.Player) => L(world).GetMatchCount(c, kind);
    // C++: LeaderboardStore.cpp:457-473
    internal int GetMarks(World world, CharacterId c) => L(world).GetMarks(c);
    // C++: LeaderboardStore.cpp:475-491
    internal int GetRewardTier(World world, CharacterId c, string rewardId) => L(world).GetRewardTier(c, rewardId);

    /// <summary>The progression row of an identity token (C++ <c>g_ledger.Find(id)</c>, ArenaLedger.h:40-45: null while not Ready); the probe.</summary>
    internal LeaderboardData.Progression? FindProgression(string id) => logic.State.Ledger.Find(id);

    /// <summary>C++: LeaderboardStore.cpp:615-667, with the town board's search around <paramref name="source"/> (null = no building).</summary>
    internal CharacterId? FindRatedCharacter(World world, LeaderboardData.Kind kind, string id, ArenaBuilding? source) =>
        L(world).FindRatedCharacter(kind, id, TownSearch(world, source), out CharacterId c) ? c : null;

    /// <summary>FindRatedCharacter for many ids (one window refresh): <see cref="LeaderboardStoreLogic{TId}.NewRatedCharacterIndex"/>. The index
    /// reads <paramref name="world"/> (captured) when asked: use it only in the serial step that made it.</summary>
    internal RatedCharacterIndex<CharacterId> NewRatedCharacterIndex(World world, ArenaBuilding? source) =>
        L(world).NewRatedCharacterIndex(TownSearch(world, source), () => host.World = world);

    // ---------- the debug setters (LeaderboardStore.cpp:369-395, :506-532) ----------
    internal bool SetRating(World world, CharacterId c, float rating) => L(world).SetRating(c, rating);
    internal bool SetMarks(World world, CharacterId c, int marks) => L(world).SetMarks(c, marks);

    // ---------- the purchases (LeaderboardStore.cpp:493-613; callers tasks 5.2 / 5.3 / M6) ----------
    internal bool BeginRewardTierPurchase(World world, CharacterId c, int cost, string rewardId, int expectedPreviousTier, int nextTier) =>
        L(world).BeginRewardTierPurchase(c, cost, rewardId, expectedPreviousTier, nextTier);
    internal void RollbackRewardTierPurchase(World world, CharacterId c, int cost, string rewardId, int previousTier) =>
        L(world).RollbackRewardTierPurchase(c, cost, rewardId, previousTier);
    internal bool HasFactionUnlock(string unlockId) => logic.HasFactionUnlock(unlockId);
    internal bool AddFactionUnlock(string unlockId) => logic.AddFactionUnlock(unlockId);
    internal void RemoveFactionUnlock(string unlockId) => logic.RemoveFactionUnlock(unlockId);
    internal bool BeginFactionUnlockPurchase(World world, CharacterId c, int cost, string unlockId) => L(world).BeginFactionUnlockPurchase(c, cost, unlockId);
    internal void RollbackFactionUnlockPurchase(World world, CharacterId c, int cost, string unlockId) => L(world).RollbackFactionUnlockPurchase(c, cost, unlockId);
    internal bool ReserveProgression(World world, CharacterId c, ArenaLedger.Purchase purchase, RewardTransaction.ReservedAccounting accounting) =>
        L(world).ReserveProgression(c, purchase, accounting);
    internal bool SpendMarks(World world, CharacterId c, int cost) => L(world).SpendMarks(c, cost);
    internal void RefundMarks(World world, CharacterId c, int amount) => L(world).RefundMarks(c, amount);
}
