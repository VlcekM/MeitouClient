using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Building;

namespace Meitou.Tests.Game;

/// <summary>
/// The build menu's lists are sorted as the original sorts them (<c>FUN_1404e4480</c>, <c>FUN_1404e4210</c>).
/// </summary>
public class BuildMenuOrderTests
{
    static BuildingBuildData B(string id, string category, string group, int materials) => new()
    {
        Id = id, Name = id, Category = category, Group = group, BuildMaterials = materials,
    };

    [Fact]
    public void Categories_and_groups_go_by_name_and_members_by_materials_descending()
    {
        var menu = new BuildMenu(
        [B("a", "walls", "Wall", 5), B("b", "Beds", "Zed", 3), B("c", "Beds", "Cot", 2), B("d", "Beds", "Cot", 9), B("e", "Beds", "Cot", 9),]);
        var all = new HashSet<string> { "a", "b", "c", "d", "e" };

        Assert.Equal(["Beds", "walls"], menu.Categories(all));

        var groups = menu.Groups("Beds", all);
        Assert.Equal(["Cot", "Zed"], groups.Select(g => g.Name));

        // From the last in the database to the first, sorted by materials descending: the equal 9s stay in that order,
        // so the later record leads and is the default.
        Assert.Equal(["e", "d", "c"], groups[0].Members.Select(m => m.Id));
        Assert.Equal("e", groups[0].Default.Id);
    }
}
