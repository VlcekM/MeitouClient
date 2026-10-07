using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Bodies;

public class StatMultiplierTests
{
    const StatUse Usual = StatUse.Hunger | StatUse.Limbs | StatUse.Pain | StatUse.DamageState;

    static void Down(MedicalState m, int part) => m.Parts[part].Flesh = -5;

    [Fact]
    public void A_healthy_fed_character_has_multiplier_one_for_every_stat()
    {
        var m = Healthy();
        for (int i = 1; i < StatsInfo.Count; i++)
            Assert.Equal(1f, m.StatMultiplier((StatsEnumerated)i, StatUse.All), 5);
    }

    [Fact]
    public void Hunger_penalty_uses_each_rows_floor()
    {
        var m = Healthy();
        m.Hunger = 1;   // the term is 1 at level 2 and above and falls to h0 at level 1
        // strength: h0 0, f0 0.1 -> m = 0 -> 0.1
        Assert.Equal(0.1f, m.StatMultiplier(StatsEnumerated.Strength), 5);
        // athletics: h0 0.7, f0 0.5 -> lerp(0.5, 1, 0.7) = 0.85
        Assert.Equal(0.85f, m.StatMultiplier(StatsEnumerated.Athletics), 5);
        // melee attack: h0 0.75, f0 0.7 -> lerp(0.7, 1, 0.75) = 0.925
        Assert.Equal(0.925f, m.StatMultiplier(StatsEnumerated.MeleeAttack), 5);
        // stealth: h0 0.9, f0 0.8 -> 0.98
        Assert.Equal(0.98f, m.StatMultiplier(StatsEnumerated.Stealth), 5);
        m.Hunger = 2;
        Assert.Equal(1f, m.StatMultiplier(StatsEnumerated.Strength));
        m.Hunger = 1.5f;
        Assert.Equal(0.55f, m.StatMultiplier(StatsEnumerated.Strength), 5);   // lerp(0.1, 1, 0.5)
        Assert.Equal(1f, m.StatMultiplier(StatsEnumerated.Toughness));        // rows without a case are 1
    }

    [Fact]
    public void Flags_switch_the_terms_off()
    {
        var m = Healthy();
        m.Hunger = 1;
        Assert.Equal(1f, m.StatMultiplier(StatsEnumerated.Strength, StatUse.Limbs));
        Down(m, LArm);
        Down(m, RArm);
        Assert.Equal(0.925f, m.StatMultiplier(StatsEnumerated.MeleeAttack, StatUse.Hunger), 5);   // hunger 1 only; arms do not count
    }

    [Fact]
    public void Both_arms_down_follow_the_arm_rows()
    {
        var m = Healthy();
        Down(m, LArm);
        Down(m, RArm);
        // A = 0, T = 1. strength: L = lerp(0.75, 1, 0) = 0.75 -> lerp(0.1, 1, 0.75) = 0.775, times 0.66 for a disabled arm
        Assert.Equal(0.775f * 0.66f, m.StatMultiplier(StatsEnumerated.Strength), 5);
        // melee attack: L = 0 -> f0 0.7 (x0.66)
        Assert.Equal(0.7f * 0.66f, m.StatMultiplier(StatsEnumerated.MeleeAttack), 5);
        // labouring: both arms disabled; L = lerp(0, 1, 0) -> f0 0.25 (no 0.66: not in the arm list)
        Assert.Equal(0.25f, m.StatMultiplier(StatsEnumerated.Labouring), 5);
        // thievery: L = lerp(0.4, 1, 0) = 0.4 -> lerp(0.25, 1, 0.4) = 0.55, x0.66
        Assert.Equal(0.55f * 0.66f, m.StatMultiplier(StatsEnumerated.Thieving), 5);
        // assassin: limb = A x 0.01 = 0 -> L = 0 -> f0 0.5, x0.66
        Assert.Equal(0.5f * 0.66f, m.StatMultiplier(StatsEnumerated.Assassination), 5);
        // stealth keeps its A term but not the 0.66: L = lerp(0.9, 1, 0) = 0.9 -> lerp(0.8, 1, 0.9) = 0.98
        Assert.Equal(0.98f, m.StatMultiplier(StatsEnumerated.Stealth), 5);
        // athletics does not look at the arms at all
        Assert.Equal(1f, m.StatMultiplier(StatsEnumerated.Athletics));
    }

    [Fact]
    public void One_disabled_arm_only_costs_the_066_because_the_other_is_the_best_arm()
    {
        var m = Healthy();
        Down(m, LArm);
        Assert.Equal(0.66f, m.StatMultiplier(StatsEnumerated.Strength), 5);
        Assert.Equal(1f, m.StatMultiplier(StatsEnumerated.Strength, StatUse.Hunger | StatUse.Limbs | StatUse.Pain));
        Assert.Equal(1f, m.StatMultiplier(StatsEnumerated.Labouring));   // not in the 0.66 list
    }

    [Fact]
    public void Head_rows_fall_through_but_differ_in_the_pain_term()
    {
        var m = Healthy();
        m.Parts[HeadIx].Flesh = 50;   // Hd = 0.5
        // science / robotics / cooking and medic / turrets share h0 0.6, l0 0.4, f0 0.25: L = lerp(0.4, 1, 0.5) = 0.7 -> lerp(0.25, 1, 0.7) = 0.775
        Assert.Equal(0.775f, m.StatMultiplier(StatsEnumerated.Science), 5);
        Assert.Equal(0.775f, m.StatMultiplier(StatsEnumerated.Medic), 5);
        Assert.Equal(0.775f, m.StatMultiplier(StatsEnumerated.Turrets), 5);
        // perception: l0 0.5, f0 0.33 -> L = 0.75 -> lerp(0.33, 1, 0.75) = 0.8325
        Assert.Equal(0.8325f, m.StatMultiplier(StatsEnumerated.Perception), 5);
        Assert.Equal(0.8325f, m.StatMultiplier(StatsEnumerated.FriendlyFire), 5);
        // crossbows: like medic's limb row but with pain floor 0.5
        Assert.Equal(0.775f, m.StatMultiplier(StatsEnumerated.Crossbows), 5);
        // pain 0 (the full penalty): science x0.25, medic none, perception x0.25, crossbows x0.5
        m.Pain = 0;
        Assert.Equal(0.25f + 0.75f * (0.7f * 0.25f), m.StatMultiplier(StatsEnumerated.Science), 5);
        Assert.Equal(0.775f, m.StatMultiplier(StatsEnumerated.Medic), 5);
        Assert.Equal(0.33f + 0.67f * (0.75f * 0.25f), m.StatMultiplier(StatsEnumerated.Perception), 5);
        Assert.Equal(0.25f + 0.75f * (0.7f * 0.5f), m.StatMultiplier(StatsEnumerated.Crossbows), 5);
        // defence uses A x Hd
        Assert.Equal(0.8f + 0.2f * 0.5f, m.StatMultiplier(StatsEnumerated.MeleeDefence, StatUse.Limbs), 5);
    }

    [Fact]
    public void Torso_damage_enters_through_the_lowest_torso_part()
    {
        var m = Healthy();
        m.Parts[StomachIx].Flesh = 50;
        // strength: A x T = 0.5 -> L = lerp(0.75, 1, 0.5) = 0.875 -> lerp(0.1, 1, 0.875) = 0.8875
        Assert.Equal(0.8875f, m.StatMultiplier(StatsEnumerated.Strength), 5);
        // swimming: h0 0.5, l0 0, f0 0.01 -> L = 0.5 -> lerp(0.01, 1, 0.5) = 0.505
        Assert.Equal(0.505f, m.StatMultiplier(StatsEnumerated.Swimming), 5);
    }

    [Fact]
    public void Bonus_factors_apply_only_when_requested_and_only_to_their_rows()
    {
        var m = Healthy();
        var f = new StatFactors(0.9f, 0.8f, 0.5f, 0.7f, 0.5f);
        Assert.Equal(0.5f, m.StatMultiplier(StatsEnumerated.Katanas, StatUse.LimbItems, f), 5);
        Assert.Equal(0.5f, m.StatMultiplier(StatsEnumerated.Blunt, StatUse.LimbItems, f), 5);
        Assert.Equal(1f, m.StatMultiplier(StatsEnumerated.Katanas, StatUse.Hunger, f));
        Assert.Equal(0.8f, m.StatMultiplier(StatsEnumerated.Dexterity, StatUse.LimbItems, f), 5);
        Assert.Equal(0.9f, m.StatMultiplier(StatsEnumerated.Assassination, StatUse.LimbItems, f), 5);
        Assert.Equal(0.7f, m.StatMultiplier(StatsEnumerated.Crossbows, StatUse.LimbItems, f), 5);
        Assert.Equal((0.33f + 0.67f * 0.5f) * 0.7f, m.StatMultiplier(StatsEnumerated.FriendlyFire, StatUse.Hunger | StatUse.Limbs | StatUse.Stun | StatUse.LimbItems, f), 5);
        Assert.Equal(1f, m.StatMultiplier(StatsEnumerated.Strength, StatUse.LimbItems, f));
    }

    [Fact]
    public void Fitted_limbs_multiply_the_stats_they_name_and_legs_count_for_stealth_athletics_and_swimming()
    {
        var m = Healthy();
        var arm = new LimbReplacement { StringId = "a", Slot = 51, HpLow = 100, HpHigh = 100, StrengthLow = 0.5f, StrengthHigh = 1.1f, SwimmingLow = 0.75f, SwimmingHigh = 0.75f };
        var leg = new LimbReplacement { StringId = "l", Slot = 52, HpLow = 100, HpHigh = 100, AthleticsLow = 1, AthleticsHigh = 1.4f, StealthLow = 0.25f, StealthHigh = 0.95f, SwimmingLow = 0.8f, SwimmingHigh = 0.8f };
        m.ApplyHit(RArm, new HitDamage(250, 0, 0), Context());
        m.ApplyHit(LLeg, new HitDamage(250, 0, 0), Context());
        Assert.True(m.FitReplacement(arm, 0.5f));
        Assert.True(m.FitReplacement(leg, 0.5f));
        m.Hunger = 3;
        Assert.Equal(0.8f, m.StatMultiplier(StatsEnumerated.Strength, StatUse.DamageState), 5);   // lerp(0.5, 1.1, 0.5)
        Assert.Equal(1.2f, m.StatMultiplier(StatsEnumerated.Athletics, StatUse.DamageState), 5);
        Assert.Equal(0.6f, m.StatMultiplier(StatsEnumerated.Stealth, StatUse.DamageState), 5);
        Assert.Equal(0.75f * 0.8f, m.StatMultiplier(StatsEnumerated.Swimming, StatUse.DamageState), 5);
        Assert.Equal(1.2f, m.LegItemFactor(StatsEnumerated.Athletics), 5);
        Assert.Equal(1f, m.ArmItemFactor(StatsEnumerated.Athletics));
    }
}

public class SpeedAndEncumbranceTests
{
    [Theory]
    [InlineData(10f, 100f, 1f)]    // W <= C: no penalty
    [InlineData(100f, 100f, 1f)]
    public void Encumbrance_factor_is_one_up_to_the_capacity(float w, float c, float expected) =>
        Assert.Equal(expected, Encumbrance.Factor(w, c));

    [Fact]
    public void Encumbrance_factor_falls_to_zero_at_twenty_times_the_capacity()
    {
        Assert.Equal(0.9f / 1.9f, Encumbrance.Factor(200, 100), 5);   // about 0.47
        Assert.Equal(0f, Encumbrance.Factor(2000, 100), 5);
        Assert.Equal(0f, Encumbrance.Factor(5000, 100));
        Assert.Equal(1f, Encumbrance.Factor(0, 100));
    }

    [Fact]
    public void Capacity_and_carried_weight_use_the_constants()
    {
        Assert.Equal(1.2f * 50 + 15, Encumbrance.Capacity(BaseGame, 50), 5);
        Assert.Equal(1.2f * 50 * 0.5f + 15, Encumbrance.Capacity(BaseGame, 50, 0.5f), 5);
        Assert.Equal(40f + 30, Encumbrance.CarriedWeight(BaseGame, 40, true));
        Assert.Equal(5f, Encumbrance.WeaponWeight(BaseGame, 10));
    }

    [Fact]
    public void Run_speed_value_interpolates_the_races_range()
    {
        Assert.Equal(70f, Speed.RunValue(Human, 0));
        Assert.Equal(120f, Speed.RunValue(Human, 100));
        Assert.Equal(80f, Speed.RunValue(Human, 20));   // what the stand-in athletics of 20 gave
        Assert.Equal(40f, Speed.RunValue(Human, 20, slotFactor: 0));   // 0.5 + 0.5 x s
    }

    [Fact]
    public void Run_speed_chain_applies_legs_hunger_and_encumbrance_with_a_floor_and_a_water_cap()
    {
        var stats = new CharacterStats();
        stats[StatsEnumerated.Athletics] = 100;
        var m = Healthy();
        Assert.Equal(120f, Speed.Run(Human, stats, m, 1), 4);
        Assert.Equal(45f, Speed.Run(Human, stats, m, 1, shallowWater: true));
        // half encumbrance factor: (120 - 11) x 0.5 + 11
        Assert.Equal(109 * 0.5f + 11, Speed.Run(Human, stats, m, 0.5f), 4);
        // hunger 1 halves the (V - 11) part
        m.Hunger = 1;
        Assert.Equal(0.5f, Speed.HungerFactor(1));
        Assert.Equal(1f, Speed.HungerFactor(2));
        Assert.Equal(109 * 0.5f + 11, Speed.Run(Human, stats, m, 1), 4);
        m.Hunger = 3;
        // the weaker leg at 0.5 -> x0.9
        m.Parts[RLeg].Flesh = 50;
        Assert.Equal(0.9f, Speed.LegFactor(m), 5);
        Assert.Equal(109 * 0.9f + 11, Speed.Run(Human, stats, m, 1), 4);
        // a leg at 0.6 or better is not penalised at all (x1.8 clamps to 1)
        m.Parts[RLeg].Flesh = 60;
        Assert.Equal(1f, Speed.LegFactor(m), 5);
        // the lower leg counts: a leg that is down gives the floor of 11
        m.Parts[RLeg].Flesh = -50;
        Assert.Equal(0f, Speed.LegFactor(m));
        Assert.Equal(11f, Speed.Run(Human, stats, m, 1));
        // the unhurt shortcut
        Assert.Equal(80f, Speed.RunUnhurt(Human, 20));
    }

    [Fact]
    public void A_fitted_leg_scales_the_speed_through_its_athletics_multiplier()
    {
        var stats = new CharacterStats();
        stats[StatsEnumerated.Athletics] = 100;
        var m = Healthy();
        m.ApplyHit(LLeg, new HitDamage(250, 0, 0), Context());
        m.FitReplacement(new LimbReplacement { StringId = "l", Slot = 52, HpLow = 100, HpHigh = 100, AthleticsLow = 0.5f, AthleticsHigh = 0.5f }, 0);
        Assert.Equal(109 * 0.5f + 11, Speed.Run(Human, stats, m, 1), 4);
    }
}
