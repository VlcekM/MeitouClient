using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Combat.CombatFixtures;

namespace Meitou.Tests.Combat;

/// <summary>The combat record views over invented records (no install), and the same facts over the install's records ([Slow]).</summary>
public class CombatDataTests
{
    static GameDatabase Db(params FcsRecord[] records)
    {
        var file = new FcsFile();
        file.Records.AddRange(records);
        var db = new GameDatabase();
        db.Apply(file, "test.mod");
        return db;
    }

    static FcsRecord Rec(string id, FcsRecordType type, string name) => new() { StringId = id, Flags = 0x10, Name = name, RecordType = type };

    [Fact]
    public void Records_read_into_typed_views()
    {
        var constants = Rec("1-t", FcsRecordType.CONSTANTS, "GLOBAL CONSTANTS");
        constants.Floats["damage multiplier"] = 0.5f;
        constants.Floats["cut damage 1"] = 10;
        constants.Floats["cut damage 99"] = 100;
        constants.Floats["block chance increase per 10levels"] = 20;
        constants.Ints["stumble damage max"] = 40;
        var weapon = Rec("2-t", FcsRecordType.WEAPON, "Blade");
        weapon.Ints["skill category"] = 1;
        weapon.Ints["attack mod"] = 3;
        weapon.Floats["cut damage multiplier"] = 1.5f;
        weapon.Floats["armour penetration"] = -0.2f;
        weapon.Bools["can block"] = true;
        weapon.References["race damage"] = [new FcsReference("9-t", 150, 0, 0)];
        var maker = Rec("3-t", FcsRecordType.WEAPON_MANUFACTURER, "Maker");
        maker.Floats["cut damage mod"] = 1.1f;
        maker.Ints["min cut damage"] = 2;
        maker.References["weapon models"] = [new FcsReference("4-t", 50, 100, 0)];
        var model = Rec("4-t", FcsRecordType.MATERIAL_SPECS_WEAPON, "Mk V");
        model.Ints["defence mod"] = -2;
        var armour = Rec("5-t", FcsRecordType.ARMOUR, "Plate");
        armour.Ints["class"] = 3;
        armour.Ints["material type"] = 3;
        armour.Ints["slot"] = 8;
        armour.Floats["cut into stun"] = 0.4f;
        armour.References["part coverage"] = [new FcsReference("101-t", 70, 0, 0)];
        var technique = Rec("6-t", FcsRecordType.COMBAT_TECHNIQUE, "Slash");
        technique.Strings["anim name"] = "chop";
        technique.Bools["katanas"] = true;
        technique.Bools["1 handed"] = true;
        technique.Ints["num techniques"] = 2;
        technique.Floats["num frames"] = 40;
        technique.Floats["anim blocked frame 1"] = 10;
        technique.Floats["anim blocked frame 2"] = 30;
        var data = CombatDatabase.From(Db(constants, weapon, maker, model, armour, technique));

        var c = data.Constants;
        Assert.Equal(5f, c.CutDamage1);          // 0.5 x 10
        Assert.Equal(50f, c.CutDamage99);
        Assert.Equal(20f, c.StumbleDamageMax);
        Assert.Equal(2f, c.BlockChanceIncreasePerLevel);
        Assert.Equal(70f, c.BaseBlockChance);   // missing: the base game's

        var w = data.Weapon("2-t")!;
        Assert.Equal((int)WeaponCategory.Sabres, w.SkillCategory);
        Assert.Equal(-0.2f, w.ArmourPenetration);
        Assert.True(w.CanBlock);
        Assert.Equal(150, w.RaceDamage["9-t"]);
        var m = data.Manufacturer("3-t")!;
        var stats = WeaponStats.Compute(1, w, m, data.WeaponMaterial("4-t")!, c);
        Assert.Equal(2f * 1.5f * 1.1f, stats.CutMultiplier, 5);
        Assert.Equal(2f, stats.MinCut);
        Assert.Equal(3, stats.AttackMod);
        Assert.Equal(-2, stats.DefenceMod);
        Assert.Equal(new WeaponModelEntry("4-t", 50, 100), m.Models.Single());

        var a = data.Armour("5-t")!;
        Assert.Equal(ArmourClass.Heavy, a.Class);
        Assert.Equal(ArmourType.MetalPlate, a.Material);
        Assert.Equal(70, a.CoverageOf("101-t"));
        Assert.Equal(0, a.CoverageOf("32-t"));

        var t = data.Techniques.Single();
        Assert.Equal("chop", t.AnimName);
        Assert.Equal(WeaponKinds.Katana | WeaponKinds.OneHanded, t.Kinds);
        Assert.Equal(2, t.Blows);
        Assert.Equal(0.25f, t.StrikeProgress(1), 5);
        Assert.Equal(0.75f, t.StrikeProgress(2), 5);
    }

    [Fact]
    public void The_crossbow_lerps_its_two_levels_by_quality()
    {
        var r = Rec("1-t", FcsRecordType.CROSSBOW, "Bow");
        r.Floats["pierce damage min 0"] = 20;
        r.Floats["pierce damage min 1"] = 30;
        r.Floats["pierce damage max 0"] = 30;
        r.Floats["pierce damage max 1"] = 45;
        r.Ints["range"] = 600;
        r.Ints["range 1"] = 800;
        r.Floats["reload time min"] = 3;
        r.Floats["reload time min 1"] = 2;
        var bow = CrossbowData.From(Db(r).Find("1-t")!);
        var half = bow.At(0.5f);
        Assert.Equal(25, half.PierceMin);
        Assert.Equal(37, half.PierceMax);      // 37.5 truncated
        Assert.Equal(700f, half.Range);
        Assert.Equal(2.5f, half.ReloadMin, 4);
        Assert.Equal(90, bow.At(1, 2).PierceMax);                       // 45 x 2
    }
}

/// <summary>The facts of docs/game/combat.md checked against the install's records (base load order). Skipped without the game.</summary>
[Slow]
public class CombatInstallTests
{
    static GameDatabase? cached;
    static CombatDatabase? combat;
    static readonly Lock gate = new();

    static GameDatabase Database()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        lock (gate) return cached ??= GameDatabase.Load(LoadOrder.BaseGame(install!));
    }

    static CombatDatabase Combat()
    {
        var db = Database();
        lock (gate) return combat ??= CombatDatabase.From(db);
    }

    [Fact]
    public void Record_counts_match_the_doc()
    {
        var data = Combat();
        Assert.Equal(48, data.Weapons.Count);
        Assert.Equal(44, data.Techniques.Count);
        Assert.Equal(7, data.Crossbows.Count);
        Assert.Equal(6, data.Guns.Count);
        Assert.Equal(9, Database().OfType(FcsRecordType.LOCATIONAL_DAMAGE).Count());
        Assert.Equal(142, data.Armours.Count);
    }

    [Fact]
    public void Constants_of_the_base_game_are_the_defaults_the_data_layer_assumes()
    {
        var c = Combat().Constants;
        var f = CombatConstants.BaseGame;
        Assert.Equal(f.DamageMultiplier, c.DamageMultiplier, 6);
        Assert.Equal(f.BluntDamage1, c.BluntDamage1, 4);
        Assert.Equal(f.BluntDamage99, c.BluntDamage99, 4);
        Assert.Equal(f.CutDamage1, c.CutDamage1, 4);
        Assert.Equal(f.CutDamage99, c.CutDamage99, 4);
        Assert.Equal(f.PierceDamageMultiplier, c.PierceDamageMultiplier, 5);
        Assert.Equal(f.StumbleDamageMax, c.StumbleDamageMax, 4);
        Assert.Equal(f.UnarmedDamageMult, c.UnarmedDamageMult, 5);
        Assert.Equal(f.AttackChanceFactor, c.AttackChanceFactor, 5);
        Assert.Equal(f.BaseBlockChance, c.BaseBlockChance, 5);
        Assert.Equal(f.BlockChanceIncreasePerLevel, c.BlockChanceIncreasePerLevel, 5);
        Assert.Equal(f.BlockChanceReductionPerLevel, c.BlockChanceReductionPerLevel, 5);
        Assert.Equal(f.MaxNumAttackSlots, c.MaxNumAttackSlots);
        Assert.Equal(f.DamageResistanceMin, c.DamageResistanceMin, 5);
        Assert.Equal(f.DamageResistanceMax, c.DamageResistanceMax, 5);
        Assert.Equal(f.BowDamage1, c.BowDamage1, 5);
        Assert.Equal(f.BowDamage99, c.BowDamage99, 5);
        // The stored numbers of the doc's table: 0.65 x 20 = 13, 0.65 x 80 = 52, 0.65 x 1.2 = 0.78.
        Assert.Equal(13f, c.CutDamage1, 3);
        Assert.Equal(52f, c.CutDamage99, 3);
        Assert.Equal(0.78f, c.PierceDamageMultiplier, 4);
    }

    [Fact]
    public void The_katana_record_has_the_dumped_fields()
    {
        var k = Combat().Weapon("476-gamedata.base")!;
        Assert.Equal("Katana", k.Name);
        Assert.Equal((int)WeaponCategory.Katanas, k.SkillCategory);
        Assert.Equal(1f, k.CutDamageMultiplier);
        Assert.Equal(0f, k.BluntDamageMultiplier);
        Assert.Equal(-0.3f, k.ArmourPenetration, 4);
        Assert.Equal(1.2f, k.BleedMult, 4);
        Assert.Equal(1.1f, k.HumanDamageMult, 4);
        Assert.Equal(0.6f, k.RobotDamageMult, 4);
        Assert.Equal(4, k.AttackMod);
        Assert.Equal(-4, k.DefenceMod);
        Assert.Equal(21, k.Length);
        Assert.Equal(2f, k.WeightKg);
        Assert.True(k.CanBlock);
        Assert.Equal(WeaponKinds.Katana, WeaponInstance.KindOf(k.SkillCategory));
    }

    [Fact]
    public void Pierce_is_set_on_eleven_of_the_forty_eight_weapons_all_creature_or_robot_creature()
    {
        var pierce = Combat().Weapons.Where(w => w.PierceDamageMultiplier != 0).ToList();
        Assert.Equal(11, pierce.Count);
        Assert.All(pierce, w => Assert.True(w.SkillCategory >= 9, w.Name));   // categories 9 to 20 are the creature attacks
        Assert.All(Combat().Weapons.Where(w => w.SkillCategory <= 8), w => Assert.Equal(0f, w.PierceDamageMultiplier));
    }

    [Fact]
    public void Technique_timing_facts_of_the_data()
    {
        var all = Combat().Techniques;
        var combo = all.Single(t => t.Name == "Downward combo");
        Assert.Equal(2, combo.Blows);
        Assert.Equal(0.509f, combo.StrikeProgress(1), 3);
        Assert.Equal(0.792f, combo.StrikeProgress(2), 3);
        Assert.Equal(0.527f, combo.StopProgress(1), 3);
        // The martial-arts rows count frames.
        var strike = all.Single(t => t.Name == "ma Double strike");
        Assert.Equal(57, strike.NumFrames);
        Assert.Equal(18f / 57, strike.StrikeProgress(1), 4);
        Assert.Equal(31f / 57, strike.StrikeProgress(2), 4);
        Assert.Equal(WeaponKinds.Unarmed, strike.Kinds);
        // Every sword, block and dodge row is in fractions.
        Assert.All(all.Where(t => t.Animal == 0 && !t.Kinds.HasFlag(WeaponKinds.Unarmed) || t.IsBlock), t => Assert.Equal(1, t.NumFrames));
        // Four human rows are disabled; one has no weapon flag at all.
        Assert.Equal(4, all.Count(t => t.Disabled));
        Assert.Equal(WeaponKinds.None, all.Single(t => t.Name == "heavy downcut").Kinds);
        // The "Dodge back" rows are flagged as blocks as well as dodges.
        var dodges = all.Where(t => t.Name.StartsWith("Dodge back")).ToList();
        Assert.Equal(4, dodges.Count);
        Assert.All(dodges, t => { Assert.True(t.IsBlock); Assert.True(t.IsDodge); });
        Assert.True(dodges.Single(t => t.Name.EndsWith("fly")).IsStumbleDodge);
        // The static rows: a negative distance marks the case a row is not for.
        var cutStatic = all.Single(t => t.Name == "Cut left static");
        Assert.Equal(-999f, cutStatic.AttackDistance);
        Assert.Equal(99f, cutStatic.AttackDistanceMinVsStatic);
        Assert.Equal(-10f, all.Single(t => t.Name == "Cut left").AttackDistanceMinVsStatic);
    }

    [Fact]
    public void Armour_coverage_refers_to_the_locational_damage_records()
    {
        var db = Database();
        var data = Combat();
        var parts = db.OfType(FcsRecordType.LOCATIONAL_DAMAGE).Select(r => r.StringId).ToHashSet();
        var covered = data.Armours.SelectMany(a => a.Coverage).ToList();
        Assert.NotEmpty(covered);
        Assert.All(covered, c => Assert.Contains(c.PartStringId, parts));
        Assert.All(covered, c => Assert.InRange(c.Percent, 0, 100));
        Assert.All(data.Armours, a => { Assert.InRange((int)a.Class, 0, 3); Assert.InRange((int)a.Material, 0, 3); });
    }

    [Fact]
    public void Every_manufacturer_model_names_a_weapon_model_record()
    {
        var data = Combat();
        Assert.NotEmpty(data.Manufacturers);
        foreach (var m in data.Manufacturers)
            foreach (var e in m.Models)
                Assert.NotNull(data.WeaponMaterial(e.ModelStringId));
    }
}

/// <summary>A duel between two base-game characters with real gear, race, techniques, clip lengths and constants ([Slow]).</summary>
[Slow]
public class RealDuelTests(ITestOutputHelper output)
{
    static readonly Lock gate = new();
    static (GameDatabase Db, CombatDatabase Combat, AnimationLengths Lengths, RaceData Race, GameInstall Install)? cached;

    static (GameDatabase Db, CombatDatabase Combat, AnimationLengths Lengths, RaceData Race, GameInstall Install) Data()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        lock (gate)
        {
            if (cached is { } c) return c;
            var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
            var race = RaceData.From(db.OfType(FcsRecordType.RACE).First(r => r.Name == "Greenlander"), db);
            return (cached = (db, CombatDatabase.From(db), AnimationLengths.Load(install!.Root), race, install!)).Value;
        }
    }

    static (Meitou.Simulation.World World, CombatSystem Combat, CharacterId A, CharacterId B) Build(int threads, ulong seed)
    {
        var (db, data, lengths, race, _) = Data();
        var katana = data.Weapon("476-gamedata.base")!;
        var maker = data.Manufacturers.First(m => m.Models.Any(e => e.Level == 50));
        var model = data.WeaponMaterial(maker.Models.First(e => e.Level == 50).ModelStringId)!;
        var chest = db.OfType(FcsRecordType.LOCATIONAL_DAMAGE).First(r => r.Name == "Chest").StringId;
        var armour = data.Armours.Where(a => a.Class == ArmourClass.Heavy && a.CoverageOf(chest) >= 90).OrderBy(a => a.StringId, StringComparer.Ordinal).First();
        var constants = GameConstants.FromDatabase(db);
        var combat = new CombatSystem(data.Techniques, data.Constants, constants, BodyOptions.Default, lengths, new CombatOptions { RecordLog = true });
        var world = new Meitou.Simulation.World(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false },
            new Meitou.Simulation.OpenGroundWalkability((_, _) => 150), [combat]);
        CharacterId Spawn(string name, float skill, ArmourData? worn, float x)
        {
            var stats = new CharacterStats();
            foreach (var s in new[] { StatsEnumerated.MeleeAttack, StatsEnumerated.MeleeDefence, StatsEnumerated.Katanas, StatsEnumerated.Dexterity, StatsEnumerated.Strength, StatsEnumerated.Toughness })
                stats[s] = skill;
            var fighter = new Fighter(WeaponInstance.Create(0.5f, katana, maker, model, data.Constants), worn is null ? [] : [ArmourPiece.Create(worn, 0.6f)]);
            var cold = new Meitou.Simulation.CharacterCold { Name = name, Stats = stats, Race = race, Medical = MedicalState.Create(race, stats.Strength), Fighter = fighter };
            return world.Characters.Spawn(new Meitou.Simulation.CharacterHot { Position = new(x, 150, 1000), Health = 100 }, cold, 0);
        }
        var a = Spawn("Plated", 45, armour, 1000);
        var b = Spawn("Bare", 55, null, 1014);
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = 0 });
        world.Commands.Enqueue(new AttackOrder([b], a) { Tick = 0 });
        return (world, combat, a, b);
    }

    [Fact]
    public void Real_gear_duel_resolves_and_matches_at_every_thread_count()
    {
        var (_, data, lengths, _, _) = Data();
        // The clips the human sword, block and dodge techniques name are all known to the skeletons (otherwise they would play 1 second).
        var human = data.Techniques.Where(t => t.Animal == 0 && !t.Disabled && (t.Kinds & WeaponKinds.Unarmed) == 0).ToList();
        output.WriteLine("clip lengths: " + string.Join(", ", human.Select(t => $"{t.AnimName} {lengths.Of(t.AnimName):0.00}")));

        List<CombatLogEntry>? first = null;
        ulong hash = 0;
        foreach (int threads in new[] { 1, 4, 16 })
        {
            var (world, combat, a, b) = Build(threads, 99);
            using (world)
            {
                int ticks = 0;
                while (ticks < 20000 && !world.Characters.Cold(a.Slot)!.Medical!.Incapacitated && !world.Characters.Cold(b.Slot)!.Medical!.Incapacitated)
                {
                    world.RunTick();
                    ticks++;
                }
                var sa = combat.StateOf(a);
                var sb = combat.StateOf(b);
                output.WriteLine($"threads {threads}: {ticks} ticks ({ticks / 30f:0.0} s); plated thrown {sa.Thrown} landed {sa.Landed} taken {sa.Taken}; bare thrown {sb.Thrown} landed {sb.Landed} taken {sb.Taken}");
                Assert.True(world.Characters.Cold(a.Slot)!.Medical!.Incapacitated != world.Characters.Cold(b.Slot)!.Medical!.Incapacitated);
                if (first is null) { first = [.. combat.Log]; hash = world.StateHash(); }
                else { Assert.Equal(first, combat.Log); Assert.Equal(hash, world.StateHash()); }
            }
        }
        Assert.InRange(first!.Count, 4, 400);
    }
}
