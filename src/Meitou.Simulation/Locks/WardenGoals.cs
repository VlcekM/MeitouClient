using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The wardens' rounds as goals of the one chooser (docs/simulation.md "Arrest and jail", "Engine goals"): 211 police free prisoners (plan
/// <c>[1, 110, 211]</c>), 155 Shop Lock all the doors (<c>[163, 162, 155]</c>, 82 and 73 when the door stands open), 197 Lock all the doors - stay
/// outside (<c>[199, 198, 197]</c>), 86 Shop open up doors (<c>[82, 78, 72, 86]</c>) and 195 Lock all my cages (<c>[82, 77, 195]</c>). Each finder names
/// one target (a prisoner, a door, a cage); a serial look keeps each member's pick for the think; a chosen goal's act-out walks to its target and
/// works it (a <see cref="Round"/> of one target) and is done once it has, so the chooser takes the next target at its next think.
/// </summary>
internal sealed class WardenGoals(LockSystem locks)
{
    /// <summary>
    /// The lock system the goals work for (its house flags, crimes and rounds).
    /// </summary>
    readonly LockSystem _locks = locks;

    #region Picks

    /// <summary>
    /// How often a member's finders look, in ticks (engine: the original's finders run at each score, so a pick is at most this old; a member's own
    /// pick is looked at again as its act-out ends).
    /// </summary>
    internal const int LookEveryTicks = 15;

    /// <summary>
    /// What a member's finder found at its last look: who looked and the target.
    /// </summary>
    readonly record struct Pick(CharacterId Id, PlanTarget Target);

    /// <summary>
    /// The prisoner each member weighing 211 found (<c>FUN_1408cf3c0</c>), by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick> _prisoners = [];
    /// <summary>
    /// The door each member weighing 155 or 197 found (their shared finder <c>FUN_1408cb1c0</c>), by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick> _toLock = [];
    /// <summary>
    /// The door each member weighing 86 found (<c>FUN_1408cb360</c>), by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick> _toOpen = [];
    /// <summary>
    /// The cage each member weighing 195 found (<c>FUN_1408cc2a0</c>), by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick> _cages = [];

    /// <summary>
    /// The finders' look (serial, the lock step), for each character on its slot's turn every <see cref="LookEveryTicks"/>: a member with a mind of
    /// its own keeps what the finder of each warden goal it weighs (<see cref="AiSystem.WeighsGoal"/>) finds; the think's desire, finder and states
    /// read the picks.
    /// </summary>
    internal void Look(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
            if (world.Turn(s, LookEveryTicks)) LookAt(world, s);

        Prune(table, _prisoners);
        Prune(table, _toLock);
        Prune(table, _toOpen);
        Prune(table, _cages);
    }

    /// <summary>
    /// The picks of the character at <paramref name="s"/>, each of a goal it weighs, else none.
    /// </summary>
    void LookAt(World world, int s)
    {
        var table = world.Characters;
        var cold = table.Next[s].Alive ? table.Cold(s) : null;
        if (cold is not { IsPlayer: false, IsImprisoned: false, Ai: { Player: false, Squad.Ai: { } board } })
        {
            _prisoners.Remove(s);
            _toLock.Remove(s);
            _toOpen.Remove(s);
            _cages.Remove(s);
            return;
        }

        var id = table.IdOf(s);
        bool locking = AiSystem.WeighsGoal(world, s, TaskType.LockAllMyDoors) || AiSystem.WeighsGoal(world, s, TaskType.LockAllMyDoorsFromOutside);
        Keep(_prisoners, s, id, AiSystem.WeighsGoal(world, s, TaskType.PoliceFreePrisonersWhenDone) ? PrisonerToFree(world, s, cold) : default);
        Keep(_toLock, s, id, locking ? DoorToLock(world, s, cold, board) : default);
        Keep(_toOpen, s, id, AiSystem.WeighsGoal(world, s, TaskType.OpenUpShopDoors) ? DoorToOpen(world, s, cold, board) : default);
        Keep(_cages, s, id, AiSystem.WeighsGoal(world, s, TaskType.LockAllTheCages) ? CageToLock(world, s) : default);
    }

    /// <summary>
    /// Keeps <paramref name="target"/> as the pick of <paramref name="slot"/>, or drops its pick for none.
    /// </summary>
    static void Keep(SortedDictionary<int, Pick> picks, int slot, CharacterId id, PlanTarget target)
    {
        if (target.IsNone) picks.Remove(slot);
        else picks[slot] = new Pick(id, target);
    }

    /// <summary>
    /// Drops the picks of slots that no longer hold the character that looked.
    /// </summary>
    static void Prune(CharacterTable table, SortedDictionary<int, Pick> picks)
    {
        List<int>? stale = null;
        foreach (var (slot, pick) in picks)
            if (!table.TryResolveNext(pick.Id, out int at) || at != slot) (stale ??= []).Add(slot);

        if (stale is not null) foreach (int slot in stale) picks.Remove(slot);
    }

    /// <summary>
    /// 211's finder <c>FUN_1408cf3c0</c> (<b>Verified</b>): the cages of the jailer's town and faction within one zone cell of it (<c>FUN_1409fc450</c>,
    /// <c>FUN_1409fb880</c>: the 3 x 3 cells round it), in list order, the first whose prisoner is held, up, its sentence served, not one the jailer
    /// regards as an enemy, and not one a squad mate already frees (the squad's work count <c>FUN_1402690e0</c>); the prisoner, at its cage.
    /// </summary>
    PlanTarget PrisonerToFree(World world, int jailer, CharacterCold jc)
    {
        var table = world.Characters;
        var at = table.Next[jailer].Position;
        int town = jc.Ai?.Squad.Ai?.Town ?? -1;
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || f.Owner != jc.Faction || town >= 0 && f.Site != town || FixtureRegistry.IsFree(table, f)) continue;
            if (!Data.World.WorldLayout.WithinOneZone(at.X, at.Z, f.Position.X, f.Position.Z) || f.Occupant == table.IdOf(jailer)) continue;
            if (!table.TryResolveNext(f.Occupant, out int p) || Served(world, p) != f || FreedByMate(world, jailer, jc, f.Occupant)) continue;

            return PlanTarget.OfCharacter(f.Occupant, f.Position);
        }

        return default;
    }

    /// <summary>
    /// The cage of the prisoner in <paramref name="s"/> when it is held, awake, its sentence served and not an enemy of the cage's faction (vfunc
    /// <c>0x3e8</c>, isEnemy, <b>Verified</b>); null otherwise.
    /// </summary>
    Fixture? Served(World world, int s) =>
        world.Characters.Cold(s) is { IsImprisoned: true, InWhat: { } where } c && c.Medical is not { Incapacitated: true }
            && FixtureRegistry.SentenceServed(c, world.GameHours) && world.Fixtures.Find(where) is { } cage
            && (_locks.Crimes is not { } crimes || !CharacterFacts.RegardsEnemy(crimes.Relations, cage.Owner, c.Faction))
            ? cage
            : null;

    /// <summary>
    /// Whether another member of the jailer's squad frees <paramref name="prisoner"/> now.
    /// </summary>
    bool FreedByMate(World world, int jailer, CharacterCold jc, CharacterId prisoner)
    {
        foreach (var (slot, act) in _acting)
        {
            if (slot != jailer && act.Round.Prisoner == prisoner && world.Characters.Cold(slot)?.SquadId == jc.SquadId) return true;
        }

        return false;
    }

    /// <summary>
    /// 155 and 197's finder <c>FUN_1408cb1c0</c> (<b>Verified</b>): in the building it stands in when that is its faction's, else in its home, the
    /// first door in index order whose lock is not effectively locked, or that stands open or broken; the door.
    /// </summary>
    static PlanTarget DoorToLock(World world, int s, CharacterCold c, Blackboard board) =>
        DoorsOf(world, s, c, board) is { } building && FirstDoor(world, building, NeedsLock) is { } door ? PlanTarget.OfFixture(door.Id, door.Position) : default;

    /// <summary>
    /// 86's finder <c>FUN_1408cb360</c> (<b>Verified</b>): in the building as for 155, the first door in index order neither broken nor open; with none
    /// in its home, the squad's house flag is cleared.
    /// </summary>
    PlanTarget DoorToOpen(World world, int s, CharacterCold c, Blackboard board)
    {
        if (DoorsOf(world, s, c, board) is not { } building) return default;
        if (FirstDoor(world, building, NeedsOpening) is { } door) return PlanTarget.OfFixture(door.Id, door.Position);

        if (building == board.OwnHome?.Key) _locks.ClearHouseLocked(building);
        return default;
    }

    /// <summary>
    /// 195's finder <c>FUN_1408cc2a0</c> (<b>Verified</b>): the cages of the building the warden stands in (function 8, <c>+0x1f0</c>), in list order,
    /// the first holding someone whose lock is not effectively locked; the cage.
    /// </summary>
    static PlanTarget CageToLock(World world, int s)
    {
        if (world.Sight.BuildingOf(s) is not { } building) return default;

        foreach (var f in world.Fixtures.OfBuilding(building))
        {
            if (f.Kind == FixtureKind.Cage && NeedsLocking(world, f)) return PlanTarget.OfFixture(f.Id, f.Position);
        }

        return default;
    }

    /// <summary>
    /// The building the door finders look in: the one the character stands in when its owner is the character's faction (building vfunc <c>0x58</c>
    /// against the character's, <c>FUN_140506cd0</c>), else its squad's home (<c>FUN_1407956a0</c>); null without either.
    /// </summary>
    static string? DoorsOf(World world, int s, CharacterCold c, Blackboard board) =>
        world.Sight.BuildingOf(s) is { } here && world.Fixtures.DoorOwner(here) == c.Faction ? here : board.OwnHome?.Key;

    /// <summary>
    /// The building's door of lowest index that <paramref name="wanted"/> takes (null: none).
    /// </summary>
    static Fixture? FirstDoor(World world, string building, Func<Fixture, bool> wanted)
    {
        Fixture? first = null;
        foreach (var f in world.Fixtures.OfBuilding(building))
        {
            if (f.Kind == FixtureKind.Door && wanted(f) && (first is null || f.DoorIndex < first.DoorIndex)) first = f;
        }

        return first;
    }

    /// <summary>
    /// A door the lock-up finder takes: its lock not effectively locked (unset, broken or below level 1), or the door open or broken.
    /// </summary>
    static bool NeedsLock(Fixture door) => door.Lock is not { EffectivelyLocked: true } || door.DoorBroken || door.Motion == DoorMotion.Open;

    /// <summary>
    /// A door the opening-up finder takes: neither broken nor open.
    /// </summary>
    static bool NeedsOpening(Fixture door) => !door.DoorBroken && door.Motion != DoorMotion.Open;

    /// <summary>
    /// A cage the warden's finder takes: someone is held in it and its lock is not effectively locked. A pure read of the occupant's cold state, which
    /// only serial steps write.
    /// </summary>
    static bool NeedsLocking(World world, Fixture cage) =>
        cage.Lock is not { EffectivelyLocked: true } && world.Characters.IsAlive(cage.Occupant)
        && world.Characters.Cold(cage.Occupant.Slot) is { InSomething: not HeldState.None } held && held.InWhat == cage.Id;

    #endregion

    #region Act-outs

    /// <summary>
    /// A member a warden goal holds: who, the goal, its target as the plan holds it and the round that works it.
    /// </summary>
    readonly record struct ActOut(CharacterId Id, TaskType Goal, PlanTarget Target, Round Round);

    /// <summary>
    /// The members a warden goal holds, by slot: added at the start, removed at the end or when done (hashed).
    /// </summary>
    readonly SortedDictionary<int, ActOut> _acting = [];

    /// <summary>
    /// The engine goals of the world (the AI's), resolved at the first step.
    /// </summary>
    DutyGoals? _duties;

    /// <summary>
    /// Scratch: the act-outs done in a step.
    /// </summary>
    readonly List<int> _done = [];

    /// <summary>
    /// The rounds the act-outs work, in slot order.
    /// </summary>
    internal IEnumerable<Round> Acting => _acting.Values.Select(a => a.Round);

    /// <summary>
    /// Registers the goals with the rows of their plans their act-outs carry out (<see cref="DutyGoals.Register"/>): the walks to the door (82, 163,
    /// 199), closing it (73), and the work (110, 162, 198, 78 and 72, 77).
    /// </summary>
    internal void Register(DutyGoals goals)
    {
        goals.Register(TaskType.PoliceFreePrisonersWhenDone, new Freeing(this), TaskType.ReleasePrisoner);
        goals.Register(TaskType.LockAllMyDoors, new LockingUp(this), TaskType.MoveToBuildingDoorInsidepos, TaskType.LockDoorFromInside,
            TaskType.MoveToBuildingDoor, TaskType.CloseDoor);
        goals.Register(TaskType.LockAllMyDoorsFromOutside, new LockingUp(this), TaskType.MoveToBuildingDoorOutsidepos,
            TaskType.LockDoorFromOutside, TaskType.MoveToBuildingDoor, TaskType.CloseDoor);
        goals.Register(TaskType.OpenUpShopDoors, new OpeningUp(this), TaskType.MoveToBuildingDoor, TaskType.UnlockDoor, TaskType.OpenDoor);
        goals.Register(TaskType.LockAllTheCages, new CageLocking(this), TaskType.MoveToBuildingDoor, TaskType.LockDoor, TaskType.CloseDoor);
    }

    /// <summary>
    /// The act-outs' step (serial, the lock step): each member a goal still holds moves its round on (<see cref="LockSystem.Advance"/>); one whose
    /// round is over is done (<see cref="DutyGoals.Done"/>; a lost walk marks the target first, <see cref="DutyGoals.LostWalk"/>) and its finders
    /// look again, so its next choice sees its next target. One the goal no longer holds is left to the AI's sweep, which ends it.
    /// </summary>
    internal void Step(World world)
    {
        if (_acting.Count == 0) return;

        _duties ??= world.SystemOf<AiSystem>()?.Duties;
        _done.Clear();
        foreach (var (slot, act) in _acting)
            if (_duties?.Runs(world, slot, act.Goal) == true && _locks.Advance(world, act.Round, slot)) _done.Add(slot);

        foreach (int slot in _done)
        {
            var act = _acting[slot];
            _acting.Remove(slot);
            if (act.Round.Lost) _duties!.LostWalk(world, slot, act.Goal, DutyGoals.KeyOf(act.Target));
            else _duties!.Done(world, slot, act.Goal);

            LookAt(world, slot);
        }
    }

    /// <summary>
    /// The act-out's start (serial): the round on the plan's target, refused when the target no longer wants the work or another member's act-out
    /// works it.
    /// </summary>
    bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        if (PlanTargetOf(plan) is not { } target || Worked(ask.Slot, target)) return false;
        if (RoundFor(world, ask, target) is not { } round || world.Fixtures.Find(round.Targets[0]) is not { } first) return false;
        if (!LockSystem.Start(world, ask.Slot, round, first, TaskSource.Goal)) return false;

        _acting[ask.Slot] = new ActOut(ask.Id, ask.Type, target, round);
        return true;
    }

    /// <summary>
    /// The finder's target in the plan (the first step on one); null without.
    /// </summary>
    static PlanTarget? PlanTargetOf(IReadOnlyList<PlanStep> plan)
    {
        foreach (var step in plan)
            if (step.Target.Kind == PlanTarget.Found) return step.Target;
        return null;
    }

    /// <summary>
    /// Whether another member's act-out works <paramref name="target"/>.
    /// </summary>
    bool Worked(int slot, in PlanTarget target)
    {
        foreach (var (other, act) in _acting)
            if (other != slot && act.Target.Character == target.Character && act.Target.Fixture == target.Fixture) return true;
        return false;
    }

    /// <summary>
    /// The round of goal <paramref name="ask"/>'s type on <paramref name="target"/>, or null when the target no longer wants it.
    /// </summary>
    Round? RoundFor(World world, in DutyAsk ask, in PlanTarget target)
    {
        var table = world.Characters;
        var id = ask.Id;
        int faction = table.Cold(ask.Slot)!.Faction;
        if (ask.Type == TaskType.PoliceFreePrisonersWhenDone)
            return ReleaseRound(world, id, faction, target.Character ?? CharacterId.None);
        if (target.Fixture is not { } fixture || world.Fixtures.Find(fixture) is not { } f) return null;

        return ask.Type switch
        {
            TaskType.LockAllMyDoors or TaskType.LockAllMyDoorsFromOutside when NeedsLock(f) => new Round
            {
                Kind = RoundKind.LockDoors, Worker = id, Faction = faction, Targets = [f.Id], Building = f.Building, Reach = LockSystem.DoorReach,
                FromOutside = ask.Type == TaskType.LockAllMyDoorsFromOutside,
            },
            TaskType.OpenUpShopDoors when NeedsOpening(f) => new Round
            {
                Kind = RoundKind.UnlockDoors, Worker = id, Faction = faction, Targets = [f.Id], Building = f.Building, Reach = LockSystem.DoorWorkReach,
            },
            TaskType.LockAllTheCages when NeedsLocking(world, f) => new Round
            {
                Kind = RoundKind.LockCages, Worker = id, Faction = faction, Targets = [f.Id], Reach = f.UseRange + LockSystem.Reach,
            },
            _ => null,
        };
    }

    /// <summary>
    /// A release round on the prisoner's cage while it is still held there and due (<see cref="Served"/>).
    /// </summary>
    Round? ReleaseRound(World world, CharacterId worker, int faction, CharacterId prisoner)
    {
        if (!world.Characters.TryResolveNext(prisoner, out int p) || Served(world, p) is not { } cage) return null;

        return new Round
        {
            Kind = RoundKind.Release, Worker = worker, Faction = faction, Targets = [cage.Id], Prisoner = prisoner, Reach = cage.UseRange + LockSystem.Reach,
        };
    }

    /// <summary>
    /// The goal ended (displaced, an order, a replan, taken, gone): the member is no longer held.
    /// </summary>
    void End(int slot, CharacterId id)
    {
        if (_acting.TryGetValue(slot, out var act) && act.Id == id) _acting.Remove(slot);
    }

    /// <summary>
    /// The target the finder of <paramref name="picks"/> gives the asker: its act-out's while the goal holds it, else its pick while the target still
    /// wants the work (read live: doors and cages change only in serial steps); factor 1.0 with one (<c>DAT_14167c308</c>), else 0.
    /// </summary>
    float Find(World world, SortedDictionary<int, Pick> picks, in DutyAsk ask, out PlanTarget target)
    {
        if (_acting.TryGetValue(ask.Slot, out var act) && act.Id == ask.Id && act.Goal == ask.Type) target = act.Target;
        else if (picks.TryGetValue(ask.Slot, out var pick) && pick.Id == ask.Id && StillWanted(world, ask.Type, pick.Target)) target = pick.Target;
        else target = default;

        return target.IsNone ? 0 : 1;
    }

    /// <summary>
    /// Whether a pick still wants its goal's work: the prisoner still held, the door still unlocked or open, the cage still held and unlocked.
    /// </summary>
    static bool StillWanted(World world, TaskType goal, in PlanTarget target)
    {
        if (goal == TaskType.PoliceFreePrisonersWhenDone)
            return target.Character is { } p && world.Characters.IsAlive(p) && world.Characters.Cold(p.Slot) is { IsImprisoned: true };
        if (target.Fixture is not { } id || world.Fixtures.Find(id) is not { } f) return false;

        return goal switch
        {
            TaskType.OpenUpShopDoors => NeedsOpening(f),
            TaskType.LockAllTheCages => NeedsLocking(world, f),
            _ => NeedsLock(f),
        };
    }

    #endregion

    #region States

    /// <summary>
    /// The states of the goals' rows for the asker (parallel: <c>Previous</c> places, fixtures and cold state only serial steps write): a finder's
    /// target first (<see cref="PlanTarget.Found"/>), else 87 for the goal's own row; null for the rest.
    /// </summary>
    bool? State(World world, in DutyAsk ask, StateType state)
    {
        var target = ask.Target;
        if (target.Kind != PlanTarget.Found) return state == StateType.AllDoorsLocked ? AllDoorsLocked(world, ask) : null;
        if (target.Character is { } prisoner) return PrisonerState(world, ask.Slot, prisoner, state);
        if (target.Fixture is not { } id || world.Fixtures.Find(id) is not { } f) return null;

        return f.Kind == FixtureKind.Door ? DoorState(world, ask.Slot, f, state) : CageState(world, ask.Slot, f, state);
    }

    /// <summary>
    /// 87 allDoorsLocked (<c>FUN_140596bd0</c>, <b>Verified</b>, docs/game/locks.md "Residents"): in a bed, or the squad's house flag set, or the home
    /// has no door the lock-up finder takes (open, broken or not effectively locked).
    /// </summary>
    bool AllDoorsLocked(World world, in DutyAsk ask)
    {
        if (world.Characters.Cold(ask.Slot) is { InSomething: HeldState.Bed }) return true;
        if (ask.Squad.Ai?.OwnHome is not { } home) return false;

        return _locks.HouseLocked(home.Key) || FirstDoor(world, home.Key, NeedsLock) is null;
    }

    /// <summary>
    /// The states of 110 for its prisoner: 78 imprisoned (<c>FUN_1405a1cc0</c>, <b>Verified</b>), 125 conscious (<c>FUN_14099a480</c>), 73 false (the
    /// evaluator <c>FUN_140598de0</c> answers false for a target that is no building, <b>Verified</b>), 1 within its cage's reach.
    /// </summary>
    static bool? PrisonerState(World world, int slot, CharacterId prisoner, StateType state)
    {
        var table = world.Characters;
        var pc = table.IsAlive(prisoner) ? table.Cold(prisoner.Slot) : null;
        return state switch
        {
            StateType.TargetIsImprisoned => pc is { InSomething: HeldState.Imprisoned },
            StateType.TargetIsConscious => pc is not null && CharacterFacts.Conscious(pc),
            StateType.MachineHasFreeOperatorSlot => false,
            StateType.IsAtLocation => pc is { InWhat: { } cageId } && world.Fixtures.Find(cageId) is { } cage
                && Near(table.Previous[slot].Position, Flat(cage.Position), cage.UseRange + LockSystem.Reach),
            _ => null,
        };
    }

    /// <summary>
    /// The states of the door rows for a door: 52 open, 54 locked and 56 broken as <see cref="RowStates"/> reads them (fixture reads); 70
    /// isObjectMine (<see cref="LockSystem.IsMine"/>); 57 at the door (<see cref="DoorPlan.AtDoor"/>); 111 and 126 within
    /// <see cref="LockSystem.DoorReach"/> of its inner and outer point; 112 and 127 the door locked with the character in a building, or outdoors
    /// (<c>FUN_1405a02d0</c>, <c>FUN_1405a0470</c>, <b>Verified</b>).
    /// </summary>
    static bool? DoorState(World world, int slot, Fixture door, StateType state)
    {
        var at = world.Characters.Previous[slot].Position;
        return state switch
        {
            StateType.DoorIsOpen or StateType.IsDoorLocked or StateType.DoorIsBroken =>
                RowStates.Holds(world, slot, state, new RowTarget(CharacterId.None, door.Id)),
            StateType.IsObjectMine => world.Characters.Cold(slot) is { } c && LockSystem.IsMine(world, c, door.Building),
            StateType.AtLocationBuildingDoor => world.Fixtures.DoorSpotOf(door) is { } spot
                ? DoorPlan.AtDoor(world.Characters.Previous[slot], spot)
                : Near(at, Flat(door.Position), LockSystem.DoorWorkReach),
            StateType.AtLocationBuildingDoorInside => Near(at, InnerPoint(world, door), LockSystem.DoorReach),
            StateType.AtLocationBuildingDoorOutside => Near(at, OuterPoint(world, door), LockSystem.DoorReach),
            StateType.DoorIsLockedAndImInside => world.Sight.BuildingOf(slot) is not null && Locked(world, slot, door),
            StateType.DoorIsLockedAndImOutside => world.Sight.BuildingOf(slot) is null && Locked(world, slot, door),
            _ => null,
        };
    }

    /// <summary>
    /// The states of 77 for a cage: 54 its lock effectively locked (<c>FUN_1405a00c0</c>), 52 false (a building with no door, <c>FUN_1405a3ad0</c>,
    /// <b>Verified</b>), 56 its lock broken, 70 its owner the character's faction or its building the character's (<b>Observed</b> reading of
    /// <c>FUN_1405a3c10</c>), 57 within its <c>use range</c> + <see cref="LockSystem.Reach"/>.
    /// </summary>
    static bool? CageState(World world, int slot, Fixture cage, StateType state) => state switch
    {
        StateType.IsDoorLocked => cage.Lock is { EffectivelyLocked: true },
        StateType.DoorIsOpen => false,
        StateType.DoorIsBroken => cage.Lock is { Broken: true },
        StateType.IsObjectMine => world.Characters.Cold(slot) is { } c && (cage.Owner == c.Faction || LockSystem.IsMine(world, c, cage.Building)),
        StateType.AtLocationBuildingDoor => Near(world.Characters.Previous[slot].Position, Flat(cage.Position), cage.UseRange + LockSystem.Reach),
        _ => null,
    };

    /// <summary>
    /// 54 isDoorLocked of a door as <see cref="RowStates"/> reads it.
    /// </summary>
    static bool Locked(World world, int slot, Fixture door) =>
        RowStates.Holds(world, slot, StateType.IsDoorLocked, new RowTarget(CharacterId.None, door.Id)) == true;

    /// <summary>
    /// A door's inner point (<see cref="DoorPoints.Inner"/>), its place without door data.
    /// </summary>
    static Vector2 InnerPoint(World world, Fixture door) =>
        world.Fixtures.DoorSpotOf(door) is { } spot ? DoorPoints.Inner(spot, door.Position) : Flat(door.Position);

    /// <summary>
    /// A door's outer point (<see cref="DoorPoints.Outer"/>), its place without door data.
    /// </summary>
    static Vector2 OuterPoint(World world, Fixture door) =>
        world.Fixtures.DoorSpotOf(door) is { } spot ? DoorPoints.Outer(spot, door.Position) : Flat(door.Position);

    /// <summary>
    /// Whether <paramref name="at"/> lies within <paramref name="reach"/> of <paramref name="spot"/> on the ground plane.
    /// </summary>
    static bool Near(Vector3 at, Vector2 spot, float reach) => Vector2.DistanceSquared(Flat(at), spot) <= reach * reach;

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    #endregion

    #region Goals

    /// <summary>
    /// Goal 211 POLICE_FREE_PRISONERS_WHEN_DONE: plan <c>[1, 110, 211]</c> on the finder's prisoner.
    /// </summary>
    sealed class Freeing(WardenGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 211's and 110's desire, AI slot 19 <c>FUN_140641550</c>: 1.0 (<b>Verified</b>, the registration's stubs).
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => 1;

        /// <summary>
        /// 211's finder (<see cref="PrisonerToFree"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(world, goals._prisoners, ask, out target);

        /// <summary>
        /// 110's states for the prisoner (<see cref="WardenGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => goals.State(world, ask, state);

        /// <summary>
        /// Walks to the prisoner's cage, unlocks it and lets it out.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Start(world, ask, plan);

        /// <summary>
        /// The jailer is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(slot, id);
    }

    /// <summary>
    /// Goals 155 LOCK_ALL_MY_DOORS (<c>[163, 162, 155]</c>) and 197 LOCK_ALL_MY_DOORS_FROM_OUTSIDE (<c>[199, 198, 197]</c>) on the finder's door; a
    /// member of a 197 squad standing inside its home has no plan, as in the original (list B !107's provider 200 needs a target, and list B gets
    /// only the goal's own hand, empty for 197: <c>FUN_14051bae0</c>).
    /// </summary>
    sealed class LockingUp(WardenGoals goals) : IDutyGoal
    {
        /// <summary>
        /// The goal's own row: AI slot 31 <c>FUN_140795f80</c> (<b>Verified</b>): 1.0 while the squad's house flag is clear; with it set, 1.0 for a
        /// member standing outdoors whose home's first door is open or broken (<c>FUN_1402ea2f0</c>), else 0. The rows on the way: 1.0 (slots 19, 57).
        /// </summary>
        public float Desire(World world, in DutyAsk ask)
        {
            if (ask.Row != ask.Type) return 1;
            if (ask.Squad.Ai?.OwnHome is not { } home) return 1;
            if (!goals._locks.HouseLocked(home.Key)) return 1;
            if (world.Sight.BuildingOf(ask.Slot) is not null) return 0;

            return LockSystem.FirstDoor(world, home.Key) is { } door && (door.DoorBroken || door.Motion == DoorMotion.Open) ? 1 : 0;
        }

        /// <summary>
        /// The shared finder of 155 and 197 (<see cref="DoorToLock"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(world, goals._toLock, ask, out target);

        /// <summary>
        /// The door rows' states and 87 (<see cref="WardenGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => goals.State(world, ask, state);

        /// <summary>
        /// Walks to the door's inner (155) or outer (197) point, closes and locks it.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Start(world, ask, plan);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(slot, id);
    }

    /// <summary>
    /// Goal 86 OPEN_UP_SHOP_DOORS: plan <c>[82, 78, 72, 86]</c> on the finder's door (78 only while it is locked); list A !87, so only while the home
    /// counts as locked.
    /// </summary>
    sealed class OpeningUp(WardenGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 86's desire and its rows', AI slots 19 and 57: 1.0 (<b>Verified</b>).
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => 1;

        /// <summary>
        /// 86's finder (<see cref="DoorToOpen"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(world, goals._toOpen, ask, out target);

        /// <summary>
        /// The door rows' states and 87 (<see cref="WardenGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => goals.State(world, ask, state);

        /// <summary>
        /// Walks to the door's nearer side, unlocks it when locked and opens it.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Start(world, ask, plan);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(slot, id);
    }

    /// <summary>
    /// Goal 195 LOCK_ALL_THE_CAGES: plan <c>[82, 77, 195]</c> on the finder's cage.
    /// </summary>
    sealed class CageLocking(WardenGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 195's desire and its rows', AI slots 19 and 57: 1.0 (<b>Verified</b>).
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => 1;

        /// <summary>
        /// 195's finder (<see cref="CageToLock"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(world, goals._cages, ask, out target);

        /// <summary>
        /// 77's states for the cage (<see cref="WardenGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => goals.State(world, ask, state);

        /// <summary>
        /// Walks to the cage and locks it.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Start(world, ask, plan);

        /// <summary>
        /// The warden is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(slot, id);
    }

    #endregion

    /// <summary>
    /// Adds the picks and the act-outs to the state hash, only while there are any.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_prisoners.Count == 0 && _toLock.Count == 0 && _toOpen.Count == 0 && _cages.Count == 0 && _acting.Count == 0) return;

        hasher.Add(0x57617264);   // "Ward"
        HashPicks(ref hasher, _prisoners);
        HashPicks(ref hasher, _toLock);
        HashPicks(ref hasher, _toOpen);
        HashPicks(ref hasher, _cages);
        hasher.Add(_acting.Count);
        foreach (var (slot, act) in _acting)
        {
            hasher.Add(slot);
            hasher.Add(act.Id);
            hasher.Add((int)act.Goal);
            act.Round.Hash(ref hasher);
        }
    }

    /// <summary>
    /// Adds one pick list to the state hash.
    /// </summary>
    static void HashPicks(ref StateHasher hasher, SortedDictionary<int, Pick> picks)
    {
        hasher.Add(picks.Count);
        foreach (var (slot, pick) in picks)
        {
            hasher.Add(slot);
            hasher.Add(pick.Id);
            hasher.Add(pick.Target.Character ?? CharacterId.None);
            hasher.Add(Rng.StableHash(pick.Target.Fixture ?? ""));
            hasher.Add(pick.Target.Position);
        }
    }
}
