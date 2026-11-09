using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Inventories, weight and eating (docs/simulation.md "Items and eating").
/// </summary>
public class InventoryTests
{
    static SimWorld World(ulong seed = 3, int threads = 1, FeedSettings? feed = null, BodyOptions? options = null)
    {
        var db = SyntheticTown.Database(anatomy: true, items: true);
        return SyntheticTown.World(seed, threads, db: db, bodies: true, feed: feed, bodyOptions: options);
    }

    static IEnumerable<int> Humans(SimWorld world) =>
        Enumerable.Range(0,
                world.Characters.HighWater).Where(s => world.Characters.Previous[s].Alive && world.Characters.Cold(s)!.Inventory is not null);

    static ItemInstance Unit(string id, int quantity, int limit, Meitou.Data.Fcs.FcsRecordType type = Data.Fcs.FcsRecordType.Item) =>
        new() { Record = id, Name = id, Type = type, Quantity = quantity, StackLimit = limit };

    [Fact]
    public void Stacks_hold_at_most_the_stack_limit_and_the_rest_takes_new_cells()
    {
        // Items take free cells: two 2 x 2 items sit side by side.
        var grid = new Inventory(4, 2);

        Assert.True(grid.Add(new ItemInstance { Record = "a", Name = "a", Width = 2, Height = 2 }));
        Assert.True(grid.Add(new ItemInstance { Record = "b", Name = "b", Width = 2, Height = 2 }));
        Assert.Equal([(0, 0), (2, 0)], grid.Items.Select(i => (i.X, i.Y)));

        var inv = new Inventory(3, 1);

        // 10 books with the data's limit 4: stacks of 4, 4 and 2, one per cell.
        Assert.True(inv.Add(Unit("book", 10, 4)));
        Assert.Equal([4, 4, 2], inv.Items.Select(i => i.Quantity));
        Assert.Equal([0, 1, 2], inv.Items.Select(i => i.X));

        // One more tops up the last stack; three more fill it and leave one that has no cell: the call says so and the unit stays with the caller.
        Assert.True(inv.Add(Unit("book", 1, 4)));
        Assert.Equal(3, inv.Items[2].Quantity);

        var rest = Unit("book", 3, 4);

        Assert.False(inv.Add(rest));
        Assert.Equal(4, inv.Items[2].Quantity);
        Assert.Equal(2, rest.Quantity);

        // Limit 1 (most items, and the default): no stacking at all.
        var single = new Inventory(4, 1);

        Assert.True(single.Add(Unit("ration", 2, 1)));
        Assert.True(single.Add(Unit("ration", 1, 1)));
        Assert.Equal([1, 1, 1], single.Items.Select(i => i.Quantity));
    }

    [Fact]
    public void Npcs_are_kept_fed_by_their_hunger_floor_but_the_player_gets_nothing_free()
    {
        // A very short hunger time: three levels last under two game hours.
        var options = new BodyOptions { HungerTime = 0.02f };
        using var fed = World(options: options);
        fed.RunTicks(40000);   // 12 game hours
        var table = fed.Characters;

        Assert.NotEmpty(Humans(fed));
        Assert.All(Humans(fed),
                s => Assert.False(table.Cold(s)!.Medical!.Dead, "NPCs are topped up to their squad's hunger floor and do not starve"));

        using var unfed = World(options: options);
        unfed.RunTicks(1);

        Assert.NotEmpty(Humans(unfed));

        foreach (int s in Humans(unfed).ToList()) unfed.Characters.Cold(s)!.IsPlayer = true;
        unfed.RunTicks(40000);

        Assert.Contains(Humans(unfed),
                s => unfed.Characters.Cold(s)!.Medical!.Dead && unfed.Characters.Cold(s)!.Medical!.Cause == DeathCause.Starvation);
    }
}

/// <summary>
/// The ITEM <c>stackable</c> field against the install's base records (docs/simulation.md "Items and eating"). Skipped without the game.
/// </summary>
[Slow]
public class StackLimitInstallTests
{
    [Fact]
    public void Stackable_is_a_stack_limit_in_the_data()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");

        var db = InstallData.BaseGame!;

        // An int on every ITEM (never a bool). MAP_ITEMs hold 1.
        var items = db.OfType(Data.Fcs.FcsRecordType.Item).ToList();

        Assert.All(items, r => Assert.False(r.Bools.ContainsKey("stackable")));
        Assert.Equal(100, ItemInfo.From(items.First(r => r.Name == "Cats")).StackLimit);

        // Crossbow bolts do not stack: each item is a quiver whose charges are the shots.
        Assert.All(db.OfType(Data.Fcs.FcsRecordType.Crossbow).SelectMany(c => c.GetReferences("ammo")).Select(r => db.Find(r.TargetStringId)!),

            b => Assert.Equal(1, ItemInfo.From(b).StackLimit));

        // Gear never stacks, whatever its record says; backpacks carry the bonus.
        Assert.All(db.OfType(Data.Fcs.FcsRecordType.Weapon).Take(20), w => Assert.Equal(1, ItemInfo.From(w).StackLimit));

        var bull = ItemInfo.From(db.OfType(Data.Fcs.FcsRecordType.Container).First(r => r.Name == "Bull Backpack"));

        Assert.Equal((1, 5f), (bull.StackBonusMinimum, bull.StackBonusMult));
    }
}
