using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

public sealed record MyGuiEvent(string Screen, string Widget, string Action, string Text = "", float Value = 0);
public sealed record MyGuiScreenFrame(string Id, IReadOnlyList<UiControl> Controls);

/// <summary>
/// Window lifetime, z-order, capture, focus and template events for the original MyGUI layouts.
/// </summary>
public sealed partial class MyGuiScreenHost(MyGuiResources resources)
{
    #region State

    readonly Dictionary<string, Screen> _screens = new(StringComparer.Ordinal);
    readonly List<string> _open = [];
    readonly Dictionary<string, string> _leaders = new(StringComparer.Ordinal);
    IReadOnlyList<MyGuiScreenFrame> _frame = [];
    (string Screen, string Widget)? _focus;
    UiTextBuffer? _edit;
    (string Screen, Vector2 At, Vector2 Offset)? _drag;
    bool _selecting;

    /// <summary>
    /// The last list line clicked and when, for the double click that accepts it; the window a double click has (the system's default, 500 ms).
    /// </summary>
    (string Screen, string List, int Line, long At)? _lastListClick;

    const double DoubleClickSeconds = 0.5;

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

    sealed partial class Screen(MyGuiLayout layout, bool modal, bool centreRoot, bool panel = false)
    {
        public MyGuiLayout Layout { get; set; } = layout;
        public bool Modal { get; } = modal;
        public bool Panel { get; } = panel;
        public bool CentreRoot { get; } = centreRoot;
        public Vector2 Offset;
        public bool Pinned;
        public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Tabs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Selected { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, float> Scroll { get; } = new(StringComparer.Ordinal);
    }

    public bool HasWindows => _open.Count > 0;
    public bool HasModal => _open.Any(id => _screens[id].Modal);
    public bool IsModal(string id) => _screens[id].Modal;
    public bool CapturesPointer => _drag is not null || CapturesWidget;
    public bool HasKeyboardFocus => _focus is not null;
    public IReadOnlyList<string> Registered => _screens.Keys.ToArray();
    public IReadOnlyList<string> OpenScreens => _open.ToArray();

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

    public MyGuiLayout Layout(string id) => _screens[id].Layout;
    public bool IsOpen(string id) => _open.Contains(id);

    /// <summary>
    /// Whether the open window's pin button is down (closing the window lets it go).
    /// </summary>
    public bool IsPinned(string id) => _open.Contains(id) && _screens[id].Pinned;

    public void SetPinned(string id, bool pinned) => _screens[id].Pinned = pinned;
    public bool IsRegistered(string id) => _screens.ContainsKey(id);

    #endregion

    #region Window lifetime

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

    public void CloseAll()
    {
        foreach (string id in _open.ToArray()) Close(id);
    }

    public void ClearSelection(string id, string list) => _screens[id].Selected.Remove(list);
    public void SetTab(string id, string widget, int index) => _screens[id].Tabs[widget] = Math.Max(0, index);

    /// <summary>
    /// Where a screen has been moved from its layout position (a dragged window keeps its offset; the host may place a window beside another).
    /// </summary>
    public Vector2 GetOffset(string id) => _screens[id].Offset;

    public void SetOffset(string id, Vector2 offset) => _screens[id].Offset = offset;

    public void SetText(string id, string widget, string text)
    {
        _screens[id].Text[widget] = text;
        if (_focus == (id, widget)) _edit?.Set(text);
    }

    public string GetText(string id, string widget) => _screens[id].Text.GetValueOrDefault(widget) ?? "";
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

    public bool Contains(Vector2 p) => Pick(p) is not null;

    /// <summary>
    /// The screens as the last <see cref="Arrange"/> laid them out (what <see cref="HitTest"/> picks from).
    /// </summary>
    public IReadOnlyList<MyGuiScreenFrame> LastFrame => _frame;

    public (string Screen, UiControl Control)? HitTest(Vector2 p) => Pick(p);

    #endregion

    #region Pointer and keys

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

    public void PointerUp()
    {
        _drag = null;
        _selecting = false;
        ReleaseWidget();
    }

    public bool Wheel(Vector2 p, float steps)
    {
        if (Pick(p) is not { } picked) return false;

        WheelWidget(picked.Id, p, steps);
        return true;
    }

    public bool Escape()
    {
        if (_open.Count == 0) return false;
        if (CloseCombo()) return true;

        var top = _frame.LastOrDefault(f => !_screens[f.Id].Panel)?.Id ?? _open.LastOrDefault(i => !_screens[i].Panel);
        if (top is not { } id) return false;

        Close(_leaders.TryGetValue(id, out var leader) && _open.Contains(leader) ? leader : id);
        return true;
    }

    public bool TextInput(char value)
    {
        if (_focus is null || _edit is null || char.IsControl(value)) return false;

        _edit.Insert(value.ToString());
        PublishEdit();
        return true;
    }

    void PublishEdit()
    {
        if (_focus is not { } f || _edit is null || _edit.ReadOnly) return;

        _screens[f.Screen].Text[f.Widget] = _edit.Text;
        Activated?.Invoke(new(f.Screen, f.Widget, "text", _edit.Text));
    }

    public bool EditKey(UiEditKey key, bool extend = false)
    {
        if (_focus is null || _edit is null) return false;
        if (key >= UiEditKey.Copy) return Clipboard(key);

        _edit.Key(key, extend);
        PublishEdit();
        return true;
    }

    public bool Backspace() => EditKey(UiEditKey.Backspace);

    /// <summary>
    /// Tab (Shift+Tab): the next edit box or button of the top window (<see cref="CycleFocus"/>).
    /// </summary>
    public bool FocusNext(bool backwards = false) => CycleFocus(backwards);

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