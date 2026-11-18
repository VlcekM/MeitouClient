using System.Globalization;
using System.Numerics;
using Kind = ProvingGrounds.Core.TownMedicCoordinator.Kind;
using Act = ProvingGrounds.Core.TownAftercarePolicy.Action;

namespace ProvingGrounds.Core.Glue;

// Task 8.4b (TW §2.9 "After the fight" steps 1-9; CAP 12; spike S8): the treatment and transport. Every second after the fight (and, in Teams 1v1,
// for the fighters already eliminated while the next duel runs) Core's planner (TownMedicCoordinator) turns the observation into one intent per
// medic and Execute carries it out through the host's orders: Bandage / Repair = a first-aid task on the patient, ApproachCarry = a run to it,
// PickUp = the lift, Deliver = the put-in-bed trip to the coordinator's bed, WalkBed = a walking patient sent to lie in that bed, Wait = stand.
// TownAftercare.cpp:270-319, :409-442, :483-1033, :1293-1401, one copy each in the C++'s order. The planner (Core TownMedicCoordinator) and the
// stall rules (TownAftercarePolicy.CommandProgress) are Core's, untouched. Every PGLog line byte for byte (floats through CMath.FormatF).

public sealed partial class TownAftercareLogic<TId>
{
    // Kenshi's TaskType numbers of Execute's orders (the native order line's task=%d; Meitou's TaskType keeps them: TaskType.cs:114, :130, :254,
    // :410, :914, :1046 at v0.17.3).
    const int MoveCusOrdered = 29, FirstAidOrder = 25, FirstAidRobot = 60, PutSomeoneInBed = 99, LiftPersonPlayerOrder = 225, UseBedOrder = 258;

    static string F1(float f) => CMath.FormatF(f, 1);
    static string F3(float f) => CMath.FormatF(f, 3);
    static string F4(float f) => CMath.FormatF(f, 4);
    static string F5(float f) => CMath.FormatF(f, 5);
    static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    static string B(bool b) => b ? "1" : "0";
    static string V(Vector3 v) => "(" + F1(v.X) + "," + F1(v.Y) + "," + F1(v.Z) + ")";

    /// <summary>The C++ <c>hand::toString()</c> of a bed (a building handle): the port prints the bed fixture's id, "none" for the NULL hand.</summary>
    static string BedText(string? bed) => bed ?? "none";

    /// <summary>The C++ <c>movement-&gt;destination</c>: the walk's destination while moving, else where the character stands (Meitou keeps no goal
    /// once a walk ends; the C++ kept the last destination).</summary>
    static Vector3 Destination(ITownAftercareHost<TId> h, TId c) => h.TryGetMoveDestination(c, out var d) ? d : h.Position(c);

    // C++: TownAftercare.cpp:270-319
    void DirectPatient(ITownAftercareHost<TId> h, TId p, bool hold)
    {
        if (h.IsNull(p) || !h.Valid(p) || h.IsUnconscious(p) || h.IsCarried(p) || h.InBed(p)) return;
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < patients.Count; ++i)
        {
            Patient patient = patients[i];
            if (!eq.Equals(patient.Actor, p)) continue;
            if (!patient.Directed)
            {
                if (!Player(h, p))
                {
                    object? receiver = Receiver(h, p);
                    bool claimed = patientReservations.Add(receiver);
                    // Port: the C++ clearOrders(receiver) after the claim is the patient's hold (spike S10: Engaged, the walk and task stopped once);
                    // a hold the registry cannot take (stale id, control unavailable, a throw) fails the claim as a full table does.
                    if (claimed && !h.Hold(p, HoldOwner.AftercarePatient))
                    {
                        patientReservations.Remove(receiver);
                        claimed = false;
                    }
                    if (!claimed)
                    {
                        // Port: the C++ prints the receiver pointer (%p); the port's receiver is the character itself.
                        h.Debug("Proving Grounds: aftercare fighter ownership failed name=" + h.Name(p) + " actor=" + h.ActorText(patient.Actor) +
                            " receiver=" + h.ActorText(p));
                        patient.Reserved = false;
                        patients[i] = patient;
                        return;
                    }
                    patient.Reserved = true;
                    patients[i] = patient;   // stored at once: a later throw must not leave a hold no table entry names (Release gives it back)
                }
                (patient.Hold, patient.Passive) = h.StandingOrders(p);
                patient.Directed = true;
                patient.ReleaseLogged = false;
                // Replace old work once. Do not enqueue rethink/body completion:
                // it can cancel the replacement native action on the next update.
                // (Port: clearAllAIGoals is the hold taken above.)
                if (hold) h.MoveTo(p, h.Position(p));
                h.Debug("Proving Grounds: aftercare fighter assigned name=" + h.Name(p) + " actor=" + h.ActorText(patient.Actor));
            }
            h.SetStandingOrders(p, hold, true);
            if (!hold && patient.Reserved && !Player(h, p))
            {
                if (!patient.SpeedDirected)
                {
                    patient.Speed = h.MoveSpeed(p);
                    patient.SpeedDirected = true;
                }
                // Self-moving patients need the same run order as carrying medics.
                // Leaving their town WALK order in place holds the whole bout open.
                Run(h, p);
            }
            patients[i] = patient;
            return;
        }
    }

    // C++: TownAftercare.cpp:409-424
    static bool NeedsRecovery(ITownAftercareHost<TId> h, TId p)
    {
        if (h.IsNull(p) || !h.Valid(p) || h.IsDead(p)) return false;
        if (!h.HasMedical(p)) return h.IsUnconscious(p);
        float minimumHealth = 1.0f;
        var parts = h.Parts(p);
        for (int i = 0; i < parts.Count; ++i)
        {
            var part = parts[i];
            if (part.Dead || part.Max <= 0) continue;
            float health = (part.Flesh - part.Stun) / part.Max;
            if (health < minimumHealth) minimumHealth = health;
        }
        return TownAftercarePolicy.NeedsRecovery(h.IsUnconscious(p), minimumHealth, h.BloodRatio(p));
    }

    // C++: TownAftercare.cpp:425-430
    bool NeedsHospital(ITownAftercareHost<TId> h, TId p)
    {
        bool winner = false;
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < patients.Count; ++i)
            if (eq.Equals(patients[i].Actor, p)) { winner = patients[i].Winner && !patients[i].EarlyAdmitted; break; }
        return !h.IsNull(p) && h.Valid(p) && TownAftercarePolicy.NeedsHospital(winner, h.IsUnconscious(p), NeedsRecovery(h, p));
    }

    // C++: TownAftercare.cpp:431-435 (Port: a score the engine cannot compute, NaN, reads as no need)
    static float NativeAidNeed(ITownAftercareHost<TId> h, TId p, bool robot)
    {
        bool medical = !h.IsNull(p) && h.Valid(p) && !h.IsDead(p) && h.HasMedical(p);
        float score = medical ? h.AidScore(p, robot) : 0;
        return score > 0 ? score : 0;
    }

    // C++: TownAftercare.cpp:436-438
    static float AidRemaining(ITownAftercareHost<TId> h, TId p) => NativeAidNeed(h, p, false) + NativeAidNeed(h, p, true);

    // C++: TownAftercare.cpp:439-442
    static bool HasAidKit(ITownAftercareHost<TId> h, TId c, bool robot) => !h.IsNull(c) && h.Valid(c) && h.HasAidKit(c, robot);

    // C++: TownAftercare.cpp:483-492
    static string IntentName(Kind kind) => kind switch
    {
        Kind.Gather => "gather", Kind.Wait => "wait", Kind.Approach => "approach",
        Kind.ApproachCarry => "approach-pickup", Kind.WalkBed => "walk-bed",
        Kind.Bandage => "bandage", Kind.Repair => "repair", Kind.PickUp => "pickup",
        Kind.Deliver => "deliver", Kind.GoHospital => "hospital", Kind.Release => "release",
        _ => "putdown",
    };

    // C++: TownAftercare.cpp:493-502
    static string ActionName(Act action) => action switch
    {
        Act.ApproachPickup => "approach-pickup", Act.WalkToBed => "walk-bed",
        Act.Treat => "treat", Act.WaitForBed => "wait-bed", Act.PickUp => "pickup",
        Act.Deliver => "deliver", Act.Gather => "gather", Act.Hold => "hold",
        Act.ReturnHospital => "hospital", Act.ApproachAid => "approach",
        _ => "done",
    };

    // C++: TownAftercare.cpp:503-513 (every Meitou character has an order queue: no "no-order-receiver")
    static string MedicBlocker(ITownAftercareHost<TId> h, TId c)
    {
        if (h.IsNull(c) || !h.Valid(c)) return "invalid";
        if (Player(h, c)) return "player-owned";
        if (h.IsDead(c)) return "dead";
        if (h.IsUnconscious(c)) return "unconscious";
        if (h.IsCarried(c)) return "being-carried";
        if (h.InCombatMode(c)) return "combat";
        if (h.NeedsStabilization(c)) return "needs-aid";
        return "ready";
    }

    /// <summary>The medic's treatment trace slot (-1: none).</summary>
    int TraceSlot(TId c)
    {
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < treatmentTraces.Length; ++i) if (treatmentTraces[i].Used && eq.Equals(treatmentTraces[i].Actor, c)) return i;
        return -1;
    }

    // C++: TownAftercare.cpp:514-579. Port: owned = the medic table; nativeGoal = HasAiGoal; preventedFallbacks and the callback line's orderChecks /
    // nativeHandled have no source (task 8.4a) and print 0.
    void TraceMedic(ITownAftercareHost<TId> h, ref Medic m, int slot, float elapsed, string phase, Kind? intentKind)
    {
        if (elapsed >= m.LastTrace && elapsed - m.LastTrace < 5) return;
        m.LastTrace = elapsed;
        TId c = m.Actor;
        if (h.IsNull(c) || !h.Valid(c)) return;
        TId p = m.Patient;
        bool hasP = !h.IsNull(p) && h.Valid(p);
        bool owned = ExistingReceiver(c) is { } r && reservations.Contains(r);
        AftercareBed<TId>? targetBed = HospitalBed(h, m.Bed);
        Vector3 cpos = h.Position(c);
        float patientDistance = hasP ? (cpos - h.Position(p)).Length() : -1.0f;
        float bedDistance = targetBed is { } tb ? (cpos - tb.Position).Length() : -1.0f;
        float progressAge = m.Command.Issued ? elapsed - m.Command.LastProgress : 0.0f;
        bool moving = h.TryGetMoveDestination(c, out _);
        Vector3 dest = Destination(h, c);
        h.Debug("Proving Grounds: aftercare medic slot=" + N(slot + 1) + " name=" + h.Name(c) + " actor=" + h.ActorText(m.Actor) +
            " phase=" + phase + " elapsed=" + F1(elapsed) + " intent=" + (intentKind is { } k ? IntentName(k) : "standby") +
            " action=" + ActionName(m.Command.Action) + " issued=" + B(m.Command.Issued) + " progressAge=" + F1(progressAge) +
            " bestDistance=" + F1(m.Command.BestDistance) + " available=" + B(Available(h, c)) + " blocker=" + MedicBlocker(h, c) +
            " owned=" + B(owned) + " nativeGoal=" + B(h.HasAiGoal(c)) + " preventedFallbacks=0" + " moving=" + B(moving) +
            " carrying=" + B(h.IsCarrying(c)) + " assignedCarry=" + B(hasP && EqualityComparer<TId>.Default.Equals(h.CarryingOf(c), p)) +
            " patient=" + (hasP ? h.Name(p) : "none") + " patientId=" + h.ActorText(m.Patient) + " patientDistance=" + F1(patientDistance) +
            " patientKO=" + B(hasP && h.IsUnconscious(p)) + " aid=" + F3(AidRemaining(h, hasP ? p : none)) +
            " bedResolved=" + B(targetBed is not null) + " bedOccupied=" + B(targetBed is not null && Occupied(h, targetBed)) +
            " bedDistance=" + F1(bedDistance) + " reservedBed=" + N((uint)(coordinator.ReservedBed(slot) + 1)) +
            " bandages=" + B(HasAidKit(h, c, false)) + " repairKit=" + B(HasAidKit(h, c, true)) +
            " position=" + V(cpos) + " destination=" + V(dest));
        TreatmentTrace trace = new() { Actor = none, Patient = none };
        int ts = TraceSlot(c);
        if (ts >= 0) { trace = treatmentTraces[ts]; treatmentTraces[ts].ClearSamples(none); }
        if (hasP)
        {
            h.Debug("Proving Grounds: aftercare medic callbacks slot=" + N(slot + 1) + " firstAid=" + N(trace.FirstAid) + " doctoring=" + N(trace.Doctoring) +
                " blocked=" + N(trace.Blocked) + " complete=" + N(trace.Completed) + " aidTime=" + F3(trace.AidTime) + " doctorTime=" + F3(trace.DoctorTime) +
                " lastPatientMatches=" + B(trace.Used && EqualityComparer<TId>.Default.Equals(trace.Patient, p)) + " orderChecks=0 nativeHandled=0");
            bool medical = h.HasMedical(p);
            float fleshGap = 0, repairGap = 0;
            if (medical)
            {
                var parts = h.Parts(p);
                for (int i = 0; i < parts.Count; ++i)
                {
                    var part = parts[i];
                    if (part.Dead) continue;
                    float gap = part.Max - part.Flesh - part.Bandage;
                    if (gap > 0) { if (part.Robotic) repairGap += gap; else fleshGap += gap; }
                }
            }
            // tryToGetCurrentGoal searches goal arrays, not explicit orders.
            // A null result is not evidence that native treatment has stopped.
            h.Debug("Proving Grounds: aftercare medic aid slot=" + N(slot + 1) + " patientId=" + h.ActorText(m.Patient) +
                " flesh=" + F3(medical ? h.AidScore(p, false) : 0) + " repair=" + F3(medical ? h.AidScore(p, true) : 0) +
                " bandages=" + B(HasAidKit(h, c, false)) + " repairKit=" + B(HasAidKit(h, c, true)) + " distance=" + F1((cpos - h.Position(p)).Length()) +
                " nativeGoalArrayMatch=" + B(h.HasAiGoal(c)) + " fleshGap=" + F3(fleshGap) + " repairGap=" + F3(repairGap) +
                " bleed=" + F3(medical ? h.BleedRate(p) : 0) + " wounds=" + N(medical ? h.WoundCount(p) : 0));
        }
    }

    // C++: TownAftercare.cpp:582-584
    static TownMedicCoordinator.Point Point(Vector3 p) => new(p.X, p.Y, p.Z);
    // C++: TownAftercare.cpp:585
    static Vector3 Position(in TownMedicCoordinator.Point p) => new(p.X, p.Y, p.Z);

    // C++: TownAftercare.cpp:586-595 (the two overloads, RootObject* and hand, are one: a character id)
    int PatientIndex(ITownAftercareHost<TId> h, TId actor)
    {
        if (h.IsNull(actor)) return -1;
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < patients.Count; ++i) if (eq.Equals(patients[i].Actor, actor)) return i;
        return -1;
    }

    // C++: TownAftercare.cpp:596-640
    int AdmitArenaStragglers(ITownAftercareHost<TId> h)
    {
        if (arenaSite is not { } site || h.Resolve(site) is not { } arena || patients.Count >= TownMedicCoordinator.MaxPatients) return 0;
        // The full-size pit's combat layout reaches 155 units from centre.
        // Leave a small margin for bodies that fall against the arena edge.
        const float sweepRadius = 175.0f;
        var nearby = h.CharactersInSphere(arena.Building.Position, sweepRadius, 256) ?? [];
        int admitted = 0;
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < nearby.Count && patients.Count < TownMedicCoordinator.MaxPatients; ++i)
        {
            TId fighter = nearby[i];
            if (h.IsNull(fighter) || !h.Valid(fighter)) continue;
            if (h.IsDead(fighter) || !h.IsUnconscious(fighter) || h.InBed(fighter) || h.IsCarried(fighter) ||
                TownArenaRuntimePolicy.IsMedic(h.RecordId(fighter))) continue;
            bool known = PatientIndex(h, fighter) >= 0;
            for (int medic = 0; !known && medic < medicCount; ++medic)
                known = eq.Equals(medics[medic].Actor, fighter);
            if (known) continue;
            var patient = new Patient(fighter) { EarlyAdmitted = true, SweepAdmitted = true };
            patients.Add(patient);
            ++admitted;
            h.Debug("Proving Grounds: final arena sweep admitted name=" + h.Name(fighter) + " actor=" + h.ActorText(patient.Actor) +
                " slot=" + N((uint)patients.Count) + " position=" + V(h.Position(fighter)));
        }
        h.Debug("Proving Grounds: final arena sweep care=" + N(careId) + " candidates=" + N((uint)nearby.Count) + " admitted=" + N(admitted) +
            " radius=" + F1(sweepRadius));
        return admitted;
    }

    // C++: TownAftercare.cpp:641-697
    TownMedicCoordinator.Observation Observe(ITownAftercareHost<TId> h, bool eliminatedOnly = false)
    {
        var o = new TownMedicCoordinator.Observation();
        o.MedicCount = medicCount;
        o.PatientCount = patients.Count;
        o.BedCount = beds.Count < TownMedicCoordinator.MaxBeds ? beds.Count : TownMedicCoordinator.MaxBeds;
        for (int i = 0; i < o.MedicCount; ++i)
        {
            TId c = medics[i].Actor;
            if (h.IsNull(c) || !h.Valid(c)) continue;
            ref var m = ref o.Medics[i];
            m.Ready = !coordinator.MedicDone(i) && Available(h, c); m.Position = Point(h.Position(c));
            m.Bandages = HasAidKit(h, c, false); m.Repairs = HasAidKit(h, c, true);
            if (h.IsCarrying(c))
            {
                m.Carrying = PatientIndex(h, h.CarryingOf(c));
                if (m.Carrying < 0) { m.Carrying = -2; m.Ready = false; }
            }
        }
        for (int i = 0; i < o.PatientCount; ++i)
        {
            TId c = patients[i].Actor;
            ref var p = ref o.Patients[i];
            p.Eligible = !eliminatedOnly || patients[i].EarlyAdmitted;
            if (h.IsNull(c) || !h.Valid(c) || h.IsDead(c)) continue;
            p.Valid = true; p.Hospital = NeedsHospital(h, c);
            p.Position = Point(h.Position(c)); p.InBed = h.InBed(c);
            // A downed fighter can retain a stale native combat flag after an
            // older bout. Final-sweep patients are safe to transport regardless.
            p.Combat = !patients[i].SweepAdmitted && h.InCombatMode(c);
            p.Robot = h.IsRobot(c);
            p.ExternalCarry = h.IsCarried(c);
            for (int j = 0; j < o.MedicCount; ++j) if (o.Medics[j].Carrying == i) p.ExternalCarry = false;
            p.Flesh = NativeAidNeed(h, c, false);
            p.Repair = NativeAidNeed(h, c, true);
            p.Bleed = h.HasMedical(c) ? h.BleedRate(c) : 0;
            p.CanWalk = !Player(h, c) && !h.IsUnconscious(c) && !h.IsCrippled(c) && !h.IsCarried(c) && !h.IsCarrying(c);
            p.Delivered = deliveries.Completed(ExistingReceiver(c));
        }
        for (int i = 0; i < o.BedCount; ++i)
        {
            if (HospitalBed(h, beds[i]) is not { } bed) continue;
            ref var b = ref o.Beds[i];
            b.Available = !Occupied(h, bed);
            b.Robot = bed.Function == SkeletonBedFunction;
            b.Occupant = PatientIndex(h, bed.Occupant);
            b.Position = Point(bed.Position);
        }
        if (returnHospital is { } rh && h.Resolve(rh) is { } hospital)
        {
            o.HospitalAvailable = true; o.Hospital = Point(hospital.Building.Position);
            for (int i = 0; i < o.BedCount; ++i) if (HospitalBed(h, beds[i]) is not null)
            {
                o.Hospital = o.Beds[i].Position; break;
            }
        }
        return o;
    }

    // C++: TownAftercare.cpp:698-707
    static string PhaseName(TownMedicCoordinator.Phase phase) => phase switch
    {
        TownMedicCoordinator.Phase.Care => "care",
        TownMedicCoordinator.Phase.Gathering => "gather",
        TownMedicCoordinator.Phase.Treatment => "treatment",
        TownMedicCoordinator.Phase.Transport => "transport",
        TownMedicCoordinator.Phase.Returning => "return",
        _ => "complete",
    };

    // C++: TownAftercare.cpp:708-718
    static string PatientState(in TownMedicCoordinator.PatientObservation p, bool done)
    {
        if (done) return "done";
        if (!p.Eligible) return "not-eliminated-still-in-match";
        if (!p.Valid) return "invalid-or-dead";
        if (p.Combat) return "combat";
        if (p.Flesh > .01f || p.Repair > .01f) return "needs-aid";
        if (p.ExternalCarry) return "external-carry";
        if (p.InBed || p.Delivered) return "delivered";
        if (p.Hospital) return "needs-transport";
        return "waiting";
    }

    // C++: TownAftercare.cpp:719-777
    void TracePatient(ITownAftercareHost<TId> h, ref Patient patient, int index, in TownMedicCoordinator.Observation o, float elapsed, bool force)
    {
        if (!force && elapsed >= patient.LastTrace && elapsed - patient.LastTrace < 5) return;
        patient.LastTrace = elapsed;
        TId c = patient.Actor;
        ref readonly var observed = ref o.Patients[index];
        bool done = coordinator.PatientDone(index);
        int assignments = 0;
        string firstIntent = "none";
        for (int i = 0; i < o.MedicCount; ++i)
        {
            ref readonly var intent = ref coordinator.Command(i);
            if (intent.Patient == index || o.Medics[i].Carrying == index)
            {
                if (assignments == 0) firstIntent = IntentName(intent.Kind);
                ++assignments;
            }
        }
        bool owned = patient.Reserved && ExistingReceiver(c) is { } r && patientReservations.Contains(r);
        if (h.IsNull(c) || !h.Valid(c))
        {
            h.Debug("Proving Grounds: aftercare fighter slot=" + N(index + 1) + " actor=" + h.ActorText(patient.Actor) + " elapsed=" + F1(elapsed) +
                " state=" + PatientState(observed, done) + " valid=0 done=" + B(done) + " directed=" + B(patient.Directed) + " owned=" + B(owned) +
                " assignments=" + N(assignments) + " firstIntent=" + firstIntent);
            return;
        }
        bool medical = h.HasMedical(c);
        bool recoveryRead = h.RosterRead(c, out var recovery);
        var (hold, passive) = h.StandingOrders(c);
        h.Debug("Proving Grounds: aftercare fighter slot=" + N(index + 1) + " name=" + h.Name(c) + " actor=" + h.ActorText(patient.Actor) +
            " elapsed=" + F1(elapsed) + " state=" + PatientState(observed, done) + " done=" + B(done) + " winner=" + B(patient.Winner) +
            " player=" + B(Player(h, c)) + " directed=" + B(patient.Directed) + " owned=" + B(owned) + " assignments=" + N(assignments) +
            " firstIntent=" + firstIntent + " dead=" + B(h.IsDead(c)) + " ko=" + B(h.IsUnconscious(c)) + " combat=" + B(h.InCombatMode(c)) +
            " carried=" + B(h.IsCarried(c)) + " externalCarry=" + B(observed.ExternalCarry) + " inBed=" + B(observed.InBed) +
            " delivered=" + B(observed.Delivered) + " hospital=" + B(observed.Hospital) + " robot=" + B(observed.Robot) +
            " hold=" + B(hold) + " passive=" + B(passive) + " aidFlesh=" + F3(observed.Flesh) + " aidRepair=" + F3(observed.Repair) +
            " bleed=" + F3(medical ? h.BleedRate(c) : 0.0f) + " wounds=" + N(medical ? h.WoundCount(c) : 0) +
            " blood=" + F3(recoveryRead ? recovery.Blood : -1.0f) + " lowestLimb=" + F3(recoveryRead ? recovery.LowestLimb : -1.0f) +
            " recovery=" + F3(recoveryRead ? recovery.Recovery : -1.0f) + " recoveryRead=" + B(recoveryRead) + " nativeGoal=" + B(h.HasAiGoal(c)) +
            " bedWait=" + F1(coordinator.BedWait(index)) + " waitExpired=" + B(coordinator.WaitExpired(index)) + " position=" + V(h.Position(c)));
        bool moving = h.TryGetMoveDestination(c, out _);
        h.Debug("Proving Grounds: aftercare patient motion care=" + N(careId) + " patient=" + h.ActorText(patient.Actor) +
            " canWalk=" + B(observed.CanWalk) + " moving=" + B(moving) + " destination=" + V(Destination(h, c)) +
            " filteredFlesh=" + F5(h.ActionableAidNeed(c, false)) + " filteredRepair=" + F5(h.ActionableAidNeed(c, true)));
    }

    // C++: TownAftercare.cpp:813-842
    void TraceSummary(ITownAftercareHost<TId> h, in TownMedicCoordinator.Observation o, float elapsed, string? outcome, bool force)
    {
        TraceBeds(h, o, elapsed, force);
        if (!force && elapsed >= lastSummaryTrace && elapsed - lastSummaryTrace < 5) return;
        lastSummaryTrace = elapsed;
        int pending = 0, aid = 0, transport = 0, invalid = 0, combat = 0, delivered = 0, deferred = 0;
        for (int i = 0; i < o.PatientCount; ++i)
        {
            ref readonly var p = ref o.Patients[i];
            if (!p.Eligible) { ++deferred; continue; }
            if (!p.Valid) ++invalid;
            if (p.Combat) ++combat;
            if (p.Delivered || p.InBed) ++delivered;
            if (!coordinator.PatientDone(i))
            {
                ++pending;
                if (p.Flesh > .01f || p.Repair > .01f) ++aid;
                else if (p.Hospital && !p.Delivered && !p.InBed) ++transport;
            }
        }
        int ready = 0, idle = 0, carrying = 0;
        for (int i = 0; i < o.MedicCount; ++i)
        {
            if (o.Medics[i].Ready) ++ready;
            if (o.Medics[i].Carrying >= 0) ++carrying;
            ref readonly var intent = ref coordinator.Command(i);
            if (o.Medics[i].Ready && o.Medics[i].Carrying < 0 && (intent.Kind == Kind.Wait || intent.Kind == Kind.Release)) ++idle;
        }
        int freeBeds = 0;
        for (int i = 0; i < o.BedCount; ++i) if (o.Beds[i].Available) ++freeBeds;
        h.Debug("Proving Grounds: aftercare summary care=" + N(careId) + " phase=" + PhaseName(coordinator.GetPhase()) + " elapsed=" + F1(elapsed) +
            " outcome=" + (outcome ?? "heartbeat") + " patients=" + N(o.PatientCount) + " notAdmitted=" + N(deferred) + " pending=" + N(pending) +
            " aidPending=" + N(aid) + " transportPending=" + N(transport) + " delivered=" + N(delivered) + " invalid=" + N(invalid) +
            " combat=" + N(combat) + " medics=" + N(o.MedicCount) + " ready=" + N(ready) + " idle=" + N(idle) + " carrying=" + N(carrying) +
            " beds=" + N(o.BedCount) + " freeBeds=" + N(freeBeds) + " missingBed=" + B(coordinator.MissingBed()) +
            " bedWaits=" + N(coordinator.ExpiredWaits()) + " preventedFallbacks=0");
    }

    // C++: TownAftercare.cpp:843-851
    static string OutcomeName(TownMedicCoordinator.Outcome outcome) => outcome switch
    {
        TownMedicCoordinator.Outcome.Pending => "pending", TownMedicCoordinator.Outcome.Treated => "treated",
        TownMedicCoordinator.Outcome.Delivered => "delivered", TownMedicCoordinator.Outcome.Recovered => "recovered",
        TownMedicCoordinator.Outcome.Unavailable => "dead-or-unavailable", _ => "unresolved-handoff",
    };

    // C++: TownAftercare.cpp:852-858
    void LogOutcome(ITownAftercareHost<TId> h, int p, float elapsed, string reason) =>
        h.Debug("Proving Grounds: aftercare outcome care=" + N(careId) + " elapsed=" + F1(elapsed) + " patient=" + h.ActorText(patients[p].Actor) +
            " outcome=" + OutcomeName(coordinator.PatientOutcome(p)) + " reason=" + reason);

    // C++: TownAftercare.cpp:863-1033. The orders through the host's adapters (docs/divergences.md "TownAftercare (task 8.4b)"): FIRST_AID_ORDER /
    // FIRST_AID_ROBOT = StartTreat, LIFT_PERSON_PLAYER_ORDER = BeginLift, PUT_SOMEONE_IN_BED = PutInBed with the coordinator's bed, USE_BED_ORDER =
    // UseBed for the patient, MOVE_CUS_ORDERED (+ both setDestination calls) = MoveTo; the native order line keeps the C++'s task numbers.
    void Execute(ITownAftercareHost<TId> h, ref Medic m, int slot, TownMedicCoordinator.Intent intent, in TownMedicCoordinator.Observation o, float elapsed)
    {
        var eq = EqualityComparer<TId>.Default;
        TId c = m.Actor;
        // Remember actual carried patients even when the assignment was revoked.
        if (!h.IsNull(c) && h.Valid(c) && h.IsCarrying(c) && PatientIndex(h, h.CarryingOf(c)) >= 0)
            m.Patient = h.CarryingOf(c);
        if (intent.Kind == Kind.PutDown) { DropPatient(h, ref m); m.Command.Reset(); return; }
        if (intent.Kind == Kind.GoHospital && Available(h, c) && !h.IsCarrying(c) && o.HospitalAvailable)
        {
            // Remove our ownership before issuing the final native move. The
            // subsequent bout cleanup must not cancel this return order.
            ReleaseMedic(h, ref m, false);
            Vector3 hospitalDestination = Position(o.Hospital);
            h.MoveTo(c, hospitalDestination);
            h.Debug("Proving Grounds: aftercare hospital return care=" + N(careId) + " slot=" + N(slot + 1) + " actor=" + h.ActorText(m.Actor) +
                " destination=" + V(hospitalDestination) + " distance=" + F1((h.Position(c) - hospitalDestination).Length()) + " nativeHandoff=1");
            NoteOrder(ref m, "hospital");
            return;
        }
        if (intent.Kind == Kind.Release || intent.Kind == Kind.GoHospital || !Available(h, c))
        {
            if (m.Directed)
                h.Debug("Proving Grounds: aftercare native release care=" + N(careId) + " slot=" + N(slot + 1) + " actor=" + h.ActorText(m.Actor) +
                    " reason=" + (Available(h, c) ? "no-active-assignment" : MedicBlocker(h, c)));
            ReleaseMedic(h, ref m); return;
        }
        TId patient = intent.Patient >= 0 ? patients[intent.Patient].Actor : none;
        string? bed = intent.Bed >= 0 ? beds[intent.Bed] : null;
        bool changed = !eq.Equals(m.Patient, patient) || m.Bed != bed || m.LastKind != intent.Kind;
        if (changed)
        {
            h.Debug("Proving Grounds: aftercare assignment care=" + N(careId) + " slot=" + N(slot + 1) + " patient=" + h.ActorText(patient) +
                " bed=" + BedText(bed) + " from=" + IntentName(m.LastKind) + " to=" + IntentName(intent.Kind) + " elapsed=" + F1(elapsed));
            if (intent.Patient >= 0)
                h.Debug("Proving Grounds: aftercare assignment role care=" + N(careId) + " slot=" + N(slot + 1) + " patient=" + h.ActorText(patient) +
                    " role=" + (coordinator.IsHelping(slot) ? "treatment-helper" : "primary-care-and-transfer"));
            m.Command.Reset();
        }
        m.Patient = patient; m.Bed = bed; m.LastKind = intent.Kind;
        if (!Direct(h, ref m)) return;   // port: the hold could not be taken (logged once)
        TId p = patient;
        bool hasP = !h.IsNull(p) && h.Valid(p);
        AftercareBed<TId>? targetBed = HospitalBed(h, bed);
        if (intent.Kind != Kind.Wait && !hasP) return;
        if ((intent.Kind == Kind.Deliver || intent.Kind == Kind.WalkBed) &&
            (targetBed is not { } tb || !Compatible(tb, h.IsRobot(p)) || (Occupied(h, tb) && !eq.Equals(tb.Occupant, p))))
        {
            m.Command.Reset(); return;   // observation will reassign this bed next tick
        }
        bool walking = intent.Kind == Kind.WalkBed;
        Vector3 destination = hasP ? h.Position(p) : h.Position(c);
        if (intent.Kind == Kind.Deliver || walking) destination = targetBed!.Value.Position;
        float distance = (h.Position(walking ? p : c) - destination).Length();
        Act action = Act.Hold;
        switch (intent.Kind)
        {
            case Kind.Bandage: case Kind.Repair: action = Act.Treat; break;
            case Kind.ApproachCarry: action = Act.ApproachPickup; break;
            case Kind.PickUp: action = Act.PickUp; break;
            case Kind.Deliver: action = Act.Deliver; break;
            case Kind.WalkBed: action = Act.WalkToBed; break;
        }
        bool issued = m.Command.Issued;
        float previousProgress = m.Command.LastProgress;
        // Only the selected treatment mode contributes medical progress.
        float aid = intent.Kind == Kind.Bandage ? NativeAidNeed(h, p, false) :
            intent.Kind == Kind.Repair ? NativeAidNeed(h, p, true) : 0;
        if (action == Act.Treat)
        {
            // An executing native treatment is progress even if its cached score
            // is updated less frequently. Completed/blocked callbacks do not count.
            int ts = TraceSlot(c);
            if (ts >= 0)
            {
                ref readonly var trace = ref treatmentTraces[ts];
                if (eq.Equals(trace.Patient, p) && trace.WorkTicks != m.LastWorkTicks && m.Command.Issued)
                    m.Command.LastProgress = elapsed;
                m.LastWorkTicks = trace.WorkTicks;
            }
        }
        if (!m.Command.Update(action, elapsed, distance, aid)) return;
        if (m.Command.StalledRetries >= 2 && intent.Patient >= 0)
        {
            h.Debug("Proving Grounds: aftercare action failed care=" + N(careId) + " slot=" + N(slot + 1) + " patient=" + h.ActorText(patient) +
                " action=" + IntentName(intent.Kind) + " retries=" + N(m.Command.StalledRetries) + " progressAge=" + F1(elapsed - previousProgress) +
                " distance=" + F1(distance) + " aid=" + F4(aid) + " reason=no-observed-progress");
            coordinator.Fail(slot, intent.Patient, intent.Kind);
            // Another medic may still be treating this patient. A failed helper
            // must not restore/cancel the patient's shared temporary care state.
            if (!coordinator.HasPatientWork(intent.Patient))
            {
                var failedPatient = patients[intent.Patient];
                RestorePatient(h, ref failedPatient);
                patients[intent.Patient] = failedPatient;
            }
            ReleaseMedic(h, ref m); return;
        }
        if (issued && hasP)
        {
            bool medical = h.HasMedical(p);
            h.Debug("Proving Grounds: aftercare stall snapshot care=" + N(careId) + " patient=" + h.ActorText(patient) + " action=" + IntentName(intent.Kind) +
                " nativeFlesh=" + F5(NativeAidNeed(h, p, false)) + " nativeRepair=" + F5(NativeAidNeed(h, p, true)) +
                " filteredFlesh=" + F5(h.ActionableAidNeed(p, false)) + " filteredRepair=" + F5(h.ActionableAidNeed(p, true)) +
                " bleed=" + F5(medical ? h.BleedRate(p) : 0) + " wounds=" + N(medical ? h.WoundCount(p) : 0) +
                " patientMoving=" + B(h.TryGetMoveDestination(p, out _)) + " patientKO=" + B(h.IsUnconscious(p)) + " patientCrippled=" + B(h.IsCrippled(p)) +
                " bed=" + BedText(bed) + " occupied=" + B(targetBed is not null && Occupied(h, targetBed)));
            if (medical)
            {
                var parts = h.Parts(p);
                for (int i = 0; i < parts.Count; ++i)
                {
                    var part = parts[i];
                    h.Debug("Proving Grounds: aftercare stall limb care=" + N(careId) + " patient=" + h.ActorText(patient) + " part=" + N((uint)i) +
                        " robotic=" + B(part.Robotic) + " dead=" + B(part.Dead) + " flesh=" + F4(part.Flesh) + " stun=" + F4(part.Stun) +
                        " bandaging=" + F4(part.Bandage) + " max=" + F4(part.Max));
                }
            }
        }
        // Goals are prepared only at an actual transition or bounded retry.
        Reassert(h, ref m);
        int task = intent.Kind switch
        {
            Kind.Bandage => FirstAidOrder, Kind.Repair => FirstAidRobot, Kind.PickUp => LiftPersonPlayerOrder,
            Kind.Deliver => PutSomeoneInBed, Kind.WalkBed => UseBedOrder, _ => MoveCusOrdered,
        };
        h.Debug("Proving Grounds: aftercare native order care=" + N(careId) + " slot=" + N(slot + 1) + " medic=" + h.ActorText(m.Actor) +
            " patient=" + h.ActorText(patient) + " bed=" + BedText(bed) + " action=" + IntentName(intent.Kind) + " task=" + N(task) +
            " executor=" + (walking ? "patient" : "medic") + " trigger=" + (issued ? "stalled-retry" : "transition") + " elapsed=" + F1(elapsed) +
            " distance=" + F1(distance) + " aid=" + F4(aid) + " retries=" + N(m.Command.StalledRetries) +
            " previousProgressAge=" + F1(issued ? elapsed - previousProgress : 0));
        if (intent.Kind == Kind.Wait)
        {
            // Hold only the reserved medic while a compatible bed is unavailable.
            h.MoveTo(c, h.Position(c));
            NoteOrder(ref m, "wait");
            return;
        }
        if (walking || intent.Kind == Kind.Deliver) Watch(h, p, bed!);
        bool ok;
        if (walking)
        {
            h.MoveTo(c, h.Position(c));
            ok = h.UseBed(p, bed!);
        }
        else
        {
            Run(h, c);
            switch (intent.Kind)
            {
                case Kind.Bandage: case Kind.Repair: ok = h.StartTreat(c, p, intent.Kind == Kind.Repair); break;
                case Kind.PickUp: ok = h.BeginLift(c, p); break;
                case Kind.Deliver: ok = h.PutInBed(c, bed!); break;
                default: h.MoveTo(c, destination); ok = true; break;   // ApproachCarry (and any other walk): MOVE_CUS_ORDERED to the patient
            }
        }
        NoteOrder(ref m, IntentName(intent.Kind) + (ok ? " ok" : " refused"));
    }

    /// <summary>Port only (the test verb): the medic's last orders, newest last, at most six ("bandage ok; pickup ok; deliver ok").</summary>
    static void NoteOrder(ref Medic m, string order)
    {
        string all = m.LastOrder.Length == 0 ? order : m.LastOrder + "; " + order;
        var parts = all.Split("; ");
        m.LastOrder = parts.Length <= 6 ? all : string.Join("; ", parts, parts.Length - 6, 6);
    }

    /// <summary>C++: TownAftercare.cpp:1293-1401 (<see cref="TreatOnce"/>). Port only: a throw (a host read or order failing) is logged once per care
    /// session and answers false, as a completed care, so TownArena's Finish releases everything through Release; a throw repeated every poll would
    /// otherwise hold the event in Aftercare for good (the C++ has no exceptions). The status keeps the last C++ text.</summary>
    public bool Treat(ITownAftercareHost<TId> h, float elapsed, bool eliminatedOnly)
    {
        try { return TreatOnce(h, elapsed, eliminatedOnly); }
        catch (Exception e)
        {
            ++treatFailures;
            if (!treatFailureLogged)
            {
                treatFailureLogged = true;
                h.Debug("Proving Grounds: aftercare treatment failed care=" + N(careId) + " error=" + e.GetType().Name + ": " + e.Message +
                    "; remaining care returned to town AI");
            }
            return false;
        }
    }

    bool treatFailureLogged;
    int treatFailures;
    /// <summary>Port only (task 8.4c quality review): Treat calls that threw in this world (F10 and pg town care).</summary>
    public int TreatFailures => treatFailures;

    // C++: TownAftercare.cpp:1293-1401
    bool TreatOnce(ITownAftercareHost<TId> h, float elapsed, bool eliminatedOnly)
    {
        // Only the sequential town mode calls this during combat. Admission is
        // permanent for the bout: waking up never returns a KO'd fighter to play.
        eliminatedOnly = eliminatedOnly && h.SparTeams1v1Active;
        if (eliminatedOnly)
        {
            for (int i = 0; i < patients.Count; ++i)
            {
                TId c = patients[i].Actor;
                if (patients[i].EarlyAdmitted || !h.SparIsEliminated(c)) continue;
                var admitted = patients[i]; admitted.EarlyAdmitted = true; patients[i] = admitted;
                h.Debug("Proving Grounds: Teams 1v1 early care admitted patient=" + h.ActorText(patients[i].Actor));
            }
        }
        else if (postFightStart < 0) postFightStart = elapsed;
        PollBedEntries(h);   // port: the setBedMode hook's stand-in (TownAftercareLogic.Beds.cs)
        if (!eliminatedOnly && TownArenaRuntimePolicy.AftercareExpired(elapsed - postFightStart))
        {
            var timedOut = Observe(h);
            for (int i = 0; i < patients.Count; ++i)
            {
                var p = patients[i];
                TracePatient(h, ref p, i, timedOut, elapsed, true);
                patients[i] = p;
            }
            for (int i = 0; i < medicCount; ++i)
                TraceMedic(h, ref medics[i], i, elapsed, PhaseName(coordinator.GetPhase()), coordinator.Command(i).Kind);
            coordinator.HandoffPending(patients.Count);
            for (int i = 0; i < patients.Count; ++i)
                LogOutcome(h, i, elapsed, "bout-time-limit");
            TraceSummary(h, timedOut, elapsed, "timeout-unresolved-handoff", true);
            status = "Medical " + PhaseName(coordinator.GetPhase()) + " timed out; remaining care returned to town AI";
            return false;
        }
        var observation = Observe(h, eliminatedOnly);
        Span<TownMedicCoordinator.Outcome> previous = stackalloc TownMedicCoordinator.Outcome[TownMedicCoordinator.MaxPatients];
        for (int i = 0; i < patients.Count; ++i) previous[i] = coordinator.PatientOutcome(i);
        coordinator.Tick(observation, true, elapsed, eliminatedOnly);
        for (int i = 0; i < patients.Count; ++i)
        {
            int index = i;
            TId p = patients[i].Actor;
            var patient = patients[i];
            if (!observation.Patients[i].Eligible)
            {
                TracePatient(h, ref patient, index, observation, elapsed, false);
                patients[i] = patient;
                continue;
            }
            bool assigned = false, walking = false;
            for (int m = 0; m < medicCount; ++m)
            {
                ref readonly var intent = ref coordinator.Command(m);
                if (intent.Patient == index && intent.Kind != Kind.Wait && intent.Kind != Kind.Release && intent.Kind != Kind.PutDown)
                {
                    assigned = true; walking = intent.Kind == Kind.WalkBed;
                }
            }
            if (!assigned || coordinator.PatientDone(index) || observation.Patients[i].Combat)
            {
                RestorePatient(h, ref patient);
                patients[i] = patient;
            }
            else DirectPatient(h, p, !walking);
            patient = patients[i];
            bool changed = previous[i] != coordinator.PatientOutcome(index);
            if (changed) LogOutcome(h, index, elapsed, coordinator.WaitExpired(index) ? "bed-wait-expired" :
                coordinator.PatientOutcome(index) == TownMedicCoordinator.Outcome.Unresolved ?
                "no-capable-unfailed-medic" : "observed-patient-state");
            TracePatient(h, ref patient, index, observation, elapsed, changed);
            patients[i] = patient;
        }
        bool complete = coordinator.GetPhase() == TownMedicCoordinator.Phase.Complete;
        if (complete && !eliminatedOnly)
        {
            int admitted = AdmitArenaStragglers(h);
            if (admitted > 0)
            {
                coordinator.Reopen();
                postFightStart = elapsed;
                for (int i = 0; i < medicCount; ++i)
                {
                    if (!Available(h, medics[i].Actor)) continue;
                    medics[i].Patient = none; medics[i].Bed = null;
                    medics[i].LastKind = Kind.Gather;
                    medics[i].Command.Reset();
                    StageMedic(h, ref medics[i], i, elapsed);
                }
                status = "Aftercare final sweep: " + N(admitted) + " unconscious fighter" + (admitted == 1 ? "" : "s") + " awaiting hospital transfer";
                return true;
            }
        }
        TraceSummary(h, observation, elapsed, complete ? (coordinator.HasUnresolved() ? "unresolved-handoff" : "complete") : "heartbeat", complete);
        for (int i = 0; i < medicCount; ++i)
        {
            TownMedicCoordinator.Intent intent = coordinator.Command(i);
            // Spare medics remain ringside without interrupting an active
            // treatment/carry/bed order to re-run team-wide Standby.
            if (eliminatedOnly && (intent.Kind == Kind.Release ||
                (intent.Kind == Kind.Wait && intent.Patient < 0)) && observation.Medics[i].Ready &&
                observation.Medics[i].Carrying < 0)
            {
                if (medics[i].LastKind != Kind.Gather)
                {
                    medics[i].Patient = none; medics[i].Bed = null;
                    medics[i].LastKind = Kind.Gather; medics[i].Command.Reset();
                }
                StageMedic(h, ref medics[i], i, elapsed);
            }
            else Execute(h, ref medics[i], i, intent, observation, elapsed);
            TraceMedic(h, ref medics[i], i, elapsed, PhaseName(coordinator.GetPhase()), intent.Kind);
        }
        if (complete && !eliminatedOnly)
        {
            status = coordinator.HasUnresolved() ? "Arena care ended with unresolved patients; remaining care returned to town AI" : CareCompleteText;
            return false;
        }
        int treatmentPending = 0, transferPending = 0;
        for (int i = 0; i < observation.PatientCount; ++i)
        {
            if (!observation.Patients[i].Eligible || coordinator.PatientDone(i)) continue;
            ref readonly var patient = ref observation.Patients[i];
            if (patient.Flesh > .01f || patient.Repair > .01f) ++treatmentPending;
            else ++transferPending;
        }
        status = "Aftercare: " + N(treatmentPending) + " awaiting treatment; " + N(transferPending) + " awaiting hospital transfer" +
            (coordinator.MissingBed() ? "; waiting for a free compatible bed" : "");
        return true;
    }
}
