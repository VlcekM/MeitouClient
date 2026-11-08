using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Building;
using Meitou.Simulation;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;

namespace Meitou.Game.Ui;

/// <summary>What a click does with the furniture and fixtures of a building: beds, cages, containers, doors, training dummies, seats and mining rocks.</summary>
sealed class FurnitureUse(PlayerInterface player)
{
    // Beds (docs/game/ui-input.md "Building hover and click", docs/game/locks.md "Beds"): with characters selected the pointer over a bed is <c>use</c>; a quick right click gives the original's
    // order 258 "Sleep" (UseBedOrder: every selected character walks there, the first to arrive lies in it until another order). Held, the menu offers the original's one bed verb: "Put in Bed"
    // (PutInBedOrder) while a selected character carries someone, else "Sleep", followed by a player bed's site verbs. A quick click while a selected character carries
    // someone lays the body in instead (<b>engine choice</b>: the original's quick click gives 258 regardless, whose plan needs the sleeper not to carry, and how it meets that is
    // <b>Unknown</b>).
    /// <summary>How near the pointer the projected middle of a bed without a footprint must be, in pixels (an engine choice).</summary>
    const float BedPickPixels = 30;

    internal BedSpot? PickBed(Vector2 pixel) => PickBedAt(pixel)?.Bed;

    /// <summary>
    /// The bed under the pixel with the ray distance, when not hidden behind a wall nor above the viewed floor of the building the player is in (<see cref="AboveViewedFloor"/>): the
    /// nearest the ray meets on its record's collision (hull prism, then triangles), as a building is picked; else the bed without a footprint whose middle (half a unit above its spot)
    /// projects nearest the pixel within <see cref="BedPickPixels"/>. Until its collision is read (on workers) the magnet stands in.
    /// </summary>
    internal (BedSpot Bed, float T)? PickBedAt(Vector2 pixel)
    {
        if (player.Ray(pixel) is not { } ray) return null;
        (BedSpot, float)? hit = null, near = null;
        float bestT = float.MaxValue, bestPixels = BedPickPixels;
        foreach (var b in player.session.CurrentSnapshot.Beds)
        {
            if (player.build.buildHost?.Placement.Footprint(b.Record) is { } footprint)
            {
                var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.Yaw);
                if (player.BuildingRayT(ray, footprint.Place(b.Position, yaw), footprint.YMin, footprint.YMax, b.Record, b.Position, b.Position.Y, yaw) is { } t && t < bestT && AboveViewedFloor?.Invoke(b.Position) != true && !player.Hidden(ray, t))
                    (hit, bestT) = ((b, t), t);
                continue;
            }
            var middle = b.Position + new Vector3(0, 0.5f, 0);
            if (player.Project(middle) is not { } at) continue;
            float d = Vector2.Distance(at, pixel), distance = Vector3.Distance(ray.Origin, middle);
            if (d < bestPixels && AboveViewedFloor?.Invoke(b.Position) != true && !player.Hidden(ray, distance)) (near, bestPixels) = ((b, distance), d);
        }
        return hit ?? near;
    }

    (BedSpot Bed, Vector2 At, long Since)? bedPending;

    /// <summary>A right press on a bed with characters selected: released before the hold time it gives the quick order, held longer it opens the menu (true: taken).</summary>
    internal bool BedPress(Vector2 p)
    {
        if (player.SelectedIds().Count == 0 || PickBed(p) is not { } bed) return false;
        bedPending = (bed, p, System.Diagnostics.Stopwatch.GetTimestamp());
        return true;
    }

    /// <summary>The selected characters carrying someone.</summary>
    CharacterId[] Carriers() => [.. player.Selected().Where(c => !c.Carrying.IsNone).Select(c => c.Id)];

    void SleepIn(BedSpot bed) => player.Say(new UseBedOrder([], bed.Id) { Tick = player.session.World.Tick });

    void PutIn(BedSpot bed, CharacterId[] carriers) => player.Say(new PutInBedOrder(carriers, bed.Id) { Tick = player.session.World.Tick });

    /// <summary>The right button let go: a press on a bed released before the menu opened.</summary>
    internal void BedRelease()
    {
        if (bedPending is not { } pending) return;
        bedPending = null;
        if (Carriers() is { Length: > 0 } carriers) PutIn(pending.Bed, carriers);
        else SleepIn(pending.Bed);
    }

    /// <summary>Once a frame: a bed press held long enough opens its menu (the original's <c>FUN_1407a6bc0</c>: 99 when the last selected character carries someone, else the bed's order 258).</summary>
    internal void UpdateBed()
    {
        if (bedPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < PlayerInterface.ContextHoldSeconds) return;
        bedPending = null;
        if (player.SelectedIds().Count == 0) return;
        var bed = pending.Bed;
        var carriers = Carriers();
        var verbs = new List<(string Caption, Action Give)> { player.Selected().LastOrDefault() is { } lead && !lead.Carrying.IsNone ? ("Put in Bed", () => PutIn(bed, carriers)) : ("Sleep", () => SleepIn(bed)) };
        // A bed the player built: its site's verbs too (dismantle...), which its quick click no longer reaches.
        if (player.session.CurrentSnapshot.Buildings.FirstOrDefault(s => bed.Id == s.Id + "@bed0") is { } site) verbs.AddRange(player.build.SiteVerbs(site));
        player.contextMenu = new(player.build.buildHost?.ItemName(bed.Record) ?? bed.Record, pending.At, verbs);
    }

    // Cages under the pointer (docs/game/ui-input.md "Context menu verbs", the cage rows; the cages are published as WorldSnapshot.Cages): a quick right click on an empty cage
    // picks a locked one's lock, puts the carried captive in a free one (108) or gets the character in (107) (with Shift held; else the cage's menu opens on the press). Engine choice: the "Cage" title.
    /// <summary>
    /// The cage under the pixel with the ray distance, when not hidden behind a wall: the nearest the ray meets on its record's collision, as a container is picked
    /// (<see cref="PickContainerAt"/>); one without collision, or above the shown floor of the building the player is in, cannot be picked.
    /// </summary>
    internal (CageSpot Cage, float T)? PickCageAt(Vector2 pixel)
    {
        if (player.Ray(pixel) is not { } ray || player.build.buildHost?.Placement is not { } placement) return null;
        var cages = player.session.CurrentSnapshot.Cages;
        (CageSpot, float)? hit = null;
        float bestT = float.MaxValue;
        foreach (var c in cages)
        {
            if (placement.Footprint(c.Record) is not { } footprint) continue;
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, c.Yaw);
            if (player.BuildingRayT(ray, footprint.Place(c.Position, yaw), footprint.YMin, footprint.YMax, c.Record, c.Position, c.Position.Y, yaw) is { } t && t < bestT && AboveViewedFloor?.Invoke(c.Position) != true && !player.Hidden(ray, t))
                (hit, bestT) = ((c, t), t);
        }
        return hit;
    }

    /// <summary>
    /// The verbs of a cage (the original's cage rows, <c>FUN_1407a6bc0</c>, <b>Verified</b>). Another faction's: locked, Pick Lock (76), Use Tools (285), Use Strength (286); unlocked, Put in
    /// (108) when a selected character carries someone, else Get in (107), or Set Free (110) when occupied. The player's own: Unlock (78) or Lock (77) by its lock, then Put in when empty and
    /// a selected character carries someone, Set Free when occupied (the release unlocks it).
    /// </summary>
    internal List<(string Caption, Action Give)> CageVerbs(CageSpot cage)
    {
        var verbs = new List<(string, Action)>();
        var selection = player.SelectedIds();
        if (selection.Count == 0) return verbs;
        bool occupied = !cage.Occupant.IsNone;
        if (cage.Owner == player.session.World.Player.Faction) OwnCageVerbs(verbs, cage, occupied);
        else OtherCageVerbs(verbs, cage, occupied, selection);
        return verbs;
    }

    /// <summary>The player's own cage: Unlock or Lock (a walk and 3.3 s of work, <see cref="DoorLockOrder"/>), then Put in when empty and carrying, Set Free when occupied.</summary>
    void OwnCageVerbs(List<(string, Action)> verbs, CageSpot cage, bool occupied)
    {
        bool locking = !cage.Locked;
        verbs.Add((locking ? "Lock" : "Unlock", () => player.Say(new DoorLockOrder([], cage.Id, locking) { Tick = player.session.World.Tick })));
        if (occupied)
        {
            SetFreeVerb(verbs, cage);
            return;
        }
        var carriers = Carriers();
        if (carriers.Length > 0) verbs.Add(("Put in", () => player.Say(new Meitou.Simulation.Crime.PutInCageOrder(carriers) { Cage = cage.Id, Tick = player.session.World.Tick })));
    }

    /// <summary>Another faction's cage: locked, Pick Lock, Use Tools and Use Strength; unlocked, Put in or Get in when empty, Set Free when occupied.</summary>
    void OtherCageVerbs(List<(string, Action)> verbs, CageSpot cage, bool occupied, IReadOnlyList<CharacterId> selection)
    {
        if (cage.Locked)
        {
            verbs.Add((player.PickLockCaption(cage.Position, cage.Level, !occupied), () => player.Say(new PickLockOrder(selection, cage.Occupant) { Cage = occupied ? null : cage.Id, Tick = player.session.World.Tick })));
            verbs.Add(("Use Tools", () => player.Say(new ForceLockOrder([], cage.Id, LockForce.Tools) { Tick = player.session.World.Tick })));
            verbs.Add(("Use Strength", () => player.Say(new ForceLockOrder([], cage.Id, LockForce.Strength) { Tick = player.session.World.Tick })));
            return;
        }
        if (occupied)
        {
            SetFreeVerb(verbs, cage);
            return;
        }
        var carriers = Carriers();
        if (carriers.Length > 0) verbs.Add(("Put in", () => player.Say(new Meitou.Simulation.Crime.PutInCageOrder(carriers) { Cage = cage.Id, Tick = player.session.World.Tick })));
        else verbs.Add(("Get in", () => player.Say(new GetInCageOrder([], cage.Id) { Tick = player.session.World.Tick })));
    }

    /// <summary>Set Free (110), not for a prisoner who is dead, out or carried (TARGET_IS_CONSCIOUS, the simulation refuses it with nothing said).</summary>
    void SetFreeVerb(List<(string, Action)> verbs, CageSpot cage)
    {
        var prisoner = player.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == cage.Occupant);
        if (prisoner is null || prisoner.Conscious) verbs.Add(("Set Free", () => player.Say(new SetFreeOrder([], cage.Occupant) { Tick = player.session.World.Tick })));
    }

    /// <summary>
    /// A right press on a cage (true, with a selection): its menu opens at once, whoever owns it and whether it is occupied, unless Shift is held (<c>FUN_140800250</c>, <b>Verified</b>);
    /// with Shift, released quickly, the first of the cage's verbs while it is empty, held, its menu. An occupied cage with occupant selection is pointed at as its occupant instead
    /// (<see cref="PlayerInterface.PickWhereAt"/>).
    /// </summary>
    internal bool CageClick(Vector2 p)
    {
        if (player.SelectedIds().Count == 0 || PickCageAt(p)?.Cage is not { } cage) return false;
        var verbs = CageVerbs(cage);
        if (!player.inventoryWindows.pressShift && player.Menu(Loc.Tr("Cage"), p, verbs) is { } now) { player.contextMenu = now; return true; }
        // The quick order is the cage's use (108 / 107 / 76), never the own cage's Lock or Unlock.
        Action? quick = cage.Occupant.IsNone ? verbs.FirstOrDefault(v => v.Caption is not ("Lock" or "Unlock")).Give : null;
        return player.ContextPressOn(p, at => player.Menu(Loc.Tr("Cage"), at, CageVerbs(cage)), quick);
    }

    // Containers of a building's interior (docs/game/locks.md "Containers"): a right click on one sends the nearest selected character to open it (pointer <c>search</c>); its inventory
    // then shows in the storage window the research bench uses (<c>PlayerInterface.Production.cs</c>), beside the character's.
    /// <summary>Whether a place is above the floor shown in a building the player is in, where nothing is drawn (the host sets it; null: never).</summary>
    public Func<Vector3, bool>? AboveViewedFloor { get; set; }

    internal ContainerSpot? PickContainer(Vector2 pixel) => PickContainerAt(pixel)?.Container;

    /// <summary>
    /// The container under the pixel with the ray distance, when not hidden behind a wall: the nearest the ray meets on its record's collision (hull prism, then triangles), as a building is
    /// picked (docs/game/ui-input.md "Building picking and selection"); one without collision, or above the shown floor of the building the player is in, cannot be picked. The
    /// collision is read on workers; until then it is not picked.
    /// </summary>
    internal (ContainerSpot Container, float T)? PickContainerAt(Vector2 pixel)
    {
        if (player.Ray(pixel) is not { } ray || player.build.buildHost?.Placement is not { } placement) return null;
        var containers = player.session.CurrentSnapshot.Containers;
        (ContainerSpot, float)? hit = null;
        float bestT = float.MaxValue;
        foreach (var c in containers)
        {
            if (placement.Footprint(c.Record) is not { } footprint) continue;
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, c.Yaw);
            if (player.BuildingRayT(ray, footprint.Place(c.Position, yaw), footprint.YMin, footprint.YMax, c.Record, c.Position, c.Position.Y, yaw) is { } t && t < bestT && AboveViewedFloor?.Invoke(c.Position) != true && !player.Hidden(ray, t))
                (hit, bestT) = ((c, t), t);
        }
        return hit;
    }

    /// <summary>
    /// A right press on a container (true, with a selection): released quickly, the open order, where Alt picks another faction's locked one (the sim gives the hint without it); held,
    /// its context menu (<see cref="PlayerInterface.ContainerVerbs"/>).
    /// </summary>
    internal bool ContainerClick(Vector2 p, bool alt)
    {
        if (player.SelectedIds().Count == 0 || PickContainer(p) is not { } container) return false;
        string name = player.build.buildHost?.ItemName(container.Record) ?? container.Record;
        if (!player.ContextPressOn(p, at => player.Menu(name, at, player.ContainerVerbs(container)), () => player.OpenContainer(container.Id, alt))) player.OpenContainer(container.Id, alt);
        return true;
    }

    /// <summary>
    /// The pointer over a container (docs/game/ui-input.md "Building hover and click", StorageBuilding): <c>search</c> for the player's own or an unowned one, else <c>lockpick</c> while
    /// locked and <c>scavenge</c> when not.
    /// </summary>
    internal string ContainerPointer(ContainerSpot container) =>
        container.Owner < 0 || container.Owner == player.session.World.Player.Faction ? "search" : container.Locked ? "lockpick" : "scavenge";

    // Doors under the pointer (docs/game/ui-input.md "Building hover and click"): a quick right click sends the nearest selected character to open a closed door or close an open one;
    // another faction's locked door takes Alt to pick its lock (with Shift held, else its menu opens on the press). Pointers: <c>door</c>, <c>lock</c> over the player's own locked door, <c>lockpick</c> over another faction's.
    /// <summary>How much of the way to the leaf a building's own collision may take before the door counts as hidden (the leaf sits in its building's wall).</summary>
    const float DoorPickSlack = 10;

    /// <summary>The door's collision triangles by its BUILDING record and index, in that building's frame with the leaf closed (the host sets it; null: no door is picked).</summary>
    public Func<string, int, IReadOnlyList<(Vector3[] Vertices, int[] Indices)>>? DoorShape { get; set; }

    /// <summary>
    /// The door whose leaf's collision the ray meets nearest, where the leaf has slid to, if any and not hidden behind another wall; locked or not (the original's pick hits a door's
    /// collision, group 5, docs/game/ui-input.md "Building picking and selection"). One without collision cannot be picked. A broken door counts only when it is the player's (it
    /// takes the repair order).
    /// </summary>
    internal (DoorPick Door, bool Locked)? PickDoor(Vector2 pixel) => PickDoorAt(pixel)?.Pick;

    /// <summary><see cref="PickDoor"/> with the ray distance to the hit.</summary>
    internal ((DoorPick Door, bool Locked) Pick, float T)? PickDoorAt(Vector2 pixel)
    {
        if (player.Ray(pixel) is not { } ray || DoorShape is not { } shapeOf) return null;
        var snapshot = player.session.CurrentSnapshot;
        ((DoorPick, bool), float)? best = null;
        float bestT = float.MaxValue;
        foreach (var door in snapshot.DoorPicks)
        {
            // A door missing from the published table is closed and unlocked. The leaf is where it has slid to (linear in the open amount: exact at closed and open).
            var state = snapshot.Doors.GetValueOrDefault((door.Building, door.Door));
            if (state.Broken && door.Owner != player.session.World.Player.Faction) continue;
            var mesh = shapeOf(door.HostRecord, door.Door);
            if (mesh.Count == 0) continue;
            var at = door.Origin + door.Slide * state.Open;
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, door.Yaw);
            if (player.groundItemPicks.RayNears(ray, mesh, at, turn) && PlayerInterface.RayMesh(ray, mesh, at, turn) is { } t && t < bestT && !player.Hidden(ray, t - DoorPickSlack))
                (best, bestT) = (((door, state.Locked), t), t);
        }
        return best;
    }

    /// <summary>Whether the door is broken (its progress ran out, <see cref="Meitou.Simulation.Locks.Fixture.DoorBroken"/>): the player's own takes the repair order (95).</summary>
    internal bool DoorBroken(DoorPick door) => player.session.CurrentSnapshot.Doors.GetValueOrDefault((door.Building, door.Door)).Broken;

    /// <summary>The pointer over a door: <c>spanner</c> for a broken one (<c>FUN_1407fc6e0</c>), <c>lock</c> for the player's own locked one, <c>lockpick</c> for another faction's, else <c>door</c>.</summary>
    internal string DoorPointer((DoorPick Door, bool Locked) pick) => DoorBroken(pick.Door) ? "spanner" : !pick.Locked ? "door" : pick.Door.Owner == player.session.World.Player.Faction ? "lock" : "lockpick";

    /// <summary>
    /// A right press on a door (true, with a selection): another faction's locked door opens its context menu (<see cref="PlayerInterface.DoorVerbs"/>) at once unless Shift is held (<c>FUN_140800250</c>,
    /// <b>Verified</b>); any other door, released quickly, gives the door order, where Alt picks another faction's locked lock, held, the menu (titled "Door": the door's own name is not
    /// published, engine choice).
    /// </summary>
    internal bool DoorClick(Vector2 p, bool alt)
    {
        if (player.SelectedIds().Count == 0 || PickDoor(p) is not { } pick) return false;
        if (!player.inventoryWindows.pressShift && pick.Locked && pick.Door.Owner != player.session.World.Player.Faction && player.Menu(Loc.Tr("Door"), p, player.DoorVerbs(pick)) is { } now) { player.contextMenu = now; return true; }
        void Quick() => player.Say(DoorBroken(pick.Door) ? new Meitou.Simulation.Construction.BuildOrder([], pick.Door.Id) { Tick = player.session.World.Tick } : new DoorOrder([], pick.Door.Id, alt) { Tick = player.session.World.Tick });
        if (!player.ContextPressOn(p, at => player.Menu(Loc.Tr("Door"), at, player.DoorVerbs(pick)), Quick)) Quick();
        return true;
    }

    // A town's training dummies (docs/game/ui-input.md "Context menu verbs": UseableStuff function 7 gives 97 USE_TRAINING_DUMMY whoever owns it): with characters selected the pointer over one
    // is <c>use</c>; a quick right click has them train there (OperateOrder), with Shift it becomes the Training job; held, the menu offers "Training". A player's own dummy is a
    // build site and goes through the site verbs instead.
    /// <summary>
    /// The dummy under the pixel with the ray distance, picked as a bed is (<see cref="PickBedAt"/>): on its record's collision, else its middle projected within <see cref="BedPickPixels"/>.
    /// </summary>
    internal (TrainingSpot Dummy, float T)? PickDummyAt(Vector2 pixel)
    {
        if (player.Ray(pixel) is not { } ray) return null;
        (TrainingSpot, float)? hit = null, near = null;
        float bestT = float.MaxValue, bestPixels = BedPickPixels;
        foreach (var d in player.session.CurrentSnapshot.Dummies)
        {
            if (player.build.buildHost?.Placement.Footprint(d.Record) is { } footprint)
            {
                var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, d.Yaw);
                if (player.BuildingRayT(ray, footprint.Place(d.Position, yaw), footprint.YMin, footprint.YMax, d.Record, d.Position, d.Position.Y, yaw) is { } t && t < bestT && AboveViewedFloor?.Invoke(d.Position) != true && !player.Hidden(ray, t))
                    (hit, bestT) = ((d, t), t);
                continue;
            }
            var middle = d.Position + new Vector3(0, 1, 0);
            if (player.Project(middle) is not { } at) continue;
            float gap = Vector2.Distance(at, pixel), distance = Vector3.Distance(ray.Origin, middle);
            if (gap < bestPixels && AboveViewedFloor?.Invoke(d.Position) != true && !player.Hidden(ray, distance)) (near, bestPixels) = ((d, distance), gap);
        }
        return hit ?? near;
    }

    (TrainingSpot Dummy, Vector2 At, long Since)? dummyPending;

    /// <summary>A right press on a dummy with characters selected: released before the hold time it trains, held longer it opens the menu (true: taken).</summary>
    internal bool DummyPress(Vector2 p)
    {
        if (player.SelectedIds().Count == 0 || PickDummyAt(p) is not { } dummy) return false;
        dummyPending = (dummy.Dummy, p, System.Diagnostics.Stopwatch.GetTimestamp());
        return true;
    }

    void Train(TrainingSpot dummy) => player.Say(player.SlowModifier
        ? new AddJob(player.SelectedIds(), TaskType.USE_TRAINING_DUMMY, dummy.Id) { Tick = player.session.World.Tick }
        : new OperateOrder(player.SelectedIds(), dummy.Id) { Tick = player.session.World.Tick });

    /// <summary>The right button let go: a press on a dummy released before the menu opened.</summary>
    internal void DummyRelease()
    {
        if (dummyPending is not { } pending) return;
        dummyPending = null;
        if (player.SelectedIds().Count > 0) Train(pending.Dummy);
    }

    /// <summary>Once a frame: a dummy press held long enough opens its menu.</summary>
    internal void UpdateDummy()
    {
        if (dummyPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < PlayerInterface.ContextHoldSeconds) return;
        dummyPending = null;
        if (player.SelectedIds().Count == 0) return;
        player.contextMenu = new(player.build.buildHost?.ItemName(pending.Dummy.Record) ?? pending.Dummy.Record, pending.At, [("Training", () => Train(pending.Dummy))]);
    }
    /// <summary>How near the pointer the projected middle of a seat without a footprint must be, in pixels (an engine choice).</summary>
    const float SeatPickPixels = 30;

    internal SeatSpot? PickSeat(Vector2 pixel) => PickSeatAt(pixel)?.Seat;

    /// <summary>
    /// The seat under the pixel with the ray distance, when not hidden behind a wall nor above the viewed floor of the building the player is in (<see cref="AboveViewedFloor"/>): the
    /// nearest the ray meets on its record's collision (hull prism, then triangles), as a building is picked; else the seat without a footprint whose middle (half a unit above its spot)
    /// projects nearest the pixel within <see cref="SeatPickPixels"/>. Until its collision is read (on workers) the magnet stands in.
    /// </summary>
    internal (SeatSpot Seat, float T)? PickSeatAt(Vector2 pixel)
    {
        if (player.Ray(pixel) is not { } ray) return null;
        (SeatSpot, float)? hit = null, near = null;
        float bestT = float.MaxValue, bestPixels = SeatPickPixels;
        foreach (var b in player.session.CurrentSnapshot.Seats)
        {
            if (player.build.buildHost?.Placement.Footprint(b.Record) is { } footprint)
            {
                var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, b.PlacementYaw);
                if (player.BuildingRayT(ray, footprint.Place(b.Position, yaw), footprint.YMin, footprint.YMax, b.Record, b.Position, b.Position.Y, yaw) is { } t && t < bestT && AboveViewedFloor?.Invoke(b.Position) != true && !player.Hidden(ray, t))
                    (hit, bestT) = ((b, t), t);
                continue;
            }
            // An invisible sitting node has no player-selectable shape; only visible seats get the pending-collision magnet.
            if (player.build.buildHost?.Parts(b.Record).Count == 0) continue;
            var middle = b.Position + new Vector3(0, 0.5f, 0);
            if (player.Project(middle) is not { } at) continue;
            float d = Vector2.Distance(at, pixel), distance = Vector3.Distance(ray.Origin, middle);
            if (d < bestPixels && AboveViewedFloor?.Invoke(b.Position) != true && !player.Hidden(ray, distance)) (near, bestPixels) = ((b, distance), d);
        }
        return hit ?? near;
    }

    (SeatSpot Seat, Vector2 At, long Since)? seatPending;

    /// <summary>A right press on a seat with characters selected: released before the hold time it gives the quick order, held longer it opens the menu (true: taken).</summary>
    internal bool SeatPress(Vector2 p)
    {
        if (player.SelectedIds().Count == 0 || PickSeat(p) is not { } seat) return false;
        seatPending = (seat, p, System.Diagnostics.Stopwatch.GetTimestamp());
        return true;
    }

    void SitOn(SeatSpot seat) => player.Say(player.SlowModifier
        ? (Meitou.Simulation.SimCommand)new AddJob(player.SelectedIds(), Meitou.Data.Gameplay.Ai.TaskType.OPERATE_MACHINERY, seat.Id) { Tick = player.session.World.Tick }
        : new OperateOrder(player.SelectedIds(), seat.Id) { Tick = player.session.World.Tick });
    internal void SeatRelease()
    {
        if (seatPending is not { } pending) return;
        seatPending = null;
        SitOn(pending.Seat);
    }
    internal void UpdateSeat()
    {
        if (seatPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < PlayerInterface.ContextHoldSeconds) return;
        seatPending = null;
        if (player.SelectedIds().Count == 0) return;
        var seat = pending.Seat;
        player.contextMenu = new(player.build.buildHost?.ItemName(seat.Record) ?? seat.Record, pending.At, [("Use", () => SitOn(seat))]);
    }

    // Mining rocks by hand (docs/simulation.md "Production", natural sites): a right click on a resource rock with characters selected has them operate it, with Shift it becomes a job
    // (Operating machine). A left click selects it; its machine window holds the mined ore, taken out by hand. Rocks are the foliage system's (Rocks); a rock is hit by its mesh's triangles, as drawn (engine choice: the original picks the rock
    // building's collision; without the mesh, a sphere of its radius).
    /// <summary>The world's resource rocks (null: none, e.g. headless or before the world is loaded).</summary>
    public Func<IResourceNodes?>? Rocks { get; set; }

    /// <summary>How far (X/Z) from the ground under the cursor a rock is looked for: a rock in front of the cursor's ground point is still hit.</summary>
    const float RockSearch = 200;

    /// <summary>The nearest rock the pixel's ray meets before the ground.</summary>
    internal ResourceNode? PickRock(Vector2 pixel) => PickRockAt(pixel)?.Rock;

    /// <summary>The nearest rock the pixel's ray meets before the ground, with the ray distance.</summary>
    internal (ResourceNode Rock, float T)? PickRockAt(Vector2 pixel)
    {
        if (Rocks?.Invoke() is not { } rocks || player.Ray(pixel) is not { } ray || player.GroundHit(ray.Origin, ray.Direction) is not { } ground) return null;
        float bestT = Vector3.Distance(ray.Origin, ground) + 1;
        ResourceNode? best = null;
        foreach (var rock in rocks.Near(0, ground.X, ground.Z, RockSearch))
        {
            if (rock.Record.Length == 0) continue;
            if (rock.RayHit(ray.Origin, ray.Direction) is { } t && t < bestT) (bestT, best) = (t, rock);
        }
        return best is { } found ? (found, bestT) : null;
    }

    /// <summary>A rock as a selected building: its site is the natural site, so the machine window shows its output once it has been mined.</summary>
    internal BuildingSelection.SelectedBuilding RockBuilding(ResourceNode rock) =>
        new(player.build.buildHost?.ItemName(rock.Record) ?? rock.Record, null, null, null, ProductionSystem.NaturalId(rock.Record, rock.Position), ProductionSystem.NaturalId(rock.Record, rock.Position),
            rock.Position, null, Record: rock.Record);

    void MineRock(IReadOnlyList<CharacterId> selection, ResourceNode rock, bool asJob)
    {
        string site = ProductionSystem.NaturalId(rock.Record, rock.Position);
        if (asJob) player.Say(new AddJob(selection, TaskType.OPERATE_MACHINERY, site) { Tick = player.session.World.Tick });
        else player.Say(new OperateOrder(selection, site) { Tick = player.session.World.Tick });
    }

    (ResourceNode Rock, Vector2 At, long Since)? rockPending;

    /// <summary>
    /// A right press on a rock with characters selected: released before the menu opens it mines (with Shift as a job), held longer it opens the menu (Mine, Add to jobs) as a site's does
    /// (true: taken).
    /// </summary>
    internal bool RockPress(Vector2 p)
    {
        if (player.SelectedIds().Count == 0 || PickRock(p) is not { } rock) return false;
        rockPending = (rock, p, System.Diagnostics.Stopwatch.GetTimestamp());
        return true;
    }

    /// <summary>The right button let go: a press on a rock released before the menu opened mines it.</summary>
    internal void RockRelease()
    {
        if (rockPending is not { } pending) return;
        rockPending = null;
        if (player.SelectedIds() is { Count: > 0 } selection) MineRock(selection, pending.Rock, player.SlowModifier);
    }

    /// <summary>Once a frame: a rock press held long enough opens its menu.</summary>
    internal void UpdateRock()
    {
        if (rockPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < PlayerInterface.ContextHoldSeconds) return;
        rockPending = null;
        if (player.SelectedIds() is not { Count: > 0 } selection) return;
        string name = player.build.buildHost?.ItemName(pending.Rock.Record) ?? pending.Rock.Record;
        player.contextMenu = new(name, pending.At,
        [
            ("Mine", () => MineRock(selection, pending.Rock, false)),
            ("Add to jobs", () => MineRock(selection, pending.Rock, true)),
        ]);
    }
}
