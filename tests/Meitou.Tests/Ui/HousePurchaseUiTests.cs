using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Ogre;
using Meitou.Data.Ui;
using Meitou.Engine;
using Meitou.Engine.Input;
using Meitou.Game;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Locks;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Ui;

public class HousePurchaseUiTests
{
    [Theory, Slow]
    [InlineData(UiMode.Faithful)]
    [InlineData(UiMode.Meitou)]
    public void Sale_price_opens_confirmation_and_purchase_updates_the_panel(UiMode mode)
    {
        var install = GameInstall.Locate(); Assert.SkipWhen(install is null, "No Kenshi install configured");
        var chinese = Meitou.Localisation.GettextCatalog.Load(GameFiles.Resolve(Path.Combine(install.Root, "locale/zh_CN/LC_MESSAGES")));
        Assert.Equal("待售：", chinese.Find("For sale:"));
        Assert.False(string.IsNullOrEmpty(chinese.Find("Do you want to buy this building for c.{1,num}?")));
        var (data, house) = HousePurchaseTests.Setup(destroyed: true);
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0,
            systems: [new PlayerSystem(), new LockSystem(GameConstants.FromDatabase(data.Db), BodyOptions.Default)]);
        var world = session.World;
        world.Player.Faction = data.PlayerFaction; world.Player.Money = 10000;
        world.Fixtures.RegisterSite(world, data, 0);
        var resources = OgreScriptResources.ForInstall(install, LoadOrder.FromInstall(install));
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { Width = 1600, Height = 900, FactionName = _ => "Nameless" };
        ui.LoadUi(mode, name => resources.Find(name));
        ui.ConfigureFrontend(data.Db, () => [], "");
        try
        {
            var focus = new PlayerInterface.SelectedBuilding("House", null, "Owner", "Town", null, house.Key, house.Position, null, Record: house.BuildingId);
            ui.SetBuilding(focus); world.RunTick();
            var sale = Assert.Single(ui.BuildingLines(focus).Stats, line => line.Action == "meitou:building-buy");
            Assert.Equal("For sale:", sale.Label); Assert.Equal("c.4,800", sale.Value); Assert.Equal(0.4f, sale.ButtonWidth);
            void Click(string name)
            {
                var button = ui.UiFrame.Last(c => c.Name.EndsWith(name, StringComparison.Ordinal));
                var point = new Vector2(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2);
                ui.MouseDown(MouseButton.Left, point, false); ui.MouseUp(MouseButton.Left, point, false);
            }
            Click(ui.UiFrame.Single(c => c.Action == "meitou:building-buy").Name);
            Assert.Contains(ui.UiFrame, c => c.Name.StartsWith("Root@Caption", StringComparison.Ordinal) && c.Caption == "House");
            Assert.Contains(ui.UiFrame, c => c.Name == "ButtonA" && c.Caption == "Yes");
            Assert.Contains(ui.UiFrame, c => c.Name == "ButtonB" && c.Caption == "No");
            Assert.Contains(ui.UiFrame, c => c.Caption == "Do you want to buy this building for c.4,800?");
            Click("ButtonB"); world.RunTick(); Assert.Equal(10000, world.Player.Money);
            Click(ui.UiFrame.Single(c => c.Action == "meitou:building-buy").Name); Click("ButtonA"); world.RunTick();
            Assert.Equal(5200, world.Player.Money);
            var lines = ui.BuildingLines(focus);
            Assert.Equal("Nameless", lines.Name.Value);
            Assert.DoesNotContain(lines.Stats, line => line.Action == "meitou:building-buy");
            Assert.Contains(lines.Stats, line => line.Action == "meitou:building-public");
        }
        finally { ui.DisposeUi(); }
    }
}
