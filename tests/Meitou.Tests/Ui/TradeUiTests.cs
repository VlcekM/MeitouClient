using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.Ui;
using Meitou.Engine;
using Meitou.Engine.Input;
using Meitou.Game;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Ui;

/// <summary>The Faithful trade (docs/simulation.md "Trade"): the trader window opens with the trade, drags between it and the pockets buy and sell at the next tick, closing it ends the trade.</summary>
public class TradeUiTests
{
    sealed class StockSource : ITickSystem, Meitou.Simulation.Trading.ITradeSource
    {
        public Meitou.Simulation.Trading.TradeView? Trade { get; set; }
        public Meitou.Simulation.Trading.TradeView? PublishTrade() => Trade;
    }
    static Vector2 Centre(UiControl c) => new(c.Bounds.X + c.Bounds.Width / 2, c.Bounds.Y + c.Bounds.Height / 2);

    static void Drag(PlayerInterface ui, Vector2 from, Vector2 to)
    {
        ui.MouseDown(MouseButton.Left, from, true);   // Shift: the whole stack (without it one unit is lifted)
        ui.MouseMove(to);
        ui.MouseUp(MouseButton.Left, to, false);
    }

    [Fact, Slow]
    public void Scrolled_stock_keeps_its_cells_and_buys_the_visible_item()
    {
        const UiMode mode = UiMode.Faithful;
        var install = GameInstall.Locate(); Assert.SkipWhen(install is null, "No Kenshi install configured");
        var stock = new StockSource();
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0, systems: [new PlayerSystem(), stock]);
        var hero = session.World.Characters.Spawn(new(), new() { Name = "Hero", IsPlayer = true, Inventory = new Inventory() }, 0);
        session.World.Player.Faction = 0; session.World.Player.Selection.Add(hero);
        stock.Trade = new(hero, new CharacterId(1, 0), "Armour King", 1000, 1000, 20, 100,
            [new(0, "top", "Top", "main", 0, 0, 1, 1, 1, 1, 1, 1, false), new(1, "bottom", "Bottom", "main", 0, 99, 1, 1, 1, 1, 1, 1, false)], [], false);
        session.World.RunTick();
        var resources = OgreScriptResources.ForInstall(install!, LoadOrder.FromInstall(install!));
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { Width = 1600, Height = 900 };
        ui.LoadUi(mode, name => resources.Find(name));
        try
        {
            var before = ui.UiFrame.Single(c => c.Name == "backpack_content");
            Assert.True(before.Bounds.Height > before.Clip!.Value.Height);
            // The stock is a ScrollView: one wheel event moves a fixed 50 px whatever its size (docs/formats/mygui.md).
            for (int i = 0; i < 60; i++) Assert.True(ui.UiScroll(Centre(before with { Bounds = before.Clip.Value }), -1));
            var after = ui.UiFrame.Single(c => c.Name == "backpack_content");
            Assert.Equal(before.Bounds.Height, after.Bounds.Height);
            Assert.True(after.Bounds.Y < before.Bounds.Y);
            var bottom = ui.UiFrame.Single(c => c.Name == "trade-item-1");
            Assert.True(bottom.Clip!.Value.Contains(Centre(bottom).X, Centre(bottom).Y));
            ui.MouseMove(Centre(bottom));
            Assert.True(ui.UiFrame.Single(c => c.Name == "trade-item-1").Properties.ContainsKey("tooltip_item"));
            Drag(ui, Centre(bottom), Centre(ui.UiFrame.Single(c => c.Name == "main")));
            var sent = session.World.Commands.TakeDue(long.MaxValue);
            Assert.Equal("bottom", Assert.Single(sent.OfType<LiftItem>()).Item.Record);
            Assert.Equal(ItemHolder.Character, Assert.Single(sent.OfType<PlaceHeld>()).Into.Holder);
            Assert.DoesNotContain(ui.UiFrame, c => c.Name == "trade-item-1" && c.Caption.Contains("c.", StringComparison.Ordinal));
        }
        finally { ui.DisposeUi(); }
    }

    [Theory, Slow]
    [InlineData(UiMode.Faithful)]
    [InlineData(UiMode.Meitou)]
    public void Dragging_between_the_trader_window_and_the_pockets_buys_and_sells(UiMode mode)
    {
        var install = GameInstall.Locate(); Assert.SkipWhen(install is null, "No Kenshi install configured");
        var data = TradeTests.Data(TradeTests.Shops);
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 150, systems: [new PlayerSystem(), new TradeSystem(data)]);
        var world = session.World;
        CharacterId Spawn(string name, float x, int faction, bool player, string template)
        {
            var squad = new Squad { Id = world.Squads.NextId(), TemplateId = template, Faction = faction, Town = 0 };
            world.Squads.Add(squad);
            var id = world.Characters.Spawn(new CharacterHot { Position = new Vector3(x, 150, 0), Health = 100 },
                new CharacterCold { Name = name, Faction = faction, IsPlayer = player, Inventory = new Inventory(), SquadId = squad.Id }, 0);
            squad.Members.Add(id);
            return id;
        }
        var hero = Spawn("hero", 0, data.PlayerFaction, true, "p");
        var keeper = Spawn("keeper", 10, data.FactionIndex("f-t"), false, "shop-t");
        world.Player.Faction = data.PlayerFaction;
        world.Player.Money = 100_000;
        world.Player.Selection.Add(hero);
        world.RunTick();   // the shop opens on the first look at the squads
        world.Commands.Enqueue(new OpenTrade(hero, keeper) { Tick = world.Tick });
        world.RunTicks(2);

        var resources = OgreScriptResources.ForInstall(install!, LoadOrder.FromInstall(install!));
        var ui = new PlayerInterface(session, (_, _) => 0, c => c.Position) { Width = 1600, Height = 900 };
        ui.LoadUi(mode, name => resources.Find(name));
        try
        {
            // The trade opened both windows: the stock shows in the trader's.
            var stock = world.Snapshot.Trade!.Stock[0];
            var source = ui.UiFrame.Single(c => c.Name == $"trade-item-{stock.Index}");
            var pockets = ui.UiFrame.First(c => c.Name == "main").Bounds;   // the inventory window opened first, so its pocket grid comes first
            var stockGrid = ui.UiFrame.Single(c => c.Name == "backpack_content").Bounds;
            Assert.Equal(stockGrid.Width / world.Snapshot.Trade.StockWidth, stockGrid.Height / world.Snapshot.Trade.StockHeight, 3);
            Assert.True(stockGrid.X > pockets.X + pockets.Width);
            var cell = new Vector2(pockets.X + pockets.Width * 4.5f / 8, pockets.Y + pockets.Height * 3.5f / 10);
            Drag(ui, Centre(source), cell);
            Assert.Equal(100_000, world.Player.Money);   // nothing moves before the tick
            world.RunTick();
            Assert.Equal(100_000 - stock.Price, world.Player.Money);
            var bought = Assert.Single(world.Characters.Cold(hero.Slot)!.Inventory!.All());
            Assert.Equal(stock.Quantity, bought.Quantity);

            // Back onto the stock: sold at the sell price.
            world.RunTick();
            var goods = world.Snapshot.Trade!.Goods.Single();
            var own = ui.UiFrame.Single(c => c.Name == $"inventory-item-{hero}-{goods.Index}");
            Drag(ui, Centre(own), Centre(ui.UiFrame.First(c => c.Name.StartsWith("trade-item-", StringComparison.Ordinal))));
            world.RunTick();
            Assert.Equal(100_000 - stock.Price + goods.Price, world.Player.Money);
            Assert.Empty(world.Characters.Cold(hero.Slot)!.Inventory!.All());

            // Escape closes the top window, the trader's: the trade ends.
            ui.UiKey(Key.Escape);
            _ = ui.UiFrame;
            world.RunTicks(2);
            Assert.Null(world.Snapshot.Trade);
        }
        finally { ui.DisposeUi(); }
    }
}
