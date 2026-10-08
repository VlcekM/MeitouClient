using System.Numerics;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>
/// Walks an attacker to its target through the path service (docs/simulation.md "Combat"). The combat system is run with <c>SelfApproach</c> off, so a character with the
/// <see cref="CharacterTask.Attack"/> task would stand still; this system, placed before the movement system, asks for a path to the target (a new one when the target has moved
/// more than <see cref="RepathDistance"/> from the goal, at most every <see cref="RepathSeconds"/>) and drops the path once the target is in reach or a swing is on.
/// </summary>
public sealed class PursuitSystem(CombatSystem combat) : ITickSystem
{
    public const float RepathDistance = 12, RepathSeconds = 0.5f;

    public void Schedule(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || n.Task != (byte)CharacterTask.Attack) continue;
            var st = combat.StateOf(new CharacterId(i, n.Generation));
            int t = st.TargetSlot;
            if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration) continue;
            const ushort Path = (ushort)MoveFlags.AnyPath;
            var d = next[t].Position - n.Position;
            float gap = MathF.Sqrt(d.X * d.X + d.Z * d.Z) - CombatTuning.Footprint(table.Cold(i)) - CombatTuning.Footprint(table.Cold(t));
            n.TaskTime -= dt;
            if (st.Down || st.AttackTech >= 0 || st.ReactTech >= 0 || gap <= CombatTuning.CloseInGap)
            {
                // In reach or mid-swing: stand and fight.
                if ((n.Flags & Path) != 0)
                {
                    n.Flags = (ushort)(n.Flags & ~Path);
                    n.PathCursor = 0;
                    table.Cold(i)!.PathRequest = 0;
                }
                continue;
            }
            var goal = new Vector2(next[t].Position.X, next[t].Position.Z);
            bool asking = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
            bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0 || Vector2.DistanceSquared(goal, n.Goal) > RepathDistance * RepathDistance;
            if (asking || !stale || n.TaskTime > 0) continue;
            n.Goal = goal;
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = RepathSeconds;
        }
    }
}


/// <summary>
/// A minimal answer to being attacked (an <b>engine rule</b>, the original's AI is stage 9): every <see cref="CheckEveryTicks"/> ticks, a character that is awake, armed with a
/// <see cref="Fighter"/>, not the player's and not fighting already, and that someone is attacking, issues its own <see cref="AttackOrder"/> against that attacker, and so do its squad mates
/// within <see cref="JoinRange"/> units of it. Hostile relations do not start fights by themselves.
/// </summary>
public sealed class RetaliationSystem(CombatSystem combat) : ITickSystem
{
    public const int CheckEveryTicks = 15;
    public const float JoinRange = 250;

    public void SlowWorld(World world)
    {
        if (world.Tick % CheckEveryTicks != 0) return;
        var table = world.Characters;
        var next = table.Next;
        var taken = new HashSet<int>();
        for (int a = 0; a < table.HighWater; a++)
        {
            if (!next[a].Alive || next[a].Task != (byte)CharacterTask.Attack) continue;
            var attacker = new CharacterId(a, next[a].Generation);
            var st = combat.StateOf(attacker);
            int t = st.TargetSlot;
            if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration || table.Cold(a) is not { Medical.Incapacitated: false }) continue;
            if (!CanFight(table, t, taken)) continue;
            var defenders = new List<CharacterId> { new(t, next[t].Generation) };
            taken.Add(t);
            var squad = table.Cold(t)!.SquadId >= 0 ? world.Squads.Find(table.Cold(t)!.SquadId) : null;
            if (squad is not null)
                foreach (var m in squad.Members)
                {
                    if (!table.TryResolveNext(m, out int s) || s == t || !CanFight(table, s, taken)) continue;
                    var d = next[s].Position - next[t].Position;
                    if (d.X * d.X + d.Z * d.Z > JoinRange * JoinRange) continue;
                    defenders.Add(m);
                    taken.Add(s);
                }
            world.Commands.Enqueue(new AttackOrder(defenders, attacker) { Tick = world.Tick + 1 });
        }
    }

    bool CanFight(CharacterTable table, int slot, HashSet<int> taken) =>
        !taken.Contains(slot) && table.Next[slot].Task != (byte)CharacterTask.Attack
        && table.Cold(slot) is { IsPlayer: false, Fighter: not null, Medical: { Incapacitated: false } };
}
