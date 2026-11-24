using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// The code mods' buttons beside the HUD's jobs list (docs/modding.md "Character panel lines and job panel buttons",
/// <see cref="UiRegistration.AddJobPanelButtons(Func{UiCharacterContext, IEnumerable{UiJobButton}})"/>). The orders panel has no free room, so when
/// a mod gives buttons the list (and its empty hint) gives up room in its frame (<see cref="UiJobButtonLayout"/>): it starts as many job rows lower
/// as there are rows of buttons, which share them above it side by side across its width, or as many columns further right, the buttons stacked a
/// job row each in them. A layout the room cannot be made in (a UI mod's: the list or one of its parents not placed in fractions of its parent, or
/// a template part; or the list too small to keep a job row or a column of its own) gets none, and without buttons the layout is unchanged. A press
/// runs the button shown for the character it was shown for.
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
    /// The layout <see cref="_shown"/> was placed by.
    /// </summary>
    UiJobButtonLayout _layout = UiJobButtonLayout.Default;

    /// <summary>
    /// The layout <see cref="_path"/> was found in, and the widgets from a root down to the jobs list (null: the list cannot be moved).
    /// </summary>
    (MyGuiLayout? Layout, MyGuiWidget[]? Path) _path;

    /// <summary>
    /// Asks the mods for <paramref name="own"/>'s buttons (none without a mod that adds any, or when the layout cannot hold them) and moves the
    /// jobs list and its hint in their frame to make room.
    /// </summary>
    internal void Bind(UiBindings b, CharacterSnapshot? own)
    {
        _shown = [];
        if (!player.HasModJobButtons || own is null || ListPath() is not { } path) return;

        var parent = ScreenRect(path[..^1]);
        var shown = player.ModJobButtons(own);
        var layout = player.ModJobLayout;
        float row = (int)(24 * player.UiScale);
        if (shown.Count == 0 || Room(path[^1].Position, parent, layout, Lines(shown.Count, layout), row) is not { } room) return;

        _shown = shown;
        _shownFor = own.Id;
        _row = row;
        _layout = layout;
        b.RelativePositions[path[^1]] = Shrunk(path[^1].Position, room);
        if (path[^2].Children.FirstOrDefault(c => c.Name == Hint) is { RealPosition: true } hint && Fits(hint.Position, room))
            b.RelativePositions[hint] = Shrunk(hint.Position, room);
    }

    /// <summary>
    /// The rows (or columns) <paramref name="count"/> buttons take.
    /// </summary>
    static int Lines(int count, UiJobButtonLayout layout) => (count + layout.PerLine - 1) / layout.PerLine;

    /// <summary>
    /// The room the list gives up, in fractions of its <paramref name="parent"/>: (0, height) for rows, (width, 0) for columns; null when the list
    /// would keep less than a job row (or a column) of its own.
    /// </summary>
    static (float X, float Y)? Room(UiRect list, UiRect parent, UiJobButtonLayout layout, int lines, float row)
    {
        var room = layout.Arrangement == UiJobButtonArrangement.Columns
            ? (lines * layout.ColumnWidth * row / parent.Width, 0f)
            : (0f, lines * row / parent.Height);
        return float.IsFinite(room.Item1 + room.Item2) && Fits(list, room) ? room : null;
    }

    /// <summary>
    /// Whether <paramref name="r"/> keeps at least as much as it gives up.
    /// </summary>
    static bool Fits(UiRect r, (float X, float Y) room) => r.Width >= 2 * room.X && r.Height >= 2 * room.Y;

    /// <summary>
    /// <paramref name="r"/> (fractions of its parent) giving up <paramref name="room"/> on its left and top, its right and bottom kept.
    /// </summary>
    static UiRect Shrunk(UiRect r, (float X, float Y) room) =>
        new(r.X + room.X, r.Y + room.Y, r.Width - room.X, r.Height - room.Y);

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
    /// The buttons in the room the arranged jobs list gave up: rows above it side by side across its width, or columns left of it a job row each;
    /// nothing when none is shown, and no button that does not lie inside the orders panel.
    /// </summary>
    internal void AddContent(List<UiControl> controls)
    {
        if (_shown.Count == 0) return;

        var list = controls.FirstOrDefault(c => c.Name == List);
        var panel = controls.FirstOrDefault(c => c.Name == "OrdersPanel");
        if (list is null || panel is null) return;

        for (int k = 0; k < _shown.Count; k++)
        {
            var bounds = Place(list.Bounds, k);
            if (!Inside(panel.Bounds, bounds)) continue;

            controls.Add(Button(k, bounds, panel.Bounds));
            AddMarker(controls, k, bounds, panel.Bounds);
        }
    }

    /// <summary>
    /// Button <paramref name="k"/>'s style icon (a marker) in a square at the start of its caption, over the button; nothing without one.
    /// </summary>
    void AddMarker(List<UiControl> controls, int k, UiRect bounds, UiRect clip)
    {
        if (_shown[k].Style?.Icon is null) return;

        var inset = new UiRect(bounds.X + 3, bounds.Y + 3, bounds.Width - 6, bounds.Height - 6);
        UiLineStyle.PlaceIcon(controls, "JobsPanel-mod-" + k.ToString(CultureInfo.InvariantCulture), _shown[k].Style, inset, clip);
    }

    /// <summary>
    /// Button <paramref name="k"/>'s rectangle beside the arranged <paramref name="list"/>, by <see cref="_layout"/>.
    /// </summary>
    UiRect Place(UiRect list, int k)
    {
        int lines = Lines(_shown.Count, _layout), line = k / _layout.PerLine, at = k % _layout.PerLine;
        if (_layout.Arrangement == UiJobButtonArrangement.Columns)
        {
            float width = _layout.ColumnWidth * _row;
            return new(list.X - (lines - line) * width, list.Y + at * _row, width, _row);
        }

        float each = list.Width / Math.Min(_layout.PerLine, _shown.Count);
        return new(list.X + at * each, list.Y - (lines - line) * _row, each, _row);
    }

    /// <summary>
    /// Whether <paramref name="r"/> lies wholly inside <paramref name="panel"/>.
    /// </summary>
    static bool Inside(UiRect panel, UiRect r) =>
        r.X >= panel.X && r.Y >= panel.Y && r.X + r.Width <= panel.X + panel.Width && r.Y + r.Height <= panel.Y + panel.Height;

    /// <summary>
    /// Button <paramref name="k"/> in <paramref name="bounds"/> (inset a pixel on each side): the game's neutral button with its caption in the
    /// orders panel's painted face and the main colour (its own skin and caption colour when it names them), its tooltip the HUD's Fixed tooltip; a
    /// lit one in the skin's selected state.
    /// </summary>
    UiControl Button(int k, UiRect bounds, UiRect? clip)
    {
        var button = _shown[k];
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FontName"] = "Kenshi_PaintedTextFont_Small",
            ["TextAlign"] = "Center",
            ["TextColour"] = UiLineStyle.Ink(button.Style?.Label) ?? PlayerInterface.MainColour,
        };
        if (button.Lit)
        {
            properties["Checked"] = "true";
            properties["StateSelected"] = "true";
        }

        if (button.Tooltip is { Length: > 0 } tip)
            foreach (var pair in UiTooltips.TextProperties(Loc.Tr(tip), "fixed")) properties[pair.Key] = pair.Value;

        var inset = new UiRect(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
        string name = "JobsPanel-mod-" + k.ToString(CultureInfo.InvariantCulture);
        string skin = button.Skin is { } own && player.UiResources.Skins.ContainsKey(own) ? own : "Kenshi_Button1Skin";
        return new(name, "Button", inset, Loc.Tr(button.Caption), ButtonAction, skin, true, properties, Vector4.Zero,
            Pickable: true, Argument: k.ToString(CultureInfo.InvariantCulture), Clip: clip);
    }

    /// <summary>
    /// A press of button <paramref name="argument"/> (its index): runs it with the click sound (none for a silent one) while the jobs list still
    /// shows the character it was laid out for.
    /// </summary>
    internal void Press(string argument)
    {
        if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) || (uint)k >= (uint)_shown.Count) return;
        if ((player.Selected().FirstOrDefault() ?? player.Lead())?.Id != _shownFor) return;

        if (!_shown[k].Silent) player.InterfaceSounds.Sound("General_Click");
        _shown[k].Press();
    }
}
