using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>
/// Cages under the pointer (docs/game/ui-input.md "Context menu verbs", the cage rows; the cages are published as <see cref="WorldSnapshot.Cages"/>): a quick right click on an empty cage
/// picks a locked one's lock, puts the carried captive in a free one (108) or gets the character in (107) (with Shift held; else the cage's menu opens on the press). Engine choice: the "Cage" title.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>
    /// The cage under the pixel with the ray distance, when not hidden behind a wall: the nearest the ray meets on its record's collision, as a container is picked
    /// (<see cref="PickContainerAt"/>); one without collision, or above the shown floor of the building the player is in, cannot be picked.
    /// </summary>
    (CageSpot Cage, float T)? PickCageAt(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray || buildHost?.Placement is not { } placement) return null;
        var cages = session.CurrentSnapshot.Cages;
        (CageSpot, float)? hit = null;
        float bestT = float.MaxValue;
        foreach (var c in cages)
        {
            if (placement.Footprint(c.Record) is not { } footprint) continue;
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, c.Yaw);
            if (BuildingRayT(ray, footprint.Place(c.Position, yaw), footprint.YMin, footprint.YMax, c.Record, c.Position, c.Position.Y, yaw) is { } t && t < bestT && AboveViewedFloor?.Invoke(c.Position) != true && !Hidden(ray, t))
                (hit, bestT) = ((c, t), t);
        }
        return hit;
    }

    /// <summary>
    /// The verbs of a cage (the original's cage rows, <c>FUN_1407a6bc0</c>, <b>Verified</b>). Another faction's: locked, Pick Lock (76), Use Tools (285), Use Strength (286); unlocked, Put in
    /// (108) when a selected character carries someone, else Get in (107), or Set Free (110) when occupied. The player's own: Unlock (78) or Lock (77) by its lock, then Put in when empty and
    /// a selected character carries someone, Set Free when occupied (the release unlocks it).
    /// </summary>
    List<(string Caption, Action Give)> CageVerbs(CageSpot cage)
    {
        var verbs = new List<(string, Action)>();
        var selection = SelectedIds();
        if (selection.Count == 0) return verbs;
        bool occupied = !cage.Occupant.IsNone;
        if (cage.Owner == session.World.Player.Faction) OwnCageVerbs(verbs, cage, occupied);
        else OtherCageVerbs(verbs, cage, occupied, selection);
        return verbs;
    }

    /// <summary>The player's own cage: Unlock or Lock (a walk and 3.3 s of work, <see cref="DoorLockOrder"/>), then Put in when empty and carrying, Set Free when occupied.</summary>
    void OwnCageVerbs(List<(string, Action)> verbs, CageSpot cage, bool occupied)
    {
        bool locking = !cage.Locked;
        verbs.Add((locking ? "Lock" : "Unlock", () => Say(new DoorLockOrder([], cage.Id, locking) { Tick = session.World.Tick })));
        if (occupied)
        {
            SetFreeVerb(verbs, cage);
            return;
        }
        var carriers = Carriers();
        if (carriers.Length > 0) verbs.Add(("Put in", () => Say(new Meitou.Simulation.Crime.PutInCageOrder(carriers) { Cage = cage.Id, Tick = session.World.Tick })));
    }

    /// <summary>Another faction's cage: locked, Pick Lock, Use Tools and Use Strength; unlocked, Put in or Get in when empty, Set Free when occupied.</summary>
    void OtherCageVerbs(List<(string, Action)> verbs, CageSpot cage, bool occupied, IReadOnlyList<CharacterId> selection)
    {
        if (cage.Locked)
        {
            verbs.Add((PickLockCaption(cage.Position, cage.Level, !occupied), () => Say(new PickLockOrder(selection, cage.Occupant) { Cage = occupied ? null : cage.Id, Tick = session.World.Tick })));
            verbs.Add(("Use Tools", () => Say(new ForceLockOrder([], cage.Id, LockForce.Tools) { Tick = session.World.Tick })));
            verbs.Add(("Use Strength", () => Say(new ForceLockOrder([], cage.Id, LockForce.Strength) { Tick = session.World.Tick })));
            return;
        }
        if (occupied)
        {
            SetFreeVerb(verbs, cage);
            return;
        }
        var carriers = Carriers();
        if (carriers.Length > 0) verbs.Add(("Put in", () => Say(new Meitou.Simulation.Crime.PutInCageOrder(carriers) { Cage = cage.Id, Tick = session.World.Tick })));
        else verbs.Add(("Get in", () => Say(new GetInCageOrder([], cage.Id) { Tick = session.World.Tick })));
    }

    /// <summary>Set Free (110), not for a prisoner who is dead, out or carried (TARGET_IS_CONSCIOUS, the simulation refuses it with nothing said).</summary>
    void SetFreeVerb(List<(string, Action)> verbs, CageSpot cage)
    {
        var prisoner = session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == cage.Occupant);
        if (prisoner is null || prisoner.Conscious) verbs.Add(("Set Free", () => Say(new SetFreeOrder([], cage.Occupant) { Tick = session.World.Tick })));
    }

    /// <summary>
    /// A right press on a cage (true, with a selection): its menu opens at once, whoever owns it and whether it is occupied, unless Shift is held (<c>FUN_140800250</c>, <b>Verified</b>);
    /// with Shift, released quickly, the first of the cage's verbs while it is empty, held, its menu. An occupied cage with occupant selection is pointed at as its occupant instead
    /// (<see cref="PickWhereAt"/>).
    /// </summary>
    bool CageClick(Vector2 p)
    {
        if (SelectedIds().Count == 0 || PickCageAt(p)?.Cage is not { } cage) return false;
        var verbs = CageVerbs(cage);
        if (!pressShift && Menu(Loc.Tr("Cage"), p, verbs) is { } now) { contextMenu = now; return true; }
        // The quick order is the cage's use (108 / 107 / 76), never the own cage's Lock or Unlock.
        Action? quick = cage.Occupant.IsNone ? verbs.FirstOrDefault(v => v.Caption is not ("Lock" or "Unlock")).Give : null;
        return ContextPressOn(p, at => Menu(Loc.Tr("Cage"), at, CageVerbs(cage)), quick);
    }
}
