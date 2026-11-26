using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The target of an order or job row whose lists are tested (<see cref="RowStates"/>): the character it names (<see cref="CharacterId.None"/>: none),
/// the site, a fixture or building id (null: none), the task's point (the original's tasker <c>+0x58</c>, <see cref="Player.OrderEntry.TaskPoint"/>;
/// zero: none) and the ground item it names (<see cref="Items.GroundPick"/>; null: none).
/// </summary>
public readonly record struct RowTarget(CharacterId Character, string? Site, Vector3 Point = default, Items.GroundPick? Item = null);

/// <summary>
/// The states of the order and job rows' lists for a character with no mind (a player character; docs/game/ai-tasks.md "Finished tests" and
/// "StateType ids"): the evaluators of the original's state registry (<c>FUN_140610250</c>, each thunk followed to its function), read on Meitou's
/// world. A state Meitou does not evaluate here answers null, and a list holding one is never taken as holding (so nothing is popped or skipped on
/// it). Pure reads (serial phases).
/// </summary>
public static class RowStates
{
    #region Lists

    /// <summary>
    /// List A as <c>FUN_14060f570</c> reads it: every entry holds, and an empty list never does. Null when an entry is not evaluated here.
    /// </summary>
    public static bool? Done(World world, int slot, TaskDataRow row, in RowTarget target)
    {
        if (row.Done.Length == 0) return false;

        return All(world, slot, row.Done, target);
    }

    /// <summary>
    /// List B as "can run" reads it without the finder (<c>FUN_14060f940</c>): every entry holds. Null when an entry is not evaluated here.
    /// </summary>
    public static bool? Needs(World world, int slot, TaskDataRow row, in RowTarget target) => All(world, slot, row.Needs, target);

    /// <summary>
    /// Whether an entry of list B that is evaluated here fails, the walk's <c>isAtLocation</c> left out (the plan step's end of a running row,
    /// <see cref="ListTests.OrderNeedsFail"/>): one Meitou does not evaluate never ends it.
    /// </summary>
    public static bool NeedFails(World world, int slot, TaskDataRow row, in RowTarget target) => FirstUnmet(world, slot, row, target) is not null;

    /// <summary>
    /// The first entry of list B, in list order, that is evaluated here and fails, the walk's <c>isAtLocation</c> left out (the planner follows the
    /// first unmet entry, <c>FUN_14051bae0</c>); null when none does.
    /// </summary>
    public static TaskCondition? FirstUnmet(World world, int slot, TaskDataRow row, in RowTarget target)
    {
        foreach (var c in row.Needs)
        {
            if (c.State == StateType.IsAtLocation) continue;
            if (Holds(world, slot, c.State, target) is { } value && value != c.Holds) return c;
        }

        return null;
    }

    /// <summary>
    /// Whether every condition holds: false at the first that does not, null when one is not evaluated here (and none failed before it).
    /// </summary>
    static bool? All(World world, int slot, TaskCondition[] list, in RowTarget target)
    {
        foreach (var c in list)
        {
            if (Holds(world, slot, c.State, target) is not { } value) return null;
            if (value != c.Holds) return false;
        }

        return true;
    }

    #endregion

    #region States

    /// <summary>
    /// 52 DOOR_IS_OPEN (<c>FUN_1405a3ad0</c>, <b>Verified</b>): the door's state +0x380 is 1 (open), or 2 (opening) with its open amount +0x37c past
    /// 0.8 (<c>DAT_141693ea8</c>).
    /// </summary>
    const float OpenPast = 0.8f;

    /// <summary>
    /// State 1's reach of a point target: flat squared distance (<c>DAT_1416ad830</c>, 144) and height (<c>DAT_141683188</c>, 20).
    /// </summary>
    const float AtFlat2 = 144, AtHeight = 20;

    /// <summary>
    /// The rested level above which state 72 holds (<c>DAT_14168a25c</c>, 0.9 as a float).
    /// </summary>
    public const float RestedPast = 0.9f;

    /// <summary>
    /// A state's value for the character at <paramref name="slot"/> and its row's target; null for a state not evaluated here.
    /// </summary>
    public static bool? Holds(World world, int slot, StateType state, in RowTarget target)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        return state switch
        {
            // AI slot 10 (FUN_140641540) answers 0: state 0 none (and 135, which shares the thunk) never holds, so 96's and 110's list A never does.
            StateType.None => false,
            StateType.IsAtLocation => target.Character.IsNone && target.Site is null && AtPoint(world, slot, target.Point),
            StateType.IsSubjectInInventory => target.Item is { } pick && SubjectInInventory(world, cold, pick),
            // 123 and 136 share the thunk 0x14002fb1c, which returns false.
            StateType.IsWorking or StateType.IsWorkingPretend => false,
            StateType.IsBuildingComplete => BuildingComplete(world, target.Site),
            StateType.IsBuildingDamaged => BuildingDamaged(world, target.Site),
            StateType.BuildingHasPower => HasPower(world, target.Site),
            StateType.DoorIsOpen => Door(world, target.Site) is { } open
                && (open.Motion == DoorMotion.Open || open.Motion == DoorMotion.Opening && open.OpenAmount > OpenPast),
            StateType.DoorIsBroken => Door(world, target.Site) is { DoorBroken: true },
            StateType.IsDoorLocked => Locked(world, target),
            StateType.IsWearingShacklesLocked => Character(world, target.Character) is { } worn
                && LockSystem.WornShackles(worn)?.Lock is { EffectivelyLocked: true },
            StateType.IsTargetDown => CharacterFacts.TargetDown(Character(world, target.Character)),
            StateType.TargetIsImprisoned => Character(world, target.Character) is { InSomething: HeldState.Imprisoned },
            // 47 (FUN_140598d20) is the carrying byte +0x348; 77, 81 and 100 share FUN_14060e5c0, its negation.
            StateType.IsCarryingSomething => !cold.Carrying.IsNone,
            StateType.IsCarriedDudeInBed or StateType.CarriedDudeNowInCage or StateType.CarriedDudeNowInDisposalMachine => cold.Carrying.IsNone,
            StateType.IsCarryingTarget => !cold.Carrying.IsNone && cold.Carrying == target.Character,
            StateType.IsCarriedDudeDead => Character(world, cold.Carrying) is { Medical.Dead: true },
            StateType.AmImprisoned => cold.InSomething == HeldState.Imprisoned,
            StateType.IsInBed => cold.InSomething == HeldState.Bed,
            StateType.IsFullyRested => FullyRested(world, Character(world, target.Character) ?? cold),
            // 80 isCarryingArmOK (1405c7930): the left arm works (medical +0x166). 26 IS_CROUCHING_OR_LYING (FUN_140599e90).
            StateType.IsCarryingArmOk => cold.Medical is not { } carrier || Carrying.CarrySystem.LeftArmUsable(carrier),
            StateType.IsCrouchingOrLying => CharacterFacts.CrouchingOrLying(cold),
            StateType.IsAggressionTowards => Threatened(world, target.Character),
            StateType.IsAggressionTowardsMe => world.SystemOf<CombatSystem>() is { } combat && CharacterFacts.ImmediateThreat(combat, world, slot),
            StateType.MachineHasFreeOperatorSlot => BedSlotFree(world, slot, target.Site),

            // 70 isObjectMine (FUN_1405a3c10) and 28 isNearTo (FUN_1405a3080 through 0x1405a3270, Verified) are false for a hand that resolves to
            // nothing; Meitou evaluates them only then.
            StateType.IsObjectMine or StateType.IsNearTo => Gone(world, target) ? false : null,

            // 145 using any turret (FUN_140997d60, Verified by disassembly): the character's turret hand +0x258 names something (type +0x260 not 0xb),
            // which the turret's operator slot 150 (FUN_140434d20) writes on every update of the manning.
            StateType.UsingAnyTurret => cold.Gunning is not null,
            _ => null,
        };
    }

    /// <summary>
    /// 73 machineHasFreeOperatorSlot (<c>FUN_140598de0</c> → <c>UseableStuff</c> slot 158 <c>FUN_1400f7bf0</c>, <b>Verified</b>) of a bed target:
    /// true for a character among its users, else while its users are fewer than its places (<c>max operators</c>; Meitou reads the users as the bed's
    /// <see cref="Fixture.Occupants"/>).
    /// The hand asked is read as the character's own (AI <c>+0x300</c> through <c>FUN_1405c6ae0</c>, <b>Observed</b>). False for a site that
    /// resolves to nothing (<see cref="SiteGone"/>: <c>FUN_140598de0</c> finds no object); null for another target that is no bed (a machine's or a
    /// cage's slots are their systems' own tests).
    /// </summary>
    static bool? BedSlotFree(World world, int slot, string? site)
    {
        if (SiteGone(world, site)) return false;
        if (site is null || world.Fixtures.Find(site) is not { Kind: FixtureKind.Bed } bed) return null;

        return FixtureRegistry.IsFree(world.Characters, bed) || bed.Occupants.Contains(world.Characters.IdOf(slot));
    }

    /// <summary>
    /// 1 isAtLocation of a point target (<c>FUN_1405a2450</c>, <b>Verified</b>): false at the zero point; else the mover is done
    /// (<c>CharMovement</c> slot 12 <c>FUN_14065edb0</c>: no path, or a finished one and not moving; Meitou: no path followed, asked for or on its
    /// way) or the character stands within flat squared distance 144 of the point and less than 20 above or below it.
    /// </summary>
    static bool AtPoint(World world, int slot, Vector3 point)
    {
        if (point == Vector3.Zero) return false;

        ref readonly var n = ref world.Characters.Next[slot];
        const MoveFlags walking = MoveFlags.HasPath | MoveFlags.NeedPath | MoveFlags.Pending;
        if (((MoveFlags)n.Flags & walking) == 0) return true;

        float dx = point.X - n.Position.X, dz = point.Z - n.Position.Z;
        return dx * dx + dz * dz < AtFlat2 && MathF.Abs(point.Y - n.Position.Y) < AtHeight;
    }

    /// <summary>
    /// 3 isSubjectInInventory (<c>FUN_14059ce30</c>, <b>Verified</b>) of an item target: in the character's inventory or the item it holds, or
    /// resolving to nothing. An item still on the ground is not; one topped up into a stack (no units left) is gone; else the item itself must be in
    /// this character's inventory or mouth, so one another character took is not.
    /// </summary>
    static bool SubjectInInventory(World world, CharacterCold cold, Items.GroundPick pick)
    {
        if (world.GroundItems.Find(pick.Id) is not null) return false;
        if (pick.Item.Quantity <= 0) return true;

        return ReferenceEquals(cold.Mouth, pick.Item) || cold.Inventory is { } inventory && inventory.All().Contains(pick.Item);
    }

    /// <summary>
    /// 2 isBuildingComplete (<c>FUN_140596b20</c>, <b>Verified</b>): an object target that is not destroyed (slot 97: 0 for a building, a door's
    /// broken byte +0x3b6) and whose complete byte (slot 69, +0x160) is set: a player building's <see cref="Construction.BuildSite.Complete"/>, a town
    /// building's machine site (complete, <see cref="Production.ProductionSystem.TownWorkSite"/>), a registered town seat or training dummy, a
    /// natural rock, or a door not broken.
    /// </summary>
    static bool BuildingComplete(World world, string? site)
    {
        if (site is null) return false;
        if (world.Buildings.Find(site) is { } building) return building.Complete;
        if (world.SystemOf<Production.ProductionSystem>()?.TownWorkSite(site) is { } town) return town.Complete;
        if (world.Fixtures.FindSeat(site) is not null || world.Fixtures.Find(site) is { Kind: FixtureKind.Training }
            || Production.ProductionSystem.IsNatural(site))
            return true;

        return Door(world, site) is { DoorBroken: false };
    }

    /// <summary>
    /// 71 isBuildingDamaged (<c>FUN_140598d60</c> → slot 101 <c>FUN_1400f6b70</c>, <b>Verified</b>): a complete building whose progress is below its
    /// target (docs/game/buildings-production.md "Repair").
    /// </summary>
    static bool BuildingDamaged(World world, string? site) =>
        site is not null && world.Buildings.Find(site) is { Complete: true } building && building.Progress < building.Target;

    /// <summary>
    /// 93 BUILDING_HAS_POWER (<c>FUN_140596ab0</c>, <b>Verified</b>): false without an object target; a machine's power by its grid
    /// (<see cref="Production.ProductionSystem.MachinePowered"/>; one drawing nothing passes); true for a building that is no machine.
    /// </summary>
    static bool HasPower(World world, string? site)
    {
        if (site is null) return false;
        if (world.Machines.Find(site) is not { } machine) return true;

        return world.SystemOf<Production.ProductionSystem>() is not { } production || production.MachinePowered(world, machine);
    }

    /// <summary>
    /// 54 isDoorLocked (<c>FUN_1405a00c0</c>, <b>Verified</b>) for an object target: its lock (slot 0x400) locked, neither it nor its owner broken
    /// (<c>FUN_140297d60</c>) and of level 1 or more (<see cref="LockState.EffectivelyLocked"/>); a broken door counts as unlocked. False for none.
    /// </summary>
    static bool Locked(World world, in RowTarget target)
    {
        if (target.Site is null || world.Fixtures.Find(target.Site) is not { } fixture) return false;

        return fixture.Lock is { EffectivelyLocked: true } && !fixture.DoorBroken;
    }

    /// <summary>
    /// 72 isFullyRested (<c>FUN_1405a0610</c>, <b>Verified</b>), of a character target, else of the character itself: false off the player's faction
    /// (faction <c>+0x250</c>); for the player's, its medical <c>+0xb0</c> (<see cref="Bodies.MedicalState.RestedLevel"/>) above 0.9
    /// (<c>FUN_140643cd0</c>, the float at <c>0x14168a25c</c>). The planner asks the same.
    /// </summary>
    public static bool FullyRested(World world, CharacterCold c) =>
        DestinationAccess.PlayerSide(world, c) && c is { Medical: { } medical, Race: { } race } && medical.RestedLevel(race) > RestedPast;

    /// <summary>
    /// 16 isAggressionTowards (<c>FUN_14059de10</c>, <b>Observed</b>) of a character target: its immediate-threat flag (+0x2b0, a melee attacker within
    /// 40); false when it does not resolve.
    /// </summary>
    static bool Threatened(World world, CharacterId who) =>
        world.Characters.TryResolveNext(who, out int at) && world.SystemOf<CombatSystem>() is { } combat
        && CharacterFacts.ImmediateThreat(combat, world, at);

    /// <summary>
    /// Whether the target names a character or a site that resolves to nothing now (the original's hand kept with an order is looked up each time it
    /// is read, <c>FUN_140284510</c>): a character no longer in the world, or <see cref="SiteGone"/>.
    /// </summary>
    public static bool Gone(World world, in RowTarget target) =>
        !target.Character.IsNone && !world.Characters.TryResolveNext(target.Character, out _) || SiteGone(world, target.Site);

    /// <summary>
    /// Whether <paramref name="site"/> names a site the world does not have (a build site, a fixture, a ground bag, a town seat, a rock, a town dummy or
    /// turret); false for none.
    /// </summary>
    public static bool SiteGone(World world, string? site) =>
        site is not null && world.Buildings.Find(site) is null && world.Fixtures.Find(site) is null
        && Items.GroundBags.Lying(world, site) is null
        && !Production.ProductionSystem.IsNatural(site) && !FixtureRegistry.IsDummy(site) && !FixtureRegistry.IsSeat(site)
        && !Production.ProductionSystem.IsTownWork(site);

    /// <summary>
    /// The door fixture of id <paramref name="site"/>; null for none or another fixture.
    /// </summary>
    static Fixture? Door(World world, string? site) =>
        site is not null && world.Fixtures.Find(site) is { Kind: FixtureKind.Door } door ? door : null;

    /// <summary>
    /// The character a hand names; null when it does not resolve.
    /// </summary>
    static CharacterCold? Character(World world, CharacterId who) =>
        !who.IsNone && world.Characters.TryResolveNext(who, out int at) ? world.Characters.Cold(at) : null;

    #endregion
}
