using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Bodies;

/// <summary>The probe tables of docs/game/character-stats.md as tests against the installed game (base load order).</summary>
[Slow]
public class BodyInstallTests
{
    static GameDatabase? cached;
    static readonly Lock gate = new();

    static GameDatabase Database()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        lock (gate) return cached ??= GameDatabase.Load(LoadOrder.BaseGame(install!));
    }

    static RaceData Race(string stringId)
    {
        var db = Database();
        return RaceData.From(db.Find(stringId) ?? throw new InvalidOperationException(stringId), db);
    }

    static RaceData RaceNamed(string name)
    {
        var db = Database();
        return RaceData.From(db.OfType(FcsRecordType.RACE).First(r => r.Name == name), db);
    }

    [Fact]
    public void Constants_of_the_body_rows_match_the_fixture_of_the_doc_table()
    {
        var c = GameConstants.FromDatabase(Database());
        var f = BaseGame;
        Assert.Equal(f.StarvationTime, c.StarvationTime);
        Assert.Equal(f.FedRecoveryRateMult, c.FedRecoveryRateMult);
        Assert.Equal(f.BedHungerRate, c.BedHungerRate, 6);
        Assert.Equal(f.EncumbranceHungerRate, c.EncumbranceHungerRate, 6);
        Assert.Equal(f.BleedRate, c.BleedRate, 6);
        Assert.Equal(f.ImmediateBloodLoss, c.ImmediateBloodLoss, 6);
        Assert.Equal(f.BleedingClotRate, c.BleedingClotRate, 6);
        Assert.Equal(f.ExtraBloodLossFromBodyparts, c.ExtraBloodLossFromBodyparts);
        Assert.Equal(f.BloodRecoveryRate, c.BloodRecoveryRate, 6);
        Assert.Equal(f.BodypartDegenerationRate, c.BodypartDegenerationRate);
        Assert.Equal(f.DegenerationMult1, c.DegenerationMult1, 6);
        Assert.Equal(f.DegenerationMult99, c.DegenerationMult99, 6);
        Assert.Equal(f.KnockoutMult1, c.KnockoutMult1);
        Assert.Equal(f.KnockoutMult99, c.KnockoutMult99, 6);
        Assert.Equal(f.KnockoutTimeBase, c.KnockoutTimeBase);
        Assert.Equal(f.MinToughnessKoPoint, c.MinToughnessKoPoint);
        Assert.Equal(f.MaxToughnessKoPoint, c.MaxToughnessKoPoint);
        Assert.Equal(f.StunRecoveryRate, c.StunRecoveryRate);
        Assert.Equal(f.BluntPermanentOrganDamage, c.BluntPermanentOrganDamage);
        Assert.Equal(f.HealRateMult, c.HealRateMult, 6);
        Assert.Equal(f.RestingHealRateMult, c.RestingHealRateMult);
        Assert.Equal(f.MedicSpeedMult, c.MedicSpeedMult);
        Assert.Equal(f.MedkitDrain1, c.MedkitDrain1, 6);
        Assert.Equal(f.MedkitDrain99, c.MedkitDrain99, 6);
        Assert.Equal(f.RobotMedicSpeedMult, c.RobotMedicSpeedMult, 6);
        Assert.Equal(f.RobotWearRate, c.RobotWearRate);
        Assert.Equal(f.ExpGainMultiplier, c.ExpGainMultiplier, 6);
        Assert.Equal(f.XpRateMedic1, c.XpRateMedic1);
        Assert.Equal(f.XpRateMedic99, c.XpRateMedic99);
        Assert.Equal(f.SkillDiffXp2xBonus, c.SkillDiffXp2xBonus);
        Assert.Equal(f.SkillDiffXp0xPenalty, c.SkillDiffXp0xPenalty);
        Assert.Equal(f.MinStrengthXpMult, c.MinStrengthXpMult, 6);
        Assert.Equal(f.WeightStrengthDiff1x, c.WeightStrengthDiff1x);
        Assert.Equal(f.WeightStrengthDiffMax, c.WeightStrengthDiffMax);
        Assert.Equal(f.XpRateStrength, c.XpRateStrength, 6);
        Assert.Equal(f.XpRateStrengthFromWalking, c.XpRateStrengthFromWalking, 6);
        Assert.Equal(f.XpRateAthletics, c.XpRateAthletics, 6);
        Assert.Equal(f.XpRateToughness, c.XpRateToughness, 6);
        Assert.Equal(f.EncumbranceBase, c.EncumbranceBase);
        Assert.Equal(f.CarryWeightMult, c.CarryWeightMult, 6);
        Assert.Equal(f.CarryPersonWeight, c.CarryPersonWeight);
        Assert.Equal(f.WeaponInventoryWeightMult, c.WeaponInventoryWeightMult, 6);
    }

    [Fact]
    public void The_nine_locational_damage_records_match_the_doc_table()
    {
        var parts = Database().OfType(FcsRecordType.LOCATIONAL_DAMAGE).Select(BodyPartTemplate.From).ToDictionary(p => p.Name);
        Assert.Equal(9, parts.Count);
        var head = parts["Head"];
        Assert.Equal((BodyPartType.Head, true, true, true, 1), (head.Type, head.Collapses, head.Vital, head.Severance, head.CollapsePart));
        Assert.Equal(0.6f, head.AffectsSkills, 5);
        Assert.Equal(2f, head.KoMult);
        var chest = parts["Chest"];
        Assert.Equal((BodyPartType.Torso, true, true, false), (chest.Type, chest.Collapses, chest.Vital, chest.Severance));
        Assert.Equal(0.3f, parts["Stomach"].AffectsMoveSpeed, 5);
        var la = parts["Left Arm"];
        Assert.Equal((BodyPartType.Arm, false, true, 4, BodySide.Left, 0), (la.Type, la.Vital, la.Severance, la.CollapsePart, la.Side, la.LimbSlot));
        Assert.Equal(0.25f, la.AffectsSkills, 5);
        Assert.Equal(1f, parts["Right Arm"].AffectsSkills);
        Assert.Equal(1, parts["Right Arm"].LimbSlot);
        Assert.Equal((32, 2), (parts["Left Leg"].CollapsePart, parts["Left Leg"].LimbSlot));
        Assert.Equal((16, 3), (parts["Right Leg"].CollapsePart, parts["Right Leg"].LimbSlot));
        Assert.Equal(0.5f, parts["Left Leg"].KoMult);
        // The animals' forelegs are ARM parts that do not collapse.
        var foreleg = parts.Values.Where(p => p.Name.Contains("Foreleg")).ToList();
        Assert.Equal(2, foreleg.Count);
        Assert.All(foreleg, p => { Assert.Equal(BodyPartType.Arm, p.Type); Assert.False(p.Collapses); });
    }

    [Fact]
    public void Greenlander_matches_the_race_and_anatomy_tables()
    {
        var g = Race("17-gamedata.quack");
        Assert.Equal("Greenlander", g.Name);
        Assert.Equal((70f, 120f, 15f), (g.SpeedMinSkill, g.SpeedMaxSkill, g.WalkSpeed));
        Assert.Equal((1f, 1f, 1f, 75f, 150f), (g.HungerRate, g.HealRate, g.BleedRate, g.MinBlood, g.MaxBlood));
        Assert.Equal(StatsEnumerated.Medic, g.HealStat);
        Assert.Equal(4f, g.PathfindFootprintRadius);
        Assert.Equal(7f, g.WaterAvoidance);   // 6 + 1
        Assert.False(g.IsRobot);
        Assert.True(g.Swims);
        foreach (var s in new[] { StatsEnumerated.Science, StatsEnumerated.Farming, StatsEnumerated.Cooking })
            Assert.Equal(1.2f, g.StatMultiplier(s));
        Assert.Equal(1f, g.StatMultiplier(StatsEnumerated.Stealth));
        Assert.Equal(7, g.Anatomy.Count);
        var byName = g.Anatomy.ToDictionary(a => a.Part.Name);
        Assert.Equal((80, 100f), (byName["Head"].HitWeight, byName["Head"].BaseHp));
        Assert.Equal((140, 100f), (byName["Chest"].HitWeight, byName["Chest"].BaseHp));
        Assert.Equal((140, 100f), (byName["Stomach"].HitWeight, byName["Stomach"].BaseHp));
        Assert.Equal(80, byName["Left Arm"].HitWeight);
        Assert.Equal(40, byName["Right Arm"].HitWeight);
        Assert.Equal(80, byName["Left Leg"].HitWeight);
        Assert.Equal(80, byName["Right Leg"].HitWeight);
    }

    [Fact]
    public void Other_races_match_the_probe_table()
    {
        var scorch = RaceNamed("Scorchlander");
        Assert.Equal(0.9f, scorch.StatMultiplier(StatsEnumerated.Strength));
        Assert.Equal(1.1f, scorch.StatMultiplier(StatsEnumerated.Dexterity));
        Assert.Equal(1.2f, scorch.StatMultiplier(StatsEnumerated.Stealth));
        Assert.Equal(0.8f, scorch.StatMultiplier(StatsEnumerated.Labouring));
        Assert.Equal(0.9f, scorch.HungerRate);
        Assert.Equal(60, scorch.Anatomy.Single(a => a.Part.Name == "Right Arm").HitWeight);

        var shek = RaceNamed("Shek");
        Assert.Equal(1.1f, shek.StatMultiplier(StatsEnumerated.Strength));
        Assert.Equal(0.8f, shek.StatMultiplier(StatsEnumerated.Dexterity));
        Assert.Equal(1.2f, shek.StatMultiplier(StatsEnumerated.MeleeAttack));
        Assert.Equal(0.8f, shek.StatMultiplier(StatsEnumerated.Athletics));
        Assert.All(shek.Anatomy, a => Assert.Equal(125f, a.BaseHp));
        Assert.Equal(1.25f, shek.HungerRate);

        var garru = RaceNamed("Garru");
        Assert.True(garru.SelfHealing);
        Assert.Equal(1, garru.ExtraAttackSlots);
        Assert.Equal(7f, garru.PathfindFootprintRadius);
        Assert.Equal(250f, garru.Anatomy.Single(a => a.Part.Name == "Head").BaseHp);
        Assert.Equal(2, garru.Anatomy.Count(a => a.Part.Name.Contains("Foreleg")));

        var leviathan = RaceNamed("Leviathan");
        Assert.True(leviathan.Gigantic);
        Assert.Equal(40f, leviathan.PathfindFootprintRadius);
        Assert.Equal(3, leviathan.ExtraAttackSlots);
        Assert.Equal(5000f, leviathan.Anatomy.Single(a => a.Part.Name == "Chest").BaseHp);
    }

    [Fact]
    public void Races_that_never_starve_and_robots_follow_the_flags()
    {
        var races = Database().OfType(FcsRecordType.RACE).ToList();
        Assert.NotEmpty(races);
        var skeleton = RaceNamed("Skeleton");
        Assert.True(skeleton.IsRobot);
        Assert.Equal(0f, skeleton.HungerRate);
        Assert.Equal(StatsEnumerated.Robotics, skeleton.HealStat);
        Assert.Equal(100f, skeleton.MinBlood);
        Assert.Equal(100f, skeleton.MaxBlood);
        // Every race lists its parts: seven, except the No-Head MkII skeleton.
        foreach (var r in races.Where(r => r.GetReferences("combat anatomy").Count > 0))
        {
            var data = RaceData.From(r, Database());
            Assert.InRange(data.Anatomy.Count, 6, 7);
        }
    }

    [Fact]
    public void Limb_replacement_records_read_with_their_quality_pairs()
    {
        var limbs = Database().OfType(FcsRecordType.LIMB_REPLACEMENT).Select(LimbReplacement.From).ToList();
        Assert.Equal(28, limbs.Count);
        Assert.All(limbs, l => Assert.InRange(l.Slot, 50, 53));
        var scoutLeg = limbs.First(l => l.Name.StartsWith("Scout Leg"));
        Assert.Equal((50f, 150f), (scoutLeg.HpLow, scoutLeg.HpHigh));
        Assert.Equal(1f, scoutLeg.Multiplier(StatsEnumerated.Athletics, 0), 5);
        Assert.Equal(1.4f, scoutLeg.Multiplier(StatsEnumerated.Athletics, 1), 5);
        Assert.Equal(0.25f, scoutLeg.Multiplier(StatsEnumerated.Stealth, 0), 5);
        Assert.Equal(0.95f, scoutLeg.Multiplier(StatsEnumerated.Stealth, 1), 5);
        Assert.True(scoutLeg.IsLeg);
        var klrArm = limbs.First(l => l.Name.StartsWith("KLR Series Arm"));
        Assert.Equal((100f, 250f), (klrArm.HpLow, klrArm.HpHigh));
        Assert.Equal(0.75f, klrArm.Multiplier(StatsEnumerated.Strength, 0), 5);
        Assert.Equal(1.1f, klrArm.Multiplier(StatsEnumerated.Strength, 1), 5);
        Assert.Equal((0f, 5f), (klrArm.UnarmedDamageBonus(0), klrArm.UnarmedDamageBonus(1)));
    }

    [Fact]
    public void Stats_records_read_and_a_race_scales_them()
    {
        var db = Database();
        var medic = db.Find("1773-gamedata.base")!;
        var values = StatsData.Read(medic);
        Assert.Equal(40f, values[(int)StatsEnumerated.Medic]);
        Assert.Equal(20f, values[(int)StatsEnumerated.Science]);
        Assert.Equal(10f, values[(int)StatsEnumerated.Athletics]);
        var stats = CharacterStats.Create(values, Race("17-gamedata.quack"), 0, 1, 1);
        Assert.Equal(24f, stats[StatsEnumerated.Science], 4);   // Greenlanders are good at science: x1.2
        Assert.Equal(40f, stats[StatsEnumerated.Medic]);
        // Base-game STATS reach 300 (the editor says 0 to 100).
        float max = db.OfType(FcsRecordType.STATS).Max(r => StatsData.Read(r).Max());
        Assert.Equal(300f, max);
    }

    [Fact]
    public void A_whole_world_of_races_builds_a_medical_state()
    {
        var db = Database();
        foreach (var r in db.OfType(FcsRecordType.RACE).Where(r => r.GetReferences("combat anatomy").Count > 0))
        {
            var race = RaceData.From(r, db);
            var m = MedicalState.Create(race, 50);
            Assert.All(m.Parts, p => Assert.True(p.MaxHp > 0));
            Assert.True(m.Blood > 0);
        }
    }
}
