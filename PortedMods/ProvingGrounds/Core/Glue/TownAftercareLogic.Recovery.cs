namespace ProvingGrounds.Core.Glue;

// Task 8.4c (TW §2.9 "Bed recovery"; CAP 12): the bed recovery sweep, the release of a bed occupant and the treatment record. TownAftercare.cpp:
// ReleaseBedOccupant :371-402 (BedReleaseOutcome), RecordTreatment :1035-1050, TickBedRecovery :1227-1292, one copy each in the C++'s order.
//
// Bed entries. The C++ learned every entry into a hospital-163 bed by any route through its setBedMode hook (RecordBedEntry :75-84 ->
// RememberBedOccupant :351-370). Meitou has no such hook; the port knows the occupants it adopted (Begin, task 8.4a), the patients it sent itself
// (the delivery poll, task 8.4b) and, at each 1 s poll of this sweep, every occupant of the Scratch Hospital's beds (the host's RecoveryBeds:
// RememberBedOccupant's rules, a hospital bed, a non-player occupant lying in it, the 64-slot cap). An entry is seen up to one poll after the
// character lay down, not at that moment (docs/divergences.md "TownAftercare (task 8.4c)").
//
// Taking out. The C++'s setBedMode(false), then stopOperating if the character still lies there, then the native AI back (ResumeNative). Meitou's
// counterparts (the host): LeaveBedMode = the Sleep task ended (TaskStart.End Stopped) and the engine's own get-up (FixtureRegistry.GetOutOfBed,
// what BedSystem.GetUp would do at the next step), StopOperating = FixtureRegistry.LeaveBed, ResumeNative = the aftercare's patient hold given
// back, or the AI asked to decide again (Ai.Replan) for a character nobody holds.

public sealed partial class TownAftercareLogic<TId>
{
    // C++: TownAftercare.cpp:71-72 (lastBedSweepTrace, lastBedSweep)
    float lastBedSweepTrace = -1000;
    uint lastBedSweep;
    /// <summary>TickBedRecovery's copy of the registry (C++ <c>BedOccupant tracked[MaxBeds]</c> on the stack): one array per world, no allocation.</summary>
    readonly BedOccupant[] trackedBeds = new BedOccupant[TownMedicCoordinator.MaxBeds];
    /// <summary>Port only (task 8.4c quality review): registry entries whose step threw (the entry forgotten; the first three logged).</summary>
    int bedEntryFailures;
    public int BedEntryFailures => bedEntryFailures;

    /// <summary>C++: TownAftercare.cpp:371-375 <c>struct BedReleaseOutcome</c>.</summary>
    public struct BedReleaseOutcome
    {
        public bool LeftBed, BedFree;
        public string Path;
        public BedReleaseOutcome() { LeftBed = false; BedFree = false; Path = "none"; }
    }

    // C++: TownAftercare.cpp:376-402
    BedReleaseOutcome ReleaseBedOccupant(ITownAftercareHost<TId> h, TId p, string bed)
    {
        var result = new BedReleaseOutcome();
        if (h.IsNull(p) || !h.Valid(p)) return result;
        result.Path = "set-bed-mode";
        h.LeaveBedMode(p);
        if (h.InBed(p))
        {
            // The bed-mode call alone sometimes leaves the character seated; the
            // operator slot is the engine's other half of the same state.
            h.StopOperating(p, bed);
            result.Path = "stop-operating";
        }
        result.LeftBed = !h.InBed(p);
        result.BedFree = !Occupied(h, h.Bed(bed));
        if (!result.LeftBed && !result.BedFree) result.Path = "none";
        // A corpse has no native AI to resume; corpse cleanup owns the body.
        if (result.LeftBed && !h.IsDead(p)) h.ResumeNative(p);
        return result;
    }

    /// <summary>Port of the setBedMode hook's bed half (RecordBedEntry :82-83) by a scan: every occupant lying in a Scratch Hospital bed is
    /// remembered (RememberBedOccupant's own rules: a valid non-player character, the 64-slot cap; a known one keeps its clock unless it moved
    /// to another bed). Allocation-free (the host's list is cached).</summary>
    void ScanHospitalBeds(ITownAftercareHost<TId> h)
    {
        var ids = h.RecoveryBeds();
        for (int i = 0; i < ids.Count; ++i)
        {
            if (HospitalBed(h, ids[i]) is not { } bed) continue;
            TId occupant = bed.Occupant;
            if (h.IsNull(occupant) || !bed.Occupied || !bed.OccupantInBed) continue;
            RememberBedOccupant(h, occupant, bed.Id);
        }
    }

    // C++: TownAftercare.cpp:1035-1050. The prefix and postfix on FirstAidSystem.Apply call it for every first-aid or robot-repair treatment tick
    // (src/Game/ArenaMedical.cs); only the ten medics Direct gave a trace slot are counted. Allocation-free.
    public void RecordTreatment(TId medic, TId patient, bool doctoring, bool blocked, bool complete, float frameTime)
    {
        var eq = EqualityComparer<TId>.Default;
        if (eq.Equals(medic, none)) return;
        for (int i = 0; i < treatmentTraces.Length; ++i) if (treatmentTraces[i].Used && eq.Equals(treatmentTraces[i].Actor, medic))
        {
            ref TreatmentTrace trace = ref treatmentTraces[i];
            trace.Patient = patient;
            if (doctoring) { ++trace.Doctoring; if (!blocked) trace.DoctorTime += frameTime; }
            else { ++trace.FirstAid; if (!blocked) trace.AidTime += frameTime; }
            if (blocked) ++trace.Blocked;
            if (complete) ++trace.Completed;
            if (!blocked && !complete && frameTime > 0) ++trace.WorkTicks;
            break;
        }
    }

    // C++: TownAftercare.cpp:1227-1292. Runs every TownArena.Tick, bouts or not (TownArena.cpp:1888); the 1 s poll on the sim clock.
    public void TickBedRecovery(ITownAftercareHost<TId> h)
    {
        if (h.IsPaused) return;
        uint now = h.NowMs;
        if (!TownAftercarePolicy.BedPollDue(lastBedSweep, now)) return;
        float dt = lastBedSweep != 0 ? unchecked(now - lastBedSweep) / 1000.0f : 0.0f;
        lastBedSweep = now;
        // A suspended tick must not age a bed stay in one step.
        bedRecoveryElapsed += dt > 1.0f ? 1.0f : dt;
        ScanHospitalBeds(h);   // port: the setBedMode hook's stand-in (header)
        if (bedOccupantCount <= 0) return;
        int count = bedOccupantCount;
        for (int i = 0; i < count; ++i) trackedBeds[i] = bedOccupants[i];
        int released = 0;
        for (int i = 0; i < count; ++i)
        {
            try { if (ReleaseIfDone(h, i)) ++released; }
            catch (Exception e)
            {
                // Port only: one entry that throws must not stop the others; it is forgotten (the scan may find it again next poll).
                TId bad = trackedBeds[i].Actor;
                if (++bedEntryFailures <= 3)
                    h.Debug("Proving Grounds: aftercare bed recovery entry failed actor=" + SafeActorText(h, bad) + " error=" + e.GetType().Name + ": " +
                        e.Message);
                ForgetBedOccupant(bad);
            }
        }
        if (released > 0 || bedRecoveryElapsed - lastBedSweepTrace >= 30.0f)
        {
            lastBedSweepTrace = bedRecoveryElapsed;
            h.Debug("Proving Grounds: aftercare bed sweep tracked=" + N(count) + " released=" + N(released) + " elapsed=" +
                CMath.FormatF(bedRecoveryElapsed, 0));
        }
    }

    static string SafeActorText(ITownAftercareHost<TId> h, TId c)
    {
        try { return h.ActorText(c); } catch { return "?"; }
    }

    /// <summary>TickBedRecovery's loop body for <c>tracked[i]</c> (C++ :1243-1285); true when it released the occupant.</summary>
    bool ReleaseIfDone(ITownAftercareHost<TId> h, int i)
    {
        {
            TId p = trackedBeds[i].Actor;
            string? bedId = trackedBeds[i].Bed;
            var bed = HospitalBed(h, bedId);
            if (h.IsNull(p) || !h.Valid(p) || Player(h, p) || bed is null || !h.InBed(p))
            {
                // Unresolvable, recruited by the player, or already out of the bed
                // on their own: nothing left to give back.
                ForgetBedOccupant(p);
                return false;
            }
            bool recoveryRead = h.RosterRead(p, out var recovery);
            bool dead = h.IsDead(p);
            bool healed = !dead && recoveryRead && recovery.Band == RosterStatusPolicy.Band.Green;
            float age = bedRecoveryElapsed - trackedBeds[i].Entered;
            if (!dead && !healed && !TownAftercarePolicy.BedStayExpired(age)) return false;
            // A patient the care session still owns, a body being carried, or a
            // fighter who went down again must not be pulled out of the bed.
            if (!dead)
            {
                if (IsManaged(h, p) || h.IsCarried(p) || h.IsUnconscious(p) || h.InCombatMode(p)) return false;
                if (bedRecoveryElapsed - trackedBeds[i].LastAttempt < TownAftercarePolicy.BedReleaseRetry()) return false;
            }
            var outcome = ReleaseBedOccupant(h, p, bed.Value.Id);
            h.Debug("Proving Grounds: aftercare bed release actor=" + h.ActorText(p) + " name=" + h.Name(p) + " bed=" + BedText(bedId) +
                " reason=" + (dead ? "dead" : healed ? "healed" : "stay-limit") + " path=" + outcome.Path + " stayAge=" + F1(age) +
                " recovery=" + F3(recoveryRead ? recovery.Recovery : -1.0f) + " blood=" + F3(recoveryRead ? recovery.Blood : -1.0f) +
                " lowestLimb=" + F3(recoveryRead ? recovery.LowestLimb : -1.0f) +
                " band=" + N(recoveryRead ? (int)recovery.Band : -1) +
                " leftBed=" + B(outcome.LeftBed) + " bedFree=" + B(outcome.BedFree));
            // The bed is the resource: stop tracking once either the character or
            // the bed itself reports the seat is free, so a refused call is retried
            // on the next poll instead of being forgotten.
            if (outcome.LeftBed || outcome.BedFree)
            {
                ForgetBedOccupant(p);
                return true;
            }
            DeferBedOccupant(p, bedRecoveryElapsed);
            return false;
        }
    }
}
