using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// The code mods' buttons beside the HUD's jobs list (docs/modding.md "Character panel lines and job panel buttons",
/// <see cref="UiRegistration.AddJobPanelButtons"/>). The orders panel has no free room, so when a mod gives buttons the list (and its empty hint)
/// starts a job row lower in its frame and the buttons share that row above it, side by side across the list's width. A layout the row cannot be
/// made in (a UI mod's: the list or one of its parents not placed in fractions of its parent, or a template part) gets none, and without buttons the
/// layout is unchanged. A press runs the button shown for the character it was shown for.
/// </summary>
sealed class JobModButtons(PlayerInterface player)
{
    /// <summary>
    /// The interface action a button runs (registered by Meitou's own extension); its argument is the button's index in <see cref="_shown"/>.
    /// </summary>
    internal const string Action = "job-mod";

    /// <summary>
    /// The registered key of <see cref="Action"/>, which the buttons name.
    /// </summary>
    const string ButtonAction = "meitou:" + Action;

    /// <summary>
    /// The jobs list and its hint, which move down to make the row.
    /// </summary>
    const string List = "JobsPanel", Hint = "JobsPanelInfo";

    /// <summary>
    /// The buttons the panel last showed.
    /// </summary>
    IReadOnlyList<UiJobButton> _shown = [];

    /// <summary>
    /// The character <see cref="_shown"/> was laid out for, so a press after the selection changed does nothing.
    /// </summary>
    CharacterId _shownFor;

    /// <summary>
    /// The buttons' row height in pixels (a job row's).
    /// </summary>
    float _row;

    /// <summary>
    /// The layout <see cref="_path"/> was found in, and the widgets from a root down to the jobs list (null: the list cannot be moved).
    /// </summary>
    (MyGuiLayout? Layout, MyGuiWidget[]? Path) _path;

    /// <summary>
    /// Asks the mods for <paramref name="own"/>'s buttons (none without a mod that adds any, or when the layout cannot hold them) and moves the
    /// jobs list and its hint a job row down in their frame to make room.
    /// </summary>
    internal void Bind(UiBindings b, CharacterSnapshot? own)
    {
        _shown = [];
        if (!player.HasModJobButtons || own is null || ListPath() is not { } path) return;

        var parent = ScreenRect(path[..^1]);
        float row = (int)(24 * player.UiScale);
        float shift = row / parent.Height;
        var list = path[^1];
        if (!float.IsFinite(shift) || list.Position.Height < 2 * shift) return;

        _shown = player.ModJobButtons(own);
        _shownFor = own.Id;
        _row = row;
        if (_shown.Count == 0) return;

        b.RelativePositions[list] = Lowered(list.Position, shift);
        if (path[^2].Children.FirstOrDefault(c => c.Name == Hint) is { RealPosition: true } hint && hint.Position.Height > shift)
            b.RelativePositions[hint] = Lowered(hint.Position, shift);
    }

    /// <summary>
    /// <paramref name="r"/> (fractions of its parent) starting <paramref name="shift"/> lower, its bottom kept.
    /// </summary>
    static UiRect Lowered(UiRect r, float shift) => r with { Y = r.Y + shift, Height = r.Height - shift };

    /// <summary>
    /// The widgets from a root of the HUD's layout down to the jobs list, found once per layout; null when the list is missing, or it or a parent
    /// is not placed in fractions of its parent or is a template's part.
    /// </summary>
    MyGuiWidget[]? ListPath()
    {
        if (!ReferenceEquals(_path.Layout, player.FaithfulHud)) _path = (player.FaithfulHud, FindPath(player.FaithfulHud.Roots));
        return _path.Path;
    }

    /// <summary>
    /// The path from one of <paramref name="widgets"/> down to the jobs list, when each widget on it is placed in fractions; null otherwise.
    /// </summary>
    static MyGuiWidget[]? FindPath(IReadOnlyList<MyGuiWidget> widgets)
    {
        foreach (var w in widgets)
        {
            if (!w.RealPosition || w.Properties.ContainsKey("TemplateOwner")) continue;
            if (w.Name == List) return [w];
            if (FindPath(w.Children) is { } below) return [w, .. below];
        }

        return null;
    }

    /// <summary>
    /// The screen rectangle of the last widget of <paramref name="path"/>, each placed in fractions of the one before (the first of the screen).
    /// </summary>
    UiRect ScreenRect(MyGuiWidget[] path)
    {
        var rect = new UiRect(0, 0, player.Width, player.Height);
        foreach (var w in path) rect = w.Position.RelativeTo(rect, true);
        return rect;
    }

    /// <summary>
    /// The buttons over the row above the arranged jobs list, side by side across its width; nothing when none is shown or the row does not lie
    /// inside the orders panel.
    /// </summary>
    internal void AddContent(List<UiControl> controls)
    {
        if (_shown.Count == 0) return;

        var list = controls.FirstOrDefault(c => c.Name == List);
        var panel = controls.FirstOrDefault(c => c.Name == "OrdersPanel");
        if (list is null || panel is null) return;

        var row = list.Bounds with { Y = list.Bounds.Y - _row, Height = _row };
        if (!Inside(panel.Bounds, row)) return;

        float width = row.Width / _shown.Count;
        for (int k = 0; k < _shown.Count; k++)
            controls.Add(Button(k, row with { X = row.X + k * width, Width = width }, panel.Bounds));
    }

    /// <summary>
    /// Whether <paramref name="r"/> lies wholly inside <paramref name="panel"/>.
    /// </summary>
    static bool Inside(UiRect panel, UiRect r) =>
        r.X >= panel.X && r.Y >= panel.Y && r.X + r.Width <= panel.X + panel.Width && r.Y + r.Height <= panel.Y + panel.Height;

    /// <summary>
    /// Button <paramref name="k"/> in <paramref name="bounds"/> (inset a pixel on each side): the game's neutral button with its caption in the
    /// orders panel's painted face and the main colour, its tooltip the HUD's Fixed tooltip.
    /// </summary>
    UiControl Button(int k, UiRect bounds, UiRect? clip)
    {
        var button = _shown[k];
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FontName"] = "Kenshi_PaintedTextFont_Small",
            ["TextAlign"] = "Center",
            ["TextColour"] = PlayerInterface.MainColour,
        };
        if (button.Tooltip is { Length: > 0 } tip)
            foreach (var pair in UiTooltips.TextProperties(Loc.Tr(tip), "fixed")) properties[pair.Key] = pair.Value;

        var inset = new UiRect(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
        string name = "JobsPanel-mod-" + k.ToString(CultureInfo.InvariantCulture);
        return new(name, "Button", inset, Loc.Tr(button.Caption), ButtonAction, "Kenshi_Button1Skin", true, properties, Vector4.Zero,
            Pickable: true, Argument: k.ToString(CultureInfo.InvariantCulture), Clip: clip);
    }

    /// <summary>
    /// A press of button <paramref name="argument"/> (its index): runs it with the click sound while the jobs list still shows the character it was
    /// laid out for.
    /// </summary>
    internal void Press(string argument)
    {
        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) || (uint)k >= (uint)_shown.Count) return;
        if ((player.Selected().FirstOrDefault() ?? player.Lead())?.Id != _shownFor) return;

        player.InterfaceSounds.Sound("General_Click");
        _shown[k].Press();
    }
}
