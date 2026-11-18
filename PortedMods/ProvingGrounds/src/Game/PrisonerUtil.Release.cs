using System.Globalization;
using System.Numerics;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Task 6.2 (PrisonerUtil.cpp:443-507, 746-766, 1110-1257; PA §2.19, CAP 15, spike S11): the handler walks to the cage; within 120 units (or after
// 45 s even if not near) and 0.45 s after it arrived, the prisoner is released (Fixtures.Release, releaser -1: no crime, not escaped) and says one
// of the eight release lines; prisoner and handler run to the formation (the handler 28 units to the side and 8 short of the prisoner's mark).
// The decisions are PrisonerUtilLogic.Release.cs; here the host's engine half (E1 list: PrisonerUtil.cs). ArenaIngress (its PgIngressHost
// pass-through) calls BeginHandlerUnlocks at Begin (ArenaIngress.cpp:1358-1361), TickHandlerUnlocks / ConsumeNewlyReleased / SendPairToArena
// while the release is pending (:1578-1590) and SendPairToArena from its formation moves (:775).

sealed partial class PrisonerUtil
{
    /// <summary>The port's stand-in for the C++'s rand() state: one deterministic state (Hashing.CRand from the unseeded Hashing.CRandSeed) on the
    /// sim thread, kept for the process (a new world does not reset it). Not the C++'s: msvcr100's rand() state is per thread (the GUI/main thread
    /// that runs the ArenaUI frame hook) and, linked dynamically, shared with Kenshi's and Ogre's own rand() calls there, so the original's sequence
    /// is effectively unpredictable (docs/divergences.md "PrisonerUtil (task 6.2"). Sim thread only.</summary>
    static uint randState = Hashing.CRandSeed;

    sealed partial class Host
    {
        public uint NowMs => Owner.clock.NowMs;

        public void SetMoveStance(CharacterId c) => Owner.Guarded("stance", () => PgEngine.SetMoveStance(W, c));

        /// <summary>The move order. Port only: the character leaves FightStarter's stale-target sweep first (task 3.5's sweep stops a character it
        /// still watches, which would end this walk; M4's LeaveStaleSweep note), as ArenaIngress does for its walk-in.</summary>
        public void MoveTo(CharacterId c, Vector3 location)
        {
            Owner.Guarded("move", () =>
            {
                Owner.fightStarter.LeaveStaleSweep(W, c);
                PgEngine.MoveTo(W, c, location);
            });
        }

        // review fix (task 6.3): these writes are on the return's path too; a throw is read as refused (PrisonerUtil.Return.cs Guarded)
        public void SetRunSpeed(CharacterId c) => Owner.Guarded("run speed", () => PgEngine.SetRunSpeed(W, c));

        public void ClearOrderQueue(CharacterId c) => Owner.Guarded("clear orders", () => PgEngine.ClearOrders(W, c));

        /// <summary>The C++'s "fake unlock moment" order (RELEASE_PRISONER) is not given: Meitou's own Set Free (Task_ReleasePrisoner,
        /// LockSystem.Cages.cs:13-44, :272) is a real release task that marks the prisoner freed and would race the mod's own release 0.45 s later
        /// (docs/divergences.md "PrisonerUtil (task 6.2"). The handler stands at the cage meanwhile.</summary>
        public void IssueReleaseOrder(CharacterId handler, string cage, CharacterId prisoner) { }

        public bool IsChainedMode(CharacterId c) => PgEngine.IsChained(W, c);

        /// <summary>Task 9.3d: the cage release binds (Cages.ReleaseAvailable, Members.FixturesRelease); false: PrepareMatch refuses prisoner matches.</summary>
        public bool CageReleaseAvailable() => Cages.ReleaseAvailable;

        public void LeavePrison(CharacterId prisoner)
        {
            string? cage = Spikes.SpikePatches.Armed ? Cages.HeldIn(W, prisoner) : null;   // read only for the test line below
            bool released = Cages.Release(W, prisoner);
            if (!Spikes.SpikePatches.Armed) return;   // TEST ONLY: one PG-PRISONER release line while the test verbs are armed
            var sb = new System.Text.StringBuilder("{\"event\":\"release\",\"tick\":").Append(W.Tick.ToString(CultureInfo.InvariantCulture))
                .Append(",\"id\":").Append(PGLog.Quote(CharacterIdText.Format(prisoner))).Append(",\"cage\":").Append(PGLog.Quote(cage))
                .Append(",\"released\":").Append(released ? "true" : "false");
            Owner.AppendReleaseEntry(W, prisoner, sb);
            ProvingGroundsMod.Ctx?.Log("PG-PRISONER " + sb.Append('}'));
        }

        /// <summary>Nothing: Meitou's chained state is wearing a piece with a lock (LockSystem.Chained, LockSystem.Shackles.cs:73-77); it has no flag
        /// to clear without taking the shackles off (docs/divergences.md "PrisonerUtil (task 6.2").</summary>
        public void ClearChainedMode(CharacterId prisoner) { }

        public void Say(CharacterId c, string text)
        {
            if (PgEngine.Bark(W, c, text)) Owner.said?.Invoke(W, c, text);
        }

        public int Rand() => Hashing.CRand(ref randState);

        public void RememberMatchFighters(IReadOnlyList<CharacterId> handlers)
        {
            var array = handlers.ToArray();
            Owner.fightStarter.RememberMatchFighters(W, array, array.Length);
        }
    }

    /// <summary>TEST ONLY: the release line's entry state (handler, its distance to the cage on x/z, the unlock timers) for the PG-PRISONER line.</summary>
    void AppendReleaseEntry(World world, CharacterId prisoner, System.Text.StringBuilder sb)
    {
        foreach (var e in logic.State.MatchEntries)
        {
            if (!e.Prisoner.Equals(prisoner)) continue;
            sb.Append(",\"handler\":").Append(PGLog.Quote(CharacterIdText.Format(e.Handler)))
              .Append(",\"arrived\":").Append(e.UnlockArrived ? "true" : "false")
              .Append(",\"unlockElapsed\":").Append(TestJson.G(e.UnlockElapsed))
              .Append(",\"holdElapsed\":").Append(TestJson.G(e.UnlockHoldElapsed));
            if (e.CageUseable is { } k && Cages.Position(world, k) is { } cp && PgEngine.Position(world, e.Handler) is { } hp)
            {
                float dx = hp.X - cp.X, dz = hp.Z - cp.Z;
                sb.Append(",\"handlerDistXZ\":").Append(TestJson.G(MathF.Sqrt(dx * dx + dz * dz)));
            }
            return;
        }
    }

    // C++: PrisonerUtil.cpp:1110-1144
    internal void BeginHandlerUnlocks(World world) => logic.BeginHandlerUnlocks(H(world));

    // C++: PrisonerUtil.cpp:1146-1221
    internal void TickHandlerUnlocks(World world) => logic.TickHandlerUnlocks(H(world));

    // C++: PrisonerUtil.cpp:1223-1237
    internal bool ConsumeNewlyReleased(World world, CharacterId prisoner) => logic.ConsumeNewlyReleased(H(world), prisoner);

    // C++: PrisonerUtil.cpp:1239-1257
    internal void SendPairToArena(World world, CharacterId prisoner, Vector3 target) => logic.SendPairToArena(H(world), prisoner, target);
}
