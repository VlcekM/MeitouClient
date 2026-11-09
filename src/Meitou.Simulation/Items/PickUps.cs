using System.Numerics;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Items;

/// <summary>
/// Pick up a ground item (<see cref="GroundItems"/> id): the nearest of the characters (the selection when empty) who may walks to it and puts it
/// in its inventory.
/// </summary>
public sealed record PickUpItemOrder(IReadOnlyList<CharacterId> Characters, int Item) : OrderCommand
{
    /// <summary>
    /// Alt was held (the original's steal modifier, <c>DAT_142133462</c>): another faction's item is taken only then; without it the order is the
    /// hint "Hold down the ALT key to steal items" and a walk to the item (<c>FUN_1407fc440</c>).
    /// </summary>
    public bool Alt { get; init; } = true;
}

/// <summary>
/// Picking up loose items (docs/simulation.md "Ground items"): walk to the item, then into the inventory (<see cref="Items.Inventory.Add"/>), or
/// a message when it has no room. The pick-up and the walk to a container start through <see cref="TaskStart.Start"/> as row 3 "Collecting item",
/// which this handles (the player system registers it, <see cref="PlayerSystem.RegisterTasks"/>).
/// </summary>
public sealed class PickUps : ITaskHandler, IOrderOwner
{
    /// <summary>
    /// A pick-up of the ground item in <see cref="TaskRequest.Detail"/>, or the walk to the container <see cref="TaskRequest.Site"/> (to open it,
    /// or only to it: <see cref="ContainerAccess.ContainerWalk"/>).
    /// </summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        var table = world.Characters;
        var walker = table.Cold(slot)!;
        if (request.Detail is int item)
        {
            walker.PickingLock = null;
            walker.Shackles = null;
            walker.PickingUpItem = item;
            if (request.Task == CharacterTask.GoTo)
                MovementSystem.GoToFloor(world, slot, request.Point, walker.RunSpeed);
            return;
        }
        if (request.Detail == ContainerAccess.ContainerWalk)
        {
            MovementSystem.GoToFloor(world, slot, request.Point, walker.RunSpeed);
            return;
        }
        var player = world.Player;
        walker.PickingLock = null;
        walker.Shackles = null;
        walker.PickingUpItem = null;
        player.OpenContainer = null;
        player.OpeningContainer = request.Site;
        player.ContainerWalker = table.IdOf(slot);
        if (request.Task == CharacterTask.GoTo)
            MovementSystem.GoToFloor(world, slot, request.Point, walker.RunSpeed);
    }

    /// <summary>
    /// A pick-up or a walk to a container that another task replaces or the Stop order ends is given up.
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var table = world.Characters;
        table.Cold(slot)!.PickingUpItem = null;
        if (world.Player.OpeningContainer is not null && world.Player.ContainerWalker == table.IdOf(slot))
            world.Player.OpeningContainer = null;
    }

    /// <summary>
    /// The act-out a pick-up or a container's opening works in where the character stands (<see cref="CharacterTask.Idle"/>) is done once the
    /// item is taken or the container opened.
    /// </summary>
    bool ITaskHandler.Done(World world, int slot, CharacterTask task)
    {
        var table = world.Characters;
        if (table.Next[slot].Task != (byte)task) return true;
        return task == CharacterTask.Idle && table.Cold(slot)!.PickingUpItem is null
                && !(world.Player.OpeningContainer is not null && world.Player.ContainerWalker == table.IdOf(slot));
    }

    /// <summary>
    /// A walker takes the item within this distance of it (an engine choice).
    /// </summary>
    public const float PickUpReach = 15;

    /// <summary>
    /// The player's animals take a pick-up order on a loose item into their bags (the Advanced setting "Animals pick up items", off by default).
    /// The original's row 3 registers animals and its pick-up puts any item but a limb into the inventory (docs/game/character-stats.md "Mouth"),
    /// so off is a Meitou choice; a limb for an animal's mouth is always taken.
    /// </summary>
    public bool AnimalsPickUp { get; set; }

    /// <summary>
    /// Whether <paramref name="c"/> may take a pick-up order on <paramref name="item"/>: anyone but an animal, which only with
    /// <see cref="AnimalsPickUp"/> or a limb for its mouth.
    /// </summary>
    bool MayPickUp(CharacterCold c, Items.ItemInstance item) => AnimalsPickUp || !SquadRegistry.IsAnimal(c)
            || Items.FeedSystem.IsMouthPickUp(c, item);

    internal void PickUpInput(World world, Items.PickUpItemOrder order)
    {
        if (world.GroundItems.Find(order.Item) is not { } item) return;
        var table = world.Characters;
        bool walkOnly = !order.Alt && item.OwnedBy >= 0 && item.OwnedBy != world.Player.Faction;
        if (walkOnly) world.Post("Hold down the ALT key to steal items");
        int best = -1;
        float bestDistance = float.MaxValue;
        foreach (var id in order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray())
        {
            if (!table.TryResolveNext(id, out int s)
                    || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, Inventory: not null, IsImprisoned: false } c
                            || !c.CarriedBy.IsNone
                || !TaskStart.Eligible(world, c, Meitou.Data.Gameplay.Ai.TaskType.Pickup) || !MayPickUp(c, item.Item)) continue;
            float d = Vector2.Distance(Flat(table.Next[s].Position), Flat(item.Position));
            if (d < bestDistance) (best, bestDistance) = (s, d);
        }
        if (best < 0) return;
        if (walkOnly && world.SystemOf<MovementSystem>() is { } movement)
            PlayerOrders.Give(world, best, new OrderEntry(Meitou.Data.Gameplay.Ai.TaskType.MoveCusOrdered, movement) { Point = item.Position },
                    order.Queued);
        else if (!walkOnly)
            PlayerOrders.Give(world, best,
                    new OrderEntry(Meitou.Data.Gameplay.Ai.TaskType.Pickup, this) { Point = item.Position, Detail = order.Item }, order.Queued);
    }

    /// <summary>
    /// Collecting an item (row 3) or opening, picking or walking to a container: the item or container is still there (else the order leaves the
    /// queue), and a player character that is awake, not caged or carried and has an inventory takes it.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var c = world.Characters.Cold(slot)!;
        if (!c.IsPlayer
                || (order.Detail is int item ? world.GroundItems.Find(item) is null
                        : world.Fixtures.Find(order.Site!) is not { Contents: not null }))
            return OrderCheck.Gone;
        return c is { Medical.Incapacitated: false, Inventory: not null, IsImprisoned: false } && c.CarriedBy.IsNone ? OrderCheck.Run
                : OrderCheck.Blocked();
    }

    /// <summary>
    /// A pick-up walks to the item, or within reach takes it where the character stands; a container order walks to it or opens it
    /// (<see cref="ContainerAccess.StartContainer"/>).
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        if (order.Detail is not int item)
        {
            ContainerAccess.StartContainer(world, slot, order);
            return;
        }
        bool far = Vector2.Distance(Flat(world.Characters.Next[slot].Position), Flat(order.Point)) > PickUpReach;
        TaskStart.Start(world, slot,
                new TaskRequest(far ? CharacterTask.GoTo : CharacterTask.Idle, Meitou.Data.Gameplay.Ai.TaskType.Pickup,
                        TaskSource.Order) { Point = order.Point, Detail = item });
    }

    // Saved: a walk to a container (kind 1 only walks there; 0 opens it); a pick-up of a ground item names a runtime id and is not saved.
    int IOrderOwner.SaveKind(OrderEntry order) => order.Detail is null ? 0 : order.Detail == ContainerAccess.ContainerWalk ? 1 : -1;
    object? IOrderOwner.LoadDetail(World world, CharacterId self, Meitou.Data.Gameplay.Ai.TaskType row, int kind, CharacterId target) => kind == 1
            ? ContainerAccess.ContainerWalk : null;

    /// <summary>
    /// Each tick: a walker in reach takes its item; one whose item is gone, who was given another goal or stopped, or who is down gives up. Gear
    /// worn from the ground re-dresses through <paramref name="assembly"/> (null: the looks stay).
    /// </summary>
    internal static void PickUpWorkers(World world, CharacterAssembly? assembly)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { PickingUpItem: { } id } c) continue;
            if (world.GroundItems.Find(id) is not { } item || c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone
                    || c.Inventory is not { } inventory)
            {
                c.PickingUpItem = null;
                continue;
            }
            var at = Flat(item.Position);
            if (Vector2.Distance(Flat(next[s].Position), at) > PickUpReach)
            {
                if (next[s].Task != (byte)CharacterTask.GoTo || Vector2.Distance(next[s].Goal, at) > PickUpReach)
                    c.PickingUpItem = null;
                continue;
            }
            c.PickingUpItem = null;
            if (next[s].Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, s);

            // An animal takes a limb in its mouth and eats it later; one with no mouth point leaves it on the ground
            // (docs/game/character-stats.md "Eating", "Mouth").
            if (Items.FeedSystem.IsMouthPickUp(c, item.Item))
            {
                TakeInMouth(world, s, c, item);
                continue;
            }

            // Another faction's item is stolen from it (the "Steal" pick-up); what stays on the ground keeps its old mark.
            int before = item.Item.Quantity;
            var mark = item.Item.Mark;
            if (item.OwnedBy >= 0 && item.OwnedBy != c.Faction) item.Item.StolenFrom = item.OwnedBy;

            // What does not fit stays on the ground (Inventory.Add leaves it in the item); gear or a bag that does not fit is worn when its slot
            // is free.
            bool wear = false;
            if (Stow(inventory, item.Item, c) || item.Item.Quantity == before
                && (wear = Items.InventoryCommands.TryEquip(inventory, item.Item, c)
                        || Items.InventoryCommands.TryWearBag(inventory, item.Item, c)))
            {
                world.GroundItems.Take(id);

                // Task_Pickup: another faction's item taken inside a building is a Theft.
                if (item.OwnedBy >= 0 && item.OwnedBy != c.Faction && world.Sight.BuildingOf(s) is not null)
                    world.SystemOf<Crime.CrimeSystem>()?.StolePickUp(world, s, item.OwnedBy);
                if (wear) assembly?.RefreshEquipment(c);
                world.EmitSound(SoundKind.PickedUp, s, next[s].Position);
            }
            else
            {
                item.Item.Mark = mark;

                // Task_Pickup sounds when any of the stack went in.
                if (item.Item.Quantity < before) world.EmitSound(SoundKind.PickedUp, s, next[s].Position);
                world.Post($"{c.Name}: Not enough room in my inventory");
            }
        }
    }

    /// <summary>
    /// A picked-up item into the grids (<see cref="Items.Inventory.Add"/>); a live animal's shut grid
    /// (<see cref="Items.InventoryCommands.MainClosed"/>) leaves only its bag.
    /// </summary>
    static bool Stow(Items.Inventory inventory, Items.ItemInstance item, CharacterCold c) =>
        !Items.InventoryCommands.MainClosed(c) ? inventory.Add(item) : inventory.Backpack?.Contents is { } bag
                && bag.AddTo("backpack_content", item);

    /// <summary>
    /// An animal's pick-up of a limb: into its mouth when it has the point, else nothing (no sound or message, the limb stays where it lies).
    /// </summary>
    static void TakeInMouth(World world, int slot, CharacterCold cold, Items.GroundItem item)
    {
        if (!Items.FeedSystem.HasMouth(cold)) return;
        world.GroundItems.Take(item.Id);
        Items.FeedSystem.HoldInMouth(world, slot, cold, item.Item, item.Fall?.SkinTone ?? default);
        world.EmitSound(SoundKind.PickedUp, slot, world.Characters.Next[slot].Position);
    }

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);
}
