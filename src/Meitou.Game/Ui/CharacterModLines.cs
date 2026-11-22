using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// The code mods' lines on the HUD's character panel (docs/modding.md "Character panel lines and job panel buttons",
/// <see cref="UiRegistration.AddCharacterPanelLines"/>): rows of <c>StatsText</c> after the game's stats lines, at their pitch and in their columns
/// (the label from 3 % of the panel, the value column from 35 % of the row), each lying wholly inside the panel; the lines that do not fit are left
/// out, and a layout without <c>StatsText</c> (a UI mod's) gets none. A press runs the line shown for the character it was shown for.
/// </summary>
sealed class CharacterModLines(PlayerInterface player)
{
    /// <summary>
    /// The interface action a line's button runs (registered by Meitou's own extension); its argument is the line's index in <see cref="_shown"/>.
    /// </summary>
    internal const string Action = "character-mod";

    /// <summary>
    /// The registered key of <see cref="Action"/>, which the buttons name.
    /// </summary>
    const string LineAction = "meitou:" + Action;

    /// <summary>
    /// The lines the panel last showed.
    /// </summary>
    IReadOnlyList<UiCharacterLine> _shown = [];

    /// <summary>
    /// The character <see cref="_shown"/> was laid out for, so a press after the panel changed characters does nothing.
    /// </summary>
    CharacterId _shownFor;

    /// <summary>
    /// Forgets the lines shown (the panel shows no character).
    /// </summary>
    internal void Clear() => _shown = [];

    /// <summary>
    /// Asks the mods for <paramref name="lead"/>'s lines (none without a mod that adds any) and adds those that fit in <c>StatsText</c> after its
    /// <paramref name="statsRows"/> rows of <paramref name="row"/> pixels; values take the standard face at <paramref name="valuePixels"/>.
    /// </summary>
    internal void AddContent(List<UiControl> controls, CharacterSnapshot lead, int statsRows, float row, float valuePixels)
    {
        Clear();
        if (!player.HasModCharacterLines || controls.FirstOrDefault(c => c.Name == "StatsText") is not { } host) return;

        _shown = player.ModCharacterLines(lead);
        _shownFor = lead.Id;
        var area = host.Bounds;
        for (int k = 0; k < _shown.Count; k++)
        {
            var line = new UiRect(area.X + area.Width * 0.03f, area.Y + (statsRows + k) * row, area.Width * 0.93f, row);
            if (line.Y + line.Height > area.Y + area.Height) break;

            AddLine(controls, k, line, valuePixels, host.Clip);
        }
    }

    /// <summary>
    /// Line <paramref name="k"/>'s controls in <paramref name="row"/>: one button across it, or its label and then its value as a button, over a bar
    /// or as text in the value column.
    /// </summary>
    void AddLine(List<UiControl> controls, int k, UiRect row, float valuePixels, UiRect? clip)
    {
        var line = _shown[k];
        string name = "StatsText-mod-" + k.ToString(CultureInfo.InvariantCulture);
        var column = row with { X = row.X + row.Width * 0.35f, Width = row.Width * 0.65f };
        var valueFace = Face("Kenshi_StandardFont_Medium", valuePixels);
        if (line.Press is not null && line.Value is null)
        {
            controls.Add(Button(name + "-button", line.Label, line.Tooltip, k, row, valueFace, clip));
            return;
        }

        var labelFace = new Dictionary<string, string> { ["FontName"] = "Kenshi_PaintedTextFont_Medium" };
        controls.Add(Text(name + "-label", line.Label, line.Tooltip, row with { Width = column.X - row.X }, labelFace, "Left Top",
            player.StatLabelColour, clip));
        if (line.Value is not { } value) return;

        if (line.Press is not null)
        {
            controls.Add(Button(name + "-button", value, line.Tooltip, k, column, valueFace, clip));
            return;
        }

        if (line.Bar is { } fill) ProgressLine.Add(controls, player.UiResources, player.Width, player.Height, column, fill, name + "-bar-", clip);
        string align = line.Bar is null ? "Right Top" : "HCenter Top";
        controls.Add(Text(name + "-value", value, line.Tooltip, column, valueFace, align, Ink(), clip));
    }

    /// <summary>
    /// The text ink of the panel's lines (<c>Kenshi_TextboxStandardText</c>'s, as the game's stats values).
    /// </summary>
    string Ink() => MyGuiColours.TemplateInk(player.UiResources, "Kenshi_TextboxStandardText");

    /// <summary>
    /// A font face: its name and its size in pixels (8 at least).
    /// </summary>
    static Dictionary<string, string> Face(string font, float pixels) => new()
    {
        ["FontName"] = font,
        ["FontPixels"] = Math.Max(8, (int)MathF.Round(pixels)).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// A text box as the stats lines draw theirs, a mouse target only with a tooltip.
    /// </summary>
    static UiControl Text(string name, string text, string? tooltip, UiRect bounds, Dictionary<string, string> face, string align, string colour,
        UiRect? clip)
    {
        var properties = new Dictionary<string, string>(face) { ["TextAlign"] = align, ["TextColour"] = colour };
        AddTooltip(properties, tooltip);
        return new(name, "TextBox", bounds, Loc.Tr(text), "", "Kenshi_TextboxStandardText", true, properties, Vector4.Zero,
            Pickable: tooltip is { Length: > 0 }, Clip: clip);
    }

    /// <summary>
    /// A button in <paramref name="bounds"/> (inset a pixel above and below, as the HUD's button lines) running line <paramref name="k"/>: the
    /// game's neutral button with its caption in the panel's main colour.
    /// </summary>
    static UiControl Button(string name, string caption, string? tooltip, int k, UiRect bounds, Dictionary<string, string> face, UiRect? clip)
    {
        var properties = new Dictionary<string, string>(face) { ["TextAlign"] = "Center", ["TextColour"] = PlayerInterface.MainColour };
        AddTooltip(properties, tooltip);
        var inset = bounds with { Y = bounds.Y + 1, Height = bounds.Height - 2 };
        return new(name, "Button", inset, Loc.Tr(caption), LineAction, "Kenshi_Button1Skin", true, properties, Vector4.Zero, Pickable: true,
            Argument: k.ToString(CultureInfo.InvariantCulture), Clip: clip);
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
    /// A press of line <paramref name="argument"/> (its index): runs it with the click sound while the panel still shows the character it was laid
    /// out for.
    /// </summary>
    internal void Press(string argument)
    {
        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) || (uint)k >= (uint)_shown.Count) return;
        if (player.Lead()?.Id != _shownFor || _shown[k].Press is not { } press) return;

        player.InterfaceSounds.Sound("General_Click");
        press();
    }
}
