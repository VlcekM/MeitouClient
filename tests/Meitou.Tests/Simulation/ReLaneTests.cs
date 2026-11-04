using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Rules settled by reverse engineering that a synthetic character can pin (docs/game/perception.md, docs/game/ai-tasks.md).</summary>
public class ReLaneTests
{
    static CharacterCold Cold() => new() { Race = Human, Medical = MedicalState.Create(Human, 50) };

    /// <summary>The squad-intact able test (<c>FUN_14079acf0</c>): a down member counts only while its first-aid need (Character <c>+0x504</c>) is under 0.2, or 0.8 for a persistent platoon with a home.</summary>
    [Theory]
    [InlineData(0.1f, false, true)]
    [InlineData(0.2f, false, false)]
    [InlineData(0.5f, false, false)]
    [InlineData(0.5f, true, true)]
    [InlineData(0.8f, true, false)]
    public void A_down_member_counts_as_able_only_below_the_need_threshold(float need, bool persistentHome, bool able) =>
        Assert.Equal(able, SquadIntact.DownIsAble(need, persistentHome));

    [Fact]
    public void A_standing_member_is_able_and_a_dead_one_is_not()
    {
        Assert.True(SquadIntact.Able(null!, Cold(), persistentHome: false));
        var dead = Cold();
        dead.Medical!.Butcher();
        Assert.False(SquadIntact.Able(null!, dead, persistentHome: false));
    }

    /// <summary>Row 98's desire by the minutes since the last bed use (<c>FUN_1405a0880</c>): 0.3 within 20, else 1 for a human.</summary>
    [Theory]
    [InlineData(5, 0.3f)]
    [InlineData(19.9, 0.3f)]
    [InlineData(20, 1f)]
    [InlineData(500, 1f)]
    public void The_bed_clock_lowers_the_sleeping_desire_just_after_a_bed(double minutes, float desire) =>
        Assert.Equal(desire, AiSystem.BedClockDesire(Cold(), minutes));

    /// <summary>A character's light is the bare daylight until a sight test (or the stat multiplier) asked for it; the next update then samples it in full (<c>FUN_1405ccd90</c>:53-60).</summary>
    [Fact]
    public void A_characters_light_is_sampled_only_after_a_request()
    {
        using var world = new SimWorld(new WorldSettings { Seed = 1, Threads = 1, PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0), []);
        var player = world.Characters.Spawn(new CharacterHot { Position = new System.Numerics.Vector3(100, 0, 100), Health = 100 }, new CharacterCold { IsPlayer = true }, 0);
        int slot = player.Slot;
        double hour = 0;
        for (; hour < 24; hour += 0.25)
        {
            world.GameHours = hour;
            world.Sight.Update(world);
            if (world.Sight.Daylight is > 0.05f and < 0.4f) break;
        }
        float day = world.Sight.Daylight;
        Assert.InRange(day, 0.05f, 0.4f);
        Assert.Equal(day, world.Sight.PeekLight(slot), 4);   // nothing asked: bare daylight
        world.Sight.Light(slot);                              // a sight test reads it first (bare), and raises the request
        world.Sight.Update(world);
        Assert.Equal(2 * day, world.Sight.PeekLight(slot), 4); // sampled: no lamps, 2 x daylight
        world.Sight.Update(world);
        Assert.Equal(day, world.Sight.PeekLight(slot), 4);   // the request was cleared: bare again
    }
}
