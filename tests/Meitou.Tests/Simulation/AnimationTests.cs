using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Tests.Bodies;
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
        var layers = system.Publish(a, []);
        Assert.Equal(0.4f, layers.Single(l => l.Name == "Cut left").Time, 3);
        Assert.DoesNotContain(layers, l => l.Name == "stand 1" && l.Weight > 0.01f);
        Assert.True(forward.Y > 0, $"the cut moved the character {forward}");
        // A heavy hit to the chest from the front, for which the combat system picked "mid blow": it plays from its start and pushes the character back.
        var back = Vector2.Zero;
        for (int i = 0; i < 15; i++) back += system.Update(a, cold, 0, dt, 1, 1, 30 + i, combatView: CombatView.None with { HitTick = 29, HitHeavy = true, HitPart = "chest", StumbleClip = lib.IndexOfAny("mid blow"), StumbleStart = 29 });
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

    [Fact]
    public void Root_motion_stops_at_another_body_and_keeps_the_sideways_part()
    {
        var world = new SimWorld(new WorldSettings { Seed = 1, Threads = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 150), []);
        // Facing +X (yaw 90 degrees), 10 units from a second character: two footprints of 4 leave 2 units to go.
        var a = world.Characters.Spawn(new CharacterHot { Position = new Vector3(1000, 150, 1000), Yaw = MathF.PI / 2, Health = 100 }, new CharacterCold(), 0);
        world.Characters.Spawn(new CharacterHot { Position = new Vector3(1010, 150, 1000), Health = 100 }, new CharacterCold(), 0);
        world.RunTick();   // builds the grid of these positions
        ref var hot = ref world.Characters.Next[a.Slot];
        AnimationSystem.Relocate(world, a.Slot, ref hot, new Vector2(0, 2));
        Assert.Equal(1002, hot.Position.X, 3);
        // A lunge of 7 more (and 3 to its right, world +Z) would end inside the other body: the closing part is dropped, the sideways part kept.
        AnimationSystem.Relocate(world, a.Slot, ref hot, new Vector2(-3, 7));
        Assert.Equal(1002, hot.Position.X, 3);
        Assert.Equal(1003, hot.Position.Z, 3);
        // Away from it, nothing is held back.
        AnimationSystem.Relocate(world, a.Slot, ref hot, new Vector2(0, -5));
        Assert.Equal(997, hot.Position.X, 3);
    }
    [Fact]
    public void A_leg_down_crawls_at_once_and_its_recovery_plays_the_get_up_clip()
    {
        // docs/game/ragdolls.md "Forced crawl": a leg below the KO point is no knockout and no fall clip, the character is a crawler (posture 2) from that tick; posture 2
        // ending in 0 queues the get-up task 65 (FUN_1405c7e20), `standing up 3`, which holds the character still.
        var lib = new AnimationLibrary([Def("stand 1", AnimationArea.All, idle: true, right: 0), Def("standing up 3", AnimationArea.All) with { DisablesMovement = true }]);
        var system = new AnimationSystem(lib, new AnimationLengths().Add("standing up 3", 2f));
        var body = BodyFixtures.Healthy();
        var ctx = BodyFixtures.Context(toughness: 50);
        var cold = new CharacterCold { Medical = body };
        var a = new CharacterAnimation();
        const float dt = 1 / 30f;
        for (int i = 0; i < 5; i++) system.Update(a, cold, 0, dt, 1, 1, i);
        body.Parts[BodyFixtures.RLeg].Flesh = -80;
        body.Tick(1e-4f, ctx);
        Assert.True(body.Crawling);
        Assert.False(body.Unconscious);
        system.Update(a, cold, 0, dt, 1, 1, 5);
        Assert.Equal(-1, a.Action);   // no fall and no get-up clip: it goes straight to the crawl
        body.Parts[BodyFixtures.RLeg].Flesh = 100;
        body.Tick(1e-4f, ctx);
        Assert.False(body.Crawling);
        system.Update(a, cold, 0, dt, 1, 1, 6);
        Assert.Equal(lib.IndexOf("standing up 3"), a.Action);
        Assert.True(a.HoldsStill);
    }

    [Fact]
    public void A_stopped_crawler_whose_leg_is_past_minus_101_per_cent_still_plays_a_crawl_idle()
    {
        // The records' leg minimum -999 is read as -101, so a severed leg (health under -100 %) fits no crawl idle; the original's idle draw (FUN_14051ec80) then takes the first of
        // its list rather than none, which left the character in the binding pose.
        var lib = new AnimationLibrary([Def("stand 1", AnimationArea.All, idle: true, right: 0),
            Def("crawl idle down", AnimationArea.Lower, idle: true) with { Category = AnimationLibrary.GroundedCategory, Prone = true, LeftLegMin = -999, RightLegMin = -999 }]);
        var system = new AnimationSystem(lib, new AnimationLengths());
        var body = BodyFixtures.Healthy();
        var cold = new CharacterCold { Medical = body };
        var a = new CharacterAnimation();
        body.Parts[BodyFixtures.RLeg].Flesh = -5 * body.Parts[BodyFixtures.RLeg].MaxHp;
        body.Tick(1e-4f, BodyFixtures.Context(toughness: 50));
        Assert.True(body.Crawling);
        for (int i = 0; i < 5; i++) system.Update(a, cold, 0, 1 / 30f, 1, 1, i);
        Assert.Contains(system.Publish(a, []), l => l.Name == "crawl idle down" && l.Weight > 0.5f);
    }
}
