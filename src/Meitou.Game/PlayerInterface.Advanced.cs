using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Title;
using Meitou.Game.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    /// <summary>
    /// The New game window's advanced options. Plain globals in the original (set to the defaults once at start-up, rewritten when a save is loaded, by the
    /// import window and by the options window): opening the window keeps the last edit or the last loaded save's values (docs/game/character-stats.md).
    /// </summary>
    internal NewGameSettings NewGameOptions { get; set; } = new();
    bool advancedOpen;
    /// <summary>The option whose help the panel shows: the last one under the pointer, kept while the panel is open (so the pointer can reach the help's scroll bar); -1: none.</summary>
    int advancedHelp = -1;
    NewGameHelpCatalog advancedHelpCatalog = new();

    // Both panels belong to the same modal screen, so the lower panel cannot block Begin, Cancel or the toggle above it.
    static MyGuiLayout WithAdvancedPanel(MyGuiLayout start, MyGuiLayout advanced)
    {
        static MyGuiWidget Prefix(MyGuiWidget w) => w with { Name = "advanced-" + w.Name, Children = w.Children.Select(Prefix).ToArray() };
        var panel = Prefix(advanced.Roots[0]) with { Type = "Widget" };
        return start with { Roots = [.. start.Roots, panel] };
    }

    void BindNewGamePanels(UiBindings b) => BindAdvancedPanels("new-game", b);

    /// <summary>
    /// The start window (and the import window) with the advanced options under it: the layout's size, its top left at 0.5 and 0.05 of the screen on the title
    /// (FUN_140918ef0, FUN_140481130), at 0.6 and 0.05 from the in-game menu (FUN_1409180e0); the options panel at its bottom left (FUN_14047ab70).
    /// </summary>
    void BindAdvancedPanels(string id, UiBindings b)
    {
        var root = faithfulScreens.Layout(id).Roots[0];
        var size = root.Position.RelativeTo(new(0, 0, Width, Height), root.RealPosition);
        float scale = Height / 1080f, width = Math.Min(size.Width, Width - 24), mainHeight = Math.Min(size.Height, Height - 24);
        float extra = advancedOpen ? Math.Max(0, Math.Min(Height * .43f, Height - 24 - mainHeight)) : 0;
        float x = Math.Min(Width * (TitleOpen || id == "import" ? 0.5f : 0.6f), Width - width), y = Math.Min(Height * 0.05f, Math.Max(0, Height - mainHeight - extra));
        // Meitou: centred across, the window with its options panel centred down (the menu stack is on the left, so the original's spot reads off-centre).
        if (UiMode == UiMode.Meitou) { x = (Width - width) / 2; y = Math.Max(0, (Height - mainHeight - extra) / 2); }
        b.Positions["Root"] = new(x, y, width, mainHeight);
        b.Positions["advanced-Root"] = new(x, y + mainHeight, width, extra);
        if (!advancedOpen) b.Hidden.Add("advanced-Root");
        b.Positions["advanced-Datapanel"] = new(x + 12 * scale, y + mainHeight + 42 * scale, width - 24 * scale, extra - 42 * scale - 80 * scale);
        b.Positions["advanced-TooltipPanel"] = new(x + 12 * scale, y + mainHeight + extra - 74 * scale, width - 24 * scale, 62 * scale);
    }

    bool AdvancedAction(MyGuiEvent e)
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

    void AddAdvancedContent(List<UiControl> controls, string screen = "new-game")
    {
        if (!advancedOpen || controls.FirstOrDefault(c => c.Name == "advanced-Datapanel") is not { } panel) return;
        float s = Height / 1080f;
        var area = panel.Bounds;
        // The labels sit on Kenshi_GenericTextBoxFlatSkin, the help panel on Kenshi_GenericTextBoxSkin; the value boxes' skin names no text colour, so they keep the palette ink.
        string labelInk = MyGuiColours.TemplateInk(uiResources, "Kenshi_GenericTextBoxFlatSkin");
        // The font resource's own size, so the Font size option (font size2) applies as it does to the help text below.
        Dictionary<string, string> Text(string align = "Left VCenter", bool value = false) => new()
        {
            ["FontName"] = "Kenshi_StandardFont_Medium", ["TextColour"] = value ? MyGuiColours.InkText : labelInk, ["TextAlign"] = align,
        };
        int sliders = NewGameSettings.Options.Length;
        float row = area.Height / (sliders + NewGameSettings.Ticks.Length), labelWidth = area.Width * .46f, arrow = 24 * s, valueWidth = 48 * s;
        for (int i = 0; i < NewGameSettings.Options.Length; i++)
        {
            var option = NewGameSettings.Options[i];
            float y = area.Y + i * row;
            // The option's name shows its help (only the name column, so the pointer can go down the right side to the help's scroll bar).
            if (new UiRect(area.X, y, labelWidth, row).Contains(mouse.X, mouse.Y) && advancedHelp != i) { advancedHelp = i; inventoryScroll.Remove(screen); }
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
            if (new UiRect(area.X, y, labelWidth, row).Contains(mouse.X, mouse.Y) && advancedHelp != sliders + i) { advancedHelp = sliders + i; inventoryScroll.Remove(screen); }
            controls.Add(new($"advanced-tick-label-{i}", "TextBox", new(area.X, y + 4 * s, labelWidth - 6 * s, row - 8 * s), Loc.Tr(tick.Label), "", "Kenshi_GenericTextBoxFlatSkin", true, Text(), Vector4.Zero, Pickable: false));
            string ticked = NewGameOptions.Ticked(i) ? "true" : "false";
            controls.Add(new($"advanced-tick-{i}", "Button", new(area.X + area.Width - valueWidth / 2 - box / 2, y + (row - box) / 2, box, box), "", $"advanced-tick:{i}", "Kenshi_TickBoxSkin", true,
                new Dictionary<string, string> { ["Checked"] = ticked, ["StateSelected"] = ticked }, Vector4.Zero));
        }
        if (controls.FirstOrDefault(c => c.Name == "advanced-TooltipPanel") is { } footer)
        {
            int helpLine = advancedHelp >= 0 && advancedHelp < sliders ? NewGameSettings.Options[advancedHelp].HelpLine
                : advancedHelp >= sliders && advancedHelp < sliders + NewGameSettings.Ticks.Length ? NewGameSettings.Ticks[advancedHelp - sliders].HelpLine : -1;
            string help = helpLine >= 0 ? advancedHelpCatalog.TextAt(helpLine) : "";
            var view = new UiRect(footer.Bounds.X + 8 * s, footer.Bounds.Y + 8 * s, footer.Bounds.Width - 16 * s, footer.Bounds.Height - 16 * s);
            var text = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextColour"] = MyGuiColours.TemplateInk(uiResources, "Kenshi_GenericTextBoxSkin"), ["TextAlign"] = "Left Top", ["WordWrap"] = "true" };
            var shown = new UiControl("advanced-help", "TextBox", view, help, "", "", true, text, Vector4.Zero, Pickable: false, Clip: view.Intersect(footer.Bounds));
            // A help text taller than the panel scrolls, with a vertical bar shown only then (the original's description box does so).
            float bar = 17 * s, content = uiPainter?.TextHeight(shown with { Bounds = view with { Width = view.Width - bar } }, Height, StandardPixels) ?? 0;
            float maximum = content > view.Height + 0.5f ? content - view.Height : 0;
            if (maximum > 0)
            {
                float scroll = Math.Clamp(inventoryScroll.GetValueOrDefault(screen), 0, maximum);
                inventoryScroll[screen] = scroll;
                inventoryViews[screen] = (view, maximum);
                shown = shown with { Bounds = view with { Width = view.Width - bar }, Properties = new Dictionary<string, string>(text) { ["TextScroll"] = scroll.ToString(CultureInfo.InvariantCulture) } };
                controls.Add(shown);
                AddScrollBar(screen, controls, new(view.X + view.Width - bar, view.Y, bar, view.Height), view.Height, content, scroll, maximum, footer.Clip);
            }
            else { inventoryViews.Remove(screen); inventoryTracks.Remove(screen); controls.Add(shown); }
        }
    }
}
