using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// A widget event of a screen: the widget, the action (its own, or select, accept, text), the text and the value.
/// </summary>
public sealed record MyGuiEvent(string Screen, string Widget, string Action, string Text = "", float Value = 0);

/// <summary>
/// A screen's controls as one arrange laid them out.
/// </summary>
public sealed record MyGuiScreenFrame(string Id, IReadOnlyList<UiControl> Controls);

/// <summary>
/// Window lifetime, z-order, capture, focus and template events for the original MyGUI layouts.
/// </summary>
public sealed partial class MyGuiScreenHost(MyGuiResources resources)
{
    #region State

    /// <summary>
    /// The registered screens by id.
    /// </summary>
    readonly Dictionary<string, Screen> _screens = new(StringComparer.Ordinal);

    /// <summary>
    /// The open screens, bottom first (panels at the bottom).
    /// </summary>
    readonly List<string> _open = [];

    /// <summary>
    /// Each follower's leader (<see cref="Follow"/>).
    /// </summary>
    readonly Dictionary<string, string> _leaders = new(StringComparer.Ordinal);

    /// <summary>
    /// The screens as the last <see cref="Arrange"/> laid them out.
    /// </summary>
    IReadOnlyList<MyGuiScreenFrame> _frame = [];

    /// <summary>
    /// The edit box with the keyboard focus; null for none.
    /// </summary>
    (string Screen, string Widget)? _focus;

    /// <summary>
    /// The focused edit box's text, caret and selection.
    /// </summary>
    UiTextBuffer? _edit;

    /// <summary>
    /// The window being dragged, where the drag began and the window's offset then.
    /// </summary>
    (string Screen, Vector2 At, Vector2 Offset)? _drag;

    /// <summary>
    /// A press in the focused edit box is selecting by drag.
    /// </summary>
    bool _selecting;

    /// <summary>
    /// The last list line clicked and when, for the double click that accepts it; the window a double click has (the system's default, 500 ms).
    /// </summary>
    (string Screen, string List, int Line, long At)? _lastListClick;

    /// <summary>
    /// The double-click time, seconds.
    /// </summary>
    const double DoubleClickSeconds = 0.5;

    /// <summary>
    /// A button pressed, a list line selected or accepted, or an edit box's text changed.
    /// </summary>
    public event Action<MyGuiEvent>? Activated;

    /// <summary>
    /// Modal screens whose empty surroundings let a click through to the screens under them (the title's windows: another title button replaces
    /// them).
    /// </summary>
    public Func<string, bool>? PassThrough { get; set; }

    /// <summary>
    /// The text index under the pointer in the one-line edit box the renderer last drew there (null: none); a press places the caret, a drag selects
    /// (Meitou: MyGUI does both).
    /// </summary>
    public Func<Vector2, int?>? CaretAt { get; set; }

    #endregion

    #region Screens

    /// <summary>
    /// A registered screen: its layout, kind and the state it keeps while closed (offset, pin, texts, tabs, list selections and scrolls).
    /// </summary>
    sealed partial class Screen(MyGuiLayout layout, bool modal, bool centreRoot, bool panel = false)
    {
        /// <summary>
        /// The screen's layout, its templates expanded.
        /// </summary>
        public MyGuiLayout Layout { get; set; } = layout;

        /// <summary>
        /// A modal screen: it takes every click outside it (unless it passes through).
        /// </summary>
        public bool Modal { get; } = modal;

        /// <summary>
        /// A HUD panel: drawn under every window, never raised or closed by Escape.
        /// </summary>
        public bool Panel { get; } = panel;

        /// <summary>
        /// The root is centred on the screen.
        /// </summary>
        public bool CentreRoot { get; } = centreRoot;

        /// <summary>
        /// How far the window was moved from its layout position.
        /// </summary>
        public Vector2 Offset;

        /// <summary>
        /// The pin button is down.
        /// </summary>
        public bool Pinned;

        /// <summary>
        /// The edit boxes' and captions' texts by widget.
        /// </summary>
        public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The tab controls' selected tabs by widget.
        /// </summary>
        public Dictionary<string, int> Tabs { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The lists' selected lines by widget.
        /// </summary>
        public Dictionary<string, int> Selected { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The scroll offsets by widget.
        /// </summary>
        public Dictionary<string, float> Scroll { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A screen is open.
    /// </summary>
    public bool HasWindows => _open.Count > 0;

    /// <summary>
    /// A modal screen is open.
    /// </summary>
    public bool HasModal => _open.Any(id => _screens[id].Modal);

    /// <summary>
    /// Whether screen <paramref name="id"/> is modal.
    /// </summary>
    public bool IsModal(string id) => _screens[id].Modal;

    /// <summary>
    /// A window drag or a widget captures the pointer.
    /// </summary>
    public bool CapturesPointer => _drag is not null || CapturesWidget;

    /// <summary>
    /// An edit box has the keyboard focus.
    /// </summary>
    public bool HasKeyboardFocus => _focus is not null;

    /// <summary>
    /// The registered screens' ids.
    /// </summary>
    public IReadOnlyList<string> Registered => _screens.Keys.ToArray();

    /// <summary>
    /// The open screens' ids, bottom first.
    /// </summary>
    public IReadOnlyList<string> OpenScreens => _open.ToArray();

    /// <summary>
    /// Registers screen <paramref name="id"/> with <paramref name="layout"/> (its templates expanded).
    /// </summary>
    public void Register(string id, MyGuiLayout layout, bool modal = false, bool centreRoot = false) =>
        _screens.Add(id, new(MyGuiTemplates.Expand(layout, resources), modal, centreRoot));

    /// <summary>
    /// Registers a HUD panel taken out of the HUD layout so that its edit boxes take typing (the town panel): drawn under every window, never raised,
    /// never closed by Escape.
    /// </summary>
    public void RegisterPanel(string id, MyGuiLayout layout) =>
        _screens.Add(id, new(MyGuiTemplates.Expand(layout, resources), false, false, panel: true));

    /// <summary>
    /// Swaps a screen's layout for one composed at run time (rows the code makes from templates, as the original's <c>createWidgetT</c>); the
    /// screen's state stays.
    /// </summary>
    public void Replace(string id, MyGuiLayout layout) => _screens[id].Layout = MyGuiTemplates.Expand(layout, resources);

    /// <summary>
    /// Screen <paramref name="id"/>'s layout.
    /// </summary>
    public MyGuiLayout Layout(string id) => _screens[id].Layout;

    /// <summary>
    /// Whether screen <paramref name="id"/> is open.
    /// </summary>
    public bool IsOpen(string id) => _open.Contains(id);

    /// <summary>
    /// Whether the open window's pin button is down (closing the window lets it go).
    /// </summary>
    public bool IsPinned(string id) => _open.Contains(id) && _screens[id].Pinned;

    /// <summary>
    /// Puts the pin button of screen <paramref name="id"/> down or up.
    /// </summary>
    public void SetPinned(string id, bool pinned) => _screens[id].Pinned = pinned;

    /// <summary>
    /// Whether screen <paramref name="id"/> is registered.
    /// </summary>
    public bool IsRegistered(string id) => _screens.ContainsKey(id);

    #endregion

    #region Window lifetime

    /// <summary>
    /// Opens screen <paramref name="id"/> on top (a panel at the bottom), dropping the keyboard focus.
    /// </summary>
    public void Open(string id)
    {
        if (!_screens.TryGetValue(id, out var s)) throw new KeyNotFoundException(id);

        if (s.Panel)
        {
            if (!_open.Contains(id)) _open.Insert(0, id);
            return;
        }

        _open.Remove(id);
        _open.Add(id);
        _focus = null;
        _edit = null;
        _keyButton = null;
    }

    /// <summary>
    /// Marries <paramref name="id"/> to <paramref name="leader"/> (the original's bag window beside the inventory): a drag on either moves both,
    /// closing the leader closes it and Escape on it closes the leader.
    /// </summary>
    public void Follow(string id, string leader) => _leaders[id] = leader;

    /// <summary>
    /// Closes screen <paramref name="id"/> and its followers: its pin, focus, drag and widget state let go.
    /// </summary>
    public void Close(string id)
    {
        foreach (var (follower, leader) in _leaders.ToArray())
        {
            if (leader == id && _open.Contains(follower)) Close(follower);
        }

        _open.Remove(id);
        _screens[id].Pinned = false;
        if (_focus?.Screen == id)
        {
            _focus = null;
            _edit = null;
        }

        if (_keyButton?.Screen == id) _keyButton = null;
        if (_drag?.Screen == id) _drag = null;
        ForgetWidgets(id);
        _frame = _frame.Where(f => f.Id != id).ToArray();
    }

    /// <summary>
    /// Closes every open screen.
    /// </summary>
    public void CloseAll()
    {
        foreach (string id in _open.ToArray()) Close(id);
    }

    /// <summary>
    /// Drops the selection of list <paramref name="list"/>.
    /// </summary>
    public void ClearSelection(string id, string list) => _screens[id].Selected.Remove(list);

    /// <summary>
    /// Selects tab <paramref name="index"/> (at least 0) of tab control <paramref name="widget"/>.
    /// </summary>
    public void SetTab(string id, string widget, int index) => _screens[id].Tabs[widget] = Math.Max(0, index);

    /// <summary>
    /// Where a screen has been moved from its layout position (a dragged window keeps its offset; the host may place a window beside another).
    /// </summary>
    public Vector2 GetOffset(string id) => _screens[id].Offset;

    /// <summary>
    /// Moves screen <paramref name="id"/> by <paramref name="offset"/> from its layout position.
    /// </summary>
    public void SetOffset(string id, Vector2 offset)
    {
        _screens[id].Offset = offset;
        _screens[id].Dragged = false;
    }

    /// <summary>
    /// Sets a widget's text (the focused edit box's too).
    /// </summary>
    public void SetText(string id, string widget, string text)
    {
        _screens[id].Text[widget] = text;
        if (_focus == (id, widget)) _edit?.Set(text);
    }

    /// <summary>
    /// A widget's text; empty for none.
    /// </summary>
    public string GetText(string id, string widget) => _screens[id].Text.GetValueOrDefault(widget) ?? "";

    /// <summary>
    /// The selected tab of tab control <paramref name="widget"/>.
    /// </summary>
    public int GetTab(string id, string widget) => _screens[id].Tabs.GetValueOrDefault(widget);

    /// <summary>
    /// Ends typing in the focused edit box (a press the screen's decoration took).
    /// </summary>
    public void Blur()
    {
        _focus = null;
        _edit = null;
        _keyButton = null;
    }

    /// <summary>
    /// The edit box being typed in (also one a screen's decoration added): its screen, name, caret and selection; null: none.
    /// </summary>
    public (string Screen, string Widget, int Caret, int SelectionStart, int SelectionLength)? Focus =>
        _focus is { } f && _edit is not null ? (f.Screen, f.Widget, _edit.Caret, _edit.SelectionStart, _edit.SelectionLength) : null;

    #endregion

    #region Arrange

    /// <summary>
    /// Lays out the open screens (modal ones last) with their kept state, the caret, the pin and the keyboard focus; <paramref name="decorate"/> adds
    /// a screen's own controls.
    /// </summary>
    public IReadOnlyList<MyGuiScreenFrame> Arrange(int width, int height, Func<string, UiBindings> bind,
        Action<string, List<UiControl>>? decorate = null)
    {
        var result = new List<MyGuiScreenFrame>();
        RepeatTick();
        foreach (string id in _open.OrderBy(i => _screens[i].Modal ? 1 : 0).ToArray())
        {
            var s = _screens[id];
            var b = bind(id);
            foreach (var t in s.Tabs) b.SelectedTabs[t.Key] = t.Value;
            foreach (var t in s.Text) b.Text[t.Key] = t.Value;
            foreach (var t in s.Selected) b.SelectedItems[t.Key] = t.Value;
            foreach (var t in s.Scroll) b.ScrollOffsets[t.Key] = t.Value;
            PushWidgetState(id, s, b);
            ApplyCaret(id, s, b);
            PlaceRoots(s, b, width, height);
            var controls = MyGuiPresentation.Arrange(s.Layout, width, height, b, resources.Layers).ToList();
            PinChecked(s, controls);
            decorate?.Invoke(id, controls);
            ArrangeKeyboard(id, controls);
            result.Add(new(id, controls));
        }

        _frame = result;
        return _frame;
    }

    /// <summary>
    /// The focused box's caret and selection, on the widgets that carry its name (or whose caption owner it is).
    /// </summary>
    void ApplyCaret(string id, Screen s, UiBindings b)
    {
        if (_focus is not { } f || f.Screen != id || _edit is null) return;

        foreach (var widget in s.Layout.Widgets.Where(w => w.Name == f.Widget || w.Properties.GetValueOrDefault("CaptionOwner") == f.Widget))
        {
            var current = b.Properties.GetValueOrDefault(widget.Name) ?? new Dictionary<string, string>();
            var properties = new Dictionary<string, string>(current, StringComparer.Ordinal)
            {
                ["Focused"] = "true",
                ["CaretIndex"] = _edit.Caret.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SelectionStart"] = _edit.SelectionStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SelectionLength"] = _edit.SelectionLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            if (widget.Properties.GetValueOrDefault("SkipCaption") != "true") b.Properties[widget.Name] = properties;
        }
    }

    /// <summary>
    /// A pinned window's pin button stays down (the skin's checked state, else pushed).
    /// </summary>
    static void PinChecked(Screen s, List<UiControl> controls)
    {
        if (!s.Pinned) return;

        for (int i = 0; i < controls.Count; i++)
        {
            if (!controls[i].Action.StartsWith("mygui:pin", StringComparison.Ordinal)) continue;

            var properties = new Dictionary<string, string>(controls[i].Properties) { ["Checked"] = "true", ["StateSelected"] = "true" };
            controls[i] = controls[i] with { Properties = properties };
        }
    }

    /// <summary>
    /// The screen and control under <paramref name="p"/>, from the top; a modal screen that does not pass clicks through takes the click on its
    /// surroundings.
    /// </summary>
    (string Id, UiControl Control)? Pick(Vector2 p)
    {
        foreach (var screen in _frame.Reverse())
        {
            if (MyGuiPresentation.HitTest(screen.Controls, p.X, p.Y) is { } hit) return (screen.Id, hit);

            if (_screens[screen.Id].Modal && PassThrough?.Invoke(screen.Id) != true)
                return (screen.Id, new UiControl("modal", "Widget", default, "", "", "", false, new Dictionary<string, string>(), default));
        }

        return null;
    }

    /// <summary>
    /// Whether a screen takes the point <paramref name="p"/>.
    /// </summary>
    public bool Contains(Vector2 p) => Pick(p) is not null;

    /// <summary>
    /// The screens as the last <see cref="Arrange"/> laid them out (what <see cref="HitTest"/> picks from).
    /// </summary>
    public IReadOnlyList<MyGuiScreenFrame> LastFrame => _frame;

    /// <summary>
    /// The screen and control under <paramref name="p"/> (<see cref="Pick"/>).
    /// </summary>
    public (string Screen, UiControl Control)? HitTest(Vector2 p) => Pick(p);

    #endregion

    #region Pointer and keys

    /// <summary>
    /// A press at <paramref name="p"/>: raises the window and presses its widget, chrome, edit box (placing the caret) or button, else starts a drag;
    /// false outside every screen.
    /// </summary>
    public bool PointerDown(Vector2 p)
    {
        _keyButton = null;
        if (Pick(p) is not { } picked)
        {
            _focus = null;
            _edit = null;
            PressOutside(null);
            return false;
        }

        var (id, c) = picked;
        var s = _screens[id];
        if (!s.Panel)
        {
            _open.Remove(id);
            _open.Add(id);
        }

        if (!c.Enabled) return true;
        if (PressWidget(id, c, p)) return true;

        if (c.Action.StartsWith("mygui:", StringComparison.Ordinal))
        {
            PressChrome(id, s, c);
            return true;
        }

        if (c.Type == "EditBox" && c.Properties.GetValueOrDefault("Static") != "true")
        {
            FocusEdit(id, c);
            if (CaretAt?.Invoke(p) is { } at)
            {
                _edit!.Place(at);
                _selecting = true;
            }

            return true;
        }

        _focus = null;
        _edit = null;
        if (c.Type == "Button")
        {
            Activated?.Invoke(new(id, c.Name, c.Action));
            return true;
        }

        BeginWindowDrag(id, s, p);
        return true;
    }

    /// <summary>
    /// The window's own chrome: close, pin, a tab or a list line (<c>mygui:list:</c>).
    /// </summary>
    void PressChrome(string id, Screen s, UiControl c)
    {
        var parts = c.Action.Split(':');
        if (parts[1] == "close") Close(id);
        else if (parts[1] == "pin") s.Pinned = !s.Pinned;
        else if (parts[1] == "tab" && parts.Length == 4) s.Tabs[parts[2]] = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
        else if (parts[1] == "list" && parts.Length == 4)
        {
            int selected = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            ListLinePressed(id, s, c, parts[2], selected);
        }
    }

    /// <summary>
    /// A list line picked: it is selected, and a second click on the same line soon after accepts it.
    /// </summary>
    void ListLinePressed(string id, Screen s, UiControl c, string list, int selected)
    {
        s.Selected[list] = selected;
        Activated?.Invoke(new(id, list, "select", c.Argument, selected));

        // A second click on the same line soon after accepts it (MyGUI ListBox: eventListSelectAccept on a double click).
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (IsDoubleClick(id, list, selected, now))
        {
            _lastListClick = null;
            Activated?.Invoke(new(id, list, "accept", c.Argument, selected));
            return;
        }

        _lastListClick = (id, list, selected, now);
    }

    /// <summary>
    /// Whether a list click is the second on the same line of the same list within the double-click time.
    /// </summary>
    bool IsDoubleClick(string id, string list, int selected, long now) =>
        _lastListClick is { } last && last.Screen == id && last.List == list && last.Line == selected
        && System.Diagnostics.Stopwatch.GetElapsedTime(last.At, now).TotalSeconds < DoubleClickSeconds;

    /// <summary>
    /// A press on a window's title strip (its top 38 px) starts dragging it, or its leader when it is married to one (<see cref="Follow"/>).
    /// </summary>
    void BeginWindowDrag(string id, Screen s, Vector2 p)
    {
        var root = _frame.First(f => f.Id == id).Controls.FirstOrDefault(n => n.Type == "Window");
        if (root is null || root.Properties.GetValueOrDefault("Movable") == "false" || !(p.Y < root.Bounds.Y + 38)) return;

        if (_leaders.TryGetValue(id, out var leader) && _open.Contains(leader))
            _drag = (leader, p, _screens[leader].Offset);
        else
            _drag = (id, p, s.Offset);
    }

    /// <summary>
    /// A pointer move: the drag selection in the focused edit box, a captured widget, or the window drag; false when nothing took it.
    /// </summary>
    public bool PointerMove(Vector2 p)
    {
        if (_selecting && _edit is not null && CaretAt?.Invoke(p) is { } at)
        {
            _edit.Place(at, extend: true);
            return true;
        }

        if (MoveWidget(p)) return true;
        if (_drag is not { } d) return false;

        DragWindow(d, p);
        return true;
    }

    /// <summary>
    /// The pointer is released: the drag, the selecting and the captured widget end.
    /// </summary>
    public void PointerUp()
    {
        _drag = null;
        _selecting = false;
        ReleaseWidget();
    }

    /// <summary>
    /// The wheel over <paramref name="p"/>: scrolls the widget under it; false outside every screen.
    /// </summary>
    public bool Wheel(Vector2 p, float steps)
    {
        if (Pick(p) is not { } picked) return false;

        WheelWidget(picked.Id, p, steps);
        return true;
    }

    /// <summary>
    /// Escape: closes the open combo list, else the top window (or its leader); false when nothing is open.
    /// </summary>
    public bool Escape()
    {
        if (_open.Count == 0) return false;
        if (CloseCombo()) return true;

        var top = _frame.LastOrDefault(f => !_screens[f.Id].Panel)?.Id ?? _open.LastOrDefault(i => !_screens[i].Panel);
        if (top is not { } id) return false;

        Close(_leaders.TryGetValue(id, out var leader) && _open.Contains(leader) ? leader : id);
        return true;
    }

    /// <summary>
    /// Types <paramref name="value"/> into the focused edit box; false without one or for a control character.
    /// </summary>
    public bool TextInput(char value)
    {
        if (_focus is null || _edit is null || char.IsControl(value)) return false;

        _edit.Insert(value.ToString());
        PublishEdit();
        return true;
    }

    /// <summary>
    /// Stores the focused edit box's text and raises its text event (nothing when it is read-only).
    /// </summary>
    void PublishEdit()
    {
        if (_focus is not { } f || _edit is null || _edit.ReadOnly) return;

        _screens[f.Screen].Text[f.Widget] = _edit.Text;
        Activated?.Invoke(new(f.Screen, f.Widget, "text", _edit.Text));
    }

    /// <summary>
    /// An editing key in the focused edit box (copy, cut and paste through the clipboard); false without one.
    /// </summary>
    public bool EditKey(UiEditKey key, bool extend = false)
    {
        if (_focus is null || _edit is null) return false;
        if (key >= UiEditKey.Copy) return Clipboard(key);

        _edit.Key(key, extend);
        PublishEdit();
        return true;
    }

    /// <summary>
    /// Backspace in the focused edit box.
    /// </summary>
    public bool Backspace() => EditKey(UiEditKey.Backspace);

    /// <summary>
    /// Tab (Shift+Tab): the next edit box or button of the top window (<see cref="CycleFocus"/>).
    /// </summary>
    public bool FocusNext(bool backwards = false) => CycleFocus(backwards);

    /// <summary>
    /// Enter: activates the focused widget, else the top window's default button; false when nothing took it.
    /// </summary>
    public bool Submit()
    {
        if (ActivateFocused()) return true;
        if (_frame.LastOrDefault() is not { } top) return false;

        var button = top.Controls.FirstOrDefault(c => c.Type == "Button" && c.Enabled && c.Properties.GetValueOrDefault("DefaultAction") == "true");
        if (button is null) return false;

        Activated?.Invoke(new(top.Id, button.Name, button.Action));
        return true;
    }

    #endregion
}