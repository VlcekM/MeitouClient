using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The act-out of order 12 Talking (<c>Task_PlayerTalkto</c>, <see cref="DialogueOrders"/>): the talker walks up to its target (the walk the row's
/// <c>isAtLocation</c> puts before the task, re-aimed as the target moves on) and, in talk range while no conversation is open, its queue and second
/// list are emptied and its task ends (the task's update, <c>FUN_14034bfc0</c>), and event 1 is fired at the target (the task's start,
/// <c>FUN_14034be70</c>), or, for Meitou's Trade order (<see cref="DialogueOrders.Trade"/>), the trade opens. Serial state of the
/// <see cref="DialogueSystem"/>, which owns it.
/// </summary>
sealed class PlayerTalks(DialogueSystem dialogue)
{
    /// <summary>
    /// A character on order 12: its target, and whether the order opens a trade instead of the conversation.
    /// </summary>
    sealed record Talk(CharacterId Talker, CharacterId Target, bool Trade);

    /// <summary>
    /// The walks in the order they began.
    /// </summary>
    readonly List<Talk> _talks = [];

    /// <summary>
    /// The walks under way.
    /// </summary>
    internal int Count => _talks.Count;

    /// <summary>
    /// Each talker and its target (read only).
    /// </summary>
    internal IEnumerable<(CharacterId Talker, CharacterId Target)> Pairs => _talks.Select(t => (t.Talker, t.Target));

    /// <summary>
    /// <paramref name="talker"/>'s order 12 starts on <paramref name="target"/> (a record of an earlier one goes).
    /// </summary>
    internal void Add(CharacterId talker, CharacterId target, bool trade)
    {
        Drop(talker);
        _talks.Add(new Talk(talker, target, trade));
    }

    /// <summary>
    /// <paramref name="talker"/>'s record goes (its task ended).
    /// </summary>
    internal void Drop(CharacterId talker) => _talks.RemoveAll(t => t.Talker == talker);

    /// <summary>
    /// Each walk's step (serial), on a copy: an arrival opens a conversation or a trade.
    /// </summary>
    internal void Step(World world)
    {
        if (_talks.Count == 0) return;

        foreach (var t in _talks.ToArray()) StepOne(world, t);
    }

    /// <summary>
    /// One walk: dropped once its talker no longer runs the order; a target gone or down ends the task; in talk range while no conversation is open
    /// it arrives, else walks on (a walk with no way there ends the task).
    /// </summary>
    void StepOne(World world, Talk talk)
    {
        var table = world.Characters;
        bool there = table.TryResolveNext(talk.Talker, out int p);
        if (!there || !DialogueOrders.Runs(table, p, TaskType.PlayerTalkTo, talk.Target))
        {
            Drop(talk.Talker);
            if (there) DialogueOrders.LetGo(table, p);
            return;
        }

        if (!table.TryResolveNext(talk.Target, out int n) || !DialogueSystem.Talkable(table, n))
        {
            Finish(world, talk, p);
            return;
        }

        if (dialogue.InConversation) return;

        if (DialogueSystem.InTalkRange(world, p, n))
        {
            Arrive(world, talk, p);
            return;
        }

        if (!DialogueOrders.WalkTo(table, p, table.Next[n].Position, table.Cold(p)!.RunSpeed)) Finish(world, talk, p);
    }

    /// <summary>
    /// In talk range: the talker stops, its order queue and second list are emptied and its task ends (the update's <c>FUN_1405074a0</c> and
    /// ended flag, docs/game/ai-tasks.md "What clears the queue"); then the conversation opens with event 1 at the target, or the trade opens.
    /// </summary>
    void Arrive(World world, Talk talk, int p)
    {
        var cold = world.Characters.Cold(p)!;
        DialogueOrders.Halt(world.Characters, p);
        cold.OrderQueue.Clear();
        PlayerOrders.ClearSecond(cold);
        Finish(world, talk, p);

        // A trader whose dialogue would not trade with the talker says its line instead (#1506).
        bool trades = talk.Trade && (!world.Characters.TryResolveNext(talk.Target, out int t) || dialogue.OpensTrade(world, t, talk.Talker));
        if (trades) world.SystemOf<Trading.TradeSystem>()?.OpenAt(world, talk.Talker, talk.Target);
        else
            dialogue.Start(world, talk.Target, talk.Talker, DialogueEvents.PlayerTalkToMe, playerStarted: true);
    }

    /// <summary>
    /// The order's task is done: the record goes and the talker stands (the order leaves its queue, when still there).
    /// </summary>
    void Finish(World world, Talk talk, int p)
    {
        Drop(talk.Talker);
        TaskStart.End(world, p, TaskEnd.Done);
    }

    /// <summary>
    /// Adds each walk's talker, target and trade mark, in order.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        foreach (var t in _talks)
        {
            hasher.Add(t.Talker);
            hasher.Add(t.Target);
            hasher.Add(t.Trade);
        }
    }
}
