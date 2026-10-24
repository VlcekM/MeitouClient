using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Locks;

/// <summary>Pick the lock of <see cref="Prisoner"/>'s cage (the original's Task_PickLock with a character target): the characters (the selection when empty) walk to it, or a caged one picks its own.</summary>
public sealed record PickLockOrder(IReadOnlyList<CharacterId> Characters, CharacterId Prisoner) : SimCommand
{
    /// <summary>The cage to pick instead, by fixture id, when it is empty (then <see cref="Prisoner"/> is None).</summary>
    public string? Cage { get; init; }
}

/// <summary>The characters (the selection when empty) leave their cage (Task_GetOutOfCage): refused while it is locked; an escape unless the sentence is served.</summary>
public sealed record GetOutOrder(IReadOnlyList<CharacterId> Characters) : SimCommand;

/// <summary>
/// The player's lock orders (docs/simulation.md "Arrest and jail"): picking a cage's lock (<see cref="Lockpicking.Step"/> once a tick while in reach, with its pending crime, rolls and
/// XP) and getting out of a cage (<see cref="FixtureRegistry.GetOut"/>). Serial phases only; the picker's fixture is <see cref="CharacterCold.PickingLock"/>. The NPCs' rounds (releasing
/// served prisoners, locking up at night, keeping cages locked) are in LockSystem.Rounds.cs.
/// </summary>
public sealed partial class LockSystem(GameConstants constants, BodyOptions options) : ITickSystem
{
    /// <summary>A picker outside the cage works on it within the cage's <c>use range</c> plus this.</summary>
    public const float Reach = 15;

    public Lockpicking Picking { get; } = new(constants, options);
    public Crime.CrimeSystem? Crimes { get; set; }

    static IReadOnlyList<CharacterId> Who(World world, IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? ids : world.Player.Selection.ToArray();

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        foreach (var command in commands)
        {
            switch (command)
            {
                case BuyBuilding buy:
                    world.Fixtures.Buy(world, buy);
                    break;
                case PickLockOrder pick:
                {
                    Fixture? cage;
                    if (pick.Cage is not null) cage = world.Fixtures.Find(pick.Cage) is { Kind: FixtureKind.Cage } empty ? empty : null;
                    else cage = table.TryResolveNext(pick.Prisoner, out int p) && table.Cold(p) is { IsImprisoned: true, InWhat: { } where } ? world.Fixtures.Find(where) : null;
                    if (cage is null) break;
                    foreach (var id in Who(world, pick.Characters))
                    {
                        if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false } c || !c.CarriedBy.IsNone) continue;
                        if (c.IsImprisoned && c.InWhat != cage.Id) continue;   // a prisoner reaches only its own cage
                        c.PickingLock = cage.Id;
                        c.Shackles = null;
                        if (!c.IsImprisoned) MovementSystem.GoTo(table, s, new Vector2(cage.Position.X, cage.Position.Z), c.RunSpeed);
                        break;   // one picker per order (the nearest is not looked for: the first listed)
                    }
                    break;
                }
                case GetOutOrder getOut:
                    foreach (var id in Who(world, getOut.Characters))
                    {
                        if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsImprisoned: true, Medical.Incapacitated: false } c) continue;
                        if (world.Fixtures.GetOut(world, id, Crimes)) { c.PickingLock = null; if (c.IsPlayer) world.Post($"{c.Name} got out of the cage"); }
                        else if (c.IsPlayer) world.Post("It's locked.");
                    }
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
                case SetFreeOrder free:
                    SetFreeInput(world, free);
                    break;
                case GetInCageOrder getIn:
                    GetInInput(world, getIn);
                    break;
                case ForceLockOrder force:
                    ForceInput(world, force);
                    break;
            }
        }
    }

    public void SlowWorld(World world)
    {
        Doors(world);
        Rounds(world);
        ShackleWorkers(world);
        Bashers(world);
        ForcedLocks(world);
        CageWorkers(world);
        var table = world.Characters;
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { PickingLock: { } id } c) continue;
            if (world.Fixtures.Find(id) is not { } f || c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone) { c.PickingLock = null; continue; }
            if (!c.IsImprisoned)
            {
                var (at, reach) = WorkSpot(world, f, new Vector2(next[s].Position.X, next[s].Position.Z));
                float d = Vector2.Distance(new Vector2(next[s].Position.X, next[s].Position.Z), at);
                if (d > reach)
                {
                    // Still walking; another order (a new goal) or a failed path ends it.
                    if (next[s].Task != (byte)CharacterTask.GoTo || Vector2.Distance(next[s].Goal, at) > (f.Kind == FixtureKind.Door ? reach : 1)) c.PickingLock = null;
                    continue;
                }
                if (next[s].Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, s);
            }
            switch (Picking.Step(world, s, f, world.TickSeconds, Crimes))
            {
                case PickOutcome.Open: c.PickingLock = null; world.EmitSound(SoundKind.Unlocked, s, next[s].Position); break;
                case PickOutcome.Success: c.PickingLock = null; world.Fixtures.Touch(f); world.EmitSound(SoundKind.Unlocked, s, next[s].Position); if (c.IsPlayer) world.Post($"{c.Name}: Pick success!");
                    if (f.Occupant is { IsNone: false } held && held != table.IdOf(s)) world.Defer(new Dialogue.DialogueEvent(held, table.IdOf(s), UnlockMyCageOrShackles) { Tick = world.Tick + 1 });   // Task_PickLock: the freed character to the picker
                    break;
                case PickOutcome.Failed: if (c.IsPlayer) world.Post($"{c.Name}: Pick failed"); break;
                case PickOutcome.NotAllowed: c.PickingLock = null; if (c.IsPlayer) world.Post($"{c.Name}: I'm not skilled enough..."); break;
            }
        }
    }
}
