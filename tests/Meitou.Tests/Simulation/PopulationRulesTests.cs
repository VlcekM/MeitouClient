using System.Numerics;
using Meitou.Simulation;
using static Meitou.Tests.Simulation.SyntheticTown;

namespace Meitou.Tests.Simulation;

/// <summary>Population rules pinned to the original's behaviour (docs/game/factions-squads-towns.md).</summary>
public class PopulationRulesTests
{
    /// <summary>FUN_1408f5ad0: 10 for an empty sector, a flat 5 below a load of 2, 1 at 2, 1 / n above.</summary>
    [Theory]
    [InlineData(0f, 10f)]
    [InlineData(0.5f, 5f)]
    [InlineData(1f, 5f)]
    [InlineData(1.5f, 5f)]
    [InlineData(2f, 1f)]
    [InlineData(4f, 0.25f)]
    public void Patrol_sector_weight_follows_the_original_constants(float load, float weight)
        => Assert.Equal(weight, PopulationSystem.PatrolWeight(load));

    /// <summary>FUN_1405cf470: state 2 returns to 1 for the player's faction or in a town friendly to the character (its own faction's), never in the open.</summary>
    [Fact]
    public void A_unique_is_freed_again_only_in_a_friendly_town_or_for_the_player()
    {
        using var world = SyntheticTown.World(5, 1, 3, new PopulationSettings { CheckEveryTicks = 1 });
        var population = (PopulationSystem)world.Systems[0];
        world.RunTick();
        var table = world.Characters;
        int slot = Enumerable.Range(0, table.Previous.Length).First(i => table.Previous[i].Alive);
        var cold = table.Cold(slot)!;
        Assert.True(population.FreeAgain(world, cold, Centre, -1));
        var open = new Vector3(-90000, 0, -90000);
        Assert.False(population.FreeAgain(world, cold, open, -1));
        Assert.True(population.FreeAgain(world, cold, open, cold.Faction));
    }

    /// <summary>FUN_140998fa0 calls the day's reset (FUN_140998e40): standing in another town than the last purchase's clears the list and records the day.</summary>
    [Fact]
    public void A_shopper_in_another_town_starts_its_list_over_and_the_day_is_recorded()
    {
        var list = new Meitou.Simulation.Ai.ShoppingList([3], [1]);
        list.Begin(1, 4, 9);
        list.Use(0);
        Assert.False(list.Pending(4, 9, 1));
        Assert.True(list.Pending(4, 9, 2));
        list.Use(0);
        // At 03:00 of the next day another town clears it and records that day, so the 07:00 reset does not clear it a second time.
        Assert.True(list.Pending(5, 3, 2));
        list.Use(0);
        Assert.False(list.Pending(5, 9, 1));
    }
}
