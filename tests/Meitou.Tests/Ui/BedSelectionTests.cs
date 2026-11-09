using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Ogre;
using Meitou.Data.Ui;
using Meitou.Data.World;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Game;
using Meitou.Game.Building;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Ui;

public class BedSelectionTests
{
    [Fact, Slow]
    public void Clicking_an_interior_bed_selects_its_own_condition_rental_and_healing_panel()
    {
        const UiMode mode = UiMode.Meitou;
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, "Kenshi install not found");
        var (original, house) = HousePurchaseTests.Setup();
        var bedRecord = new FcsRecord { StringId = "camp-bed", Name = "Camp Bed", RecordType = FcsRecordType.Building, Flags = 0x10 };
        bedRecord.Ints["build materials"] = 1;
        bedRecord.Ints["max operators"] = 1;
        bedRecord.Floats["output rate"] = 4;
        bedRecord.References["functionality"] = [new("bed-function", 0, 0, 0)];
        var function = new FcsRecord { StringId = "bed-function", RecordType = FcsRecordType.BuildingFunctionality, Flags = 0x10 };
        function.Ints["function"] = TownBuildings.BedFunction;
        var residents = new FcsRecord { StringId = "residents", RecordType = FcsRecordType.SquadTemplate, Flags = 0x10 };
        residents.Bools["public beds"] = true;
        residents.Ints["bed usage cost"] = 100;
        var file = new FcsFile();
        file.Records.AddRange([bedRecord, function, residents]);
        original.Db.Apply(file, "test.mod");
        house = house with { Name = "Bar", Spots = [new(HomeSpotKind.Bed, new(100, 0, 100), 0) { Record = "camp-bed" }] };
        var data = PopulationData.Create(original.Db, [new TownPlacement("town-instance", "town", house.Position, null)],
            buildings: new Dictionary<string, List<TownBuilding>> { ["town-instance"] = [house] });
        using var session = new WorldSession(Vector3.Zero, (-1000, -1000, 1000, 1000), (_, _) => 0,
            systems: [new PlayerSystem(), new BedSystem(data), new LockSystem(data.Bodies.Constants, data.BodyOptions)]);
        var world = session.World;
        world.Player.Faction = data.PlayerFaction;
        world.Fixtures.RegisterSite(world, data, 0);
        world.Squads.Add(new Squad { Id = 1, TemplateId = "residents", HomeBuilding = house });
        world.RunTick();
        var bed = Assert.Single(world.Snapshot.Beds);
        var resources = OgreScriptResources.ForInstall(install, LoadOrder.FromInstall(install));
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position)
        {
            Width = 1600, Height = 900, FactionName = _ => "Owner",
        };
        ui.BuildingSelection.BuildTarget = id => BuildingBuildData.From(data.Db.Find(id)!).Target;
        ui.LoadUi(mode, name => resources.Find(name));
        ui.Build.BuildHost = new BuildServices
        {
            Menu = new BuildMenu([]), ItemName = id => data.Db.Find(id)?.Name ?? id, Description = _ => "", Parts = _ => [],
            Placement = new PlacementServices { Ground = (_, _) => 0, Footprint = _ => null, StaticAround = (_, _) => [] },
            Towns = () => [], PlayerFactionId = () => "player", DrawGhosts = _ => { },
        };
        ui.Eye = new(100, 80, 0);
        ui.ViewProjection = Matrix4x4.CreateLookAt(ui.Eye, bed.Position + new Vector3(0, 0.5f, 0), Vector3.UnitY)
            * Meitou.Game.Rendering.Frame.DepthConvention.Perspective(1, 1600 / 900f, 0.1f, 1000);
        try
        {
            ui.BuildingSelection.SetBuilding(new("Bar", "House", "Owner", "Town", null, house.Key, house.Position, null, Record: house.BuildingId));
            world.RunTick();
            Assert.Null(world.Snapshot.InspectedBuilding!.Bed);
            var point = new Vector2(800, 450);
            ui.MouseDown(MouseButton.Left, point, false);
            ui.MouseUp(MouseButton.Left, point, false);
            world.RunTick();
            Assert.Equal(bed.Id, world.Player.InspectedBuilding);
            Assert.Empty(world.Snapshot.InspectedBuilding!.Doors);
            Assert.Equal(100, world.Snapshot.InspectedBuilding.Bed!.Cost);
            Assert.Contains(ui.UiFrame, c => c.Caption == "Camp Bed");
            var focus = new BuildingSelection.SelectedBuilding("Camp Bed", null, "Owner", null, null, bed.Id, bed.Position, null, Record: bed.Record);
            var lines = ui.BuildingSelection.BuildingLines(focus);
            Assert.Contains(lines.State, l => l.Label == "Town:");
            Assert.Contains(lines.State, l => l.Label == "Cost to use:" && l.Value == "(Pay c.100)");
            Assert.Contains(lines.Stats, l => l.Label == "Healing bonus:" && l.Value == "x4");
            Assert.DoesNotContain(lines.Stats, l => l.Label is "Open/Close:" or "Security:" or "Open to public:");
            var condition = Assert.Single(ui.BuildingSelection.BuildingRows(focus));
            Assert.Equal("Condition", condition.Name);
            Assert.Equal("1", condition.Value);
            Assert.Equal(1, condition.Yellow);
            // Changing access rules must update the selected panel rather than caching the original price.
            data.Relations.Set(data.FactionIndex("owner"), data.PlayerFaction, 100);
            world.RunTick();
            Assert.Equal(0, world.Snapshot.InspectedBuilding!.Bed!.Cost);
            Assert.DoesNotContain(ui.BuildingSelection.BuildingLines(focus).State, l => l.Label == "Cost to use:");
            data.Relations.Set(data.FactionIndex("owner"), data.PlayerFaction, 0);
            world.Squads.Remove(1);
            world.RunTick();
            Assert.Equal(-1, world.Snapshot.InspectedBuilding!.Bed!.Cost);
            Assert.Contains(ui.BuildingSelection.BuildingLines(focus).State, l => l.Label == "Cost to use:" && l.Value == "Private");
        }
        finally { ui.DisposeUi(); }
    }
}
