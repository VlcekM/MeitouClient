using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/FightStarter.h/.cpp (Proving Grounds by Shutori, GPLv3), task 3.1 (PA §2.7, CAP 1, spike S1).
// This file is the public surface (FightStarter.h); every C++ function, in order with its line range, is in Core/Glue/FightStarterLogic.cs,
// which runs over IFightWorld so its decisions are tested with a fake (E3, Tests/Glue/FightStarterLogicTests.cs). src/Game/PgFightWorld.cs
// is that world over PgEngine. The pure target choice is Core/FightTargeting.cs (golden vectors from the C++); the two lists, the
// hit-reaction queue and KOWatcher's keep-alive clock are Core/Glue/FightStarterState.cs. Meitou 0.17.3 (tag v0.17.3, fa34a1b). Every method
// runs on the sim thread in a serial step (Inputs, SlowWorld); the orders it gives are deferred to the next tick (World.Defer).
//
// Engine calls of the C++ and what replaces them (E1; IFightWorld member -> adapter method in src/Engine/PgEngine.cs, bound in PgFightWorld):
//   ou->player->getAllPlayerCharacters() membership (IsPlayerCharacter, :56-68) ... PgEngine.IsPlayer(id) (CharacterCold.IsPlayer)
//   c->isValid(), isDead(), isUnconcious() (IsLivingFighter :132-135, MakeParticipant :176-184) ... PgEngine.Exists / IsDead / IsUnconscious
//   c->getPosition() (FindNearestLivingOpponent :223, :232) ... PgEngine.Position(id)
//   c->getStandingOrder(PASSIVE/HOLD/DEF/AGG), getMovementSpeedOrders() (:123-127) ... PgEngine.Orders(id) (toggles, hold point, speed)
//   SetJobsEnabled(c, false/true): selection + OrdersPanel::update + MainBarGUI::pressedKey(26) (:70-104) ... PgEngine.SetJobsOff(id) /
//       SetJobsOn(id): the per-character Jobs toggle set directly (divergences.md)
//   c->setStandingOrder(PASSIVE/HOLD/DEF false, AGG true) (SetCombatStance :285-288) ... PgEngine.SetCombatStance(id) (Passive, Hold,
//       Block off; AGG has no counterpart)
//   self->rememberCharacter(other, ST_TEMPORARY_ENEMY) (MarkTempEnemy :291-297) ... none: fighting pairs are tagged Enemy to each other by
//       the senses (AggroSystem.cs:1559-1590, CAP 1); kept as an empty step (divergences.md)
//   addOrder(NULL, FOCUSED_MELEE_ATTACK, target, false, clear, pos) (player, :310-316); clearAllAIGoals() + addGoal(UNPROVOKED_FOCUSED_
//       MELEE_ATTACK, target) + reThinkCurrentAIAction() (NPC, :324-327, :338) ... PgEngine.Attack(id, target, player, queued): AttackOrder
//       { Picked = true } for a player character, { Picked = false, Source = TaskSource.Npc } for anyone else
//   attacker->attackTarget(target), target->attackingYou(attacker, true, false) (:331-332) ... none: both sides are ordered (CAP 1: an NPC
//       does not answer a same-faction attack by itself), which every caller does
//   c->clearAllTempEnemyStatuses(ST_TEMPORARY_ENEMY) (:366, :439) ... none (no temp-enemy memory, above)
//   removeJob(FOCUSED_MELEE_ATTACK / UNPROVOKED_...), ClearPlayerAttackOrders (addOrder MOVE_CUS_ORDERED + removeJob, :344-355),
//       endCombatMode(), clearAllAIGoals(), reThinkCurrentAIAction() (DisengageFighter :370-386, ClearFighterForRetarget :444-451) ...
//       PgEngine.EndTask(id): TaskStart.End(world, slot, TaskEnd.Stopped) (CAP 1)
//   setStandingOrder(...saved), movement->setDesiredSpeedOrders(saved) + restoreDesiredSpeed() (:394-403) ... PgEngine.RestoreStance(id, saved)
//   setStandingOrder(AGG, false) for a fighter with no saved state (:407) ... none (AGG has no counterpart)
//   endCombatMode() on a dead fighter (:417-420) ... PgEngine.Stop(ids) (the StopCommand of spike S1's restore, sent for every valid fighter)
//   c->getAttackTarget().getCharacter() (:602) ... PgEngine.AttackTarget(id) (CombatSystem.TargetOf, the last tick's)
//   c->isInCombatMode(true, false) (:620) ... PgEngine.InMeleeCombatMode(id)
//   (no C++ call: Meitou-only stale-target sweep, task 3.5) ... PgEngine.AttackTarget, PgEngine.InCombat / Running (InFight), PgEngine.Stop
//   a->isEnemy(b, false) diagnostic (Engage :669-674) ... none: no line (the orders start next tick; divergences.md)
//   PGLog::Debug(...) ... PGLog.Debug, the C++ texts byte for byte
//   SparSession::IsActive / GetMode / IsEliminated / IsSparringOpponent (ReactToIncomingAttack :575-581) ... IFightSession, given by the caller
//       (read through IFightSession<CharacterId>, bound to the world below)
//   the C++'s hit hook calling ReactToIncomingAttack at once (CombatHooks.cpp:246-247) ... RecordIncomingAttack from CombatHooks' ApplyBlow
//       postfix for a hit or block between sparring opponents (task 3.2b; without that patch from the system's ICombatHitObserver for landed
//       melee hits) (records only: the commit step must not change the world, CAP 3) + ApplyIncomingAttacks in the next serial step

/// <summary>What FightStarter reads of the running match (C++: the SparSession calls of FightStarter.cpp:575-581). src/Game/SparSession.cs
/// (task 3.3) implements it. Serial steps only: the hit observer only records, ApplyIncomingAttacks reads this in the next serial step.</summary>
interface IFightSession
{
    bool IsActive { get; }
    MatchRules.MatchMode Mode { get; }
    bool IsEliminated(World world, CharacterId c);
    bool IsSparringOpponent(World world, CharacterId a, CharacterId b);
}

/// <summary>
/// The glue of the original FightStarter.cpp: makes two or more characters fight each other for real, keeps them engaged, and gives them back
/// their standing orders, speed and jobs. One object per world, owned by ProvingGroundsSystem. Public surface = FightStarter.h (callers:
/// SparSession 3.3, KOWatcher 3.3, CombatHooks 3.2, prisoners M6, town M7); each call binds the world and runs the C++ function in
/// <see cref="FightStarterLogic{TId, TOrders}"/>.
/// </summary>
sealed class FightStarter(Action<string>? ownLog = null)
{
    readonly FightStarterLogic<CharacterId, PgEngine.PgOrders> logic = new(ownLog, CharacterIdText.Format);
    readonly PgFightWorld fightWorld = new(ownLog);   // bound to the call's world, reused (no allocation per call)
    readonly SessionView sessionView = new();

    /// <summary>Hits recorded per tick before the rest are dropped (FightStarterLogic.IncomingCapacity).</summary>
    internal const int IncomingCapacity = FightStarterLogic<CharacterId, PgEngine.PgOrders>.IncomingCapacity;

    /// <summary>How many fighters have remembered orders (the busy row of the F10 status).</summary>
    internal int RememberedCount => logic.RememberedCount;

    /// <summary>Disengage steps that threw in this world (F10 row).</summary>
    internal int DisengageFailures => logic.DisengageFailureCount;

    /// <summary>Hits the ring had no room for in this world (F10 row).</summary>
    internal long DroppedHits => logic.DroppedHits;

    /// <summary>The remembered state of a fighter, for the probe and tests.</summary>
    internal bool TryGetRemembered(CharacterId c, out FightStarterLogic<CharacterId, PgEngine.PgOrders>.OrderState state) =>
        logic.TryGetRemembered(c, out state);

    PgFightWorld W(World world) => fightWorld.Bind(world);

    /// <summary>A game session read through the world of this call.</summary>
    sealed class SessionView : IFightSession<CharacterId>
    {
        IFightSession? session;
        World? world;
        internal SessionView Bind(IFightSession s, World w) { session = s; world = w; return this; }
        public bool IsActive => session!.IsActive;
        public MatchRules.MatchMode Mode => session!.Mode;
        public bool IsEliminated(CharacterId c) => session!.IsEliminated(world!, c);
        public bool IsSparringOpponent(CharacterId a, CharacterId b) => session!.IsSparringOpponent(world!, a, b);
    }

    IFightSession<CharacterId>? View(IFightSession? session, World world) => session is null ? null : sessionView.Bind(session, world);

    // ---------- namespace FightStarter (FightStarter.h), C++ line ranges in Core/Glue/FightStarterLogic.cs ----------

    /// <summary>World teardown path: forget the remembered fighters without touching them (also the safe-save cleanup, WorldLifecycle.cpp:68).</summary>
    // C++: FightStarter.cpp:458-462
    internal void AbandonWorldState(World world) => logic.AbandonWorldState();

    // C++: FightStarter.cpp:464-470
    internal void RememberMatchFighters(World world, CharacterId[] fighters, int count) => logic.RememberMatchFighters(W(world), fighters, count);

    // C++: FightStarter.cpp:472-537
    internal void EngageMatch(World world, CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count, MatchRules.MatchMode mode) =>
        logic.EngageMatch(W(world), fighters, teams, count, mode);

    // C++: FightStarter.cpp:539-548
    internal void DisengageMatch(World world, CharacterId[] fighters, int count) => logic.DisengageMatch(W(world), fighters, count);

    // C++: FightStarter.cpp:550-571
    internal void RetargetMatchAfterElimination(World world, CharacterId[] allFighters, int allCount, CharacterId[]? remainingFighters,
        MatchRules.MatchTeam[]? remainingTeams, int remainingCount, MatchRules.MatchMode mode) =>
        logic.RetargetMatchAfterElimination(W(world), allFighters, allCount, remainingFighters, remainingTeams, remainingCount, mode);

    // C++: FightStarter.cpp:573-589
    internal void ReactToIncomingAttack(World world, CharacterId defender, CharacterId attacker, IFightSession? session) =>
        logic.ReactToIncomingAttack(W(world), defender, attacker, View(session, world));

    // C++: FightStarter.cpp:591-658
    internal void KeepEngagedMatch(World world, CharacterId[] fighters, MatchRules.MatchTeam[] teams, int count, MatchRules.MatchMode mode) =>
        logic.KeepEngagedMatch(W(world), fighters, teams, count, mode);

    // C++: FightStarter.cpp:660-675
    internal void Engage(World world, CharacterId a, CharacterId b) => logic.Engage(W(world), a, b);

    // C++: FightStarter.cpp:677-681
    internal void Disengage(World world, CharacterId a, CharacterId b) => logic.Disengage(W(world), a, b);

    // C++: FightStarter.cpp:683-693
    internal void KeepEngaged(World world, CharacterId a, CharacterId b) => logic.KeepEngaged(W(world), a, b);

    // ---------- the hit path (C++: CombatHooks.cpp:246-247 calls ReactToIncomingAttack from the hit hook) ----------

    /// <summary>From CombatHooks' ApplyBlow postfix (a hit or block between sparring opponents, task 3.2b) or, without that patch, from the
    /// system's <c>ICombatHitObserver.HitResolved</c> (both serial, during the combat system's Apply, CombatSystem.Apply.cs:49, :210;
    /// CombatHit.cs:47-59): records the pair only. The observer must not change the world (CAP 3); <see cref="ApplyIncomingAttacks"/>
    /// answers it in the next serial step. No allocation (a fixed ring); a full ring drops the hit.</summary>
    internal void RecordIncomingAttack(CharacterId defender, CharacterId attacker) => logic.RecordIncomingAttack(defender, attacker);

    /// <summary>The recorded hits in order, each through <see cref="ReactToIncomingAttack"/>. Serial: the system's SlowWorld, which runs after
    /// the Apply phase that recorded them (the next serial step); the orders it gives start the tick after.</summary>
    internal void ApplyIncomingAttacks(World world, IFightSession? session) => logic.ApplyIncomingAttacks(W(world), View(session, world));

    /// <summary>Meitou-only (task 3.5): one more stop for a fighter disengaged in the last 3 s that has a target but no fight (an opponent's
    /// initiative yield re-set it; FightStarterLogic.SweepStaleTargets). Serial: the system's SlowWorld, every tick.</summary>
    internal void SweepStaleTargets(World world) => logic.SweepStaleTargets(W(world));

    /// <summary>Port only (task 4.3b): ArenaIngress's walk-in takes its fighter off the stale-target sweep before its move order
    /// (FightStarterLogic.LeaveStaleSweep).</summary>
    internal void LeaveStaleSweep(World world, CharacterId c) => logic.LeaveStaleSweep(c);
}
