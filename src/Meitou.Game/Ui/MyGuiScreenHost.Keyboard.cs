using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// Keyboard side of the host: the edit boxes' clipboard (MyGUI 3.2.2 <c>EditBox</c>, docs/game/ui-input.md "Edit boxes") and Tab / Shift+Tab / Enter
/// / Space over the top window's edit boxes and buttons. MyGUI has no key traversal and no key-focus look for buttons (<b>Verified</b>:
/// <c>InputManager</c> and <c>Button</c> of 3.2.2), so the buttons' part is Meitou's.
/// </summary>
public sealed partial class MyGuiScreenHost
{
    #region Widget checks

    /// <summary>
    /// Whether property <paramref name="key"/> of <paramref name="c"/> is <c>true</c>.
    /// </summary>
    static bool Is(UiControl c, string key) => c.Properties.GetValueOrDefault(key) == "true";

    /// <summary>
    /// Whether <paramref name="c"/> has an area and some of it is inside its clip.
    /// </summary>
    static bool Visible(UiControl c) =>
        c.Bounds.Width > 0 && c.Bounds.Height > 0 && (c.Clip is not { } clip || c.Bounds.Intersect(clip) is { Width: > 0, Height: > 0 });

    /// <summary>
    /// Whether Tab stops at <paramref name="c"/>: an enabled, visible edit box that is neither static nor read-only.
    /// </summary>
    static bool IsEditStop(UiControl c) => c.Enabled && c.Type == "EditBox" && !Is(c, "Static") && !Is(c, "ReadOnly") && Visible(c);

    /// <summary>
    /// A button of the layout itself: not a window's close/pin, a scroll bar's part, a tab header or a list row (those are reached by the pointer).
    /// </summary>
    static bool IsButtonStop(UiControl c)
    {
        if (!c.Enabled || c.Type != "Button" || c.Pickable == false) return false;
        if (c.Action.StartsWith("mygui:", StringComparison.Ordinal) || c.Name.Contains('@', StringComparison.Ordinal)) return false;

        return !c.Properties.ContainsKey("TemplateOwner") && !c.Properties.ContainsKey("ListOwner") && Visible(c);
    }

    #endregion

    #region Edit boxes

    /// <summary>
    /// MyGUI's <c>EditBox</c> default for <c>MaxTextLength</c>, used for a box that sets none.
    /// </summary>
    const int DefaultMaxLength = 2048;

    /// <summary>
    /// The boxes' own clipboard, for when the system's is unavailable.
    /// </summary>
    string _localClipboard = "";

    /// <summary>
    /// The system clipboard's text (null: unavailable) and where copied text goes; the host that owns a window sets them. Without them the boxes
    /// share a clipboard of their own.
    /// </summary>
    public Func<string?>? ClipboardReader { get; set; }

    /// <summary>
    /// Where copied text goes; null: the boxes' own clipboard only.
    /// </summary>
    public Action<string>? ClipboardWriter { get; set; }

    /// <summary>
    /// A box's mode from its layout properties: <c>MaxTextLength</c>, <c>MultiLine</c> (not with <c>Password</c>), <c>WordWrap</c>, <c>Password</c>
    /// and <c>ReadOnly</c>.
    /// </summary>
    static UiTextBuffer NewBuffer(UiControl c, string text)
    {
        bool password = Is(c, "Password");
        string? limit = c.Properties.GetValueOrDefault("MaxTextLength");
        bool parsed = int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int max);
        int maxLength = parsed && max > 0 ? max : DefaultMaxLength;

        return new(text)
        {
            MaxLength = maxLength,
            MultiLine = Is(c, "MultiLine") && !password,
            WordWrap = Is(c, "WordWrap"),
            Password = password,
            ReadOnly = Is(c, "ReadOnly"),
        };
    }

    /// <summary>
    /// Puts the typing focus on an edit box; a read-only one (MyGUI focuses them to select and copy) keeps its text out of the screen's typed text,
    /// which would freeze it.
    /// </summary>
    void FocusEdit(string id, UiControl c)
    {
        var s = _screens[id];
        bool readOnly = Is(c, "ReadOnly");
        _keyButton = null;
        _focus = (id, c.Name);
        if (!readOnly) s.Text.TryAdd(c.Name, c.Caption);

        _edit = NewBuffer(c, readOnly ? c.Caption : s.Text[c.Name]);
    }

    /// <summary>
    /// The system clipboard's text, else the boxes' own.
    /// </summary>
    string? ReadClipboard()
    {
        try
        {
            return ClipboardReader?.Invoke() ?? _localClipboard;
        }
        catch (Exception)
        {
            return _localClipboard;   // no system clipboard: the boxes' own
        }
    }

    /// <summary>
    /// Copies <paramref name="text"/> to the boxes' own clipboard and the system's.
    /// </summary>
    void WriteClipboard(string text)
    {
        _localClipboard = text;
        try
        {
            ClipboardWriter?.Invoke(text);
        }
        catch (Exception)
        {
            /* no system clipboard: the boxes' own */
        }
    }

    /// <summary>
    /// Copy, Cut and Paste in the focused box (MyGUI <c>commandCopy</c>, <c>commandCut</c>, <c>commandPast</c>): a password box copies and cuts
    /// nothing, a read-only one cuts as a copy and takes no paste, pasting replaces the selection. Meitou leaves the clipboard alone when there is
    /// nothing to copy (MyGUI clears its own then).
    /// </summary>
    bool Clipboard(UiEditKey key)
    {
        if (_edit is null) return false;

        if (key == UiEditKey.Paste)
        {
            if (!_edit.ReadOnly && ReadClipboard() is { Length: > 0 } text)
            {
                _edit.Insert(text);
                PublishEdit();
            }

            return true;
        }

        if (_edit.Password || _edit.SelectionLength == 0) return true;

        WriteClipboard(_edit.SelectedText);
        if (key == UiEditKey.Cut && !_edit.ReadOnly)
        {
            _edit.DeleteSelection();
            PublishEdit();
        }

        return true;
    }

    /// <summary>
    /// Enter in the focused box (MyGUI): a multi-line box takes a line break (Ctrl+Enter accepts), any other accepts, which here is the default
    /// action; a read-only box does nothing. True when the box took it.
    /// </summary>
    public bool EditEnter(bool ctrl)
    {
        if (_edit is null || _edit.ReadOnly) return false;

        if (_edit.MultiLine && !ctrl)
        {
            _edit.Insert("\n");
            PublishEdit();
            return true;
        }

        Submit();
        return true;
    }

    #endregion

    #region Focus and buttons

    /// <summary>
    /// The keyboard focus ring's colour.
    /// </summary>
    static readonly Vector4 RingColour = new(0.769f, 0.627f, 0.384f, 1);   // brass-light #c4a062

    /// <summary>
    /// The focus ring's properties: it takes no pointer.
    /// </summary>
    static readonly Dictionary<string, string> NoMouse = new(StringComparer.Ordinal) { ["NeedMouse"] = "false" };

    /// <summary>
    /// The button that Enter and Space press (Meitou's keyboard focus), on the top window.
    /// </summary>
    (string Screen, string Widget)? _keyButton;

    /// <summary>
    /// Draw a ring (2 px <c>brass-light</c>, offset 2) round the button the keyboard is on; the Meitou UI mode's focus look (docs/meitou-ui.md). The
    /// Faithful mode has none.
    /// </summary>
    public bool FocusRing { get; set; }

    /// <summary>
    /// The focused box is read-only: it takes only the keys that read it.
    /// </summary>
    public bool FocusReadOnly => _edit?.ReadOnly == true;

    /// <summary>
    /// Tab / Shift+Tab over the top window's editable boxes and buttons in layout order (Meitou's; the first Tab goes to the first edit box, else the
    /// first button). On a window that is not modal and has no edit box it does nothing until a button has the focus, so Tab stays the game's
    /// change-squad key there; false: nothing taken.
    /// </summary>
    bool CycleFocus(bool backwards)
    {
        // Tab never moves into a HUD panel (it is change_squad there)
        if (_frame.LastOrDefault(f => !_screens[f.Id].Panel) is not { } top) return false;

        var stops = top.Controls.Where(c => IsEditStop(c) || IsButtonStop(c)).ToList();
        if (stops.Count == 0) return false;

        int current = stops.FindIndex(c => _focus == (top.Id, c.Name) || _keyButton == (top.Id, c.Name));
        int firstEdit = stops.FindIndex(IsEditStop);
        if (current < 0 && firstEdit < 0 && !_screens[top.Id].Modal) return false;

        int next = current < 0 ? backwards ? stops.Count - 1 : Math.Max(0, firstEdit) : (current + (backwards ? stops.Count - 1 : 1)) % stops.Count;
        if (stops[next].Type == "EditBox")
        {
            FocusEdit(top.Id, stops[next]);
        }
        else
        {
            _focus = null;
            _edit = null;
            _keyButton = (top.Id, stops[next].Name);
        }

        return true;
    }

    /// <summary>
    /// Enter or Space on the button the keyboard is on: it is pressed like a click on its middle (so tabs, sounds and events run as for the pointer).
    /// False: no button has the focus.
    /// </summary>
    public bool ActivateFocused()
    {
        if (_keyButton is not { } k || _frame.LastOrDefault() is not { } top || top.Id != k.Screen) return false;

        if (top.Controls.FirstOrDefault(c => c.Name == k.Widget && IsButtonStop(c)) is not { } button)
        {
            _keyButton = null;
            return false;
        }

        var at = new Vector2(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2);
        if (Pick(at) is { } hit && hit.Id == top.Id && hit.Control.Name == button.Name)
            PointerDown(at);
        else
            Activated?.Invoke(new(top.Id, button.Name, button.Action));

        // The press may have closed the window or opened another.
        if (_open.Count > 0 && _open[^1] == k.Screen) _keyButton = k;
        return true;
    }

    /// <summary>
    /// After a screen is arranged: a read-only focused box takes the text the screen shows now, and the button the keyboard is on gets the ring when
    /// <see cref="FocusRing"/> is on.
    /// </summary>
    void ArrangeKeyboard(string id, List<UiControl> controls)
    {
        if (_focus is { } f && f.Screen == id && _edit is { ReadOnly: true })
        {
            var box = controls.FirstOrDefault(c => c.Name == f.Widget);
            if (box is not null && box.Caption != _edit.Text) _edit.Set(box.Caption);
        }

        if (!FocusRing || _keyButton is not { } k || k.Screen != id) return;

        var button = controls.FirstOrDefault(c => c.Name == k.Widget && IsButtonStop(c));
        if (button is null) return;

        var b = button.Bounds;
        void Bar(string part, float x, float y, float w, float h) =>
            controls.Add(new("KeyFocusRing." + part, "Widget", new(x, y, w, h), "", "", "", true, NoMouse, RingColour, Pickable: false));
        Bar("top", b.X - 4, b.Y - 4, b.Width + 8, 2);
        Bar("bottom", b.X - 4, b.Y + b.Height + 2, b.Width + 8, 2);
        Bar("left", b.X - 4, b.Y - 2, 2, b.Height + 4);
        Bar("right", b.X + b.Width + 2, b.Y - 2, 2, b.Height + 4);
    }

    #endregion
}