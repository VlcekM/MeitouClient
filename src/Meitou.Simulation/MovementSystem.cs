using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// Movement on the walkability stand-in (docs/game/pathfinding.md "Movement") and the first tasks: wander within the squad's home
/// radius, go to (move orders), follow the squad leader.
/// <list type="bullet">
/// <item>Speed: the stat S in decimetres (world units) per second, capped by the speed mode (walk speed, 55, none); the character
/// speeds up by 15 units/s per second (<see cref="Acceleration"/>, Observed in the follow steering) and stops at once on arrival.</item>
/// <item>Separation: the repulsion along self-other is nonzero only closer than R and proportional to 100 x (1 - d/R), over at most
/// 26 neighbours (Observed); R is twice the footprint radius 4 of a human (<see cref="SeparationRadius"/>), and the scale from that
/// proportionality to a speed is an engine choice (<see cref="SeparationStrength"/>).</item>
/// <item>Paths: a character that wants one is flagged in Think; the Schedule step of the next tick asks the <see cref="PathService"/>;
/// an answer is applied in a serial step, so a path is never read while it is replaced.</item>
/// </list>
/// Not modelled yet (Unknown or later): water states and swimming, slopes (the original has no slope penalty), the combat speed
/// multiplier, road routes for far trips, the formation slot rules (Unknown), queued orders, turning speed.
/// </summary>
public sealed class MovementSystem(PathService paths) : ITickSystem
{
    /// <summary>Speed gained per second (docs/game/pathfinding.md, the follow steering: "ramps up by 15 per second").</summary>
    public const float Acceleration = 15;
    /// <summary>R of the separation steering: neighbour radius plus margin; a human's footprint radius is 4.</summary>
    public const float SeparationRadius = 8;
    /// <summary>Speed in units/s of a repulsion of magnitude 100 (engine choice).</summary>
    public const float SeparationStrength = 0.15f;
    public const int MaxSeparationContributions = 26;
    /// <summary>A character within this distance of a waypoint has reached it.</summary>
    public const float ArriveRadius = 10;
    /// <summary>Formation offsets (3 units apart in the factory) are spread by this when followers stand beside the leader (the slot rules are Unknown).</summary>
    public const float FormationScale = 2.5f;
    /// <summary>A follower farther than this from its slot asks for a path to it.</summary>
    public const float FollowSlack = 14;
    /// <summary>Seconds between a follower's path requests, and the wait after a failed one.</summary>
    public const float RepathSeconds = 0.5f, FailedPathWait = 2;
    /// <summary>A wanderer waits this many seconds (uniform) after reaching a place.</summary>
    public const float WaitMin = 3, WaitMax = 10;
    /// <summary>A follower farther than this from its slot runs at full speed to catch up.</summary>
    public const float CatchUpDistance = 60;

    public PathService Paths { get; } = paths;

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        ApplyAnswers(world);
        var table = world.Characters;
        foreach (var command in commands)
        {
            if (command is RepathCommand)
            {
                for (int s = 0; s < table.Next.Length; s++)
                {
                    ref var c = ref table.Next[s];
                    if (!c.Alive || c.Task != (byte)CharacterTask.GoTo || (c.Flags & (ushort)(MoveFlags.Pending | MoveFlags.HasPath)) == 0) continue;
                    c.Flags = (ushort)((c.Flags & ~(ushort)(MoveFlags.Pending | MoveFlags.HasPath)) | (ushort)MoveFlags.NeedPath);
                    c.PathCursor = 0;
                    table.Cold(s)!.PathRequest = 0;
                }
                continue;
            }
            if (command is not MoveOrder order) continue;
            // An empty list orders the selection (the UI sends orders for whatever is selected when the click happens).
            var who = order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray();
            var slots = new List<int>();
            foreach (var id in who) if (table.TryResolveNext(id, out int found)) slots.Add(found);
            var targets = Formation.Place([.. slots.Select(s => new Vector2(table.Next[s].Position.X, table.Next[s].Position.Z))], new Vector2(order.Target.X, order.Target.Z));
            for (int k = 0; k < slots.Count; k++)
            {
                int slot = slots[k];
                ref var n = ref table.Next[slot];
                var cold = table.Cold(slot)!;
                var target = targets[k];
                bool busy = n.Task == (byte)CharacterTask.GoTo && (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending | MoveFlags.HasPath)) != 0;
                if (order.Queued && busy)
                {
                    cold.OrderQueue.Add(target);   // after the order in hand (shift + right click)
                    continue;
                }
                GoTo(table, slot, target, SpeedMode.Free);
            }
        }
    }

    /// <summary>Sends a character to a point at a speed mode, dropping its queue and path (serial phases only).</summary>
    public static void GoTo(CharacterTable table, int slot, Vector2 target, byte mode)
    {
        ref var n = ref table.Next[slot];
        var cold = table.Cold(slot)!;
        cold.OrderQueue.Clear();
        n.Task = (byte)CharacterTask.GoTo;
        n.Goal = target;
        n.Mode = mode;
        n.Flags = (ushort)((n.Flags & ~(ushort)(MoveFlags.Pending | MoveFlags.HasPath)) | (ushort)MoveFlags.NeedPath);
        n.PathCursor = 0;
        cold.PathRequest = 0;
    }

    /// <summary>Drops a character's orders and path and makes it stand (serial phases only).</summary>
    public static void Stop(CharacterTable table, int slot)
    {
        ref var n = ref table.Next[slot];
        n.Flags = (ushort)(n.Flags & ~(ushort)(MoveFlags.NeedPath | MoveFlags.Pending | MoveFlags.HasPath));
        n.Task = (byte)CharacterTask.Idle;
        n.PathCursor = 0;
        var cold = table.Cold(slot)!;
        cold.OrderQueue.Clear();
        cold.PathRequest = 0;
    }

    public void Schedule(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int i = 0; i < next.Length; i++)
        {
            if (!next[i].Alive || (next[i].Flags & (ushort)MoveFlags.NeedPath) == 0) continue;
            var cold = table.Cold(i)!;
            var to = new Vector3(next[i].Goal.X, world.Walkability.GroundHeight(next[i].Goal.X, next[i].Goal.Y), next[i].Goal.Y);
            cold.PathRequest = Paths.Submit(new CharacterId(i, next[i].Generation), next[i].Position, to, cold.FootprintRadius, cold.WaterFactor);
            next[i].Flags = (ushort)((next[i].Flags & ~(ushort)MoveFlags.NeedPath) | (ushort)MoveFlags.Pending);
        }
        // The synchronous service has the answers already.
        if (Paths.IsSynchronous) ApplyAnswers(world);
    }

    void ApplyAnswers(World world)
    {
        var table = world.Characters;
        foreach (var answer in Paths.Drain())
        {
            if (!table.TryResolveNext(answer.Character, out int slot)) continue;
            var cold = table.Cold(slot)!;
            if (cold.PathRequest != answer.Id) continue;   // an older request: the character was given another goal since
            ref var n = ref table.Next[slot];
            n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.Pending);
            if (answer.Path.Found)
            {
                cold.Path = [.. answer.Path.Points.Skip(1)];
                n.PathCursor = 0;
                n.Flags |= (ushort)MoveFlags.HasPath;
            }
            else
            {
                n.TaskTime = FailedPathWait;
                if (n.Task == (byte)CharacterTask.GoTo) { n.Task = (byte)CharacterTask.Idle; cold.OrderQueue.Clear(); }
            }
        }
    }

    public void Think(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            ref var n = ref next[i];
            var task = (CharacterTask)n.Task;
            var flags = (MoveFlags)n.Flags;
            var cold = table.Cold(i)!;
            if (cold.Medical is { Incapacitated: true }) continue;
            bool busy = (flags & (MoveFlags.NeedPath | MoveFlags.Pending | MoveFlags.HasPath)) != 0;
            if (task == CharacterTask.Follow)
            {
                var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
                var leader = squad?.Leader ?? CharacterId.None;
                if (leader.IsNone || leader.Slot == i || !table.IsAlive(leader))
                {
                    n.Task = (byte)CharacterTask.Wander;
                    continue;
                }
                var lead = prev[leader.Slot];
                var slotPos = new Vector2(lead.Position.X, lead.Position.Z) + cold.FormationOffset * FormationScale;
                var here = new Vector2(n.Position.X, n.Position.Z);
                float away = Vector2.Distance(here, slotPos);
                n.TaskTime -= dt;
                n.Mode = away > CatchUpDistance ? SpeedMode.Free : lead.Mode;
                if (away > FollowSlack && (flags & (MoveFlags.NeedPath | MoveFlags.Pending)) == 0 && n.TaskTime <= 0
                    && (!busy || Vector2.DistanceSquared(slotPos, n.Goal) > 20 * 20))
                {
                    n.Goal = slotPos;
                    n.Flags |= (ushort)MoveFlags.NeedPath;
                    n.TaskTime = RepathSeconds;
                }
            }
            else if (task == CharacterTask.Wander)
            {
                n.Mode = SpeedMode.Walk;
                if (busy) continue;
                n.TaskTime -= dt;
                if (n.TaskTime > 0) continue;
                var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
                var centre = squad is not null ? squad.HomeCentre : new Vector2(n.Position.X, n.Position.Z);
                float radius = squad is not null ? squad.HomeRadius : 100;
                ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(i)), RngPurpose.Wander, (ulong)world.Tick);
                float angle = Rng.Float(roll) * MathF.Tau, r = MathF.Sqrt(Rng.Float(Rng.Mix(roll))) * radius;
                n.Goal = centre + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * r;
                n.Flags |= (ushort)MoveFlags.NeedPath;
            }
        }
    }

    public void Move(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        var walk = world.Walkability;
        float dt = world.TickSeconds;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            ref readonly var p = ref prev[i];
            ref var n = ref next[i];
            var cold = table.Cold(i)!;
            if (cold.Medical is { Incapacitated: true })
            {
                // Lying down (knocked out or dead): no steering, no pushing; the body system has already dropped the orders.
                n.Velocity = Vector3.Zero;
                n.Animation = 0;
                continue;
            }
            float speed = MathF.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Z * p.Velocity.Z);
            var dir = Vector2.Zero;
            float desired = 0;
            bool finished = false;

            if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0 && n.PathCursor < cold.Path.Length)
            {
                var waypoint = cold.Path[n.PathCursor];
                var to = new Vector2(waypoint.X - p.Position.X, waypoint.Z - p.Position.Z);
                float dist = to.Length();
                bool last = n.PathCursor == cold.Path.Length - 1;
                if (dist <= (last ? ArriveRadius : ArriveRadius * 0.5f))
                {
                    n.PathCursor++;
                    if (last) finished = true;
                }
                else
                {
                    dir = to / dist;
                    desired = MathF.Min(n.MaxSpeed, SpeedMode.Cap(n.Mode, n.WalkSpeed));
                }
            }
            else if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0)
            {
                finished = true;
            }

            if (finished)
            {
                n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                n.PathCursor = 0;
                speed = 0;
                if (n.Task == (byte)CharacterTask.GoTo)
                {
                    if (cold.OrderQueue.Count > 0)
                    {
                        // The next queued order (shift + right click): a path to it is asked for next tick.
                        n.Goal = cold.OrderQueue[0];
                        cold.OrderQueue.RemoveAt(0);
                        n.Flags |= (ushort)MoveFlags.NeedPath;
                    }
                    else n.Task = (byte)CharacterTask.Idle;
                }
                else
                {
                    ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(i)), RngPurpose.Think, (ulong)world.Tick);
                    n.TaskTime = WaitMin + Rng.Float(roll) * (WaitMax - WaitMin);
                }
            }
            else
            {
                speed = desired > speed ? MathF.Min(speed + Acceleration * dt, desired) : desired;
            }

            // Separation from the neighbours of the last tick, visited in the grid's fixed order.
            var push = new SeparationState { Table = table, Self = i };
            world.Grid.Query(p.Position.X, p.Position.Z, SeparationRadius, ref push, static (int other, ref SeparationState s) =>
                {
                    if (other == s.Self || s.Count >= MaxSeparationContributions) return;
                    var all = s.Table.Previous;
                    var d = new Vector2(all[s.Self].Position.X - all[other].Position.X, all[s.Self].Position.Z - all[other].Position.Z);
                    float len = d.Length();
                    if (len >= SeparationRadius) return;
                    s.Count++;
                    // Exactly on top of each other: the lower slot is pushed one way, the other the opposite.
                    s.Sum += (len > 1e-4f ? d / len : (s.Self < other ? Vector2.UnitX : -Vector2.UnitX)) * (100 * (1 - len / SeparationRadius));
                });

            var step = (dir * speed + push.Sum * SeparationStrength) * dt;
            float x = p.Position.X + step.X, z = p.Position.Z + step.Y;
            if (step != Vector2.Zero)
            {
                if (walk.IsWalkable(x, z))
                {
                    n.Position = new Vector3(x, walk.GroundHeight(x, z), z);
                    if (speed > 0.1f) n.Yaw = MathF.Atan2(dir.X, dir.Y);
                }
                else
                {
                    // Blocked (water): drop the path and wait before trying again.
                    speed = 0;
                    n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                    n.TaskTime = FailedPathWait;
                    if (n.Task == (byte)CharacterTask.GoTo) { n.Task = (byte)CharacterTask.Idle; cold.OrderQueue.Clear(); }
                }
            }
            n.Velocity = new Vector3(dir.X * speed, 0, dir.Y * speed);
            n.Animation = (ushort)(speed < 1 ? 0 : speed <= n.WalkSpeed + 1 ? 1 : 2);
            n.AnimationTime = n.Animation == p.Animation ? p.AnimationTime + dt : 0;
        }
    }

    struct SeparationState
    {
        public CharacterTable Table;
        public int Self;
        public int Count;
        public Vector2 Sum;
    }
}
