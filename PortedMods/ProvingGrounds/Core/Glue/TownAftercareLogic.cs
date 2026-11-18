using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/TownAftercare.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.4 (TW §2.9, CAP 12, 19, spike S10):
// the C++ decisions over ITownAftercareHost (the character, building, bed and hold reads and writes). The glue (src/Town/TownAftercare*.cs) binds the
// host to a world and to CharacterIds; Core never sees an engine type. A character is a TId, a bed its fixture id (Meitou's "<building key>@bed<k>",
// FixtureRegistry.cs:519-541), a building an ArenaBuilding as ArenaIdentity reads it. Split by the plan's partial files (controller decision 8.4):
//   TownAftercareLogic.cs ........ task 8.4a: the file-level state (TownAftercare.cpp:27-157), Player / Available (:158-167), the four reservation
//                                   tables and their public calls (:1051-1079), ControlAvailable, GetStatus, IsManaged, MarkWinner, ResetState,
//                                   Release, AbandonWorldState (:1104-1154), RestorePatient (:219-269, task 8.4b's directed half included)
//   TownAftercareLogic.Setup.cs .. task 8.4a: Run / Direct / Reassert / ReleaseMedic (:168-218), HospitalBed / Compatible / Occupied /
//                                   IsArenaHospitalBed and the bed registry's Forget / Remember / Defer (:320-407), StageMedic (:443-482), Begin
//                                   (:1155-1212), Standby (:1213-1226)
//   TownAftercareLogic.Treat.cs .. task 8.4b: DirectPatient (:270-319), NeedsRecovery / NeedsHospital / NativeAidNeed / AidRemaining / HasAidKit
//                                   (:409-442), the names, MedicBlocker, TraceMedic (:483-579), Point, PatientIndex, AdmitArenaStragglers,
//                                   Observe, the traces, LogOutcome (:580-862), Execute (:863-1033), Treat (:1293-1401)
//   TownAftercareLogic.Beds.cs ... task 8.4b: the bed entries the port sees by polling its own deliveries (the setBedMode hook's stand-in,
//                                   RecordBedEntry :75-84), TraceBeds (:778-812)
//   TownAftercareLogic.Recovery.cs task 8.4c: ReleaseBedOccupant (:371-402), RecordTreatment (:1035-1050), TickBedRecovery (:1227-1292) and the
//                                   scan of the hospital's beds (the setBedMode hook's other stand-in)
//
// The C++'s ownership hook (InstallHooks :1080-1103: CheckMedicOrders on AITaskSytem::checkOrders, RecordBedEntry on Character::setBedMode) has
// no Meitou counterpart: the port holds a reserved NPC through the world's hold registry (Core/Glue/HoldRegistry.cs, spike S10's Engaged) and
// ControlAvailable answers whether that hold's members resolve (docs/divergences.md "TownAftercare (task 8.4a)"). The reservation tables keep the
// C++'s capacities and refusals (Core TownMedicAIPolicy.ReceiverReservations: medics 10, patients 16, bench 16, external 4); a C++ order receiver
// (<c>c->getOrdersReciever()</c>, one per character) is the character itself here, interned as one object per TId so Core's reference-compared
// tables can hold it. The C++ SRWLOCK is not needed: everything runs in the sim thread's serial steps (no checkOrders hook on AI workers).

/// <summary>A bed fixture as the host reads it now: its id, the BUILDING record of the building it stands in (the C++
/// <c>furnitureParentBuilding()</c>'s string id), its BUILDING functionality's <c>function</c> (6 BF_BED, 25 BF_SKELETON_BED;
/// <c>Fixture.Bed.Function</c>, Fixtures.cs:80, 375), its occupant (<paramref name="Occupant"/>, the C++ <c>bed->getOccupant()</c>), whether that
/// occupant is valid and holds the bed (<c>!FixtureRegistry.IsFree</c>, :1412-1414) and whether it lies in it (<c>inSomething == IN_BED</c>:
/// <c>InSomething == HeldState.Bed</c> on this bed), and (task 8.4b) its <paramref name="Position"/> (the C++ <c>bed-&gt;getPosition()</c>).</summary>
public readonly record struct AftercareBed<TId>(string Id, string BuildingRecord, int Function, TId Occupant, bool Occupied, bool OccupantInBed,
    Vector3 Position = default);

/// <summary>What the "aftercare fighter released" line reads of a patient (TownAftercare.cpp:242-260): dead, KO, in combat, carried, in a bed,
/// the Hold and Passive standing orders, the actionable aid needs (ArenaMedical::ActionableAidNeed, flesh and repair), the bleed rate and wound
/// count (0 without a medical system), the roster recovery read (blood and lowest limb; <see cref="RecoveryRead"/> false when it failed), whether
/// the AI has a goal, and the position.</summary>
public readonly record struct AftercarePatientRead(bool Dead, bool Unconscious, bool InCombat, bool Carried, bool InBed, bool Hold, bool Passive,
    float AidFlesh, float AidRepair, float BleedRate, uint Wounds, bool RecoveryRead, float Blood, float LowestLimb, bool NativeGoal, Vector3 Position);

/// <summary>What TownAftercare.cpp reads of the world and does to it (task 8.4a's members; 8.4b and 8.4c add theirs). The glue implements it over
/// PgEngine, ArenaIdentity, the fixture registry and the world's hold registry (src/Town/TownAftercare.cs); the fake is in
/// Tests/Glue/TownAftercareLogicTests.cs.</summary>
public interface ITownAftercareHost<TId>
{
    // ---------- the world ----------
    /// <summary><c>ou-&gt;isPaused()</c>: always false in the port's step (a paused world runs no SlowWorld).</summary>
    bool IsPaused { get; }
    /// <summary><c>GetTickCount()</c>: the sim clock's milliseconds.</summary>
    uint NowMs { get; }
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);
    /// <summary><c>PGLog::NextId()</c>.</summary>
    uint NextLogId();

    // ---------- characters ----------
    /// <summary>The C++ NULL character.</summary>
    TId None { get; }
    bool IsNull(TId c);
    /// <summary><c>c &amp;&amp; c-&gt;isValid()</c> (and <c>getOrdersReciever()</c>: every Meitou character has one).</summary>
    bool Valid(TId c);
    /// <summary>The C++ <c>Player(c)</c> (:158-163): one of the player's characters.</summary>
    bool IsPlayer(TId c);
    bool IsDead(TId c);
    bool IsUnconscious(TId c);
    bool IsCarried(TId c);
    /// <summary><c>c-&gt;isCarryingSomething</c>.</summary>
    bool IsCarrying(TId c);
    /// <summary><c>c-&gt;isInCombatMode(true, true)</c>.</summary>
    bool InCombatMode(TId c);
    /// <summary><c>ArenaMedical::NeedsStabilization(c)</c>.</summary>
    bool NeedsStabilization(TId c);
    /// <summary><c>c-&gt;getName()</c>.</summary>
    string Name(TId c);
    /// <summary><c>hand::toString()</c> of the character's hand (the log lines' <c>actor=</c>).</summary>
    string ActorText(TId c);
    /// <summary><c>c-&gt;getPosition()</c> (read after <see cref="Valid"/>).</summary>
    Vector3 Position(TId c);
    /// <summary><c>movement-&gt;isCurrentlyMoving()</c> and then <c>movement-&gt;destination</c>; false when standing.</summary>
    bool TryGetMoveDestination(TId c, out Vector3 destination);
    /// <summary>Whether the character's AI has a goal now (the C++ <c>currentGoal(receiver)</c> of the hand-off line).</summary>
    bool HasAiGoal(TId c);
    /// <summary><c>c-&gt;getCarryingObject()</c> as a character (the NULL character when it carries nobody).</summary>
    TId CarryingOf(TId c);
    /// <summary>The reads of the "aftercare fighter released" line (read after <see cref="Valid"/>).</summary>
    AftercarePatientRead ReadForRelease(TId c);

    // ---------- orders (the glue's adapters) ----------
    /// <summary>The town AI is taken off the character for <paramref name="owner"/> (the C++ reservation's checkOrders hook; the port: the hold
    /// registry, spike S10). A second call by the same owner re-asserts it (the C++ <c>Reassert</c>'s clearAllAIGoals). False for a stale id.</summary>
    bool Hold(TId c, HoldOwner owner);
    /// <summary><paramref name="owner"/> lets go; the NPC returns to its town AI only when no owner is left.</summary>
    void ReleaseHold(TId c, HoldOwner owner);
    /// <summary><c>c-&gt;getMovementSpeedOrders()</c> (Meitou: the move speed mode, <c>CharacterCold.RunSpeed</c>).</summary>
    byte MoveSpeed(TId c);
    /// <summary><c>setDesiredSpeedOrders(speed)</c> + <c>restoreDesiredSpeed()</c>.</summary>
    void RestoreMoveSpeed(TId c, byte speed);
    /// <summary><c>c-&gt;dropCarriedObject(true, false)</c> (DropPatient :196).</summary>
    void DropCarried(TId c);
    /// <summary>The C++ <c>Run(c)</c> (:168-171): <c>setDesiredSpeedOrders(RUN)</c> + <c>setDesiredSpeed(RUN)</c>.</summary>
    void Run(TId c);
    /// <summary><c>addOrder(NULL, MOVE_CUS_ORDERED, NULL, false, true, point)</c> + both <c>setDestination(point)</c> calls (:154-160).</summary>
    void MoveTo(TId c, Vector3 point);

    // ---------- buildings and beds ----------
    /// <summary>A held building as it is now (null: gone).</summary>
    PlacedBuilding? Resolve(ArenaBuildingRef held);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(origin, radius, BUILDING, max, NULL)</c>.</summary>
    IReadOnlyList<PlacedBuilding>? BuildingsInSphere(Vector3 origin, float radius, int max);
    /// <summary>The bed fixtures inside <paramref name="building"/> in spot order (every kind of bed; <see cref="TownAftercareLogic{TId}.HospitalBed"/>
    /// filters them).</summary>
    IReadOnlyList<string> BedsOf(ArenaBuilding building);
    /// <summary>The bed fixture <paramref name="id"/> as it is now; null when gone.</summary>
    AftercareBed<TId>? Bed(string id);

    // ---------- task 8.4b: treatment and transport (TownAftercare.cpp:219-319, :409-1033, :1293-1401) ----------
    /// <summary><c>SparSession::IsActive() &amp;&amp; SparSession::GetMode() == MatchRules::ModeTeams1v1</c> (Treat :1296-1297).</summary>
    bool SparTeams1v1Active { get; }
    /// <summary><c>SparSession::IsEliminated(c)</c>.</summary>
    bool SparIsEliminated(TId c);
    /// <summary><c>ArenaIdentity::GetStringId(c)</c>: the character's record id ("" for a stale id).</summary>
    string RecordId(TId c);
    /// <summary><c>ou-&gt;getObjectsWithinSphere(origin, radius, CHARACTER, max, NULL)</c> (AdmitArenaStragglers :603-605).</summary>
    IReadOnlyList<TId>? CharactersInSphere(Vector3 origin, float radius, int max);
    /// <summary><c>c-&gt;inSomething == IN_BED</c>.</summary>
    bool InBed(TId c);
    /// <summary>Whether <paramref name="c"/> lies in the bed <paramref name="bed"/> (<c>inSomething == IN_BED</c> on that bed): the delivery receipt
    /// the C++ setBedMode hook recorded (RecordBedEntry :75-84), read by the port's poll.</summary>
    bool LiesIn(TId c, string bed);
    /// <summary><c>c-&gt;isCrippled()</c>.</summary>
    bool IsCrippled(TId c);
    /// <summary><c>c-&gt;getRace()-&gt;robot</c> (false without a race).</summary>
    bool IsRobot(TId c);
    /// <summary><c>c-&gt;getMedical() != NULL</c>.</summary>
    bool HasMedical(TId c);
    /// <summary><c>medical-&gt;scoreFirstAidNeed(robot)</c> as the engine computes it (may be 0 or below; 0 without a medical state).</summary>
    float AidScore(TId c, bool robot);
    /// <summary><c>ArenaMedical::ActionableAidNeed(c, robot)</c>.</summary>
    float ActionableAidNeed(TId c, bool robot);
    /// <summary><c>medical-&gt;currentBleedRate</c> (0 without a medical state).</summary>
    float BleedRate(TId c);
    /// <summary><c>medical-&gt;wounds.size()</c> (0 without a medical state).</summary>
    uint WoundCount(TId c);
    /// <summary><c>medical-&gt;blood / medical-&gt;getMaxBlood()</c>, 1 when the maximum is not above 0 (NeedsRecovery :421-423).</summary>
    float BloodRatio(TId c);
    /// <summary><c>medical-&gt;anatomy</c> in order (empty without a medical state). The list may be the host's reusable buffer: read it before the
    /// next host call that reads parts, never keep it.</summary>
    IReadOnlyList<AftercarePart> Parts(TId c);
    /// <summary><c>RosterStatus::Read(c, recovery, &amp;error)</c>: false when it failed.</summary>
    bool RosterRead(TId c, out RosterStatus.Snapshot recovery);
    /// <summary>HasAidKit (:432-436): the best item with function ITEM_FIRSTAID (or ITEM_ROBOTREPAIR) has charges left.</summary>
    bool HasAidKit(TId c, bool robot);
    /// <summary><c>getStandingOrder(M_SET_ORDER_HOLD)</c> and <c>(M_SET_ORDER_PASSIVE)</c>.</summary>
    (bool Hold, bool Passive) StandingOrders(TId c);
    /// <summary><c>setStandingOrder(M_SET_ORDER_HOLD, hold)</c> then <c>(M_SET_ORDER_PASSIVE, passive)</c>.</summary>
    void SetStandingOrders(TId c, bool hold, bool passive);
    /// <summary>RestorePatient's native restart's <c>clearOrders(receiver)</c> (:236): the order queue emptied and the running task ended (a
    /// walk to a bed stopped). Serial step.</summary>
    void ClearOrders(TId c);
    /// <summary>Execute's FIRST_AID_ORDER / FIRST_AID_ROBOT on <paramref name="patient"/> (:1003-1004, :1024-1027). False: refused.</summary>
    bool StartTreat(TId medic, TId patient, bool robot);
    /// <summary>Execute's LIFT_PERSON_PLAYER_ORDER on <paramref name="patient"/> (:1005, :1024-1027): walks to the body and lifts it. False: refused.</summary>
    bool BeginLift(TId medic, TId patient);
    /// <summary>Execute's PUT_SOMEONE_IN_BED with the bed <paramref name="bed"/> (:1006, :1024-1027): carries the carried patient to that bed and lays it
    /// in. False: refused.</summary>
    bool PutInBed(TId medic, string bed);
    /// <summary>Execute's USE_BED_ORDER for a walking patient (:1007, :1020-1023): walks to the bed <paramref name="bed"/> and lies in it. False:
    /// refused.</summary>
    bool UseBed(TId patient, string bed);

    // ---------- task 8.4c: bed recovery (TownAftercare.cpp:371-402, :1227-1292) ----------
    /// <summary>The bed fixtures TickBedRecovery scans each poll for occupants (the Scratch Hospital 163's beds; the setBedMode hook's stand-in).
    /// The list may be the host's cached one: read it, never keep it.</summary>
    IReadOnlyList<string> RecoveryBeds();
    /// <summary>ReleaseBedOccupant's <c>setBedModeOriginal(p, false, bed)</c>: the Sleep task ended and the engine's own get-up. Serial step.</summary>
    void LeaveBedMode(TId c);
    /// <summary>ReleaseBedOccupant's fallback <c>bed-&gt;stopOperating(actor)</c>: the character taken out of the bed <paramref name="bed"/> at once.
    /// Serial step.</summary>
    void StopOperating(TId c, string bed);
    /// <summary>ReleaseBedOccupant's <c>TownMedicAIPolicy::ResumeNative</c>: the character's AI back (the aftercare's patient hold given back if
    /// it holds it, else the AI asked to decide again). Serial step.</summary>
    void ResumeNative(TId c);
}

/// <summary>One <c>medical-&gt;anatomy[i]</c> as TownAftercare reads it: <c>isRobotic()</c>, <c>isDead()</c>, <c>flesh</c>, <c>fleshStun</c>,
/// <c>bandaging</c>, <c>maxHealth()</c>.</summary>
public readonly record struct AftercarePart(bool Robotic, bool Dead, float Flesh, float Stun, float Bandage, float Max);

/// <summary>TownAftercare.cpp's state and decisions for one world. Sim thread, serial steps only.</summary>
public sealed partial class TownAftercareLogic<TId> where TId : notnull
{
    // ---------- the file-level state (TownAftercare.cpp:27-157) ----------

    /// <summary>C++: TownAftercare.cpp:123-134 <c>struct Medic</c> (no receiver pointer: the reservation's key is the actor; a value type updated
    /// in place through <see cref="medics"/>' array).</summary>
    public struct Medic
    {
        public TId Actor, Patient;
        public string? Bed;
        public bool Directed;
        public TownMedicCoordinator.Kind LastKind;
        public byte Speed;
        public float LastRecall, LastRecallLog, LastTrace;
        public uint LastWorkTicks;
        public TownAftercarePolicy.CommandProgress Command;
        /// <summary>Port only (the test verb pg town care): the last six orders Execute gave, newest last, each with whether the engine took it
        /// ("bandage ok; pickup ok; deliver refused"; "" before the first).</summary>
        public string LastOrder;
        /// <summary>Port only: the "medic ownership failed" line was written for this medic (once).</summary>
        public bool OwnershipFailedLogged;

        public Medic(TId actor, TId none)
        {
            Actor = actor; Patient = none; Bed = null; Directed = false; LastKind = TownMedicCoordinator.Kind.Wait; Speed = 0;
            LastRecall = -1000; LastRecallLog = -1000; LastTrace = -1000; LastWorkTicks = 0; Command = new TownAftercarePolicy.CommandProgress();
            LastOrder = "";
        }
    }

    /// <summary>C++: TownAftercare.cpp:135-143 <c>struct Patient</c> (no receiver pointer: <see cref="Reserved"/> = the C++ receiver set).</summary>
    public struct Patient
    {
        public TId Actor;
        public bool Directed, Hold, Passive, Winner, ReleaseLogged, EarlyAdmitted, SweepAdmitted, SpeedDirected;
        public byte Speed;
        public bool Reserved;   // the C++ receiver != NULL
        public float LastTrace;

        public Patient(TId actor)
        {
            Actor = actor; Directed = Hold = Passive = Winner = ReleaseLogged = EarlyAdmitted = SweepAdmitted = SpeedDirected = false;
            Speed = 0; Reserved = false; LastTrace = -1000;
        }
    }

    /// <summary>C++: TownAftercare.cpp:62-67 <c>struct BedOccupant</c>.</summary>
    public struct BedOccupant
    {
        public TId Actor;
        public string? Bed;
        public float Entered;
        public float LastAttempt;
    }

    readonly TId none;
    readonly Dictionary<TId, object> receivers = [];   // one key object per character (the C++ order receiver)
    readonly TownMedicAIPolicy.ReceiverReservations reservations = TownMedicAIPolicy.NewReservations();                    // :29
    readonly TownMedicAIPolicy.ReceiverReservations patientReservations = TownMedicAIPolicy.NewPatientReservations();      // :30
    readonly TownMedicAIPolicy.ReceiverReservations benchReservations = TownMedicAIPolicy.NewPatientReservations();        // :31
    readonly TownMedicAIPolicy.ReceiverReservations externalReservations = TownMedicAIPolicy.NewExternalReservations();    // :32
    readonly TownMedicAIPolicy.Deliveries deliveries = new();                                                             // :33
    readonly BedOccupant[] bedOccupants = new BedOccupant[TownMedicCoordinator.MaxBeds];                                  // :68
    int bedOccupantCount;                                                                                                // :69
    float bedRecoveryElapsed;                                                                                            // :70
    // :71-72 lastBedSweepTrace / lastBedSweep: TownAftercareLogic.Recovery.cs (task 8.4c)
    bool controlAvailable;                                                                                               // :96
    readonly TownMedicCoordinator.Coordinator coordinator = new();                                                       // :144
    readonly Medic[] medics = new Medic[TownMedicCoordinator.MaxMedics];                                                // :145 (at most RequiredMedics = 10)
    readonly List<Patient> patients = [];                                                                                // :146
    readonly List<string> beds = [];                                                                                     // :147
    ArenaBuildingRef? arenaSite;                                                                                         // :148
    ArenaBuildingRef? returnHospital;                                                                                    // :149
    Vector3 standbyPoint = Vector3.Zero;                                                                                 // :150
    Vector3 standbySide = new(1, 0, 0);                                                                                  // :151
    uint lastStandby;                                                                                                    // :152
    float standbyElapsed;                                                                                                // :153
    float lastSummaryTrace = -1000;                                                                                      // :154
    string status = "";                                                                                                  // :155
    uint careId;                                                                                                         // :156
    float postFightStart = -1;                                                                                           // :157
    int medicCount;

    /// <summary>C++: TownAftercare.cpp:34-46 <c>struct TreatmentTrace</c> (the medic's slot is taken by Direct, emptied by ReleaseReceiver). The
    /// order-check counts (<c>receiver</c>, <c>orderChecks</c>, <c>nativeHandled</c>) have no source in the port (the checkOrders hook; task 8.4a's
    /// divergence row) and are not kept. RecordTreatment (:1035-1050, task 8.4c) fills the counts from the FirstAidSystem.Apply prefix and
    /// postfix (src/Game/ArenaMedical.cs).</summary>
    struct TreatmentTrace
    {
        public TId Actor, Patient;
        public bool Used;   // the C++ actor != NULL
        public uint FirstAid, Doctoring, Blocked, Completed, WorkTicks;
        public float AidTime, DoctorTime;

        public void ClearSamples(TId none) { Patient = none; FirstAid = Doctoring = Blocked = Completed = 0; AidTime = DoctorTime = 0; }
    }
    readonly TreatmentTrace[] treatmentTraces = new TreatmentTrace[TownMedicCoordinator.MaxMedics];                     // :46 (10)

    /// <param name="none">The C++ NULL character (the host's None).</param>
    /// <param name="controlAvailable">The C++ <c>InstallHooks()</c> result (the port: the hold registry's members resolve).</param>
    public TownAftercareLogic(TId none, bool controlAvailable)
    {
        this.none = none;
        this.controlAvailable = controlAvailable;
        for (int i = 0; i < TownMedicCoordinator.MaxBeds; ++i) bedOccupantTrace[i] = none;
        for (int i = 0; i < treatmentTraces.Length; ++i) treatmentTraces[i] = new TreatmentTrace { Actor = none, Patient = none };
    }

    // ---------- reads for the glue and the test verb ----------

    /// <summary>The reserved medics, in slot order (the C++ <c>medics</c>).</summary>
    public ReadOnlySpan<Medic> Medics => medics.AsSpan(0, medicCount);
    /// <summary>The registered fighters (the C++ <c>patients</c>).</summary>
    public IReadOnlyList<Patient> Patients => patients;
    /// <summary>The hospital's beds of the running care (the C++ <c>beds</c>).</summary>
    public IReadOnlyList<string> Beds => beds;
    /// <summary>The bed registry's entries (C++ <c>bedOccupants[0..bedOccupantCount)</c>).</summary>
    public ReadOnlySpan<BedOccupant> BedOccupants => bedOccupants.AsSpan(0, bedOccupantCount);
    public Vector3 StandbyPoint => standbyPoint;
    public Vector3 StandbySide => standbySide;
    public ArenaBuildingRef? ArenaSite => arenaSite;
    public ArenaBuildingRef? ReturnHospital => returnHospital;
    public uint CareId => careId;
    /// <summary>Whether <paramref name="c"/> is in the medic table (tests, the test verb).</summary>
    public bool MedicReserved(TId c) => receivers.TryGetValue(c, out var r) && reservations.Contains(r);
    /// <summary>Whether <paramref name="c"/> is in the bench table.</summary>
    public bool BenchReserved(TId c) => receivers.TryGetValue(c, out var r) && benchReservations.Contains(r);
    /// <summary>Whether <paramref name="c"/> is in the external table.</summary>
    public bool ExternalReserved(TId c) => receivers.TryGetValue(c, out var r) && externalReservations.Contains(r);
    /// <summary>Whether <paramref name="c"/> is in the patient table.</summary>
    public bool PatientReserved(TId c) => receivers.TryGetValue(c, out var r) && patientReservations.Contains(r);

    /// <summary>The C++ order receiver of <paramref name="c"/>: null for the NULL character, else its one key object.</summary>
    object? Receiver(ITownAftercareHost<TId> h, TId c)
    {
        if (h.IsNull(c)) return null;
        if (!receivers.TryGetValue(c, out var r)) receivers[c] = r = new object();
        return r;
    }

    /// <summary>The receiver of <paramref name="c"/> if one was ever made, else null (the release paths: nothing to remove, nothing created).</summary>
    object? ExistingReceiver(TId c) => receivers.TryGetValue(c, out var r) ? r : null;

    // C++: TownAftercare.cpp:158-163
    static bool Player(ITownAftercareHost<TId> h, TId c) => h.IsPlayer(c);

    // C++: TownAftercare.cpp:164-167
    static bool Available(ITownAftercareHost<TId> h, TId c) =>
        !h.IsNull(c) && h.Valid(c) && !Player(h, c) && !h.IsDead(c) && !h.IsUnconscious(c) &&
        !h.IsCarried(c) && !h.InCombatMode(c) && !h.NeedsStabilization(c);

    // ---------- the public calls (TownAftercare.cpp:1034-1154) ----------

    // C++: TownAftercare.cpp:1051
    public bool ControlAvailable() => controlAvailable;

    // C++: TownAftercare.cpp:1052-1059
    public bool ReserveBenchOrders(ITownAftercareHost<TId> h, TId c)
    {
        object? receiver = Receiver(h, c);
        if (!controlAvailable || receiver == null) return false;
        return !reservations.Contains(receiver) && !patientReservations.Contains(receiver) &&
            !externalReservations.Contains(receiver) && benchReservations.Add(receiver);
    }

    // C++: TownAftercare.cpp:1060-1064
    public void ReleaseBenchOrders(ITownAftercareHost<TId> h, TId c)
    {
        if (!h.IsNull(c) && ExistingReceiver(c) is { } receiver) benchReservations.Remove(receiver);
    }

    // C++: TownAftercare.cpp:1065-1074
    public bool ReserveExternalOrders(ITownAftercareHost<TId> h, TId c)
    {
        object? receiver = Receiver(h, c);
        if (!controlAvailable || receiver == null) return false;
        bool available = TownMedicAIPolicy.CanReserveExternal(
            reservations.Contains(receiver), patientReservations.Contains(receiver) || benchReservations.Contains(receiver),
            externalReservations.Contains(receiver));
        return available && externalReservations.Add(receiver);
    }

    // C++: TownAftercare.cpp:1075-1079
    public void ReleaseExternalOrders(ITownAftercareHost<TId> h, TId c)
    {
        if (!h.IsNull(c) && ExistingReceiver(c) is { } receiver) externalReservations.Remove(receiver);
    }

    // C++: TownAftercare.cpp:1104
    public string GetStatus() => status;

    // C++: TownAftercare.cpp:1105-1111
    public bool IsManaged(ITownAftercareHost<TId> h, TId c)
    {
        if (h.IsNull(c)) return false;
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < medicCount; ++i) if (eq.Equals(medics[i].Actor, c)) return true;
        for (int i = 0; i < patients.Count; ++i) if (eq.Equals(patients[i].Actor, c))
            return !coordinator.PatientDone(i);
        return false;
    }

    // C++: TownAftercare.cpp:1112-1120
    public void MarkWinner(ITownAftercareHost<TId> h, TId fighter)
    {
        if (h.IsNull(fighter)) return;
        var eq = EqualityComparer<TId>.Default;
        for (int i = 0; i < patients.Count; ++i)
            if (eq.Equals(patients[i].Actor, fighter))
            {
                var p = patients[i]; p.Winner = true; patients[i] = p;
                h.Debug("Proving Grounds: aftercare fighter marked winner name=" + h.Name(fighter) + " actor=" + h.ActorText(patients[i].Actor));
            }
    }

    // C++: TownAftercare.cpp:1121-1146 (preventedFallbacks :1138 is not kept, task 8.4a)
    void ResetState(bool worldTeardown)
    {
        reservations.Clear();
        patientReservations.Clear();
        if (TownAftercarePolicy.ClearExternalReservations(worldTeardown))
        {
            externalReservations.Clear(); benchReservations.Clear();
        }
        deliveries.Clear();
        watched.Clear();   // port: the poll's copy of the deliveries' watches (TownAftercareLogic.Beds.cs)
        // The bed registry outlives a bout on purpose: a delivered patient stops
        // being managed while still in the bed, and only a real world teardown
        // invalidates the handles behind it.
        if (worldTeardown)
        {
            bedOccupantCount = 0; bedRecoveryElapsed = 0;
            lastBedSweep = 0; lastBedSweepTrace = -1000;
            receivers.Clear();   // port: the interned receivers die with the world (every table that could hold one is empty now)
            bedKeys.Clear();
        }
        for (int i = 0; i < treatmentTraces.Length; ++i) treatmentTraces[i] = new TreatmentTrace { Actor = none, Patient = none };
        coordinator.Reset();
        postFightStart = -1;
        treatFailureLogged = false;
        ClearMedics(); patients.Clear(); beds.Clear(); lastStandby = 0; standbyElapsed = 0; lastSummaryTrace = -1000;
        lastBedTrace = -1000;
        for (int i = 0; i < TownMedicCoordinator.MaxBeds; ++i) { bedOccupantTrace[i] = none; bedAvailableTrace[i] = false; }
        arenaSite = null;
        returnHospital = null;
    }

    void ClearMedics()
    {
        Array.Clear(medics, 0, medicCount);
        medicCount = 0;
    }

    // C++: TownAftercare.cpp:1147-1151
    public void Release(ITownAftercareHost<TId> h)
    {
        for (int i = 0; i < medicCount; ++i) ReleaseMedic(h, ref medics[i]);
        for (int i = 0; i < patients.Count; ++i)
        {
            var p = patients[i];
            RestorePatient(h, ref p);
            patients[i] = p;
        }
        ResetState(false);
    }

    /// <summary>Port only: Release threw part-way; the bout's state is dropped as Release's own ResetState(false) would (the bed registry kept), the
    /// glue gives the holds back itself.</summary>
    public void ResetAfterFailedRelease() => ResetState(false);

    // C++: TownAftercare.cpp:1152-1154 (no actor access: the world's characters may already be gone)
    public void AbandonWorldState() => ResetState(true);

    /// <summary>C++: TownAftercare.cpp:219-269 RestorePatient. Port: the patient table entry is the C++ receiver (Reserved); the native restart
    /// (:234-239) under the C++'s conditions (up, not carried, not in a bed, not fighting) clears the patient's orders (<c>clearOrders</c>: the host's
    /// ClearOrders, so a walk to a bed stops) and its clearAllAIGoals / reThinkCurrentAIAction are the hold given back (the registry's Engaged off +
    /// Ai.Replan, spike S10); the hold is given back for every patient the table held, as the C++'s removal from the table ends its checkOrders
    /// suppression whatever the patient's state. A patient in a bed keeps lying (no ClearOrders: "do not cancel bed occupancy"). The registry keeps the NPC held while another owner (the bout's
    /// TownFighter until Finish) still holds it.</summary>
    void RestorePatient(ITownAftercareHost<TId> h, ref Patient patient)
    {
        bool npcOwned = patient.Reserved;
        if (patient.Reserved) patientReservations.Remove(Receiver(h, patient.Actor));
        patient.Reserved = false;
        TId p = patient.Actor;
        bool valid = !h.IsNull(p) && h.Valid(p);
        bool nativeRestarted = false;
        if (patient.Directed && valid)
        {
            // Do not cancel bed occupancy when returning the standing orders.
            h.SetStandingOrders(p, patient.Hold, patient.Passive);
            if (patient.SpeedDirected) h.RestoreMoveSpeed(p, patient.Speed);
            if (npcOwned && !h.IsDead(p) && !h.IsUnconscious(p) && !h.IsCarried(p) && !h.InBed(p) && !h.InCombatMode(p))
            {
                h.ClearOrders(p);
                nativeRestarted = true;
            }
        }
        if (npcOwned) h.ReleaseHold(p, HoldOwner.AftercarePatient);
        if (!patient.ReleaseLogged && valid)
        {
            var r = h.ReadForRelease(p);
            static string B(bool b) => b ? "1" : "0";
            static string F3(float f) => CMath.FormatF(f, 3);
            static string F1(float f) => CMath.FormatF(f, 1);
            h.Debug("Proving Grounds: aftercare fighter released name=" + h.Name(p) + " actor=" + h.ActorText(p) +
                " npcOwned=" + B(npcOwned) + " nativeRestarted=" + B(nativeRestarted) + " dead=" + B(r.Dead) + " ko=" + B(r.Unconscious) +
                " combat=" + B(r.InCombat) + " carried=" + B(r.Carried) + " inBed=" + B(r.InBed) + " hold=" + B(r.Hold) + " passive=" + B(r.Passive) +
                " aidFlesh=" + F3(r.AidFlesh) + " aidRepair=" + F3(r.AidRepair) + " bleed=" + F3(r.BleedRate) +
                " wounds=" + r.Wounds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " blood=" + F3(r.RecoveryRead ? r.Blood : -1.0f) + " lowestLimb=" + F3(r.RecoveryRead ? r.LowestLimb : -1.0f) +
                " recoveryRead=" + B(r.RecoveryRead) + " nativeGoal=" + B(r.NativeGoal) +
                " position=(" + F1(r.Position.X) + "," + F1(r.Position.Y) + "," + F1(r.Position.Z) + ")");
        }
        else if (!patient.ReleaseLogged)
        {
            h.Debug("Proving Grounds: aftercare fighter released actor=" + h.ActorText(p) + " valid=0");
        }
        patient.ReleaseLogged = true;
        patient.Directed = false;
        patient.SpeedDirected = false;
    }

    /// <summary>C++: TownAftercare.cpp:1384-1385 (the completion status).</summary>
    public const string CareCompleteText = "Arena care complete; medics returning to hospital";
}
