using System.Globalization;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Task 6.3 (PrisonerUtil.cpp:539-744, 768-784, 1311-1368, 1407-1774; ArenaMedical.cpp:227-267; PA §2.19, §2.11; CAP 12, 15; spike S11): ringside
// aid (while the spar runs, the handler of an eliminated prisoner who needs it treats it, the order given again every 3 s), the return (each
// prisoner to its own cage when free and in range, else the nearest free one; a conscious prisoner runs back with its handler, an unconscious one
// is treated for up to 20 s if it needs it and then carried, the pick-up asked again every 2.5 s; within 36 of the cage or after 60 s it is
// imprisoned there) and the lock check (30 s; Meitou's Imprison locks the cage, so it passes at once on a working lock). The decisions are
// PrisonerUtilLogic.Return.cs; here the host's engine half (E1 list: PrisonerUtil.cs). Callers: the frame loop (TickReturn :3157,
// TickRingsideAid :3167), SparSession's stop and ArenaIngress's Cancel (BeginReturnToCages), the Arena window's failed start and the save safety
// (ReturnAllToCages).

sealed partial class PrisonerUtil
{
    /// <summary>The kinds of engine write that have thrown in this world (each logged once).</summary>
    readonly HashSet<string> failedWrites = new(StringComparer.Ordinal);

    /// <summary>Runs one engine write of the return (review fix): a throw is logged once per <paramref name="kind"/> and read as the write being
    /// refused, so the logic reaches its own ending (a placement refused fails the lock check as "does not contain the expected prisoner"; a lift
    /// or treat refused times out) instead of re-entering and re-throwing every tick with the return pending for the rest of the world.</summary>
    bool Guarded(string kind, Func<bool> write)
    {
        try { return write(); }
        catch (Exception e)
        {
            if (failedWrites.Add(kind))
                ProvingGroundsMod.Ctx?.LogError($"prisoner return: {kind} failed (read as refused; logged once): {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    void Guarded(string kind, Action write) => Guarded(kind, () => { write(); return true; });

    sealed partial class Host
    {
        // review fix (tidy): the cage fixture of the last key asked, for this tick (the lock check asks one cage several times)
        string? cageKey; long cageTick = -1; Meitou.Simulation.Locks.Fixture? cageFixture;

        Meitou.Simulation.Locks.Fixture? CageOf(string key)
        {
            if (cageTick != W.Tick || cageKey != key)
            {
                cageKey = key; cageTick = W.Tick; cageFixture = Cages.Cage(W, key);
            }
            return cageFixture;
        }

        public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c);
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        public bool IsEliminated(CharacterId c) => Owner.spar().IsEliminated(W, c);
        public bool ShouldStabilizePrisoner() => ArenaMedical.ShouldStabilizePrisoner(ArenaMedical.GetProtocol());
        public bool NeedsStabilization(CharacterId c) => ArenaMedical.NeedsStabilization(W, c);
        public void ShowPlayerMessage(string text) => PgEngine.Message(W, text);

        /// <summary>setPrisonMode(true, cage): Cages.PutInCage (S11's recipe: let go if carried, then Fixtures.Imprison, which locks the cage).</summary>
        public void Imprison(CharacterId prisoner, string cage)
        {
            if (CageOf(cage) is not { } fixture) return;
            bool done = Owner.Guarded("imprison", () => Cages.PutInCage(W, prisoner, fixture));
            if (!Spikes.SpikePatches.Armed) return;   // TEST ONLY: one PG-PRISONER imprison line while the test verbs are armed
            ProvingGroundsMod.Ctx?.Log("PG-PRISONER {\"event\":\"imprison\",\"tick\":" + W.Tick.ToString(CultureInfo.InvariantCulture)
                + ",\"id\":" + PGLog.Quote(CharacterIdText.Format(prisoner)) + ",\"cage\":" + PGLog.Quote(cage)
                + ",\"imprisoned\":" + (done ? "true" : "false") + ",\"locked\":" + (Cages.IsLocked(fixture) ? "true" : "false") + "}");
        }

        /// <summary>Nothing: Meitou's chained state is wearing a piece with a lock (LockSystem.Chained); the mod puts no shackles on
        /// (docs/divergences.md "PrisonerUtil (task 6.3").</summary>
        public void SetChainedMode(CharacterId prisoner) { }

        public bool IsDestroyed(string cage) => CageOf(cage) is { } f && Cages.IsDestroyed(W, f);
        public bool IsBroken(string cage) => CageOf(cage) is { } f && Cages.IsBroken(W, f);
        public bool HasLock(string cage) => CageOf(cage) is { } f && Cages.HasLock(f);

        /// <summary>The C++'s KenshiLib export <c>AI::isDoorLocked</c>; the port reads the lock itself (Cages.IsLocked), so it is always there.</summary>
        public bool LockVerifierAvailable() => true;

        /// <summary><c>getAI() != NULL</c>: every Meitou character can be read and ordered; a valid one has it.</summary>
        public bool HasAI(CharacterId c) => Valid(c);

        public bool IsDoorLocked(string cage) => CageOf(cage) is { } f && Cages.IsLocked(f);

        /// <summary>Never rejected: no lock order is given (<see cref="IssueLockOrder"/>).</summary>
        public bool LockOrderRejected(CharacterId handler, string cage) => false;

        /// <summary>The C++'s LOCK_DOOR order has no Meitou counterpart for a cage (Meitou's cage locking is a warden's round, LockSystem.Rounds.cs:520,
        /// not an order), and Imprison has locked the cage already: nothing is given. The logic still waits the C++'s 30 s for the lock and then
        /// reports "did not engage" (docs/divergences.md "PrisonerUtil (task 6.3").</summary>
        public void IssueLockOrder(CharacterId handler, string cage) { }

        /// <summary>clearAllAIGoals + reThinkCurrentAIAction: the task ended (TaskStart.End, Stopped: the walk and the queue's front cleared, a new
        /// decision asked), FightStarter's own counterpart (PgEngine.EndTask). Port only: the character leaves FightStarter's stale-target sweep
        /// first, as before a move (task 6.2).</summary>
        public void ClearAIGoals(CharacterId c)
        {
            Owner.Guarded("clear AI goals", () =>
            {
                Owner.fightStarter.LeaveStaleSweep(W, c);
                PgEngine.EndTask(W, c);
            });
        }

        public bool IsCarryingSomething(CharacterId c) => PgEngine.IsCarrying(W, c);
        public bool IsBeingCarried(CharacterId c) => PgEngine.IsCarried(W, c);
        public void DropCarriedObject(CharacterId c) => Owner.Guarded("let go", () => PgEngine.LetGo(W, c));

        /// <summary>LIFT_PERSON_PLAYER_ORDER: CarrySystem.BeginPickUp (PgEngine.BeginLift). The handler passes the 3.2b pick-up refusal as its
        /// prisoner's own carrier (CombatMatchView.IsOwnCarrier; the published view lists each match prisoner with its handler). Port only: it
        /// leaves FightStarter's stale-target sweep first.</summary>
        public void LiftPerson(CharacterId handler, CharacterId prisoner)
        {
            Owner.LiftsAsked++;   // TEST ONLY count
            if (Owner.Guarded("lift", () => { Owner.fightStarter.LeaveStaleSweep(W, handler); return PgEngine.BeginLift(W, handler, prisoner); }))
                Owner.LiftsStarted++;
        }

        /// <summary>ArenaMedical::IssueStabilizeOrder (M4's treat path: first aid, or the robot kit when the robot need is higher, TaskStart.Start
        /// Treat). Port only: the medic leaves FightStarter's stale-target sweep first.</summary>
        public void IssueStabilizeOrder(CharacterId medic, CharacterId target)
        {
            Owner.TreatsAsked++;   // TEST ONLY count
            if (Owner.Guarded("treat", () => { Owner.fightStarter.LeaveStaleSweep(W, medic); return ArenaMedical.IssueStabilizeOrder(W, medic, target); }))
                Owner.TreatsStarted++;
        }
    }

    /// <summary>TEST ONLY counts of the return's work in this world (pg prisoners shows them): treat orders asked / started, lift orders asked /
    /// started.</summary>
    internal int TreatsAsked, TreatsStarted, LiftsAsked, LiftsStarted;

    // C++: PrisonerUtil.cpp:1311-1368, called from ArenaUI.cpp ForgottenGUI_update_hook (:3167)
    internal void TickRingsideAid(World world) => logic.TickRingsideAid(H(world));

    // C++: PrisonerUtil.cpp:1577-1725, called from ArenaUI.cpp ForgottenGUI_update_hook (:3157)
    internal void TickReturn(World world) => logic.TickReturn(H(world));

    // C++: PrisonerUtil.cpp:1742-1756
    internal void TickPendingCageLocks(World world) => logic.TickPendingCageLocks(H(world));

    // C++: PrisonerUtil.cpp:1407-1575 (SparSession's stop, ArenaIngress's Cancel)
    internal void BeginReturnToCages(World world, out string statusOut) => logic.BeginReturnToCages(H(world), out statusOut);

    // C++: PrisonerUtil.cpp:1758-1774 (the Arena window's failed start, the save safety)
    internal void ReturnAllToCages(World world, out string statusOut) => logic.ReturnAllToCages(H(world), out statusOut);
}
