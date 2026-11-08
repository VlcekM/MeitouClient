using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;

namespace Meitou.Game;

/// <summary>
/// Trading (docs/simulation.md "Trade", docs/game/ui-screens.md section 6), shown while the world publishes a trade (<see cref="TradeView"/>), the same in both UI modes:
/// <list type="bullet">
/// <item>The trader's stock is the original's trader inventory window (<c>Kenshi_InventoryTraderWindow</c>, loaded by <c>FUN_140155ee0</c>) beside the character's inventory window, both at the
/// top left of the screen (<c>FUN_1406e4960</c>). The window is the layout's grown by what the stock grid needs beyond the layout's grid, as a square (<c>FUN_140150570</c>); the grid scrolls
/// in the layout's scroll view.</item>
/// <item>Items are held with the mouse between the grids (PlayerInterface.Inventory.cs): stock placed in the customer's windows is bought there, the customer's items placed on the stock are
/// sold. Closing the trader window ends the trade. Prices are in the item tooltips (Value, Sell value); a stolen item held over the stock shows the fencing prompt.</item>
/// </list>
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The trader whose window the interface opened (none: no trade shown) and whether its closing was already sent.</summary>
    CharacterId shownTrader = CharacterId.None;
    bool tradeCloseSent;

    /// <summary>Opens the trader beside the inventory when a trade opens, and ends the trade when the player closes its window.</summary>
    void SyncTradeScreens()
    {
        if (faithfulScreens is null) return;
        var trade = session.CurrentSnapshot.Trade;
        if (trade is null)
        {
            if (!shownTrader.IsNone && faithfulScreens.IsOpen("trader")) faithfulScreens.Close("trader");
            shownTrader = CharacterId.None;
            tradeCloseSent = false;
            return;
        }
        if (trade.Trader != shownTrader)
        {
            shownTrader = trade.Trader;
            tradeCloseSent = false;
            faithfulScreens.ResetScroll("trader");
            if (!faithfulScreens.IsOpen("inventory")) faithfulScreens.Open("inventory");
            faithfulScreens.Open("trader");
            PlaceTradeWindows();
            return;
        }
        if (!faithfulScreens.IsOpen("trader") && !tradeCloseSent)
        {
            Say(new CloseTrade { Tick = session.World.Tick });
            tradeCloseSent = true;
        }
    }

    /// <summary>
    /// The trade's windows side by side from the top left (<c>FUN_1406e4960</c>, <b>Observed</b> by decompile and the reference screenshot): a margin of 5 % of the smaller screen side from
    /// the top and the left, then the bag (when open), the character and the trader, each starting where the previous one ends.
    /// </summary>
    void PlaceTradeWindows()
    {
        var frames = faithfulScreens.Arrange(Width, Height, BindFaithfulScreen, AddFaithfulContent);
        UiRect? Root(string screen) => frames.FirstOrDefault(f => f.Id == screen)?.Controls.FirstOrDefault(c => c.Type == "Window")?.Bounds;
        float margin = (int)(Math.Min(Width, Height) * 0.05f), x = margin;
        void Put(string id)
        {
            if (!faithfulScreens.IsOpen(id) || Root(id) is not { } r) return;
            faithfulScreens.SetOffset(id, faithfulScreens.GetOffset(id) + new Vector2(x - r.X, margin - r.Y));
            x += r.Width;
        }
        Put("bag");
        Put("inventory");
        Put("trader");
    }

    /// <summary>The grid widget of an inventory window: the bag's contents grid, a pocket grid, else the largest widget drawn with the inventory slot skin.</summary>
    static string? GridIn(IReadOnlyList<UiControl> controls) =>
        controls.FirstOrDefault(c => c.Name == "backpack_content")?.Name ?? controls.FirstOrDefault(c => c.Name == "main")?.Name
        ?? controls.Where(c => c.Skin.Contains("InventorySlot", StringComparison.Ordinal)).OrderByDescending(c => c.Bounds.Width * c.Bounds.Height).FirstOrDefault()?.Name;

    string? TraderGrid(string screen) => GridIn(faithfulScreens.Arrange(Width, Height, BindFaithfulScreen, AddFaithfulContent).First(f => f.Id == screen).Controls);

    /// <summary>The trader's name in the caption, its ARRANGE button; the money line is added after arranging.</summary>
    void BindTraderScreen(UiBindings b)
    {
        var trade = session.CurrentSnapshot.Trade;
        b.Text["Root"] = trade?.TraderName.ToUpperInvariant() ?? "TRADER";
        b.Properties["datapanel"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
        b.Disabled.Remove("ArrangeButton");
        b.Actions["ArrangeButton"] = "ArrangeButton";
        if (trade is null) b.Disabled.Add("ArrangeButton");
    }

    /// <summary>
    /// How much the trader window grows (<c>FUN_140150570</c>, <b>Verified</b> by decompile): the stock grid is first sized as many rows as columns, at the slot skin's cell, and the window and
    /// the scroll view grow by that size less the layout's grid size; the grid then takes its real rows as the scroll view's canvas.
    /// </summary>
    (float Dx, float Dy) TraderGrowth(int columns)
    {
        var layout = faithfulScreens.Layout("trader");
        if (layout.Find("Root") is not { } root || layout.Find("Content") is not { } content || layout.Find("backpack_content") is not { } grid) return (0, 0);
        var r = root.Position.RelativeTo(new(0, 0, Width, Height), root.RealPosition);
        float side = columns * InventoryCell;
        return (side - grid.Position.Width * content.Position.Width * r.Width, side - grid.Position.Height * content.Position.Height * r.Height);
    }

    void BindTraderGeometry(UiBindings b)
    {
        var layout = faithfulScreens.Layout("trader");
        if (session.CurrentSnapshot.Trade is { } trade && layout.Find("Root") is { } root)
        {
            var r = root.Position.RelativeTo(new(0, 0, Width, Height), root.RealPosition);
            int columns = Math.Max(1, trade.StockWidth), rows = Math.Max(1, trade.StockHeight);
            var (dx, dy) = TraderGrowth(columns);
            b.Positions["Root"] = new(0, 0, r.Width + dx, r.Height + dy);
            // The scroll view grows with the window (its frame in fractions of the content area) and scrolls the stock grid as its canvas: the host's generic ScrollView draws the template's bars.
            if (layout.Find("Content") is { } content && layout.Find("scrollview_backpack_content") is { } view)
            {
                float contentW = content.Position.Width * (r.Width + dx), contentH = content.Position.Height * (r.Height + dy), w0 = content.Position.Width * r.Width, h0 = content.Position.Height * r.Height;
                b.RelativePositions[view] = new(view.Position.X * w0 / contentW, view.Position.Y * h0 / contentH, (view.Position.Width * w0 + dx) / contentW, (view.Position.Height * h0 + dy) / contentH);
                b.Canvas[view.Name] = (columns * InventoryCell, rows * InventoryCell);
            }
        }
    }

    /// <summary>
    /// Places the trader window's parts after the growth: the money box keeps its size at the top left, ARRANGE stays centred at the bottom. The scroll view grows with the window (<see cref="BindTraderGeometry"/>)
    /// and the stock grid is its canvas: it sits where the generic ScrollView (<c>Kenshi_ScrollView</c> template: client, bar, offset) put the canvas, clipped to the client.
    /// </summary>
    void ArrangeTraderWindow(List<UiControl> controls)
    {
        if (session.CurrentSnapshot.Trade is not { } trade) return;
        var layout = faithfulScreens.Layout("trader");
        int contentIndex = controls.FindIndex(c => c.Name == "Content"), viewIndex = controls.FindIndex(c => c.Name == "scrollview_backpack_content");
        if (contentIndex < 0 || viewIndex < 0 || layout.Find("Content") is not { } contentWidget) return;
        int columns = Math.Max(1, trade.StockWidth), rows = Math.Max(1, trade.StockHeight);
        var (dx, dy) = TraderGrowth(columns);
        float cell = InventoryCell;
        var c = controls[contentIndex].Bounds;
        float w0 = c.Width - contentWidget.Position.Width * dx, h0 = c.Height - contentWidget.Position.Height * dy;
        UiRect Fixed(MyGuiWidget w, UiRect parent, float pw, float ph) => new(parent.X + w.Position.X * pw, parent.Y + w.Position.Y * ph, w.Position.Width * pw, w.Position.Height * ph);
        void Move(string name, UiRect bounds) { int i = controls.FindIndex(x => x.Name == name); if (i >= 0) controls[i] = controls[i] with { Bounds = bounds }; }
        if (layout.Find("datapanel") is { } panel && contentWidget.Children.FirstOrDefault(w => w.Children.Contains(panel)) is { } box)
        {
            var boxBounds = Fixed(box, c, w0, h0);
            int boxIndex = controls.FindIndex(x => x.Skin == box.Skin && x.Name == box.Name);
            if (boxIndex >= 0) controls[boxIndex] = controls[boxIndex] with { Bounds = boxBounds };
            Move("datapanel", panel.Position.RelativeTo(boxBounds, true));
        }
        if (layout.Find("ArrangeButton") is { } arrange)
        {
            float bw = arrange.Position.Width * w0, bh = arrange.Position.Height * h0;
            Move("ArrangeButton", new(c.X + (c.Width - bw) / 2, c.Y + c.Height - (1 - arrange.Position.Y) * h0, bw, bh));
        }

        // The scroll view (the generic ScrollView, bars and offset included) has the canvas where it put it; the grid is that canvas, so it draws over the view's frame, under its bars.
        var scrollView = controls[viewIndex];
        var client = controls.FirstOrDefault(x => x.Properties.GetValueOrDefault("TemplateOwner") == scrollView.Name && x.Properties.GetValueOrDefault("TemplatePart") == "Client")?.Bounds ?? scrollView.Bounds;
        int gridIndex = controls.FindIndex(x => x.Name == "backpack_content");
        if (gridIndex < 0) return;
        var slot = controls[gridIndex];
        controls.RemoveAt(gridIndex);
        viewIndex = controls.FindIndex(x => x.Name == "scrollview_backpack_content");
        controls.Insert(viewIndex + 1, slot with
        {
            Bounds = new(MyGuiPresentation.Num(scrollView.Properties, "CanvasX", client.X), MyGuiPresentation.Num(scrollView.Properties, "CanvasY", client.Y), columns * cell, rows * cell),
            Clip = client.Intersect(slot.Clip ?? client),
            Properties = new Dictionary<string, string>(slot.Properties) { ["TileSize"] = FormattableString.Invariant($"{cell} {cell}") },
        });
    }

    /// <summary>The money line and the stock in the trader window's grid.</summary>
    void AddTraderContent(List<UiControl> controls)
    {
        if (session.CurrentSnapshot.Trade is not { } trade || GridIn(controls) is not { } gridName) return;
        AddLines(controls, "datapanel", [new("Money:", "c." + Money(trade.ShopMoney))], 20 * InventoryScale);
        var slot = controls.First(c => c.Name == gridName);
        int columns = Math.Max(1, trade.StockWidth), rows = Math.Max(1, trade.StockHeight);
        var area = slot.Bounds;
        // A building shop's cells outside every container hold nothing: greyed over the grid's tiles.
        foreach (var (r, k) in trade.Unused.Select((r, k) => (r, k)))
            controls.Add(new($"trade-unused-{k}", "Widget",
                new(area.X + r.X * area.Width / columns, area.Y + r.Y * area.Height / rows, r.Width * area.Width / columns, r.Height * area.Height / rows),
                "", "", "", true, new Dictionary<string, string>(), new Vector4(0, 0, 0, 0.65f), Pickable: false, Clip: slot.Clip));
        foreach (var item in trade.Stock)
        {
            if (Lifted(ItemOwner.Stock, item.Index)) continue;
            AddItemCell(controls, $"trade-item-{item.Index}",
                new(area.X + item.X * area.Width / columns, area.Y + item.Y * area.Height / rows, item.Width * area.Width / columns, item.Height * area.Height / rows),
                item.Record, item.Material, item.Name, item.Quantity, ItemTip.Of(item), "TradeItem", item.Index, slot.Clip, charge: item.Charge, colour: item.Colour);
        }
    }

    /// <summary>
    /// While trading, the trade item behind an inventory item of a seller's (<see cref="TradeView.GoodsOf"/>): its tooltip gains the prices (Value, Sell value; <c>FUN_14079e7a0</c>,
    /// <b>Observed</b> by decompile, see <see cref="ItemLines"/>). Null: no trade, or a character who cannot take the trade.
    /// </summary>
    TradeItemView? SellTip(CharacterId owner, int index) =>
        session.CurrentSnapshot.Trade is { } trade && trade.GoodsOf.TryGetValue(owner, out var goods) ? goods.FirstOrDefault(g => g.Index == index) : null;

    /// <summary>
    /// The fencing prompt beside a seller's item (<see cref="TradeView.GoodsOf"/>) held over the stock (<c>FUN_140712920</c>, <b>Observed</b> by decompile). An illegal good first:
    /// "ILLEGAL GOODS!" and "NO!" in the Bad colour, or for a trader that buys illegal goods "Illegal goods?" and "No problem!" in the Good colour. Else a stolen one: stolen from the trader's own faction, "Fencing chance:" and the
    /// chance in the Bad colour; stolen from anyone else and the trader buys stolen goods, "Stolen goods?" and "No problem!" in the Good colour; otherwise nothing.
    /// </summary>
    void AddFencePrompt(List<UiControl> controls)
    {
        if (held is not { } item || session.CurrentSnapshot.Trade is not { } trade || item.From.Holder != ItemHolder.Character
            || faithfulScreens.HitTest(mouse) is not { Screen: "trader" } || trade.Held is not { } goods) return;
        (string Label, string Value, string Colour)? prompt = goods.Illegal ? (trade.BuysIllegal ? ("Illegal goods?", "No problem!", GoodColour) : ("ILLEGAL GOODS!", "NO!", BadColour))
            : !goods.Stolen ? null
            : goods.FenceChance is { } chance ? ("Fencing chance:", $"{(int)(chance * 100)}%", BadColour)
            : trade.BuysStolen ? ("Stolen goods?", "No problem!", GoodColour) : null;
        if (prompt is not { } p) return;
        float scale = InventoryScale, width = 240 * scale, height = 34 * scale;
        var box = new UiRect(Math.Clamp(mouse.X + 18 * scale, 0, Width - width), Math.Clamp(mouse.Y - height - 8 * scale, 0, Height - height), width, height);
        controls.Add(new("fence-prompt", "Widget", box, "", "", "Kenshi_GenericTextBoxSkin", true, new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
        AddLines(controls, "fence-prompt", [new(p.Label, p.Value, p.Colour, p.Colour)], 20 * scale);
        int last = controls.Count;
        for (int i = last - 2; i < last; i++)
            if (i >= 0 && controls[i].Name.StartsWith("fence-prompt-", StringComparison.Ordinal))
                controls[i] = controls[i] with { Bounds = controls[i].Bounds with { Y = controls[i].Bounds.Y + 7 * scale } };
    }

    /// <summary>
    /// Sends a sale. A stolen item that a shop which is no fence may catch first asks (<c>FUN_140715290</c>, <b>Observed</b> by decompile): "You have a {N}% chance of getting caught selling this
    /// stolen item.\nFence it anyway?" under the title "Fencing Item", N = 100 less the chance of getting away in per cent, rounded half up; only Yes sells.
    /// </summary>
    void Sell(TradeView trade, CharacterId seller, InventoryItemToken token, int quantity, int? x, int y)
    {
        var sale = new SellItem(token, quantity) { Tick = session.World.Tick, X = x, Y = y, From = seller };
        if (!trade.GoodsOf.TryGetValue(seller, out var goods) || goods.FirstOrDefault(g => g.Index == token.Index) is not { FenceOdds: { } odds })
        {
            Say(sale);
            return;
        }
        Confirm("FENCING ITEM", FenceQuestion(odds), () => Say(sale with { Tick = session.World.Tick }));
        UpdateMenuPause();
    }

    /// <summary>The fence confirmation in the player's language, for a sale with <paramref name="odds"/> of getting away (the game's msgid).</summary>
    static string FenceQuestion(float odds)
    {
        int caught = 100 - (int)MathF.Floor(odds * 100 + 0.5f);
        return Loc.Tr("You have a {1}% chance of getting caught selling this stolen item.\nFence it anyway?", caught);
    }

    static string Money(long cats) => cats.ToString("N0", CultureInfo.InvariantCulture);
}
