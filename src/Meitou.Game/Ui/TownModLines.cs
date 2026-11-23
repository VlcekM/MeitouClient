using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation.Production;

namespace Meitou.Game.Ui;

/// <summary>
/// The code mods' lines on the HUD's town panel (docs/modding.md "Town panel lines", <see cref="UiRegistration.AddTownPanelLines"/>). When the panel is
/// bound the mods are asked for the shown outpost's lines and the panel grows upward by the layout's row pitch per line: its lower edge stays, its
/// own rows keep their distance from its top (so they move up with it), and the lines are drawn in the new rows below Battery charge, each a copy
/// of that row: the label as the row's label, the value or a button in the bars' column, or one button across the row. A layout the lines cannot
/// be placed in (a UI mod's) gets none. A press runs the line shown for the outpost it was shown for.
/// </summary>
sealed class TownModLines(PlayerInterface player)
{
    /// <summary>
    /// The press action prefix of a line's button (then the line's index in <see cref="_shown"/>).
    /// </summary>
    internal const string LineAction = "town-mod-line:";

    /// <summary>
    /// The layout's last and second last label rows (their distance is the row pitch) and the last row's bar (the value column).
    /// </summary>
    const string LastLabel = "lbOutpostBattery", PreviousLabel = "lbOutpostPower", LastBar = "BatteryProgressBar";

    /// <summary>
    /// The lines the panel last showed.
    /// </summary>
    IReadOnlyList<UiTownLine> _shown = [];

    /// <summary>
    /// The name of the panel's root widget, whose arranged rectangle the lines must lie in.
    /// </summary>
    string _root = "";

    /// <summary>
    /// The outpost <see cref="_shown"/> was laid out for (<see cref="OutpostSnapshot.Number"/>), so a press after the panel changed outposts does
    /// nothing.
    /// </summary>
    int _shownFor;

    /// <summary>
    /// Asks the mods for <paramref name="town"/>'s lines (none without a mod that adds any, or when the panel's layout cannot hold them) and grows
    /// the panel's root upward to hold them.
    /// </summary>
    internal void Bind(UiBindings b, OutpostSnapshot town, bool owned)
    {
        Clear();
        if (!player.HasModTownLines) return;

        var layout = player.FaithfulScreens.Layout(TownPanel.TownScreen);
        if (layout.Roots.Count == 0) return;

        var root = layout.Roots[0];
        var panel = root.Position.RelativeTo(new UiRect(0, 0, player.Width, player.Height), root.RealPosition);
        if (RowPitch(root, panel) is not { } pitch) return;

        _shown = player.ModTownLines(town, owned, player.Session.CurrentSnapshot);
        _shownFor = town.Number;
        _root = root.Name;
        if (_shown.Count == 0) return;

        // Made at its layout size and placed taller: its rows keep their places from its top (MyGUI's Left Top), the new ones fill the growth.
        float growth = pitch * _shown.Count;
        b.Positions[root.Name] = panel with { Y = panel.Y - growth, Height = panel.Height + growth };
        b.MadeSizes[root.Name] = (panel.Width, panel.Height);
    }

    /// <summary>
    /// Forgets the lines shown (the panel shows no outpost).
    /// </summary>
    internal void Clear() => _shown = [];

    /// <summary>
    /// The lines' controls over the arranged panel, below its last row: nothing when no line is shown or the rows they copy were not arranged, and
    /// no line whose row does not lie inside the arranged panel.
    /// </summary>
    internal void AddContent(List<UiControl> controls)
    {
        if (_shown.Count == 0) return;

        var panel = controls.FirstOrDefault(c => c.Name == _root);
        var label = controls.FirstOrDefault(c => c.Name == LastLabel);
        var previous = controls.FirstOrDefault(c => c.Name == PreviousLabel);
        var bar = controls.FirstOrDefault(c => c.Name == LastBar);
        if (panel is null || label is null || previous is null || bar is null) return;

        float pitch = label.Bounds.Y - previous.Bounds.Y;
        for (int k = 0; k < _shown.Count; k++)
        {
            var row = label.Bounds with { Y = label.Bounds.Y + (k + 1) * pitch };
            var column = row with { X = bar.Bounds.X, Width = bar.Bounds.Width };
            if (!Inside(panel.Bounds, row) || !Inside(panel.Bounds, column)) continue;

            AddLine(controls, k, label, row, column);
        }
    }

    /// <summary>
    /// Whether <paramref name="r"/> lies wholly inside <paramref name="panel"/>.
    /// </summary>
    static bool Inside(UiRect panel, UiRect r) =>
        r.X >= panel.X && r.Y >= panel.Y && r.X + r.Width <= panel.X + panel.Width && r.Y + r.Height <= panel.Y + panel.Height;

    /// <summary>
    /// Line <paramref name="k"/>'s controls: its icon, its label in <paramref name="row"/> and its value or button in <paramref name="column"/>, or
    /// one button across both; the line style's colours over the panel's.
    /// </summary>
    void AddLine(List<UiControl> controls, int k, UiControl label, UiRect row, UiRect column)
    {
        var line = _shown[k];
        string name = "TownModLine" + k.ToString(CultureInfo.InvariantCulture);
        row = UiLineStyle.PlaceIcon(controls, name, line.Style, row, label.Clip);
        string? labelInk = UiLineStyle.Ink(line.Style?.Label), valueInk = UiLineStyle.Ink(line.Style?.Value);
        if (line.Press is not null && line.Value is null)
        {
            var across = row with { Width = column.X + column.Width - row.X };
            controls.Add(Button(name + "Button", line.Label, line.Tooltip, k, label, across, labelInk));
            return;
        }

        controls.Add(Text(name + "Label", line.Label, line.Tooltip, label, row, null, labelInk));
        if (line.Value is not { } value) return;

        controls.Add(line.Press is null
            ? Text(name + "Value", value, line.Tooltip, label, column, "Center", valueInk)
            : Button(name + "Button", value, line.Tooltip, k, label, column, valueInk));
    }

    /// <summary>
    /// A text in <paramref name="bounds"/> as the panel's labels draw theirs (a copy of <paramref name="label"/>, its alignment
    /// <paramref name="align"/> when given), a mouse target only with a tooltip.
    /// </summary>
    static UiControl Text(string name, string text, string? tooltip, UiControl label, UiRect bounds, string? align, string? ink)
    {
        var properties = new Dictionary<string, string>(label.Properties, StringComparer.Ordinal) { ["Caption"] = text };
        if (align is not null) properties["TextAlign"] = align;
        if (ink is not null) properties["TextColour"] = ink;
        AddTooltip(properties, tooltip);
        return label with
        {
            Name = name, Bounds = bounds, Caption = Loc.Tr(text), Action = "", Properties = properties, Pickable = tooltip is { Length: > 0 },
        };
    }

    /// <summary>
    /// A button in <paramref name="bounds"/> (inset a pixel above and below, as the HUD's button lines) running line <paramref name="k"/>: the
    /// game's neutral button with its caption in the label font and the panel's main colour, as the building panel's own action lines.
    /// </summary>
    static UiControl Button(string name, string caption, string? tooltip, int k, UiControl label, UiRect bounds, string? ink)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FontName"] = label.Properties.GetValueOrDefault("FontName") ?? "Kenshi_StandardFont_Medium",
            ["TextAlign"] = "Center",
            ["TextColour"] = ink ?? PlayerInterface.MainColour,
        };
        AddTooltip(properties, tooltip);
        string action = LineAction + k.ToString(CultureInfo.InvariantCulture);
        var inset = bounds with { Y = bounds.Y + 1, Height = bounds.Height - 2 };
        return new(name, "Button", inset, Loc.Tr(caption), action, "Kenshi_Button1Skin", true, properties, Vector4.Zero, Pickable: true,
            Clip: label.Clip);
    }

    /// <summary>
    /// The line's tooltip (the HUD's Fixed tooltip) on <paramref name="properties"/>; nothing for none.
    /// </summary>
    static void AddTooltip(Dictionary<string, string> properties, string? tooltip)
    {
        if (tooltip is not { Length: > 0 }) return;

        foreach (var pair in UiTooltips.TextProperties(Loc.Tr(tooltip), "fixed")) properties[pair.Key] = pair.Value;
    }

    /// <summary>
    /// A press of line <paramref name="action"/>: runs it with the click sound while the panel still shows the outpost it was laid out for.
    /// </summary>
    internal void Press(string action, int shownTown)
    {
        if (!int.TryParse(action.AsSpan(LineAction.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int k)) return;
        if ((uint)k >= (uint)_shown.Count || shownTown != _shownFor || _shown[k].Press is not { } press) return;

        player.InterfaceSounds.Sound("General_Click");
        press();
    }

    /// <summary>
    /// The distance in pixels between the layout's last two label rows within <paramref name="panel"/>, the rectangle of <paramref name="root"/>;
    /// null when the lines cannot be placed in the layout (a UI mod's): the two rows or the last bar are not children of the root itself, a child
    /// of the root is not kept at its distance from the top (aligned to the bottom, centred or stretched, it would move into the new rows), or the
    /// rows do not run downward.
    /// </summary>
    static float? RowPitch(MyGuiWidget root, UiRect panel)
    {
        var last = root.Children.FirstOrDefault(c => c.Name == LastLabel);
        var previous = root.Children.FirstOrDefault(c => c.Name == PreviousLabel);
        if (last is null || previous is null || root.Children.All(c => c.Name != LastBar)) return null;
        if (root.Children.Any(c => !UiRect.TopAnchored(c.Align))) return null;

        float pitch = last.Position.RelativeTo(panel, last.RealPosition).Y - previous.Position.RelativeTo(panel, previous.RealPosition).Y;
        return pitch > 0 ? pitch : null;
    }
}
