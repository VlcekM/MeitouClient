using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Task 7.3 (NPC-vs-NPC bouts, TW §2.2 steps 1-13; TownArena.cpp:136-150, 433-465, 591-697, 1074-1097, 1138-1145, 1464-1779, 1890-2066): the
// engine half of the bout. The decisions are Core/Glue/TownArenaLogic.Bouts.cs (fake-tested); this file is the host's task-7.3 members, the borrow
// list (spike S10) and the entry points SparSession, ArenaIngress, the save and the debug menu call. Meitou 0.17.3 (fa34a1b).
//
// Engine calls of the C++ in task 7.3's ranges (E1) and what replaces them:
//   ou->getTimeStamp_inGameHours().getTotalHours() ... World.GameHours (World.cs:436)
//   LeaderboardStore::GetTownChallenges() ... LeaderboardStore.GetTownChallenges (the held card while Ready)
//   Building::getInstanceID()->uid (the saved bout's registry) ... ArenaIdentity.KeyText(ref): the building's stable key (a town building's
//       TownBuilding.Key, zone + level instance id, fixed by the level data; a player building's site id, saved by Meitou), the port's instance id
//   Character::getInstanceID()->uid (LegacyOpponentId, only when the fighter has no persistent identity) ... CharacterIdText.Format (session-only:
//       Meitou has no per-character instance uid; docs/divergences.md "TownArena (task 7.3)")
//   ou->showPlayerAMessage(text, true) ... PgEngine.Message
//   ou->getObjectsWithinSphere(..., CHARACTER, 512, NULL) (BeginEvent :1523, :1536) ... the roster host's CharactersInSphere (task 7.2)
//   Building::getBuildState()->isComplete, getPosition, getTown ... ArenaBuilding.Finished / Position, PlacedBuilding.Town (ArenaIdentity, task 4.1)
//   ArenaIngress::Get/SetLocationMode, Begin(mode, pair, teams, n, NULL, 0, arena), GetStatus ... ArenaIngress (task 4.3b; the arena as the
//       site override, resolved again by the ingress)
//   SparSession::GetMode / IsEliminated ... SparSession
//   ArenaRingGuard::SetArena(arena) ... ArenaIngress.SetRingSite (the staging's own registration, task 4.3b)
//   Character::clearAllAIGoals (ArenaIngress.cpp:437 for a town fighter) and the STAND_STILL goal of ParkCharacterInPlace (:608-614) ... the borrow
//       list below: the world's hold registry with the owner TownFighter (task 8.4a, src/Engine/Holds.cs: Engaged = true + Ai.Walking = false +
//       TaskStart.Stand once, spike S10), re-asserted every SlowWorld (TownArena.Tick), let go when the town event ends (Finish; the NPC returns
//       to its town AI only when no other owner, e.g. the aftercare, holds it) or forgotten when the world is left (AbandonWorldState);
//       setDesiredSpeed(RUN) ... PgEngine.SetRunSpeed (PgIngressHost)
//   TownBookie::BeginNpcBout / PreparationValid / HasPendingWager / NpcAssembling / CombatStarted / Capture / RestorePlannedNpcBout ...
//       src/Town/TownBookie.cs (task 8.1a: the market, the wager and the ticket)
//   TownAftercare::ControlAvailable / Begin / Standby / MarkWinner / Treat / GetStatus ... src/Town/TownAftercare.cs (task 8.4a; Treat task 8.4b:
//       the treatment and transport, so a town bout ends in Finish after its aftercare, up to 300 s after the fight)
//   TownSpectators::Begin / ReactToResult ... src/Town/TownSpectators.cs (task 8.2)
//   TownCorpseCleanup::Begin / Seal / AdmitArenaDeaths / HasPending / GetStatus ... src/Town/TownCorpseCleanup.cs (task 8.6)
//   TownAnnouncer::Begin ... src/Town/TownAnnouncer.cs (task 8.3); TownLimbShop::Begin ... src/Town/TownLimbShop.cs (task 8.5)
//   TownDiagnosticRun::NextRequest / Assign / CompletePending / AbortPending / RetryPending ... src/Town/TownDiagnosticRun.cs
//       (TownArena.Diagnostic, task 8.7a)

sealed partial class TownArena
{
    // ---------- the borrow list (spike S10, overview §9): the bout fighter's owner in the world's hold registry (task 8.4a) ----------

    /// <summary>True when TownArena holds <paramref name="c"/> as a bout fighter (ArenaIngress's park leaves such an NPC to TownArena).</summary>
    internal bool HoldsNpc(CharacterId c) => holds.IsHeldBy(c, HoldOwner.TownFighter);

    /// <summary>The town fighter's AI is taken off its town package (the C++ <c>clearAllAIGoals</c>, ArenaIngress.cpp:437): held in the world's
    /// hold registry with the owner <see cref="HoldOwner.TownFighter"/> (spike S10's recipe the first time anyone holds it, a re-assert after).
    /// A player character is never held (the AI never runs one, AiSystem.cs:366). Serial step.</summary>
    internal void BorrowFighter(World world, CharacterId c) => holds.Hold(world, c, HoldOwner.TownFighter);

    /// <summary>The bout fighters let go (the registry's <see cref="Holds.ReleaseAll"/>): each NPC returns to its town AI (Engaged = false,
    /// Ai.Replan = true) only when no other owner holds it, so a fighter the aftercare holds as a patient stays held (task 8.4a closes task 7.3's
    /// TO-DO here).</summary>
    void ReleaseHolds(World world) => holds.ReleaseAll(world, HoldOwner.TownFighter);

    // ---------- the save (ArenaNativePersistence.cpp:205, :362) ----------

    /// <summary>C++: TownArena.cpp:593-607 (ArenaNativePersistence.cpp:205): the announced bout into the save's snapshot; false = the arena part of
    /// the save is not written. <paramref name="world"/> = the world of the last serial step (null before the first: no bout can be planned then).</summary>
    internal bool CapturePlannedNpcBout(World? world, ArenaPersistence.PlannedNpcBout output) =>
        world is null ? CaptureWithoutWorld(output) : logic.CapturePlannedNpcBout(H(world), output);

    bool CaptureWithoutWorld(ArenaPersistence.PlannedNpcBout output)
    {
        output.AssignFrom(new ArenaPersistence.PlannedNpcBout());
        return !logic.State.PlannedNpc.Valid;
    }

    /// <summary>C++: TownArena.cpp:608-619 (ArenaNativePersistence.cpp:362, a loaded snapshot): applied at the world's first serial step with the
    /// snapshot, before anything ticks.</summary>
    internal void RestorePlannedNpcBout(World world, ArenaPersistence.PlannedNpcBout saved) => logic.RestorePlannedNpcBout(H(world), saved);

    sealed partial class Host
    {
        // ---------- task 7.3 ----------
        public CharacterId None => CharacterId.None;
        public double TotalHours => W.GameHours;
        public TownChallengePolicy.Card TownChallenges() => owner.store().GetTownChallenges();
        public string BuildingUid(ArenaBuildingRef building) => ArenaIdentity.KeyText(building);
        public string CharacterUid(CharacterId c) => c.IsNone ? "" : CharacterIdText.Format(c);
        public void ShowPlayerMessage(string text) => PgEngine.Message(W, text);
        public ArenaLocationMode IngressGetLocationMode() => owner.ingress.GetLocationMode();
        public void IngressSetLocationMode(ArenaLocationMode mode) => owner.ingress.SetLocationMode(mode);
        public bool IngressBegin(MatchRules.MatchMode mode, CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count, ArenaBuildingRef arena) =>
            owner.ingress.Begin(W, mode, fighters, teams, count, null, 0, arena);
        public string IngressGetStatus() => owner.ingress.GetStatus();
        public MatchRules.MatchMode SparMode() => owner.spar().GetMode();
        public bool SparIsEliminated(CharacterId c) => owner.spar().IsEliminated(W, c);
        public void RingGuardSetArena(ArenaBuildingRef arena) => owner.ingress.SetRingSite(W, arena);
        public void ReleaseTownHolds() => owner.ReleaseHolds(W);
        public void TownRulesBoutStarted()
        {
            var st = owner.logic.State;
            owner.rules.BoutStarted(W, st.Fighters.AsSpan(0, st.FighterCount), st.FighterTeams.AsSpan(0, st.FighterCount));
        }

        // ---- the bookie (task 8.1a, src/Town/TownBookie.cs) ----
        public void BookieBeginNpcBout(CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count, double scoreA, double scoreB) =>
            owner.bookie.BeginNpcBout(W, fighters, teams, count, scoreA, scoreB);
        public bool BookiePreparationValid() => owner.bookie.PreparationValid(W);
        public bool BookieHasPendingWager() => owner.bookie.HasPendingWager(W);
        public void BookieNpcAssembling() => owner.bookie.NpcAssembling(W);
        public void BookieCombatStarted() => owner.bookie.CombatStarted(W);
        public void BookieCapturePlannedNpcBout(ArenaPersistence.PlannedNpcBout output) => owner.bookie.CapturePlannedNpcBout(world, output);
        public bool BookieRestorePlannedNpcBout(ArenaPersistence.PlannedNpcBout saved) => owner.bookie.RestorePlannedNpcBout(W, saved);
        public void AnnouncerBegin(ArenaBuildingRef arena, bool playerMatch, int challenge, string[] roles, int roleCount, CharacterId[] fighters,
            int fighterCount) => owner.announcer.Begin(W, arena, playerMatch, challenge, roles, roleCount, fighters, fighterCount);
        // C++ TownSpectators::Begin (task 8.2). Port only: a throw is logged once, the guests already invited are released (the backstop as Release)
        public void SpectatorsBegin(ArenaBuildingRef arena)
        {
            try { owner.spectators.Begin(W, arena); }
            catch (Exception e) { owner.LogSpectatorFailure(StepSpectatorBegin, e); SpectatorsRelease(); }
        }
        // C++ TownSpectators::ReactToResult (task 8.2). Port only: a throw is logged once (Release follows it in OnMatchResult)
        public void SpectatorsReactToResult(SparPodium.Snapshot result)
        {
            try { owner.spectators.ReactToResult(W, result); }
            catch (Exception e) { owner.LogSpectatorFailure(StepSpectatorReact, e); }
        }
        // C++ TownCorpseCleanup::Begin (task 8.6), at staging. Port only: a throw is logged once and the cleanup released (the backstop as Release)
        public void CorpseCleanupBegin(ArenaBuildingRef arena, CharacterId[] fighters, int count)
        {
            try { owner.corpseCleanup.Begin(W, arena, fighters, count); }
            catch (Exception e) { owner.LogCorpseFailure(StepCorpseBegin, e); CorpseCleanupRelease(); }
        }
        public void CorpseCleanupSeal() => owner.corpseCleanup.Seal();                      // C++ TownCorpseCleanup::Seal (task 8.6)
        // C++ TownCorpseCleanup::AdmitArenaDeaths (task 8.6), after the medics finish. Port only: a throw is logged once
        public void CorpseCleanupAdmitArenaDeaths()
        {
            try { owner.corpseCleanup.AdmitArenaDeaths(W); }
            catch (Exception e) { owner.LogCorpseFailure(StepCorpseAdmit, e); }
        }
        // C++ TownCorpseCleanup::HasPending (task 8.6): the bout's Finish waits while it is true. Port only: a throw is logged once and answers
        // false (the bout finishes and Release gives the handler back)
        public bool CorpseCleanupHasPending()
        {
            try { return owner.corpseCleanup.HasPending(W); }
            catch (Exception e) { owner.LogCorpseFailure(StepCorpsePending, e); return false; }
        }
        public string CorpseCleanupGetStatus() => owner.corpseCleanup.GetStatus();          // C++ TownCorpseCleanup::GetStatus (task 8.6)
        public bool AftercareControlAvailable() => owner.aftercare.ControlAvailable();      // C++ TownAftercare::ControlAvailable (task 8.4a)
        public void AftercareBegin(List<CharacterId> doctors, CharacterId[] fighters, int count, ArenaBuildingRef arena,
            System.Numerics.Vector3 standby) =>
            owner.aftercare.Begin(W, doctors, new ArraySegment<CharacterId>(fighters, 0, count), arena, standby);   // C++ TownAftercare::Begin
        public void AftercareStandby() => owner.aftercare.Standby(W);                        // C++ TownAftercare::Standby
        public void AftercareMarkWinner(CharacterId c) => owner.aftercare.MarkWinner(W, c);  // C++ TownAftercare::MarkWinner
        public bool AftercareTreat(float elapsed, bool eliminatedOnly) =>
            owner.aftercare.Treat(W, elapsed, eliminatedOnly);                               // C++ TownAftercare::Treat (task 8.4b)
        public string AftercareGetStatus() => owner.aftercare.GetStatus();                  // C++ TownAftercare::GetStatus
        public void LimbShopBegin(ArenaBuildingRef? registry, CharacterId[] participants, int count) => owner.limbShop.Begin(W, registry, participants, count);
        public TownMatchmakingPolicy.DiagnosticRequest DiagnosticNextRequest() => owner.Diagnostic.NextRequest();             // C++ TownDiagnosticRun::NextRequest (task 8.7a)
        public void DiagnosticAssign(TownMatchmakingPolicy.Match match, double hours) => owner.Diagnostic.Assign(match, hours);   // C++ TownDiagnosticRun::Assign (task 8.7a)
        public void DiagnosticCompletePending(SparPodium.Snapshot result, double hours) => owner.Diagnostic.CompletePending(result, hours);   // C++ TownDiagnosticRun::CompletePending (task 8.7a)
        public void DiagnosticAbortPending() => owner.Diagnostic.AbortPending();             // C++ TownDiagnosticRun::AbortPending (task 8.7a)
        public void DiagnosticRetryPending() => owner.Diagnostic.RetryPending();             // C++ TownDiagnosticRun::RetryPending (task 8.7a)
    }

    // ---------- TEST ONLY (pg town start / the bout part of every pg town line) ----------

    /// <summary>TEST ONLY <c>pg town start</c>: plans a bout (BeginTrial) when none is announced, then makes the announced bout due now (its start
    /// time = the game hours now, the schedule clock reset) so the next tick's schedule check begins it, as it would after the 1-2 game-hour lead
    /// (TW §2.2 step 6). Null when a bout is announced and due, else why not. Port only: the original has no such shortcut.</summary>
    string? StartForTest(World world)
    {
        var h = H(world);
        if (logic.IsBusy()) return "a town event is already running";
        if (!logic.State.PlannedNpc.Valid && !logic.BeginTrial(h)) return "no bout could be planned: " + logic.GetStatus();
        if (logic.State.RestoredNpcPending) return "the announced bout is a restored one still waiting for its fighters";
        logic.State.NextNpcHours = world.GameHours;
        logic.State.LastScheduleCheck = 0;
        return null;
    }

    /// <summary>TEST ONLY <c>pg town sit [id]</c> (task 8.2, spike S16): one NPC held and sent to the nearest free Arena Pillow seat of the bound Town
    /// Registry's town (src/Town/TownSpectators.cs SitForTest). Null on success, else why not.</summary>
    string? SitForTest(World world, IReadOnlyList<string> args)
    {
        var h = H(world);
        if (logic.State.TownRegistry is not { } tr || h.Resolve(tr) is not { } reg || reg.Town == TownKey.None) return "no Town Registry bound";
        return spectators.SitForTest(world, reg.Building.Position, reg.Town, args);
    }

    /// <summary>TEST ONLY: characters outside the bout that targeted a bout fighter while the event was Preparing or Fighting, since the event
    /// began (each SlowWorld; nobody joins a town bout, TW §2.13). The intruder scan runs only while the test verbs are armed; the
    /// <see cref="lastBout"/> copy below runs every tick, armed or not (a few list writes, read only by pg town).</summary>
    readonly List<CharacterId> intruders = [];
    TownPhase lastIntruderPhase;
    /// <summary>TEST ONLY: the fighters and teams of the latest event that had any (kept after Finish clears the state), for the scenario's
    /// after-bout reads.</summary>
    readonly List<(CharacterId Id, MatchRules.MatchTeam Team)> lastBout = [];

    void WatchIntrudersForTest(World world)
    {
        var st = logic.State;
        if (st.Phase == TownPhase.Preparing && lastIntruderPhase == TownPhase.Idle) intruders.Clear();
        lastIntruderPhase = st.Phase;
        if (st.FighterCount > 0)
        {
            lastBout.Clear();
            for (int i = 0; i < st.FighterCount; ++i) lastBout.Add((st.Fighters[i], st.FighterTeams[i]));
        }
        if (!Spikes.SpikePatches.Armed || (st.Phase != TownPhase.Preparing && st.Phase != TownPhase.Fighting) || st.FighterCount == 0) return;
        if (st.ActiveRegistry is not { } r || ArenaIdentity.Resolve(world, data, r, out _) is not { } registry) return;
        var combat = PgEngine.FindCombat(world);
        foreach (var c in PgEngine.CharactersNear(world, registry.Position, TownArenaLogic<CharacterId>.BoutSearchRadius + 600))
        {
            if (logic.IsFighter(H(world), c) || intruders.Contains(c)) continue;
            CharacterId target = PgEngine.AttackTarget(world, combat, c);
            if (!target.IsNone && logic.IsFighter(H(world), target)) intruders.Add(c);
        }
    }

    /// <summary>The bout part of every pg town line.</summary>
    string BoutJson(World world)
    {
        var st = logic.State;
        static string N(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static string Q(string t) => PGLog.Quote(t);
        string Id(CharacterId c) => Q(CharacterIdText.Format(c));
        string Row(CharacterId c)
        {
            string key = store().CharacterKey(world, c, false);
            if (key.Length == 0) return "null";
            foreach (var r in store().Data.TownStandings)
                if (r.Id == key)
                    return "{\"mmr\":" + TestJson.G(r.Mmr) + ",\"wins\":" + N(r.Wins) + ",\"losses\":" + N(r.Losses) + ",\"matches\":" + N(r.Matches) + "}";
            return "null";
        }
        var sb = new System.Text.StringBuilder();
        sb.Append(",\"gameHours\":").Append(TestJson.G(world.GameHours))
          .Append(",\"nextNpcHours\":").Append(TestJson.G(st.NextNpcHours))
          .Append(",\"restoredPending\":").Append(st.RestoredNpcPending ? "true" : "false")
          .Append(",\"plannedA\":[").Append(string.Join(",", st.PlannedNpc.A.Select(Q))).Append(']')
          .Append(",\"plannedB\":[").Append(string.Join(",", st.PlannedNpc.B.Select(Q))).Append(']')
          .Append(",\"plannedFighters\":[").Append(string.Join(",", st.PlannedFighters.Select(Id))).Append(']')
          .Append(",\"fighters\":[");
        for (int i = 0; i < st.FighterCount; ++i)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(Id(st.Fighters[i])).Append(",\"team\":").Append(Q(st.FighterTeams[i].ToString()))
              .Append(",\"town\":").Append(Row(st.Fighters[i])).Append('}');
        }
        sb.Append("],\"plannedRows\":[").Append(string.Join(",", st.PlannedFighters.Select(Row))).Append(']')
          .Append(",\"lastBout\":[").Append(string.Join(",", lastBout.Select(b => "{\"id\":" + Id(b.Id) + ",\"team\":" + Q(b.Team.ToString())
              + ",\"town\":" + Row(b.Id) + "}"))).Append(']')
          .Append(",\"holds\":[").Append(string.Join(",", TownFighterHolds().Select(Id))).Append(']')
          .Append(",\"intruders\":[").Append(string.Join(",", intruders.Select(Id))).Append(']')
          .Append(",\"townStandings\":").Append(N(store().Data.TownStandings.Count))
          .Append(",\"ambientHistory\":").Append(N(store().GetTownChallenges().AmbientHistory.Count))
          .Append(",\"ingress\":").Append(Q(ingress.GetStatus()))
          .Append(",\"lastCancel\":").Append(Q(bookie.LastCancelReason));
        return sb.ToString();
    }

    /// <summary>The NPCs the registry holds for the bout (owner <see cref="HoldOwner.TownFighter"/>), in hold order (the test verb's "holds").</summary>
    List<CharacterId> TownFighterHolds()
    {
        var list = new List<CharacterId>();
        var registry = holds.Registry;
        for (int i = 0; i < registry.Count; i++)
        {
            var (id, owners) = registry.At(i);
            if ((owners & HoldOwner.TownFighter) != 0) list.Add(id);
        }
        return list;
    }
}
