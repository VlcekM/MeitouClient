using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Ui;
using Meitou.Game;
using Meitou.Game.Building;
using Meitou.Game.Host;
using Meitou.Game.Ui;

namespace Meitou.Tests.Ui;

/// <summary>
/// The search row over the build window: typing filters the lists, the arrows reach the matching subtypes first, the ticks and the clear button
/// change the filter, and Esc empties the box.
/// </summary>
public class BuildSearchWindowTests
{
    /// <summary>
    /// CAMPING holds a bed group of three (Hay Bed, Camp Bed, Camp Bed Deluxe by materials) and a campfire; BUILDINGS a house.
    /// </summary>
    static readonly BuildingBuildData[] Buildings =
    [
        new() { Id = "house", Name = "House", Category = "BUILDINGS", Group = "House" },
        new() { Id = "deluxe", Name = "Camp Bed Deluxe", Category = "CAMPING", Group = "Bed", BuildMaterials = 1 },
        new() { Id = "camp", Name = "Camp Bed", Category = "CAMPING", Group = "Bed", BuildMaterials = 5 },
        new() { Id = "hay", Name = "Hay Bed", Category = "CAMPING", Group = "Bed", BuildMaterials = 9 },
        new() { Id = "fire", Name = "Campfire", Category = "CAMPING", Group = "Campfire" },
    ];

    [Fact]
    public void The_row_sits_over_the_main_panel_with_its_box_clear_button_and_ticks()
    {
        var (session, ui) = Open();
        using var _ = session;

        var frame = Frame(ui);
        var panel = Assert.Single(frame, c => c.Name == "MainPanel").Bounds;
        var row = Assert.Single(frame, c => c.Name == "build-search-panel").Bounds;
        Assert.Equal(panel.Y, row.Y + row.Height, 3);
        Assert.Equal(panel.Width, row.Width, 3);
        Assert.Contains(frame, c => c.Name == BuildSearchBar.Box && c.Type == "EditBox");
        Assert.False(Assert.Single(frame, c => c.Name == BuildSearchBar.ClearButton).Enabled);
        Assert.All(Ticks(frame), t => Assert.Equal("true", t.Properties["Checked"]));
    }

    [Fact]
    public void Typing_filters_the_lists_and_the_right_arrow_steps_through_the_matches_first()
    {
        var (session, ui) = Open();
        using var _ = session;

        Type(ui, "camp bed");
        Assert.Equal(["CAMPING"], Rows(ui, "CategoriesList"));

        Click(ui, "CategoriesList@row0");
        Assert.Equal(["Bed"], Rows(ui, "BuildingsList"));
        Assert.Equal("camp", ui.Build.CurrentBuilding()!.Id);

        // Camp Bed, Camp Bed Deluxe, then the Hay Bed that does not match, and round again.
        ClickWidget(ui, "BuldingTypeNextButton");
        Assert.Equal("deluxe", ui.Build.CurrentBuilding()!.Id);
        ClickWidget(ui, "BuldingTypeNextButton");
        Assert.Equal("hay", ui.Build.CurrentBuilding()!.Id);
        ClickWidget(ui, "BuldingTypeNextButton");
        Assert.Equal("camp", ui.Build.CurrentBuilding()!.Id);
    }

    [Fact]
    public void The_clear_button_and_esc_empty_the_query_and_show_every_category()
    {
        var (session, ui) = Open();
        using var _ = session;

        Type(ui, "house");
        Assert.Equal(["BUILDINGS"], Rows(ui, "CategoriesList"));
        Assert.True(Assert.Single(Frame(ui), c => c.Name == BuildSearchBar.ClearButton).Enabled);

        ClickWidget(ui, BuildSearchBar.ClearButton);
        Assert.Equal(["BUILDINGS", "CAMPING"], Rows(ui, "CategoriesList"));
        Assert.Equal("", ui.Build.Search.Filter.Query);

        Type(ui, "house");
        Assert.True(ui.Build.Search.Escape());
        Assert.Equal(["BUILDINGS", "CAMPING"], Rows(ui, "CategoriesList"));

        // A second Esc leaves the empty box; a third is no longer the box's.
        Assert.True(ui.Build.Search.Escape());
        Assert.False(ui.Build.Search.Escape());
    }

    [Fact]
    public void A_tick_turned_off_stops_searching_that_scope()
    {
        var (session, ui) = Open();
        using var _ = session;

        Type(ui, "camping");
        Assert.Equal(["CAMPING"], Rows(ui, "CategoriesList"));

        ClickWidget(ui, BuildSearchBar.TickPrefix + "0");
        Assert.Equal("false", Ticks(Frame(ui))[0].Properties["Checked"]);
        Assert.Empty(Rows(ui, "CategoriesList"));
    }

    /// <summary>
    /// A session with the catalog unlocked and build mode open.
    /// </summary>
    static (WorldSession Session, PlayerInterface Ui) Open()
    {
        var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: []);
        Unlock(session);
        var ui = Interface(session);
        ui.UiMode = UiMode.Meitou;
        ui.Build.SetBuildMode(true);
        return (session, ui);
    }

    /// <summary>
    /// Clicks the search box and types <paramref name="text"/> into it.
    /// </summary>
    static void Type(PlayerInterface ui, string text)
    {
        ClickWidget(ui, BuildSearchBar.Box);
        foreach (char ch in text) Assert.True(ui.FaithfulScreens.TextInput(ch));
    }

    /// <summary>
    /// The captions of a list's rows.
    /// </summary>
    static List<string> Rows(PlayerInterface ui, string list) =>
        [.. Frame(ui).Where(c => c.Name.StartsWith(list + "@row", StringComparison.Ordinal)).Select(c => c.Caption)];

    /// <summary>
    /// The three ticks, in the row's order.
    /// </summary>
    static List<UiControl> Ticks(IReadOnlyList<UiControl> frame) =>
        [.. Enumerable.Range(0, 3).Select(i => Assert.Single(frame, c => c.Name == BuildSearchBar.TickPrefix + i))];

    /// <summary>
    /// Unlocks the catalog through research so the build menu uses the published snapshot.
    /// </summary>
    static void Unlock(WorldSession session)
    {
        var file = new FcsFile();
        foreach (var building in Buildings)
            file.Records.Add(new FcsRecord { StringId = building.Id, Name = building.Name, RecordType = FcsRecordType.Building, Flags = 0x10 });

        var research = new FcsRecord { StringId = "research", Name = "Research", RecordType = FcsRecordType.Research, Flags = 0x10 };
        research.References["enable buildings"] = [.. Buildings.Select(b => new FcsReference(b.Id, 0, 0, 0))];
        file.Records.Add(research);
        var database = new GameDatabase();
        database.Apply(file, "test.mod");
        session.World.Research.Set(database, [research.StringId]);
        session.World.RunTick();
    }

    /// <summary>
    /// An asset-free build layout with the original's MainPanel, lists, name and arrows, and the screens build mode closes.
    /// </summary>
    static PlayerInterface Interface(WorldSession session)
    {
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { FaithfulScreens = new(new()) };
        var empty = MyGuiReader.ParseLayout("<MyGUI type='Layout'><Widget name='Root' position='0 0 1 1'/></MyGUI>");
        foreach (string screen in new[] { "inventory", "bag", "limbs", "loot", "loot-bag", "building", "bench", "storage" })
            ui.FaithfulScreens.Register(screen, empty);

        ui.FaithfulScreens.Register("build", MyGuiReader.ParseLayout("""
            <MyGUI type="Layout"><Widget name="Root" position="0 0 800 600">
              <Widget name="MainPanel" position="0 200 700 300">
                <Widget type="ListBox" name="CategoriesList" position="0 0 180 250"><Property key="LineSkin" value="TestRow"/></Widget>
                <Widget type="ListBox" name="BuildingsList" position="200 0 180 250"><Property key="LineSkin" value="TestRow"/></Widget>
                <Widget type="Button" name="BuldingTypePrevButton" position="400 0 20 30"/>
                <Widget type="TextBox" name="BuildingName" position="420 0 240 30"/>
                <Widget type="Button" name="BuldingTypeNextButton" position="660 0 20 30"/>
              </Widget>
            </Widget></MyGUI>
            """));
        ui.FaithfulScreens.Activated += ui.Build.BuildAction;
        ui.Build.BuildHost = new BuildServices
        {
            Menu = new BuildMenu(Buildings), ItemName = id => id, Description = _ => "", Parts = _ => [],
            Placement = new PlacementServices { Ground = (_, _) => 0, Footprint = _ => null, StaticAround = (_, _) => [] },
            Towns = () => [], PlayerFactionId = () => "player", DrawGhosts = _ => { },
        };
        return ui;
    }

    /// <summary>
    /// Arranges through the screen host, with the build window's bindings and added content, as in the game.
    /// </summary>
    static IReadOnlyList<UiControl> Frame(PlayerInterface ui)
    {
        UiBindings Bind(string _)
        {
            var bindings = new UiBindings();
            ui.Build.BindBuildScreen(bindings);
            return bindings;
        }

        void Add(string _, List<UiControl> controls) => ui.Build.AddBuildContent(controls);

        return Assert.Single(ui.FaithfulScreens.Arrange(800, 600, Bind, Add)).Controls;
    }

    /// <summary>
    /// Presses and releases the centre of a list row.
    /// </summary>
    static void Click(PlayerInterface ui, string row) => ClickWidget(ui, row);

    /// <summary>
    /// Presses and releases the centre of the widget named <paramref name="name"/>.
    /// </summary>
    static void ClickWidget(PlayerInterface ui, string name)
    {
        var bounds = Assert.Single(Frame(ui), c => c.Name == name).Bounds;
        Assert.True(ui.FaithfulScreens.PointerDown(new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)));
        ui.FaithfulScreens.PointerUp();
    }
}
