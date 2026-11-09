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
        var r = new FcsRecord { StringId = id, Flags = New, Name = id, RecordType = FcsRecordType.Faction };
        r.Ints["default relation"] = defaultRelation;
        if (explicitRelations.Length > 0)
        {
            r.References["relations"] = [.. explicitRelations.Select(e => new FcsReference(e.Target, e.Value, 0, 0))];
        }

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
        int a = rel.IndexOf("1-t");
        int b = rel.IndexOf("2-t");
        int c = rel.IndexOf("3-t");
        int d = rel.IndexOf("4-t");

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
    public void Constants_apply_the_loaders_rescalings_and_fall_back_to_the_editor_defaults()
    {
        var r = new FcsRecord { StringId = "1-t", Flags = New, Name = "GLOBAL CONSTANTS", RecordType = FcsRecordType.Constants };
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
        Assert.Equal(20, c.WeightStrengthDiff1X);
        Assert.Equal(24, c.StarvationTime);   // the editor default: the record does not have the field
        Assert.Equal(GameConstants.Default.StarvationTime, c.StarvationTime);
        Assert.Equal(0.0005f * 0.1f, GameConstants.Default.BleedingClotRate, 9);
        Assert.Equal(5, GameConstants.FromDatabase(new GameDatabase()).Sunrise);
    }
}

/// <summary>
/// The same views against the installed game, with the probe values the docs quote.
/// </summary>
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
        // Standing: the whole-body idles (the editor's six poses and "stand 1"); the sword idle only with a weapon in hand;
        // squat is an action.
        var idles = lib.Idles(plain).Select(p => lib.Definitions[p.Index].Name).ToList();
        Assert.Contains("stand 1", idles);
        Assert.DoesNotContain("squat", idles);
        Assert.DoesNotContain("stand 1 sword", idles);
        Assert.Contains("stand 1 sword", lib.Idles(sword).Select(p => lib.Definitions[p.Index].Name));
        // A hurt left leg takes the limp clips and drops the plain walk.
        var limp = new AnimationStance { LeftLeg = 20 };
        Assert.Contains("limp L lower", Names(AnimationArea.Lower, 14, limp));
    }

    /// <summary>
    /// The installed buildings give the stats panel's class inputs (docs/game/ui-screens.md "Selected building"): a Bed's Healing bonus x8
    /// and a Training Dummy MkII's Max Skill Level 10 from the output value <c>+0x1a4</c>; only the hydroponic farm draws an Efficiency line;
    /// a Ceiling Lamp in a building is indoors (slot 59), a Light Post I never.
    /// </summary>
    [Fact, Slow]
    public void The_installed_buildings_give_the_panel_class_inputs()
    {
        var db = InstallData.BaseGame;
        Assert.SkipWhen(db is null, "Kenshi install not found");
        Meitou.Data.Gameplay.Construction.MachineData Machine(string name) =>
            Data.Gameplay.Construction.MachineData.From(
                db!,
                db!.Records.Values.First(r => r.Type == FcsRecordType.Building && r.Name == name))!;
        Assert.Equal(8, Machine("Bed").OutputValue);
        Assert.Equal(10, (int)Machine("Training Dummy MkII").OutputValue - 1);
        Assert.True(Machine("Hydroponic Wheatstraw").Farm!.Indoor);
        Assert.False(Machine("Wheat Farm S").Farm!.Indoor);
        Assert.True(Machine("Ceiling Lamp") is { IsLight: true, InteriorFurniture: true });
        Assert.True(Machine("Light Post I") is { IsLight: true, InteriorFurniture: false });
    }
}
