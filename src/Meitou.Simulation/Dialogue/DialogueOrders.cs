using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The talk orders, one-shot orders of the character's queue: the player's 12 Talking (<c>Task_PlayerTalkto</c> with the walk before it, the Talk
/// order, and Meitou's Trade order on it, <see cref="PlayerTalks"/>) and the dialogue's (docs/game/dialogue.md "Effects") 102 Making announcement
/// (<c>Task_TalktoPlayer</c> with the walk before it, DA 2, <see cref="ApproachQueue"/>), 164 Following and talking (DA 16, the movement's follow,
/// <see cref="TalkFollowers"/>) and 165 Stalking (DA 17, <see cref="TownStalkers"/>). It checks and starts them and acts out
/// <see cref="CharacterTask.Talk"/>; its records are the act-outs' state, each dropped once its character no longer runs the order. Serial phases.
/// </summary>
sealed class DialogueOrders(PlayerTalks talks, ApproachQueue approaches, TalkFollowers followers, TownStalkers stalkers) : IOrderOwner, ITaskHandler
{
    #region Orders

    /// <summary>
    /// The <see cref="OrderEntry.Detail"/> of a 12 that opens a trade on arrival instead of the conversation (Meitou's Trade order; saved as kind 1).
    /// </summary>
    internal static readonly object Trade = new();

    /// <summary>
    /// A talk order needs a target that is there and is not the character itself, and 12 one that can be talked to (alive, not out cold): else it
    /// leaves the queue. A 12 whose talker cannot walk (a prisoner: row 29 is not in its repertoire, so the plan has no walk to the target) waits
    /// blocked until the target is in talk range. The repertoire test is the queue's.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Target, out int t) || t == slot) return OrderCheck.Gone;
        if (order.Row != TaskType.PlayerTalkTo) return OrderCheck.Run;
        if (!DialogueSystem.Talkable(table, t)) return OrderCheck.Gone;

        bool walks = TaskStart.Eligible(world, table.Cold(slot)!, TaskType.MoveCusOrdered);
        return walks || DialogueSystem.InTalkRange(table, slot, t) ? OrderCheck.Run : OrderCheck.Blocked();
    }

    /// <summary>
    /// A running 12 whose talker cannot walk ends and waits blocked once its target is out of talk range (its plan fails again).
    /// </summary>
    bool IOrderOwner.StillHolds(World world, int slot, OrderEntry order) =>
        order.Row != TaskType.PlayerTalkTo || ((IOrderOwner)this).CanRun(world, slot, order).CanRun;

    /// <summary>
    /// Starts the order's task: 164 the follow of its target (the shared follow task, <c>Task_FollowAndTalk</c>), 12, 102 and 165 the talk act-out.
    /// A sleeper given a 12 gets up first, as for a move.
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var cold = world.Characters.Cold(slot)!;
        if (order.Row == TaskType.PlayerTalkTo && cold.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, world.Characters.IdOf(slot));

        if (order.Row != TaskType.FollowWhileTalking)
        {
            var talk = new TaskRequest(CharacterTask.Talk, order.Row, TaskSource.Order) { Target = order.Target, Detail = order.Detail };
            TaskStart.Start(world, slot, talk);
            return;
        }

        var follow = new TaskRequest(CharacterTask.Follow, order.Row, TaskSource.Order) { Target = order.Target };
        if (TaskStart.Start(world, slot, follow) == StartResult.Started) followers.Add(world.Characters.IdOf(slot), order.Target);
    }

    /// <summary>
    /// A Trade order is saved as kind 1, a Talk with none.
    /// </summary>
    int IOrderOwner.SaveKind(OrderEntry order) => order.Detail is null ? 0 : order.Detail == Trade ? 1 : -1;

    /// <summary>
    /// Kind 1 of a 12 is the Trade order.
    /// </summary>
    object? IOrderOwner.LoadDetail(World world, CharacterId self, TaskType row, int kind, CharacterId target) =>
        row == TaskType.PlayerTalkTo && kind == 1 ? Trade : null;

    /// <summary>
    /// The act-outs' steps (the dialogue's slow-world step): the player's talks, the walk-ups, the follows while talking and the stalkers.
    /// </summary>
    internal void Step(World world)
    {
        talks.Step(world);
        approaches.Step(world);
        followers.Step(world);
        stalkers.Step(world);
    }

    /// <summary>
    /// Whether the character at <paramref name="slot"/> still runs the talk order <paramref name="row"/> on <paramref name="target"/>: that order is
    /// started at its queue's front or in its second list (a loaded queue the chooser picked), and its task is the order's
    /// (<see cref="RunningTask.Runs"/>). A record for one that does not is dropped.
    /// </summary>
    internal static bool Runs(CharacterTable table, int slot, TaskType row, CharacterId target)
    {
        var cold = table.Cold(slot)!;
        bool front = cold.OrderQueue.Front is { Started: true } f && f.Row == row && f.Target == target;
        bool second = cold.SecondList.Exists(e => e.Started && e.Row == row && e.Target == target);
        bool task = cold.Running is { Source: TaskSource.Order } run && run.Row == row && run.Runs((CharacterTask)table.Next[slot].Task);
        return (front || second) && task;
    }

    /// <summary>
    /// A record dropped while its character still stands on the talk act-out (its order cleared by one that cannot start yet): the act-out stops,
    /// as the original's replan deletes the running task, so the character stands idle for its AI rather than on a task nothing steers.
    /// </summary>
    internal static void LetGo(CharacterTable table, int slot)
    {
        if (table.Next[slot].Task == (byte)CharacterTask.Talk) TaskStart.Stand(table, slot);
    }

    #endregion

    #region Talk act-out

    /// <summary>
    /// Starts the talk act-out of 12, 102 or 165: the character stops (the tasks' starts stop their own mover, <c>FUN_14034be70</c>,
    /// <c>FUN_14034c5a0</c>) and its record is made; its step walks it from there.
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
        else if (request.Row == TaskType.PlayerTalkTo) talks.Add(id, request.Target, request.Detail == Trade);
        else
            approaches.Add(id, request.Target);
    }

    /// <summary>
    /// The talk act-out ends: its record goes.
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var id = world.Characters.IdOf(slot);
        talks.Drop(id);
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
