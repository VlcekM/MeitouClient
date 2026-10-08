using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Tests.Gameplay;

public class GameplayDataTests
{
    const uint New = 0x10;

    static FcsRecord Faction(string id, int defaultRelation, params (string Target, int Value)[] explicitRelations)
    {
        var r = new FcsRecord { StringId = id, Flags = New, Name = id, RecordType = FcsRecordType.FACTION };
        r.Ints["default relation"] = defaultRelation;
        if (explicitRelations.Length > 0) r.References["relations"] = [.. explicitRelations.Select(e => new FcsReference(e.Target, e.Value, 0, 0))];
        return r;
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
    public void Relations_start_from_the_explicit_entry_else_the_lower_default_and_self_is_100()
    {
        var db = Db(
            Faction("1-t", 0),
            Faction("2-t", -50),
            Faction("3-t", 20, ("1-t", 75), ("3-t", -10), ("missing-t", 5)),
            Faction("4-t", 60, ("2-t", 0)));
        var rel = FactionRelations.Build(db);
        int a = rel.IndexOf("1-t"), b = rel.IndexOf("2-t"), c = rel.IndexOf("3-t"), d = rel.IndexOf("4-t");
        Assert.Equal(100, rel.Get(a, a));
        Assert.Equal(-50, rel.Get(a, b));        // min(0, -50)
        Assert.Equal(-50, rel.Get(b, a));
        Assert.Equal(75, rel.Get(c, a));         // explicit, one way only
        Assert.Equal(0, rel.Get(a, c));          // min(0, 20)
        Assert.Equal(100, rel.Get(c, c));        // self wins over an explicit self entry
        Assert.Equal(0, rel.Get(d, b));          // explicit 0 beats the default -50
        Assert.Equal(-50, rel.Get(b, d));
        Assert.Equal(20, rel.Get(d, c));         // min(60, 20)
        Assert.Equal(-1, rel.IndexOf("missing-t"));
    }

    [Fact]
    public void Thresholds_are_minus_30_for_hostile_and_50_for_ally()
    {
        var db = Db(Faction("1-t", 0), Faction("2-t", 0, ("1-t", -30)), Faction("3-t", 0, ("1-t", -29), ("2-t", 50)), Faction("4-t", 0, ("1-t", 49)));
        var rel = FactionRelations.Build(db);
        int one = rel.IndexOf("1-t"), two = rel.IndexOf("2-t"), three = rel.IndexOf("3-t"), four = rel.IndexOf("4-t");
        Assert.True(rel.IsHostile(two, one));
        Assert.False(rel.IsHostile(three, one));
        Assert.False(rel.IsHostile(one, one));
        Assert.True(rel.IsAlly(three, two));
        Assert.False(rel.IsAlly(four, one));
        Assert.True(rel.IsAlly(one, one));
    }

    [Fact]
    public void Coexistence_is_read_and_one_way()
    {
        var a = Faction("1-t", 0);
        a.References["coexistence"] = [new("2-t", 0, 0, 0)];
        var rel = FactionRelations.Build(Db(a, Faction("2-t", 0)));
        Assert.True(rel.Coexists(0, 1));
        Assert.False(rel.Coexists(1, 0));
        Assert.True(rel.Coexists(1, 1));
    }

    [Fact]
    public void Constants_apply_the_loaders_rescalings_and_fall_back_to_the_editor_defaults()
    {
        var r = new FcsRecord { StringId = "1-t", Flags = New, Name = "GLOBAL CONSTANTS", RecordType = FcsRecordType.CONSTANTS };
        r.Floats["bleed rate"] = 0.01f;
        r.Floats["exp gain multiplier"] = 3;
        r.Floats["xp rate athletics"] = 1;
        r.Floats["minimum lockpick chance"] = 5;
        r.Floats["block chance increase per 10levels"] = 12;
        r.Floats["min dismantle materials percentage"] = 60;
        r.Floats["appearance random deviation percentage"] = 0.4f;
        r.Ints["weight strength diff 1x"] = 20;   // an int in the data
        r.Ints["days per year"] = 100;
        var c = GameConstants.FromDatabase(Db(r));
        Assert.Equal(0.001f, c.BleedRate, 6);
        Assert.Equal(0.75f, c.ExpGainMultiplier, 6);
        Assert.Equal(0.5f, c.XpRateAthletics, 6);
        Assert.Equal(0.05f, c.MinimumLockpickChance, 6);
        Assert.Equal(0.6f, c.MinDismantleMaterialsFraction, 6);
        Assert.Equal(0.2f, c.AppearanceRandomDeviation, 6);
        Assert.Equal(20, c.WeightStrengthDiff1x);
        Assert.Equal(24, c.StarvationTime);   // the editor default: the record does not have the field
        Assert.Equal(GameConstants.Default.StarvationTime, c.StarvationTime);
        Assert.Equal(0.0005f * 0.1f, GameConstants.Default.BleedingClotRate, 9);
        Assert.Equal(5, GameConstants.FromDatabase(new GameDatabase()).Sunrise);
    }

    [Fact]
    public void Town_setup_rules_clamp_the_radius_multiplier_and_clear_nest_marker_foliage()
    {
        TownData Make(int type, float mult, float range)
        {
            var r = new FcsRecord { StringId = "1-t", Flags = New, Name = "town", RecordType = FcsRecordType.TOWN };
            r.Ints["type"] = type;
            r.Floats["town radius mult"] = mult;
            r.Floats["no-foliage range"] = range;
            return TownData.From(Db(r).Find("1-t")!);
        }
        Assert.Equal(1, Make(2, 0.05f, 100).TownRadiusMult);
        Assert.Equal(0.1f, Make(2, 0.1f, 100).TownRadiusMult);
        Assert.Equal(5, Make(2, 9, 100).TownRadiusMult);
        Assert.Equal(100, Make(2, 1, 100).NoFoliageRange);
        Assert.Equal(0, Make((int)TownType.NestMarker, 1, 100).NoFoliageRange);
        Assert.Equal(TownType.Ruins, Make(4, 1, 0).Type);
    }
}

/// <summary>The same views against the installed game, with the probe values the docs quote.</summary>
[Slow]
public class GameplayInstallTests
{
    // The base game load order: the numbers of the docs' probes (factions-squads-towns.md, game-loop.md) hold for it and for the full order.
    static GameDatabase Database()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        return InstallData.BaseGame!;
    }

    [Fact]
    public void Constants_match_the_probe_table_of_game_loop()
    {
        var c = GameConstants.FromDatabase(Database());
        Assert.Equal(100, c.DaysPerYear);
        Assert.Equal(5, c.Sunrise);
        Assert.Equal(23, c.Sunset);
        Assert.Equal(54, c.Latitude);
        Assert.Equal(0.35f, c.NightDarkness, 6);
        Assert.Equal(6, c.BuildSpeed);
        Assert.Equal(2, c.ProductionSpeed);
        Assert.Equal(0.3f, c.ResearchRate, 6);
        Assert.Equal(1.3f, c.ResearchLevelIncreaseRate, 6);
        Assert.Equal(1, c.PrisonTime);
        Assert.Equal(60, c.StarvationTime);
        Assert.Equal(20, c.KnockoutTimeBase);
        Assert.Equal(4, c.AnimationBlendRate);
        Assert.Equal((0.33f, 0.7f, 3f), (c.BedHungerRate, c.EncumbranceHungerRate, c.FedRecoveryRateMult));
        Assert.Equal((0.25f, 2f), (c.HealRateMult, c.RestingHealRateMult));
        Assert.Equal((1.5f, 1f, 1f), (c.BodypartDegenerationRate, c.RobotWearRate, c.StunRecoveryRate));
        Assert.Equal((10, 20, 30), (c.MaxSquads, c.MaxSquadSize, c.MaxFactionSize));
        // The loader's rescalings (character-stats.md): bleed rate 0.01 x0.1, clot 0.0085 x0.1, blood recovery 0.4 x0.1, exp gain 3 x0.25.
        Assert.Equal(0.001f, c.BleedRate, 7);
        Assert.Equal(0.00085f, c.BleedingClotRate, 7);
        Assert.Equal(0.04f, c.BloodRecoveryRate, 7);
        Assert.Equal(0.75f, c.ExpGainMultiplier, 6);
        Assert.Equal(0.5f, c.XpRateAthletics, 6);
        Assert.Equal(0.05f, c.MinimumLockpickChance, 6);
        Assert.Equal(0.6f, c.MinDismantleMaterialsFraction, 6);
        Assert.Equal(0.2f, c.AppearanceRandomDeviation, 6);
        Assert.Equal(2f, c.MedkitDrain1, 6);
        Assert.Equal(20, c.WeightStrengthDiff1x);
        Assert.Equal(3, c.MedicSpeedMult);
    }

    [Fact]
    public void Faction_relations_reproduce_the_documented_counts()
    {
        var factions = FactionData.LoadAll(Database());
        Assert.Equal(103, factions.Count);
        var player = factions.Single(f => f.Name == "Nameless");
        Assert.Equal("204-gamedata.base", player.Id);
        var rel = FactionRelations.Build(factions);
        int p = rel.IndexOf(player.Id);
        int pairs = 0, hostile = 0, allied = 0, strictlyAllied = 0, explicitEntries = 0;
        for (int a = 0; a < rel.Count; a++)
        {
            if (a == p) continue;
            for (int b = 0; b < rel.Count; b++)
            {
                if (b == p || b == a) continue;
                pairs++;
                if (rel.IsHostile(a, b)) hostile++;
                if (rel.IsAlly(a, b)) allied++;
                if (rel.Get(a, b) > 50) strictlyAllied++;
            }
            explicitEntries += factions[a].Relations.Keys.Count(id => id != player.Id && id != factions[a].Id && rel.IndexOf(id) >= 0);
        }
        Assert.Equal(102 * 101, pairs);
        Assert.Equal(3356, hostile);
        Assert.Equal(62, allied);
        Assert.Equal(56, strictlyAllied);
        Assert.Equal(221, explicitEntries);
        Assert.Equal(18, factions.Count(f => f.DefaultRelation <= -30));
        Assert.Equal(21, factions.Count(f => f.DefaultRelation != 0));
        Assert.Equal(30, factions.Count(f => f.NotReal));
        Assert.Equal(96, factions.Count(f => f.RoamingPopulation == 50));
        Assert.Equal(38, factions.Count(f => f.FundamentalType == FundamentalType.Bandit));
        Assert.Equal(81, factions.Sum(f => f.Coexistence.Count));
        Assert.Equal(100, rel.Get(p, p));
    }

    [Fact]
    public void The_base_games_animation_records_form_a_walk_jog_run_chain_and_weapon_and_idle_variants()
    {
        var lib = AnimationLibrary.FromDatabase(Database());
        string[] Names(AnimationArea area, float speed, AnimationStance s)
        {
            var into = new List<(int Index, float Weight)>();
            lib.Movement(area, speed, s, into);
            return [.. into.Where(p => p.Weight > 0).Select(p => lib.Definitions[p.Index].Name).Order()];
        }
        var plain = new AnimationStance();
        Assert.Equal(["walk lower"], Names(AnimationArea.Lower, 14, plain));
        Assert.Equal(["walk upper"], Names(AnimationArea.Upper, 14, plain));
        Assert.Equal(["jog lower", "walk lower"], Names(AnimationArea.Lower, 30, plain));
        Assert.Equal(["jog upper"], Names(AnimationArea.Upper, 45, plain));
        Assert.Equal(["jog lower", "run lower"], Names(AnimationArea.Lower, 60, plain));
        Assert.Equal(["run lower"], Names(AnimationArea.Lower, 120, plain));
        var sword = new AnimationStance { Right = new HandHold(WeaponKinds.Katana) };
        Assert.Contains("walk upper sword", Names(AnimationArea.Upper, 14, sword));
        Assert.DoesNotContain("walk upper", Names(AnimationArea.Upper, 14, sword));
        Assert.Contains("run upper sword", Names(AnimationArea.Upper, 120, sword));
        // Standing: the whole-body idles (the editor's six poses and "stand 1"); the sword idle only with a weapon in hand; squat is an action.
        var idles = lib.Idles(plain).Select(p => lib.Definitions[p.Index].Name).ToList();
        Assert.Contains("stand 1", idles);
        Assert.DoesNotContain("squat", idles);
        Assert.DoesNotContain("stand 1 sword", idles);
        Assert.Contains("stand 1 sword", lib.Idles(sword).Select(p => lib.Definitions[p.Index].Name));
        // A hurt left leg takes the limp clips and drops the plain walk.
        var limp = new AnimationStance { LeftLeg = 20 };
        Assert.Contains("limp L lower", Names(AnimationArea.Lower, 14, limp));
    }

    [Fact]
    public void Squad_templates_have_the_documented_counts()
    {
        var db = Database();
        var templates = SquadTemplate.LoadAll(db).Where(t => !t.IsUnique).ToList();
        Assert.Equal(959, templates.Count);
        Assert.Equal(853, templates.Sum(t => t.Squad.Count));
        Assert.Equal(127, templates.Sum(t => t.Squad2.Count));
        Assert.Equal(169, templates.Sum(t => t.Animals.Count));
        Assert.Equal(23, templates.Sum(t => t.Animals2.Count));
        Assert.Equal(567, templates.Count(t => t.Leader is not null));
        Assert.Equal(24, templates.Count(t => t.Leader is { V1: 100 }));
        Assert.Equal(121, templates.Count(t => t.WorldState.Count > 0));
        Assert.All(templates, t => Assert.NotEmpty(t.Id));
    }


    [Fact]
    public void New_game_starts_match_the_documented_records()
    {
        var starts = NewGameStart.LoadAll(Database());
        Assert.Equal(13, starts.Count);
        var wanderer = NewGameStart.Find(starts, NewGameStart.DefaultName)!;
        Assert.Equal((1000, "Default", "RPG"), (wanderer.Money, wanderer.Difficulty, wanderer.Style));
        Assert.Equal(new System.Numerics.Vector2(4000, 4000), wanderer.StartPosition);
        Assert.False(wanderer.ForceStartPos);
        Assert.Single(wanderer.Squad);
        Assert.Contains(wanderer.Towns, t => Database().Find(t.Id)?.Name == "The Hub");
        var bottom = NewGameStart.Find(starts, "rock bottom")!;
        Assert.True(bottom.ForceStartPos);
        Assert.Equal(new System.Numerics.Vector2(66484, -90000), bottom.StartPosition);
        Assert.Equal(1, starts.Count(s => s.ForceStartPos));
        Assert.Equal(3, starts.Count(s => s.ForceRace.Count > 0));
        Assert.Equal(4, starts.Count(s => s.FactionRelations.Count > 0));
        Assert.Equal(4000, NewGameStart.Find(starts, "The Freedom Seekers")!.Money);
        Assert.Equal(8, starts.Count(s => s.Id.EndsWith("gamedata.base")));
    }
    [Fact]
    public void Towns_have_the_documented_type_histogram_and_the_hub_is_a_town()
    {
        var towns = TownData.LoadAll(Database());
        Assert.Equal(346, towns.Count);
        int[] expected = [40, 32, 80, 29, 106, 13, 13, 1, 2, 24, 6];
        for (int t = 0; t < expected.Length; t++) Assert.Equal(expected[t], towns.Count(x => (int)x.Type == t));
        var hub = towns.First(t => t.Name == "The Hub");
        Assert.Equal(TownType.Town, hub.Type);
        Assert.Equal(350, hub.SizeRadius);
        Assert.Equal(1, hub.TownRadiusMult);
        Assert.NotNull(hub.Faction);
        Assert.Equal(4, hub.Residents.Count);
        Assert.All(towns.Where(t => t.Type == TownType.NestMarker), t => Assert.Equal(0, t.NoFoliageRange));
    }
}
