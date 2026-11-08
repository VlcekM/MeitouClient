using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Ui;

/// <summary>
/// Keyboard side of the host: the edit boxes' clipboard (MyGUI 3.2.2 <c>EditBox</c>, docs/game/ui-input.md "Edit boxes") and Tab / Shift+Tab / Enter / Space over the top window's edit boxes and buttons.
/// MyGUI has no key traversal and no key-focus look for buttons (<b>Verified</b>: <c>InputManager</c> and <c>Button</c> of 3.2.2), so the buttons' part is Meitou's.
/// </summary>
public sealed partial class MyGuiScreenHost
{
    /// <summary>MyGUI's <c>EditBox</c> default for <c>MaxTextLength</c>, used for a box that sets none.</summary>
    const int DefaultMaxLength = 2048;
    static readonly Vector4 RingColour = new(0.769f, 0.627f, 0.384f, 1);   // brass-light #c4a062
    static readonly Dictionary<string, string> NoMouse = new(StringComparer.Ordinal) { ["NeedMouse"] = "false" };
    /// <summary>The button that Enter and Space press (Meitou's keyboard focus), on the top window.</summary>
    (string Screen, string Widget)? keyButton;
    string localClipboard = "";

    /// <summary>The system clipboard's text (null: unavailable) and where copied text goes; the host that owns a window sets them. Without them the boxes share a clipboard of their own.</summary>
    public Func<string?>? ClipboardReader { get; set; }
    public Action<string>? ClipboardWriter { get; set; }
    /// <summary>Draw a ring (2 px <c>brass-light</c>, offset 2) round the button the keyboard is on; the Meitou UI mode's focus look (docs/meitou-ui.md). The Faithful mode has none.</summary>
    public bool FocusRing { get; set; }
    /// <summary>The focused box is read-only: it takes only the keys that read it.</summary>
    public bool FocusReadOnly => edit?.ReadOnly == true;

    static bool Is(UiControl c, string key) => c.Properties.GetValueOrDefault(key) == "true";
    static bool Visible(UiControl c) => c.Bounds.Width > 0 && c.Bounds.Height > 0 && (c.Clip is not { } clip || c.Bounds.Intersect(clip) is { Width: > 0, Height: > 0 });
    static bool IsEditStop(UiControl c) => c.Enabled && c.Type == "EditBox" && !Is(c, "Static") && !Is(c, "ReadOnly") && Visible(c);
    /// <summary>A button of the layout itself: not a window's close/pin, a scroll bar's part, a tab header or a list row (those are reached by the pointer).</summary>
    static bool IsButtonStop(UiControl c) => c.Enabled && c.Type == "Button" && c.Pickable != false && !c.Action.StartsWith("mygui:", StringComparison.Ordinal) && !c.Name.Contains('@', StringComparison.Ordinal)
        && !c.Properties.ContainsKey("TemplateOwner") && !c.Properties.ContainsKey("ListOwner") && Visible(c);

    /// <summary>A box's mode from its layout properties: <c>MaxTextLength</c>, <c>MultiLine</c> (not with <c>Password</c>), <c>WordWrap</c>, <c>Password</c> and <c>ReadOnly</c>.</summary>
    static UiTextBuffer NewBuffer(UiControl c, string text)
    {
        bool password = Is(c, "Password");
        return new(text)
        {
            MaxLength = int.TryParse(c.Properties.GetValueOrDefault("MaxTextLength"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int max) && max > 0 ? max : DefaultMaxLength,
            MultiLine = Is(c, "MultiLine") && !password, WordWrap = Is(c, "WordWrap"), Password = password, ReadOnly = Is(c, "ReadOnly"),
        };
    }

    /// <summary>Puts the typing focus on an edit box; a read-only one (MyGUI focuses them to select and copy) keeps its text out of the screen's typed text, which would freeze it.</summary>
    void FocusEdit(string id, UiControl c)
    {
        var s = screens[id];
        bool readOnly = Is(c, "ReadOnly");
        keyButton = null;
        focus = (id, c.Name);
        if (!readOnly) s.Text.TryAdd(c.Name, c.Caption);
        edit = NewBuffer(c, readOnly ? c.Caption : s.Text[c.Name]);
    }

    string? ReadClipboard()
    {
        try { return ClipboardReader?.Invoke() ?? localClipboard; }
        catch (Exception) { return localClipboard; }   // no system clipboard: the boxes' own
    }

    void WriteClipboard(string text)
    {
        localClipboard = text;
        try { ClipboardWriter?.Invoke(text); }
        catch (Exception) { /* no system clipboard: the boxes' own */ }
    }

    /// <summary>
    /// Copy, Cut and Paste in the focused box (MyGUI <c>commandCopy</c>, <c>commandCut</c>, <c>commandPast</c>): a password box copies and cuts nothing, a read-only one cuts as a copy and takes no paste, pasting replaces the selection.
    /// Meitou leaves the clipboard alone when there is nothing to copy (MyGUI clears its own then).
    /// </summary>
    bool Clipboard(UiEditKey key)
    {
        if (edit is null) return false;
        if (key == UiEditKey.Paste)
        {
            if (!edit.ReadOnly && ReadClipboard() is { Length: > 0 } text) { edit.Insert(text); PublishEdit(); }
            return true;
        }
        if (edit.Password || edit.SelectionLength == 0) return true;
        WriteClipboard(edit.SelectedText);
        if (key == UiEditKey.Cut && !edit.ReadOnly) { edit.DeleteSelection(); PublishEdit(); }
        return true;
    }

    /// <summary>
    /// Enter in the focused box (MyGUI): a multi-line box takes a line break (Ctrl+Enter accepts), any other accepts, which here is the default action; a read-only box does nothing. True when the box took it.
    /// </summary>
    public bool EditEnter(bool ctrl)
    {
        if (edit is null || edit.ReadOnly) return false;
        if (edit.MultiLine && !ctrl) { edit.Insert("\n"); PublishEdit(); return true; }
        Submit();
        return true;
    }

    /// <summary>
    /// Tab / Shift+Tab over the top window's editable boxes and buttons in layout order (Meitou's; the first Tab goes to the first edit box, else the first button). On a window that is not modal and has
    /// no edit box it does nothing until a button has the focus, so Tab stays the game's change-squad key there; false: nothing taken.
    /// </summary>
    bool CycleFocus(bool backwards)
    {
        if (frame.LastOrDefault(f => !screens[f.Id].Panel) is not { } top) return false;   // Tab never moves into a HUD panel (it is change_squad there)
        var stops = top.Controls.Where(c => IsEditStop(c) || IsButtonStop(c)).ToList();
        if (stops.Count == 0) return false;
        int current = stops.FindIndex(c => focus == (top.Id, c.Name) || keyButton == (top.Id, c.Name)), firstEdit = stops.FindIndex(IsEditStop);
        if (current < 0 && firstEdit < 0 && !screens[top.Id].Modal) return false;
        int next = current < 0 ? backwards ? stops.Count - 1 : Math.Max(0, firstEdit) : (current + (backwards ? stops.Count - 1 : 1)) % stops.Count;
        if (stops[next].Type == "EditBox") FocusEdit(top.Id, stops[next]);
        else { focus = null; edit = null; keyButton = (top.Id, stops[next].Name); }
        return true;
    }

    /// <summary>Enter or Space on the button the keyboard is on: it is pressed like a click on its middle (so tabs, sounds and events run as for the pointer). False: no button has the focus.</summary>
    public bool ActivateFocused()
    {
        if (keyButton is not { } k || frame.LastOrDefault() is not { } top || top.Id != k.Screen) return false;
        if (top.Controls.FirstOrDefault(c => c.Name == k.Widget && IsButtonStop(c)) is not { } button) { keyButton = null; return false; }
        var at = new Vector2(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2);
        if (Pick(at) is { } hit && hit.Id == top.Id && hit.Control.Name == button.Name) PointerDown(at);
        else Activated?.Invoke(new(top.Id, button.Name, button.Action));
        if (open.Count > 0 && open[^1] == k.Screen) keyButton = k;   // the press may have closed the window or opened another
        return true;
    }

    /// <summary>After a screen is arranged: a read-only focused box takes the text the screen shows now, and the button the keyboard is on gets the ring when <see cref="FocusRing"/> is on.</summary>
    void ArrangeKeyboard(string id, List<UiControl> controls)
    {
        if (focus is { } f && f.Screen == id && edit is { ReadOnly: true } && controls.FirstOrDefault(c => c.Name == f.Widget) is { } box && box.Caption != edit.Text) edit.Set(box.Caption);
        if (!FocusRing || keyButton is not { } k || k.Screen != id || controls.FirstOrDefault(c => c.Name == k.Widget && IsButtonStop(c)) is not { } button) return;
        var b = button.Bounds;
        void Bar(string part, float x, float y, float w, float h) => controls.Add(new("KeyFocusRing." + part, "Widget", new(x, y, w, h), "", "", "", true, NoMouse, RingColour, Pickable: false));
        Bar("top", b.X - 4, b.Y - 4, b.Width + 8, 2);
        Bar("bottom", b.X - 4, b.Y + b.Height + 2, b.Width + 8, 2);
        Bar("left", b.X - 4, b.Y - 2, 2, b.Height + 4);
        Bar("right", b.X + b.Width + 2, b.Y - 2, 2, b.Height + 4);
    }
}
