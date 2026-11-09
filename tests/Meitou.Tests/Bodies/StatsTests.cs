using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Tests.Bodies;

public class StatsTests
{
    [Theory]
    [InlineData(50f, 0.2550f)]     // about a quarter at 50
    [InlineData(101f, 0f)]         // nothing at the cap
    public void Core_gain_follows_the_squared_curve(float stat, float f)
    {
        float after = CharacterStats.CoreGain(stat, 1);
        Assert.Equal(stat + f, after, 3);
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
        var part = new FcsRecord { StringId = "1-t", Flags = 0x10, Name = "Head", RecordType = FcsRecordType.LocationalDamage };
        part.Ints["body part type"] = 3;
        part.Bools["death"] = true;
        part.Bools["collapses"] = true;
        part.Ints["collapse part"] = 1;
        var race = new FcsRecord { StringId = "2-t", Flags = 0x10, Name = "Test", RecordType = FcsRecordType.Race };
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
    public void Stats_from_groups_fan_out_and_save_round_trips()
    {
        var v = CharacterStats.FromGroups(combat: 40, unarmed: 0, stealth: 30, ranged: 0, strength: 25);
        Assert.Equal(40, v[(int)StatsEnumerated.MeleeAttack]);
        Assert.Equal(30, v[(int)StatsEnumerated.Stealth]);
        Assert.Equal(25, v[(int)StatsEnumerated.Strength]);
        // The constructor's fan-out (FUN_14064cb30): combat sets the body stats too, a tenth of it martial arts and dodge, half assassination;
        // stealth overwrites assassination and medic and lifts dexterity to max(unarmed, stealth); the strength field wins over combat's.
        Assert.Equal(4, v[(int)StatsEnumerated.MartialArts]);
        Assert.Equal(4, v[(int)StatsEnumerated.Dodge]);
        Assert.Equal(40, v[(int)StatsEnumerated.Toughness]);
        Assert.Equal(30, v[(int)StatsEnumerated.Dexterity]);
        Assert.Equal(30, v[(int)StatsEnumerated.Assassination]);
        Assert.Equal(30, v[(int)StatsEnumerated.Medic]);
        Assert.Equal(40, v[(int)StatsEnumerated.Athletics]);
        Assert.Equal(1, v[(int)StatsEnumerated.Crossbows]);
        var u = CharacterStats.FromGroups(combat: 40, unarmed: 50, stealth: 0, ranged: 20, strength: 0);
        Assert.Equal(
            (50f, 50f, 50f, 40f),
            (u[(int)StatsEnumerated.MartialArts], u[(int)StatsEnumerated.Dodge],
                u[(int)StatsEnumerated.Dexterity], u[(int)StatsEnumerated.Strength]));
        Assert.Equal((20f, 20f, 20f), (u[(int)StatsEnumerated.Crossbows], u[(int)StatsEnumerated.Perception], u[(int)StatsEnumerated.Turrets]));

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
