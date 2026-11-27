using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Building;

namespace Meitou.Tests.Game;

/// <summary>
/// The indoor and outdoor rules of <see cref="PlacementCheck"/> (docs/game/buildings-production.md "Placement").
/// </summary>
public class PlacementCheckTests
{
    /// <summary>
    /// Flat empty ground at height 0 with no footprints, towns or other buildings.
    /// </summary>
    static readonly PlacementServices Open = new()
    {
        Ground = (_, _) => 0,
        Footprint = _ => null,
        StaticAround = (_, _) => [],
    };

    static PlacementQuery Ghost(BuildingBuildData building) =>
        new(building, Vector3.Zero, Quaternion.Identity, 0, 0, [], [], [], [], [], "player", Open);

    /// <summary>
    /// The floor of a level building the player owns, round the origin.
    /// </summary>
    static GhostFloor OwnedFloor() => new("house", [new(-100, -100), new(100, -100), new(100, 100), new(-100, 100)], 0, "house-1", true);

    static BuildingBuildData Building(bool rain) => new() { Id = "b", Name = "b", IsRainCollector = rain };

    [Fact]
    public void A_rain_collector_may_not_stand_on_a_buildings_floor()
    {
        var query = Ghost(Building(rain: true)) with { On = OwnedFloor() };

        Assert.Equal(PlacementFailure.MustBeOutside, PlacementCheck.Evaluate(query));
    }

    [Fact]
    public void A_rain_collector_may_stand_on_the_ground_or_a_roof()
    {
        Assert.Equal(PlacementFailure.None, PlacementCheck.Evaluate(Ghost(Building(rain: true))));
        Assert.Equal(PlacementFailure.None, PlacementCheck.Evaluate(Ghost(Building(rain: true)) with { Surface = 0 }));
    }

    [Fact]
    public void Other_buildings_may_stand_on_a_buildings_floor()
    {
        var query = Ghost(Building(rain: false)) with { On = OwnedFloor() };

        Assert.Equal(PlacementFailure.None, PlacementCheck.Evaluate(query));
    }
}
