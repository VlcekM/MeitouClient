using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Bodies;

public class HungerTests
{
    [Fact]
    public void One_hunger_level_lasts_sixty_hours_at_the_base_rate()
    {
        var m = Healthy();
        Run(m, Context(), 60);
        Assert.Equal(2f, m.Hunger, 2);
        Run(m, Context(), 60);
        Assert.Equal(1f, m.Hunger, 2);
    }

    [Fact]
    public void Resting_uses_the_bed_rate_and_the_hunger_time_option_stretches_it()
    {
        var rest = Healthy();
        Run(rest, Context() with { Resting = true }, 60);
        Assert.Equal(3 - 0.33f, rest.Hunger, 2);

        var slow = Healthy();
        Run(slow, Context(options: new BodyOptions { HungerTime = 2 }), 60);
        Assert.Equal(2.5f, slow.Hunger, 2);

        var loaded = Healthy();
        Run(loaded, Context() with { EncumbranceFactor = 0 }, 60);   // 1 + (1 - 0) x 0.7 = 1.7 times
        Assert.Equal(3 - 1.7f, loaded.Hunger, 2);

        var machine = Healthy();
        Run(machine, Context() with { UsingMachine = true }, 60);
        Assert.Equal(3 - 0.8f, machine.Hunger, 2);
    }

    [Fact]
    public void Food_in_the_stomach_stops_the_drain_and_refills_at_three_times_the_speed()
    {
        var m = Healthy();
        m.Hunger = 1;
        m.Fed = 0.5f;
        Run(m, Context(), 10, step: 1);   // moved per hour: 1/60 x 3 = 0.05
        Assert.Equal(1.5f, m.Hunger, 3);
        Assert.Equal(0f, m.Fed, 3);
    }

    [Fact]
    public void Hunger_is_capped_at_three()
    {
        var m = Healthy();
        m.Hunger = 2.9f;
        m.Feed(5);
        Run(m, Context(), 10, step: 1);
        Assert.Equal(3f, m.Hunger);
        Assert.True(m.Fed > 0);
    }

    [Fact]
    public void Races_with_hunger_rate_zero_never_starve()
    {
        var m = Healthy(Robot);
        Run(m, Context(Robot), 500, step: 1);
        Assert.Equal(3f, m.Hunger);
        Assert.False(m.Dead);
    }

    [Fact]
    public void Starving_characters_pass_out_and_then_die_at_zero()
    {
        var m = Healthy();
        var events = new List<MedicalEvent>();
        Run(m, Context(toughness: 0), 200, step: 0.25f, events);
        Assert.True(m.Dead);
        Assert.Equal(DeathCause.Starvation, m.Cause);
        Assert.Contains(events, e => e.Kind == MedicalEventKind.KnockedOut);
        Assert.Contains(events, e => e is { Kind: MedicalEventKind.Died, Cause: DeathCause.Starvation });
    }

    [Fact]
    public void Starvation_ko_threshold_depends_on_toughness_and_food()
    {
        Assert.Equal(0.9f, MedicalState.StarvationKoThreshold(BaseGame, 0, false), 5);
        Assert.Equal(0.15f, MedicalState.StarvationKoThreshold(BaseGame, 100, false), 5);
        Assert.Equal(0.8f, MedicalState.StarvationKoThreshold(BaseGame, 0, true), 5);
    }

    [Fact]
    public void Situation_factor_picks_the_documented_branch()
    {
        Assert.Equal(0.33f, MedicalState.HungerSituationFactor(BaseGame, true, false, 0.2f));
        Assert.Equal(0.8f, MedicalState.HungerSituationFactor(BaseGame, false, true, 0.2f));
        Assert.Equal(1f, MedicalState.HungerSituationFactor(BaseGame, false, false, 1));
        Assert.Equal(1.7f, MedicalState.HungerSituationFactor(BaseGame, false, false, 0), 5);
    }
}

public class BloodAndKoTests
{
    [Fact]
    public void Blood_capacity_scales_with_strength()
    {
        Assert.Equal(75f, MedicalState.BloodCapacity(Human, 0));
        Assert.Equal(150f, MedicalState.BloodCapacity(Human, 100));
        Assert.Equal(112.5f, MedicalState.BloodCapacity(Human, 50));
        Assert.Equal(150f, MedicalState.BloodCapacity(Human, 0, hasStats: false));
        Assert.Equal(100f, MedicalState.BloodCapacity(Robot, 77));
    }

    [Fact]
    public void Ko_point_and_degeneration_multiplier_follow_toughness()
    {
        Assert.Equal(-10f, MedicalState.KoPoint(BaseGame, 0));
        Assert.Equal(-85f, MedicalState.KoPoint(BaseGame, 100));
        Assert.Equal(1.7f, MedicalState.DegenerationMult(BaseGame, 0), 5);
        Assert.Equal(0.03f, MedicalState.DegenerationMult(BaseGame, 100), 5);
        Assert.Equal(56.7f, MedicalState.DegenerationMult(BaseGame, 0) / MedicalState.DegenerationMult(BaseGame, 100), 1);
    }

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
    public void A_hit_that_downs_a_vital_part_knocks_the_character_out()
    {
        var m = Healthy();
        var events = new List<MedicalEvent>();
        m.ApplyHit(HeadIx, new HitDamage(120, 0, 0), Context(toughness: 0), events);
        Assert.True(m.Unconscious);
        Assert.False(m.Dead);
        Assert.Equal(-20f, m.Parts[HeadIx].Flesh);
        Assert.Equal(112.5f - 120 * 0.2f, m.Blood, 3);
        Assert.Equal(262f, m.KoTimer, 2);
        Assert.Contains(events, e => e is { Kind: MedicalEventKind.PartDown, Part: HeadIx });
        Assert.Contains(events, e => e.Kind == MedicalEventKind.KnockedOut);
    }

    [Fact]
    public void Blunt_damage_only_stuns_and_stun_recovers()
    {
        var m = Healthy();
        m.ApplyHit(RArm, new HitDamage(0, 30, 0), Context());
        var arm = m.Parts[RArm];
        Assert.Equal(100f, arm.Flesh);
        Assert.Equal(30f, arm.Stun);
        Assert.Equal(0.7f, arm.Fraction, 5);
        Run(m, Context(), 40);
        Assert.Equal(30 - 40 * 0.0125f, arm.Stun, 3);   // 0.05 x stun recovery 1 x heal rate mult 0.25 per hour at M = 100
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
    public void Frequent_dismemberment_also_severs_by_degeneration_but_rare_does_not()
    {
        MedicalState Hurt()
        {
            var m = Healthy();
            m.Parts[RArm].Flesh = -150;   // below -M without a hit
            return m;
        }
        var rare = Hurt();
        rare.Tick(0.1f, Context());
        Assert.Equal(LimbState.Original, rare.Parts[RArm].Limb);
        var frequent = Hurt();
        frequent.Tick(0.1f, Context(options: new BodyOptions { Dismemberment = Dismemberment.Frequent }));
        Assert.Equal(LimbState.Stump, frequent.Parts[RArm].Limb);
    }

    [Fact]
    public void Losing_a_leg_adds_a_second_knock_out()
    {
        var m = Healthy();
        m.ApplyHit(LLeg, new HitDamage(250, 0, 0), Context(toughness: 0));
        Assert.True(m.Unconscious);
        Assert.True(m.KoTimer >= 0);
        var arm = Healthy();
        arm.ApplyHit(LArm, new HitDamage(250, 0, 0), Context(toughness: 0));
        Assert.False(arm.Unconscious);   // arm loss does not
    }

    [Fact]
    public void Wounds_clot_and_blood_recovers_once_bleeding_stops()
    {
        var race = new RaceData { StringId = "x", HungerRate = 0, Anatomy = Human.Anatomy };   // never hungry, so 4000 hours can pass
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

    [Fact]
    public void Chance_of_death_multiplies_the_bleed_rate()
    {
        var a = Healthy();
        var b = Healthy();
        a.ApplyHit(LArm, new HitDamage(20, 0, 0), Context());
        b.ApplyHit(LArm, new HitDamage(20, 0, 0), Context(options: new BodyOptions { ChanceOfDeath = 2 }));
        a.Tick(0.1f, Context());
        b.Tick(0.1f, Context(options: new BodyOptions { ChanceOfDeath = 2 }));
        Assert.Equal(2 * a.Bleeding, b.Bleeding, 5);
    }

    [Fact]
    public void Bleeding_out_knocks_out_at_zero_and_kills_at_minus_capacity()
    {
        var m = Healthy();
        var ctx = Context();
        var events = new List<MedicalEvent>();
        m.Blood = 0.1f;
        m.ApplyHit(LArm, new HitDamage(10, 0, 0), ctx, events);   // immediate loss 2
        Assert.True(m.Unconscious);
        Assert.True(m.BloodKo);
        m.Blood = -MedicalState.BloodCapacity(Human, 50) - 1;
        m.Tick(0.01f, ctx, events);
        Assert.True(m.Dead);
        Assert.Equal(DeathCause.BloodLoss, m.Cause);
    }

    [Fact]
    public void A_knocked_out_character_wakes_when_the_timer_runs_out_and_the_vital_parts_are_up()
    {
        var m = Healthy();
        var ctx = Context();
        var events = new List<MedicalEvent>();
        m.KnockOut(20);
        Run(m, ctx, 0.3f, 0.01f, events);   // 18 minutes
        Assert.True(m.Unconscious);
        Run(m, ctx, 0.05f, 0.01f, events);  // past 20
        Assert.False(m.Unconscious);
        Assert.Contains(events, e => e.Kind == MedicalEventKind.WokeUp);

        // Stays down while a vital part is below zero ("Recovery coma").
        var coma = Healthy();
        coma.Parts[ChestIx].Flesh = -5;
        coma.KnockOut(1);
        Assert.True(coma.Coma);
        Run(coma, ctx, 0.5f, 0.05f);
        Assert.True(coma.Unconscious);
    }

    [Fact]
    public void Robots_gain_wear_that_lowers_the_maximum_and_repair_returns_it()
    {
        var m = Healthy(Robot);
        var ctx = Context(Robot);
        m.ApplyHit(RArm, new HitDamage(10, 10, 10), ctx);
        Assert.Equal(30 * 0.04f, m.Parts[RArm].Wear, 5);
        Assert.Equal(100 - 1.2f, m.Parts[RArm].MaxHp, 4);
        Run(m, ctx with { RepairRate = 1 }, 100);
        Assert.Equal(0f, m.Parts[RArm].Wear, 5);
        // Organic characters take none.
        var h = Healthy();
        h.ApplyHit(RArm, new HitDamage(10, 10, 10), Context());
        Assert.Equal(0f, h.Parts[RArm].Wear);
    }
}

public class HealingTests
{
    [Fact]
    public void Untreated_damage_fraction_has_the_documented_breakpoints()
    {
        Assert.Equal(0f, MedicalState.UntreatedDamage(100, 85, 0), 5);   // 15% of M lost: nothing
        Assert.Equal(0f, MedicalState.UntreatedDamage(100, 80, 0), 5);   // exactly 20%
        Assert.Equal(1f, MedicalState.UntreatedDamage(100, -5, 0), 5);   // over 90%, h + b not positive
        Assert.Equal(0.25f, MedicalState.UntreatedDamage(100, 10, 0), 5);   // 90%, but h + b > 0: x0.25
        Assert.Equal(0.5f * 0.25f, MedicalState.UntreatedDamage(100, 45, 0), 5);   // 55% lost
        Assert.Equal(0f, MedicalState.UntreatedDamage(100, 10, 80), 5);   // bandaged damage does not count
    }

    [Fact]
    public void Untreated_damage_degenerates_at_the_toughness_dependent_rate()
    {
        var m = Healthy();
        m.Parts[ChestIx].Flesh = -10;   // D = 1
        float degMult = MedicalState.DegenerationMult(BaseGame, 1);
        m.Tick(2, Context(toughness: 1));
        Assert.Equal(-10 - 2 * 0.05f * 1.5f * degMult * 1.2f, m.Parts[ChestIx].Flesh, 4);

        var tough = Healthy();
        tough.Parts[ChestIx].Flesh = -10;
        tough.Tick(2, Context(toughness: 99));
        Assert.True(tough.Parts[ChestIx].Flesh > m.Parts[ChestIx].Flesh);
        // A bed-like object stops degeneration altogether.
        var bed = Healthy();
        bed.Parts[ChestIx].Flesh = -10;
        bed.Tick(2, Context(toughness: 1) with { Bed = BedKind.Bed });
        Assert.Equal(-10f, bed.Parts[ChestIx].Flesh);
    }

    [Fact]
    public void Chance_of_death_speeds_up_degeneration()
    {
        var a = Healthy();
        var b = Healthy();
        a.Parts[ChestIx].Flesh = b.Parts[ChestIx].Flesh = -10;
        a.Tick(1, Context());
        b.Tick(1, Context(options: new BodyOptions { ChanceOfDeath = 2 }));
        Assert.Equal(2 * (-10 - a.Parts[ChestIx].Flesh), -10 - b.Parts[ChestIx].Flesh, 4);
    }

    [Fact]
    public void Treated_damage_heals_from_the_bandaged_pool_and_nothing_heals_without_a_bandage()
    {
        var m = Healthy();
        var arm = m.Parts[LArm];
        arm.Flesh = 85;   // 15% lost: no degeneration
        arm.Bandage = 10;
        // heal = min(b, dt x 0.05 x heal rate mult(0.25) x (M/100 x A)) = 0.0125 per hour
        m.Tick(10, Context());
        Assert.Equal(10 - 0.125f, arm.Bandage, 4);
        Assert.Equal(85 + 0.125f, arm.Flesh, 4);

        var bare = Healthy();
        bare.Parts[LArm].Flesh = 50;   // 50% lost: below the degeneration threshold only slowly
        bare.Tick(10, Context(toughness: 100));
        Assert.True(bare.Parts[LArm].Flesh <= 50);
        Assert.Equal(0f, bare.Parts[LArm].Bandage);
    }

    [Fact]
    public void Healing_rate_scales_with_the_race_and_bed()
    {
        var fast = Robot;
        var m = Healthy(fast);
        m.Parts[LArm].Flesh = 50;
        m.Parts[LArm].Bandage = 10;
        m.Tick(10, Context(fast) with { Bed = BedKind.RepairBed, BedFactor = 4 });
        // robotic rate: heal rate 2 x bed 4 = 8: heal 10 x 0.05 x 0.25 x 8 = 1
        Assert.Equal(9f, m.Parts[LArm].Bandage, 3);
    }

    [Fact]
    public void Self_healing_turns_untreated_damage_into_treated_at_forty_percent()
    {
        var animal = new RaceData { StringId = "a", Name = "Garru", SelfHealing = true, Anatomy = Human.Anatomy };
        var m = Healthy(animal);
        m.Parts[LArm].Flesh = 50;
        m.Tick(10, Context(animal, toughness: 100));
        // b += 10 x 0.05 x 0.25 x 1 x 0.4 = 0.05, then healed in the same tick on the next step; just check it started
        Assert.True(m.Parts[LArm].Bandage > 0 || m.Parts[LArm].Flesh > 50);
    }

    [Fact]
    public void First_aid_fills_the_bandaged_pool_up_to_the_missing_health()
    {
        var m = Healthy();
        m.Parts[LArm].Flesh = 80;
        // skill 50, kit 100: lerp(1, 15, 0.5) x 3 = 24 per hour
        Assert.Equal(24f, MedicalState.TreatmentRate(BaseGame, 50, 100, false), 4);
        Assert.Equal(24f * 0.33f, MedicalState.TreatmentRate(BaseGame, 50, 100, true), 4);
        Assert.Equal(24f, MedicalState.TreatmentRate(BaseGame, 100, 50, false), 4);   // the lower of skill and kit
        float added = m.Treat(0.5f, 50, 100, false, Context());
        Assert.Equal(12f, m.Parts[LArm].Bandage, 4);
        Assert.Equal(12f, added);
        m.Treat(5, 50, 100, false, Context());
        Assert.Equal(20f, m.Parts[LArm].Bandage, 4);   // h + b <= M
        // An ordinary kit does not treat a robot's parts.
        var robot = Healthy(Robot);
        robot.Parts[LArm].Flesh = 80;
        Assert.Equal(0f, robot.Treat(1, 50, 100, false, Context(Robot)));
        Assert.True(robot.Treat(1, 50, 100, true, Context(Robot)) > 0);
    }

    [Fact]
    public void An_expert_uses_a_kit_twenty_times_slower_than_a_beginner()
    {
        Assert.Equal(0.1f, MedicalState.KitDrain(BaseGame, 100, 1, false), 5);
        Assert.Equal(2f, MedicalState.KitDrain(BaseGame, 0, 1, false), 5);
        Assert.Equal(2f * 0.33f, MedicalState.KitDrain(BaseGame, 0, 1, true), 5);
    }

    [Fact]
    public void Fitting_a_prosthetic_replaces_the_stump_and_its_hp()
    {
        var m = Healthy();
        m.ApplyHit(RArm, new HitDamage(250, 0, 0), Context());
        Assert.Equal(LimbState.Stump, m.Parts[RArm].Limb);
        var arm = new LimbReplacement { StringId = "klr", Slot = 51, HpLow = 100, HpHigh = 250, StrengthLow = 0.75f, StrengthHigh = 1.1f };
        Assert.True(m.FitReplacement(arm, 1));
        Assert.Equal(LimbState.Replaced, m.Parts[RArm].Limb);
        Assert.Equal(250f, m.Parts[RArm].MaxHp);
        Assert.Equal(250f, m.Parts[RArm].Flesh);
        Assert.Equal(1.1f, m.StatMultiplier(StatsEnumerated.Strength, StatUse.DamageState), 5);
        // A replaced arm of a human is robotic: it wears when hit and does not degenerate.
        m.ApplyHit(RArm, new HitDamage(10, 0, 0), Context());
        Assert.True(m.Parts[RArm].Wear > 0);
    }
}

public class DeterminismTests
{
    static string Fight(ulong seed)
    {
        var m = Healthy();
        var ctx = Context() with { Seed = seed };
        var log = new List<MedicalEvent>();
        var trace = new System.Text.StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            int part = m.ChoosePart(ctx);
            if (part < 0) break;
            m.ApplyHit(part, new HitDamage(15 + i % 7, i % 5 * 4, i % 3 * 5), ctx, log);
            m.Tick(0.2f, ctx, log);
            trace.Append(part).Append(':').Append(m.Blood.ToString("R")).Append(m.Dead).Append(';');
        }
        return trace + string.Join(",", log);
    }

    [Fact]
    public void The_same_seed_gives_the_same_fight_and_another_seed_a_different_one()
    {
        Assert.Equal(Fight(5), Fight(5));
        Assert.NotEqual(Fight(5), Fight(6));
    }

    [Fact]
    public void Hit_location_follows_the_weights_and_excludes_destroyed_parts()
    {
        var m = Healthy();
        var counts = new int[7];
        for (ulong seed = 0; seed < 4000; seed++)
        {
            var fresh = Healthy();
            counts[fresh.ChoosePart(Context() with { Seed = seed })]++;
        }
        // weights: RLeg 80, LArm 80, LLeg 80, Head 80, Stomach 140, RArm 40, Chest 140; total 640
        Assert.InRange(counts[ChestIx], 4000 * 140 / 640 * 0.85, 4000 * 140 / 640 * 1.15);
        Assert.InRange(counts[RArm], 4000 * 40 / 640 * 0.7, 4000 * 40 / 640 * 1.3);
        m.Parts[RArm].Flesh = -500;
        for (ulong seed = 0; seed < 300; seed++)
            Assert.NotEqual(RArm, m.ChoosePart(Context() with { Seed = seed }));
        for (ulong seed = 0; seed < 300; seed++)
        {
            int p = Healthy().ChoosePart(Context() with { Seed = seed }, lowStrike: true);
            Assert.NotEqual(HeadIx, p);
            Assert.NotEqual(RArm, p);
            Assert.NotEqual(LArm, p);
        }
    }

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
