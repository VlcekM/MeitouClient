using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Bodies;

public class StatsTests
{
    [Fact]
    public void Stat_numbers_and_storage_follow_the_doc()
    {
        Assert.Equal(39, StatsInfo.Count);
        Assert.Equal("toughness2", StatsInfo.FieldName(StatsEnumerated.Toughness));
        Assert.Equal("unarmed", StatsInfo.FieldName(StatsEnumerated.MartialArts));
        Assert.Equal("ff", StatsInfo.FieldName(StatsEnumerated.FriendlyFire));
        Assert.Equal("bow smith", StatsInfo.FieldName(StatsEnumerated.SmithingBow));
        Assert.Equal(StatsEnumerated.Medic, StatsInfo.StorageOf(StatsEnumerated.HiveMedic));
        Assert.Equal(StatsEnumerated.Medic, StatsInfo.StorageOf(StatsEnumerated.Vet));
        Assert.Equal(StatsEnumerated.None, StatsInfo.StorageOf(StatsEnumerated.Weapons));
        Assert.Equal(StatsEnumerated.None, StatsInfo.StorageOf(StatsEnumerated.Survival));
        Assert.Equal(33, StatsInfo.RecordStats.Count);   // the 33 fields of the FCS STATS record
        Assert.DoesNotContain(StatsEnumerated.MassCombat, StatsInfo.RecordStats);
        Assert.Equal(StatsEnumerated.Katanas, StatsInfo.FromFieldName("katana"));
    }

    [Theory]
    [InlineData(1f, 0.9803f)]      // ((101 - 1) / 101)^2: a new stat grows at nearly the full rate
    [InlineData(50f, 0.2550f)]     // about a quarter at 50
    [InlineData(101f, 0f)]         // nothing at the cap
    public void Core_gain_follows_the_squared_curve(float stat, float f)
    {
        float after = CharacterStats.CoreGain(stat, 1);
        Assert.Equal(stat + f, after, 3);
    }

    [Fact]
    public void Core_gain_limits_amount_and_the_curve_factor()
    {
        Assert.Equal(10f, CharacterStats.CoreGain(10, 0));       // amount must be positive
        Assert.Equal(10f, CharacterStats.CoreGain(10, 20.5f));   // and at most 20
        Assert.True(CharacterStats.CoreGain(10, 20) > 10);
        // Above 101 the square makes the stat grow again, until f exceeds 20 (about 553).
        Assert.True(CharacterStats.CoreGain(150, 1) > 150);
        Assert.True(CharacterStats.CoreGain(552, 1) > 552);
        Assert.Equal(600f, CharacterStats.CoreGain(600, 1));
        Assert.Equal(20f, CharacterStats.CoreGain(float.NaN, 1));
    }

    [Fact]
    public void Gain_shares_medic_between_the_three_heal_stats_and_ignores_stats_without_storage()
    {
        var s = new CharacterStats();
        s[StatsEnumerated.Medic] = 10;
        Assert.Equal(10, s[StatsEnumerated.Vet]);
        s.Gain(StatsEnumerated.HiveMedic, 5);
        Assert.True(s[StatsEnumerated.Medic] > 10);
        Assert.Equal(0, s.Gain(StatsEnumerated.Weapons, 5));
        s[StatsEnumerated.Survival] = 99;
        Assert.Equal(0, s[StatsEnumerated.Survival]);
    }

    [Fact]
    public void Race_stat_map_applies_goods_then_bads_then_strength_and_dexterity()
    {
        var greenlander = RaceData.BuildStatMap([4, 12, 13], [0]);
        Assert.Equal(1.2f, greenlander[(int)StatsEnumerated.Science]);
        Assert.Equal(1.2f, greenlander[(int)StatsEnumerated.Farming]);
        Assert.Equal(1.2f, greenlander[(int)StatsEnumerated.Cooking]);
        Assert.Equal(1f, greenlander[(int)StatsEnumerated.Stealth]);
        // A stat in both lists ends bad; RACE strength / dexterity win for stats 1 and 18.
        var shek = RaceData.BuildStatMap([2, 21, 1], [4, 3, 1, 18], strength: 1.1f, dexterity: 0.8f);
        Assert.Equal(1.2f, shek[(int)StatsEnumerated.MeleeAttack]);
        Assert.Equal(0.8f, shek[(int)StatsEnumerated.Science]);
        Assert.Equal(1.1f, shek[(int)StatsEnumerated.Strength]);
        Assert.Equal(0.8f, shek[(int)StatsEnumerated.Dexterity]);
        var both = RaceData.BuildStatMap([10], [10]);
        Assert.Equal(0.8f, both[10]);
    }

    static GameDatabase Db(params FcsRecord[] records)
    {
        var file = new FcsFile();
        file.Records.AddRange(records);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    [Fact]
    public void Race_record_scan_continues_past_a_stored_zero_and_stops_at_the_first_missing_key()
    {
        var part = new FcsRecord { StringId = "1-t", Flags = 0x10, Name = "Head", RecordType = FcsRecordType.LOCATIONAL_DAMAGE };
        part.Ints["body part type"] = 3;
        part.Bools["death"] = true;
        part.Bools["collapses"] = true;
        part.Ints["collapse part"] = 1;
        var race = new FcsRecord { StringId = "2-t", Flags = 0x10, Name = "Test", RecordType = FcsRecordType.RACE };
        race.Ints["stats good0"] = 0;
        race.Ints["stats good1"] = 4;
        race.Ints["stats good3"] = 13;   // after a gap: never read
        race.Ints["stats bad0"] = 9;
        race.Floats["strength"] = 0.9f;
        race.Floats["water avoidance"] = 6;
        race.Bools["cant enter buildings"] = true;
        race.References["combat anatomy"] = [new FcsReference("1-t", 80, 125, 0), new FcsReference("missing", 1, 1, 0)];
        var db = Db(part, race);
        var data = RaceData.From(db.Find("2-t")!, db);
        Assert.Equal(1.2f, data.StatMultiplier(StatsEnumerated.Science));
        Assert.Equal(1f, data.StatMultiplier(StatsEnumerated.Cooking));
        Assert.Equal(0.8f, data.StatMultiplier(StatsEnumerated.Medic));
        Assert.Equal(0.9f, data.StatMultiplier(StatsEnumerated.Strength));
        Assert.Equal(7f, data.WaterAvoidance);
        Assert.False(data.CanEnterBuildings);
        var entry = Assert.Single(data.Anatomy);
        Assert.Equal(80, entry.HitWeight);
        Assert.Equal(125f, entry.BaseHp);
        Assert.Equal(BodyPartType.Head, entry.Part.Type);
        Assert.True(entry.Part.Vital);
    }

    [Fact]
    public void Water_avoidance_is_transformed()
    {
        Assert.Equal(7f, new RaceData { WaterAvoidanceRaw = 6 }.WaterAvoidance);
        Assert.Equal(1f, new RaceData { WaterAvoidanceRaw = 0 }.WaterAvoidance);
        Assert.Equal(1f / 11, new RaceData { WaterAvoidanceRaw = -10 }.WaterAvoidance, 6);
    }

    [Fact]
    public void Body_part_side_and_limb_slot_come_from_the_collapse_bits()
    {
        Assert.Equal(0, LeftArm.LimbSlot);
        Assert.Equal(1, RightArm.LimbSlot);
        Assert.Equal(2, LeftLeg.LimbSlot);
        Assert.Equal(3, RightLeg.LimbSlot);
        Assert.Equal(-1, Head.LimbSlot);
        Assert.Equal(-1, Chest.LimbSlot);
        var foreleg = new BodyPartTemplate { StringId = "f", Type = BodyPartType.Arm, CollapsePart = 4 };
        Assert.Equal(0, foreleg.LimbSlot);
    }

    [Fact]
    public void Limb_replacement_interpolates_between_the_two_stored_values()
    {
        var scout = new LimbReplacement { StringId = "s", Slot = 52, HpLow = 50, HpHigh = 150, AthleticsLow = 1, AthleticsHigh = 1.4f, StealthLow = 0.25f, StealthHigh = 0.95f };
        Assert.True(scout.IsLeg);
        Assert.Equal(50, scout.Hp(0));
        Assert.Equal(100, scout.Hp(0.5f));
        Assert.Equal(150, scout.Hp(1));
        Assert.Equal(1.2f, scout.Multiplier(StatsEnumerated.Athletics, 0.5f), 5);
        Assert.Equal(0.6f, scout.Multiplier(StatsEnumerated.Stealth, 0.5f), 5);
        Assert.Equal(1f, scout.Multiplier(StatsEnumerated.Strength, 0.5f));
        var thief = new LimbReplacement { StringId = "t", Slot = 51, ThieveryLow = 0.9f, ThieveryHigh = 1.25f };
        Assert.Equal(thief.Multiplier(StatsEnumerated.Thieving, 1), thief.Multiplier(StatsEnumerated.Lockpicking, 1));
        Assert.Equal(1.25f, thief.Multiplier(StatsEnumerated.Lockpicking, 1));
        Assert.Equal(0.9f, thief.Multiplier(StatsEnumerated.Thieving, -3));   // t is clamped
    }

    [Fact]
    public void Creating_stats_randomises_then_multiplies_by_the_race_and_is_deterministic()
    {
        var baseValues = new float[StatsInfo.Count];
        foreach (var s in StatsInfo.RecordStats) baseValues[(int)s] = 40;
        var none = CharacterStats.Create(baseValues, Human, 0, 1, 1);
        Assert.Equal(40, none[StatsEnumerated.Strength]);
        Assert.Equal(48, none[StatsEnumerated.Science], 4);   // x1.2 from the race

        var a = CharacterStats.Create(baseValues, Human, 5, 123, 99);
        var b = CharacterStats.Create(baseValues, Human, 5, 123, 99);
        Assert.Equal(a.ToArray(), b.ToArray());
        var other = CharacterStats.Create(baseValues, Human, 5, 123, 100);
        Assert.NotEqual(a.ToArray(), other.ToArray());
        foreach (var s in StatsInfo.RecordStats)
            Assert.InRange(a[s] / Human.StatMultiplier(s), 35 - 1e-3f, 45 + 1e-3f);
        // Not all offsets are equal: the stats are drawn separately.
        Assert.True(StatsInfo.RecordStats.Select(s => a[s] / Human.StatMultiplier(s)).Distinct().Count() > 10);
    }

    [Fact]
    public void Stats_from_groups_fan_out_and_save_round_trips()
    {
        var v = CharacterStats.FromGroups(combat: 40, unarmed: 0, stealth: 30, ranged: 0, strength: 25);
        Assert.Equal(40, v[(int)StatsEnumerated.MeleeAttack]);
        Assert.Equal(30, v[(int)StatsEnumerated.Stealth]);
        Assert.Equal(25, v[(int)StatsEnumerated.Strength]);
        Assert.Equal(1, v[(int)StatsEnumerated.MartialArts]);

        var stats = CharacterStats.FromValues(v);
        stats[StatsEnumerated.MassCombat] = 3;
        var saved = new Dictionary<string, float>();
        stats.WriteSave(saved);
        Assert.Equal(40, saved["attack"]);
        Assert.Equal(3, saved["mass combat"]);
        Assert.True(saved.ContainsKey("toughness2"));
        var back = CharacterStats.ReadSave(saved);
        Assert.Equal(stats.ToArray(), back.ToArray());
    }
}

public class XpTests
{
    static readonly XpService Xp = new(BaseGame, BodyOptions.Default);

    [Fact]
    public void Skill_difference_factor_doubles_at_plus_ten_and_vanishes_at_minus_twenty_five()
    {
        Assert.Equal(1f, Xp.SkillDifferenceFactor(30, 30));
        Assert.Equal(2f, Xp.SkillDifferenceFactor(30, 40), 5);
        Assert.Equal(0f, Xp.SkillDifferenceFactor(30, 5), 5);
        Assert.Equal(0.6f, Xp.SkillDifferenceFactor(30, 20), 5);   // 1 - 10 * 0.04
    }

    [Fact]
    public void Continuous_gain_uses_point_oh_two_the_exp_multiplier_and_the_race_map()
    {
        var stats = new CharacterStats();
        stats[StatsEnumerated.Science] = 1;
        // 0.02 * dt(10) * 0.75 * amount(1) * race(1.2) = 0.18; curve at 1: (100/101)^2
        float expected = 1 + 0.18f * (100f / 101) * (100f / 101);
        Assert.Equal(expected, Xp.Continuous(stats, Human, StatsEnumerated.Science, 10, 1), 4);
    }

    [Fact]
    public void Hit_dealt_trains_attack_weapon_dexterity_and_strength_and_scales_with_the_damage_option()
    {
        var a = new CharacterStats();
        var b = new CharacterStats();
        var doubled = new XpService(BaseGame, new BodyOptions { GlobalDamageMultiplier = 2 });
        Xp.Combat(a, Human, XpEvent.HitDealt, false, StatsEnumerated.Katanas, 1, 0.5f);
        doubled.Combat(b, Human, XpEvent.HitDealt, false, StatsEnumerated.Katanas, 1, 0.5f);
        foreach (var s in new[] { StatsEnumerated.MeleeAttack, StatsEnumerated.Katanas, StatsEnumerated.Dexterity, StatsEnumerated.Strength })
        {
            Assert.True(a[s] > 1, s.ToString());
            Assert.True(b[s] - 1 > 1.9f * (a[s] - 1), s.ToString());
        }
        Assert.Equal(1, a[StatsEnumerated.MeleeAttack] - 0.075f * (100f / 101) * (100f / 101) + 0f, 3);
        Assert.Equal(1, a[StatsEnumerated.MeleeDefence]);
    }

    [Fact]
    public void Hit_taken_trains_defence_and_toughness()
    {
        var s = new CharacterStats();
        Xp.Combat(s, Human, XpEvent.HitTaken, false, StatsEnumerated.None, 1, 0.5f);
        Assert.True(s[StatsEnumerated.MeleeDefence] > 1.1f);   // 2 x 0.075 x f
        Assert.True(s[StatsEnumerated.Toughness] > 1);
        Assert.Equal(1, s[StatsEnumerated.MeleeAttack]);
    }

    [Fact]
    public void Losing_a_limb_is_a_large_one_off_toughness_gain()
    {
        var s = new CharacterStats();
        // continuous(dt 1, amount 300): 0.02 * 0.75 * 300 = 4.5 (within the core curve's limit of 20)
        Assert.Equal(1 + 4.5f * (100f / 101) * (100f / 101), Xp.ToughnessFromLimbLoss(s, Human), 3);
    }

    [Fact]
    public void Weapon_weight_strength_factor_is_clamped()
    {
        Assert.Equal(0.1f, Xp.WeaponWeightStrengthFactor(5, 40, 1), 5);
        Assert.Equal(0.1f + 0.5f, Xp.WeaponWeightStrengthFactor(50, 40, 1), 5);
        Assert.Equal(0.1f + 1f, Xp.WeaponWeightStrengthFactor(500, 40, 1), 5);
    }

    [Fact]
    public void Strength_from_carrying_and_lockpicking_and_medic_gain()
    {
        var s = new CharacterStats();
        Assert.Equal(1f, Xp.StrengthFromCarrying(s, Human, 1, 10));         // unencumbered: nothing
        Assert.True(Xp.StrengthFromCarrying(s, Human, 0.5f, 10) > 1);
        Assert.True(Xp.Lockpicking(s, Human, true) > 1);
        var fail = new CharacterStats();
        Xp.Lockpicking(fail, Human, false);
        Assert.True(fail[StatsEnumerated.Lockpicking] < s[StatsEnumerated.Lockpicking]);
        var medic = new CharacterStats();
        Assert.True(Xp.Medic(medic, Human, Human, 15) > 1);
    }
}
