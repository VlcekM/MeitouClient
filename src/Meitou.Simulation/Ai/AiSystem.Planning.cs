using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The AI's planning: the <see cref="PlanContext"/> each think plans through and the planner's targets.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// Puts the way through a locked door (the lock system's, <see cref="Locks.LockSystem.StartDoorWay"/>) at the front of the NPC's kept plan as the
    /// step the original's planner adds there (docs/game/locks.md "Locked doors on the way": 78 / 80 unlocking, 81 / 287 bashing, 76 picking, 285
    /// cutting), on the door at <paramref name="door"/>: the plan step ends it by its finished test and list A and any choice may take over
    /// (<see cref="PlanSteps"/>). False when the character has no kept plan to put it in (an engine-run mind, a goal that is its own plan): the lock
    /// system then holds it.
    /// </summary>
    internal static bool TakeDoorStep(CharacterCold cold, TaskType row, Vector3 door)
    {
        if (row == TaskType.None || cold.Ai is not { Engine: false, Goal: { } goal } mind) return false;
        if (mind.Plan.Count == 0 || !PlanContext.Stepped(goal)) return false;

        if (!IsDoorStep(mind.Plan[0].Type)) mind.Plan.Insert(0, new PlanStep(row, new PlanTarget(Targets.Door, door)));
        return true;
    }

    /// <summary>
    /// Whether the NPC's kept plan runs a door step now (<see cref="TakeDoorStep"/>): the lock system works the way only while it does.
    /// </summary>
    internal static bool RunsDoorStep(CharacterCold cold) => cold.Ai is { Plan: [var step, ..] } && IsDoorStep(step.Type);

    /// <summary>
    /// The rows of a way through a door (<see cref="TakeDoorStep"/>).
    /// </summary>
    static bool IsDoorStep(TaskType type) => type is TaskType.UnlockDoor or TaskType.UnlockDoorHere or TaskType.BashDoor or TaskType.BashDoorHere
        or TaskType.PickLock or TaskType.CutLock;

    /// <summary>
    /// Kinds of <see cref="PlanTarget"/> the finders give.
    /// </summary>
    static class Targets
    {
        /// <summary>
        /// The target kinds: a spot, bed, seat, turret, town, work place, machine and store; the character a follow goal follows (its place is read
        /// live, <see cref="PlanContext.Followed"/>); the door a way through works (<see cref="TakeDoorStep"/>, the door read live from
        /// <see cref="CharacterCold.Bashing"/>).
        /// </summary>
        public const byte Spot = 1, Bed = 2, Seat = 3, Turret = 4, Town = 5, Work = 6, Machine = 7, Store = 8, Follow = 9, Door = 10;
    }

    /// <summary>
    /// How near a home's position counts as at home (units; engine stand-in for the original's inside-the-building test).
    /// </summary>
    const float HomeRadius = 20;

    /// <summary>
    /// The steps the engine carries out besides a goal's own (docs/simulation.md "Planner"): moving and running (the goal's walk), travelling to the
    /// mission town (the travel goal's walk), standing at a node, shopping, sleeping, manning a turret, operating or pretending to (sitting at the
    /// seat), sheathing and getting out of bed, drawing a weapon, going home and getting near a target (walks of their own before the goal's
    /// act-out). A plan ending in another step fails.
    /// </summary>
    static bool Carried(TaskType step) => step is TaskType.MoveOnFreeWill or TaskType.MoveOnFreeWillFast or TaskType.TravelToTargetTown
        or TaskType.TravelToTargetTownFast or TaskType.StandAtNode or TaskType.BuyShit
        or TaskType.UseBed or TaskType.UseBedOrder or TaskType.UseTurret or TaskType.OperateMachinery or TaskType.CollectOutputResource
        or TaskType.FillMachine or TaskType.PretendToOperateMachinery or TaskType.UnequipWeapon
        or TaskType.GetOutOfBed or TaskType.GetOutOfBedIfItsEmergency or TaskType.GetOutOfBedOnceHealed
        or TaskType.EquipWeapon or TaskType.EquipCrossbow or TaskType.GoHomebuilding or TaskType.GetNearTo
        or TaskType.EnterBuilding or TaskType.LeaveBuilding;

    /// <summary>
    /// One character's view for the <see cref="TaskPlanner"/> (one per worker thread, bound per think): its states (docs/simulation.md "Planner"
    /// lists the stand-ins), the targets of the goals Meitou acts out, and the desires of the rows on the way.
    /// </summary>
    sealed class PlanContext : IPlanWorld
    {
        /// <summary>
        /// The worker thread's context (<see cref="Local"/>).
        /// </summary>
        [ThreadStatic] static PlanContext? s_local;
        /// <summary>
        /// The calling worker thread's context, made on its first use.
        /// </summary>
        public static PlanContext Local => s_local ??= new PlanContext();

        /// <summary>
        /// The AI system it is bound to.
        /// </summary>
        AiSystem _system = null!;
        /// <summary>
        /// The world it is bound to.
        /// </summary>
        World _world = null!;
        /// <summary>
        /// The mind of the bound character.
        /// </summary>
        AiMind _mind = null!;
        /// <summary>
        /// The blackboard of the bound character's squad.
        /// </summary>
        Blackboard _board = null!;
        /// <summary>
        /// The bound character's slot.
        /// </summary>
        int _slot;
        /// <summary>
        /// A scratch step list of the bound think.
        /// </summary>
        readonly List<PlanStep> _scratch = [];
        /// <summary>
        /// The worker thread's functionality cache for the gate test of state 131 (<see cref="TownExit.HasGates"/>).
        /// </summary>
        readonly Dictionary<string, Meitou.Data.GameRecord?> _gateFunctions = new(StringComparer.Ordinal);

        /// <summary>
        /// State 131's reaches (<c>FUN_140506d30</c>, <b>Verified</b>): a gated town's point outside its gates (squared 62500,
        /// <c>DAT_1416e3460</c>), a character target that no longer resolves (22500, <c>DAT_1416e345c</c>) and the point before its building's door
        /// (10000, <c>DAT_14168dae4</c>), as distances.
        /// </summary>
        const float GatesReach = 250, LostTargetReach = 150, DoorReach = 100;
        /// <summary>
        /// State 131's share of an ungated town's radius (<c>DAT_141696c90</c>) and how far out its door point lies, A + (A − B) × 3
        /// (<c>DAT_14168a254</c>).
        /// </summary>
        const float TownShare = 0.4f, DoorBeyond = 3;

        /// <summary>
        /// The task repertoire it plans with, the original's <c>FUN_14060e840</c> order (<see cref="Repertoire.Of"/>: imprisoned 4, crippled 0x40,
        /// chained 0x10, human 1, else animal 0x20). The platoon repertoire (2) is not modelled.
        /// </summary>
        public int Category => _world.Characters.Cold(_slot) is { } c ? Repertoire.Of(_world, c) : Repertoire.Human;

        /// <summary>
        /// Restricted rows are offered: the character's faction is not the player's (the name is the old reading, kept; see
        /// <see cref="IPlanWorld.Squadless"/>).
        /// </summary>
        public bool Squadless => _restrictedOpen;
        /// <summary>
        /// Whether restricted rows are open (set by <see cref="Bind"/>: the character is not of the player's side).
        /// </summary>
        bool _restrictedOpen;

        /// <summary>
        /// Of the player's faction (the character or its faction): the side whose characters can be fully rested (state 72).
        /// </summary>
        bool _playerSide;

        /// <summary>
        /// Binds the context to the character at slot <paramref name="at"/> of <paramref name="w"/> and sets which rows are open to it.
        /// </summary>
        public PlanContext Bind(AiSystem s, World w, int at, AiMind m, Blackboard b)
        {
            (_system, _world, _slot, _mind, _board) = (s, w, at, m, b);

            // Restricted rows are open to every plan that is not the player faction's (FUN_14051bae0, Verified; the flagged search is the orders
            // step, not planned here).
            int player = w.Player.Faction >= 0 ? w.Player.Faction : s.Data.PlayerFaction;
            var cold = w.Characters.Cold(at);
            _playerSide = cold is not null && (cold.IsPlayer || player >= 0 && cold.Faction == player);
            _restrictedOpen = cold is not null && !_playerSide;
            return this;
        }

        /// <summary>
        /// Plans a goal; true (and the plan in <see cref="AiMind.Plan"/>) when it has one whose first step the engine carries out; a failed plan leaves
        /// the mind's plan as it was. A goal without a TaskData row is its own plan; one whose list A already holds is left out when its class's
        /// finished test agrees (the scorer's done test), but for the <paramref name="current"/> goal, which the scorer does not test.
        /// </summary>
        public bool TryPlan(AiGoal goal, float desire, bool current = false)
        {
            var type = goal.Task.Type;

            // The assault is its own plan (engine choice): its finder's target is not a plan target, the combat system draws weapons and closes in,
            // and its only list B entry (!47, not carrying) is what Holds already requires of a character the AI runs. So are the combat goals
            // (engine choice): the desire already holds the finder's pick (and for the protect goals row 13's attacker), and the fight is the combat
            // system's. And the Medic goal: its desire holds the patient finder's score, and the treatment is the first aid system's. And the leader
            // rescue (142: its finder and its done state, the leader down, are the desire's; the lift is the carry system's), and the rescue to a bed
            // (148: its desire and finder are kept by the carry system's sweep, which lifts the body). And a captive goal: its desire holds what the
            // job start found, and the job is the captive system's.
            if (EngineActs(type))
            {
                _mind.Plan.Clear();
                _mind.Plan.Add(new PlanStep(type, default));
                return true;
            }

            // Self preservation (the planner's choice between its providers 13 and 35, SelfPreservation.Runs): [35, 32] when 35 wins, else [13, 32]
            // when row 13's finder takes an attacker of its own (AI::findAttackerOf on itself, AiSystem.SelfDefencePick; the fight is the combat
            // system's, as the combat goals'); with neither the next goal is tried.
            if (type == TaskType.SelfPreservation)
            {
                if (_world.Characters.Cold(_slot) is not { } me) return false;
                bool runs = SelfPreservation.Runs(me, desire, SelfPreservation.Walled(_world, _slot));
                if (!runs && _system.SelfDefencePick(_world, _slot).IsNone) return false;
                _mind.Plan.Clear();
                _mind.Plan.Add(new PlanStep(runs ? TaskType.RunAway : TaskType.AttackAttackersOf, default));
                _mind.Plan.Add(new PlanStep(type, default));
                return true;
            }

            // A follow goal aimed at the squad's mission (44 of a bodyguard contract) is its own plan too (engine choice): the row needs a target
            // and has no finder, its target being the goal's own, the contract's mission target, which is a character and no place to plan a
            // walk to. The following is the movement system's.
            if (TaskBehaviours.Of(type) == AiBehaviour.Follow && goal.Task.Target == TaskTargetType.SquadMission)
            {
                if (!_world.Characters.IsAlive(_board.MissionTarget)) return false;

                _mind.Plan.Clear();
                _mind.Plan.Add(new PlanStep(type, default));
                return true;
            }

            if (TaskDataTable.Of(type) is not { } row)
            {
                _mind.Plan.Clear();
                _mind.Plan.Add(new PlanStep(type, default));
                return true;
            }

            // The planner looks the root row up in the character's repertoire: a row it lacks plans nothing (docs/game/ai-tasks.md "Leaving the
            // night cage"), so an animal of a bar squad does not relax (214 is human only).
            if ((row.Category & Category) == 0) return false;

            var target = GoalTarget(row);
            if (row.Is(TaskDataFlags.NeedsTarget) && !row.Is(TaskDataFlags.Finder) && target.IsNone) return false;

            // A goal whose list A holds is done (FUN_14060f570) when a fresh task of its class says it is finished (slot 4, FUN_14050df10). The scorer
            // asks it of a goal that is not the current one only.
            if (!current && row.Done.Length > 0 && TaskFinished.Fresh(_world, _slot, type, CharacterId.None)
                && Array.TrueForAll(row.Done, c => Holds(c, target)))
            {
                return false;
            }
            if (TaskPlanner.Local.Plan(row, desire, target, this, _mind.Plan, _scratch) <= 0) return false;
            if (_scratch[0].Type != type && !Carried(_scratch[0].Type)) return false;
            _mind.Plan.Clear();
            _mind.Plan.AddRange(_scratch);
            return true;
        }

        /// <summary>
        /// Whether goal <paramref name="type"/> is its own plan, acted out by an engine system (the assault, the combat goals, Medic, the leader
        /// rescue, the rescue to a bed, the captive goals: <see cref="TryPlan"/>).
        /// </summary>
        static bool EngineActs(TaskType type) => TaskBehaviours.Of(type) is AiBehaviour.Assault or AiBehaviour.Fight or AiBehaviour.Medic
            or AiBehaviour.RescueLeader or AiBehaviour.RescueToBed or AiBehaviour.Captive;

        /// <summary>
        /// Whether a goal's plan is the planner's, run step by step (<see cref="PlanSteps"/>): not one of the plans of its own (<see cref="TryPlan"/>:
        /// the engine-run goals, Self preservation, a follow aimed at the squad's mission), and of a row that acts or has an effect (a row with
        /// neither, Idleness among them, makes no task: the original's update drops such a goal at once, <c>FUN_140511130</c>).
        /// </summary>
        public static bool Stepped(AiGoal goal)
        {
            var type = goal.Task.Type;
            if (EngineActs(type) || type == TaskType.SelfPreservation) return false;
            if (TaskBehaviours.Of(type) == AiBehaviour.Follow && goal.Task.Target == TaskTargetType.SquadMission) return false;

            return TaskDataTable.Of(type) is { } row && (row.Is(TaskDataFlags.Acts) || row.Effect is not null);
        }

        /// <summary>
        /// A goal row's own target: its finder's when it needs one and has none of its own; the followed character for a follow row without one
        /// (141, 171: state 59 reads the building the follower's walk heads into, <c>FUN_140793230</c> with the task's hand, else the mover's
        /// destination, which following puts by the followed character); else none.
        /// </summary>
        PlanTarget GoalTarget(TaskDataRow row)
        {
            if (row.Is(TaskDataFlags.NeedsTarget)) return row.Is(TaskDataFlags.Finder) ? default : Find(row, default);
            if (TaskBehaviours.Of(row.Type) != AiBehaviour.Follow) return default;

            return Followed() is >= 0 and var at ? new PlanTarget(Targets.Follow, _world.Characters.Next[at].Position) : default;
        }

        /// <summary>
        /// The slot of the character the bound one follows (its slave owner, else its squad leader, as <c>FollowFactor</c> resolves it); -1 for none.
        /// </summary>
        public int Followed()
        {
            var cold = _world.Characters.Cold(_slot)!;
            var target = Kidnap.ObedientSlave.OwnerOf(_world, cold) ?? _mind.Squad.Leader;
            return _world.Characters.TryResolveNext(target, out int at) && at != _slot ? at : -1;
        }

        /// <summary>
        /// States 59 DESTINATION_IS_ACCESSIBLE and 168 DESTINATION_IS_ACCESSIBLE_ANIMAL for the target's building (<see cref="DestinationBuilding"/>),
        /// providers folded in (<see cref="DestinationAccess.Accessible"/>): a walk into a building shut by another's locked doors does not plan.
        /// </summary>
        bool Accessible(in PlanTarget target, bool animalDefault) =>
            DestinationAccess.Accessible(_world, _slot, DestinationBuilding(target), animalDefault, _restrictedOpen);

        /// <summary>
        /// The building a target lies in (<c>FUN_140793230</c>): the followed character's; none for a town (a town's own place is outdoors); else the
        /// room at the target's point, a point with no floor height taken on the ground (a workplace, a store, the home); null outdoors. Engine
        /// reading: for a point the original reads the building of the mover's destination (Character <c>_destinationInsideBuilding</c>), which is
        /// the target's once the walk there is set.
        /// </summary>
        string? DestinationBuilding(in PlanTarget target)
        {
            if (target.Kind == Targets.Follow) return Followed() is >= 0 and var at ? _world.Sight.BuildingOf(at) : null;
            if (target.IsNone || target.Kind == Targets.Town) return null;

            return DestinationAccess.BuildingAt(_world, target.Position);
        }

        /// <summary>
        /// 131 atPackageTarget (<c>FUN_140506d30</c>, <b>Verified</b>; row 215's list A), at the squad's package target (<see cref="PackageTarget"/>):
        /// a character as <see cref="AtCharacter"/>; no target, or a town that is not there, holds; a town with gates
        /// (<see cref="TownExit.HasGates"/>) within 250 of its point outside the gates (Town vfunc 0x38, <see cref="TownExit"/>), any other within
        /// 0.4 of its radius.
        /// </summary>
        bool AtPackageTarget()
        {
            var (town, character) = _system.PackageTarget(_world, _mind.Squad, _board);
            if (!character.IsNone) return AtCharacter(character);
            if (TownPlace(_system.Data, _world, town) is not { } placed) return true;

            var here = Flat(Me.Position);
            if (!TownExit.HasGates(_world, _system.Data, town, _gateFunctions))
                return Vector2.DistanceSquared(here, placed.Centre) < placed.Radius * TownShare * (placed.Radius * TownShare);

            var outside = TownExit.Of(_world, _system.Data, _system._locks?.Regions, town, _gateFunctions);
            return Vector2.DistanceSquared(here, outside) < GatesReach * GatesReach;
        }

        /// <summary>
        /// 131 for a character target: one that no longer resolves is the hand's own point, which the package hands leave at the origin; else (state
        /// 90 held, as <see cref="HeldTrue"/>, so the original's 1.1-radius test behind walls is not reached) at it when the building the target is in
        /// lets the character in as state 59's own test reads it (<see cref="DestinationAccess.OpenTo"/>; outdoors, or the same building, holds), else
        /// within 100 of the point 3 beyond that building's first door (<see cref="DoorFront"/>).
        /// </summary>
        bool AtCharacter(CharacterId who)
        {
            var here = Flat(Me.Position);
            if (!_world.Characters.TryResolveNext(who, out int at)) return here.LengthSquared() < LostTargetReach * LostTargetReach;

            var building = _world.Sight.BuildingOf(at);
            if (building is null || DestinationAccess.OpenTo(_world, _slot, building)) return true;

            var inside = Flat(_world.Characters.Next[at].Position);
            return DoorFront(_world, building, inside, DoorBeyond) is { } front && Vector2.DistanceSquared(here, front) < DoorReach * DoorReach;
        }

        /// <summary>
        /// The desire of a row on the way: the sleeping desire for UseBed (0 drops the node), else the row's task profile desire, 1 for a random or
        /// own desire function.
        /// </summary>
        public float Desire(TaskDataRow row, in PlanTarget target)
        {
            // row 98: AI slot 30 (FUN_1405a0880); 0 drops the node
            if (row.Type == TaskType.UseBed) return _system.SleepingDesire(_world, _board, _slot);
            float d = TaskProfiles.Of(row.Type).Desire;
            return d >= 0 ? d : 1;   // a random or own desire function counts as 1 on the way
        }

        /// <summary>
        /// The bound character's next hot row.
        /// </summary>
        ref CharacterHot Me => ref _world.Characters.Next[_slot];

        /// <summary>
        /// Whether the bound character is at a target: none counts as there, as does a walk there that ended on its own; a work target by its work
        /// range, else within the target kind's slack and on the target's floor.
        /// </summary>
        bool At(in PlanTarget t)
        {
            if (t.IsNone) return true;
            var p = Me.Position;
            var mind = _world.Characters.Cold(_slot)?.Ai;
            if (mind is not null && mind.ArrivedThere(Flat(t.Position), Flat(p))) return true;   // its walk there ended on its own (isAtLocation)
            if (t.Kind == Targets.Work) return AtWork(in Me, t.Position, mind?.WorkRange ?? WorkReach);
            float slack = t.Kind == Targets.Store ? StoreSlack : t.Kind == Targets.Turret ? TurretSlack : _system.Settings.PostSlack;
            if (Vector2.DistanceSquared(Flat(p), Flat(t.Position)) > slack * slack) return false;

            // A spot on a floor counts on that floor only (NaN: no floor).
            return float.IsNaN(t.Position.Y) || MathF.Abs(p.Y - t.Position.Y) <= FloorSlack;
        }

        /// <summary>
        /// The player's machine a <see cref="Targets.Machine"/> target stands at, or a <see cref="Targets.Work"/> target's machine: an outpost
        /// workplace's real machine (its site id), else the town work machine there (matched by position; read-only, the production tick is the only
        /// writer).
        /// </summary>
        Production.Machine? MachineAt(in PlanTarget t)
        {
            if (t.Kind is not (Targets.Machine or Targets.Work)) return null;
            if (t.Kind == Targets.Work && OutpostWorkplaceAt(t.Position) is { RuntimeKey: { } site })
                return _world.Machines.Find(site);

            foreach (var m in _world.Machines.All)
            {
                if (m.Position.X == t.Position.X && m.Position.Z == t.Position.Z
                    && (t.Kind == Targets.Machine || Production.ProductionSystem.IsTownWork(m.Site)))
                    return m;
            }

            return null;
        }

        /// <summary>
        /// The workplaces of the board's town, a site's or an outpost's (none outside a town).
        /// </summary>
        IEnumerable<Meitou.Data.World.TownBuilding> Workplaces() => _system.WorkplacesOf(_board.HomeTown);

        /// <summary>
        /// The outpost workplace standing at <paramref name="at"/> when the board's town is an outpost; null otherwise.
        /// </summary>
        Meitou.Data.World.TownBuilding? OutpostWorkplaceAt(Vector3 at)
        {
            if (_board.Outpost <= 0) return null;

            foreach (var b in Workplaces())
                if (b.Position.X == at.X && b.Position.Z == at.Z) return b;

            return null;
        }

        /// <summary>
        /// The flat distance to a target (0 for none).
        /// </summary>
        float Distance(in PlanTarget t) => t.IsNone ? 0 : Vector2.Distance(Flat(Me.Position), Flat(t.Position));

        /// <summary>
        /// Whether the bound character is at a target and resting or idle.
        /// </summary>
        bool Settled(in PlanTarget t) => At(t) && (CharacterTask)Me.Task is CharacterTask.Rest or CharacterTask.Idle;

        /// <summary>
        /// A state's value for a target. States of rooms, doors, machines and fights the AI does not run into (it acts out goals only out of a fight,
        /// carrying nothing) have a fixed stand-in value; an unlisted state is false.
        /// </summary>
        public bool Holds(TaskCondition condition, in PlanTarget target)
        {
            var cold = _world.Characters.Cold(_slot)!;
            if (target.Kind == Targets.Door) return DoorHolds(condition, cold);

            bool value = condition.State switch
            {
                StateType.IsAtLocation or StateType.IsAtLocation46 or StateType.AtNode => At(target),
                StateType.DestinationIsAccessible => Accessible(target, animalDefault: false),
                StateType.DestinationIsAccessibleAnimal => Accessible(target, animalDefault: true),
                StateType.IsWithin10Meters => Distance(target) < Within10Meters,
                StateType.IsWithin50Meters => Distance(target) < Within50Meters,
                StateType.IsNearTo => Distance(target) <= 10,
                // 62 (slot 149), 63 (slot 175) and 64 (62 or 63) of a machine target (FUN_1405974e0, FUN_1405a2ba0, FUN_1405a2e60, Verified); false
                // for any other target. 65 (any of the character's machines jammed) and 105 are not wired.
                StateType.MachineHasEmptyInputs => MachineAt(target)?.HasEmptyInputs == true,
                StateType.IsMachineOutputFull => MachineAt(target)?.OutputBlocked == true,
                StateType.IsMachineJammed => MachineAt(target)?.Jammed == true,
                // A seat target is the claimed piece: a workstation of the home's layout as the claim found it (TownPlaceClaims.Claim).
                StateType.BuildingHasPower => target.Kind == Targets.Seat
                    ? !_board.UnpoweredSeats.Contains(_slot)
                    : MachineAt(target) is not { } machine
                        || _world.SystemOf<Production.ProductionSystem>()?.MachinePowered(_world, machine) != false,
                // 82 of a machine target (FUN_14059bd80): room in the inventory or the worn backpack for a unit of its output item.
                StateType.GotInventoryRoomForResources => MachineAt(target) is { Output: { } room } && cold.Inventory is { } bag
                    && _system.Data.Items.Create(room.Recipe.Item, 1) is { } unit && bag.HasRoomFor(unit),
                // 67 for the ditch target (a storage the finder found takes a carried item, FUN_1405a59a0): the character still carries something the
                // collection lists. For a machine target (row 93 at a town machine) it carries an item an input takes (FUN_1405a45f0); 66
                // (FUN_140597530): it carries the machine's output item.
                StateType.HaveSomeResourcesFor =>
                    target.Kind == Targets.Store && cold.Inventory is { } carried ? Production.ProductionSystem.Ditchable(carried).Any()
                    : MachineAt(target) is { } fillable && Production.ProductionSystem.CarriesInputOf(cold, fillable),
                StateType.HaveSomeResourcesFrom => MachineAt(target) is { } emptiable && Production.ProductionSystem.CarriesOutputOf(cold, emptiable),
                // 5: a weapon in hand that is not ranged; 156: a ranged one; 157: either (FUN_140596a30 / FUN_140596a70 / FUN_14060e540, Verified).
                StateType.HasWeaponEquipped => cold.DrawnWeapon != WeaponKinds.None && cold.DrawnWeapon != BowHold,
                StateType.HasWeaponEquipped156 => cold.DrawnWeapon == BowHold,
                StateType.HasWeaponEquipped157 => cold.DrawnWeapon != WeaponKinds.None,
                StateType.HasWeapon => cold.Fighter is { Weapon: not null, Unarmed: false },
                StateType.IsCarryingSomething => !cold.Carrying.IsNone,
                _ => BodyOrTownHolds(condition.State, target, cold),
            };
            return value == condition.Holds;
        }

        /// <summary>
        /// A state of a door step (<see cref="TakeDoorStep"/>) for the door its way through works: the door's own states (52 open, 53 and 54 / 55
        /// locked, the door here being that door, 56 broken) as <see cref="RowStates"/> reads them; every other state of the step's list B (at the
        /// door, the lock skill, tools, a weapon, its owner) is the way the lock system chose and works by (<see cref="Locks.LockSystem.WayThrough"/>,
        /// asked again each update; with no way left the way fails), held true.
        /// </summary>
        bool DoorHolds(TaskCondition condition, CharacterCold cold)
        {
            var state = condition.State switch
            {
                StateType.DoorIsOpenHere => StateType.DoorIsOpen,
                StateType.DoorIsLockedHere => StateType.IsDoorLocked,
                var other => other,
            };
            if (state is not (StateType.DoorIsOpen or StateType.IsDoorLocked or StateType.DoorIsBroken)) return true;

            return RowStates.Holds(_world, _slot, state, new RowTarget(CharacterId.None, cold.Bashing)) == condition.Holds;
        }

        /// <summary>
        /// The states of the body, its belongings, its home and town and its squad (<see cref="Holds"/> for the others); the rest are held
        /// true or false.
        /// </summary>
        bool BodyOrTownHolds(StateType state, in PlanTarget target, CharacterCold cold) => state switch
        {
            // 23 (FUN_140598860): the items or the worn backpack hold a first aid kit. 39 (the state-16 evaluator FUN_14059de10 asked of itself,
            // Observed): the immediate-threat flag (Character +0x2b0). 139 (FUN_140598e90): deep water (water state 3), 0 held in a bed or cage.
            // 103 (FUN_1409980c0): a leg down that could still be fixed.
            StateType.HaveFirstAidKit => FirstAid.FirstAidSystem.HoldsKit(cold, Items.ItemInfo.FirstAidKitFunction),
            StateType.IsAggressionTowardsMe => _system._combat is { } combat && CharacterFacts.ImmediateThreat(combat, _world, _slot),
            StateType.MeSwim => cold.WaterState == 3 && cold.InSomething == Locks.HeldState.None,
            StateType.MyLegsAreMessedUp => cold.Medical is { LegsMessedUp: true },
            // 121 (FUN_14059b7b0): the inventory or worn backpack holds an item stolen for it (goal 184's list A is !121; TownDutySystem acts it
            // out).
            StateType.CarryingExcessLoot => Kidnap.TownDutySystem.CarriesLoot(_world, cold),
            StateType.IsInBed => cold.InSomething == Locks.HeldState.Bed
                || (CharacterTask)Me.Task == CharacterTask.Rest && _mind.Behaviour == AiBehaviour.Sleep,
            // 123 IS_WORKING and 136 IS_WORKING_PRETEND share an evaluator that answers false (the thunk 14002fb1c, Verified), so 87 and 221 never
            // end by their list A.
            StateType.IsWorking or StateType.IsWorkingPretend => false,
            StateType.UsingTargetTurret or StateType.UsingAnyTurret => Settled(target),
            // AI::iAmInsideTargetBuilding (FUN_14059a4b0) for a target that is not a building: inside its home building (no home: held true).
            // A player-built home has no indoor volume in the sight map: within the home radius counts (engine choice).
            StateType.AmInsideTargetBuilding => _board.Home is not { } inHome
                || (inHome.RuntimeKey is null ? _world.Sight.BuildingOf(_slot) == inHome.Key
                    : Vector2.DistanceSquared(Flat(Me.Position), Flat(inHome.Position)) <= HomeRadius * HomeRadius),
            StateType.IsAtHome => _board.Home is not { } home || _mind.HomeUnreachable
                || Vector2.DistanceSquared(Flat(Me.Position), Flat(home.Position)) <= HomeRadius * HomeRadius,
            // Without a town it is never at its home town; its destination town (patrolling in town) is wherever it is.
            StateType.IsAtHomeTown => !_board.HomeTown.IsNone && _system.AtHomeTown(_world, _mind.Squad, _board, _world.Characters.IdOf(_slot)),
            // The running package's mission town when it has one (the trader's next town), else the home town.
            StateType.AtDestinationTown or StateType.AtDestinationTown94 => _board.Destination.IsNone
                ? _system.AtHomeTown(_world, _mind.Squad, _board, _world.Characters.IdOf(_slot))
                : _system.CurrentTown(_world, Me.Position) == _board.Destination,
            StateType.HasDestinationTown => !_board.HomeTown.IsNone || !_board.Destination.IsNone,
            StateType.AtATown => _system.TownAt(Me.Position) >= 0,
            // Any alarm of its town in reach (goal 210's own-faction filter is its desire's).
            StateType.AlarmsInTheVicinity => _system.AlarmOf(_world, _slot, TaskType.InvestigateAlarms, _board) is not null,
            // FUN_140997c70: no leader, dead, down or crippled
            StateType.IsLeaderDown => SquadIntact.Down(_world.Characters, _mind.Squad.Leader)
                || _world.Characters.TryResolveNext(_mind.Squad.Leader, out int lead)
                && _world.Characters.Cold(lead) is { Medical.Crawling: true },
            // 72 of the character itself (its rows target it or a bed): never for one not of the player's faction, so an NPC's sleep rows
            // (98, 100, 175, 261) are never done.
            StateType.IsFullyRested => RowStates.FullyRested(_world, cold),
            StateType.HasOneWorkingArm => cold.Medical is not { } arms || arms.ArmWorks(left: true) || arms.ArmWorks(left: false),
            // 80 (evaluator 1405c7930, Verified): the medical left-arm byte +0x166; the lift rows 213, 240, 225, 246, 68 and 170 need it. States
            // 7 and 48 are not wired: no goal reaches those rows.
            StateType.IsCarryingArmOk => cold.Medical is not { } carrier || Carrying.CarrySystem.LeftArmUsable(carrier),
            StateType.HasTwoWorkingArm => cold.Medical is not { } arms2 || arms2.BothArmsWork,
            // The turret goals' done test (149, 222, 234: list A "6*"): no enemy sensed, so guards man the turrets only while enemies come
            // (docs/simulation.md "AI", turrets).
            StateType.IsNoEnemiesInVicinity => !SensesEnemy(),
            // 26 IS_CROUCHING_OR_LYING (FUN_140599e90): false when crippled (a crawler: the flag is set with posture 2), else true staying low,
            // playing dead or in a bed; the two unidentified flag reads are not modelled.
            StateType.IsCrouchingOrLying => CharacterFacts.CrouchingOrLying(cold),
            // 141 (FUN_14060e640): the building to sleep in is private and a stranger stands in the home. 108 (FUN_14060e5e0): the same finder
            // without the public test.
            StateType.StrangersInHouseP => _system.StrangersInHouseP(_world, _board, _slot),
            StateType.StrangersInHouse => _system.StrangersInHouse(_world, _board, _slot),
            StateType.AtPackageTarget => AtPackageTarget(),
            _ => HeldTrue(state),
        };

        /// <summary>
        /// States held true (moving, standing up, power, buildings, doors); an unlisted state is false.
        /// </summary>
        static bool HeldTrue(StateType state) => state switch
        {
            // Held true: moving, standing up, power and buildings; doors are the movement's and the lock-up round's (docs/simulation.md
            // "Planner"), but for the way into the destination (59, 168: Accessible).
            StateType.IsMovementAllowed or StateType.CanGetUp or StateType.IsBuildingComplete
                or StateType.MachineHasFreeOperatorSlot or StateType.IsReadyForAction
                or StateType.DoorIsOpen or StateType.DoorIsOpenHere or StateType.AtLocationBuildingDoor or StateType.AtCurrentLocationBuildingDoor
                or StateType.AtLocationBuildingDoorInside or StateType.AtLocationBuildingDoorOutside or StateType.LocationIsAccessible or StateType.DestinationNotBlockedByFortifications or StateType.AtLocationFortificationGate
                or StateType.DoorIsLockedAndImInside or StateType.DoorIsLockedAndImOutside or StateType.AllDoorsLocked or StateType.IsObjectMine
                or StateType.IsAlly or StateType.CrowdLimit8 or StateType.TargetStandingStill
                or StateType.IsMachineOutputEmpty or StateType.HasBuildingMaterials or StateType.BuildingHasBuildingMaterials => true,
            _ => false,
        };

        /// <summary>
        /// An enemy is sensed (the negation of <c>isNoEnemiesInVicinity</c>, <c>FUN_140597630</c>: AI <c>+0xbc</c> <c>numEnemies</c> non-zero,
        /// <b>Verified</b>): the character's senses as of its last look count an enemy (<see cref="CharacterFacts.Senses"/>); none before it.
        /// </summary>
        bool SensesEnemy() => CharacterFacts.Senses(_world, _slot) is { Enemies: > 0 };

        /// <summary>
        /// The target finders of the goals Meitou acts out (the spots, beds, seats, turrets and towns it already picks); none for the rest.
        /// </summary>
        public PlanTarget Find(TaskDataRow row, in PlanTarget from)
        {
            var type = row.Type;
            switch (type)
            {
                case TaskType.StandAtShopkeeperNode or TaskType.StandAtGuardNodeHomebuilding or TaskType.StandAtGuardNodeHomebuildingIndoorsOnly
                    or TaskType.StandAtGuardNodeHometownOutside or TaskType.StandAtGeneralNode or TaskType.StayInHome
                    or TaskType.HoldPosition or TaskType.ManTheGate:
                    // Staying home on its claimed chair, else at home.
                    if (type == TaskType.StayInHome && _board.SeatClaims.TryGetValue(_slot, out var chair) && !_mind.SeatUnreachable)
                        return new PlanTarget(Targets.Seat, chair.Position);

                    if (_system.NodeGoalFindsNothing(_mind, _board, type)) return default;
                    return Spot(Targets.Spot, _system.SpotFor(_world, _mind, _board, AiBehaviour.Post, type));
                case TaskType.GoHomeAndGoToBed or TaskType.GoHomeAndGoToBedSecure:
                    // A character in a bed keeps it (the bed search FUN_1408cfe00, docs/game/locks.md "Bed search"): the target is where it lies.
                    if (_world.Characters.Cold(_slot) is { InSomething: Locks.HeldState.Bed })
                        return Spot(Targets.Bed, (Flat(Me.Position), true, Me.Position.Y, null, false));

                    if (_system.SpareBed(_world, _board, _mind, _slot) is { } spare)
                        return Spot(Targets.Bed, (Flat(spare.Position), true, spare.Position.Y, null, false));

                    if (!_system.HasBed(_world, _board, _mind, _slot)) return default;
                    return Spot(Targets.Bed, _system.SpotFor(_world, _mind, _board, AiBehaviour.Sleep, type));
                case TaskType.GoHomebuilding:
                    return Spot(Targets.Spot, _system.SpotFor(_world, _mind, _board, AiBehaviour.GoHome, type));
                case TaskType.WanderTown or TaskType.Shopping or TaskType.Wanderer:
                    {
                        var behaviour = TaskBehaviours.Of(type);
                        var at = _mind.Goal?.Task.Type == type && _mind.Target is { } kept
                            ? kept : _system.TargetFor(_world, _world.Characters.IdOf(_slot), behaviour, _board.Town, _mind);
                        if (at is not { } p) return default;
                        return new PlanTarget(type == TaskType.Wanderer ? Targets.Town : Targets.Spot,
                            type == TaskType.Wanderer ? p with { Y = float.NaN } : p);
                    }
                case TaskType.SitAround or TaskType.SitOnThrone:
                    // Reigning sits on a throne furniture, no target without one (FUN_1408cbd50)
                    return _board.SitClaims.TryGetValue(_slot, out var sit) && !_mind.SeatUnreachable
                        ? new PlanTarget(Targets.Seat, sit.Spot.Position) : default;
                case TaskType.RelaxInTownPackage:
                    return _board.SeatClaims.TryGetValue(_slot, out var seat) && !_mind.SeatUnreachable
                        ? new PlanTarget(Targets.Seat, seat.Position) : default;
                case TaskType.AutoLabouringMines or TaskType.AutoLabouringMinesPretend:
                    // The building it works at: the one it walks to now, else the pick it will make (the same roll, AiMind.WorkEpoch).
                    if (_mind.Behaviour == AiBehaviour.Work && _mind.Goal?.Task.Type == type && _mind.Target is { } current)
                        return new PlanTarget(Targets.Work, current);

                    if (_mind.Goal?.Task.Type == type && _mind.WorkPost is { } held)
                        return new PlanTarget(Targets.Work, new Vector3(held.Position.X, float.NaN, held.Position.Z));

                    return _system.PickWork(_world, _world.Characters.IdOf(_slot), _board.HomeTown,
                            _mind.WorkEpoch + (_mind.WorkActive ? 1 : 0), type) is { } work
                        ? new PlanTarget(Targets.Work, new Vector3(work.Position.X, float.NaN, work.Position.Z)) : default;
                case TaskType.GetRidOfResourcesInMyInventory or TaskType.EmptyMachineOutputs or TaskType.DitchAllResources:
                    // Rows 114, 113 and 242 share the finder FUN_1405a59a0: the nearest storage taking a carried item (TownDutySystem.DitchStore, a
                    // read-only look here).
                    return _world.SystemOf<Kidnap.TownDutySystem>() is { } duties && _world.Characters.Cold(_slot) is { } carrier
                        && duties.DitchStore(_world, _mind.Squad, carrier, Me.Position, register: false) is var (_, spot)
                        ? new PlanTarget(Targets.Store, new Vector3(spot.X, float.NaN, spot.Y)) : default;
                case TaskType.WantToFillMachine:
                    // Row 94 (FUN_14059d9b0): the machine whose output holds what the target machine's inputs need (the town's workplaces here).
                    return MachineAt(from) is { } wanting
                        && Production.ProductionSystem.InputSource(_world, wanting, Workplaces(), Me.Position) is { } source
                        ? new PlanTarget(Targets.Work, new Vector3(source.Position.X, float.NaN, source.Position.Z)) : default;
                case TaskType.ManATurret or TaskType.ManATurretOnBuilding or TaskType.ManATurretPlayerJob or TaskType.UseTurret:
                    return _board.TurretClaims.TryGetValue(_slot, out int t) && (uint)t < (uint)_board.Turrets.Count
                        && _board.Turrets[t].Turret is { } turret
                        ? new PlanTarget(Targets.Turret, TurretSpot(_world, turret)) : default;
            }

            return default;
        }

        /// <summary>
        /// A spot as the act-out walks to it: one on a floor over a hole furniture cuts is at the nearest floor point (<see cref="GoToSpot"/>).
        /// </summary>
        PlanTarget Spot(byte kind, (Vector2 Target, bool Home, float? Floor, float? Yaw, bool Watch) s)
        {
            if (s.Floor is { } y && _world.Walkability.FloorNear(s.Target.X, s.Target.Y, y, FloorSlack) is { } floor) return new(kind, floor);
            return new(kind, new Vector3(s.Target.X, s.Floor ?? float.NaN, s.Target.Y));
        }
    }
}