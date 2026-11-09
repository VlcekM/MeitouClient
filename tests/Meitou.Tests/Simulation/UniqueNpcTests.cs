using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using static Meitou.Tests.Simulation.SyntheticTown;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Unique characters (docs/simulation.md "Unique characters"): made once, a claimed one replaced or left out, the table saved.
/// </summary>
public class UniqueNpcTests(ITestOutputHelper output)
{
    [Fact]
    public void A_claimed_unique_gets_its_replacement_or_nobody_and_the_squad_keeps_a_leader()
    {
        // Chief (the leader of both Patrols) is unique with a replacement; Hermit (the faction folk's leader) is unique without one and taken before
        // the town is made.
        FcsRecord Person(string id, string name, bool unique)
        {
            var r = Rec(id, FcsRecordType.Character, name);
            r.References["race"] = [Ref("1-t", 100)];
            r.Bools["unique"] = unique;
            return r;
        }
        var chief = Person("7-t", "Chief", true);
        chief.References["unique replacement spawn"] = [Ref("6-t")];
        var db = Database(residentSquads: 2, extra: [chief, Person("6-t", "Stand-in", false), Person("5-t", "Hermit", true)],
            changePatrol: patrol => patrol.References["leader"] = [Ref("7-t", 1)], changeFolk: folk => folk.References["leader"] = [Ref("5-t", 1)]);

        using var world = SyntheticTown.World(5, 1, db: db, settings: new PopulationSettings { CheckEveryTicks = 1 });
        world.Uniques.TryClaim("5-t");
        world.RunTick();

        var table = world.Characters;
        var all = Enumerable.Range(0, table.Previous.Length).Where(i => table.Previous[i].Alive).Select(i => table.Cold(i)!).ToList();
        Assert.Single(all, c => c.RecordId == "7-t");
        Assert.DoesNotContain(all, c => c.RecordId == "5-t");
        // The other Patrol is led by Chief's replacement; the folk squad by its first Guard.
        var standInChar = Assert.Single(all, c => c.RecordId == "6-t");
        Assert.Equal((int)SquadRole.Leader, standInChar.Role);
        var folk = Assert.Single(world.Squads.All, s => s.TemplateId == "12-t");
        var folkLeader = table.Cold(folk.Leader.Slot)!;
        Assert.Equal("2-t", folkLeader.RecordId);
        Assert.Equal((int)SquadRole.Leader, folkLeader.Role);
        // Chief is claimed and bound to the character in the world.
        var entry = world.Uniques.Find("7-t")!;
        Assert.True(entry.Claimed);
        Assert.Equal("7-t", world.Uniques.RecordOf(entry.Handle));
    }

    [Fact, Slow]
    public void Real_towns_make_each_unique_once_and_a_meitou_save_keeps_the_table()
    {
        var install = InstallData.Install;
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var db = InstallData.FullLoadOrder!;
        var levels = InstallData.Levels!;
        var data = PopulationData.Create(db, levels.Towns(), new GeneratedAppearances(db, install!.Root));
        var walk = new OpenGroundWalkability((_, _) => 300);
        SimWorld NewWorld() => new(new WorldSettings { Seed = 5, Threads = 2, PublishSnapshots = false }, walk,
            StandardSystems.Build(data, walk,
                    new StandardSystemOptions { Parts = StandardParts.Population | StandardParts.Player,
                            Population = new PopulationSettings { CheckEveryTicks = 1 } }).Systems);
        using var world = NewWorld();
        var population = world.Systems.OfType<PopulationSystem>().Single();

        // Towns whose residents or bar squads have a unique leader; the first such leader with a replacement is taken beforehand.
        var targets = new List<(TownSite Site, string Unique)>();
        foreach (var s in data.Sites)
        {
            int fi = s.Town.Faction is { } f ? data.FactionIndex(f) : -1;
            var faction = fi >= 0 ? data.Factions[fi] : null;
            foreach (var e in Data.World.ResidentHomes.Entries(s.Town, faction).Concat(PopulationSystem.BarEntries(s.Town, faction)))
                if (db.Find(e.Id)?.GetReferences("leader") is [{ TargetStringId: var leader }, ..] && UniqueCharacters.IsUnique(db.Find(leader)))
                    targets.Add((s, leader));
        }
        var taken = targets.First(t => db.Find(t.Unique)!.GetReferences("unique replacement spawn").Count > 0);
        world.Uniques.TryClaim(taken.Unique);
        var visit = targets.Select(t => t.Site).Distinct().Take(12).Prepend(taken.Site).Distinct().ToList();
        foreach (var s in visit)
        {
            world.Commands.Enqueue(new FocusCommand(s.Position) { Tick = world.Tick });
            Assert.True(TestWaits.TickUntil(world, () => population.StatusOf(s.Index) == SiteStatus.Loaded), $"{s.Town.Name} was made");
        }

        // Every unique person in the world is there once (the gate skips animals, as the original; Protector Of The Flame names itself as its
        // replacement).
        var live = new Dictionary<string, int>(StringComparer.Ordinal);
        var table = world.Characters;
        for (int i = 0; i < table.Previous.Length; i++)
            if (table.Previous[i].Alive && table.Cold(i) is { } c && db.Find(c.RecordId) is { Type: FcsRecordType.Character } r
                    && UniqueCharacters.IsUnique(r))
                live[c.RecordId] = live.GetValueOrDefault(c.RecordId) + 1;
        output.WriteLine($"{visit.Count} towns, {live.Count} uniques in the world, {world.Uniques.Count} entries");
        Assert.True(live.Count > 5);
        Assert.All(live, k => Assert.True(k.Value == 1 || db.Find(k.Key)!.Name == "Protector Of The Flame", $"{db.Find(k.Key)!.Name} x{k.Value}"));
        Assert.False(live.ContainsKey(taken.Unique));

        // A Meitou save writes the claimed entries and a new world reads them back.
        string root = Path.Combine(Path.GetTempPath(), $"meitou-uniques-{Environment.ProcessId}");
        try
        {
            var save = SaveCapture.Capture(world, data, new SaveClock(1, 13, 0), null,
                    new SaveCaptureOptions { Placements = levels.Towns().ToList() });
            string folder = QuickSave.Folder(root);
            save.Write(folder);
            var back = SaveGame.Load(folder);
            int claimed = world.Uniques.All.Count(e => e.Value.Claimed);
            Assert.Equal(claimed, back.Camera.Record.Ints["num usedUniques"]);
            using var again = NewWorld();
            SaveLoader.Load(again, back, data, new SaveLoadOptions { ApplyRelations = false, Appearances = data.Appearances });
            Assert.Equal(world.Uniques.All.Where(e => e.Value.Claimed).Select(e => (e.Key, e.Value.State)),
                again.Uniques.All.Select(e => (e.Key, e.Value.State)));
            foreach (var s in again.Systems) (s as IDisposable)?.Dispose();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            foreach (var s in world.Systems) (s as IDisposable)?.Dispose();
        }
    }
}
