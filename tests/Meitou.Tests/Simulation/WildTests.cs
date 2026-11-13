using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Population;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The wild population: homeless squads of the area sectors (6.2) and nests (6.3).
/// </summary>
public class WildTests(ITestOutputHelper output)
{
    // ---- a synthetic wilderness ----

    const uint New = 0x10;
    static FcsRecord Rec(string id, FcsRecordType type, string name) => new() { StringId = id, Flags = New, Name = name, RecordType = type };

    /// <summary>
    /// The synthetic town's data plus a biome group with homeless spawns and a dog nest; the group covers the four zones round the world's origin.
    /// <paramref name="changePatrol"/> changes the <c>Patrol</c> template (<c>10-t</c>, also a homeless spawn), <paramref name="changeDen"/> the
    /// nest's TOWN and <paramref name="extra"/> adds records.
    /// </summary>
    static PopulationData WildData(Action<FcsRecord>? changePatrol = null, Action<FcsRecord>? changeDen = null, params FcsRecord[] extra)
    {
        var db = SyntheticTown.Database(residentSquads: 1, changePatrol: changePatrol);
        var dogs = Rec("52-t", FcsRecordType.SquadTemplate, "Dog pack");
        dogs.References["animals"] = [new FcsReference("4-t", 3, 0, 50)];
        dogs.References["faction"] = [new FcsReference("53-t", 0, 0, 0)];
        var beasts = Rec("53-t", FcsRecordType.Faction, "Beasts");
        beasts.Ints["default relation"] = -50;
        var nest = Rec("51-t", FcsRecordType.Town, "Dog Den");
        nest.Ints["type"] = 0;
        nest.Ints["nest resident population"] = 10;
        nest.Floats["size radius"] = 200;
        nest.Floats["town radius mult"] = 1;
        nest.References["faction"] = [new FcsReference("53-t", 0, 0, 0)];
        nest.References["roaming squads"] = [new FcsReference("52-t", 100, 0, 0)];
        changeDen?.Invoke(nest);
        var group = Rec("50-t", FcsRecordType.BiomeGroup, "Wilds");
        group.Ints["index"] = 0x123456;
        group.Ints["num nests"] = 2;
        group.Floats["homeless spawn amount"] = 1.5f;
        group.References["homeless spawns"] = [new FcsReference("10-t", 30, 0, 0), new FcsReference("52-t", 10, 0, 0),
                new FcsReference("11-t", 50, 0, 0)];
        group.References["nests"] = [new FcsReference("51-t", 0, 0, 0)];
        var file = new FcsFile();
        file.Records.AddRange([dogs, beasts, nest, group, .. extra]);
        db.Apply(file, "wild.mod");

        var groups = BiomeGroupData.LoadAll(db);
        var cells = new int[WeatherAreas.Cells * WeatherAreas.Cells];
        Array.Fill(cells, -1);
        for (int z = 31; z <= 32; z++)
            for (int x = 31; x <= 32; x++) cells[z * WeatherAreas.Cells + x] = 0;
        return PopulationData.Create(db, [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)], biomes: new BiomeMap(groups, cells));
    }

    static readonly Vector3 Origin = new(0, 300, 0);   // the corner of four zones: all four are active

    /// <summary>
    /// Whether the sector's own town (the town or nest nearest its centre, when it stands in the sector) is one of <paramref name="nests"/>.
    /// </summary>
    static bool OwnTownIsNest(PopulationData data, IReadOnlyList<WildSite> nests, WildSite sector)
    {
        var (minX, minZ, maxX, maxZ) = WeatherAreas.CellRect(sector.Zone.X, sector.Zone.Y);
        var centre = new Vector2((minX + maxX) / 2, (minZ + maxZ) / 2);
        // The towns PopulationSystem.NearestTown counts: not nest markers, points of interest or Null.
        float town = data.Sites.Where(s => s.Town.Type is not (TownType.NestMarker or TownType.PointOfInterest or TownType.Null))
            .Select(s => Vector2.Distance(centre, new Vector2(s.Position.X, s.Position.Z)))
            .DefaultIfEmpty(float.MaxValue)
            .Min();
        var nearest = nests.MinBy(n => Vector2.Distance(centre, n.Spots[0]))!;
        return nearest.Zone == sector.Zone && Vector2.Distance(centre, nearest.Spots[0]) < town;
    }

    static (SimWorld World, PopulationSystem Population) WildWorld(PopulationData data, int threads,
            StandardParts parts = StandardParts.Population | StandardParts.Movement, ulong seed = 3)
    {
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = parts, SynchronousPaths = true,
            Population = new PopulationSettings { CheckEveryTicks = 15, UnloadGraceSeconds = 2 },
        });
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = 4 }, walk,
                systems.Systems);
        world.Commands.Enqueue(new FocusCommand(Origin) { Tick = 0 });
        return (world, systems.Population!);
    }

    static List<string> WildSquads(SimWorld world, PopulationSystem pop)
    {
        var list = new List<string>();
        foreach (var site in pop.WildSites)
            foreach (int id in pop.WildSquadsOf(site.Index))
            {
                var squad = world.Squads.Find(id)!;
                var members = string.Join(",", squad.Members.Select(m => world.Characters.Cold(m.Slot)!.RecordId));
                list.Add($"{site.Kind} {site.Index} {squad.TemplateId} {squad.Position} [{members}]");
            }
        return list;
    }

    [Fact]
    public void A_wilderness_gets_its_homeless_squads_and_nests()
    {
        var data = WildData();
        var (world, pop) = WildWorld(data, 1);
        using var _ = world;
        world.RunTicks(16);
        var sectors = pop.WildSites.Where(s => s.Kind == WildKind.Sector).ToList();
        var nests = pop.WildSites.Where(s => s.Kind == WildKind.Nest).ToList();
        Assert.Equal(4, sectors.Count);
        Assert.Equal(2, nests.Count);
        Assert.All(nests, n => Assert.Equal("Dog Den", n.Nest!.Name));
        // A den stands in a sector of its own (FUN_1408f62e0) for at least one of them: that sector makes none of the group's squads.
        Assert.Contains(sectors, s => OwnTownIsNest(data, nests, s));
        foreach (var s in sectors)
        {
            Assert.Equal(SiteStatus.Loaded, pop.WildStatusOf(s.Index));
            // ceil(1.5) = 2 squads; the gated template never.
            Assert.Equal(OwnTownIsNest(data, nests, s) ? 0 : 2, pop.WildSquadsOf(s.Index).Count);
            Assert.All(pop.WildSquadsOf(s.Index), id => Assert.NotEqual("11-t", world.Squads.Find(id)!.TemplateId));
        }
        foreach (var n in nests)
        {
            // floor(0.7 x 10) = 7 members; a pack is expected to be 3: two packs, then 1 free is too little.
            var squads = pop.WildSquadsOf(n.Index);
            Assert.Equal(2, squads.Count);
            foreach (int id in squads)
            {
                var squad = world.Squads.Find(id)!;
                Assert.Equal("52-t", squad.TemplateId);
                Assert.Equal(3, squad.Members.Count);
                Assert.Equal(data.FactionIndex("53-t"), squad.Faction);
                Assert.True(Vector2.Distance(squad.HomeCentre, new Vector2(squad.Position.X, squad.Position.Z)) <= 200 * 0.6f + 1);
            }
        }
        foreach (var line in WildSquads(world, pop)) output.WriteLine(line);
    }

    [Fact]
    public void A_nest_without_roaming_squads_leaves_its_sector_the_groups_homeless_squads()
    {
        // The original's maker skips the group's list only for a nest whose pool-2 list is not empty (docs/game/factions-squads-towns.md "Homeless
        // squads").
        var data = WildData(changeDen: d => d.References.Remove("roaming squads"));
        var (world, pop) = WildWorld(data, 1);
        using var _ = world;
        world.RunTicks(16);
        var sectors = pop.WildSites.Where(s => s.Kind == WildKind.Sector).ToList();
        var nests = pop.WildSites.Where(s => s.Kind == WildKind.Nest).ToList();
        Assert.Contains(sectors, s => OwnTownIsNest(data, nests, s));
        // The maker weighs a template by its share of the world's living platoons, town residents of the same template included (FUN_1406b9d10):
        // later sectors may find it used up.
        Assert.All(sectors, s => Assert.InRange(pop.WildSquadsOf(s.Index).Count, 0, 2));
        Assert.All(sectors.Where(s => OwnTownIsNest(data, nests, s)), s => Assert.NotEmpty(pop.WildSquadsOf(s.Index)));
        Assert.All(nests, n => Assert.Empty(pop.WildSquadsOf(n.Index)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Wild_squads_stay_as_themselves_while_their_area_is_unloaded_only_with_the_meitou_switch(bool keep)
    {
        var data = WildData();
        var (world, pop) = WildWorld(data, 1);
        using var _ = world;
        pop.KeepWild = keep;
        world.RunTicks(16);
        var before = WildSquads(world, pop);
        Assert.NotEmpty(before);
        var marked = world.Squads.Find(pop.WildSquadsOf(pop.WildSites.First(s => pop.WildSquadsOf(s.Index).Count > 0).Index)[0])!.Members[0];
        world.Characters.Cold(marked.Slot)!.Name = "Marked";
        int squads = pop.WildSites.Sum(s => pop.WildSquadsOf(s.Index).Count);

        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = world.Tick });
        world.RunTicks(200);   // the block's 4 s, then the 2 s grace
        Assert.Equal(0, world.Characters.Count);
        Assert.Equal(keep ? squads : 0, pop.WildSites.Sum(s => pop.KeptWildOf(s.Index).Count));

        // Back: Meitou makes the kept squads again (and no more), Faithful rolls them anew from the seed.
        world.Commands.Enqueue(new FocusCommand(Origin) { Tick = world.Tick });
        world.RunTicks(32);
        Assert.Equal(squads, pop.WildSites.Sum(s => pop.WildSquadsOf(s.Index).Count));
        Assert.Equal(keep, world.Squads.All.SelectMany(s => s.Members).Any(m => world.Characters.Cold(m.Slot)!.Name == "Marked"));
        Assert.All(pop.WildSites, s => Assert.Empty(pop.KeptWildOf(s.Index)));
        // Rolled anew, they come out the same: squad ids and slots are new; site, template, place and members are the same.
        if (!keep) Assert.Equal(before, WildSquads(world, pop));
    }

    [Fact]
    public void An_occupier_is_kept_while_its_sector_is_unloaded_and_occupies_again_in_faithful_mode()
    {
        // The original makes an occupier's platoon persistent (docs/game/ai.md "Package classes"): Faithful drops the other wild squads as their area
        // unloads, not it.
        var roam = Rec("60-t", FcsRecordType.AiPackage, "Roam");
        var occupy = Rec("61-t", FcsRecordType.AiPackage, "Occupy");
        occupy.Ints["signal func"] = 30;
        var data = WildData(p => p.References["AI packages"] = [new FcsReference("60-t", 0, 0, 0), new FcsReference("61-t", 1, 0, 0)],
                extra: [roam, occupy]);
        var (world, pop) = WildWorld(data, 1, StandardParts.Population | StandardParts.Movement | StandardParts.Ai);
        using var _ = world;
        pop.KeepWild = false;
        world.RunTicks(32);
        var squad = pop.WildSites
            .Where(s => s.Kind == WildKind.Sector)
            .SelectMany(s => pop.WildSquadsOf(s.Index))
            .Select(id => world.Squads.Find(id)!)
            .First(s => s.TemplateId == "10-t");
        // It took an outpost round where it stands (an occupation only follows an assault; its state is set as the start would).
        var town = world.Buildings.AddTown(squad.Position, data.PlayerFaction);
        var board = squad.Ai!;
        Assert.True(board.Resume(board.Packages.First(p => p.Package.Id == "61-t").Package));
        board.Destination = CampaignTown.OfPlayer(town.Number);
        world.Characters.Cold(squad.Members[0].Slot)!.Name = "Marked";
        world.RunTicks(30);
        Assert.Equal(PackageClass.OccupyTown, board.CurrentClass);

        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = world.Tick });
        world.RunTicks(200);   // the block's 4 s, then the 2 s grace
        Assert.Equal(0, world.Characters.Count);
        Assert.Equal(1, pop.WildSites.Sum(s => pop.KeptWildOf(s.Index).Count));

        world.Commands.Enqueue(new FocusCommand(Origin) { Tick = world.Tick });
        world.RunTicks(32);
        var back = world.Squads.All.Single(s => s.Members.Any(m => world.Characters.Cold(m.Slot)!.Name == "Marked"));
        Assert.Equal(PackageClass.OccupyTown, back.Ai!.CurrentClass);
        Assert.Equal(CampaignTown.OfPlayer(town.Number), back.Ai.Destination);
    }

    /// <summary>
    /// The wild round the origin for 600 ticks; pinned in <see cref="GoldenHashTests"/>.
    /// </summary>
    internal static List<ulong> Scripted(int threads)
    {
        var (world, _) = WildWorld(WildData(), threads);
        using var __ = world;
        var hashes = new List<ulong>();
        for (int c = 0; c < 4; c++)
        {
            world.RunTicks(150);
            hashes.Add(world.StateHash());
        }
        return hashes;
    }

    // ---- the installed game ----

    static PopulationData RealData()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        var biomes = BiomeMap.Create(db, WeatherAreas.Load(InstallData.Install!));
        return PopulationData.Create(db, InstallData.Levels!.Towns(), biomes: biomes);
    }

    static (SimWorld World, StandardSystemSet Systems) RealWorld(PopulationData data, Vector3 at, StandardParts parts, int threads = 1)
    {
        var walk = new OpenGroundWalkability((_, _) => 300);
        var systems = StandardSystems.Build(data, walk, new StandardSystemOptions
        {
            Parts = parts, SynchronousPaths = true,
            Population = new PopulationSettings { CheckEveryTicks = 15 },
            Combat = new CombatOptions { SelfApproach = false, TickMedical = false },
        });
        var world = new SimWorld(new WorldSettings { Seed = 1, Threads = threads, PublishSnapshots = false }, walk, systems.Systems);
        world.Commands.Enqueue(new FocusCommand(at) { Tick = 0 });
        return (world, systems);
    }

    [Fact]
    [Slow]
    public void A_wolf_den_spawns_its_wolves()
    {
        var data = RealData();
        var sites = PopulationSystem.BuildWildSites(data, new PopulationSettings(), data.Biomes!, 1);
        var den = sites.First(s => s.Kind == WildKind.Nest && s.Nest!.Name == "Wolf Den");
        var at = new Vector3(den.Spots[0].X, 300, den.Spots[0].Y);
        var (world, systems) = RealWorld(data, at,
                StandardParts.Population | StandardParts.Movement | StandardParts.Body | StandardParts.Combat | StandardParts.Animation);
        using var _ = world;
        world.RunTicks(16);
        var pop = systems.Population!;
        var squads = pop.WildSquadsOf(den.Index);
        Assert.NotEmpty(squads);
        int members = 0;
        foreach (int id in squads)
        {
            var squad = world.Squads.Find(id)!;
            Assert.Contains(den.Nest!.RoamingSquads, e => e.Id == squad.TemplateId);
            foreach (var m in squad.Members)
            {
                var cold = world.Characters.Cold(m.Slot)!;
                Assert.Equal(FcsRecordType.AnimalCharacter, data.Db.Find(cold.RecordId)!.Type);
                Assert.NotNull(cold.Fighter);
                members++;
            }
            output.WriteLine($"{squad.Name}: {squad.Members.Count}");
        }
        // floor(0.7 x 10) = 7, and a squad is skipped when its expected size passes the free room (unless the pool is empty and more than 4 are
        // free).
        Assert.InRange(members, 1, (int)(0.7f * den.Nest!.NestResidentPopulation) + 4);
        world.RunTicks(300);
    }
}
