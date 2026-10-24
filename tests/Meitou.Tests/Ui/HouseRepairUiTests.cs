using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.Ui;
using Meitou.Engine;
using Meitou.Engine.Input;
using Meitou.Game;
using Meitou.Game.Building;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Ui;

public class HouseRepairUiTests
{
    [Theory, Slow]
    [InlineData(UiMode.Faithful, false)]
    [InlineData(UiMode.Meitou, true)]
    public void Right_hold_on_a_purchased_ruin_opens_Repair_and_shift_adds_an_engineer_job(UiMode mode, bool shift)
    {
        var install = GameInstall.Locate(); Assert.SkipWhen(install is null, "No Kenshi install configured");
        var (data, house) = HousePurchaseTests.Setup(destroyed: true);
        using var session = new WorldSession(Vector3.Zero, (-1000, -1000, 1000, 1000), (_, _) => 0,
            systems: [new PlayerSystem(), new LockSystem(data.Bodies.Constants, data.BodyOptions)]);
        var world = session.World;
        world.Player.Faction = data.PlayerFaction; world.Player.Money = 10000;
        world.Fixtures.RegisterSite(world, data, 0);
        world.Commands.Enqueue(new BuyBuilding(house.Key, 4800) { Tick = world.Tick }); world.RunTick();
        var hero = world.Characters.Spawn(new CharacterHot { Position = new(50, 0, 100), Health = 100 },
            new CharacterCold { Name = "Builder", IsPlayer = true, Faction = data.PlayerFaction, Inventory = new Inventory() }, 0);
        world.Player.Selection.Add(hero); world.RunTick();
        var resources = OgreScriptResources.ForInstall(install, LoadOrder.FromInstall(install));
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { Width = 1600, Height = 900, SlowModifier = shift };
        ui.LoadUi(mode, name => resources.Find(name));
        ui.BuildHost = new BuildServices
        {
            Menu = new BuildMenu([]), ItemName = id => id, Description = _ => "", Parts = _ => [],
            Placement = new PlacementServices { Ground = (_, _) => 0, Footprint = _ => null, StaticAround = (_, _) => [] },
            Towns = () => [], PlayerFactionId = () => "player", DrawGhosts = _ => { },
        };
        ui.Eye = new(100, 80, 0);
        ui.ViewProjection = Matrix4x4.CreateLookAt(ui.Eye, house.Position + new Vector3(0, 8, 0), Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(1, 1600 / 900f, 0.1f, 1000);
        try
        {
            var point = new Vector2(800, 450);
            ui.MouseDown(MouseButton.Right, point, shift);
            Thread.Sleep(TimeSpan.FromSeconds(PlayerInterface.ContextHoldSeconds + 0.05));
            ui.Steer();
            Assert.True(ui.ContextMenuOpen);
            var repair = Assert.Single(ui.UiFrame, c => c.Name.StartsWith("context-verb-", StringComparison.Ordinal) && c.Caption == "Repair");
            Assert.DoesNotContain(ui.UiFrame, c => c.Name.StartsWith("context-verb-", StringComparison.Ordinal) && c.Caption == "Dismantle");
            point = new Vector2(repair.Bounds.X + repair.Bounds.Width / 2, repair.Bounds.Y + repair.Bounds.Height / 2);
            ui.MouseDown(MouseButton.Left, point, shift); ui.MouseUp(MouseButton.Left, point, shift);
            Assert.False(ui.ContextMenuOpen);
            var commands = world.Commands.TakeDue(long.MaxValue);
            Assert.Equal(house.Key, Assert.Single(commands.OfType<BuildOrder>()).Site);
            Assert.Equal(shift ? 1 : 0, commands.OfType<AddJob>().Count());
        }
        finally { ui.DisposeUi(); }
    }
}
