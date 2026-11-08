using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Game.Title;

/// <summary>The New game window's advanced options (docs/game/character-stats.md): the panel and the settings a new game starts with.</summary>
sealed class AdvancedOptions(PlayerInterface player)
{
    /// <summary>
    /// The New game window's advanced options. Plain globals in the original (set to the defaults once at start-up, rewritten when a save is loaded, by the
    /// import window and by the options window): opening the window keeps the last edit or the last loaded save's values (docs/game/character-stats.md).
    /// </summary>
    internal NewGameSettings NewGameOptions { get; set; } = new();
    internal bool AdvancedOpen;
    /// <summary>The option whose help the panel shows: the last one under the pointer, kept while the panel is open (so the pointer can reach the help's scroll bar); -1: none.</summary>
    internal int AdvancedHelp = -1;
    internal NewGameHelpCatalog AdvancedHelpCatalog = new();

    // Both panels belong to the same modal screen, so the lower panel cannot block Begin, Cancel or the toggle above it.
    internal static MyGuiLayout WithAdvancedPanel(MyGuiLayout start, MyGuiLayout advanced)
    {
        static MyGuiWidget Prefix(MyGuiWidget w) => w with { Name = "advanced-" + w.Name, Children = w.Children.Select(Prefix).ToArray() };
        var panel = Prefix(advanced.Roots[0]) with { Type = "Widget" };
        return start with { Roots = [.. start.Roots, panel] };
    }

    internal void BindNewGamePanels(UiBindings b) => BindAdvancedPanels("new-game", b);

    /// <summary>
    /// The start window (and the import window) with the advanced options under it: the layout's size, its top left at 0.5 and 0.05 of the screen on the title
    /// (FUN_140918ef0, FUN_140481130), at 0.6 and 0.05 from the in-game menu (FUN_1409180e0); the options panel at its bottom left (FUN_14047ab70).
    /// </summary>
    internal void BindAdvancedPanels(string id, UiBindings b)
    {
        var root = player.FaithfulScreens.Layout(id).Roots[0];
        var size = root.Position.RelativeTo(new(0, 0, player.Width, player.Height), root.RealPosition);
        float scale = player.Height / 1080f, width = Math.Min(size.Width, player.Width - 24), mainHeight = Math.Min(size.Height, player.Height - 24);
        float extra = AdvancedOpen ? Math.Max(0, Math.Min(player.Height * .43f, player.Height - 24 - mainHeight)) : 0;
        float x = Math.Min(player.Width * (player.TitleOpen || id == "import" ? 0.5f : 0.6f), player.Width - width), y = Math.Min(player.Height * 0.05f, Math.Max(0, player.Height - mainHeight - extra));
        // Meitou: centred across, the window with its options panel centred down (the menu stack is on the left, so the original's spot reads off-centre).
        if (player.UiMode == UiMode.Meitou) { x = (player.Width - width) / 2; y = Math.Max(0, (player.Height - mainHeight - extra) / 2); }
        b.Positions["Root"] = new(x, y, width, mainHeight);
        b.Positions["advanced-Root"] = new(x, y + mainHeight, width, extra);
        if (!AdvancedOpen) b.Hidden.Add("advanced-Root");
        b.Positions["advanced-Datapanel"] = new(x + 12 * scale, y + mainHeight + 42 * scale, width - 24 * scale, extra - 42 * scale - 80 * scale);
        b.Positions["advanced-TooltipPanel"] = new(x + 12 * scale, y + mainHeight + extra - 74 * scale, width - 24 * scale, 62 * scale);
    }

    internal bool AdvancedAction(MyGuiEvent e)
    {
        var parts = e.Action.Split(':');
        if (parts.Length == 2 && parts[0] == "advanced-tick" && int.TryParse(parts[1], out int t) && t >= 0 && t < NewGameSettings.Ticks.Length)
        {
            NewGameOptions = NewGameOptions.SetTick(t, !NewGameOptions.Ticked(t));
            return true;
        }
        if (parts.Length < 2 || !parts[0].StartsWith("advanced-", StringComparison.Ordinal) ||
            !int.TryParse(parts[1], out int i) || i < 0 || i >= NewGameSettings.Options.Length) return false;
        var option = NewGameSettings.Options[i];
        float value = parts[0] switch
        {
            "advanced-slider" => option.Min + Math.Clamp(e.Value, 0, 1) * (option.Max - option.Min),
            "advanced-less" => NewGameOptions[i] - .05f,
            "advanced-more" => NewGameOptions[i] + .05f,
            _ => NewGameOptions[i],
        };
        NewGameOptions = NewGameOptions.Set(i, MathF.Round(value * 20) / 20);
        return true;
    }

    internal void AddAdvancedContent(List<UiControl> controls, string screen = "new-game")
    {
        if (!AdvancedOpen || controls.FirstOrDefault(c => c.Name == "advanced-Datapanel") is not { } panel) return;
        float s = player.Height / 1080f;
        var area = panel.Bounds;
        // The labels sit on Kenshi_GenericTextBoxFlatSkin, the help panel on Kenshi_GenericTextBoxSkin; the value boxes' skin names no text colour, so they keep the palette ink.
        string labelInk = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_GenericTextBoxFlatSkin");
        // The font resource's own size, so the Font size option (font size2) applies as it does to the help text below.
        Dictionary<string, string> Text(string align = "Left VCenter", bool value = false) => new()
        {
            ["FontName"] = "Kenshi_StandardFont_Medium",
            ["TextColour"] = value ? MyGuiColours.InkText : labelInk,
            ["TextAlign"] = align,
        };
        int sliders = NewGameSettings.Options.Length;
        float row = area.Height / (sliders + NewGameSettings.Ticks.Length), labelWidth = area.Width * .46f, arrow = 24 * s, valueWidth = 48 * s;
        for (int i = 0; i < NewGameSettings.Options.Length; i++)
        {
            var option = NewGameSettings.Options[i];
            float y = area.Y + i * row;
            // The option's name shows its help (only the name column, so the pointer can go down the right side to the help's scroll bar).
            if (new UiRect(area.X, y, labelWidth, row).Contains(player.Mouse.X, player.Mouse.Y) && AdvancedHelp != i) { AdvancedHelp = i; player.ScrollBars.InventoryScroll.Remove(screen); }
            var label = Text();
            controls.Add(new($"advanced-label-{i}", "TextBox", new(area.X, y + 4 * s, labelWidth - 6 * s, row - 8 * s), Loc.Tr(option.Label), "", "Kenshi_GenericTextBoxFlatSkin", true, label, Vector4.Zero, Pickable: false));
            float left = area.X + labelWidth, right = area.X + area.Width - valueWidth - arrow;
            controls.Add(new($"advanced-less-{i}", "Button", new(left, y + (row - arrow) / 2, arrow, arrow), "", $"advanced-less:{i}", "Kenshi_SpeedArrowLeftSkin", true, new Dictionary<string, string>(), Vector4.Zero));
            controls.Add(new($"advanced-more-{i}", "Button", new(right, y + (row - arrow) / 2, arrow, arrow), "", $"advanced-more:{i}", "Kenshi_SpeedArrowRightSkin", true, new Dictionary<string, string>(), Vector4.Zero));
            var track = new UiRect(left + arrow + 6 * s, y + (row - 18 * s) / 2, right - left - arrow - 12 * s, 18 * s);
            controls.Add(new($"advanced-track-{i}", "Widget", track, "", $"advanced-slider:{i}", "Kenshi_SliderSkin", true, new Dictionary<string, string> { ["Slider"] = "true" }, Vector4.Zero, Pickable: true));
            float t = (NewGameOptions[i] - option.Min) / (option.Max - option.Min);
            controls.Add(new($"advanced-knob-{i}", "Button", new(track.X + t * track.Width - 6 * s, y + (row - 28 * s) / 2, 12 * s, 28 * s), "", "", "Kenshi_SliderButtonSkin", true, new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
            controls.Add(new($"advanced-value-{i}", "TextBox", new(right + arrow, y + 4 * s, valueWidth, row - 8 * s), NewGameOptions[i].ToString("0.##", CultureInfo.InvariantCulture), "", "Kenshi_SpeedTextboxSkin", true, Text("Center", true), Vector4.Zero, Pickable: false));
        }
        // The ticks under the sliders (FUN_1409151d0: "Bandits loot the player", "Easy prospecting"), the box at the right end of the row.
        for (int i = 0; i < NewGameSettings.Ticks.Length; i++)
        {
            var tick = NewGameSettings.Ticks[i];
            float y = area.Y + (sliders + i) * row, box = Math.Min(row - 6 * s, 28 * s);
            if (new UiRect(area.X, y, labelWidth, row).Contains(player.Mouse.X, player.Mouse.Y) && AdvancedHelp != sliders + i) { AdvancedHelp = sliders + i; player.ScrollBars.InventoryScroll.Remove(screen); }
            controls.Add(new($"advanced-tick-label-{i}", "TextBox", new(area.X, y + 4 * s, labelWidth - 6 * s, row - 8 * s), Loc.Tr(tick.Label), "", "Kenshi_GenericTextBoxFlatSkin", true, Text(), Vector4.Zero, Pickable: false));
            string ticked = NewGameOptions.Ticked(i) ? "true" : "false";
            controls.Add(new($"advanced-tick-{i}", "Button", new(area.X + area.Width - valueWidth / 2 - box / 2, y + (row - box) / 2, box, box), "", $"advanced-tick:{i}", "Kenshi_TickBoxSkin", true,
                new Dictionary<string, string> { ["Checked"] = ticked, ["StateSelected"] = ticked }, Vector4.Zero));
        }
        if (controls.FirstOrDefault(c => c.Name == "advanced-TooltipPanel") is { } footer)
        {
            int helpLine = AdvancedHelp >= 0 && AdvancedHelp < sliders ? NewGameSettings.Options[AdvancedHelp].HelpLine
                : AdvancedHelp >= sliders && AdvancedHelp < sliders + NewGameSettings.Ticks.Length ? NewGameSettings.Ticks[AdvancedHelp - sliders].HelpLine : -1;
            string help = helpLine >= 0 ? AdvancedHelpCatalog.TextAt(helpLine) : "";
            var view = new UiRect(footer.Bounds.X + 8 * s, footer.Bounds.Y + 8 * s, footer.Bounds.Width - 16 * s, footer.Bounds.Height - 16 * s);
            var text = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextColour"] = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_GenericTextBoxSkin"), ["TextAlign"] = "Left Top", ["WordWrap"] = "true" };
            var shown = new UiControl("advanced-help", "TextBox", view, help, "", "", true, text, Vector4.Zero, Pickable: false, Clip: view.Intersect(footer.Bounds));
            // A help text taller than the panel scrolls, with a vertical bar shown only then (the original's description box does so).
            float bar = 17 * s, content = player.UiPainter?.TextHeight(shown with { Bounds = view with { Width = view.Width - bar } }, player.Height, player.StandardPixels) ?? 0;
            float maximum = content > view.Height + 0.5f ? content - view.Height : 0;
            if (maximum > 0)
            {
                float scroll = Math.Clamp(player.ScrollBars.InventoryScroll.GetValueOrDefault(screen), 0, maximum);
                player.ScrollBars.InventoryScroll[screen] = scroll;
                player.ScrollBars.InventoryViews[screen] = (view, maximum);
                shown = shown with { Bounds = view with { Width = view.Width - bar }, Properties = new Dictionary<string, string>(text) { ["TextScroll"] = scroll.ToString(CultureInfo.InvariantCulture) } };
                controls.Add(shown);
                player.ScrollBars.AddScrollBar(screen, controls, new(view.X + view.Width - bar, view.Y, bar, view.Height), view.Height, content, scroll, maximum, footer.Clip);
            }
            else { player.ScrollBars.InventoryViews.Remove(screen); player.ScrollBars.InventoryTracks.Remove(screen); controls.Add(shown); }
        }
    }
}