using System.Numerics;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Task 8.4b (TW §2.9 "After the fight", CAP 12, spike S8): the host's reads and orders for the treatment and transport (Core/Glue/
// TownAftercareLogic.Treat.cs). Engine calls of the C++ in task 8.4b's ranges (E1) and what replaces them:
//   SparSession::IsActive / GetMode / IsEliminated (Treat :1296-1305) ... SparSession (the world's)
//   ou->getObjectsWithinSphere(arena, 175, CHARACTER, 256, NULL), ArenaIdentity::GetStringId (AdmitArenaStragglers :603-614) ... PgEngine
//       CharactersNear (max 256) / RecordId
//   inSomething == IN_BED, isCrippled, getRace()->robot ... PgEngine InBed / IsCrippled / IsRobot
//   getMedical, scoreFirstAidNeed(robot), currentBleedRate, wounds.size(), blood / getMaxBlood(), anatomy[i] (flesh, fleshStun, bandaging,
//       maxHealth, isDead, isRobotic) ... PgEngine HasMedical / AidNeed / BleedRate / WoundCount / BloodRatio / MedicalParts
//   ArenaMedical::ActionableAidNeed, RosterStatus::Read ... ArenaMedical.ActionableAidNeed, PgEngine.RosterHealth
//   Inventory::getBestItemWithFunction(ITEM_FIRSTAID | ITEM_ROBOTREPAIR)->chargesLeft (HasAidKit :439-442) ... PgEngine.HasAidKit
//   get/setStandingOrder(M_SET_ORDER_HOLD / PASSIVE) ... PgEngine HoldPassive / SetStandingOrders
//   clearOrders(receiver) + clearAllAIGoals (DirectPatient :285, :292) and the native restart (RestorePatient :234-239) ... the world's hold
//       registry with the owner AftercarePatient (src/Engine/Holds.cs, spike S10); the restart's clearOrders also PgEngine.ClearOrders (task 6.2:
//       OrderQueue.Clear + TaskStart.End(Stopped), serial step)
//   addOrder(NULL, FIRST_AID_ORDER / FIRST_AID_ROBOT, p, ...) ... PgEngine.StartTreat (task 4.7: CharacterTask.Treat, rows 25 / 60, the patient
//       as Target; FirstAidSystem.Apply picks the kit itself; source Goal kept: the source is read only for a GoTo walk, TaskStart.OnAiWalk :391-395,
//       and never for Treat)
//   addOrder(NULL, LIFT_PERSON_PLAYER_ORDER, p, ...) ... PgEngine.BeginLift (task 6.3: CarrySystem.BeginPickUp, a PickUp task, source Goal kept for
//       the same reason; the lift ends when cold.Carrying == the patient, spike S8)
//   addOrder(targetBed, PUT_SOMEONE_IN_BED, ...) / p->addOrder(targetBed, USE_BED_ORDER, ...) ... Beds.PutIn / Beds.UseBed (TownAftercare.Beds.cs)
//   addOrder(NULL, MOVE_CUS_ORDERED, NULL, false, true, pos) + both setDestination calls ... PgEngine.MoveTo (task 8.4a)

sealed partial class TownAftercare
{
    sealed partial class Host
    {
        public bool SparTeams1v1Active => owner.spar() is { } s && s.IsActive(W) && s.GetMode() == MatchRules.MatchMode.Teams1v1;
        public bool SparIsEliminated(CharacterId c) => owner.spar() is { } s && s.IsEliminated(W, c);
        public string RecordId(CharacterId c) => PgEngine.RecordId(W, c) ?? "";
        public IReadOnlyList<CharacterId>? CharactersInSphere(Vector3 origin, float radius, int max) => PgEngine.CharactersNear(W, origin, radius, max);
        public bool InBed(CharacterId c) => PgEngine.InBed(W, c);
        public bool IsCrippled(CharacterId c) => PgEngine.IsCrippled(W, c);
        public bool IsRobot(CharacterId c) => PgEngine.IsRobot(W, c);
        public bool HasMedical(CharacterId c) => PgEngine.HasMedical(W, c);
        public float AidScore(CharacterId c, bool robot) => PgEngine.AidNeed(W, c, robot) is var s && float.IsFinite(s) ? s : 0f;
        public float ActionableAidNeed(CharacterId c, bool robot) => ArenaMedical.ActionableAidNeed(W, c, robot);
        public float BleedRate(CharacterId c) => PgEngine.BleedRate(W, c);
        public uint WoundCount(CharacterId c) => (uint)PgEngine.WoundCount(W, c);
        public float BloodRatio(CharacterId c) => PgEngine.BloodRatio(W, c) is var r && float.IsFinite(r) ? r : 1f;
        // one reusable list (the logic reads it at once and never keeps it across host calls: ITownAftercareHost.Parts)
        readonly List<AftercarePart> parts = [];
        public IReadOnlyList<AftercarePart> Parts(CharacterId c) { PgEngine.MedicalParts(W, c, parts); return parts; }
        public bool RosterRead(CharacterId c, out RosterStatus.Snapshot recovery) => PgEngine.RosterHealth(W, c, out recovery, out _);
        public bool HasAidKit(CharacterId c, bool robot) => PgEngine.HasAidKit(W, c, robot);
        public (bool Hold, bool Passive) StandingOrders(CharacterId c) => PgEngine.HoldPassive(W, c);
        public void SetStandingOrders(CharacterId c, bool hold, bool passive) => PgEngine.SetStandingOrders(W, c, hold, passive);
        public bool StartTreat(CharacterId medic, CharacterId patient, bool robot) => PgEngine.StartTreat(W, medic, patient, robot);
        public bool BeginLift(CharacterId medic, CharacterId patient) => PgEngine.BeginLift(W, medic, patient);
        public void ClearOrders(CharacterId c) => PgEngine.ClearOrders(W, c);   // RestorePatient :236 clearOrders: the queue emptied, the task ended
    }
}
