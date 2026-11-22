using Meitou.Game.Rendering.Characters;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The stains worn items keep (docs/game/blood-decals.md "On weapons and gear", <see cref="GearBlood.StepStain"/>).
/// </summary>
public class GearBloodStainTests
{
    /// <summary>
    /// One part stepped <paramref name="seconds"/> in one-second steps with the wound at <paramref name="wound"/>.
    /// </summary>
    static void Run(ref float level, ref float soaking, ref float seen, float wound, int seconds, float wash)
    {
        for (int i = 0; i < seconds; i++) GearBlood.StepStain(ref level, ref soaking, ref seen, wound, 1, wash, false);
    }

    /// <summary>
    /// New blood soaks in over about <see cref="GearBlood.SoakSeconds"/>, not at once, and stays when the wound heals.
    /// </summary>
    [Fact]
    public void BloodSoaksInAndStaysAfterHealing()
    {
        float level = 0, soaking = 0, seen = 0;
        float wash = GearBlood.StainWash(false, 0);

        Run(ref level, ref soaking, ref seen, 0.6f, 5, wash);
        Assert.InRange(level, 0.05f, 0.2f);

        // Soaked in, less the little the slow fade took meanwhile.
        Run(ref level, ref soaking, ref seen, 0.6f, 60, wash);
        Assert.InRange(level, 0.53f, 0.6f);

        // Healed: the wound's blood is gone, the stain is not (it only fades over half an hour).
        Run(ref level, ref soaking, ref seen, 0, 60, wash);
        Assert.InRange(level, 0.5f, 0.6f);
    }

    /// <summary>
    /// Blood that stays the same on the wound does not soak in again: only a rise is new blood.
    /// </summary>
    [Fact]
    public void LastingBloodSoaksInOnce()
    {
        float level = 0, soaking = 0, seen = 0;
        Run(ref level, ref soaking, ref seen, 0.3f, 600, 0);

        Assert.Equal(0.3f, level, 3);
    }

    /// <summary>
    /// Wading washes a stain in about <see cref="GearBlood.WadeSeconds"/>, full rain in about <see cref="GearBlood.StainRainSeconds"/>.
    /// </summary>
    [Fact]
    public void WaterAndRainWashItOut()
    {
        float level = 1, soaking = 0, seen = 0;
        Run(ref level, ref soaking, ref seen, 0, (int)GearBlood.WadeSeconds + 1, GearBlood.StainWash(true, 0));
        Assert.Equal(0, level);

        level = 1;
        Run(ref level, ref soaking, ref seen, 0, (int)(GearBlood.StainRainSeconds / 2), GearBlood.StainWash(false, 1));
        Assert.InRange(level, 0.4f, 0.6f);
        Run(ref level, ref soaking, ref seen, 0, (int)GearBlood.StainRainSeconds, GearBlood.StainWash(false, 1));
        Assert.Equal(0, level);
    }

    /// <summary>
    /// A stain put back from a save takes the wounds as they are: the blood under it does not soak in a second time.
    /// </summary>
    [Fact]
    public void ARestoredStainDoesNotSoakTwice()
    {
        float level = 0.4f, soaking = 0, seen = 0;
        GearBlood.StepStain(ref level, ref soaking, ref seen, 0.4f, 0, 0, true);
        Run(ref level, ref soaking, ref seen, 0.4f, 120, 0);

        Assert.Equal(0.4f, level, 3);
    }
}
