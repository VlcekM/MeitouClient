using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>An NPC walking up to a player character to talk (the original's task 102 <c>Task_TalktoPlayer</c>), and the game hours it gives up at.</summary>
    sealed record Approach(CharacterId Npc, CharacterId Target, double GiveUpHours);

    readonly List<Approach> _approaches = [];

    /// <summary>Game hours an approach may take before the NPC gives up with event 60 (<c>Task_TalktoPlayer</c>: 25 game minutes, <b>Observed</b>).</summary>
    const double ApproachGiveUpHours = 25.0 / 60;
    /// <summary>EV_GIVE_UP_CHASE.</summary>
    const int GiveUpChase = 60;

    /// <summary>
    /// <c>DA_TALK_TO_LEADER</c> (2, <c>FUN_140680560</c>): for a player-faction target, the speaker squad's leader gets task 102 on it with event 1, so it walks up and opens a
    /// conversation. Nothing when the leader is already in the open player conversation (its line would send it again); a bubble speaker that leads still walks up.
    /// </summary>
    void TalkToLeader(World world, Conversation c, CharacterCold mc, CharacterId target, CharacterCold tc)
    {
        if (tc.Faction != world.Player.Faction && !tc.IsPlayer || mc.SquadId < 0 || world.Squads.Find(mc.SquadId) is not { Leader: { IsNone: false } leader }) return;
        if (_open?.Npc == leader || _approaches.Any(a => a.Npc == leader)) return;
        if (!world.Characters.TryResolveNext(leader, out int l) || !Awake(world.Characters, l)) return;
        _approaches.Add(new Approach(leader, target, world.GameHours + ApproachGiveUpHours));
        Hold(world, l);
    }

    /// <summary>
    /// The NPC approaches (serial): it walks to the target and, in talk range (<see cref="InTalkRange"/>) while no conversation is open, fires event 1 at it (the player's
    /// conversation window opens with it); after 25 game minutes it gives up (plan dropped, event 60 at the target) once the target is getting away, else it walks on; a
    /// target or NPC gone or down ends it.
    /// </summary>
    void StepApproaches(World world)
    {
        var table = world.Characters;
        for (int k = 0; k < _approaches.Count; k++)
        {
            var a = _approaches[k];
            int n = -1, t = -1;
            bool done = !table.TryResolveNext(a.Npc, out n) || !Awake(table, n) || !table.TryResolveNext(a.Target, out t) || !Awake(table, t);
            if (!done && _open is null && InTalkRange(table, n, t))
            {
                TaskStart.Stand(table, n);
                _approaches.RemoveAt(k--);
                Start(world, a.Npc, a.Target, DialogueEvents.PlayerTalkToMe, quiet: true);
                if (_open?.Npc != a.Npc) Release(world, a.Npc);
                continue;
            }
            if (!done && world.GameHours >= a.GiveUpHours && GettingAway(table, n, t))
            {
                // The NPC drops its plan and the approach, and event 60 fires.
                if (table.Cold(n)?.Ai is { } mind) { mind.Plan.Clear(); mind.Goal = null; mind.Version = -1; }
                TaskStart.Stand(table, n);
                world.Defer(new DialogueEvent(a.Npc, a.Target, GiveUpChase) { Tick = world.Tick + 1 });
                done = true;
            }
            if (done)
            {
                _approaches.RemoveAt(k--);
                Release(world, a.Npc);
                continue;
            }
            // Walking: towards where the target stands, again when it has moved on.
            var to = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
            ref var nh = ref table.Next[n];
            table.Cold(n)!.Engaged = true;
            if (table.Cold(n)!.InCombat) continue;   // its walk would end the fight it is in (a new task drops the fight task)
            if (nh.Task != (byte)CharacterTask.GoTo || Vector2.Distance(nh.Goal, to) > TalkReach) TaskStart.Walk(world, n, to, SpeedMode.Free, Data.Gameplay.Ai.TaskType.TalktoNearestPlayerCharacter, TaskSource.Npc);
        }
    }

    /// <summary>
    /// Whether the target is getting away from the NPC (<c>FUN_140331ee0</c>): it runs away (task 35 or 127), or it walks to a move goal farther from the NPC than it stands.
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

    void HashApproaches(ref StateHasher hasher)
    {
        foreach (var a in _approaches) { hasher.Add(a.Npc); hasher.Add(a.Target); hasher.Add(a.GiveUpHours); }
    }
}