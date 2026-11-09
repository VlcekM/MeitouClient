using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Pick the lock of <see cref="Prisoner"/>'s cage (the original's Task_PickLock with a character target): the characters (the selection when empty)
/// walk to it, or a caged one picks its own.
/// </summary>
public sealed record PickLockOrder(IReadOnlyList<CharacterId> Characters, CharacterId Prisoner) : OrderCommand
{
    /// <summary>
    /// The cage to pick instead, by fixture id, when it is empty (then <see cref="Prisoner"/> is None).
    /// </summary>
    public string? Cage { get; init; }
}

/// <summary>
/// The characters (the selection when empty) leave their cage (Task_GetOutOfCage): refused while it is locked; an escape unless it is a kidnap
/// victim.
/// </summary>
public sealed record GetOutOrder(IReadOnlyList<CharacterId> Characters) : OrderCommand;

/// <summary>
/// The player's lock orders (docs/simulation.md "Arrest and jail"): picking a cage's lock (<see cref="Lockpicking.Step"/> once a tick while in reach,
/// with its pending crime, rolls and XP) and getting out of a cage (<see cref="FixtureRegistry.GetOut"/>). Serial phases only; the picker's fixture
/// is <see cref="CharacterCold.PickingLock"/>. The NPCs' rounds (releasing served prisoners, locking up at night, keeping cages locked) are in
/// LockSystem.Rounds.cs.
/// </summary>
public sealed partial class LockSystem(GameConstants constants, BodyOptions options) : ITickSystem
{
    /// <summary>
    /// A picker outside the cage works on it within the cage's <c>use range</c> plus this.
    /// </summary>
    public const float Reach = 15;

    /// <summary>
    /// The lock-picking rolls and XP.
    /// </summary>
    public Lockpicking Picking { get; } = new(constants, options);
    /// <summary>
    /// The crime system that hears of break-ins (null: none).
    /// </summary>
    public Crime.CrimeSystem? Crimes { get; set; }

    /// <summary>
    /// The characters an order names, else the player's selection.
    /// </summary>
    static IReadOnlyList<CharacterId> Who(World world, IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? ids : world.Player.Selection.ToArray();

    /// <summary>
    /// The lock orders: buying a house, picking and forcing locks, cages, shackles, doors and the gate regions.
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case BuyBuilding buy:
                    world.Fixtures.Buy(world, buy);
                    break;
                case PickLockOrder pick:
                    PickLockInput(world, pick);
                    break;
                case GetOutOrder getOut:
                    GetOutInput(world, getOut);
                    break;
                case ShackleOrder shackles:
                    ShackleInput(world, shackles);
                    break;
                case PutShacklesOrder putOn:
                    PutShacklesInput(world, putOn);
                    break;
                case ToggleDoorsOrder or ToggleDoorLocksOrder or Construction.SetBuildingPublic:
                    DoorInputs(world, command);
                    break;
                case DoorOrder door:
                    DoorOrderInput(world, door);
                    break;
                case DoorLockOrder doorLock:
                    DoorLockInput(world, doorLock);
                    break;
                case SetFreeOrder free:
                    SetFreeInput(world, free);
                    break;
                case GetInCageOrder getIn:
                    GetInInput(world, getIn);
                    break;
                case ForceLockOrder force:
                    ForceInput(world, force);
                    break;
                case GateRegionsCommand regions:
                    GateInputs(regions);
                    break;
            }
        }
    }

    /// <summary>
    /// A pick-lock order: the first character listed that can reach the cage (its own, for a prisoner) picks its lock.
    /// </summary>
    void PickLockInput(World world, PickLockOrder pick)
    {
        var table = world.Characters;
        Fixture? cage;
        if (pick.Cage is not null)
        {
            cage = world.Fixtures.Find(pick.Cage) is { Kind: FixtureKind.Cage } empty ? empty : null;
        }
        else
        {
            cage = table.TryResolveNext(pick.Prisoner, out int p) && table.Cold(p) is { IsImprisoned: true, InWhat: { } where }
                ? world.Fixtures.Find(where)
                : null;
        }

        if (cage is null) return;
        foreach (var id in Who(world, pick.Characters))
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false } c
                    || !c.CarriedBy.IsNone)
                continue;

            if (c.IsImprisoned && c.InWhat != cage.Id) continue;   // a prisoner reaches only its own cage

            // One picker per order (the nearest is not looked for: the first listed whose repertoire has row 76); a prisoner picks
            // where it stands.
            if (StartWork(world, s, c.IsImprisoned ? CharacterTask.Idle : CharacterTask.GoTo, TaskType.PickLock, cage.Id,
                    queued: pick.Queued))
                break;
        }
    }

    /// <summary>
    /// A get-out order: each prisoner listed leaves its cage when it is unlocked.
    /// </summary>
    void GetOutInput(World world, GetOutOrder getOut)
    {
        var table = world.Characters;
        foreach (var id in Who(world, getOut.Characters))
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsImprisoned: true, Medical.Incapacitated: false } c)
                continue;

            if (world.Fixtures.GetOut(world, id, Crimes))
            {
                c.PickingLock = null;
                if (c.IsPlayer) world.Post(Loc.Tr("{1} got out of the cage", c.Name));
            }
            else if (c.IsPlayer) world.Post("It's locked.");
        }
    }

    /// <summary>
    /// The play speed of the picking clip: the lerp 0.55 to 2.0 over 0.01 x skill (<c>FUN_140015b63</c>, Verified literals).
    /// </summary>
    internal static float PickPlayRate(float skill) => 0.55f + (2.0f - 0.55f) * (skill * 0.01f);

    /// <summary>
    /// The lock step: caged characters held, the doors and rounds, then the shackle, bash, forcing, door-lock and cage work and the escapes.
    /// </summary>
    public void SlowWorld(World world)
    {
        world.Fixtures.HoldCaged(world);
        Doors(world);
        Rounds(world);
        ShackleWorkers(world);
        Bashers(world);
        ForcedLocks(world);
        DoorLockJobs(world);
        CageWorkers(world);
        OpportunisticEscapes(world);
        ShackleEscapes(world);
        var table = world.Characters;
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { PickingLock: { } id } c) continue;
            if (world.Fixtures.Find(id) is not { } f || c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone)
            {
                c.PickingLock = null;
                continue;
            }

            if (!c.IsImprisoned)
            {
                var (at, reach) = WorkSpot(world, f, new Vector2(next[s].Position.X, next[s].Position.Z));
                float d = Vector2.Distance(new Vector2(next[s].Position.X, next[s].Position.Z), at);
                if (d > reach)
                {
                    // Still walking; another order (a new goal) or a failed path ends it.
                    if (Redirected(next[s], at, reach)) c.PickingLock = null;
                    continue;
                }

                if (next[s].Task == (byte)CharacterTask.GoTo) AtWork(world, s);
            }

            if (c.StaggeredUntil > world.Tick) continue;   // a stagger interrupts the picking
            c.PickWork = (world.Tick, f.Clips.Lockpick, PickPlayRate(Lockpicking.Skill(c)));
            switch (Picking.Step(world, s, f, world.TickSeconds, Crimes))
            {
                case PickOutcome.Open:
                    c.PickingLock = null;
                    world.EmitSound(SoundKind.Unlocked, s, next[s].Position);
                    break;
                case PickOutcome.Success:
                    c.PickingLock = null;
                    world.Fixtures.Touch(f);
                    world.EmitSound(SoundKind.Unlocked, s, next[s].Position);
                    PickFloater(world, s, true);

                    // Task_PickLock: the freed character to the picker.
                    if (f.Occupant is { IsNone: false } held && held != table.IdOf(s))
                        world.Defer(new Dialogue.DialogueEvent(held, table.IdOf(s), UnlockMyCageOrShackles) { Tick = world.Tick + 1 });

                    break;
                case PickOutcome.Failed:
                    PickFloater(world, s, false);

                    // A failed roll: the captive says event 14.
                    if (f.Occupant is { IsNone: false } caged && caged != table.IdOf(s))
                        world.Defer(new Dialogue.DialogueEvent(caged, table.IdOf(s), UnlockMyCageAttempt) { Tick = world.Tick + 1 });

                    break;
                case PickOutcome.NotAllowed:
                    c.PickingLock = null;
                    if (c.IsPlayer) Say(world, c, "I'm not skilled enough...");
                    break;
            }
        }
    }

    /// <summary>
    /// A pick roll's result floating over the picker, any picker, after every roll (Task_PickLock update <c>FUN_140359160</c>, <b>Verified</b>):
    /// "Pick success!" or "Pick failed".
    /// </summary>
    static void PickFloater(World world, int s, bool success)
    {
        var text = Loc.Tr(success ? "Pick success!" : "Pick failed");
        world.Floaters?.Enqueue(Floater.Pick(world.Characters.IdOf(s), world.Characters.Next[s].Position, text, success));
    }

    /// <summary>
    /// Posts a character's own line as "name: line" (a lock worker's "It's locked."). The line is a game msgid looked up on its own, so every
    /// language with a game catalogue shows it translated; the "{1}: {2}" frame is Meitou's.
    /// </summary>
    static void Say(World world, CharacterCold c, string line) => world.Post(Loc.Tr("{1}: {2}", c.Name, Loc.Tr(line)));
}