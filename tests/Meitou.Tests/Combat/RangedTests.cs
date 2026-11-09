using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;
using static Meitou.Tests.Combat.CombatFixtures;
using static Meitou.Tests.Combat.DuelFixture;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Combat;

/// <summary>
/// Crossbows (docs/game/combat.md "Ranged and turrets"): a skilled shooter hits and spends a bolt, runs dry and draws its melee weapon,
/// and a skirmish is deterministic.
/// </summary>
public class RangedTests(ITestOutputHelper output)
{
    /// <summary>
    /// CROSSBOW "Ranger" (66169-Newwworld.mod) as dumped from the base game; its ammunition stands in as "bolts".
    /// </summary>
    internal static CrossbowData Ranger { get; } = new()
    {
        StringId = "66169-Newwworld.mod", Name = "Ranger",
        PierceDamageMin0 = 20, PierceDamageMin1 = 30, PierceDamageMax0 = 30, PierceDamageMax1 = 45, Range0 = 600, Range1 = 800,
        ReloadTimeMin0 = 3, ReloadTimeMin1 = 2, ReloadTimeMax0 = 4.5f, ReloadTimeMax1 = 4, AccuracyDeviation0 = 5, AccuracyDeviation1 = 3,
        ShotSpeed0 = 400, ShotSpeed1 = 500, BleedMult = 0.6f, NumShots = 1, AccuracyPerfectSkill = 60, AmmoId = "bolts",
    };

    // ------------------------------------------------------------------ worlds

    /// <summary>
    /// A crossbowman: the given skills, a Ranger at quality 0 (or none) and <paramref name="bolts"/> bolts in one item, plus a katana for melee.
    /// </summary>
    internal static CharacterCold Shooter(float crossbows, float perception, int bolts, int faction = 0, CrossbowData? bow = null,
        float friendlyFire = 0)
    {
        var cold = new Combatant { Name = "shooter" }.MakeCold();
        cold.Stats![StatsEnumerated.Crossbows] = crossbows;
        cold.Stats[StatsEnumerated.Perception] = perception;
        cold.Stats[StatsEnumerated.FriendlyFire] = friendlyFire;
        var x = bow ?? Ranger;
        cold.Fighter = new Fighter(cold.Fighter!.Weapon) { Crossbow = CrossbowInstance.Create(0, x, Constants, 1) };
        cold.Faction = faction;
        cold.Inventory = new Inventory();
        if (bolts > 0)
        {
            cold.Inventory.Items.Add(new ItemInstance { Record = x.AmmoId, Section = "main", Charges = bolts, MaxCharges = 30, StackLimit = 99 });
        }

        return cold;
    }

    static CharacterId Put(SimWorld world, CharacterCold cold, Vector3 at) =>
        world.Characters.Spawn(new CharacterHot { Position = at, Health = 100 }, cold, 0);

    static CharacterCold Dummy(int faction, float toughness = 50)
    {
        var cold = new Combatant { Name = "target", Toughness = toughness }.MakeCold();
        cold.Faction = faction;
        return cold;
    }

    [Fact]
    public void A_skilled_shooter_reloads_and_hits_a_still_target_at_range_using_one_bolt_a_shot()
    {
        using var world = NewWorld(5, 1, out var combat);
        var shooter = Put(world, Shooter(100, 100, 3), new Vector3(1000, 150, 1000));
        var target = Put(world, Dummy(1, toughness: 100), new Vector3(1300, 150, 1000));
        world.Commands.Enqueue(new AttackOrder([shooter], target) { Tick = 0 });
        int ticks = 0;
        while (ticks < 900 && !combat.Log.Any(e => e.Ranged)) { world.RunTick(); ticks++; }
        var hit = Assert.Single(combat.Log, e => e.Ranged);
        output.WriteLine($"hit after {ticks} ticks: part {hit.Part} pierce {hit.Pierce:0.0} KO {hit.Knockout}");
        Assert.True(hit.Intended);
        Assert.Equal(target.Slot, hit.Defender);
        Assert.True(hit.Pierce > 0);
        Assert.Equal(0f, hit.Cut);
        // Reload (3 s for s = 1) + steady aim (0.4 s) + flight (300 units at about 397 units a second): about 4.2 s.
        Assert.InRange(ticks, 3.4 * 30, 5 * 30);
        Assert.Equal(1, combat.ProjectilesFired);
        Assert.Equal(2, Ammunition.Count(world.Characters.Cold(shooter.Slot)!.Inventory, "bolts"));
        var s = combat.StateOf(shooter);
        Assert.True(s.Ranged);
        Assert.Equal(1, s.Landed);
        Assert.Equal(1, combat.StateOf(target).Taken);
        // XP: aiming and reloading trained the crossbow skill and perception.
        Assert.True(world.Characters.Cold(shooter.Slot)!.Stats![StatsEnumerated.Crossbows] > 99.9f);
    }

    /// <summary>
    /// Combat mode (docs/game/ui-screens.md section 4 "Combat mode"): a shooting fight is the ranged controller's combatMode,
    /// not the melee flag, so isInCombatMode(1, 1) holds while its target is up and (1, 0) does not; the shooting task sets
    /// Current Skill to Crossbows; the stats show the crossbow lines (mode 2) only while the shooter stands still.
    /// </summary>
    [Fact]
    public void A_shooter_is_in_ranged_combat_mode_only_and_shows_the_crossbow_stats_while_still()
    {
        using var world = NewWorld(5, 1, out var combat);
        var shooter = Put(world, Shooter(100, 100, 3), new Vector3(1000, 150, 1000));
        var target = Put(world, Dummy(1, toughness: 100), new Vector3(1300, 150, 1000));
        world.Commands.Enqueue(new AttackOrder([shooter], target) { Tick = 0 });
        var cold = world.Characters.Cold(shooter.Slot)!;
        for (int t = 0; t < 60 && !combat.StateOf(shooter).Ranged; t++) world.RunTick();
        world.RunTick();
        Assert.True(combat.StateOf(shooter).Ranged);
        Assert.True(CombatSystem.RangedCombatMode(cold));
        Assert.False(combat.MeleeCombatMode(shooter, cold));
        Assert.True(combat.IsInCombatMode(world.Characters, shooter, cold, true, true));
        Assert.False(combat.IsInCombatMode(world.Characters, shooter, cold, true, false));
        Assert.Equal(StatsEnumerated.Crossbows, cold.SkillInUse);
        Assert.Equal(2, combat.StatsDisplayMode(shooter, cold, moving: false));
        Assert.Equal(0, combat.StatsDisplayMode(shooter, cold, moving: true));
        // A target that is down ends ranged combat mode though the fight goes on.
        world.Characters.Cold(target.Slot)!.CarriedBy = shooter;
        Assert.False(combat.IsInCombatMode(world.Characters, shooter, cold, true, true));
    }

    /// <summary>
    /// Strength XP in combat mode (calculateWeaponWeightXPMult, docs/game/ui-screens.md section 4): the weight of the weapon in hand,
    /// a drawn crossbow's own, and 1 - encumbrance with nothing drawn; out of combat mode the carrying rate stays.
    /// </summary>
    [Fact]
    public void Strength_xp_in_combat_mode_takes_the_drawn_weapons_weight_or_fists()
    {
        var bodies = new BodySystem(Bodies.BodyFixtures.BaseGame);
        var cold = Shooter(50, 50, 0);
        var drawnBow = CrossbowInstance.Create(0, Ranger, Constants, 1, weight: 75);
        cold.Fighter = new Fighter(cold.Fighter!.Weapon) { Crossbow = drawnBow, EncumbranceFactor = 0.6f };
        float strength = cold.Stats!.Strength, mult = cold.Medical!.StatMultiplier(StatsEnumerated.Strength);
        float Factor(float w) => 0.1f + Math.Clamp((w - strength * mult) / 20, 0, 1);
        var x = new ExtendedStats(1, 0.3f, 3, -40, 30, 40, 1);
        Assert.Equal(x, bodies.WithCombatMode(x, cold, false));
        cold.DrawnWeapon = HandHold.OfCategory((int)WeaponCategory.Bow).Weapon;
        Assert.Equal(Factor(75), bodies.WithCombatMode(x, cold, true).StrengthXp, 5);
        cold.DrawnWeapon = HandHold.OfCategory(cold.Fighter.Weapon!.Data.SkillCategory).Weapon;
        Assert.Equal(Factor(cold.Fighter.Weapon.Stats.Weight), bodies.WithCombatMode(x, cold, true).StrengthXp, 5);
        cold.DrawnWeapon = WeaponKinds.None;
        var fists = bodies.WithCombatMode(x, cold, true);
        Assert.True(fists.Fists);
        Assert.Equal(0.4f, fists.StrengthXp, 5);
    }

    [Fact]
    public void A_shooter_running_dry_draws_its_melee_weapon_and_closes_in()
    {
        using var world = NewWorld(6, 1, out var combat);
        var shooter = Put(world, Shooter(60, 60, 2), new Vector3(1000, 150, 1000));
        var target = Put(world, Dummy(1, toughness: 100), new Vector3(1200, 150, 1000));
        world.Commands.Enqueue(new AttackOrder([shooter], target) { Tick = 0 });
        bool sawRanged = false;
        for (int k = 0; k < 2400 && combat.StateOf(shooter).Thrown == 0; k++)
        {
            world.RunTick();
            sawRanged |= combat.StateOf(shooter).Ranged;
        }
        var s = combat.StateOf(shooter);
        output.WriteLine($"fired {combat.ProjectilesFired}, ranged hits {combat.Log.Count(e => e.Ranged)}, thrown {s.Thrown}, tick {world.Tick}");
        Assert.True(sawRanged);
        Assert.Equal(2, combat.ProjectilesFired);
        Assert.Equal(0, Ammunition.Count(world.Characters.Cold(shooter.Slot)!.Inventory, "bolts"));
        Assert.True(s.Dry);
        Assert.False(s.Ranged);
        Assert.True(s.Thrown > 0, "the shooter fought on in melee");
        Assert.Equal(HandHold.OfCategory((int)WeaponCategory.Katanas).Weapon, world.Characters.Cold(shooter.Slot)!.DrawnWeapon);
    }

    // ------------------------------------------------------------------ determinism

    /// <summary>
    /// Six squads of three: crossbowmen of faction 0 against katana fighters of faction 1, 250 units apart, cut into one-slot partitions.
    /// </summary>
    static SimWorld Skirmish(ulong seed, int threads, out CombatSystem combat)
    {
        var world = NewWorld(seed, threads, out combat, minPartition: 1);
        for (int g = 0; g < 6; g++)
        {
            float x = 600 + g * 900, z = 1000;
            var shooters = new List<CharacterId>();
            var fighters = new List<CharacterId>();
            for (int k = 0; k < 3; k++)
            {
                float skill = 20 + 15 * ((g + k) % 5);
                shooters.Add(Put(world, Shooter(skill, skill, 2 + (g + k) % 3, 0, friendlyFire: skill), new Vector3(x + k * 12, 150, z + k * 8)));
                fighters.Add(Put(world, Dummy(1), new Vector3(x + 250 + k * 10, 150, z + 20 * k)));
            }
            for (int k = 0; k < 3; k++)
            {
                world.Commands.Enqueue(new AttackOrder([shooters[k]], fighters[(k + g) % 3]) { Tick = 0 });
                world.Commands.Enqueue(new AttackOrder([fighters[k]], shooters[(k + 1) % 3]) { Tick = 0 });
            }
        }
        return world;
    }

    internal static List<ulong> Hashes(int threads, ulong seed = 13)
    {
        using var world = Skirmish(seed, threads, out var combat);
        var hashes = new List<ulong>();
        int ran = 0;
        foreach (int target in new[] { 30, 150, 300, 600, 1200 })
        {
            world.RunTicks(target - ran);
            ran = target;
            hashes.Add(world.StateHash());
        }
        return hashes;
    }

    [Fact]
    public void A_skirmish_with_crossbows_is_the_same_at_any_thread_count()
    {
        var one = Hashes(1);
        var four = Hashes(4);
        var sixteen = Hashes(16);
        Assert.Equal(one, four);
        Assert.Equal(one, sixteen);
        Assert.Equal(one, Hashes(1));
        Assert.NotEqual(one, Hashes(1, 14));
        using var world = Skirmish(13, 4, out var combat);
        world.RunTicks(1200);
        int rangedHits = combat.Log.Count(e => e.Ranged);
        int strayHits = combat.Log.Count(e => e.Ranged && !e.Intended);
        int meleeBlows = combat.Log.Count(e => !e.Ranged);
        output.WriteLine($"fired {combat.ProjectilesFired}, ranged hits {rangedHits} (to a friend or another: {strayHits}), melee blows {meleeBlows}");
        Assert.True(combat.ProjectilesFired > 10);
        Assert.Contains(combat.Log, e => e.Ranged);
        Assert.Contains(combat.Log, e => !e.Ranged);
    }
}

/// <summary>
/// The crossbow facts of docs/game/combat.md "Ranged and turrets" against the install's records (base load order),
/// and a real crossbowman shooting. Skipped without the game.
/// </summary>
[Slow]
public class RangedInstallTests(ITestOutputHelper output)
{
    static Data.GameDatabase Db()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        return InstallData.BaseGame!;
    }

    [Fact]
    public void A_real_crossbowman_shoots_a_ranger_bolt_at_a_katana_fighter()
    {
        var db = Db();
        var combatData = CombatDatabase.From(db);
        var constants = GameConstants.FromDatabase(db);
        var race = RaceData.From(db.OfType(FcsRecordType.Race).First(r => r.Name == "Greenlander"), db);
        var combat = new CombatSystem(
            combatData.Techniques, combatData.Constants, constants, BodyOptions.Default, new AnimationLengths(),
            new CombatOptions { RecordLog = true });
        var settings = new WorldSettings { Seed = 3, PublishSnapshots = false };
        using var world = new SimWorld(settings, new OpenGroundWalkability((_, _) => 150), [combat, Senses(combat)]);
        var items = new ItemFactory(db, constants);
        var ranger = combatData.Crossbows.Single(x => x.Name == "Ranger");
        CharacterCold Make(bool shooter, int faction)
        {
            var stats = new CharacterStats();
            var skills = new[]
            {
                StatsEnumerated.MeleeAttack, StatsEnumerated.MeleeDefence, StatsEnumerated.Katanas, StatsEnumerated.Dexterity,
                StatsEnumerated.Strength, StatsEnumerated.Toughness, StatsEnumerated.Crossbows, StatsEnumerated.Perception,
            };
            foreach (var s in skills)
            {
                stats[s] = 60;
            }

            var fighter = new Fighter(WeaponInstance.Create(0.5f, combatData.Weapon("476-gamedata.base")!, null, null, combatData.Constants))
            {
                Crossbow = shooter ? CrossbowInstance.Create(0, ranger, combatData.Constants, 1) : null,
            };
            var inv = new Inventory();
            if (shooter) inv.Add(items.Create(ranger.AmmoId)!);
            return new CharacterCold
            {
                Name = shooter ? "shooter" : "fighter", Faction = faction, Stats = stats, Race = race,
                Medical = MedicalState.Create(race, stats.Strength), Fighter = fighter, Inventory = inv,
            };
        }
        var a = world.Characters.Spawn(new CharacterHot { Position = new(1000, 150, 1000), Health = 100 }, Make(true, 0), 0);
        var b = world.Characters.Spawn(new CharacterHot { Position = new(1200, 150, 1000), Health = 100 }, Make(false, 1), 0);
        world.Commands.Enqueue(new AttackOrder([a], b) { Tick = 0 });
        world.Commands.Enqueue(new AttackOrder([b], a) { Tick = 0 });
        world.RunTicks(1800);
        output.WriteLine($"fired {combat.ProjectilesFired}, bolt hits {combat.Log.Count(e => e.Ranged)}, "
            + $"melee blows {combat.Log.Count(e => !e.Ranged)}, "
            + $"bolts left {Ammunition.Count(world.Characters.Cold(a.Slot)!.Inventory, ranger.AmmoId)}");
        foreach (var e in combat.Log.Where(e => e.Ranged)) output.WriteLine($"  t{e.Tick} part {e.Part} pierce {e.Pierce:0.0} stun {e.Stun:0.0}");

        Assert.True(combat.ProjectilesFired >= 1);

        // Every bolt taken is fired or still loaded (a bolt loaded when the fight turned to melee stays loaded).
        Assert.Equal(30 - combat.ProjectilesFired - combat.StateOf(a).Loaded,
            Ammunition.Count(world.Characters.Cold(a.Slot)!.Inventory, ranger.AmmoId));
    }
}
