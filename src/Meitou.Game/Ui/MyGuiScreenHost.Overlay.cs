using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// The mod overlays' text boxes (<see cref="UiNodeKind.TextBox"/>): HUD controls outside every screen that take the keys through the screens' edit
/// focus, caret, undo and clipboard. Their texts are kept under <see cref="OverlayScreen"/>, a screen that is registered on first use and never opened;
/// their text events carry it as their screen.
/// </summary>
public sealed partial class MyGuiScreenHost
{
    /// <summary>
    /// The screen id the overlay text boxes' focus and text events carry.
    /// </summary>
    public const string OverlayScreen = "ui-overlay";

    /// <summary>
    /// The property that marks an overlay control as a text box.
    /// </summary>
    public const string OverlayEditProperty = "OverlayEdit";

    /// <summary>
    /// Whether <paramref name="c"/> is an overlay text box.
    /// </summary>
    static bool IsOverlayEdit(UiControl c) => c.Type == "EditBox" && c.Properties.ContainsKey(OverlayEditProperty);

    /// <summary>
    /// A press at <paramref name="p"/> on an overlay control: an enabled text box takes the keys and places the caret as a screen's box does (a
    /// disabled one only takes the click). False for any other control.
    /// </summary>
    public bool PressOverlayEdit(UiControl c, Vector2 p)
    {
        if (!IsOverlayEdit(c)) return false;
        if (!c.Enabled) return true;

        SyncOverlayText(c);
        PressEdit(OverlayScreen, c, p);
        return true;
    }

    /// <summary>
    /// After the overlay is arranged: each text box's text is the node's (the focused box's buffer takes it when it differs), and the focused one
    /// shows its caret and selection. The focus lets go of a box that is gone or disabled.
    /// </summary>
    public void ArrangeOverlayEdits(List<UiControl> controls)
    {
        bool shown = false;
        for (int i = 0; i < controls.Count; i++)
        {
            var c = controls[i];
            if (!IsOverlayEdit(c)) continue;

            SyncOverlayText(c);
            if (_focus != (OverlayScreen, c.Name) || _edit is null || !c.Enabled) continue;

            controls[i] = WithCaret(c, _edit);
            shown = true;
        }

        if (!shown && _focus?.Screen == OverlayScreen) Blur();
    }

    /// <summary>
    /// Keeps a text box's text as its node gives it.
    /// </summary>
    void SyncOverlayText(UiControl c)
    {
        _screens.TryAdd(OverlayScreen, new(new MyGuiLayout("3.2.0", []), false, false, panel: true));
        _screens[OverlayScreen].Text[c.Name] = c.Caption;
        if (_focus == (OverlayScreen, c.Name) && _edit is not null && _edit.Text != c.Caption) _edit.Set(c.Caption);
    }

    /// <summary>
    /// <paramref name="c"/> drawn focused, with <paramref name="edit"/>'s caret and selection.
    /// </summary>
    static UiControl WithCaret(UiControl c, UiTextBuffer edit)
    {
        var properties = new Dictionary<string, string>(c.Properties, StringComparer.Ordinal)
        {
            ["Focused"] = "true",
            ["CaretIndex"] = edit.Caret.ToString(CultureInfo.InvariantCulture),
            ["SelectionStart"] = edit.SelectionStart.ToString(CultureInfo.InvariantCulture),
            ["SelectionLength"] = edit.SelectionLength.ToString(CultureInfo.InvariantCulture),
        };

        return c with { Properties = properties };
    }
}
