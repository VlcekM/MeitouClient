using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The dialogue's talk orders (docs/game/dialogue.md "Effects"), one-shot orders of the character's queue: 102 Making announcement
/// (<c>Task_TalktoPlayer</c> with the walk before it, DA 2, <see cref="ApproachQueue"/>), 164 Following and talking (DA 16, the movement's follow,
/// <see cref="TalkFollowers"/>) and 165 Stalking (DA 17, <see cref="TownStalkers"/>). It checks and starts them and acts out
/// <see cref="CharacterTask.Talk"/>; its records are the act-outs' state, each dropped once its character no longer runs the order. Serial phases.
/// </summary>
sealed class DialogueOrders(ApproachQueue approaches, TalkFollowers followers, TownStalkers stalkers) : IOrderOwner, ITaskHandler
{
    #region Orders

    /// <summary>
    /// A talk order needs a target that is there and is not the character itself (else it leaves the queue); the repertoire test is the queue's.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order) =>
        world.Characters.TryResolveNext(order.Target, out int t) && t != slot ? OrderCheck.Run : OrderCheck.Gone;

    /// <summary>
    /// Starts the order's task: 164 the follow of its target (the shared follow task, <c>Task_FollowAndTalk</c>), 102 and 165 the talk act-out.
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        if (order.Row != TaskType.FollowWhileTalking)
        {
            TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Talk, order.Row, TaskSource.Order) { Target = order.Target });
            return;
        }

        var follow = new TaskRequest(CharacterTask.Follow, order.Row, TaskSource.Order) { Target = order.Target };
        if (TaskStart.Start(world, slot, follow) == StartResult.Started) followers.Add(world.Characters.IdOf(slot), order.Target);
    }

    /// <summary>
    /// The act-outs' steps (the dialogue's slow-world step): the walk-ups, the follows while talking and the stalkers.
    /// </summary>
    internal void Step(World world)
    {
        approaches.Step(world);
        followers.Step(world);
        stalkers.Step(world);
    }

    /// <summary>
    /// Whether the character at <paramref name="slot"/> still runs the talk order <paramref name="row"/> on <paramref name="target"/>: its queue's
    /// front is that order, started, and its task the order's (<see cref="RunningTask.Runs"/>). A record for one that does not is dropped.
    /// </summary>
    internal static bool Runs(CharacterTable table, int slot, TaskType row, CharacterId target)
    {
        var cold = table.Cold(slot)!;
        bool front = cold.OrderQueue.Front is { Started: true } f && f.Row == row && f.Target == target;
        return front && cold.Running is { Source: TaskSource.Order } run && run.Row == row && run.Runs((CharacterTask)table.Next[slot].Task);
    }

    #endregion

    #region Talk act-out

    /// <summary>
    /// Starts the talk act-out of 102 or 165: the character stops (the task's start stops its own mover, <c>FUN_14034c5a0</c>) and its record is
    /// made; its step walks it from there.
    /// </summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        var table = world.Characters;
        MovementSystem.Stop(table, slot);
        table.Next[slot].Task = (byte)CharacterTask.Talk;
        table.Next[slot].TaskTime = 0;
        table.Cold(slot)!.PathFailed = false;   // a failure from before is not this walk's

        var id = table.IdOf(slot);
        if (request.Row == TaskType.TownStalker) stalkers.Add(id, request.Target);
        else
            approaches.Add(id, request.Target);
    }

    /// <summary>
    /// The talk act-out ends: its record goes.
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var id = world.Characters.IdOf(slot);
        approaches.Drop(id);
        stalkers.Drop(id);
    }

    /// <summary>
    /// The act-out's walk toward <paramref name="goal"/> on its own task (<see cref="MovementSystem.Aim"/>): asked anew when the walker has no way
    /// there or the goal has moved more than <see cref="DialogueSystem.TalkReach"/> from the one it walks to. False when the last way there was not
    /// found: the walk fails.
    /// </summary>
    internal static bool WalkTo(CharacterTable table, int slot, Vector2 goal, byte mode)
    {
        ref var n = ref table.Next[slot];
        var cold = table.Cold(slot)!;
        if (cold.PathFailed) return false;

        bool walking = (n.Flags & (ushort)MoveFlags.AnyPath) != 0;
        if (walking && Vector2.Distance(n.Goal, goal) <= DialogueSystem.TalkReach) return true;

        MovementSystem.Aim(table, slot, goal, mode);
        return true;
    }

    /// <summary>
    /// The act-out stands where it is, on its own task: its way dropped.
    /// </summary>
    internal static void Halt(CharacterTable table, int slot)
    {
        ref var n = ref table.Next[slot];
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
        n.PathCursor = 0;
        table.Cold(slot)!.PathRequest = 0;
    }

    /// <summary>
    /// Turns the character at <paramref name="slot"/> to face <paramref name="at"/> (the mover's facing call, slot <c>0x38</c>); unchanged on one
    /// spot.
    /// </summary>
    internal static void Face(CharacterTable table, int slot, Vector3 at)
    {
        ref var n = ref table.Next[slot];
        float dx = at.X - n.Position.X, dz = at.Z - n.Position.Z;
        if (dx * dx + dz * dz > 1e-4f) n.Yaw = MathF.Atan2(dx, dz);
    }

    #endregion
}
