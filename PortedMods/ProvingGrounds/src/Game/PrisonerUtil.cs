using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/PrisonerUtil.h/.cpp (Proving Grounds by Shutori, GPLv3), PA §2.19, CAP 15. The C++ decisions are
// Core/Glue/PrisonerUtilLogic.cs + PrisonerUtilLogic.Discovery.cs (fake-tested, Tests/Glue/PrisonerUtilLogicTests.cs) over IPrisonerHost; this
// partial class binds them to a world and to CharacterIds. Its parts, by the plan's groups (M6-prisoners.md task 6.1):
//   PrisonerUtil.cs ............ the members every caller uses, the state reads, AbandonWorldState / ClearMatchState
//   PrisonerUtil.Discovery.cs .. task 6.1: the host (cages, owners, held characters through src/Engine/Cages.cs), the roster, FindCageForOccupant,
//                                CountHandlerCandidates, PrepareMatch and the match reads
//   PrisonerUtil.Release.cs .... task 6.2: the handler walk to the cage, the release (Fixtures.Release, the release line), the pair to the arena
//   PrisonerUtil.Protection.cs . task 6.2: the handlers' park, the protection of a released prisoner, the cadenced upkeep
//   PrisonerUtil.Return.cs ..... task 6.3: ringside aid, the return to the cages (walk, treat, carry, Fixtures.Imprison) and the lock check
// The state (C++ g_rosterPrisoners, g_matchEntries, g_matchHandlers, g_returning, ... PrisonerUtil.cpp:86-104) is the logic's one
// PrisonerUtilState per world: this object is built by ProvingGroundsSystem, which is built again for every world.
//
// Engine calls of the C++ in the ported ranges (E1) and what replaces them:
//   Building / UseableStuff reads (getSpecialFunction, getUseableStuff, findAllFurnitureWithFunction, getOccupant, isThePlayer, getPosition,
//   isValid) ... src/Engine/Cages.cs (its own E1 list), through PrisonerUtil.Discovery.cs's host; Ownerships (PrisonerUtilPlatoon.cpp) ...
//   src/Game/PrisonerUtilPlatoon.cs (PrisonerUtilInternal, declared in src/Game/PrisonerUtilInternal.cs)
//   ou->getObjectsWithinSphere(origin, 2500, BUILDING, 512) ... PgEngine.BuildingsNear(world, data, origin, 2500, null, 512)
//   ou->getCharactersWithinSphere(origin, 2500, 0, 2500, 512, 512) ... PgEngine.CharactersNear(world, origin, 2500, 512) (the C++'s far / near
//       caps are 512 each and its near radius 0, so at most 512 come back: docs/divergences.md "PrisonerUtil")
//   character isValid / isDead / getPosition / inSomething / inWhat ... PgEngine.Exists / IsDead / Position, Cages.IsImprisoned / HeldIn
//   ArenaIngress::GetBoundRegistry ... ArenaIngress.GetBoundRegistry; registry->isValid() ... ArenaIdentity.Resolve of its ref
//   FightStarter::DisengageMatch (ClearMatchState) ... FightStarter.DisengageMatch
//   PGLog::Debug ... PGLog.Debug (the C++ texts byte for byte)
//   task 6.2 (PrisonerUtil.cpp:443-537, 746-800, 1110-1405): GetTickCount ... PgClock.NowMs; setStandingOrder HOLD/PASSIVE off (IssueMove,
//       ClearParkOrders) ... PgEngine.SetMoveStance; movement->setDestination + addOrder(MOVE_CUS_ORDERED) + setDestination ... PgEngine.MoveTo
//       (one MoveOrder); setDesiredSpeedOrders(RUN) + setDesiredSpeed(RUN) ... PgEngine.SetRunSpeed (SpeedMode.Free); ClearHandlerOrderQueue's
//       addOrder + removeJob ... PgEngine.ClearOrders; addOrder(RELEASE_PRISONER) ... not issued (divergences.md); isChainedMode ...
//       PgEngine.IsChained; setPrisonMode(false, NULL) ... Cages.Release (Fixtures.Release, releaser -1); setChainedMode(false) ... nothing
//       (divergences.md); say ... PgEngine.Bark; rand() ... Core Hashing.CRand over one process-wide state; FightStarter::RememberMatchFighters
//       ... FightStarter.RememberMatchFighters; ApplyParkOrders (clearAllAIGoals, PASSIVE, HOLD, AGG off, reThink) ...
//       PgIngressHost.ParkPlayerInPlace (EndWalk + SetBenchStance, M4's player hold); ReinforceParkOrders ... PgEngine.SetBenchStance;
//       SparSession::IsActive / IsSparringOpponent ... SparSession; getAllAttackers ... PgEngine.AttackersOf (CombatSystem.TargetOf);
//       getAttackTarget ... PgEngine.AttackTarget; clearTempEnemyStatus, rememberCharacter(ST_TEMPORARY_ALLY) ... nothing (Meitou keeps no
//       temporary enemy / ally memory; the isolation is CombatHooks' BestAttackerOf / FindFoe postfixes over the published view, task 3.2b);
//       removeJob(FOCUSED_MELEE_ATTACK / UNPROVOKED_...) ... PgEngine.EndAttack; endCombatMode ... PgEngine.EndCombat
//   task 6.3 (PrisonerUtil.cpp:539-744, 768-784, 1311-1368, 1407-1774): isUnconcious ... PgEngine.IsUnconscious; getName ... PgEngine.Name;
//       SparSession::IsEliminated ... SparSession; ArenaMedical::ShouldStabilizePrisoner(GetProtocol()) / NeedsStabilization /
//       IssueStabilizeOrder ... ArenaMedical (M4, its treat path: TaskStart.Start Treat, first aid or robot kit); showPlayerAMessage ...
//       PgEngine.Message (world.Post); clearAllAIGoals + reThinkCurrentAIAction ... PgEngine.EndTask; isCarryingSomething / isBeingCarried
//       ... PgEngine.IsCarrying / IsCarried; dropCarriedObject ... PgEngine.LetGo (CarrySystem.LetGo); addOrder(LIFT_PERSON_PLAYER_ORDER) ...
//       PgEngine.BeginLift (CarrySystem.BeginPickUp; CombatMatchView.IsOwnCarrier lets the handler past the 3.2b refusal);
//       setPrisonMode(true, cage) ... Cages.PutInCage (LetGo if carried, Fixtures.Imprison, which locks the cage, spike S11);
//       setChainedMode(true) ... nothing; destroyed / isBroken / getDoorLock / AI::isDoorLocked ... Cages.IsDestroyed / IsBroken / HasLock /
//       IsLocked (LockState.EffectivelyLocked); checkPlayerOrderForProblems(LOCK_DOOR) / addOrder(LOCK_DOOR) ... not given (Imprison locks;
//       divergences.md "PrisonerUtil (task 6.3")
//
// Callers (C++ -> port): ArenaUI (PgArenaUIHost), ArenaIngress (PgIngressHost, an 11-line pass-through kept as M6's brief decided), SparSession
// (PgSparHost: BeginReturnToCages, 6.3), WorldLifecycle (save safety), CombatHooks (CopyMatchPrisoners: the published isolation view),
// LeaderboardStore (roster / match prisoners), DebugMenu (DebugMenuSim), PrisonerRecruitment (ForgetRosterPrisoner, 6.4).

/// <summary>The PrisonerUtil of one world: the logic and its state, bound to a world for each call (sim thread, serial steps only).</summary>
sealed partial class PrisonerUtil
{
    readonly ArenaIngress ingress;
    readonly FightStarter fightStarter;
    readonly PopulationData data;
    readonly PgClock clock;
    readonly Func<SparSession> spar;
    readonly Action<World, CharacterId, string>? said;
    readonly Host host;
    readonly PrisonerUtilLogic<CharacterId> logic = new();

    /// <summary><paramref name="spar"/> is read lazily (SparSession is built after this object); <paramref name="said"/>: a TEST ONLY observer of
    /// the release line (a PG-PRISONER line while the test verbs are armed), may be null.</summary>
    internal PrisonerUtil(ArenaIngress ingress, FightStarter fightStarter, PopulationData data, PgClock clock, Func<SparSession> spar,
        Action<World, CharacterId, string>? said = null)
    {
        this.ingress = ingress;
        this.fightStarter = fightStarter;
        this.data = data;
        this.clock = clock;
        this.spar = spar;
        this.said = said;
        host = new Host(this);
    }

    /// <summary>The C++ file-level state (read by the test verb pg prisoners).</summary>
    internal PrisonerUtilState<CharacterId> State => logic.State;

    IPrisonerHost<CharacterId> H(World world) => host.Bind(world);

    // ---------- the frame loop (ArenaUI.cpp ForgottenGUI_update_hook :3131-3186) ----------

    // C++: PrisonerUtil.cpp:1382-1405, called from ArenaUI.cpp ForgottenGUI_update_hook (:3156); PrisonerUtil.Protection.cs
    internal void TickMatchUpkeep(World world) => logic.TickMatchUpkeep(H(world));

    // TickReturn (:3157) and TickRingsideAid (:3167) are PrisonerUtil.Return.cs (task 6.3).

    // ---------- runtime state (save safety, the window, the debug menu) ----------

    // C++: PrisonerUtil.cpp:831-834
    internal bool HasRuntimeActivity(World world) => logic.HasRuntimeActivity();
    // C++: PrisonerUtil.cpp:1732-1737
    internal bool HasPendingCageLocks(World world) => logic.HasPendingCageLocks();
    // C++: PrisonerUtil.cpp:1739
    internal bool HasCageReturnFailure(World world) => logic.HasCageReturnFailure();
    // C++: PrisonerUtil.cpp:1740
    internal string GetCageReturnStatus(World world) => logic.GetCageReturnStatus();
    // C++: PrisonerUtil.cpp:1727-1730
    internal bool IsReturning(World world) => logic.IsReturning();
    // C++: PrisonerUtil.cpp:836-853
    internal void AbandonWorldState(World world) => logic.AbandonWorldState();
    // C++: PrisonerUtil.cpp:1776-1804
    internal void ClearMatchState(World world) => logic.ClearMatchState(H(world));

    // The return (BeginReturnToCages, ReturnAllToCages, TickReturn, TickPendingCageLocks) and ringside aid are PrisonerUtil.Return.cs (task 6.3).
    // The release (BeginHandlerUnlocks, TickHandlerUnlocks, ConsumeNewlyReleased, SendPairToArena) is PrisonerUtil.Release.cs; the handlers'
    // park (ParkMatchHandlers, ReinforceParkedHandlers) and the protection are PrisonerUtil.Protection.cs (task 6.2).
}
