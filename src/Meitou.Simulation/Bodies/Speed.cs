using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The run-speed chain (docs/game/character-stats.md "Derived values", <c>FUN_140883db0</c>, <c>FUN_140886460</c>, <c>FUN_140884970</c>, <c>FUN_140644d30</c>): the
/// stat S that movement turns into units per second (docs/game/pathfinding.md "Movement"). The population code used a stand-in athletics of 20; it should call
/// <see cref="Run"/> with the character's stats and medical state instead.
/// </summary>
public static class Speed
{
    /// <summary>The floor of S: a crippled character still crawls at 11.</summary>
    public const float Minimum = 11;

    /// <summary>The cap of S in shallow water (water state 1), <b>Verified</b> by reading <c>FUN_140886460</c>.</summary>
    public const float ShallowWaterCap = 45;

    /// <summary>
    /// The run speed value V (<c>FUN_140883db0</c>): lerp(<c>speed min skill</c>, <c>speed max skill</c>, athletics x 0.01) times (0.5 + 0.5 x s), s the value of character slot
    /// <c>+0x398</c> (a constant 1.0 for humans, so the factor is 1).
    /// </summary>
    public static float RunValue(RaceData race, float athletics, float slotFactor = 1) =>
        (race.SpeedMinSkill + (race.SpeedMaxSkill - race.SpeedMinSkill) * (athletics * 0.01f)) * (0.5f + 0.5f * slotFactor);

    /// <summary>The leg factor: the lower of the two legs' health fractions x 1.8, clamped to [0, 1] (1 for a character without legs, such as a snake-like creature).</summary>
    public static float LegFactor(MedicalState medical)
    {
        float lowest = float.MaxValue;
        foreach (var p in medical.Parts)
            if (p.Template.Type == BodyPartType.Leg) lowest = MathF.Min(lowest, p.Fraction);
        return lowest == float.MaxValue ? 1 : Math.Clamp(lowest * 1.8f, 0, 1);
    }

    /// <summary>The hunger factor (<c>FUN_140644d30</c>): lerp(0.5, 1, clamp(hunger - 1, 0, 1)); malnourished characters move at half speed at worst.</summary>
    public static float HungerFactor(float hunger) => 0.5f + 0.5f * Math.Clamp(hunger - 1, 0, 1);

    /// <summary>
    /// S = (V - 11) x leg factor x hunger factor x the fitted legs' athletics multipliers x encumbrance factor x <paramref name="statsFactor"/> (<c>CharStats+0x18</c>, 1 when
    /// unknown) + 11, never below 11; shallow water caps it at 45.
    /// </summary>
    public static float Run(RaceData race, CharacterStats stats, MedicalState medical, float encumbranceFactor, bool shallowWater = false, float statsFactor = 1)
    {
        float v = RunValue(race, stats.Athletics);
        float s = (v - Minimum) * LegFactor(medical) * HungerFactor(medical.Hunger) * medical.LegItemFactor(StatsEnumerated.Athletics)
            * encumbranceFactor * statsFactor + Minimum;
        s = MathF.Max(s, Minimum);
        return shallowWater ? MathF.Min(s, ShallowWaterCap) : s;
    }

    /// <summary>
    /// The speed with no medical state: an unhurt, fed, unencumbered character of this race and athletics. This is what the stand-in used to compute,
    /// with the real athletics.
    /// </summary>
    public static float RunUnhurt(RaceData race, float athletics) => MathF.Max(RunValue(race, athletics), Minimum);
}
