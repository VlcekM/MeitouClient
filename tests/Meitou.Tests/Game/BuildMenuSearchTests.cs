using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Building;

namespace Meitou.Tests.Game;

/// <summary>
/// The build menu search keeps the categories, groups and members whose shown names hold every word of the query, as its ticks allow, and puts a
/// group's matching members first.
/// </summary>
public class BuildMenuSearchTests
{
    static BuildingBuildData B(string id, string name, string category, string group, int materials = 0) => new()
    {
        Id = id, Name = name, Category = category, Group = group, BuildMaterials = materials,
    };

    /// <summary>
    /// A catalog with a two-member group (Storage Box: large and small), single buildings and categories named like no building.
    /// </summary>
    static readonly BuildMenu Menu = new(
    [
        B("box-l", "Large Storage Box", "STORAGE", "Storage Box", 9),
        B("box-s", "Small Storage Box", "STORAGE", "Storage Box", 5),
        B("chest", "Chest", "STORAGE", "Chest"),
        B("lamp", "Lamp", "LIGHTS", "Lamp"),
        B("wall", "Stone Wall", "WALLS", "Stone Wall"),
        B("forge", "Iron Forge", "INDUSTRY", "Iron Forge"),
    ]);

    /// <summary>
    /// Every building of <see cref="Menu"/> unlocked.
    /// </summary>
    static readonly HashSet<string> All = ["box-l", "box-s", "chest", "lamp", "wall", "forge"];

    static IReadOnlyList<string> Categories(BuildMenuSearch search) =>
        search.Categories(Menu.Categories(All), c => c, c => Menu.Groups(c, All), g => g.Name, m => m.Name);

    static IReadOnlyList<BuildGroup> Groups(BuildMenuSearch search, string category) =>
        search.Groups(Menu.Groups(category, All), search.CategoryHit(category), g => g.Name, m => m.Name);

    static IEnumerable<string> Ids(IReadOnlyList<BuildGroup> groups) => groups.SelectMany(g => g.Members.Select(m => g.Name + "/" + m.Id));

    [Fact]
    public void An_empty_or_blank_query_keeps_the_menu_as_it_is()
    {
        var blank = new BuildMenuSearch("   ", BuildSearchScope.All);

        Assert.False(blank.Active);
        Assert.Equal(Menu.Categories(All), Categories(blank));
        Assert.Equal(Ids(Menu.Groups("STORAGE", All)), Ids(Groups(blank, "STORAGE")));
    }

    [Fact]
    public void A_copy_with_another_query_searches_for_the_new_query()
    {
        var search = BuildMenuSearch.Empty with { Query = "lamp" };

        Assert.True(search.Active);
        Assert.Equal(["LIGHTS"], Categories(search));
    }

    [Fact]
    public void A_building_name_keeps_only_its_category_and_group_ignoring_case()
    {
        var search = new BuildMenuSearch("wALL", BuildSearchScope.All);

        Assert.Equal(["WALLS"], Categories(search));
        Assert.Equal(["Stone Wall"], Groups(search, "WALLS").Select(g => g.Name));
    }

    [Fact]
    public void A_category_name_lists_every_building_of_the_category()
    {
        var search = new BuildMenuSearch("storage", BuildSearchScope.Categories);

        Assert.Equal(["STORAGE"], Categories(search));
        Assert.Equal(["Chest", "Storage Box"], Groups(search, "STORAGE").Select(g => g.Name));
    }

    [Fact]
    public void Every_word_must_appear()
    {
        Assert.Equal(["INDUSTRY"], Categories(new BuildMenuSearch("forge iron", BuildSearchScope.All)));
        Assert.Empty(Categories(new BuildMenuSearch("iron wall", BuildSearchScope.All)));
    }

    [Fact]
    public void A_subtype_hit_keeps_its_group_with_the_matching_members_first()
    {
        var search = new BuildMenuSearch("small", BuildSearchScope.Subtypes);

        Assert.Equal(["STORAGE"], Categories(search));
        var group = Assert.Single(Groups(search, "STORAGE"));
        Assert.Equal(["box-s", "box-l"], group.Members.Select(m => m.Id));
        Assert.Equal("box-s", group.Default.Id);
    }

    [Fact]
    public void A_group_matched_by_its_own_name_keeps_its_members_order()
    {
        var group = Assert.Single(Groups(new BuildMenuSearch("storage box", BuildSearchScope.Buildings), "STORAGE"));

        Assert.Equal(["box-l", "box-s"], group.Members.Select(m => m.Id));
    }

    [Fact]
    public void Unticked_scopes_are_not_searched()
    {
        // The category's own name, with Categories off.
        Assert.Empty(Categories(new BuildMenuSearch("lights", BuildSearchScope.Buildings | BuildSearchScope.Subtypes)));

        // A subtype's name, with Subtypes off.
        Assert.Empty(Categories(new BuildMenuSearch("small", BuildSearchScope.Categories | BuildSearchScope.Buildings)));

        // A building's name, with Buildings off: a lone building is no subtype.
        Assert.Empty(Categories(new BuildMenuSearch("chest", BuildSearchScope.Categories | BuildSearchScope.Subtypes)));

        Assert.Empty(Categories(new BuildMenuSearch("wall", BuildSearchScope.None)));
    }
}
