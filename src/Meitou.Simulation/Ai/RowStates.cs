using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The target of an order or job row whose lists are tested (<see cref="RowStates"/>): the character it names (<see cref="CharacterId.None"/>: none)
/// and the site, a fixture or building id (null: none).
/// </summary>
public readonly record struct RowTarget(CharacterId Character, string? Site);

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
            // 123 and 136 share the thunk 0x14002fb1c, which returns false.
            StateType.IsWorking or StateType.IsWorkingPretend => false,
            StateType.IsBuildingComplete => BuildingComplete(world, target.Site),
            StateType.IsBuildingDamaged => BuildingDamaged(world, target.Site),
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
            StateType.IsAggressionTowards => Threatened(world, target.Character),
            StateType.IsAggressionTowardsMe => world.SystemOf<CombatSystem>() is { } combat && CharacterFacts.ImmediateThreat(combat, world, slot),
            _ => null,
        };
    }

    /// <summary>
    /// 2 isBuildingComplete (<c>FUN_140596b20</c>, <b>Verified</b>): an object target that is not destroyed (slot 97: 0 for a building, a door's
    /// broken byte +0x3b6) and whose complete byte (slot 69, +0x160) is set: a player building's <see cref="Construction.BuildSite.Complete"/>, a door
    /// not broken.
    /// </summary>
    static bool BuildingComplete(World world, string? site)
    {
        if (site is null) return false;
        if (world.Buildings.Find(site) is { } building) return building.Complete;

        return Door(world, site) is { DoorBroken: false };
    }

    /// <summary>
    /// 71 isBuildingDamaged (<c>FUN_140598d60</c> → slot 101 <c>FUN_1400f6b70</c>, <b>Verified</b>): a complete building whose progress is below its
    /// target (docs/game/buildings-production.md "Repair").
    /// </summary>
    static bool BuildingDamaged(World world, string? site) =>
        site is not null && world.Buildings.Find(site) is { Complete: true } building && building.Progress < building.Target;

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
    /// 16 isAggressionTowards (<c>FUN_14059de10</c>, <b>Observed</b>) of a character target: its immediate-threat flag (+0x2b0, a melee attacker within
    /// 40); false when it does not resolve.
    /// </summary>
    static bool Threatened(World world, CharacterId who) =>
        world.Characters.TryResolveNext(who, out int at) && world.SystemOf<CombatSystem>() is { } combat
        && CharacterFacts.ImmediateThreat(combat, world, at);

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
