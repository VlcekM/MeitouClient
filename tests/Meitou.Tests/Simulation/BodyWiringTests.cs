using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Tests.Bodies;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Stage 7 in the world: stats and medical states at spawn, the medical tick, the speed chain, limping, lying down and determinism.
/// </summary>
public class BodyWiringTests
{
    /// <summary>
    /// Hurts every second character's left leg at tick 20 and knocks the others out at tick 60 (a scripted serial input).
    /// </summary>
    sealed class Hitter : ITickSystem
    {
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands)
        {
            if (world.Tick != 20 && world.Tick != 60) return;
            var table = world.Characters;
            for (int s = 0; s < table.Next.Length; s++)
            {
                if (!table.Next[s].Alive || table.Cold(s) is not { Medical: { } medical, Race: { } race, Stats: { } stats })
                    continue;
                var ctx = new MedicalContext(GameConstants.Default, BodyOptions.Default, race)
                {
                    Toughness = stats.Toughness, Strength = stats.Strength, Seed = world.Seed, CharacterKey = Rng.Key(table.IdOf(s)),
                };
                if (world.Tick == 20 && s % 2 == 0) medical.ApplyHit(1, new HitDamage(60, 10, 0), ctx);
                if (world.Tick == 60 && s % 2 == 1) medical.ApplyHit(0, new HitDamage(0, 150, 0), ctx);
            }
        }
    }

    static SimWorld World(ulong seed, int threads, float timeScale = 1, bool hit = false)
    {
        var db = SyntheticTown.Database(anatomy: true);
        return SyntheticTown.World(seed, threads, db: db, bodies: true, bodyTimeScale: timeScale, extra: hit ? [new Hitter()] : []);
    }

    [Fact]
    public void A_character_killed_by_a_hit_stops_and_its_corpse_is_removed_after_twelve_game_hours()
    {
        using var world = World(7, 1);
        world.RunTicks(2);
        var table = world.Characters;
        int slot = Enumerable.Range(0, table.HighWater).First(s => table.Previous[s].Alive && table.Cold(s)!.Medical is not null);
        var id = table.IdOf(slot);
        var cold = table.Cold(slot)!;
        var ctx = new MedicalContext(GameConstants.Default, BodyOptions.Default, cold.Race!) { Toughness = 50, Strength = 50, Seed = 7,
                CharacterKey = Rng.Key(id) };
        cold.Medical!.ApplyHit(0, new HitDamage(100000, 0, 0), ctx);

        Assert.True(cold.Medical.Dead);

        world.RunTicks(5);

        Assert.Equal(Vector3.Zero, table.Previous[slot].Velocity);
        Assert.True(table.IsAlive(id), "the corpse lies there");

        world.RunTicks((int)(BodySystem.CorpseTicks + 10));

        Assert.False(table.IsAlive(id));
    }

    internal static List<ulong> Hashes(ulong seed, int threads, float timeScale)
    {
        using var world = World(seed, threads, timeScale, hit: true);
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in new[] { 1, 19, 21, 59, 61, 200, 600 })
        {
            world.RunTicks(target - ran);
            ran = target;
            hashes.Add(world.StateHash());
        }
        return hashes;
    }
}
