using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Animals (docs/game/animals.md) built from the installed game's ANIMAL_CHARACTER records: their bodies, natural weapons, techniques, fights and
/// aggro.
/// </summary>
[Slow]
public class AnimalTests(ITestOutputHelper output)
{
    const string Bonedog = "3979-gamedata.base", BeakThing = "2859-gamedata.base", Megaraptor = "50983-rebirth.mod";

    // With the animation system: animals play their own clip sets (no clip lengths here, so the 1 s default).
    const StandardParts Fighting = StandardParts.Movement | StandardParts.Pursuit | StandardParts.Body | StandardParts.Combat
        | StandardParts.Retaliation | StandardParts.Aggro
        | StandardParts.Animation;

    static GameDatabase Db()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        return InstallData.BaseGame!;
    }

    static PopulationData Data(GameDatabase db) => PopulationData.Create(db, []);

    static GameRecord Race(GameDatabase db, GameRecord animal) =>
        db.Find(animal.GetReferences("race").First(r => r.Values.Value0 > 0).TargetStringId)!;

    [Fact]
    public void Animal_bodies_and_stats_come_from_their_records()
    {
        var db = Db();
        var factory = new BodyFactory(db);
        var beak = db.Find(BeakThing)!;

        Assert.Equal(FcsRecordType.AnimalCharacter, beak.Type);

        var animal = AnimalData.From(beak)!;

        Assert.Equal((60, 1f, 0.3f, 1f), (animal.AnimalStrength, animal.HpMult, animal.ScaleMin, animal.ScaleMax));

        var (race, stats, medical) = factory.Create(Race(db, beak), beak, 1, 2);

        Assert.Equal("Beak Thing", race.Name);

        // combat stats 35 fan out (stats randomise 2 moves each by at most 2): a tenth to martial arts, the whole to dexterity, strength, toughness.
        Assert.InRange(stats[StatsEnumerated.MartialArts], 1.5f, 5.5f);
        Assert.InRange(stats[StatsEnumerated.Dexterity], 33, 37);
        Assert.InRange(stats[StatsEnumerated.Strength], 33, 37);
        Assert.Equal(1f, stats.Scale);

        // No humanoid stats object: the blood capacity is the race's full max blood x HP mult x size (adult: scale max 1).
        Assert.False(medical.HasStatsObject);
        Assert.Equal(race.MaxBlood, medical.Blood, 2);
        Assert.Equal(400f, medical.Blood, 2);

        float Blood(float k, float z) => race.MinBlood + (race.MaxBlood - race.MinBlood) * k * z;

        // Forelegs take the arm slots; the parts are the race's.
        Assert.Equal(race.Anatomy.Count, medical.Parts.Count);
        Assert.Contains(medical.Parts, p => p.Template.Name.Contains("Foreleg"));

        // A young beak thing: smaller (parts' HP multiplier and the blood both scale with lerp(age, 0.3, 1)) and its stats read at half.
        var (_, youngStats, young) = factory.Create(Race(db, beak), beak, 1, 2, age: 0.5f);

        Assert.Equal(0.5f, youngStats.Scale);
        Assert.Equal(0.65f, young.ScaleK, 4);
        Assert.Equal(Blood(0.65f, 1), young.Blood, 2);
        Assert.Equal(medical.Parts[0].MaxHp * 0.65f, young.Parts[0].MaxHp, 3);

        // HP mult is the body scale: a megaraptor (HP mult 3) has three times the part HP and blood of a swamp raptor of the same age.
        var mega = db.Find(Megaraptor)!;
        var (_, _, megaMedical) = factory.Create(Race(db, mega), mega, 1, 2);

        Assert.Equal(3f, megaMedical.ScaleZ);

        var megaRace = factory.Race(Race(db, mega));

        Assert.Equal(megaRace.MinBlood + (megaRace.MaxBlood - megaRace.MinBlood) * 3 * AnimalData.From(mega)!.Scale(1), megaMedical.Blood, 1);

        output.WriteLine($"beak thing: blood {medical.Blood}, parts {string.Join(", ", medical.Parts.Select(p => $"{p.Template.Name} {p.MaxHp:0}"))}");

        // A new bonedog carries its death items, flagged as such.
        var items = new Meitou.Simulation.Items.ItemFactory(db, GameConstants.FromDatabase(db));
        var death = items.Build(db.Find(Bonedog), null).Items.Select(i => (i.Name, i.Quantity, i.Death)).Order().ToArray();

        Assert.Equal([("Animal Skin", 1, true), ("Animal Teeth", 4, true), ("Raw Meat", 3, true)], death);
    }

    static (SimWorld World, StandardSystemSet Systems, PopulationSystem Population) Make(PopulationData data)
    {
        var walk = new OpenGroundWalkability((_, _) => 300);
        var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = Fighting, SynchronousPaths = true,
            Combat = new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = true },
        });
        var world = new SimWorld(new WorldSettings { Seed = 1, Threads = 1, MinPartitionSize = 1, PublishSnapshots = false }, walk, systems.Systems);
        return (world, systems, new PopulationSystem(data, new PopulationSettings()));
    }

    /// <summary>
    /// A CHARACTER of the base game with a race of its own (so it has a body without a generated look): the human opponent.
    /// </summary>
    static string Human(GameDatabase db) => db.OfType(FcsRecordType.Character)
        .First(r => r.GetReferences("race").Any(x => x.Values.Value0 > 0 && db.Find(x.TargetStringId)?.Name == "Greenlander")
                && r.GetReferences("stats").Count > 0).StringId;

    /// <summary>
    /// A bonedog's fight with a person goes through the hit pipeline (bites with pierce, the person's hits on its body, no block or dodge). The
    /// attack is ordered: its attack on sight is a goal of its squad's AI package (16), and this world runs no AI.
    /// </summary>
    [Fact]
    public void A_hostile_bonedogs_attack_on_a_person_goes_through_the_hit_pipeline()
    {
        var db = Db();
        var data = Data(db);
        var (world, systems, population) = Make(data);
        using var _ = world;
        var me = population.SpawnSandbox(world, Human(db), new Vector2(5000, 5000), player: true);

        // A predator of a faction hostile to the player's, 150 units off and facing it.
        var dog = population.SpawnSandbox(world, Bonedog, new Vector2(5150, 5000), player: false, yaw: -MathF.PI / 2);
        var table = world.Characters;

        // A middling brawler, so both sides land blows: with its data stats the dog knocks it out before its first hit, from MA ~35 its hits cancel
        // every bite.
        var stats = table.Cold(me.Slot)!.Stats!;
        stats[StatsEnumerated.MartialArts] = 20;
        stats[StatsEnumerated.Toughness] = 50;
        int mine = table.Cold(me.Slot)!.Faction, theirs = table.Cold(dog.Slot)!.Faction;

        Assert.True(AggroSystem.IsEnemy(data.Relations, theirs, mine));

        world.Commands.Enqueue(new AttackOrder([dog], me) { Tick = world.Tick });
        world.RunTicks(60);

        Assert.True(table.Cold(dog.Slot)!.InCombat, "the dog attacks the player");

        world.RunTicks(1800);
        var log = systems.Combat!.Log;
        var bites = log.Where(e => e.Attacker == dog.Slot && e.Outcome == BlowOutcome.Hit).ToList();
        var back = log.Where(e => e.Attacker == me.Slot && e.Outcome == BlowOutcome.Hit).ToList();
        output.WriteLine($"dog blows {log.Count(e => e.Attacker == dog.Slot)}, hits {bites.Count}, first {bites.FirstOrDefault()}; "
            + $"the player's hits {back.Count}");
        var pc = table.Cold(me.Slot)!;
        output.WriteLine($"player: {pc.Name} unconscious {pc.Medical!.Unconscious} dead {pc.Medical.Dead} blood {pc.Medical.Blood} "
            + $"fighter {pc.Fighter?.Weapon?.Data.Name ?? "fists"} combat {pc.InCombat}; dog blood {table.Cold(dog.Slot)?.Medical?.Blood}");

        Assert.NotEmpty(bites);
        Assert.All(bites, b => Assert.True(b.Cut + b.Blunt + b.Pierce > 0));
        Assert.Contains(bites, b => b.Pierce > 0);             // the dog's weapon has pierce 0.5
        Assert.NotEmpty(back);                                  // the idle player answers and hurts the dog's body
        Assert.Contains(table.Cold(dog.Slot)?.Medical?.Parts ?? [], p => p.Flesh < p.MaxHp);

        // The animal never blocks or dodges.
        Assert.DoesNotContain(log, e => e.Defender == dog.Slot && e.Outcome is BlowOutcome.Blocked or BlowOutcome.Dodged);
    }
}
