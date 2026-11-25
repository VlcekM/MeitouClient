using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Opens the closed doors of a player building, or closes its open ones when any is open (the door panel's open/close button, <c>FUN_140546fb0</c>; a
/// locked door is unlocked first).
/// </summary>
public sealed record ToggleDoorsOrder(string Building) : SimCommand;

/// <summary>
/// Flips <c>wants lock</c> on every door of a player building (the door panel's lock button, <c>FUN_140547060</c>): a closed door locks at once, an
/// open one when it closes; unlocking clears the lock.
/// </summary>
public sealed record ToggleDoorLocksOrder(string Building) : SimCommand;

/// <summary>
/// A right click on a door (docs/game/ui-input.md "Building hover and click"): each of the characters (the selection when empty) walks to it
/// and opens a closed one or closes an open one (72 OPEN_DOOR / 73 CLOSE_DOOR; a locked one: "It's locked."). Another faction's locked door:
/// with <see cref="PickLock"/> (Alt) its lock is picked (76 PICK_LOCK), else the hint and a walk to it.
/// </summary>
public sealed record DoorOrder(IReadOnlyList<CharacterId> Characters, string Door, bool PickLock = false) : OrderCommand;

/// <summary>
/// The context menu's Lock (77) or Unlock (140) on one of the player's doors, or Lock (77) / Unlock (78) on one of its cages (<c>FUN_1407a6bc0</c>):
/// the nearest of the characters (the selection when empty) walks to it and works the lock for <see cref="LockSystem.LockWorkSeconds"/>
/// (Task_LockDoor / Task_UnlockDoor, whose update closes the target's door only when it has one, <c>FUN_140337670</c>); not the panel's instant
/// toggle (<see cref="ToggleDoorLocksOrder"/>). A cage's unlock runs row 140, whose conditions are 78's.
/// </summary>
public sealed record DoorLockOrder(IReadOnlyList<CharacterId> Characters, string Door, bool Lock) : OrderCommand;

/// <summary>
/// A character's running <see cref="DoorLockOrder"/>: the door, whether it is being locked, and the game seconds worked at it.
/// </summary>
public sealed class DoorLockJob(string door, bool locking)
{
    /// <summary>
    /// The door's fixture id.
    /// </summary>
    public string Door { get; } = door;
    /// <summary>
    /// Whether the door is being locked (else unlocked).
    /// </summary>
    public bool Lock { get; } = locking;
    /// <summary>
    /// The game seconds worked at it.
    /// </summary>
    public float Work { get; set; }
}

/// <summary>
/// The doors (docs/simulation.md "Cages, beds and locks"; the original's rules in docs/game/locks.md "Doors"): every tick the opening and closing
/// leaves move (<see cref="FixtureRegistry.StepDoors"/>);
/// every <see cref="WalkerEveryTicks"/> ticks a character whose path runs through a closed door opens it, or stops at a locked one ("It's locked.");
/// the player's finished buildings
/// get their doors registered (<see cref="FixtureRegistry.RegisterPlayerBuilding"/>) and the player's door orders are applied. Serial phases only.
/// <list type="bullet">
/// <item><b>Engine choice</b>: the original's walk needs its destination and location "accessible" (states 59, 60), which Task_OpenDoor (84, 85)
/// makes true by opening the door (the walker faces the door, a locked one fails the task); here the walk itself opens it: a walker within
/// <see cref="OpenReach"/> of a closed door's leaf whose current or next path leg crosses the leaf's plane within its width (plus
/// <see cref="DoorSlack"/>). At a locked one the walker stops, within <see cref="LockedReach"/> of the leaf on its current leg; an NPC whose door
/// it is then unlocks and opens it (<see cref="UnlockOwnWay"/>). Both reaches run from
/// the nearest point of the leaf, not its centre: a gate's leaf is 70 wide and more, and a walker crossing it off centre was never stopped (#560).
/// The walker does not wait for the leaf (the navmesh lets paths through closed unlocked doors).</item>
/// <item>The original closes no door behind a walker (<b>Observed</b>: only Task_CloseDoor, Task_LockDoor and the door panel start a close, and only
/// the lock tasks need a closed door). Meitou does the same: a door a walker opened stays open.</item>
/// </list>
/// </summary>
public sealed partial class LockSystem
{
    /// <summary>
    /// Engine choices of the walk-through opening (see the class remarks).
    /// </summary>
    public const int WalkerEveryTicks = 3;
    /// <summary>
    /// Engine choices of the walk-through opening (see the class remarks): the opening reach, the reach at a locked door, the slack past the leaf's
    /// width, and the leaf's depth and height a walker is tested against.
    /// </summary>
    public const float OpenReach = 40, LockedReach = 15, DoorSlack = 6, DoorDepth = 12, DoorHeight = 40;

    /// <summary>
    /// The building registry revision the player's doors were last registered at (-1: never).
    /// </summary>
    long _buildingsRevision = -1;

    /// <summary>
    /// The door orders: toggling a building's doors, opening, closing, locking and unlocking one.
    /// </summary>
    void DoorInputs(World world, SimCommand command)
    {
        var fixtures = world.Fixtures;
        switch (command)
        {
            case ToggleDoorsOrder toggle:
                {
                    var doors = PlayerDoors(world, toggle.Building);
                    if (doors.Count == 0) break;

                    // Meitou: a door still moving counts by where it is heading and turns back (the original's open and close act only from closed /
                    // open, so a slow gate ignored the button for seconds).
                    bool anyOpen = doors.Any(d => d.Motion is DoorMotion.Open or DoorMotion.Opening);
                    foreach (var d in doors)
                    {
                        if (anyOpen)
                        {
                            if (d.Motion == DoorMotion.Opening) d.Motion = DoorMotion.Closing;
                            else FixtureRegistry.CloseDoor(d);
                            continue;
                        }

                        if (d.Lock is { EffectivelyLocked: true } l)
                        {
                            l.Locked = false;
                            fixtures.Touch(d);
                        }

                        if (d.Motion == DoorMotion.Closing) d.Motion = DoorMotion.Opening;
                        else FixtureRegistry.OpenDoor(d);
                    }

                    break;
                }
            case ToggleDoorLocksOrder lockToggle:
                {
                    var doors = PlayerDoors(world, lockToggle.Building);
                    if (doors.Count == 0) break;
                    bool wants = !doors.All(d => d.WantsLock);
                    foreach (var d in doors)
                    {
                        d.WantsLock = wants;
                        if (d.Lock is not { } l) continue;
                        if (wants && d.Motion == DoorMotion.Closed) l.Locked = true;
                        else if (!wants && l.EffectivelyLocked) l.Locked = false;
                        fixtures.Touch(d);
                    }

                    break;
                }
            case Construction.SetBuildingPublic open when world.Buildings.Find(open.Building) is null or { LevelPlacement: not null }:
                // A town building the player owns (a site's flag is the construction system's).
                if (world.Fixtures.OwnsBuilding(open.Building, world.Player.Faction))
                    fixtures.SetPublic(open.Building, open.Public);

                break;
        }
    }

    /// <summary>
    /// Where a character works a fixture from, and how near counts: a door's side nearer <paramref name="from"/>, out from the leaf; anything else
    /// its own place. Public for code mods that send characters to work a lock (docs/modding.md "Chooser goals and jobs").
    /// </summary>
    public static (Vector2 At, float Reach) WorkSpot(World world, Fixture f, Vector2 from)
    {
        if (f.Kind != FixtureKind.Door || world.Fixtures.DoorSpotOf(f) is not { } spot)
            return (Flat(f.Position), f.UseRange + Reach);

        var outside = StandSpot(f, spot);
        var centre = new Vector2(spot.Centre.X, spot.Centre.Z);
        var inside = 2 * centre - outside;
        return (Vector2.DistanceSquared(from, inside) < Vector2.DistanceSquared(from, outside) ? inside : outside, DoorWorkReach);
    }

    /// <summary>
    /// Whether the character <paramref name="n"/> is at its work on <paramref name="f"/>: within the reach of its <see cref="WorkSpot"/>
    /// (<paramref name="at"/>, <paramref name="reach"/>; while it walks, the side its walk aims at, as a walk up a ramp round a building passes nearer
    /// the door's inner side, #1405), at a cage on its floor and at a door within <see cref="DoorPlan.DoorHeight"/> of the leaf's foot. The rows
    /// reach it through <c>isAtLocation</c>, <c>AI::isWithinChoppingRange</c> (<c>FUN_14059c6d0</c>, <b>Verified</b>: the flat distance and the
    /// same floor, <see cref="FixtureRegistry.WorkHeight"/> for a cage, 130 for a door), so one standing under an upper-storey cage (#1397) or under
    /// a door up a ramp (#1405) is not at it. Containers keep the flat test: a container's walk floor lies up to 17 off its origin
    /// (docs/game/locks.md, #292).
    /// </summary>
    static bool AtWorkSpot(World world, Fixture f, in CharacterHot n, out Vector2 at, out float reach)
    {
        var position = n.Position;
        (at, reach) = WorkSpot(world, f, n.Task == (byte)CharacterTask.GoTo ? n.Goal : Flat(position));
        bool level = f.Kind switch
        {
            FixtureKind.Cage => MathF.Abs(position.Y - f.Position.Y) < FixtureRegistry.WorkHeight,
            FixtureKind.Door => world.Fixtures.DoorSpotOf(f) is not { } spot || MathF.Abs(position.Y - spot.Foot) <= DoorPlan.DoorHeight,
            _ => true,
        };

        return level && Vector2.Distance(Flat(position), at) <= reach;
    }

    /// <summary>
    /// A work spot <paramref name="at"/> of <paramref name="f"/> (<see cref="WorkSpot"/>) at the height its walk aims for: a door's leaf foot, anything
    /// else its own height. A door's position is its host building's origin, which can lie far under the door (a door up a ramp, #1405).
    /// </summary>
    static Vector3 WorkPoint(World world, Fixture f, Vector2 at)
    {
        float y = world.Fixtures.DoorSpotOf(f) is { } spot ? spot.Foot : f.Position.Y;
        return new Vector3(at.X, y, at.Y);
    }

    /// <summary>
    /// The <see cref="OrderEntry.TaskPoint"/> of a door's right click: its own order is made at the door on the terrain
    /// (<see cref="OrderEntry.AtBuilding"/>); the walk to a barred one (no Alt) is a move made at the door's <c>getPositionMarker</c> from the zero
    /// point (<c>FUN_1407fc6e0</c> -> <c>FUN_1407fa850</c>, vfunc <c>0x470</c> <c>FUN_14054b850</c> with <c>ZERO</c>, <b>Verified</b>).
    /// </summary>
    static Vector3 DoorPoint(World world, DoorOrder order, Fixture door, bool barred)
    {
        if (!order.Clicked) return default;
        if (!barred || order.PickLock) return OrderEntry.AtBuilding(world, order, door.Position);

        return world.Fixtures.PositionMarker(door.Id, Vector3.Zero) ?? door.Position;
    }

    /// <summary>
    /// The door order: every player character of it (the selection when empty) gets the door's order through its queue, as the original's giver
    /// <c>FUN_1407f9e20</c> gives it to each selected character, quick click and menu alike (docs/game/ai-tasks.md "Order givers"): another
    /// faction's locked door is picked (76) with Alt, else only walked to with the hint (<c>FUN_1407fa850</c>, a move of every selected one); any
    /// other is opened (72) or, open, closed (73). Whether it can run is the queue's test (its rows' lists), not the giver's.
    /// </summary>
    void DoorOrderInput(World world, DoorOrder order)
    {
        if (world.Fixtures.Find(order.Door) is not { Kind: FixtureKind.Door, DoorBroken: false } door) return;
        var table = world.Characters;
        bool barred = door.Lock is { EffectivelyLocked: true } && door.Motion == DoorMotion.Closed && door.Owner != world.Player.Faction;
        var row = barred && order.PickLock ? TaskType.PickLock : door.Motion is DoorMotion.Open or DoorMotion.Opening ? TaskType.CloseDoor
                : TaskType.OpenDoor;
        var point = DoorPoint(world, order, door, barred);
        bool given = false;
        foreach (var id in Who(world, order.Characters))
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true }) continue;

            GiveWork(world, s, CharacterTask.GoTo, row, door.Id, queued: order.Queued, point: point);
            given = true;
        }

        if (given && barred && !order.PickLock) world.Post("Hold down the ALT key to pick the lock.");
    }

    /// <summary>
    /// The lock-door order: the nearest player character able to locks or unlocks a door or cage the player owns.
    /// </summary>
    void DoorLockInput(World world, DoorLockOrder order)
    {
        if (world.Fixtures.Find(order.Door) is not { Kind: FixtureKind.Door or FixtureKind.Cage, DoorBroken: false, Lock: { } l } door
                || door.Owner != world.Player.Faction
            || l.EffectivelyLocked == order.Lock)
        {
            return;
        }

        var table = world.Characters;
        int best = -1;
        float bestDistance = float.MaxValue;
        var row = order.Lock ? TaskType.LockDoor : TaskType.UnlockDoorPlayerOrder;
        foreach (var id in Who(world, order.Characters))
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { Medical.Incapacitated: false, IsImprisoned: false } c
                    || !c.CarriedBy.IsNone || !TaskStart.Eligible(world, c, row))
                continue;

            float d = Vector2.Distance(Flat(table.Next[s].Position), WorkSpot(world, door, Flat(table.Next[s].Position)).At);
            if (d < bestDistance) (best, bestDistance) = (s, d);
        }

        if (best >= 0) StartWork(world, best, CharacterTask.GoTo, row, door.Id, queued: order.Queued);
    }

    /// <summary>
    /// Each tick: a character in reach of the door it was ordered to lock or unlock works the lock 0.3 per game second
    /// (<see cref="LockWorkSeconds"/>, Task_LockDoor / Task_UnlockDoor update <c>FUN_140337670</c>, <b>Verified</b>); when the work reaches 1 the
    /// lock is set (a closing door first starts closing, <c>FUN_140298c50</c>) or cleared, with the lock or unlock sound. One given another order,
    /// downed, fighting or gone gives up.
    /// </summary>
    static void DoorLockJobs(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { DoorJob: { } job } c) continue;
            ref var n = ref next[s];
            if (world.Fixtures.Find(job.Door) is not { Kind: FixtureKind.Door or FixtureKind.Cage, DoorBroken: false, Lock: { } l } door
                    || job.Lock == l.EffectivelyLocked
                || c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone || c.IsImprisoned || c.InCombat
                        || n.Task is not ((byte)CharacterTask.Idle or (byte)CharacterTask.GoTo))
            {
                c.DoorJob = null;
                continue;
            }

            if (!AtWorkSpot(world, door, n, out var at, out float reach))
            {
                // Still walking; another order (a new goal) or a failed path ends it, and so does being led away from the work.
                if (job.Work > 0 || Redirected(n, at, reach)) c.DoorJob = null;
                continue;
            }

            if (n.Task == (byte)CharacterTask.GoTo)
            {
                // Arrived: stand and start. A move order given since the arrival is a new goal, not this walk.
                if (job.Work > 0)
                {
                    c.DoorJob = null;
                    continue;
                }

                AtWork(world, s);
            }

            if (c.StaggeredUntil > world.Tick) continue;   // a stagger interrupts the work
            job.Work += world.TickSeconds;
            StampLockWork(world, c, job.Work, job.Lock);
            if (job.Work < LockWorkSeconds) continue;
            c.DoorJob = null;
            if (job.Lock && door.Kind == FixtureKind.Door)
            {
                // Task_LockDoor closes an open door and sets the lock at once (the door finishes closing locked); `wants lock` is not touched.
                if (door.Motion == DoorMotion.Opening) door.Motion = DoorMotion.Closing;
                else FixtureRegistry.CloseDoor(door);
            }

            world.Fixtures.SetLocked(door, job.Lock);
            world.EmitSound(job.Lock ? SoundKind.Locked : SoundKind.Unlocked, s, n.Position);
        }
    }

    /// <summary>
    /// Records a lock worker's bar for this tick (<see cref="CharacterCold.LockWork"/>): Task_LockDoor's fill rises from 0 and Task_UnlockDoor's
    /// falls from 1, by 0.3 per game second (<c>FUN_140337670</c>, <c>FUN_14033d420</c>, <b>Verified</b>: the task's <c>+0x80</c> handed to the
    /// bar each update).
    /// </summary>
    static void StampLockWork(World world, CharacterCold c, float worked, bool locking)
    {
        float done = Math.Clamp(worked / LockWorkSeconds, 0, 1);
        c.LockWork = (world.Tick, locking ? done : 1 - done, locking);
    }

    /// <summary>
    /// Each tick, in slot order: each door walker (<see cref="CharacterCold.DoorWalk"/>) in reach opens or closes its door; one given another order,
    /// downed or gone gives up.
    /// </summary>
    static void DoorWalkers(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is { DoorWalk: { } wanted } c) DoorWalkStep(world, s, c, wanted);
        }
    }

    /// <summary>
    /// One door walker's step (<see cref="DoorWalkers"/>).
    /// </summary>
    static void DoorWalkStep(World world, int s, CharacterCold c, string wanted)
    {
        var table = world.Characters;
        if (!table.Next[s].Alive || world.Fixtures.Find(wanted) is not { DoorBroken: false } door || c.Medical is { Incapacitated: true }
            || !c.CarriedBy.IsNone)
        {
            c.DoorWalk = null;
            return;
        }

        ref var n = ref table.Next[s];
        if (!AtWorkSpot(world, door, n, out var at, out float reach))
        {
            if (Redirected(n, at, reach)) c.DoorWalk = null;
            return;
        }

        if (n.Task == (byte)CharacterTask.GoTo) AtWork(world, s);
        c.DoorWalk = null;

        // Its row says which: 72 opens, 73 closes, so of several walkers one arriving after another did nothing undoes it.
        bool open = door.Motion is DoorMotion.Open or DoorMotion.Opening;
        bool close = c.Running?.Row switch { TaskType.CloseDoor => true, TaskType.OpenDoor => false, _ => open };
        if (close)
        {
            if (door.Motion == DoorMotion.Open) FixtureRegistry.CloseDoor(door);
        }
        else if (open) return;
        else if (door.Lock is { EffectivelyLocked: true })
        {
            if (c.IsPlayer) Say(world, c, "It's locked.");
        }
        else FixtureRegistry.OpenDoor(door);
    }

    /// <summary>
    /// The door fixtures of a building the player's faction owns (none for any other).
    /// </summary>
    static List<Fixture> PlayerDoors(World world, string building)
    {
        var list = new List<Fixture>();
        foreach (var f in world.Fixtures.OfBuilding(building))
        {
            if (f.Kind == FixtureKind.Door && !f.DoorBroken && f.Owner >= 0 && f.Owner == world.Player.Faction)
                list.Add(f);
        }

        return list;
    }

    /// <summary>
    /// The doors' step: the player's buildings registered, the leaves moved and held, walkers opening doors, and every <see cref="WalkerEveryTicks"/>
    /// ticks the doors walkers reach opened.
    /// </summary>
    void Doors(World world)
    {
        SyncPlayerBuildings(world);
        world.Fixtures.StepDoors(world.TickSeconds);
        HoldAtLeaves(world);
        DoorWalkers(world);
        if (world.Tick % WalkerEveryTicks == 0) OpenForWalkers(world);
        _closeBehind.Step(world);
    }

    /// <summary>
    /// The player's finished buildings have their doors registered; a building no longer finished (dismantled, ruined) loses them, except a machine
    /// being dismantled (its broken flag, docs/game/buildings-production.md "Broken machines"), which the original keeps standing: a cage or safe of
    /// it stays, its lock counting as broken.
    /// </summary>
    void SyncPlayerBuildings(World world)
    {
        var reg = world.Buildings;
        if (reg.Revision == _buildingsRevision) return;
        _buildingsRevision = reg.Revision;
        var fixtures = world.Fixtures;
        bool Standing(Construction.BuildSite site) => site.Complete || site.Upgrading || world.Machines.Find(site.Id)?.Broken == true;

        // An upgraded or downgraded building keeps its doors while it is built, and gets the new record's: their state carries over by door index.
        var carried = new Dictionary<string, Fixture[]>(StringComparer.Ordinal);
        foreach (var b in fixtures.Buildings.ToArray())
        {
            if (fixtures.OfBuilding(b) is [{ Site: -1 } first, ..] list)
            {
                var site = reg.Find(b);
                if (site is { Destroyed: false } && Standing(site) && first.HostRecordId == site.Record) continue;
                if (site is { Destroyed: false, Upgrading: true } or { Destroyed: false, Complete: true })
                    carried[b] = [.. list];

                fixtures.Unregister(b);
            }
        }

        if (reg.Count == 0 || world.Systems.OfType<PopulationSystem>().FirstOrDefault()?.Data is not { } data) return;
        foreach (var site in reg.All)
        {
            if (Standing(site) && !site.Destroyed)
            {
                fixtures.RegisterPlayerBuilding(world, data, site);
                if (carried.Remove(site.Id, out var old)) fixtures.CarryDoors(site.Id, old);
            }
        }
    }

    /// <summary>
    /// The state of one scan of the walkers at a door: the door, its leaf, and what the walkers did.
    /// </summary>
    struct WalkerScan
    {
        /// <summary>
        /// The world scanned.
        /// </summary>
        public World World;
        /// <summary>
        /// The door.
        /// </summary>
        public Fixture Door;
        /// <summary>
        /// Where its leaf is.
        /// </summary>
        public DoorSpot Spot;
        /// <summary>
        /// Whether a walker opened it.
        /// </summary>
        public bool Opened;
        /// <summary>
        /// The faction of the walker that opened the door (-1: none).
        /// </summary>
        public int OpenerFaction;
        /// <summary>
        /// The slot of the walker that opened the door (-1: none).
        /// </summary>
        public int OpenerSlot;
        /// <summary>
        /// The walkers whose next path leg crosses the door while it is locked (null: none).
        /// </summary>
        public List<int>? Stopped;
    }

    /// <summary>
    /// Opens the closed, unlocked doors that walkers' paths run through; a walker at a locked one goes through it its own way or stops
    /// (<see cref="AtLockedDoor"/>).
    /// </summary>
    void OpenForWalkers(World world)
    {
        var fixtures = world.Fixtures;
        foreach (var f in fixtures.All)
        {
            if (f.Kind != FixtureKind.Door || f.DoorBroken || f.Motion is DoorMotion.Open or DoorMotion.Opening) continue;
            if (fixtures.DoorSpotOf(f) is not { } spot) continue;
            var scan = new WalkerScan { World = world, Door = f, Spot = spot, OpenerFaction = -1, OpenerSlot = -1 };
            world.Grid.Query(spot.Centre.X, spot.Centre.Z, OpenReach + spot.HalfWidth, ref scan, static (int slot, ref WalkerScan s) =>
            {
                if (s.Opened) return;
                var table = s.World.Characters;
                ref var n = ref table.Next[slot];
                if (!n.Alive || table.Cold(slot) is not { } c || c.IsImprisoned) return;
                if (MathF.Abs(n.Position.Y - s.Spot.Foot) > DoorHeight) return;
                if (((MoveFlags)n.Flags & MoveFlags.HasPath) == 0 || c.Medical is { Incapacitated: true }) return;
                float away = FromLeaf(n.Position, s.Spot);
                if (away > OpenReach) return;
                if (s.Door.Lock is { EffectivelyLocked: true })
                {
                    // Task_OpenDoor fails at a locked door for anyone, the owner too (docs/game/locks.md "Doors").
                    if (away > LockedReach || !Crosses(n.Position, c.Path, n.PathCursor, s.Spot, legs: 1)) return;
                    (s.Stopped ??= []).Add(slot);
                    return;
                }

                if (!Crosses(n.Position, c.Path, n.PathCursor, s.Spot, legs: 2)) return;
                s.Opened = FixtureRegistry.OpenDoor(s.Door) || s.Door.Motion == DoorMotion.Opening;
                if (s.Opened) (s.OpenerFaction, s.OpenerSlot) = (c.Faction, slot);
            });
            if (scan.Opened)
            {
                NoteDoorOpened(world, f, scan.OpenerFaction);
                _closeBehind.Note(world, f, spot, scan.OpenerSlot);
            }

            if (scan.Stopped is { } stopped) AtLockedDoor(world, f, stopped);
        }
    }

    /// <summary>
    /// The walkers at a locked door (docs/game/locks.md "Locked doors on the way"): the planner's way through (<see cref="WayThrough"/>: out, 80
    /// unlocking its own or 287 bashing; in, 81 bashing, 78 / 140 unlocking its own, or where the restricted rows are offered 285 cutting or 76
    /// picking), then 72 / 74 opening it, so the first with a way goes through (<see cref="StartDoorWay"/>) unless someone already works the door;
    /// the others stop there, a player's character saying "It's locked." (a lift's or a talk's own walk that no order runs fails instead, its owner
    /// taking it as a lost walk, <see cref="MovementSystem.FailOwnWalk"/>). A patroller has no way through a gate and no way in
    /// (<see cref="PatrolsPast"/>), nor has a member of an AI-run squad living in an outpost through a door of the player's on a walk of the
    /// AI's (<see cref="WayThrough"/>): it stops there, saying nothing.
    /// </summary>
    void AtLockedDoor(World world, Fixture door, List<int> stopped)
    {
        var table = world.Characters;
        bool worked = Worked(world, door.Id);
        foreach (int slot in stopped)
        {
            var c = table.Cold(slot)!;
            ref var n = ref table.Next[slot];
            if (c.DoorWay is not null) continue;
            bool none = worked || c.Bashing is not null || Busy(table.IdOf(slot)) || PatrolsPast(world, slot, c, door);
            var way = none ? DoorWayKind.None : WayThrough(world, slot, door.Building, door);
            if (way != DoorWayKind.None)
            {
                StartDoorWay(world, slot, door, n.Goal, n.Mode);
                worked = true;
                continue;
            }

            // An order's lift or talk ends at the door as before (the order queue reads no failed path); any other one fails as a walk.
            if (c.Running is { Source: TaskSource.Order } || !MovementSystem.FailOwnWalk(ref n, c))
            {
                TaskStart.Stand(table, slot);
                c.WalkEnd = WalkEnd.Failed;
            }

            if (c.IsPlayer && !SquadAutonomy.KeepsPlayerLocks(c)) SayLocked(world, c, door, n.Position);
        }
    }

    /// <summary>
    /// Whether an NPC on 36 Patrolling town has no way through a locked door: the patrol's move goes straight to the movement
    /// (<c>FUN_1405c6b30</c>, <b>Verified</b>), not through the planner, and row 36's list B holds 60 but neither 59
    /// <c>DESTINATION_IS_ACCESSIBLE</c>, whose providers are the ways in (78 / 140 unlock, 81 bash, 76 pick, 285 cut), nor 90
    /// <c>DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS</c>, whose provider 122 Open gate (after 78 Unlocking door) opens a gate on the way
    /// (<b>Verified</b>, row table). So the patroller stops at a gate, even its own faction's (#885: Mourn's guards opened the Ancient Lab gate
    /// on the Great White Gorillo), and at the locked door of a building it stands outside (#974: Sho-Battai's hero patrol worked a shut shop's
    /// lock at night, was booked for Burglary and fought the garrison); 60 still takes it out of the building it stands in.
    /// </summary>
    static bool PatrolsPast(World world, int slot, CharacterCold c, Fixture door)
    {
        if (c.IsPlayer || c.Ai?.Goal?.Task.Type != TaskType.PatrolTown) return false;

        return door.IsGate || world.Sight.BuildingOf(slot) != door.Building;
    }

    /// <summary>
    /// Someone is on its way through the door (<see cref="StartDoorWay"/>).
    /// </summary>
    static bool Worked(World world, string doorId)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s) is { DoorWay: not null, Bashing: { } id } && id == doorId) return true;

        return false;
    }

    /// <summary>
    /// Says "It's locked." for a walker stopped at <paramref name="door"/>, unless it last said so at that door from within <see cref="OpenReach"/>
    /// of here: a character stuck behind a locked gate whose job or goal keeps repathing through it would say it at every stop (Meitou).
    /// </summary>
    static void SayLocked(World world, CharacterCold c, Fixture door, Vector3 here)
    {
        if (c.SaidLocked is { } said && said.Door == door && Vector3.Distance(said.Where, here) <= OpenReach) return;

        c.SaidLocked = (door, here);
        Say(world, c, "It's locked.");
    }

    /// <summary>
    /// Task_OpenDoor (<c>FUN_1403370f0</c>, <b>Verified</b>) clears the door owner's "house locked" flag whenever a character outside the player's
    /// faction opens a door (docs/game/locks.md "Doors").
    /// </summary>
    internal void NoteDoorOpened(World world, Fixture door, int openerFaction)
    {
        if (openerFaction != world.Player.Faction) _houseLocked.Remove(door.Building);
    }

    /// <summary>
    /// An owner's door unlocked and opened on its way (Task_UnlockDoor, then Task_OpenDoor): the unlock's end clears the unlocker's squad's house flag
    /// when the door is its home's (<c>FUN_14033d420</c>, <b>Verified</b>), and the opening clears it as <see cref="NoteDoorOpened"/>.
    /// </summary>
    void UnlockAndOpen(World world, int slot, CharacterCold c, Fixture door)
    {
        world.Fixtures.SetLocked(door, false);
        FixtureRegistry.OpenDoor(door);
        world.EmitSound(SoundKind.Unlocked, slot, world.Characters.Next[slot].Position);

        var squad = world.Squads.Find(c.SquadId);
        if ((squad?.Ai?.OwnHome ?? squad?.HomeBuilding)?.Key == door.Building) _houseLocked.Remove(door.Building);
        NoteDoorOpened(world, door, c.Faction);
    }

    /// <summary>
    /// The open amount from which a door lets walkers through (the original's navmesh treats it as open from 0.6, docs/game/locks.md "Navigation").
    /// </summary>
    public const float PassableOpen = 0.6f;

    /// <summary>
    /// The state of one scan of the characters at a door's leaf.
    /// </summary>
    struct LeafScan
    {
        /// <summary>
        /// The world scanned.
        /// </summary>
        public World World;
        /// <summary>
        /// Where the leaf is.
        /// </summary>
        public DoorSpot Spot;
    }

    /// <summary>
    /// A door less than <see cref="PassableOpen"/> open is solid: a character that moved this tick from one side of its leaf into or through it,
    /// within its width, is put back on its own side at its footprint radius, so a walker waits while the door it opened slides open (a gate takes
    /// seconds) instead of passing through the closed leaf. Engine stand-in for the original's door faces, which no path crosses before 0.6. Serial
    /// phases only.
    /// </summary>
    static void HoldAtLeaves(World world)
    {
        var fixtures = world.Fixtures;
        foreach (var f in fixtures.All)
        {
            if (f.Kind != FixtureKind.Door || f.DoorBroken || f.OpenAmount >= PassableOpen || fixtures.DoorSpotOf(f) is not { } spot)
                continue;

            var scan = new LeafScan { World = world, Spot = spot };
            world.Grid.Query(spot.Centre.X, spot.Centre.Z, spot.HalfWidth + DoorDepth, ref scan, static (int slot, ref LeafScan s) =>
            {
                var table = s.World.Characters;
                ref var n = ref table.Next[slot];
                if (!n.Alive || table.Cold(slot) is not { } c || !c.CarriedBy.IsNone || MathF.Abs(n.Position.Y - s.Spot.Foot) > DoorHeight)
                    return;

                var c2 = new Vector2(s.Spot.Centre.X, s.Spot.Centre.Z);
                var normal = new Vector2(s.Spot.Normal.X, s.Spot.Normal.Z);
                var was = new Vector2(table.Previous[slot].Position.X, table.Previous[slot].Position.Z);
                var now = new Vector2(n.Position.X, n.Position.Z);
                float before = Vector2.Dot(was - c2, normal), after = Vector2.Dot(now - c2, normal);
                float r = c.FootprintRadius > 0 ? c.FootprintRadius : 4;

                // Only one coming from a side; one already in the leaf (a door closed on it) is let out.
                if (MathF.Abs(before) < r * 0.5f || (after > 0) == (before > 0) && MathF.Abs(after) >= r) return;
                var lateral = now - c2 - normal * after;
                if (lateral.Length() > s.Spot.HalfWidth) return;
                var back = normal * ((before > 0 ? r : -r) - after);
                n.Position += new Vector3(back.X, 0, back.Y);
            });
        }
    }

    /// <summary>
    /// How far <paramref name="at"/> is from the leaf's closed width in X/Z: its centre line, <see cref="DoorSpot.HalfWidth"/> each way along the
    /// wall.
    /// </summary>
    internal static float FromLeaf(Vector3 at, DoorSpot door)
    {
        var d = new Vector2(at.X - door.Centre.X, at.Z - door.Centre.Z);
        var along = new Vector2(-door.Normal.Z, door.Normal.X);
        float t = Math.Clamp(Vector2.Dot(d, along), -door.HalfWidth, door.HalfWidth);
        return (d - along * t).Length();
    }

    /// <summary>
    /// Whether one of the next <paramref name="legs"/> legs of a path (from the walker to its next point, then on) crosses the leaf's plane within
    /// its width plus <see cref="DoorSlack"/>, in X/Z.
    /// </summary>
    static bool Crosses(Vector3 at, Vector3[] path, int cursor, DoorSpot door, int legs)
    {
        var c = new Vector2(door.Centre.X, door.Centre.Z);
        var normal = new Vector2(door.Normal.X, door.Normal.Z);
        var a = new Vector2(at.X, at.Z);
        for (int k = cursor; k < path.Length && k < cursor + legs; k++)
        {
            var b = new Vector2(path[k].X, path[k].Z);
            float da = Vector2.Dot(a - c, normal), db = Vector2.Dot(b - c, normal);
            if ((da <= 0) != (db <= 0) && Vector2.Distance(a + (b - a) * (da / (da - db)), c) <= door.HalfWidth + DoorSlack)
                return true;

            a = b;
        }

        return false;
    }
}