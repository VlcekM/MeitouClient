using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The planner's view of an NPC attacker whose target is shut in a building (docs/game/locks.md "Bash door"): row 4 Attacking needs
/// DESTINATION_IS_ACCESSIBLE, its provider 85 finds the building and wants DOOR_IS_OPEN, whose providers (72 Opening door, 81 and 287 Bashing door)
/// the <see cref="TaskPlanner"/> weighs as the original does. Only the door states are evaluated; the rest of row 4's list B is held (the fight runs
/// it). Serial phase only.
/// </summary>
sealed class DoorPlan : IPlanWorld
{
    /// <summary>
    /// Kinds of <see cref="PlanTarget"/>: the attack target, and the building (at its main door's leaf).
    /// </summary>
    public const byte Foe = 1, Building = 2;
    /// <summary>
    /// The door's reach for AT_LOCATION_BUILDING_DOOR (<c>AI::isWithinChoppingRange</c>, x2 standing) and its height slack.
    /// </summary>
    public const float DoorReach = 25, DoorHeight = 130;
    /// <summary>
    /// The desire of 81 / 287 for a bandit (<c>scoreAggressiveAction</c>).
    /// </summary>
    public const float BanditDesire = 1.1f;

    /// <summary>
    /// The world of the plan being made.
    /// </summary>
    World _world = null!;
    /// <summary>
    /// The combat system the aggression test reads (null: none).
    /// </summary>
    CombatSystem? _combat;
    /// <summary>
    /// The planning character's slot.
    /// </summary>
    int _me;
    /// <summary>
    /// Where the planning character wants to go.
    /// </summary>
    Vector3 _destination;
    /// <summary>
    /// The planning character.
    /// </summary>
    CharacterCold _cold = null!;
    /// <summary>
    /// The building instance the way in is shut by.
    /// </summary>
    string _building = "";
    /// <summary>
    /// The building's main door.
    /// </summary>
    Fixture _door = null!;
    /// <summary>
    /// Where the main door's leaf is.
    /// </summary>
    DoorSpot _spot;
    /// <summary>
    /// The character's desire to bash the door (<see cref="AggressiveTowards"/>).
    /// </summary>
    float _aggressive;
    /// <summary>
    /// The lock system the fortification state reads (null: none).
    /// </summary>
    LockSystem? _locks;
    /// <summary>
    /// The plan the last <see cref="Plans"/> made.
    /// </summary>
    readonly List<PlanStep> _plan = [];

    /// <summary>
    /// The human repertoire (1).
    /// </summary>
    public int Category => 0x1;
    /// <summary>
    /// Restricted rows are open: the attacker is planned as an NPC.
    /// </summary>
    public bool Squadless => true;

    /// <summary>
    /// Plans row 4 for attacker <paramref name="s"/> at <paramref name="t"/>, shut in <paramref name="shutIn"/> behind its main door; true when the
    /// plan bashes it.
    /// </summary>
    public bool PlansBash(World w, CombatSystem? fights, int s, int t, string shutIn, Fixture mainDoor, DoorSpot mainSpot, LockSystem? gates = null)
            =>
        Plans(w, fights, s, w.Characters.Next[t].Position, shutIn, mainDoor, mainSpot, gates, TaskType.MeleeAttack) == Entry.Bash;

    /// <summary>
    /// How a walk into a building goes: no way in, a walk (the door opens as the character passes, row 72), or the door is bashed first.
    /// </summary>
    public enum Entry
    {
        /// <summary>
        /// No way in.
        /// </summary>
        Blocked,
        /// <summary>
        /// A walk: the door opens as the character passes (row 72).
        /// </summary>
        Walk,
        /// <summary>
        /// The door is bashed first.
        /// </summary>
        Bash
    }

    /// <summary>
    /// Plans goal 216 (its effect 107 is provided by row 156 Entering building) for character <paramref name="s"/> (a ransacker) into
    /// <paramref name="shutIn"/> behind its main door, with state 59 as the original has it: <c>Building::hasAnOpenDoor</c>, so a closed unlocked
    /// door counts as shut and the planner opens it (72) or, for a locked one, bashes it (81) when the character wants to
    /// (<see cref="AggressiveTowards"/>).
    /// </summary>
    public Entry PlansEntry(World w, CombatSystem? fights, int s, string shutIn, Fixture mainDoor, DoorSpot mainSpot, LockSystem? gates) =>
        Plans(w, fights, s, mainSpot.Centre, shutIn, mainDoor, mainSpot, gates, TaskType.RunAroundTownLookingForPeople);

    /// <summary>
    /// Plans goal <paramref name="goal"/> for the character at <paramref name="s"/> towards <paramref name="to"/>, shut in <paramref name="shutIn"/>
    /// behind its main door, and says how the way in goes: blocked when there is no plan, a bash when the plan bashes the door, else a walk.
    /// </summary>
    Entry Plans(World w, CombatSystem? fights, int s, Vector3 to, string shutIn, Fixture mainDoor, DoorSpot mainSpot, LockSystem? gates,
            TaskType goal)
    {
        (_world, _combat, _me, _destination, _building, _door, _spot, _locks) = (w, fights, s, to, shutIn, mainDoor, mainSpot, gates);
        _cold = w.Characters.Cold(s)!;
        _aggressive = AggressiveTowards(w, fights, _cold, shutIn, mainSpot.Centre);
        if (TaskDataTable.Of(goal) is not { } row) return Entry.Blocked;
        var target = new PlanTarget(Foe, to);
        if (TaskPlanner.Local.Plan(row, 1, target, this, [], _plan) <= 0) return Entry.Blocked;
        foreach (var step in _plan)
            if (step.Type is TaskType.BashDoor or TaskType.BashDoorHere) return Entry.Bash;
        return Entry.Walk;
    }

    /// <summary>
    /// <c>AI::scoreAggressiveAction</c> for the building (docs/game/locks.md "Bash door", <b>Observed</b>): 0 in the basher's home town; a bandit
    /// 1.1; else 0 towards an ally, 1 towards a hostile faction (relation at or below the hostile threshold, coexistence not read: FUN_1406b26d0,
    /// Verified), and towards the rest 0 for a trader, civilian, diplomat or adventurer faction, else 1. Not modelled: the war-campaign case (its
    /// byte is <b>Unknown</b>).
    /// </summary>
    internal static float AggressiveTowards(World world, CombatSystem? combat, CharacterCold cold, string building, Vector3 at)
    {
        if (cold.SquadId >= 0 && world.Squads.Find(cold.SquadId)?.Ai is { Town: >= 0 } board && world.SystemOf<AiSystem>()?.TownAt(at) == board.Town)
            return 0;

        if (cold.NpcClass == FundamentalType.Bandit) return BanditDesire;
        int owner = world.Fixtures.DoorOwner(building);
        if (combat?.Relations is not { } relations || !relations.Contains(cold.Faction))
            return owner == cold.Faction ? 0 : 1;

        if (relations.Contains(owner))
        {
            if (relations.Get(cold.Faction, owner) >= FactionRelations.AllyThreshold) return 0;
            if (relations.IsHostile(cold.Faction, owner)) return 1;
        }

        return relations.Factions[cold.Faction].FundamentalType is FundamentalType.Trader or FundamentalType.Civilian or FundamentalType.Diplomat
                or FundamentalType.Adventurer ? 0 : 1;
    }

    /// <summary>
    /// The desire of a row on the way: the bash rows the character's aggression, else the row's task profile desire (1 for a random or own one).
    /// </summary>
    public float Desire(TaskDataRow row, in PlanTarget target)
    {
        // The gate rows (scoreBashGate: the same score, shared by the crowd) never win on a house: 81 comes first.
        if (row.Type is TaskType.BashDoor or TaskType.BashDoorHere or TaskType.BashGate or TaskType.Unnamed290)
            return _aggressive;

        float d = TaskProfiles.Of(row.Type).Desire;
        return d >= 0 ? d : 1;
    }

    /// <summary>
    /// A door state's value for a target (the door's open state, the character at the door or the foe, its weapon, the way not blocked by
    /// fortifications); the rest of row 4's list B is held.
    /// </summary>
    public bool Holds(TaskCondition condition, in PlanTarget target)
    {
        ref var n = ref _world.Characters.Next[_me];
        bool atBuilding = target.Kind == Building;
        bool value = condition.State switch
        {
            // hasAnOpenDoor (FUN_140597030, the state's evaluator for every goal): a door broken, open or opening, so a closed unlocked door counts
            // as shut.
            StateType.DestinationIsAccessible => _world.Fixtures.HasOpenDoor(_building),
            StateType.IsWithin50Meters => Vector2.Distance(Flat(n.Position), Flat(_destination)) < AiSystem.Within50Meters,
            StateType.DoorIsOpen => _door.DoorBroken || _door.Motion is DoorMotion.Open or DoorMotion.Opening,
            StateType.IsDoorLocked => _door.Lock is { EffectivelyLocked: true } && !_door.DoorBroken,
            StateType.DoorIsBroken => _door.DoorBroken,
            StateType.AtLocationBuildingDoor => atBuilding && AtDoor(n, _spot),
            StateType.IsObjectMine => _world.Fixtures.DoorOwner(_building) == _cold.Faction,
            StateType.IsAlly => _combat?.Relations is { } r && r.Contains(_cold.Faction) && r.Contains(_world.Fixtures.DoorOwner(_building))
                && r.Get(_cold.Faction, _world.Fixtures.DoorOwner(_building)) >= FactionRelations.AllyThreshold,
            // Task_BashDoor wants a weapon; an animal's natural one counts.
            StateType.HasWeaponEquipped => _cold.Fighter is { } f && (f.IsAnimal || !f.Unarmed),
            // 90 is evaluated for house doors as for gates (FUN_140597160, Verified): the walk to the door or the foe must not lie behind a gate that
            // is not open.
            StateType.DestinationNotBlockedByFortifications => _locks is null
                    || _locks.NotBlocked(_world, n.Position, atBuilding ? _spot.Centre : _destination),
            StateType.IsMovementAllowed or StateType.LocationIsAccessible or StateType.IsReadyForAction or StateType.CrowdLimit8 => true,
            _ => false,
        };
        return value == condition.Holds;
    }

    /// <summary>
    /// 85's finder (<c>AI::getDestinationBuilding</c>): the building the target is in. 216's (<c>FUN_1408cb510</c>): the building the caller drew, as
    /// the destination.
    /// </summary>
    public PlanTarget Find(TaskDataRow row, in PlanTarget target) => row.Type switch
    {
        TaskType.OpenDoorForDestination => new PlanTarget(Building, _spot.Centre),
        TaskType.RunAroundTownLookingForPeople => new PlanTarget(Foe, _destination),
        _ => default,
    };

    /// <summary>
    /// AT_LOCATION_BUILDING_DOOR (<c>AI::isWithinChoppingRange</c>, <b>Observed</b>): within <see cref="DoorReach"/> of the door's leaf on the ground
    /// (x2 while standing) and <see cref="DoorHeight"/> in height. Engine reading: the leaf's centre stands in for the door's position marker; the
    /// gateway's 45 is not modelled.
    /// </summary>
    internal static bool AtDoor(in CharacterHot n, DoorSpot spot)
    {
        float reach = (n.Flags & (ushort)MoveFlags.AnyPath) == 0 ? DoorReach * 2 : DoorReach;
        return Vector2.Distance(Flat(n.Position), Flat(spot.Centre)) < reach && MathF.Abs(n.Position.Y - spot.Foot) <= DoorHeight;
    }

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);
}