using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Bodies;

public class HungerTests
{
    [Fact]
    public void Starving_characters_pass_out_and_then_die_at_zero()
    {
        var m = Healthy();
        var events = new List<MedicalEvent>();
        // 200 game hours
        float seconds = 200 / MedicalState.GameHoursPerSecond;
        float step = 0.25f / MedicalState.GameHoursPerSecond;
        Run(m, Context(toughness: 0), seconds, step: step, events);
        Assert.True(m.Dead);
        Assert.Equal(DeathCause.Starvation, m.Cause);
        Assert.Contains(events, e => e.Kind == MedicalEventKind.KnockedOut);
        Assert.Contains(events, e => e is { Kind: MedicalEventKind.Died, Cause: DeathCause.Starvation });
    }
}

public class BloodAndKoTests
{
    [Fact]
    public void Ko_time_is_the_base_plus_the_documented_terms()
    {
        var m = Healthy();
        var ctx = Context(toughness: 0);
        Assert.Equal(20f, m.KoTime(ctx));   // a healthy body adds nothing
        // Head flesh -20, blood 24 under the capacity 112.5, k = 3 at toughness 0:
        //   vital head: (100 - (-20 + 0)) x 3 x 0.25 = 90; head h - s < 0: 20 x 3 x 2 = 120; blood: 24 x 3 x 0.5 / 112.5 x 100 = 32
        m.Parts[HeadIx].Flesh = -20;
        m.Blood = 112.5f - 24;
        Assert.Equal(20 + 90 + 120 + 32, m.KoTime(ctx), 3);
        // The same body with toughness 100: k = 0.75.
        Assert.Equal(20 + (90 + 120 + 32) * 0.25f, m.KoTime(Context(toughness: 100)), 3);
    }

    [Fact]
    public void Destroying_a_vital_part_kills_and_a_limb_is_severed_subject_to_the_setting()
    {
        var dead = Healthy();
        var events = new List<MedicalEvent>();
        dead.ApplyHit(HeadIx, new HitDamage(250, 0, 0), Context(), events);
        Assert.True(dead.Dead);
        Assert.Equal(DeathCause.VitalPart, dead.Cause);

        var rare = Healthy();
        rare.ApplyHit(RArm, new HitDamage(250, 0, 0), Context(), events);
        Assert.Equal(LimbState.Stump, rare.Parts[RArm].Limb);
        Assert.Equal(5f, rare.Parts[RArm].MaxHp);
        Assert.Contains(events, e => e is { Kind: MedicalEventKind.PartSevered, Part: RArm });
        Assert.True(rare.Bleeding == 0);                     // set by the next tick
        rare.Tick(0.01f, Context());
        Assert.True(rare.Bleeding > 0);

        var never = Healthy();
        never.ApplyHit(RArm, new HitDamage(250, 0, 0), Context(options: new BodyOptions { Dismemberment = Dismemberment.Never }));
        Assert.Equal(LimbState.Original, never.Parts[RArm].Limb);

        var noStats = Healthy();
        noStats.ApplyHit(RArm, new HitDamage(250, 0, 0), Context() with { HasStats = false });
        Assert.Equal(LimbState.Original, noStats.Parts[RArm].Limb);
    }

    [Fact]
    public void Wounds_clot_and_blood_recovers_once_bleeding_stops()
    {
        // Never hungry, so 4000 seconds can pass.
        var race = new RaceData { StringId = "x", HungerRate = 0, Anatomy = Human.Anatomy };
        var m = Healthy(race);
        var ctx = Context(race);
        m.ApplyHit(LArm, new HitDamage(20, 0, 0), ctx);   // wound 20 x 0.001 = 0.02
        Assert.Equal(0.02f, m.Wounds[0], 5);
        float cap = MedicalState.BloodCapacity(race, 50);
        m.Tick(1, ctx);
        Assert.Equal(0.02f - 0.00085f, m.Wounds[0], 5);
        Assert.Equal((0.02f - 0.00085f) * 1.2f, m.Bleeding, 5);
        float lowest = m.Blood;
        Run(m, ctx, 40);
        Assert.Empty(m.Wounds);
        Assert.True(m.Blood > lowest);
        Run(m, ctx, 4000, step: 1);
        Assert.Equal(cap, m.Blood, 3);
    }
}

public class LegCollapseTests
{
    /// <summary>
    /// A body whose right leg has flesh <paramref name="flesh"/>, bandage <paramref name="bandage"/> and rig <paramref name="rig"/>,
    /// ticked once so it holds the KO point of toughness 50.
    /// </summary>
    static MedicalState Leg(float flesh, float bandage = 0, float rig = 0, LimbState limb = LimbState.Original)
    {
        var m = Healthy();
        var leg = m.Parts[RLeg];
        (leg.Flesh, leg.Bandage, leg.Rig, leg.Limb) = (flesh, bandage, rig, limb);
        m.Tick(1e-4f, Context(toughness: 50));
        return m;
    }

    [Fact]
    public void One_leg_crawls_below_the_ko_point_not_below_zero_and_bandage_not_rig_lifts_it()
    {
        // docs/game/ragdolls.md "Forced crawl": KO point at toughness 50 = -(10 + 75 x 0.5) = -47.5.
        Assert.Equal(-47.5f, Leg(100).LegKoPoint, 3);
        var limping = Leg(-20);
        Assert.True(limping.Parts[RLeg].IsDown);             // under 0 by the arm rule, but a leg only limps there
        Assert.False(limping.Crawling);
        Assert.True(Leg(-60).Crawling);                      // past the KO point: down
        Assert.False(Leg(-60, bandage: 30).Crawling);        // h - s + b = -30: up again
        Assert.True(Leg(-60, rig: 40).Crawling);             // a splint does not count in this test
        Assert.True(Leg(100, limb: LimbState.Stump).Crawling);   // a stump is always down
    }
}

public class DeterminismTests
{
    [Fact]
    public void Save_keys_round_trip()
    {
        var m = Healthy();
        m.ApplyHit(LArm, new HitDamage(30, 10, 0), Context());
        m.Hunger = 1.7f;
        m.Fed = 0.2f;
        var floats = new Dictionary<string, float>();
        var bools = new Dictionary<string, bool>();
        var strings = new Dictionary<string, string>();
        m.WriteSave(floats, bools, strings);
        Assert.Equal(1.7f, floats["hung"]);
        Assert.Equal(7, Enumerable.Range(0, 20).Count(k => strings.ContainsKey("sid" + k)));
        Assert.Equal(70f, floats["flesh" + LArm]);
        Assert.Equal(10f, floats["stun" + LArm]);
        Assert.False(bools["dead"]);
        var byId = Human.Anatomy.ToDictionary(a => a.Part.StringId, a => a.Part);
        var back = MedicalState.ReadSave(Human, floats, bools, strings, id => byId.GetValueOrDefault(id));
        Assert.Equal(m.Parts.Count, back.Parts.Count);
        Assert.Equal(m.Blood, back.Blood);
        Assert.Equal(1.7f, back.Hunger);
        for (int k = 0; k < m.Parts.Count; k++)
        {
            Assert.Equal(m.Parts[k].Flesh, back.Parts[k].Flesh);
            Assert.Equal(m.Parts[k].Stun, back.Parts[k].Stun);
            Assert.Equal(m.Parts[k].HitWeight, back.Parts[k].HitWeight);
            Assert.Equal(m.Parts[k].Template.StringId, back.Parts[k].Template.StringId);
        }
    }
}

/// <summary>
/// Every toughness read of the medical code is stat x stats multiplier through an unclamped lerp (docs/game/character-stats.md "coma point").
/// </summary>
public class ToughnessReadTests
{
    [Fact]
    public void An_animals_toughness_is_scaled_by_its_stats_multiplier()
    {
        var stats = new CharacterStats { Scale = 0.5f };
        stats[StatsEnumerated.Toughness] = 80;
        Assert.Equal(40f, stats.ScaledToughness, 3);
        Assert.Equal(MedicalState.KoPoint(BaseGame, 40), MedicalState.KoPoint(BaseGame, stats.ScaledToughness));
        Assert.Equal(-40f, MedicalState.KoPoint(BaseGame, stats.ScaledToughness), 3);
    }
}
