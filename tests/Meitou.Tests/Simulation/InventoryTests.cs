using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Inventories, weight and eating (docs/simulation.md "Items and eating").</summary>
public class InventoryTests
{
    static SimWorld World(ulong seed = 3, int threads = 1, FeedSettings? feed = null, BodyOptions? options = null)
    {
        var db = SyntheticTown.Database(anatomy: true, items: true);
        return SyntheticTown.World(seed, threads, db: db, bodies: true, feed: feed, bodyOptions: options);
    }

    static IEnumerable<int> Humans(SimWorld world) =>
        Enumerable.Range(0, world.Characters.HighWater).Where(s => world.Characters.Previous[s].Alive && world.Characters.Cold(s)!.Inventory is not null);

    [Fact]
    public void Characters_start_with_the_items_their_record_lists_and_animals_have_none()
    {
        using var world = World();
        world.RunTicks(2);
        var table = world.Characters;
        bool guard = false;
        foreach (int s in Humans(world))
        {
            var cold = table.Cold(s)!;
            if (cold.RecordId != "2-t") { Assert.Empty(cold.Inventory!.Items); continue; }
            guard = true;
            var ration = Assert.Single(cold.Inventory!.Items);
            Assert.Equal("70-t", ration.Record);
            Assert.Equal(2, ration.Quantity);
            Assert.True(ration.IsFood);
            Assert.Equal(1.0f, ration.Nutrition, 3);
            Assert.Equal("main", ration.Section);
            Assert.Equal(2f, cold.Inventory.ContentWeight(), 3);
        }
        Assert.True(guard);
        foreach (var s in Enumerable.Range(0, table.HighWater).Where(s => table.Previous[s].Alive && table.Cold(s)!.RecordId == "4-t")) Assert.Null(table.Cold(s)!.Inventory);
    }

    [Fact]
    public void Items_take_free_cells_and_stack_and_a_full_grid_refuses()
    {
        var inv = new Inventory(4, 2);
        ItemInstance Item(string id, int w, int h, bool stack = false) => new() { Record = id, Name = id, Width = w, Height = h, Stackable = stack };
        Assert.True(inv.Add(Item("a", 2, 2)));
        Assert.True(inv.Add(Item("b", 2, 2)));
        Assert.Equal([(0, 0), (2, 0)], inv.Items.Select(i => (i.X, i.Y)));
        Assert.False(inv.Add(Item("c", 1, 1)));
        var bolts = new Inventory(4, 2);
        Assert.True(bolts.Add(Item("bolt", 1, 1, stack: true)));
        Assert.True(bolts.Add(Item("bolt", 1, 1, stack: true)));
        Assert.Equal(2, Assert.Single(bolts.Items).Quantity);
    }

    [Fact]
    public void A_load_beyond_the_capacity_slows_the_walker()
    {
        using var world = World();
        world.RunTicks(2);
        var table = world.Characters;
        int s = Humans(world).First(x => table.Cold(x)!.RecordId == "2-t");
        float light = table.Previous[s].MaxSpeed;
        table.Cold(s)!.Inventory!.Items.Add(new ItemInstance { Record = "x", Name = "anvil", UnitWeight = 400 });
        world.RunTicks(2);
        Assert.True(table.Previous[s].MaxSpeed < light, $"{table.Previous[s].MaxSpeed} < {light}");
    }

    [Fact]
    public void A_hungry_character_eats_its_own_food_and_the_stomach_fills()
    {
        using var world = World(feed: new FeedSettings { ProvisionNpcs = false });
        world.RunTicks(2);
        var table = world.Characters;
        int s = Humans(world).First(x => table.Cold(x)!.RecordId == "2-t");
        var cold = table.Cold(s)!;
        cold.Medical!.Hunger = 1.2f;
        world.RunTicks(35);
        Assert.Equal(1, Assert.Single(cold.Inventory!.Items).Quantity);
        Assert.True(cold.Medical.Fed > 0.5f || cold.Medical.Hunger > 1.2f);
        // Not hungry again while the stomach digests: nothing more is eaten.
        world.RunTicks(300);
        Assert.Equal(1, cold.Inventory.Items.Single().Quantity);
    }

    [Fact]
    public void A_hungry_character_without_food_is_fed_by_a_squad_mate_standing_near()
    {
        using var world = World(feed: new FeedSettings { ProvisionNpcs = false, SquadReach = 1000 });
        world.RunTicks(2);
        var table = world.Characters;
        var squad = Humans(world).Select(x => table.Cold(x)!).Where(c => c.RecordId == "2-t").GroupBy(c => c.SquadId).First(g => g.Count() > 1).ToList();
        squad[0].Inventory!.Items.Clear();
        squad[0].Medical!.Hunger = 1.2f;
        int before = squad.Skip(1).Sum(c => c.Inventory!.Items.Sum(i => i.Quantity));
        world.RunTicks(35);
        Assert.True(squad[0].Medical!.Fed > 0 || squad[0].Medical!.Hunger > 1.2f);
        Assert.Equal(before - 1, squad.Skip(1).Sum(c => c.Inventory!.Items.Sum(i => i.Quantity)));
    }

    [Fact]
    public void Nobody_starves_when_npcs_are_provisioned_but_the_player_gets_nothing_free()
    {
        // A very short hunger time: three levels last under two game hours.
        var options = new BodyOptions { HungerTime = 0.02f };
        using var fed = World(feed: new FeedSettings { ProvisionHours = 0.5f }, options: options);
        fed.RunTicks(40000);   // 12 game hours
        var table = fed.Characters;
        Assert.NotEmpty(Humans(fed));
        Assert.All(Humans(fed), s => Assert.False(table.Cold(s)!.Medical!.Dead, "provisioned NPCs do not starve"));
        Assert.Contains(Humans(fed), s => table.Cold(s)!.NextProvisionTick > 0);

        using var unfed = World(feed: new FeedSettings { ProvisionNpcs = false }, options: options);
        unfed.RunTicks(40000);
        Assert.Contains(Humans(unfed), s => unfed.Characters.Cold(s)!.Medical!.Dead && unfed.Characters.Cold(s)!.Medical!.Cause == DeathCause.Starvation);
    }

    [Fact]
    public void Eating_and_provisioning_do_not_change_with_the_thread_count()
    {
        static List<ulong> Hashes(int threads)
        {
            using var world = World(9, threads, new FeedSettings { CheckEveryTicks = 10 }, new BodyOptions { HungerTime = 0.02f });
            var hashes = new List<ulong>();
            int ran = 0;
            foreach (int target in new[] { 1, 100, 3000, 9000 })
            {
                world.RunTicks(target - ran);
                ran = target;
                hashes.Add(world.StateHash());
            }
            return hashes;
        }
        var one = Hashes(1);
        Assert.Equal(one, Hashes(4));
        Assert.Equal(one, Hashes(16));
        Assert.Equal(one.Count, one.Distinct().Count());
    }

    [Fact]
    public void A_squads_purse_is_part_of_the_state()
    {
        static ulong Run(long money)
        {
            using var world = World(5);
            world.RunTicks(2);
            world.Squads.Find(world.Squads.All.First().Id)!.Money = money;
            return world.StateHash();
        }
        Assert.NotEqual(Run(0), Run(50));
    }
}
