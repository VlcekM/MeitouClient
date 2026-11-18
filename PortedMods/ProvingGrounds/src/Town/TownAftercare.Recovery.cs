using Meitou.Simulation;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Town;

// Task 8.4c (TW §2.9 "Bed recovery", CAP 12; m8-research.md "Taking a patient out of a bed"): the host's reads and orders for the bed recovery
// (Core/Glue/TownAftercareLogic.Recovery.cs). Engine calls of the C++ in task 8.4c's ranges (E1) and what replaces them:
//   RecordBedEntry's hospital-bed half (the setBedMode hook, :75-84) ... a scan of the Scratch Hospital 163's beds each poll: RecoveryBeds (the
//       bed fixtures of every standing hospital 163 of Scratch's site, Beds.OfTownRecord, found again when Scratch's site or the world's ruins
//       revision changes (a building destroyed or rebuilt, FixtureRegistry.RuinsRevision, as Towns.Refresh), and every 30 s while none)
//   setBedModeOriginal(p, false, bed) ... Beds.LeaveBedMode (TaskStart.End Stopped + FixtureRegistry.GetOutOfBed)
//   bed->stopOperating(actor) ... Beds.StopOperating (FixtureRegistry.LeaveBed)
//   TownMedicAIPolicy::ResumeNative (clearOrders, reThinkCurrentAIAction, clearAllAIGoals, chooseGoal, callForUpdate) ... the hold registry's
//       release of the owner AftercarePatient when the aftercare holds the character (Engaged off + Ai.Replan only once no owner is left), else
//       PgEngine.Replan for a character nobody holds (another owner's hold is left to that owner)
//   RosterStatus::Read, isDead / isBeingCarried / isUnconcious / isInCombatMode(true, true), inSomething == IN_BED ... the host's existing reads
//   GetTickCount, ou->isPaused ... PgClock.NowMs, false (a paused world runs no serial step)

sealed partial class TownAftercare
{
    const uint RecoveryBedRetryMs = 30_000;
    readonly List<string> recoveryBeds = [];
    readonly List<(string Bed, string Record)> recoveryScratch = [];
    object? recoveryScratchSite;   // the TownSite the list was found for (a new Scratch site finds it again)
    long recoveryRuinsRevision = -1;   // world.Fixtures.RuinsRevision when found: a building destroyed or rebuilt (same TownSite) finds it again
    uint recoveryBedsTriedAt;
    bool recoveryBedsTried;

    sealed partial class Host
    {
        public IReadOnlyList<string> RecoveryBeds()
        {
            var o = owner;
            var site = o.towns.Scratch;
            long ruins = Beds.RuinsRevision(W);
            if (!ReferenceEquals(site, o.recoveryScratchSite) || ruins != o.recoveryRuinsRevision)
            {
                o.recoveryBeds.Clear(); o.recoveryScratchSite = site; o.recoveryRuinsRevision = ruins; o.recoveryBedsTried = false;
            }
            if (site is null || (o.recoveryBeds.Count > 0)) return o.recoveryBeds;
            uint now = o.clock.NowMs;
            if (o.recoveryBedsTried && unchecked(now - o.recoveryBedsTriedAt) < RecoveryBedRetryMs) return o.recoveryBeds;
            o.recoveryBedsTried = true; o.recoveryBedsTriedAt = now;
            o.recoveryScratch.Clear();
            Beds.OfTownRecord(W, site, TownAftercareLogic<CharacterId>.HospitalRecord, o.recoveryScratch);
            foreach (var (bed, record) in o.recoveryScratch)
            {
                o.bedBuildingRecord[bed] = record;   // the bed reads' building record (Host.Bed)
                o.recoveryBeds.Add(bed);
            }
            return o.recoveryBeds;
        }

        public void LeaveBedMode(CharacterId c) => Beds.LeaveBedMode(W, c);
        public void StopOperating(CharacterId c, string bed) => Beds.StopOperating(W, c, bed);

        public void ResumeNative(CharacterId c)
        {
            var holds = owner.holds;
            if (holds.IsHeldBy(c, HoldOwner.AftercarePatient)) holds.Release(W, c, HoldOwner.AftercarePatient);
            else if (!holds.IsHeld(c)) PgEngine.Replan(W, c);
        }
    }
}
