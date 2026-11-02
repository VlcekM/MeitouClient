using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The planner's view of an NPC attacker whose target is shut in a building (docs/game/locks.md "Bash door"): row 4 Attacking needs DESTINATION_IS_ACCESSIBLE, its provider 85
/// finds the building and wants DOOR_IS_OPEN, whose providers (72 Opening door, 81 and 287 Bashing door) the <see cref="TaskPlanner"/> weighs as the original does. Only the door
/// states are evaluated; the rest of row 4's list B is held (the fight runs it). Serial phase only.
/// </summary>
sealed class DoorPlan : IPlanWorld
{
    /// <summary>Kinds of <see cref="PlanTarget"/>: the attack target, and the building (at its main door's leaf).</summary>
    public const byte Foe = 1, Building = 2;
    /// <summary>The door's reach for AT_LOCATION_BUILDING_DOOR (<c>AI::isWithinChoppingRange</c>, x2 standing) and its height slack.</summary>
    public const float DoorReach = 25, DoorHeight = 130;
    /// <summary>The desire of 81 / 287 for a bandit (<c>scoreAggressiveAction</c>).</summary>
    public const float BanditDesire = 1.1f;

    World world = null!;
    CombatSystem? combat;
    int me, foe;
    CharacterCold cold = null!;
    string building = "";
    Fixture door = null!;
    DoorSpot spot;
    float aggressive;
    LockSystem? locks;
    readonly List<PlanStep> plan = [];

    public int Category => 0x1;
    public bool Squadless => true;

    /// <summary>
    /// Plans row 4 for attacker <paramref name="s"/> at <paramref name="t"/>, shut in <paramref name="shutIn"/> behind its main door; true when the plan bashes it.
    /// </summary>
    public bool PlansBash(World w, CombatSystem? fights, int s, int t, string shutIn, Fixture mainDoor, DoorSpot mainSpot, LockSystem? gates = null)
    {
        (world, combat, me, foe, building, door, spot, locks) = (w, fights, s, t, shutIn, mainDoor, mainSpot, gates);
        cold = w.Characters.Cold(s)!;
        aggressive = AggressiveTowards(w, fights, cold, shutIn, mainSpot.Centre);
        if (TaskDataTable.Of(TaskType.MELEE_ATTACK) is not { } row) return false;
        var target = new PlanTarget(Foe, w.Characters.Next[t].Position);
        if (TaskPlanner.Local.Plan(row, 1, target, this, [], plan) <= 0) return false;
        foreach (var step in plan)
            if (step.Type is TaskType.BASH_DOOR or TaskType.BASH_DOOR_HERE) return true;
        return false;
    }

    /// <summary>
    /// <c>AI::scoreAggressiveAction</c> for the building (docs/game/locks.md "Bash door", <b>Observed</b>): 0 in the basher's home town; a bandit 1.1; else 0 towards an ally,
    /// 1 towards a hostile faction (relation at or below the hostile threshold, coexistence not read: FUN_1406b26d0, Verified), and towards the rest 0 for a trader, civilian, diplomat or adventurer faction, else 1. Not modelled: the war-campaign case (its byte is <b>Unknown</b>).
    /// </summary>
    internal static float AggressiveTowards(World world, CombatSystem? combat, CharacterCold cold, string building, Vector3 at)
    {
        if (cold.SquadId >= 0 && world.Squads.Find(cold.SquadId)?.Ai is { Town: >= 0 } board && world.SystemOf<AiSystem>()?.TownAt(at) == board.Town) return 0;
        if (cold.NpcClass == FundamentalType.Bandit) return BanditDesire;
        int owner = world.Fixtures.DoorOwner(building);
        if (combat?.Relations is not { } relations || !relations.Contains(cold.Faction)) return owner == cold.Faction ? 0 : 1;
        if (relations.Contains(owner))
        {
            if (relations.Get(cold.Faction, owner) >= FactionRelations.AllyThreshold) return 0;
            if (relations.IsHostile(cold.Faction, owner)) return 1;
        }
        return relations.Factions[cold.Faction].FundamentalType is FundamentalType.Trader or FundamentalType.Civilian or FundamentalType.Diplomat or FundamentalType.Adventurer ? 0 : 1;
    }

    public float Desire(TaskDataRow row, in PlanTarget target)
    {
        // The gate rows (scoreBashGate: the same score, shared by the crowd) never win on a house: 81 comes first.
        if (row.Type is TaskType.BASH_DOOR or TaskType.BASH_DOOR_HERE or TaskType.BASH_GATE or TaskType.Unnamed290) return aggressive;
        float d = TaskProfiles.Of(row.Type).Desire;
        return d >= 0 ? d : 1;
    }

    public bool Holds(TaskCondition condition, in PlanTarget target)
    {
        ref var n = ref world.Characters.Next[me];
        bool atBuilding = target.Kind == Building;
        bool value = condition.State switch
        {
            // hasAnOpenDoor: none broken, open or opening (Meitou passes a closed unlocked door, so a shut building is one whose doors are all locked).
            StateType.DESTINATION_IS_ACCESSIBLE => !world.Fixtures.IsShut(building),
            StateType.isWithin50Meters => Vector2.Distance(Flat(n.Position), Flat(world.Characters.Next[foe].Position)) <= 50,
            StateType.DOOR_IS_OPEN => door.DoorBroken || door.Motion is DoorMotion.Open or DoorMotion.Opening,
            StateType.isDoorLocked => door.Lock is { EffectivelyLocked: true } && !door.DoorBroken,
            StateType.DOOR_IS_BROKEN => door.DoorBroken,
            StateType.AT_LOCATION_BUILDING_DOOR => atBuilding && AtDoor(n, spot),
            StateType.isObjectMine => world.Fixtures.DoorOwner(building) == cold.Faction,
            StateType.isAlly => combat?.Relations is { } r && r.Contains(cold.Faction) && r.Contains(world.Fixtures.DoorOwner(building))
                && r.Get(cold.Faction, world.Fixtures.DoorOwner(building)) >= FactionRelations.AllyThreshold,
            // Task_BashDoor wants a weapon; an animal's natural one counts.
            StateType.hasWeaponEquipped => cold.Fighter is { } f && (f.IsAnimal || !f.Unarmed),
            // 90 is evaluated for house doors as for gates (FUN_140597160, Verified): the walk to the door or the foe must not lie behind a gate that is not open.
            StateType.DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS => locks is null || locks.NotBlocked(world, n.Position, atBuilding ? spot.Centre : world.Characters.Next[foe].Position),
            StateType.isMovementAllowed or StateType.LOCATION_IS_ACCESSIBLE or StateType.isReadyForAction or StateType.crowdLimit8 => true,
            _ => false,
        };
        return value == condition.Holds;
    }

    /// <summary>85's finder (<c>AI::getDestinationBuilding</c>): the building the target is in.</summary>
    public PlanTarget Find(TaskDataRow row, in PlanTarget target) =>
        row.Type == TaskType.OPEN_DOOR_FOR_DESTINATION ? new PlanTarget(Building, spot.Centre) : default;

    /// <summary>
    /// AT_LOCATION_BUILDING_DOOR (<c>AI::isWithinChoppingRange</c>, <b>Observed</b>): within <see cref="DoorReach"/> of the door's leaf on the ground (x2 while standing) and
    /// <see cref="DoorHeight"/> in height. Engine reading: the leaf's centre stands in for the door's position marker; the gateway's 45 is not modelled.
    /// </summary>
    internal static bool AtDoor(in CharacterHot n, DoorSpot spot)
    {
        float reach = (n.Flags & (ushort)MoveFlags.AnyPath) == 0 ? DoorReach * 2 : DoorReach;
        return Vector2.Distance(Flat(n.Position), Flat(spot.Centre)) < reach && MathF.Abs(n.Position.Y - spot.Foot) <= DoorHeight;
    }

    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);
}
