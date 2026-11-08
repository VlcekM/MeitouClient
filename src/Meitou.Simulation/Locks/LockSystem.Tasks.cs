using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The lock system's task starts (docs/simulation.md "Task starts"): each order (and a warden's round) starts through <see cref="TaskStart.Start"/> with its row, which the lock system
/// handles whatever the walk that acts it out: pick lock 76, unlock / cut / break shackles 201 / 185 / 186 (putting shackles on, an engine stand-in for the original's inventory drag,
/// has no row: <see cref="TaskType.None"/>), set free 110, get in 107, open / close door 72 / 73, lock / unlock door 77 / 140, cut / break lock 285 / 286, smash 226; a round runs
/// release 110, lock 77 or unlock 78.
/// </summary>
public sealed partial class LockSystem : ITaskOwner, ITaskHandler, IOrderOwner
{
    static readonly TaskType[] Rows =
    [
        TaskType.PICK_LOCK, TaskType.PICK_LOCK_ON_SHACKLES, TaskType.CUT_SHACKLES, TaskType.BRUTE_FORCE_SHACKLES, TaskType.None, TaskType.RELEASE_PRISONER, TaskType.USE_CAGE,
        TaskType.OPEN_DOOR, TaskType.CLOSE_DOOR, TaskType.LOCK_DOOR, TaskType.UNLOCK_DOOR, TaskType.UNLOCK_DOOR_PLAYER_ORDER, TaskType.CUT_LOCK, TaskType.BRUTE_FORCE_LOCK,
        TaskType.BASH_DOOR_PLAYER_ORDER,
    ];

    public void RegisterTasks(TaskHandlers tasks)
    {
        foreach (var row in Rows) tasks.Register(row, this);
    }

    /// <summary>Whether the row is a lock order's (<see cref="World.CanOrder(TaskType, CharacterId, CharacterId, string?, System.Numerics.Vector3)"/>).</summary>
    internal static bool OwnsRow(TaskType row) => row != TaskType.None && Array.IndexOf(Rows, row) >= 0;

    /// <summary>The detail a lock order carries when a walk to its work starts it (<see cref="World.CanOrder(TaskType, CharacterId, CharacterId, string?, System.Numerics.Vector3)"/>).</summary>
    internal object WorkDetail() => new LockWork(CharacterTask.GoTo, null);

    /// <summary>The row of a shackle job.</summary>
    static TaskType RowOf(ShackleWork work) => work switch
    {
        ShackleWork.Pick => TaskType.PICK_LOCK_ON_SHACKLES,
        ShackleWork.Cut => TaskType.CUT_SHACKLES,
        ShackleWork.Break => TaskType.BRUTE_FORCE_SHACKLES,
        _ => TaskType.None,
    };

    /// <summary>The row of a lock forced by <paramref name="force"/>.</summary>
    static TaskType RowOf(LockForce force) => force switch
    {
        LockForce.Tools => TaskType.CUT_LOCK,
        LockForce.Strength => TaskType.BRUTE_FORCE_LOCK,
        _ => TaskType.BASH_DOOR_PLAYER_ORDER,
    };

    /// <summary>
    /// Gives the character a lock order's work (<see cref="PlayerOrders.Give"/>), which starts through <see cref="TaskStart.Start"/> as the act-out <paramref name="task"/>; false (and nothing given)
    /// when it cannot run (the row is not the character's): the order's picker then tries the next character.
    /// </summary>
    bool StartWork(World world, int slot, CharacterTask task, TaskType row, string? site, CharacterId? target = null, object? detail = null, bool queued = false)
    {
        var order = new OrderEntry(row, this) { Site = site, Target = target ?? CharacterId.None, Detail = new LockWork(task, detail) };
        if (!PlayerOrders.CanRun(world, slot, order).CanRun) return false;
        PlayerOrders.Give(world, slot, order, queued);
        return true;
    }

    /// <summary>
    /// Lock work done where the character stands (<see cref="CharacterTask.Idle"/>: a prisoner on its own cage or shackles, a worker at its lock) is done once the character holds no work; a walk
    /// once it ended.
    /// </summary>
    bool ITaskHandler.Done(World world, int slot, CharacterTask task)
    {
        var table = world.Characters;
        if (table.Next[slot].Task != (byte)task) return true;
        if (task != CharacterTask.Idle) return false;
        var c = table.Cold(slot)!;
        return c is { PickingLock: null, Shackles: null, ForcingLock: null, DoorJob: null, Freeing.IsNone: true, EnteringCage: null, BashOrdered: false }
            && !(world.Player.DoorTarget is not null && world.Player.DoorWalker == table.IdOf(slot));
    }

    /// <summary>
    /// A lock order's worker reached its work (serial): it stands and works where it stands, so its order's provenance now runs <see cref="CharacterTask.Idle"/>. The next start then ends the
    /// work through this handler (a move order given at the cage is no longer stopped by the work's own walk check), and the order queue sees it run until it is <see cref="ITaskHandler.Done"/>.
    /// </summary>
    static void AtWork(World world, int slot)
    {
        TaskStart.Stand(world.Characters, slot);
        var c = world.Characters.Cold(slot)!;
        if (c.Running is { Source: TaskSource.Order, Task: CharacterTask.GoTo } run && Array.IndexOf(Rows, run.Row) >= 0) c.Running = run with { Task = CharacterTask.Idle };
    }

    /// <summary>A lock order's act-out and the work's own parameters (<see cref="TaskRequest.Detail"/>).</summary>
    sealed record LockWork(CharacterTask Task, object? Detail);

    // Saved: 1 a walk to the work, 2 in place (kind 0, the original's, is a walk). The work's parameter is the row's own: a shackle job and a forced lock are told by their rows (RowOf).
    int IOrderOwner.SaveKind(OrderEntry order) =>
        order.Detail is LockWork { Task: CharacterTask.GoTo or CharacterTask.Idle, Detail: null or ShackleWork or LockForce } work ? work.Task == CharacterTask.Idle ? 2 : 1 : -1;

    object? IOrderOwner.LoadDetail(World world, CharacterId self, TaskType row, int kind, CharacterId target) =>
        new LockWork(kind == 2 ? CharacterTask.Idle : CharacterTask.GoTo, row switch
        {
            TaskType.PICK_LOCK_ON_SHACKLES => ShackleWork.Pick,
            TaskType.CUT_SHACKLES => ShackleWork.Cut,
            TaskType.BRUTE_FORCE_SHACKLES => ShackleWork.Break,
            TaskType.CUT_LOCK => LockForce.Tools,
            TaskType.BRUTE_FORCE_LOCK => LockForce.Strength,
            TaskType.BASH_DOOR_PLAYER_ORDER => LockForce.Smash,
            _ => null,
        });

    /// <summary>
    /// A lock order (the pickers choose who gets it from the conditions of the moment): a player character's (else it leaves the queue) that is awake, not carried and free, or caged and
    /// working on its own cage or shackles where it stands; its fixture or character still there. What else the work needs is tested where it is done (the worker gives up there).
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var c = table.Cold(slot)!;
        if (!c.IsPlayer || order.Site is { } site && world.Fixtures.Find(site) is null || !order.Target.IsNone && !table.TryResolveNext(order.Target, out _)) return OrderCheck.Gone;
        bool inPlace = ((LockWork)order.Detail!).Task == CharacterTask.Idle;
        return c.Medical is not { Incapacitated: true } && c.CarriedBy.IsNone && (inPlace || !c.IsImprisoned) ? OrderCheck.Run : OrderCheck.Blocked();
    }

    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var work = (LockWork)order.Detail!;
        TaskStart.Start(world, slot, new TaskRequest(work.Task, order.Row, TaskSource.Order) { Site = order.Site, Target = order.Target, Detail = work.Detail });
    }

    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        var table = world.Characters;
        var c = table.Cold(slot)!;
        if (request.Detail is Round round)
        {
            StartRound(world, slot, round, world.Fixtures.Find(request.Site!)!);
            return;
        }
        switch (request.Row)
        {
            case TaskType.PICK_LOCK or TaskType.OPEN_DOOR or TaskType.CLOSE_DOOR when world.Fixtures.Find(request.Site!) is { Kind: FixtureKind.Door } door:
                StartDoor(world, slot, door, request.Row == TaskType.PICK_LOCK);
                break;
            case TaskType.PICK_LOCK when world.Fixtures.Find(request.Site!) is { Kind: FixtureKind.Cage } cage:
                c.PickingLock = cage.Id;
                c.Shackles = null;
                c.DoorJob = null;
                if (!c.IsImprisoned) MovementSystem.GoTo(table, slot, new Vector2(cage.Position.X, cage.Position.Z), c.RunSpeed);
                break;
            case TaskType.PICK_LOCK:
                // Another faction's locked container, with Alt (the original's FUN_1407fc6e0).
                var container = world.Fixtures.Find(request.Site!)!;
                c.Shackles = null;
                c.PickingLock = container.Id;
                MovementSystem.GoToFloor(world, slot, container.Position, c.RunSpeed);
                break;
            case TaskType.PICK_LOCK_ON_SHACKLES or TaskType.CUT_SHACKLES or TaskType.BRUTE_FORCE_SHACKLES or TaskType.None:
                c.PickingLock = null;
                c.Shackles = new ShackleJob(request.Target, (ShackleWork)request.Detail!);
                if (request.Target != table.IdOf(slot) && table.TryResolveNext(request.Target, out int w)) MovementSystem.GoTo(table, slot, Flat(table.Next[w].Position), c.RunSpeed);
                break;
            case TaskType.RELEASE_PRISONER:
                ClearWork(c);
                c.Freeing = request.Target;
                MovementSystem.GoToFloor(world, slot, world.Fixtures.Find(request.Site!)!.Position, c.RunSpeed);
                break;
            case TaskType.USE_CAGE:
                ClearWork(c);
                c.EnteringCage = request.Site;
                MovementSystem.GoToFloor(world, slot, world.Fixtures.Find(request.Site!)!.Position, c.RunSpeed);
                break;
            case TaskType.LOCK_DOOR or TaskType.UNLOCK_DOOR_PLAYER_ORDER:
                StartDoorLock(world, slot, world.Fixtures.Find(request.Site!)!, request.Row == TaskType.LOCK_DOOR);
                break;
            case TaskType.CUT_LOCK or TaskType.BRUTE_FORCE_LOCK or TaskType.BASH_DOOR_PLAYER_ORDER:
                StartForce(world, slot, world.Fixtures.Find(request.Site!)!, (LockForce)request.Detail!);
                break;
        }
    }

    /// <summary>
    /// Lock work another task replaces or the Stop order ends lets go of its lock, shackles, door, prisoner or cage (a warden's round ends by itself once its worker is on another task).
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var table = world.Characters;
        var c = table.Cold(slot)!;
        if (c.Running is { Source: TaskSource.Npc }) return;
        ClearWork(c);
        if (c.BashOrdered) (c.Bashing, c.BashOrdered) = (null, false);
        if (world.Player.DoorWalker == table.IdOf(slot)) world.Player.DoorTarget = null;
    }

    /// <summary>The walk to a door: opened or closed when reached (72 / 73), or its lock picked (76); another faction's locked door without Alt is only walked to.</summary>
    static void StartDoor(World world, int slot, Fixture door, bool pick)
    {
        var table = world.Characters;
        var walker = table.Cold(slot)!;
        var player = world.Player;
        var (at, _) = WorkSpot(world, door, Flat(table.Next[slot].Position));
        walker.Shackles = null;
        walker.PickingLock = null;
        walker.DoorJob = null;
        player.DoorTarget = null;
        if (door.Lock is { EffectivelyLocked: true } && door.Motion == DoorMotion.Closed && door.Owner != world.Player.Faction)
        {
            if (pick) walker.PickingLock = door.Id;
        }
        else (player.DoorTarget, player.DoorWalker) = (door.Id, table.IdOf(slot));
        MovementSystem.GoToFloor(world, slot, new Vector3(at.X, door.Position.Y, at.Y), walker.RunSpeed);
    }

    /// <summary>The walk to one of the player's doors to lock (77) or unlock (140) it.</summary>
    static void StartDoorLock(World world, int slot, Fixture door, bool locking)
    {
        var table = world.Characters;
        var walker = table.Cold(slot)!;
        var (at, _) = WorkSpot(world, door, Flat(table.Next[slot].Position));
        ClearWork(walker);
        walker.Bashing = null;
        walker.BashOrdered = false;
        if (world.Player.DoorWalker == table.IdOf(slot)) world.Player.DoorTarget = null;
        walker.DoorJob = new DoorLockJob(door.Id, locking);
        MovementSystem.GoToFloor(world, slot, new Vector3(at.X, door.Position.Y, at.Y), walker.RunSpeed);
    }

    /// <summary>The walk to a lock to cut (285) or break (286) it, or to a door to smash (226).</summary>
    static void StartForce(World world, int slot, Fixture fixture, LockForce force)
    {
        var table = world.Characters;
        var walker = table.Cold(slot)!;
        var at = force == LockForce.Smash ? StandSpot(fixture, world.Fixtures.DoorSpotOf(fixture)!.Value) : WorkSpot(world, fixture, Flat(table.Next[slot].Position)).At;
        walker.PickingLock = null;
        walker.Shackles = null;
        walker.ForcingLock = null;
        walker.DoorJob = null;
        walker.Bashing = null;
        walker.BashOrdered = false;
        if (world.Player.DoorWalker == table.IdOf(slot)) world.Player.DoorTarget = null;
        if (force == LockForce.Smash) (walker.Bashing, walker.BashOrdered, walker.BashTime) = (fixture.Id, true, 0);
        else (walker.ForcingLock, walker.Force) = (fixture.Id, force);
        MovementSystem.GoToFloor(world, slot, new Vector3(at.X, fixture.Position.Y, at.Y), walker.RunSpeed);
    }
}
