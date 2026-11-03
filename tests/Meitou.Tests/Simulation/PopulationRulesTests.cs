using Meitou.Simulation;

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
}
