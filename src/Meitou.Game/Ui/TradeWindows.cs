using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Trading;

namespace Meitou.Game.Ui;

/// <summary>
/// Trading (docs/simulation.md "Trade", docs/game/ui-screens.md section 6), shown while the world publishes a trade (<see cref="TradeView"/>), the same in both UI modes:
/// <list type="bullet">
/// <item>The trader's stock is the original's trader inventory window (<c>Kenshi_InventoryTraderWindow</c>, loaded by <c>FUN_140155ee0</c>) beside the character's inventory window, both at the
/// top left of the screen (<c>FUN_1406e4960</c>). The window is the layout's grown by what the stock grid needs beyond the layout's grid, as a square (<c>FUN_140150570</c>); the grid scrolls
/// in the layout's scroll view.</item>
/// <item>Items are held with the mouse between the grids (InventoryWindows): stock placed in the customer's windows is bought there, the customer's items placed on the stock are
/// sold. Closing the trader window ends the trade. Prices are in the item tooltips (Value, Sell value); a stolen item held over the stock shows the fencing prompt.</item>
/// </list>
/// </summary>
sealed class TradeWindows(PlayerInterface player)
{
    /// <summary>The trader whose window the interface opened (none: no trade shown) and whether its closing was already sent.</summary>
    internal CharacterId shownTrader = CharacterId.None;
    bool tradeCloseSent;

    /// <summary>Opens the trader beside the inventory when a trade opens, and ends the trade when the player closes its window.</summary>
    internal void SyncTradeScreens()
    {
        if (player.faithfulScreens is null) return;
        var trade = player.session.CurrentSnapshot.Trade;
        if (trade is null)
        {
            if (!shownTrader.IsNone && player.faithfulScreens.IsOpen("trader")) player.faithfulScreens.Close("trader");
            shownTrader = CharacterId.None;
            tradeCloseSent = false;
            return;
        }
        if (trade.Trader != shownTrader)
        {
            shownTrader = trade.Trader;
            tradeCloseSent = false;
            player.faithfulScreens.ResetScroll("trader");
            if (!player.faithfulScreens.IsOpen("inventory")) player.faithfulScreens.Open("inventory");
            player.faithfulScreens.Open("trader");
            PlaceTradeWindows();
            return;
        }
        if (!player.faithfulScreens.IsOpen("trader") && !tradeCloseSent)
        {
            player.Say(new CloseTrade { Tick = player.session.World.Tick });
            tradeCloseSent = true;
        }
    }

    /// <summary>
    /// The trade's windows side by side from the top left (<c>FUN_1406e4960</c>, <b>Observed</b> by decompile and the reference screenshot): a margin of 5 % of the smaller screen side from
    /// the top and the left, then the bag (when open), the character and the trader, each starting where the previous one ends.
    /// </summary>
    internal void PlaceTradeWindows()
    {
        var frames = player.faithfulScreens.Arrange(player.Width, player.Height, player.BindFaithfulScreen, player.AddFaithfulContent);
        UiRect? Root(string screen) => frames.FirstOrDefault(f => f.Id == screen)?.Controls.FirstOrDefault(c => c.Type == "Window")?.Bounds;
        float margin = (int)(Math.Min(player.Width, player.Height) * 0.05f), x = margin;
        void Put(string id)
        {
            if (!player.faithfulScreens.IsOpen(id) || Root(id) is not { } r) return;
            player.faithfulScreens.SetOffset(id, player.faithfulScreens.GetOffset(id) + new Vector2(x - r.X, margin - r.Y));
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

    string? TraderGrid(string screen) => GridIn(player.faithfulScreens.Arrange(player.Width, player.Height, player.BindFaithfulScreen, player.AddFaithfulContent).First(f => f.Id == screen).Controls);

    /// <summary>The trader's name in the caption, its ARRANGE button; the money line is added after arranging.</summary>
    internal void BindTraderScreen(UiBindings b)
    {
        var trade = player.session.CurrentSnapshot.Trade;
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
        var layout = player.faithfulScreens.Layout("trader");
        if (layout.Find("Root") is not { } root || layout.Find("Content") is not { } content || layout.Find("backpack_content") is not { } grid) return (0, 0);
        var r = root.Position.RelativeTo(new(0, 0, player.Width, player.Height), root.RealPosition);
        float side = columns * player.InventoryCell;
        return (side - grid.Position.Width * content.Position.Width * r.Width, side - grid.Position.Height * content.Position.Height * r.Height);
    }

    internal void BindTraderGeometry(UiBindings b)
    {
        var layout = player.faithfulScreens.Layout("trader");
        if (player.session.CurrentSnapshot.Trade is { } trade && layout.Find("Root") is { } root)
        {
            var r = root.Position.RelativeTo(new(0, 0, player.Width, player.Height), root.RealPosition);
            int columns = Math.Max(1, trade.StockWidth), rows = Math.Max(1, trade.StockHeight);
            var (dx, dy) = TraderGrowth(columns);
            b.Positions["Root"] = new(0, 0, r.Width + dx, r.Height + dy);
            // The scroll view grows with the window (its frame in fractions of the content area) and scrolls the stock grid as its canvas: the host's generic ScrollView draws the template's bars.
            if (layout.Find("Content") is { } content && layout.Find("scrollview_backpack_content") is { } view)
            {
                float contentW = content.Position.Width * (r.Width + dx), contentH = content.Position.Height * (r.Height + dy), w0 = content.Position.Width * r.Width, h0 = content.Position.Height * r.Height;
                b.RelativePositions[view] = new(view.Position.X * w0 / contentW, view.Position.Y * h0 / contentH, (view.Position.Width * w0 + dx) / contentW, (view.Position.Height * h0 + dy) / contentH);
                b.Canvas[view.Name] = (columns * player.InventoryCell, rows * player.InventoryCell);
            }
        }
    }

    /// <summary>
    /// Places the trader window's parts after the growth: the money box keeps its size at the top left, ARRANGE stays centred at the bottom. The scroll view grows with the window (<see cref="BindTraderGeometry"/>)
    /// and the stock grid is its canvas: it sits where the generic ScrollView (<c>Kenshi_ScrollView</c> template: client, bar, offset) put the canvas, clipped to the client.
    /// </summary>
    internal void ArrangeTraderWindow(List<UiControl> controls)
    {
        if (player.session.CurrentSnapshot.Trade is not { } trade) return;
        var layout = player.faithfulScreens.Layout("trader");
        int contentIndex = controls.FindIndex(c => c.Name == "Content"), viewIndex = controls.FindIndex(c => c.Name == "scrollview_backpack_content");
        if (contentIndex < 0 || viewIndex < 0 || layout.Find("Content") is not { } contentWidget) return;
        int columns = Math.Max(1, trade.StockWidth), rows = Math.Max(1, trade.StockHeight);
        var (dx, dy) = TraderGrowth(columns);
        float cell = player.InventoryCell;
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
    internal void AddTraderContent(List<UiControl> controls)
    {
        if (player.session.CurrentSnapshot.Trade is not { } trade || GridIn(controls) is not { } gridName) return;
        player.AddLines(controls, "datapanel", [new("Money:", "c." + Money(trade.ShopMoney))], 20 * player.InventoryScale);
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
            if (player.inventoryWindows.Lifted(ItemOwner.Stock, item.Index)) continue;
            player.AddItemCell(controls, $"trade-item-{item.Index}",
                new(area.X + item.X * area.Width / columns, area.Y + item.Y * area.Height / rows, item.Width * area.Width / columns, item.Height * area.Height / rows),
                item.Record, item.Material, item.Name, item.Quantity, TooltipRuntime.ItemTip.Of(item), "TradeItem", item.Index, slot.Clip, charge: item.Charge, colour: item.Colour);
        }
    }

    /// <summary>
    /// While trading, the trade item behind an inventory item of a seller's (<see cref="TradeView.GoodsOf"/>): its tooltip gains the prices (Value, Sell value; <c>FUN_14079e7a0</c>,
    /// <b>Observed</b> by decompile, see <see cref="TooltipRuntime.ItemLines"/>). Null: no trade, or a character who cannot take the trade.
    /// </summary>
    internal TradeItemView? SellTip(CharacterId owner, int index) =>
        player.session.CurrentSnapshot.Trade is { } trade && trade.GoodsOf.TryGetValue(owner, out var goods) ? goods.FirstOrDefault(g => g.Index == index) : null;

    /// <summary>
    /// The fencing prompt beside a seller's item (<see cref="TradeView.GoodsOf"/>) held over the stock (<c>FUN_140712920</c>, <b>Observed</b> by decompile). An illegal good first:
    /// "ILLEGAL GOODS!" and "NO!" in the Bad colour, or for a trader that buys illegal goods "Illegal goods?" and "No problem!" in the Good colour. Else a stolen one: stolen from the trader's own faction, "Fencing chance:" and the
    /// chance in the Bad colour; stolen from anyone else and the trader buys stolen goods, "Stolen goods?" and "No problem!" in the Good colour; otherwise nothing.
    /// </summary>
    internal void AddFencePrompt(List<UiControl> controls)
    {
        if (player.inventoryWindows.held is not { } item || player.session.CurrentSnapshot.Trade is not { } trade || item.From.Holder != ItemHolder.Character
            || player.faithfulScreens.HitTest(player.mouse) is not { Screen: "trader" } || trade.Held is not { } goods) return;
        (string Label, string Value, string Colour)? prompt = goods.Illegal ? (trade.BuysIllegal ? ("Illegal goods?", "No problem!", PlayerInterface.GoodColour) : ("ILLEGAL GOODS!", "NO!", PlayerInterface.BadColour))
            : !goods.Stolen ? null
            : goods.FenceChance is { } chance ? ("Fencing chance:", $"{(int)(chance * 100)}%", PlayerInterface.BadColour)
            : trade.BuysStolen ? ("Stolen goods?", "No problem!", PlayerInterface.GoodColour) : null;
        if (prompt is not { } p) return;
        float scale = player.InventoryScale, width = 240 * scale, height = 34 * scale;
        var box = new UiRect(Math.Clamp(player.mouse.X + 18 * scale, 0, player.Width - width), Math.Clamp(player.mouse.Y - height - 8 * scale, 0, player.Height - height), width, height);
        controls.Add(new("fence-prompt", "Widget", box, "", "", "Kenshi_GenericTextBoxSkin", true, new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
        player.AddLines(controls, "fence-prompt", [new(p.Label, p.Value, p.Colour, p.Colour)], 20 * scale);
        int last = controls.Count;
        for (int i = last - 2; i < last; i++)
            if (i >= 0 && controls[i].Name.StartsWith("fence-prompt-", StringComparison.Ordinal))
                controls[i] = controls[i] with { Bounds = controls[i].Bounds with { Y = controls[i].Bounds.Y + 7 * scale } };
    }

    /// <summary>
    /// Sends a sale. A stolen item that a shop which is no fence may catch first asks (<c>FUN_140715290</c>, <b>Observed</b> by decompile): "You have a {N}% chance of getting caught selling this
    /// stolen item.\nFence it anyway?" under the title "Fencing Item", N = 100 less the chance of getting away in per cent, rounded half up; only Yes sells.
    /// </summary>
    internal void Sell(TradeView trade, CharacterId seller, InventoryItemToken token, int quantity, int? x, int y)
    {
        var sale = new SellItem(token, quantity) { Tick = player.session.World.Tick, X = x, Y = y, From = seller };
        if (!trade.GoodsOf.TryGetValue(seller, out var goods) || goods.FirstOrDefault(g => g.Index == token.Index) is not { FenceOdds: { } odds })
        {
            player.Say(sale);
            return;
        }
        player.Confirm("FENCING ITEM", FenceQuestion(odds), () => player.Say(sale with { Tick = player.session.World.Tick }));
        player.UpdateMenuPause();
    }

    /// <summary>The fence confirmation in the player's language, for a sale with <paramref name="odds"/> of getting away (the game's msgid).</summary>
    internal static string FenceQuestion(float odds)
    {
        int caught = 100 - (int)MathF.Floor(odds * 100 + 0.5f);
        return Loc.Tr("You have a {1}% chance of getting caught selling this stolen item.\nFence it anyway?", caught);
    }

    internal static string Money(long cats) => cats.ToString("N0", CultureInfo.InvariantCulture);

    // The character trading window (docs/game/ui-screens.md "Character trading", DA 41 / 42), shown while the world publishes one (CharacterTradeView), the same in both UI
    // modes: <c>Kenshi_CharacterSelectionWindow</c> with a <c>Kenshi_CharacterSelectionBox</c> row per character stacked in its scroll view. A row's tick box selects it; the total, the
    // selected count and the buyer's money are kept up to date; the confirm button is enabled while a row is ticked and the total is within the buyer's money (<c>FUN_1407281c0</c>),
    // else the total shows in the Bad colour. Confirm sends the ticked characters and closes the window; cancel, Esc or closing it sends the close.
    internal const string CharacterTradeScreen = "character-trade";
    /// <summary>A row's height: the row layout's own (<c>Kenshi_CharacterSelectionBox</c> root, 0.180556 of the screen's height); its width is the scroll view's (engine choice).</summary>
    const float CharacterRowShare = 0.180556f;

    internal MyGuiLayout? characterRowLayout;
    /// <summary>The window on show (its <see cref="CharacterTradeView.Id"/>; 0 none), whether its closing was sent, and the ticked rows' characters.</summary>
    long shownCharacterTrade;
    bool characterTradeCloseSent;
    readonly HashSet<CharacterId> characterTradeTicked = [];

    /// <summary>Opens the window when the world opens one, closes it when the world's closes, and tells the world when the player closed it.</summary>
    internal void SyncCharacterTradeScreen()
    {
        if (player.faithfulScreens is null) return;
        var trade = player.session.CurrentSnapshot.CharacterTrade;
        if (trade is null)
        {
            if (shownCharacterTrade != 0 && player.faithfulScreens.IsOpen(CharacterTradeScreen)) player.faithfulScreens.Close(CharacterTradeScreen);
            shownCharacterTrade = 0;
            characterTradeCloseSent = false;
            characterTradeTicked.Clear();
            return;
        }
        if (trade.Id != shownCharacterTrade)
        {
            shownCharacterTrade = trade.Id;
            characterTradeCloseSent = false;
            characterTradeTicked.Clear();
            player.CloseContextMenu();
            player.faithfulScreens.ResetScroll(CharacterTradeScreen);
            player.faithfulScreens.Open(CharacterTradeScreen);
            return;
        }
        if (!player.faithfulScreens.IsOpen(CharacterTradeScreen) && !characterTradeCloseSent)
        {
            player.Say(new CloseCharacterTrade { Tick = player.session.World.Tick });
            characterTradeCloseSent = true;
        }
    }

    /// <summary>The ticked rows still listed: how many and their total price.</summary>
    (int Count, long Total) TickedRows(CharacterTradeView trade)
    {
        int count = 0;
        long total = 0;
        foreach (var row in trade.Rows)
            if (characterTradeTicked.Contains(row.Character)) { count++; total += row.Price; }
        return (count, total);
    }

    float CharacterRowHeight => player.Height * CharacterRowShare;

    internal void BindCharacterTradeScreen(UiBindings b)
    {
        if (player.session.CurrentSnapshot.CharacterTrade is not { } trade) return;
        var (count, total) = TickedRows(trade);
        bool affordable = total <= trade.BuyerMoney;
        b.Text["CaptionTextBox"] = trade.Title;
        b.Text["ConfirmButton"] = trade.ConfirmCaption;
        b.Text["CancelButton"] = Loc.Tr("Cancel");
        b.Text["lbTotalMoney"] = Loc.Tr("Total:");
        b.Text["MoneyAmountTextBox"] = "c." + Money(total);
        if (!affordable) b.Properties["MoneyAmountTextBox"] = new Dictionary<string, string> { ["TextColour"] = PlayerInterface.BadColour };
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
    /// The rows, each the row layout arranged at its place on the scroll view's canvas and clipped to its client: the portrait (the renderer's atlas cell, <see cref="CharacterTradePortraits"/>)
    /// under the name, the price, the two stats panels as label/value lines and the tick box.
    /// </summary>
    internal void AddCharacterTradeRows(List<UiControl> controls)
    {
        if (player.session.CurrentSnapshot.CharacterTrade is not { } trade || characterRowLayout is null || controls.FirstOrDefault(c => c.Name == "CharacterSelectionItemBox") is not { } view) return;
        var client = controls.FirstOrDefault(x => x.Properties.GetValueOrDefault("TemplateOwner") == view.Name && x.Properties.GetValueOrDefault("TemplatePart") == "Client")?.Bounds ?? view.Bounds;
        var clip = view.Clip is { } outer ? client.Intersect(outer) : client;
        float x0 = MyGuiPresentation.Num(view.Properties, "CanvasX", client.X), y0 = MyGuiPresentation.Num(view.Properties, "CanvasY", client.Y);
        float rowHeight = CharacterRowHeight;
        var cells = CharacterTradePortraits().ToDictionary(p => p.Character, p => p.Cell);
        // The row's root is placed in screen fractions (position_real): the layout is arranged on the "screen" whose fractions make it the row's box.
        var rootShare = characterRowLayout.Find("Root")?.Position is { Width: > 0, Height: > 0 } r ? (r.Width, r.Height) : (1f, 1f);
        for (int i = 0; i < trade.Rows.Count; i++)
        {
            var row = trade.Rows[i];
            var box = new UiRect(x0, y0 + i * rowHeight, client.Width, rowHeight);
            if (box.Intersect(clip).Height <= 0) continue;
            bool ticked = characterTradeTicked.Contains(row.Character);
            string prefix = "character-row-" + i.ToString(CultureInfo.InvariantCulture) + "-";
            var b = new UiBindings { TemplateScale = player.InventoryScale };
            b.Text["NameTextBox"] = row.Name;
            b.Text["PriceTextBox"] = "c." + Money(row.Price);
            b.Actions["CheckBox"] = "character-tick:" + i.ToString(CultureInfo.InvariantCulture);
            b.Properties["CheckBox"] = new Dictionary<string, string> { ["Checked"] = ticked ? "true" : "false", ["StateSelected"] = ticked ? "true" : "false" };
            foreach (var c in MyGuiPresentation.Arrange(characterRowLayout, (int)MathF.Round(box.Width / rootShare.Item1), (int)MathF.Round(box.Height / rootShare.Item2), b, player.uiResources.Layers))
            {
                var bounds = c.Bounds with { X = c.Bounds.X + box.X, Y = c.Bounds.Y + box.Y };
                var own = c.Clip is { } inner ? (inner with { X = inner.X + box.X, Y = inner.Y + box.Y }).Intersect(clip) : clip;
                controls.Add(c with { Name = c.Name.Length > 0 ? prefix + c.Name : c.Name, Bounds = bounds, Clip = own });
                // The portrait goes on its frame, under the name nested in it.
                if (c.Name == "Portrait" && cells.TryGetValue(row.Character, out int cell))
                {
                    controls.Add(new(prefix + "portrait", "ImageBox", bounds, "", "", "", true,
                        new Dictionary<string, string> { ["ImageTexture"] = PortraitBar.PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = player.portraitBar.PortraitRect(cell) },
                        Vector4.Zero, Pickable: false, Clip: own));
                }
            }
            foreach (var (panel, lines) in new[] { ("DataPanel1", row.Panel1), ("DataPanel2", row.Panel2) })
            {
                if (lines.Count == 0 || controls.FirstOrDefault(c => c.Name == prefix + panel) is not { } host) continue;
                player.AddLines(controls, prefix + panel, [.. lines.Select(l => new PlayerInterface.HudLine(l.Label, l.Value))], host.Bounds.Height / 5, PlayerInterface.StandardTextPixels * player.InventoryScale);
            }
        }
    }

    internal void CharacterTradeAction(MyGuiEvent e)
    {
        if (player.session.CurrentSnapshot.CharacterTrade is not { } trade) return;
        if (e.Action.StartsWith("character-tick:", StringComparison.Ordinal) && int.TryParse(e.Action.AsSpan(15), NumberStyles.None, CultureInfo.InvariantCulture, out int i))
        {
            if (i < trade.Rows.Count && !characterTradeTicked.Remove(trade.Rows[i].Character)) characterTradeTicked.Add(trade.Rows[i].Character);
        }
        else if (e.Widget == "ConfirmButton")
        {
            var (count, total) = TickedRows(trade);
            if (count == 0 || total > trade.BuyerMoney) return;
            player.Say(new ConfirmCharacterTrade([.. trade.Rows.Select(r => r.Character).Where(characterTradeTicked.Contains)]) { Tick = player.session.World.Tick });
            characterTradeCloseSent = true;
            player.faithfulScreens.Close(CharacterTradeScreen);
        }
        else if (e.Widget == "CancelButton") player.faithfulScreens.Close(CharacterTradeScreen);
    }

    /// <summary>
    /// The rows' characters whose portraits the host draws, each into an atlas cell after the squad bar's (and the squads screen's) and, while no conversation shows them, the
    /// conversation's two; the rows past the last cell show none.
    /// </summary>
    public IReadOnlyList<(CharacterId Character, int Cell)> CharacterTradePortraits()
    {
        if (player.session.CurrentSnapshot.CharacterTrade is not { } trade || !player.faithfulScreens.IsOpen(CharacterTradeScreen)) return [];
        int next = player.portraitBar.PortraitMembers().Count, end = player.session.CurrentSnapshot.Conversation is null ? PortraitBar.PortraitCells : PortraitBar.DialogueNpcCell;
        var list = new List<(CharacterId, int)>();
        foreach (var row in trade.Rows)
        {
            if (next >= end) break;
            list.Add((row.Character, next++));
        }
        return list;
    }

    // The loot window (docs/simulation.md "Carrying, rescue and looting", engine choice: the original's is <b>Unknown</b>): a downed character's whole inventory in a second character window (<c>loot</c>, <c>Kenshi_InventoryCharacterWindow</c>) beside the
    // looter's, its worn gear in the equipment slots, and its bag in <c>loot-bag</c> (the generic window, OPEN BAG). Items are held with the mouse between the two characters' windows
    // (InventoryWindows) or moved with a right click (TakeLootItem, PutLootItem); closing it closes the loot (CloseContainer).
    /// <summary>The body whose loot window is shown, and one whose window the player closed (not reopened while the sim still has it open).</summary>
    CharacterId shownLoot = CharacterId.None, closedLoot = CharacterId.None;
    /// <summary>The loot's bag window is wanted (OPEN BAG in the loot window toggles it).</summary>
    internal bool lootBagOpen = true;
    /// <summary>The character whose bag the loot window opened (not left open with OPEN BAG): it closes again with the loot window unless OPEN BAG was pressed meanwhile.</summary>
    internal CharacterId lootOpenedBag = CharacterId.None;

    internal static bool IsAnimal(CharacterSnapshot? c) => c is { Animal: true };

    internal static bool IsLootScreen(string screen) => screen is "loot" or "loot-bag";

    /// <summary>The open loot's body (its snapshot carries the item grid), or null.</summary>
    internal CharacterSnapshot? LootBody()
    {
        var id = player.session.CurrentSnapshot.OpenLoot;
        return id.IsNone ? null : player.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id);
    }

    internal MyGuiLayout? characterInventoryLayout, animalInventoryLayout;
    internal readonly Dictionary<string, bool> animalWindow = new(StringComparer.Ordinal);

    /// <summary>
    /// Once a frame, before the windows are arranged: the inventory and loot windows take the original's animal window (<c>Kenshi_InventoryAnimalWindow</c>: the main grid,
    /// the backpack slot with OPEN BAG and the data panel, no equipment slots) while they show an animal, the character window otherwise.
    /// </summary>
    internal void SyncInventoryLayouts()
    {
        if (player.faithfulScreens is null || characterInventoryLayout is null || animalInventoryLayout is null) return;
        foreach (var (id, owner) in player.inventoryWindows.pinnedInventories.Keys.Select(slot => (slot, player.inventoryWindows.WindowCharacter(slot))).Prepend(("inventory", player.inventoryWindows.InventoryOwner())).Append(("loot", LootBody())))
        {
            bool animal = IsAnimal(owner);
            if (animalWindow.GetValueOrDefault(id) == animal) continue;
            animalWindow[id] = animal;
            player.faithfulScreens.Replace(id, animal ? animalInventoryLayout : characterInventoryLayout);
        }
    }

    /// <summary>Once a frame: the loot windows follow the sim's open loot; the player closing the window closes the loot.</summary>
    internal void SyncLootScreen()
    {
        if (player.faithfulScreens is null) return;
        if (!lootOpenedBag.IsNone && !player.faithfulScreens.IsOpen("loot")) { player.inventoryWindows.bagOpen.Remove(lootOpenedBag); lootOpenedBag = CharacterId.None; }
        var open = player.session.CurrentSnapshot.OpenLoot;
        if (closedLoot != open) closedLoot = CharacterId.None;
        if (!shownLoot.IsNone && shownLoot == open && !player.faithfulScreens.IsOpen("loot"))
        {
            player.Say(new CloseContainer { Tick = player.session.World.Tick });
            (closedLoot, shownLoot) = (open, CharacterId.None);
            return;
        }
        if (shownLoot != open && (open.IsNone || open != closedLoot))
        {
            if (!shownLoot.IsNone) player.faithfulScreens.Close("loot");
            shownLoot = open;
            if (!open.IsNone)
            {
                if (player.Selected().Count > 0 && !player.faithfulScreens.IsOpen("inventory")) player.faithfulScreens.Open("inventory");
                player.faithfulScreens.Open("loot");
                // The original opens both characters' bags with the window (the looted one's is open by default); OPEN BAG still toggles them afterwards. The looter's
                // remembered OPEN BAG choice is kept: a bag opened here closes with the window.
                if (player.inventoryWindows.InventoryOwner() is { } mine && player.inventoryWindows.bagOpen.Add(mine.Id)) lootOpenedBag = mine.Id;
                player.PlaceWindowRow();
                player.inventoryScroll.Remove("loot-bag");
            }
        }
        bool want = player.faithfulScreens.IsOpen("loot") && lootBagOpen && LootBody() is { ItemGrid: { BagWidth: > 0, BagHeight: > 0 } };
        if (want == player.faithfulScreens.IsOpen("loot-bag")) return;
        if (!want) { player.faithfulScreens.Close("loot-bag"); if (player.faithfulScreens.IsOpen("loot")) player.PlaceWindowRow(); return; }
        player.faithfulScreens.Open("loot-bag");
        player.PlaceWindowRow();
    }

    internal void BindLootScreen(string id, UiBindings b)
    {
        var body = LootBody();
        b.Text["Root"] = (id == "loot-bag" ? body?.ItemGrid?.Items.FirstOrDefault(i => i.Section == "backpack_attach")?.Name ?? "BACKPACK" : body?.Name ?? "INVENTORY").ToUpperInvariant();
        if (id == "loot-bag") { b.Hidden.Add("CapacityText"); b.Hidden.Add("TypeText"); return; }
        b.Properties["datapanel"] = new Dictionary<string, string> { ["SkipCaption"] = "true" };
        if (body?.ItemGrid is { BagWidth: > 0, BagHeight: > 0 }) { b.Disabled.Remove("OpenBagButton"); b.Actions["OpenBagButton"] = "OpenBagButton"; }
        // LIMBS opens the body's limbs window, as the inventory's does for its character (enabled by the same limb holder).
        if (body is not null && InventoryWindows.HasLimbWindow(body)) { b.Disabled.Remove("LimbsButton"); b.Actions["LimbsButton"] = "LimbsButton"; }
    }
}
