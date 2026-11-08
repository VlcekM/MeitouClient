using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Locks;

/// <summary>How a fixture's lock is forced by the player's context-menu verbs: Use Tools (285, cut with a tool), Use Strength (286, break) and Smash (226, bash a door).</summary>
public enum LockForce : byte { Tools, Strength, Smash }

/// <summary>
/// Force the lock of a door or container (<see cref="Fixture.Id"/>): the nearest of the characters (the selection when empty) who may walks to it and cuts it (Task_CutLock, needs a tool),
/// breaks it by strength (Task_BruteForceLock) or, for a door, bashes it with its weapon (Task_BashDoor).
/// </summary>
public sealed record ForceLockOrder(IReadOnlyList<CharacterId> Characters, string Fixture, LockForce Force) : OrderCommand;

/// <summary>
/// The player's orders to cut, break or bash a lock (docs/game/locks.md "Cutting and breaking"): the same rules as the shackle versions, on a fixture's lock. The pending crime of every
/// update is the picking crime (<see cref="Lockpicking"/>); bashing reuses the NPC bashers (LockSystem.Bash.cs), without their need for an attack target.
/// </summary>
public sealed partial class LockSystem
{
    void ForceInput(World world, ForceLockOrder order)
    {
        if (world.Fixtures.Find(order.Fixture) is not { } fixture || fixture.Lock is not { EffectivelyLocked: true } || order.Force == LockForce.Smash && fixture.Kind != FixtureKind.Door) return;
        if (fixture.Kind == FixtureKind.Door && fixture.DoorBroken) return;
        var table = world.Characters;
        int best = -1;
        float bestDistance = float.MaxValue;
        foreach (var id in Who(world, order.Characters))
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, IsImprisoned: false } c || !c.CarriedBy.IsNone || !TaskStart.Eligible(world, c, RowOf(order.Force))) continue;
            float d = Vector2.Distance(Flat(table.Next[s].Position), Flat(fixture.Position));
            if (d < bestDistance) (best, bestDistance) = (s, d);
        }
        if (best < 0) return;
        var walker = table.Cold(best)!;
        switch (order.Force)
        {
            case LockForce.Tools when Lockpicking.BestTool(walker) is null:
                world.Post($"{walker.Name}: I don't have any tools");
                return;
            case LockForce.Smash:
                // Task_BashDoor needs a weapon (an animal's natural one counts); a door without a published spot cannot be struck.
                if (walker.Fighter is not { } fighter || !fighter.IsAnimal && fighter.Unarmed)
                {
                    world.Post($"{walker.Name}: I need a weapon to smash that");
                    return;
                }
                if (world.Fixtures.DoorSpotOf(fixture) is null || SmashRefused(walker, fixture)) return;
                break;
        }
        StartWork(world, best, CharacterTask.GoTo, RowOf(order.Force), fixture.Id, detail: order.Force, queued: order.Queued);
    }

    /// <summary>Row 226's own conditions (!70 isObjectMine, !128 isAlly): a door of the smasher's faction or of an ally's is never smashed on an order.</summary>
    bool SmashRefused(CharacterCold walker, Fixture door) =>
        door.Owner == walker.Faction || Combat?.Relations is { } r && r.Contains(walker.Faction) && r.Contains(door.Owner) && r.IsAlly(walker.Faction, door.Owner);

    /// <summary>Each tick: a character in reach of the lock it was ordered to force cuts or breaks it; one given another order, downed or gone gives up.</summary>
    void ForcedLocks(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { ForcingLock: { } id } c) continue;
            if (world.Fixtures.Find(id) is not { } f || f.Lock is not { EffectivelyLocked: true } || f.Kind == FixtureKind.Door && f.DoorBroken || c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone || c.IsImprisoned)
            {
                c.ForcingLock = null;
                continue;
            }
            var (at, reach) = WorkSpot(world, f, Flat(next[s].Position));
            if (Vector2.Distance(Flat(next[s].Position), at) > reach)
            {
                // Still walking; another order (a new goal) or a failed path ends it.
                if (Redirected(next[s], at, reach)) c.ForcingLock = null;
                continue;
            }
            if (next[s].Task == (byte)CharacterTask.GoTo) AtWork(world, s);
            if (c.Force == LockForce.Strength)
            {
                c.ForcingLock = null;
                if (Picking.Break(world, s, f, Crimes)) BruteForced(world, s, c, f.Kind == FixtureKind.Cage ? f.Occupant : CharacterId.None);
                else if (c.IsPlayer) Say(world, c, "I'm not strong enough to do that");
                continue;
            }
            if (Lockpicking.BestTool(c) is null) { c.ForcingLock = null; if (c.IsPlayer) Say(world, c, "I don't have any tools"); continue; }
            if (Picking.CutLock(world, s, f, world.TickSeconds, Crimes))
            {
                c.ForcingLock = null;
                world.EmitSound(SoundKind.Unlocked, s, next[s].Position);
                if (c.IsPlayer) Say(world, c, "Shackles broken!");
            }
        }
    }
}
