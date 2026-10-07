using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Tests.Bodies;

/// <summary>Shared inputs of the body tests: the base game's CONSTANTS, a human race with the Greenlander anatomy, and contexts.</summary>
static class BodyFixtures
{
    /// <summary>
    /// The CONSTANTS values of the base game (docs/game/character-stats.md "CONSTANTS used by this subsystem", loader scalings applied). The editor defaults
    /// that <see cref="GameConstants.Default"/> carries differ from these, so formula tests use this record.
    /// </summary>
    public static GameConstants BaseGame { get; } = GameConstants.Default with
    {
        StarvationTime = 60,
        FedRecoveryRateMult = 3,
        BedHungerRate = 0.33f,
        EncumbranceHungerRate = 0.7f,
        BleedRate = 0.001f,
        ImmediateBloodLoss = 0.2f,
        BleedingClotRate = 0.00085f,
        ExtraBloodLossFromBodyparts = 1,
        BloodRecoveryRate = 0.04f,
        BodypartDegenerationRate = 1.5f,
        DegenerationMult1 = 1.7f,
        DegenerationMult99 = 0.03f,
        KnockoutMult1 = 3,
        KnockoutMult99 = 0.75f,
        KnockoutTimeBase = 20,
        MinToughnessKoPoint = 10,
        MaxToughnessKoPoint = 85,
        StunRecoveryRate = 1,
        BluntPermanentOrganDamage = 0,
        HealRateMult = 0.25f,
        RestingHealRateMult = 2,
        MedicSpeedMult = 3,
        MedkitDrain1 = 2,
        MedkitDrain99 = 0.1f,
        RobotMedicSpeedMult = 0.33f,
        RobotWearRate = 1,
        ExpGainMultiplier = 0.75f,
        XpRateMedic1 = 15,
        XpRateMedic99 = 150,
        SkillDiffXp2xBonus = 10,
        SkillDiffXp0xPenalty = 25,
        MinStrengthXpMult = 0.1f,
        WeightStrengthDiff1x = 20,
        WeightStrengthDiffMax = 1,
        XpRateStrength = 0.5f,
        XpRateStrengthFromWalking = 0.5f,
        XpRateAthletics = 0.5f,
        XpRateToughness = 1.33f,
        EncumbranceBase = 15,
        CarryWeightMult = 1.2f,
        CarryPersonWeight = 30,
        WeaponInventoryWeightMult = 0.5f,
        DamageMultiplier = 0.65f,
    };

    public static BodyPartTemplate Head { get; } = new() { StringId = "32-t", Name = "Head", Type = BodyPartType.Head, Collapses = true, Vital = true, Severance = true, CollapsePart = 1, AffectsSkills = 0.6f, KoMult = 2 };
    public static BodyPartTemplate Chest { get; } = new() { StringId = "101-t", Name = "Chest", Type = BodyPartType.Torso, Collapses = true, Vital = true, CollapsePart = 1, KoMult = 1 };
    public static BodyPartTemplate Stomach { get; } = new() { StringId = "100-t", Name = "Stomach", Type = BodyPartType.Torso, Collapses = true, Vital = true, Severance = true, CollapsePart = 1, AffectsMoveSpeed = 0.3f, KoMult = 1 };
    public static BodyPartTemplate LeftArm { get; } = new() { StringId = "28-t", Name = "Left Arm", Type = BodyPartType.Arm, Collapses = true, Severance = true, CollapsePart = 4, AffectsSkills = 0.25f, KoMult = 0.5f };
    public static BodyPartTemplate RightArm { get; } = new() { StringId = "29-t", Name = "Right Arm", Type = BodyPartType.Arm, Collapses = true, Severance = true, CollapsePart = 2, AffectsSkills = 1, KoMult = 0.5f };
    public static BodyPartTemplate LeftLeg { get; } = new() { StringId = "30-t", Name = "Left Leg", Type = BodyPartType.Leg, Collapses = true, Severance = true, CollapsePart = 32, KoMult = 0.5f };
    public static BodyPartTemplate RightLeg { get; } = new() { StringId = "31-t", Name = "Right Leg", Type = BodyPartType.Leg, Collapses = true, Severance = true, CollapsePart = 16, KoMult = 0.5f };

    /// <summary>Part indices of <see cref="Human"/> (the order of the Greenlander's <c>combat anatomy</c>: right leg, left arm, left leg, head, stomach, right arm, chest).</summary>
    public const int RLeg = 0, LArm = 1, LLeg = 2, HeadIx = 3, StomachIx = 4, RArm = 5, ChestIx = 6;

    /// <summary>A Greenlander: anatomy 80/100, 140/100, 140/100, arms 80 and 40, legs 80 and 80; good Science, Farming, Cooking.</summary>
    public static RaceData Human { get; } = new()
    {
        StringId = "17-t",
        Name = "Greenlander",
        StatMultipliers = RaceData.BuildStatMap([4, 12, 13], [0]),
        Anatomy =
        [
            new AnatomyEntry(RightLeg, 80, 100), new AnatomyEntry(LeftArm, 80, 100), new AnatomyEntry(LeftLeg, 80, 100), new AnatomyEntry(Head, 80, 100),
            new AnatomyEntry(Stomach, 140, 100), new AnatomyEntry(RightArm, 40, 100), new AnatomyEntry(Chest, 140, 100),
        ],
    };

    /// <summary>A skeleton-type robot: never hungry, blood 100/100, heal rate 2, bleed rate 0.1.</summary>
    public static RaceData Robot { get; } = new()
    {
        StringId = "r-t", Name = "Skeleton", IsRobot = true, HungerRate = 0, HealRate = 2, BleedRate = 0.1f, MinBlood = 100, MaxBlood = 100, HealStat = StatsEnumerated.Robotics,
        Anatomy = Human.Anatomy,
    };

    public static MedicalContext Context(RaceData? race = null, BodyOptions? options = null, float toughness = 50, float strength = 50) =>
        new(BaseGame, options ?? BodyOptions.Default, race ?? Human) { Toughness = toughness, Strength = strength, Seed = 42, CharacterKey = 7 };

    public static MedicalState Healthy(RaceData? race = null, float strength = 50) => MedicalState.Create(race ?? Human, strength);

    /// <summary>Ticks in steps of <paramref name="step"/> hours until <paramref name="hours"/> have passed.</summary>
    public static void Run(MedicalState m, in MedicalContext ctx, float hours, float step = 0.1f, List<MedicalEvent>? events = null)
    {
        int n = (int)MathF.Round(hours / step);
        for (int i = 0; i < n; i++) m.Tick(step, ctx, events);
    }
}
