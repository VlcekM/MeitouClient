using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Game.Ui;

namespace Meitou.Game;

/// <summary>
/// The character trading window (docs/game/ui-screens.md "Character trading", DA 41 / 42), shown while the world publishes one (<see cref="CharacterTradeView"/>), the same in both UI
/// modes: <c>Kenshi_CharacterSelectionWindow</c> with a <c>Kenshi_CharacterSelectionBox</c> row per character stacked in its scroll view. A row's tick box selects it; the total, the
/// selected count and the buyer's money are kept up to date; the confirm button is enabled while a row is ticked and the total is within the buyer's money (<c>FUN_1407281c0</c>),
/// else the total shows in the Bad colour. Confirm sends the ticked characters and closes the window; cancel, Esc or closing it sends the close.
/// </summary>
sealed partial class PlayerInterface
{
    const string CharacterTradeScreen = "character-trade";
    /// <summary>A row's height: the row layout's own (<c>Kenshi_CharacterSelectionBox</c> root, 0.180556 of the screen's height); its width is the scroll view's (engine choice).</summary>
    const float CharacterRowShare = 0.180556f;

    MyGuiLayout? characterRowLayout;
    /// <summary>The window on show (its <see cref="CharacterTradeView.Id"/>; 0 none), whether its closing was sent, and the ticked rows' characters.</summary>
    long shownCharacterTrade;
    bool characterTradeCloseSent;
    readonly HashSet<CharacterId> characterTradeTicked = [];

    /// <summary>Opens the window when the world opens one, closes it when the world's closes, and tells the world when the player closed it.</summary>
    void SyncCharacterTradeScreen()
    {
        if (faithfulScreens is null) return;
        var trade = session.CurrentSnapshot.CharacterTrade;
        if (trade is null)
        {
            if (shownCharacterTrade != 0 && faithfulScreens.IsOpen(CharacterTradeScreen)) faithfulScreens.Close(CharacterTradeScreen);
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
            CloseContextMenu();
            faithfulScreens.ResetScroll(CharacterTradeScreen);
            faithfulScreens.Open(CharacterTradeScreen);
            return;
        }
        if (!faithfulScreens.IsOpen(CharacterTradeScreen) && !characterTradeCloseSent)
        {
            Say(new CloseCharacterTrade { Tick = session.World.Tick });
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

    float CharacterRowHeight => Height * CharacterRowShare;

    void BindCharacterTradeScreen(UiBindings b)
    {
        if (session.CurrentSnapshot.CharacterTrade is not { } trade) return;
        var (count, total) = TickedRows(trade);
        bool affordable = total <= trade.BuyerMoney;
        b.Text["CaptionTextBox"] = trade.Title;
        b.Text["ConfirmButton"] = trade.ConfirmCaption;
        b.Text["CancelButton"] = Loc.Tr("Cancel");
        b.Text["lbTotalMoney"] = Loc.Tr("Total:");
        b.Text["MoneyAmountTextBox"] = "c." + Money(total);
        if (!affordable) b.Properties["MoneyAmountTextBox"] = new Dictionary<string, string> { ["TextColour"] = BadColour };
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
    void AddCharacterTradeRows(List<UiControl> controls)
    {
        if (session.CurrentSnapshot.CharacterTrade is not { } trade || characterRowLayout is null || controls.FirstOrDefault(c => c.Name == "CharacterSelectionItemBox") is not { } view) return;
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
            var b = new UiBindings { TemplateScale = InventoryScale };
            b.Text["NameTextBox"] = row.Name;
            b.Text["PriceTextBox"] = "c." + Money(row.Price);
            b.Actions["CheckBox"] = "character-tick:" + i.ToString(CultureInfo.InvariantCulture);
            b.Properties["CheckBox"] = new Dictionary<string, string> { ["Checked"] = ticked ? "true" : "false", ["StateSelected"] = ticked ? "true" : "false" };
            foreach (var c in MyGuiPresentation.Arrange(characterRowLayout, (int)MathF.Round(box.Width / rootShare.Item1), (int)MathF.Round(box.Height / rootShare.Item2), b, uiResources.Layers))
            {
                var bounds = c.Bounds with { X = c.Bounds.X + box.X, Y = c.Bounds.Y + box.Y };
                var own = c.Clip is { } inner ? (inner with { X = inner.X + box.X, Y = inner.Y + box.Y }).Intersect(clip) : clip;
                controls.Add(c with { Name = c.Name.Length > 0 ? prefix + c.Name : c.Name, Bounds = bounds, Clip = own });
                // The portrait goes on its frame, under the name nested in it.
                if (c.Name == "Portrait" && cells.TryGetValue(row.Character, out int cell))
                {
                    controls.Add(new(prefix + "portrait", "ImageBox", bounds, "", "", "", true,
                        new Dictionary<string, string> { ["ImageTexture"] = PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = PortraitRect(cell) },
                        Vector4.Zero, Pickable: false, Clip: own));
                }
            }
            foreach (var (panel, lines) in new[] { ("DataPanel1", row.Panel1), ("DataPanel2", row.Panel2) })
            {
                if (lines.Count == 0 || controls.FirstOrDefault(c => c.Name == prefix + panel) is not { } host) continue;
                AddLines(controls, prefix + panel, [.. lines.Select(l => new HudLine(l.Label, l.Value))], host.Bounds.Height / 5, StandardTextPixels * InventoryScale);
            }
        }
    }

    void CharacterTradeAction(MyGuiEvent e)
    {
        if (session.CurrentSnapshot.CharacterTrade is not { } trade) return;
        if (e.Action.StartsWith("character-tick:", StringComparison.Ordinal) && int.TryParse(e.Action.AsSpan(15), NumberStyles.None, CultureInfo.InvariantCulture, out int i))
        {
            if (i < trade.Rows.Count && !characterTradeTicked.Remove(trade.Rows[i].Character)) characterTradeTicked.Add(trade.Rows[i].Character);
        }
        else if (e.Widget == "ConfirmButton")
        {
            var (count, total) = TickedRows(trade);
            if (count == 0 || total > trade.BuyerMoney) return;
            Say(new ConfirmCharacterTrade([.. trade.Rows.Select(r => r.Character).Where(characterTradeTicked.Contains)]) { Tick = session.World.Tick });
            characterTradeCloseSent = true;
            faithfulScreens.Close(CharacterTradeScreen);
        }
        else if (e.Widget == "CancelButton") faithfulScreens.Close(CharacterTradeScreen);
    }

    /// <summary>
    /// The rows' characters whose portraits the host draws, each into an atlas cell after the squad bar's (and the squads screen's) and, while no conversation shows them, the
    /// conversation's two; the rows past the last cell show none.
    /// </summary>
    public IReadOnlyList<(CharacterId Character, int Cell)> CharacterTradePortraits()
    {
        if (session.CurrentSnapshot.CharacterTrade is not { } trade || !faithfulScreens.IsOpen(CharacterTradeScreen)) return [];
        int next = PortraitMembers().Count, end = session.CurrentSnapshot.Conversation is null ? PortraitCells : DialogueNpcCell;
        var list = new List<(CharacterId, int)>();
        foreach (var row in trade.Rows)
        {
            if (next >= end) break;
            list.Add((row.Character, next++));
        }
        return list;
    }
}
