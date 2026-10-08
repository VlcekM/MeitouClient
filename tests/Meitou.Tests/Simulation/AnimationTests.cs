using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class AnimationTests
{
    static AnimationDefinition Def(string name, AnimationArea area, float move = 0, float play = 0, bool idle = false, int right = 2, WeaponKinds kinds = WeaponKinds.All, bool synch = false,
        float min = 0, float max = 0) => new()
    {
        Id = name, Name = name, Clip = name, Area = area, MoveSpeed = move, PlaySpeed = play, Idle = idle, Synchs = synch, MinSpeed = min, MaxSpeed = max,
        IdleChance = 100, IdleTimeMin = 10, IdleTimeMax = 40, Chance = 100, WeaponLeft = 0, WeaponRight = right, CombatMode = 0, StealthMode = 0,
        Kinds = kinds, LeftLegMin = 0, LeftLegMax = 100, RightLegMin = 0, RightLegMax = 100,
    };

    /// <summary>The base game's records: idles, a walk (14), a jog (45) and a run (90) of the lower and upper body with their min / max speeds, and sword-hand variants.</summary>
    static AnimationLibrary Library() => new(
    [
        Def("stand 1", AnimationArea.All, idle: true, right: 0),
        Def("stand 1 sword", AnimationArea.All, idle: true, right: 1),
        Def("walk lower", AnimationArea.Lower, 14, 0.06f, synch: true, min: -50, max: 40),
        Def("walk upper", AnimationArea.Upper, 14, 0.06f, right: 0, synch: true, min: -50, max: 45),
        Def("walk upper sword", AnimationArea.Upper, 14, 0.06f, right: 1, synch: true, min: 0, max: 45),
        Def("jog lower", AnimationArea.Lower, 45, 0.026f, synch: true, min: 18, max: 74),
        Def("jog upper", AnimationArea.Upper, 45, 0.026f, right: 0, synch: true, min: 14, max: 74),
        Def("run lower", AnimationArea.Lower, 90, 0.02f, synch: true, min: 45, max: 99999),
        Def("run upper", AnimationArea.Upper, 90, 0.02f, right: 0, synch: true, min: 45, max: 99999),
        Def("run upper sword", AnimationArea.Upper, 74, 0.02f, right: 1, synch: true, min: 45, max: 999),
    ]);

    static AnimationLengths Lengths() => new AnimationLengths().Add("walk lower", 1.4f).Add("walk upper", 1.4f).Add("jog lower", 0.9f).Add("jog upper", 0.9f).Add("run lower", 0.5667f).Add("run upper", 0.5667f);

    static Dictionary<string, float> Weights(AnimationSystem system, CharacterAnimation a) => system.Publish(a).ToDictionary(l => l.Name, l => l.Weight);

    [Fact]
    public void Every_movement_clip_weighs_the_sigmoid_of_its_speed_ramp_normalised_per_body_half()
    {
        var lib = Library();
        float W(float speed, AnimationStance s, string name)
        {
            var into = new List<(int, float)>();
            lib.Movement(AnimationArea.Lower, speed, s, into);
            return into.Where(p => lib.Definitions[p.Item1].Name == name).Sum(p => p.Item2);
        }
        var plain = new AnimationStance();
        // Under move speed a clip whose min speed is under 1 weighs fully; nothing else fits at a stroll.
        Assert.Equal(1, W(5, plain, "walk lower"), 3);
        Assert.Equal(1, W(14, plain, "walk lower"), 3);
        // At 29.5 the walk has fallen to r = 10.5 / 26 and the jog risen to r = 11.5 / 27: sigmoids 0.1275 and 0.1852, normalised.
        Assert.Equal(0.4078, W(29.5f, plain, "walk lower"), 3);
        Assert.Equal(0.5922, W(29.5f, plain, "jog lower"), 3);
        Assert.Equal(1, W(45, plain, "jog lower"), 3);
        Assert.Equal(0.0767, W(60, plain, "run lower"), 3);
        Assert.Equal(1, W(300, plain, "run lower"), 3);
        var walk = new AnimationDefinition { Id = "w", Name = "w", Clip = "w", MoveSpeed = 14, MinSpeed = -50, MaxSpeed = 40, LeftLegMin = 60, LeftLegIdeal = 90, LeftLegMax = 100, RightLegMin = 60, RightLegIdeal = 90, RightLegMax = 100 };
        // The leg ramp over the worse leg, a max over 99 read as 101: 1 at the ideal 90, 1 / 11 at full health, 0 under 60.
        Assert.Equal(1, walk.LegWeight(90, 100), 4);
        Assert.Equal(1 / 11f, walk.LegWeight(100, 100), 4);
        Assert.Equal(0.5, walk.LegWeight(100, 75), 4);
        Assert.Equal(0, walk.LegWeight(59, 100));
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
        // Walking at 14: a quarter second (blend rate 4) takes the idle out and the walk in. (The first clips started at full weight.)
        int ticks = 0;
        while (Weights(system, a).GetValueOrDefault("stand 1") > 0 && ticks < 100) { system.Update(a, cold, 14, dt, 1, 1, 100 + ticks); ticks++; }
        Assert.InRange(ticks, 7, 9);
        var walking = Weights(system, a);
        Assert.Equal(["walk lower", "walk upper"], walking.Keys.Order());
        Assert.All(walking.Values, w => Assert.Equal(1, w, 3));
    }

    [Fact]
    public void The_synched_phase_advances_by_speed_times_play_speed_in_cycles_and_keeps_both_bodies_in_step()
    {
        var system = new AnimationSystem(Library(), Lengths());
        var a = new CharacterAnimation();
        var cold = new CharacterCold();
        const float dt = 1 / 30f;
        for (int i = 0; i < 290; i++) system.Update(a, cold, 14, dt, 1, 1, i);
        // 9.67 s at 14 * 0.06 cycles per second (F = 1 without a body file): 8.12 cycles, whatever the clip's length.
        float expected = 290 / 30f * 14 * 0.06f;
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

    // ---- combat ----

    static AnimationDefinition Stumble(string name, bool big, int from, params string[] parts) => Def(name, AnimationArea.All) with
    {
        IsAction = true, Relocates = true, PlaySpeed = 1, Stumbles = parts, BigStumble = big, StumbleFrom = from, Chance = 0, WeaponRight = 2,
    };

    static AnimationDefinition Technique(string name, AnimationArea area, bool relocates) => Def(name, AnimationArea.All) with
    {
        Area = area, IsAction = true, Technique = true, Relocates = relocates, WeaponRight = 2, CombatMode = 2, StealthMode = 2,
    };

    static AnimationLibrary CombatLibrary() => new(
    [
        Def("stand 1", AnimationArea.All, idle: true, right: 0),
        Def("walk lower", AnimationArea.Lower, 14, 0.06f, synch: true, min: -50, max: 40),
        Def("walk upper", AnimationArea.Upper, 14, 0.06f, right: 0, synch: true, min: -50, max: 45),
        Stumble("mid blow light", false, 0, "chest", "head"),
        Stumble("mid blow", true, 0, "chest", "head"),
        Stumble("back blow light", false, 1, "chest", "head"),
        Stumble("back blow low", true, 1, "chest"),
        Technique("Cut left", AnimationArea.All, true),
        Technique("Block up", AnimationArea.Upper, false),
    ]);

    [Fact]
    public void A_hit_picks_the_stumbles_of_its_body_part_by_heaviness_and_side()
    {
        var lib = CombatLibrary();
        string[] Names(string part, bool big, int side)
        {
            var into = new List<(int, float)>();
            lib.Stumbles(part, big, side, new AnimationStance(), -1, into);
            return [.. into.Select(p => lib.Definitions[p.Item1].Name)];
        }
        Assert.Equal(["mid blow light"], Names("chest", false, 0));
        Assert.Equal(["mid blow"], Names("head", true, 0));
        Assert.Equal(["back blow light"], Names("head", false, 1));
        Assert.Empty(Names("head", true, 1));   // no heavy rear clip lists the head
        Assert.Equal(["back blow low"], Names("chest", true, 1));
    }

    [Fact]
    public void A_technique_follows_the_combat_timing_and_a_hit_plays_its_reaction_that_moves_the_character()
    {
        var lengths = Lengths().Add("Cut left", 1.0f).Add("mid blow", 2.0f)
            .AddRoot("Cut left", [0, 1], [Vector2.Zero, new Vector2(0, 7)])
            .AddRoot("mid blow", [0, 2], [Vector2.Zero, new Vector2(0, -10)]);
        var lib = CombatLibrary();
        var system = new AnimationSystem(lib, lengths);
        var a = new CharacterAnimation();
        var cold = new CharacterCold();
        const float dt = 1 / 30f;
        for (int i = 0; i < 10; i++) system.Update(a, cold, 0, dt, 1, 1, i);
        // An attack at 40 % plays its clip at 40 % of its length, alone (it is the whole body), and steps forward with its root track.
        int cut = lib.IndexOfAny("Cut left");
        var forward = Vector2.Zero;
        for (int i = 0; i < 12; i++) forward += system.Update(a, cold, 0, dt, 1, 1, 10 + i, combatView: CombatView.None with { Technique = cut, Progress = (i + 1) / 30f });
        var layers = system.Publish(a);
        Assert.Equal(0.4f, layers.Single(l => l.Name == "Cut left").Time, 3);
        Assert.DoesNotContain(layers, l => l.Name == "stand 1" && l.Weight > 0.01f);
        Assert.True(forward.Y > 0, $"the cut moved the character {forward}");
        // A heavy hit to the chest from the front: "mid blow" plays from its start and pushes the character back.
        var back = Vector2.Zero;
        for (int i = 0; i < 15; i++) back += system.Update(a, cold, 0, dt, 1, 1, 30 + i, combatView: CombatView.None with { HitTick = 29, HitHeavy = true, HitPart = "chest" });
        Assert.Equal(lib.IndexOfAny("mid blow"), a.Action);
        Assert.Equal(0.5f, a.ActionTime, 3);
        Assert.True(back.Y < 0, $"the hit moved the character {back}");
        // A block plays on the upper body and does not move the character.
        int block = lib.IndexOfAny("Block up");
        var still = Vector2.Zero;
        system.Update(a, cold, 0, dt, 1, 1, 50, combatView: CombatView.None with { Technique = block, Progress = 0 });
        Assert.Equal(-1, a.Action);   // the technique cut the reaction short (it fades out over a quarter second, still moving the character, as in Kenshi)
        for (int i = 1; i < 10; i++) system.Update(a, cold, 0, dt, 1, 1, 50 + i, combatView: CombatView.None with { Technique = block, Progress = i / 30f });
        for (int i = 10; i < 20; i++) still += system.Update(a, cold, 0, dt, 1, 1, 50 + i, combatView: CombatView.None with { Technique = block, Progress = i / 30f });
        Assert.Equal(Vector2.Zero, still);
    }

    // ---- in a world ----

    static (SimWorld World, Squad Squad) Start(int threads, ulong seed = 6)
    {
        var db = SyntheticTown.Database();
        var data = SyntheticTown.Data(db);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = StandardParts.Population | StandardParts.Player | StandardParts.Movement | StandardParts.Animation,
            Population = new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 5 }, SynchronousPaths = true, AnimationLibrary = Library(), AnimationLengths = Lengths(),
        });
        var population = systems.Population!;
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = true }, walk, systems.Systems);
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
