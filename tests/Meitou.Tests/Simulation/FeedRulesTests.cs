using Meitou.Simulation.Items;

namespace Meitou.Tests.Simulation;

/// <summary>Small rules of the feeding and first aid kit tests (docs/game/animals.md "Death items", docs/game/ai-tasks.md "Medic jobs").</summary>
public class FeedRulesTests
{
    static ItemInstance Food(bool death) => new() { Record = "food", ItemFunction = FeedSystem.FoodFunction, Quantity = 1, Charges = 100, MaxCharges = 100, Death = death };

    /// <summary>The bite's item lookup skips a death item while its owner lives and counts it once the owner is dead (<c>FUN_140748d70</c>, <c>FUN_1407465d0</c>).</summary>
    [Fact]
    public void A_death_item_is_food_only_for_a_dead_owner()
    {
        var items = new List<ItemInstance> { Food(death: true) };
        Assert.Null(FeedSystem.Best(items, FeedSystem.FoodFunction, null));
        Assert.Same(items[0], FeedSystem.Best(items, FeedSystem.FoodFunction, null, ownerDead: true));
    }
}

public class MedicKitFlagTests
{
    /// <summary>The medic finder's kit flags compare only the item function (<c>FUN_14004b84e</c>): an empty kit still counts, another function does not.</summary>
    [Fact]
    public void A_kit_flag_tests_only_the_item_function()
    {
        var inventory = new Inventory(4, 4);
        inventory.Items.Add(new ItemInstance { Record = "kit", ItemFunction = ItemInfo.FirstAidKitFunction, Quantity = 1, Charges = 0, MaxCharges = 100 });
        Assert.True(Meitou.Simulation.FirstAid.FirstAidSystem.HasFunction(inventory, ItemInfo.FirstAidKitFunction));
        Assert.False(Meitou.Simulation.FirstAid.FirstAidSystem.HasFunction(inventory, ItemInfo.RobotKitFunction));
    }
}
