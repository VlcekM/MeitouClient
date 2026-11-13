using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Economy;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// A town's artifacts given out when it makes its residents, kept by its shop and saved (docs/game/economy.md "Artifacts"), on the synthetic town.
/// </summary>
public class TownArtifactTests
{
    static readonly Vector3 VaultAt = SyntheticTown.Centre + new Vector3(0, 0, 100), HutAt = SyntheticTown.Centre + new Vector3(0, 0, -100);
    static readonly ZoneCoordinate VaultZone = WorldLayout.ZoneOf(VaultAt.X, VaultAt.Z), HutZone = WorldLayout.ZoneOf(HutAt.X, HutAt.Z);
    static readonly string VaultKey = BuildingKey.Of(VaultZone, "vault");
    static readonly SaveCaptureOptions Options = new() { Placements = [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)] };

    /// <summary>
    /// Testville (items worth up to 5000) with three books (100) and a core (1000), and two homes held by their level-data template: a vault, the
    /// shop of a trader squad of three whose template takes items up to 500, with two lockable chests; and a hut, whose template takes items up to
    /// 5000 and whose interior the level data does not name. <paramref name="takes"/> false: neither template takes any.
    /// </summary>
    static PopulationData Data(bool takes = true)
    {
        var chest = SyntheticTown.Rec("chest-t", FcsRecordType.Building, "Chest");
        chest.Bools["has lock"] = true;
        chest.Bools["has inventory"] = true;
        var house = SyntheticTown.Rec("house-t", FcsRecordType.Building, "House");
        var book = SyntheticTown.Rec("book-t", FcsRecordType.Item, "Book");
        book.Ints["value"] = 100;
        book.Ints["stackable"] = 10;
        var core = SyntheticTown.Rec("core-t", FcsRecordType.Item, "Core");
        core.Ints["value"] = 1000;
        var idol = SyntheticTown.Rec("idol-t", FcsRecordType.Item, "Idol");
        idol.Ints["value"] = 100000;
        var junk = SyntheticTown.Rec("junk-t", FcsRecordType.Item, "Junk");
        junk.Ints["value"] = 5;
        var vendor = SyntheticTown.Rec("vl-t", FcsRecordType.VendorList, "Junk vendor");
        vendor.References["items"] = [SyntheticTown.Ref("junk-t", 100)];
        var vault = SyntheticTown.Rec("vault-t", FcsRecordType.SquadTemplate, "Vault keeper");
        vault.References["leader"] = [SyntheticTown.Ref("2-t", 1)];
        vault.References["squad"] = [SyntheticTown.Ref("2-t", 2)];
        vault.Bools["is trader"] = true;
        vault.References["vendors"] = [SyntheticTown.Ref("vl-t", 100)];
        vault.Ints["vendors fill total amount"] = 2;
        vault.Ints["item artifacts base value"] = takes ? 500 : 0;
        var hut = SyntheticTown.Rec("hut-t", FcsRecordType.SquadTemplate, "Hut");
        hut.Ints["item artifacts base value"] = takes ? 5000 : 0;
        var artifacts = SyntheticTown.Rec("art-t", FcsRecordType.Artifacts, "Artifacts");
        artifacts.References["items"] = [SyntheticTown.Ref("book-t", 3), SyntheticTown.Ref("core-t", 1)];
        var db = SyntheticTown.Database(overrideFlag: true, extra: [chest, house, book, core, idol, junk, vendor, vault, hut, artifacts],
                changeTown: t =>
        {
            t.References["residents"] = [SyntheticTown.Ref("vault-t", 1), SyntheticTown.Ref("hut-t", 1)];
            t.Ints["item artifacts max value"] = 5000;
        });
        TownFixture Chest(int i) => new($"{VaultKey}/{i}", "chest-t", false, VaultAt, 0)
        {
            Function = TownBuildings.GeneralStorageFunction, StorageWidth = 4, StorageHeight = 4,
        };
        var vaultHome = new TownBuilding("vault", "house-t", "Vault", VaultAt, 0, true, false, [])
        {
            Zone = VaultZone, CanHouse = true, Furnished = true, InteriorLayout = "Vault", HandleSerial = 1, Fixtures = [Chest(0),
                    Chest(1)],
        };
        var hutHome = new TownBuilding("hut", "house-t", "Hut", HutAt, 0, false, false, []) { Zone = HutZone, CanHouse = true, Furnished = true,
                HandleSerial = 2 };
        return PopulationData.Create(db, [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)],
            buildings: new Dictionary<string, List<TownBuilding>> { ["inst"] = [vaultHome, hutHome] });
    }

    static SimWorld World(PopulationData data, StandardParts parts = 0)
    {
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var world = new SimWorld(new WorldSettings { Seed = 7, PublishSnapshots = false }, walk,
            SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Movement | parts,
                    new PopulationSettings { CheckEveryTicks = 1, UnloadGraceSeconds = 1 }).Systems);
        world.Player.Faction = data.PlayerFaction;
        return world;
    }

    static void Visit(SimWorld world, int ticks)
    {
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = world.Tick });
        world.RunTicks(ticks);
    }

    static List<Fixture> Chests(SimWorld world) => [.. world.Fixtures.OfBuilding(VaultKey).Where(f => f.Contents is not null)];

    [Fact]
    public void A_town_gives_its_artifacts_to_its_homes_when_it_makes_its_residents_and_loses_those_of_a_home_without_an_interior()
    {
        var data = Data();
        using var world = World(data);
        var population = world.Systems.OfType<PopulationSystem>().Single();
        Assert.Equal([new ArtifactStack("book-t", "", "", 0, 3), new ArtifactStack("core-t", "", "", 0, 1)], population.ArtifactsOf(world, 0));
        Visit(world, 2);

        Assert.Empty(population.ArtifactsOf(world, 0));
        // The core is worth more than the vault takes, so it is the hut's, which has no interior and loses it; each book is drawn between the two
        // homes (boost's draws on mt19937(11667085): the vault).
        var chests = Chests(world);
        var books = chests.SelectMany(c => c.Contents!.Items).ToList();
        Assert.Equal(3, books.Sum(b => b.Quantity));
        Assert.All(books, b => Assert.Equal(("book-t", true), (b.Record, b.Unique)));
        Assert.All(chests.Where(c => c.Contents!.Items.Count > 0), c => Assert.True(c.Lock!.Locked));
    }

    [Fact]
    public void A_town_with_no_home_that_takes_artifacts_keeps_its_list()
    {
        var data = Data(takes: false);
        using var world = World(data);
        var population = world.Systems.OfType<PopulationSystem>().Single();
        Visit(world, 2);
        Assert.Equal([new ArtifactStack("book-t", "", "", 0, 3), new ArtifactStack("core-t", "", "", 0, 1)], population.ArtifactsOf(world, 0));
        Assert.All(Chests(world), c => Assert.Empty(c.Contents!.Items));
    }

    [Fact]
    public void A_building_shop_takes_the_artifacts_of_its_chests_into_its_stock_and_keeps_them_out_of_its_purse_when_it_restocks()
    {
        var data = Data();
        using var world = World(data, StandardParts.Trade);
        Visit(world, 30);
        var squad = world.Squads.All.Single(s => s.TemplateId == "vault-t");
        var shop = squad.Shop!;
        Assert.Equal(VaultKey, shop.Building);
        Assert.All(Chests(world), c => Assert.Empty(c.Contents!.Items));
        Assert.Equal(3, shop.Stock.Items.Where(i => i.Unique).Sum(i => i.Quantity));
        Assert.Contains(shop.Stock.Items, i => i.Record == "junk-t");

        // An idol (100000), unique as an artifact is, would raise the purse to 25000 if a fill counted it.
        var idol = data.Items.Create("idol-t")!;
        idol.Unique = true;
        Assert.True(shop.Stock.Add(idol));
        var artifacts = shop.Stock.Items.Where(i => i.Unique).ToList();
        int fills = shop.Fills;
        shop.NextRestockTick = world.Tick;
        world.RunTicks(30);
        Assert.True(shop.Fills > fills);
        Assert.Equal(artifacts, shop.Stock.Items.Where(i => i.Unique));
        Assert.Equal(3000, squad.Money);   // 0.25 x two junk units, at least 3000
    }

    [Fact]
    public void A_save_made_while_the_shop_lies_dead_keeps_its_stock_in_the_chests()
    {
        var data = Data();
        using var world = World(data, StandardParts.Trade);
        Visit(world, 30);
        var squad = world.Squads.All.Single(s => s.TemplateId == "vault-t");
        foreach (var m in squad.Members) world.Characters.Cold(m.Slot)!.DiedTick = world.Tick;
        world.RunTicks(30);   // the trade system's next look closes the shop
        Assert.Null(squad.Shop);

        // The save holds the vault's zone file, so the chests' contents are written into its state (no squad is written: nobody is alive).
        var builder = SaveCapture.PrepareBase(data, null, Options);
        var file = new SaveFile();
        var list = new FcsRecord { Type = (int)FcsRecordType.InstanceCollection, Id = 1, Name = "0", StringId = WorldLevelData.BuildingListId };
        var instance = new FcsInstance { Id = "vault", Target = "house-t", Position = VaultAt };
        instance.States.Add("vault-S23");
        list.Instances.Add(instance);
        file.Records.AddRange([new FcsRecord { Type = (int)FcsRecordType.GamestateBuilding, Id = 2, Name = "0", StringId = "vault-S23" }, list]);
        builder.Game.Zones[VaultZone] = new SaveZone(VaultZone, file);
        var save = SaveCapture.Capture(world, data, new SaveClock(1, 12, 0), null, Options, builder);
        string folder = Path.Combine(Temp("artifacts-dead"), QuickSave.Name);
        try
        {
            save.Write(folder);
            using var loaded = World(data);
            SaveLoader.Load(loaded, SaveGame.Load(folder), data);
            loaded.Fixtures.RegisterSite(loaded, data, 0);
            var kept = Chests(loaded).SelectMany(c => c.Contents!.Items).ToList();
            Assert.Equal(3, kept.Where(i => i.Record == "book-t" && i.Unique).Sum(i => i.Quantity));
            Assert.Contains(kept, i => i.Record == "junk-t" && !i.Unique);   // the rest of the stock too
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public void A_shop_squad_made_afresh_takes_the_artifacts_its_old_one_held()
    {
        var data = Data();
        using var world = World(data, StandardParts.Trade);
        Visit(world, 30);
        var old = world.Squads.All.Single(s => s.TemplateId == "vault-t");
        Assert.Equal(3, old.Shop!.Stock.Items.Where(i => i.Unique).Sum(i => i.Quantity));
        // Two of its three die (at least 60 %), the leader keeps the shop open; the town unloads with the shop stored and is made afresh at the next
        // visit.
        foreach (var m in old.Members.Where(m => m != old.Leader)) world.Characters.Cold(m.Slot)!.DiedTick = world.Tick;
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = world.Tick });
        world.RunTicks(300);
        Visit(world, 60);

        var fresh = world.Squads.All.Single(s => s.TemplateId == "vault-t");
        Assert.NotEqual(old.Id, fresh.Id);
        Assert.Equal(3, fresh.Shop!.Stock.Items.Where(i => i.Unique && i.Record == "book-t").Sum(i => i.Quantity));
        Assert.All(Chests(world), c => Assert.Empty(c.Contents!.Items));
    }

    [Fact]
    public void A_containers_unique_items_keep_their_flag_in_the_save()
    {
        var data = Data();
        var grid = new Inventory(4, 4);
        var artifact = data.Items.Create("book-t")!;
        artifact.Unique = true;
        Assert.True(grid.AddTo("main", artifact));
        Assert.True(grid.AddTo("main", data.Items.Create("book-t")!));   // a plain book does not join the unique one
        Assert.Equal(2, grid.Items.Count);
        var read = new Inventory(4, 4);
        ContainerSave.Read(ContainerSave.Write(grid)!, read, data.Items);
        Assert.Equal([true, false], read.Items.Select(i => i.Unique));
    }

    [Fact]
    public void The_lists_are_saved_and_loaded_and_an_import_gives_the_towns_new_ones()
    {
        var data = Data();
        using var world = World(data, StandardParts.Player);
        world.Systems.OfType<PopulationSystem>().Single().StartPlayer(world,
                NewGameStart.From(data.Db.Find("40-t")!));   // an import needs a squad of the player's
        Visit(world, 2);
        var save = SaveCapture.Capture(world, data, new SaveClock(1, 12, 0), null, Options);
        var state = save.Towns.Single(t => t.Instance == "inst").Record;
        Assert.Equal(0, state.Ints["artifacts count"]);

        string folder = Path.Combine(Temp("artifacts"), QuickSave.Name);
        try
        {
            save.Write(folder);
            using var loaded = World(data);
            SaveLoader.Load(loaded, SaveGame.Load(folder), data);
            var population = loaded.Systems.OfType<PopulationSystem>().Single();
            Assert.Empty(population.ArtifactsOf(loaded, 0));

            // An import: a town of another faction is cut and gets a new list; one of the player's kept whole adds the new list to its own.
            var other = SaveImport.Filter(SaveGame.Load(folder), ImportOptions.Default, data).Towns.Single(t => t.Instance == "inst").Record;
            Assert.Equal([new ArtifactStack("book-t", "", "", 0, 3), new ArtifactStack("core-t", "", "", 0, 1)],
                    PopulationSystem.ReadArtifacts(other, data.Db));
            var game = SaveGame.Load(folder);
            var mine = game.Towns.Single(t => t.Instance == "inst").Record;
            (mine.Strings["faction"], mine.Bools["discovered"]) = ("204-gamedata.base", true);
            PopulationSystem.WriteArtifacts(mine, [new ArtifactStack("core-t", "", "", 0, 2)]);
            var kept = SaveImport.Filter(game, ImportOptions.Default, data).Towns.Single(t => t.Instance == "inst").Record;
            Assert.Equal([new ArtifactStack("core-t", "", "", 0, 2), new ArtifactStack("book-t", "", "", 0, 3),
                    new ArtifactStack("core-t", "", "", 0, 1)],
                PopulationSystem.ReadArtifacts(kept, data.Db));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }
}
