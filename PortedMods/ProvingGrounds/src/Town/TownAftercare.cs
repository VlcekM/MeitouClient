using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Port of upstream/ProvingGrounds/plugin/src/TownAftercare.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.4 (TW §2.9, CAP 12, 19, spike S10).
// The C++ decisions are Core/Glue/TownAftercareLogic*.cs (fake-tested, Tests/Glue/TownAftercareLogicTests.cs) over ITownAftercareHost; this class
// binds them to a world and to CharacterIds. Its parts, by the plan's partial files: this file = the members every caller uses and the host's
// character reads; TownAftercare.Setup.cs (task 8.4a) = the host's buildings, beds and orders and the test verb; TownAftercare.Treat.cs /
// .Beds.cs (task 8.4b) = the treatment's reads and orders (their own E1 lists); TownAftercare.Recovery.cs (task 8.4c) = the bed recovery's reads and orders. The state (C++ file-level globals, TownAftercare.cpp:27-157) is the logic's, one object per world: TownArena builds this object
// (it is built again for every world) and clears it through its AbandonWorldState chain (TownArena.cpp:1800-1806 -> AbandonWorldState :1152-1154).
//
// Engine calls of the C++ in task 8.4a's ranges (E1) and what replaces them:
//   ou->player->getAllPlayerCharacters() (Player :158-163) ... PgEngine.IsPlayer (CharacterCold.IsPlayer)
//   Character::isValid / getOrdersReciever / isDead / isUnconcious / isBeingCarried / isInCombatMode(true,true) / isCarryingSomething ... PgEngine
//       Exists / (every character has one) / IsDead / IsUnconscious / IsCarried / InCombatMode / IsCarrying
//   ArenaMedical::NeedsStabilization ... ArenaMedical.NeedsStabilization (task 4.7)
//   Character::getName, hand::toString ... PgEngine.Name, CharacterIdText.Format
//   Character::getPosition, CharMovement::isCurrentlyMoving / destination ... PgEngine.Position, PgEngine.MoveDestination (no height: the character's)
//   getMovementSpeedOrders / setDesiredSpeedOrders + restoreDesiredSpeed / setDesiredSpeed(RUN) ... PgEngine.RunSpeedOf / SetRunSpeedMode /
//       SetRunSpeed (CharacterCold.RunSpeed; Kenshi's RUN = SpeedMode.Free)
//   addOrder(MOVE_CUS_ORDERED, point) + movement->setDestination + setDestination ... PgEngine.MoveTo (one MoveOrder for the next tick, CAP 5; a
//       player-order walk, not a Goal-sourced one: a conversation does not stop it, TaskStart.cs:394)
//   the reservation tables' checkOrders hook (InstallHooks :1080-1103, CheckMedicOrders :97-115) and TownMedicAIPolicy PrepareOwnedOrder /
//       ResumeNative (clearOrders, reThinkCurrentAIAction, clearAllAIGoals, chooseGoal, callForUpdate) ... the world's hold registry
//       (src/Engine/Holds.cs: Engaged, spike S10) with the owner AftercareMedic; ControlAvailable = its members resolve; currentGoal ...
//       PgEngine.HasAiGoal (AiMind.Goal)
//   dropCarriedObject(true, false) (DropPatient :196), getCarryingObject ... PgEngine.LetGo (CarrySystem.LetGo: the body let go where the
//       medic stands), PgEngine.CarryingOf (CharacterCold.Carrying)
//   the release line's reads (:242-260): getStandingOrder(HOLD / PASSIVE), inSomething == IN_BED, ArenaMedical::ActionableAidNeed,
//       currentBleedRate, wounds.size(), RosterStatus::Read, currentGoal ... PgEngine.HoldPassive / InBed, ArenaMedical.ActionableAidNeed,
//       PgEngine.BleedRate / WoundCount / RosterHealth / HasAiGoal
//   RecordBedEntry (the setBedMode hook) ... none: task 8.4b reads the patients it sent to a bed at each poll (TownAftercareLogic.Beds.cs), task
//       8.4c scans the hospital's beds (TownAftercare.Recovery.cs)
//   ou->getObjectsWithinSphere(arena, 2500, BUILDING, 1024, NULL), Building::getTown, ArenaIdentity::GetStringId ... PgEngine.BuildingsNear +
//       ArenaIdentity.From (as TownArena's host), PlacedBuilding.Town
//   HospitalBed / Occupied / bed->getOccupant / inSomething == IN_BED ... src/Engine/Beds.cs (the hospital's bed fixtures)
//   PGLog::Debug / NextId ... PGLog.Debug / NextId (texts byte for byte; floats through CMath.FormatF)
//   GetTickCount, ou->isPaused ... PgClock.NowMs, false (a paused world runs no serial step)

/// <summary>The TownAftercare of one world: the logic and its state, bound to a world for each call (sim thread, serial steps only).</summary>
sealed partial class TownAftercare
{
    readonly PopulationData data;
    readonly PgClock clock;
    readonly Holds holds;
    readonly Towns towns;   // task 8.4c: Scratch's site, for the hospital beds the bed recovery scans
    readonly Func<SparSession?> spar;   // task 8.4b: Teams 1v1 early care (read lazily: ProvingGroundsSystem builds it after TownArena)
    readonly Host host;
    readonly TownAftercareLogic<CharacterId> logic = new(CharacterId.None, Holds.ControlAvailable);

    internal TownAftercare(PopulationData data, PgClock clock, Holds holds, Towns towns, Func<SparSession?> spar)
    {
        this.data = data;
        this.clock = clock;
        this.holds = holds;
        this.towns = towns;
        this.spar = spar;
        host = new Host(this);
    }

    internal TownAftercareLogic<CharacterId> Logic => logic;

    ITownAftercareHost<CharacterId> H(World world) => host.Bind(world);

    // ---------- the public calls (TownAftercare.h) ----------
    internal bool ControlAvailable() => logic.ControlAvailable();                                            // C++ :1051
    internal string GetStatus() => logic.GetStatus();                                                       // C++ :1104
    internal bool IsManaged(World world, CharacterId c) => logic.IsManaged(H(world), c);                     // C++ :1105-1111
    internal void MarkWinner(World world, CharacterId c) => logic.MarkWinner(H(world), c);                   // C++ :1112-1120
    internal void Release(World world) => logic.Release(H(world));                                           // C++ :1147-1151
    internal void ResetAfterFailedRelease() => logic.ResetAfterFailedRelease();                              // port only (a Release that threw)
    internal void AbandonWorldState() => logic.AbandonWorldState();                                          // C++ :1152-1154
    internal void Begin(World world, IReadOnlyList<CharacterId> doctors, IReadOnlyList<CharacterId> fighters, ArenaBuildingRef arena,
        Vector3 standby) => logic.Begin(H(world), doctors, fighters, arena, standby);                         // C++ :1155-1212
    internal void Standby(World world) => logic.Standby(H(world));                                          // C++ :1213-1226
    internal bool Treat(World world, float elapsed, bool eliminatedOnly) => logic.Treat(H(world), elapsed, eliminatedOnly);   // C++ :1293-1401 (task 8.4b)
    internal void TickBedRecovery(World world) => logic.TickBedRecovery(H(world));                         // C++ :1227-1292 (task 8.4c)
    /// <summary>C++ :1035-1050 RecordTreatment (task 8.4c): from the FirstAidSystem.Apply prefix and postfix (src/Game/ArenaMedical.cs), serial
    /// commit step; no world read, no allocation.</summary>
    internal void RecordTreatment(CharacterId medic, CharacterId patient, bool doctoring, bool blocked, bool complete, float frameTime) =>
        logic.RecordTreatment(medic, patient, doctoring, blocked, complete, frameTime);

    /// <summary>C++ :1052-1059 ReserveBenchOrders, then the port's hold (an NPC is held by the bench owner; a player character is not held, the AI
    /// never runs one). A hold that cannot be taken (stale id) gives the reservation back.</summary>
    internal bool ReserveBenchOrders(World world, CharacterId c)
    {
        var h = H(world);
        if (!logic.ReserveBenchOrders(h, c)) return false;
        if (h.Hold(c, HoldOwner.Bench)) return true;
        logic.ReleaseBenchOrders(h, c);
        return false;
    }

    /// <summary>C++ :1060-1064 ReleaseBenchOrders, then the bench's hold given back.</summary>
    internal void ReleaseBenchOrders(World world, CharacterId c)
    {
        var h = H(world);
        logic.ReleaseBenchOrders(h, c);
        h.ReleaseHold(c, HoldOwner.Bench);
    }

    /// <summary>C++ :1065-1074 ReserveExternalOrders (the announcer, limb shop and corpse cleanup share its four slots; tasks 8.3, 8.5, 8.6), then
    /// the port's hold with the owner External.</summary>
    internal bool ReserveExternalOrders(World world, CharacterId c)
    {
        var h = H(world);
        if (!logic.ReserveExternalOrders(h, c)) return false;
        if (h.Hold(c, HoldOwner.External)) return true;
        logic.ReleaseExternalOrders(h, c);
        return false;
    }

    /// <summary>C++ :1075-1079 ReleaseExternalOrders, then the External hold given back.</summary>
    internal void ReleaseExternalOrders(World world, CharacterId c)
    {
        var h = H(world);
        logic.ReleaseExternalOrders(h, c);
        h.ReleaseHold(c, HoldOwner.External);
    }

    /// <summary>ITownAftercareHost over one world, rebound for every call.</summary>
    sealed partial class Host(TownAftercare owner) : ITownAftercareHost<CharacterId>
    {
        World? world;

        internal Host Bind(World w) { world = w; return this; }

        World W => world ?? throw new InvalidOperationException("TownAftercare used before a world was bound");

        // ---------- the world ----------
        public bool IsPaused => false;   // a paused world runs no SlowWorld (World.Interface.cs:16-40)
        public uint NowMs => owner.clock.NowMs;
        public void Debug(string line) => PGLog.Debug(line);
        public uint NextLogId() => PGLog.NextId();

        // ---------- characters ----------
        public CharacterId None => CharacterId.None;
        public bool IsNull(CharacterId c) => c.IsNone;
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool IsPlayer(CharacterId c) => PgEngine.IsPlayer(W, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c);
        public bool IsCarried(CharacterId c) => PgEngine.IsCarried(W, c);
        public bool IsCarrying(CharacterId c) => PgEngine.IsCarrying(W, c);
        public bool InCombatMode(CharacterId c) => PgEngine.InCombatMode(W, c);
        public bool NeedsStabilization(CharacterId c) => ArenaMedical.NeedsStabilization(W, c);
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        public string ActorText(CharacterId c) => CharacterIdText.Format(c);
        // Vector3.Zero for a stale id: the logic reads a position only after Valid(c)
        public Vector3 Position(CharacterId c) => PgEngine.Position(W, c) ?? Vector3.Zero;
        public bool TryGetMoveDestination(CharacterId c, out Vector3 destination)
        {
            if (PgEngine.MoveDestination(W, c) is { } d) { destination = d; return true; }
            destination = Vector3.Zero;
            return false;
        }
        public bool HasAiGoal(CharacterId c) => PgEngine.HasAiGoal(W, c);
        public CharacterId CarryingOf(CharacterId c) => PgEngine.CarryingOf(W, c);
        public AftercarePatientRead ReadForRelease(CharacterId c)
        {
            var (hold, passive) = PgEngine.HoldPassive(W, c);
            bool read = PgEngine.RosterHealth(W, c, out var recovery, out _);
            return new AftercarePatientRead(PgEngine.IsDead(W, c), PgEngine.IsUnconscious(W, c), PgEngine.InCombatMode(W, c), PgEngine.IsCarried(W, c),
                PgEngine.InBed(W, c), hold, passive, ArenaMedical.ActionableAidNeed(W, c, false), ArenaMedical.ActionableAidNeed(W, c, true),
                PgEngine.BleedRate(W, c), (uint)PgEngine.WoundCount(W, c), read, recovery.Blood, recovery.LowestLimb, PgEngine.HasAiGoal(W, c),
                PgEngine.Position(W, c) ?? Vector3.Zero);
        }
    }
}
