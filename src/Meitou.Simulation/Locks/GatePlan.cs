using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The planner's view of an NPC attacker whose target is behind a gate that is not open (docs/game/locks.md "Gates"): row 4 Attacking needs
/// DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS (90), whose provider 122 Open gate finds the gate and needs it unlocked (!54), which 123 / 290 Bashing gate give at the gate
/// (AT_LOCATION_FORTIFICATION_GATE 91, walked to by 121). Only the gate's states are evaluated; the rest of row 4's list B is held. Serial phase only.
/// 122 is a restricted row, which the original's planner offers to every plan of a character whose faction is not the player's (<c>FUN_14051bae0</c>, <b>Verified</b>); the attackers here are NPCs.
/// </summary>
sealed class GatePlan : IPlanWorld
{
    /// <summary>Kinds of <see cref="PlanTarget"/>: the attack target, the gate (at its main door's leaf), and the wall 270 falls back to when no gate leads to the target.</summary>
    public const byte Foe = 1, Gate = 2, Wall = 3;
    /// <summary>A gateway's reach (<c>getReachRange</c>, docs/game/locks.md "Door and place"; x2 standing), and the most bashers one gate takes (crowdLimit8).</summary>
    public const float GateReach = 45;
    public const int CrowdLimit = 8;

    World world = null!;
    CombatSystem? combat;
    LockSystem locks = null!;
    int me;
    Vector3 to;
    TaskType goal;
    CharacterCold cold = null!;
    string building = "";
    Fixture door = null!;
    DoorSpot spot;
    BuildSite? wall;
    float aggressive;
    readonly List<PlanStep> plan = [];

    public int Category => 0x1;
    public bool Squadless => true;

    /// <summary>Plans row 4 for attacker <paramref name="s"/> at <paramref name="t"/> through gate <paramref name="gate"/> (its main door and leaf); true when the plan bashes it.</summary>
    public bool PlansBash(World w, CombatSystem? fights, LockSystem lockSystem, int s, int t, string gate, Fixture mainDoor, DoorSpot mainSpot) =>
        PlansBash(w, fights, lockSystem, s, t, gate, mainDoor, mainSpot, null);

    /// <summary>
    /// Plans row 4 through <paramref name="smash"/>, a wall of the attacker's region, when no gate leads to the target (270's fallback, 272 Smashing); true when the plan smashes it.
    /// </summary>
    public bool PlansSmash(World w, CombatSystem? fights, LockSystem lockSystem, int s, int t, BuildSite smash) =>
        PlansBash(w, fights, lockSystem, s, t, "", null!, default, smash);

    bool PlansBash(World w, CombatSystem? fights, LockSystem lockSystem, int s, int t, string gate, Fixture mainDoor, DoorSpot mainSpot, BuildSite? smash) =>
        Run(w, fights, lockSystem, s, w.Characters.Next[t].Position, TaskType.MELEE_ATTACK, gate, mainDoor, mainSpot, smash) == DoorPlan.Entry.Bash;

    /// <summary>
    /// Plans goal 216 (ransacking: row 156 Entering building needs state 90) for ransacker <paramref name="s"/> into a building at <paramref name="dest"/> behind gate <paramref name="gate"/>: blocked when no plan exists,
    /// a bash when the plan bashes the gate (123 / 290), else a walk (an unlocked closed gate is opened, row 122).
    /// </summary>
    public DoorPlan.Entry PlansEntry(World w, CombatSystem? fights, LockSystem lockSystem, int s, Vector3 dest, string gate, Fixture mainDoor, DoorSpot mainSpot) =>
        Run(w, fights, lockSystem, s, dest, TaskType.RUN_AROUND_TOWN_LOOKING_FOR_PEOPLE, gate, mainDoor, mainSpot, null);

    DoorPlan.Entry Run(World w, CombatSystem? fights, LockSystem lockSystem, int s, Vector3 dest, TaskType goalRow, string gate, Fixture mainDoor, DoorSpot mainSpot, BuildSite? smash)
    {
        (world, combat, locks, me, to, goal, building, door, spot, wall) = (w, fights, lockSystem, s, dest, goalRow, gate, mainDoor, mainSpot, smash);
        cold = w.Characters.Cold(s)!;
        aggressive = smash is null ? DoorPlan.AggressiveTowards(w, fights, cold, gate, mainSpot.Centre) : 1;
        if (TaskDataTable.Of(goal) is not { } row) return DoorPlan.Entry.Blocked;
        var target = new PlanTarget(Foe, dest);
        if (TaskPlanner.Local.Plan(row, 1, target, this, [], plan) <= 0) return DoorPlan.Entry.Blocked;
        foreach (var step in plan)
            if (step.Type is TaskType.BASH_GATE or TaskType.Unnamed290 or TaskType.BASH_DOOR or TaskType.SMASH_BUILDING) return DoorPlan.Entry.Bash;
        return DoorPlan.Entry.Walk;
    }

    public float Desire(TaskDataRow row, in PlanTarget target)
    {
        if (row.Type is TaskType.BASH_DOOR or TaskType.BASH_DOOR_HERE or TaskType.BASH_GATE or TaskType.Unnamed290 or TaskType.SMASH_BUILDING) return aggressive;
        float d = TaskProfiles.Of(row.Type).Desire;
        return d >= 0 ? d : 1;
    }

    public bool Holds(TaskCondition condition, in PlanTarget target)
    {
        ref var n = ref world.Characters.Next[me];
        bool atGate = target.Kind == Gate;
        int owner = wall?.Owner ?? world.Fixtures.DoorOwner(building);
        bool value = condition.State switch
        {
            StateType.DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS => locks.NotBlocked(world, n.Position, to),
            // 163 isBuildingDestroyed (272's effect): the wall is gone, or the gate's door is broken; 46 isAtLocation: in reach of the wall or the gate.
            StateType.isBuildingDestroyed => wall is not null ? wall.Destroyed : door.DoorBroken,
            StateType.isAtLocation_46 => target.Kind == Wall ? AtWall(world, locks, wall, n) : atGate && AtGate(n, spot),
            StateType.DOOR_IS_OPEN => door is not null && (door.DoorBroken || door.Motion is DoorMotion.Open or DoorMotion.Opening),
            StateType.isDoorLocked => door is not null && door.Lock is { EffectivelyLocked: true } && !door.DoorBroken,
            StateType.DOOR_IS_BROKEN => door is not null && door.DoorBroken,
            StateType.AT_LOCATION_FORTIFICATION_GATE or StateType.AT_LOCATION_BUILDING_DOOR => atGate && AtGate(n, spot),
            StateType.isObjectMine => owner == cold.Faction,
            StateType.isAlly => combat?.Relations is { } r && r.Contains(cold.Faction) && r.Contains(owner) && r.Get(cold.Faction, owner) >= FactionRelations.AllyThreshold,
            StateType.hasWeaponEquipped => cold.Fighter is { } f && (f.IsAnimal || !f.Unarmed),
            // 123 / 290 carry !158: a melee threat within reach of the basher refuses the plan (Character +0x2b0, docs/game/combat.md "Threats").
            StateType.iAmBeingMeleeAttacked_immediate => combat is not null && CharacterFacts.ImmediateThreat(combat, world, me),
            StateType.crowdLimit8 => LockSystem.BashersAt(world, wall?.Id ?? door!.Id) < CrowdLimit,
            StateType.isMovementAllowed or StateType.LOCATION_IS_ACCESSIBLE or StateType.DESTINATION_IS_ACCESSIBLE or StateType.isReadyForAction
                or StateType.isWithin50Meters => true,
            _ => false,
        };
        return value == condition.Holds;
    }

    /// <summary>
    /// 122's finder: the gate (the first one on the route to the target, <c>FUN_1402ebdd0</c>; engine reading: the nearest of the equally good). 270's (<c>FUN_1405a3f60</c>): the same gate first, else
    /// the wall of the attacker's region the caller picked (<see cref="LockSystem.NearestWall"/>).
    /// </summary>
    public PlanTarget Find(TaskDataRow row, in PlanTarget target)
    {
        bool gated = wall is null;
        return row.Type switch
        {
            TaskType.RUN_AROUND_TOWN_LOOKING_FOR_PEOPLE => new PlanTarget(Foe, to),   // 216's finder: the building the caller drew
            TaskType.OPEN_FORTIFICATION_GATE when gated => new PlanTarget(Gate, spot.Centre),
            TaskType.ASSAULT_FORTIFICATIONS_PREFER_GATES => gated ? new PlanTarget(Gate, spot.Centre) : new PlanTarget(Wall, wall!.Position),
            _ => default,
        };
    }

    /// <summary>
    /// The wall's isWithinChoppingRange (<c>FUN_1405a25b0</c>, as <see cref="AtGate"/>): within the wall's reach range (<see cref="SiteReach.Wall"/>) of its footprint (its position without a construction system), at its height.
    /// 272's state 46 has only 67 Running as provider, whose own list B needs state 90 again (a repeat of 270): 270 -> 272 plans only for an attacker already in reach of the wall (tested).
    /// </summary>
    internal static bool AtWall(World world, LockSystem locks, BuildSite? wall, in CharacterHot n) =>
        wall is not null && SiteReach.Within(n, SiteReach.Gap(locks.Combat?.Construction?.Settings.EdgeGap, wall, n.Position), SiteReach.Wall) && SiteReach.AtHeight(n, wall, door: false);

    /// <summary>AT_LOCATION_FORTIFICATION_GATE (<c>FUN_1405a25b0</c> → <c>AI::isWithinChoppingRange</c>, <b>Verified</b> call): within <see cref="GateReach"/> of the leaf (x2 standing).</summary>
    internal static bool AtGate(in CharacterHot n, DoorSpot spot)
    {
        float reach = (n.Flags & (ushort)MoveFlags.AnyPath) == 0 ? GateReach * 2 : GateReach;
        return Vector2.Distance(new(n.Position.X, n.Position.Z), new(spot.Centre.X, spot.Centre.Z)) < reach && MathF.Abs(n.Position.Y - spot.Foot) <= DoorPlan.DoorHeight;
    }
}
