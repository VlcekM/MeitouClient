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
/// DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS (90), whose provider 122 Open gate finds the gate and needs it unlocked (!54), which 123 / 290 Bashing
/// gate give at the gate (AT_LOCATION_FORTIFICATION_GATE 91, walked to by 121). Only the gate's states are evaluated; the rest of row 4's list B is
/// held. Serial phase only. 122 is a restricted row, which the original's planner offers to every plan of a character whose faction is not the
/// player's (<c>FUN_14051bae0</c>, <b>Verified</b>); the attackers here are NPCs.
/// </summary>
sealed class GatePlan : IPlanWorld
{
    /// <summary>
    /// Kinds of <see cref="PlanTarget"/>: the attack target, the gate (at its main door's leaf), and the wall 270 falls back to when no gate leads to
    /// the target.
    /// </summary>
    public const byte Foe = 1, Gate = 2, Wall = 3;
    /// <summary>
    /// A gateway's reach (<c>getReachRange</c>, docs/game/locks.md "Door and place"; x2 standing), and the most bashers one gate takes (crowdLimit8).
    /// </summary>
    public const float GateReach = 45;
    /// <summary>
    /// The most bashers one gate takes (crowdLimit8; see <see cref="GateReach"/>).
    /// </summary>
    public const int CrowdLimit = 8;

    /// <summary>
    /// The world of the plan being made.
    /// </summary>
    World _world = null!;
    /// <summary>
    /// The combat system the aggression test reads (null: none).
    /// </summary>
    CombatSystem? _combat;
    /// <summary>
    /// The lock system the fortification state reads.
    /// </summary>
    LockSystem _locks = null!;
    /// <summary>
    /// The planning character's slot.
    /// </summary>
    int _me;
    /// <summary>
    /// Where the planning character wants to go.
    /// </summary>
    Vector3 _to;
    /// <summary>
    /// The goal row planned.
    /// </summary>
    TaskType _goal;
    /// <summary>
    /// The planning character.
    /// </summary>
    CharacterCold _cold = null!;
    /// <summary>
    /// The gate building's instance id ("" for a wall smash).
    /// </summary>
    string _building = "";
    /// <summary>
    /// The gate's main door.
    /// </summary>
    Fixture _door = null!;
    /// <summary>
    /// Where the main door's leaf is.
    /// </summary>
    DoorSpot _spot;
    /// <summary>
    /// The wall 270 falls back to (null: a gate is planned).
    /// </summary>
    BuildSite? _wall;
    /// <summary>
    /// The character's desire to bash the gate (1 for a wall smash).
    /// </summary>
    float _aggressive;
    /// <summary>
    /// The plan the last <see cref="Run"/> made.
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
    /// Plans row 4 for attacker <paramref name="s"/> at <paramref name="t"/> through gate <paramref name="gate"/> (its main door and leaf); true when
    /// the plan bashes it.
    /// </summary>
    public bool PlansBash(World w, CombatSystem? fights, LockSystem lockSystem, int s, int t, string gate, Fixture mainDoor, DoorSpot mainSpot) =>
        PlansBash(w, fights, lockSystem, s, t, gate, mainDoor, mainSpot, null);

    /// <summary>
    /// Plans row 4 through <paramref name="smash"/>, a wall of the attacker's region, when no gate leads to the target (270's fallback, 272
    /// Smashing); true when the plan smashes it.
    /// </summary>
    public bool PlansSmash(World w, CombatSystem? fights, LockSystem lockSystem, int s, int t, BuildSite smash) =>
        PlansBash(w, fights, lockSystem, s, t, "", null!, default, smash);

    /// <summary>
    /// Plans row 4 for attacker <paramref name="s"/> at <paramref name="t"/> through gate <paramref name="gate"/> or wall <paramref name="smash"/>;
    /// true when the plan bashes or smashes it.
    /// </summary>
    bool PlansBash(World w, CombatSystem? fights, LockSystem lockSystem, int s, int t, string gate, Fixture mainDoor, DoorSpot mainSpot,
            BuildSite? smash) =>
        Run(w, fights, lockSystem, s, w.Characters.Next[t].Position, TaskType.MeleeAttack, gate, mainDoor, mainSpot, smash) == DoorPlan.Entry.Bash;

    /// <summary>
    /// Plans goal 216 (ransacking: row 156 Entering building needs state 90) for ransacker <paramref name="s"/> into a building at
    /// <paramref name="dest"/> behind gate <paramref name="gate"/>: blocked when no plan exists, a bash when the plan bashes the gate (123 / 290),
    /// else a walk (an unlocked closed gate is opened, row 122).
    /// </summary>
    public DoorPlan.Entry PlansEntry(World w, CombatSystem? fights, LockSystem lockSystem, int s, Vector3 dest, string gate, Fixture mainDoor,
            DoorSpot mainSpot) =>
        Run(w, fights, lockSystem, s, dest, TaskType.RunAroundTownLookingForPeople, gate, mainDoor, mainSpot, null);

    /// <summary>
    /// Plans <paramref name="goalRow"/> for the character at <paramref name="s"/> towards <paramref name="dest"/> through the gate or wall and says
    /// how the way in goes: blocked, a walk or a bash.
    /// </summary>
    DoorPlan.Entry Run(World w, CombatSystem? fights, LockSystem lockSystem, int s, Vector3 dest, TaskType goalRow, string gate, Fixture mainDoor,
            DoorSpot mainSpot, BuildSite? smash)
    {
        (_world, _combat, _locks, _me, _to, _goal, _building, _door, _spot, _wall) = (w, fights, lockSystem, s, dest, goalRow, gate, mainDoor,
                mainSpot, smash);
        _cold = w.Characters.Cold(s)!;
        _aggressive = smash is null ? DoorPlan.AggressiveTowards(w, fights, _cold, gate, mainSpot.Centre) : 1;
        if (TaskDataTable.Of(_goal) is not { } row) return DoorPlan.Entry.Blocked;
        var target = new PlanTarget(Foe, dest);
        if (TaskPlanner.Local.Plan(row, 1, target, this, [], _plan) <= 0) return DoorPlan.Entry.Blocked;
        foreach (var step in _plan)
        {
            if (step.Type is TaskType.BashGate or TaskType.Unnamed290 or TaskType.BashDoor or TaskType.SmashBuilding)
                return DoorPlan.Entry.Bash;
        }

        return DoorPlan.Entry.Walk;
    }

    /// <summary>
    /// The desire of a row on the way: the bash and smash rows the character's aggression, else the row's task profile desire (1 for a random or
    /// own one).
    /// </summary>
    public float Desire(TaskDataRow row, in PlanTarget target)
    {
        if (row.Type is TaskType.BashDoor or TaskType.BashDoorHere or TaskType.BashGate or TaskType.Unnamed290 or TaskType.SmashBuilding)
            return _aggressive;

        float d = TaskProfiles.Of(row.Type).Desire;
        return d >= 0 ? d : 1;
    }

    /// <summary>
    /// A gate state's value for a target (the gate's open state, the character at the gate or the foe, the way not blocked by fortifications, the
    /// wall); the rest is held.
    /// </summary>
    public bool Holds(TaskCondition condition, in PlanTarget target)
    {
        ref var n = ref _world.Characters.Next[_me];
        bool atGate = target.Kind == Gate;
        int owner = _wall?.Owner ?? _world.Fixtures.DoorOwner(_building);
        bool value = condition.State switch
        {
            StateType.DestinationNotBlockedByFortifications => _locks.NotBlocked(_world, n.Position, _to),
            // 163 isBuildingDestroyed (272's effect): the wall is gone, or the gate's door is broken; 46 isAtLocation: in reach of the wall or the
            // gate.
            StateType.IsBuildingDestroyed => _wall is not null ? _wall.Destroyed : _door.DoorBroken,
            StateType.IsAtLocation46 => target.Kind == Wall ? AtWall(_world, _locks, _wall, n) : atGate && AtGate(n, _spot),
            StateType.DoorIsOpen => _door is not null && (_door.DoorBroken || _door.Motion is DoorMotion.Open or DoorMotion.Opening),
            StateType.IsDoorLocked => _door is not null && _door.Lock is { EffectivelyLocked: true } && !_door.DoorBroken,
            StateType.DoorIsBroken => _door is not null && _door.DoorBroken,
            StateType.AtLocationFortificationGate or StateType.AtLocationBuildingDoor => atGate && AtGate(n, _spot),
            StateType.IsObjectMine => owner == _cold.Faction,
            StateType.IsAlly => _combat?.Relations is { } r && r.Contains(_cold.Faction) && r.Contains(owner)
                    && r.Get(_cold.Faction, owner) >= FactionRelations.AllyThreshold,
            StateType.HasWeaponEquipped => _cold.Fighter is { } f && (f.IsAnimal || !f.Unarmed),
            // 123 / 290 carry !158: a melee threat within reach of the basher refuses the plan (Character +0x2b0, docs/game/combat.md "Threats").
            StateType.IAmBeingMeleeAttackedImmediate => _combat is not null && CharacterFacts.ImmediateThreat(_combat, _world, _me),
            StateType.CrowdLimit8 => LockSystem.BashersAt(_world, _wall?.Id ?? _door!.Id) < CrowdLimit,
            StateType.IsMovementAllowed or StateType.LocationIsAccessible or StateType.DestinationIsAccessible or StateType.IsReadyForAction
                or StateType.IsWithin50Meters => true,
            _ => false,
        };
        return value == condition.Holds;
    }

    /// <summary>
    /// 122's finder: the gate (the first one on the route to the target, <c>FUN_1402ebdd0</c>; engine reading: the nearest of the equally good).
    /// 270's (<c>FUN_1405a3f60</c>): the same gate first, else the wall of the attacker's region the caller picked
    /// (<see cref="LockSystem.NearestWall"/>).
    /// </summary>
    public PlanTarget Find(TaskDataRow row, in PlanTarget target)
    {
        bool gated = _wall is null;
        return row.Type switch
        {
            TaskType.RunAroundTownLookingForPeople => new PlanTarget(Foe, _to),   // 216's finder: the building the caller drew
            TaskType.OpenFortificationGate when gated => new PlanTarget(Gate, _spot.Centre),
            TaskType.AssaultFortificationsPreferGates => gated ? new PlanTarget(Gate, _spot.Centre) : new PlanTarget(Wall, _wall!.Position),
            _ => default,
        };
    }

    /// <summary>
    /// The wall's isWithinChoppingRange (<c>FUN_1405a25b0</c>, as <see cref="AtGate"/>): within the wall's reach range (<see cref="SiteReach.Wall"/>)
    /// of its footprint (its position without a construction system), at its height. 272's state 46 has only 67 Running as provider, whose own list B
    /// needs state 90 again (a repeat of 270): 270 -> 272 plans only for an attacker already in reach of the wall (tested).
    /// </summary>
    internal static bool AtWall(World world, LockSystem locks, BuildSite? wall, in CharacterHot n) =>
        wall is not null && SiteReach.Within(n, SiteReach.Gap(locks.Combat?.Construction?.Settings.EdgeGap, wall, n.Position), SiteReach.Wall)
                && SiteReach.AtHeight(n, wall, door: false);

    /// <summary>
    /// AT_LOCATION_FORTIFICATION_GATE (<c>FUN_1405a25b0</c> → <c>AI::isWithinChoppingRange</c>, <b>Verified</b> call): within
    /// <see cref="GateReach"/> of the leaf (x2 standing).
    /// </summary>
    internal static bool AtGate(in CharacterHot n, DoorSpot spot)
    {
        float reach = (n.Flags & (ushort)MoveFlags.AnyPath) == 0 ? GateReach * 2 : GateReach;
        return Vector2.Distance(new(n.Position.X, n.Position.Z), new(spot.Centre.X, spot.Centre.Z)) < reach
                && MathF.Abs(n.Position.Y - spot.Foot) <= DoorPlan.DoorHeight;
    }
}