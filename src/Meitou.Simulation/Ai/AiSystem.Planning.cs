using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

public sealed partial class AiSystem
{
    /// <summary>
    /// Kinds of <see cref="PlanTarget"/> the finders give.
    /// </summary>
    static class Targets
    {
        public const byte Spot = 1, Bed = 2, Seat = 3, Turret = 4, Town = 5, Work = 6, Machine = 7, Store = 8;
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
        [ThreadStatic] static PlanContext? s_local;
        public static PlanContext Local => s_local ??= new PlanContext();

        AiSystem _system = null!;
        World _world = null!;
        AiMind _mind = null!;
        Blackboard _board = null!;
        int _slot;
        readonly List<PlanStep> _scratch = [];

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
        bool _restrictedOpen;

        // Of the player's faction (the character or its faction): the side whose characters can be fully rested (state 72).
        bool _playerSide;

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
        /// Plans a goal; true (and the plan in <see cref="AiMind.Plan"/>) when it has one whose first step the engine carries out. A goal without a
        /// TaskData row is its own plan; one whose list A already holds is left out (the scorer's done test).
        /// </summary>
        public bool TryPlan(AiGoal goal, float desire)
        {
            var type = goal.Task.Type;

            // The assault is its own plan (engine choice): its finder's target is not a plan target, the combat system draws weapons and closes in,
            // and its only list B entry (!47, not carrying) is what Holds already requires of a character the AI runs. So are the combat goals
            // (engine choice): the desire already holds the finder's pick (and for the protect goals row 13's attacker), and the fight is the combat
            // system's. And the Medic goal: its desire holds the patient finder's score, and the treatment is the first aid system's. And the leader
            // rescue (142: its finder and its done state, the leader down, are the desire's; the lift is the carry system's). And a captive goal: its
            // desire holds what the job start found, and the job is the captive system's.
            if (TaskBehaviours.Of(type) is AiBehaviour.Assault or AiBehaviour.Fight or AiBehaviour.Medic or AiBehaviour.RescueLeader
                or AiBehaviour.Captive)
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

            if (TaskDataTable.Of(type) is not { } row)
            {
                _mind.Plan.Clear();
                _mind.Plan.Add(new PlanStep(type, default));
                return true;
            }

            var target = row.Is(TaskDataFlags.NeedsTarget) && !row.Is(TaskDataFlags.Finder) ? Find(row, default) : default;
            if (row.Is(TaskDataFlags.NeedsTarget) && !row.Is(TaskDataFlags.Finder) && target.IsNone) return false;
            if (row.Done.Length > 0 && Array.TrueForAll(row.Done, c => Holds(c, target))) return false;
            if (TaskPlanner.Local.Plan(row, desire, target, this, _mind.Plan, _scratch) <= 0) return false;
            if (_scratch[0].Type != type && !Carried(_scratch[0].Type)) return false;
            _mind.Plan.Clear();
            _mind.Plan.AddRange(_scratch);
            return true;
        }

        public float Desire(TaskDataRow row, in PlanTarget target)
        {
            // row 98: AI slot 30 (FUN_1405a0880); 0 drops the node
            if (row.Type == TaskType.UseBed) return _system.SleepingDesire(_world, _board, _slot);
            float d = TaskProfiles.Of(row.Type).Desire;
            return d >= 0 ? d : 1;   // a random or own desire function counts as 1 on the way
        }

        ref CharacterHot Me => ref _world.Characters.Next[_slot];

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
        /// The player's machine a <see cref="Targets.Machine"/> target stands at (matched by its position; read-only, the production tick is the only
        /// writer). No finder gives such a target yet: the goals Meitou plans against machines are NPC town work, whose buildings are level data, and
        /// the player's own machine work is the job system's.
        /// </summary>
        Production.Machine? MachineAt(in PlanTarget t)
        {
            if (t.Kind is not (Targets.Machine or Targets.Work)) return null;
            foreach (var m in _world.Machines.All)
            {
                if (m.Position.X == t.Position.X && m.Position.Z == t.Position.Z
                    && (t.Kind == Targets.Machine || Production.ProductionSystem.IsTownWork(m.Site)))
                    return m;
            }

            return null;
        }

        IEnumerable<Meitou.Data.World.TownBuilding> Workplaces() => _board.Town >= 0 ? _system.Places(_board.Town).Workplaces : [];

        float Distance(in PlanTarget t) => t.IsNone ? 0 : Vector2.Distance(Flat(Me.Position), Flat(t.Position));

        bool Settled(in PlanTarget t) => At(t) && (CharacterTask)Me.Task is CharacterTask.Rest or CharacterTask.Idle;

        /// <summary>
        /// A state's value for a target. States of rooms, doors, machines and fights the AI does not run into (it acts out goals only out of a fight,
        /// carrying nothing) have a fixed stand-in value; an unlisted state is false.
        /// </summary>
        public bool Holds(TaskCondition condition, in PlanTarget target)
        {
            var cold = _world.Characters.Cold(_slot)!;
            bool value = condition.State switch
            {
                StateType.IsAtLocation or StateType.IsAtLocation46 or StateType.AtNode => At(target),
                StateType.IsWithin10Meters => Distance(target) < Within10Meters,
                StateType.IsWithin50Meters => Distance(target) < Within50Meters,
                StateType.IsNearTo => Distance(target) <= 10,
                // 62 (slot 149), 63 (slot 175) and 64 (62 or 63) of a machine target (FUN_1405974e0, FUN_1405a2ba0, FUN_1405a2e60, Verified); false
                // for any other target. 65 (any of the character's machines jammed) and 105 are not wired.
                StateType.MachineHasEmptyInputs => MachineAt(target)?.HasEmptyInputs == true,
                StateType.IsMachineOutputFull => MachineAt(target)?.OutputBlocked == true,
                StateType.IsMachineJammed => MachineAt(target)?.Jammed == true,
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
                // 5: a weapon in hand that is not ranged; 156: a ranged one (FUN_140596a30 / FUN_140596a70, Verified); 157's evaluator is an
                // undecoded stub.
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
            StateType.IsWorking or StateType.IsWorkingPretend or StateType.UsingTargetTurret or StateType.UsingAnyTurret => Settled(target),
            // AI::iAmInsideTargetBuilding (FUN_14059a4b0) for a target that is not a building: inside its home building (no home: held true).
            StateType.AmInsideTargetBuilding => _board.Home is not { } inHome || _world.Sight.BuildingOf(_slot) == inHome.Key,
            StateType.IsAtHome => _board.Home is not { } home || _mind.HomeUnreachable
                || Vector2.DistanceSquared(Flat(Me.Position), Flat(home.Position)) <= HomeRadius * HomeRadius,
            // Without a town it is never at its home town; its destination town (patrolling in town) is wherever it is.
            StateType.IsAtHomeTown => _board.Town >= 0 && _system.AtHomeTown(_world, _mind.Squad, _board, _world.Characters.IdOf(_slot)),
            // The running package's mission town when it has one (the trader's next town), else the home town.
            StateType.AtDestinationTown or StateType.AtDestinationTown94 => _board.Destination.IsNone
                ? _system.AtHomeTown(_world, _mind.Squad, _board, _world.Characters.IdOf(_slot))
                : _system.CurrentTown(_world, Me.Position) == _board.Destination,
            StateType.HasDestinationTown => _board.Town >= 0 || !_board.Destination.IsNone,
            StateType.AtATown => _system.TownAt(Me.Position) >= 0,
            // Any alarm of its town in reach (goal 210's own-faction filter is its desire's).
            StateType.AlarmsInTheVicinity => _system.AlarmOf(_world, _slot, TaskType.InvestigateAlarms, _board) is not null,
            // FUN_140997c70: no leader, dead, down or crippled
            StateType.IsLeaderDown => SquadIntact.Down(_world.Characters, _mind.Squad.Leader)
                || _world.Characters.TryResolveNext(_mind.Squad.Leader, out int lead)
                && _world.Characters.Cold(lead) is { Medical.Crawling: true },
            // 72 (FUN_1405a0610, Observed): never for a character not of the player's faction, so an NPC's sleep rows (98, 100, 175, 261)
            // are never done; for the player's a medical float above 0.9 (MedicalSystem +0xb0, meaning Unknown), here "not incapacitated".
            StateType.IsFullyRested => _playerSide && cold.Medical is not { Incapacitated: true },
            StateType.HasOneWorkingArm => cold.Medical is not { } arms || arms.ArmWorks(left: true) || arms.ArmWorks(left: false),
            // 80 (evaluator 1405c7930, Verified): the medical left-arm byte +0x166; the lift rows 213, 240, 225, 246, 68 and 170 need it. States
            // 7 and 48 are not wired: no goal reaches those rows.
            StateType.IsCarryingArmOk => cold.Medical is not { } carrier || Carrying.CarrySystem.LeftArmUsable(carrier),
            StateType.HasTwoWorkingArm => cold.Medical is not { } arms2 || arms2.BothArmsWork,
            // The turret goals' done test (149, 222, 234: list A "6*"): no enemy near, so guards man the turrets only while enemies come
            // (docs/simulation.md "AI", turrets).
            StateType.IsNoEnemiesInVicinity => !EnemyNear(cold),
            // 26 IS_CROUCHING_OR_LYING (FUN_140599e90): false when crippled (a crawler: the flag is set with posture 2), else true staying low,
            // playing dead or in a bed; the two unidentified flag reads are not modelled.
            StateType.IsCrouchingOrLying => CharacterFacts.CrouchingOrLying(cold),
            // 141 (FUN_14060e640): the building to sleep in is private and a stranger stands in the home. 108 (FUN_14060e5e0): the same finder
            // without the public test.
            StateType.StrangersInHouseP => _system.StrangersInHouseP(_world, _board, _slot),
            StateType.StrangersInHouse => _system.StrangersInHouse(_world, _board, _slot),
            _ => HeldTrue(state),
        };

        /// <summary>
        /// States held true (moving, standing up, power, buildings, doors); an unlisted state is false.
        /// </summary>
        static bool HeldTrue(StateType state) => state switch
        {
            // Held true: moving, standing up, power and buildings; doors are the movement's and the lock-up round's (docs/simulation.md
            // "Planner").
            StateType.IsMovementAllowed or StateType.CanGetUp or StateType.IsBuildingComplete
                or StateType.BuildingHasPower or StateType.MachineHasFreeOperatorSlot or StateType.IsReadyForAction
                or StateType.DoorIsOpen or StateType.DoorIsOpenHere or StateType.AtLocationBuildingDoor or StateType.AtCurrentLocationBuildingDoor
                or StateType.AtLocationBuildingDoorInside or StateType.AtLocationBuildingDoorOutside or StateType.DestinationIsAccessible
                or StateType.LocationIsAccessible or StateType.DestinationNotBlockedByFortifications or StateType.AtLocationFortificationGate
                or StateType.DoorIsLockedAndImInside or StateType.DoorIsLockedAndImOutside or StateType.AllDoorsLocked or StateType.IsObjectMine
                or StateType.IsAlly or StateType.CrowdLimit8 or StateType.TargetStandingStill
                or StateType.IsMachineOutputEmpty or StateType.HasBuildingMaterials or StateType.BuildingHasBuildingMaterials => true,
            _ => false,
        };

        struct EnemyScan
        {
            public CharacterTable Table;
            public FactionRelations Relations;
            public int Self, Faction;
            public bool Found;
        }

        /// <summary>
        /// An enemy is near (the negation of <c>isNoEnemiesInVicinity</c>): a living character up within <see cref="EnemyVicinity"/> whose faction is
        /// an enemy of this one's (the relations table). <b>Engine choice</b>: the original's test and its radius are <b>Unknown</b>.
        /// </summary>
        bool EnemyNear(CharacterCold cold)
        {
            if (!_system.Relations.Contains(cold.Faction)) return false;
            var p = Me.Position;
            var scan = new EnemyScan { Table = _world.Characters, Relations = _system.Relations, Self = _slot, Faction = cold.Faction };
            _world.Grid.Query(p.X, p.Z, EnemyVicinity, ref scan, static (int other, ref EnemyScan st) =>
            {
                if (st.Found || other == st.Self || !st.Table.Previous[other].Alive
                    || st.Table.Cold(other) is not { Medical.Incapacitated: false } oc)
                    return;

                if (AggroSystem.IsEnemy(st.Relations, st.Faction, oc.Faction)) st.Found = true;
            });
            return scan.Found;
        }

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
                    return Spot(Targets.Spot, _system.SpotFor(_mind, _board, AiBehaviour.Post, type));
                case TaskType.GoHomeAndGoToBed or TaskType.GoHomeAndGoToBedSecure:
                    // A character in a bed keeps it (the bed search FUN_1408cfe00, docs/game/locks.md "Bed search"): the target is where it lies.
                    if (_world.Characters.Cold(_slot) is { InSomething: Locks.HeldState.Bed })
                        return Spot(Targets.Bed, (Flat(Me.Position), true, Me.Position.Y, null));

                    if (_system.SpareBed(_world, _board, _mind, _slot) is { } spare)
                        return Spot(Targets.Bed, (Flat(spare.Position), true, spare.Position.Y, null));

                    if (!_system.HasBed(_world, _board, _mind, _slot)) return default;
                    return Spot(Targets.Bed, _system.SpotFor(_mind, _board, AiBehaviour.Sleep, type));
                case TaskType.GoHomebuilding:
                    return Spot(Targets.Spot, _system.SpotFor(_mind, _board, AiBehaviour.GoHome, type));
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

                    return _system.PickWork(_world, _world.Characters.IdOf(_slot), _board.Town,
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
        PlanTarget Spot(byte kind, (Vector2 Target, bool Home, float? Floor, float? Yaw) s)
        {
            if (s.Floor is { } y && _world.Walkability.FloorNear(s.Target.X, s.Target.Y, y, FloorSlack) is { } floor) return new(kind, floor);
            return new(kind, new Vector3(s.Target.X, s.Floor ?? float.NaN, s.Target.Y));
        }
    }
}