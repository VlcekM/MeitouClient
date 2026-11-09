using System.Numerics;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Items;

/// <summary>
/// Open a container (<see cref="Locks.Fixture.Id"/>): the nearest of the characters (the selection when empty) who may walks to it and its window
/// opens. Another faction's locked one: with <paramref name="Alt"/> its lock is picked (76 PICK_LOCK), else the hint and a walk to it; a shop's
/// without Alt: the steal hint and a walk.
/// </summary>
public sealed record OpenContainerOrder(IReadOnlyList<CharacterId> Characters, string Fixture, bool Alt = false) : OrderCommand;

/// <summary>
/// Closes the open container's window.
/// </summary>
public sealed record CloseContainer : SimCommand;

/// <summary>
/// Opening a container of a building's interior (docs/game/locks.md "Containers"): a character walks up to it, its inventory shows in a window
/// beside the character's, and items are moved with <see cref="Production.PutInMachine"/> / <see cref="Production.TakeFromMachine"/> addressed by
/// the fixture id. <b>Engine choices</b>: the reach, a locked one refusing, a shop's containers staying the shop's (each its own rectangle of the
/// shop's stock, <see cref="Trading.Shop.ForBuilding"/>). Taking from another faction's is stealing (<see cref="Crime.CrimeSystem.Steal"/>).
/// </summary>
public static class ContainerAccess
{
    /// <summary>
    /// A character opens or uses a container within this distance of it (an engine choice).
    /// </summary>
    public const float ContainerReach = 20;

    /// <summary>
    /// The <see cref="TaskRequest.Detail"/> of a walk to a container that only walks there (the hint to hold Alt was shown).
    /// </summary>
    internal static readonly object ContainerWalk = new();

    /// <summary>
    /// How far above or below a container a character may stand to open or use it (engine choice: a storey is about 66).
    /// </summary>
    public const float ContainerFloorSlack = 20;

    /// <summary>
    /// How far (X/Z) a character may be from a container it puts into or takes from: the original's limit for an item moved out of a character's
    /// inventory (60; out of a building's, its bounds radius + 40, which a chest's is close to; docs/game/economy.md "Trading range").
    /// </summary>
    public const float TradingRange = 60;

    /// <summary>
    /// An <see cref="OpenContainerOrder"/>: the nearest character who may goes to the container with an order <paramref name="owner"/> gives.
    /// </summary>
    internal static void OpenContainerInput(World world, Items.OpenContainerOrder order, IOrderOwner owner)
    {
        if (world.Fixtures.Find(order.Fixture) is not { Contents: not null } fixture) return;
        var table = world.Characters;
        int best = -1;
        float bestDistance = float.MaxValue;
        bool theirs = fixture.Owner >= 0 && fixture.Owner != world.Player.Faction;
        bool locked = fixture.Lock is { EffectivelyLocked: true };

        // Another faction's locked container is picked (76) with Alt (the original's FUN_1407fc6e0); the rest is row 3, a walk to it.
        var row = locked && theirs && order.Alt ? Meitou.Data.Gameplay.Ai.TaskType.PickLock : Meitou.Data.Gameplay.Ai.TaskType.Pickup;
        foreach (var id in order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray())
        {
            if (!table.TryResolveNext(id, out int s)
                    || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, Inventory: not null, IsImprisoned: false } c
                            || !c.CarriedBy.IsNone
                || !TaskStart.Eligible(world, c, row)) continue;
            float d = Vector2.Distance(Flat(table.Next[s].Position), Flat(fixture.Position));
            if (d < bestDistance) (best, bestDistance) = (s, d);
        }
        if (best < 0) return;
        var walker = table.Cold(best)!;
        if (locked)
        {
            if (!theirs)
            {
                world.Post($"{walker.Name}: It's locked.");
                return;
            }

            // Without Alt: the hint and a walk to it.
            if (!order.Alt) world.Post("Hold down the ALT key to pick the lock.");
            PlayerOrders.Give(world, best, ContainerOrder(owner, row, fixture, walkOnly: !order.Alt), order.Queued);
            return;
        }
        if (ShopOf(world, fixture) is not null && !order.Alt && theirs)
        {
            // A shop's stock without Alt: the original's hint and a move; with Alt (the menu's Loot) the walker steals from it (the window shows
            // that container's part of the shop's stock).
            world.Post("Hold down the ALT key to steal items");
            PlayerOrders.Give(world, best, ContainerOrder(owner, row, fixture, walkOnly: true), order.Queued);
            return;
        }
        PlayerOrders.Give(world, best, ContainerOrder(owner, row, fixture, walkOnly: false), order.Queued);
    }

    /// <summary>
    /// The order to open (row 3), pick (76) or only walk to (<paramref name="walkOnly"/>) a container, given by <paramref name="owner"/>.
    /// </summary>
    static OrderEntry ContainerOrder(IOrderOwner owner, Meitou.Data.Gameplay.Ai.TaskType row, Locks.Fixture fixture, bool walkOnly) =>
        new(row, owner) { Site = fixture.Id, Point = fixture.Position, Detail = walkOnly ? ContainerWalk : null };

    /// <summary>
    /// The start of a container order: the walk to it, or, in reach on its floor, the opening where the character stands.
    /// </summary>
    internal static void StartContainer(World world, int slot, OrderEntry order)
    {
        var request = new TaskRequest(CharacterTask.GoTo, order.Row, TaskSource.Order) { Site = order.Site, Point = order.Point,
                Detail = order.Detail };
        var at = world.Characters.Next[slot].Position;
        bool walk = order.Row != Meitou.Data.Gameplay.Ai.TaskType.Pickup || order.Detail == ContainerWalk
            || Vector2.Distance(Flat(at), Flat(order.Point)) > ContainerReach || !SameFloor(at, order.Point);
        TaskStart.Start(world, slot, walk ? request : request with { Task = CharacterTask.Idle });
    }

    /// <summary>
    /// Each tick: the walker in reach opens its container; one given another order, downed or gone gives up. An open window stays until the
    /// player closes it; it closes when its opener is down or gone.
    /// </summary>
    internal static void ContainerWorkers(World world)
    {
        var player = world.Player;
        var table = world.Characters;
        if (player.OpeningContainer is { } wanted)
        {
            if (world.Fixtures.Find(wanted) is not { Contents: not null } fixture || !table.TryResolveNext(player.ContainerWalker, out int s)
                    || table.Cold(s) is not { Medical.Incapacitated: false } || !table.Cold(s)!.CarriedBy.IsNone)
                player.OpeningContainer = null;
            else
            {
                var at = Flat(fixture.Position);
                if (Vector2.Distance(Flat(table.Next[s].Position), at) <= ContainerReach && SameFloor(table.Next[s].Position, fixture.Position))
                {
                    if (table.Next[s].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, s);
                    (player.OpenContainer, player.ContainerOpener, player.OpeningContainer) = (wanted, player.ContainerWalker, null);
                }
                else if (table.Next[s].Task != (byte)CharacterTask.GoTo || Vector2.Distance(table.Next[s].Goal, at) > ContainerReach)
                    player.OpeningContainer = null;
            }
        }
        if (player.OpenContainer is { } open)
        {
            // The original has no walk-away test: the windows stay until the player closes them (docs/game/locks.md "Containers"); only an opener
            // who is down or gone closes it here.
            bool keep = world.Fixtures.Find(open) is { Contents: not null } && table.TryResolveNext(player.ContainerOpener, out int o)
                    && table.Cold(o) is { Medical.Incapacitated: false };
            if (!keep) player.OpenContainer = null;
        }
    }

    /// <summary>
    /// The building shop <paramref name="container"/> is a stock container of, if any.
    /// </summary>
    internal static Trading.Shop? ShopOf(World world, Locks.Fixture container) =>
        world.Squads.All.Select(s => s.Shop).FirstOrDefault(s => s?.Building == container.Building && s.RegionOf(container.Id, out _));

    /// <summary>
    /// The grid a container window works on: the container's own, but for a building shop's stock container the shop's stock
    /// (<see cref="Trading.Shop.ForBuilding"/>), of which the container's grid is <paramref name="region"/>: the window shows and takes only the
    /// items there (at cells relative to its corner), taking them is theft.
    /// </summary>
    internal static Items.Inventory GridOf(World world, Locks.Fixture container, out Items.GridRegion region)
    {
        if (ShopOf(world, container) is { } shop && shop.RegionOf(container.Id, out region)) return shop.Stock;
        region = new(0, 0, container.Contents!.Width, container.Contents.Height);
        return container.Contents;
    }

    /// <summary>
    /// The grid a container window works on (<see cref="GridOf(World, Locks.Fixture, out Items.GridRegion)"/>), without its region.
    /// </summary>
    internal static Items.Inventory GridOf(World world, Locks.Fixture container) => GridOf(world, container, out _);

    /// <summary>
    /// Whether two points are within <see cref="ContainerFloorSlack"/> of each other in height.
    /// </summary>
    static bool SameFloor(Vector3 a, Vector3 b) => MathF.Abs(a.Y - b.Y) <= ContainerFloorSlack;

    /// <summary>
    /// The container Take/Put address by <paramref name="site"/> (a fixture id) and the character, when it is open and the character is in reach;
    /// posts "Out of trading range." otherwise (<see cref="TradingRange"/>). Null when <paramref name="site"/> is not a container.
    /// </summary>
    internal static Locks.Fixture? ContainerFor(World world, string site, CharacterId who, out CharacterCold? cold)
    {
        cold = null;
        if (world.Fixtures.Find(site) is not { Contents: not null } f) return null;
        var table = world.Characters;
        if (!table.TryResolveNext(who, out int slot) || table.Cold(slot) is not { IsPlayer: true, Inventory: not null } c)
            return f;
        if (Vector2.Distance(Flat(table.Next[slot].Position), Flat(f.Position)) > TradingRange)
        {
            world.Post("Out of trading range.");
            return f;
        }
        cold = c;
        return f;
    }

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);
}
