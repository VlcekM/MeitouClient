using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Tests.Bodies;

/// <summary>
/// The probe tables of docs/game/character-stats.md as tests against the installed game (base load order).
/// </summary>
[Slow]
public class BodyInstallTests
{
    [Theory]
    [InlineData("11623-Dialogue (10).mod")]
    [InlineData("14862-Dialogue.mod")]
    public void Shek_guards_roll_their_stats_from_the_records_integer_randomisation(string recordId)
    {
        var db = Database();
        var character = db.Find(recordId)!;
        Assert.Equal(10, character.GetInt("stats randomise"));
        Assert.False(character.Floats.ContainsKey("stats randomise"));
        var race = db.Find(character.GetReferences("race").First().TargetStringId)!;
        var factory = new BodyFactory(db);
        var a = factory.Create(race, character, 123, 1).Stats;
        var b = factory.Create(race, character, 123, 2).Stats;
        Assert.NotEqual(a[StatsEnumerated.MeleeAttack], b[StatsEnumerated.MeleeAttack]);
        Assert.NotEqual(a[StatsEnumerated.Strength], b[StatsEnumerated.Strength]);
    }

    static GameDatabase? s_cached;
    static readonly Lock Gate = new();

    static GameDatabase Database()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        lock (Gate) return s_cached ??= GameDatabase.Load(LoadOrder.BaseGame(install!));
    }

    static RaceData Race(string stringId)
    {
        var db = Database();
        return RaceData.From(db.Find(stringId) ?? throw new InvalidOperationException(stringId), db);
    }

    [Fact]
    public void The_nine_locational_damage_records_match_the_doc_table()
    {
        var parts = Database().OfType(FcsRecordType.LocationalDamage).Select(BodyPartTemplate.From).ToDictionary(p => p.Name);
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
        Assert.All(foreleg, p =>
        {
            Assert.Equal(BodyPartType.Arm, p.Type);
            Assert.False(p.Collapses);
        });
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
}
