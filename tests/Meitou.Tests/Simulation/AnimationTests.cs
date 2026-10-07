using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class AnimationTests
{
    static AnimationDefinition Def(string name, AnimationArea area, float move = 0, float play = 0, bool idle = false, int right = 2, WeaponKinds kinds = WeaponKinds.All, bool synch = false) => new()
    {
        Id = name, Name = name, Clip = name, Area = area, MoveSpeed = move, PlaySpeed = play, Idle = idle, Synchs = synch,
        IdleChance = 100, IdleTimeMin = 10, IdleTimeMax = 40, Chance = 100, WeaponLeft = 0, WeaponRight = right, CombatMode = 0, StealthMode = 0,
        Kinds = kinds, LeftLegMin = 0, LeftLegMax = 100, RightLegMin = 0, RightLegMax = 100,
    };

    /// <summary>The shape of the base game's records: idles, a walk (14), a jog (45) and a run (90) chain of the lower and upper body, and a sword-hand variant.</summary>
    static AnimationLibrary Library() => new(
    [
        Def("stand 1", AnimationArea.All, idle: true, right: 0),
        Def("stand 1 sword", AnimationArea.All, idle: true, right: 1),
        Def("walk lower", AnimationArea.Lower, 14, 0.06f, synch: true),
        Def("walk upper", AnimationArea.Upper, 14, 0.06f, right: 0, synch: true),
        Def("walk upper sword", AnimationArea.Upper, 14, 0.06f, right: 1, synch: true),
        Def("jog lower", AnimationArea.Lower, 45, 0.026f, synch: true),
        Def("jog upper", AnimationArea.Upper, 45, 0.026f, right: 0, synch: true),
        Def("run lower", AnimationArea.Lower, 90, 0.02f, synch: true),
        Def("run upper", AnimationArea.Upper, 90, 0.02f, right: 0, synch: true),
        Def("run upper sword", AnimationArea.Upper, 90, 0.02f, right: 1, synch: true),
    ]);

    static AnimationLengths Lengths() => new AnimationLengths().Add("walk lower", 1.4f).Add("walk upper", 1.4f).Add("jog lower", 0.9f).Add("jog upper", 0.9f).Add("run lower", 0.5667f).Add("run upper", 0.5667f);

    static Dictionary<string, float> Weights(AnimationSystem system, CharacterAnimation a) => system.Publish(a).ToDictionary(l => l.Name, l => l.Weight);

    [Fact]
    public void The_movement_chain_blends_the_two_clips_around_the_speed_and_uses_one_beyond_the_ends()
    {
        var lib = Library();
        float W(float speed, AnimationStance s, string name)
        {
            var into = new List<(int, float)>();
            lib.Movement(AnimationArea.Lower, speed, s, into);
            return into.Where(p => lib.Definitions[p.Item1].Name == name).Sum(p => p.Item2);
        }
        var plain = new AnimationStance();
        Assert.Equal(1, W(5, plain, "walk lower"), 3);
        Assert.Equal(1, W(14, plain, "walk lower"), 3);
        Assert.Equal(0.5, W(29.5f, plain, "walk lower"), 3);
        Assert.Equal(0.5, W(29.5f, plain, "jog lower"), 3);
        Assert.Equal(1, W(45, plain, "jog lower"), 3);
        Assert.Equal(0.25, W(45 + 11.25f, plain, "run lower"), 3);
        Assert.Equal(1, W(300, plain, "run lower"), 3);
    }

    [Fact]
    public void A_held_weapon_picks_the_weapon_variants_and_sheathed_weapons_the_plain_ones()
    {
        var lib = Library();
        var into = new List<(int, float)>();
        lib.Movement(AnimationArea.Upper, 14, new AnimationStance(), into);
        Assert.Equal(["walk upper"], into.Select(p => lib.Definitions[p.Item1].Name));
        into.Clear();
        lib.Movement(AnimationArea.Upper, 14, new AnimationStance { Right = new HandHold(WeaponKinds.Katana) }, into);
        Assert.Equal(["walk upper sword"], into.Select(p => lib.Definitions[p.Item1].Name));
        Assert.Equal(["stand 1"], lib.Idles(new AnimationStance()).Select(p => lib.Definitions[p.Index].Name));
        Assert.Equal(["stand 1 sword"], lib.Idles(new AnimationStance { Right = new HandHold(WeaponKinds.Sabre) }).Select(p => lib.Definitions[p.Index].Name));
        // A sword record does not take a held weapon of a kind it lists no flag for.
        var narrow = new AnimationLibrary([Def("stand sword", AnimationArea.All, idle: true, right: 1, kinds: WeaponKinds.Katana)]);
        Assert.Empty(narrow.Idles(new AnimationStance { Right = new HandHold(WeaponKinds.Blunt) }));
        Assert.Single(narrow.Idles(new AnimationStance { Right = new HandHold(WeaponKinds.Katana) }));
    }

    [Fact]
    public void A_standing_character_idles_and_starting_to_walk_crossfades_at_the_blend_rate()
    {
        var system = new AnimationSystem(Library(), Lengths(), blendRate: 4);
        var a = new CharacterAnimation();
        var cold = new CharacterCold();
        const float dt = 1 / 30f;
        for (int i = 0; i < 40; i++) system.Update(a, cold, 0, dt, 1, 1, i);
        var idle = Weights(system, a);
        Assert.Equal(["stand 1"], idle.Keys);
        Assert.Equal(1, idle["stand 1"], 3);
        // Walking at 14: a quarter second (blend rate 4) takes the idle out and the walk in.
        int ticks = 0;
        while (Weights(system, a).GetValueOrDefault("stand 1") > 0 && ticks < 100) { system.Update(a, cold, 14, dt, 1, 1, 100 + ticks); ticks++; }
        Assert.InRange(ticks, 7, 9);
        var walking = Weights(system, a);
        Assert.Equal(["walk lower", "walk upper"], walking.Keys.Order());
        Assert.All(walking.Values, w => Assert.Equal(1, w, 3));
    }

    [Fact]
    public void The_synched_phase_advances_by_speed_times_play_speed_over_the_clip_length_and_keeps_both_bodies_in_step()
    {
        var system = new AnimationSystem(Library(), Lengths());
        var a = new CharacterAnimation();
        var cold = new CharacterCold();
        const float dt = 1 / 30f;
        for (int i = 0; i < 290; i++) system.Update(a, cold, 14, dt, 1, 1, i);
        // 9.67 s at 14 * 0.06 clip seconds per second over 1.4 s clips: 5.8 cycles.
        float expected = 290 / 30f * 14 * 0.06f / 1.4f;
        Assert.Equal(expected - MathF.Floor(expected), a.Phase, 2);
        var layers = system.Publish(a);
        var lower = layers.Single(l => l.Name == "walk lower");
        var upper = layers.Single(l => l.Name == "walk upper");
        Assert.Equal(lower.Time, upper.Time, 4);
        Assert.InRange(lower.Time, 0, 1.4f);
        // Faster feet at higher speed: the run's clips go round faster than the walk's.
        float before = a.Phase;
        for (int i = 0; i < 30; i++) system.Update(a, cold, 90, dt, 1, 1, 300 + i);
        Assert.NotEqual(before, a.Phase);
    }

    [Fact]
    public void A_character_with_a_drawn_sword_walks_with_the_sword_variant_and_the_idle_is_kept_for_its_time()
    {
        var system = new AnimationSystem(Library(), Lengths());
        var a = new CharacterAnimation();
        var cold = new CharacterCold { DrawnWeapon = WeaponKinds.Katana };
        const float dt = 1 / 30f;
        for (int i = 0; i < 30; i++) system.Update(a, cold, 14, dt, 1, 1, i);
        Assert.Contains("walk upper sword", Weights(system, a).Keys);
        Assert.DoesNotContain("walk upper", Weights(system, a).Keys);
        // A new idle is not rolled every tick: the same one plays until its 10..40 s are up.
        var still = new CharacterAnimation();
        system.Update(still, new CharacterCold(), 0, dt, 1, 1, 0);
        int first = still.Idle;
        for (int i = 1; i < 300; i++) system.Update(still, new CharacterCold(), 0, dt, 1, 1, i);
        Assert.Equal(first, still.Idle);
    }

    // ---- in a world ----

    static (SimWorld World, Squad Squad) Start(int threads, ulong seed = 6)
    {
        var db = SyntheticTown.Database();
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var population = new PopulationSystem(data, new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 5 });
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = true }, walk,
            [population, new PlayerSystem(), new MovementSystem(new PathService(walk, true)), new AnimationSystem(Library(), Lengths())]);
        var squad = population.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        return (world, squad);
    }

    [Fact]
    public void In_a_world_characters_idle_then_walk_when_ordered_and_the_snapshot_carries_the_layers()
    {
        var (w, squad) = Start(1);
        using var _ = w;
        w.RunTicks(30);
        var who = w.Snapshot.Characters.First(c => c.Id == squad.Leader);
        Assert.Contains(who.Animations, l => l.Name.StartsWith("stand 1"));
        var at = w.Characters.Previous[squad.Leader.Slot].Position;
        w.Commands.Enqueue(new MoveOrder([squad.Leader], new Vector3(at.X + 300, 0, at.Z)) { Tick = w.Tick });
        w.RunTicks(60);
        var moving = w.Snapshot.Characters.First(c => c.Id == squad.Leader);
        Assert.Contains(moving.Animations, l => l.Name.Contains("lower") && l.Weight > 0.5f);
        Assert.DoesNotContain(moving.Animations, l => l.Name.StartsWith("stand"));
        // A squad mate that was not ordered still stands.
        var mate = w.Snapshot.Characters.First(c => c.Id == squad.Members[1]);
        Assert.Contains(mate.Animations, l => l.Name.StartsWith("stand"));
    }

    static List<ulong> Scripted(int threads)
    {
        var (w, squad) = Start(threads, 11);
        using var _ = w;
        var m = squad.Members;
        var at = w.Characters.Previous[m[0].Slot].Position;
        w.Commands.Enqueue(new MoveOrder([m[0]], new Vector3(at.X + 400, 0, at.Z)) { Tick = 5 });
        w.Commands.Enqueue(new MoveOrder([m[1]], new Vector3(at.X, 0, at.Z + 60)) { Tick = 40 });
        w.Commands.Enqueue(new StopCommand([]) { Tick = 200 });
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in new[] { 10, 60, 150, 260, 500 })
        {
            w.RunTicks(target - ran);
            ran = target;
            hashes.Add(w.StateHash());
        }
        return hashes;
    }

    [Fact]
    public void Animation_state_hashes_the_same_at_1_4_and_16_threads()
    {
        var one = Scripted(1);
        Assert.Equal(one, Scripted(4));
        Assert.Equal(one, Scripted(16));
        Assert.Equal(5, one.Distinct().Count());
    }
}
