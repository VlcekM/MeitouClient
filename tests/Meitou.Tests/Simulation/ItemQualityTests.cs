using Meitou.Simulation.Items;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Simulation;

/// <summary>
/// A new ITEM carries its record's quality (Item::Item, FUN_14075faf0), which the item tooltip and the lockpick choice read.
/// </summary>
public class ItemQualityTests
{
    [Fact]
    public void A_new_item_takes_its_records_quality_not_the_makers()
    {
        var items = new ItemFactory(FirstAidTests.KitDatabase(), BaseGame);

        Assert.Equal(40, items.Create("72-t", 1, 90)!.Quality);
        Assert.Equal(20, items.Create("74-t")!.Quality);
    }
}
