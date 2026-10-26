using System.Numerics;

namespace Meitou.Simulation.Items
{
    /// <summary>Pick up a ground item (<see cref="GroundItems"/> id): the nearest of the characters (the selection when empty) who may walks to it and puts it in its inventory.</summary>
    public sealed record PickUpItemOrder(IReadOnlyList<CharacterId> Characters, int Item) : SimCommand;
}

namespace Meitou.Simulation
{
    /// <summary>Picking up loose items (docs/simulation.md "Ground items"): walk to the item, then into the inventory (<see cref="Items.Inventory.Add"/>), or a message when it has no room.</summary>
    public sealed partial class PlayerSystem
    {
        /// <summary>A walker takes the item within this distance of it (an engine choice).</summary>
        public const float PickUpReach = 15;

        static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

        static void PickUpInput(World world, Items.PickUpItemOrder order)
        {
            if (world.GroundItems.Find(order.Item) is not { } item) return;
            var table = world.Characters;
            int best = -1;
            float bestDistance = float.MaxValue;
            foreach (var id in order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray())
            {
                if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, Inventory: not null, IsImprisoned: false } c || !c.CarriedBy.IsNone) continue;
                float d = Vector2.Distance(Flat(table.Next[s].Position), Flat(item.Position));
                if (d < bestDistance) (best, bestDistance) = (s, d);
            }
            if (best < 0) return;
            var walker = table.Cold(best)!;
            walker.PickingLock = null;
            walker.Shackles = null;
            walker.PickingUpItem = order.Item;
            if (bestDistance > PickUpReach) MovementSystem.GoToFloor(world, best, item.Position, walker.RunSpeed);
        }

        /// <summary>Each tick: a walker in reach takes its item; one whose item is gone, who was given another goal or stopped, or who is down gives up.</summary>
        void PickUpWorkers(World world)
        {
            var table = world.Characters;
            var next = table.Next;
            for (int s = 0; s < table.HighWater; s++)
            {
                if (!next[s].Alive || table.Cold(s) is not { PickingUpItem: { } id } c) continue;
                if (world.GroundItems.Find(id) is not { } item || c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone || c.Inventory is not { } inventory)
                {
                    c.PickingUpItem = null;
                    continue;
                }
                var at = Flat(item.Position);
                if (Vector2.Distance(Flat(next[s].Position), at) > PickUpReach)
                {
                    if (next[s].Task != (byte)CharacterTask.GoTo || Vector2.Distance(next[s].Goal, at) > PickUpReach) c.PickingUpItem = null;
                    continue;
                }
                c.PickingUpItem = null;
                if (next[s].Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, s);
                // An animal takes a limb in its mouth and eats it later (docs/game/character-stats.md "Eating").
                if (Items.FeedSystem.CanHold(c, item.Item))
                {
                    world.GroundItems.Take(id);
                    Items.FeedSystem.HoldInMouth(world, s, c, item.Item);
                    world.EmitSound(SoundKind.PickedUp, s, next[s].Position);
                    continue;
                }
                // Another faction's item is stolen from it (the "Steal" pick-up); what stays on the ground keeps its old mark.
                int before = item.Item.Quantity, mark = item.Item.StolenFrom;
                if (item.OwnedBy >= 0 && item.OwnedBy != c.Faction) item.Item.StolenFrom = item.OwnedBy;
                // What does not fit stays on the ground (Inventory.Add leaves it in the item); gear that does not fit is worn when its slot is free.
                bool wear = false;
                if (inventory.Add(item.Item) || item.Item.Quantity == before && (wear = Items.InventoryCommands.TryEquip(inventory, item.Item, c.Race)))
                {
                    world.GroundItems.Take(id);
                    // Task_Pickup: another faction's item taken inside a building is a Theft.
                    if (item.OwnedBy >= 0 && item.OwnedBy != c.Faction && world.Sight.BuildingOf(s) is not null) world.SystemOf<Crime.CrimeSystem>()?.StolePickUp(world, s, item.OwnedBy);
                    if (wear) assembly?.RefreshEquipment(c);
                    world.EmitSound(SoundKind.PickedUp, s, next[s].Position);
                }
                else
                {
                    item.Item.StolenFrom = mark;
                    // Task_Pickup sounds when any of the stack went in.
                    if (item.Item.Quantity < before) world.EmitSound(SoundKind.PickedUp, s, next[s].Position);
                    world.Post($"{c.Name}: Not enough room in my inventory");
                }
            }
        }
    }
}
