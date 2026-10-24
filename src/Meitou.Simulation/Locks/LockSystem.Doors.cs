using System.Numerics;

namespace Meitou.Simulation.Locks;

/// <summary>Opens the closed doors of a player building, or closes its open ones when any is open (the door panel's open/close button, <c>FUN_140546fb0</c>; a locked door is unlocked first).</summary>
public sealed record ToggleDoorsOrder(string Building) : SimCommand;

/// <summary>Flips <c>wants lock</c> on every door of a player building (the door panel's lock button, <c>FUN_140547060</c>): a closed door locks at once, an open one when it closes; unlocking clears the lock.</summary>
public sealed record ToggleDoorLocksOrder(string Building) : SimCommand;

/// <summary>
/// A quick right click on a door (docs/game/ui-input.md "Building hover and click"): the nearest of the characters (the selection when empty) walks to it and opens a closed one or closes an
/// open one (72 OPEN_DOOR / 73 CLOSE_DOOR; a locked one: "It's locked."). Another faction's locked door: with <see cref="PickLock"/> (Alt) its lock is picked (76 PICK_LOCK), else the
/// hint and a walk to it.
/// </summary>
public sealed record DoorOrder(IReadOnlyList<CharacterId> Characters, string Door, bool PickLock = false) : SimCommand;

/// <summary>
/// The doors (docs/simulation.md "Cages, beds and locks"; the original's rules in docs/game/locks.md "Doors"): every tick the opening and closing leaves move (<see cref="FixtureRegistry.StepDoors"/>);
/// every <see cref="WalkerEveryTicks"/> ticks a character whose path runs through a closed door opens it, or stops at a locked one ("It's locked."); the player's finished buildings
/// get their doors registered (<see cref="FixtureRegistry.RegisterPlayerBuilding"/>) and the player's door orders are applied. Serial phases only.
/// <list type="bullet">
/// <item><b>Engine choice</b>: the original's walk needs its destination and location "accessible" (states 59, 60), which Task_OpenDoor (84, 85) makes true by opening the door
/// (the walker faces the door, a locked one fails the task); here the walk itself opens it: a walker within <see cref="OpenReach"/> of a closed door whose current or next path leg
/// crosses the leaf's plane within its width (plus <see cref="DoorSlack"/>). At a locked one the walker stops, within <see cref="LockedReach"/> on its current leg. The walker does not
/// wait for the leaf (the navmesh lets paths through closed unlocked doors).</item>
/// <item>The original closes no door behind a walker (<b>Observed</b>: only Task_CloseDoor, Task_LockDoor and the door panel start a close, and only the lock tasks need a closed door).
/// Meitou (<see cref="DoorsCloseBehind"/>): a door a walker opened closes <see cref="AutoCloseSeconds"/> after the last character went through or stood in it, unless it wants its lock.</item>
/// </list>
/// </summary>
public sealed partial class LockSystem
{
    /// <summary>Engine choices of the walk-through opening (see the class remarks).</summary>
    public const int WalkerEveryTicks = 3;
    public const float OpenReach = 40, LockedReach = 15, DoorSlack = 6, DoorDepth = 12, DoorHeight = 40;
    /// <summary>Meitou: a door a walker opened closes again this many game seconds after the last character went through or stood in it.</summary>
    public const float AutoCloseSeconds = 5;

    /// <summary>Meitou (on; Faithful off, the original leaves doors open): doors walkers opened close behind them (<see cref="AutoCloseSeconds"/>). Set by the host's settings.</summary>
    public bool DoorsCloseBehind { get; set; } = true;

    long buildingsRevision = -1;

    void DoorInputs(World world, SimCommand command)
    {
        var fixtures = world.Fixtures;
        switch (command)
        {
            case ToggleDoorsOrder toggle:
            {
                var doors = PlayerDoors(world, toggle.Building);
                if (doors.Count == 0) break;
                // Meitou: a door still moving counts by where it is heading and turns back (the original's open and close act only from closed / open, so a
                // slow gate ignored the button for seconds).
                bool anyOpen = doors.Any(d => d.Motion is DoorMotion.Open or DoorMotion.Opening);
                foreach (var d in doors)
                {
                    if (anyOpen)
                    {
                        if (d.Motion == DoorMotion.Opening) { d.Motion = DoorMotion.Closing; d.AutoOpened = false; }
                        else FixtureRegistry.CloseDoor(d);
                        continue;
                    }
                    if (d.Lock is { EffectivelyLocked: true } l) { l.Locked = false; fixtures.Touch(d); }
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
                if (world.Fixtures.OwnsBuilding(open.Building, world.Player.Faction)) fixtures.SetPublic(open.Building, open.Public);
                break;
        }
    }

    /// <summary>Where a character works a fixture from, and how near counts: a door's side nearer <paramref name="from"/>, out from the leaf; anything else its own place.</summary>
    static (Vector2 At, float Reach) WorkSpot(World world, Fixture f, Vector2 from)
    {
        if (f.Kind != FixtureKind.Door || world.Fixtures.DoorSpotOf(f) is not { } spot) return (Flat(f.Position), f.UseRange + Reach);
        var outside = StandSpot(f, spot);
        var centre = new Vector2(spot.Centre.X, spot.Centre.Z);
        var inside = 2 * centre - outside;
        return (Vector2.DistanceSquared(from, inside) < Vector2.DistanceSquared(from, outside) ? inside : outside, BashReach);
    }

    void DoorOrderInput(World world, DoorOrder order)
    {
        if (world.Fixtures.Find(order.Door) is not { Kind: FixtureKind.Door, DoorBroken: false } door) return;
        var table = world.Characters;
        int best = -1;
        float bestDistance = float.MaxValue;
        foreach (var id in Who(world, order.Characters))
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, IsImprisoned: false } c || !c.CarriedBy.IsNone) continue;
            float d = Vector2.Distance(Flat(table.Next[s].Position), WorkSpot(world, door, Flat(table.Next[s].Position)).At);
            if (d < bestDistance) (best, bestDistance) = (s, d);
        }
        if (best < 0) return;
        var walker = table.Cold(best)!;
        var player = world.Player;
        var (at, _) = WorkSpot(world, door, Flat(table.Next[best].Position));
        walker.Shackles = null;
        walker.PickingLock = null;
        player.DoorTarget = null;
        if (door.Lock is { EffectivelyLocked: true } && door.Motion == DoorMotion.Closed && door.Owner != world.Player.Faction)
        {
            if (order.PickLock) walker.PickingLock = door.Id;
            else world.Post("Hold down the ALT key to pick the lock.");
        }
        else (player.DoorTarget, player.DoorWalker) = (door.Id, table.IdOf(best));
        MovementSystem.GoTo(table, best, at, walker.RunSpeed);
    }

    /// <summary>Each tick: the door walker in reach opens or closes its door; one given another order, downed or gone gives up.</summary>
    static void DoorWalkers(World world)
    {
        var player = world.Player;
        if (player.DoorTarget is not { } wanted) return;
        var table = world.Characters;
        if (world.Fixtures.Find(wanted) is not { DoorBroken: false } door || !table.TryResolveNext(player.DoorWalker, out int s) || table.Cold(s) is not { Medical.Incapacitated: false } c || !c.CarriedBy.IsNone)
        {
            player.DoorTarget = null;
            return;
        }
        ref var n = ref table.Next[s];
        var (at, reach) = WorkSpot(world, door, Flat(n.Position));
        if (Vector2.Distance(Flat(n.Position), at) > reach)
        {
            if (n.Task != (byte)CharacterTask.GoTo || Vector2.Distance(n.Goal, at) > reach) player.DoorTarget = null;
            return;
        }
        if (n.Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, s);
        player.DoorTarget = null;
        if (door.Motion is DoorMotion.Open or DoorMotion.Opening) { if (door.Motion == DoorMotion.Open) FixtureRegistry.CloseDoor(door); }
        else if (door.Lock is { EffectivelyLocked: true }) world.Post($"{c.Name}: It's locked.");
        else FixtureRegistry.OpenDoor(door);
    }

    /// <summary>The door fixtures of a building the player's faction owns (none for any other).</summary>
    static List<Fixture> PlayerDoors(World world, string building)
    {
        var list = new List<Fixture>();
        foreach (var f in world.Fixtures.OfBuilding(building))
            if (f.Kind == FixtureKind.Door && !f.DoorBroken && f.Owner >= 0 && f.Owner == world.Player.Faction) list.Add(f);
        return list;
    }

    void Doors(World world)
    {
        SyncPlayerBuildings(world);
        world.Fixtures.StepDoors(world.TickSeconds);
        HoldAtLeaves(world);
        DoorWalkers(world);
        if (world.Tick % WalkerEveryTicks == 0) OpenForWalkers(world);
    }

    /// <summary>The player's finished buildings have their doors registered; a building no longer finished (dismantled, ruined) loses them.</summary>
    void SyncPlayerBuildings(World world)
    {
        var reg = world.Buildings;
        if (reg.Revision == buildingsRevision) return;
        buildingsRevision = reg.Revision;
        var fixtures = world.Fixtures;
        // An upgraded or downgraded building keeps its doors while it is built, and gets the new record's: their state carries over by door index.
        var carried = new Dictionary<string, Fixture[]>(StringComparer.Ordinal);
        foreach (var b in fixtures.Buildings.ToArray())
            if (fixtures.OfBuilding(b) is [{ Site: -1 } first, ..] list)
            {
                var site = reg.Find(b);
                if (site is { Destroyed: false } && (site.Complete || site.Upgrading) && first.HostRecordId == site.Record) continue;
                if (site is { Destroyed: false, Upgrading: true } or { Destroyed: false, Complete: true }) carried[b] = [.. list];
                fixtures.Unregister(b);
            }
        if (reg.Count == 0 || world.Systems.OfType<PopulationSystem>().FirstOrDefault()?.Data is not { } data) return;
        foreach (var site in reg.All)
            if ((site.Complete || site.Upgrading) && !site.Destroyed)
            {
                fixtures.RegisterPlayerBuilding(world, data, site);
                if (carried.Remove(site.Id, out var old)) fixtures.CarryDoors(site.Id, old);
            }
    }

    struct WalkerScan
    {
        public World World;
        public Fixture Door;
        public DoorSpot Spot;
        public bool Opened;
        /// <summary>The door is open (or opening): the scan only notes who goes through or stands in it.</summary>
        public bool Watch;
        public bool Passed;
    }

    /// <summary>
    /// Opens the closed, unlocked doors that walkers' paths run through (a walker at a locked one stops); notes who goes through or stands in the doors walkers opened, and closes
    /// one nobody has used for <see cref="AutoCloseSeconds"/> (Meitou, <see cref="DoorsCloseBehind"/>).
    /// </summary>
    void OpenForWalkers(World world)
    {
        var fixtures = world.Fixtures;
        foreach (var f in fixtures.All)
        {
            if (f.Kind != FixtureKind.Door || f.DoorBroken) continue;
            bool watch = f.Motion is DoorMotion.Open or DoorMotion.Opening;
            if (watch && !f.AutoOpened) continue;
            if (fixtures.DoorSpotOf(f) is not { } spot) continue;
            var scan = new WalkerScan { World = world, Door = f, Spot = spot, Watch = watch };
            world.Grid.Query(spot.Centre.X, spot.Centre.Z, OpenReach, ref scan, static (int slot, ref WalkerScan s) =>
            {
                if (s.Opened || s.Passed) return;
                var table = s.World.Characters;
                ref var n = ref table.Next[slot];
                if (!n.Alive || table.Cold(slot) is not { } c || c.IsImprisoned) return;
                if (MathF.Abs(n.Position.Y - s.Spot.Foot) > DoorHeight) return;
                if (s.Watch)
                {
                    // Anyone in the doorway (down or standing) or walking through it keeps it open.
                    s.Passed = InDoorway(n.Position, s.Spot) || ((MoveFlags)n.Flags & MoveFlags.HasPath) != 0 && Crosses(n.Position, c.Path, n.PathCursor, s.Spot, legs: 2);
                    return;
                }
                if (((MoveFlags)n.Flags & MoveFlags.HasPath) == 0 || c.Medical is { Incapacitated: true }) return;
                float away = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), new Vector2(s.Spot.Centre.X, s.Spot.Centre.Z));
                if (away > OpenReach) return;
                if (s.Door.Lock is { EffectivelyLocked: true })
                {
                    // Task_OpenDoor fails at a locked door for anyone, the owner too (docs/game/locks.md "Doors").
                    if (away > LockedReach || !Crosses(n.Position, c.Path, n.PathCursor, s.Spot, legs: 1)) return;
                    MovementSystem.Stop(table, slot);
                    if (c.IsPlayer) s.World.Post($"{c.Name}: It's locked.");
                    return;
                }
                if (!Crosses(n.Position, c.Path, n.PathCursor, s.Spot, legs: 2)) return;
                s.Opened = FixtureRegistry.OpenDoor(s.Door) || s.Door.Motion == DoorMotion.Opening;
            });
            if (scan.Opened && !watch) { f.AutoOpened = true; f.LastPass = world.Tick; }
            else if (watch && scan.Passed) f.LastPass = world.Tick;
            // A door that wants its lock is left open: closing it would lock it, and a locked gate shuts its building for every path.
            else if (watch && DoorsCloseBehind && !f.WantsLock && f.Motion == DoorMotion.Open && (world.Tick - f.LastPass) * world.TickSeconds >= AutoCloseSeconds)
                FixtureRegistry.CloseDoor(f);
        }
    }

    /// <summary>The open amount from which a door lets walkers through (the original's navmesh treats it as open from 0.6, docs/game/locks.md "Navigation").</summary>
    public const float PassableOpen = 0.6f;

    struct LeafScan
    {
        public World World;
        public DoorSpot Spot;
    }

    /// <summary>
    /// A door less than <see cref="PassableOpen"/> open is solid: a character that moved this tick from one side of its leaf into or through it, within its width, is put back
    /// on its own side at its footprint radius, so a walker waits while the door it opened slides open (a gate takes seconds) instead of passing through the closed leaf.
    /// Engine stand-in for the original's door faces, which no path crosses before 0.6. Serial phases only.
    /// </summary>
    static void HoldAtLeaves(World world)
    {
        var fixtures = world.Fixtures;
        foreach (var f in fixtures.All)
        {
            if (f.Kind != FixtureKind.Door || f.DoorBroken || f.OpenAmount >= PassableOpen || fixtures.DoorSpotOf(f) is not { } spot) continue;
            var scan = new LeafScan { World = world, Spot = spot };
            world.Grid.Query(spot.Centre.X, spot.Centre.Z, spot.HalfWidth + DoorDepth, ref scan, static (int slot, ref LeafScan s) =>
            {
                var table = s.World.Characters;
                ref var n = ref table.Next[slot];
                if (!n.Alive || table.Cold(slot) is not { } c || !c.CarriedBy.IsNone || MathF.Abs(n.Position.Y - s.Spot.Foot) > DoorHeight) return;
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

    /// <summary>Whether a character stands in the door's leaf box, widened by <see cref="DoorSlack"/> along the wall and <see cref="DoorDepth"/> through it, in X/Z.</summary>
    static bool InDoorway(Vector3 at, DoorSpot door)
    {
        var d = new Vector2(at.X - door.Centre.X, at.Z - door.Centre.Z);
        var normal = new Vector2(door.Normal.X, door.Normal.Z);
        float through = Vector2.Dot(d, normal);
        return MathF.Abs(through) <= DoorDepth && (d - normal * through).Length() <= door.HalfWidth + DoorSlack;
    }

    /// <summary>Whether one of the next <paramref name="legs"/> legs of a path (from the walker to its next point, then on) crosses the leaf's plane within its width plus <see cref="DoorSlack"/>, in X/Z.</summary>
    static bool Crosses(Vector3 at, Vector3[] path, int cursor, DoorSpot door, int legs)
    {
        var c = new Vector2(door.Centre.X, door.Centre.Z);
        var normal = new Vector2(door.Normal.X, door.Normal.Z);
        var a = new Vector2(at.X, at.Z);
        for (int k = cursor; k < path.Length && k < cursor + legs; k++)
        {
            var b = new Vector2(path[k].X, path[k].Z);
            float da = Vector2.Dot(a - c, normal), db = Vector2.Dot(b - c, normal);
            if ((da <= 0) != (db <= 0) && Vector2.Distance(a + (b - a) * (da / (da - db)), c) <= door.HalfWidth + DoorSlack) return true;
            a = b;
        }
        return false;
    }
}
