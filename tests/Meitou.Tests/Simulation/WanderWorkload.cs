using System.Numerics;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// A dummy workload that leans on every rule of the parallel phases: seeded wandering (think), separation steering summed over
/// neighbours from the grid and a walkability test (move), blows on the nearest neighbour and spawns as effects (act), and deaths
/// and births applied in the commit with slot reuse. Used by the determinism tests.
/// </summary>
sealed class WanderSystem : ITickSystem
{
    public const int Hit = 1, Birth = 2;
    public int MaxCharacters = 600;
    public float HitReach = 45;

    public static float Ground(float x, float z) => 90 + 40 * (1 + MathF.Sin(x * 0.01f) * MathF.Cos(z * 0.013f));

    public static SimWorld Create(ulong seed, int threads, int characters, bool publish = false, int minPartition = 16)
    {
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = publish, MinPartitionSize = minPartition },
            new OpenGroundWalkability(Ground), [new WanderSystem()]);
        Populate(world, characters);
        return world;
    }

    static void Populate(SimWorld world, int characters)
    {
        // Setup rolls come from the same keyed streams, so the starting world depends on the seed alone.
        for (int n = 0; world.Characters.Count < characters; n++)
        {
            ulong r = Rng.Hash(world.Seed, (ulong)n, RngPurpose.Spawn);
            float x = Rng.Float(r) * 700, z = Rng.Float(Rng.Mix(r)) * 700;
            if (!world.Walkability.IsWalkable(x, z)) continue;
            world.Characters.Spawn(new CharacterHot { Position = new(x, Ground(x, z), z), Health = 100 },
                new CharacterCold { Name = $"c{n}", Faction = n % 7 }, 0);
        }
    }

    public void Think(SimWorld world, Partition part)
    {
        var prev = world.Characters.Previous;
        var next = world.Characters.Next;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            if ((world.Tick + i) % 30 != 0) continue;
            ulong roll = Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(i)), RngPurpose.Wander, (ulong)world.Tick);
            float angle = Rng.Float(roll) * MathF.Tau, speed = 20 + Rng.Float(Rng.Mix(roll)) * 60;
            next[i].Velocity = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * speed;
        }
    }

    struct PushState
    {
        public CharacterTable Table;
        public int Self;
        public Vector2 Sum;
    }

    struct NearestState
    {
        public CharacterTable Table;
        public int Self;
        public int Slot;
        public float Distance;
    }

    public void Move(SimWorld world, Partition part)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        for (int i = part.Start; i < part.End; i++)
        {
            ref readonly var p = ref table.Previous[i];
            if (!p.Alive) continue;
            ref var n = ref table.Next[i];
            // Separation: the sum runs over the neighbours in the grid's order, a function of the state alone.
            var push = new PushState { Table = table, Self = i };
            world.Grid.Query(p.Position.X, p.Position.Z, 30, ref push, static (int other, ref PushState s) =>
            {
                if (other == s.Self) return;
                var prev = s.Table.Previous;
                var d = new Vector2(prev[s.Self].Position.X - prev[other].Position.X, prev[s.Self].Position.Z - prev[other].Position.Z);
                float len = d.Length();
                if (len is > 0 and < 30) s.Sum += d / len * (1 - len / 30);
            });
            var step = new Vector3(n.Velocity.X + push.Sum.X * 40, 0, n.Velocity.Z + push.Sum.Y * 40) * dt;
            float x = p.Position.X + step.X, z = p.Position.Z + step.Z;
            if (world.Walkability.IsWalkable(x, z))
            {
                n.Position = new Vector3(x, world.Walkability.GroundHeight(x, z), z);
                if (step.X != 0 || step.Z != 0) n.Yaw = MathF.Atan2(step.X, step.Z);
            }
            else
            {
                n.Velocity = -n.Velocity;   // turn round at the water
            }
        }
    }

    public void Act(SimWorld world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            ulong key = Rng.Key(table.IdOf(i));
            ulong roll = Rng.Hash(world.Seed, key, RngPurpose.Combat, (ulong)world.Tick);
            if (roll % 60 == 0)
            {
                // The nearest other character in reach; ties go to the lower slot, whatever order the grid gives them.
                var best = new NearestState { Table = table, Self = i, Slot = -1, Distance = float.MaxValue };
                world.Grid.Query(prev[i].Position.X, prev[i].Position.Z, HitReach, ref best, static (int other, ref NearestState b) =>
                {
                    if (other == b.Self) return;
                    var p = b.Table.Previous;
                    float d = Vector3.DistanceSquared(p[b.Self].Position, p[other].Position);
                    if (d < b.Distance || (d == b.Distance && other < b.Slot)) (b.Slot, b.Distance) = (other, d);
                });
                if (best.Slot >= 0 && best.Distance < HitReach * HitReach)
                    effects.Emit(i, best.Slot, Hit, amount: 20 + Rng.Float(Rng.Mix(roll)) * 20);
            }
            else if (roll % 700 == 1)
            {
                effects.Emit(i, i, Birth, position: prev[i].Position + new Vector3(5, 0, 5));
            }
        }
    }

    public void Apply(SimWorld world, in Effect effect)
    {
        var table = world.Characters;
        switch (effect.Kind)
        {
            case Hit:
                if (!table.Next[effect.Target].Alive) return;
                table.Next[effect.Target].Health -= effect.Amount;
                if (table.Next[effect.Target].Health <= 0) table.Remove(new CharacterId(effect.Target, table.Next[effect.Target].Generation));
                break;
            case Birth:
                if (table.Count >= MaxCharacters || !world.Walkability.IsWalkable(effect.Position.X, effect.Position.Z)) return;
                table.Spawn(new CharacterHot { Position = new(effect.Position.X, world.Walkability.GroundHeight(effect.Position.X, effect.Position.Z), effect.Position.Z), Health = 100 }, new CharacterCold { Name = "child", Faction = 7 }, world.Tick);
                break;
        }
    }
}
