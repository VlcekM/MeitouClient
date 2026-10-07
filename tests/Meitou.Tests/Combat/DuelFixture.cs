using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Combat;

/// <summary>A fighter to put in a headless fight: stats, weapon and armour.</summary>
sealed record Combatant
{
    public string Name { get; init; } = "fighter";
    public float Attack { get; init; } = 50;
    public float Defence { get; init; } = 50;
    public float WeaponSkill { get; init; } = 50;
    public float Dexterity { get; init; } = 50;
    public float Strength { get; init; } = 50;
    public float Toughness { get; init; } = 50;
    public float Dodge { get; init; } = 50;
    public float Martial { get; init; } = 50;
    public WeaponData? Weapon { get; init; } = Katana;
    public float Quality { get; init; } = 0.5f;
    public ArmourData[] Armour { get; init; } = [];
    public float ArmourQuality { get; init; } = 0.5f;

    public CharacterStats MakeStats()
    {
        var s = new CharacterStats();
        s[StatsEnumerated.MeleeAttack] = Attack;
        s[StatsEnumerated.MeleeDefence] = Defence;
        s[StatsEnumerated.Dexterity] = Dexterity;
        s[StatsEnumerated.Strength] = Strength;
        s[StatsEnumerated.Toughness] = Toughness;
        s[StatsEnumerated.Dodge] = Dodge;
        s[StatsEnumerated.MartialArts] = Martial;
        foreach (var skill in new[] { StatsEnumerated.Katanas, StatsEnumerated.Sabres, StatsEnumerated.Blunt, StatsEnumerated.HeavyWeapons, StatsEnumerated.Hackers, StatsEnumerated.Polearms })
            s[skill] = WeaponSkill;
        return s;
    }

    public Fighter MakeFighter() => new(Weapon is null ? null : WeaponInstance.Create(Quality, Weapon, null, null, Constants),
        Armour.Select(a => ArmourPiece.Create(a, ArmourQuality)));
}

/// <summary>Builds worlds with the combat system alone: flat ground, a Greenlander body, base-game constants and the synthetic katana techniques.</summary>
static class DuelFixture
{
    public static AnimationLengths Lengths { get; } = new AnimationLengths()
        .Add("cut left", 0.9f).Add("combo", 1.3f).Add("block up", 0.7f).Add("block left", 0.7f).Add("block right", 0.7f).Add("block thrust", 0.7f).Add("dodge back", 0.8f);

    /// <summary><paramref name="minPartition"/> is the fewest slots in a partition: 1 puts even two fighters in different partitions, so more than one thread really runs them side by side.</summary>
    public static SimWorld NewWorld(ulong seed, int threads, out CombatSystem combat, CombatOptions? options = null, IEnumerable<CombatTechnique>? techniques = null, int minPartition = 1)
    {
        combat = new CombatSystem(techniques ?? KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, Lengths, options ?? new CombatOptions { RecordLog = true });
        return new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = minPartition }, new OpenGroundWalkability((_, _) => 150), [combat]);
    }

    public static CharacterId Spawn(SimWorld world, Combatant c, Vector3 position)
    {
        var stats = c.MakeStats();
        var cold = new CharacterCold
        {
            Name = c.Name, Stats = stats, Race = Human, Medical = MedicalState.Create(Human, stats.Strength), Fighter = c.MakeFighter(),
        };
        return world.Characters.Spawn(new CharacterHot { Position = position, Health = 100 }, cold, 0);
    }

    /// <summary>Two fighters facing each other <paramref name="distance"/> units apart on the X axis, ordered to fight at tick 0.</summary>
    public static (CharacterId A, CharacterId B) Pair(SimWorld world, Combatant a, Combatant b, float distance = 14, float x = 1000, float z = 1000)
    {
        var ia = Spawn(world, a, new Vector3(x, 150, z));
        var ib = Spawn(world, b, new Vector3(x + distance, 150, z));
        world.Commands.Enqueue(new AttackOrder([ia], ib) { Tick = 0 });
        world.Commands.Enqueue(new AttackOrder([ib], ia) { Tick = 0 });
        return (ia, ib);
    }

    public static bool Down(SimWorld world, CharacterId id) => world.Characters.Cold(id.Slot)!.Medical!.Incapacitated;

    /// <summary>Runs until one of the two is out or the limit; returns the tick count.</summary>
    public static int RunToEnd(SimWorld world, CharacterId a, CharacterId b, int limit = 6000)
    {
        int ticks = 0;
        while (ticks < limit && !Down(world, a) && !Down(world, b))
        {
            world.RunTick();
            ticks++;
        }
        return ticks;
    }
}
