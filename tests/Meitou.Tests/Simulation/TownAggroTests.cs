using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Faction hostility over the installed game's data: towns keep the peace among friends, hostile characters in sight fight.
/// </summary>
[Slow]
public class TownAggroTests(ITestOutputHelper output)
{
    const StandardParts Fighting = StandardParts.Population | StandardParts.Movement | StandardParts.Pursuit | StandardParts.Body | StandardParts.Combat
        | StandardParts.Retaliation | StandardParts.Aggro;

    static PopulationData Data()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        // Its own data per test: the relation table is live and shared by the worlds built on one PopulationData.
        // With the looks: a CHARACTER without a race reference gets its race from the look, and without a race there is no body and no fighter.
        var db = InstallData.BaseGame!;
        return PopulationData.Create(db, InstallData.Levels!.Towns(), new GeneratedAppearances(db, InstallData.Install!.Root));
    }

    static (SimWorld World, StandardSystemSet Systems) Make(PopulationData data, Vector3 at, StandardParts parts = Fighting, float multiplier = 1,
            int threads = 1)
    {
        var walk = new OpenGroundWalkability((_, _) => 300);
        var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = parts, SynchronousPaths = true,
            Population = new PopulationSettings { CheckEveryTicks = 1, SquadSizeMultiplier = multiplier },
            Combat = new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = true },
            Aggro = new AggroSettings { RecordLog = true },
        });
        var world = new SimWorld(new WorldSettings { Seed = 1, Threads = threads, PublishSnapshots = false }, walk, systems.Systems);
        world.Commands.Enqueue(new FocusCommand(at) { Tick = 0 });
        return (world, systems);
    }

    static readonly string[] Towns = ["The Hub", "Squin", "Stack", "Admag", "Mongrel", "Shark", "Sho-Battai", "Waystation"];

    [Fact]
    public void Towns_keep_the_peace_between_friends_and_every_fight_is_between_enemies()
    {
        var data = Data();
        var relations = data.Relations;
        int checkedTowns = 0, totalFighters = 0;
        foreach (string name in Towns)
        {
            if (data.Sites.FirstOrDefault(s => s.Town.Name == name) is not { } site) continue;
            checkedTowns++;
            var (world, systems) = Make(data, site.Position);
            using var _ = world;
            world.RunTicks(1800);
            var table = world.Characters;
            int alive = 0, fighters = 0;
            for (int s = 0; s < table.HighWater; s++) if (table.Previous[s].Alive) { alive++; if (table.Cold(s)!.Fighter is not null) fighters++; }
            totalFighters += fighters;
            // Every fight the aggro started is between a faction and one it is hostile to, neither coexisting with the other.
            foreach (var e in systems.Aggro!.Log)
                Assert.True(AggroSystem.IsEnemy(relations, e.AttackerFaction, e.TargetFaction),
                        $"{name}: {data.Factions[e.AttackerFaction].Name} attacked {data.Factions[e.TargetFaction].Name}");
            // Every blow is between enemies one way or the other (the other side answers: retaliation), never within a faction or between allies.
            var factionsAtBlows = new HashSet<(int, int)>();
            foreach (var b in systems.Combat!.Log)
            {
                int fa = table.Cold(b.Attacker)?.Faction ?? -1, fd = table.Cold(b.Defender)?.Faction ?? -1;
                if (fa < 0 || fd < 0) continue;   // the slot was reused after a corpse was removed
                Assert.NotEqual(fa, fd);
                Assert.True(AggroSystem.IsEnemy(relations, fa, fd) || AggroSystem.IsEnemy(relations, fd, fa),
                        $"{name}: a blow between {data.Factions[fa].Name} and {data.Factions[fd].Name}");
                Assert.False(relations.IsAlly(fa, fd) || relations.IsAlly(fd, fa));
                factionsAtBlows.Add((fa, fd));
            }
            var factionsHere = world.Squads.All.Select(s => s.Faction).Where(f => f >= 0).Distinct().Select(f => data.Factions[f].Name);
            output.WriteLine($"{name}: {alive} characters ({fighters} fighters) of {string.Join(", ", factionsHere)}; "
                + $"{systems.Aggro.Started} fights started, {systems.Combat.Log.Count} blows"
                + (factionsAtBlows.Count > 0
                    ? $" between {string.Join("; ", factionsAtBlows.Select(p => $"{data.Factions[p.Item1].Name} -> {data.Factions[p.Item2].Name}"))}"
                    : ""));
        }
        Assert.True(checkedTowns >= 3, "the towns are placed in the world");
        Assert.True(totalFighters > 100, "the residents can fight");
        Assert.Equal(0, relations.Changes);   // no player: nobody's deeds changed the table
    }
}
