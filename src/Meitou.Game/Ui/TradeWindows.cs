using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;

namespace Meitou.Game.Ui;

/// <summary>
/// Trading (docs/simulation.md "Trade", docs/game/ui-screens.md section 6), shown while the world publishes a trade (<see cref="TradeView"/>), the
/// same in both UI modes:
/// <list type="bullet">
/// <item>The trader's stock is the original's trader inventory window (<c>Kenshi_InventoryTraderWindow</c>, loaded by <c>FUN_140155ee0</c>) beside
/// the character's inventory window, both at the top left of the screen (<c>FUN_1406e4960</c>). The window is the layout's grown by what the stock
/// grid needs beyond the layout's grid, as a square (<c>FUN_140150570</c>); the grid scrolls in the layout's scroll view.</item>
/// <item>Items are held with the mouse between the grids (InventoryWindows): stock placed in the customer's windows is bought there, the customer's
/// items placed on the stock are sold. Closing the trader window ends the trade. Prices are in the item tooltips (Value, Sell value); a stolen item
/// held over the stock shows the fencing prompt.</item>
/// </list>
/// </summary>
sealed class TradeWindows(PlayerInterface player)
{
    #region Trader

    /// <summary>
    /// The trader whose window the interface opened (none: no trade shown) and whether its closing was already sent.
    /// </summary>
    internal CharacterId ShownTrader = CharacterId.None;

    /// <summary>
    /// The shown trade's closing was already sent.
    /// </summary>
    bool _tradeCloseSent;

    /// <summary>
    /// Opens the trader beside the inventory when a trade opens, and ends the trade when the player closes its window.
    /// </summary>
    internal void SyncTradeScreens()
    {
        if (player.FaithfulScreens is null) return;

        var trade = player.Session.CurrentSnapshot.Trade;

        if (trade is null)
        {
            if (!ShownTrader.IsNone && player.FaithfulScreens.IsOpen("trader")) player.FaithfulScreens.Close("trader");

            ShownTrader = CharacterId.None;
            _tradeCloseSent = false;
            return;
        }

        if (trade.Trader != ShownTrader)
        {
            ShownTrader = trade.Trader;
            _tradeCloseSent = false;
            player.FaithfulScreens.ResetScroll("trader");
            if (!player.FaithfulScreens.IsOpen("inventory")) player.FaithfulScreens.Open("inventory");

            player.FaithfulScreens.Open("trader");
            PlaceTradeWindows();
            return;
        }

        if (!player.FaithfulScreens.IsOpen("trader") && !_tradeCloseSent)
        {
            player.Say(new CloseTrade { Tick = player.Session.World.Tick });
            _tradeCloseSent = true;
        }
    }

    /// <summary>
    /// The trade's windows side by side from the top left (<c>FUN_1406e4960</c>, <b>Observed</b> by decompile and the reference screenshot): a margin
    /// of 5 % of the smaller screen side from the top and the left, then the bag (when open), the character and the trader, each starting where the
    /// previous one ends.
    /// </summary>
    internal void PlaceTradeWindows()
    {
        var frames = player.FaithfulScreens.Arrange(player.Width, player.Height, player.ScreenRouter.BindFaithfulScreen,
            player.ScreenRouter.AddFaithfulContent);

        UiRect? Root(string screen) => frames.FirstOrDefault(f => f.Id == screen)?.Controls.FirstOrDefault(c => c.Type == "Window")?.Bounds;

        float margin = (int)(Math.Min(player.Width, player.Height) * 0.05f), x = margin;

        void Put(string id)
        {
            if (!player.FaithfulScreens.IsOpen(id) || Root(id) is not { } r) return;

            player.FaithfulScreens.SetOffset(id, player.FaithfulScreens.GetOffset(id) + new Vector2(x - r.X, margin - r.Y));
            x += r.Width;
        }

        Put("bag");
        Put("inventory");
        Put("trader");
    }

    /// <summary>
    /// The grid widget of an inventory window: the bag's contents grid, a pocket grid, else the largest widget drawn with the inventory slot skin.
    /// </summary>
    static string? GridIn(IReadOnlyList<UiControl> controls) =>
        controls.FirstOrDefault(c => c.Name == "backpack_content")?.Name ?? controls.FirstOrDefault(c => c.Name == "main")?.Name
        ?? controls
            .Where(c => c.Skin.Contains("InventorySlot", StringComparison.Ordinal))
            .OrderByDescending(c => c.Bounds.Width * c.Bounds.Height)
            .FirstOrDefault()?.Name;

    /// <summary>
    /// The trader window's item grid widget, from an arrangement of the open screens.
    /// </summary>
    string? TraderGrid(string screen)
    {
        var frames = player.FaithfulScreens.Arrange(player.Width, player.Height, player.ScreenRouter.BindFaithfulScreen,
            player.ScreenRouter.AddFaithfulContent);
        return GridIn(frames.First(f => f.Id == screen).Controls);
    }

    /// <summary>
    /// The trader's name in the caption, its ARRANGE button; the money line is added after arranging.
    /// </summary>
    internal void BindTraderScreen(UiBindings b)
    {
        var trade = player.Session.CurrentSnapshot.Trade;
        b.Text["Root"] = trade?.TraderName.ToUpperInvariant() ?? "TRADER";
        b.Properties["datapanel"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
        b.Disabled.Remove("ArrangeButton");
        b.Actions["ArrangeButton"] = "ArrangeButton";
        if (trade is null) b.Disabled.Add("ArrangeButton");
    }

    /// <summary>
    /// How much the trader window grows (<c>FUN_140150570</c>, <b>Verified</b> by decompile): the stock grid is first sized as many rows as columns,
    /// at the slot skin's cell, and the window and the scroll view grow by that size less the layout's grid size; the grid then takes its real rows
    /// as the scroll view's canvas.
    /// </summary>
    (float Dx, float Dy) TraderGrowth(int columns)
    {
        var layout = player.FaithfulScreens.Layout("trader");
        if (layout.Find("Root") is not { } root || layout.Find("Content") is not { } content || layout.Find("backpack_content") is not { } grid)
            return (0, 0);

        var r = root.Position.RelativeTo(new(0, 0, player.Width, player.Height), root.RealPosition);
        float side = columns * player.InventoryCell;
        return (side - grid.Position.Width * content.Position.Width * r.Width, side - grid.Position.Height * content.Position.Height * r.Height);
    }

    /// <summary>
    /// The trader window's geometry while a trade is shown.
    /// </summary>
    internal void BindTraderGeometry(UiBindings b)
    {
        var layout = player.FaithfulScreens.Layout("trader");

        if (player.Session.CurrentSnapshot.Trade is { } trade && layout.Find("Root") is { } root)
        {
            var r = root.Position.RelativeTo(new(0, 0, player.Width, player.Height), root.RealPosition);
            int columns = Math.Max(1, trade.StockWidth), rows = Math.Max(1, trade.StockHeight);
            var (dx, dy) = TraderGrowth(columns);
            b.Positions["Root"] = new(0, 0, r.Width + dx, r.Height + dy);

            // The scroll view grows with the window (its frame in fractions of the content area) and scrolls the stock grid as its canvas: the host's
            // generic ScrollView draws the template's bars.
            if (layout.Find("Content") is { } content && layout.Find("scrollview_backpack_content") is { } view)
            {
                float contentW = content.Position.Width * (r.Width + dx), contentH = content.Position.Height * (r.Height + dy);
                float w0 = content.Position.Width * r.Width, h0 = content.Position.Height * r.Height;
                b.RelativePositions[view] = new(view.Position.X * w0 / contentW, view.Position.Y * h0 / contentH,
                    (view.Position.Width * w0 + dx) / contentW, (view.Position.Height * h0 + dy) / contentH);
                b.Canvas[view.Name] = (columns * player.InventoryCell, rows * player.InventoryCell);
            }
        }
    }

    /// <summary>
    /// Places the trader window's parts after the growth: the money box keeps its size at the top left, ARRANGE stays centred at the bottom. The
    /// scroll view grows with the window (<see cref="BindTraderGeometry"/>) and the stock grid is its canvas: it sits where the generic ScrollView
    /// (<c>Kenshi_ScrollView</c> template: client, bar, offset) put the canvas, clipped to the client.
    /// </summary>
    internal void ArrangeTraderWindow(List<UiControl> controls)
    {
        if (player.Session.CurrentSnapshot.Trade is not { } trade) return;

        var layout = player.FaithfulScreens.Layout("trader");
        int contentIndex = controls.FindIndex(c => c.Name == "Content"), viewIndex = controls.FindIndex(c => c.Name == "scrollview_backpack_content");
        if (contentIndex < 0 || viewIndex < 0 || layout.Find("Content") is not { } contentWidget) return;

        int columns = Math.Max(1, trade.StockWidth), rows = Math.Max(1, trade.StockHeight);
        var (dx, dy) = TraderGrowth(columns);
        float cell = player.InventoryCell;
        var c = controls[contentIndex].Bounds;
        float w0 = c.Width - contentWidget.Position.Width * dx, h0 = c.Height - contentWidget.Position.Height * dy;

        UiRect Fixed(MyGuiWidget w, UiRect parent, float pw, float ph) =>
            new(parent.X + w.Position.X * pw, parent.Y + w.Position.Y * ph, w.Position.Width * pw, w.Position.Height * ph);

        void Move(string name, UiRect bounds)
        {
            int i = controls.FindIndex(x => x.Name == name);
            if (i >= 0) controls[i] = controls[i] with { Bounds = bounds };
        }

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

        // The scroll view (the generic ScrollView, bars and offset included) has the canvas where it put it; the grid is that canvas, so it draws
        // over the view's frame, under its bars.
        var scrollView = controls[viewIndex];
        var client = controls.FirstOrDefault(x => IsTemplateClient(x, scrollView.Name))?.Bounds ?? scrollView.Bounds;
        int gridIndex = controls.FindIndex(x => x.Name == "backpack_content");
        if (gridIndex < 0) return;

        var slot = controls[gridIndex];
        controls.RemoveAt(gridIndex);
        viewIndex = controls.FindIndex(x => x.Name == "scrollview_backpack_content");
        controls.Insert(viewIndex + 1, slot with
        {
            Bounds = new(MyGuiPresentation.Num(scrollView.Properties, "CanvasX", client.X),
                MyGuiPresentation.Num(scrollView.Properties, "CanvasY", client.Y), columns * cell, rows * cell),
            Clip = client.Intersect(slot.Clip ?? client),
            Properties = new Dictionary<string, string>(slot.Properties) { ["TileSize"] = FormattableString.Invariant($"{cell} {cell}") },
        });
    }

    /// <summary>
    /// The Client part of a scroll view's template (the area its canvas shows).
    /// </summary>
    static bool IsTemplateClient(UiControl c, string owner) =>
        c.Properties.GetValueOrDefault("TemplateOwner") == owner && c.Properties.GetValueOrDefault("TemplatePart") == "Client";

    /// <summary>
    /// The money line and the stock in the trader window's grid.
    /// </summary>
    internal void AddTraderContent(List<UiControl> controls)
    {
        if (player.Session.CurrentSnapshot.Trade is not { } trade || GridIn(controls) is not { } gridName) return;

        player.AddLines(controls, "datapanel", [new("Money:", "c." + Money(trade.ShopMoney))], 20 * player.InventoryScale);
        var slot = controls.First(c => c.Name == gridName);
        int columns = Math.Max(1, trade.StockWidth), rows = Math.Max(1, trade.StockHeight);
        var area = slot.Bounds;

        // A building shop's cells outside every container hold nothing: greyed over the grid's tiles.
        foreach (var (r, k) in trade.Unused.Select((r, k) => (r, k)))
        {
            controls.Add(new($"trade-unused-{k}", "Widget",
                new(area.X + r.X * area.Width / columns, area.Y + r.Y * area.Height / rows,
                    r.Width * area.Width / columns, r.Height * area.Height / rows),
                "", "", "", true, new Dictionary<string, string>(), new Vector4(0, 0, 0, 0.65f), Pickable: false, Clip: slot.Clip));
        }

        foreach (var item in trade.Stock)
        {
            if (player.InventoryWindows.Lifted(ItemOwner.Stock, item.Index)) continue;

            player.ItemCells.AddItemCell(controls, $"trade-item-{item.Index}",
                new(area.X + item.X * area.Width / columns, area.Y + item.Y * area.Height / rows, item.Width * area.Width / columns,
                    item.Height * area.Height / rows),
                item.Record, item.Material, item.Name, item.Quantity, TooltipRuntime.ItemTip.Of(item), "TradeItem", item.Index, slot.Clip,
                charge: item.Charge, colour: item.Colour);
        }
    }

    /// <summary>
    /// While trading, the trade item behind an inventory item of a seller's (<see cref="TradeView.GoodsOf"/>): its tooltip gains the prices (Value,
    /// Sell value; <c>FUN_14079e7a0</c>, <b>Observed</b> by decompile, see <see cref="TooltipRuntime.ItemLines"/>). Null: no trade, or a character
    /// who cannot take the trade.
    /// </summary>
    internal TradeItemView? SellTip(CharacterId owner, int index) =>
        player.Session.CurrentSnapshot.Trade is { } trade && trade.GoodsOf.TryGetValue(owner, out var goods)
            ? goods.FirstOrDefault(g => g.Index == index)
            : null;

    /// <summary>
    /// The fencing prompt beside a seller's item (<see cref="TradeView.GoodsOf"/>) held over the stock (<c>FUN_140712920</c>, <b>Observed</b> by
    /// decompile). An illegal good first: "ILLEGAL GOODS!" and "NO!" in the Bad colour, or for a trader that buys illegal goods "Illegal goods?" and
    /// "No problem!" in the Good colour. Else a stolen one: stolen from the trader's own faction, "Fencing chance:" and the chance in the Bad colour;
    /// stolen from anyone else and the trader buys stolen goods, "Stolen goods?" and "No problem!" in the Good colour; otherwise nothing.
    /// </summary>
    internal void AddFencePrompt(List<UiControl> controls)
    {
        if (player.InventoryWindows.Held is not { } item || player.Session.CurrentSnapshot.Trade is not { } trade
            || item.From.Holder != ItemHolder.Character
            || player.FaithfulScreens.HitTest(player.Mouse) is not { Screen: "trader" } || trade.Held is not { } goods)
        {
            return;
        }

        (string Label, string Value, string Colour)? prompt = goods.Illegal
            ? (trade.BuysIllegal ? ("Illegal goods?", "No problem!", PlayerInterface.GoodColour) : ("ILLEGAL GOODS!", "NO!", PlayerInterface.BadColour))
            : !goods.Stolen ? null
            : goods.FenceChance is { } chance ? ("Fencing chance:", $"{(int)(chance * 100)}%", PlayerInterface.BadColour)
            : trade.BuysStolen ? ("Stolen goods?", "No problem!", PlayerInterface.GoodColour) : null;
        if (prompt is not { } p) return;

        float scale = player.InventoryScale, width = 240 * scale, height = 34 * scale;
        var box = new UiRect(Math.Clamp(player.Mouse.X + 18 * scale, 0, player.Width - width),
            Math.Clamp(player.Mouse.Y - height - 8 * scale, 0, player.Height - height), width, height);
        controls.Add(new("fence-prompt", "Widget", box, "", "", "Kenshi_GenericTextBoxSkin", true, new Dictionary<string, string>(), Vector4.Zero,
            Pickable: false));
        player.AddLines(controls, "fence-prompt", [new(p.Label, p.Value, p.Colour, p.Colour)], 20 * scale);
        int last = controls.Count;

        for (int i = last - 2; i < last; i++)
        {
            if (i >= 0 && controls[i].Name.StartsWith("fence-prompt-", StringComparison.Ordinal))
                controls[i] = controls[i] with { Bounds = controls[i].Bounds with { Y = controls[i].Bounds.Y + 7 * scale } };
        }
    }

    /// <summary>
    /// Sends a sale. A stolen item that a shop which is no fence may catch first asks (<c>FUN_140715290</c>, <b>Observed</b> by decompile): "You have
    /// a {N}% chance of getting caught selling this stolen item.\nFence it anyway?" under the title "Fencing Item", N = 100 less the chance of
    /// getting away in per cent, rounded half up; only Yes sells.
    /// </summary>
    internal void Sell(TradeView trade, CharacterId seller, InventoryItemToken token, int quantity, int? x, int y)
    {
        var sale = new SellItem(token, quantity) { Tick = player.Session.World.Tick, X = x, Y = y, From = seller };

        if (!trade.GoodsOf.TryGetValue(seller, out var goods) || goods.FirstOrDefault(g => g.Index == token.Index) is not { FenceOdds: { } odds })
        {
            player.Say(sale);
            return;
        }

        player.Confirm("FENCING ITEM", FenceQuestion(odds), () => player.Say(sale with { Tick = player.Session.World.Tick }));
        player.UpdateMenuPause();
    }

    /// <summary>
    /// The fence confirmation in the player's language, for a sale with <paramref name="odds"/> of getting away (the game's msgid).
    /// </summary>
    internal static string FenceQuestion(float odds)
    {
        int caught = 100 - (int)MathF.Floor(odds * 100 + 0.5f);
        return Loc.Tr("You have a {1}% chance of getting caught selling this stolen item.\nFence it anyway?", caught);
    }

    /// <summary>
    /// <paramref name="cats"/> with thousands separators.
    /// </summary>
    internal static string Money(long cats) => cats.ToString("N0", CultureInfo.InvariantCulture);

    #endregion

    #region Character trading

    // The character trading window (docs/game/ui-screens.md "Character trading", DA 41 / 42), shown while the world publishes one
    // (CharacterTradeView), the same in both UI modes: <c>Kenshi_CharacterSelectionWindow</c> with a <c>Kenshi_CharacterSelectionBox</c> row per
    // character stacked in its scroll view. A row's tick box selects it; the total, the selected count and the buyer's money are kept up to date; the
    // confirm button is enabled while a row is ticked and the total is within the buyer's money (<c>FUN_1407281c0</c>), else the total shows in the
    // Bad colour. Confirm sends the ticked characters and closes the window; cancel, Esc or closing it sends the close.
    /// <summary>
    /// The character trading window's screen id.
    /// </summary>
    internal const string CharacterTradeScreen = "character-trade";

    /// <summary>
    /// A row's height: the row layout's own (<c>Kenshi_CharacterSelectionBox</c> root, 0.180556 of the screen's height); its width is the scroll
    /// view's (engine choice).
    /// </summary>
    const float CharacterRowShare = 0.180556f;

    /// <summary>
    /// The row layout (<c>Kenshi_CharacterSelectionBox</c>); null before the UI is read.
    /// </summary>
    internal MyGuiLayout? CharacterRowLayout;

    /// <summary>
    /// The window on show (its <see cref="CharacterTradeView.Id"/>; 0 none), whether its closing was sent, and the ticked rows' characters.
    /// </summary>
    long _shownCharacterTrade;

    /// <summary>
    /// The shown window's closing was already sent.
    /// </summary>
    bool _characterTradeCloseSent;

    /// <summary>
    /// The ticked rows' characters.
    /// </summary>
    readonly HashSet<CharacterId> _characterTradeTicked = [];

    /// <summary>
    /// Opens the window when the world opens one, closes it when the world's closes, and tells the world when the player closed it.
    /// </summary>
    internal void SyncCharacterTradeScreen()
    {
        if (player.FaithfulScreens is null) return;

        var trade = player.Session.CurrentSnapshot.CharacterTrade;

        if (trade is null)
        {
            if (_shownCharacterTrade != 0 && player.FaithfulScreens.IsOpen(CharacterTradeScreen))
                player.FaithfulScreens.Close(CharacterTradeScreen);

            _shownCharacterTrade = 0;
            _characterTradeCloseSent = false;
            _characterTradeTicked.Clear();
            return;
        }

        if (trade.Id != _shownCharacterTrade)
        {
            _shownCharacterTrade = trade.Id;
            _characterTradeCloseSent = false;
            _characterTradeTicked.Clear();
            player.ContextMenuWindow.CloseContextMenu();
            player.FaithfulScreens.ResetScroll(CharacterTradeScreen);
            player.FaithfulScreens.Open(CharacterTradeScreen);
            return;
        }

        if (!player.FaithfulScreens.IsOpen(CharacterTradeScreen) && !_characterTradeCloseSent)
        {
            player.Say(new CloseCharacterTrade { Tick = player.Session.World.Tick });
            _characterTradeCloseSent = true;
        }
    }

    /// <summary>
    /// The ticked rows still listed: how many and their total price.
    /// </summary>
    (int Count, long Total) TickedRows(CharacterTradeView trade)
    {
        int count = 0;
        long total = 0;

        foreach (var row in trade.Rows)
        {
            if (_characterTradeTicked.Contains(row.Character))
            {
                count++;
                total += row.Price;
            }
        }

        return (count, total);
    }

    /// <summary>
    /// A row's height in pixels.
    /// </summary>
    float CharacterRowHeight => player.Height * CharacterRowShare;

    /// <summary>
    /// The window's bindings: title, confirm caption, the ticked count and total (in the Bad colour when over the buyer's money), and Confirm while a
    /// row is ticked and affordable.
    /// </summary>
    internal void BindCharacterTradeScreen(UiBindings b)
    {
        if (player.Session.CurrentSnapshot.CharacterTrade is not { } trade) return;

        var (count, total) = TickedRows(trade);
        bool affordable = total <= trade.BuyerMoney;
        b.Text["CaptionTextBox"] = trade.Title;
        b.Text["ConfirmButton"] = trade.ConfirmCaption;
        b.Text["CancelButton"] = Loc.Tr("Cancel");
        b.Text["lbTotalMoney"] = Loc.Tr("Total:");
        b.Text["MoneyAmountTextBox"] = "c." + Money(total);
        if (!affordable)
            b.Properties["MoneyAmountTextBox"] = new Dictionary<string, string> { ["TextColour"] = PlayerInterface.BadColour };

        b.Text["SelectedTotalTextBox"] = $"{count}/{trade.Rows.Count}";
        b.Text["lbBuyersMoney"] = Loc.Tr("Buyer's money:");
        b.Text["TotalMoneyBuyer"] = "c." + Money(trade.BuyerMoney);
        b.Actions["CancelButton"] = "CancelButton";
        b.Disabled.Remove("CancelButton");
        b.Actions["ConfirmButton"] = "ConfirmButton";
        if (count > 0 && affordable) b.Disabled.Remove("ConfirmButton");

        // The rows stack down the scroll view (as wide as its client: no horizontal bar).
        b.Canvas["CharacterSelectionItemBox"] = (0, trade.Rows.Count * CharacterRowHeight);
    }

    /// <summary>
    /// The rows, each the row layout arranged at its place on the scroll view's canvas and clipped to its client: the portrait (the renderer's atlas
    /// cell, <see cref="CharacterTradePortraits"/>) under the name, the price, the two stats panels as label/value lines and the tick box.
    /// </summary>
    internal void AddCharacterTradeRows(List<UiControl> controls)
    {
        if (player.Session.CurrentSnapshot.CharacterTrade is not { } trade || CharacterRowLayout is null
            || controls.FirstOrDefault(c => c.Name == "CharacterSelectionItemBox") is not { } view)
        {
            return;
        }

        var client = controls.FirstOrDefault(x => IsTemplateClient(x, view.Name))?.Bounds ?? view.Bounds;
        var clip = view.Clip is { } outer ? client.Intersect(outer) : client;
        float x0 = MyGuiPresentation.Num(view.Properties, "CanvasX", client.X), y0 = MyGuiPresentation.Num(view.Properties, "CanvasY", client.Y);
        float rowHeight = CharacterRowHeight;
        var cells = CharacterTradePortraits().ToDictionary(p => p.Character, p => p.Cell);

        // The row's root is placed in screen fractions (position_real): the layout is arranged on the "screen" whose fractions make it the row's box.
        var rootShare = CharacterRowLayout.Find("Root")?.Position is { Width: > 0, Height: > 0 } r ? (r.Width, r.Height) : (1f, 1f);

        for (int i = 0; i < trade.Rows.Count; i++)
        {
            var row = trade.Rows[i];
            var box = new UiRect(x0, y0 + i * rowHeight, client.Width, rowHeight);
            if (box.Intersect(clip).Height <= 0) continue;

            bool ticked = _characterTradeTicked.Contains(row.Character);
            string prefix = "character-row-" + i.ToString(CultureInfo.InvariantCulture) + "-";
            var b = new UiBindings { TemplateScale = player.InventoryScale };
            b.Text["NameTextBox"] = row.Name;
            b.Text["PriceTextBox"] = "c." + Money(row.Price);
            b.Actions["CheckBox"] = "character-tick:" + i.ToString(CultureInfo.InvariantCulture);
            b.Properties["CheckBox"] = new Dictionary<string, string>
            {
                ["Checked"] = ticked ? "true" : "false",
                ["StateSelected"] = ticked ? "true" : "false",
            };

            foreach (var c in MyGuiPresentation.Arrange(CharacterRowLayout, (int)MathF.Round(box.Width / rootShare.Item1),
                (int)MathF.Round(box.Height / rootShare.Item2), b, player.UiResources.Layers))
            {
                var bounds = c.Bounds with { X = c.Bounds.X + box.X, Y = c.Bounds.Y + box.Y };
                var own = c.Clip is { } inner ? (inner with { X = inner.X + box.X, Y = inner.Y + box.Y }).Intersect(clip) : clip;
                controls.Add(c with { Name = c.Name.Length > 0 ? prefix + c.Name : c.Name, Bounds = bounds, Clip = own });

                // The portrait goes on its frame, under the name nested in it.
                if (c.Name == "Portrait" && cells.TryGetValue(row.Character, out int cell))
                {
                    controls.Add(new(prefix + "portrait", "ImageBox", bounds, "", "", "", true,
                        new Dictionary<string, string>
                        {
                            ["ImageTexture"] = PortraitBar.PortraitTexture,
                            ["ImageFlipV"] = "true",
                            ["ImageRect"] = player.PortraitBar.PortraitRect(cell),
                        },
                        Vector4.Zero, Pickable: false, Clip: own));
                }
            }

            foreach (var (panel, lines) in new[] { ("DataPanel1", row.Panel1), ("DataPanel2", row.Panel2) })
            {
                if (lines.Count == 0 || controls.FirstOrDefault(c => c.Name == prefix + panel) is not { } host)
                    continue;

                player.AddLines(controls, prefix + panel, [.. lines.Select(l => new PlayerInterface.HudLine(l.Label, l.Value))],
                    host.Bounds.Height / 5, PlayerInterface.StandardTextPixels * player.InventoryScale);
            }
        }
    }

    /// <summary>
    /// A window event: a row's tick flipped, Confirm (sends the ticked characters), or Cancel (sends the close).
    /// </summary>
    internal void CharacterTradeAction(MyGuiEvent e)
    {
        if (player.Session.CurrentSnapshot.CharacterTrade is not { } trade) return;

        if (e.Action.StartsWith("character-tick:", StringComparison.Ordinal)
            && int.TryParse(e.Action.AsSpan(15), NumberStyles.None, CultureInfo.InvariantCulture, out int i))
        {
            if (i < trade.Rows.Count && !_characterTradeTicked.Remove(trade.Rows[i].Character))
                _characterTradeTicked.Add(trade.Rows[i].Character);
        }
        else if (e.Widget == "ConfirmButton")
        {
            var (count, total) = TickedRows(trade);
            if (count == 0 || total > trade.BuyerMoney) return;

            player.Say(new ConfirmCharacterTrade([.. trade.Rows.Select(r => r.Character).Where(_characterTradeTicked.Contains)])
            {
                Tick = player.Session.World.Tick,
            });
            _characterTradeCloseSent = true;
            player.FaithfulScreens.Close(CharacterTradeScreen);
        }
        else if (e.Widget == "CancelButton") player.FaithfulScreens.Close(CharacterTradeScreen);
    }

    /// <summary>
    /// The rows' characters whose portraits the host draws, each into an atlas cell after the squad bar's (and the squads screen's) and, while no
    /// conversation shows them, the conversation's two; the rows past the last cell show none.
    /// </summary>
    public IReadOnlyList<(CharacterId Character, int Cell)> CharacterTradePortraits()
    {
        if (player.Session.CurrentSnapshot.CharacterTrade is not { } trade || !player.FaithfulScreens.IsOpen(CharacterTradeScreen))
            return [];

        int next = player.PortraitBar.PortraitMembers().Count;
        int end = player.Session.CurrentSnapshot.Conversation is null ? PortraitBar.PortraitCells : PortraitBar.DialogueNpcCell;
        var list = new List<(CharacterId, int)>();

        foreach (var row in trade.Rows)
        {
            if (next >= end) break;

            list.Add((row.Character, next++));
        }

        return list;
    }

    #endregion

    #region Loot

    // The loot window (docs/simulation.md "Carrying, rescue and looting", engine choice: the original's is <b>Unknown</b>): a downed character's
    // whole inventory in a second character window (<c>loot</c>, <c>Kenshi_InventoryCharacterWindow</c>) beside the looter's, its worn gear in the
    // equipment slots, and its bag in <c>loot-bag</c> (the generic window, OPEN BAG). Items are held with the mouse between the two characters'
    // windows (InventoryWindows) or moved with a right click (TakeLootItem, PutLootItem); closing it closes the loot (CloseContainer).
    /// <summary>
    /// The character and the animal inventory window layouts; null before the UI is read.
    /// </summary>
    internal MyGuiLayout? CharacterInventoryLayout, AnimalInventoryLayout;

    /// <summary>
    /// Whether each inventory or loot window shows the animal layout now.
    /// </summary>
    internal readonly Dictionary<string, bool> AnimalWindow = new(StringComparer.Ordinal);

    /// <summary>
    /// The body whose loot window is shown, and one whose window the player closed (not reopened while the sim still has it open).
    /// </summary>
    CharacterId _shownLoot = CharacterId.None, _closedLoot = CharacterId.None;

    /// <summary>
    /// The loot's bag window is wanted (OPEN BAG in the loot window toggles it).
    /// </summary>
    internal bool LootBagOpen = true;

    /// <summary>
    /// The character whose bag the loot window opened (not left open with OPEN BAG): it closes again with the loot window unless OPEN BAG was pressed
    /// meanwhile.
    /// </summary>
    internal CharacterId LootOpenedBag = CharacterId.None;

    /// <summary>
    /// Whether <paramref name="c"/> is an animal.
    /// </summary>
    internal static bool IsAnimal(CharacterSnapshot? c) => c is { Animal: true };

    /// <summary>
    /// Whether <paramref name="screen"/> is the loot window or its bag's.
    /// </summary>
    internal static bool IsLootScreen(string screen) => screen is "loot" or "loot-bag";

    /// <summary>
    /// The open loot's body (its snapshot carries the item grid), or null.
    /// </summary>
    internal CharacterSnapshot? LootBody()
    {
        var id = player.Session.CurrentSnapshot.OpenLoot;
        return id.IsNone ? null : player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id);
    }

    /// <summary>
    /// Once a frame, before the windows are arranged: the inventory and loot windows take the original's animal window
    /// (<c>Kenshi_InventoryAnimalWindow</c>: the main grid, the backpack slot with OPEN BAG and the data panel, no equipment slots) while they show
    /// an animal, the character window otherwise.
    /// </summary>
    internal void SyncInventoryLayouts()
    {
        if (player.FaithfulScreens is null || CharacterInventoryLayout is null || AnimalInventoryLayout is null) return;

        foreach (var (id, owner) in player.InventoryWindows.PinnedInventories.Keys
            .Select(slot => (slot, player.InventoryWindows.WindowCharacter(slot)))
            .Prepend(("inventory", player.InventoryWindows.InventoryOwner()))
            .Append(("loot", LootBody())))
        {
            bool animal = IsAnimal(owner);
            if (AnimalWindow.GetValueOrDefault(id) == animal) continue;

            AnimalWindow[id] = animal;
            player.FaithfulScreens.Replace(id, animal ? AnimalInventoryLayout : CharacterInventoryLayout);
        }
    }

    /// <summary>
    /// Once a frame: the loot windows follow the sim's open loot; the player closing the window closes the loot.
    /// </summary>
    internal void SyncLootScreen()
    {
        if (player.FaithfulScreens is null) return;

        if (!LootOpenedBag.IsNone && !player.FaithfulScreens.IsOpen("loot"))
        {
            player.InventoryWindows.BagOpen.Remove(LootOpenedBag);
            LootOpenedBag = CharacterId.None;
        }

        var open = player.Session.CurrentSnapshot.OpenLoot;
        if (_closedLoot != open) _closedLoot = CharacterId.None;

        if (!_shownLoot.IsNone && _shownLoot == open && !player.FaithfulScreens.IsOpen("loot"))
        {
            player.Say(new CloseContainer { Tick = player.Session.World.Tick });
            (_closedLoot, _shownLoot) = (open, CharacterId.None);
            return;
        }

        if (_shownLoot != open && (open.IsNone || open != _closedLoot))
        {
            if (!_shownLoot.IsNone) player.FaithfulScreens.Close("loot");

            _shownLoot = open;

            if (!open.IsNone)
            {
                if (player.Selected().Count > 0 && !player.FaithfulScreens.IsOpen("inventory"))
                    player.FaithfulScreens.Open("inventory");

                player.FaithfulScreens.Open("loot");

                // The original opens both characters' bags with the window (the looted one's is open by default); OPEN BAG still toggles them
                // afterwards. The looter's remembered OPEN BAG choice is kept: a bag opened here closes with the window.
                if (player.InventoryWindows.InventoryOwner() is { } mine && player.InventoryWindows.BagOpen.Add(mine.Id))
                    LootOpenedBag = mine.Id;

                player.InventoryLayout.PlaceWindowRow();
                player.ScrollBars.InventoryScroll.Remove("loot-bag");
            }
        }

        bool want = player.FaithfulScreens.IsOpen("loot") && LootBagOpen && LootBody() is { ItemGrid: { BagWidth: > 0, BagHeight: > 0 } };
        if (want == player.FaithfulScreens.IsOpen("loot-bag")) return;

        if (!want)
        {
            player.FaithfulScreens.Close("loot-bag");
            if (player.FaithfulScreens.IsOpen("loot")) player.InventoryLayout.PlaceWindowRow();

            return;
        }

        player.FaithfulScreens.Open("loot-bag");
        player.InventoryLayout.PlaceWindowRow();
    }

    /// <summary>
    /// The loot window's (or its bag's) bindings: the caption (the body's or its bag's name), OPEN BAG with a bag and LIMBS with a limb window.
    /// </summary>
    internal void BindLootScreen(string id, UiBindings b)
    {
        var body = LootBody();
        string caption = id == "loot-bag"
            ? body?.ItemGrid?.Items.FirstOrDefault(i => i.Section == "backpack_attach")?.Name ?? "BACKPACK"
            : body?.Name ?? "INVENTORY";
        b.Text["Root"] = caption.ToUpperInvariant();

        if (id == "loot-bag")
        {
            b.Hidden.Add("CapacityText");
            b.Hidden.Add("TypeText");
            return;
        }

        b.Properties["datapanel"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };

        if (body?.ItemGrid is { BagWidth: > 0, BagHeight: > 0 })
        {
            b.Disabled.Remove("OpenBagButton");
            b.Actions["OpenBagButton"] = "OpenBagButton";
        }

        // LIMBS opens the body's limbs window, as the inventory's does for its character (enabled by the same limb holder).
        if (body is not null && InventoryWindows.HasLimbWindow(body))
        {
            b.Disabled.Remove("LimbsButton");
            b.Actions["LimbsButton"] = "LimbsButton";
        }
    }

    #endregion
}