using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game;

/// <summary>
/// The Faithful frontend windows' placement and their line panels (docs/game/ui-screens.md "Title screen", "Load and save windows", "Start window").
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>
    /// The load and save windows at the layout's size, centred on the screen in play and with their left edge at the screen's centre on the title (the end of
    /// FUN_140480100); the import window at 0.5 and 0.05 of the screen with its advanced options under it (FUN_140481130).
    /// </summary>
    void PlaceSlotWindow(string id, UiBindings b)
    {
        if (id == "import") { BindAdvancedPanels(id, b); if (!advancedOpen) b.Hidden.Add("advanced-Root"); return; }
        var root = faithfulScreens.Layout(id).Roots[0];
        var size = root.Position.RelativeTo(new(0, 0, Width, Height), root.RealPosition);
        float w = Math.Min(size.Width, Width), h = Math.Min(size.Height, Height);
        float x = TitleOpen && UiMode == UiMode.Faithful ? Math.Min(Width / 2f, Width - w) : (Width - w) / 2;
        b.Positions[root.Name] = new(x, (Height - h) / 2, w, h);
        // Meitou: the save list ends above the buttons' row, where the Kenshi saves tick goes (WithSaveFilter).
        if (id == "load" && UiMode == UiMode.Meitou) b.Positions["GamesList"] = new(x + 0.0102941f * w, (Height - h) / 2 + 0.135385f * h, 0.644118f * w, 0.69f * h);
    }

    /// <summary>The load window with a Meitou row under its list: a tick that hides the base game's saves, styled as the reset tick (hidden in Faithful).</summary>
    static MyGuiLayout WithSaveFilter(MyGuiLayout load)
    {
        var root = load.Roots[0];
        MyGuiWidget Row(string type, string name, string skin, UiRect at, Dictionary<string, string> properties) =>
            new(type, name, skin, "", "Left Bottom", at, true, properties, new Dictionary<string, string>(), []);
        MyGuiWidget[] row =
        [
            Row("TextBox", "lbHideKenshiSaves", "Kenshi_GenericTextBoxFlat", new(0.0102941f, 0.836923f, 0.58f, 0.123077f), new() { ["Caption"] = "Hide Kenshi saves", ["TextAlign"] = "Left VCenter" }),
            Row("Button", HideKenshiTick, "Kenshi_TickBoxSkin", new(0.6029412f, 0.844615f, 0.0514706f, 0.107692f), []),
        ];
        return load with { Roots = [root with { Children = [.. root.Children, .. row] }, .. load.Roots.Skip(1)] };
    }

    /// <summary>
    /// The lines of a DatapanelGUI over the arranged widget <paramref name="panel"/> (FUN_1406f62a0, FUN_1406f9ab0, FUN_1406f7cb0): rows of 0.94 / max(1, (int)(<paramref name="rows"/> ×
    /// panel height / screen height)) of its height from 0.03 of it (rows 55 by default, FUN_1406fce20; the start window sets 40), the label from 3 % of the width and 96 % wide, a value right-aligned in 93 % from the same place. The last label of a
    /// <paramref name="lastWraps"/> panel is a wrapped paragraph down to the panel's bottom (the start's description).
    /// </summary>
    void AddDatapanel(List<UiControl> controls, string panel, IReadOnlyList<(string Label, string Value)> lines, bool lastWraps, float rows = 55)
    {
        if (controls.FirstOrDefault(c => c.Name == panel) is not { } host) return;
        var area = host.Bounds;
        float view = area.Height, width = area.Width;
        float pitch = 0.94f / Math.Max(1, (int)(rows * view / Math.Max(1, Height))) * view;
        var clip = area.Intersect(host.Clip ?? area);
        for (int i = 0; i < lines.Count; i++)
        {
            var (label, value) = lines[i];
            float top = area.Y + i * pitch + 0.03f * view;
            bool wrap = lastWraps && i == lines.Count - 1;
            var labelBounds = new UiRect(area.X + 0.03f * width, top, (wrap ? 0.96f : value.Length > 0 ? 0.93f : 0.96f) * width, wrap ? area.Y + area.Height - top : pitch);
            controls.Add(new($"{panel}-label-{i}", "TextBox", labelBounds, label, "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.TemplateInk(uiResources, "Kenshi_TextboxStandardText"), ["WordWrap"] = wrap ? "true" : "false" }, Vector4.Zero, Pickable: false, Clip: clip));
            if (value.Length == 0) continue;
            controls.Add(new($"{panel}-value-{i}", "TextBox", new(area.X + 0.03f * width, top, 0.93f * width, pitch), value, "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["TextAlign"] = "Right Top", ["TextColour"] = MyGuiColours.TemplateInk(uiResources, "Kenshi_TextboxStandardText") }, Vector4.Zero, Pickable: false, Clip: clip));
        }
    }

    /// <summary>
    /// The message box with its buttons centred as a group and its text in the palette's light Main colour, in both modes (Meitou's choice for readability: the base
    /// game leaves them where the layout puts them, left of centre, and the text in dark ink on the dark window).
    /// </summary>
    void StyleMessageBox(List<UiControl> controls)
    {
        if (controls.FirstOrDefault(c => c.Name == "Root") is not { } root) return;
        string[] buttons = [.. new[] { "ButtonA", "ButtonB", "ButtonC" }.Where(n => controls.Any(c => c.Name == n))];
        if (buttons.Length > 0)
        {
            var bounds = buttons.Select(n => controls.Last(c => c.Name == n).Bounds).ToArray();
            float left = bounds.Min(r => r.X), right = bounds.Max(r => r.X + r.Width);
            float dx = root.Bounds.X + (root.Bounds.Width - (right - left)) / 2 - left;
            for (int i = 0; i < controls.Count; i++)
                if (buttons.Any(n => controls[i].Name == n || controls[i].Name.StartsWith(n + "@", StringComparison.Ordinal)))
                    controls[i] = controls[i] with { Bounds = controls[i].Bounds with { X = controls[i].Bounds.X + dx } };
        }
        for (int i = 0; i < controls.Count; i++)
            if (controls[i].Name == "MessageText" || controls[i].Properties.GetValueOrDefault("CaptionOwner") == "MessageText")
                controls[i] = controls[i] with { Properties = new Dictionary<string, string>(controls[i].Properties) { ["TextColour"] = MainColour } };
    }

    /// <summary>The start's lines (FUN_140916620): difficulty, cash, play style as the record has them (not translated), then its description.</summary>
    void AddStartLines(List<UiControl> controls)
    {
        if (starts.Count == 0) return;
        var start = starts[startIndex];
        AddDatapanel(controls, "DescriptionPanel",
        [
            (Loc.Tr("Difficulty:"), start.Difficulty), (Loc.Tr("Cash:"), "c." + start.Money.ToString(CultureInfo.InvariantCulture)), (Loc.Tr("Play Style:"), start.Style),
            (start.Description, ""),
        ], lastWraps: true, rows: 40);
    }

    /// <summary>
    /// The selected save's lines in the info box (FUN_14047e780): its date, the CAMERA's version ("Ancient" when it has none), faction name, money, members, area and day;
    /// empty while nothing is selected.
    /// </summary>
    void AddSlotInfo(List<UiControl> controls)
    {
        if (selectedSlot is not { } slot || slotInfo is not { } info) return;
        AddDatapanel(controls, "InfoPanel",
        [
            (Loc.Tr("Date"), SlotDate(slot)), (Loc.Tr("File version"), info.Version.Length > 0 ? info.Version : "Ancient"), (Loc.Tr("Faction name"), info.Faction),
            (Loc.Tr("Funds"), "c." + info.Funds.ToString(CultureInfo.InvariantCulture)), (Loc.Tr("Members size"), info.Members.ToString(CultureInfo.InvariantCulture)),
            (Loc.Tr("Location"), info.Area), (Loc.Tr("Day"), info.Day.ToString(CultureInfo.InvariantCulture)),
        ], lastWraps: false);
    }
}
