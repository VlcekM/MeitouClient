using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The run-speed chain (docs/game/character-stats.md "Derived values", <c>FUN_140883db0</c>, <c>FUN_140886460</c>, <c>FUN_140884970</c>,
/// <c>FUN_140644d30</c>): the stat S that movement turns into units per second (docs/game/pathfinding.md "Movement"). <c>BodySystem</c> and the
/// population code call <see cref="Run"/> with the character's stats and medical state.
/// </summary>
public static class Speed
{
    /// <summary>
    /// The floor of S.
    /// </summary>
    public const float Minimum = 11;

    /// <summary>
    /// S while crawling (<c>FUN_140886460</c>'s last overrides, <b>Observed</b>): 7 staying low (posture 1), 5 with a leg down (medical
    /// <c>+0x160</c>), the latter winning.
    /// </summary>
    public const float StayingLowSpeed = 7, CrawlSpeed = 5;

    /// <summary>
    /// The cap of S in shallow water (water state 1), <b>Verified</b> by reading <c>FUN_140886460</c>.
    /// </summary>
    public const float ShallowWaterCap = 45;

    /// <summary>
    /// The run speed value V (<c>FUN_140883db0</c>): lerp(<c>speed min skill</c>, <c>speed max skill</c>, athletics x 0.01) times (0.5 + 0.5 x s), s
    /// the value of character slot <c>+0x398</c> (a constant 1.0 for humans, so the factor is 1).
    /// </summary>
    public static float RunValue(RaceData race, float athletics, float slotFactor = 1) =>
        (race.SpeedMinSkill + (race.SpeedMaxSkill - race.SpeedMinSkill) * (athletics * 0.01f)) * (0.5f + 0.5f * slotFactor);

    /// <summary>
    /// The leg factor: the lower of the two legs' health fractions x 1.8, clamped to [0, 1] (1 for a character without legs, such as a snake-like
    /// creature).
    /// </summary>
    public static float LegFactor(MedicalState medical)
    {
        float lowest = float.MaxValue;
        foreach (var p in medical.PartSpan)
            if (p.Template.Type == BodyPartType.Leg) lowest = MathF.Min(lowest, p.Fraction);
        return lowest == float.MaxValue ? 1 : Math.Clamp(lowest * 1.8f, 0, 1);
    }

    /// <summary>
    /// The hunger factor (<c>FUN_140644d30</c>): lerp(0.5, 1, clamp(hunger - 1, 0, 1)); malnourished characters move at half speed at worst.
    /// </summary>
    public static float HungerFactor(float hunger) => 0.5f + 0.5f * Math.Clamp(hunger - 1, 0, 1);

    /// <summary>
    /// S = (V - 11) x leg factor x hunger factor x the fitted legs' athletics multipliers x encumbrance factor x <paramref name="statsFactor"/> (the
    /// gear factor <c>CharStats+0x18</c>, 1 for none, <see cref="Combat.Fighter.AthleticsFactor"/>) + 11, never below 11; shallow water caps it at
    /// 45. A crawler moves at <see cref="CrawlSpeed"/> or <see cref="StayingLowSpeed"/> instead.
    /// </summary>
    public static float Run(RaceData race, CharacterStats stats, MedicalState medical, float encumbranceFactor, bool shallowWater = false,
        float statsFactor = 1)
    {
        if (medical.Crawling) return CrawlSpeed;
        if (medical.Prone) return StayingLowSpeed;
        float v = RunValue(race, stats.Athletics);
        float s = (v - Minimum) * LegFactor(medical) * HungerFactor(medical.Hunger) * medical.LegItemFactor(StatsEnumerated.Athletics)
            * encumbranceFactor * statsFactor + Minimum;
        s = MathF.Max(s, Minimum);
        return shallowWater ? MathF.Min(s, ShallowWaterCap) : s;
    }

    /// <summary>
    /// The speed of a crippled character and of a voluntary crawler (<b>Verified</b>, <c>FUN_140886460</c>: a final override after the water and
    /// sneak caps, 5.0 when crippled, 7.0 at posture 1; docs/game/ragdolls.md "Forced crawl"). Meitou has no voluntary crawl yet.
    /// </summary>
    public const float Crippled = 5, VoluntaryCrawl = 7;

    /// <summary>
    /// The water state 0..3 of a character standing (swimming at the surface counts as standing on the seabed below) at height
    /// <paramref name="standY"/> (<b>Verified</b>, <c>FUN_1405c7fd0</c>, docs/game/pathfinding.md "Water states"): 0 dry (also when standY +
    /// <see cref="WaterSlack"/> is above the water level), 3 deep (size x 15 + standY at or under the water level), 2 waist-deep (size x 6), else 1
    /// for a race that <c>swims</c>, which is the only state the shallows have.
    /// </summary>
    public static int WaterState(float standY, float size, bool swims)
    {
        if (standY + WaterSlack > WorldWater.Height) return 0;
        if (size * DeepWaterDepth + standY <= WorldWater.Height) return 3;
        if (size * WaistWaterDepth + standY <= WorldWater.Height) return 2;
        return swims ? 1 : 0;
    }

    /// <summary>
    /// The wet water state 1..3 of a swimmer of size 1 over ground <paramref name="depth"/> under the water level: what the navmesh's water areas
    /// stand for (docs/game/pathfinding.md "Water costs").
    /// </summary>
    public static int WetStateOfDepth(float depth) => depth >= DeepWaterDepth ? 3 : depth >= WaistWaterDepth ? 2 : 1;

    /// <summary>
    /// The water level's margin over a character's height, and the depths (times its size) that make the water waist-deep and deep (<b>Verified</b>,
    /// <c>FUN_1405c7fd0</c>).
    /// </summary>
    public const float WaterSlack = 2, WaistWaterDepth = 6, DeepWaterDepth = 15;

    /// <summary>
    /// The height a character in the water is drawn at: the seabed it wades along (a race that does not swim walks it at any depth), a swimmer's no
    /// lower than <see cref="DeepWaterDepth"/> x size under the surface, which is where the deep state begins (<see cref="WaterState"/>), so the swim
    /// clips, whose pelvis is about 12 above their origin, lie in the surface (<b>Observed</b>: the clips' <c>Bip01</c> height; the original's own
    /// swimming height, and the use of <c>swim offset</c>, are <b>Unknown</b>).
    /// </summary>
    public static float WadingHeight(float terrainY, float size, bool swims) =>
        swims ? MathF.Max(terrainY, WorldWater.Height - DeepWaterDepth * size) : terrainY;

    /// <summary>
    /// The base swim speed before the body's factors (<c>FUN_140883e30</c>, the Swimming tooltip's "Max Swim Speed:"; <b>Verified</b>, decompile: the
    /// helper <c>FUN_140015b63</c> is (c - b) x a + b): lerp(swim speed mult x swimming x 0.02, 6, 25) = 6 + 19 x, not clamped, so 25 at swimming 50
    /// (mult 1) and 44 at 100.
    /// </summary>
    public static float SwimBase(RaceData race, CharacterStats stats) =>
        6 + (25 - 6) * (race.SwimSpeedMult * stats[StatsEnumerated.Swimming] * 0.02f);

    /// <summary>
    /// The swim speed (<b>Verified</b>, <c>FUN_140885eb0</c>): <see cref="SwimBase"/> x the leg, hunger, limb-item (all four limbs) and encumbrance
    /// factors, x 0.6 per unusable arm; at least 4, and 1 with no usable arm.
    /// </summary>
    public static float Swim(RaceData race, CharacterStats stats, MedicalState medical, float encumbranceFactor)
    {
        bool left = medical.ArmWorks(left: true), right = medical.ArmWorks(left: false);
        if (!left && !right) return 1;
        float legs = LegFactor(medical) * (left ? 1 : 0.6f) * (right ? 1 : 0.6f);
        float items = medical.LegItemFactor(StatsEnumerated.Swimming) * medical.ArmItemFactor(StatsEnumerated.Swimming);
        float s = SwimBase(race, stats) * HungerFactor(medical.Hunger) * items * legs * encumbranceFactor;
        return MathF.Max(s, 4);
    }

    /// <summary>
    /// The top speed in water state <paramref name="state"/> instead of the run speed <paramref name="run"/> (<b>Verified</b>, <c>FUN_140886460</c>):
    /// the shallows (1) cap it at <see cref="ShallowWaterCap"/>; a race that does not <c>swim</c> walks the bottom of waist-deep and deep water at
    /// twice its walk speed; a swimmer's waist-deep speed is its race walk speed and its deep speed <paramref name="swim"/>.
    /// </summary>
    public static float InWater(int state, float run, RaceData race, float swim)
    {
        if (state == 0) return run;
        if (state == 1) return MathF.Min(run, ShallowWaterCap);
        if (!race.Swims) return race.WalkSpeed * 2;
        return state == 2 ? race.WalkSpeed : swim;
    }

    /// <summary>
    /// The speed with no medical state: an unhurt, fed, unencumbered character of this race and athletics. This is what the stand-in used to compute,
    /// with the real athletics.
    /// </summary>
    public static float RunUnhurt(RaceData race, float athletics) => MathF.Max(RunValue(race, athletics), Minimum);
}