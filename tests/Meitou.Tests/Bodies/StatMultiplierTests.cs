using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Bodies;

public class StatMultiplierTests
{
    static void Down(MedicalState m, int part) => m.Parts[part].Flesh = -5;

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

        // stealth and athletics do not look at the arms at all (the game never sends stealth through the table, #46)
        Assert.Equal(1f, m.StatMultiplier(StatsEnumerated.Stealth));
        Assert.Equal(1f, m.StatMultiplier(StatsEnumerated.Athletics));
    }
}

public class SpeedAndEncumbranceTests
{
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

        // the lower leg counts: a leg that is down crawls at 5 (not the floor of 11)
        m.Parts[RLeg].Flesh = -50;
        Assert.Equal(0f, Speed.LegFactor(m));
        m.Tick(0.01f, Context());   // the crawl is the medical tick's flag: none before the first tick
        Assert.Equal(Speed.CrawlSpeed, Speed.Run(Human, stats, m, 1));

        // the unhurt shortcut
        Assert.Equal(80f, Speed.RunUnhurt(Human, 20));
    }
}
