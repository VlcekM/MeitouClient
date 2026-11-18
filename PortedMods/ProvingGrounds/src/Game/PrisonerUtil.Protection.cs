using Meitou.Simulation;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Task 6.2 (PrisonerUtil.cpp:509-537, 786-800, 1259-1309, 1370-1405; PA §2.19, §2.8; CAP 1, 2, 5, 19): the handlers stand still during the match
// (parked at the countdown and after the start, the hold re-asserted every 0.25 s while the spar runs: the C++ never used Engaged on a player
// character, so neither do we); every 0.10 s (at once after a release) anyone attacking a released prisoner who is not its sparring opponent
// is broken off, and so is the prisoner chasing a non-opponent. Together with task 3.2b's isolation postfixes (CombatHooks BestAttackerOf /
// FindFoe over the published view, which lists every match prisoner: CopyMatchPrisoners) this stands for the original's isAlly / attackTarget /
// iShouldntAggravateThisTarget hooks; a match prisoner and its sparring opponent are not an isolated pair (CombatHookLogic.ShouldIsolatePair
// asks IsSparringOpponent first), exactly as two squad-mates in a spar. A prisoner of another faction is fought with the normal crime and
// relation rules (no exemption in the original, PA §2.8): no patch here. The decisions are PrisonerUtilLogic.Protection.cs.

sealed partial class PrisonerUtil
{
    sealed partial class Host
    {
        /// <summary>ApplyParkOrders on a handler: M4's player hold (PgIngressHost.ParkPlayerInPlace: the walk ended, Passive + Hold where it
        /// stands; one hold mechanism). Port only: the handler leaves FightStarter's stale-target sweep first, so the sweep never stops a parked
        /// handler. A handler is always a player character (ArenaUI.cpp:510-525 picks the handlers from the squad), so no NPC is held here.</summary>
        public void ParkInPlace(CharacterId c)
        {
            Owner.Guarded("park", () =>
            {
                Owner.fightStarter.LeaveStaleSweep(W, c);
                if (PgEngine.IsPlayer(W, c)) PgIngressHost.ParkPlayerInPlace(W, c);
            });
        }

        /// <summary>ReinforceParkOrders: Passive + Hold re-asserted (PgEngine.SetBenchStance; it keeps the hold point a character already
        /// holds), the AI and its task left alone, as the C++ (no clearAllAIGoals).</summary>
        public void ReassertPark(CharacterId c)
        {
            if (PgEngine.IsPlayer(W, c)) PgEngine.SetBenchStance(W, c, true);
        }

        public bool SparIsActive() => Owner.spar().IsActive(W);
        public bool IsSparringOpponent(CharacterId a, CharacterId b) => Owner.spar().IsSparringOpponent(W, a, b);
        public void AllAttackers(CharacterId c, List<CharacterId> into)
        {
            Owner.ProtectionPasses++;   // TEST ONLY count: one per released prisoner per protection pass
            PgEngine.AttackersOf(W, c, into);
        }
        public CharacterId AttackTarget(CharacterId c) => PgEngine.AttackTarget(W, c);

        /// <summary>Nothing: Meitou keeps no temporary enemy memory between fighters (the senses tag two fighters Enemy while they fight, CAP 1;
        /// FightStarter's own note in PgEngine.cs).</summary>
        public void ClearTempEnemyStatus(CharacterId a, CharacterId b) { }

        public void RemoveMeleeAttackJobs(CharacterId c) => Owner.CountBreakOff(c, PgEngine.EndAttack(W, c));
        public void EndCombatMode(CharacterId c) => Owner.CountBreakOff(c, PgEngine.EndCombat(W, c));

        /// <summary>Nothing: Meitou has no temporary ally memory; the handler and its prisoner are an isolated pair in the published combat view
        /// (no foe either way, CombatHooks), which keeps them apart.</summary>
        public void RememberTemporaryAlly(CharacterId a, CharacterId b) { }
    }

    /// <summary>TEST ONLY counts of the protection's work in this world (pg prisoners shows them): the passes over a released prisoner, and the
    /// attack task ends / combat exits it made on outsiders and on match prisoners.</summary>
    internal int ProtectionPasses, OutsiderBreakOffs, PrisonerBreakOffs;

    /// <summary>TEST ONLY: the outsiders broken off, each with its count, the last <see cref="OutsiderIdsKept"/> distinct ones (oldest dropped).</summary>
    internal readonly List<(CharacterId Id, int Count)> OutsidersBrokenOff = [];
    const int OutsiderIdsKept = 8;

    void CountBreakOff(CharacterId c, bool done)
    {
        if (!done) return;
        if (logic.State.MatchEntries.Exists(e => e.Prisoner.Equals(c))) { PrisonerBreakOffs++; return; }
        OutsiderBreakOffs++;
        int i = OutsidersBrokenOff.FindIndex(o => o.Id.Equals(c));
        int n = 1;
        if (i >= 0) { n = OutsidersBrokenOff[i].Count + 1; OutsidersBrokenOff.RemoveAt(i); }
        else if (OutsidersBrokenOff.Count == OutsiderIdsKept) OutsidersBrokenOff.RemoveAt(0);
        OutsidersBrokenOff.Add((c, n));
    }

    // C++: PrisonerUtil.cpp:1259-1309 (also run by TickMatchUpkeep)
    internal void ProtectMatchPrisoners(World world) => logic.ProtectMatchPrisoners(H(world));

    // C++: PrisonerUtil.cpp:1370-1374, ArenaIngress.cpp:856 (countdown) and :951-952 (after StartMatch)
    internal void ParkMatchHandlers(World world) => logic.ParkMatchHandlers(H(world));

    // C++: PrisonerUtil.cpp:1376-1380, ArenaIngress.cpp:1444-1445 (each countdown tick)
    internal void ReinforceParkedHandlers(World world) => logic.ReinforceParkedHandlers(H(world));
}
