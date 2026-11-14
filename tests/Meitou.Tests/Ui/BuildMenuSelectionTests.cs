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
/// The build window's fresh menu state and retained list highlights agree after reopening and changing categories.
/// </summary>
public class BuildMenuSelectionTests
{
    /// <summary>
    /// Reopening starts without a category or building, as a newly constructed original build window does.
    /// </summary>
    [Fact]
    public void Reopening_clears_the_page_and_both_list_highlights()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: []);
        BuildingBuildData[] buildings =
        [
            new() { Id = "house", Name = "House", Category = "BUILDINGS", Group = "House" },
            new() { Id = "bed", Name = "Bed", Category = "CAMPING", Group = "Bed" },
            new() { Id = "fire", Name = "Campfire", Category = "CAMPING", Group = "Campfire" },
        ];
        Unlock(session, buildings);
        var ui = Interface(session, buildings);

        ui.Build.SetBuildMode(true);
        AssertUnselected(ui);
        Click(ui, "CategoriesList@row1");
        Click(ui, "BuildingsList@row1");
        Assert.Equal("fire", ui.Build.CurrentBuilding()!.Id);
        AssertChecked(ui, "CategoriesList@row1");
        AssertChecked(ui, "BuildingsList@row1");

        ui.Build.SetBuildMode(false);
        ui.Build.SetBuildMode(true);
        AssertUnselected(ui);
        Click(ui, "CategoriesList@row0");
        Assert.Equal("house", ui.Build.CurrentBuilding()!.Id);
        AssertChecked(ui, "CategoriesList@row0");
        AssertChecked(ui, "BuildingsList@row0");

        Click(ui, "CategoriesList@row1");
        Click(ui, "BuildingsList@row1");
        Click(ui, "CategoriesList@row0");
        Assert.Equal("house", ui.Build.CurrentBuilding()!.Id);
        AssertChecked(ui, "BuildingsList@row0");
        ui.Build.SetBuildMode(false);
    }

    /// <summary>
    /// Unlocks the synthetic catalog through research so the normal build menu uses the published snapshot.
    /// </summary>
    static void Unlock(WorldSession session, IReadOnlyList<BuildingBuildData> buildings)
    {
        var file = new FcsFile();
        foreach (var building in buildings)
            file.Records.Add(new FcsRecord { StringId = building.Id, Name = building.Name, RecordType = FcsRecordType.Building, Flags = 0x10 });

        var research = new FcsRecord { StringId = "research", Name = "Research", RecordType = FcsRecordType.Research, Flags = 0x10 };
        research.References["enable buildings"] = [.. buildings.Select(b => new FcsReference(b.Id, 0, 0, 0))];
        file.Records.Add(research);
        var database = new GameDatabase();
        database.Apply(file, "test.mod");
        session.World.Research.Set(database, [research.StringId]);
        session.World.RunTick();
    }

    /// <summary>
    /// Registers a small asset-free build layout and the inventory screens closed by build mode.
    /// </summary>
    static PlayerInterface Interface(WorldSession session, IReadOnlyList<BuildingBuildData> buildings)
    {
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { FaithfulScreens = new(new()) };
        var empty = MyGuiReader.ParseLayout("<MyGUI type='Layout'><Widget name='Root' position='0 0 1 1'/></MyGUI>");
        foreach (string screen in new[] { "inventory", "bag", "limbs", "loot", "loot-bag", "building", "bench", "storage" })
            ui.FaithfulScreens.Register(screen, empty);

        ui.FaithfulScreens.Register("build", MyGuiReader.ParseLayout("""
            <MyGUI type="Layout"><Widget name="Root" position="0 0 600 300">
              <Widget type="ListBox" name="CategoriesList" position="0 0 180 250"><Property key="LineSkin" value="TestRow"/></Widget>
              <Widget type="ListBox" name="BuildingsList" position="200 0 180 250"><Property key="LineSkin" value="TestRow"/></Widget>
              <Widget type="TextBox" name="BuildingName" position="400 0 180 30"/>
            </Widget></MyGUI>
            """));
        ui.FaithfulScreens.Activated += ui.Build.BuildAction;
        ui.Build.BuildHost = new BuildServices
        {
            Menu = new BuildMenu(buildings), ItemName = id => id, Description = _ => "", Parts = _ => [],
            Placement = new PlacementServices { Ground = (_, _) => 0, Footprint = _ => null, StaticAround = (_, _) => [] },
            Towns = () => [], PlayerFactionId = () => "player", DrawGhosts = _ => { },
        };
        return ui;
    }

    /// <summary>
    /// Arranges through the screen host so its retained selections are applied over the build bindings, as in the game.
    /// </summary>
    static IReadOnlyList<UiControl> Frame(PlayerInterface ui)
    {
        UiBindings Bind(string _)
        {
            var bindings = new UiBindings();
            ui.Build.BindBuildScreen(bindings);
            return bindings;
        }

        return Assert.Single(ui.FaithfulScreens.Arrange(800, 600, Bind)).Controls;
    }

    /// <summary>
    /// Selects a rendered list row through the host to populate its retained state as well as the build mode's state.
    /// </summary>
    static void Click(PlayerInterface ui, string row)
    {
        var bounds = Assert.Single(Frame(ui), c => c.Name == row).Bounds;
        Assert.True(ui.FaithfulScreens.PointerDown(new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)));
        ui.FaithfulScreens.PointerUp();
    }

    /// <summary>
    /// The category list stays available while the building list, preview and both highlights are empty.
    /// </summary>
    static void AssertUnselected(PlayerInterface ui)
    {
        var frame = Frame(ui);
        Assert.Contains(frame, c => c.Name == "CategoriesList@row0");
        Assert.DoesNotContain(frame, c => c.Name.StartsWith("BuildingsList@row", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, c => c.Properties.GetValueOrDefault("Checked") == "true");
        Assert.Equal("", Assert.Single(frame, c => c.Name == "BuildingName").Caption);
        Assert.Null(ui.Build.CurrentBuilding());
    }

    /// <summary>
    /// The given row is the only highlighted row in its list.
    /// </summary>
    static void AssertChecked(PlayerInterface ui, string row)
    {
        string prefix = row[..row.IndexOf('@')] + "@row";
        bool Checked(UiControl c) => c.Name.StartsWith(prefix, StringComparison.Ordinal) && c.Properties.GetValueOrDefault("Checked") == "true";

        var selected = Assert.Single(Frame(ui), Checked);
        Assert.Equal(row, selected.Name);
    }
}
