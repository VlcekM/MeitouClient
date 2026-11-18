using System.Globalization;

namespace ProvingGrounds.Core.Glue;

// The decisions of upstream/ProvingGrounds/plugin/src/FightStarter.cpp (Proving Grounds by Shutori, GPLv3), task 3.1, over a small world
// interface so they run without the game (E3). src/Game/FightStarter.cs is the public surface (FightStarter.h) and binds the world through
// PgEngine (src/Game/PgFightWorld.cs, the E1 table of engine calls in FightStarter.cs's header); Tests/Glue/FightStarterLogicTests.cs runs the
// same code over a fake. Every function below is the C++ function of the same name, in the C++'s order, with its line range.

/// <summary>What FightStarter reads of and does to the game (the engine calls of FightStarter.cpp; one call per C++ engine call, see the E1
/// table in src/Game/FightStarter.cs). <typeparamref name="TOrders"/>: the remembered orders (the C++'s standing orders and speed).</summary>
public interface IFightWorld<TId, TOrders>
{
    /// <summary>The current tick (Meitou's World.Tick): an attack order and a disengage in the same tick need the late stop.</summary>
    long Tick { get; }
    /// <summary>The "no character" id (a C++ null pointer).</summary>
    TId None { get; }
    bool IsNone(TId c);
    bool Exists(TId c);
    bool IsDead(TId c);
    bool IsUnconscious(TId c);
    bool IsPlayer(TId c);
    bool TryGetPosition(TId c, out float x, out float y, out float z);
    /// <summary>The orders now (getStandingOrder + getMovementSpeedOrders); false for a stale id.</summary>
    bool TryGetOrders(TId c, out TOrders orders);
    /// <summary>Passive, Hold, Defensive off (SetCombatStance's setStandingOrder calls).</summary>
    void SetCombatStance(TId c);
    /// <summary>SetJobsEnabled(c, false): true when Jobs was on and the call turned it off.</summary>
    bool SetJobsOff(TId c);
    /// <summary>SetJobsEnabled(c, true).</summary>
    void SetJobsOn(TId c);
    /// <summary>The standing orders and speed put back as remembered.</summary>
    void RestoreStance(TId c, TOrders saved);
    /// <summary>The attack order (player: a player order; anyone else: an NPC fight), for the next tick.</summary>
    void Attack(TId attacker, TId target, bool player, bool queued);
    /// <summary>The fight, the attack jobs and the order queue ended (the disengage cleanup).</summary>
    void EndTask(TId c);
    /// <summary>A stop for these fighters, <paramref name="ticksAhead"/> ticks from now. Nothing for an empty list.</summary>
    void Stop(IReadOnlyList<TId> fighters, int ticksAhead);
    /// <summary>getAttackTarget().getCharacter(); the none id for nobody.</summary>
    TId AttackTarget(TId c);
    /// <summary>isInCombatMode(true, false).</summary>
    bool InMeleeCombatMode(TId c);
    /// <summary>Meitou-only (task 3.5, the stale-target sweep): a fight task runs (CharacterCold.InCombat, or the running task is an attack), as
    /// opposed to a target left in the combat state with no fight behind it.</summary>
    bool InFight(TId c);
}

/// <summary>What FightStarter reads of the running match (C++: the SparSession calls of FightStarter.cpp:575-581).</summary>
public interface IFightSession<TId>
{
    bool IsActive { get; }
    MatchRules.MatchMode Mode { get; }
    bool IsEliminated(TId c);
    bool IsSparringOpponent(TId a, TId b);
}

/// <summary>
/// FightStarter.cpp's state and decisions: makes two or more characters fight, keeps them engaged, gives them back their standing orders,
/// speed and jobs. One object per world. The engine is reached only through the <see cref="IFightWorld{TId, TOrders}"/> given to each call.
/// </summary>
public sealed class FightStarterLogic<TId, TOrders> where TId : IEquatable<TId>
{
    /// <summary>C++: FightStarter.cpp:33-42 <c>FighterOrderState</c> (minus the fighter, the list's key): the orders and speed remembered, and
    /// whether the plugin turned Jobs off.</summary>
    public readonly record struct OrderState(TOrders Orders, bool JobsToggledOff);

    readonly FighterList<TId, OrderState> originalStates = new();          // C++ :50 g_originalStates
    readonly FighterList<TId, TId> focusAssignments = new();               // C++ :51 g_focusAssignments
    readonly IncomingAttackQueue<TId> incoming = new(IncomingCapacity);    // the hit observer -> the next serial step (CAP 3)
    readonly AttackOrderStamps<TId> attackOrdered = new();                 // Meitou-only: who got an AttackOrder in which tick (late stop)
    readonly FighterList<TId, long> staleSweep = new();                    // Meitou-only (task 3.5): disengaged fighter -> last tick swept
    readonly Action<string>? ownLog;                                       // the port's own diagnostic lines (Meitou's log), no C++ text
    readonly Func<TId, string> describe;
    readonly Dictionary<string, int> disengageFailures = new(StringComparer.Ordinal);   // per step name
    int disengageFailureCount;
    bool droppedLogged;

    /// <summary>Meitou-only (task 3.5): how many ticks after a full disengage <see cref="SweepStaleTargets"/> watches a fighter: 3 s, far beyond
    /// the initiative yield that re-sets a target (CombatTuning.YieldSeconds 0.5 s, from the end of the yielder's block or dodge,
    /// CombatSystem.Act.cs:766-769).</summary>
    public const int StaleSweepTicks = 90;

    /// <summary>Hits recorded per tick before the rest are dropped: far above what a bout lands (a blow every ~0.5 s per fighter).</summary>
    public const int IncomingCapacity = 256;

    /// <param name="ownLog">Where the port's own lines go (logged once per world each: a fighter whose disengage threw, dropped hits).</param>
    /// <param name="describe">A fighter id as text for those lines.</param>
    public FightStarterLogic(Action<string>? ownLog = null, Func<TId, string>? describe = null)
    {
        this.ownLog = ownLog;
        this.describe = describe ?? (c => c?.ToString() ?? "none");
    }

    /// <summary>How many fighters have remembered orders (the busy row of the F10 status).</summary>
    public int RememberedCount => originalStates.Count;

    /// <summary>Hits the ring had no room for (since this world started).</summary>
    public long DroppedHits => incoming.Dropped;

    /// <summary>The remembered state of a fighter, for the probe and tests.</summary>
    public bool TryGetRemembered(TId c, out OrderState state) => originalStates.TryGet(c, out state);

    /// <summary>The fighter's assigned target (tests).</summary>
    public bool TryGetAssignedTarget(TId c, out TId target) => focusAssignments.TryGet(c, out target);

    // ---------- anonymous namespace of the C++ ----------

    // C++: FightStarter.cpp:56-68
    static bool IsPlayerCharacter(IFightWorld<TId, TOrders> w, TId c) => !w.IsNone(c) && w.IsPlayer(c);

    // C++: FightStarter.cpp:70-104 (enabled false only: the restore is SetJobsEnabled(c, true) at :414, see DisengageFighter)
    static bool SetJobsEnabledOff(IFightWorld<TId, TOrders> w, TId c)
    {
        if (w.IsNone(c) || !w.Exists(c) || !IsPlayerCharacter(w, c))
            return false;
        return w.SetJobsOff(c);
    }

    // C++: FightStarter.cpp:106-114
    int FindOriginalState(TId c) => originalStates.Find(c);

    // C++: FightStarter.cpp:116-130
    void RememberOriginalState(IFightWorld<TId, TOrders> w, TId c)
    {
        if (w.IsNone(c) || !w.Exists(c) || FindOriginalState(c) >= 0)
            return;
        if (!w.TryGetOrders(c, out TOrders orders))
            return;
        bool jobsToggledOff = SetJobsEnabledOff(w, c);
        originalStates.AddIfAbsent(c, new OrderState(orders, jobsToggledOff));
    }

    // C++: FightStarter.cpp:132-135
    static bool IsLivingFighter(IFightWorld<TId, TOrders> w, TId c) => FightTargeting.IsLivingFighter(Read(w, c));

    // C++: FightStarter.cpp:137-145
    TId FindAssignedTarget(IFightWorld<TId, TOrders> w, TId fighter) =>
        focusAssignments.TryGet(fighter, out var target) ? target : w.None;

    // C++: FightStarter.cpp:147-163
    void SetAssignedTarget(IFightWorld<TId, TOrders> w, TId fighter, TId target)
    {
        if (w.IsNone(fighter))
            return;
        focusAssignments.Set(fighter, target);
    }

    // C++: FightStarter.cpp:165-174
    void ClearAssignedTarget(TId fighter) => focusAssignments.Remove(fighter);

    /// <summary>One fighter as Core/FightTargeting reads its <c>Character*</c> (C++ :176-277 read isValid, isDead, isUnconcious, getPosition).</summary>
    static FightTargeting.Fighter Read(IFightWorld<TId, TOrders> w, TId c)
    {
        if (w.IsNone(c)) return default;
        var f = new FightTargeting.Fighter { Present = true, Valid = w.Exists(c) };
        if (!f.Valid) return f;
        f.Dead = w.IsDead(c);
        f.Unconscious = w.IsUnconscious(c);
        if (w.TryGetPosition(c, out float x, out float y, out float z)) { f.X = x; f.Y = y; f.Z = z; }
        return f;
    }

    static FightTargeting.Fighter[] Read(IFightWorld<TId, TOrders> w, TId[] fighters, int count)
    {
        var f = new FightTargeting.Fighter[count];
        for (int i = 0; i < count; ++i) f[i] = Read(w, fighters[i]);
        return f;
    }

    // C++: FightStarter.cpp:279-289
    void SetCombatStance(IFightWorld<TId, TOrders> w, TId c)
    {
        if (w.IsNone(c))
            return;
        RememberOriginalState(w, c);
        // Drop passive/hold so they will actually fight.
        w.SetCombatStance(c);
    }

    // C++: FightStarter.cpp:291-297. Meitou keeps no temporary-enemy memory a mod must write: two characters that fight are tagged Enemy to
    // each other by the senses whatever the faction (AggroSystem.cs:1559-1590, CAP 1). Kept as a step so the callers read as the C++.
    static void MarkTempEnemy(IFightWorld<TId, TOrders> w, TId self, TId other)
    {
        if (w.IsNone(self) || w.IsNone(other))
            return;
    }

    // C++: FightStarter.cpp:299-342
    void IssueAttackOrder(IFightWorld<TId, TOrders> w, TId attacker, TId target, bool clearExistingOrders)
    {
        if (w.IsNone(attacker) || w.IsNone(target))
            return;

        bool playerControlled = IsPlayerCharacter(w, attacker);
        // Player characters need an actual player order (row 5, Picked, which beats Self preservation and jobs); NPCs (released prisoners,
        // town fighters) get a fight the AI does not own (Picked false, Source Npc), which replaces their package goal at once (CAP 1).
        // attackTarget / attackingYou / reThinkCurrentAIAction have no counterpart: every caller orders both sides.
        w.Attack(attacker, target, playerControlled, queued: !clearExistingOrders);
        attackOrdered.Stamp(attacker, w.Tick);   // a disengage later in this tick must stop it once more (DisengageFighter)
        if (clearExistingOrders)
            SetAssignedTarget(w, attacker, target);
    }

    // C++: FightStarter.cpp:344-355: the order queue overwritten and the attack jobs removed; in Meitou ending the task does both
    // (TaskStart.End clears the queue's front, PlayerOrders.Ending, OrderQueue.cs:373-381).
    // The C++ guard at :346 (a player character or a town fighter only) is dropped on purpose: the one TaskStart.End is also the counterpart of
    // removeJob / endCombatMode / clearAllAIGoals / reThinkCurrentAIAction, which the C++ runs for every living fighter (:370-386), and for any
    // other NPC its queue part does nothing (an NPC has no running player order: OrderQueue.Ending returns, OrderQueue.cs:377). divergences.md
    // "The disengage cleanup".
    static void ClearPlayerAttackOrders(IFightWorld<TId, TOrders> w, TId c)
    {
        if (w.IsNone(c))
            return;
        w.EndTask(c);
    }

    // C++: FightStarter.cpp:357-428. Port-only order for exception safety: a valid fighter is put in the stop (and late stop) lists before any
    // engine write, and Jobs is turned back on in a finally, so a throw from the cleanup or the stance restore still leaves it stopped with
    // Jobs on; the throw then reaches DisengageEach (logged once) and the remembered state stays (the erase is the last step), so a later full
    // disengage can still restore the stance.
    void DisengageFighter(IFightWorld<TId, TOrders> w, TId c, List<TId> stops, List<TId> lateStops)
    {
        // Meitou-only: the C++ disengage is synchronous; here an AttackOrder given this tick runs at tick+1 after the StopCommand's player half
        // and would queue the fight again, so such a fighter gets a second StopCommand at tick+2 (AttackOrderStamps, PgEngine.Stop).
        bool orderedThisTick = attackOrdered.TakeOrderedIn(c, w.Tick);
        int stateIndex = FindOriginalState(c);
        FightDisengagePolicy.Plan plan = FightDisengagePolicy.BuildFullDisengagePlan();

        if (!w.IsNone(c) && w.Exists(c))
        {
            // the StopCommand (spike S1's restore; for a dead fighter the C++'s endCombatMode), queued first (see above)
            stops.Add(c);
            if (orderedThisTick)
                lateStops.Add(c);

            if (plan.ClearTempEnemies) { }   // no temp-enemy memory (MarkTempEnemy)

            if (!w.IsDead(c))
            {
                try
                {
                    // removeFocusedMeleeJobs, clearPlayerAttackOrders, endCombatMode, clearAiGoals, rethinkAi: one TaskStart.End (CAP 1)
                    if (plan.RemoveFocusedMeleeJobs || plan.ClearPlayerAttackOrders || plan.EndCombatMode || plan.ClearAiGoals || plan.RethinkAi)
                        ClearPlayerAttackOrders(w, c);

                    if (plan.RestoreStandingOrders && stateIndex >= 0)
                        w.RestoreStance(c, originalStates[stateIndex].Value.Orders);
                    // else: setStandingOrder(AGG, false) has no counterpart
                }
                finally
                {
                    if (stateIndex >= 0 && originalStates[stateIndex].Value.JobsToggledOff)
                        w.SetJobsOn(c);
                }
            }
        }
        if (stateIndex >= 0)
            originalStates.Remove(c);
        ClearAssignedTarget(c);
    }

    // C++: FightStarter.cpp:430-453
    void ClearFighterForRetarget(IFightWorld<TId, TOrders> w, TId c)
    {
        FightDisengagePolicy.Plan plan = FightDisengagePolicy.BuildInMatchRetargetPlan();

        if (w.IsNone(c) || !w.Exists(c))
            return;

        if (plan.ClearTempEnemies) { }   // no temp-enemy memory (MarkTempEnemy)

        if (w.IsDead(c))
            return;

        // removeFocusedMeleeJobs + clearPlayerAttackOrders: the stale order and fight end with the task; the standing orders and Jobs stay
        // as the engage left them (the remembered state is kept)
        if (plan.RemoveFocusedMeleeJobs || plan.ClearPlayerAttackOrders)
            ClearPlayerAttackOrders(w, c);
        ClearAssignedTarget(c);
    }

    /// <summary>Port-only: DisengageFighter for each, each under its own try/catch (the first failure in this world is logged once), then the
    /// stop and the late stop for every valid fighter (each Stop under its own try/catch too, so a throw there never replaces the exception
    /// EngageMatch's undo rethrows). DisengageMatch's body, shared with EngageMatch's undo.</summary>
    void DisengageEach(IFightWorld<TId, TOrders> w, TId[] fighters, int count)
    {
        var stops = new List<TId>(count);
        var lateStops = new List<TId>();
        for (int i = 0; i < count; ++i)
        {
            try { DisengageFighter(w, fighters[i], stops, lateStops); }
            catch (Exception e) { LogDisengageFailure("disengage", "disengage of " + describe(fighters[i]), e); }
        }
        // spike S1's restore ends with a StopCommand (the C++'s endCombatMode for a dead fighter is in it too)
        try { w.Stop(stops, 1); }
        catch (Exception e) { LogDisengageFailure("stop", "stop", e); }
        // and the fighters given an AttackOrder this tick are stopped again after that order has run (see DisengageFighter)
        try { w.Stop(lateStops, 2); }
        catch (Exception e) { LogDisengageFailure("late stop", "late stop", e); }
        // and each stopped fighter is watched for a target left without a fight (SweepStaleTargets)
        long until = w.Tick + StaleSweepTicks;
        for (int i = 0; i < stops.Count; ++i)
            staleSweep.Set(stops[i], until);
    }

    /// <summary>
    /// Meitou-only (task 3.5; divergences.md "A target left after the disengage"), once per serial step: a fighter disengaged in the last
    /// <see cref="StaleSweepTicks"/> ticks that has an attack target but no fight (<see cref="IFightWorld{TId, TOrders}.InFight"/> false, not
    /// down) gets one more StopCommand for the next tick. Why: an opponent that lost the initiative roll leaves a yield record (YieldTo /
    /// YieldUntil) the disengage does not clear (CombatSystem.Disengage drops only the character's own slot, CombatSystem.cs:608-631), and
    /// for up to 0.5 s after the yielder's block or dodge ends the combat step turns the stopped fighter on its yielder whether it fights or not
    /// (TurnOnYielder, CombatSystem.Act.cs:531-545). With no fight task nothing clears that target again: it stayed for good, the "still
    /// targeting after pg stop" seen in tasks 3.4 and 3.5 (spar-stop, PG-WATCH: target set, row null, not in combat). The C++'s endCombatMode
    /// leaves no target. A fighter that fights again (a new order, a new match: InFight) is never touched, and EngageMatch takes a fighter off
    /// the sweep. No allocation once the lists have their size.
    /// </summary>
    public void SweepStaleTargets(IFightWorld<TId, TOrders> w)
    {
        if (staleSweep.Count == 0)
            return;
        sweepStops.Clear();
        for (int i = staleSweep.Count - 1; i >= 0; --i)
        {
            TId c = staleSweep[i].Key;
            if (w.Tick > staleSweep[i].Value || w.IsNone(c) || !w.Exists(c) || w.IsDead(c))
            {
                staleSweep.Remove(c);
                continue;
            }
            if (!w.IsUnconscious(c) && !w.IsNone(w.AttackTarget(c)) && !w.InFight(c))
                sweepStops.Add(c);
        }
        if (sweepStops.Count == 0)
            return;
        try { w.Stop(sweepStops, 1); }
        catch (Exception e) { LogDisengageFailure("stale-target stop", "stale-target stop", e); }
    }

    readonly List<TId> sweepStops = new();

    /// <summary>Port only (task 4.3b): takes a fighter off the stale-target sweep without engaging it. ArenaIngress calls it when a Teams 1v1
    /// walk-in starts (ArenaIngress.cpp:1717-1758): the walk-in's move order must not be stopped by a sweep that still watches the fighter
    /// (a target left from an earlier disengage would make the sweep send a StopCommand, which ends the walk).</summary>
    public void LeaveStaleSweep(TId c) => staleSweep.Remove(c);

    /// <summary>Fighters the stale-target sweep still watches (tests).</summary>
    public int StaleSweepCount => staleSweep.Count;

    /// <summary>Counts the failure; logs the first one of each step (<paramref name="step"/>: disengage, stop, late stop) in this world.</summary>
    void LogDisengageFailure(string step, string what, Exception e)
    {
        disengageFailureCount++;
        disengageFailures.TryGetValue(step, out int n);
        disengageFailures[step] = n + 1;
        if (n > 0)
            return;
        ownLog?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"FightStarter {what} failed (logged once per step per world): {e.GetType().Name}: {e.Message}"));
    }

    /// <summary>Disengage steps that threw in this world (the F10 row).</summary>
    public int DisengageFailureCount => disengageFailureCount;

    // ---------- namespace FightStarter (FightStarter.h) ----------

    /// <summary>World teardown path: forget the remembered fighters without touching them (also the safe-save cleanup, WorldLifecycle.cpp:68).</summary>
    // C++: FightStarter.cpp:458-462
    public void AbandonWorldState()
    {
        staleSweep.Clear();   // port only (task 3.5)
        originalStates.Clear();
        focusAssignments.Clear();
        incoming.Clear();
        attackOrdered.Clear();
    }

    // C++: FightStarter.cpp:464-470
    public void RememberMatchFighters(IFightWorld<TId, TOrders> w, TId[] fighters, int count)
    {
        if (fighters is null || count <= 0)
            return;
        for (int i = 0; i < count; ++i)
            RememberOriginalState(w, fighters[i]);
    }

    // C++: FightStarter.cpp:472-537. Port-only: a throw part-way disengages the fighters this call engaged for the first time (no remembered
    // state before it: their stance, Jobs and orders came from this call), then rethrows, so none is left with Jobs off and the stance on with
    // nothing to undo it. A fighter that already had remembered state (a retarget mid-match, RetargetMatchAfterElimination; or after
    // RememberMatchFighters) is left as it is: its owner's full disengage restores it, as the C++ retarget keeps the state (:563-565).
    public void EngageMatch(IFightWorld<TId, TOrders> w, TId[] fighters, MatchRules.MatchTeam[] teams, int count, MatchRules.MatchMode mode)
    {
        if (fighters is null || teams is null || count < 2)
            return;

        PGLog.Debug("Proving Grounds: FightStarter EngageMatch (temp-enemy + focused melee orders)");

        var f = Read(w, fighters, count);
        var touched = new TId[count];
        int touchedCount = 0;
        try
        {
            for (int i = 0; i < count; ++i)
            {
                TId fighter = fighters[i];
                if (!FightTargeting.IsLivingFighter(f[i]))
                    continue;

                if (FindOriginalState(fighter) < 0)
                    touched[touchedCount++] = fighter;   // first engaged by this call: undone on a throw
                staleSweep.Remove(fighter);   // port only (task 3.5): fighting again, not a stale target
                SetCombatStance(w, fighter);

                if (mode == MatchRules.MatchMode.LastStanding)
                {
                    // Everyone is hostile, but only order each fighter toward their
                    // nearest opponent. Long index-based paths crossed in the middle
                    // and collapsed the whole field into one central melee.
                    for (int j = 0; j < count; ++j)
                    {
                        if (FightTargeting.IsLivingOpponent(mode, i, j, f, teams))
                            MarkTempEnemy(w, fighter, fighters[j]);
                    }

                    int targetIdx = FightTargeting.Choose(mode, f, teams, count, i);   // C++ :498-499 FindNearestLivingOpponent
                    if (targetIdx >= 0)
                        IssueAttackOrder(w, fighter, fighters[targetIdx], true);
                    continue;
                }

                if (mode == MatchRules.MatchMode.TeamAvB ||
                    mode == MatchRules.MatchMode.Teams1v1)
                {
                    // Establish hostility with the full opposing team, then assign
                    // one distributed opponent per fighter. Large matches used to
                    // send everyone at the first enemy and leave a queue behind the
                    // frontline.
                    for (int j = 0; j < count; ++j)
                    {
                        if (FightTargeting.IsLivingOpponent(mode, i, j, f, teams))
                            MarkTempEnemy(w, fighter, fighters[j]);
                    }

                    int targetIdx = FightTargeting.Choose(mode, f, teams, count, i);   // C++ :518-519 FindMatchedLivingTeamOpponent
                    if (targetIdx >= 0)
                        IssueAttackOrder(w, fighter, fighters[targetIdx], true);
                    continue;
                }

                // every other mode value: every living opponent is ordered (the first clears the queue), not FightTargeting.Choose's one
                bool firstOrder = true;
                for (int j = 0; j < count; ++j)
                {
                    if (!FightTargeting.IsLivingOpponent(mode, i, j, f, teams))
                        continue;

                    MarkTempEnemy(w, fighter, fighters[j]);
                    IssueAttackOrder(w, fighter, fighters[j], firstOrder);
                    firstOrder = false;
                }
            }
        }
        catch
        {
            DisengageEach(w, touched, touchedCount);
            throw;
        }
    }

    // C++: FightStarter.cpp:539-548. Port-only: each fighter's disengage under its own try/catch (DisengageEach), so one failing fighter never
    // keeps the stop from the others.
    public void DisengageMatch(IFightWorld<TId, TOrders> w, TId[] fighters, int count)
    {
        if (fighters is null || count <= 0)
            return;

        PGLog.Debug("Proving Grounds: FightStarter DisengageMatch");

        DisengageEach(w, fighters, count);
    }

    // C++: FightStarter.cpp:550-571
    public void RetargetMatchAfterElimination(IFightWorld<TId, TOrders> w, TId[] allFighters, int allCount, TId[]? remainingFighters,
        MatchRules.MatchTeam[]? remainingTeams, int remainingCount, MatchRules.MatchMode mode)
    {
        if (allFighters is null || allCount <= 0)
            return;

        PGLog.Debug("Proving Grounds: FightStarter retarget after elimination");

        // Deliberately preserve g_originalStates and Jobs. Full disengage uses
        // the global toggle_jobs UI command, which clicks audibly and briefly
        // interrupts the selected squad before EngageMatch toggles it back.
        // (The port sets Jobs directly, but keeps the same split: the remembered state stays until the full disengage.)
        for (int i = 0; i < allCount; ++i)
            ClearFighterForRetarget(w, allFighters[i]);

        if (remainingFighters is not null && remainingTeams is not null && remainingCount >= 2)
            EngageMatch(w, remainingFighters, remainingTeams, remainingCount, mode);
    }

    // C++: FightStarter.cpp:573-589
    public void ReactToIncomingAttack(IFightWorld<TId, TOrders> w, TId defender, TId attacker, IFightSession<TId>? session)
    {
        bool active = session is { IsActive: true };
        if (!FightTargeting.ShouldReact(IsLivingFighter(w, defender), IsLivingFighter(w, attacker), active,
                active ? session!.Mode : default,
                active && session!.IsEliminated(defender),
                active && session!.IsEliminated(attacker),
                active && session!.IsSparringOpponent(defender, attacker),
                FindAssignedTarget(w, defender).Equals(attacker)))
        {
            return;
        }

        MarkTempEnemy(w, defender, attacker);
        IssueAttackOrder(w, defender, attacker, true);
        PGLog.Debug("Proving Grounds: fighter retargeted to incoming attacker");
    }

    // C++: FightStarter.cpp:591-658
    public void KeepEngagedMatch(IFightWorld<TId, TOrders> w, TId[] fighters, MatchRules.MatchTeam[] teams, int count, MatchRules.MatchMode mode)
    {
        if (fighters is null || teams is null || count < 2)
            return;

        var f = Read(w, fighters, count);
        for (int i = 0; i < count; ++i)
        {
            TId fighter = fighters[i];
            if (!FightTargeting.IsLivingFighter(f[i]))
                continue;

            TId currentTarget = w.AttackTarget(fighter);
            bool validTarget = false;

            if (!w.IsNone(currentTarget))
            {
                for (int j = 0; j < count; ++j)
                {
                    if (!fighters[j].Equals(currentTarget))
                        continue;
                    validTarget = FightTargeting.IsLivingOpponent(mode, i, j, f, teams);
                    break;
                }
            }

            // A non-player fighter can retain an attack target even after its
            // AI combat task stalls. Treat that as healthy only while it is
            // actually in melee combat, so the keep-alive can repair the job.
            if (FightTargeting.KeepsTarget(validTarget, IsPlayerCharacter(w, fighter), w.InMeleeCombatMode(fighter)))
                continue;

            // C++ :623-656: Last Standing the nearest living opponent, the team modes the distributed one, any other mode value the first
            // living opponent (the loop's break), each ordered with the queue cleared
            int targetIdx = FightTargeting.Choose(mode, f, teams, count, i);
            if (targetIdx >= 0)
            {
                MarkTempEnemy(w, fighter, fighters[targetIdx]);
                IssueAttackOrder(w, fighter, fighters[targetIdx], true);
            }
        }
    }

    // C++: FightStarter.cpp:660-675 (without the isEnemy diagnostic, :669-674: the orders start next tick, divergences.md)
    public void Engage(IFightWorld<TId, TOrders> w, TId a, TId b)
    {
        if (w.IsNone(a) || w.IsNone(b))
            return;

        TId[] fighters = [a, b];
        MatchRules.MatchTeam[] teams = [MatchRules.MatchTeam.TeamA, MatchRules.MatchTeam.TeamB];
        EngageMatch(w, fighters, teams, 2, MatchRules.MatchMode.TeamAvB);
    }

    // C++: FightStarter.cpp:677-681
    public void Disengage(IFightWorld<TId, TOrders> w, TId a, TId b)
    {
        TId[] fighters = [a, b];
        DisengageMatch(w, fighters, 2);
    }

    // C++: FightStarter.cpp:683-693
    public void KeepEngaged(IFightWorld<TId, TOrders> w, TId a, TId b)
    {
        if (w.IsNone(a) || w.IsNone(b) || !w.Exists(a) || !w.Exists(b))
            return;
        if (w.IsDead(a) || w.IsDead(b) || w.IsUnconscious(a) || w.IsUnconscious(b))
            return;

        TId[] fighters = [a, b];
        MatchRules.MatchTeam[] teams = [MatchRules.MatchTeam.TeamA, MatchRules.MatchTeam.TeamB];
        KeepEngagedMatch(w, fighters, teams, 2, MatchRules.MatchMode.TeamAvB);
    }

    // ---------- the hit path (C++: CombatHooks.cpp:246-247 calls ReactToIncomingAttack from the hit hook) ----------

    /// <summary>From the hit observer (serial, must not change the world, CAP 3): records the pair only; <see cref="ApplyIncomingAttacks"/>
    /// answers it in the next serial step. No allocation (a fixed ring); a full ring drops the hit.</summary>
    public void RecordIncomingAttack(TId defender, TId attacker) => incoming.TryAdd(defender, attacker);

    /// <summary>The recorded hits in order, each through <see cref="ReactToIncomingAttack"/> (the next serial step; the orders it gives start the
    /// tick after). The first time hits were dropped in this world, one line says how many.</summary>
    public void ApplyIncomingAttacks(IFightWorld<TId, TOrders> w, IFightSession<TId>? session)
    {
        if (!droppedLogged && incoming.Dropped > 0)
        {
            droppedLogged = true;
            ownLog?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"FightStarter: the hit queue was full, {incoming.Dropped} hit(s) dropped without a reaction (logged once per world; capacity {IncomingCapacity} per tick)"));
        }
        while (incoming.TryTake(out var defender, out var attacker))
            ReactToIncomingAttack(w, defender, attacker, session);
    }
}
