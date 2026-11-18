// Port of upstream/ProvingGrounds/plugin/src/TownMedicCoordinator.h (Proving Grounds by Shutori, GPLv3).
using System.Runtime.CompilerServices;

namespace ProvingGrounds.Core;

/// <summary>C++: TownMedicCoordinator.h (header-only, namespace <c>TownMedicCoordinator</c>): the aftercare planner (TW §2.9). No
/// engine objects, clocks or native calls: observations in, intentions out.
/// <para>The observation structs are copied by value in the C++ (<c>Observation Observe()</c> returns one, the glue keeps
/// <c>const Observation observation = Observe()</c>), so they are C# structs and their fixed arrays are <c>[InlineArray]</c>s that
/// copy with them. .NET throws on the built-in Equals/GetHashCode of a struct holding an InlineArray (brief, task 1.8): compare
/// fields. Several C++ constructors set non-zero defaults (<c>carrying = -1</c>, <c>eligible = true</c>, <c>occupant = -1</c>,
/// <c>Intent</c> kind <c>Wait</c> with patient and bed -1): ALWAYS create these with <c>new X()</c>; <c>default(X)</c> and the
/// elements of a new <c>X[]</c> skip that constructor and are NOT the C++ default (see each type).</para>
/// <para>Unreachable in the C++ too: no code sets the phases Treatment, Transport and Returning, the outcome Recovered, or produces
/// the intent Approach (only <see cref="Coordinator.Fail"/> may name it). They are kept with their numeric values.</para>
/// <para>Indexes outside the C++ arrays (counts above MaxMedics / MaxPatients / MaxBeds, a carried index of MaxPatients or more,
/// <c>HandoffPending(count &gt; MaxPatients)</c>, <c>Fail</c> or <c>Command</c> with m outside 0..9) are undefined behaviour in the
/// C++; here they throw IndexOutOfRangeException. The glue caps patients at MaxPatients (TownAftercare.cpp:599, 609, 1170) and the
/// beds at MaxBeds (:645-646); the Meitou glue (M8) must keep those caps, or a Tick loop throws.</para></summary>
public static class TownMedicCoordinator
{
    // C++: TownMedicCoordinator.h:7 (an anonymous enum)
    public const int MaxMedics = 10, MaxPatients = 16, MaxBeds = 64;

    /// <summary>C++: TownMedicCoordinator.h:8</summary>
    public enum Phase { Gathering = 0, Treatment = 1, Transport = 2, Returning = 3, Complete = 4, Care = 5 }

    /// <summary>C++: TownMedicCoordinator.h:9</summary>
    public enum Kind { Gather = 0, Wait = 1, Approach = 2, Bandage = 3, Repair = 4, PickUp = 5, Deliver = 6, GoHospital = 7, Release = 8,
        PutDown = 9, ApproachCarry = 10, WalkBed = 11 }

    /// <summary>C++: TownMedicCoordinator.h:10-13 (the C++ default is 0, 0, 0, as <c>default(Point)</c> is).</summary>
    public struct Point
    {
        public float X, Y, Z;

        public Point(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    // C++: TownMedicCoordinator.h:14-17
    public static float Distance(in Point a, in Point b)
    {
        float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>C++: TownMedicCoordinator.h:18-23. WARNING: <c>default(MedicObservation)</c> has <see cref="Carrying"/> 0 (= carrying
    /// patient 0); the C++ default is -1 (empty): use <c>new MedicObservation()</c>.</summary>
    public struct MedicObservation
    {
        public bool Ready, Bandages, Repairs;
        /// <summary>-1 empty, -2 foreign object, otherwise patient index</summary>
        public int Carrying;
        public Point Position;

        // C++: TownMedicCoordinator.h:22
        public MedicObservation() { Ready = false; Bandages = false; Repairs = false; Carrying = -1; Position = new Point(); }
    }

    /// <summary>C++: TownMedicCoordinator.h:24-30. WARNING: <c>default(PatientObservation)</c> has <see cref="Eligible"/> false; the
    /// C++ default is true: use <c>new PatientObservation()</c>.</summary>
    public struct PatientObservation
    {
        public bool Valid, Hospital, Robot, InBed, Delivered, ExternalCarry, Combat, CanWalk, Eligible;
        public float Flesh, Repair, Bleed;
        public Point Position;

        // C++: TownMedicCoordinator.h:28-29
        public PatientObservation()
        {
            Valid = false; Hospital = false; Robot = false; InBed = false; Delivered = false;
            ExternalCarry = false; Combat = false; CanWalk = false; Eligible = true; Flesh = 0; Repair = 0; Bleed = 0;
            Position = new Point();
        }
    }

    /// <summary>C++: TownMedicCoordinator.h:31-36. WARNING: <c>default(BedObservation)</c> has <see cref="Occupant"/> 0 (= patient 0);
    /// the C++ default is -1: use <c>new BedObservation()</c>.</summary>
    public struct BedObservation
    {
        public bool Available, Robot;
        /// <summary>patient index, or -1</summary>
        public int Occupant;
        public Point Position;

        // C++: TownMedicCoordinator.h:35
        public BedObservation() { Available = false; Robot = false; Occupant = -1; Position = new Point(); }
    }

    /// <summary><c>MedicObservation[MaxMedics]</c></summary>
    [InlineArray(MaxMedics)] public struct MedicObservations { MedicObservation _e; }
    /// <summary><c>PatientObservation[MaxPatients]</c></summary>
    [InlineArray(MaxPatients)] public struct PatientObservations { PatientObservation _e; }
    /// <summary><c>BedObservation[MaxBeds]</c></summary>
    [InlineArray(MaxBeds)] public struct BedObservations { BedObservation _e; }

    /// <summary>C++: TownMedicCoordinator.h:37-45. WARNING: <c>default(Observation)</c> holds <c>default</c> elements (patients not
    /// eligible, medics carrying patient 0, beds occupied by patient 0): use <c>new Observation()</c>, which default-constructs every
    /// element as the C++ does. Entries at or above the counts keep whatever they hold and are still read by index (a carried patient
    /// index, an assignment left from an earlier tick with more patients), as in the C++.</summary>
    public struct Observation
    {
        public int MedicCount, PatientCount, BedCount;
        public MedicObservations Medics;
        public PatientObservations Patients;
        public BedObservations Beds;
        public bool HospitalAvailable;
        public Point Hospital;

        // C++: TownMedicCoordinator.h:44 (the arrays' elements default-constructed, as the C++ does)
        public Observation()
        {
            MedicCount = 0; PatientCount = 0; BedCount = 0; HospitalAvailable = false; Hospital = new Point();
            for (int i = 0; i < MaxMedics; ++i) Medics[i] = new MedicObservation();
            for (int i = 0; i < MaxPatients; ++i) Patients[i] = new PatientObservation();
            for (int i = 0; i < MaxBeds; ++i) Beds[i] = new BedObservation();
        }
    }

    /// <summary>C++: TownMedicCoordinator.h:46-50. WARNING: <c>default(Intent)</c> is kind <see cref="Kind.Gather"/> (0) with patient
    /// and bed 0; the C++ default <c>Intent()</c> is Wait, -1, -1: use <c>new Intent()</c> or <c>new Intent(kind, ...)</c>.</summary>
    public struct Intent
    {
        public Kind Kind;
        public int Patient, Bed;

        // C++: TownMedicCoordinator.h:49 (k = Wait)
        public Intent() : this(Kind.Wait) { }

        // C++: TownMedicCoordinator.h:49
        public Intent(Kind k, int p = -1, int b = -1) { Kind = k; Patient = p; Bed = b; }
    }

    /// <summary>C++: TownMedicCoordinator.h:51</summary>
    public enum Outcome { Pending = 0, Treated = 1, Delivered = 2, Recovered = 3, Unavailable = 4, Unresolved = 5 }

    /// <summary>C++: TownMedicCoordinator.h:52-336. One planner per town (TownAftercare.cpp:144), never copied, so a class without
    /// Clone. The C++ private members are <c>internal</c> (the golden test reads them).</summary>
    public sealed class Coordinator
    {
        internal Phase phase;
        internal readonly int[] assigned = new int[MaxMedics], reservedBed = new int[MaxMedics], activeMode = new int[MaxMedics];
        internal readonly int[] helperTarget = new int[MaxMedics], helperMode = new int[MaxMedics];
        internal readonly uint[,] failed = new uint[MaxMedics, MaxPatients];
        internal readonly bool[] walkFailed = new bool[MaxPatients];
        internal bool missingBed;
        internal readonly Outcome[] outcomes = new Outcome[MaxPatients];
        internal readonly Intent[] commands = new Intent[MaxMedics];
        // Time a patient has spent owing a hospital transfer with a medic
        // blocked on Wait for it. Cleared by any real work on that patient.
        internal readonly float[] bedWait = new float[MaxPatients];
        internal readonly bool[] bedWaitExpired = new bool[MaxPatients];
        internal int expiredWaits;
        internal float lastTick;

        // C++: TownMedicCoordinator.h:66-68
        static bool NeedsAid(in PatientObservation p)
        {
            return p.Valid && (p.Flesh > .01f || p.Repair > .01f);
        }

        // C++: TownMedicCoordinator.h:69-72
        static int Mode(in MedicObservation m, in PatientObservation p)
        {
            bool flesh = m.Bandages && p.Flesh > .01f, robot = m.Repairs && p.Repair > .01f;
            return robot && (!flesh || p.Repair > p.Flesh) ? 1 : flesh ? 0 : -1;
        }

        // C++: TownMedicCoordinator.h:73-76
        static bool Carried(int p, in Observation o)
        {
            for (int m = 0; m < o.MedicCount; ++m) if (o.Medics[m].Carrying == p) return true;
            return false;
        }

        // C++: TownMedicCoordinator.h:77-80
        bool Assigned(int p, in Observation o)
        {
            for (int m = 0; m < o.MedicCount; ++m) if (assigned[m] == p) return true;
            return false;
        }

        // C++: TownMedicCoordinator.h:81
        bool Failed(int m, int p, Kind k) { return (failed[m, p] & (1u << (int)k)) != 0; }

        // C++: TownMedicCoordinator.h:82-94
        int AidMode(int m, int p, in Observation o)
        {
            // Keep the native treatment running when its relative score crosses
            // the other injury type. Switch only when the current work clears.
            if (assigned[m] == p)
            {
                if (activeMode[m] == 0 && o.Medics[m].Bandages && o.Patients[p].Flesh > .01f && !Failed(m, p, Kind.Bandage)) return 0;
                if (activeMode[m] == 1 && o.Medics[m].Repairs && o.Patients[p].Repair > .01f && !Failed(m, p, Kind.Repair)) return 1;
            }
            if (helperTarget[m] == p)
            {
                if (helperMode[m] == 0 && o.Medics[m].Bandages && o.Patients[p].Flesh > .01f && !Failed(m, p, Kind.Bandage)) return 0;
                if (helperMode[m] == 1 && o.Medics[m].Repairs && o.Patients[p].Repair > .01f && !Failed(m, p, Kind.Repair)) return 1;
            }
            return Mode(o.Medics[m], o.Patients[p]);
        }

        // C++: TownMedicCoordinator.h:95-104
        bool Eligible(int m, int p, in Observation o)
        {
            if (!o.Patients[p].Eligible || outcomes[p] != Outcome.Pending || !o.Patients[p].Valid || o.Patients[p].Combat ||
                o.Patients[p].ExternalCarry || Carried(p, o)) return false;
            if (NeedsAid(o.Patients[p]))
            {
                int mode = AidMode(m, p, o);
                return mode >= 0 && !Failed(m, p, mode != 0 ? Kind.Repair : Kind.Bandage);
            }
            if (Failed(m, p, Kind.Deliver)) return false;
            return (o.Patients[p].CanWalk && !walkFailed[p]) || !Failed(m, p, Kind.PickUp);
        }

        // C++: TownMedicCoordinator.h:105-108
        bool BedUsable(int b, int p, in Observation o)
        {
            return b >= 0 && b < o.BedCount && o.Beds[b].Robot == o.Patients[p].Robot &&
                (o.Beds[b].Available || o.Beds[b].Occupant == p);
        }

        // C++: TownMedicCoordinator.h:109-120
        int FindBed(int m, int p, in Observation o)
        {
            int best = -1; float nearest = 0;
            for (int b = 0; b < o.BedCount; ++b)
            {
                if (!BedUsable(b, p, o)) continue;
                bool taken = false;
                for (int other = 0; other < o.MedicCount; ++other)
                    if (other != m && reservedBed[other] == b) taken = true;
                float distance = Distance(o.Patients[p].Position, o.Beds[b].Position);
                if (!taken && (best < 0 || distance < nearest)) { best = b; nearest = distance; }
            }
            return best;
        }

        // C++: TownMedicCoordinator.h:122
        public Coordinator() { Reset(); }

        // C++: TownMedicCoordinator.h:123-135
        public void Reset()
        {
            phase = Phase.Gathering; missingBed = false;
            for (int p = 0; p < MaxPatients; ++p)
            {
                outcomes[p] = Outcome.Pending; walkFailed[p] = false;
                bedWait[p] = 0.0f; bedWaitExpired[p] = false;
            }
            expiredWaits = 0; lastTick = 0.0f;
            for (int m = 0; m < MaxMedics; ++m)
            {
                assigned[m] = reservedBed[m] = activeMode[m] = -1;
                helperTarget[m] = helperMode[m] = -1; commands[m] = new Intent(Kind.Release);
                for (int p = 0; p < MaxPatients; ++p) failed[m, p] = 0;
            }
        }

        // C++: TownMedicCoordinator.h:136
        public Phase GetPhase() { return phase; }

        /// <summary>A reference, as the C++ <c>const Intent&amp;</c>: it shows later ticks' commands, and <see cref="Tick"/>,
        /// <see cref="Fail"/>, <see cref="Reopen"/> and <see cref="Reset"/> overwrite it. Copy it (<c>Intent i = c.Command(m);</c>)
        /// where the C++ copies (TownAftercare.cpp:1369) and before calling Fail.</summary>
        // C++: TownMedicCoordinator.h:137
        public ref readonly Intent Command(int m) { return ref commands[m]; }

        // C++: TownMedicCoordinator.h:138-147
        public void Reopen()
        {
            if (phase != Phase.Complete) return;
            phase = Phase.Care; missingBed = false;
            for (int m = 0; m < MaxMedics; ++m)
            {
                assigned[m] = reservedBed[m] = activeMode[m] = -1;
                helperTarget[m] = helperMode[m] = -1;
                commands[m] = new Intent(Kind.Wait);
            }
            for (int p = 0; p < MaxPatients; ++p) { bedWait[p] = 0.0f; bedWaitExpired[p] = false; }
        }

        // C++: TownMedicCoordinator.h:148
        public bool IsHelping(int m) { return assigned[m] < 0 && helperTarget[m] >= 0; }

        // C++: TownMedicCoordinator.h:149-154
        public bool HasPatientWork(int p)
        {
            for (int m = 0; m < MaxMedics; ++m)
                if (commands[m].Patient == p && commands[m].Kind != Kind.Wait &&
                    commands[m].Kind != Kind.Release && commands[m].Kind != Kind.PutDown) return true;
            return false;
        }

        // C++: TownMedicCoordinator.h:155
        public bool PatientDone(int p) { return outcomes[p] != Outcome.Pending; }

        // C++: TownMedicCoordinator.h:156
        public Outcome PatientOutcome(int p) { return outcomes[p]; }

        // A bounded bed wait reports its own outcome so the log can tell a
        // handoff from a patient that walked off, instead of one shared reason.
        // C++: TownMedicCoordinator.h:159
        public float BedWait(int p) { return p >= 0 && p < MaxPatients ? bedWait[p] : 0.0f; }

        // C++: TownMedicCoordinator.h:160
        public bool WaitExpired(int p) { return p >= 0 && p < MaxPatients ? bedWaitExpired[p] : false; }

        // C++: TownMedicCoordinator.h:161
        public int ExpiredWaits() { return expiredWaits; }

        // C++: TownMedicCoordinator.h:162
        public int ReservedBed(int m) { return m >= 0 && m < MaxMedics ? reservedBed[m] : -1; }

        // C++: TownMedicCoordinator.h:163 (the medic index is unused)
        public bool MedicDone(int m) { return phase == Phase.Complete; }

        // C++: TownMedicCoordinator.h:164
        public bool MissingBed() { return missingBed; }

        // C++: TownMedicCoordinator.h:165-168
        public bool HasUnresolved()
        {
            for (int p = 0; p < MaxPatients; ++p) if (outcomes[p] == Outcome.Unresolved) return true;
            return false;
        }

        // C++: TownMedicCoordinator.h:169-176
        public void Fail(int m, int p, Kind k)
        {
            if (p < 0 || p >= MaxPatients) return;
            if (k == Kind.WalkBed) walkFailed[p] = true;
            else failed[m, p] |= 1u << (int)(k == Kind.ApproachCarry ? Kind.PickUp : k);
            assigned[m] = reservedBed[m] = activeMode[m] = -1;
            helperTarget[m] = helperMode[m] = -1;
            commands[m] = new Intent(Kind.Release);
        }

        // C++: TownMedicCoordinator.h:177-179
        public void HandoffPending(int count)
        {
            for (int p = 0; p < count; ++p) if (outcomes[p] == Outcome.Pending) outcomes[p] = Outcome.Unresolved;
        }

        // C++: TownMedicCoordinator.h:180-335
        public void Tick(in Observation o, bool fightOver, float elapsed, bool holdSpares)
        {
            for (int m = 0; m < MaxMedics; ++m) commands[m] = new Intent(Kind.Release);
            // A suspended or restarted clock must not age a transfer wait.
            float dt = elapsed - lastTick;
            lastTick = elapsed;
            if (dt < 0.0f || dt > 1.0f) dt = dt > 1.0f ? 1.0f : 0.0f;
            if (phase == Phase.Complete) return;
            if (!fightOver)
            {
                for (int m = 0; m < o.MedicCount; ++m) if (o.Medics[m].Ready) commands[m] = new Intent(Kind.Gather);
                return;
            }
            phase = Phase.Care; missingBed = false;
            for (int p = 0; p < o.PatientCount; ++p)
            {
                if (outcomes[p] != Outcome.Pending || !o.Patients[p].Eligible) continue;
                ref readonly PatientObservation patient = ref o.Patients[p];
                if (!patient.Valid) outcomes[p] = Outcome.Unavailable;
                else if (!NeedsAid(patient) && !Carried(p, o) && !patient.ExternalCarry)
                {
                    if (patient.Delivered || patient.InBed) outcomes[p] = Outcome.Delivered;
                    else if (!patient.Hospital) outcomes[p] = Outcome.Treated;
                }
            }
            // Release obsolete assignments before assigning any new work.
            for (int m = 0; m < o.MedicCount; ++m)
            {
                int p = assigned[m];
                if (!o.Medics[m].Ready || (p >= 0 && o.Medics[m].Carrying != p && !Eligible(m, p, o))) assigned[m] = reservedBed[m] = activeMode[m] = -1;
            }
            // An observed carry takes precedence over all planned assignments.
            for (int m = 0; m < o.MedicCount; ++m)
            {
                int p = o.Medics[m].Carrying;
                if (p < 0) continue;
                if (!o.Patients[p].Eligible) continue;
                if (!o.Patients[p].Valid) { commands[m] = new Intent(Kind.PutDown, p); continue; }
                if (!o.Medics[m].Ready) continue;
                for (int other = 0; other < o.MedicCount; ++other)
                    if (other != m && assigned[other] == p) assigned[other] = reservedBed[other] = activeMode[other] = -1;
                if (assigned[m] != p) reservedBed[m] = -1;
                assigned[m] = p;
            }
            for (int m = 0; m < o.MedicCount; ++m)
            {
                if (!o.Medics[m].Ready || commands[m].Kind == Kind.PutDown) continue;
                if (assigned[m] < 0)
                {
                    int best = -1; float priority = 0;
                    for (int q = 0; q < o.PatientCount; ++q)
                    {
                        if (Assigned(q, o) || !Eligible(m, q, o)) continue;
                        ref readonly PatientObservation candidate = ref o.Patients[q];
                        float score = (candidate.Bleed > .0001f ? 100000.0f : 0) +
                            (NeedsAid(candidate) ? 10000.0f : 0) + candidate.Flesh + candidate.Repair -
                            Distance(o.Medics[m].Position, candidate.Position);
                        if (best < 0 || score > priority) { best = q; priority = score; }
                    }
                    assigned[m] = best;
                }
                int p = assigned[m];
                if (p < 0) continue;
                ref readonly PatientObservation patient = ref o.Patients[p];
                if (NeedsAid(patient) && o.Medics[m].Carrying != p)
                {
                    activeMode[m] = AidMode(m, p, o);
                    commands[m] = new Intent(activeMode[m] == 1 ? Kind.Repair : Kind.Bandage, p);
                    continue;
                }
                if (!BedUsable(reservedBed[m], p, o)) reservedBed[m] = -1;
                if (reservedBed[m] < 0) reservedBed[m] = FindBed(m, p, o);
                if (reservedBed[m] < 0) { commands[m] = new Intent(Kind.Wait, p); missingBed = true; continue; }
                Kind kind = o.Medics[m].Carrying == p ? Kind.Deliver :
                    patient.CanWalk && !walkFailed[p] ? Kind.WalkBed :
                    Distance(o.Medics[m].Position, patient.Position) > 12.0f ? Kind.ApproachCarry : Kind.PickUp;
                commands[m] = new Intent(kind, p, reservedBed[m]);
            }
            // Extra hands treat only. The primary owner above retains the bed
            // claim and is the only medic allowed to start this patient's transfer.
            for (int m = 0; m < o.MedicCount; ++m)
            {
                int p = helperTarget[m];
                if (!o.Medics[m].Ready || o.Medics[m].Carrying != -1 || assigned[m] >= 0 ||
                    p < 0 || !NeedsAid(o.Patients[p]) || !Eligible(m, p, o))
                    helperTarget[m] = helperMode[m] = -1;
            }
            for (int m = 0; m < o.MedicCount; ++m)
            {
                if (!o.Medics[m].Ready || o.Medics[m].Carrying != -1 || assigned[m] >= 0 ||
                    commands[m].Kind == Kind.PutDown) continue;
                if (helperTarget[m] < 0)
                {
                    int best = -1, fewest = MaxMedics + 1; float nearest = 0;
                    for (int q = 0; q < o.PatientCount; ++q)
                    {
                        if (!NeedsAid(o.Patients[q]) || !Eligible(m, q, o)) continue;
                        int helpers = 0;
                        for (int other = 0; other < o.MedicCount; ++other)
                            if (helperTarget[other] == q) ++helpers;
                        float distance = Distance(o.Medics[m].Position, o.Patients[q].Position);
                        if (helpers < fewest || (helpers == fewest && (best < 0 || distance < nearest)))
                        {
                            best = q; fewest = helpers; nearest = distance;
                        }
                    }
                    helperTarget[m] = best;
                }
                int p = helperTarget[m];
                if (p >= 0)
                {
                    helperMode[m] = AidMode(m, p, o);
                    commands[m] = new Intent(helperMode[m] == 1 ? Kind.Repair : Kind.Bandage, p);
                }
            }
            // Bound the wait for a hospital bed. A patient that still owes a
            // transfer but has no bed resolved holds its medic on Wait; nothing
            // else ages that hold out, so a full or unavailable hospital used to
            // pin the patient, the medics and the bout open until the 300s cap.
            // Real work on the patient (treatment, a carry, a resolved
            // pickup/approach/delivery or a bed walk) clears the clock, so a
            // normal bed wait is never charged the budget.
            for (int p = 0; p < o.PatientCount; ++p)
            {
                ref readonly PatientObservation patient = ref o.Patients[p];
                if (outcomes[p] != Outcome.Pending || !patient.Eligible || !patient.Valid ||
                    !patient.Hospital || NeedsAid(patient) || patient.Delivered ||
                    patient.InBed || patient.Combat || patient.ExternalCarry || Carried(p, o))
                {
                    bedWait[p] = 0.0f; continue;
                }
                bool blocked = false;
                for (int m = 0; m < o.MedicCount; ++m)
                    if (commands[m].Patient == p && commands[m].Kind == Kind.Wait) { blocked = true; break; }
                if (!blocked) { bedWait[p] = 0.0f; continue; }
                bedWait[p] += dt;
                if (!TownAftercarePolicy.TransferWaitExpired(bedWait[p])) continue;
                outcomes[p] = Outcome.Unresolved;
                bedWaitExpired[p] = true;
                ++expiredWaits;
                for (int m = 0; m < MaxMedics; ++m) if (assigned[m] == p) Fail(m, p, Kind.Wait);
            }
            bool pending = false;
            for (int p = 0; p < o.PatientCount; ++p) if (outcomes[p] == Outcome.Pending)
            {
                if (!o.Patients[p].Eligible) { pending = true; continue; }
                bool candidate = Assigned(p, o) || o.Patients[p].Combat ||
                    o.Patients[p].ExternalCarry || Carried(p, o);
                for (int m = 0; m < o.MedicCount; ++m)
                    if (o.Medics[m].Ready && Eligible(m, p, o)) candidate = true;
                if (!candidate) outcomes[p] = Outcome.Unresolved;
                else pending = true;
            }
            if (!pending)
            {
                phase = Phase.Complete;
                for (int m = 0; m < o.MedicCount; ++m)
                {
                    if (commands[m].Kind == Kind.PutDown || !o.Medics[m].Ready || o.Medics[m].Carrying != -1) continue;
                    commands[m] = new Intent(o.HospitalAvailable && Distance(o.Medics[m].Position, o.Hospital) > 12.0f ? Kind.GoHospital : Kind.Release);
                }
            }
            else
            {
                // Spare medics head back to the hospital instead of holding
                // formation ringside: a single patient waiting on a bed must not
                // park the whole team while the bout stays open. The waiter keeps
                // the Wait command the assignment loop set for it, and any other
                // ready medic is still re-assigned the moment work appears.
                // Mid-bout care (sequential Teams 1v1) keeps the old ringside
                // hold, so the next eliminated fighter still has its team at hand.
                for (int m = 0; m < o.MedicCount; ++m)
                {
                    if (!o.Medics[m].Ready || o.Medics[m].Carrying != -1) continue;
                    if (commands[m].Kind != Kind.Release) continue;
                    commands[m] = holdSpares ? new Intent(Kind.Wait) : new Intent(o.HospitalAvailable &&
                        Distance(o.Medics[m].Position, o.Hospital) > 12.0f ? Kind.GoHospital : Kind.Release);
                }
            }
        }
    }
}
