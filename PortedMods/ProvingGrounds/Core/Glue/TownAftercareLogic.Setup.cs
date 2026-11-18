using System.Globalization;
using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Task 8.4a (TW §2.9 "Setup at staging", "During the fight"): the medical team at staging, the hospital's beds, the adoption of beds already in use,
// and the standby line the medics hold through the fight. TownAftercare.cpp:168-218, :320-407, :443-482, :1155-1226, in the C++'s order.

public sealed partial class TownAftercareLogic<TId>
{
    /// <summary>The Scratch Hospital's record (TownAftercare.cpp:325, :1186).</summary>
    public const string HospitalRecord = PgRecords.ScratchHospital;
    /// <summary>BF_BED / BF_SKELETON_BED (Meitou's bed functionality <c>function</c> values, Fixtures.cs:80).</summary>
    public const int BedFunction = 6, SkeletonBedFunction = 25;

    // C++: TownAftercare.cpp:168-171
    static void Run(ITownAftercareHost<TId> h, TId c) => h.Run(c);

    // C++: TownAftercare.cpp:172-185. Port: the reservation's checkOrders hook is the hold registry's medic hold (spike S10); the treatment-trace
    // slot (:178-180, task 8.4b) is keyed by the medic (no receiver pointer).
    // Port: a hold the registry cannot take (Hold false: stale id, control unavailable, a throw) undoes the reservation as the patient's claim does
    // (DirectPatient): the medic is left undirected and skipped this poll (false), logged once per medic.
    bool Direct(ITownAftercareHost<TId> h, ref Medic m)
    {
        TId c = m.Actor;
        if (!m.Directed)
        {
            object? receiver = Receiver(h, c);
            reservations.Add(receiver);
            if (!h.Hold(c, HoldOwner.AftercareMedic))
            {
                reservations.Remove(receiver);
                if (!m.OwnershipFailedLogged)
                {
                    m.OwnershipFailedLogged = true;
                    h.Debug("Proving Grounds: aftercare medic ownership failed actor=" + h.ActorText(c));
                }
                return false;
            }
            for (int i = 0; i < treatmentTraces.Length; ++i) if (!treatmentTraces[i].Used)
            {
                treatmentTraces[i].Actor = c; treatmentTraces[i].Used = true; break;
            }
            m.Speed = h.MoveSpeed(c);
            m.Directed = true;
        }
        return true;
    }

    // C++: TownAftercare.cpp:186-192. TownMedicAIPolicy::PrepareOwnedOrder = clearAllAIGoals: the port re-asserts the medic's hold (Engaged).
    void Reassert(ITownAftercareHost<TId> h, ref Medic m)
    {
        TId c = m.Actor;
        if (h.IsNull(c) || !h.Valid(c)) return;
        if (!Direct(h, ref m)) return;
        h.Hold(c, HoldOwner.AftercareMedic);
    }

    // C++: TownAftercare.cpp:193-197 (dropCarriedObject(true, false): the host's DropCarried; a medic carries one of ours only from task 8.4b's
    // treatment on, which sets m.Patient)
    static void DropPatient(ITownAftercareHost<TId> h, ref Medic m)
    {
        TId c = m.Actor;
        if (!h.IsNull(c) && h.Valid(c) && !Player(h, c) && !h.IsNull(m.Patient) &&
            EqualityComparer<TId>.Default.Equals(h.CarryingOf(c), m.Patient))
            h.DropCarried(c);
    }

    /// <summary>Tests only: <see cref="DropPatient"/> on a copy of a medic (8.4b sets a medic's patient; before it the tests set it).</summary>
    internal static void DropPatientForTest(ITownAftercareHost<TId> h, ref Medic m) => DropPatient(h, ref m);

    // C++: TownAftercare.cpp:198-218 (ReleaseReceiver :116-122: the C++ receiver is set only by Direct, so a medic never directed has nothing in the
    // table). Port: the hold is given back for every directed medic, after the speed as the C++ order (the C++'s checkOrders hook stops at
    // ReleaseReceiver whatever the medic's state); the town AI's restart (ResumeNative) is the hold release's Ai.Replan, so the hand-off line reads
    // whether a goal is chosen right after it.
    void ReleaseMedic(ITownAftercareHost<TId> h, ref Medic m, bool resume = true)
    {
        TId c = m.Actor;
        if (m.Directed)
        {
            reservations.Remove(Receiver(h, c));
            // ReleaseReceiver :116-122: the medic's treatment trace slot emptied (task 8.4b)
            var eq = EqualityComparer<TId>.Default;
            for (int i = 0; i < treatmentTraces.Length; ++i)
                if (treatmentTraces[i].Used && eq.Equals(treatmentTraces[i].Actor, c)) treatmentTraces[i] = new TreatmentTrace { Actor = none, Patient = none };
        }
        bool own = m.Directed && !h.IsNull(c) && h.Valid(c) && !Player(h, c);
        if (own)
        {
            DropPatient(h, ref m);
            h.RestoreMoveSpeed(c, m.Speed);
        }
        if (m.Directed) h.ReleaseHold(c, HoldOwner.AftercareMedic);
        if (own)
        {
            if (resume && !h.IsDead(c) && !h.IsUnconscious(c) && !h.InCombatMode(c) && !h.IsCarrying(c))
                h.Debug("Proving Grounds: medic native handoff actor=" + h.ActorText(c) + " goalSelected=" + (h.HasAiGoal(c) ? "1" : "0") +
                    " scheduled=1");
            h.Debug("Proving Grounds: medic returned to town AI actor=" + h.ActorText(c));
        }
        m.Directed = false;
        m.Patient = none; m.Bed = null; m.Command.Reset();
    }

    /// <summary>C++: TownAftercare.cpp:320-331 HospitalBed. Meitou's town beds have no construction state, operator count or "disabled" switch
    /// (only power and reachability, FixtureRegistry.cs:1462): the complete / single-operator / enabled tests reduce to the bed fixture existing
    /// (docs/divergences.md "TownAftercare (task 8.4a)").</summary>
    public AftercareBed<TId>? HospitalBed(ITownAftercareHost<TId> h, string? id)
    {
        if (id is null || h.Bed(id) is not { } bed) return null;
        if (bed.Function != BedFunction && bed.Function != SkeletonBedFunction) return null;
        if (!TownArenaRuntimePolicy.IdEquals(bed.BuildingRecord, HospitalRecord)) return null;
        return bed;
    }

    // C++: TownAftercare.cpp:332-335
    public static bool Compatible(AftercareBed<TId>? bed, bool robot) =>
        bed is { } b && (robot ? b.Function == SkeletonBedFunction : b.Function == BedFunction);

    // C++: TownAftercare.cpp:336-339
    public bool Occupied(ITownAftercareHost<TId> h, AftercareBed<TId>? bed) =>
        bed is { } b && TownFurniturePolicy.Occupied(!h.IsNull(b.Occupant), !h.IsNull(b.Occupant), b.Occupied);

    // C++: TownAftercare.cpp:340-342
    public bool IsArenaHospitalBed(ITownAftercareHost<TId> h, string? id) => HospitalBed(h, id) is not null;

    // C++: TownAftercare.cpp:343-350
    void ForgetBedOccupant(TId actor)
    {
        var eq = EqualityComparer<TId>.Default;
        for (int i = bedOccupantCount - 1; i >= 0; --i)
            if (eq.Equals(bedOccupants[i].Actor, actor)) bedOccupants[i] = bedOccupants[--bedOccupantCount];
    }

    // C++: TownAftercare.cpp:351-370
    void RememberBedOccupant(ITownAftercareHost<TId> h, TId patient, string? bed)
    {
        if (h.IsNull(patient) || !h.Valid(patient) || bed is null || Player(h, patient)) return;
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < bedOccupantCount; ++i)
            if (eq.Equals(bedOccupants[i].Actor, patient))
            {
                // Moving to a different bed restarts the recovery clock.
                if (bedOccupants[i].Bed != bed)
                {
                    bedOccupants[i].Bed = bed;
                    bedOccupants[i].Entered = bedRecoveryElapsed;
                }
                return;
            }
        if (bedOccupantCount < TownMedicCoordinator.MaxBeds)
        {
            ref var entry = ref bedOccupants[bedOccupantCount++];
            entry.Actor = patient; entry.Bed = bed; entry.Entered = bedRecoveryElapsed; entry.LastAttempt = -1000;
        }
    }

    // C++: TownAftercare.cpp:403-407
    void DeferBedOccupant(TId actor, float attempt)
    {
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < bedOccupantCount; ++i)
            if (eq.Equals(bedOccupants[i].Actor, actor)) bedOccupants[i].LastAttempt = attempt;
    }

    // C++: TownAftercare.cpp:443-482
    void StageMedic(ITownAftercareHost<TId> h, ref Medic m, int slot, float elapsed)
    {
        TId c = m.Actor;
        if (!Available(h, c) || h.IsCarrying(c)) return;
        if (!Direct(h, ref m)) return;
        float offset = ((float)slot - ((float)medicCount - 1.0f) * .5f) * 16.0f;
        Vector3 point = standbyPoint + standbySide * offset;
        Vector3 delta = h.Position(c) - point;
        float distance = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        // Hysteresis keeps nearby medics from alternating between hold/move.
        bool arrived = distance <= (m.Command.Action == TownAftercarePolicy.Action.Hold ? 18.0f : 8.0f);
        TownAftercarePolicy.Action action = arrived ? TownAftercarePolicy.Action.Hold : TownAftercarePolicy.Action.Gather;
        if (elapsed < m.LastRecall || elapsed - m.LastRecall >= 1.0f)
        {
            // Port: Meitou's walk goal has no height (PgEngine.MoveDestination takes the character's own), so the logged error is that 3D length.
            bool moving = h.TryGetMoveDestination(c, out Vector3 destination);
            Vector3 error = destination - point;
            if (TownAftercarePolicy.StandbyOrderLost(moving, error.X * error.X + error.Z * error.Z))
            {
                m.Command.Reset();
                m.LastRecall = elapsed;
                if (elapsed < m.LastRecallLog || elapsed - m.LastRecallLog >= 30.0f)
                {
                    m.LastRecallLog = elapsed;
                    h.Debug("Proving Grounds: medic repeatedly recalled slot=" + (slot + 1).ToString(CultureInfo.InvariantCulture) +
                        " destinationError=" + CMath.FormatF(error.Length(), 1));
                }
            }
        }
        if (m.Command.Update(action, elapsed, distance, 0))
        {
            Reassert(h, ref m);
            c = m.Actor;
            if (h.IsNull(c) || !h.Valid(c)) return;
            // Match the working handler's movement handoff. Removing the move
            // job here handed an arrived medic straight back to their town AI.
            h.MoveTo(c, arrived ? h.Position(c) : point);
        }
        if (!arrived) Run(h, c);
    }

    // C++: TownAftercare.cpp:1155-1212
    public void Begin(ITownAftercareHost<TId> h, IReadOnlyList<TId> doctors, IReadOnlyList<TId> fighters, ArenaBuildingRef arena, Vector3 standby)
    {
        Release(h); careId = h.NextLogId(); standbyPoint = standby; arenaSite = arena;
        h.Debug("Proving Grounds: aftercare controller=native-v2; native medical scores, independent patients, no direct pickup fallback");
        PlacedBuilding? arenaNow = h.Resolve(arena);
        Vector3 arenaPos = arenaNow is { } ap ? ap.Building.Position : standby;   // the C++ arena is valid here (BeginEvent just resolved it)
        Vector3 direction = standby - arenaPos;
        direction.Y = 0;
        if (direction.LengthSquared() > 1.0f) direction = Vector3.Normalize(direction); else direction = new Vector3(1, 0, 0);
        standbySide = new Vector3(-direction.Z, 0, direction.X);
        int count = TownAftercarePolicy.RequiredMedics(fighters.Count);
        for (int i = 0; i < doctors.Count && medicCount < count; ++i)
        {
            var m = new Medic(doctors[i], none);
            medics[medicCount++] = m;
            h.Debug("Proving Grounds: medic reserved slot=" + (i + 1).ToString(CultureInfo.InvariantCulture) + " actor=" + h.ActorText(m.Actor));
        }
        for (int i = 0; i < fighters.Count && i < TownMedicCoordinator.MaxPatients; ++i)
        {
            var p = new Patient(fighters[i]);
            patients.Add(p);
            TId fighter = p.Actor;
            bool valid = !h.IsNull(fighter) && h.Valid(fighter);
            h.Debug("Proving Grounds: aftercare fighter registered slot=" + (i + 1).ToString(CultureInfo.InvariantCulture) +
                " name=" + (valid ? h.Name(fighter) : "invalid") + " actor=" + h.ActorText(p.Actor) +
                " player=" + (valid && Player(h, fighter) ? "1" : "0") + " valid=" + (valid ? "1" : "0"));
        }
        if (arenaNow is { } arenaPlaced && h.BuildingsInSphere(arenaPos, 2500, 1024) is { } nearby)
        {
            // Port: the C++ walks every building in the sphere, the hospital and each bed (a bed is a building in Kenshi, its parent the
            // hospital); Meitou's beds are fixtures of their building, so the beds come from each hospital 163 of the arena's town found here.
            for (int i = 0; i < nearby.Count; ++i)
            {
                var b = nearby[i];
                if (b.Town != arenaPlaced.Town || !TownArenaRuntimePolicy.IdEquals(ArenaIdentityLogic.GetStringId(b.Building), HospitalRecord)) continue;
                returnHospital = b.Building.Ref;
                var bedIds = h.BedsOf(b.Building);
                for (int k = 0; k < bedIds.Count; ++k)
                {
                    if (HospitalBed(h, bedIds[k]) is null) continue;
                    if (!beds.Contains(bedIds[k])) beds.Add(bedIds[k]);
                }
            }
        }
        h.Debug("Proving Grounds: medical team - " + medicCount.ToString(CultureInfo.InvariantCulture) + " medics, " +
            beds.Count.ToString(CultureInfo.InvariantCulture) + " hospital beds");
        // Adopt whoever is already lying in the hospital's beds: a patient from an
        // earlier bout (or an earlier build) whose care session is long gone still
        // owes the bed back once healed, and no session owns them any more.
        int occupiedBeds = 0, inBed = 0;
        for (int i = 0; i < beds.Count; ++i)
        {
            if (HospitalBed(h, beds[i]) is not { } bed) continue;
            TId occupant = bed.Occupant;
            if (h.IsNull(occupant) || !h.Valid(occupant) || !bed.Occupied) continue;
            ++occupiedBeds;
            if (!bed.OccupantInBed || Player(h, occupant)) continue;
            ++inBed;
            RememberBedOccupant(h, occupant, bed.Id);
        }
        h.Debug("Proving Grounds: aftercare bed adopt care=" + careId.ToString(CultureInfo.InvariantCulture) + " occupied=" +
            occupiedBeds.ToString(CultureInfo.InvariantCulture) + " owned=" + inBed.ToString(CultureInfo.InvariantCulture) + " tracked=" +
            bedOccupantCount.ToString(CultureInfo.InvariantCulture));
        Standby(h);
    }

    // C++: TownAftercare.cpp:1213-1226
    public void Standby(ITownAftercareHost<TId> h)
    {
        uint now = h.NowMs;
        float dt = lastStandby != 0 ? unchecked(now - lastStandby) / 1000.0f : 0;
        lastStandby = now;
        if (h.IsPaused) return;
        // A suspended tick must not turn into an immediate stalled-order retry.
        if (dt > 1.0f) dt = 1.0f;
        standbyElapsed += dt;
        for (int i = 0; i < medicCount; ++i)
        {
            ref Medic m = ref medics[i];
            if (!Available(h, m.Actor)) { ReleaseMedic(h, ref m); continue; }
            StageMedic(h, ref m, i, standbyElapsed);
        }
    }
}
