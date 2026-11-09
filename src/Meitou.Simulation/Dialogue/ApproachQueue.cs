using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The NPCs walking up to a player character to talk (the original's task 102 <c>Task_TalktoPlayer</c>, started by <c>DA_TALK_TO_LEADER</c>):
/// each walks to its target and opens the player's conversation in talk range, or gives up after 25 game minutes once the target is getting away.
/// Serial state of the <see cref="DialogueSystem"/>, which owns it.
/// </summary>
sealed class ApproachQueue(DialogueSystem dialogue)
{
    /// <summary>
    /// An NPC walking up to a player character to talk (the original's task 102 <c>Task_TalktoPlayer</c>), and the game hours it gives up at.
    /// </summary>
    sealed record Approach(CharacterId Npc, CharacterId Target, double GiveUpHours);

    /// <summary>
    /// The approaches in the order they began.
    /// </summary>
    readonly List<Approach> _approaches = [];

    /// <summary>
    /// Game hours an approach may take before the NPC gives up with event 60 (<c>Task_TalktoPlayer</c>: 25 game minutes, <b>Observed</b>).
    /// </summary>
    const double ApproachGiveUpHours = 25.0 / 60;

    /// <summary>
    /// EV_GIVE_UP_CHASE.
    /// </summary>
    const int GiveUpChase = 60;

    /// <summary>
    /// The approaches under way.
    /// </summary>
    internal int Count => _approaches.Count;

    /// <summary>
    /// Whether <paramref name="npc"/> is already walking up to someone.
    /// </summary>
    internal bool Has(CharacterId npc) => _approaches.Any(a => a.Npc == npc);

    /// <summary>
    /// <paramref name="npc"/> starts walking up to <paramref name="target"/>; it gives up after <see cref="ApproachGiveUpHours"/>.
    /// </summary>
    internal void Add(World world, CharacterId npc, CharacterId target) =>
        _approaches.Add(new Approach(npc, target, world.GameHours + ApproachGiveUpHours));

    /// <summary>
    /// The NPC approaches (serial): it walks to the target and, in talk range (<see cref="DialogueSystem.InTalkRange"/>) while no conversation is
    /// open, fires event 1 at it (the player's conversation window opens with it); after 25 game minutes it gives up (plan dropped, event 60 at the
    /// target) once the target is getting away, else it walks on; a target or NPC gone or down ends it.
    /// </summary>
    internal void Step(World world)
    {
        var table = world.Characters;
        for (int k = 0; k < _approaches.Count; k++)
        {
            var a = _approaches[k];
            int n = -1, t = -1;
            bool done = !table.TryResolveNext(a.Npc, out n) || !DialogueSystem.Awake(table, n)
                || !table.TryResolveNext(a.Target, out t) || !DialogueSystem.Awake(table, t);
            if (!done && !dialogue.InConversation && DialogueSystem.InTalkRange(table, n, t))
            {
                TaskStart.Stand(table, n);
                _approaches.RemoveAt(k--);
                dialogue.Start(world, a.Npc, a.Target, DialogueEvents.PlayerTalkToMe, quiet: true);
                if (!dialogue.TalksWith(a.Npc)) DialogueSystem.Release(world, a.Npc);
                continue;
            }

            if (!done && world.GameHours >= a.GiveUpHours && GettingAway(table, n, t))
            {
                GiveUp(world, table, a, n);
                done = true;
            }

            if (done)
            {
                _approaches.RemoveAt(k--);
                DialogueSystem.Release(world, a.Npc);
                continue;
            }

            WalkOn(world, table, n, t);
        }
    }

    /// <summary>
    /// Walking: towards where the target stands, again when it has moved on.
    /// </summary>
    static void WalkOn(World world, CharacterTable table, int n, int t)
    {
        var to = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
        ref var nh = ref table.Next[n];
        table.Cold(n)!.Engaged = true;

        // Its walk would end the fight it is in (a new task drops the fight task).
        if (table.Cold(n)!.InCombat) return;
        if (nh.Task != (byte)CharacterTask.GoTo || Vector2.Distance(nh.Goal, to) > DialogueSystem.TalkReach)
        {
            TaskStart.Walk(world, n, to, SpeedMode.Free, Data.Gameplay.Ai.TaskType.TalktoNearestPlayerCharacter, TaskSource.Npc);
        }
    }

    /// <summary>
    /// The NPC drops its plan and the approach, and event 60 fires at the target.
    /// </summary>
    static void GiveUp(World world, CharacterTable table, Approach a, int n)
    {
        if (table.Cold(n)?.Ai is { } mind)
        {
            mind.Plan.Clear();
            mind.Goal = null;
            mind.Version = -1;
        }

        TaskStart.Stand(table, n);
        world.Defer(new DialogueEvent(a.Npc, a.Target, GiveUpChase) { Tick = world.Tick + 1 });
    }

    /// <summary>
    /// Whether the target is getting away from the NPC (<c>FUN_140331ee0</c>): it runs away (task 35 or 127), or it walks to a move goal farther from
    /// the NPC than it stands.
    /// </summary>
    static bool GettingAway(CharacterTable table, int npc, int target)
    {
        var tc = table.Cold(target);
        if (tc is { Fleeing: true } || tc?.Ai?.Step is Data.Gameplay.Ai.TaskType.RunAway or Data.Gameplay.Ai.TaskType.RunAwayHometown) return true;
        ref readonly var tn = ref table.Next[target];
        if (tn.Task != (byte)CharacterTask.GoTo || tn.Goal == Vector2.Zero) return false;
        var me = new Vector2(table.Next[npc].Position.X, table.Next[npc].Position.Z);
        return Vector2.DistanceSquared(tn.Goal, me) > Vector2.DistanceSquared(new Vector2(tn.Position.X, tn.Position.Z), me);
    }

    /// <summary>
    /// Adds each approach's NPC, target and give-up time, in order.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        foreach (var a in _approaches)
        {
            hasher.Add(a.Npc);
            hasher.Add(a.Target);
            hasher.Add(a.GiveUpHours);
        }
    }
}
