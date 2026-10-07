using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Tests.Bodies;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Stage 7 in the world: stats and medical states at spawn, the medical tick, the speed chain, limping, lying down and determinism.</summary>
public class BodyWiringTests
{
    /// <summary>Hurts every second character's left leg at tick 20 and knocks the others out at tick 60 (a scripted serial input).</summary>
    sealed class Hitter : ITickSystem
    {
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands)
        {
            if (world.Tick != 20 && world.Tick != 60) return;
            var table = world.Characters;
            for (int s = 0; s < table.Next.Length; s++)
            {
                if (!table.Next[s].Alive || table.Cold(s) is not { Medical: { } medical, Race: { } race, Stats: { } stats }) continue;
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
    public void Characters_with_a_race_get_stats_and_a_medical_state_at_spawn_and_animals_do_not()
    {
        using var world = World(3, 1);
        world.RunTicks(2);
        var table = world.Characters;
        int humans = 0, animals = 0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Previous[s].Alive) continue;
            var cold = table.Cold(s)!;
            if (cold.RecordId == "4-t")
            {
                animals++;
                Assert.Null(cold.Medical);
                continue;
            }
            humans++;
            Assert.NotNull(cold.Race);
            Assert.NotNull(cold.Stats);
            Assert.Equal(3, cold.Medical!.Parts.Count);
            // The speed chain replaced the stand-in (70 + 50 x 0.2 = 80): athletics comes from the record's stats.
            Assert.Equal(Speed.Run(cold.Race!, cold.Stats!, cold.Medical, 1), table.Previous[s].MaxSpeed, 2);
            Assert.NotEqual(80f, table.Previous[s].MaxSpeed);
            // The water cost: race avoidance 3 gives 4 (a + 1).
            Assert.Equal(4f, cold.WaterFactor, 3);
        }
        Assert.True(humans > 0);
        Assert.True(animals > 0);
    }

    [Fact]
    public void The_player_faction_avoids_water_half_as_much()
    {
        var db = SyntheticTown.Database(anatomy: true);
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var population = new PopulationSystem(data);
        using var world = new SimWorld(new WorldSettings { Seed = 4, PublishSnapshots = false }, walk, [population, new PlayerSystem(), new MovementSystem(new PathService(walk, true))]);
        var squad = population.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        foreach (var m in squad.Members) Assert.Equal(2f, world.Characters.Cold(m.Slot)!.WaterFactor, 3);
    }

    [Fact]
    public void Hunger_falls_over_game_hours_and_the_blood_stays_full_when_unhurt()
    {
        using var world = World(5, 1);
        world.RunTicks(2);
        var table = world.Characters;
        int slot = Enumerable.Range(0, table.HighWater).First(s => table.Previous[s].Alive && table.Cold(s)!.Medical is not null);
        var medical = table.Cold(slot)!.Medical!;
        float hunger = medical.Hunger, blood = medical.Blood;
        // 10000 ticks are 30.6 game hours.
        world.RunTicks(10000);
        Assert.InRange(medical.Hunger, 0, hunger - 0.01f);
        Assert.Equal(blood, medical.Blood, 1);
        Assert.False(medical.Dead);
    }

    [Fact]
    public void A_knocked_out_character_stops_and_stays_where_it_fell()
    {
        using var world = World(6, 1);
        var table = world.Characters;
        world.RunTicks(400);   // wanderers are on their way
        int slot = -1;
        for (int s = 0; s < table.HighWater && slot < 0; s++)
            if (table.Previous[s].Alive && table.Cold(s)!.Medical is not null && new Vector2(table.Previous[s].Velocity.X, table.Previous[s].Velocity.Z).Length() > 1) slot = s;
        Assert.True(slot >= 0, "somebody was walking");
        var cold = table.Cold(slot)!;
        cold.Medical!.KnockOut(100000);
        world.RunTicks(3);
        var at = table.Previous[slot].Position;
        Assert.Equal(Vector3.Zero, table.Previous[slot].Velocity);
        world.RunTicks(300);
        Assert.Equal(at, table.Previous[slot].Position);
        Assert.True(cold.Medical.Incapacitated);
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
        var ctx = new MedicalContext(GameConstants.Default, BodyOptions.Default, cold.Race!) { Toughness = 50, Strength = 50, Seed = 7, CharacterKey = Rng.Key(id) };
        cold.Medical!.ApplyHit(0, new HitDamage(100000, 0, 0), ctx);
        Assert.True(cold.Medical.Dead);
        world.RunTicks(5);
        Assert.Equal(Vector3.Zero, table.Previous[slot].Velocity);
        Assert.True(table.IsAlive(id), "the corpse lies there");
        world.RunTicks((int)(BodySystem.CorpseTicks + 10));
        Assert.False(table.IsAlive(id));
    }

    [Fact]
    public void A_hurt_leg_picks_the_limp_clips_and_a_character_that_is_out_lies_down()
    {
        static AnimationDefinition Idle(string name, float min, float max) => new()
        {
            Id = name, Name = name, Clip = name, Area = AnimationArea.All, Idle = true, IdleChance = 100, IdleTimeMin = 10, IdleTimeMax = 40, Chance = 100,
            WeaponLeft = 0, WeaponRight = 0, CombatMode = 0, StealthMode = 0, Kinds = WeaponKinds.All,
            LeftLegMin = min, LeftLegMax = max, RightLegMin = 0, RightLegMax = 100,
        };
        var library = new AnimationLibrary([Idle("stand 1", 60, 100), Idle("limp stand L", -100, 60),
            new AnimationDefinition { Id = "sleeponfloor", Name = "sleeponfloor", Clip = "sleeponfloor", Area = AnimationArea.All, IsAction = true, Loop = true, WeaponLeft = 0, WeaponRight = 0, CombatMode = 0, StealthMode = 0, Kinds = WeaponKinds.All, LeftLegMin = 1000, LeftLegMax = 1000, RightLegMin = 1000, RightLegMax = 1000 }]);
        var system = new AnimationSystem(library, new AnimationLengths());
        string Played(CharacterCold cold)
        {
            var a = new CharacterAnimation();
            for (int i = 0; i < 40; i++) system.Update(a, cold, 0, 1 / 30f, 1, 1, i);
            return string.Join(",", system.Publish(a).Where(l => l.Weight > 0.99f).Select(l => l.Name));
        }
        Assert.Equal("stand 1", Played(new CharacterCold { Medical = BodyFixtures.Healthy() }));

        var hurt = BodyFixtures.Healthy();
        hurt.Parts[BodyFixtures.LLeg].Flesh = hurt.Parts[BodyFixtures.LLeg].MaxHp * 0.25f;
        Assert.Equal("limp stand L", Played(new CharacterCold { Medical = hurt }));

        var down = BodyFixtures.Healthy();
        down.KnockOut(60);
        Assert.Equal("sleeponfloor", Played(new CharacterCold { Medical = down }));
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

    [Fact]
    public void Bodies_do_not_change_with_the_thread_count()
    {
        var one = Hashes(9, 1, 2000);
        Assert.Equal(one, Hashes(9, 4, 2000));
        Assert.Equal(one, Hashes(9, 16, 2000));
        Assert.Equal(one, Hashes(9, 1, 2000));
        Assert.NotEqual(one[^1], Hashes(10, 1, 2000)[^1]);
    }

    [Fact]
    public void The_hits_show_in_the_state_and_the_body_time_scale_changes_it()
    {
        var plain = Hashes(9, 1, 1);
        var fast = Hashes(9, 1, 2000);
        Assert.Equal(plain[0], fast[0]);   // nothing happens before the first hit
        Assert.NotEqual(plain[^1], fast[^1]);
        Assert.NotEqual(plain[1], plain[2]);   // the hit at tick 20 is in the hash of tick 21
    }
}
