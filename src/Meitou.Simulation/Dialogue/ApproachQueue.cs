using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The act-out of order 102 (<c>Task_TalktoPlayer</c>, given by <c>DA_TALK_TO_LEADER</c>, <see cref="DialogueOrders"/>): the NPC walks up to its
/// target (the walk the row's <c>isAtLocation</c> puts before the task, re-aimed as the target moves on) and, in talk range while no conversation is
/// open, stops and fires event 1 at it; it then stands facing its target while its conversation lasts and is done when its dialogue is free. Serial
/// state of the <see cref="DialogueSystem"/>, which owns it.
/// </summary>
sealed class ApproachQueue(DialogueSystem dialogue)
{
    /// <summary>
    /// An NPC on order 102: its target, and whether it has fired its event (it talks).
    /// </summary>
    sealed class Approach(CharacterId npc, CharacterId target)
    {
        /// <summary>
        /// The NPC walking up.
        /// </summary>
        public CharacterId Npc { get; } = npc;

        /// <summary>
        /// The character it walks up to.
        /// </summary>
        public CharacterId Target { get; } = target;

        /// <summary>
        /// It has fired event 1 at its target.
        /// </summary>
        public bool Talks { get; set; }
    }

    /// <summary>
    /// The approaches in the order they began.
    /// </summary>
    readonly List<Approach> _approaches = [];

    /// <summary>
    /// The approaches under way.
    /// </summary>
    internal int Count => _approaches.Count;

    /// <summary>
    /// Whether <paramref name="npc"/> is walking up to someone or talks on its order 102.
    /// </summary>
    internal bool Has(CharacterId npc) => _approaches.Exists(a => a.Npc == npc);

    /// <summary>
    /// Each approaching NPC and its target (read only).
    /// </summary>
    internal IEnumerable<(CharacterId Npc, CharacterId Target)> Pairs => _approaches.Select(a => (a.Npc, a.Target));

    /// <summary>
    /// <paramref name="npc"/>'s order 102 starts on <paramref name="target"/> (a record of an earlier one goes).
    /// </summary>
    internal void Add(CharacterId npc, CharacterId target)
    {
        Drop(npc);
        _approaches.Add(new Approach(npc, target));
    }

    /// <summary>
    /// <paramref name="npc"/>'s record goes (its task ended).
    /// </summary>
    internal void Drop(CharacterId npc) => _approaches.RemoveAll(a => a.Npc == npc);

    /// <summary>
    /// Each approach's step (serial), on a copy: firing the event runs the line's effects, which may give orders.
    /// </summary>
    internal void Step(World world)
    {
        if (_approaches.Count == 0) return;

        foreach (var a in _approaches.ToArray()) StepOne(world, a);
    }

    /// <summary>
    /// One approach: dropped once its NPC no longer runs the order; a target gone or down ends the task; talking, it faces the target until its
    /// dialogue is free; walking, it fires its event in talk range while no conversation is open, else walks on (a walk with no way there ends the
    /// task).
    /// </summary>
    void StepOne(World world, Approach a)
    {
        var table = world.Characters;
        bool there = table.TryResolveNext(a.Npc, out int n);
        if (!there || !DialogueOrders.Runs(table, n, TaskType.SeekAndTalkAndSendSignal, a.Target))
        {
            Drop(a.Npc);
            if (there) DialogueOrders.LetGo(table, n);
            return;
        }

        if (a.Talks)
        {
            Converse(world, a, n);
            return;
        }

        if (!table.TryResolveNext(a.Target, out int t) || !DialogueSystem.Awake(table, t))
        {
            Finish(world, a, n, "dropped (the target is gone or down)");
            return;
        }

        if (!dialogue.InConversation && DialogueSystem.InTalkRange(world, n, t))
        {
            Fire(world, a, n);
            return;
        }

        var to = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
        if (!DialogueOrders.WalkTo(table, n, to, SpeedMode.Free)) Finish(world, a, n, "gives up (no way to the target)");
    }

    /// <summary>
    /// The task's start in talk range (<c>FUN_14034c5a0</c>): the NPC stops and fires event 1 from its own dialogue at its target (the player's
    /// conversation opens, the NPC turned to face its partner).
    /// </summary>
    void Fire(World world, Approach a, int n)
    {
        DialogueOrders.Halt(world.Characters, n);
        a.Talks = true;
        LogWalkUp(world, a, "arrived, fires event 1");
        dialogue.Start(world, a.Npc, a.Target, DialogueEvents.PlayerTalkToMe, quiet: true);
    }

    /// <summary>
    /// The task's update after the start (<c>FUN_14034c7c0</c>): the NPC faces its target each update and is done once its dialogue is free
    /// (<c>FUN_140671c60</c>); a target gone ends it too.
    /// </summary>
    void Converse(World world, Approach a, int n)
    {
        var table = world.Characters;
        if (dialogue.Free(a.Npc) ||!table.TryResolveNext(a.Target, out int t))
        {
            Finish(world, a, n, "done (its conversation is over)");
            return;
        }

        DialogueOrders.Face(table, n, table.Next[t].Position);
    }

    /// <summary>
    /// The order's task is done (the plan end pops the order): the record goes and the NPC stands.
    /// </summary>
    void Finish(World world, Approach a, int n, string why)
    {
        LogWalkUp(world, a, why);
        Drop(a.Npc);
        TaskStart.End(world, n, TaskEnd.Done);
    }

    /// <summary>
    /// A walk-up line in the dialogue log: the walker, the one it walks to, then <paramref name="what"/>.
    /// </summary>
    static void LogWalkUp(World world, Approach a, string what) =>
        world.DialogueLines.Line(world, "walkup", $"{EventLog.Who(world, a.Npc)} to {EventLog.Who(world, a.Target)}: {what}");

    /// <summary>
    /// Adds each approach's NPC, target and talk mark, in order.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        foreach (var a in _approaches)
        {
            hasher.Add(a.Npc);
            hasher.Add(a.Target);
            hasher.Add(a.Talks);
        }
    }
}
